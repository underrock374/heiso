using System.Buffers.Binary;
using System.Text;

namespace Fh6.Core;

/// <summary>
/// 録画ファイルの長さ(秒)を読む。MP4/MOV(通常・Hybrid・Fragmented)に対応。
/// 映像トラックの mdhd を優先し、無ければ mvhd、それも 0 なら mvex/mehd、
/// それも無ければ(Fragmented MP4)各 moof の映像トラックの終了時刻を使う。
/// MKV など未対応形式は null。
/// </summary>
public static class VideoDuration
{
    public static double? TryRead(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".mov" or ".m4v")) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadMp4(fs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 映像トラックのフレームレート(fps。サンプル数 ÷ 長さ)。moov の stts から、Fragmented MP4 は各 moof から数える。
    /// 読めなければ null
    /// </summary>
    public static double? TryReadFrameRate(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".mov" or ".m4v")) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[]? moov = null;
            var moofs = new List<(long Start, long Size)>();
            foreach (var (type, start, size) in Boxes(fs, 0, fs.Length))
            {
                if (type == "moov") moov = ReadBytes(fs, start, size);
                else if (type == "moof") moofs.Add((start, size));
            }
            if (moov == null) return null;

            var (timescale, samples, duration) = VideoSampleTable(moov);
            if (samples == 0 && moofs.Count > 0)
            {
                // Fragmented MP4: 映像トラックのサンプルを各 moof から数える
                var tracks = TrackTable(moov);
                foreach (var (start, size) in moofs)
                {
                    if (size > 64L * 1024 * 1024) continue;
                    var (n, d) = FragmentVideoSamples(ReadBytes(fs, start, size), tracks);
                    samples += n;
                    duration += d;
                }
            }
            return timescale > 0 && samples > 0 && duration > 0 ? samples * (double)timescale / duration : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>映像トラックの timescale と、stts のサンプル数・長さの合計</summary>
    private static (uint Timescale, ulong Samples, ulong Duration) VideoSampleTable(byte[] moov)
    {
        foreach (var (type, start, size) in Boxes(moov, 0, moov.Length))
        {
            if (type != "trak") continue;
            var trak = moov.AsSpan((int)start, (int)size).ToArray();
            foreach (var (t2, s2, z2) in Boxes(trak, 0, trak.Length))
            {
                if (t2 != "mdia") continue;
                var mdia = trak.AsSpan((int)s2, (int)z2).ToArray();
                uint ts = 0;
                bool video = false;
                ulong samples = 0, duration = 0;
                foreach (var (t3, s3, z3) in Boxes(mdia, 0, mdia.Length))
                {
                    var b = mdia.AsSpan((int)s3, (int)z3);
                    if (t3 == "mdhd") ts = ReadTimeHeader(b).Timescale;
                    else if (t3 == "hdlr" && b.Length >= 12) video = Encoding.ASCII.GetString(b.Slice(8, 4)) == "vide";
                    else if (t3 == "minf") (samples, duration) = SttsTotals(b.ToArray());
                }
                if (video) return (ts, samples, duration);
            }
        }
        return (0, 0, 0);
    }

    /// <summary>minf/stbl/stts のサンプル数と長さの合計</summary>
    private static (ulong Samples, ulong Duration) SttsTotals(byte[] minf)
    {
        foreach (var (t, s, z) in Boxes(minf, 0, minf.Length))
        {
            if (t != "stbl") continue;
            var stbl = minf.AsSpan((int)s, (int)z).ToArray();
            foreach (var (t2, s2, z2) in Boxes(stbl, 0, stbl.Length))
            {
                if (t2 != "stts") continue;
                var b = stbl.AsSpan((int)s2, (int)z2);
                if (b.Length < 8) return (0, 0);
                uint entries = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                ulong samples = 0, duration = 0;
                for (int i = 0; i < entries && 8 + i * 8 + 8 <= b.Length; i++)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 8));
                    uint delta = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12 + i * 8));
                    samples += count;
                    duration += (ulong)count * delta;
                }
                return (samples, duration);
            }
        }
        return (0, 0);
    }

    /// <summary>moof 1 個分の、映像トラックのサンプル数と長さの合計</summary>
    private static (ulong Samples, ulong Duration) FragmentVideoSamples(byte[] moof, Dictionary<uint, (uint Timescale, bool IsVideo)> tracks)
    {
        ulong samples = 0, duration = 0;
        foreach (var (type, start, size) in Boxes(moof, 0, moof.Length))
        {
            if (type != "traf") continue;
            var traf = moof.AsSpan((int)start, (int)size).ToArray();
            uint trackId = 0, defaultDur = 0;
            ulong n = 0, d = 0;
            foreach (var (t2, s2, z2) in Boxes(traf, 0, traf.Length))
            {
                var b = traf.AsSpan((int)s2, (int)z2);
                if (b.Length < 8) continue;
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
                if (t2 == "tfhd")
                {
                    trackId = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                    int o = 8;
                    if ((flags & 0x01) != 0) o += 8;
                    if ((flags & 0x02) != 0) o += 4;
                    if ((flags & 0x08) != 0 && b.Length >= o + 4) defaultDur = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
                }
                else if (t2 == "trun")
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                    int o = 8;
                    if ((flags & 0x001) != 0) o += 4;
                    if ((flags & 0x004) != 0) o += 4;
                    int per = 0;
                    if ((flags & 0x100) != 0) per += 4;
                    if ((flags & 0x200) != 0) per += 4;
                    if ((flags & 0x400) != 0) per += 4;
                    if ((flags & 0x800) != 0) per += 4;
                    n += count;
                    if ((flags & 0x100) == 0) d += (ulong)count * defaultDur;
                    else
                        for (uint i = 0; i < count && o + 4 <= b.Length; i++, o += per)
                            d += BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
                }
            }
            if (tracks.TryGetValue(trackId, out var tr) && tr.IsVideo)
            {
                samples += n;
                duration += d;
            }
        }
        return (samples, duration);
    }

    /// <summary>OBS が書き終わるまで少し待ちながら読む</summary>
    public static async Task<double?> TryReadWithRetryAsync(string path, int attempts = 10, int delayMs = 500)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (File.Exists(path))
            {
                var d = TryRead(path);
                if (d is > 0) return d;
            }
            await Task.Delay(delayMs);
        }
        return null;
    }

    private static double? ReadMp4(Stream s)
    {
        byte[]? moov = null;
        var moofs = new List<(long Start, long Size)>();
        foreach (var (type, start, size) in Boxes(s, 0, s.Length))
        {
            if (type == "moov") moov = ReadBytes(s, start, size);
            else if (type == "moof") moofs.Add((start, size));
        }
        if (moov == null) return null;

        var d = FromMoov(moov);
        if (d is > 0) return d;

        // Fragmented MP4(moov に長さが入っていない): 各 moof の最終時刻の最大値
        if (moofs.Count == 0) return null;
        var tracks = TrackTable(moov);
        double best = 0;
        foreach (var (start, size) in moofs)
        {
            if (size > 64L * 1024 * 1024) continue;
            var end = FragmentEnd(ReadBytes(s, start, size), tracks);
            if (end > best) best = end;
        }
        return best > 0 ? best : null;
    }

    /// <summary>track_id → (timescale, 映像か)</summary>
    private static Dictionary<uint, (uint Timescale, bool IsVideo)> TrackTable(byte[] moov)
    {
        var table = new Dictionary<uint, (uint, bool)>();
        foreach (var (type, start, size) in Boxes(moov, 0, moov.Length))
        {
            if (type != "trak") continue;
            var trak = moov.AsSpan((int)start, (int)size).ToArray();
            uint id = 0, ts = 0;
            bool video = false;
            foreach (var (t2, s2, z2) in Boxes(trak, 0, trak.Length))
            {
                var b = trak.AsSpan((int)s2, (int)z2);
                if (t2 == "tkhd" && b.Length >= 24)
                    id = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(b[0] == 1 ? 20 : 12));
                else if (t2 == "mdia")
                {
                    var mdia = b.ToArray();
                    foreach (var (t3, s3, z3) in Boxes(mdia, 0, mdia.Length))
                    {
                        var c = mdia.AsSpan((int)s3, (int)z3);
                        if (t3 == "mdhd") ts = ReadTimeHeader(c).Timescale;
                        else if (t3 == "hdlr" && c.Length >= 12) video = Encoding.ASCII.GetString(c.Slice(8, 4)) == "vide";
                    }
                }
            }
            if (id != 0 && ts != 0) table[id] = (ts, video);
        }
        return table;
    }

    /// <summary>moof 1個分の、映像トラックの終了時刻(秒)。映像トラックが無ければ全トラックの最大</summary>
    private static double FragmentEnd(byte[] moof, Dictionary<uint, (uint Timescale, bool IsVideo)> tracks)
    {
        double videoEnd = 0, anyEnd = 0;
        foreach (var (type, start, size) in Boxes(moof, 0, moof.Length))
        {
            if (type != "traf") continue;
            var traf = moof.AsSpan((int)start, (int)size).ToArray();
            uint trackId = 0, defaultDur = 0;
            ulong baseTime = 0;
            ulong total = 0;
            foreach (var (t2, s2, z2) in Boxes(traf, 0, traf.Length))
            {
                var b = traf.AsSpan((int)s2, (int)z2);
                if (b.Length < 8) continue;
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
                if (t2 == "tfhd")
                {
                    trackId = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                    int o = 8;
                    if ((flags & 0x01) != 0) o += 8;
                    if ((flags & 0x02) != 0) o += 4;
                    if ((flags & 0x08) != 0 && b.Length >= o + 4) defaultDur = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
                }
                else if (t2 == "tfdt")
                {
                    baseTime = b[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(b.Slice(4)) : BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                }
                else if (t2 == "trun")
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                    int o = 8;
                    if ((flags & 0x001) != 0) o += 4;
                    if ((flags & 0x004) != 0) o += 4;
                    int per = 0;
                    if ((flags & 0x100) != 0) per += 4;
                    if ((flags & 0x200) != 0) per += 4;
                    if ((flags & 0x400) != 0) per += 4;
                    if ((flags & 0x800) != 0) per += 4;
                    if ((flags & 0x100) == 0)
                    {
                        total += (ulong)count * defaultDur;
                    }
                    else
                    {
                        for (uint i = 0; i < count && o + 4 <= b.Length; i++, o += per)
                            total += BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
                    }
                }
            }
            if (!tracks.TryGetValue(trackId, out var tr)) continue;
            double end = (double)(baseTime + total) / tr.Timescale;
            anyEnd = Math.Max(anyEnd, end);
            if (tr.IsVideo) videoEnd = Math.Max(videoEnd, end);
        }
        return videoEnd > 0 ? videoEnd : anyEnd;
    }

    private static double? FromMoov(byte[] moov)
    {
        uint movieTimescale = 0;
        double? movie = null, video = null, fragmented = null;

        foreach (var (type, start, size) in Boxes(moov, 0, moov.Length))
        {
            var body = moov.AsSpan((int)start, (int)size);
            switch (type)
            {
                case "mvhd":
                    (movieTimescale, var dur) = ReadTimeHeader(body);
                    if (movieTimescale > 0 && dur > 0) movie = (double)dur / movieTimescale;
                    break;

                case "trak":
                    var t = FromTrak(body.ToArray());
                    if (t is TrakInfo ti && ti.IsVideo && ti.Seconds > 0) video ??= ti.Seconds;
                    break;

                case "mvex":
                    foreach (var (mt, ms, msize) in Boxes(body.ToArray(), 0, size))
                    {
                        if (mt != "mehd") continue;
                        var mb = body.Slice((int)ms, (int)msize);
                        ulong fd = mb[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(mb.Slice(4)) : BinaryPrimitives.ReadUInt32BigEndian(mb.Slice(4));
                        if (movieTimescale > 0 && fd > 0) fragmented = (double)fd / movieTimescale;
                    }
                    break;
            }
        }
        return video ?? movie ?? fragmented;
    }

    private record struct TrakInfo(bool IsVideo, double Seconds);

    private static TrakInfo? FromTrak(byte[] trak)
    {
        foreach (var (type, start, size) in Boxes(trak, 0, trak.Length))
        {
            if (type != "mdia") continue;
            var mdia = trak.AsSpan((int)start, (int)size).ToArray();
            bool isVideo = false;
            double seconds = 0;
            foreach (var (mt, ms, msize) in Boxes(mdia, 0, mdia.Length))
            {
                var b = mdia.AsSpan((int)ms, (int)msize);
                if (mt == "hdlr" && b.Length >= 12)
                    isVideo = Encoding.ASCII.GetString(b.Slice(8, 4)) == "vide";
                else if (mt == "mdhd")
                {
                    var (ts, dur) = ReadTimeHeader(b);
                    if (ts > 0) seconds = (double)dur / ts;
                }
            }
            return new TrakInfo(isVideo, seconds);
        }
        return null;
    }

    /// <summary>mvhd / mdhd 共通: version, flags, 作成/更新時刻, timescale, duration</summary>
    private static (uint Timescale, ulong Duration) ReadTimeHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < 20) return (0, 0);
        if (b[0] == 1)
        {
            if (b.Length < 32) return (0, 0);
            return (BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20)), BinaryPrimitives.ReadUInt64BigEndian(b.Slice(24)));
        }
        return (BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12)), BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16)));
    }

    // ---- box walking ----

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
            else if (size == 0)
            {
                size = to - pos;
            }
            if (size < hlen || pos + size > to) yield break;
            yield return (type, pos + hlen, size - hlen);
            pos += size;
        }
    }

    private static IEnumerable<(string Type, long BodyStart, long BodySize)> Boxes(byte[] buf, long from, long to)
    {
        using var ms = new MemoryStream(buf, 0, buf.Length, false);
        foreach (var b in Boxes(ms, from, to)) yield return b;
    }

    private static byte[] ReadBytes(Stream s, long start, long size)
    {
        if (size > 512L * 1024 * 1024) throw new InvalidDataException("moov too large");
        var buf = new byte[size];
        s.Position = start;
        int read = 0;
        while (read < size)
        {
            int n = s.Read(buf, read, (int)(size - read));
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }
}
