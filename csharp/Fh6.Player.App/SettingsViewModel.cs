using System.IO;
using System.Text.Json;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>ffmpeg の場所・版・使えるエンコーダ。Selected は設定で選んだエンコーダ(使えなければ、使えるものの先頭)</summary>
public sealed record FfmpegStatus(string? Path, string? Version, List<string> Encoders, string? Selected);

/// <summary>
/// 画面の設定のダイアログと、書き出しのダイアログの ffmpeg の状態(PlayerSettings に保存するもの)。
/// probe は ffmpeg を探して試す処理(設定の ffmpeg の場所から、選ぶ前の状態を作る)。テストでは偽物に差し替える
/// </summary>
public sealed class SettingsViewModel(IShell shell, PlayerSettings settings, Func<string?, FfmpegStatus>? probe = null)
{
    private readonly Func<string?, FfmpegStatus> _probe = probe ?? ProbeFfmpeg;
    private FfmpegStatus? _ffmpeg;
    private string? _ffmpegFor;   // どの設定で調べた結果か

    /// <summary>画面の設定(PlayerSettings.View)を保存した後(ならしの幅で作るものを作り直すため)</summary>
    public event Action? ViewSaved;

    /// <summary>画面からのメッセージのうち、設定のものを扱う。扱わないメッセージなら null</summary>
    public Task? Handle(ViewMessage msg)
    {
        switch (msg)
        {
            case ViewProbeFfmpeg m:
                return SendFfmpegStatusAsync(force: m.Force == true);
            case ViewSetFfmpegPath m:
                settings.FfmpegPath = m.Path?.Trim() is { Length: > 0 } p ? p : null;
                settings.Save();
                return SendFfmpegStatusAsync(force: true);
            case ViewBrowseFfmpeg:
                return BrowseFfmpegAsync();
            case ViewSetExportEncoder m:
                settings.ExportEncoder = m.Encoder;
                settings.Save();
                return Task.CompletedTask;
            case ViewSetReplayLead { Frames: double rl }:
                {
                    // リプレイを割り当てたときのずらし量(設定の「同期」。コマ)。既定と同じなら空にして、既定が変わったら追う
                    int frames = Math.Clamp((int)Math.Round(rl), -60, 60);
                    settings.ReplayLeadFrames = frames == PlayerSettings.DefaultReplayLeadFrames ? null : frames;
                    settings.Save();
                    PlayerLog.Info($"リプレイを新しく割り当てたときのずらし量: 前へ {frames} コマ");   // 割り当て済みのリプレイは変えない
                    return Task.CompletedTask;
                }
            case ViewSaveSettings m:
                // 画面の設定(丸ごと届く)。保存して、次に読み込むページへ渡す値も替える
                settings.View = (m.Settings ?? new ViewSettings()).WithDefaults();
                settings.Save();
                shell.RefreshPageData();
                ViewSaved?.Invoke();
                return Task.CompletedTask;
            case ViewRememberWindow m:
                settings.RememberWindow = m.On == true;
                if (!settings.RememberWindow) settings.Window = null;
                settings.Save();
                return Task.CompletedTask;
        }
        return null;
    }

    /// <summary>画面の設定のダイアログに渡す、C# 側で持っている設定</summary>
    public HostPrefs Prefs() => new(settings.RememberWindow, settings.Language ?? Strings.Auto,
        settings.ReplayLeadFrames ?? PlayerSettings.DefaultReplayLeadFrames, PlayerSettings.DefaultReplayLeadFrames);

    /// <summary>
    /// ページの読み込みより前に動かすスクリプト: 画面の言語の辞書(window.HEISO_I18N)、画面の設定と単位の「自動」(window.HEISO_SETTINGS)。
    /// 外枠が登録する(ページのスクリプトより先に動くので、日本語が一瞬出てから英語に変わること、既定の設定で一瞬描くことがない)
    /// </summary>
    public string PageScript()
    {
        var i18n = Strings.PageData();
        var view = new PageSettings(settings.View != null, (settings.View ?? new ViewSettings()).WithDefaults(), DisplayUnits.AutoImperial());
        return $"window.HEISO_I18N = {JsonSerializer.Serialize(i18n, MessageJson.Options)};\nwindow.HEISO_SETTINGS = {JsonSerializer.Serialize(view, MessageJson.Options)};";
    }

    /// <summary>画面の言語を変えて保存する(画面の読み直しは外枠が行う)</summary>
    public void SetLanguage(string? setting)
    {
        settings.Language = setting is Strings.Japanese or Strings.English ? setting : Strings.Auto;
        settings.Save();
        Strings.Use(settings.Language);
        PlayerLog.Info($"画面の言語: {settings.Language}({Strings.Language})");
    }

    /// <summary>ffmpeg の場所・版・使えるエンコーダ(試すのに数秒かかるので、設定が変わるまで覚えておく)</summary>
    public async Task<FfmpegStatus> FfmpegStatusAsync(bool force)
    {
        var key = settings.FfmpegPath ?? "";
        if (!force && _ffmpeg != null && _ffmpegFor == key) return WithSelected(_ffmpeg);
        var configured = settings.FfmpegPath;
        var st = await Task.Run(() => _probe(configured));
        _ffmpeg = st;
        _ffmpegFor = key;
        PlayerLog.Info($"ffmpeg: {st.Path ?? "見つからない"} {st.Version} / 使えるエンコーダ: {string.Join(", ", st.Encoders)}");
        return WithSelected(st);
    }

    /// <summary>設定で選んだエンコーダ(使えなければ、使えるものの先頭)</summary>
    private FfmpegStatus WithSelected(FfmpegStatus st)
    {
        var want = settings.ExportEncoder;
        var sel = !string.IsNullOrEmpty(want) && want != "auto" && st.Encoders.Contains(want) ? want : st.Encoders.FirstOrDefault();
        return st with { Selected = sel };
    }

    private async Task SendFfmpegStatusAsync(bool force)
    {
        shell.Post(new HostFfmpegStatus(true, null, null, null, null, null, null, null));
        var st = await FfmpegStatusAsync(force);
        shell.Post(new HostFfmpegStatus(
            Checking: false,
            Configured: settings.FfmpegPath,
            Path: st.Path,
            Version: st.Version,
            Encoders: st.Encoders.Select(id => new FfmpegEncoderChoice(id, Ffmpeg.EncoderName(id))).ToArray(),
            Encoder: string.IsNullOrEmpty(settings.ExportEncoder) ? "auto" : settings.ExportEncoder,
            Selected: st.Selected,
            ExportMode: settings.ExportMode ?? "copy"));
    }

    private async Task BrowseFfmpegAsync()
    {
        var picked = await shell.PickFileAsync(
            Strings.T("ffmpeg.exe を選んでください"),
            $"ffmpeg (ffmpeg.exe)|ffmpeg.exe|{Strings.T("すべてのファイル")} (*.*)|*.*",
            _ffmpeg?.Path is string cur ? Path.GetDirectoryName(cur) : null);
        if (picked == null) return;
        settings.FfmpegPath = picked;
        settings.Save();
        await SendFfmpegStatusAsync(force: true);
    }

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

    private static FfmpegStatus ProbeFfmpeg(string? configured)
    {
        var path = Ffmpeg.Find(configured, FfmpegSearchDirs());
        if (path == null) return new FfmpegStatus(null, null, new List<string>(), null);
        var ver = Ffmpeg.Version(path);
        return new FfmpegStatus(path, ver, ver == null ? new List<string>() : Ffmpeg.UsableEncoders(path), null);
    }
}
