namespace Fh6.Core;

/// <summary>パワーカーブ: 回転数(区切りの真ん中、rpm)ごとの出力(hp)。回転数の昇順</summary>
public sealed record PowerCurve(double[] Rpm, double[] Hp)
{
    public double MaxHp => Hp.Max();
    public double PeakRpm => Rpm[Array.IndexOf(Hp, MaxHp)];

    /// <summary>回転数 rpm の出力(hp)。区切りの真ん中の間は直線で補う。カーブの範囲(両端の区切りの外側半分まで)の外は null</summary>
    public double? At(double rpm)
    {
        double half = PowerAnalysis.BinRpm / 2;
        if (!double.IsFinite(rpm) || rpm < Rpm[0] - half || rpm > Rpm[^1] + half) return null;
        if (rpm <= Rpm[0]) return Hp[0];
        if (rpm >= Rpm[^1]) return Hp[^1];
        int i = Array.FindIndex(Rpm, r => r >= rpm);
        double f = (rpm - Rpm[i - 1]) / (Rpm[i] - Rpm[i - 1]);
        return Hp[i - 1] + (Hp[i] - Hp[i - 1]) * f;
    }
}

public enum ShiftVerdict { Early, Ok, Late }

/// <summary>
/// 全開のシフトアップ。Index は変速を決めた行(ギアが変わる直前の、回転数が一番高い行)。GainPct は変速の得失(計算できなければ null)。
/// HitLimiter は、変速の直前にリミッターに当たっていたか(全開なのに出力が 0 以下の行があった)。LimiterMs は、変速の前にリミッターに張り付いていた時間(ms)
/// </summary>
public readonly record struct ShiftEvent(int Index, int FromGear, double Rpm, double? GainPct, bool HitLimiter = false, double LimiterMs = 0)
{
    public ShiftVerdict? Verdict => GainPct is double g ? PowerAnalysis.Judge(g) : null;
}

/// <summary>
/// 1 台(車種と PI の組)の性能: パワーカーブ、ギア比(次のギア ÷ 今のギア)、最適な変速回転数(null = リミッターまで引っ張る方が得)、
/// リミッターの回転数(分からなければ null)、ギアごとの最適な範囲(得失が ±3% 以内になる回転数の幅。無ければ入れない)
/// </summary>
public sealed record PowerProfile(int Ordinal, int Pi, PowerCurve? Curve, IReadOnlyDictionary<int, double> Ratios, IReadOnlyDictionary<int, double?> OptimalRpm,
    double? LimiterRpm = null, IReadOnlyDictionary<int, (double Lo, double Hi)>? OptimalRange = null)
{
    /// <summary>gear で rpm のとき、今すぐ次のギアへ変速した場合の得失(最大出力に対する %)。計算できなければ null。リミッターの手前は早くない(PowerAnalysis.Gain)</summary>
    public double? Gain(int gear, double rpm) =>
        Curve != null && Ratios.TryGetValue(gear, out var r) ? PowerAnalysis.Gain(Curve, rpm, r, LimiterRpm) : null;
}

