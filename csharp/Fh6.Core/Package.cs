using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Core;

/// <summary>配布パッケージを作れない・開けない(理由は利用者にそのまま見せてよい文)</summary>
public sealed class PackageException(string message) : Exception(message);

/// <summary>
/// 配布パッケージ(.heiso.zip)のメタデータ package.json。形式は docs/session-format.md「配布パッケージ」。
/// 分からない値は null。キーは snake_case(session.json と同じ)
/// </summary>
public sealed class PackageInfo
{
    public const string FormatPrefix = "heiso-package/";
    public const int FormatVersion = 1;
    public const string FileName = "package.json";
    public const string Extension = ".heiso.zip";
    public const string VideoName = "video.mp4";

    public string Format { get; set; } = FormatPrefix + FormatVersion;
    public string AppVersion { get; set; } = typeof(PackageInfo).Assembly.GetName().Version?.ToString(3) ?? "";
    public string CreatedAt { get; set; } = "";
    /// <summary>ゲーム("fh6")</summary>
    public string Game { get; set; } = "fh6";
    /// <summary>位置の種類("world" = ゲーム内の世界座標。x = 東、y = 上、z = 北の左手系)</summary>
    public string PositionKind { get; set; } = "world";
    /// <summary>元の記録のフォルダ名</summary>
    public string SourceSession { get; set; } = "";
    public PackageCourse Course { get; set; } = new();
    public PackageCar Car { get; set; } = new();
    public bool? Autodrive { get; set; }
    public Assists Assists { get; set; } = new();
    /// <summary>視点(chase / hood / cockpit など)。今は記録していないので null</summary>
    public string? View { get; set; }
    /// <summary>ライブ録画か("live")リプレイ録画か("replay")。今は記録していないので null</summary>
    public string? Capture { get; set; }
    public PackageRace Race { get; set; } = new();
    public PackageVideo Video { get; set; } = new();
    public PackageSkips Skips { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static PackageInfo Parse(string json) =>
        JsonSerializer.Deserialize<PackageInfo>(json, SessionMeta.JsonOptions) ?? throw new InvalidDataException(Strings.T("package.json が空です"));

    /// <summary>フォルダに package.json があれば読む(展開したパッケージか)。無ければ null</summary>
    public static PackageInfo? TryLoad(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>形式の番号。読めなければ -1</summary>
    [JsonIgnore]
    public int Version =>
        Format.StartsWith(FormatPrefix, StringComparison.Ordinal) && int.TryParse(Format[FormatPrefix.Length..], out var v) ? v : -1;
}

public sealed class PackageCourse
{
    /// <summary>登録名(記録アプリで一致・登録したもの)。無ければ null</summary>
    public string? Name { get; set; }
    /// <summary>レース開始の位置(m)と向き(yaw、ラジアン)。コースの照合に使う</summary>
    public double StartX { get; set; }
    public double StartZ { get; set; }
    public double StartYaw { get; set; }
}

public sealed class PackageCar
{
    public string? Name { get; set; }
    public string? Setup { get; set; }
    /// <summary>レース開始の行のテレメトリーの値(car_ordinal、car_class、car_pi、drivetrain、num_cylinders)</summary>
    public int? Ordinal { get; set; }
    public int? ClassRaw { get; set; }
    public int? Pi { get; set; }
    public int? DrivetrainRaw { get; set; }
    public int? Cylinders { get; set; }
}

public sealed class PackageRace
{
    /// <summary>レース開始の行と、ゴールの行(ゴール直後の断片を除いた、レース中の最後の行)の recv_time(epoch 秒)</summary>
    public double StartRecvTime { get; set; }
    public double EndRecvTime { get; set; }
    /// <summary>停止区間の明けに経過時間が 0 に戻って見つけたレース開始(リスタート)</summary>
    public bool Restart { get; set; }
    /// <summary>完走したか(推定。RaceSegment.LikelyFinished)</summary>
    public bool Finished { get; set; }
    /// <summary>完走・中断を何で決めたか(RaceSegment.FinishedBy: lap / results / menu / log_end / speed)</summary>
    public string FinishedBy { get; set; } = "";
    /// <summary>race_end / next_start / log_end</summary>
    public string EndReason { get; set; } = "";
    /// <summary>レースタイム(秒。ゴールの行 = ゴール直後の断片を除いたレース中の最後の行の cur_race_time)。完走でなければ null</summary>
    public double? RaceTimeSec { get; set; }
    /// <summary>走り終えた周の数(ゴールした周を含む)</summary>
    public int Laps { get; set; }
}

public sealed class PackageVideo
{
    public string File { get; set; } = PackageInfo.VideoName;
    public double DurationSec { get; set; }
    public double? Fps { get; set; }
    /// <summary>元の録画の何秒から切り出したか(キーフレームの時刻)</summary>
    public double CutFromSec { get; set; }
    /// <summary>動画とテレメトリーの時刻の対応の決め方: "started_event"(OBS の録画開始イベント)/ "manual"(手動補正あり)</summary>
    public string SyncMethod { get; set; } = "started_event";
    /// <summary>手動補正のずらし量(秒)。補正なしなら 0</summary>
    public double ManualShiftSec { get; set; }
    /// <summary>作り直した(720p など)ときの中身。元の画質のまま切り出したなら null</summary>
    public PackageReencode? Reencoded { get; set; }
}

/// <summary>書き出しで動画を作り直した中身(ffmpeg)</summary>
public sealed class PackageReencode
{
    /// <summary>エンコーダの ID(h264_nvenc / h264_amf / h264_qsv / libx264)</summary>
    public string Encoder { get; set; } = "";
    /// <summary>出力の高さ(元がこれより小さければ元のまま)</summary>
    public int Height { get; set; }
    public int VideoKbps { get; set; }
    public int AudioKbps { get; set; }
    public string? FfmpegVersion { get; set; }
}

/// <summary>書き出しで動画を作り直すときの指定(ffmpeg の場所と中身)</summary>
public sealed record PackageReencodeOptions(string Ffmpeg, Ffmpeg.Options Encode);

public sealed class PackageSkips
{
    public int Count { get; set; }
    public double TotalSec { get; set; }
    /// <summary>レースの長さ(開始〜最後の行)からスキップ区間を除いた長さ(秒)</summary>
    public double CleanSec { get; set; }
    public List<PackageSkip> Ranges { get; set; } = new();
}

public sealed class PackageSkip
{
    /// <summary>redo(やり直し)/ pause(一時停止)</summary>
    public string Kind { get; set; } = "";
    public double FromRecvTime { get; set; }
    public double ToRecvTime { get; set; }
    /// <summary>パッケージの動画での秒</summary>
    public double FromVideoSec { get; set; }
    public double ToVideoSec { get; set; }
}

/// <summary>書き出しの結果</summary>
public sealed record PackageResult(string Path, long Bytes, PackageInfo Info, int TelemetryRows, double ElapsedSec);

/// <summary>
/// 記録(セッションフォルダ)のレース 1 本を .heiso.zip に書き出す。
/// 動画は作り直さずに切り出す(Mp4Cut)。切り出しの頭はレース開始の MarginSec 前より前のキーフレーム、終わりはゴールの行(RaceSegment.FinishIndex)の MarginSec 後。
/// reencode を渡すと、ffmpeg で作り直す(720p など)。そのときは頭をキーフレームに合わせる必要が無いので、レース開始のちょうど MarginSec 前のコマから
/// </summary>
public static class PackageWriter
{
    public const double MarginSec = 5;
    public const string TelemetryName = "telemetry.csv.gz";

    private static readonly string[] Columns = RaceSample.Columns
        .Concat(RaceSkips.Columns)
        .Concat(new[] { "lap_no" })   // 完走の判定(ゴールで周回数が増えたか)と周回数
        .Distinct().ToArray();

    /// <param name="raceStartRecvTime">書き出すレースの開始の recv_time(RaceSegment.StartRecvTime)</param>
    /// <param name="reencode">作り直すなら ffmpeg の場所と中身。null なら元の画質のまま切り出す</param>
    /// <param name="progress">進み具合(0〜1)。作り直すときの ffmpeg の進みを 0〜0.95 に、Zip を書き終えたら 1</param>
    public static PackageResult Write(string sessionFolder, double raceStartRecvTime, string destPath,
                                      PackageReencodeOptions? reencode = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (PackageInfo.TryLoad(sessionFolder) != null) throw new PackageException(Strings.T("パッケージから、もう一度パッケージは作れません。元の記録を開いてください"));

        SessionMeta meta;
        try { meta = SessionMeta.Load(sessionFolder); }
        catch (Exception ex) { throw new PackageException(Strings.T("session.json を読めませんでした: {error}", ("error", ex.Message))); }
        if (!meta.Complete) throw new PackageException(Strings.T("記録中の記録からは作れません(記録アプリで記録を止めてからにしてください)"));

        var video = meta.FindVideo(sessionFolder) ?? throw new PackageException(Strings.T("録画ファイルが見つかりません"));
        var map = VideoTimeMap.Create(meta.Video, VideoSyncMethod.StartedEvent, out _);
        if (map == null || map.IsFallback || meta.Video.StartedEventRecvTime is not double startedEvent)
            throw new PackageException(Strings.T("録画開始の時刻が記録されていません(古い記録は「HeisoRecorder --resync」で作り直してください)"));

        var csvPath = Path.Combine(sessionFolder, string.IsNullOrEmpty(meta.Files.TelemetryCsv) ? SessionWriter.CsvName : meta.Files.TelemetryCsv);
        if (!File.Exists(csvPath)) throw new PackageException(Strings.T("テレメトリーのファイルがありません: {file}", ("file", Path.GetFileName(csvPath))));
        var t = TelemetryTable.Load(csvPath, Columns);
        var race = RaceSegmentDetector.Detect(t).FirstOrDefault(r => Math.Abs(r.StartRecvTime - raceStartRecvTime) < RaceNames.SameRowSec)
                   ?? throw new PackageException(Strings.T("指定したレースが記録の中に見つかりません"));

        // ---- 動画を切る(一時ファイルに。書き出し先と同じフォルダ) ----
        var destDir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        Directory.CreateDirectory(destDir);
        var tmpVideo = Path.Combine(destDir, $".heiso-{Guid.NewGuid():N}.mp4");
        var tmpZip = destPath + ".part";
        try
        {
            double from = Math.Max(0, map.ToVideoSec(race.StartRecvTime) - MarginSec);
            // 終わりはゴールの行(ゴール直後の断片を除いた、レース中の最後の行)から。断片はリザルト画面の後に出るので、
            // 最後の行から数えるとリザルト画面(ランキング = 自分や他のプレイヤーの名前)がまるごと入る
            double to = map.ToVideoSec(t[TelemetryTable.RecvTime][race.FinishIndex]) + MarginSec;
            Mp4Cut.Result cut;
            PackageReencode? reencoded = null;
            if (reencode == null)
            {
                try { cut = Mp4Cut.Cut(video, tmpVideo, from, to); }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or EndOfStreamException)
                {
                    throw new PackageException(Strings.T("録画ファイルを切り出せませんでした: {error}", ("error", ex.Message)));
                }
            }
            else
            {
                (cut, reencoded) = Reencode(video, tmpVideo, from, to, meta.Video.FileFps, reencode, progress, ct);
            }
            ct.ThrowIfCancellationRequested();

            // 切り出した範囲のテレメトリー時刻
            double r0 = map.ToRecvTime(cut.StartSec), r1 = map.ToRecvTime(cut.EndSec);

            // ---- session.json と package.json ----
            var newMeta = CutSession(meta, cut, r0, r1, race.StartRecvTime);
            newMeta.Video.FileFps ??= VideoDuration.TryReadFrameRate(tmpVideo);   // 古い記録には file_fps が無い
            var newMap = VideoTimeMap.Create(newMeta.Video, VideoSyncMethod.StartedEvent, out _)!;
            var info = BuildInfo(meta, t, race, cut, map, newMap, Path.GetFileName(Path.GetFullPath(sessionFolder).TrimEnd('\\', '/')));
            info.Video.Fps = newMeta.Video.FileFps;
            info.Video.Reencoded = reencoded;

            // ---- Zip(一時ファイルに書いてから名前を変える) ----
            int rows;
            using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, PackageInfo.FileName, JsonSerializer.Serialize(info, SessionMeta.JsonOptions));
                WriteText(zip, SessionMeta.FileName, JsonSerializer.Serialize(newMeta, SessionMeta.JsonOptions));
                using (var es = zip.CreateEntry(TelemetryName, CompressionLevel.NoCompression).Open())
                    rows = CutTelemetry(csvPath, es, r0, r1);
                using (var es = zip.CreateEntry(PackageInfo.VideoName, CompressionLevel.NoCompression).Open())
                using (var vs = File.OpenRead(tmpVideo))
                    vs.CopyTo(es, 1 << 20);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tmpZip, destPath, overwrite: true);
            progress?.Report(1);
            return new PackageResult(destPath, new FileInfo(destPath).Length, info, rows, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            TryDelete(tmpVideo);
            TryDelete(tmpZip);
        }
    }

