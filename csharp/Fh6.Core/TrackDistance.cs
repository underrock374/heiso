namespace Fh6.Core;

/// <summary>
/// 行ごとの区間番号と、座標から求めた累積の走行距離(m)。
/// dist_traveled はメートルではないので使わない(docs/telemetry-field-notes.md)。
/// </summary>
public static class TrackDistance
{
    /// <summary>1サンプルでこれを超えて水平に動いたらファストトラベルとみなし、区間を切る</summary>
    public const double FastTravelM = 200;

    /// <summary>
    /// segment: 区間番号(0 から)。is_race_on == 0 の行と座標が無い行は -1。
    /// 停止区間(is_race_on == 0)とファストトラベルで区間が切れる。
    /// distance: 記録の先頭からの累積の水平距離(x, z)。区間の中の移動だけを足し、区間をまたぐ移動は足さない。
    /// segment が -1 の行は NaN。
    /// </summary>
    public static (int[] Segment, double[] Distance) Compute(double[] isRaceOn, double[] x, double[] z)
    {
        int n = isRaceOn.Length;
        if (x.Length != n || z.Length != n) throw new ArgumentException("列の長さが揃っていません");

        var seg = new int[n];
        var dist = new double[n];
        int current = -1;
        bool inSegment = false;
        double total = 0, px = 0, pz = 0;

        for (int i = 0; i < n; i++)
        {
            bool on = isRaceOn[i] != 0 && !double.IsNaN(isRaceOn[i]) && double.IsFinite(x[i]) && double.IsFinite(z[i]);
            if (!on)
            {
                seg[i] = -1;
                dist[i] = double.NaN;
                inSegment = false;
                continue;
            }

            if (inSegment)
            {
                double step = Math.Sqrt((x[i] - px) * (x[i] - px) + (z[i] - pz) * (z[i] - pz));
                if (step > FastTravelM) current++; // ファストトラベル: 新しい区間、距離は足さない
                else total += step;
            }
            else
            {
                current++;
                inSegment = true;
            }

            seg[i] = current;
            dist[i] = total;
            px = x[i];
            pz = z[i];
        }
        return (seg, dist);
    }
}