/// <summary>
/// エンジンの出力とギアから、変速の良し悪しを計算する(性能モード。docs/spec.md「性能モード」、docs/telemetry-field-notes.md「エンジン出力とシフトポイント」)。
/// - パワーカーブ: 全開(アクセル 95% 以上)で出力が正の行(変速の後 1 秒を除く)を、回転数 250 rpm ごとに分けた出力の 75 パーセンタイル。20 行未満の区切りは使わない
/// - ギア比: ギアごとの「回転数 ÷ 速度」の中央値(全開、4 輪のスリップ率が小さい行)の、次のギアとの比。
///   変速の直前・直後の回転数の比は、変速中も回転が動くので使わない(実データで全部 0.9 前後になった)
/// - 最適な変速回転数: 変速の前後で出力が等しくなる回転数(ピークより上で、P(r) ≤ P(r × 比) になる最初の r)
/// - 変速の得失 = (変速した後の出力 − 今の出力) ÷ 最大出力 × 100。正 = もう変速した方が得(遅れている)、負 = まだ早い
/// </summary>
public static class PowerAnalysis
{
    /// <summary>全開とみなすアクセル(0〜255。95%)</summary>
    public const double FullThrottle = 242;
    public const double BinRpm = 250;
    public const int MinBinRows = 20;
    /// <summary>
    /// 区切りの中の出力のどの位置を使うか(75 パーセンタイル)。過給の立ち上がりが遅れた行(コーナーの立ち上がりなど)が混ざると、
    /// 中央値ではターボの効く前の低い値に引っ張られる(実データで、同じ車・同じ回転数の中央値が記録によって 60 hp 違った)
    /// </summary>
    public const double CurvePercentile = 0.75;
    /// <summary>ギアが変わってから、この時間(ms)の行はパワーカーブに使わない</summary>
    public const double AfterShiftMs = 1000;
    /// <summary>パワーカーブを作るのに要る区切りの数</summary>
    public const int MinBins = 6;
    /// <summary>ギア比を出すのに要る、そのギアの行の数</summary>
    public const int MinGearRows = 30;
    /// <summary>ギア比に使う行の、4 輪のスリップ率(tire_slip_ratio)の絶対値の上限</summary>
    public const double MaxSlipRatio = 0.3;
    /// <summary>ギア比に使う行の最低速度(m/s)</summary>
    public const double MinSpeed = 5;
    /// <summary>得失がこの範囲(±%)なら適正</summary>
    public const double OkPct = 3;
    /// <summary>変速中(gear == 11)がこれより長く続いたら、シフトアップとみなさない(ms)</summary>
    public const double MaxShiftMs = 1000;
    /// <summary>変速した回転数は、ギアが変わる前のこの時間(ms)の中の最高</summary>
    public const double PeakWindowMs = 300;
    /// <summary>リミッターのこの回転数(rpm)手前より上で変速したら、「早い」にしない(それ以上は回らない)</summary>
    public const double LimiterMarginRpm = 250;
    /// <summary>リミッターの回転数は、全開の行の回転数のこのパーセンタイル</summary>
    public const double LimiterPercentile = 0.995;
    /// <summary>リミッターを決めるのに要る、全開の行の数</summary>
    public const int LimiterMinRows = 200;
    /// <summary>変速の前のこの時間(ms)に、全開で出力が 0 以下の行があれば、リミッターに当たってから変速した</summary>
    public const double LimiterDwellMs = 500;
    /// <summary>リミッターに当たったとみなす、変速した回転数からの幅(rpm)</summary>
    public const double LimiterNearRpm = 150;
    private const double WattsPerHp = 745.7;

    public static ShiftVerdict Judge(double gainPct) =>
        gainPct < -OkPct ? ShiftVerdict.Early : gainPct > OkPct ? ShiftVerdict.Late : ShiftVerdict.Ok;

    private static bool IsDriveGear(double g) => g >= 1 && g <= 10;

    /// <summary>
    /// パワーカーブ。rows はこの車の行(is_race_on の行を時刻の順に)。全開の区切りが MinBins 未満なら null。
    /// ギアが変わってから AfterShiftMs の間の行は使わない(ターボ車は変速で過給が落ち、戻るまで出力が低い。
    /// 変速の時機を比べるには、過給が戻った後の出力で比べる)
    /// </summary>
    public static PowerCurve? Curve(IReadOnlyList<int> rows, double[] tsMs, double[] rpm, double[] powerW, double[] accel, double[] gear)
    {
        var bins = new SortedDictionary<int, List<double>>();
        double lastChange = double.NegativeInfinity;
        for (int k = 0; k < rows.Count; k++)
        {
            int i = rows[k];
            if (k > 0 && gear[i] != gear[rows[k - 1]]) lastChange = tsMs[i];
            if (tsMs[i] - lastChange < AfterShiftMs) continue;
            if (accel[i] < FullThrottle || !IsDriveGear(gear[i]) || !(powerW[i] > 0) || !(rpm[i] > 0)) continue;
            int b = (int)Math.Floor(rpm[i] / BinRpm);
            if (!bins.TryGetValue(b, out var list)) bins[b] = list = new();
            list.Add(powerW[i] / WattsPerHp);
        }
        var used = bins.Where(kv => kv.Value.Count >= MinBinRows).ToList();
        if (used.Count < MinBins) return null;
        return new PowerCurve(
            used.Select(kv => kv.Key * BinRpm + BinRpm / 2).ToArray(),
            used.Select(kv => Math.Round(Percentile(kv.Value, CurvePercentile), 1)).ToArray());
    }

