namespace Fh6.Player;

/// <summary>
/// 摩擦円・荷重移動のうち、ならしの幅と選んだレースで変わる値(前は画面の JS の ggAt・ltRaw・ltBaseline・ggEstimate・loadSeries・gripSeries)。
/// レースの選択か幅が変わったときに SelectionViewModel が作り、HostFriction で送る(コマごとには作らない)。
/// 点の位置・円の半径・表示の上限は C# が決め、それをキャンバスのどこに描くかは画面が決める
/// </summary>
public static class FrictionDynamic
{
    /// <summary>ならす幅の既定(前後の秒)。画面の「ならし」で変える</summary>
    public const double DefaultSmoothSec = 0.25;
    /// <summary>円の推定に使う行の最低速度(km/h。止まっている間の揺れとグリッドの発進を除く)</summary>
    public const double MinKmh = 20;
    /// <summary>点群に出す行の最低速度(km/h)</summary>
    public const double CloudKmh = 5;
    /// <summary>円を作るのに要る行数(30fps で 5 秒ぶん)</summary>
    public const int RingMinRows = 150;
    /// <summary>円の大きさ: その路面で出た値の上位 2%</summary>
    public const double RingPercentile = 0.98;
    /// <summary>これを超える加速度(G)は、接触・着地・復帰のはね上がり。円の推定にもグラフの % にも使わない</summary>
    public const double SaneG = 6;
    /// <summary>表示の上限 = 最大の円の何倍か</summary>
    public const double CapRatio = 1.3;
    /// <summary>円を作れなかったときの表示の上限(G)と、作れたときの下限(G)</summary>
    public const double DefaultMaxG = 1.5, MinScaleG = 1;
    /// <summary>ならさないとき(基準を選ぶとき)に前後の行を平均に入れる、時刻の差の上限(秒)</summary>
    public const double NeighborSec = 0.1;
    /// <summary>どれかの車輪の縮みがこれ未満なら伸び切り(空中。AirDetector と同じ境目)。荷重移動に使わない</summary>
    public const double AirSusp = 0.03;
    /// <summary>荷重移動の基準に使う行: 前後・横とも、この G 未満</summary>
    public const double CalmG = 0.1;
    /// <summary>荷重移動の基準に使う行: この速度(km/h)以上</summary>
    public const double CalmKmh = 40;
    /// <summary>荷重移動の基準を作るのに要る行数(足りなければ基準 0)</summary>
    public const int MinCalmRows = 60;
    /// <summary>荷重移動(縮みの差)はこれより大きくならない。超えたら円の推定に使わない</summary>
    public const double SaneLoad = 1;
    /// <summary>円を作れなかったときの表示の上限(縮みの差)と、作れたときの下限</summary>
    public const double DefaultMaxLoad = 0.4, MinScaleLoad = 0.2;
    /// <summary>円を作らない路面のまとめ(5 = 不明)</summary>
    public const int UnknownGroup = 5;

    /// <summary>画面から届いた幅を、使える値にする(0 以下・読めない値は既定の幅)</summary>
    public static double SmoothSec(double? sec) => sec is double s && double.IsFinite(s) && s > 0 ? s : DefaultSmoothSec;

    /// <summary>
    /// 行ごとの [横 G, 前後 G] を前後 sec 秒の三角の重み(近い行ほど重い)でならす。値の無い行から先はまたがない。値が無ければ null。
    /// sec が 0 なら、前後 1 行ずつ(時刻の差が NeighborSec 以下)の平均(基準を選ぶときのフレームごとの揺れを軽くならす)
    /// </summary>
    public static (double?[] X, double?[] Y) SmoothG(double[] t, double?[] lat, double?[] lon, double sec)
    {
        int n = t.Length;
        var has = new bool[n];
        var vx = new double[n];
        var vy = new double[n];
        for (int j = 0; j < n; j++)
        {
            if (lat[j] is not { } a || lon[j] is not { } b) continue;
            has[j] = true; vx[j] = a; vy[j] = b;
        }
        if (sec > 0) return Smooth(t, vx, vy, has, sec);
        var xs = new double?[n];
        var ys = new double?[n];
        for (int i = 0; i < n; i++)
        {
            if (!has[i]) continue;
            double a = vx[i], b = vy[i], w = 1;
            for (int j = i - 1; j <= i + 1; j += 2)
            {
                if (j < 0 || j >= n || !has[j] || Math.Abs(t[j] - t[i]) > NeighborSec) continue;
                a += vx[j]; b += vy[j]; w++;
            }
            xs[i] = a / w;
            ys[i] = b / w;
        }
        return (xs, ys);
    }

