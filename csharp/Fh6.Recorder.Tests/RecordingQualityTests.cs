using Microsoft.Extensions.Logging.Abstractions;

namespace Fh6.Recorder.Tests;

public class QualityPresetTests
{
    [Fact]
    public void プロファイル名は段階の名前()
    {
        Assert.Equal("HEISO 軽量", QualityPresets.Find("light")!.ProfileName);
        Assert.True(QualityPresets.IsKnown("obs"));
        Assert.False(QualityPresets.IsKnown("ultra"));
    }

    [Fact]
    public void 英語の名前のプロファイルも同じ段階として使う()
    {
        var light = QualityPresets.Find("light")!;
        Assert.Equal("HEISO Light", light.ProfileNameIn("en"));
        Assert.Equal("HEISO 軽量", light.ProfileNameIn("ja"));
        // 日本語の画面でも、英語の名前で作ったプロファイルを見つける(今の言語の名前が先)
        Assert.Equal("HEISO Light", light.FindProfile(new[] { "無題", "HEISO Light" }));
        Assert.Equal("HEISO 軽量", light.FindProfile(new[] { "HEISO Light", "HEISO 軽量" }));
        Assert.Null(light.FindProfile(new[] { "無題", "HEISO Minimum" }));
    }

    [Fact]
    public void 出力解像度は基本解像度の縦横比に合わせる()
    {
        Assert.Equal((960, 540), QualityPresets.OutputSize(QualityPresets.Find("minimum")!, 3840, 2160));
        Assert.Equal((1720, 720), QualityPresets.OutputSize(QualityPresets.Find("light")!, 3440, 1440));   // 21:9
        Assert.Equal((1280, 720), QualityPresets.OutputSize(QualityPresets.Find("standard")!, 1280, 720)); // 基本解像度より大きくしない
    }
}

public sealed class ObsQualityServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-rec-test-").FullName;
    private readonly FakeObs _fake = new();
    private ObsClient? _client;
    private CancellationTokenSource? _cts;

    public ObsQualityServiceTests()
    {
        _fake.Profiles["HEISO 軽量"] = new FakeObs.Profile { OutputWidth = 1280, OutputHeight = 720, RecQuality = "Stream", VBitrate = "6000" };
        _fake.Profiles["HEISO 高画質"] = new FakeObs.Profile { Fps = 60, RecQuality = "HQ" };
    }

    private async Task<ObsQualityService> Connect()
    {
        var opt = new RecorderOptions { OutputDir = _dir, Obs = new ObsOptions { Host = "localhost", Port = _fake.Port } };
        _client = new ObsClient(opt, NullLogger<ObsClient>.Instance);
        var service = new ObsQualityService(_client, opt, NullLogger<ObsQualityService>.Instance);
        _cts = new CancellationTokenSource();
        await _client.StartAsync(_cts.Token);
        for (int i = 0; i < 100 && !_client.Connected; i++) await Task.Delay(50);
        Assert.True(_client.Connected, "偽の OBS につながらない");
        return service;
    }

    private string RestoreFile => Path.Combine(_dir, RecorderFiles.RestoreName);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_client != null) { try { await _client.StopAsync(CancellationToken.None); } catch { } }
        await _fake.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task プロファイルを切り替えて元に戻す()
    {
        var s = await Connect();
        var q = await s.ApplyAsync("light");

        Assert.True(q.Applied);
        Assert.Null(q.Error);
        Assert.Equal("HEISO 軽量", q.Profile);
        Assert.Equal("HEISO 軽量", _fake.CurrentProfile);
        // 切り替えた後の実際の値を読んでいる
        Assert.Equal((1280, 720, 30.0, "Stream", 6000), (q.OutputWidth!.Value, q.OutputHeight!.Value, q.Fps!.Value, q.RecQuality!, q.BitrateKbps!.Value));
        Assert.True(File.Exists(RestoreFile));   // 途中で落ちても戻せるよう、元のプロファイル名を残している

        Assert.True(await s.RestoreAsync());
        Assert.Equal("無題", _fake.CurrentProfile);
        Assert.False(File.Exists(RestoreFile));
        Assert.Null(await s.RestoreAsync());   // 2回目は戻すものが無い

        // 設定値を直接書き換える要求は一度も出さない
        Assert.DoesNotContain(_fake.Snapshot(), x => x.StartsWith("SetVideoSettings") || x.StartsWith("SetProfileParameter"));
    }

    [Fact]
    public async Task OBSの設定のままなら何も変えない()
    {
        var s = await Connect();
        var q = await s.ApplyAsync(QualityPresets.Obs);
        Assert.False(q.Applied);
        Assert.Null(q.Error);
        Assert.DoesNotContain(_fake.Snapshot(), x => x.StartsWith("SetCurrentProfile"));
        Assert.Null(await s.RestoreAsync());
    }

    [Fact]
    public async Task プロファイルが無ければ作り方を案内して今の設定のまま()
    {
        var s = await Connect();
        var q = await s.ApplyAsync("medium");
        Assert.False(q.Applied);
        Assert.Contains("HEISO 中間", q.Error);
        Assert.Contains("--setup-obs-profiles", q.Error);
        Assert.Equal("無題", _fake.CurrentProfile);
        Assert.False(File.Exists(RestoreFile));
    }

    [Fact]
    public async Task 切り替えに失敗したら元に戻して理由を返す()
    {
        var s = await Connect();
        _fake.Fail = type => type == "SetCurrentProfile" && _fake.CurrentProfile == "無題" ? 500 : null;
        var q = await s.ApplyAsync("high");
        Assert.False(q.Applied);
        Assert.Contains("切り替えられませんでした", q.Error);
        Assert.Equal("無題", _fake.CurrentProfile);
        Assert.False(File.Exists(RestoreFile));
    }

    [Fact]
    public async Task 前回戻し損ねたプロファイルはOBSにつながったときに戻す()
    {
        // 前回のアプリが「HEISO 軽量」に切り替えたまま落ちた状態
        RecorderFiles.Write(RestoreFile, new ObsProfileRestore { Profile = "無題", SavedAt = "2026-09-27T19:49:00+09:00" });
        _fake.CurrentProfile = "HEISO 軽量";

        await Connect();
        for (int i = 0; i < 100 && File.Exists(RestoreFile); i++) await Task.Delay(50);

        Assert.False(File.Exists(RestoreFile));
        Assert.Equal("無題", _fake.CurrentProfile);
    }

    [Fact]
    public async Task 最後に選んだ段階を覚える()
    {
        var s = await Connect();
        Assert.Equal(QualityPresets.Obs, s.SelectedPreset);   // 初めは今までと同じ(変えない)
        s.SavePreset("medium");
        Assert.Equal("medium", s.SelectedPreset);
        Assert.Throws<ArgumentException>(() => s.SavePreset("ultra"));
        File.WriteAllText(Path.Combine(_dir, RecorderFiles.PrefsName), "{ broken");
        Assert.Equal(QualityPresets.Obs, s.SelectedPreset);
    }
}
