namespace Fh6.Core;

/// <summary>
/// 周のスタート。Lap はこれから始まる周の番号(2 = 2 周目)。PrevLapSec は終わった周のタイム(分からなければ null)。
/// IsFinish はゴール(周回数が増えた直後にレースが終わった。最後の周の終わり)。
/// </summary>
public readonly record struct LapEvent(int Index, int Lap, double? PrevLapSec, bool FromLapCounter, bool IsFinish = false);

/// <summary>
/// 周回レースで、各周のスタート(スタートラインを越えた瞬間)を見つける。
/// 1. lap_no が増えたら周のスタート。前の周のタイムは直前の cur_lap(docs/telemetry-field-notes.md「ラップ関連フィールド」)。
///    増えてから 2 秒以内にレースが終わったら(停止区間か race_pos が 0)ゴール(ゴール直後に周回数が 1 増える短い断片が出るため)
/// 2. レース中に lap_no が一度も増えない場合(タイムアタックでは周回の値が全部 0): スタート地点から一度 300m 以上離れた後、
///    20m 以内・向き 30 度以内で戻ってきた瞬間。dist_traveled が 0 より大きい間だけ(フリーロームで同じ場所を通っても周にしない)
/// </summary>
public static class LapDetector
{
    public const double AwayM = 300;
    public const double PassM = 20;
    public const double PassYawDeg = 30;
    public const double FinishWithinSec = 2;

    /// <param name="timeMs">行ごとの時刻(ミリ秒、recv_time × 1000)</param>
    /// <param name="starts">StartDetector の結果(レースと発進を位置で見分けるときの基準)</param>
    public static List<LapEvent> Detect(double[] timeMs, double[] isRaceOn, double[] racePos, double[] lapNo, double[] curLap,
                                        double[] distTraveled, double[] x, double[] z, double[] yaw, IReadOnlyList<StartEvent> starts)
    {
        int n = timeMs.Length;
        var list = new List<LapEvent>();

        // 1. lap_no が増えた(レース中。レースのスタートで数え直す。リワインドで戻った後に同じ値へ増えても数えない)
        double maxLap = 0;
        int prev = -1;
        for (int i = 0; i < n; i++)
        {
            if (isRaceOn[i] != 1) continue;
            bool inRace = racePos[i] >= 1;
            if (prev >= 0 && racePos[prev] == 0 && inRace) maxLap = lapNo[i];      // レースのスタート
            if (inRace && prev >= 0 && racePos[prev] >= 1 && lapNo[i] > lapNo[prev] && lapNo[i] > maxLap)
            {
                maxLap = lapNo[i];
                double? t = curLap[prev] > 0 ? Math.Round(curLap[prev], 3) : null;
                list.Add(new LapEvent(i, (int)lapNo[i] + 1, t, true, IsFinish(i)));
            }
            prev = i;
        }

        // 2. lap_no が増えないレース・イベント: スタート地点に戻ってきた瞬間
        foreach (var s in starts)
        {
            if (s.Kind == StartKind.Respawn) continue;
            int end = EventEnd(s.Index);
            if (list.Any(l => l.FromLapCounter && l.Index > s.Index && l.Index <= end)) continue;

            double sx = x[s.Index], sz = z[s.Index], syaw = yaw[s.Index];
            bool away = false;
            int best = -1, lap = 1;
            double bestD = double.MaxValue, lastPassMs = timeMs[s.Index];
            for (int i = s.Index + 1; i <= end; i++)
            {
                if (isRaceOn[i] != 1) continue;
                double d = Math.Sqrt((x[i] - sx) * (x[i] - sx) + (z[i] - sz) * (z[i] - sz));
                if (d > AwayM)
                {
                    if (best >= 0) Pass();
                    away = true;
                    continue;
                }
                double dyaw = Math.Abs((yaw[i] - syaw) * 180 / Math.PI % 360);
                if (dyaw > 180) dyaw = 360 - dyaw;
                if (away && d < PassM && dyaw < PassYawDeg && d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) Pass();

            void Pass()
            {
                lap++;
                list.Add(new LapEvent(best, lap, Math.Round((timeMs[best] - lastPassMs) / 1000, 3), false));
                lastPassMs = timeMs[best];
                best = -1;
                bestD = double.MaxValue;
                away = false;
            }
        }

        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;

        bool IsFinish(int from)
        {
            for (int j = from + 1; j < n && timeMs[j] - timeMs[from] <= FinishWithinSec * 1000; j++)
                if (isRaceOn[j] != 1 || racePos[j] == 0) return true;
            return false;
        }

        // スタートからイベントが続く範囲の終わり(dist_traveled が 0 に戻る、または次のスタートの手前まで)
        int EventEnd(int from)
        {
            int next = starts.Where(st => st.Index > from && st.Kind != StartKind.Respawn).Select(st => st.Index).DefaultIfEmpty(n).First();
            int end = from;
            bool moved = false;
            for (int i = from + 1; i < next; i++)
            {
                if (isRaceOn[i] != 1) continue;
                if (distTraveled[i] > 0) moved = true;
                else if (moved) break;   // イベントが終わった(フリーロームは dist_traveled が 0)
                end = i;
            }
            return moved ? end : from;
        }
    }
}