    /// <summary>ギア比(次のギア ÷ 今のギア)。キーは今のギア。両方のギアに MinGearRows 以上の行があるものだけ</summary>
    public static Dictionary<int, double> GearRatios(IEnumerable<int> rows, double[] rpm, double[] speed, double[] accel, double[] gear, double[][] slipRatio)
    {
        var k = new Dictionary<int, List<double>>();
        foreach (int i in rows)
        {
            if (accel[i] < FullThrottle || !IsDriveGear(gear[i]) || !(speed[i] > MinSpeed) || !(rpm[i] > 0)) continue;
            if (slipRatio.Any(s => !double.IsFinite(s[i]) || Math.Abs(s[i]) > MaxSlipRatio)) continue;
            int g = (int)gear[i];
            if (!k.TryGetValue(g, out var list)) k[g] = list = new();
            list.Add(rpm[i] / speed[i]);
        }
        var perGear = k.Where(kv => kv.Value.Count >= MinGearRows).ToDictionary(kv => kv.Key, kv => Median(kv.Value));
        var ratios = new Dictionary<int, double>();
        foreach (var (g, v) in perGear)
            if (perGear.TryGetValue(g + 1, out var next)) ratios[g] = Math.Round(next / v, 4);
        return ratios;
    }

    /// <summary>最適な変速回転数(25 rpm 刻み)。ピークより上で、変速した後の出力が今の出力以上になる最初の回転数。カーブの中に無ければ null(リミッターまで引っ張る方が得)</summary>
    public static double? OptimalShiftRpm(PowerCurve c, double ratio)
    {
        double top = c.Rpm[^1] + BinRpm / 2;
        for (double r = c.PeakRpm; r <= top; r += 25)
        {
            var now = c.At(r);
            var after = c.At(r * ratio);
            if (now == null || after == null) continue;
            if (after >= now) return r;
        }
        return null;
    }

    /// <summary>
    /// 変速の得失(最大出力に対する %)。今の回転数か変速した後の回転数がカーブの外なら null。
    /// リミッターの LimiterMarginRpm 手前より上では、負(まだ早い)を 0 にする(それ以上は回らず、リミッターで出力が切れるので、そこで変速するのが一番よい)
    /// </summary>
    public static double? Gain(PowerCurve c, double rpm, double ratio, double? limiterRpm = null)
    {
        var now = c.At(rpm);
        var after = c.At(rpm * ratio);
        if (now == null || after == null) return null;
        double g = Math.Round((after.Value - now.Value) / c.MaxHp * 100, 1);
        return g < 0 && limiterRpm is double l && rpm >= l - LimiterMarginRpm ? 0 : g;
    }

    /// <summary>
    /// リミッターの回転数: 全開(ギア 1〜10)の行の回転数の 99.5 パーセンタイル。行が LimiterMinRows 未満なら null。
    /// engine_max_rpm は使わない(実データで、リミッターより 300〜750 rpm 高い車があった)
    /// </summary>
    public static double? LimiterRpm(IEnumerable<int> rows, double[] rpm, double[] accel, double[] gear)
    {
        var v = rows.Where(i => accel[i] >= FullThrottle && IsDriveGear(gear[i]) && rpm[i] > 0).Select(i => rpm[i]).ToList();
        return v.Count < LimiterMinRows ? null : Math.Round(Percentile(v, LimiterPercentile));
    }

    /// <summary>
    /// 最適な範囲: 得失が ±OkPct 以内になる回転数の幅(25 rpm 刻み。リミッターの手前の扱いは Gain と同じ)。
    /// 出力が平らな車(電気自動車など)では広く、山の尖った車では最適な変速回転数のまわりの狭い幅になる。
    /// 出力の山(最大)より上で、つながっている一番上の幅だけを取る(ターボ車は、低い回転の過給の谷でも得失が ±3% に入ることがあるため)。無ければ null
    /// </summary>
    public static (double Lo, double Hi)? OptimalRange(PowerCurve c, double ratio, double? limiterRpm)
    {
        double top = c.Rpm[^1] + BinRpm / 2;
        if (limiterRpm is double l) top = Math.Min(top, l);
        double? lo = null, hi = null;
        for (double r = Math.Floor(top / 25) * 25; r >= c.PeakRpm; r -= 25)
        {
            bool ok = Gain(c, r, ratio, limiterRpm) is double g && Math.Abs(g) <= OkPct;
            if (ok) { hi ??= r; lo = r; }
            else if (hi != null) break;   // 一番上の幅が切れた
        }
        return lo is double a && hi is double b ? (a, b) : null;
    }

