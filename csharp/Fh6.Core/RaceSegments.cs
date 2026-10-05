namespace Fh6.Core;

/// <summary>
/// 記録済みの CSV の中のレース1本分の区間。
/// StartIndex はレース開始の行、EndIndex はレース中(順位 1 以上)の最後の行。
/// </summary>
public sealed record RaceSegment
{
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public double StartRecvTime { get; init; }
    public double EndRecvTime { get; init; }
    /// <summary>2通り目(停止区間の明けに経過時間が 0 に戻った)で見つけたレース開始</summary>
    public bool Restart { get; init; }
    /// <summary>区間の終わり方: race_end(順位が 0 に戻った)/ next_start(次のレースが始まった)/ log_end(記録の終わり)</summary>
    public string EndReason { get; init; } = "";
    /// <summary>レースを終えた瞬間の行(ゴール直後の断片を除いた、レース中の最後の行)。レースタイムはこの行の cur_race_time</summary>
    public int FinishIndex { get; init; }
    /// <summary>レースを終えた瞬間(FinishIndex の行)の速度</summary>
    public double EndSpeedKmh { get; init; }
    /// <summary>レースを終えた直後の停止区間の長さ(秒)。停止区間に入らずに終わったら 0</summary>
    public double GapAfterSec { get; init; }
    /// <summary>停止区間のまま記録が終わった(GapAfterSec はそこまでの長さで、本当はもっと長い)</summary>
    public bool GapReachesLogEnd { get; init; }
    /// <summary>完走らしいか(推定。決め方は RaceSegmentDetector と FinishedBy)</summary>
    public bool LikelyFinished { get; init; }
    /// <summary>
    /// 完走・中断を何で決めたか: "lap"(終わる直前に周回数が増えた)/ "results"(直後の停止区間でレースの時計が進んだ = リザルト画面)/
    /// "menu"(直後の停止区間で時計が止まっていた = メニューから中断)/ "log_end"(停止区間のまま記録が終わった)/ "speed"(終えた瞬間の速度と停止区間の長さ)
    /// </summary>
    public string FinishedBy { get; init; } = "";
    /// <summary>レースの開始の位置と向き(コースの照合に使う)</summary>
    public double StartX { get; init; }
    public double StartZ { get; init; }
    public double StartYaw { get; init; }
}

/// <summary>
/// 記録済みの CSV から、RaceTracker の判定でレース区間の一覧を作る。
/// 完走か中断かは次の順で決める(2026-09-28、実データ 16 本で利用者の答えと一致。2026-09-30 に 1 と 2・3 の順を入れ替え):
/// 1. 直後の停止区間の明けに順位 0 で、レースの時計が停止区間の半分以上進んでいた → 完走(リザルト画面の間は時計が進む)
/// 2. 同じく時計が停止区間の半分未満しか進んでいなかった → 中断(ポーズメニューの間は時計がほぼ止まる。開くまでに 1 秒ほど進むことがある)
/// 3. 終わる直前(2 秒以内)に周回数(lap_no)が増えた → 完走(ゴールで周回数が増える種類のレース)。
///    1・2 より後に見る(周回レースで次の周に入った直後にメニューからやめても、周回数が増えた直後に終わるため。091446)
/// 4. 停止区間のまま記録が終わった → 完走(リザルト画面で録画を止めた)
/// 5. それ以外(停止区間の後すぐ次のレースが始まり、時計が 0 に戻った): 終えた瞬間が 150 km/h 以上で、停止区間が 12 秒以上なら完走。
///    中断はメニューを開くため減速することが多い。ただしゴールの速度はコースによって 69 km/h のこともあり(211710)、速度だけでは決めない
/// </summary>
public static class RaceSegmentDetector
{
    public const double FinishMinKmh = 150;
    public const double FinishMinGapSec = 12;
    /// <summary>周回数が増えてからこの秒数以内にレースが終わったらゴール(LapDetector.FinishWithinSec と同じ)</summary>
    public const double LapFinishWithinSec = 2;
    /// <summary>リザルト画面: 停止区間の間に時計がその長さのこの割合以上進んだ</summary>
    public const double ResultsClockRatio = 0.5;
    /// <summary>メニュー: 停止区間の間の時計の進みがこれ未満</summary>
    public const double MenuClockMaxSec = 0.5;
    /// <summary>停止区間の後のレース中の続きがこれより短ければ、ゴール直後の断片として区間の終わりの判断から外す</summary>
    public const double FragmentMaxSec = 0.5;

