namespace Fh6.Core;

/// <summary>ジャンプ(空中の区間)。StartIndex〜EndIndex が空中の行。落差は最高点から着地までの標高差(m)</summary>
public readonly record struct AirEvent(int StartIndex, int EndIndex, double DurationSec, double TakeoffKmh, double DropM);

/// <summary>
/// 空中の区間を見つける(docs/telemetry-field-notes.md「空中判定」)。
/// norm_suspension_travel は 0.0 = 伸び切り、1.0 = 縮み切り。4輪とも 0.03 未満が 0.15 秒以上続いたら空中。
/// </summary>
public static class AirDetector
{
    public const double ExtendedBelow = 0.03;
    public const double MinSec = 0.15;

    /// <summary>行ごとに「4輪とも伸び切っている」か(is_race_on == 1 の行だけ)</summary>
    public static bool[] AllWheelsExtended(double[] isRaceOn, double[] suspFl, double[] suspFr, double[] suspRl, double[] suspRr)
    {
        int n = isRaceOn.Length;
        var air = new bool[n];
        for (int i = 0; i < n; i++)
            air[i] = isRaceOn[i] == 1 && suspFl[i] < ExtendedBelow && suspFr[i] < ExtendedBelow
                     && suspRl[i] < ExtendedBelow && suspRr[i] < ExtendedBelow;
        return air;
    }

    /// <param name="timeMs">行ごとの時刻(ミリ秒)。recv_time × 1000 を渡す。timestamp_ms は 60fps 近くでは約 32ms 刻みで、約半分の行が直前と同じ値になるため使わない</param>
    /// <param name="speed">m/s(CSV の speed)</param>
    public static List<AirEvent> Detect(double[] timeMs, bool[] extended, double[] speed, double[] positionY)
    {
        int n = timeMs.Length;
        var list = new List<AirEvent>();
        int start = -1;
        for (int i = 0; i <= n; i++)
        {
            bool air = i < n && extended[i];
            // 時計が戻った・大きく空いたら、そこで区間を切る
            bool continues = start >= 0 && i < n && timeMs[i] - timeMs[i - 1] is > 0 and <= 250;
            if (air && (start < 0 || continues))
            {
                if (start < 0) start = i;
                continue;
            }
            if (start >= 0) Emit(start, i - 1);
            start = air ? i : -1;
        }
        return list;

        void Emit(int start, int end)
        {
            double dur = (timeMs[end] - timeMs[start]) / 1000;
            if (dur < MinSec) return;
            double top = double.MinValue;
            for (int k = start; k <= end; k++) top = Math.Max(top, positionY[k]);
            // 着地の高さは、空中の次の行(接地した行)。記録の終わりなら空中の最後の行
            double landY = end + 1 < n && double.IsFinite(positionY[end + 1]) ? positionY[end + 1] : positionY[end];
            list.Add(new AirEvent(start, end, Math.Round(dur, 2), Math.Round(speed[start] * 3.6, 1), Math.Round(Math.Max(0, top - landY), 1)));
        }
    }
}
