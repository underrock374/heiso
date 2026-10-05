using System.Net;
using System.Net.Sockets;
using Fh6.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fh6.Recorder.Tests;

/// <summary>受信した UDP の送り直し(パススルー。SimHub などと併用するため)</summary>
public sealed class UdpForwardTests
{
    [Fact]
    public void 送り先の書き方と_自分自身への送り直しを断る()
    {
        var (ok, err) = TelemetryService.ParseForward(new[] { "127.0.0.1:5301", " 192.168.1.20:20777 ", "" }, 5400);
        Assert.Equal(new[] { "127.0.0.1:5301", "192.168.1.20:20777" }, ok.Select(t => t.Label));
        Assert.Equal(5301, ok[0].End.Port);
        Assert.Null(err);

        var (none, bad) = TelemetryService.ParseForward(new[] { "5301", "127.0.0.1:99999", "127.0.0.1:5400" }, 5400);
        Assert.Empty(none);
        Assert.Contains("ホスト:ポート", bad);
        Assert.Contains("ぐるぐる", bad);   // 自分の受信ポート

        Assert.Equal((0, (string?)null), (TelemetryService.ParseForward(null, 5400).Targets.Count, TelemetryService.ParseForward(null, 5400).Error));
    }

    [Fact]
    public async Task 受けたパケットをそのまま送り先へ送り直す()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));   // SimHub の代わり
        int targetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port;
        int ownPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) ownPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        var opt = new RecorderOptions { UdpPort = ownPort, ForwardTo = { $"127.0.0.1:{targetPort}" } };
        var telemetry = new TelemetryService(opt, NullLogger<TelemetryService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await telemetry.StartAsync(cts.Token);
        try
        {
            for (int i = 0; i < 50 && !telemetry.Listening; i++) await Task.Delay(50);
            Assert.True(telemetry.Listening);

            var packet = new byte[Fh6Packet.Size];
            for (int i = 0; i < packet.Length; i++) packet[i] = (byte)(i * 7);
            var shortPacket = new byte[] { 1, 2, 3 };   // 大きさの違うパケットも、そのまま送る
            using var game = new UdpClient();
            await game.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, ownPort));
            await game.SendAsync(shortPacket, new IPEndPoint(IPAddress.Loopback, ownPort));

            var r1 = await target.ReceiveAsync(cts.Token);
            var r2 = await target.ReceiveAsync(cts.Token);
            Assert.Equal(packet, r1.Buffer);
            Assert.Equal(shortPacket, r2.Buffer);
            Assert.Equal(2, telemetry.ForwardedPackets);
            Assert.Equal(1, telemetry.TotalPackets - telemetry.BadSizePackets);   // 記録の側でも受けている

            var info = telemetry.Snapshot().Forward!;
            Assert.Equal(new[] { $"127.0.0.1:{targetPort}" }, info.Targets);
            Assert.Equal(2, info.Packets);
        }
        finally
        {
            await telemetry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void 送り先を設定していなければ状態に出さない()
    {
        var telemetry = new TelemetryService(new RecorderOptions(), NullLogger<TelemetryService>.Instance);
        Assert.Null(telemetry.Snapshot().Forward);
    }
}
