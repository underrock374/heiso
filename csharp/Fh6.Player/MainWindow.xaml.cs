using System.Diagnostics;
using System.IO;
using System.Windows;
using Fh6.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Fh6.Player;

/// <summary>
/// 外枠。画面(動画・グラフ・操作)はすべて wwwroot の HTML/JS で作り、操作の流れは機能ごとの ViewModel(Fh6.Player.App)が持つ。
/// ここは WebView2(ページ、仮想ホスト、メッセージの受け渡し、読み直しと立て直し)と、閉じるまで待つ画面と、ウィンドウの位置だけを受け持つ
/// </summary>
public partial class MainWindow : Window, IShell
{
    /// <summary>車のプロファイルの置き場所(計測モード)</summary>
    private static readonly ProfileStore Profiles = new(PlayerPaths.ProfilesDir);

    /// <summary>
    /// ウィンドウとメッセージの題名。v0 は 1 本の走りを見る「単走版」として公開する(2 本を並べる並走は v1 から)。
    /// exe 名(HeisoPlayer.exe)は版によらず変えない
    /// </summary>
    public static string AppTitle => $"{Strings.T("HEISO 単走版")} {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

    private readonly string? _initialPath;
    private readonly PlayerSettings _settings = PlayerSettings.Load();
    private bool _initialOpened;
    private readonly OpenedSessionViewModel _opened;
    private readonly SessionsViewModel _sessions;
    private readonly SettingsViewModel _prefs;
    private readonly VideoViewModel _video;
    private readonly ExportViewModel _export;
    private string? _altHostDir;   // alt.heiso に割り当てているフォルダ
    private readonly JobSlot _job = new();   // 書き出しとリプレイを探す処理の「中止」

    // 画面(WebView2 の描画のプロセス)が落ちたときの立て直し: 画面から約 2 秒ごとに届く動画の位置を覚え、読み直した後にそこへ戻す
    private double _lastVideoSec;
    private bool _lastPlaying;
    private double? _restoreSec;
    private bool _restorePlay;
    private bool _crashNoticeClosed;   // 前回の異常終了のお知らせを、画面で閉じた

