using Fh6.Core;

namespace Fh6.Core.Tests;

public class RaceTrackerTests
{
    private static readonly CarSignature CarA = new(2163, 2, 585, 0, 4);
    private static readonly CarSignature CarB = new(2164, 4, 738, 2, 10);

    /// <summary>合成のパケット列を作って流す</summary>
    private sealed class Feed
    {
        public readonly RaceTracker Tracker = new();
        public readonly List<RaceTrackerEvent> Events = new();
        private double _t = 1000;

        public Feed Add(int count = 1, bool on = true, int pos = 0, double raceTime = 0, double kmh = 0, CarSignature? car = null,
                        double x = 0, double z = 0, double yaw = 0, double raceTimeStep = 0)
        {
            for (int i = 0; i < count; i++)
            {
                _t += 1 / 60.0;
                var s = on
                    ? new RaceSample(_t, _t * 1000, true, pos, raceTime + i * raceTimeStep, kmh / 3.6, x, z, yaw, car ?? CarA)
                    : new RaceSample(_t, _t * 1000, false, 0, 0, 0, 0, 0, 0, default);   // 停止区間は全部 0
                Events.AddRange(Tracker.Feed(s));
            }
            return this;
        }

        public List<T> Of<T>() => Events.OfType<T>().ToList();
    }

    [Fact]
    public void 車両は30パケット続けて同じなら切り替わり()
    {
        var f = new Feed().Add(29);
        Assert.Empty(f.Of<CarChanged>());
        f.Add(1);
        var c = Assert.Single(f.Of<CarChanged>());
        Assert.Null(c.Previous);
        Assert.Equal(new[] { "記録開始時の車両" }, c.Changes);

        // 別の車が 29 パケットで戻っても切り替わらない
        f.Add(29, car: CarB).Add(5);
        Assert.Single(f.Of<CarChanged>());
        f.Add(30, car: CarB);
        var d = f.Of<CarChanged>()[1];
        Assert.Equal(CarA, d.Previous);
        Assert.Equal(new[] { "車種" }, d.Changes);

        // 同じ車で PI と駆動方式が変わった
        f.Add(30, car: CarB with { Pi = 800, Drivetrain = 1 });
        Assert.Equal(new[] { "PI", "駆動方式" }, f.Of<CarChanged>()[2].Changes);
    }

    [Fact]
    public void 停止区間と車種0は車両の判定に使わない()
    {
        var f = new Feed().Add(20).Add(50, on: false).Add(9);
        Assert.Empty(f.Of<CarChanged>());
        f.Add(1);
        Assert.Single(f.Of<CarChanged>());   // 停止区間を挟んでも数えは続く(20 + 9 + 1 = 30)

        var g = new Feed().Add(40, car: new CarSignature(0, 0, 0, 0, 0));
        Assert.Empty(g.Of<CarChanged>());
    }

    [Fact]
    public void 順位が0から1以上でレース開始_1以上から0でレース終了()
    {
        var f = new Feed()
            .Add(40)                                                          // フリーローム
            .Add(60, on: false)                                               // カウントダウン
            .Add(600, pos: 8, raceTime: 0, raceTimeStep: 1 / 60.0, kmh: 150, x: 10, z: 20, yaw: 1)
            .Add(5, pos: 0, kmh: 42);
        var s = Assert.Single(f.Of<RaceStarted>());
        Assert.False(s.Restart);
        Assert.Equal((10.0, 20.0, 1.0), (s.X, s.Z, s.Yaw));
        var e = Assert.Single(f.Of<RaceEnded>());
        Assert.Equal(42, e.LastSpeedKmh);
    }

    [Fact]
    public void 停止区間の明けに経過時間が0に戻ればリスタート()
    {
        var f = new Feed()
            .Add(300, pos: 8, raceTimeStep: 1 / 60.0)                          // 5 秒走る
            .Add(30, on: false)                                               // メニュー
            .Add(60, pos: 8, raceTime: 0, raceTimeStep: 1 / 60.0);            // 経過時間 0 から
        var starts = f.Of<RaceStarted>();
        Assert.Equal(2, starts.Count);
        Assert.True(starts[1].Restart);
    }

