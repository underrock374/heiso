using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Core.Tests;

/// <summary>
/// テスト用の MP4 を組み立てる(OBS の出力と同じ並び: ftyp、mdat、moov)。
/// 映像: timescale 30、1 コマ 1、キーフレーム 30 コマごと、B フレームの表示のずれ(ctts)あり、編集リストの頭 0。
/// 音声: timescale 48000、1 サンプル 1024、編集リストの頭 1024(AAC の頭の詰め物)。
/// サンプルの中身の先頭に「V / A + 番号」を書き、切り出した後にどのサンプルかを確かめられるようにする
/// </summary>
internal static class TestMp4
{
    public const int Fps = 30, KeyInterval = 30, AudioDelta = 1024, AudioTs = 48000, AudioPriming = 1024;

    public static int Ctts(int i) => i % 2 == 0 ? 1 : 0;

    public static void Write(string path, double seconds)
    {
        int vCount = (int)Math.Round(seconds * Fps);
        int aCount = (int)Math.Ceiling((seconds * AudioTs + AudioPriming) / AudioDelta);
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));

        using var mdat = new MemoryStream();
        var vOff = new long[vCount];
        var aOff = new long[aCount];
        var vSize = new int[vCount];
        var aSize = new int[aCount];
        long mdatBody = ftyp.Length + 16;   // mdat は 64bit の見出し
        for (int i = 0; i < vCount; i++)
        {
            vOff[i] = mdatBody + mdat.Position;
            vSize[i] = 40 + i % 7;
            mdat.Write(Sample('V', i, vSize[i]));
        }
        for (int i = 0; i < aCount; i++)
        {
            aOff[i] = mdatBody + mdat.Position;
            aSize[i] = 20;
            mdat.Write(Sample('A', i, aSize[i]));
        }

        var video = Trak(1, "vide", Fps, vCount, 1, vSize, vOff, editMediaTime: 0, seconds,
                         ctts: Enumerable.Range(0, vCount).Select(Ctts).ToArray(),
                         sync: Enumerable.Range(0, vCount).Where(i => i % KeyInterval == 0).ToArray());
        var audio = Trak(2, "soun", AudioTs, aCount, AudioDelta, aSize, aOff, editMediaTime: AudioPriming, seconds, null, null);
        var moov = Box("moov", Full("mvhd", 0, U32(0), U32(0), U32(1000), U32((uint)(seconds * 1000)), U32(0x00010000),
                                     new byte[2 + 10 + 36 + 24], U32(3)), video, audio);

        using var f = File.Create(path);
        f.Write(ftyp);
        var hdr = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(hdr, 4);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), (ulong)(16 + mdat.Length));
        f.Write(hdr);
        f.Write(mdat.ToArray());
        f.Write(moov);
    }

    /// <summary>サンプルの中身の先頭から、種類と番号を読む</summary>
    public static (char Kind, int Index) ReadSample(string path, long offset)
    {
        using var f = File.OpenRead(path);
        f.Position = offset;
        var b = new byte[5];
        f.ReadExactly(b);
        return ((char)b[0], BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(1)));
    }

    private static byte[] Sample(char kind, int index, int size)
    {
        var b = new byte[size];
        b[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), index);
        return b;
    }

    private static byte[] Trak(uint id, string handler, uint ts, int count, int delta, int[] sizes, long[] offsets,
                               long editMediaTime, double seconds, int[]? ctts, int[]? sync)
    {
        bool vide = handler == "vide";
        var tkhd = Full("tkhd", 3, U32(0), U32(0), U32(id), U32(0), U32((uint)(seconds * 1000)), new byte[8],
                        new byte[8], new byte[36], U32(vide ? 1920u << 16 : 0), U32(vide ? 1080u << 16 : 0));
        var elst = Full("elst", 0, U32(1), U32((uint)(seconds * 1000)), U32((uint)editMediaTime), U32(0x00010000));
        var mdhd = Full("mdhd", 0, U32(0), U32(0), U32(ts), U32((uint)(count * delta)), new byte[] { 0x55, 0xC4, 0, 0 });
        var hdlr = Full("hdlr", 0, U32(0), Encoding.ASCII.GetBytes(handler), new byte[12], Encoding.ASCII.GetBytes("Handler\0"));
        var tables = new List<byte[]>
        {
            Full("stsd", 0, U32(0)),
            Full("stts", 0, U32(1), U32((uint)count), U32((uint)delta)),
        };
        if (ctts != null)
            tables.Add(Full("ctts", 0, new[] { U32((uint)count) }.Concat(ctts.SelectMany(c => new[] { U32(1), U32((uint)c) })).ToArray()));
        if (sync != null)
            tables.Add(Full("stss", 0, new[] { U32((uint)sync.Length) }.Concat(sync.Select(s => U32((uint)s + 1))).ToArray()));
        tables.Add(Full("stsc", 0, U32(1), U32(1), U32(1), U32(1)));   // 1 チャンク 1 サンプル
        tables.Add(Full("stsz", 0, new[] { U32(0), U32((uint)count) }.Concat(sizes.Select(s => U32((uint)s))).ToArray()));
        tables.Add(Full("stco", 0, new[] { U32((uint)count) }.Concat(offsets.Select(o => U32((uint)o))).ToArray()));
        var header = vide ? Full("vmhd", 1, new byte[8]) : Full("smhd", 0, new byte[4]);
        var dinf = Box("dinf", Full("dref", 0, U32(1), Full("url ", 1)));
        return Box("trak", tkhd, Box("edts", elst),
                   Box("mdia", mdhd, hdlr, Box("minf", header, dinf, Box("stbl", tables.ToArray()))));
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    private static byte[] Full(string type, uint flags, params byte[][] parts) =>
        Box(type, new[] { U32(flags & 0xFFFFFF) }.Concat(parts).ToArray());
}