    public static List<RaceSegment> Detect(TelemetryTable t) => Detect(t, RaceTracker.Replay(t));

    public static List<RaceSegment> Detect(TelemetryTable t, List<(int Index, RaceTrackerEvent Event)> events)
    {
        var recv = t[TelemetryTable.RecvTime];
        var on = t["is_race_on"];
        var pos = t["race_pos"];
        var speed = t["speed"];
        var raceTime = t["cur_race_time"];
        var lap = t.Contains("lap_no") ? t["lap_no"] : null;   // 読んでいなければ 1. の判定は使わない
        int n = t.RowCount;
        bool Racing(int i) => on[i] == 1 && pos[i] >= 1;

        var result = new List<RaceSegment>();
        for (int k = 0; k < events.Count; k++)
        {
            if (events[k].Event is not RaceStarted rs) continue;
            int start = events[k].Index;

            // 区間の終わり: 次のレース終了か次のレース開始の手前。無ければ記録の終わり
            int stop = n;
            string reason = "log_end";
            for (int j = k + 1; j < events.Count; j++)
            {
                if (events[j].Event is RaceEnded) { stop = events[j].Index; reason = "race_end"; break; }
                if (events[j].Event is RaceStarted) { stop = events[j].Index; reason = "next_start"; break; }
            }

            int end = LastRacing(start, stop);
            if (end < 0) continue;
            int finish = WithoutFragment(start, end);

            // 終えた直後の停止区間
            double gap = 0;
            bool gapToEnd = false;
            int resume = -1;   // 停止区間の明けの最初の行
            if (finish + 1 < n && on[finish + 1] != 1)
            {
                int j = finish + 1;
                while (j < n && on[j] != 1) j++;
                gapToEnd = j >= n;
                gap = (gapToEnd ? recv[n - 1] : recv[j]) - recv[finish];
                if (!gapToEnd) resume = j;
            }
            var kmh = Math.Round(speed[finish] * 3.6, 1);
            var (finished, by) = Finished(start, end, finish, resume, gap, gapToEnd, kmh);

            var seg = new RaceSegment
            {
                StartIndex = start, EndIndex = end, StartRecvTime = recv[start], EndRecvTime = recv[end],
                Restart = rs.Restart, EndReason = reason, FinishIndex = finish, EndSpeedKmh = kmh,
                GapAfterSec = Math.Round(gap, 2), GapReachesLogEnd = gapToEnd,
                LikelyFinished = finished, FinishedBy = by,
                StartX = rs.X, StartZ = rs.Z, StartYaw = rs.Yaw,
            };

            // 2通り目の検出で見つけた短い区間が、ゴール直後の断片だったら直前の区間にまとめる
            if (result.Count > 0 && rs.Restart && recv[end] - recv[start] < FragmentMaxSec)
            {
                result[^1] = result[^1] with { EndIndex = end, EndRecvTime = recv[end], EndReason = reason };
                continue;
            }
            result.Add(seg);
        }
        return result;

        (bool, string) Finished(int start, int end, int finish, int resume, double gap, bool gapToEnd, double kmh)
        {
            // 停止区間の明けに順位 0 のまま: 時計が進んでいればリザルト画面、ほとんど進んでいなければメニュー。
            // 周回数より先に見る(周回レースで次の周に入った直後にメニューからやめると、周回数が増えた直後に終わるため。091446)
            if (resume >= 0 && pos[resume] == 0)
            {
                double advance = raceTime[resume] - raceTime[finish];
                if (advance >= ResultsClockRatio * gap && advance > MenuClockMaxSec) return (true, "results");
                // メニューでも開くまでの間などに時計が少し進む(091446 は 8.4 秒の間に 1.05 秒)。リザルト画面ほど進んでいなければメニュー
                if (advance > -0.05) return (false, "menu");
            }
            // 1. 終わる直前に周回数が増えた。ゴール直後の断片(リザルト画面の停止区間の後に一瞬出るレース中の行)で増えることが多いので、
            //    停止区間をまたいで直前のレース中の行と比べる
            if (lap != null)
                for (int i = end; i > start && recv[end] - recv[i] <= LapFinishWithinSec; i--)
                {
                    if (!Racing(i)) continue;
                    int p = i - 1;
                    while (p >= start && !Racing(p)) p--;
                    if (p >= start && lap[i] > lap[p]) return (true, "lap");
                }
            // 4. 停止区間のまま記録が終わった
            if (gapToEnd) return (true, "log_end");
            // 5. 速度と停止区間の長さ
            return (kmh >= FinishMinKmh && gap >= FinishMinGapSec, "speed");
        }

        int LastRacing(int from, int stopExclusive)
        {
            for (int i = stopExclusive - 1; i >= from; i--)
                if (Racing(i)) return i;
            return -1;
        }

        // 最後の「停止区間の後のレース中の続き」が FragmentMaxSec 未満なら、その手前のレース中の最後の行
        int WithoutFragment(int from, int end)
        {
            int runStart = end;
            while (runStart - 1 >= from && Racing(runStart - 1)) runStart--;
            if (runStart == from || recv[end] - recv[runStart] >= FragmentMaxSec) return end;
            if (on[runStart - 1] == 1) return end;   // 停止区間を挟んでいない
            int prev = LastRacing(from, runStart);
            return prev >= 0 ? prev : end;
        }
    }
}

