using Fh6.Core;

namespace Fh6.Core.Tests;

public class SurfaceExcursionDetectorTests
{
    // 路面の値: 舗装 0.00、ダート 0.12、芝 0.60
    private const double P = 0.00, D = 0.12, G = 0.60;

    private sealed class Run
    {
        public readonly List<double> T = new(), On = new(), Speed = new(), Slip = new();
        public readonly List<bool> Air = new();
        public readonly List<double>[] Surf = { new(), new(), new(), new() };
        public readonly List<double>[] Kerb = { new(), new(), new(), new() };
        private double _t;

        /// <summary>1 行(17ms 間隔 ≒ 60Hz)。surf は左前・右前・左後・右後</summary>
        public Run Add(double fl, double fr, double rl, double rr, double slip = 0.3, double kmh = 200,
                       bool on = true, bool air = false, bool kerbFr = false)
        {
            T.Add(_t); _t += 17;
            On.Add(on ? 1 : 0); Speed.Add(kmh / 3.6); Slip.Add(slip); Air.Add(air);
            double[] s = { fl, fr, rl, rr };
            for (int w = 0; w < 4; w++) { Surf[w].Add(s[w]); Kerb[w].Add(w == 1 && kerbFr ? 1 : 0); }
            return this;
        }

        public Run Repeat(int rows, double fl, double fr, double rl, double rr, double slip = 0.3, bool kerbFr = false)
        {
            for (int i = 0; i < rows; i++) Add(fl, fr, rl, rr, slip, kerbFr: kerbFr);
            return this;
        }

        public List<SurfaceExcursion> Detect() => SurfaceExcursionDetector.Detect(
            T.ToArray(), On.ToArray(), Speed.ToArray(), Air.ToArray(),
            Surf.Select(x => x.ToArray()).ToArray(), Kerb.Select(x => x.ToArray()).ToArray(), Slip.ToArray());
    }

    [Fact]
    public void 舗装から芝に落ちてスリップしたら結果を伴う逸脱()
    {
        // 174607 の 5:13 の形: 舗装 → 右前・右後が芝 → 約 1.2 秒後にスリップが 1.0 超え
        var r = new Run().Repeat(40, P, P, P, P).Repeat(20, P, G, P, G).Repeat(50, P, P, P, P).Repeat(30, P, P, P, P, slip: 1.39).Repeat(20, P, P, P, P);
        var e = Assert.Single(r.Detect());
        Assert.Equal(40, e.StartIndex);
        Assert.Equal(new[] { 1, 3 }, e.Wheels);
        Assert.Equal(new[] { SurfaceKind.Grass, SurfaceKind.Grass }, e.Surfaces);
        Assert.True(e.Consequential);
        Assert.Equal(110, e.SlipIndex);
        Assert.Equal(1.39, e.SlipPeak);
        Assert.False(e.EndsInGap);
    }

    [Fact]
    public void スリップを伴わなければ強調しない()
    {
        var r = new Run().Repeat(40, P, P, P, P).Repeat(20, D, P, P, P).Repeat(200, P, P, P, P);
        Assert.False(Assert.Single(r.Detect()).Consequential);
    }

    [Fact]
    public void 縁石は逸脱にしない()
    {
        var r = new Run().Repeat(40, P, P, P, P).Repeat(20, P, G, P, P, slip: 1.2, kerbFr: true).Repeat(40, P, P, P, P);
        Assert.Empty(r.Detect());
    }

    [Fact]
    public void 縁石の路面の値は逸脱にしない()
    {
        // FH6 では wheel_on_rumble_strip が立たず、縁石は surface_rumble 0.24 で出る(173311・211710)
        const double K = 0.24;
        var r = new Run().Repeat(40, P, P, P, P).Repeat(20, P, K, P, K, slip: 1.2).Repeat(40, P, P, P, P);
        Assert.Empty(r.Detect());
    }

    [Fact]
    public void ちらつきは逸脱にせず舗装の連続も途切れさせない()
    {
        // 110104 の 3:35.9 の形: 1 サンプルだけダート(ちらつき)→ 舗装 0.2 秒 → 雪・ダートに出入りしてスリップ → リワインド
        var r = new Run().Repeat(30, P, P, P, P).Add(D, P, P, P).Repeat(12, P, P, P, P);
        r.Repeat(3, P, P, P, P, slip: 1.7);   // スリップが路面の変化より少し先に上がる
        r.Repeat(10, D, 0.10, 0.10, D, slip: 1.1).Add(0, 0, 0, 0, slip: 0, kmh: 0, on: false);
        var e = Assert.Single(r.Detect());
        Assert.Equal(46, e.StartIndex);
        Assert.True(e.Consequential);
        Assert.True(e.SlipIndex < e.StartIndex);   // 0.3 秒前までのスリップを結びつける
        Assert.True(e.EndsInGap);
    }