    /// <summary>
    /// ffmpeg で from〜to を作り直す。頭はコマの境目に合わせる(from の直後のコマの時刻 = 新しい動画の 0 秒)。
    /// 終わりは元の動画の長さを超えない。作り直した動画の実際の長さで、切り出しの結果(元の動画のどこからどこまでか)を返す
    /// </summary>
    private static (Mp4Cut.Result Cut, PackageReencode Info) Reencode(string video, string dest, double from, double to, double? fileFps,
                                                                      PackageReencodeOptions r, IProgress<double>? progress, CancellationToken ct)
    {
        double fps = fileFps ?? VideoDuration.TryReadFrameRate(video) ?? 30;
        from = Math.Ceiling(from * fps - 1e-6) / fps;   // コマの境目(そのコマから始まる)
        if (VideoDuration.TryRead(video) is double len) to = Math.Min(to, len);
        if (to <= from) throw new PackageException(Strings.T("切り出す範囲がありません"));
        var o = r.Encode;
        var sub = progress == null ? null : new Progress<double>(v => progress.Report(v * 0.95));
        try
        {
            Ffmpeg.Transcode(r.Ffmpeg, video, dest, from, to - from, fps, o, sub, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new PackageException(Strings.T("動画を作り直せませんでした: {error}", ("error", ex.Message)));
        }
        double actual = VideoDuration.TryRead(dest) ?? (to - from);
        return (new Mp4Cut.Result(from, from + actual, 0, 0), new PackageReencode
        {
            Encoder = o.Encoder, Height = o.Height, VideoKbps = o.VideoKbps, AudioKbps = o.AudioKbps, FfmpegVersion = Ffmpeg.Version(r.Ffmpeg),
        });
    }

    /// <summary>既定のファイル名: &lt;フォルダ名&gt;_&lt;コース&gt;_&lt;車&gt;.heiso.zip(分からないものは付けない。ファイル名に使えない文字は _)</summary>
    public static string DefaultFileName(string sessionFolderName, string? course, string? car)
    {
        var parts = new[] { sessionFolderName, course, car }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim());
        var name = string.Join("_", parts);
        var bad = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return name + PackageInfo.Extension;
    }