    /// <summary>
    /// 行ごとの縮みの差 [右 − 左, 前 − 後ろ] を、前後 sec 秒の三角の重みでならす(基準は引かない)。
    /// 値が無いか、どれかの車輪が伸び切り(空中)なら null。そういう行から先はまたがない(着地の前後を混ぜない)
    /// </summary>
    public static (double?[] X, double?[] Y) SmoothLoad(double[] t, SuspensionSeries s, double sec)
    {
        int n = t.Length;
        var has = new bool[n];
        var vx = new double[n];
        var vy = new double[n];
        for (int j = 0; j < n; j++)
        {
            if (s.Fl[j] is not { } fl || s.Fr[j] is not { } fr || s.Rl[j] is not { } rl || s.Rr[j] is not { } rr) continue;
            if (Math.Min(Math.Min(fl, fr), Math.Min(rl, rr)) < AirSusp) continue;
            has[j] = true;
            vx[j] = (fr + rr - fl - rl) / 2;
            vy[j] = (fl + fr - rl - rr) / 2;
        }
        return Smooth(t, vx, vy, has, sec);
    }

    /// <summary>
    /// 前後 sec 秒の三角の重みの平均(SmoothG・SmoothLoad の中身)。has が偽の行は null で、その行から先はまたがない。
    /// 足す順は前の JS と同じ(その行、前へ近い順、後ろへ近い順)。小数の丸めまで同じ値にするため
    /// </summary>
    private static (double?[] X, double?[] Y) Smooth(double[] t, double[] vx, double[] vy, bool[] has, double sec)
    {
        int n = t.Length;
        var xs = new double?[n];
        var ys = new double?[n];
        for (int i = 0; i < n; i++)
        {
            if (!has[i]) continue;
            double x = vx[i], y = vy[i], w = 1, ti = t[i];
            for (int j = i - 1; j >= 0; j--)
            {
                double dt = Math.Abs(t[j] - ti);
                if (dt >= sec || !has[j]) break;
                double k = 1 - dt / sec;
                x += vx[j] * k; y += vy[j] * k; w += k;
            }
            for (int j = i + 1; j < n; j++)
            {
                double dt = Math.Abs(t[j] - ti);
                if (dt >= sec || !has[j]) break;
                double k = 1 - dt / sec;
                x += vx[j] * k; y += vy[j] * k; w += k;
            }
            xs[i] = x / w;
            ys[i] = y / w;
        }
        return (xs, ys);
    }

    /// <summary>並べ替えていない数の並びの p 番目(0〜1)の値(前の JS の ggPercentile と同じ選び方。偶数個の中央値は小さい方)</summary>
    public static double Percentile(List<double> values, double p)
    {
        var s = values.ToArray();
        Array.Sort(s);
        return s[Math.Min(s.Length - 1, Math.Max(0, (int)Math.Ceiling(p * s.Length) - 1))];
    }

    /// <summary>荷重移動の基準: 差の中央値と、使った行数、4 輪それぞれの縮みの中央値(4 輪の棒の白の線。行が足りなければ null)</summary>
    public sealed record Baseline(double X, double Y, int N, double[]? Wheels);

