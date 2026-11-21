namespace Fh6.Player;

/// <summary>
/// 横軸を距離にしたグラフと、標高の段のための計算(前は画面の JS の buildDistMap・buildCutRows・elevRange)。
/// 結果は BuildMarks でレースの印に入れて画面へ送る
/// </summary>
public static class DistanceAxis
{
    /// <summary>標高の段の上下は、この幅(m)より狭くしない(都内など標高差の小さいコースで、小さな凹凸を大げさに見せない)</summary>
    public const double ElevMinSpanM = 50;

    /// <summary>
    /// 距離軸のグラフに出す行: レースの行(RaceDistanceSeries の並び)のうち、最後まで残った走り(k = 1)で、
    /// 距離が前に出した行より大きいもの。返すのは、その行の並びの中の番号
    /// </summary>
    public static int[] GraphRows(IReadOnlyList<double> d, IReadOnlyList<int> k)
    {
        var rows = new List<int>();
        double last = double.NegativeInfinity;
        for (int j = 0; j < d.Count; j++)
        {
            if (k[j] == 0 || !(d[j] > last)) continue;
            rows.Add(j);
            last = d[j];
        }
        return rows.ToArray();
    }

    /// <summary>やり直した走り(どれかのレースで、最後まで残らなかった行)の、系列の中の位置。小さい順</summary>
    public static int[] CutRows(IEnumerable<RaceDistanceSeries> races) =>
        races.SelectMany(r => r.I.Where((_, j) => r.K[j] == 0)).Distinct().Order().ToArray();

    /// <summary>
    /// 標高の段の上下 [下, 上](m、整数)。値の最低〜最高に 5% の余白。幅が 50m 未満なら、真ん中を中心に 50m。値が無ければ null
    /// </summary>
    public static int[]? ElevRange(IEnumerable<double?> values)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (v is not { } x) continue;
            if (x < lo) lo = x;
            if (x > hi) hi = x;
        }
        if (!double.IsFinite(lo)) return null;
        double span = hi - lo;
        if (span < ElevMinSpanM)
        {
            double c = (lo + hi) / 2;
            return [(int)Math.Floor(c - ElevMinSpanM / 2), (int)Math.Ceiling(c + ElevMinSpanM / 2)];
        }
        return [(int)Math.Floor(lo - span * 0.05), (int)Math.Ceiling(hi + span * 0.05)];
    }
}
