namespace Fh6.Core;

/// <summary>
/// レース区間の中の、スタートからの距離(m。座標の水平距離の積み上げ)。距離軸のグラフに使う。
/// リワインドとチェックポイント逃しの復帰では、それまでに走った道筋の近くに戻っていれば、そこの距離から数え直す。
/// 戻った先より後に走っていた行(後のどの行よりも距離が大きい行)は「やり直した走り」として Kept = false にする(距離軸のグラフには出さない)。
/// dist_traveled はメートルではなく、コースごとに係数が違うので使わない。
/// </summary>
public static class RaceDistance
{
    /// <summary>1 サンプルでこれを超えて位置が飛んだら、復帰(チェックポイント逃しなど)とみなす</summary>
    public const double JumpM = 20;
    /// <summary>戻った先が、それまでの道筋からこの距離以内なら、そこの距離から数え直す</summary>
    public const double RejoinM = 30;
    /// <summary>戻った先を探すのは、直前のこの距離の道筋だけ(周回で前の周の同じ場所に戻らないように)</summary>
    public const double SearchBackM = 2000;
    /// <summary>戻った先がほぼ道筋の上とみなす距離(リワインドはそれまでに走った位置そのものに戻る)</summary>
    public const double ExactM = 5;
    /// <summary>一番新しい候補からこの距離(道筋で)以内なら、ほぼ道筋の上の候補を優先する</summary>
    public const double PreferExactWithinM = 500;

    /// <summary>
    /// 行 from 〜 to(両端を含む)の距離。Distance は停止区間の行と座標の無い行が NaN。
    /// Kept は、最後まで残った走りの行だけ true(停止中の行も含むので、距離は行の順に減らないが、同じ値が続くことがある)
    /// </summary>
    public static (double[] Distance, bool[] Kept) Compute(double[] isRaceOn, double[] x, double[] z, int from, int to)
    {
        int n = to - from + 1;
        var dist = new double[n];
        var kept = new bool[n];
        Array.Fill(dist, double.NaN);

        // 今の道筋(残っている行の番号。距離は増えていく)
        var path = new List<int>();
        bool gap = false;
        double total = 0;
        int prev = -1;   // 1 つ前の走行中の行(道筋に積んでいなくても)

        for (int k = 0; k < n; k++)
        {
            int i = from + k;
            bool on = isRaceOn[i] == 1 && double.IsFinite(x[i]) && double.IsFinite(z[i]);
            if (!on)
            {
                gap = true;
                continue;
            }
            if (path.Count == 0)
            {
                path.Add(k);
                dist[k] = total = 0;
                gap = false;
                prev = i;
                continue;
            }

            double step = Hypot(x[i] - x[prev], z[i] - z[prev]);
            if (gap || step > JumpM)
            {
                // 停止区間の明けか位置の飛び: 道筋の近くに戻っていれば、そこから数え直す
                int at = Nearest(path, from, x, z, dist, x[i], z[i]);
                if (at >= 0)
                {
                    int pk = path[at];
                    // 戻った先より後の道筋は捨てる(その行は、後で距離が戻るので Kept = false になる)
                    path.RemoveRange(at + 1, path.Count - at - 1);
                    total = dist[pk] + Hypot(x[i] - x[from + pk], z[i] - z[from + pk]);
                }
                // 近くに無ければ(道筋から離れた所への復帰)、距離を足さずに続ける
            }
            else
            {
                total += step;
            }
            gap = false;
            prev = i;
            dist[k] = total;
            // 同じ距離の行(停止中)は道筋に積まない
            if (total > dist[path[^1]]) path.Add(k);
        }

        // 最後まで残った走り: 後のどの行よりも距離が大きくない行
        double min = double.PositiveInfinity;
        for (int k = n - 1; k >= 0; k--)
        {
            if (double.IsNaN(dist[k])) continue;
            kept[k] = dist[k] <= min + 1e-9;
            min = Math.Min(min, dist[k]);
        }
        return (dist, kept);
    }

    /// <summary>
    /// やり直し区間: Kept = false の走行中の行が続く所の、最初の行と、その後で最初に Kept = true になる行(戻った先から走り直す行)。
    /// 番号は Compute の結果の配列の中の位置(from からの相対)。間の停止区間(リワインドの画面など)も区間に入る。
    /// 再生時に、この区間を飛ばす(クリーン再生)
    /// </summary>
    public static List<(int Start, int Resume)> RedoRanges(double[] dist, bool[] kept)
    {
        var ranges = new List<(int, int)>();
        int start = -1;
        for (int k = 0; k < dist.Length; k++)
        {
            if (double.IsNaN(dist[k])) continue;
            if (!kept[k]) { if (start < 0) start = k; }
            else if (start >= 0)
            {
                ranges.Add((start, k));
                start = -1;
            }
        }
        return ranges;
    }

    /// <summary>
    /// 戻った先の、path の中の位置。直前の道筋から順にさかのぼり、(px, pz) に RejoinM 以内まで近づいた一続きの所ごとに、一番近い点を候補にする。
    /// 一番新しい候補を選ぶ。ただし一番新しい候補から道筋で PreferExactWithinM 以内に、ほぼ道筋の上(ExactM 以内)の候補があればそれを選ぶ
    /// (ヘアピンの反対側の道より、リワインドで戻った道筋そのものを。前の周の同じ場所は選ばない)。直前 SearchBackM の範囲だけを探す。無ければ -1
    /// </summary>
    private static int Nearest(List<int> path, int from, double[] x, double[] z, double[] dist, double px, double pz)
    {
        double limit = dist[path[^1]] - SearchBackM;
        int firstNear = -1, runBest = -1;
        double runBestD = double.MaxValue;
        for (int j = path.Count - 1; j >= -1; j--)
        {
            bool inRange = j >= 0 && dist[path[j]] >= limit;
            double d = inRange ? Hypot(px - x[from + path[j]], pz - z[from + path[j]]) : double.MaxValue;
            if (d <= RejoinM)
            {
                if (d < runBestD) { runBest = j; runBestD = d; }
                continue;
            }
            // 近づいていた一続きの所を抜けた
            if (runBest >= 0)
            {
                if (firstNear >= 0 && dist[path[firstNear]] - dist[path[runBest]] > PreferExactWithinM) break;
                if (runBestD <= ExactM) return runBest;
                if (firstNear < 0) firstNear = runBest;
                runBest = -1;
                runBestD = double.MaxValue;
            }
            if (!inRange) break;
            if (firstNear >= 0 && dist[path[firstNear]] - dist[path[j]] > PreferExactWithinM) break;
        }
        return firstNear;
    }

    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
}