    /// <summary>
    /// 荷重移動の基準: 選んでいる行のうち、まっすぐ一定の速さ(前後・横とも CalmG 未満、CalmKmh 以上)で走っている行の、縮みの差の中央値。
    /// G は sec によらず前後 1 行の平均で見る。行が MinCalmRows に足りなければ 0
    /// </summary>
    public static Baseline BuildBaseline(IReadOnlyList<int> rows, bool[] excluded, IReadOnlyList<double?> speed,
        (double?[] X, double?[] Y) calmG, (double?[] X, double?[] Y) load, SuspensionSeries s)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        var ws = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
        var wheels = new[] { s.Fl, s.Fr, s.Rl, s.Rr };
        foreach (int i in rows)
        {
            if (excluded[i]) continue;
            if (speed[i] is not { } kmh || kmh < CalmKmh) continue;
            if (calmG.X[i] is not { } gx || calmG.Y[i] is not { } gy || Math.Abs(gx) >= CalmG || Math.Abs(gy) >= CalmG) continue;
            if (load.X[i] is not { } lx || load.Y[i] is not { } ly) continue;
            xs.Add(lx); ys.Add(ly);
            for (int w = 0; w < 4; w++) ws[w].Add(wheels[w][i]!.Value);
        }
        if (xs.Count < MinCalmRows) return new Baseline(0, 0, xs.Count, null);
        return new Baseline(Percentile(xs, 0.5), Percentile(ys, 0.5), xs.Count, ws.Select(w => Percentile(w, 0.5)).ToArray());
    }

    /// <summary>路面のまとめ(FrictionStatic.SurfaceGroups の 0 以上の番号)ごとの円。r は半径、n は使った行数</summary>
    public sealed record Ring(int Group, double R, int N);

    /// <summary>
    /// 円の推定: 路面ごと(舗装は 1 つにまとめる)に、点の大きさの上位 2%。行数が RingMinRows に足りない路面は円を作らない。
    /// 速度が MinKmh 未満の行、除く行、sane を超える大きさの行、空中・混在・不明の路面の行は使わない。
    /// 並びは、その路面が選んだ行に初めて出た順(前の JS と同じ。円を描く順になる)
    /// </summary>
    public static FrictionRing[] EstimateRings(IReadOnlyList<int> rows, bool[] excluded, IReadOnlyList<double?> speed, IReadOnlyList<int> group,
        (double?[] X, double?[] Y) points, double sane)
    {
        var buckets = new Dictionary<int, List<double>>();   // 足した順を保つ(消さないので、列挙は足した順)
        foreach (int i in rows)
        {
            if (excluded[i]) continue;
            if (speed[i] is not { } kmh || kmh < MinKmh) continue;
            if (points.X[i] is not { } x || points.Y[i] is not { } y) continue;
            double m = Hypot(x, y);
            if (m > sane) continue;
            int g = group[i];
            if (g < 0 || g == UnknownGroup) continue;
            if (!buckets.TryGetValue(g, out var list)) buckets[g] = list = new List<double>();
            list.Add(m);
        }
        return buckets.Where(b => b.Value.Count >= RingMinRows)
            .Select(b => new FrictionRing(b.Key, Percentile(b.Value, RingPercentile), b.Value.Count)).ToArray();
    }

    /// <summary>表示の上限: いちばん大きい円の CapRatio 倍(下限 min)。円が 1 つも無ければ none</summary>
    public static double ScaleMax(FrictionRing[] rings, double min, double none) =>
        rings.Length == 0 ? none : Math.Max(min, rings.Max(r => r.R) * CapRatio);

    /// <summary>
    /// 行ごとの「摩擦円の %」(合成 G ÷ その路面の G の円 × 100、小数 1 桁)。4 輪の路面が混ざっている行は、円がある路面の車輪の円の平均で割る
    /// (雪・芝は円を作れるほど走らないことが多いので、その車輪は数えない)。円が無い路面・空中・値が無い行と、はね上がり(SaneG 超)は null
    /// </summary>
    public static double?[] GripPercent((double?[] X, double?[] Y) g, IReadOnlyList<int> group, SurfaceSeries surface, FrictionRing[] rings)
    {
        var r = rings.ToDictionary(x => x.Group, x => x.R);
        var wheels = new[] { surface.Fl, surface.Fr, surface.Rl, surface.Rr };
        double? RingOf(int i)
        {
            int grp = group[i];
            if (grp >= 0) return r.TryGetValue(grp, out var v) ? v : null;
            if (grp != FrictionStatic.GroupMixed) return null;
            double sum = 0;
            int n = 0;
            foreach (var w in wheels)
            {
                int k = w[i]!.Value & 7, gk = FrictionStatic.PavedKinds.Contains(k) ? 0 : k;
                if (r.TryGetValue(gk, out var v)) { sum += v; n++; }
            }
            return n > 0 ? sum / n : null;
        }
        var outp = new double?[group.Count];
        for (int i = 0; i < outp.Length; i++)
        {
            if (g.X[i] is not { } x || g.Y[i] is not { } y) continue;
            double m = Hypot(x, y);
            if (m > SaneG || RingOf(i) is not { } ring || !(ring > 0)) continue;
            outp[i] = JsRound(m / ring * 1000) / 10;
        }
        return outp;
    }

    /// <summary>点群に出す行(選んでいる行のうち、除く行でなく、速度が CloudKmh 以上で、点があるもの)。表示の上限で切るのは画面</summary>
    public static int[] CloudRows(IReadOnlyList<int> rows, bool[] excluded, IReadOnlyList<double?> speed, double?[] x) =>
        rows.Where(i => !excluded[i] && speed[i] is { } kmh && kmh >= CloudKmh && x[i] != null).ToArray();

    /// <summary>
    /// 選んだ行(null なら全部)と幅で、摩擦円と荷重移動の全部を作る。グラフの系列は % の小数 1 桁。
    /// 点は丸めずに送る(小数 4 桁に丸めると、読み取り値の G・% の最後の桁が、実データで 1〜2% の行で前と変わったため)
    /// </summary>
    public static HostFriction Build(SessionSeries s, SessionMarks m, int? race, double sec)
    {
        int n = s.T.Length;
        IReadOnlyList<int> rows = race is int r && r >= 0 && r < m.Races.Length ? m.Races[r].Dist.I : Enumerable.Range(0, n).ToArray();
        var excluded = new bool[n];
        foreach (int i in m.GgExcluded) excluded[i] = true;
        var group = s.Friction.Group;

        var g = SmoothG(s.T, s.GLat, s.GLong, sec);
        var calm = SmoothG(s.T, s.GLat, s.GLong, 0);
        var raw = SmoothLoad(s.T, s.Susp, sec);
        var b = BuildBaseline(rows, excluded, s.Speed, calm, raw, s.Susp);
        var load = (X: raw.X.Select(v => v - b.X).ToArray(), Y: raw.Y.Select(v => v - b.Y).ToArray());

        var ggRings = EstimateRings(rows, excluded, s.Speed, group, g, SaneG);
        var loadRings = EstimateRings(rows, excluded, s.Speed, group, load, SaneLoad);
        // グラフでは G と同じ段に重ねるので、向きを G に合わせる: 前後は正 = 後ろが沈む(加速の側)、左右は正 = 左が沈む(右コーナーの側)
        static double?[] Pct(double?[] v) => v.Select(x => x is { } d ? JsRound(-d * 1000) / 10 : (double?)null).ToArray();
        return new HostFriction(
            Race: race,
            Sec: sec,
            Gg: new FrictionView(g.X, g.Y, ggRings, ScaleMax(ggRings, MinScaleG, DefaultMaxG), CloudRows(rows, excluded, s.Speed, g.X)),
            Load: new FrictionView(load.X, load.Y, loadRings, ScaleMax(loadRings, MinScaleLoad, DefaultMaxLoad), CloudRows(rows, excluded, s.Speed, load.X)),
            BaseWheels: b.Wheels,
            LoadLong: Pct(load.Y),
            LoadLat: Pct(load.X),
            Grip: GripPercent(g, group, s.Surface, ggRings));
    }

    /// <summary>JS の Math.hypot と同じ値(2 つの数の hypot は、どちらも sqrt(x² + y²) を正しく丸めた値)</summary>
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>JS の Math.round(.5 は大きい方へ)</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);
}
