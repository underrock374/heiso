using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Fh6.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Registry = Fh6.Core.Registry;   // Microsoft.Win32.Registry と区別する(登録ファイル fh6_registry.json)

namespace Fh6.Player;

/// <summary>
/// 外枠。画面(動画・グラフ・操作)はすべて wwwroot の HTML/JS で作り、ここはファイルを開くことと、
/// セッションを読み込んで JS へ渡すことだけを受け持つ。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// ウィンドウとメッセージの題名。v0 は 1 本の走りを見る「単走版」として公開する(2 本を並べる並走は v1 から)。
    /// exe 名(HeisoPlayer.exe)は版によらず変えない
    /// </summary>
    public static string AppTitle => $"{Strings.T("HEISO 単走版")} {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

    private const string AppHost = "app.heiso";
    private const string SessionHost = "session.heiso";
    private const string VideoHost = "video.heiso";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string? _initialPath;
    private readonly PlayerSettings _settings = PlayerSettings.Load();
    private bool _initialOpened;
    private LoadedSession? _session;
    private VideoSyncMethod _method = VideoSyncMethod.StartedEvent;

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
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HEISO", "Player", "WebView2");
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
        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
        // Ctrl+ホイールはグラフの拡大・縮小に使うので、画面全体の拡大は止める
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        await RegisterI18nAsync();
        core.Navigate($"https://{AppHost}/index.html");
    }

    private string? _i18nScriptId;

    /// <summary>
    /// 画面の言語の辞書(Fh6.Core の strings.json から)と Windows の地域を、ページの読み込みより前に window.HEISO_I18N として渡す
    /// (ページのスクリプトより先に動くので、日本語が一瞬出てから英語に変わることがない)。言語を変えたら登録し直してページを読み直す
    /// </summary>
    private async Task RegisterI18nAsync()
    {
        var core = Web.CoreWebView2;
        if (_i18nScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_i18nScriptId);
        // region は単位の「自動」に使う(Windows の地域。米国・英国ならマイル。units.js)
        var json = JsonSerializer.Serialize(new { lang = Strings.Language, strings = Strings.Dictionary(Strings.Language), region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName });
        _i18nScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.HEISO_I18N = {json};");
    }

    /// <summary>画面の言語を変える(設定に保存し、辞書を登録し直して、同じ記録・同じ位置で画面を読み直す)</summary>
    private async Task SetLanguageAsync(string? setting)
    {
        _settings.Language = setting is Strings.Japanese or Strings.English ? setting : Strings.Auto;
        _settings.Save();
        Strings.Use(_settings.Language);
        PlayerLog.Info($"画面の言語: {_settings.Language}({Strings.Language})");
        Title = _session == null ? AppTitle : $"{AppTitle} - {DisplayName}";
        await RegisterI18nAsync();
        _restoreSec = _session != null ? _lastVideoSec : null;
        _restorePlay = false;
        _restoreQuiet = true;
        Web.CoreWebView2?.Reload();
    }

    private bool _restoreQuiet;   // 読み直した理由が言語の切り替え(「画面が止まった」とは知らせない)

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch { return; }

        var type = msg.GetProperty("type").GetString();
        if (type is not ("position" or "log")) PlayerLog.Info($"画面から: {type}");
        switch (type)
        {
            case "position":
                // 動画の位置(画面が落ちたときに戻すため)
                if (msg.TryGetProperty("sec", out var ps) && ps.ValueKind == JsonValueKind.Number) _lastVideoSec = ps.GetDouble();
                _lastPlaying = msg.TryGetProperty("playing", out var pp) && pp.ValueKind == JsonValueKind.True;
                break;
            case "log":
                // 画面(JS)のエラーや操作の記録
                var level = msg.TryGetProperty("level", out var lv) ? lv.GetString() : "info";
                var text = "画面: " + (msg.TryGetProperty("message", out var lm) ? lm.GetString() : "");
                if (level == "error") PlayerLog.Error(text); else PlayerLog.Info(text);
                break;
            case "setLanguage":
                var lang = msg.TryGetProperty("language", out var sl) ? sl.GetString() : null;
                _ = Dispatcher.InvokeAsync(() => SetLanguageAsync(lang));   // ページを読み直すので、メッセージの処理を終えてから
                break;
            case "crashNoticeClosed":
                _crashNoticeClosed = true;
                break;
            case "openLogs":
                // ログのフォルダをエクスプローラーで開く(閉じるまで待つ画面ではないので、ここで開いてよい)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PlayerLog.Dir}\"") { UseShellExecute = true }); }
                catch (Exception ex) { PlayerLog.Error("ログのフォルダを開けない", ex); }
                break;
            case "ready":
                // 画面の設定のダイアログに、C# 側で持っている設定を渡す
                Post(new { type = "prefs", rememberWindow = _settings.RememberWindow, language = _settings.Language ?? Strings.Auto });
                // 画面の設定(どの記録にも共通)の同期の方式を使う
                if (msg.TryGetProperty("method", out var rm) && Enum.TryParse<VideoSyncMethod>(rm.GetString(), out var readyMethod))
                    _method = readyMethod;
                SendCrashNotice();
                // セッションを開いた後の読み直しなら、そのデータを送る(画面が落ちた後の立て直しなら、元の位置へ)
                if (_session != null)
                {
                    SendSession(keepVideo: false);
                    if (_restoreSec is double rs)
                    {
                        Post(new { type = "restore", sec = rs, play = _restorePlay, quiet = _restoreQuiet });
                        _restoreQuiet = false;
                        PlayerLog.Info($"画面を立て直した(動画 {rs:F1} 秒へ{(_restorePlay ? "、再生を続ける" : "")})");
                        _restoreSec = null;
                    }
                }
                else if (!_initialOpened && !string.IsNullOrEmpty(_initialPath))
                {
                    _initialOpened = true;
                    await OpenAsync(_initialPath);
                }
                break;
            case "open":
                // WebView2 のメッセージの処理の中で、閉じるまで待つ画面(ダイアログ)を開いてはいけない。
                // 開いている間に WebView2 あての処理が届くと、WebView2 が割り込みを検出して止まり、アプリごと落ちる
                // (2026-09-27〜28 に 8 回。EmbeddedBrowserWebView.dll、0x80000003)。メッセージの処理を終えてから開く
                _ = Dispatcher.InvokeAsync(ChooseAndOpenAsync);
                break;
            case "openPackage":
                _ = Dispatcher.InvokeAsync(ChooseAndOpenPackageAsync);   // 上と同じく、メッセージの処理を終えてから
                break;
            case "export":
                if (msg.TryGetProperty("race", out var er) && er.ValueKind == JsonValueKind.Number)
                {
                    int race = er.GetInt32();
                    bool reencode = msg.TryGetProperty("mode", out var em) && em.GetString() == "720";
                    _ = Dispatcher.InvokeAsync(() => ExportAsync(race, reencode));   // 保存先を選ぶ画面も、メッセージの処理を終えてから
                }
                break;
            case "cancelJob":
                _jobCts?.Cancel();
                break;
            case "probeFfmpeg":
                _ = SendFfmpegStatusAsync(force: msg.TryGetProperty("force", out var pf) && pf.ValueKind == JsonValueKind.True);
                break;
            case "setFfmpegPath":
                _settings.FfmpegPath = msg.TryGetProperty("path", out var fp) && fp.GetString() is string fps && fps.Trim().Length > 0 ? fps.Trim() : null;
                _settings.Save();
                _ = SendFfmpegStatusAsync(force: true);
                break;
            case "browseFfmpeg":
                _ = Dispatcher.InvokeAsync(BrowseFfmpeg);   // ファイルを選ぶ画面も、メッセージの処理を終えてから
                break;
            case "setExportEncoder":
                _settings.ExportEncoder = msg.TryGetProperty("encoder", out var ee) ? ee.GetString() : null;
                _settings.Save();
                break;
            case "rememberWindow":
                _settings.RememberWindow = msg.TryGetProperty("on", out var on) && on.GetBoolean();
                if (!_settings.RememberWindow) _settings.Window = null;
                _settings.Save();
                break;
            case "manualSync":
                SaveManualSync(msg);
                break;
            case "raceNamesForm":
                SendRaceNamesForm(msg.GetProperty("race").GetInt32());
                break;
            case "editRaceNames":
                EditRaceNames(msg);
                break;
            case "clearManualSync":
                ClearManualSync();
                break;
            case "method":
                if (Enum.TryParse<VideoSyncMethod>(msg.GetProperty("method").GetString(), out var m))
                {
                    _method = m;
                    SendSession(keepVideo: true);
                }
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
        Post(new
        {
            type = "crashNotice",
            startedAt = since.ToString("yyyy-MM-dd HH:mm"),
            logDir = PlayerLog.Dir,
            dump = PlayerLog.LatestCrashDump(since),
        });
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
                _restoreSec = _session != null ? _lastVideoSec : null;
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
    public void NotifyError(string message) => Post(new { type = "notice", error = message });

    private bool _choosing;

    /// <summary>フォルダを選ぶ画面を出して開く。WebView2 のイベントの外(Dispatcher から)で呼ぶこと</summary>
    private async Task ChooseAndOpenAsync()
    {
        if (_choosing) return;   // 続けて押されても、画面は 1 つだけ
        _choosing = true;
        try
        {
            // 毎回、前回開いた記録の 1 つ上(記録アプリの保存先)から始める
            var dlg = new OpenFolderDialog { Title = Strings.T("セッションフォルダを選んでください"), InitialDirectory = _settings.InitialFolder() ?? "" };
            if (dlg.ShowDialog(this) != true) return;
            await OpenAsync(dlg.FolderName);
        }
        finally
        {
            _choosing = false;
        }
    }

    /// <summary>配布パッケージを選ぶ画面を出して開く。WebView2 のイベントの外(Dispatcher から)で呼ぶこと</summary>
    private async Task ChooseAndOpenPackageAsync()
    {
        if (_choosing) return;
        _choosing = true;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = Strings.T("配布パッケージを選んでください"),
                Filter = $"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}|{Strings.T("すべてのファイル")} (*.*)|*.*",
                InitialDirectory = _settings.PackageFolder() ?? "",
            };
            if (dlg.ShowDialog(this) != true) return;
            await OpenAsync(dlg.FileName);
        }
        finally
        {
            _choosing = false;
        }
    }

    /// <summary>配布パッケージを展開する場所(%LOCALAPPDATA%\HEISO\Player\packages)</summary>
    private static string PackagesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HEISO", "Player", "packages");

    private static bool IsPackageFile(string path) =>
        File.Exists(path) && path.EndsWith(PackageInfo.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>開いている配布パッケージのファイル(記録のフォルダを開いているときは null)</summary>
    private string? _packageFile;

    /// <summary>セッションフォルダ(またはその中のファイル)か、配布パッケージ(.heiso.zip)を開く</summary>
    private async Task OpenAsync(string path)
    {
        string? package = IsPackageFile(path) ? Path.GetFullPath(path) : null;
        var folder = package != null ? "" : File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        Post(new { type = "loading", name = Path.GetFileName(package ?? folder) });
        PlayerLog.Info($"開く: {package ?? folder}");
        try
        {
            var sw = Stopwatch.StartNew();
            if (package != null)
            {
                folder = await Task.Run(() => PackageReader.Extract(package, PackagesDir));
                PlayerLog.Info($"パッケージを展開した: {folder}({sw.ElapsedMilliseconds} ms)");
            }
            _session = await Task.Run(() => LoadedSession.Load(folder));
            _packageFile = package;
            if (package != null)
            {
                _settings.LastPackageDir = Path.GetDirectoryName(package);
                _settings.Save();
            }
            else _settings.Remember(folder);
            PlayerLog.Info($"開いた: {_session.Table.RowCount} 行、レース {_session.Races.Count} 本、動画 {_session.VideoPath ?? "なし"}、{_session.Fps?.ToString("0.###") ?? "-"} fps、{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"開けない: {package ?? folder}", ex);
            _session = null;
            _packageFile = null;
            Post(new { type = "error", message = ex is UserFacingException or PackageException ? ex.Message : Strings.T("開けませんでした: {message}", ("message", ex.Message)) });
            return;
        }

        // 動画を置いたフォルダを仮想ホストに割り当てる。割り当ては読み込み済みのページには効かない
        // (WebView2 の挙動。動画が「対応していない形式」になる)ので、ページを読み直し、"ready" を受けてからデータを送る
        var core = Web.CoreWebView2;
        var videoDir = _session.VideoPath == null ? null : Path.GetDirectoryName(_session.VideoPath);
        bool external = videoDir != null && !string.Equals(videoDir, folder, StringComparison.OrdinalIgnoreCase);
        core.ClearVirtualHostNameToFolderMapping(SessionHost);
        core.SetVirtualHostNameToFolderMapping(SessionHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        core.ClearVirtualHostNameToFolderMapping(VideoHost);
        if (external)
            core.SetVirtualHostNameToFolderMapping(VideoHost, videoDir!, CoreWebView2HostResourceAccessKind.Allow);

        Title = $"{AppTitle} - {DisplayName}";
        core.Reload();
    }

    /// <summary>画面に出す名前: 記録ならフォルダ名、パッケージならファイル名(.heiso.zip を除く)</summary>
    private string DisplayName =>
        _packageFile != null ? Path.GetFileName(_packageFile)[..^PackageInfo.Extension.Length]
        : _session != null ? Path.GetFileName(_session.Folder) : "";

    private bool _exporting;
    private CancellationTokenSource? _jobCts;

    /// <summary>
    /// 選んでいるレースを配布パッケージに書き出す。reencode なら ffmpeg で 720p に作り直す(数分かかるので、進み具合と中止を画面に出す)。
    /// 保存先を選ぶ画面を出すので、WebView2 のイベントの外(Dispatcher から)で呼ぶこと
    /// </summary>
    private async Task ExportAsync(int raceIndex, bool reencode)
    {
        var s = _session;
        if (s == null || _exporting || _choosing) return;
        if (s.Package != null) { Toast(error: Strings.T("パッケージから、もう一度パッケージは作れません。元の記録を開いてください")); return; }
        if (raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var race = s.Races[raceIndex];
        _settings.ExportMode = reencode ? "720" : "copy";
        _settings.Save();

        // 720p: ffmpeg とエンコーダを先に決める(保存先を選んだ後で「使えない」と言わないように)
        PackageReencodeOptions? options = null;
        if (reencode)
        {
            var st = await GetFfmpegStatusAsync(force: false);
            if (st.Path == null) { Toast(error: Strings.T("ffmpeg が見つかりません。設定の「書き出し」で ffmpeg の場所を指定してください")); return; }
            var enc = st.Selected;
            if (enc == null) { Toast(error: Strings.T("この PC で使える H.264 のエンコーダが ffmpeg にありません(設定の「書き出し」で確かめられます)")); return; }
            double fps = s.Fps ?? 30;
            options = new PackageReencodeOptions(st.Path, new Ffmpeg.Options(enc, VideoKbps: Ffmpeg.DefaultVideoKbps(fps)));
        }

        string path;
        _choosing = true;
        try
        {
            var name = PackageWriter.DefaultFileName(Path.GetFileName(s.Folder), race.Course, race.Car);
            if (reencode) name = name[..^PackageInfo.Extension.Length] + "_720p" + PackageInfo.Extension;
            var dlg = new SaveFileDialog
            {
                Title = reencode ? Strings.T("配布パッケージ(720p)の保存先") : Strings.T("配布パッケージの保存先"),
                Filter = $"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}",
                FileName = name,
                InitialDirectory = _settings.ExportFolder(s.Folder),
                AddExtension = false,
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        finally
        {
            _choosing = false;
        }
        // 名前の最後を .heiso.zip にそろえる(「abc」や「abc.zip」と入れられても)
        if (!path.EndsWith(PackageInfo.Extension, StringComparison.OrdinalIgnoreCase))
            path = (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path) + PackageInfo.Extension;

        _exporting = true;
        using var cts = new CancellationTokenSource();
        _jobCts = cts;
        var what = reencode ? Strings.T("{n} レース目を 720p で書き出しています({encoder})", ("n", raceIndex + 1), ("encoder", Ffmpeg.EncoderName(options!.Encode.Encoder)))
                            : Strings.T("{n} レース目を書き出しています", ("n", raceIndex + 1));
        Job(what, 0, cancellable: reencode);
        PlayerLog.Info($"書き出す: {s.Folder} のレース {raceIndex + 1}(開始 {race.Segment.StartRecvTime:F3})→ {path}{(reencode ? $"(720p、{options!.Encode.Encoder}、{options.Encode.VideoKbps} kbps)" : "")}");
        // 進み具合は 0.5 秒に 1 回だけ画面へ(ffmpeg の知らせは細かいので)
        double lastSent = -1;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(v =>
        {
            if (v < 1 && sw.Elapsed.TotalSeconds - lastSent < 0.5) return;
            lastSent = sw.Elapsed.TotalSeconds;
            Job(what, v, cancellable: reencode);
        });
        try
        {
            var r = await Task.Run(() => PackageWriter.Write(s.Folder, race.Segment.StartRecvTime, path, options, progress, cts.Token));
            _settings.LastExportDir = Path.GetDirectoryName(path);
            _settings.Save();
            PlayerLog.Info($"書き出した: {r.Bytes / 1e6:F1} MB、動画 {r.Info.Video.CutFromSec:F2} 秒から {r.Info.Video.DurationSec:F1} 秒、テレメトリー {r.TelemetryRows} 行、{r.ElapsedSec:F1} 秒");
            Job(null);
            Toast(message: Strings.T("書き出しました: {file}({mb:F0} MB、{sec:F0} 秒)。場所: {dir}", ("file", Path.GetFileName(path)), ("mb", r.Bytes / 1e6), ("sec", r.ElapsedSec), ("dir", Path.GetDirectoryName(path))), sec: 10);
        }
        catch (OperationCanceledException)
        {
            PlayerLog.Info($"書き出しを中止した: {path}");
            Job(null);
            Toast(message: Strings.T("書き出しを中止しました"));
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"書き出せない: {path}", ex);
            Job(null);
            Toast(error: ex is PackageException ? ex.Message : Strings.T("書き出せませんでした: {message}", ("message", ex.Message)));
        }
        finally
        {
            _exporting = false;
            _jobCts = null;
        }
    }

    /// <summary>長くかかる処理の進み具合を画面の下に出す(text が null なら消す)</summary>
    private void Job(string? text, double progress = 0, bool cancellable = false) =>
        Post(new { type = "job", text, progress = Math.Round(progress, 3), cancellable });

    // ---- ffmpeg(720p の書き出し) ----

    private sealed record FfmpegStatus(string? Path, string? Version, List<string> Encoders, string? Selected);
    private FfmpegStatus? _ffmpeg;
    private string? _ffmpegFor;   // どの設定で調べた結果か

    /// <summary>
    /// ffmpeg を探す場所(設定が空のとき。PATH の後に見る): 再生アプリのフォルダの中・隣(HEISO を展開したフォルダ)の ffmpeg フォルダ、
    /// %LOCALAPPDATA%\HEISO\ffmpeg
    /// </summary>
    private static IEnumerable<string> FfmpegSearchDirs()
    {
        var app = AppContext.BaseDirectory.TrimEnd('\\', '/');
        yield return Path.Combine(app, "ffmpeg");
        if (Path.GetDirectoryName(app) is string parent) yield return Path.Combine(parent, "ffmpeg");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HEISO", "ffmpeg");
    }

    /// <summary>ffmpeg の場所・版・使えるエンコーダ(試すのに数秒かかるので、設定が変わるまで覚えておく)</summary>
    private async Task<FfmpegStatus> GetFfmpegStatusAsync(bool force)
    {
        var key = _settings.FfmpegPath ?? "";
        if (!force && _ffmpeg != null && _ffmpegFor == key) return WithSelected(_ffmpeg);
        var st = await Task.Run(() =>
        {
            var path = Ffmpeg.Find(_settings.FfmpegPath, FfmpegSearchDirs());
            if (path == null) return new FfmpegStatus(null, null, new List<string>(), null);
            var ver = Ffmpeg.Version(path);
            return new FfmpegStatus(path, ver, ver == null ? new List<string>() : Ffmpeg.UsableEncoders(path), null);
        });
        _ffmpeg = st;
        _ffmpegFor = key;
        PlayerLog.Info($"ffmpeg: {st.Path ?? "見つからない"} {st.Version} / 使えるエンコーダ: {string.Join(", ", st.Encoders)}");
        return WithSelected(st);
    }

    /// <summary>設定で選んだエンコーダ(使えなければ、使えるものの先頭)</summary>
    private FfmpegStatus WithSelected(FfmpegStatus st)
    {
        var want = _settings.ExportEncoder;
        var sel = !string.IsNullOrEmpty(want) && want != "auto" && st.Encoders.Contains(want) ? want : st.Encoders.FirstOrDefault();
        return st with { Selected = sel };
    }

    private async Task SendFfmpegStatusAsync(bool force)
    {
        Post(new { type = "ffmpegStatus", checking = true });
        var st = await GetFfmpegStatusAsync(force);
        Post(new
        {
            type = "ffmpegStatus",
            checking = false,
            configured = _settings.FfmpegPath,
            path = st.Path,
            version = st.Version,
            encoders = st.Encoders.Select(id => new { id, name = Ffmpeg.EncoderName(id) }).ToArray(),
            encoder = string.IsNullOrEmpty(_settings.ExportEncoder) ? "auto" : _settings.ExportEncoder,
            selected = st.Selected,
            exportMode = _settings.ExportMode ?? "copy",
        });
    }

    /// <summary>ffmpeg.exe を選ぶ画面。WebView2 のイベントの外(Dispatcher から)で呼ぶこと</summary>
    private async Task BrowseFfmpeg()
    {
        if (_choosing) return;
        _choosing = true;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = Strings.T("ffmpeg.exe を選んでください"),
                Filter = $"ffmpeg (ffmpeg.exe)|ffmpeg.exe|{Strings.T("すべてのファイル")} (*.*)|*.*",
                InitialDirectory = _ffmpeg?.Path is string cur ? Path.GetDirectoryName(cur) ?? "" : "",
            };
            if (dlg.ShowDialog(this) != true) return;
            _settings.FfmpegPath = dlg.FileName;
            _settings.Save();
        }
        finally
        {
            _choosing = false;
        }
        await SendFfmpegStatusAsync(force: true);
    }

    /// <summary>画面の下に短く知らせる(手動補正の欄には触れない)。sec は出しておく秒数</summary>
    private void Toast(string? message = null, string? error = null, double? sec = null) =>
        Post(new { type = "toast", message, error, sec });

    private void SendSession(bool keepVideo)
    {
        if (_session == null) return;
        var s = _session;
        var map = VideoTimeMap.Create(s.Meta.Video, _method, out var mapError);

        string? videoUrl = null;
        if (s.VideoPath != null)
        {
            var host = string.Equals(Path.GetDirectoryName(s.VideoPath), s.Folder, StringComparison.OrdinalIgnoreCase) ? SessionHost : VideoHost;
            videoUrl = $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(s.VideoPath))}";
        }

        var v = s.Meta.Video;
        Post(new
        {
            type = "session",
            keepVideo,
            name = DisplayName,
            label = s.Meta.Session.Label,
            comment = SessionFolderName.TryParse(s.Package?.SourceSession ?? Path.GetFileName(s.Folder), out _, out var comment) ? comment : null,
            // 配布パッケージを開いているか(書き出すボタンを出さない)
            package = s.Package != null,
            startedAt = s.Meta.Session.StartedAt,
            complete = s.Meta.Complete,
            videoUrl,
            videoError = s.VideoPath == null ? Strings.T("録画ファイルが見つかりません(テレメトリーだけ表示します)") : null,
            method = _method.ToString(),
            mapError,
            sync = new
            {
                samplesTotal = v.SyncSamples.Count,
                samplesUsed = map?.SamplesUsed ?? 0,
                isFallback = map?.IsFallback ?? false,
                manualSync = v.ManualSync.Count,
                manualShiftSec = map?.ManualShiftSec ?? 0,
                endCheckSec = v.EndCheckSec,
                clockRate = v.ClockRate,
                fileDurationSec = v.FileDurationSec,
            },
            rows = s.Table.RowCount,
            fps = s.Fps,
            series = map == null ? null : s.BuildSeries(map),
            marks = map == null ? null : s.BuildMarks(map),
        });
    }

    /// <summary>
    /// 手動補正の点を保存する。graphSec はグラフで指した瞬間(今の変換での動画の秒)、videoSec は映像のコマの秒。
    /// グラフの位置をテレメトリー時刻に直すのは VideoTimeMap だけで行う
    /// </summary>
    private void SaveManualSync(JsonElement msg)
    {
        if (_session == null) return;
        var map = VideoTimeMap.Create(_session.Meta.Video, _method, out _);
        if (map == null) { Notice(error: Strings.T("動画との対応が取れないため、補正できません")); return; }
        var point = new ManualSyncPoint
        {
            RecvTime = Math.Round(map.ToRecvTime(msg.GetProperty("graphSec").GetDouble()), 4),
            VideoSec = Math.Round(msg.GetProperty("videoSec").GetDouble(), 4),
            Note = msg.TryGetProperty("note", out var n) && n.GetString() is string note && note.Trim().Length > 0 ? note.Trim() : null,
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
        };
        UpdateManualSync(() => SessionMeta.AppendManualSync(_session.Folder, point), Strings.T("手動補正を保存しました"));
    }

    private void ClearManualSync()
    {
        if (_session == null) return;
        UpdateManualSync(() => SessionMeta.ClearManualSync(_session.Folder), Strings.T("手動補正を消しました"));
    }

    /// <summary>session.json を書き換え、読み直して描き直す(動画の位置はそのまま)</summary>
    private void UpdateManualSync(Func<string?> write, string done)
    {
        try
        {
            var error = write();
            if (error != null) { Notice(error: error); return; }
            _session!.Meta = SessionMeta.Load(_session.Folder);
        }
        catch (Exception ex)
        {
            PlayerLog.Error("手動補正を session.json に書けない", ex);
            Notice(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return;
        }
        SendSession(keepVideo: true);
        Notice(message: done);
    }

    private void Notice(string? message = null, string? error = null) => Post(new { type = "notice", message, error });

    // ---- レースのコース名・車名(記録アプリの「直前のレース」の直し方と同じ) ----

    /// <summary>
    /// 名前を付けるダイアログの中身: 登録済みのコース(スタートの位置で一致するものを先頭)、その車種の名前、同じ組のセッティング。
    /// 登録ファイルは読むだけ(書くのは記録アプリ。RegistryInbox 経由)
    /// </summary>
    private void SendRaceNamesForm(int raceIndex)
    {
        var s = _session;
        if (s == null || raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var (seg, course, car, setup) = s.Races[raceIndex];
        var reg = Registry.TryRead(Path.Combine(s.SaveFolder, Registry.FileName)) ?? new Registry();
        var match = CourseMatcher.Match(reg.Courses, seg.StartX, seg.StartZ, seg.StartYaw);
        var sig = s.RaceCar(seg);
        var setups = sig is CarSignature cs ? reg.FindSetups(cs) : new List<RegisteredSetup>();
        Post(new
        {
            type = "raceNamesForm",
            race = raceIndex,
            editable = s.Package == null && s.Meta.Complete,
            reason = s.Package != null ? Strings.T("パッケージの名前は変えられません") : !s.Meta.Complete ? Strings.T("記録中の記録には保存できません") : null,
            course = new
            {
                current = course,
                matchId = match?.Course.Id,
                list = reg.Courses
                    .OrderByDescending(c => c.Id == match?.Course.Id).ThenBy(c => c.Name, StringComparer.CurrentCulture)
                    .Select(c => new { id = c.Id, name = c.Name, kind = c.Kind }).ToArray(),
            },
            car = sig is CarSignature g ? new
            {
                ordinal = g.Ordinal, sig = g.ToString(), current = car, registered = reg.CarName(g.Ordinal),
                setup = setup, setups = setups.Select(x => new { id = x.Id, name = x.Name }).ToArray(),
            } : null,
        });
    }

    /// <summary>
    /// レースに付けた名前を保存する: この記録の session.json に race_edited を足し(レースの一覧と書き出しにすぐ出る)、
    /// 保存先の登録の受け渡し用のファイルに頼みを足す(記録アプリが登録ファイルに取り込む)
    /// </summary>
    private void EditRaceNames(JsonElement msg)
    {
        var s = _session;
        if (s == null) return;
        int raceIndex = msg.GetProperty("race").GetInt32();
        if (raceIndex < 0 || raceIndex >= s.Races.Count) return;
        if (s.Package != null) { Toast(error: Strings.T("パッケージの名前は変えられません")); return; }
        string? Str(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0 ? v.GetString()!.Trim() : null;

        var seg = s.Races[raceIndex].Segment;
        var sig = s.RaceCar(seg);
        var reg = Registry.TryRead(Path.Combine(s.SaveFolder, Registry.FileName)) ?? new Registry();
        string? courseId = Str("courseId"), courseName = Str("courseName"), courseKind = Str("courseKind");
        string? carName = Str("carName"), setupId = Str("setupId"), setupName = Str("setupName");
        if (courseId == RegistryInbox.NewId && courseName == null) { Toast(error: Strings.T("レース名を入れてください")); return; }
        if (courseId != null && courseId != RegistryInbox.NewId && courseName == null) courseName = reg.CourseById(courseId)?.Name;
        if (setupId == RegistryInbox.NewId && setupName == null && sig is CarSignature g0) setupName = $"{g0.ClassName}{g0.Pi} {g0.DrivetrainName}";
        if (setupId != null && setupId != RegistryInbox.NewId && setupName == null) setupName = reg.SetupById(setupId)?.Name;

        var data = new System.Text.Json.Nodes.JsonObject
        {
            ["race_start_recv_time"] = Math.Round(seg.StartRecvTime, 6),
            ["changes"] = new System.Text.Json.Nodes.JsonArray("再生アプリで名前を付けた"),
            ["source"] = "player",
            ["course_id"] = courseId == RegistryInbox.NewId ? null : courseId,
            ["course_name"] = courseName, ["course_kind"] = courseKind,
            ["car_ordinal"] = sig?.Ordinal, ["car_name"] = carName,
            ["setup_id"] = setupId == RegistryInbox.NewId ? null : setupId, ["setup_name"] = setupName,
        };
        var ev = new SessionEvent
        {
            RecvTime = Math.Round(Clock.Now(), 6), Type = "race_edited",
            Detail = $"再生アプリで名前を付けた: {courseName ?? "コース不明"} / {carName ?? "車両不明"}{(setupName != null ? $" {setupName}" : "")}",
            Data = data,
        };
        try
        {
            var error = SessionMeta.AppendEvent(s.Folder, ev);
            if (error != null) { Toast(error: error); return; }
            s.Meta = SessionMeta.Load(s.Folder);
            s.RefreshNames();
        }
        catch (Exception ex)
        {
            PlayerLog.Error("レースの名前を session.json に書けない", ex);
            Toast(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return;
        }

        // 登録ファイルへは、受け渡し用のファイル経由で(記録アプリが取り込む)
        string registryNote;
        try
        {
            RegistryInbox.Append(s.SaveFolder, new RegistryRequest
            {
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                Session = Path.GetFileName(s.Folder), RaceStartRecvTime = Math.Round(seg.StartRecvTime, 6),
                CourseId = courseId, CourseName = courseName, CourseKind = courseKind,
                StartX = Math.Round(seg.StartX, 3), StartZ = Math.Round(seg.StartZ, 3), StartYaw = Math.Round(seg.StartYaw, 5),
                CarOrdinal = sig?.Ordinal, ClassRaw = sig?.Class, Pi = sig?.Pi, DrivetrainRaw = sig?.Drivetrain, Cylinders = sig?.Cylinders,
                CarName = carName, SetupId = setupId, SetupName = setupName,
            });
            registryNote = Strings.T("記録アプリを次に起動したとき(起動中なら数秒で)登録します");
        }
        catch (Exception ex)
        {
            PlayerLog.Error("登録の受け渡し用のファイルに書けない", ex);
            registryNote = Strings.T("登録には回せませんでした: {message}", ("message", ex.Message));
        }
        PlayerLog.Info($"レース {raceIndex + 1} の名前: {ev.Detail}");
        SendSession(keepVideo: true);
        Toast(message: Strings.T("名前を保存しました。{note}", ("note", registryNote)), sec: 6);
    }

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

    private void Post(object message) =>
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
}
