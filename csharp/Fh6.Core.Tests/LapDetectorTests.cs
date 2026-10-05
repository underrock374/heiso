using Fh6.Core;

namespace Fh6.Core.Tests;

public class LapDetectorTests
{
    private sealed class Run
    {
        public readonly List<double> T = new(), On = new(), Pos = new(), Lap = new(), CurLap = new(), Dist = new(), X = new(), Z = new(), Yaw = new();
        private double _t;

        public Run Add(double lap, double curLap, double pos = 8, bool on = true, double x = 0, double z = 0, double dist = 0, double yaw = 0)
        {
            T.Add(_t); _t += 33;
            On.Add(on ? 1 : 0); Pos.Add(on ? pos : 0); Lap.Add(on ? lap : 0); CurLap.Add(on ? curLap : 0);
            Dist.Add(dist); X.Add(x); Z.Add(z); Yaw.Add(yaw);
            return this;
        }

        public List<LapEvent> Detect(IReadOnlyList<StartEvent>? starts = null) => LapDetector.Detect(
            T.ToArray(), On.ToArray(), Pos.ToArray(), Lap.ToArray(), CurLap.ToArray(), Dist.ToArray(), X.ToArray(), Z.ToArray(), Yaw.ToArray(),
            starts ?? Array.Empty<StartEvent>());
    }

    [Fact]
    public void 周回数が増えたら周のスタート()
    {
        var r = new Run();
        for (int i = 0; i < 10; i++) r.Add(0, i * 0.033, pos: 0);          // グリッド前
        for (int i = 0; i < 100; i++) r.Add(0, i * 0.5);                    // 1 周目
        for (int i = 0; i < 100; i++) r.Add(1, i * 0.5);                    // 2 周目
        for (int i = 0; i < 100; i++) r.Add(2, i * 0.5);                    // 3 周目
        var laps = r.Detect();
        Assert.Equal(2, laps.Count);
        Assert.Equal((110, 2, 49.5, true), (laps[0].Index, laps[0].Lap, laps[0].PrevLapSec!.Value, laps[0].FromLapCounter));
        Assert.Equal(3, laps[1].Lap);
        Assert.False(laps[1].IsFinish);
    }

    [Fact]
    public void リワインドで戻ってからもう一度増えても数えない()
    {
        var r = new Run().Add(0, 0, pos: 0);
        for (int i = 0; i < 50; i++) r.Add(0, i);
        for (int i = 0; i < 10; i++) r.Add(1, i);                           // 2 周目に入る
        r.Add(0, 0, on: false);                                             // リワインドの停止区間
        for (int i = 0; i < 10; i++) r.Add(0, 45 + i);                      // 1 周目の終わりに戻る
        for (int i = 0; i < 10; i++) r.Add(1, i);                           // もう一度 2 周目
        Assert.Single(r.Detect());
    }

    [Fact]
    public void ゴール直後の断片はゴール()
    {
        // 1 周だけのレース: ゴールの瞬間に周回数が 1 増え、0.1 秒でレースが終わる
        var r = new Run().Add(0, 0, pos: 0);
        for (int i = 0; i < 100; i++) r.Add(0, i * 1.7);
        for (int i = 0; i < 3; i++) r.Add(1, 0);
        r.Add(0, 0, on: false);
        var lap = Assert.Single(r.Detect());
        Assert.True(lap.IsFinish);
        Assert.Equal(168.3, lap.PrevLapSec!.Value, 1);
    }

    [Fact]
    public void 周回数が増えないタイムアタックは位置で見分ける()
    {
        // スタート地点 (0, 0) から北へ 400m 行って戻る周を 3 周。race_pos は 0、dist_traveled は増えていく
        var r = new Run();
        double dist = 0;
        for (int lap = 0; lap < 3; lap++)
        {
            for (int i = 0; i < 40; i++) r.Add(0, 0, pos: 0, x: 0, z: i * 10, dist: ++dist, yaw: 0);          // 北へ
            for (int i = 0; i < 40; i++) r.Add(0, 0, pos: 0, x: 30, z: 400 - i * 10, dist: ++dist, yaw: Math.PI); // 南へ(逆向きで近くを通る)
            for (int i = 0; i < 5; i++) r.Add(0, 0, pos: 0, x: 30 - i * 6, z: 0, dist: ++dist, yaw: -Math.PI / 2);
        }
        r.Add(0, 0, pos: 0, x: 0, z: 0, dist: ++dist, yaw: 0);
        var starts = new[] { new StartEvent(0, StartKind.Launch) };
        var laps = r.Detect(starts);
        Assert.Equal(new[] { 2, 3, 4 }, laps.Select(l => l.Lap));
        Assert.All(laps, l => Assert.False(l.FromLapCounter));
        Assert.All(laps, l => Assert.Equal(0, r.X[l.Index]));   // スタート地点を同じ向きで通った行
    }

    [Fact]
    public void フリーロームで同じ場所を通っても周にしない()
    {
        var r = new Run();
        for (int lap = 0; lap < 2; lap++)
        {
            for (int i = 0; i < 40; i++) r.Add(0, 0, pos: 0, z: i * 10);   // dist_traveled は 0 のまま
            for (int i = 0; i < 40; i++) r.Add(0, 0, pos: 0, z: 400 - i * 10);
        }
        Assert.Empty(r.Detect(new[] { new StartEvent(0, StartKind.Launch) }));
    }
}