public sealed class Mp4CutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-mp4cut-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void 直前のキーフレームから切り出す()
    {
        var src = Path.Combine(_dir, "src.mp4");
        var dst = Path.Combine(_dir, "dst.mp4");
        TestMp4.Write(src, 10);

        var r = Mp4Cut.Cut(src, dst, 4.4, 6.25);

        // 4.4 秒の前のキーフレームは 120 コマ目(表示の時刻は ctts の 1 を足して 121/30 秒)
        Assert.Equal(121.0 / 30, r.StartSec, 6);
        // 終わりは 6.25 秒(187.5 → 188)までに始まるコマまで: 120〜188 の 69 コマ
        Assert.Equal(69, r.VideoSamples);
        Assert.Equal(189.0 / 30, r.EndSec, 6);

        var tracks = Mp4Cut.Inspect(dst);
        var v = tracks.Single(t => t.Handler == "vide");
        var a = tracks.Single(t => t.Handler == "soun");
        Assert.Equal(69, v.Samples);
        Assert.Equal(new[] { 0, 30, 60 }, v.KeyFrames);                      // 先頭がキーフレーム
        Assert.Equal(('V', 120), TestMp4.ReadSample(dst, v.FirstSampleOffset));
        Assert.Equal(1, v.EditMediaTime);                                    // 最初のコマの表示の時刻から見せる

        // 音声: 映像の頭(121/30 秒)と同じ時刻を含むサンプルから。頭の詰め物(1024)を含めて 193,600 + 1024 → 190 番目の途中
        long want = (long)Math.Floor(121.0 / 30 * TestMp4.AudioTs) + TestMp4.AudioPriming;
        int a0 = (int)(want / TestMp4.AudioDelta);
        Assert.Equal(('A', a0), TestMp4.ReadSample(dst, a.FirstSampleOffset));
        Assert.Equal(want - a0 * TestMp4.AudioDelta, a.EditMediaTime);
        Assert.Equal(r.AudioSamples, a.Samples);

        // 長さとフレームレートは VideoDuration で読める
        Assert.Equal(69.0 / 30, VideoDuration.TryRead(dst)!.Value, 2);
        Assert.Equal(30, VideoDuration.TryReadFrameRate(dst)!.Value, 1);
    }

    [Fact]
    public void 範囲が動画の外にはみ出しても切れる()
    {
        var src = Path.Combine(_dir, "src.mp4");
        var dst = Path.Combine(_dir, "dst.mp4");
        TestMp4.Write(src, 5);
        var r = Mp4Cut.Cut(src, dst, -3, 99);
        Assert.Equal(1.0 / 30, r.StartSec, 6);
        Assert.Equal(150, r.VideoSamples);
    }

    [Fact]
    public void FragmentedMP4は断る()
    {
        var frag = Path.Combine(AppContext.BaseDirectory, "testdata", "video", "fragmented.mp4");
        Assert.Throws<NotSupportedException>(() => Mp4Cut.Cut(frag, Path.Combine(_dir, "x.mp4"), 0, 1));
    }
}

