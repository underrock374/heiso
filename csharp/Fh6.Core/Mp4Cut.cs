using System.Buffers.Binary;
using System.Text;

namespace Fh6.Core;

/// <summary>
/// MP4 を再エンコードせずに切り出す(サンプルをそのままコピーし、サンプルの表と編集リストを作り直す)。
/// 映像は、切りたい始まりの直前のキーフレームから始める(再エンコードしないため。HEISO の OBS プロファイルではキーフレームは 1 秒ごと、
/// OBS の「基本」出力モードのままでは NVENC で約 8.3 秒ごと)。
/// 音声は、映像の始まりと同じ時刻からにそろえる。映像 1 本・音声 0〜1 本の、moov と mdat が別々の普通の MP4(OBS の出力)向け。
/// Fragmented MP4 には対応しない。
/// </summary>
public static class Mp4Cut
{
    /// <summary>切り出しの結果。StartSec は、元の動画のどの時刻が新しい動画の 0 秒になったか(映像のキーフレームの時刻)</summary>
    public sealed record Result(double StartSec, double EndSec, int VideoSamples, int AudioSamples);

    /// <summary>元の動画の fromSec〜toSec を含むように切り出して dest に書く</summary>
    public static Result Cut(string source, string dest, double fromSec, double toSec)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[]? ftyp = null, moov = null;
        foreach (var (type, start, size) in Boxes(src, 0, src.Length))
        {
            if (type == "ftyp") ftyp = Read(src, start - 8, size + 8);
            else if (type == "moov") moov = Read(src, start, size);
            else if (type == "moof") throw new NotSupportedException(Strings.T("Fragmented MP4 は切り出せません"));
        }
        if (ftyp == null || moov == null) throw new InvalidDataException(Strings.T("MP4 の ftyp か moov がありません"));

        var tracks = new List<Track>();
        foreach (var (type, start, size) in Boxes(moov))
            if (type == "trak") tracks.Add(Track.Parse(moov.AsSpan((int)start, (int)size).ToArray()));
        var video = tracks.FirstOrDefault(t => t.Handler == "vide") ?? throw new InvalidDataException(Strings.T("映像のトラックがありません"));
        var audio = tracks.FirstOrDefault(t => t.Handler == "soun");

        // ---- 映像: 始まりの直前のキーフレームから、終わりまで ----
        long want0 = (long)Math.Floor(fromSec * video.Timescale) + video.EditMediaTime;
        long want1 = (long)Math.Ceiling(toSec * video.Timescale) + video.EditMediaTime;
        int v0 = 0;
        for (int s = 0; s < video.Count; s++)
            if (video.IsSync(s) && video.Pts(s) <= want0) v0 = s;
        int v1 = video.Count - 1;
        for (int s = v0; s < video.Count; s++)
            if (video.Dts[s] > want1) { v1 = s - 1; break; }
        if (v1 <= v0) throw new InvalidDataException(Strings.T("切り出す範囲に映像がありません"));
        long vStartPts = video.Pts(v0);
        long vEndPts = video.Dts[v1] + video.Durations[v1];
        double startSec = (double)(vStartPts - video.EditMediaTime) / video.Timescale;
        double endSec = (double)(vEndPts - video.EditMediaTime) / video.Timescale;

        // ---- 音声: 映像と同じ時刻の範囲 ----
        int a0 = 0, a1 = -1;
        long aMediaStart = 0;
        if (audio != null)
        {
            long aw0 = (long)Math.Floor(startSec * audio.Timescale) + audio.EditMediaTime;
            long aw1 = (long)Math.Ceiling(endSec * audio.Timescale) + audio.EditMediaTime;
            for (int s = 0; s < audio.Count; s++) if (audio.Dts[s] <= aw0) a0 = s;
            a1 = audio.Count - 1;
            for (int s = a0; s < audio.Count; s++) if (audio.Dts[s] >= aw1) { a1 = s - 1; break; }
            aMediaStart = aw0 - audio.Dts[a0];   // 最初のサンプルの途中から見せる(編集リスト)
        }

