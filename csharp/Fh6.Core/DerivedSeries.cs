namespace Fh6.Core;

/// <summary>路面の分類(surface_rumble の値。docs/telemetry-field-notes.md「surface_rumble の値と路面の対応」)</summary>
/// <remarks>
/// 0.10 と 0.20 は雪・ジャンプ台以外の場所にも出る。0.24 は縁石(サーキットで確認)。wheel_on_rumble_strip は FH6 では立たない(13 本の記録で一度も 1 にならない)。
/// 6 は再生アプリで「空中」に使うので空けておく。
/// </remarks>
public enum SurfaceKind { Paved = 0, Snow = 1, Dirt = 2, Ramp = 3, Grass = 4, Unknown = 5, Kerb = 7 }

/// <summary>テレメトリーから計算するグラフ用の値(勾配、旋回の曲がり具合、路面の分類)</summary>
public static class DerivedSeries
{
    /// <summary>
    /// 勾配(%、正 = 上り)。区間の中で前後 halfWindowM(既定 15m)の範囲の標高差 ÷ 水平距離。
    /// 範囲が minSpanM 未満、または区間の外(segment == -1)の行は NaN。
    /// 車体の pitch はサスペンションの沈みを含むので使わず、座標から求める。
    /// </summary>
    public static double[] Grade(int[] segment, double[] distance, double[] positionY, double halfWindowM = 15, double minSpanM = 10)
    {
        int n = segment.Length;
        var g = new double[n];
        for (int i = 0; i < n; i++)
        {
            g[i] = double.NaN;
            if (segment[i] < 0 || !double.IsFinite(positionY[i])) continue;
            // 同じ区間で、距離が前後 halfWindowM に入る行の端(距離は区間の中で増えるだけ)
            int lo = i, hi = i;
            while (lo > 0 && segment[lo - 1] == segment[i] && distance[lo - 1] >= distance[i] - halfWindowM) lo--;
            while (hi < n - 1 && segment[hi + 1] == segment[i] && distance[hi + 1] <= distance[i] + halfWindowM) hi++;
            double span = distance[hi] - distance[lo];
            if (span < minSpanM || !double.IsFinite(positionY[lo]) || !double.IsFinite(positionY[hi])) continue;
            g[i] = (positionY[hi] - positionY[lo]) / span * 100;
        }
        return g;
    }

    public const double TurnMinKmh = 18;
    public const double TurnLimit = 40;

    /// <summary>
    /// 旋回の曲がり具合 = 1000 / R(R は車が実際に描いた線の半径 m。正 = 右)。angular_velocity_y ÷ speed × 1000。
    /// 速度 18 km/h 未満は NaN、±40(R = 25m)で頭打ち。道路の設計の半径ではない(タイヤの限界で決まる)。
    /// </summary>
    public static double[] TurnCurvature(double[] isRaceOn, double[] yawRate, double[] speed)
    {
        int n = yawRate.Length;
        var c = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = speed[i];
            c[i] = isRaceOn[i] == 1 && double.IsFinite(v) && v * 3.6 >= TurnMinKmh && double.IsFinite(yawRate[i])
                ? Math.Clamp(yawRate[i] / v * 1000, -TurnLimit, TurnLimit)
                : double.NaN;
        }
        return c;
    }

    public static SurfaceKind ClassifySurface(double rumble)
    {
        if (!double.IsFinite(rumble)) return SurfaceKind.Unknown;
        if (Near(rumble, 0.00)) return SurfaceKind.Paved;
        if (Near(rumble, 0.10)) return SurfaceKind.Snow;
        if (Near(rumble, 0.12)) return SurfaceKind.Dirt;
        if (Near(rumble, 0.20)) return SurfaceKind.Ramp;
        if (Near(rumble, 0.24)) return SurfaceKind.Kerb;
        if (Near(rumble, 0.60)) return SurfaceKind.Grass;
        return SurfaceKind.Unknown;

        static bool Near(double a, double b) => Math.Abs(a - b) < 0.005;
    }
}