    [Fact]
    public void リワインドはレース開始にしない()
    {
        var f = new Feed()
            .Add(300, pos: 8, raceTimeStep: 1 / 60.0)
            .Add(30, on: false)
            .Add(60, pos: 8, raceTime: 3.2, raceTimeStep: 1 / 60.0);          // 0 より大きい値に戻る
        Assert.Single(f.Of<RaceStarted>());

        // 直前の経過時間が 3 秒以下なら、0 に戻ってもリスタートにしない(v3 と同じ)
        var g = new Feed()
            .Add(120, pos: 8, raceTimeStep: 1 / 60.0)
            .Add(30, on: false)
            .Add(60, pos: 8, raceTime: 0, raceTimeStep: 1 / 60.0);
        Assert.Single(g.Of<RaceStarted>());
    }

    [Fact]
    public void 停止区間の間は順位の変化を見ない()
    {
        // 停止区間は順位も 0 で届くが、明けて順位が 1 以上のままならレースは続いている(終了にも開始にもしない)
        var f = new Feed()
            .Add(300, pos: 8, raceTimeStep: 1 / 60.0)
            .Add(30, on: false)
            .Add(60, pos: 8, raceTime: 10, raceTimeStep: 1 / 60.0);
        Assert.Single(f.Of<RaceStarted>());
        Assert.Empty(f.Of<RaceEnded>());
    }
}

public class CourseMatcherTests
{
    // 北東へ向いたスタート(yaw 0.6 rad)
    private static readonly RegisteredCourse Course = new() { Id = "R0001", Name = "テスト", X = 100, Z = 200, Yaw = 0.6 };

    private static (double X, double Z) Offset(double along, double lateral)
    {
        double fx = Math.Sin(Course.Yaw), fz = Math.Cos(Course.Yaw);
        // 横(右)の単位ベクトルは (cos yaw, -sin yaw)
        return (Course.X + along * fx + lateral * fz, Course.Z + along * fz - lateral * fx);
    }

    [Fact]
    public void グリッドの後方71mは一致()
    {
        var (x, z) = Offset(-71, 1.5);
        var m = CourseMatcher.Match(new[] { Course }, x, z, Course.Yaw + 0.02);
        Assert.NotNull(m);
        Assert.Equal(71.0, m.Value.GridOffsetM, 1);
    }

    [Fact]
    public void 横20mは不一致()
    {
        var (x, z) = Offset(0, 20);
        Assert.Null(CourseMatcher.Match(new[] { Course }, x, z, Course.Yaw));
    }

    [Fact]
    public void 向き25度は不一致_逆向きも不一致()
    {
        Assert.Null(CourseMatcher.Match(new[] { Course }, Course.X, Course.Z, Course.Yaw + 25 * Math.PI / 180));
        Assert.Null(CourseMatcher.Match(new[] { Course }, Course.X, Course.Z, Course.Yaw + Math.PI));
        // 向きの差は ±180 度で折り返して比べる(yaw が -π と π の近くでも一致する)
        var c = new RegisteredCourse { Id = "R0002", Name = "南向き", X = 0, Z = 0, Yaw = Math.PI - 0.01 };
        Assert.NotNull(CourseMatcher.Match(new[] { c }, 0, 0, -Math.PI + 0.01));
    }

    [Fact]
    public void 近いほうのコースを選ぶ()
    {
        var near = new RegisteredCourse { Id = "R0002", Name = "近い", X = Offset(-40, 0).X, Z = Offset(-40, 0).Z, Yaw = Course.Yaw };
        var (x, z) = Offset(-45, 0);
        Assert.Equal("R0002", CourseMatcher.Match(new[] { Course, near }, x, z, Course.Yaw)!.Value.Course.Id);
    }

    [Fact]
    public void より前方のスタートを見たら基準点を動かす()
    {
        var r = new Registry();
        var c = r.AddCourse("テスト", "road", 100, 200, 0.6, null);
        double fx = Math.Sin(0.6), fz = Math.Cos(0.6);

        // 後方(グリッド 8 番手)では動かさない
        Assert.NotNull(r.MatchAndUpdate(100 - 70 * fx, 200 - 70 * fz, 0.6));
        Assert.Equal((100.0, 200.0), (c.X, c.Z));
        // 前方 10m のスタート(1 番グリッド)を見たら、そこを基準にする
        Assert.NotNull(r.MatchAndUpdate(100 + 10 * fx, 200 + 10 * fz, 0.6));
        Assert.Equal(Math.Round(100 + 10 * fx, 2), c.X);
        Assert.Equal(3, c.Seen);
    }
}
