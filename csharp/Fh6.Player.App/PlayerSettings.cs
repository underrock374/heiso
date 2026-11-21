using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Player;

/// <summary>
/// 再生アプリの設定(%LOCALAPPDATA%\HEISO\Player\player.json)。
/// 「セッションフォルダを開く」で最初に出すフォルダ(前回開いた記録の 1 つ上 = 記録アプリの保存先)と、ウィンドウの大きさと位置。
/// 画面の設定(印・再生・グラフなど)は View(ViewSettings)。ウィンドウは WPF の部品なのでここに持つ。
/// </summary>
public sealed class PlayerSettings
{
    public string? LastRoot { get; set; }

    /// <summary>前回、配布パッケージを書き出したフォルダ</summary>
    public string? LastExportDir { get; set; }

    /// <summary>前回、配布パッケージを開いたフォルダ</summary>
    public string? LastPackageDir { get; set; }

    /// <summary>
    /// ffmpeg.exe の場所(ファイルかフォルダ)。空なら PATH と、HEISO のフォルダの近くの ffmpeg フォルダを探す。
    /// 720p で書き出すときに使う(ffmpeg は同梱しない)
    /// </summary>
    public string? FfmpegPath { get; set; }

    /// <summary>720p で書き出すときのエンコーダ(h264_nvenc など)。空か "auto" なら、この PC で使えるものを順に(NVIDIA → AMD → Intel → CPU)</summary>
    public string? ExportEncoder { get; set; }

    /// <summary>書き出しの方法: "copy"(元の画質のまま)/ "720"(ffmpeg で 720p に)。最後に選んだもの</summary>
    public string? ExportMode { get; set; }

    /// <summary>画面の言語: "auto"(Windows の表示言語が日本語なら日本語、それ以外は英語)/ "ja" / "en"。空なら auto</summary>
    public string? Language { get; set; }

    /// <summary>
    /// リプレイの映像を前へずらす量の既定(コマ)。164031 のリプレイ(30fps)で、ブレーキランプ・変速の音とグラフを見比べて合わせた量
    /// (167 ms = 30fps の 5 コマ。2026-10-11)。60fps の動画で同じ時間かは確かめていない
    /// </summary>
    public const int DefaultReplayLeadFrames = 5;

    /// <summary>
    /// リプレイを新しく割り当てたときに、「同期の微調整」で前へずらしておく量(その動画のコマ。負なら後ろへ)。空なら DefaultReplayLeadFrames。
    /// 自動で見つけて「採用」したときと、「いまのコマ = リプレイの先頭」「= ゴール」で決めたときに、その動画のずらし量(session.json の video_alt の shift_sec)に入れる。
    /// 割り当て済みのリプレイは変えない
    /// </summary>
    public int? ReplayLeadFrames { get; set; }

    /// <summary>新しく割り当てたリプレイのずらし量(秒。前へずらすので負)。fps はその動画のフレームレート(分からなければ 30)</summary>
    public double ReplayDefaultShiftSec(double? fps) =>
        Math.Round(-(ReplayLeadFrames ?? DefaultReplayLeadFrames) / (fps is double f && f > 0 ? f : 30), 4);

    /// <summary>画面の設定(ViewSettings)。null ならまだ保存が無い(前の版は画面の localStorage に置いていた。画面が一度だけ引き継いで送る)</summary>
    public ViewSettings? View { get; set; }

    /// <summary>
    /// 記録ごとに、最後に表示していた割り当てた動画(新しい順、LastVideosMax 件まで)。録画を表示していた記録は載せない。
    /// 開いたときにその動画を表示する(spec「開き直したときの表示と、割り当て済みのリプレイ」の A)。見るための状態なので、session.json には書かない
    /// </summary>
    public List<LastVideo>? LastVideos { get; set; }

    public const int LastVideosMax = 200;

    /// <summary>その記録で最後に表示していた割り当てた動画の id(録画だったか、覚えていなければ null)</summary>
    public string? LastVideoOf(string session) => LastVideos?.FirstOrDefault(v => v.Session == session)?.Id;