public sealed class PackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("heiso-package-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private const double Zero = 1000.0;   // 動画の 0 秒の recv_time
    private const double RaceStart = 1010.0;

    /// <summary>
    /// 偽の記録: 20 秒、30Hz。10 秒からレース(45 m/s で東へ)、12〜14.5 秒は一時停止(フォトモード)、16 秒でゴールして記録の終わりまで停止区間。
    /// 動画は 20 秒。手動補正で 0.1 秒ずらしてある。
    /// fragment なら 30 秒にして、リザルト画面(16〜24 秒の停止区間)の後に 0.1 秒だけ周回数が 1 増えたレース中の行(ゴール直後の断片)を出す
    /// </summary>
    private string MakeSession(bool fragment = false)
    {
        var folder = Path.Combine(_root, "20260928_120000#テスト");
        Directory.CreateDirectory(folder);
        int seconds = fragment ? 30 : 20;
        TestMp4.Write(Path.Combine(folder, "rec.mp4"), seconds);

        var cols = new[]
        {
            "recv_time", "timestamp_ms", "is_race_on", "race_pos", "cur_race_time", "speed", "position_x", "position_y", "position_z", "yaw",
            "car_ordinal", "car_class", "car_pi", "drivetrain", "num_cylinders", "lap_no", "cur_lap", "dist_traveled",
        };
        var sb = new StringBuilder(string.Join(",", cols) + "\n");
        for (int i = 0; i < seconds * 30; i++)
        {
            double t = Zero + i / 30.0;
            double s = t - RaceStart;
            bool frag = fragment && t >= 1024 && t < 1024.1;
            bool off = (t >= 1012 && t < 1014.5) || (t >= 1016 && !frag);
            double[] v;
            if (off) v = new double[cols.Length];
            else if (frag)   // リザルト画面の分だけ時計が進み、周回数が 1 増えている
                v = new double[] { 0, i * 33, 1, 1, 11.5, 30, 162, 10, 0, 0, 1234, 4, 738, 2, 6, 1, 0, 162 };
            else
            {
                bool racing = t >= RaceStart;
                double x = !racing ? 0 : t < 1012 ? 45 * s : 90 + 45 * (t - 1014.5);
                double rt = !racing ? 0 : t < 1012 ? s : 2.0 + (t - 1014.5);
                v = new double[] { 0, i * 33, 1, racing ? 1 : 0, rt, racing ? 45 : 0, x, 10, 0, 0, 1234, 4, 738, 2, 6, 0, rt, racing ? x : -5 };
            }
            v[0] = t;
            sb.Append(string.Join(",", v.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))).Append('\n');
        }
        using (var gz = new GZipStream(File.Create(Path.Combine(folder, "telemetry.csv.gz")), CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(sb.ToString()));

        var meta = new SessionMeta
        {
            Complete = true,
            Files = new SessionFiles { Video = "rec.mp4", TelemetryRaw = "telemetry.raw.gz" },
            Video = new VideoInfo
            {
                StartedEventRecvTime = Zero, VideoZeroRecvTime = Zero, VideoZeroMethod = "started_event", FileFps = 30, FileDurationSec = 20,
                OutputPath = @"D:\MyUser\Video\rec.mp4",
                SyncSamples = { new[] { 1002.0, 1.0, 20 }, new[] { 1011.0, 10.0, 20 } },
                ManualSync = { new ManualSyncPoint { RecvTime = RaceStart, VideoSec = 10.1, Note = "スタート" } },
            },
            Events =
            {
                new SessionEvent { RecvTime = 999.9, Type = "obs_record_started", Detail = @"D:\MyUser\Video\rec.mp4" },
                new SessionEvent { RecvTime = 1000.5, Type = "car_detected", Data = new JsonObject { ["ordinal"] = 1234, ["name"] = "テストカー", ["setup_name"] = "標準" } },
                new SessionEvent { RecvTime = RaceStart, Type = "race_start", Data = new JsonObject { ["course_name"] = "テストコース" } },
            },
            Stats = new WriterStats(),
        };
        meta.Save(folder);
        return folder;
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void レース1本を書き出す()
    {
        var folder = MakeSession();
        var dest = Path.Combine(_root, "out", PackageWriter.DefaultFileName("20260928_120000#テスト", "テストコース", "テスト/カー"));
        Assert.EndsWith("20260928_120000#テスト_テストコース_テスト_カー.heiso.zip", dest);

        var result = PackageWriter.Write(folder, RaceStart, dest);
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!, ".heiso-*"));   // 一時ファイルを残さない

        using var zip = ZipFile.OpenRead(dest);
        Assert.Equal(new[] { "package.json", "session.json", "telemetry.csv.gz", "video.mp4" }, zip.Entries.Select(e => e.FullName).Order());
        Assert.Equal(zip.GetEntry("video.mp4")!.Length, zip.GetEntry("video.mp4")!.CompressedLength);   // 動画は圧縮しない

        // 動画: レース開始(動画 10.1 秒 = 手動補正込み)の 5 秒前 = 5.1 秒の前のキーフレームは 150 コマ目(表示 151/30 秒)
        var info = result.Info;
        double cut = 151.0 / 30;
        Assert.Equal(Math.Round(cut, 4), info.Video.CutFromSec);
        Assert.Equal(Math.Round(20 - cut, 4), info.Video.DurationSec);
        Assert.Equal(("manual", 0.1), (info.Video.SyncMethod, info.Video.ManualShiftSec));

        // package.json
        Assert.Equal("heiso-package/1", info.Format);
        Assert.Equal(("fh6", "world", "20260928_120000#テスト"), (info.Game, info.PositionKind, info.SourceSession));
        Assert.Equal("テストコース", info.Course.Name);
        Assert.Equal(("テストカー", "標準", 1234, 4, 738, 2, 6),
            (info.Car.Name, info.Car.Setup, info.Car.Ordinal, info.Car.ClassRaw, info.Car.Pi, info.Car.DrivetrainRaw, info.Car.Cylinders));
        Assert.True(info.Race.Finished);
        Assert.Equal(("log_end", 1), (info.Race.FinishedBy, info.Race.Laps));   // 周回数が 0 のままのイベントを完走 = 1 周
        Assert.Equal(RaceStart, info.Race.StartRecvTime, 6);
        Assert.Equal(2.0 + (479 / 30.0 - 14.5), info.Race.RaceTimeSec!.Value, 3);
        var skip = Assert.Single(info.Skips.Ranges);
        Assert.Equal("pause", skip.Kind);
        Assert.Equal(1, info.Skips.Count);
        Assert.Equal(435 / 30.0 - 359 / 30.0, info.Skips.TotalSec, 3);
        Assert.Equal(skip.FromRecvTime - Zero + 0.1 - cut, skip.FromVideoSec, 0.001);   // パッケージの動画の秒(ミリ秒に丸めてある)
        Assert.Equal(info.Race.EndRecvTime - info.Race.StartRecvTime - info.Skips.TotalSec, info.Skips.CleanSec, 0.002);

        // package.json のキーは snake_case
        var pj = JsonNode.Parse(ReadEntry(zip, "package.json"))!;
        Assert.Equal("テストカー", (string)pj["car"]!["name"]!);
        Assert.NotNull(pj["video"]!["cut_from_sec"]);

        // session.json: 同じテレメトリー時刻が、切り出した動画では切り出しの頭の分だけ前の秒になる
        var oldMeta = SessionMeta.Load(folder);
        var newMeta = JsonSerializer.Deserialize<SessionMeta>(ReadEntry(zip, "session.json"), SessionMeta.JsonOptions)!;
        var oldMap = VideoTimeMap.Create(oldMeta.Video, VideoSyncMethod.StartedEvent, out _)!;
        var newMap = VideoTimeMap.Create(newMeta.Video, VideoSyncMethod.StartedEvent, out _)!;
        var oldSamples = VideoTimeMap.Create(oldMeta.Video, VideoSyncMethod.SyncSamples, out _)!;
        var newSamples = VideoTimeMap.Create(newMeta.Video, VideoSyncMethod.SyncSamples, out _)!;
        foreach (var r in new[] { 1005.0, RaceStart, 1013.3, 1019.0 })
        {
            Assert.Equal(oldMap.ToVideoSec(r) - cut, newMap.ToVideoSec(r), 6);
            Assert.Equal(oldSamples.ToVideoSec(r) - cut, newSamples.ToVideoSec(r), 6);
        }
        Assert.Null(newMeta.Video.OutputPath);                       // 利用者の PC のパスを入れない
        Assert.Equal(("video.mp4", "telemetry.csv.gz", (string?)null), (newMeta.Files.Video, newMeta.Files.TelemetryCsv, newMeta.Files.TelemetryRaw));
        Assert.DoesNotContain(newMeta.Events, e => e.Type == "obs_record_started");   // 範囲の外の出来事
        Assert.Contains(newMeta.Events, e => e.Type == "race_start");
        Assert.Null(newMeta.Stats);
        Assert.DoesNotContain("MyUser", ReadEntry(zip, "session.json"));

        // テレメトリー: 範囲内の行だけ、見出しはそのまま
        using var csv = new StreamReader(new GZipStream(zip.GetEntry("telemetry.csv.gz")!.Open(), CompressionMode.Decompress));
        Assert.StartsWith("recv_time,timestamp_ms,", csv.ReadLine());
        var times = new List<double>();
        while (csv.ReadLine() is string line) times.Add(double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture));
        Assert.Equal(result.TelemetryRows, times.Count);
        double r0 = Zero + cut - 0.1, r1 = Zero + 20 - 0.1;
        Assert.All(times, t => Assert.InRange(t, r0 - 1e-6, r1 + 1e-6));
        Assert.InRange(times.Count, 449, 450);   // 148〜597 行目(4.93〜19.9 秒)
    }

    [Fact]
    public void ゴール直後の断片があっても_動画はゴールの5秒後で終える()
    {
        // 断片(24 秒)から数えると 29 秒まで = リザルト画面(ランキング)がまるごと入る
        var folder = MakeSession(fragment: true);
        var info = PackageWriter.Write(folder, RaceStart, Path.Combine(_root, "f.heiso.zip")).Info;

        double goal = 479 / 30.0 + 0.1;   // ゴールの行の、元の動画での秒(手動補正 0.1 秒込み)
        double end = info.Video.CutFromSec + info.Video.DurationSec;
        Assert.InRange(end, goal + PackageWriter.MarginSec, goal + PackageWriter.MarginSec + 0.1);
        Assert.Equal(("lap", true), (info.Race.FinishedBy, info.Race.Finished));
        Assert.Equal(Zero + 479 / 30.0, info.Race.EndRecvTime, 6);                     // ゴールの行
        Assert.Equal(2.0 + (479 / 30.0 - 14.5), info.Race.RaceTimeSec!.Value, 3);      // 断片の時計(11.5 秒)ではない
        Assert.Equal(1, info.Race.Laps);
    }

    [Fact]
    public void カンマの地域の形式でも_記録を読んで書き出したパッケージの数値はピリオド()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");   // 1.5 を「1,5」と書く形式
            var folder = MakeSession();
            var dest = Path.Combine(_root, "fr.heiso.zip");
            var info = PackageWriter.Write(folder, RaceStart, dest).Info;
            Assert.Equal(2.0 + (479 / 30.0 - 14.5), info.Race.RaceTimeSec!.Value, 3);   // 記録の CSV を正しく読めている

            using var zip = ZipFile.OpenRead(dest);
            var pj = ReadEntry(zip, "package.json");
            Assert.Matches(@"""race_time_sec"": 3\.4\d+", pj);
            Assert.Equal(info.Video.CutFromSec, PackageInfo.Parse(pj).Video.CutFromSec);   // 読み直しても同じ値
            using var csv = new StreamReader(new GZipStream(zip.GetEntry("telemetry.csv.gz")!.Open(), CompressionMode.Decompress));
            csv.ReadLine();
            var row = csv.ReadLine()!;
            Assert.Matches(@"^\d+\.\d+,", row);   // recv_time はピリオド、区切りはカンマのまま
            Assert.Equal(18, row.Split(',').Length);   // 偽の記録の列の数(小数点のカンマで列が増えていない)
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void 書き出せないときは理由を返す()
    {
        var folder = MakeSession();
        var dest = Path.Combine(_root, "x.heiso.zip");
        Assert.Contains("見つかりません", Assert.Throws<PackageException>(() => PackageWriter.Write(folder, 1234.5, dest)).Message);

        var meta = SessionMeta.Load(folder);
        meta.Complete = false;
        meta.Save(folder);
        Assert.Contains("記録中", Assert.Throws<PackageException>(() => PackageWriter.Write(folder, RaceStart, dest)).Message);

        meta.Complete = true;
        meta.Video.StartedEventRecvTime = null;
        meta.Video.SyncSamples.Clear();
        meta.Save(folder);
        Assert.Contains("--resync", Assert.Throws<PackageException>(() => PackageWriter.Write(folder, RaceStart, dest)).Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void 展開して開ける()
    {
        var folder = MakeSession();
        var dest = Path.Combine(_root, "p.heiso.zip");
        PackageWriter.Write(folder, RaceStart, dest);
        var cache = Path.Combine(_root, "cache");

        var dir = PackageReader.Extract(dest, cache);
        Assert.StartsWith(Path.Combine(cache, "p_"), dir);
        foreach (var f in new[] { "package.json", "session.json", "telemetry.csv.gz", "video.mp4" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        var meta = SessionMeta.Load(dir);
        Assert.Equal(Path.Combine(dir, "video.mp4"), meta.FindVideo(dir));
        Assert.Equal("テストコース", PackageInfo.TryLoad(dir)!.Course.Name);

        // 2 回目は展開し直さない(展開先で保存した手動補正などが残る)
        File.WriteAllText(Path.Combine(dir, "memo.txt"), "x");
        Assert.Equal(dir, PackageReader.Extract(dest, cache));
        Assert.True(File.Exists(Path.Combine(dir, "memo.txt")));

        // パッケージからもう一度は作れない
        Assert.Throws<PackageException>(() => PackageWriter.Write(dir, RaceStart, Path.Combine(_root, "again.heiso.zip")));
    }

    [Fact]
    public void 知らない形式は開かない()
    {
        var path = Path.Combine(_root, "new.heiso.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("package.json").Open()))
            w.Write("{\"format\":\"heiso-package/2\"}");
        var ex = Assert.Throws<PackageException>(() => PackageReader.Extract(path, Path.Combine(_root, "cache")));
        Assert.Contains("新しい版", ex.Message);

        var notZip = Path.Combine(_root, "bad.heiso.zip");
        File.WriteAllText(notZip, "not a zip");
        Assert.Throws<PackageException>(() => PackageReader.Extract(notZip, Path.Combine(_root, "cache")));
    }
}