    /// <summary>
    /// 切り出した範囲に合わせた session.json。動画の 0 秒を切り出した頭(cut.StartSec)に合わせ、同じテレメトリー時刻が同じコマを指すようにする。
    /// 出来事と印は範囲内のもの(と、このレースを後から直したもの)だけ。利用者の PC のパスと、記録全体の統計は入れない
    /// </summary>
    public static SessionMeta CutSession(SessionMeta meta, Mp4Cut.Result cut, double r0, double r1, double raceStartRecvTime)
    {
        // 深いコピー(知らないキーも残す)
        var m = JsonSerializer.Deserialize<SessionMeta>(JsonSerializer.Serialize(meta, SessionMeta.JsonOptions), SessionMeta.JsonOptions)!;
        double s = cut.StartSec;
        var v = m.Video;
        v.VideoZeroRecvTime += s;
        v.StartedEventRecvTime += s;
        // (同期に使う値なので丸めない)
        v.SyncSamples = v.SyncSamples.Select(p => p.Select((x, i) => i == 1 ? x - s : x).ToArray()).ToList();
        foreach (var p in v.ManualSync) p.VideoSec -= s;
        v.FileDurationSec = Math.Round(cut.EndSec - cut.StartSec, 4);
        v.EndCheckSec = null;   // 録画の終わりの検算は元の録画についてのもの
        v.OutputPath = null;
        v.RecordedIntoSessionFolder = true;
        m.Files.Video = PackageInfo.VideoName;
        m.Files.TelemetryCsv = TelemetryName;
        m.Files.TelemetryRaw = null;
        m.Stats = null;

        bool InRange(double t) => t >= r0 && t <= r1;
        m.Markers = m.Markers.Where(k => InRange(k.RecvTime)).ToList();
        m.Events = m.Events.Where(e => InRange(e.RecvTime) || AboutRace(e, raceStartRecvTime)).ToList();
        return m;
    }

