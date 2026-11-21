namespace Fh6.Player.App.Tests;

/// <summary>
/// 摩擦円・荷重移動の計算(FrictionDynamic)の意味の確かめ。合成データ(30Hz。舗装を半径 1.5G の円運動で 3000 行、そのあとダートを半径 0.8G で 600 行)で、
/// 円の大きさ・向き・ならし方・除く行が、画面の説明のとおりになるか。前は tools/dev/frictioncircle.test.mjs で JS を確かめていたもの
/// </summary>
public sealed class FrictionDynamicBehaviorTests
{
    private const int N = 3600, DirtFrom = 3000, StraightRows = 300;
    private static readonly string[] Wheels = ["fl", "fr", "rl", "rr"];

    /// <summary>合成データ。load なら、最初の 10 秒をまっすぐ一定の速さにして、縮みを付ける(前輪の素の縮み 0.25、後輪 0.35。1G で差 0.2)</summary>
    private sealed class Synthetic
    {
        public double[] T = new double[N];
        public double?[] Speed = new double?[N], GLat = new double?[N], GLong = new double?[N];
        public Dictionary<string, int?[]> Surface = Wheels.ToDictionary(w => w, _ => new int?[N]);
        public Dictionary<string, double?[]> Susp = Wheels.ToDictionary(w => w, _ => new double?[N]);
        public List<int> Excluded = new();

        public Synthetic(bool load = false)
        {
            for (int i = 0; i < N; i++)
            {
                bool dirt = i >= DirtFrom;
                double r = dirt ? 0.8 : 1.5, a = i * 0.02;
                T[i] = i / 30.0;
                Speed[i] = 100;
                GLat[i] = r * Math.Sin(a);     // 右が正
                GLong[i] = r * Math.Cos(a);    // 前が正
                foreach (var w in Wheels) Surface[w][i] = dirt ? 2 : 0;
                if (load && i < StraightRows) { GLat[i] = 0; GLong[i] = 0; }
                // 前後・左右それぞれ、片側の 2 輪が +、反対が −
                double fb = -0.1 * GLong[i]!.Value, lr = -0.1 * GLat[i]!.Value;
                Susp["fl"][i] = 0.25 + fb - lr; Susp["fr"][i] = 0.25 + fb + lr;
                Susp["rl"][i] = 0.35 - fb - lr; Susp["rr"][i] = 0.35 - fb + lr;
            }
        }

        public SurfaceSeries SurfaceSeries => new(Surface["fl"], Surface["fr"], Surface["rl"], Surface["rr"]);
        public SuspensionSeries SuspSeries => new(Susp["fl"], Susp["fr"], Susp["rl"], Susp["rr"]);

        public SessionSeries Series() => new(
            T: T, Speed: Speed, Accel: null!, Brake: null!, Steer: null!, Gear: null!, SlipF: null!, SlipR: null!,
            Balance: null!, Counter: null!, Grade: null!, Elev: null!, X: null!, Z: null!, Turn: null!, GLong: GLong, GLat: GLat,
            Susp: SuspSeries, Rpm: null!, OptRpm: null!, OptRpmHi: null!, Gain: null!, Full: null!, PerfCar: null!, Perf: null!, Readout: null!,
            Surface: SurfaceSeries, Course: null!, Friction: new FrictionSeries(FrictionStatic.SurfaceGroups(SurfaceSeries), null!));

        public SessionMarks Marks(params List<int>[] races) => new(
            Starts: [], Contacts: [], Excursions: [], Skips: [], Laps: [], Shifts: [], Jumps: [], ElevRange: null, Cut: [], GgExcluded: Excluded.ToArray(),
            Races: races.Select(rows => new RaceMark(
                T0: 0, T1: 0, Tf: 0, Restart: false, Finished: true, FinishedBy: "", EndKmh: 0, Course: null, Car: null, Setup: null,
                Dist: new RaceDistanceSeries(rows, [], [], []), ElevRange: null, Auto: null, AutoLo: null, AutoHi: null)).ToArray());