    /// <summary>
    /// 全開のシフトアップ(g → g + 1。間の変速中 11 は MaxShiftMs まで)。変速を決めた行で全開だったものだけ。
    /// rows は時刻の順。gainOf(行, ギア, 回転数) で得失を付ける
    /// </summary>
    public static List<ShiftEvent> Upshifts(IReadOnlyList<int> rows, double[] tsMs, double[] gear, double[] rpm, double[] accel, double[] powerW, Func<int, int, double, double?> gainOf)
    {
        var list = new List<ShiftEvent>();
        for (int k = 0; k < rows.Count - 1; k++)
        {
            int i = rows[k];
            if (!IsDriveGear(gear[i])) continue;
            int j = k + 1;
            while (j < rows.Count && gear[rows[j]] == 11 && tsMs[rows[j]] - tsMs[i] <= MaxShiftMs) j++;
            if (j >= rows.Count || gear[rows[j]] != gear[i] + 1 || tsMs[rows[j]] - tsMs[i] > MaxShiftMs) continue;
            // 変速を決めた所 = 直前 PeakWindowMs の同じギアの行で、回転数が一番高い行(変速の始めには、ギアが変わる前に回転が落ち始める)
            int peak = i;
            for (int b = k - 1; b >= 0 && gear[rows[b]] == gear[i] && tsMs[i] - tsMs[rows[b]] <= PeakWindowMs; b--)
                if (rpm[rows[b]] > rpm[peak]) peak = rows[b];
            if (accel[peak] < FullThrottle) continue;
            // リミッターに当たっていたか: 変速を決めた行まで(回転が落ち始める前)の LimiterDwellMs に、
            // 一番高い回転数の近く(LimiterNearRpm 以内)で、全開なのに出力が 0 以下の行がある。
            // 回転が落ち始めた後の出力 0 は、変速そのもの(点火を切る)なので数えない(実データで、AT の変速にも出た)
            bool hit = false;
            int pk = k;
            while (rows[pk] != peak) pk--;
            for (int b = pk; b >= 0 && gear[rows[b]] == gear[i] && tsMs[peak] - tsMs[rows[b]] <= LimiterDwellMs; b--)
                if (accel[rows[b]] >= FullThrottle && powerW[rows[b]] <= 0 && rpm[rows[b]] >= rpm[peak] - LimiterNearRpm) { hit = true; break; }
            int g = (int)gear[i];
            list.Add(new ShiftEvent(peak, g, Math.Round(rpm[peak]), gainOf(peak, g, rpm[peak]), hit));
        }
        return list;
    }

    /// <summary>リミッターで出力が切れた行の前後、この時間(ms)を「リミッターに張り付いている」とする</summary>
    public const double LimiterBounceMs = 300;

    /// <summary>
    /// リミッターに張り付いている行(行番号で引く)。全開でリミッターの LimiterMarginRpm 手前より上なのに出力が 0 以下の行と、
    /// その前後 LimiterBounceMs の同じギアの行。張り付いている間は回転数がリミッターの下まで上下する(CR-X で 8,450〜8,700 rpm)ので、
    /// 回転数だけで見ると「早い」と「適正」を細かく行き来する。rows は時刻の順。limiterOf(行) はその行の車のリミッター
    /// </summary>
    public static bool[] LimiterBounce(IReadOnlyList<int> rows, int rowCount, double[] tsMs, double[] gear, double[] rpm, double[] accel, double[] powerW, Func<int, double?> limiterOf)
    {
        var mask = new bool[rowCount];
        for (int k = 0; k < rows.Count; k++)
        {
            int i = rows[k];
            if (accel[i] < FullThrottle || !IsDriveGear(gear[i]) || powerW[i] > 0 || limiterOf(i) is not double l || rpm[i] < l - LimiterMarginRpm) continue;
            // 前は、もうリミッターの手前に来ている行だけ(上がってくる途中は、まだ回るので張り付きではない)。後ろは、跳ね返って下がった行も
            for (int b = k; b >= 0 && gear[rows[b]] == gear[i] && tsMs[i] - tsMs[rows[b]] <= LimiterBounceMs && rpm[rows[b]] >= l - LimiterMarginRpm; b--) mask[rows[b]] = true;
            for (int f = k + 1; f < rows.Count && gear[rows[f]] == gear[i] && tsMs[rows[f]] - tsMs[i] <= LimiterBounceMs; f++) mask[rows[f]] = true;
        }
        return mask;
    }