    /// <summary>このレースについて後から直した出来事(race_edited / course_confirmed)</summary>
    private static bool AboutRace(SessionEvent e, double raceStart) =>
        e.Type is "race_edited" or "course_confirmed" && e.Data?["race_start_recv_time"] is System.Text.Json.Nodes.JsonValue jv
        && jv.TryGetValue<double>(out var t) && Math.Abs(t - raceStart) < RaceNames.SameRowSec;

    private static PackageInfo BuildInfo(SessionMeta meta, TelemetryTable t, RaceSegment race, Mp4Cut.Result cut,
                                         VideoTimeMap map, VideoTimeMap newMap, string sourceSession)
    {
        var (car, setup) = RaceNames.Car(meta.Events, race.StartRecvTime);
        int? Int(string col) => t.Contains(col) && double.IsFinite(t[col][race.StartIndex]) ? (int)t[col][race.StartIndex] : null;

        var skips = RaceSkips.Find(t, new[] { race });
        double raceSec = t[TelemetryTable.RecvTime][race.FinishIndex] - race.StartRecvTime;
        double skipSec = skips.Sum(g => g.Sec);

        double? raceTime = null;
        // ゴールの行で取る(ゴール直後の断片の行は、リザルト画面の間に進んだ時計の値になっている)
        if (race.LikelyFinished && t.Contains("cur_race_time") && double.IsFinite(t["cur_race_time"][race.FinishIndex]))
            raceTime = Math.Round(t["cur_race_time"][race.FinishIndex], 3);

        return new PackageInfo
        {
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            SourceSession = sourceSession,
            Course = new PackageCourse
            {
                Name = RaceNames.Course(meta.Events, race.StartRecvTime),
                StartX = Math.Round(race.StartX, 3), StartZ = Math.Round(race.StartZ, 3), StartYaw = Math.Round(race.StartYaw, 5),
            },
            Car = new PackageCar
            {
                Name = car, Setup = setup,
                Ordinal = Int("car_ordinal"), ClassRaw = Int("car_class"), Pi = Int("car_pi"),
                DrivetrainRaw = Int("drivetrain"), Cylinders = Int("num_cylinders"),
            },
            Autodrive = meta.Session.Autodrive,
            Assists = meta.Session.Assists,
            Race = new PackageRace
            {
                StartRecvTime = race.StartRecvTime, EndRecvTime = t[TelemetryTable.RecvTime][race.FinishIndex], Restart = race.Restart,
                Finished = race.LikelyFinished, FinishedBy = race.FinishedBy, EndReason = race.EndReason, RaceTimeSec = raceTime, Laps = CountLaps(t, race),
            },
            Video = new PackageVideo
            {
                DurationSec = Math.Round(cut.EndSec - cut.StartSec, 4),
                CutFromSec = Math.Round(cut.StartSec, 4),
                SyncMethod = map.HasManualSync ? "manual" : "started_event",
                ManualShiftSec = Math.Round(map.ManualShiftSec, 4),
            },
            Skips = new PackageSkips
            {
                Count = skips.Count,
                TotalSec = Math.Round(skipSec, 3),
                CleanSec = Math.Round(raceSec - skipSec, 3),
                Ranges = skips.Select(g => new PackageSkip
                {
                    Kind = g.Kind, FromRecvTime = g.FromRecvTime, ToRecvTime = g.ToRecvTime,
                    FromVideoSec = Math.Round(newMap.ToVideoSec(g.FromRecvTime), 3), ToVideoSec = Math.Round(newMap.ToVideoSec(g.ToRecvTime), 3),
                }).ToList(),
            },
        };
    }