        public HostFriction Build(double sec = FrictionDynamic.DefaultSmoothSec, int? race = null, params List<int>[] races) =>
            FrictionDynamic.Build(Series(), Marks(races), race, sec);
    }

    private static double R(FrictionView v, int group) => v.Rings.Single(r => r.Group == group).R;

    [Fact]
    public void 百分位は前のJSと同じ選び方()
    {
        Assert.Equal(5, FrictionDynamic.Percentile([5, 1, 3, 2, 4], 0.98));
        Assert.Equal(2, FrictionDynamic.Percentile([5, 1, 3, 2, 4], 0.4));
        Assert.Equal(2, FrictionDynamic.Percentile([4, 1, 3, 2], 0.5));   // 偶数個の中央値は小さい方
    }

    [Fact]
    public void 円は路面ごとに出た大きさの上位2パーセント()
    {
        var f = new Synthetic().Build();
        Assert.Equal(1.5, R(f.Gg, 0), 0.02);
        Assert.Equal(0.8, R(f.Gg, 2), 0.02);
        Assert.DoesNotContain(f.Gg.Rings, r => r.Group == 1);                 // 走っていない路面の円は無い
        Assert.Equal(1.5 * FrictionDynamic.CapRatio, f.Gg.ScaleMax, 0.05);    // 表示の上限は最大の円の 1.3 倍

        // 行数が足りない路面は円を作らない(ダートを 100 行だけのレースにする)
        var few = Enumerable.Range(0, DirtFrom + 100).ToList();
        Assert.True(100 < FrictionDynamic.RingMinRows);
        Assert.DoesNotContain(new Synthetic().Build(race: 0, races: few).Gg.Rings, r => r.Group == 2);

        // 低速の行は推定に使わない
        var slow = new Synthetic();
        Array.Fill(slow.Speed, 10);
        var fs = slow.Build();
        Assert.Empty(fs.Gg.Rings);
        Assert.Equal(FrictionDynamic.DefaultMaxG, fs.Gg.ScaleMax);
        Assert.Equal(FrictionDynamic.DefaultMaxLoad, fs.Load.ScaleMax);
    }

    [Fact]
    public void 接触のまわりとはね上がりは円を膨らませない()
    {
        // 接触のまわり(前後 0.3 秒 = 19 行)に 5G(上限の 6G 未満)のはね上がりを 4 か所。舗装の 2.5% で、上位 2% の円を膨らませる量
        var spike = new Synthetic();
        var contacts = new List<double>();
        foreach (int c in (int[])[500, 1000, 1500, 2000])
        {
            for (int i = c - 9; i <= c + 9; i++) { spike.GLat[i] = 5; spike.GLong[i] = 0; }
            contacts.Add(c / 30.0);
        }
        for (int i = 2500; i < 2505; i++) { spike.GLat[i] = 15; spike.GLong[i] = 0; }   // 15G は印が無くても使わない
        Assert.True(R(spike.Build(sec: 0.01).Gg, 0) > 1.6);                               // 除かなければ円が膨らむ(この確かめの前提)
        spike.Excluded = FrictionStatic.ExcludedRows(spike.T, contacts, []).ToList();
        var f = spike.Build(sec: 0.01);
        Assert.Equal(1.5, R(f.Gg, 0), 0.02);
        Assert.DoesNotContain(spike.Excluded, f.Gg.Cloud.Contains);                       // 点群にも出さない
        Assert.Null(f.Grip[2502]);                                                        // はね上がりは「摩擦円の %」も出さない
    }

    [Fact]
    public void Gのならしは前後に同じだけ広がり幅の外へ届かない()
    {
        var d = new Synthetic(load: true);
        d.GLong[150] = 1;   // まっすぐの区間に 1 行だけの G の段差
        double? G(double sec, int i) => FrictionDynamic.SmoothG(d.T, d.GLat, d.GLong, sec).Y[i];
        Assert.InRange(G(0.25, 150)!.Value, 1e-9, 1.0 / 3);
        Assert.Equal(G(0.25, 148)!.Value, G(0.25, 152)!.Value, 1e-12);
        Assert.Equal(0, G(0.25, 158)!.Value, 1e-12);
        Assert.Equal(1.0 / 3, G(0, 150)!.Value, 1e-12);   // 幅 0 なら前後 3 行の平均(基準を選ぶとき)
        d.GLat[50] = null;
        Assert.Null(FrictionDynamic.SmoothG(d.T, d.GLat, d.GLong, 0.25).X[50]);   // 値が無い行は null
    }

