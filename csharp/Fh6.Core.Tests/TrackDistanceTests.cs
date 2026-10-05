using Fh6.Core;

namespace Fh6.Core.Tests;

public class TrackDistanceTests
{
    [Fact]
    public void 直進の累積距離()
    {
        // 東へ 3m、北へ 4m ずつ = 1 サンプル 5m
        var on = new double[] { 1, 1, 1, 1 };
        var x = new double[] { 0, 3, 6, 9 };
        var z = new double[] { 0, 4, 8, 12 };
        var (seg, dist) = TrackDistance.Compute(on, x, z);
        Assert.Equal(new[] { 0, 0, 0, 0 }, seg);
        Assert.Equal(new double[] { 0, 5, 10, 15 }, dist);
    }

    [Fact]
    public void 停止区間で区間が切れて距離は足さない()
    {
        var on = new double[] { 1, 1, 0, 0, 1, 1 };
        var x = new double[] { 0, 10, 0, 0, 50, 60 };
        var z = new double[] { 0, 0, 0, 0, 0, 0 };
        var (seg, dist) = TrackDistance.Compute(on, x, z);
        Assert.Equal(new[] { 0, 0, -1, -1, 1, 1 }, seg);
        Assert.Equal(10, dist[1]);
        Assert.True(double.IsNaN(dist[2]));
        // 停止前の 10m のまま再開し、10m から先を足す(停止中の移動 40m は足さない)
        Assert.Equal(10, dist[4]);
        Assert.Equal(20, dist[5]);
    }

    [Fact]
    public void ファストトラベルで区間が切れて距離は足さない()
    {
        var on = new double[] { 1, 1, 1, 1 };
        var x = new double[] { 0, 1, 3001, 3002 };
        var z = new double[] { 0, 0, 0, 0 };
        var (seg, dist) = TrackDistance.Compute(on, x, z);
        Assert.Equal(new[] { 0, 0, 1, 1 }, seg);
        Assert.Equal(new double[] { 0, 1, 1, 2 }, dist);
    }

    [Fact]
    public void 座標が無い行は区間の外()
    {
        var on = new double[] { 1, 1, 1 };
        var x = new double[] { 0, double.NaN, 2 };
        var z = new double[] { 0, 0, 0 };
        var (seg, _) = TrackDistance.Compute(on, x, z);
        Assert.Equal(new[] { 0, -1, 1 }, seg);
    }
}
