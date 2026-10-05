using System.Text.Json;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// 録画の画質の段階。段階ごとに OBS のプロファイル「HEISO ○○」を用意しておき、録画の間だけ切り替える。
/// プロファイルの中身(出力解像度・fps・録画画質)は --setup-obs-profiles で作る(OBS の画面で自由に直してよい)。
/// OBS の設定を外から書き換えると録画用のエンコーダが作り直されず録画できなくなるため、プロファイルごと切り替える(2026-09-27 に実機で確認)。
/// </summary>
/// <param name="Height">出力解像度の高さ。幅は基本(キャンバス)解像度の縦横比から決める</param>
/// <param name="RecQuality">OBS「基本」出力モードの録画画質(Stream = 配信と同じ / Small = 高画質・中 / HQ = 区別のつかない画質)</param>
/// <param name="MbPerMin">目安の容量(MB/分)。画面の表示用</param>
public sealed record QualityPreset(string Id, string Name, int Height, int Fps, string RecQuality, int? BitrateKbps, int MbPerMin, string? Note = null)
{
    public const string ProfilePrefix = "HEISO ";

    /// <summary>画面に出す段階の名前(今の言語)</summary>
    public string DisplayName => Id switch
    {
        "minimum" => Strings.T("最小"),
        "light" => Strings.T("軽量"),
        "medium" => Strings.T("中間"),
        "standard" => Strings.T("標準"),
        "high" => Strings.T("高画質"),
        _ => Name,
    };

    /// <summary>画面に出す補足(今の言語)</summary>
    public string? DisplayNote => Note == null ? null : Id == "high" ? Strings.T("ゲームが 60fps で動いているときだけ") : Note;

    /// <summary>この段階で新しく作る OBS のプロファイル名(今の言語。英語なら「HEISO Light」など)</summary>
    public string ProfileName => ProfileNameIn(Strings.Language);

    /// <summary>その言語でのプロファイル名</summary>
    public string ProfileNameIn(string language) => ProfilePrefix + Strings.TIn(language, Name);

    /// <summary>この段階のプロファイルとして認める名前(今の言語の名前が先。日本語で作ったものも英語で作ったものも使う)</summary>
    public IEnumerable<string> ProfileNames => new[] { ProfileName, ProfileNameIn("ja"), ProfileNameIn("en") }.Distinct();

    /// <summary>OBS にあるプロファイルのうち、この段階のもの(無ければ null)</summary>
    public string? FindProfile(IEnumerable<string> profiles)
    {
        var set = profiles as ICollection<string> ?? profiles.ToList();
        return ProfileNames.FirstOrDefault(set.Contains);
    }
}

public static class QualityPresets
{
    /// <summary>OBS の設定を何も変えない</summary>
    public const string Obs = "obs";

    public static readonly IReadOnlyList<QualityPreset> All = new[]
    {
        new QualityPreset("minimum",  "最小",   540, 30, "Stream", 2500, 19),
        new QualityPreset("light",    "軽量",   720, 30, "Stream", 6000, 45),
        new QualityPreset("medium",   "中間",  1080, 30, "Stream", 10000, 75),
        new QualityPreset("standard", "標準",  1080, 30, "Small", null, 125),
        new QualityPreset("high",     "高画質", 1080, 60, "HQ", null, 250, "ゲームが 60fps で動いているときだけ"),
    };

    public static QualityPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static bool IsKnown(string? id) => id == Obs || Find(id) != null;

    /// <summary>出力解像度(基本解像度の縦横比で幅を決める。エンコーダのため偶数にそろえる。基本解像度より大きくしない)</summary>
    public static (int Width, int Height) OutputSize(QualityPreset p, int baseWidth, int baseHeight)
    {
        int h = Math.Min(p.Height, baseHeight > 0 ? baseHeight : p.Height);
        h -= h % 2;
        double aspect = baseWidth > 0 && baseHeight > 0 ? (double)baseWidth / baseHeight : 16.0 / 9;
        int w = (int)Math.Round(h * aspect / 2) * 2;
        return (w, h);
    }
}

/// <summary>保存先フォルダの小さな JSON ファイル(最後に選んだ段階、戻し損ねた OBS のプロファイル)</summary>
public static class RecorderFiles
{
    public const string PrefsName = ".recorder_prefs.json";
    public const string RestoreName = ".recorder_obs_restore.json";

    private static readonly JsonSerializerOptions Json = SessionMeta.JsonOptions;

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class RecorderPrefs
{
    public string Quality { get; set; } = QualityPresets.Obs;
    /// <summary>録画するシーン(ObsSceneService)。null なら OBS で今選んでいるシーンのまま</summary>
    public string? Scene { get; set; }
}

/// <summary>録画の前に使っていた OBS のプロファイル(録画の後に戻す)</summary>
public sealed class ObsProfileRestore
{
    public string Profile { get; set; } = "";
    public string SavedAt { get; set; } = "";
}
