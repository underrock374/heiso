namespace Fh6.Core;

/// <summary>
/// レース 1 本の「最終の時間軸」(リプレイの時間軸)。レースタイム(cur_race_time)0 からゴールまでを、
/// リワインド・リスタートの後の試行で上書きしてつないだ行の並び。ゲームのリプレイは、この時間軸を実時間で流す
/// (docs/tasks/008-replay-external-video.md、docs/telemetry-field-notes.md「リプレイ録画」)。
///
/// 使う行はレース区間(RaceSegmentDetector)の StartIndex 〜 FinishIndex の、走行中(is_race_on == 1、race_pos ≥ 1)の行だけ。
/// 前のレースの残りとゴール直後の断片は、レース区間の決め方で外れる(長さでは捨てない。短い正当な試行を落とさないため)。
///
/// 停止区間で区切った一続き(ブロック)を時間順に見て、そのブロックの最初のレースタイム a 以上の行を捨ててから足す。
/// RaceDistance.Kept(位置で「やり直した走り」を決める。距離軸とスキップ区間に使う)とは別の考え方で、
/// チェックポイント逃しの強制復帰(時計は進み、位置だけ戻る)の走りは、時計が進んでいるのでここでは残る。
/// </summary>
public sealed class RaceTimeline
{
    /// <summary>レースタイムでこれより大きな飛びがあれば、すき間として記録する</summary>
    public const double GapSec = 0.5;

    /// <summary>使う列</summary>
    public static readonly string[] Columns = { TelemetryTable.RecvTime, "is_race_on", "race_pos", "cur_race_time" };

    /// <summary>つないだ行の、元の記録での行の番号(レースタイムの順)</summary>
    public IReadOnlyList<int> Rows { get; }

    /// <summary>各行のレースタイム(Rows と同じ順。単調に増える)</summary>
    public IReadOnlyList<double> RaceTime { get; }

    /// <summary>最終の時間軸の長さ T(ゴールの行のレースタイム)</summary>
    public double DurationSec => RaceTime.Count > 0 ? RaceTime[^1] : 0;

    /// <summary>つないだ試行(ブロック)の数。使われずに全部上書きされたブロックは数えない</summary>
    public int Attempts { get; }

    /// <summary>レースタイムで GapSec を超えるすき間(前の行のレースタイム、次の行のレースタイム)</summary>
    public IReadOnlyList<(double From, double To)> Gaps { get; }

    private RaceTimeline(List<int> rows, List<double> raceTime, int attempts, List<(double, double)> gaps)
    {
        Rows = rows;
        RaceTime = raceTime;
        Attempts = attempts;
        Gaps = gaps;
    }

    public static RaceTimeline Build(TelemetryTable t, RaceSegment race)
    {
        var on = t["is_race_on"];
        var pos = t["race_pos"];
        var rt = t["cur_race_time"];
        bool Racing(int i) => on[i] == 1 && pos[i] >= 1 && double.IsFinite(rt[i]);

        // (行の番号, ブロックの番号)
        var rows = new List<int>();
        var blockOf = new List<int>();
        int block = -1;
        bool inBlock = false;
        for (int i = race.StartIndex; i <= race.FinishIndex && i < t.RowCount; i++)
        {
            if (!Racing(i)) { inBlock = false; continue; }
            if (!inBlock)
            {
                // ブロックの始まり: 最初のレースタイム以上の行を捨てる(リワインド・リスタートが、それ以降を上書きする)
                block++;
                inBlock = true;
                double a = rt[i];
                int keep = rows.Count;
                while (keep > 0 && rt[rows[keep - 1]] >= a) keep--;
                rows.RemoveRange(keep, rows.Count - keep);
                blockOf.RemoveRange(keep, blockOf.Count - keep);
            }
            // ブロックの中で時計が戻った行(念のため)は足さない
            if (rows.Count > 0 && rt[i] < rt[rows[^1]]) continue;
            rows.Add(i);
            blockOf.Add(block);
        }

        var raceTime = rows.Select(i => rt[i]).ToList();
        var gaps = new List<(double, double)>();
        for (int k = 1; k < raceTime.Count; k++)
            if (raceTime[k] - raceTime[k - 1] > GapSec) gaps.Add((raceTime[k - 1], raceTime[k]));
        return new RaceTimeline(rows, raceTime, blockOf.Distinct().Count(), gaps);
    }

    /// <summary>
    /// 仮想の連続した時刻に振り直す(仮想セッション)。各行の時刻 = start + (レースタイム − 先頭のレースタイム)。
    /// 単調に増えるので、既存の VideoTimeMap(動画の秒 = 時刻 − 動画 0 秒の時刻)がそのまま使える
    /// </summary>
    public double[] Retime(double start)
    {
        var r = new double[RaceTime.Count];
        if (r.Length == 0) return r;
        double t0 = RaceTime[0];
        for (int k = 0; k < r.Length; k++) r[k] = start + (RaceTime[k] - t0);
        return r;
    }

    /// <summary>
    /// 仮想セッションの表: 最終の時間軸の行だけを並べ、recv_time を Retime(start) に置き換えたもの(全列)。
    /// リプレイの再生で、既存のグラフ・距離軸・コース図の作り方をそのまま使うため
    /// </summary>
    public TelemetryTable VirtualTable(TelemetryTable t, double start) => t.Subset(Rows, Retime(start));

    /// <summary>
    /// そのレースタイムに当たる、つないだ並びの中の位置(レースタイムがそれ以下の最後の行)。範囲の外なら端の行。
    /// ライブ ↔ リプレイの切り替えで、同じ地点に合わせるのに使う
    /// </summary>
    public int IndexAtRaceTime(double raceTime)
    {
        if (RaceTime.Count == 0) return -1;
        int lo = 0, hi = RaceTime.Count - 1;
        if (raceTime <= RaceTime[0]) return 0;
        if (raceTime >= RaceTime[hi]) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (RaceTime[mid] <= raceTime) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
