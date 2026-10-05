using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fh6.Core;

/// <summary>session.json(ロガー v2 のサイドカー JSON と同じキーを session/markers/stats に持つ)</summary>
public sealed class SessionMeta
{
    public string Format { get; set; } = "fh6-recorder/1";
    public string AppVersion { get; set; } = typeof(SessionMeta).Assembly.GetName().Version?.ToString(3) ?? "";
    public bool Complete { get; set; }
    public SessionInfo Session { get; set; } = new();
    public SessionFiles Files { get; set; } = new();
    public VideoInfo Video { get; set; } = new();
    public List<Marker> Markers { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = new();
    public WriterStats? Stats { get; set; }

    /// <summary>このクラスが知らないキー。読んで書き戻しても消さないために持つ</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public const string FileName = "session.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void AddEvent(string type, string? detail = null) =>
        Events.Add(new SessionEvent { RecvTime = Clock.Now(), Type = type, Detail = detail });

    /// <summary>セッションフォルダの session.json を読む</summary>
    public static SessionMeta Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException(Strings.T("session.json が空です"));
    }

    /// <summary>録画ファイルを探す。files.video → video.output_path(とフォルダ内の同名)→ フォルダ内の *.mp4 の順</summary>
    public string? FindVideo(string folder)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(Files.Video))
            candidates.Add(Path.IsPathRooted(Files.Video) ? Files.Video : Path.Combine(folder, Files.Video));
        if (!string.IsNullOrEmpty(Video.OutputPath))
        {
            candidates.Add(Video.OutputPath);
            candidates.Add(Path.Combine(folder, Path.GetFileName(Video.OutputPath)));
        }
        candidates.AddRange(Directory.EnumerateFiles(folder, "*.mp4"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 手動補正の点を session.json の video.manual_sync の末尾に足す。保存の直前に読み直すので、他のアプリが足したキーや出来事は残る。
    /// 記録中(complete が false)の記録には書かない(記録アプリが上書きするため)。書けなければ理由を返す
    /// </summary>
    public static string? AppendManualSync(string folder, ManualSyncPoint point)
    {
        var meta = Load(folder);
        if (!meta.Complete) return Strings.T("記録中の記録には保存できません(記録アプリで記録を止めてから保存してください)");
        meta.Video.ManualSync.Add(point);
        meta.Save(folder);
        return null;
    }

    /// <summary>
    /// 出来事を events の末尾に足す(再生アプリでレースに名前を付けたときの race_edited など)。
    /// 保存の直前に読み直すので、知らないキーや他のアプリが足した出来事は残る。記録中の記録には書かない。書けなければ理由を返す
    /// </summary>
    public static string? AppendEvent(string folder, SessionEvent e)
    {
        var meta = Load(folder);
        if (!meta.Complete) return Strings.T("記録中の記録には保存できません(記録アプリで記録を止めてから保存してください)");
        meta.Events.Add(e);
        meta.Save(folder);
        return null;
    }

    /// <summary>手動補正の点を全部消す(video.manual_sync を空にする)。書けなければ理由を返す</summary>
    public static string? ClearManualSync(string folder)
    {
        var meta = Load(folder);
        if (!meta.Complete) return Strings.T("記録中の記録は変えられません(記録アプリで記録を止めてからにしてください)");
        meta.Video.ManualSync.Clear();
        meta.Save(folder);
        return null;
    }

    public void Save(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class SessionInfo
{
    public string Label { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsReference { get; set; }
    public bool? Autodrive { get; set; }
    public string? Weather { get; set; }
    public string? TimeOfDay { get; set; }
    public string? CarHint { get; set; }
    public string? TuneId { get; set; }
    public Assists Assists { get; set; } = new();
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public double StartedRecvTime { get; set; }
    public double? EndedRecvTime { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Assists
{
    public string? Abs { get; set; }
    public string? Tcs { get; set; }
    public string? Stm { get; set; }
    public string? Line { get; set; }
    public string? Shift { get; set; }
}

public sealed class SessionFiles
{
    public string TelemetryCsv { get; set; } = SessionWriter.CsvName;
    public string? TelemetryRaw { get; set; }
    /// <summary>セッションフォルダ内なら相対名、外なら絶対パス</summary>
    public string? Video { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VideoInfo
{
    public bool Requested { get; set; }
    /// <summary>録画中なら true(OBS 側で止められたら false になる)</summary>
    public bool Active { get; set; }
    public bool RecordedIntoSessionFolder { get; set; }
    public string? ObsVersion { get; set; }
    public string? OutputPath { get; set; }

    /// <summary>
    /// 動画の 0 秒に相当する recv_time(epoch 秒)。sync_samples から求めた値(無ければ開始イベントの受信時刻)。
    /// 動画が途中で伸び縮みしている可能性があるので、同期再生では sync_samples の補間を優先すること。
    /// </summary>
    public double? VideoZeroRecvTime { get; set; }

    /// <summary>VideoZeroRecvTime の求め方("started_event" / "sync_samples")。無い記録は sync_samples 由来で約 1 秒遅い</summary>
    public string? VideoZeroMethod { get; set; }

    /// <summary>
    /// [recv_time, 動画内の時刻(秒), 問い合わせ往復(ms)] の並び。録画中に約2秒ごとに
    /// OBS の GetRecordStatus.outputDuration を記録したもの。
    /// テレメトリ時刻 → 動画時刻は、この点列を区分線形補間して求める。
    /// </summary>
    public List<double[]> SyncSamples { get; set; } = new();

    /// <summary>sync_samples を直線で当てはめた傾き(動画秒 / 実時間秒)。1.0 なら伸び縮みなし</summary>
    public double? ClockRate { get; set; }

    /// <summary>録画の開始を要求した時刻(StartRecord を送る直前)</summary>
    public double? StartRequestedRecvTime { get; set; }

    public double? StartedEventRecvTime { get; set; }
    public double? StopRequestedRecvTime { get; set; }
    public double? StoppedEventRecvTime { get; set; }

    /// <summary>録画ファイルから読んだ実際の長さ(秒)。MP4 のみ</summary>
    public double? FileDurationSec { get; set; }

    /// <summary>録画ファイルの映像のフレームレート(fps。サンプル数 ÷ 長さ)。MP4 のみ</summary>
    public double? FileFps { get; set; }

    /// <summary>
    /// 終端の検算: (停止要求時刻 − video_zero_recv_time)− file_duration_sec。
    /// 0 に近いほど sync_samples が動画の時間軸と合っている。
    /// </summary>
    public double? EndCheckSec { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// 手動補正で合わせた点(再生アプリが書く)。自動で求めた値とは別に持ち、上書きしない。
    /// 今は最後の1点だけを使い、全体を平行にずらす。
    /// </summary>
    public List<ManualSyncPoint> ManualSync { get; set; } = new();

    /// <summary>録画の画質の段階と、実際に OBS に設定した値(録画しなかった記録と古い記録には無い)</summary>
    public VideoQuality? Quality { get; set; }

    /// <summary>録画した OBS のシーン(録画しなかった記録と 2026-10-05 より前の記録には無い)</summary>
    public VideoScene? Scene { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>録画した OBS のシーン(docs/session-format.md「録画したシーン」)</summary>
public sealed class VideoScene
{
    /// <summary>記録アプリで選んだシーン。null なら OBS で今選んでいるシーンのまま</summary>
    public string? Selected { get; set; }
    /// <summary>録画したシーン</summary>
    public string? Name { get; set; }
    public bool Applied { get; set; }
    /// <summary>切り替える前のシーン(切り替えたときだけ)</summary>
    public string? Previous { get; set; }
    public string? Error { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>録画の画質(docs/session-format.md「録画の画質」)</summary>
public sealed class VideoQuality
{
    public string Preset { get; set; } = "obs";
    public bool Applied { get; set; }
    /// <summary>録画に使った OBS のプロファイル(切り替えたときだけ)</summary>
    public string? Profile { get; set; }
    public int? OutputWidth { get; set; }
    public int? OutputHeight { get; set; }
    public double? Fps { get; set; }
    /// <summary>OBS の出力モード(Simple / Advanced)</summary>
    public string? OutputMode { get; set; }
    public string? RecQuality { get; set; }
    public int? BitrateKbps { get; set; }
    public string? Error { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>「このテレメトリー時刻 = 動画のこの秒」と手で合わせた点</summary>
public sealed class ManualSyncPoint
{
    public double RecvTime { get; set; }
    public double VideoSec { get; set; }
    public string? Note { get; set; }
    public string? CreatedAt { get; set; }
}

public sealed class Marker
{
    public double RecvTime { get; set; }
    public uint? TimestampMs { get; set; }
    public string Label { get; set; } = "";
}

public sealed class SessionEvent
{
    public double RecvTime { get; set; }
    public string Type { get; set; } = "";
    public string? Detail { get; set; }

    /// <summary>出来事ごとの値(車両・レース・コースの出来事。docs/session-format.md)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Data { get; set; }
}
