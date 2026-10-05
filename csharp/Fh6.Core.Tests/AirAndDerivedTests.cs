using Fh6.Core;

namespace Fh6.Core.Tests;

public class AirDetectorTests
{
    /// <summary>空中かどうかの並びから行を作る(33ms 間隔、速度 200 km/h、放物線の高さ)</summary>
    private static (double[] Ts, bool[] Air, double[] Speed, double[] Y) Rows(bool[] air)
    {
        int n = air.Length;
        var ts = Enumerable.Range(0, n).Select(i => i * 33.0).ToArray();
        var y = new double[n];
        for (int i = 0; i < n; i++) y[i] = 100;
        // 空中の区間は 5m 上がってから、飛び出しより 3m 低い所に着地
        int s = Array.IndexOf(air, true);
        if (s >= 0)
        {
            int e = Array.LastIndexOf(air, true);
            for (int i = s; i <= e; i++) { double f = (double)(i - s) / (e - s); y[i] = 100 + 5 * Math.Sin(f * Math.PI) - 3 * f; }
            for (int i = e + 1; i < n; i++) y[i] = 97;
        }
        return (ts, air, Enumerable.Repeat(200 / 3.6, n).ToArray(), y);
    }

    private static bool[] Pattern(int ground, int airRows, int after) =>
        Enumerable.Repeat(false, ground).Concat(Enumerable.Repeat(true, airRows)).Concat(Enumerable.Repeat(false, after)).ToArray();

    [Fact]
    public void 伸び切りが続いたら空中()
    {
        var (ts, air, sp, y) = Rows(Pattern(10, 40, 10));   // 39 間隔 × 33ms = 1.29 秒
        var a = Assert.Single(AirDetector.Detect(ts, air, sp, y));
        Assert.Equal(10, a.StartIndex);
        Assert.Equal(49, a.EndIndex);
        Assert.Equal(1.29, a.DurationSec, 2);
        Assert.Equal(200, a.TakeoffKmh);
        Assert.Equal(6.6, a.DropM, 1);   // 最高点 約 103.6m → 着地 97m
    }

    [Fact]
    public void 短い空中は拾わない()
    {
        var (ts, air, sp, y) = Rows(Pattern(10, 4, 10));    // 0.1 秒
        Assert.Empty(AirDetector.Detect(ts, air, sp, y));
    }

    [Fact]
    public void 一輪でも接地していれば空中でない()
    {
        double[] on = { 1, 1, 1 }, ext = { 0.01, 0.01, 0.01 }, touch = { 0.01, 0.5, 0.01 };
        Assert.Equal(new[] { true, false, true }, AirDetector.AllWheelsExtended(on, ext, ext, ext, touch));
        // 停止区間(ゼロ埋め)は空中にしない
        Assert.Equal(new[] { false }, AirDetector.AllWheelsExtended(new double[] { 0 }, new[] { 0.0 }, new[] { 0.0 }, new[] { 0.0 }, new[] { 0.0 }));
    }
}

public class DerivedSeriesTests
{
    [Fact]
    public void 一定の上り坂の勾配()
    {
        // 1m ごとに 0.08m 上がる(8%)
        int n = 100;
        var seg = new int[n];
        var dist = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var y = dist.Select(d => 50 + d * 0.08).ToArray();
        var g = DerivedSeries.Grade(seg, dist, y);
        Assert.All(g.Skip(20).Take(60), v => Assert.Equal(8, v, 6));
    }

    [Fact]
    public void 範囲が短いときと区間の外は勾配を出さない()
    {
        var seg = new[] { 0, 0, 0, -1, 1, 1 };
        var dist = new[] { 0.0, 2, 4, double.NaN, 4, 6 };
        var y = new[] { 0.0, 1, 2, 0, 2, 3 };
        Assert.All(DerivedSeries.Grade(seg, dist, y), v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void 勾配は区間をまたがない()
    {
        // 区間 0 は平ら、区間 1 は 10% の上り。境目の近くでも混ざらない
        int n = 80;
        var seg = Enumerable.Range(0, n).Select(i => i < 40 ? 0 : 1).ToArray();
        var dist = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var y = Enumerable.Range(0, n).Select(i => i < 40 ? 100.0 : 200 + (i - 40) * 0.1).ToArray();
        var g = DerivedSeries.Grade(seg, dist, y);
        Assert.Equal(0, g[38], 6);
        Assert.Equal(10, g[41], 6);
    }

    [Fact]
    public void 旋回の曲がり具合()
    {
        // 20 m/s で 0.2 rad/s の右旋回 = R 100m → 1000/R = 10。左は負
        var c = DerivedSeries.TurnCurvature(new double[] { 1, 1, 1, 1, 0 }, new[] { 0.2, -0.2, 0.2, 5.0, 0.2 }, new[] { 20.0, 20, 3, 20, 20 });
        Assert.Equal(10, c[0], 6);
        Assert.Equal(-10, c[1], 6);
        Assert.True(double.IsNaN(c[2]));   // 18 km/h 未満
        Assert.Equal(DerivedSeries.TurnLimit, c[3]);   // 頭打ち
        Assert.True(double.IsNaN(c[4]));   // 停止区間
    }

    [Theory]
    [InlineData(0.0, SurfaceKind.Paved)]
    [InlineData(0.10, SurfaceKind.Snow)]
    [InlineData(0.12, SurfaceKind.Dirt)]
    [InlineData(0.20, SurfaceKind.Ramp)]
    [InlineData(0.60, SurfaceKind.Grass)]
    [InlineData(0.24, SurfaceKind.Kerb)]
    [InlineData(0.35, SurfaceKind.Unknown)]
    [InlineData(double.NaN, SurfaceKind.Unknown)]
    public void 路面の分類(double rumble, SurfaceKind kind)
    {
        Assert.Equal(kind, DerivedSeries.ClassifySurface(rumble));
    }
}
