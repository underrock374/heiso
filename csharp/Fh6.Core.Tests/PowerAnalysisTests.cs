using Fh6.Core;

namespace Fh6.Core.Tests;

public class PowerAnalysisTests
{
    /// <summary>試しのエンジン: 6,500 rpm で 500 hp の山なり(3,000〜8,000 rpm)</summary>
    private static double Hp(double rpm) => 500 - Math.Pow((rpm - 6500) / 2000, 2) * 150;

    /// <summary>回転数ごとに全開の行を並べる(各回転数を 25 行ずつ。出力は W)</summary>
    private static (int[] Rows, double[] Rpm, double[] Power, double[] Accel, double[] Gear) Sweep(double from = 3000, double to = 8000, double accel = 255, int gear = 3)
    {
        var rpm = new List<double>();
        for (double r = from; r < to; r += 10)
            for (int k = 0; k < 3; k++) rpm.Add(r + k);
        int n = rpm.Count;
        return (Enumerable.Range(0, n).ToArray(), rpm.ToArray(), rpm.Select(r => Hp(r) * 745.7).ToArray(),
                Enumerable.Repeat(accel, n).ToArray(), Enumerable.Repeat((double)gear, n).ToArray());
    }

    private static double[] Ts(int n) => Enumerable.Range(0, n).Select(i => i * 33.0).ToArray();