    /// <summary>変速の前に、リミッターに張り付いていた時間(ms)。shiftRow(変速を決めた行)から前へ、同じギアで張り付いている行が続く間。張り付いていなければ 0</summary>
    public static double TimeAtLimiterMs(IReadOnlyList<int> rows, int shiftRow, double[] tsMs, double[] gear, bool[] bounce)
    {
        int k = -1;
        for (int j = 0; j < rows.Count; j++) if (rows[j] == shiftRow) { k = j; break; }
        if (k < 0 || !bounce[shiftRow]) return 0;
        int first = k;
        while (first > 0 && bounce[rows[first - 1]] && gear[rows[first - 1]] == gear[shiftRow]) first--;
        return Math.Round(tsMs[shiftRow] - tsMs[rows[first]]);
    }

    /// <summary>自動変速の見分けに要る、全開のシフトアップの数</summary>
    public const int AutoMinShifts = 8;
    /// <summary>変速回転数の 10〜90 パーセンタイルの幅がこれ以下なら、自動変速(か AutoDrive)とみなす(rpm)</summary>
    public const double AutoMaxSpreadRpm = 250;

    /// <summary>
    /// 自動変速(AT)か AutoDrive の走りらしいか。ゲームの自動変速はギアによらずほぼ同じ回転数で上げるので、
    /// 1 レースの全開のシフトアップの回転数がそろう(実データで 10〜90 パーセンタイルの幅が AT 66〜83 rpm、AutoDrive 165 rpm、
    /// 手動 690〜808 rpm)。シフトアップが AutoMinShifts 未満か、1 つのギアからしか無ければ null(分からない)。
    /// ただし、半分以上の変速がリミッターに当たってから(HitLimiter)なら、手動でリミッターまで引っ張っている
    /// (回転数がリミッターにそろう)ので自動変速としない。戻り値の Lo / Hi は 10 / 90 パーセンタイル
    /// </summary>
    public static (bool Auto, double Lo, double Hi)? LooksAutomatic(IReadOnlyCollection<ShiftEvent> shifts)
    {
        if (shifts.Count < AutoMinShifts || shifts.Select(s => s.FromGear).Distinct().Count() < 2) return null;
        var rpm = shifts.Select(s => s.Rpm).ToList();
        double lo = Percentile(rpm, 0.1), hi = Percentile(rpm, 0.9);
        bool limiterShifting = shifts.Count(s => s.HitLimiter) * 2 >= shifts.Count;
        return (hi - lo <= AutoMaxSpreadRpm && !limiterShifting, Math.Round(lo), Math.Round(hi));
    }

    /// <summary>
    /// 記録の中の車ごとの性能。車は車種と PI の組で分ける(チューニングで PI が変わると出力も変わる)。
    /// rows は is_race_on の行を時刻の順に。carOf(行) は車の組。tsMs は行の時刻(ms)
    /// </summary>
    public static List<(PowerProfile Profile, List<int> Rows)> Profiles(IReadOnlyList<int> rows, Func<int, (int Ordinal, int Pi)> carOf, double[] tsMs,
        double[] rpm, double[] powerW, double[] accel, double[] gear, double[] speed, double[][] slipRatio)
    {
        var groups = new Dictionary<(int, int), List<int>>();
        foreach (int i in rows)
        {
            var car = carOf(i);
            if (car.Ordinal <= 0) continue;
            if (!groups.TryGetValue(car, out var list)) groups[car] = list = new();
            list.Add(i);
        }
        var result = new List<(PowerProfile, List<int>)>();
        foreach (var ((ordinal, pi), list) in groups)
        {
            var curve = Curve(list, tsMs, rpm, powerW, accel, gear);
            var ratios = GearRatios(list, rpm, speed, accel, gear, slipRatio);
            var limiter = LimiterRpm(list, rpm, accel, gear);
            var opt = curve == null ? new Dictionary<int, double?>() : ratios.ToDictionary(kv => kv.Key, kv => OptimalShiftRpm(curve, kv.Value));
            var range = new Dictionary<int, (double, double)>();
            if (curve != null)
                foreach (var (g, r) in ratios)
                    if (OptimalRange(curve, r, limiter) is { } x) range[g] = x;
            result.Add((new PowerProfile(ordinal, pi, curve, ratios, opt, limiter, range), list));
        }
        return result;
    }

    /// <summary>p(0〜1)の位置の値(間は直線で補う)</summary>
    private static double Percentile(List<double> v, double p)
    {
        var s = v.OrderBy(x => x).ToArray();
        double pos = (s.Length - 1) * p;
        int lo = (int)Math.Floor(pos), hi = Math.Min(s.Length - 1, lo + 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }
}