    /// <summary>
    /// 走り終えた周の数。レース中の lap_no(終えた周の数)の一番大きい値。
    /// 完走で、ゴールで周回数が増えなかったとき(周回レースの最後の周や、周回数が 0 のままのイベント)は、最後の周の分を 1 足す
    /// </summary>
    private static int CountLaps(TelemetryTable t, RaceSegment race)
    {
        if (!t.Contains("lap_no")) return race.LikelyFinished ? 1 : 0;
        var lap = t["lap_no"];
        var on = t["is_race_on"];
        var pos = t["race_pos"];
        double max = 0;
        for (int i = race.StartIndex; i <= race.EndIndex; i++)
            if (on[i] == 1 && pos[i] >= 1 && double.IsFinite(lap[i])) max = Math.Max(max, lap[i]);
        return (int)max + (race.LikelyFinished && race.FinishedBy != "lap" ? 1 : 0);
    }

    /// <summary>CSV(gzip)の見出しと、recv_time が r0〜r1 の行を、そのままの文字で書く。書いた行の数(見出しを除く)を返す</summary>
    private static int CutTelemetry(string csvPath, Stream dest, double r0, double r1)
    {
        using var src = new GZipStream(File.OpenRead(csvPath), CompressionMode.Decompress);
        using var reader = new StreamReader(src, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        using var gz = new GZipStream(dest, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new StreamWriter(gz, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
        var header = reader.ReadLine() ?? throw new PackageException(Strings.T("テレメトリーの CSV が空です"));
        int col = Array.IndexOf(header.TrimStart('﻿').Split(',').Select(c => c.Trim()).ToArray(), TelemetryTable.RecvTime);
        if (col < 0) throw new PackageException(Strings.T("テレメトリーに recv_time の列がありません"));
        writer.WriteLine(header);
        int rows = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!double.TryParse(Field(line, col), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) continue;
            if (r < r0 || r > r1) continue;
            writer.WriteLine(line);
            rows++;
        }
        return rows;
    }

    /// <summary>カンマ区切りの n 番目(0 起点)の欄</summary>
    private static ReadOnlySpan<char> Field(string line, int n)
    {
        int start = 0;
        for (int i = 0; i < n; i++)
        {
            start = line.IndexOf(',', start);
            if (start < 0) return ReadOnlySpan<char>.Empty;
            start++;
        }
        int end = line.IndexOf(',', start);
        return line.AsSpan(start, (end < 0 ? line.Length : end) - start);
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>
/// .heiso.zip を展開して、再生アプリが普通のセッションフォルダと同じに開けるようにする。
/// 展開先は cacheRoot\&lt;名前&gt;_&lt;ハッシュ&gt;(ハッシュはファイルのフルパス・大きさ・更新日時から)。同じパッケージは展開し直さない
/// </summary>
public static class PackageReader
{
    /// <summary>展開し終えた印のファイル(開くたびに更新日時を新しくする。古いものから消す)</summary>
    public const string DoneMarker = ".extracted";

    /// <summary>残しておく展開先の数(1 つ数百 MB になるため)</summary>
    public const int KeepExtracted = 5;

    private static readonly HashSet<string> KnownEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        PackageInfo.FileName, SessionMeta.FileName, PackageWriter.TelemetryName, PackageInfo.VideoName,
    };

    public static string Extract(string zipPath, string cacheRoot)
    {
        var fi = new FileInfo(zipPath);
        if (!fi.Exists) throw new PackageException(Strings.T("ファイルがありません: {path}", ("path", zipPath)));
        var key = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        var baseName = fi.Name.EndsWith(PackageInfo.Extension, StringComparison.OrdinalIgnoreCase)
            ? fi.Name[..^PackageInfo.Extension.Length] : Path.GetFileNameWithoutExtension(fi.Name);
        if (baseName.Length > 40) baseName = baseName[..40];
        var dir = Path.Combine(cacheRoot, $"{baseName}_{hash}");
        var marker = Path.Combine(dir, DoneMarker);

        if (File.Exists(marker))
        {
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            return dir;
        }

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new PackageException(Strings.T("Zip として読めません(壊れているか、パッケージではありません)")); }
        using (zip)
        {
            var infoEntry = zip.GetEntry(PackageInfo.FileName) ?? throw new PackageException(Strings.T("HEISO のパッケージではありません(package.json がありません)"));
            PackageInfo info;
            using (var r = new StreamReader(infoEntry.Open())) info = PackageInfo.Parse(r.ReadToEnd());
            if (info.Version != PackageInfo.FormatVersion)
                throw new PackageException(info.Version > PackageInfo.FormatVersion
                    ? Strings.T("新しい版のアプリで作ったパッケージです({format})。アプリを新しくしてください", ("format", info.Format))
                    : Strings.T("知らない形式のパッケージです({format})", ("format", info.Format)));
            foreach (var name in new[] { SessionMeta.FileName, PackageWriter.TelemetryName, info.Video.File })
                if (zip.GetEntry(name) == null) throw new PackageException(Strings.T("パッケージに {name} がありません", ("name", name)));

            var tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            // フォルダを含まない、知っている名前のファイルだけ(Zip の中のパスで展開先の外に書かないように)
            foreach (var e in zip.Entries)
            {
                if (e.FullName != e.Name || !(KnownEntries.Contains(e.Name) || e.Name == info.Video.File)) continue;
                e.ExtractToFile(Path.Combine(tmp, e.Name));
            }
            File.WriteAllText(Path.Combine(tmp, DoneMarker), zipPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
        }
        Prune(cacheRoot, dir);
        return dir;
    }

    /// <summary>展開先を新しいものから KeepExtracted 個だけ残す(今開いたものは残す)</summary>
    private static void Prune(string cacheRoot, string keep)
    {
        try
        {
            var old = new DirectoryInfo(cacheRoot).EnumerateDirectories()
                .Where(d => !string.Equals(d.FullName, keep, StringComparison.OrdinalIgnoreCase))
                .Select(d => (Dir: d, Marker: new FileInfo(Path.Combine(d.FullName, DoneMarker))))
                .OrderByDescending(x => x.Marker.Exists ? x.Marker.LastWriteTimeUtc : DateTime.MinValue)
                .Skip(KeepExtracted - 1);
            foreach (var (d, _) in old)
                try { d.Delete(true); } catch { /* 開いている途中などは次の機会に */ }
        }
        catch { }
    }
}