    public MainWindow(string? initialPath)
    {
        _initialPath = initialPath;
        InitializeComponent();
        _opened = new OpenedSessionViewModel(this, _settings, Profiles, RefreshSession);
        _sessions = new SessionsViewModel(this, _settings, Profiles, () => _opened.Session?.Folder);
        _prefs = new SettingsViewModel(this, _settings);
        _video = new VideoViewModel(this, _settings, _opened, _prefs, _job);
        _export = new ExportViewModel(this, _settings, _opened, _prefs, _job, _video.ActiveAlt);
        Strings.Use(_settings.Language);
        Title = AppTitle;
        RestorePlacement();
        Closing += (_, _) => SavePlacement();
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // 既定では exe の隣にキャッシュを作るので、書き込める場所に置く
            var dataDir = PlayerPaths.WebViewDir;
            // 「自動でスタート3秒前から再生」のため、操作なしでも音の出る動画を再生できるようにする。
            // 動画のビデオオーバーレイは既定で使わない(GPU のプロセスが固まったことがあるため。PlayerSettings.VideoOverlays)
            var args = "--autoplay-policy=no-user-gesture-required";
            if (!_settings.VideoOverlays) args += " --disable-direct-composition-video-overlays";
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir,
                options: new CoreWebView2EnvironmentOptions(args));
            PlayerLog.Info($"WebView2 {env.BrowserVersionString} / {args}");
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            PlayerLog.Error("WebView2 を起動できない", ex);
            MessageBox.Show(this,
                Strings.T("画面の部品(Microsoft Edge WebView2 ランタイム)を起動できませんでした。Microsoft のサイトから「WebView2 ランタイム」を入れてから、もう一度起動してください。") + "\n\n" + ex.Message,
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = Web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(VirtualHosts.App, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
        // Ctrl+ホイールはグラフの拡大・縮小に使うので、画面全体の拡大は止める
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        await RegisterPageDataAsync();
        core.Navigate($"https://{VirtualHosts.App}/index.html");
    }

    private string? _pageScriptId;
    private Task _pageScript = Task.CompletedTask;

    /// <summary>
    /// 画面の言語の辞書と画面の設定を、ページの読み込みより前に渡すスクリプト(SettingsViewModel.PageScript)を登録し直す。
    /// 続けて呼ばれても順に登録する(古い中身が残らないように)。言語を変えたときと、画面が設定を保存したとき
    /// </summary>
    private Task RegisterPageDataAsync() => _pageScript = RegisterAfter(_pageScript);

    private async Task RegisterAfter(Task before)
    {
        await Task.WhenAny(before);   // 前の登録が失敗しても続ける
        if (Web.CoreWebView2 is not { } core) return;
        if (_pageScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_pageScriptId);
        _pageScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(_prefs.PageScript());
    }

    /// <summary>IShell: ページの読み込みより前に渡すものを登録し直す</summary>
    public void RefreshPageData() => _ = RegisterPageDataAsync();

    /// <summary>画面の言語を変える(設定に保存し、辞書を登録し直して、同じ記録・同じ位置で画面を読み直す)</summary>
    private async Task SetLanguageAsync(string? setting)
    {
        _prefs.SetLanguage(setting);
        Title = _opened.Session == null ? AppTitle : $"{AppTitle} - {_opened.DisplayName}";
        await RegisterPageDataAsync();
        _restoreSec = _opened.Session != null ? _lastVideoSec : null;
        _restorePlay = false;
        _restoreQuiet = true;
        Web.CoreWebView2?.Reload();
    }

    private bool _restoreQuiet;   // 読み直した理由が言語の切り替え(「画面が止まった」とは知らせない)
    private int? _restoreRace;    // 読み直した後に選ぶレース(動画を切り替えたとき)

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = MessageJson.Parse(e.WebMessageAsJson, out var parseError);
        if (msg == null)
        {
            PlayerLog.Error($"画面からのメッセージを読めない: {parseError} / {e.WebMessageAsJson[..Math.Min(e.WebMessageAsJson.Length, 300)]}");
            return;
        }
        if (msg is not (ViewPosition or ViewLog)) PlayerLog.Info($"画面から: {MessageJson.TypeName(msg)}");
        switch (msg)
        {
            case ViewPosition m:
                // 動画の位置(画面が落ちたときに戻すため)
                if (m.Sec is double ps) _lastVideoSec = ps;
                _lastPlaying = m.Playing == true;
                break;
            case ViewLog m:
                // 画面(JS)のエラーや操作の記録
                var text = "画面: " + m.Message;
                if (m.Level == "error") PlayerLog.Error(text); else PlayerLog.Info(text);
                break;
            case ViewSetLanguage m:
                _ = Dispatcher.InvokeAsync(() => SetLanguageAsync(m.Language));   // ページを読み直すので、メッセージの処理を終えてから
                break;
            case ViewCrashNoticeClosed:
                _crashNoticeClosed = true;
                break;
            case ViewOpenLogs:
                // ログのフォルダをエクスプローラーで開く(閉じるまで待つ画面ではないので、ここで開いてよい)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PlayerLog.Dir}\"") { UseShellExecute = true }); }
                catch (Exception ex) { PlayerLog.Error("ログのフォルダを開けない", ex); }
                break;
            case ViewReady m:
                // 画面の設定のダイアログに、C# 側で持っている設定を渡す
                Post(_prefs.Prefs());
                // 画面の設定(どの記録にも共通)の同期の方式を使う
                _opened.SetMethod(m.Method);
                SendCrashNotice();
                // セッションを開いた後の読み直しなら、そのデータを送る(画面が落ちた後の立て直しなら、元の位置へ)
                if (_opened.Session != null)
                {
                    _video.SendSession(keepVideo: false);
                    if (_restoreSec is double rs)
                    {
                        Post(new HostRestore(rs, _restorePlay, _restoreQuiet, _restoreRace));
                        _restoreQuiet = false;
                        _restoreRace = null;
                        PlayerLog.Info($"画面を立て直した(動画 {rs:F1} 秒へ{(_restorePlay ? "、再生を続ける" : "")})");
                        _restoreSec = null;
                    }
                }
                else if (!_initialOpened && !string.IsNullOrEmpty(_initialPath))
                {
                    _initialOpened = true;
                    await _opened.OpenAsync(_initialPath);
                }
                break;
            case ViewCancelJob:
                _job.Cancel();
                break;
            default:
                // 割り当てた動画を表示していれば、手動補正はその動画のもの(_video)。録画なら開いている記録のもの(_opened)
                _ = _video.Handle(msg) ?? _opened.Handle(msg) ?? _sessions.Handle(msg) ?? _prefs.Handle(msg) ?? _export.Handle(msg);
                break;
        }
    }

