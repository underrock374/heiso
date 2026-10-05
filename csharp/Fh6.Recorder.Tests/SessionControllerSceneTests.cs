using Fh6.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fh6.Recorder.Tests;

/// <summary>録画するシーン: OBS で別のシーンを選んだままでも、選んだシーンに切り替えて録画し、止めたら元に戻す</summary>
public sealed class SessionControllerSceneTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-rec-test-").FullName;
    private readonly FakeObs _fake = new();
    private ObsClient? _client;
    private readonly CancellationTokenSource _cts = new();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_client != null) { try { await _client.StopAsync(CancellationToken.None); } catch { } }
        await _fake.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(SessionController, ObsSceneService, ObsQualityService)> Setup()
    {
        var opt = new RecorderOptions
        {
            OutputDir = _dir, SaveRawPackets = false,
            Obs = new ObsOptions { Host = "localhost", Port = _fake.Port, RecordIntoSessionFolder = true },
        };
        _client = new ObsClient(opt, NullLogger<ObsClient>.Instance);
        var quality = new ObsQualityService(_client, opt, NullLogger<ObsQualityService>.Instance);
        var scene = new ObsSceneService(_client, opt, NullLogger<ObsSceneService>.Instance);
        var telemetry = new TelemetryService(opt, NullLogger<TelemetryService>.Instance);   // 受信は始めない
        var controller = new SessionController(opt, telemetry, _client, quality, NullLogger<SessionController>.Instance, scene: scene);
        await _client.StartAsync(_cts.Token);
        for (int i = 0; i < 100 && !_client.Connected; i++) await Task.Delay(50);
        Assert.True(_client.Connected);
        return (controller, scene, quality);
    }

    private async Task Record(SessionController controller)
    {
        _fake.RecordPath = Path.Combine(AppContext.BaseDirectory, "testdata", "video", "normal.mp4");
        var (ok, error) = await controller.StartAsync(video: true, label: "test", quality: "obs");
        Assert.True(ok, error);
        (ok, error) = await controller.StopAsync();
        Assert.True(ok, error);
    }

    [Fact]
    public async Task 別のシーンを選んだままでも_選んだシーンで録画して元に戻す()
    {
        _fake.Scenes.Clear();
        _fake.Scenes.AddRange(new[] { "ゲーム", "画面キャプチャ" });
        _fake.CurrentScene = "画面キャプチャ";   // 撮影のあと戻し忘れた
        var (controller, scene, _) = await Setup();
        scene.SaveScene("ゲーム");

        await Record(controller);

        var log = _fake.Snapshot();
        int IndexOf(string x) => log.FindIndex(l => l.StartsWith(x));
        Assert.True(IndexOf("SetCurrentProgramScene ゲーム") >= 0);
        Assert.True(IndexOf("SetCurrentProgramScene ゲーム") < IndexOf("StartRecord"));
        Assert.Contains("RecordScene ゲーム", log);
        Assert.True(IndexOf("StopRecord") < IndexOf("SetCurrentProgramScene 画面キャプチャ"));
        Assert.Equal("画面キャプチャ", _fake.CurrentScene);

        var meta = SessionMeta.Load(Directory.GetDirectories(_dir).Single());
        var sc = meta.Video.Scene!;
        Assert.Equal(("ゲーム", "ゲーム", true, "画面キャプチャ"), (sc.Selected, sc.Name, sc.Applied, sc.Previous));
        Assert.Null(sc.Error);
        Assert.Contains(meta.Events, e => e.Type == "obs_scene_applied");
        Assert.Contains(meta.Events, e => e.Type == "obs_scene_restored");
        Assert.False(File.Exists(Path.Combine(_dir, ObsSceneService.RestoreName)));
    }

    [Fact]
    public async Task 選んでいなければ_今のシーンのまま録画し_そのシーンを残す()
    {
        _fake.Scenes.AddRange(new[] { "ゲーム" });
        _fake.CurrentScene = "ゲーム";
        var (controller, _, _) = await Setup();

        await Record(controller);

        Assert.DoesNotContain(_fake.Snapshot(), l => l.StartsWith("SetCurrentProgramScene"));
        var sc = SessionMeta.Load(Directory.GetDirectories(_dir).Single()).Video.Scene!;
        Assert.Equal((null, "ゲーム", false), (sc.Selected, sc.Name, sc.Applied));
    }

    [Fact]
    public async Task 選んだシーンが無ければ_今のシーンで録画して理由を残す()
    {
        var (controller, scene, _) = await Setup();
        scene.SaveScene("消したシーン");

        await Record(controller);

        Assert.DoesNotContain(_fake.Snapshot(), l => l.StartsWith("SetCurrentProgramScene"));
        var meta = SessionMeta.Load(Directory.GetDirectories(_dir).Single());
        var sc = meta.Video.Scene!;
        Assert.Equal(("消したシーン", "シーン", false), (sc.Selected, sc.Name, sc.Applied));
        Assert.NotNull(sc.Error);
        Assert.Contains(meta.Events, e => e.Type == "obs_scene_failed");
        Assert.True(meta.Video.Active == false && meta.Files.Video != null);   // 録画はできている
    }

    [Fact]
    public async Task シーンと画質は同じファイルに覚え_片方を変えてももう片方は消えない()
    {
        var (_, scene, quality) = await Setup();
        scene.SaveScene("ゲーム");
        quality.SavePreset("light");
        Assert.Equal("ゲーム", scene.SelectedScene);
        scene.SaveScene("");
        Assert.Null(scene.SelectedScene);
        Assert.Equal("light", quality.SelectedPreset);
    }

    [Fact]
    public async Task 前回戻し損ねたシーンは_OBSにつながったときに戻す()
    {
        _fake.Scenes.Clear();
        _fake.Scenes.AddRange(new[] { "ゲーム", "画面キャプチャ" });
        _fake.CurrentScene = "ゲーム";
        RecorderFiles.Write(Path.Combine(_dir, ObsSceneService.RestoreName), new ObsSceneRestore { Scene = "画面キャプチャ", SavedAt = "x" });
        await Setup();
        for (int i = 0; i < 100 && _fake.CurrentScene != "画面キャプチャ"; i++) await Task.Delay(50);
        Assert.Equal("画面キャプチャ", _fake.CurrentScene);
        for (int i = 0; i < 40 && File.Exists(Path.Combine(_dir, ObsSceneService.RestoreName)); i++) await Task.Delay(50);
        Assert.False(File.Exists(Path.Combine(_dir, ObsSceneService.RestoreName)));
    }
}
