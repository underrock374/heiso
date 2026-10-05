using Fh6.Core;

namespace Fh6.Core.Tests;

public class SyncCalcTests
{
    // 実測に合わせた合成データ: 動画 0 秒は録画開始イベントの受信時刻 t0。
    // OBS の outputDuration は実際より 0.98 秒遅れて報告され、動画は実時間の 0.998 倍で進む
    private const double T0 = 1_790_000_000.0;
    private const double Lag = 0.98;
    private const double Rate = 0.998;

    private static (VideoInfo V, double LastT) Synthetic()
    {
        var v = new VideoInfo { StartedEventRecvTime = T0 };
        double lastT = 0;
        for (int i = 0; i < 30; i++)
        {
            double t = T0 + 3 + 2 * i;
            v.SyncSamples.Add(new[] { t, (t - T0) * Rate - Lag, 20.0 });
            lastT = t;
        }
        // 往復が遅い問い合わせ(150ms 超)は、値がずれていても使われない
        v.SyncSamples.Insert(1, new[] { T0 + 4, 999.0, 400.0 });
        return (v, lastT);
    }

    [Fact]
    public void 動画0秒は録画開始イベントの時刻()
    {
        var (v, lastT) = Synthetic();
        double stop = lastT + 1.5;
        v.StopRequestedRecvTime = stop;
        v.FileDurationSec = Math.Round((stop - T0) * Rate, 3);

        SyncCalc.Finalize(v);

        Assert.Equal(T0, v.VideoZeroRecvTime);
        Assert.Equal(SyncCalc.ZeroFromStartedEvent, v.VideoZeroMethod);
        Assert.Equal(Rate, v.ClockRate);
        // 動画 0 秒から停止までの実時間と、(0.998 倍で進んだ)ファイルの長さの差
        Assert.Equal(Math.Round((stop - T0) * (1 - Rate), 3), v.EndCheckSec!.Value, 3);
    }

    [Fact]
    public void 時計が伸び縮みしなければ終端の検算は0()
    {
        var v = new VideoInfo { StartedEventRecvTime = T0, StopRequestedRecvTime = T0 + 90.8, FileDurationSec = 90.8 };
        SyncCalc.Finalize(v);
        Assert.Equal(0, v.EndCheckSec);
    }

    [Fact]
    public void 録画開始イベントが無ければsync_samplesから求める()
    {
        var (v, _) = Synthetic();
        v.StartedEventRecvTime = null;

        SyncCalc.Finalize(v);

        // 最初の5点の (recv_time − 動画秒) の中央値。OBS の遅れ(0.98 秒)と時計の比のぶん後ろにずれる
        Assert.Equal(SyncCalc.ZeroFromSyncSamples, v.VideoZeroMethod);
        Assert.Equal(T0 + Lag + 7 * (1 - Rate), v.VideoZeroRecvTime!.Value, 4);
    }

    [Fact]
    public void 対応点が無くても録画開始イベントで検算できる()
    {
        var v = new VideoInfo { StartedEventRecvTime = T0, StopRequestedRecvTime = T0 + 60, FileDurationSec = 59.0 };

        SyncCalc.Finalize(v);

        Assert.Equal(T0, v.VideoZeroRecvTime);
        Assert.Null(v.ClockRate);
        Assert.Equal(1.0, v.EndCheckSec);
    }
}
