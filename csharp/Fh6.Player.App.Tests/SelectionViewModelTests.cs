namespace Fh6.Player.App.Tests;

/// <summary>選んでいるレースと、それで決まる摩擦円・荷重移動(SelectionViewModel)。系列と印は小さな合成のもの</summary>
public sealed class SelectionViewModelTests
{
    private const int N = 40;
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings = new();
    private readonly SelectionViewModel _vm;

    public SelectionViewModelTests() => _vm = new SelectionViewModel(_shell, _settings);

    /// <summary>40 行の系列と、10 行ずつの 3 レースの印</summary>
    private static (SessionSeries Series, SessionMarks Marks) Data()
    {
        double?[] Fill(double v) => Enumerable.Repeat<double?>(v, N).ToArray();
        int?[] Kind() => Enumerable.Repeat<int?>(0, N).ToArray();
        var series = new SessionSeries(
            T: Enumerable.Range(0, N).Select(i => i / 30.0).ToArray(), Speed: Fill(100), Accel: null!, Brake: null!, Steer: null!, Gear: null!, SlipF: null!, SlipR: null!,
            Balance: null!, Counter: null!, Grade: null!, Elev: null!, X: null!, Z: null!, Turn: null!, GLong: Fill(0.5), GLat: Fill(0.2),
            Susp: new SuspensionSeries(Fill(0.3), Fill(0.3), Fill(0.4), Fill(0.4)), Rpm: null!, OptRpm: null!, OptRpmHi: null!, Gain: null!, Full: null!, PerfCar: null!,
            Perf: null!, Readout: null!, Surface: new SurfaceSeries(Kind(), Kind(), Kind(), Kind()), Course: null!,
            Friction: new FrictionSeries(new int[N], null!));
        var races = Enumerable.Range(0, 3).Select(k => new RaceMark(
            T0: k, T1: k + 0.3, Tf: k + 0.3, Restart: false, Finished: true, FinishedBy: "", EndKmh: 0, Course: null, Car: null, Setup: null,
            Dist: new RaceDistanceSeries(Enumerable.Range(k * 10, 10).ToList(), [], [], []), ElevRange: null, Auto: null, AutoLo: null, AutoHi: null)).ToArray();
        var marks = new SessionMarks(Starts: [], Contacts: [], Excursions: [], Races: races, Skips: [], Laps: [], Shifts: [], Jumps: [], ElevRange: null, Cut: [], GgExcluded: []);
        return (series, marks);
    }

    [Theory]
    [InlineData("all", "time", null)]
    [InlineData("race", "time", 0)]
    [InlineData("all", "dist", 0)]   // 距離は 1 つのレースについて
    public void 新しく開いたときは設定の記録を開いたときの表示で選ぶ(string view, string axis, int? expected)
    {
        _settings.View = new ViewSettings { View = view, Axis = axis }.WithDefaults();
        var (s, m) = Data();
        Assert.Equal(expected, _vm.Shown(s, m, keep: false));
        Assert.Equal(expected, _vm.Race);
    }

    [Fact]
    public void 同じ記録を送り直すときは選択を残し_無くなったレースなら全体()
    {
        var (s, m) = Data();
        _vm.Shown(s, m, keep: false);
        _vm.Handle(new ViewSelectRace(2, "pick"));
        Assert.Equal(2, _vm.Shown(s, m, keep: true));
        Assert.Null(_vm.Shown(s, m with { Races = m.Races[..1] }, keep: true));
        Assert.Null(_vm.Shown(null, null, keep: true));   // グラフを出せない
    }

    [Fact]
    public void 選んだら答えを返してから_そのレースの摩擦円を送る()
    {
        var (s, m) = Data();
        _vm.Shown(s, m, keep: false);
        Assert.Same(Task.CompletedTask, _vm.Handle(new ViewSelectRace(1, "follow")));
        Assert.Equal(new HostRaceSelected(1, SelectionViewModel.CauseFollow), _shell.Posted[0]);
        var f = Assert.IsType<HostFriction>(_shell.Posted[1]);
        Assert.Equal(1, f.Race);
        Assert.Equal(Enumerable.Range(10, 10), f.Gg.Cloud);   // そのレースの行の点群
        Assert.Equal(FrictionDynamic.DefaultSmoothSec, f.Sec);
    }

    [Fact]
    public void 範囲の外の番号は全体_知らない理由は選択欄で選んだことにする()
    {
        var (s, m) = Data();
        _vm.Shown(s, m, keep: false);
        _vm.Handle(new ViewSelectRace(5, "?"));
        Assert.Equal(new HostRaceSelected(null, SelectionViewModel.CausePick), _shell.Posted[0]);
        Assert.Null(_vm.Race);
    }

    [Fact]
    public void 表示幅で選択を外したときは摩擦円を作り直さない()
    {
        var (s, m) = Data();
        _vm.Shown(s, m, keep: false);
        _vm.Handle(new ViewSelectRace(0, "pick"));
        _shell.Posted.Clear();
        _vm.Handle(new ViewSelectRace(null, "leave"));
        Assert.Equal(new HostRaceSelected(null, SelectionViewModel.CauseLeave), Assert.Single(_shell.Posted));
    }

    [Fact]
    public void ならしの幅が変わったときだけ作り直す()
    {
        var (s, m) = Data();
        _vm.Shown(s, m, keep: false);
        _vm.SendFriction();
        _shell.Posted.Clear();
        _vm.SettingsSaved();   // 幅は同じ
        Assert.Empty(_shell.Posted);
        _settings.View = new ViewSettings { LtSmooth = 0.75 }.WithDefaults();
        _vm.SettingsSaved();
        Assert.Equal(0.75, Assert.IsType<HostFriction>(Assert.Single(_shell.Posted)).Sec);
    }

    [Fact]
    public void レースの選択でなければ扱わない() => Assert.Null(_vm.Handle(new ViewSessionList(false)));
}