    /// <summary>その記録で表示している動画を覚えて保存する(id が null なら録画。忘れる)。変わらなければ保存しない</summary>
    public void SetLastVideo(string session, string? id)
    {
        var list = LastVideos ??= [];
        int i = list.FindIndex(v => v.Session == session);
        if (id == null ? i < 0 : i == 0 && list[0].Id == id) return;
        if (i >= 0) list.RemoveAt(i);
        if (id != null)
        {
            list.Insert(0, new LastVideo(session, id));
            if (list.Count > LastVideosMax) list.RemoveRange(LastVideosMax, list.Count - LastVideosMax);
        }
        Save();
    }

    /// <summary>ウィンドウの大きさと位置を覚えるか(画面の設定のダイアログで切り替える)</summary>
    public bool RememberWindow { get; set; }

    /// <summary>最後に閉じたときのウィンドウ(RememberWindow のときだけ保存する)</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>
    /// 動画を DirectComposition のビデオオーバーレイで表示するか(既定は使わない)。
    /// 2026-09-28、WebView2 の GPU のプロセスがビデオオーバーレイの表示(SwapChainPresenter)で固まり、
    /// 見張り役に作り直された後、映像だけが止まって音は鳴り続けた。オーバーレイを使わなければ、その処理を通らない。
    /// 使うと GPU の負荷は少し下がる。ここを true にすると使う(画面の設定には出していない)
    /// </summary>
    public bool VideoOverlays { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => PlayerPaths.SettingsFile;

    public static PlayerSettings Load(string? path = null)
    {
        PlayerSettings s;
        try { s = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new(); }
        catch { s = new(); }
        s._path = path;
        return s;
    }

    /// <summary>読んだファイル(Save で書く先)。null なら DefaultPath</summary>
    private string? _path;

    public void Save(string? path = null)
    {
        path ??= _path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* 保存できなくても開く操作は続ける */ }
    }

    /// <summary>
    /// フォルダ選択で最初に出すフォルダ。前回開いた記録の 1 つ上があればそこ、無ければ記録アプリの既定の保存先(ビデオ\FH6Recorder)。
    /// </summary>
    public string? InitialFolder()
    {
        if (!string.IsNullOrEmpty(LastRoot) && Directory.Exists(LastRoot)) return LastRoot;
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FH6Recorder");
        return Directory.Exists(def) ? def : null;
    }

    /// <summary>書き出す場所の最初のフォルダ: 前回書き出したフォルダ、無ければセッションフォルダの 1 つ上</summary>
    public string ExportFolder(string sessionFolder)
    {
        if (!string.IsNullOrEmpty(LastExportDir) && Directory.Exists(LastExportDir)) return LastExportDir;
        return Path.GetDirectoryName(Path.GetFullPath(sessionFolder).TrimEnd('\\', '/')) ?? sessionFolder;
    }

    /// <summary>パッケージを開く画面の最初のフォルダ: 前回開いたフォルダ、前回書き出したフォルダの順</summary>
    public string? PackageFolder() =>
        new[] { LastPackageDir, LastExportDir }.FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));

    /// <summary>セッションフォルダを開けたら、その保存先(1 つ上。アーカイブの中なら、その上)を覚える</summary>
    public void Remember(string sessionFolder)
    {
        var parent = Fh6.Core.SessionCatalog.SaveRootOf(sessionFolder);
        if (string.IsNullOrEmpty(parent) || parent == LastRoot) return;
        LastRoot = parent;
        Save();
    }
}

/// <summary>
/// 記録で最後に表示していた割り当てた動画。Session は記録を始めた日時(フォルダ名の頭。yyyyMMdd_HHmmss)で、
/// アーカイブへ移しても、フォルダ名のメモを変えても同じ記録とみなす
/// </summary>
public sealed record LastVideo(string Session, string Id);

/// <summary>ウィンドウの位置と大きさ(最大化していたときは、元に戻したときの位置と大きさ)</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
