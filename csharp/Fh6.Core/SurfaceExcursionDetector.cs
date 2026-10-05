namespace Fh6.Core;

/// <summary>
/// 路面の逸脱(舗装を走っていたのに、どれかの車輪が舗装から外れた)と、それに続くスリップ・停止区間。
/// Wheels は外れた車輪(0 = 左前, 1 = 右前, 2 = 左後, 3 = 右後)、Surfaces はその車輪の路面(舗装以外で最も多かったもの)。
/// SlipIndex / SlipPeak はスリップ(前後平均の大きい方)が限界 1.0 を超えた最初の行と最大値(超えなければ -1 / その間の最大値)。
/// </summary>
public sealed record SurfaceExcursion(int StartIndex, int EndIndex, int[] Wheels, SurfaceKind[] Surfaces,
                                      int SlipIndex, double SlipPeak, int SlipEndIndex, bool EndsInGap)
{
    /// <summary>結果を伴う(スリップが限界を超えた、または停止区間に入った)。画面で強調する</summary>
    public bool Consequential => SlipIndex >= 0 || EndsInGap;
}

/// <summary>
/// 4 輪とも舗装の状態が続いた後に、どれかの車輪が舗装から外れた瞬間を見つけ、その後のスリップを結びつける。
/// 縁石(surface_rumble 0.24。念のため wheel_on_rumble_strip も)は舗装から外れたとみなさない。
/// 閾値は暫定(2026-09-27 の 174607・110104・173311 で決めた)。
/// </summary>
public static class SurfaceExcursionDetector
{
    public sealed record Options
    {
        /// <summary>逸脱の前に、4 輪とも舗装がこの秒数以上続いていること</summary>
        public double PavedBeforeSec { get; init; } = 0.3;
        /// <summary>舗装から外れた状態がこの秒数以上続いたら逸脱(これより短いものはちらつきとして無視)</summary>
        public double MinOffSec { get; init; } = 0.1;
        /// <summary>この秒数以内に続く逸脱は 1 件にまとめる</summary>
        public double MergeSec { get; init; } = 0.3;
        /// <summary>対象にする速度(km/h)</summary>
        public double MinKmh { get; init; } = 30;
        /// <summary>スリップの限界</summary>
        public double SlipLimit { get; init; } = 1.0;
        /// <summary>逸脱のこの秒数前からスリップを見る(スリップと路面の変化がほぼ同時のことがある)</summary>
        public double SlipBeforeSec { get; init; } = 0.3;
        /// <summary>逸脱からこの秒数後までスリップと停止区間を見る</summary>
        public double AfterSec { get; init; } = 2.0;
    }

    public static readonly Options Default = new();

    /// <param name="timeMs">行ごとの時刻(ミリ秒、recv_time × 1000)</param>
    /// <param name="speed">m/s</param>
    /// <param name="extended">行ごとに 4 輪とも伸び切っているか(空中)</param>
    /// <param name="surfaceRumble">車輪ごとの surface_rumble(左前・右前・左後・右後)</param>
    /// <param name="rumbleStrip">車輪ごとの wheel_on_rumble_strip</param>
    /// <param name="slip">行ごとのスリップ(前輪平均と後輪平均の大きい方)</param>
    public static List<SurfaceExcursion> Detect(double[] timeMs, double[] isRaceOn, double[] speed, bool[] extended,
                                                double[][] surfaceRumble, double[][] rumbleStrip, double[] slip,
                                                Options? options = null)
    {
        var o = options ?? Default;
        int n = timeMs.Length;
        if (surfaceRumble.Length != 4 || rumbleStrip.Length != 4) throw new ArgumentException("車輪は 4 つ");

        // 行ごとの状態: 対象外 / 4 輪とも舗装 / どれかが外れている(外れた車輪のビット)
        const int Ineligible = -1;
        var off = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (isRaceOn[i] != 1 || extended[i] || !(speed[i] * 3.6 >= o.MinKmh)) { off[i] = Ineligible; continue; }
            int bits = 0;
            for (int w = 0; w < 4; w++)
            {
                if (rumbleStrip[w][i] != 0) continue;   // 縁石
                var k = DerivedSeries.ClassifySurface(surfaceRumble[w][i]);
                if (k is not (SurfaceKind.Paved or SurfaceKind.Ramp or SurfaceKind.Kerb)) bits |= 1 << w;
            }
            off[i] = bits;
        }

