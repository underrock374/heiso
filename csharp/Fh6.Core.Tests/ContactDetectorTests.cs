using Fh6.Core;

namespace Fh6.Core.Tests;

public class ContactDetectorTests
{
    private sealed class Run
    {
        public List<double> Ts = new(), On = new(), Speed = new(), Ax = new(), Az = new();
        public List<bool> Air = new();
        private double _t;

        /// <summary>1 行足す。kmh は速度、ax / az は水平方向の加速度(m/s²)</summary>
        public Run Add(double kmh, double ax = 0, double az = 0, bool air = false, double gapMs = 33, bool on = true)
        {
            _t += Ts.Count == 0 ? 0 : gapMs;
            Ts.Add(_t); On.Add(on ? 1 : 0); Speed.Add(kmh / 3.6); Ax.Add(ax); Az.Add(az); Air.Add(air);
            return this;
        }

        public Run Cruise(double kmh, int rows, double ax = 0, double az = 0)
        {
            for (int i = 0; i < rows; i++) Add(kmh, ax, az);
            return this;
        }

        public List<ContactEvent> Detect() =>
            ContactDetector.Detect(Ts.ToArray(), On.ToArray(), Speed.ToArray(), Ax.ToArray(), Az.ToArray(), Air.ToArray());
    }

    [Fact]
    public void 水平加速度の跳ね上がりを拾う()
    {
        // 壁への激突の実例に近い: 横 −124・前後 −72 m/s² が 1 サンプルで出て、速度が 203 → 163
        var r = new Run().Cruise(203, 30, ax: -15).Add(176, ax: -124, az: -72).Add(172, ax: -95, az: -52).Cruise(163, 20, ax: -15);
        var c = Assert.Single(r.Detect());
        Assert.Equal(ContactKind.Contact, c.Kind);
        Assert.Equal(30, c.Index);
        Assert.Equal(143.4, c.PeakAccel, 1);
        Assert.Equal(203, c.SpeedBeforeKmh);
        Assert.Equal(163, c.SpeedAfterKmh);
        Assert.True(c.PeakG > 14);
    }

    [Fact]
    public void ブレーキ中の追突も拾う()
    {
        // 132511 の 1:07: ブレーキ 78% で減速中に前の車へ。前後 −194 m/s²(上限に張り付き)、200 → 122 km/h
        var r = new Run();
        for (int i = 0; i < 20; i++) r.Add(210 - i, az: -12);
        r.Add(122, az: -194).Add(122, az: -109).Cruise(110, 20, az: -18);
        var c = Assert.Single(r.Detect());
        Assert.Equal(191, c.SpeedBeforeKmh);
        Assert.True(c.DropKmh > 60);
    }

    [Fact]
    public void 横から押されて速度が上がる接触も拾う()
    {
        // 110104 の 7:55: 横 +55 m/s²、速度は 162 → 168 と上がる
        var r = new Run().Cruise(162, 30, ax: -13).Add(168, ax: 55.6, az: -11).Add(169, ax: 39).Cruise(168, 20);
        var c = Assert.Single(r.Detect());
        Assert.Equal(ContactKind.Contact, c.Kind);
        Assert.True(c.DropKmh < 0);
    }

    [Fact]
    public void 高い加速度が続くだけでは拾わない()
    {
        // 高速コーナーや強いブレーキで 2G 前後が続く。跳ね上がりではない
        var r = new Run();
        for (int i = 0; i < 60; i++) r.Add(200 - i, ax: 20 + i * 0.4, az: -18);
        Assert.Empty(r.Detect());
    }

    [Fact]
    public void 続けて起きたものは一件にまとめて最大の加速度を残す()
    {
        var r = new Run().Cruise(150, 30).Add(140, ax: 60).Cruise(140, 5).Add(130, ax: 110).Cruise(130, 30);
        var c = Assert.Single(r.Detect());
        Assert.Equal(110, c.PeakAccel);
    }

    [Fact]
    public void 離れた二回は別々()
    {
        var r = new Run().Cruise(150, 30).Add(140, ax: 60).Cruise(140, 30).Add(130, az: -80).Cruise(130, 30);
        Assert.Equal(2, r.Detect().Count);
    }

    [Fact]
    public void 空中の直後は着地()
    {
        var r = new Run().Cruise(200, 30);
        for (int i = 0; i < 20; i++) r.Add(200, air: true);
        r.Add(190, az: -96).Cruise(190, 20);
        Assert.Equal(ContactKind.Landing, Assert.Single(r.Detect()).Kind);
    }

    [Fact]
    public void 止まったままの跳ね上がりは拾わない()
    {
        // チェックポイント逃しの復帰直後: 速度 0 のまま 196 → 146 → 111 m/s² と減っていく(173311 の 3:10)
        var r = new Run().Cruise(0, 30, az: 8).Add(0, az: 196).Add(0, az: 146).Add(1, az: 111).Cruise(2, 10);
        Assert.Empty(r.Detect());
    }

    [Fact]
    public void 停止区間と間隔が空いた行はまたがない()
    {
        var r = new Run().Cruise(150, 30).Add(0, on: false).Add(150, ax: 80).Cruise(150, 10);
        Assert.Empty(r.Detect());

        var g = new Run().Cruise(150, 30).Add(150, ax: 80, gapMs: 400).Cruise(150, 10);
        Assert.Empty(g.Detect());
    }
}
