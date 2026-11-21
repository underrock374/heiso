namespace Fh6.Player;

/// <summary>
/// 摩擦円・荷重移動のうち、記録を開いたときに 1 度だけ決まる値(前は画面の JS の ggSurfaceGroup・ggExcludeMask・ltWheelsSmooth)。
/// 結果は BuildSeries・BuildMarks で画面へ送る
/// </summary>
public static class FrictionStatic
{
    /// <summary>路面のまとめ: 停止区間(値が無い)</summary>
    public const int GroupNone = -1;
    /// <summary>路面のまとめ: 空中(どれかの車輪)</summary>
    public const int GroupAir = -2;
    /// <summary>路面のまとめ: 混在(4 輪の路面が違う)</summary>
    public const int GroupMixed = -3;

    /// <summary>舗装にまとめる路面(舗装・ジャンプ台・縁石)。路面の逸脱の判定(SurfaceExcursionDetector)と同じ扱い</summary>
    public static readonly int[] PavedKinds = [0, 3, 7];

    /// <summary>接触の前後に、円の推定と点群から除く時間(秒)</summary>
    public const double ContactSec = 0.3;
    /// <summary>ジャンプの始まりの前に除く時間(秒)</summary>
    public const double BeforeJumpSec = 0.1;
    /// <summary>着地の後に除く時間(秒)</summary>
    public const double AfterJumpSec = 0.5;

    /// <summary>4 輪の棒をならす幅(前後の秒)。画面の「ならし」によらない</summary>
    public const double WheelSmoothSec = 0.25;
    /// <summary>4 輪の棒: 幅の中で一度でもこの縮み未満なら「浮きかけ」</summary>
    public const double WheelLight = 0.05;
    /// <summary>4 輪の棒: 幅の中で一度でもこの縮みを超えたら「縮み切りに近い」(底突きの判定は 0.98)</summary>
    public const double WheelBottom = 0.95;
    /// <summary>4 輪の棒の状態のビット(車輪 k の分は、これを 2k ビット左へずらす)</summary>
    public const int WheelLightBit = 1, WheelBottomBit = 2;

    /// <summary>
    /// 行ごとの路面のまとめ。舗装(ジャンプ台・縁石を含む)は 0、ほかは SurfaceKind の番号(1 雪など、2 ダート、4 芝・荒れ地、5 不明)。
    /// 値が無ければ GroupNone、どれかの車輪が空中なら GroupAir、4 輪で違えば GroupMixed
    /// </summary>
    public static int[] SurfaceGroups(SurfaceSeries s)
    {
        var groups = new int[s.Fl.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            if (s.Fl[i] is not { } fl || s.Fr[i] is not { } fr || s.Rl[i] is not { } rl || s.Rr[i] is not { } rr) { groups[i] = GroupNone; continue; }
            int[] k = [fl & 7, fr & 7, rl & 7, rr & 7];
            if (k.Contains(6)) { groups[i] = GroupAir; continue; }
            var g = k.Select(v => PavedKinds.Contains(v) ? 0 : v).ToArray();
            groups[i] = g.All(v => v == g[0]) ? g[0] : GroupMixed;
        }
        return groups;
    }

    /// <summary>
    /// 円の推定と点群から除く行(系列の位置。小さい順): 接触の前後 ContactSec 秒と、ジャンプの BeforeJumpSec 秒前から着地の AfterJumpSec 秒後まで。
    /// t は系列の横軸(動画の秒)、時刻は印の値
    /// </summary>
    public static int[] ExcludedRows(IReadOnlyList<double> t, IEnumerable<double> contacts, IEnumerable<(double T0, double T1)> jumps)
    {
        var spans = contacts.Select(c => (A: c - ContactSec, B: c + ContactSec))
            .Concat(jumps.Select(j => (A: j.T0 - BeforeJumpSec, B: j.T1 + AfterJumpSec)));
        var rows = new SortedSet<int>();
        foreach (var (a, b) in spans)
            for (int i = LowerBound(t, a); i < t.Count && t[i] <= b; i++) rows.Add(i);
        return rows.ToArray();
    }

    /// <summary>昇順の t の中で、v 以上になる最初の位置</summary>
    private static int LowerBound(IReadOnlyList<double> t, double v)
    {
        int lo = 0, hi = t.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 4 輪の棒: 車輪ごとに、前後 WheelSmoothSec 秒の三角の重みの平均(1 万分の 1 の整数)、幅の中でいちばん縮んだ値(記録の値は小数 3 桁なので、1000 分の 1 の整数)、
    /// 状態(WheelLightBit: 幅の中で浮きかけ、WheelBottomBit: 縮み切りに近い。4 輪を 1 つの整数に)。行の 4 輪の値がそろっていなければ null。
    /// 伸び切りの行もまたいでならす(浮いた車輪の棒も出すため)
    /// </summary>
    public static WheelBarsSeries WheelBars(IReadOnlyList<double> t, SuspensionSeries s)
    {
        var wheels = new[] { s.Fl, s.Fr, s.Rl, s.Rr };
        int n = t.Count;
        var avg = wheels.Select(_ => new int?[n]).ToArray();
        var hi = wheels.Select(_ => new int?[n]).ToArray();
        var state = new int?[n];
        bool All(int j) => wheels.All(w => w[j] != null);
        for (int i = 0; i < n; i++)
        {
            if (!All(i)) continue;
            var sum = wheels.Select(w => w[i]!.Value).ToArray();
            var lo = (double[])sum.Clone();
            var top = (double[])sum.Clone();
            double weight = 1;
            foreach (int dir in (int[])[-1, 1])
            {
                for (int j = i + dir; j >= 0 && j < n; j += dir)
                {
                    double dt = Math.Abs(t[j] - t[i]);
                    if (dt >= WheelSmoothSec || !All(j)) break;
                    double k = 1 - dt / WheelSmoothSec;
                    for (int w = 0; w < 4; w++)
                    {
                        double x = wheels[w][j]!.Value;
                        sum[w] += x * k;
                        if (x < lo[w]) lo[w] = x;
                        if (x > top[w]) top[w] = x;
                    }
                    weight += k;
                }
            }
            int bits = 0;
            for (int w = 0; w < 4; w++)
            {
                avg[w][i] = (int)Math.Round(sum[w] / weight * 10000);
                hi[w][i] = (int)Math.Round(top[w] * 1000);
                bits |= ((lo[w] < WheelLight ? WheelLightBit : 0) | (top[w] > WheelBottom ? WheelBottomBit : 0)) << (2 * w);
            }
            state[i] = bits;
        }
        WheelBar Bar(int w) => new(Avg: avg[w], Hi: hi[w]);
        return new WheelBarsSeries(Fl: Bar(0), Fr: Bar(1), Rl: Bar(2), Rr: Bar(3), State: state);
    }
}