    /// <summary>
    /// 前回が正常に終わっていなければ、画面に知らせる(ログとクラッシュダンプの場所)。
    /// 記録を開くと画面を読み直すので、閉じられるまで画面の準備ができるたびに送る
    /// </summary>
    private void SendCrashNotice()
    {
        if (_crashNoticeClosed || PlayerLog.PreviousUncleanStart is not DateTime since) return;
        Post(new HostCrashNotice(since.ToString("yyyy-MM-dd HH:mm"), PlayerLog.Dir, PlayerLog.LatestCrashDump(since)));
    }

    /// <summary>
    /// WebView2 の中のプロセスが落ちた・止まった。記録し、描画のプロセスか GPU のプロセスなら画面を読み直して同じ記録・同じ位置に戻す
    /// (GPU のプロセスは WebView2 が作り直すが、動画の映像が止まったまま音だけ鳴り続けることがあった。2026-09-28)。
    /// ここは WebView2 のイベントの中なので、閉じるまで待つ画面は開かない(開くなら Dispatcher.InvokeAsync で後から)
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        PlayerLog.Error($"WebView2 のプロセスが落ちた: {e.ProcessFailedKind} / 理由 {e.Reason} / 終了コード {e.ExitCode} / {e.ProcessDescription}");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
            case CoreWebView2ProcessFailedKind.GpuProcessExited:
                _restoreSec = _opened.Session != null ? _lastVideoSec : null;
                _restorePlay = _lastPlaying;
                _ = Dispatcher.InvokeAsync(() => Web.CoreWebView2?.Reload());
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // WebView2 全体が止まった: 画面は使えないので、起動し直してもらう
                _ = Dispatcher.InvokeAsync(() => MessageBox.Show(this,
                    Strings.T("画面の部品(WebView2)が止まりました。お手数ですが、アプリを起動し直してください。") + "\n" +
                    Strings.T("ログ: {dir}", ("dir", PlayerLog.Dir)), AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning));
                break;
            // そのほかのプロセス(ネットワーク・音声など)は WebView2 が自分で起動し直すので、記録だけ
        }
    }

    /// <summary>画面に知らせる(App の例外の処理から呼ぶ)</summary>
    public void NotifyError(string message) => Post(new HostNotice(null, message));

    private bool _choosing;

    /// <summary>
    /// 閉じるまで待つ画面(ダイアログ)を出す。ほかの選ぶ画面が開いていれば出さずに null(続けて押されても、画面は 1 つだけ)。
    /// WebView2 のメッセージの処理の中で、閉じるまで待つ画面を開いてはいけない。開いている間に WebView2 あての処理が届くと、
    /// WebView2 が割り込みを検出して止まり、アプリごと落ちる(2026-09-27〜28 に 8 回。EmbeddedBrowserWebView.dll、0x80000003)。
    /// メッセージの処理を終えてから開く
    /// </summary>
    private async Task<string?> ShowDialogAsync(Func<string?> show)
    {
        if (_choosing) return null;
        _choosing = true;
        try
        {
            return await Dispatcher.InvokeAsync(show);
        }
        finally
        {
            _choosing = false;
        }
    }

    /// <summary>IShell: フォルダを選ぶ画面</summary>
    public Task<string?> PickFolderAsync(string title, string? initialDir) => ShowDialogAsync(() =>
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = initialDir ?? "" };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    });

    /// <summary>IShell: ファイルを選ぶ画面</summary>
    public Task<string?> PickFileAsync(string title, string filter, string? initialDir) => ShowDialogAsync(() =>
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = initialDir ?? "" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    });

    /// <summary>IShell: 保存先を選ぶ画面</summary>
    public Task<string?> PickSaveFileAsync(string title, string filter, string fileName, string? initialDir) => ShowDialogAsync(() =>
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, InitialDirectory = initialDir ?? "", AddExtension = false };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    });

    /// <summary>IShell: 開いた記録を画面に出す。動画を置いたフォルダを仮想ホストに割り当て、ページを読み直す("ready" を受けてからデータを送る)</summary>
    public void ShowSession(LoadedSession session, string? altDir)
    {
        // 割り当ては読み込み済みのページには効かない(WebView2 の挙動。動画が「対応していない形式」になる)ので、読み直す
        var core = Web.CoreWebView2;
        var folder = session.Folder;
        var videoDir = session.VideoPath == null ? null : Path.GetDirectoryName(session.VideoPath);
        bool external = videoDir != null && !string.Equals(videoDir, folder, StringComparison.OrdinalIgnoreCase);
        core.ClearVirtualHostNameToFolderMapping(VirtualHosts.Session);
        core.SetVirtualHostNameToFolderMapping(VirtualHosts.Session, folder, CoreWebView2HostResourceAccessKind.Allow);
        core.ClearVirtualHostNameToFolderMapping(VirtualHosts.Video);
        if (external)
            core.SetVirtualHostNameToFolderMapping(VirtualHosts.Video, videoDir!, CoreWebView2HostResourceAccessKind.Allow);
        if (_altHostDir != null) core.ClearVirtualHostNameToFolderMapping(VirtualHosts.Alt);   // 前の記録で割り当てた動画のフォルダ
        _altHostDir = altDir;   // 最初に表示する割り当てた動画のフォルダ
        if (altDir != null) core.SetVirtualHostNameToFolderMapping(VirtualHosts.Alt, altDir, CoreWebView2HostResourceAccessKind.Allow);

        Title = $"{AppTitle} - {_opened.DisplayName}";
        core.Reload();
    }

    /// <summary>IShell: 割り当てた動画のフォルダを alt.heiso に割り当てる。変わればページを読み直して、その位置へ戻す</summary>
    public bool ShowVideoFolder(string dir, double sec, int? race)
    {
        if (string.Equals(_altHostDir, dir, StringComparison.OrdinalIgnoreCase)) return false;
        var core = Web.CoreWebView2;
        if (_altHostDir != null) core.ClearVirtualHostNameToFolderMapping(VirtualHosts.Alt);
        core.SetVirtualHostNameToFolderMapping(VirtualHosts.Alt, dir, CoreWebView2HostResourceAccessKind.Allow);
        _altHostDir = dir;
        _restoreSec = sec;
        _restorePlay = false;
        _restoreQuiet = true;
        _restoreRace = race;
        core.Reload();
        return true;
    }

    /// <summary>IShell: 画面からのメッセージの処理を終えてから続ける(閉じるまで待つ画面とページの読み直しは、その後で。PickFolderAsync の説明)</summary>
    public async Task AfterMessageAsync() => await Dispatcher.InvokeAsync(() => { });

    /// <summary>覚えていたウィンドウの位置と大きさに戻す。今つながっている画面の外(モニターを外したときなど)なら使わない</summary>
    private void RestorePlacement()
    {
        var w = _settings.Window;
        if (!_settings.RememberWindow || w == null || w.Width < 400 || w.Height < 300) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(w.Left, w.Top, w.Width, 40);   // タイトルバーが画面の中に 100 x 40 以上あれば使う
        var visible = Rect.Intersect(screen, title);
        if (visible.IsEmpty || visible.Width < 100) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = w.Left;
        Top = w.Top;
        Width = w.Width;
        Height = w.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (!_settings.RememberWindow) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.IsEmpty) return;
        _settings.Window = new WindowPlacement
        {
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _settings.Save();
    }

    /// <summary>記録を書き換えたので、画面へ送り直す(動画の位置はそのまま)</summary>
    private void RefreshSession() => _video.SendSession(keepVideo: true);

    /// <summary>IShell: 画面へ送る</summary>
    public void Post(HostMessage message) =>
        Web.CoreWebView2?.PostWebMessageAsJson(MessageJson.Serialize(message));
}
