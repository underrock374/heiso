using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>画面の言語(Strings.Use)はプロセス全体で 1 つなので、言語を変えるテストはほかのテストと同時に動かさない</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>画面の設定と ffmpeg の状態(SettingsViewModel)。設定は一時フォルダに書き、ffmpeg は試さずに偽の結果を返す</summary>
[Collection(nameof(LanguageCollection))]
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "heiso-settings-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings;
    private readonly List<string?> _probed = [];
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = PlayerSettings.Load(SettingsFile);
        _vm = new SettingsViewModel(_shell, _settings, configured =>
        {
            _probed.Add(configured);
            return configured == "none"
                ? new FfmpegStatus(null, null, [], null)
                : new FfmpegStatus(@"C:\ffmpeg\bin\ffmpeg.exe", "7.1", ["h264_nvenc", "libx264"], null);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string SettingsFile => Path.Combine(_dir, "player.json");

    private PlayerSettings Saved() => PlayerSettings.Load(SettingsFile);

    [Fact]
    public async Task ffmpegの状態は調べている間を先に送り_次に結果を送る()
    {
        _settings.ExportMode = "720";
        await _vm.Handle(new ViewProbeFfmpeg(false))!;

        Assert.Equal(2, _shell.Posted.Count);
        Assert.Equal(new HostFfmpegStatus(true, null, null, null, null, null, null, null), _shell.Posted[0]);
        var st = Assert.IsType<HostFfmpegStatus>(_shell.Posted[1]);
        Assert.False(st.Checking);
        Assert.Null(st.Configured);
        Assert.Equal(@"C:\ffmpeg\bin\ffmpeg.exe", st.Path);
        Assert.Equal("7.1", st.Version);
        Assert.Equal(["h264_nvenc", "libx264"], st.Encoders!.Select(e => e.Id));
        Assert.Equal(Ffmpeg.EncoderName("h264_nvenc"), st.Encoders![0].Name);
        Assert.Equal("auto", st.Encoder);
        Assert.Equal("h264_nvenc", st.Selected);   // 自動なら、使えるものの先頭
        Assert.Equal("720", st.ExportMode);
    }

    [Fact]
    public async Task ffmpegは設定が変わるまで調べ直さない()
    {
        await _vm.Handle(new ViewProbeFfmpeg(false))!;
        await _vm.Handle(new ViewProbeFfmpeg(false))!;
        await _vm.FfmpegStatusAsync(force: false);
        Assert.Single(_probed);

        await _vm.Handle(new ViewProbeFfmpeg(true))!;   // 「もう一度探す」
        Assert.Equal(2, _probed.Count);

        await _vm.Handle(new ViewSetFfmpegPath("  none  "))!;
        Assert.Equal(new string?[] { null, null, "none" }, _probed);
        Assert.Equal("none", Saved().FfmpegPath);
        var st = Assert.IsType<HostFfmpegStatus>(_shell.Posted.Last());
        Assert.Equal("none", st.Configured);
        Assert.Null(st.Path);
        Assert.Empty(st.Encoders!);
        Assert.Null(st.Selected);
    }

    [Fact]
    public async Task ffmpegの場所を空にすると探す()
    {
        _settings.FfmpegPath = "none";
        await _vm.Handle(new ViewSetFfmpegPath("   "))!;
        Assert.Null(Saved().FfmpegPath);
        Assert.Equal(new string?[] { null }, _probed);
    }

    [Theory]
    [InlineData("libx264", "libx264")]
    [InlineData("h264_amf", "h264_nvenc")]   // 使えないものを選んでいたら、使えるものの先頭
    [InlineData("auto", "h264_nvenc")]
    public async Task エンコーダは設定で選んだものを使う(string chosen, string selected)
    {
        await _vm.Handle(new ViewSetExportEncoder(chosen))!;
        Assert.Equal(chosen, Saved().ExportEncoder);
        Assert.Empty(_shell.Posted);
        Assert.Equal(selected, (await _vm.FfmpegStatusAsync(force: false)).Selected);
    }

    [Fact]
    public async Task ffmpegexeを選ぶと保存して調べ直す_選ばなければ何もしない()
    {
        await _vm.Handle(new ViewBrowseFfmpeg())!;
        Assert.Empty(_shell.Posted);
        Assert.Null(_shell.AskedInitialDir);

        await _vm.FfmpegStatusAsync(force: false);
        _shell.File = @"D:\tools\ffmpeg.exe";
        await _vm.Handle(new ViewBrowseFfmpeg())!;
        Assert.Equal(@"C:\ffmpeg\bin", _shell.AskedInitialDir);   // 今使っている ffmpeg のフォルダから
        Assert.StartsWith("ffmpeg (ffmpeg.exe)|ffmpeg.exe|", _shell.AskedFilter);
        Assert.Equal(@"D:\tools\ffmpeg.exe", Saved().FfmpegPath);
        Assert.Equal(@"D:\tools\ffmpeg.exe", _probed.Last());
        Assert.False(Assert.IsType<HostFfmpegStatus>(_shell.Posted.Last()).Checking);
    }

    [Theory]
    [InlineData(8.0, 8)]
    [InlineData(2.4, 2)]
    [InlineData(-100.0, -60)]
    [InlineData(100.0, 60)]
    public async Task リプレイのずらし量は範囲に収めて保存する(double frames, int saved)
    {
        await _vm.Handle(new ViewSetReplayLead(frames))!;
        Assert.Equal(saved, Saved().ReplayLeadFrames);
        Assert.Equal(saved, _vm.Prefs().ReplayLeadFrames);
    }

    [Fact]
    public async Task リプレイのずらし量が既定と同じなら空にする()
    {
        _settings.ReplayLeadFrames = 8;
        await _vm.Handle(new ViewSetReplayLead(PlayerSettings.DefaultReplayLeadFrames))!;
        Assert.Null(Saved().ReplayLeadFrames);
        Assert.Equal(new HostPrefs(false, Strings.Auto, PlayerSettings.DefaultReplayLeadFrames, PlayerSettings.DefaultReplayLeadFrames), _vm.Prefs());
        Assert.Null(_vm.Handle(new ViewSetReplayLead(null)));   // 値が無ければ扱わない
    }

    [Fact]
    public async Task ウィンドウの位置を覚えないにすると覚えていた位置も消す()
    {
        _settings.Window = new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600 };
        await _vm.Handle(new ViewRememberWindow(true))!;
        Assert.True(Saved().RememberWindow);
        Assert.NotNull(Saved().Window);
        Assert.True(_vm.Prefs().RememberWindow);

        await _vm.Handle(new ViewRememberWindow(false))!;
        Assert.False(Saved().RememberWindow);
        Assert.Null(Saved().Window);
    }

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("en", "en")]
    [InlineData("fr", "auto")]
    [InlineData(null, "auto")]
    public void 言語は日本語か英語か自動で保存する(string? setting, string saved)
    {
        try
        {
            _vm.SetLanguage(setting);
            Assert.Equal(saved, Saved().Language);
            Assert.Equal(saved, _vm.Prefs().Language);
        }
        finally
        {
            Strings.Use(Strings.Japanese);   // ほかのテストは日本語で動かす
        }
    }

    [Fact]
    public void 画面の設定は既定の値で埋めて保存し_次のページへ渡すものを登録し直す()
    {
        var sent = new ViewSettings
        {
            Span = 60, MapMode = null, LtSmooth = 0.5,
            Layouts = new GraphLayouts(new GraphLayout("p6", [new GraphLane("m", ["speed", "gear"])]), null),
            SyncFloat = new SyncFloatState(true, 12, null),
        };
        Assert.Same(Task.CompletedTask, _vm.Handle(new ViewSaveSettings(sent)));

        var view = Saved().View!;
        Assert.Equal(60, view.Span);
        Assert.Equal(0.5, view.LtSmooth);
        Assert.Equal("2d", view.MapMode);                      // 届かなかった値は既定
        Assert.Equal(true, view.Contacts);
        Assert.Equal(["speed", "gear"], view.Layouts!.Vis!.Lanes![0].S!);
        Assert.Null(view.Layouts.Perf);                       // 段の組み合わせは既定を持たない(画面が決める)
        Assert.Equal(new SyncFloatState(true, 12, null), view.SyncFloat);
        Assert.Equal(1, _shell.PageDataRefreshed);

        // 中身が無くても落とさず、全部既定で保存する
        _vm.Handle(new ViewSaveSettings(null));
        Assert.Equal(ViewSettings.Defaults, Saved().View);
    }

    [Fact]
    public void ページへ渡す設定は_保存が無ければ既定の値で_まだ保存していないと伝える()
    {
        static System.Text.Json.Nodes.JsonNode Page(string script) =>
            System.Text.Json.Nodes.JsonNode.Parse(script.Split("window.HEISO_SETTINGS = ")[1].TrimEnd(';'))!;

        var first = Page(_vm.PageScript());
        Assert.False(first["saved"]!.GetValue<bool>());          // 画面が前の版の値を一度だけ引き継ぐ
        Assert.Equal(20, first["settings"]!["span"]!.GetValue<double>());
        Assert.Equal("StartedEvent", first["settings"]!["method"]!.GetValue<string>());
        Assert.Contains("window.HEISO_I18N = ", _vm.PageScript());
        Assert.Equal(Fh6.Core.DisplayUnits.AutoImperial(), first["autoImperial"]!.GetValue<bool>());   // 単位の「自動」(記録アプリと同じ決め方)

        _vm.Handle(new ViewSaveSettings(new ViewSettings { Span = 0 }));
        var next = Page(_vm.PageScript());
        Assert.True(next["saved"]!.GetValue<bool>());
        Assert.Equal(0, next["settings"]!["span"]!.GetValue<double>());
    }

    [Fact]
    public void 既定の値は前の画面の既定と同じ()
    {
        // 前は画面の DEFAULT_SETTINGS(app.js)。値を変えると、設定を保存していない人の画面が変わる
        var d = ViewSettings.Defaults;
        Assert.Equal((true, false, false, false, "StartedEvent", 1.0, 20.0), (d.Contacts, d.Launches, d.AutoStart, d.SkipRedo, d.Method, d.Rate, d.Span));
        Assert.Equal(("side", 0.0, 0.0, "vis", "2d", "load", "lap"), (d.MapPos, d.MapW, d.MapH, d.ViewMode, d.MapMode, d.MapModePerf, d.MapColor));
        Assert.Equal(("tilt", "auto", "live", 0.0, 3.0, true, 0.25), (d.Map3dView, d.Map3dExag, d.MapFollow, d.MapZoom, d.GgTrail, d.GgCloud, d.LtSmooth));
        Assert.Equal(("all", "time", "auto", "auto", "auto", "graph"), (d.View, d.Axis, d.UnitSpeed, d.UnitDist, d.UnitElev, d.SettingsTab));
        Assert.Equal(d, d.WithDefaults());
    }

    [Fact]
    public void 設定のメッセージでなければ扱わない()
    {
        Assert.Null(_vm.Handle(new ViewSessionList(false)));
        Assert.Null(_vm.Handle(new ViewSetLanguage("en")));   // 画面を読み直すのは外枠
    }
}
