using System.Text.Json.Nodes;
using Fh6.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fh6.Recorder.Tests;

/// <summary>記録の開始から停止までで、OBS への要求が 切り替え → 録画 → 戻し の順に出ること</summary>
public sealed class SessionControllerQualityTests : IAsyncDisposable
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

    private async Task<SessionController> Setup()
    {
        var opt = new RecorderOptions
        {
            OutputDir = _dir, SaveRawPackets = false,
            Obs = new ObsOptions { Host = "localhost", Port = _fake.Port, RecordIntoSessionFolder = true },
        };
        _client = new ObsClient(opt, NullLogger<ObsClient>.Instance);
        var quality = new ObsQualityService(_client, opt, NullLogger<ObsQualityService>.Instance);
        var telemetry = new TelemetryService(opt, NullLogger<TelemetryService>.Instance);   // 受信は始めない
        var controller = new SessionController(opt, telemetry, _client, quality, NullLogger<SessionController>.Instance);
        await _client.StartAsync(_cts.Token);
        for (int i = 0; i < 100 && !_client.Connected; i++) await Task.Delay(50);
        Assert.True(_client.Connected);
        return controller;
    }

    [Fact]
    public async Task 切り替えてから録画し止めてから戻す()
    {
        _fake.Profiles["HEISO 最小"] = new FakeObs.Profile { OutputWidth = 960, OutputHeight = 540, Mode = "Advanced", RecQuality = "Stream", VBitrate = "2500" };
        var controller = await Setup();
        _fake.RecordPath = Path.Combine(AppContext.BaseDirectory, "testdata", "video", "normal.mp4");

        var (ok, error) = await controller.StartAsync(video: true, label: "test", quality: "minimum");
        Assert.True(ok, error);
        (ok, error) = await controller.StopAsync();
        Assert.True(ok, error);

        // プロファイルの切り替え → 録画先 → 録画開始(切り替えたプロファイルで)→ 録画停止 → 録画先を戻す → プロファイルを戻す
        var log = _fake.Snapshot();
        int IndexOf(string x) => log.FindIndex(l => l.StartsWith(x));
        int LastIndexOf(string x) => log.FindLastIndex(l => l.StartsWith(x));
        Assert.True(IndexOf("SetCurrentProfile HEISO 最小") >= 0);
        Assert.True(IndexOf("SetCurrentProfile HEISO 最小") < IndexOf("SetRecordDirectory"));
        Assert.True(IndexOf("SetRecordDirectory") < IndexOf("StartRecord (HEISO 最小)"));
        Assert.True(IndexOf("StartRecord") < IndexOf("StopRecord"));
        Assert.True(IndexOf("StopRecord") < LastIndexOf("SetRecordDirectory"));
        Assert.True(LastIndexOf("SetRecordDirectory") < IndexOf("SetCurrentProfile 無題"));
        Assert.Equal("無題", _fake.CurrentProfile);
        Assert.Equal(@"C:\Videos", _fake.RecordDirectory);

        // session.json に選んだ段階と実際の値が残る
        var folder = Directory.GetDirectories(_dir).Single();
        var meta = SessionMeta.Load(folder);
        var q = meta.Video.Quality!;
        Assert.Equal(("minimum", true, "HEISO 最小"), (q.Preset, q.Applied, q.Profile!));
        Assert.Equal((960, 540, 30.0, "Stream", 2500), (q.OutputWidth!.Value, q.OutputHeight!.Value, q.Fps!.Value, q.RecQuality!, q.BitrateKbps!.Value));
        Assert.Equal("Advanced", q.OutputMode);
        // 録画の開始を要求した時刻は、録画開始イベントを受けた時刻より前
        Assert.NotNull(meta.Video.StartRequestedRecvTime);
        Assert.True(meta.Video.StartRequestedRecvTime <= meta.Video.StartedEventRecvTime);
        Assert.Contains(meta.Events, e => e.Type == "obs_quality_applied");
        Assert.Contains(meta.Events, e => e.Type == "obs_quality_restored");
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, SessionMeta.FileName)))!;
        Assert.Equal("HEISO 最小", (string)saved["video"]!["quality"]!["profile"]!);

        // 選んだ段階は次の記録のために覚えている
        var status = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(controller.GetStatus()))!;
        Assert.Equal("minimum", (string)status["quality"]!["selected"]!);
    }

    [Fact]
    public async Task ログだけの記録ではOBSの設定に触れない()
    {
        var controller = await Setup();
        var (ok, _) = await controller.StartAsync(video: false, label: null, quality: "high");
        Assert.True(ok);
        await controller.StopAsync();
        Assert.DoesNotContain(_fake.Snapshot(), x => x.StartsWith("SetCurrentProfile") || x.StartsWith("SetRecordDirectory"));
    }
}