    [Fact]
    public void 舗装が短ければ逸脱にしない()
    {
        // ダート路を走っていて、舗装が 0.2 秒だけ挟まる
        var r = new Run().Repeat(40, D, D, D, D).Repeat(12, P, P, P, P).Repeat(20, D, D, D, D, slip: 1.5);
        Assert.Empty(r.Detect());
    }

    [Fact]
    public void 続けて起きた逸脱は一件にまとめる()
    {
        var r = new Run().Repeat(40, P, P, P, P).Repeat(10, P, G, P, P).Repeat(10, P, P, P, P).Repeat(10, P, P, P, D).Repeat(40, P, P, P, P);
        var e = Assert.Single(r.Detect());
        Assert.Equal(new[] { 1, 3 }, e.Wheels);
    }

    [Fact]
    public void 低速と空中は対象外()
    {
        var slow = new Run();
        for (int i = 0; i < 40; i++) slow.Add(P, P, P, P, kmh: 20);
        for (int i = 0; i < 20; i++) slow.Add(G, G, G, G, kmh: 20);
        Assert.Empty(slow.Detect());

        var air = new Run().Repeat(40, P, P, P, P);
        for (int i = 0; i < 20; i++) air.Add(G, G, G, G, air: true);
        Assert.Empty(air.Detect());
    }
}

public class StartRespawnTests
{
    [Fact]
    public void チェックポイント逃しの復帰を見分ける()
    {
        // レース中に走行 → 1 サンプルで 100m 飛んで止まる(停止区間なし)→ 1.3 秒後に動き出す
        var ts = new List<double>(); var on = new List<double>(); var sp = new List<double>(); var pos = new List<double>();
        var x = new List<double>(); var z = new List<double>();
        double t = 0, px = 0;
        void Row(double kmh, double jump = 0) { ts.Add(t); t += 17; on.Add(1); sp.Add(kmh / 3.6); pos.Add(12); px += kmh / 3.6 * 0.017 + jump; x.Add(px); z.Add(0); }
        for (int i = 0; i < 30; i++) Row(80);
        Row(0, jump: -100);
        for (int i = 0; i < 76; i++) Row(0);
        for (int i = 0; i < 20; i++) Row(3 + i * 2);

        var starts = StartDetector.Detect(ts.ToArray(), on.ToArray(), sp.ToArray(), pos.ToArray(), x.ToArray(), z.ToArray());
        var s = Assert.Single(starts);
        Assert.Equal(StartKind.Respawn, s.Kind);
        Assert.True(s.IsRace);

        // 座標を渡さなければ、これまでどおりレースのスタート
        Assert.Equal(StartKind.Race, Assert.Single(StartDetector.Detect(ts.ToArray(), on.ToArray(), sp.ToArray(), pos.ToArray())).Kind);
    }

    [Fact]
    public void 順位が0でも距離が増えていくイベントはレースのスタート()
    {
        // タイムアタック: race_pos は 0 のまま、動き出すと dist_traveled が増える。フリーロームは 0 のまま
        var ts = new List<double>(); var on = new List<double>(); var sp = new List<double>(); var pos = new List<double>(); var dist = new List<double>();
        double t = 0, d = 0;
        void Row(double kmh, bool eventRunning) { ts.Add(t); t += 17; on.Add(1); sp.Add(kmh / 3.6); pos.Add(0); if (eventRunning) d += kmh / 3.6 * 0.017; dist.Add(d); }
        for (int i = 0; i < 80; i++) Row(0, false);
        for (int i = 0; i < 60; i++) Row(3 + i, true);
        var ta = Assert.Single(StartDetector.Detect(ts.ToArray(), on.ToArray(), sp.ToArray(), pos.ToArray(), distTraveled: dist.ToArray()));
        Assert.Equal(StartKind.Race, ta.Kind);

        var free = Assert.Single(StartDetector.Detect(ts.ToArray(), on.ToArray(), sp.ToArray(), pos.ToArray(), distTraveled: new double[ts.Count]));
        Assert.Equal(StartKind.Launch, free.Kind);
    }

    [Fact]
    public void フリーロームのファストトラベルは復帰にしない()
    {
        var ts = new List<double>(); var on = new List<double>(); var sp = new List<double>(); var pos = new List<double>();
        var x = new List<double>(); var z = new List<double>();
        double t = 0, px = 0;
        void Row(double kmh, double jump = 0) { ts.Add(t); t += 17; on.Add(1); sp.Add(kmh / 3.6); pos.Add(0); px += jump; x.Add(px); z.Add(0); }
        for (int i = 0; i < 30; i++) Row(60);
        Row(0, jump: 3000);
        for (int i = 0; i < 80; i++) Row(0);
        for (int i = 0; i < 20; i++) Row(3 + i * 2);

        var s = Assert.Single(StartDetector.Detect(ts.ToArray(), on.ToArray(), sp.ToArray(), pos.ToArray(), x.ToArray(), z.ToArray()));
        Assert.Equal(StartKind.Launch, s.Kind);
    }
}
