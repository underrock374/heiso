using Fh6.Core;

namespace Fh6.Core.Tests;

public class VideoTimeMapTests
{
    // SyncCalcTests と同じ条件: 開始イベントの受信が t0、動画 0 秒はその 0.98 秒後、動画は実時間の 0.998 倍で進む
    private const double T0 = 1_790_000_000.0;
    private const double Zero = T0 + 0.98;
    private const double Rate = 0.998;

    private static VideoInfo Synthetic()
    {
        var v = new VideoInfo { StartedEventRecvTime = T0 };
        for (int i = 0; i < 30; i++)
        {
            double t = T0 + 3 + 2 * i;
            v.SyncSamples.Add(new[] { t, (t - Zero) * Rate, 20.0 });
        }
        return v;
    }

    private static VideoTimeMap Map(VideoInfo v, VideoSyncMethod m = VideoSyncMethod.SyncSamples)
    {
        var map = VideoTimeMap.Create(v, m, out var error);
        Assert.Null(error);
        return map!;
    }

    [Fact]
    public void 点の上と点の間で正しく変換できる()
    {
        var map = Map(Synthetic());
        Assert.Equal(30, map.SamplesUsed);
        Assert.False(map.IsFallback);

        foreach (var t in new[] { T0 + 3, T0 + 4, T0 + 10.3, T0 + 61 })
            Assert.Equal((t - Zero) * Rate, map.ToVideoSec(t), 9);
    }

    [Fact]
    public void 往復で元に戻る()
    {
        var map = Map(Synthetic());
        foreach (var t in new[] { T0, T0 + 3, T0 + 17.77, T0 + 61, T0 + 100 })
            Assert.Equal(t, map.ToRecvTime(map.ToVideoSec(t)), 6);
    }

    [Fact]
    public void 範囲外は傾き1で延長する()
    {
        var map = Map(Synthetic());
        double first = T0 + 3, last = T0 + 3 + 2 * 29;
        Assert.Equal(map.ToVideoSec(first) - 2.5, map.ToVideoSec(first - 2.5), 9);
        Assert.Equal(map.ToVideoSec(last) + 10, map.ToVideoSec(last + 10), 9);
    }

    [Fact]
    public void 往復が遅い点と動画秒が減る点は使わない()
    {
        var v = Synthetic();
        v.SyncSamples.Add(new[] { T0 + 4, 999.0, 400.0 });          // 往復 400ms
        v.SyncSamples.Add(new[] { T0 + 6, 0.5, 20.0 });             // 動画秒が前の点より小さい
        var map = Map(v);
        Assert.Equal(30, map.SamplesUsed);
        Assert.Equal((T0 + 4 - Zero) * Rate, map.ToVideoSec(T0 + 4), 9);
    }

    [Fact]
    public void 開始イベント方式は受信時刻を0秒とする()
    {
        var map = Map(Synthetic(), VideoSyncMethod.StartedEvent);
        Assert.Equal(0, map.ToVideoSec(T0), 9);
        Assert.Equal(10, map.ToVideoSec(T0 + 10), 9);
        // sync_samples 方式との差は約 0.98 秒
        Assert.Equal(0.98, map.ToVideoSec(T0 + 3) - Map(Synthetic()).ToVideoSec(T0 + 3), 2);
    }

    [Fact]
    public void 対応表の方式で点が無ければ録画開始イベントで代用する()
    {
        var v = new VideoInfo { VideoZeroRecvTime = Zero, StartedEventRecvTime = T0 };
        var map = Map(v);
        Assert.True(map.IsFallback);
        Assert.Equal(5, map.ToVideoSec(T0 + 5), 9);
    }

    [Fact]
    public void 録画開始イベントも点も無ければvideo_zero_recv_timeで代用する()
    {
        var v = new VideoInfo { VideoZeroRecvTime = Zero };
        var map = Map(v, VideoSyncMethod.StartedEvent);
        Assert.True(map.IsFallback);
        Assert.Equal(5, map.ToVideoSec(Zero + 5), 9);
    }

    [Fact]
    public void 録画開始イベントが無ければ対応表で代用する()
    {
        var v = Synthetic();
        v.StartedEventRecvTime = null;
        var map = Map(v, VideoSyncMethod.StartedEvent);
        Assert.True(map.IsFallback);
        Assert.Equal(30, map.SamplesUsed);
        Assert.Equal((T0 + 10 - Zero) * Rate, map.ToVideoSec(T0 + 10), 9);
    }

    [Fact]
    public void 既定の方式は録画開始イベント()
    {
        Assert.Equal(VideoSyncMethod.StartedEvent, default(VideoSyncMethod));
        var map = Map(Synthetic(), VideoSyncMethod.StartedEvent);
        Assert.False(map.IsFallback);
        Assert.Equal(0, map.SamplesUsed);
    }

    [Fact]
    public void 基準が何も無ければnull()
    {
        Assert.Null(VideoTimeMap.Create(new VideoInfo(), VideoSyncMethod.SyncSamples, out var e1));
        Assert.NotNull(e1);
        Assert.Null(VideoTimeMap.Create(new VideoInfo(), VideoSyncMethod.StartedEvent, out var e2));
        Assert.NotNull(e2);
    }

    [Fact]
    public void manual_syncは最後の点だけが効く()
    {
        var v = Synthetic();
        double t = T0 + 20;
        double auto = Map(v).ToVideoSec(t);
        v.ManualSync.Add(new ManualSyncPoint { RecvTime = t, VideoSec = auto + 5 });   // 古い点(使われない)
        v.ManualSync.Add(new ManualSyncPoint { RecvTime = t, VideoSec = auto + 0.12 });

        var map = Map(v);
        Assert.True(map.HasManualSync);
        Assert.Equal(0.12, map.ManualShiftSec, 9);
        Assert.Equal(auto + 0.12, map.ToVideoSec(t), 9);
        Assert.Equal(Map(Synthetic()).ToVideoSec(T0 + 50) + 0.12, map.ToVideoSec(T0 + 50), 9);
        Assert.Equal(t, map.ToRecvTime(auto + 0.12), 6);
    }
}