/// <summary>session.json の出来事から、レース区間のコース名と車名を取る</summary>
public static class RaceNames
{
    /// <summary>同じ行の出来事とみなす時刻の差(秒)。どちらも同じパケットの recv_time なので、丸めの差だけ</summary>
    public const double SameRowSec = 0.01;

    /// <summary>
    /// レース開始(startRecvTime)のコース名。後のものほど優先する: 記録中に一致した race_start、
    /// そのレースについて画面で登録・選択した course_confirmed、後から直した race_edited。無ければ null
    /// </summary>
    public static string? Course(IEnumerable<SessionEvent> events, double startRecvTime)
    {
        string? name = null;
        foreach (var e in events)
        {
            if (e.Data == null) continue;
            if (e.Type == "race_start" && Near(e.RecvTime, startRecvTime))
                name = Str(e.Data["course_name"]) ?? name;
            else if (e.Type is "course_confirmed" or "race_edited" && Num(e.Data["race_start_recv_time"]) is double t && Near(t, startRecvTime))
                name = Str(e.Data["course_name"]) ?? name;
        }
        return name;
    }

    /// <summary>
    /// レースの車名とセッティング名。レース開始の時点で検知していた車両(car_detected)の名前に、
    /// その車両を確定した car_confirmed(レースの後でも、次に車両が変わるか次のレースが始まるまで)、
    /// 後から直した race_edited を重ねる。無ければ (null, null)
    /// </summary>
    public static (string? Car, string? Setup) Car(IEnumerable<SessionEvent> events, double startRecvTime)
    {
        int? ordinal = null;
        string? car = null, setup = null;
        (string? Car, string? Setup)? edited = null;
        bool closed = false;   // レースの後に車両が変わったか、次のレースが始まった
        foreach (var e in events)
        {
            if (e.Data == null) continue;
            var d = e.Data;
            if (e.Type == "race_edited")
            {
                if (Num(d["race_start_recv_time"]) is double t && Near(t, startRecvTime))
                    edited = (Str(d["car_name"]) ?? edited?.Car, Str(d["setup_name"]) ?? edited?.Setup);
                continue;
            }
            bool beforeStart = e.RecvTime <= startRecvTime + SameRowSec;
            if (!beforeStart && e.Type is "car_detected" or "race_start") closed = true;
            if (closed) continue;
            if (e.Type == "car_detected" && beforeStart)
            {
                ordinal = (int?)Num(d["ordinal"]);
                car = Str(d["name"]);
                setup = Str(d["setup_name"]);
            }
            else if (e.Type == "car_confirmed" && (beforeStart || (ordinal != null && (int?)Num(d["ordinal"]) == ordinal)))
            {
                ordinal = (int?)Num(d["ordinal"]) ?? ordinal;
                car = Str(d["name"]) ?? car;
                setup = Str(d["setup_name"]) ?? setup;
            }
        }
        return edited is var (ec, es) ? (ec ?? car, es ?? setup) : (car, setup);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < SameRowSec;

    private static string? Str(System.Text.Json.Nodes.JsonNode? n) =>
        n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static double? Num(System.Text.Json.Nodes.JsonNode? n) =>
        n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