    [Fact]
    public void 全開の行から回転数ごとの出力の75パーセンタイルでカーブを作る()
    {
        var (rows, rpm, power, accel, gear) = Sweep();
        var c = PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear);
        Assert.NotNull(c);
        Assert.Equal(3125, c.Rpm[0]);
        Assert.Equal(7875, c.Rpm[^1]);
        Assert.Equal(500, c.MaxHp, 0);
        Assert.InRange(c.PeakRpm, 6375, 6625);
        Assert.InRange(c.At(5000)!.Value, Hp(5000), Hp(5000) + 10);   // 区切りの中の 75 パーセンタイル(上り坂なら少し上)。区切りの間は直線で補う
        Assert.Null(c.At(2000));
        Assert.Null(c.At(8500));
    }

    [Fact]
    public void 部分開度と負の出力とニュートラルは使わない()
    {
        var (rows, rpm, power, accel, gear) = Sweep(accel: 200);
        Assert.Null(PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear));
        (rows, rpm, power, accel, gear) = Sweep(gear: 11);
        Assert.Null(PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear));
        (rows, rpm, power, accel, gear) = Sweep();
        for (int i = 0; i < power.Length; i++) if (rpm[i] >= 7000) power[i] = -1000;   // リミッターカット
        var c = PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear)!;
        Assert.Equal(6875, c.Rpm[^1]);
    }

    [Fact]
    public void 変速の後1秒の行はカーブに使わない()
    {
        // 4,750〜5,250 rpm の行は全部、変速の直後で過給が戻らず出力が 3 割低い。変速の後 1 秒を除けば、その区切りは使われず、
        // 間は前後の区切りから補うので、低い値にならない(除かなければ 5,000 rpm は 3 割低く出る)
        var (rows, rpm, power, accel, gear) = Sweep();
        var ts = Ts(rows.Length);
        int first = Array.FindIndex(rpm, r => r >= 4750), last = Array.FindLastIndex(rpm, r => r < 5250);
        for (int i = first; i <= last; i++) power[i] *= 0.7;
        gear[first - 1] = 11;   // 変速中 → 3 速に戻る(first でギアが変わる)
        for (int i = first; i <= last; i++) ts[i] = ts[first - 1] + 33 + (i - first) * 3.0;   // 変わってから 0.5 秒の間に全部ある
        for (int i = last + 1; i < ts.Length; i++) ts[i] = ts[last] + 1000 + (i - last) * 33.0;
        var c = PowerAnalysis.Curve(rows, ts, rpm, power, accel, gear)!;
        Assert.DoesNotContain(4875.0, c.Rpm);
        Assert.DoesNotContain(5125.0, c.Rpm);
        Assert.InRange(c.At(5000)!.Value, Hp(5000) * 0.95, Hp(5000) + 10);
    }

    [Fact]
    public void 最適な変速回転数は変速の前後で出力が等しくなる所()
    {
        var (rows, rpm, power, accel, gear) = Sweep();
        var c = PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear)!;
        // (r − 6500)² = (0.75r − 6500)² → r = 13000 / 1.75 ≈ 7,429
        Assert.InRange(PowerAnalysis.OptimalShiftRpm(c, 0.75)!.Value, 7375, 7475);
        // 変速すると回転が落ちすぎるなら、カーブの中には無い(リミッターまで)
        Assert.Null(PowerAnalysis.OptimalShiftRpm(c, 0.4));
    }

    [Fact]
    public void 変速の得失は早いと負で遅いと正()
    {
        var (rows, rpm, power, accel, gear) = Sweep();
        var c = PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear)!;
        var early = PowerAnalysis.Gain(c, 6500, 0.75)!.Value;   // 4,875 rpm に落ちる
        var late = PowerAnalysis.Gain(c, 7900, 0.75)!.Value;
        Assert.True(early < -3, $"{early}");
        Assert.True(late > 3, $"{late}");
        Assert.Equal(ShiftVerdict.Early, PowerAnalysis.Judge(early));
        Assert.Equal(ShiftVerdict.Late, PowerAnalysis.Judge(late));
        Assert.Equal(ShiftVerdict.Ok, PowerAnalysis.Judge(PowerAnalysis.Gain(c, 7429, 0.75)!.Value));
    }

    [Fact]
    public void ギア比は回転数と速度の比の中央値から()
    {
        // 3 速: 200 rpm/(m/s)、4 速: 150。4 速の行の一部はホイールスピン(スリップ率 0.8)で比がずれている
        var rpm = new List<double>(); var speed = new List<double>(); var gear = new List<double>(); var slip = new List<double>();
        for (int i = 0; i < 40; i++) { speed.Add(20 + i * 0.5); rpm.Add((20 + i * 0.5) * 200); gear.Add(3); slip.Add(0.05); }
        for (int i = 0; i < 40; i++) { speed.Add(30 + i * 0.5); rpm.Add((30 + i * 0.5) * 150); gear.Add(4); slip.Add(0.05); }
        for (int i = 0; i < 20; i++) { speed.Add(30); rpm.Add(30 * 180); gear.Add(4); slip.Add(0.8); }
        int n = rpm.Count;
        var s = slip.ToArray();
        var ratios = PowerAnalysis.GearRatios(Enumerable.Range(0, n), rpm.ToArray(), speed.ToArray(), Enumerable.Repeat(255.0, n).ToArray(), gear.ToArray(),
            new[] { s, s, s, s });
        Assert.Equal(0.75, ratios[3], 3);
        Assert.False(ratios.ContainsKey(4));   // 5 速の行が無い
    }

    [Fact]
    public void 全開のシフトアップだけを拾う()
    {
        // 2 速 → 変速中 → 3 速(全開)、3 速 → 4 速(部分開度)、4 速 → 3 速(シフトダウン)、3 速 → 変速中が長い → 4 速
        double[] gear =  { 2, 2, 11, 11, 3, 3, 3, 4, 4, 3, 3, 11, 11, 11, 4 };
        double[] accel = { 255, 255, 0, 0, 255, 255, 150, 150, 255, 255, 255, 0, 0, 0, 255 };
        double[] rpm =   { 7000, 7200, 6000, 5500, 5400, 6000, 6800, 5200, 5300, 6500, 7100, 6000, 5000, 4000, 5000 };
        double[] ts = Enumerable.Range(0, gear.Length).Select(i => i * 33.0).ToArray();
        ts[12] = ts[11] + 600; ts[13] = ts[12] + 600; ts[14] = ts[13] + 33;
        var shifts = PowerAnalysis.Upshifts(Enumerable.Range(0, gear.Length).ToList(), ts, gear, rpm, accel, Enumerable.Repeat(100.0, gear.Length).ToArray(), (i, g, r) => g == 2 ? 5.0 : null);
        var s = Assert.Single(shifts);
        Assert.Equal(1, s.Index);
        Assert.Equal(2, s.FromGear);
        Assert.Equal(7200, s.Rpm);
        Assert.Equal(ShiftVerdict.Late, s.Verdict);
    }

    [Fact]
    public void 変速した回転数はギアが変わる直前の最高()
    {
        // ギアが変わる前に回転が落ち始める(7,300 → 7,100 → 6,900 → 変速中)。0.3 秒より前の行(7,500)は見ない
        double[] gear =  { 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 11, 4 };
        double[] accel = Enumerable.Repeat(255.0, gear.Length).ToArray();
        double[] rpm =   { 7500, 6000, 6100, 6200, 6300, 6400, 6500, 6600, 7000, 7300, 7100, 6900, 6000, 5500 };
        double[] ts = Enumerable.Range(0, gear.Length).Select(i => i * 33.0).ToArray();
        var s = Assert.Single(PowerAnalysis.Upshifts(Enumerable.Range(0, gear.Length).ToList(), ts, gear, rpm, accel, Enumerable.Repeat(100.0, gear.Length).ToArray(), (i, g, r) => null));
        Assert.Equal(9, s.Index);
        Assert.Equal(7300, s.Rpm);
        Assert.Null(s.Verdict);
    }

    [Fact]
    public void 変速回転数がギアによらずそろえば自動変速()
    {
        // 実データの形: AT はどのギアも 7,050〜7,130 rpm、手動は 6,900〜7,750 rpm に散らばる
        static List<ShiftEvent> Make(params (int Gear, double Rpm)[] s) => s.Select((x, k) => new ShiftEvent(k, x.Gear, x.Rpm, null)).ToList();
        var at = Make((2, 7079), (2, 7101), (3, 7090), (3, 7131), (3, 7048), (4, 7101), (4, 7050), (4, 7128), (5, 7102));
        var mt = Make((2, 7662), (2, 7506), (2, 7740), (3, 7575), (3, 6922), (3, 7701), (4, 7400), (4, 7206), (4, 6894));
        var a = PowerAnalysis.LooksAutomatic(at)!.Value;
        Assert.True(a.Auto);
        Assert.InRange(a.Lo, 7048, 7079);
        Assert.InRange(a.Hi, 7102, 7131);
        Assert.False(PowerAnalysis.LooksAutomatic(mt)!.Value.Auto);
        Assert.Null(PowerAnalysis.LooksAutomatic(at.Take(7).ToList()));                         // 少なすぎる
        Assert.Null(PowerAnalysis.LooksAutomatic(Make(Enumerable.Repeat((3, 7100.0), 10).ToArray())));   // 1 つのギアだけ
    }

    [Fact]
    public void リミッターの手前で変速したら早いにしない()
    {
        // 変速すると回転が落ちすぎて、どこで変速しても損になる車(最適な変速回転数はカーブの中に無い = リミッターまで)
        var (rows, rpm, power, accel, gear) = Sweep();
        var c = PowerAnalysis.Curve(rows, Ts(rows.Length), rpm, power, accel, gear)!;
        Assert.Null(PowerAnalysis.OptimalShiftRpm(c, 0.6));
        Assert.True(PowerAnalysis.Gain(c, 7900, 0.6)!.Value < -3);                 // リミッターが分からなければ「早い」
        Assert.Equal(0, PowerAnalysis.Gain(c, 7900, 0.6, limiterRpm: 8000));       // リミッターの 250 rpm 手前より上は 0(適正)
        Assert.True(PowerAnalysis.Gain(c, 7500, 0.6, limiterRpm: 8000)!.Value < -3); // まだ手前なら早い
        var range = PowerAnalysis.OptimalRange(c, 0.6, 8000)!.Value;
        Assert.Equal((7750, 8000), range);
    }

    [Fact]
    public void 出力が平らなら最適な範囲が広い()
    {
        // 電気自動車の形: 6,000 rpm までは出力が回転に比例し、そこから 15,000 rpm まで平ら、その上で下がる
        static double Ev(double r) => r < 6000 ? 637 * r / 6000 : r <= 15000 ? 637 : 637 - (r - 15000) * 0.1;
        var rpm = new List<double>();
        for (double r = 2000; r < 16500; r += 10) for (int k = 0; k < 3; k++) rpm.Add(r + k);
        int n = rpm.Count;
        var c = PowerAnalysis.Curve(Enumerable.Range(0, n).ToList(), Ts(n), rpm.ToArray(), rpm.Select(r => Ev(r) * 745.7).ToArray(),
            Enumerable.Repeat(255.0, n).ToArray(), Enumerable.Repeat(1.0, n).ToArray())!;
        var range = PowerAnalysis.OptimalRange(c, 0.52, 16500)!.Value;
        Assert.InRange(range.Lo, 11000, 11700);   // 変速した後が 6,000 rpm 付近(平らな所の入口)に入る所から
        Assert.InRange(range.Hi, 15200, 15500);   // 出力が 3% 落ちる所まで
    }

    [Fact]
    public void リミッターは全開の回転数の上の方()
    {
        var rpm = Enumerable.Range(0, 1000).Select(i => 3000.0 + i * 4.4).ToArray();   // 3,000〜7,396 rpm
        var accel = Enumerable.Repeat(255.0, rpm.Length).ToArray();
        var gear = Enumerable.Repeat(3.0, rpm.Length).ToArray();
        Assert.InRange(PowerAnalysis.LimiterRpm(Enumerable.Range(0, rpm.Length), rpm, accel, gear)!.Value, 7370, 7400);
        Assert.Null(PowerAnalysis.LimiterRpm(Enumerable.Range(0, 100), rpm, accel, gear));   // 行が少ない
    }

    [Fact]
    public void リミッターに当てて変速する手動は自動変速としない()
    {
        // どのギアも 8,800 rpm で上げる。AT なら当たらずに上がり、手動でリミッターまで引っ張れば直前に出力が 0 以下になる
        static List<ShiftEvent> Make(bool hit) => Enumerable.Range(0, 9).Select(k => new ShiftEvent(k, 2 + k % 3, 8790 + k * 5, 0, hit)).ToList();
        Assert.True(PowerAnalysis.LooksAutomatic(Make(false))!.Value.Auto);
        Assert.False(PowerAnalysis.LooksAutomatic(Make(true))!.Value.Auto);

        // Upshifts が当たったかを見る: 3 速で、変速の 0.2 秒前に全開で出力が負の行
        double[] gear =  { 3, 3, 3, 3, 3, 11, 4 };
        double[] accel = { 255, 255, 255, 255, 255, 0, 255 };
        double[] rpm =   { 8600, 8700, 8800, 8810, 8790, 7000, 6500 };
        double[] power = { 1e5, 1e5, -1e4, 1e5, 1e5, 0, 1e5 };
        var s = Assert.Single(PowerAnalysis.Upshifts(Enumerable.Range(0, gear.Length).ToList(), Ts(gear.Length), gear, rpm, accel, power, (i, g, r) => null));
        Assert.True(s.HitLimiter);
        power[2] = 1e5;
        Assert.False(Assert.Single(PowerAnalysis.Upshifts(Enumerable.Range(0, gear.Length).ToList(), Ts(gear.Length), gear, rpm, accel, power, (i, g, r) => null)).HitLimiter);
        // 回転が落ち始めた後(一番高い 8,810 rpm の後)の出力 0 は、変速そのもの(点火を切る)なので当たったとしない
        power[4] = -1e4;
        Assert.False(Assert.Single(PowerAnalysis.Upshifts(Enumerable.Range(0, gear.Length).ToList(), Ts(gear.Length), gear, rpm, accel, power, (i, g, r) => null)).HitLimiter);
    }

    [Fact]
    public void リミッターに張り付いている行と時間()
    {
        // 3 速でリミッター(8,700)に当たり、8,450〜8,700 を上下してから 4 速へ。出力は当たった瞬間だけ負
        double[] rpm =   { 8000, 8300, 8600, 8700, 8460, 8690, 8450, 8700, 8480, 8700, 7000, 6500 };
        double[] gear =  { 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 11, 4 };
        double[] power = { 1e5, 1e5, 1e5, -1e4, 1e5, -1e4, 1e5, -1e4, 1e5, -1e4, 0, 1e5 };
        double[] accel = Enumerable.Repeat(255.0, rpm.Length).ToArray();
        var rows = Enumerable.Range(0, rpm.Length).ToList();
        var ts = Ts(rpm.Length);
        var mask = PowerAnalysis.LimiterBounce(rows, rpm.Length, ts, gear, rpm, accel, power, i => 8700);
        Assert.True(mask.Skip(2).Take(8).All(x => x));        // リミッターの手前(8,600)から上げるまで(当たった後、8,450 rpm に下がった行も)
        Assert.False(mask[1]);                                 // 上がってくる途中(8,300)は、まだ回るので張り付きではない
        Assert.False(mask[11]);                                // 次のギアは別
        Assert.Equal(7 * 33, PowerAnalysis.TimeAtLimiterMs(rows, 9, ts, gear, mask));
        // リミッターが分からなければ、張り付きとしない
        Assert.DoesNotContain(true, PowerAnalysis.LimiterBounce(rows, rpm.Length, ts, gear, rpm, accel, power, i => null));
    }

    [Fact]
    public void 車種とPIの組ごとに分ける()
    {
        var (rows, rpm, power, accel, gear) = Sweep();
        int n = rows.Length;
        var speed = rpm.Select(r => r / 200).ToArray();
        var zero = new double[n];
        var p = PowerAnalysis.Profiles(rows, i => (100, i < n / 2 ? 800 : 850), Ts(n), rpm, power, accel, gear, speed, new[] { zero, zero, zero, zero });
        Assert.Equal(2, p.Count);
        Assert.Equal(n / 2, p[0].Rows.Count);
        Assert.Equal(800, p[0].Profile.Pi);
        Assert.Empty(p[0].Profile.Ratios);   // 3 速しか無い
        Assert.Null(p[0].Profile.Gain(3, 6000));
    }
}