        // ---- 書き出し: ftyp、mdat(映像と音声をおよそ時刻順に交互のチャンクで)、moov ----
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write);
        dst.Write(ftyp);
        long mdatStart = dst.Position;
        dst.Write(new byte[16]);   // mdat の見出し(64bit の大きさ。後で書く)
        var vChunks = new List<(long Offset, int Count)>();
        var aChunks = new List<(long Offset, int Count)>();
        int vi = v0, ai = a0;
        const double ChunkSec = 0.5;
        var buf = new byte[1 << 20];
        while (vi <= v1 || (audio != null && ai <= a1))
        {
            double vt = vi <= v1 ? (double)video.Dts[vi] / video.Timescale : double.MaxValue;
            double at = audio != null && ai <= a1 ? (double)audio.Dts[ai] / audio.Timescale - (double)(audio.EditMediaTime) / audio.Timescale : double.MaxValue;
            double vtn = vt - (double)video.EditMediaTime / video.Timescale;
            if (vtn <= at)
            {
                long off = dst.Position; int n = 0;
                double end = vtn + ChunkSec;
                while (vi <= v1 && (double)(video.Dts[vi] - video.EditMediaTime) / video.Timescale < end) { CopySample(src, dst, video, vi, ref buf); vi++; n++; }
                vChunks.Add((off, n));
            }
            else
            {
                long off = dst.Position; int n = 0;
                double end = at + ChunkSec;
                while (ai <= a1 && (double)(audio!.Dts[ai] - audio.EditMediaTime) / audio.Timescale < end) { CopySample(src, dst, audio, ai, ref buf); ai++; n++; }
                aChunks.Add((off, n));
            }
        }
        long mdatEnd = dst.Position;
        dst.Position = mdatStart;
        var hdr = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(hdr, 4);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), (ulong)(mdatEnd - mdatStart));
        dst.Write(hdr);
        dst.Position = mdatEnd;

        // ---- moov を作り直す ----
        uint movieTs = MovieTimescale(moov);
        double durSec = endSec - startSec;
        var newMoov = new List<byte[]>();
        foreach (var (type, start, size) in Boxes(moov))
        {
            var body = moov.AsSpan((int)start, (int)size).ToArray();
            if (type == "mvhd") newMoov.Add(Box("mvhd", SetDuration(body, (ulong)Math.Round(durSec * movieTs), mvhd: true)));
            else if (type == "trak")
            {
                var t = Track.Parse(body);
                if (t.Handler == "vide")
                    newMoov.Add(t.Rebuild(v0, v1, vChunks, editMediaTime: vStartPts - video.Dts[v0], durSec, movieTs));
                else if (t.Handler == "soun" && audio != null && a1 >= a0)
                    newMoov.Add(t.Rebuild(a0, a1, aChunks, editMediaTime: aMediaStart, durSec, movieTs));
                // それ以外のトラック(字幕など)は入れない
            }
            else newMoov.Add(Box(type, body));
        }
        dst.Write(Box("moov", Concat(newMoov)));
        return new Result(startSec, endSec, v1 - v0 + 1, audio != null ? a1 - a0 + 1 : 0);
    }

    /// <summary>トラックの中身の要約。KeyFrames はキーフレームのサンプル番号(0 起点。表が無ければ null = 全部)</summary>
    public sealed record TrackInfo(string Handler, uint Timescale, long EditMediaTime, int Samples, long MediaDuration,
                                   int[]? KeyFrames, long FirstSampleOffset);

    /// <summary>MP4 のトラックを読む(切り出しの確認と、録画のキーフレーム間隔を調べるため)</summary>
    public static List<TrackInfo> Inspect(string path)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[]? moov = null;
        foreach (var (type, start, size) in Boxes(src, 0, src.Length))
            if (type == "moov") moov = Read(src, start, size);
        if (moov == null) throw new InvalidDataException(Strings.T("MP4 の moov がありません"));
        var list = new List<TrackInfo>();
        foreach (var (type, start, size) in Boxes(moov))
        {
            if (type != "trak") continue;
            var t = Track.Parse(moov.AsSpan((int)start, (int)size).ToArray());
            long dur = t.Count == 0 ? 0 : t.Dts[^1] + t.Durations[^1];
            list.Add(new TrackInfo(t.Handler, t.Timescale, t.EditMediaTime, t.Count, dur,
                                   t.Sync?.Order().ToArray(), t.Count > 0 ? t.Offsets[0] : -1));
        }
        return list;
    }

    private static void CopySample(FileStream src, FileStream dst, Track t, int s, ref byte[] buf)
    {
        int size = t.Sizes[s];
        if (buf.Length < size) buf = new byte[size];
        src.Position = t.Offsets[s];
        int read = 0;
        while (read < size)
        {
            int n = src.Read(buf, read, size - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        dst.Write(buf, 0, size);
    }

    private static uint MovieTimescale(byte[] moov)
    {
        foreach (var (type, start, size) in Boxes(moov))
            if (type == "mvhd")
            {
                var b = moov.AsSpan((int)start, (int)size);
                return BinaryPrimitives.ReadUInt32BigEndian(b.Slice(b[0] == 1 ? 20 : 12));
            }
        return 1000;
    }

    /// <summary>mvhd / tkhd / mdhd の duration を書き換える(version 0 は 32bit、1 は 64bit)</summary>
    private static byte[] SetDuration(byte[] body, ulong duration, bool mvhd = false, bool tkhd = false)
    {
        var b = (byte[])body.Clone();
        bool v1 = b[0] == 1;
        int off = tkhd ? (v1 ? 28 : 20) : (v1 ? 24 : 16);
        if (v1) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(off), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(off), (uint)Math.Min(duration, uint.MaxValue));
        return b;
    }

    // ---- トラック ----

    private sealed class Track
    {
        public byte[] Raw = Array.Empty<byte>();
        public string Handler = "";
        public uint Timescale;
        public long EditMediaTime;      // 元の編集リストの media_time(音声の先頭の詰め物など)
        public long[] Dts = Array.Empty<long>();
        public int[] Durations = Array.Empty<int>();
        public int[]? Ctts;             // 表示の時刻のずれ(B フレーム)。無ければ null
        public byte CttsVersion;
        public HashSet<int>? Sync;      // キーフレーム(0 起点)。無ければ全部がキーフレーム
        public int[] Sizes = Array.Empty<int>();
        public long[] Offsets = Array.Empty<long>();
        public int Count => Sizes.Length;

        public bool IsSync(int s) => Sync == null || Sync.Contains(s);
        public long Pts(int s) => Dts[s] + (Ctts?[s] ?? 0);

        public static Track Parse(byte[] trak)
        {
            var t = new Track { Raw = trak };
            byte[]? stts = null, ctts = null, stss = null, stsc = null, stsz = null, stco = null, co64 = null;
            void Walk(byte[] buf, string path)
            {
                foreach (var (type, start, size) in Boxes(buf))
                {
                    var body = buf.AsSpan((int)start, (int)size).ToArray();
                    switch (type)
                    {
                        case "mdia": case "minf": case "stbl": case "edts": Walk(body, path + "/" + type); break;
                        case "mdhd": t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(body[0] == 1 ? 20 : 12)); break;
                        case "hdlr": t.Handler = Encoding.ASCII.GetString(body, 8, 4); break;
                        case "elst":
                            uint n = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4));
                            if (n > 0) t.EditMediaTime = body[0] == 1 ? BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(16)) : BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(12));
                            if (t.EditMediaTime < 0) t.EditMediaTime = 0;
                            break;
                        case "stts": stts = body; break;
                        case "ctts": ctts = body; break;
                        case "stss": stss = body; break;
                        case "stsc": stsc = body; break;
                        case "stsz": stsz = body; break;
                        case "stco": stco = body; break;
                        case "co64": co64 = body; break;
                    }
                }
            }
            Walk(trak, "");
            if (stts == null || stsz == null || stsc == null || (stco == null && co64 == null))
                throw new InvalidDataException(Strings.T("トラック({track})のサンプルの表が足りません", ("track", t.Handler)));

            // 大きさ
            uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(4));
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(8));
            t.Sizes = new int[count];
            for (int i = 0; i < count; i++) t.Sizes[i] = fixedSize != 0 ? (int)fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(12 + i * 4));
            // 時刻
            t.Dts = new long[count];
            t.Durations = new int[count];
            {
                int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(4)), s = 0;
                long dts = 0;
                for (int e = 0; e < entries && s < count; e++)
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(8 + e * 8));
                    int d = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12 + e * 8));
                    for (uint k = 0; k < n && s < count; k++, s++) { t.Dts[s] = dts; t.Durations[s] = d; dts += d; }
                }
            }
            if (ctts != null)
            {
                t.CttsVersion = ctts[0];
                t.Ctts = new int[count];
                int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(ctts.AsSpan(4)), s = 0;
                for (int e = 0; e < entries && s < count; e++)
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(ctts.AsSpan(8 + e * 8));
                    int off = BinaryPrimitives.ReadInt32BigEndian(ctts.AsSpan(12 + e * 8));
                    for (uint k = 0; k < n && s < count; k++, s++) t.Ctts[s] = off;
                }
            }
            if (stss != null)
            {
                int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(4));
                t.Sync = new HashSet<int>();
                for (int e = 0; e < entries; e++) t.Sync.Add((int)BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(8 + e * 4)) - 1);
            }
            // 位置(チャンクの位置と、チャンクごとのサンプル数から)
            long[] chunkOffsets;
            if (co64 != null)
            {
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(co64.AsSpan(4));
                chunkOffsets = new long[n];
                for (int i = 0; i < n; i++) chunkOffsets[i] = (long)BinaryPrimitives.ReadUInt64BigEndian(co64.AsSpan(8 + i * 8));
            }
            else
            {
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(stco!.AsSpan(4));
                chunkOffsets = new long[n];
                for (int i = 0; i < n; i++) chunkOffsets[i] = BinaryPrimitives.ReadUInt32BigEndian(stco.AsSpan(8 + i * 4));
            }
            t.Offsets = new long[count];
            {
                int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(4));
                var first = new int[entries]; var per = new int[entries];
                for (int e = 0; e < entries; e++)
                {
                    first[e] = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(8 + e * 12)) - 1;
                    per[e] = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(12 + e * 12));
                }
                int s = 0;
                for (int c = 0; c < chunkOffsets.Length && s < count; c++)
                {
                    int e = Array.FindLastIndex(first, f => f <= c);
                    long off = chunkOffsets[c];
                    for (int k = 0; k < per[e] && s < count; k++, s++) { t.Offsets[s] = off; off += t.Sizes[s]; }
                }
            }
            return t;
        }

        /// <summary>サンプル s0〜s1 だけのトラックを作る(サンプルの表・編集リスト・長さを作り直す。それ以外の箱はそのまま)</summary>
        public byte[] Rebuild(int s0, int s1, List<(long Offset, int Count)> chunks, long editMediaTime, double durSec, uint movieTs)
        {
            int n = s1 - s0 + 1;
            long mediaDur = Dts[s1] + Durations[s1] - Dts[s0];
            var stbl = new List<byte[]>();
            // stts(同じ長さが続く所をまとめる)
            {
                var runs = new List<(uint N, int D)>();
                for (int s = s0; s <= s1; s++)
                {
                    if (runs.Count > 0 && runs[^1].D == Durations[s]) runs[^1] = (runs[^1].N + 1, runs[^1].D);
                    else runs.Add((1, Durations[s]));
                }
                var b = new byte[8 + runs.Count * 8];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)runs.Count);
                for (int i = 0; i < runs.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 8), runs[i].N);
                    BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12 + i * 8), runs[i].D);
                }
                stbl.Add(Box("stts", b));
            }
            if (Ctts != null)
            {
                var runs = new List<(uint N, int O)>();
                for (int s = s0; s <= s1; s++)
                {
                    if (runs.Count > 0 && runs[^1].O == Ctts[s]) runs[^1] = (runs[^1].N + 1, runs[^1].O);
                    else runs.Add((1, Ctts[s]));
                }
                var b = new byte[8 + runs.Count * 8];
                b[0] = CttsVersion;
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)runs.Count);
                for (int i = 0; i < runs.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 8), runs[i].N);
                    BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12 + i * 8), runs[i].O);
                }
                stbl.Add(Box("ctts", b));
            }
            if (Sync != null)
            {
                var sync = Enumerable.Range(s0, n).Where(Sync.Contains).Select(s => s - s0 + 1).ToList();
                var b = new byte[8 + sync.Count * 4];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)sync.Count);
                for (int i = 0; i < sync.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 4), (uint)sync[i]);
                stbl.Add(Box("stss", b));
            }
            // stsc(チャンクごとのサンプル数。同じ数が続く所はまとめる)
            {
                var runs = new List<(int First, int Per)>();
                for (int c = 0; c < chunks.Count; c++)
                    if (runs.Count == 0 || runs[^1].Per != chunks[c].Count) runs.Add((c + 1, chunks[c].Count));
                var b = new byte[8 + runs.Count * 12];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)runs.Count);
                for (int i = 0; i < runs.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 12), (uint)runs[i].First);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12 + i * 12), (uint)runs[i].Per);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16 + i * 12), 1);
                }
                stbl.Add(Box("stsc", b));
            }
            {
                var b = new byte[12 + n * 4];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)n);
                for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12 + i * 4), (uint)Sizes[s0 + i]);
                stbl.Add(Box("stsz", b));
            }
            {
                // 4GB を超える位置があれば co64
                bool big = chunks.Any(c => c.Offset > uint.MaxValue);
                var b = new byte[8 + chunks.Count * (big ? 8 : 4)];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)chunks.Count);
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (big) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8 + i * 8), (ulong)chunks[i].Offset);
                    else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 4), (uint)chunks[i].Offset);
                }
                stbl.Add(Box(big ? "co64" : "stco", b));
            }

            // 箱を作り直す: stbl はサンプルの表を入れ替え(stsd など他はそのまま)、edts は新しい編集リスト、長さは切り出した長さ
            var elst = new byte[8 + 12];
            BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(8), (uint)Math.Round(durSec * movieTs));
            BinaryPrimitives.WriteInt32BigEndian(elst.AsSpan(12), (int)editMediaTime);
            BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(16), 0x00010000);   // 等速
            var sampleTables = new HashSet<string> { "stts", "ctts", "stss", "stsc", "stsz", "stco", "co64", "sbgp", "sgpd", "sdtp" };

            byte[] RebuildBox(string type, byte[] body) => type switch
            {
                "tkhd" => Box(type, SetDuration(body, (ulong)Math.Round(durSec * movieTs), tkhd: true)),
                "mdhd" => Box(type, SetDuration(body, (ulong)mediaDur)),
                "edts" => Box("edts", Box("elst", elst)),
                "mdia" or "minf" => Box(type, Concat(Children(body).Select(c => RebuildBox(c.Type, c.Body)))),
                "stbl" => Box(type, Concat(Children(body).Where(c => !sampleTables.Contains(c.Type)).Select(c => Box(c.Type, c.Body)).Concat(stbl))),
                _ => Box(type, body),
            };
            var children = Children(Raw).Select(c => RebuildBox(c.Type, c.Body)).ToList();
            if (!Children(Raw).Any(c => c.Type == "edts"))
                children.Insert(1, Box("edts", Box("elst", elst)));   // tkhd の次に
            return Box("trak", Concat(children));
        }
    }

    // ---- 箱 ----

    private static IEnumerable<(string Type, byte[] Body)> Children(byte[] buf)
    {
        foreach (var (type, start, size) in Boxes(buf))
            yield return (type, buf.AsSpan((int)start, (int)size).ToArray());
    }

    private static byte[] Box(string type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private static byte[] Read(Stream s, long start, long size)
    {
        var b = new byte[size];
        s.Position = start;
        int read = 0;
        while (read < size)
        {
            int n = s.Read(b, read, (int)(size - read));
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return b;
    }

    private static IEnumerable<(string Type, long BodyStart, long BodySize)> Boxes(byte[] buf)
    {
        using var ms = new MemoryStream(buf, false);
        foreach (var b in Boxes(ms, 0, buf.Length)) yield return b;
    }

    private static IEnumerable<(string Type, long BodyStart, long BodySize)> Boxes(Stream s, long from, long to)
    {
        var hdr = new byte[16];
        long pos = from;
        while (pos + 8 <= to)
        {
            s.Position = pos;
            if (s.Read(hdr, 0, 8) < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.ASCII.GetString(hdr, 4, 4);
            int hlen = 8;
            if (size == 1)
            {
                if (s.Read(hdr, 8, 8) < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                hlen = 16;
            }
            else if (size == 0) size = to - pos;
            if (size < hlen || pos + size > to) yield break;
            yield return (type, pos + hlen, size - hlen);
            pos += size;
        }
    }
}
