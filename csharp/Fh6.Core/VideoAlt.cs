using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Core;

/// <summary>
/// 記録の録画のほかに割り当てた動画(session.json の video_alt[])。リプレイと、利用者が選んだ別の動画。
/// 形式は docs/session-format.md「割り当てた動画」、作業指示 docs/tasks/008-replay-external-video.md
/// </summary>
public sealed class VideoAlt
{
    public const string KindReplay = "replay";
    public const string KindExternal = "external";
    public const string ContentLive = "live";
    public const string ContentReplay = "replay";

    public string Id { get; set; } = "";
    /// <summary>replay(記録の動画の中から見つけたリプレイ)/ external(利用者が選んだ動画)</summary>
    public string Kind { get; set; } = KindExternal;
    /// <summary>live(ライブ走行の動画)/ replay(リプレイの動画)</summary>
    public string Content { get; set; } = ContentLive;
    public string? Label { get; set; }
    /// <summary>動画の絶対パス(その PC のもの。パッケージには入れない)</summary>
    public string? Path { get; set; }
    /// <summary>セッションフォルダの中にあるときだけ、フォルダからの相対パス</summary>
    public string? PathRel { get; set; }
    public VideoAltFile File { get; set; } = new();
    /// <summary>どのレースに付けたか(レースの開始の行の recv_time)</summary>
    public double RaceStartRecvTime { get; set; }
    public VideoAltSync Sync { get; set; } = new();
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// 動画を探す。path_rel → path の順。見つかっても、サイズか長さ(0.5 秒より大きく)が割り当てたときと違えば Changed。
    /// duration は長さを読む関数(テストで差し替える。既定は VideoDuration.TryRead)
    /// </summary>
    public VideoAltLocation Locate(string sessionFolder, Func<string, double?>? duration = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(PathRel)) candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(sessionFolder, PathRel)));
        if (!string.IsNullOrEmpty(Path)) candidates.Add(Path);
        var found = candidates.FirstOrDefault(System.IO.File.Exists);
        if (found == null) return new VideoAltLocation(VideoAltStatus.Missing, null);
        if (File.SizeBytes is long size && new FileInfo(found).Length != size) return new VideoAltLocation(VideoAltStatus.Changed, found);
        if (File.DurationSec is double want && (duration ?? VideoDuration.TryRead)(found) is double got && Math.Abs(got - want) > 0.5)
            return new VideoAltLocation(VideoAltStatus.Changed, found);
        return new VideoAltLocation(VideoAltStatus.Found, found);
    }

    /// <summary>動画のパスを決める(path と、フォルダの中なら path_rel)。サイズと更新日時も読み直す。長さと fps は呼ぶ側が入れる</summary>
    public void SetPath(string sessionFolder, string videoPath)
    {
        var full = System.IO.Path.GetFullPath(videoPath);
        Path = full;
        var folder = System.IO.Path.GetFullPath(sessionFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        PathRel = full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full[folder.Length..] : null;
        var fi = new FileInfo(full);
        File.SizeBytes = fi.Length;
        File.MtimeUtc = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

public sealed class VideoAltFile
{
    public long? SizeBytes { get; set; }
    public double? DurationSec { get; set; }
    public double? Fps { get; set; }
    public string? MtimeUtc { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VideoAltSync
{
    public const string BasisRaceTime = "race_time";
    public const string BasisRecvTime = "recv_time";
    public const string MethodAutoBlackMotion = "auto_black_motion";
    public const string MethodAutoBlack = "auto_black";
    public const string MethodManual = "manual";

    /// <summary>race_time(リプレイ。replay_zero_video_sec で合わせる)/ recv_time(ライブ。points で合わせる)</summary>
    public string Basis { get; set; } = BasisRecvTime;
    /// <summary>auto_black_motion / auto_black / manual</summary>
    public string Method { get; set; } = MethodManual;
    /// <summary>自動のときの確度(high / low)。手動なら null</summary>
    public string? Confidence { get; set; }
    public double? ReplayZeroVideoSec { get; set; }
    public double? ReplayEndVideoSec { get; set; }
    public double? CleanDurationSec { get; set; }
    public VideoAltCheck? Check { get; set; }
    public List<ManualSyncPoint> Points { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VideoAltCheck
{
    public double Corr { get; set; }
    public double LagSec { get; set; }
}

public enum VideoAltStatus
{
    /// <summary>見つかり、割り当てたときと同じ</summary>
    Found,
    /// <summary>見つからない(選び直してもらう)</summary>
    Missing,
    /// <summary>見つかったが、サイズか長さが違う(別の動画かもしれない。選び直してもらう)</summary>
    Changed,
}

public readonly record struct VideoAltLocation(VideoAltStatus Status, string? FoundPath);