    [Fact]
    public void 荷重移動の基準はまっすぐ一定の速さのときの差()
    {
        var d = new Synthetic(load: true);
        var raw = FrictionDynamic.SmoothLoad(d.T, d.SuspSeries, 0.25);
        Assert.Equal(0, raw.X[100]!.Value, 1e-9);
        Assert.Equal(-0.1, raw.Y[100]!.Value, 1e-9);   // ばねの違いの分
        var all = Enumerable.Range(0, N).ToArray();
        var b = FrictionDynamic.BuildBaseline(all, new bool[N], d.Speed, FrictionDynamic.SmoothG(d.T, d.GLat, d.GLong, 0), raw, d.SuspSeries);
        Assert.Equal(0, b.X, 1e-9);
        Assert.Equal(-0.1, b.Y, 1e-9);
        Assert.True(b.N >= 290, $"まっすぐの行を基準に使う: {b.N}");
        Assert.Equal(0.25, b.Wheels![0], 1e-9);   // 4 輪の棒の白の線: 4 輪それぞれのまっすぐのときの縮み
        Assert.Equal(0.35, b.Wheels![3], 1e-9);
        Assert.Equal(b.Wheels, d.Build().BaseWheels);

        // 空中(どれかの車輪が伸び切り)は使わない。となりの行は、伸び切りの行を除いてならす
        var air = new Synthetic(load: true);
        air.Susp["rl"][500] = 0.01;
        var ra = FrictionDynamic.SmoothLoad(air.T, air.SuspSeries, 0.25);
        Assert.Null(ra.X[500]);
        Assert.NotNull(ra.X[501]);
    }

    [Fact]
    public void 縮みの差のならしは幅の中だけに届く()
    {
        // 1 行だけの段差(前後の差 +0.3)は薄まり、前後に同じだけ広がる(遅れない)。0.25 秒より離れた行には届かない
        var bump = new Synthetic(load: true);
        bump.Susp["fl"][150] += 0.15; bump.Susp["fr"][150] += 0.15; bump.Susp["rl"][150] -= 0.15; bump.Susp["rr"][150] -= 0.15;
        double At(double sec, int i) => FrictionDynamic.SmoothLoad(bump.T, bump.SuspSeries, sec).Y[i]!.Value + 0.1;
        Assert.InRange(At(0.25, 150), 1e-9, 0.3 / 3);
        Assert.Equal(At(0.25, 148), At(0.25, 152), 1e-12);
        Assert.True(At(0.25, 157) > 0);
        Assert.Equal(0, At(0.25, 158), 1e-12);
        // 幅を 1 秒にすると、もっと薄まって遠くまで届く
        Assert.True(At(1, 150) < At(0.25, 150) / 2);
        Assert.True(At(1, 170) > 0);
        Assert.Equal(0, At(1, 180), 1e-12);
    }

