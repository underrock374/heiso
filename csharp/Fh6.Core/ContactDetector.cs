namespace Fh6.Core;

public enum ContactKind { Contact, Landing }

/// <summary>接触(または着地)の候補。Index は加速度が跳ね上がった行。速度は km/h、加速度は m/s²</summary>
public readonly record struct ContactEvent(int Index, ContactKind Kind, double PeakAccel, double SpeedBeforeKmh, double SpeedAfterKmh)
{
    public double PeakG => PeakAccel / 9.80665;
    public double DropKmh => SpeedBeforeKmh - SpeedAfterKmh;
}

/// <summary>
/// 車や壁への接触の候補を見つける。水平方向の加速度 √(acceleration_x² + acceleration_z²) が1サンプルで跳ね上がった瞬間。
/// ブレーキの有無は問わない(ブレーキ中の追突、横から押されて速度が上がる接触も拾うため)。
/// 直前に空中だった場合は着地として分ける。閾値は暫定で、実データを見て調整する(2026-09-28 の分析)。
/// 軽く当てられた接触は加速度にほとんど表れず、拾えないことがある。
/// </summary>
public static class ContactDetector
{
    public sealed record Options
    {
        /// <summary>水平加速度がこれを超えたら(m/s²。約 4G)</summary>
        public double MinAccel { get; init; } = 40;
        /// <summary>前のサンプルからこれ以上増えたら(m/s²。跳ね上がり)</summary>
        public double MinRise { get; init; } = 25;
        /// <summary>この秒数以内に続く候補は1件にまとめる</summary>
        public double MergeSec { get; init; } = 0.5;
        /// <summary>行の間隔がこれより空いていたら計算しない(取りこぼし・停止区間)</summary>
        public double MaxGapMs { get; init; } = 250;
        /// <summary>直前のこの秒数以内に空中なら着地とする</summary>
        public double LandingWithinSec { get; init; } = 0.3;
        /// <summary>接触の後の最低速度を探す秒数</summary>
        public double AfterSec { get; init; } = 0.5;
        /// <summary>前後の行とも速度がこれ未満(km/h)なら拾わない。チェックポイント逃しの復帰直後に、止まったまま 20G の跳ね上がりが出るため</summary>
        public double MinSpeedKmh { get; init; } = 5;
    }

    public static readonly Options Default = new();

    /// <param name="timeMs">行ごとの時刻(ミリ秒)。recv_time × 1000 を渡す。timestamp_ms は 60fps 近くでは約 32ms 刻みで、約半分の行が直前と同じ値になるため使わない</param>
    /// <param name="speed">m/s(CSV の speed)</param>
    /// <param name="extended">行ごとに 4 輪とも伸び切っているか(AirDetector.AllWheelsExtended)</param>
    public static List<ContactEvent> Detect(double[] timeMs, double[] isRaceOn, double[] speed,
                                            double[] accelX, double[] accelZ, bool[] extended, Options? options = null)
    {
        var o = options ?? Default;
        int n = timeMs.Length;
        foreach (var a in new[] { isRaceOn, speed, accelX, accelZ })
            if (a.Length != n) throw new ArgumentException("列の長さが揃っていません");
        if (extended.Length != n) throw new ArgumentException("列の長さが揃っていません");

        var list = new List<ContactEvent>();
        double lastHitTs = double.NegativeInfinity, lastAirTs = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            if (isRaceOn[i] != 1) continue;
            if (extended[i]) lastAirTs = timeMs[i];
            if (i == 0 || isRaceOn[i - 1] != 1) continue;
            double dt = timeMs[i] - timeMs[i - 1];
            if (!(dt > 0) || dt > o.MaxGapMs) continue;

            double a = Horizontal(accelX[i], accelZ[i]), a0 = Horizontal(accelX[i - 1], accelZ[i - 1]);
            if (!(a > o.MinAccel && a - a0 >= o.MinRise)) continue;
            if (speed[i] * 3.6 < o.MinSpeedKmh && speed[i - 1] * 3.6 < o.MinSpeedKmh) continue;

            // 続けて起きたものは1件に(最大の加速度だけ更新する)
            if (timeMs[i] - lastHitTs <= o.MergeSec * 1000 && list.Count > 0)
            {
                var prev = list[^1];
                if (a > prev.PeakAccel) list[^1] = prev with { PeakAccel = Math.Round(a, 1) };
                lastHitTs = timeMs[i];
                continue;
            }
            lastHitTs = timeMs[i];

            bool landing = timeMs[i] - lastAirTs <= o.LandingWithinSec * 1000;
            double before = speed[i - 1] * 3.6, after = speed[i] * 3.6;
            for (int k = i; k < n && isRaceOn[k] == 1 && timeMs[k] - timeMs[i] <= o.AfterSec * 1000; k++)
                after = Math.Min(after, speed[k] * 3.6);

            list.Add(new ContactEvent(i, landing ? ContactKind.Landing : ContactKind.Contact,
                                      Math.Round(a, 1), Math.Round(before, 1), Math.Round(after, 1)));
        }
        return list;
    }

    private static double Horizontal(double x, double z) => Math.Sqrt(x * x + z * z);
}