        // 舗装から外れた行の連なり(0.1 秒以上のもの)を探す。短いちらつきは舗装として扱う
        var runs = new List<(int Start, int End, int Bits)>();
        for (int i = 0; i < n;)
        {
            if (off[i] <= 0) { i++; continue; }
            int s = i, bits = 0;
            while (i < n && off[i] > 0) { bits |= off[i]; i++; }
            if (timeMs[i - 1] - timeMs[s] >= o.MinOffSec * 1000) runs.Add((s, i - 1, bits));
        }

        var counted = new bool[n];
        foreach (var r in runs)
            for (int j = r.Start; j <= r.End; j++) counted[j] = true;

        var list = new List<SurfaceExcursion>();
        foreach (var r in runs)
        {
            // 0.3 秒以内に続く逸脱は前のものにまとめる
            if (list.Count > 0 && timeMs[r.Start] - timeMs[list[^1].EndIndex] <= o.MergeSec * 1000)
            {
                var last = list[^1];
                list[^1] = Build(last.StartIndex, r.End, WheelBits(last.Wheels) | r.Bits);
                continue;
            }
            if (PavedBefore(r.Start)) list.Add(Build(r.Start, r.End, r.Bits));
        }
        return list;

        SurfaceExcursion Build(int start, int end, int bits)
        {
            var wheels = Enumerable.Range(0, 4).Where(w => (bits & (1 << w)) != 0).ToArray();
            var surfaces = wheels.Select(w => MostCommonOff(w, start, end)).ToArray();

            // スリップ: 逸脱の 0.3 秒前から 2 秒後まで
            int slipIdx = -1, slipEnd = -1;
            double peak = 0;
            bool gap = false;
            int from = start;
            while (from > 0 && timeMs[start] - timeMs[from - 1] <= o.SlipBeforeSec * 1000 && isRaceOn[from - 1] == 1) from--;
            for (int j = from; j < n && timeMs[j] - timeMs[start] <= o.AfterSec * 1000; j++)
            {
                if (isRaceOn[j] != 1) { gap = j > start; break; }
                if (!double.IsFinite(slip[j])) continue;
                peak = Math.Max(peak, slip[j]);
                if (slip[j] > o.SlipLimit && slipIdx < 0) slipIdx = j;
            }
            if (slipIdx >= 0)
            {
                slipEnd = slipIdx;
                while (slipEnd + 1 < n && isRaceOn[slipEnd + 1] == 1 && slip[slipEnd + 1] > o.SlipLimit) slipEnd++;
            }
            return new SurfaceExcursion(start, end, wheels, surfaces, slipIdx, Math.Round(peak, 2), slipEnd, gap);
        }

        SurfaceKind MostCommonOff(int w, int start, int end)
        {
            var counts = new Dictionary<SurfaceKind, int>();
            for (int j = start; j <= end; j++)
            {
                var k = DerivedSeries.ClassifySurface(surfaceRumble[w][j]);
                if (k is SurfaceKind.Paved or SurfaceKind.Ramp or SurfaceKind.Kerb || rumbleStrip[w][j] != 0) continue;
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }
            return counts.Count == 0 ? SurfaceKind.Unknown : counts.MaxBy(kv => kv.Value).Key;
        }

        // 直前が、対象外の行と逸脱を挟まずに舗装で 0.3 秒以上続いたか(ちらつきは舗装として扱う)
        bool PavedBefore(int start)
        {
            int last = start - 1;
            if (last < 0 || off[last] == Ineligible || counted[last]) return false;
            for (int k = last; k >= 0 && off[k] != Ineligible && !counted[k]; k--)
                if (timeMs[last] - timeMs[k] >= o.PavedBeforeSec * 1000) return true;
            return false;
        }
    }

    private static int WheelBits(int[] wheels) => wheels.Aggregate(0, (b, w) => b | (1 << w));
}