    [Fact]
    public void 荷重移動の円と向きとグラフの系列()
    {
        var d = new Synthetic(load: true);
        var f = d.Build();
        // 舗装は 1.5G の円運動 → 縮みの差は 0.2 × 1.5 = 0.3。ダートは 0.8G → 0.16
        Assert.Equal(0.3, R(f.Load, 0), 0.01);
        Assert.Equal(0.16, R(f.Load, 2), 0.01);
        Assert.Equal(0.3 * FrictionDynamic.CapRatio, f.Load.ScaleMax, 0.02);

        // 向き: ブレーキ(前後 G が負)で前が沈む、右コーナー(横 G が正)で左が沈む
        int brake = Enumerable.Range(StraightRows, DirtFrom - StraightRows).First(i => d.GLong[i] < -1.45);
        int right = Enumerable.Range(StraightRows, DirtFrom - StraightRows).First(i => d.GLat[i] > 1.45);
        Assert.True(f.Load.Y[brake] > 0.25 && Math.Abs(f.Load.X[brake]!.Value) < 0.1, $"ブレーキで前が沈む: {f.Load.X[brake]} {f.Load.Y[brake]}");
        Assert.True(f.Load.X[right] < -0.25 && Math.Abs(f.Load.Y[right]!.Value) < 0.1, $"右コーナーで左が沈む: {f.Load.X[right]} {f.Load.Y[right]}");

        // グラフの系列は G と同じ向き(ブレーキで前後が負、右コーナーで左右が正)の %。値は荷重移動の点と同じ(向きだけ逆)
        Assert.Equal(N, f.LoadLong.Length);
        Assert.True(f.LoadLong[brake] < -25);
        Assert.True(f.LoadLat[right] > 25);
        Assert.Equal(-100 * f.Load.Y[brake]!.Value, f.LoadLong[brake]!.Value, 0.06);
        // 「ならし」を変えると、点もグラフの系列も同じ幅でならす
        var f1 = d.Build(sec: 1);
        Assert.NotEqual(f.Load.Y[brake], f1.Load.Y[brake]);
        Assert.Equal(-100 * f1.Load.Y[brake]!.Value, f1.LoadLong[brake]!.Value, 0.06);
    }

    [Fact]
    public void 摩擦円のパーセントはその路面の円で割る()
    {
        var f = new Synthetic(load: true).Build();
        Assert.Equal(100, f.Grip[1000]!.Value, 3);   // 舗装: 1.5G の円運動は円の 100% 前後
        Assert.Equal(100, f.Grip[3300]!.Value, 3);   // ダート: 0.8G の円運動も、ダートの円に対して 100% 前後

        // 路面が混ざれば、4 輪の円の平均で割る(前の 2 輪だけダート)
        var mixed = new Synthetic(load: true);
        mixed.Surface["fl"][1000] = 2; mixed.Surface["fr"][1000] = 2;
        // 円の無い路面(雪)の車輪は数えない
        mixed.Surface["rl"][1001] = 1; mixed.Surface["rr"][1001] = 1;
        var fm = mixed.Build();
        double Mag(int i) => Math.Sqrt(Math.Pow(fm.Gg.X[i]!.Value, 2) + Math.Pow(fm.Gg.Y[i]!.Value, 2));
        Assert.Equal(100 * Mag(1000) / ((R(fm.Gg, 0) + R(fm.Gg, 2)) / 2), fm.Grip[1000]!.Value, 0.1);
        Assert.Equal(100 * Mag(1001) / R(fm.Gg, 0), fm.Grip[1001]!.Value, 0.1);
    }

    [Fact]
    public void 選んだレースの行で円と基準と点群を作る()
    {
        var d = new Synthetic(load: true);
        var dirtOnly = Enumerable.Range(DirtFrom, N - DirtFrom).ToList();
        var f = d.Build(race: 0, races: dirtOnly);
        Assert.Equal([2], f.Gg.Rings.Select(r => r.Group));
        Assert.Equal(dirtOnly, f.Gg.Cloud);
        Assert.Null(f.BaseWheels);                   // ダートの区間にはまっすぐの行が無い(基準 0)
        Assert.Equal(N, f.Gg.X.Length);              // 点は記録の全部の行
        // レースの番号が範囲の外なら記録全体
        Assert.Equal(2, d.Build(race: 5, races: dirtOnly).Gg.Rings.Length);
    }

    [Fact]
    public void ならしの幅が読めなければ既定の幅()
    {
        Assert.Equal(FrictionDynamic.DefaultSmoothSec, FrictionDynamic.SmoothSec(null));
        Assert.Equal(FrictionDynamic.DefaultSmoothSec, FrictionDynamic.SmoothSec(0));
        Assert.Equal(FrictionDynamic.DefaultSmoothSec, FrictionDynamic.SmoothSec(double.NaN));
        Assert.Equal(0.75, FrictionDynamic.SmoothSec(0.75));
    }
}
