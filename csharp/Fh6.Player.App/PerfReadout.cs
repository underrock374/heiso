namespace Fh6.Player;

/// <summary>
/// 性能モードの読み取り値とパワーカーブの今の点(前は画面の JS の showPerfReadout の判定と、powercurve.js の pwAt・info)。
/// 結果は BuildSeries で系列に入れて画面へ送る。文言と描き方は画面に残す
/// </summary>
public static class PerfReadout
{
    /// <summary>変速の得失(%)がこの幅の中なら「適正」。外なら早い(負)・遅い(正)</summary>
    public const double OkPct = 3;

    /// <summary>カーブの区切り(250 rpm ごと)の半分。両端の区切りから、これより外の回転数は出力を出さない</summary>
    public const double HalfBinRpm = 125;

    /// <summary>行ごとの状態(State)。得失があれば判定、無ければ出せない理由</summary>
    public static class States
    {
        public const string Early = "early", Ok = "ok", Late = "late";
        public const string NotFull = "notFull";            // 全開でない
        public const string NoCurve = "noCurve";            // パワーカーブが無い
        public const string NextFewRows = "nextFewRows";    // 次のギアは使ったが、全開の行が足りず比が分からない
        public const string NextUnused = "nextUnused";      // このギアより上を使っていない
        public const string RpmOutside = "rpmOutside";      // 今の回転数がカーブの外
        public const string AfterOutside = "afterOutside";  // 変速した後の回転数がカーブの外
    }

    /// <summary>カーブ [[rpm, hp], …] の rpm での出力(区切りの間は直線で。範囲の外は null)</summary>
    public static double? HpAt(double[][]? curve, double? rpm)
    {
        if (curve is not { Length: > 0 } || rpm is not { } r) return null;
        if (r < curve[0][0] - HalfBinRpm || r > curve[^1][0] + HalfBinRpm) return null;
        if (r <= curve[0][0]) return curve[0][1];
        for (int k = 1; k < curve.Length; k++)
        {
            if (r <= curve[k][0])
            {
                double r0 = curve[k - 1][0], p0 = curve[k - 1][1], r1 = curve[k][0], p1 = curve[k][1];
                return p0 + (p1 - p0) * (r - r0) / (r1 - r0);
            }
        }
        return curve[^1][1];
    }

    /// <summary>得失(%)の判定</summary>
    public static string Verdict(double gain) => gain < -OkPct ? States.Early : gain > OkPct ? States.Late : States.Ok;

    /// <summary>系列の値から、行ごとの読み取り値を作る(画面が前に系列から出していたのと同じ値を使う)</summary>
    public static PerfReadoutSeries Build(SessionSeries s) => Build(s.Rpm, s.Gear, s.Full, s.Gain, s.PerfCar, s.Perf.Cars);

    /// <summary>系列の回転数・ギア・全開か・得失・行の車と、車ごとの性能から、行ごとの読み取り値を作る</summary>
    public static PerfReadoutSeries Build(
        IReadOnlyList<double?> rpms, IReadOnlyList<double?> gears, IReadOnlyList<int?> full, IReadOnlyList<double?> gains, IReadOnlyList<int?> perfCar, PerfCarInfo[] cars)
    {
        int n = rpms.Count;
        var hp = new double?[n];
        var rpmAfter = new double?[n];
        var hpAfter = new double?[n];
        var state = new string?[n];
        var fallback = cars.FirstOrDefault(c => c.Curve != null);
        for (int i = 0; i < n; i++)
        {
            // 行の車(perf.cars の位置)。無ければ、カーブのある最初の車
            var car = perfCar[i] is { } k && k < cars.Length ? cars[k] : fallback;
            double? rpm = rpms[i], gear = gears[i];
            double? ratio = car != null && gear is { } g && car.Ratios.TryGetValue(((int)g).ToString(), out var x) && x != 0 ? x : null;
            if (car?.Curve != null) hp[i] = HpAt(car.Curve, rpm);
            // 変速した後の点は全開の行だけ(パワーカーブに描くのと、「変速した後の … rpm がカーブの外」に使う。どちらも全開のときだけ)
            if (ratio != null && rpm != null && full[i] == 1)
            {
                rpmAfter[i] = rpm * ratio;
                if (car?.Curve != null) hpAfter[i] = HpAt(car.Curve, rpmAfter[i]);
            }
            state[i] = gains[i] is { } gain ? Verdict(gain)
                : gear == null || full[i] == null ? null
                : full[i] == 0 ? States.NotFull
                : car?.Curve == null ? States.NoCurve
                : ratio == null ? (car.Gears.Contains((int)gear.Value + 1) ? States.NextFewRows : States.NextUnused)
                : hp[i] == null ? States.RpmOutside
                : States.AfterOutside;
        }
        return new PerfReadoutSeries(Hp: hp, RpmAfter: rpmAfter, HpAfter: hpAfter, State: state);
    }
}
