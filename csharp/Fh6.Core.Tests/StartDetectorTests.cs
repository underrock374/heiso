using Fh6.Core;

namespace Fh6.Core.Tests;

public class StartDetectorTests
{
    /// <summary>速度(km/h)の並びから、33ms 間隔(または指定の間隔の繰り返し)の行を作る</summary>
    private static (double[] Ts, double[] On, double[] Speed, double[] Pos) Rows(double[] kmh, int[]? gapsMs = null,
                                                                                  double[]? on = null, double pos = 1)
    {
        int n = kmh.Length;
        var ts = new double[n];
        for (int i = 1; i < n; i++) ts[i] = ts[i - 1] + (gapsMs == null ? 33 : gapsMs[(i - 1) % gapsMs.Length]);
        return (ts, on ?? Enumerable.Repeat(1.0, n).ToArray(), kmh.Select(v => v / 3.6).ToArray(), Enumerable.Repeat(pos, n).ToArray());
    }

    private static double[] Still(int rows) => Enumerable.Repeat(0.0, rows).ToArray();
    private static double[] Ramp(int rows) => Enumerable.Range(1, rows).Select(i => i * 3.0).ToArray();

    [Fact]
    public void 一秒以上止まってから動き出すとスタート()
    {
        var kmh = Still(40).Concat(Ramp(20)).ToArray();   // 39 間隔 × 33ms = 1.29 秒止まる
        var (ts, on, sp, pos) = Rows(kmh);
        var s = Assert.Single(StartDetector.Detect(ts, on, sp, pos));
        Assert.Equal(39, s.Index);     // 動き出す直前の行
        Assert.True(s.IsRace);
    }

    [Fact]
    public void 一秒未満の停止は拾わない()
    {
        var kmh = Ramp(10).Reverse().Concat(Still(25)).Concat(Ramp(20)).ToArray();   // 24 間隔 × 33ms = 0.79 秒
        var (ts, on, sp, pos) = Rows(kmh);
        Assert.Empty(StartDetector.Detect(ts, on, sp, pos));
    }

    [Fact]
    public void 停止区間をまたいで止まっていた時間を足さない()
    {
        // 走行中に止まる 0.5 秒 → メニュー(is_race_on=0)→ 走って再開 → 止まる 0.5 秒 → 発進
        var kmh = Ramp(5).Concat(Still(16)).Concat(Still(2)).Concat(Enumerable.Repeat(20.0, 5)).Concat(Still(16)).Concat(Ramp(10)).ToArray();
        var on = Enumerable.Repeat(1.0, kmh.Length).ToArray();
        on[21] = on[22] = 0;
        var (ts, _, sp, pos) = Rows(kmh);
        Assert.Empty(StartDetector.Detect(ts, on, sp, pos));
    }

    [Fact]
    public void 停止区間の明けに止まった状態から加速したらスタート()
    {
        // グリッドのカウントダウン(is_race_on=0)→ グリーンフラッグで速度 0 のまま 0.1 秒 → 加速(実測の形)
        var kmh = Enumerable.Repeat(100.0, 10).Concat(Still(3)).Concat(Still(3)).Concat(Ramp(10)).ToArray();
        var on = Enumerable.Repeat(1.0, kmh.Length).ToArray();
        on[10] = on[11] = on[12] = 0;
        var (ts, _, sp, pos) = Rows(kmh);
        var s = Assert.Single(StartDetector.Detect(ts, on, sp, pos));
        Assert.Equal(15, s.Index);
    }

    [Fact]
    public void リワインド明けは走っている状態から始まるのでスタートではない()
    {
        var kmh = Enumerable.Repeat(150.0, 10).Concat(Still(3)).Concat(Enumerable.Repeat(140.0, 10)).ToArray();
        var on = Enumerable.Repeat(1.0, kmh.Length).ToArray();
        on[10] = on[11] = on[12] = 0;
        var (ts, _, sp, pos) = Rows(kmh);
        Assert.Empty(StartDetector.Detect(ts, on, sp, pos));
    }

    [Fact]
    public void race_posでレースと発進を分ける()
    {
        var kmh = Still(40).Concat(Ramp(20)).ToArray();
        var (ts, on, sp, pos) = Rows(kmh, pos: 0);
        Assert.False(Assert.Single(StartDetector.Detect(ts, on, sp, pos)).IsRace);
    }

    [Fact]
    public void 送信間隔が不揃いでも時間で判定する()
    {
        // 31ms×6 + 47ms×1 の周期(実測のフレームペーシング)。30 行 ≒ 1.0 秒
        var gaps = new[] { 31, 31, 31, 31, 31, 31, 47 };
        var kmh = Still(34).Concat(Ramp(10)).ToArray();
        var (ts, on, sp, pos) = Rows(kmh, gaps);
        var s = Assert.Single(StartDetector.Detect(ts, on, sp, pos));
        Assert.True(ts[s.Index] - ts[0] >= 1000);

        var shortKmh = Still(28).Concat(Ramp(10)).ToArray();
        var r = Rows(shortKmh, gaps);
        Assert.Empty(StartDetector.Detect(r.Ts, r.On, r.Speed, r.Pos));
    }

    [Fact]
    public void ゆっくり動き出してもスタートとみなす()
    {
        // 止まる → 3 km/h でじわじわ → 5 km/h 超
        var kmh = Still(40).Concat(Enumerable.Repeat(3.0, 20)).Concat(Ramp(10)).ToArray();
        var (ts, on, sp, pos) = Rows(kmh);
        Assert.Equal(39, Assert.Single(StartDetector.Detect(ts, on, sp, pos)).Index);
    }
}
