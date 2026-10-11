using System.Globalization;
using System.Text;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class RaceTimelineTests
{
    /// <summary>走行の一続き(ブロック)と停止区間を並べて CSV を作る。60 行/秒</summary>
    private sealed class Csv
    {
        private static readonly string[] Columns = RaceTimeline.Columns;
        private readonly StringBuilder _sb = new(string.Join(",", Columns) + "\n");
        private double _t = 1_790_000_000;
        public int Rows { get; private set; }

        /// <summary>レースタイム raceTime から sec 秒走る</summary>
        public Csv Run(double raceTime, double sec, int pos = 3)
        {
            int n = (int)Math.Round(sec * 60);
            for (int i = 0; i <= n; i++) Line(1, pos, raceTime + i / 60.0);
            return this;
        }

        /// <summary>停止区間(リワインドの画面など)</summary>
        public Csv Stop(double sec)
        {
            int n = Math.Max(1, (int)Math.Round(sec * 60));
            for (int i = 0; i < n; i++) Line(0, 0, 0);
            return this;
        }

        private void Line(int on, int pos, double rt)
        {
            _t += 1 / 60.0;
            _sb.AppendLine(string.Join(",", F(_t), on, pos, F(rt)));
            Rows++;
        }

        private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        public TelemetryTable Table() => TelemetryTable.Load(new StringReader(_sb.ToString()), Columns);
    }

    private static RaceSegment Race(int start, int finish) => new() { StartIndex = start, EndIndex = finish, FinishIndex = finish };

    [Fact]
    public void 一続きで走り切れば_その行がそのまま時間軸()
    {
        var csv = new Csv().Run(0, 30);
        var tl = RaceTimeline.Build(csv.Table(), Race(0, csv.Rows - 1));
        Assert.Equal(30, tl.DurationSec, 3);
        Assert.Equal(1, tl.Attempts);
        Assert.Empty(tl.Gaps);
        Assert.Equal(csv.Rows, tl.Rows.Count);
    }

    [Fact]
    public void リワインドした後の試行で上書きしてつなぐ()
    {
        // 0〜20 秒を走り、リワインドで 12 秒へ戻って 30 秒まで。12 秒以上の最初の試行の行は捨てる
        var csv = new Csv().Run(0, 20).Stop(3).Run(12, 18);
        var tl = RaceTimeline.Build(csv.Table(), Race(0, csv.Rows - 1));
        Assert.Equal(30, tl.DurationSec, 3);
        Assert.Equal(2, tl.Attempts);
        Assert.Empty(tl.Gaps);
        Assert.True(tl.RaceTime.Zip(tl.RaceTime.Skip(1)).All(p => p.First < p.Second));
        // 12 秒より前は最初の試行、12 秒からは 2 回目の試行の行(停止区間の後)
        int at12 = tl.IndexAtRaceTime(12);
        Assert.True(tl.Rows[at12] > 20 * 60 + 3 * 60);
        Assert.True(tl.Rows[at12 - 1] < 20 * 60);
    }

    [Fact]
    public void 前のリワインドより手前に戻っても_最後のリスタートでも_最後の試行が勝つ()
    {
        // 0〜20、15 へ戻って〜25、5 へ戻って〜40、リスタート(0 へ)して〜50
        var csv = new Csv().Run(0, 20).Stop(2).Run(15, 10).Stop(2).Run(5, 35).Stop(2).Run(0, 50);
        var tl = RaceTimeline.Build(csv.Table(), Race(0, csv.Rows - 1));
        Assert.Equal(50, tl.DurationSec, 3);
        Assert.Equal(1, tl.Attempts);   // リスタートで、それより前は全部上書きされた
        Assert.Empty(tl.Gaps);
    }

    [Fact]
    public void 時計が飛んだ所はすき間として記録する()
    {
        var csv = new Csv().Run(0, 10).Stop(2).Run(12, 8);
        var tl = RaceTimeline.Build(csv.Table(), Race(0, csv.Rows - 1));
        var gap = Assert.Single(tl.Gaps);
        Assert.Equal(10, gap.From, 3);
        Assert.Equal(12, gap.To, 3);
    }

    [Fact]
    public void レース区間の外の行は使わない()
    {
        // 先頭に前のレースの残り(レースタイム 500 秒、1 秒未満)、末尾にゴール直後の断片
        var csv = new Csv().Run(500, 0.2).Stop(5);
        int start = csv.Rows;
        csv.Run(0, 30);
        int finish = csv.Rows - 1;
        csv.Stop(10).Run(40, 0.1);
        var tl = RaceTimeline.Build(csv.Table(), Race(start, finish));
        Assert.Equal(30, tl.DurationSec, 3);
        Assert.Equal(0, tl.RaceTime[0], 3);
        Assert.Equal(start, tl.Rows[0]);
    }

    [Fact]
    public void 仮想の時刻は連続して増え_レースタイムで位置を引ける()
    {
        var csv = new Csv().Run(0, 20).Stop(3).Run(12, 18);
        var tl = RaceTimeline.Build(csv.Table(), Race(0, csv.Rows - 1));
        var v = tl.Retime(1000);
        Assert.Equal(1000, v[0], 6);
        Assert.Equal(1030, v[^1], 3);
        Assert.True(v.Zip(v.Skip(1)).All(p => p.Second > p.First));
        Assert.Equal(0, tl.IndexAtRaceTime(-5));
        Assert.Equal(tl.Rows.Count - 1, tl.IndexAtRaceTime(999));
        Assert.Equal(25, tl.RaceTime[tl.IndexAtRaceTime(25.004)], 2);
    }

    [Fact]
    public void 仮想セッションの表は_つないだ行だけで_recv_timeを振り直す()
    {
        var csv = new Csv().Run(0, 20).Stop(3).Run(12, 18);
        var t = csv.Table();
        var tl = RaceTimeline.Build(t, Race(0, csv.Rows - 1));
        var v = tl.VirtualTable(t, 1000);
        Assert.Equal(tl.Rows.Count, v.RowCount);
        Assert.Equal(tl.Retime(1000), v[TelemetryTable.RecvTime]);
        Assert.Equal(tl.RaceTime, v["cur_race_time"]);
        Assert.All(v["is_race_on"], x => Assert.Equal(1, x));
        // 元の表は変わらない
        Assert.True(t[TelemetryTable.RecvTime][0] > 1_000_000_000);
    }
}

/// <summary>リプレイを録った実記録(2026-10-10)。HEISO_TEST_LOGS のフォルダにその記録があるときだけ動かす</summary>
public sealed class ReplayLogsFactAttribute : FactAttribute
{
    public static readonly string[] Folders = { "20261010_213641", "20261010_214545" };

    public ReplayLogsFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName);
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d) || Folders.Any(f => Find(d, f) == null))
            Skip = $"{RealLogsFactAttribute.EnvName} のフォルダに、リプレイを録った記録({string.Join("、", Folders)})があるときだけ動かす";
    }

    public static string? Find(string root, string prefix) =>
        Directory.GetDirectories(root).FirstOrDefault(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal));
}

public class RaceTimelineRealLogTests
{
    [ReplayLogsFact]
    public void 実記録で最終の時間軸の長さがレースタイムと一致する()
    {
        var root = Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName)!;
        // AD: 自動運転・リワインドなし。MD: 手動・リワインド 7 回(試行は 8 つ、うち 1 つは次のリワインドで全部上書きされ、つないだのは 7 つ)
        foreach (var (prefix, expectedT, attempts) in new[] { ("20261010_213641", 231.26, 1), ("20261010_214545", 237.13, 7) })
        {
            var folder = ReplayLogsFactAttribute.Find(root, prefix)!;
            var t = TelemetryTable.Load(Path.Combine(folder, SessionWriter.CsvName), RaceSample.Columns.Concat(RaceTimeline.Columns).Distinct());
            var race = RaceSegmentDetector.Detect(t).First(s => s.LikelyFinished);
            var tl = RaceTimeline.Build(t, race);
            Assert.True(Math.Abs(tl.DurationSec - expectedT) <= 0.1, $"{prefix}: T = {tl.DurationSec}");
            Assert.Equal(0, tl.RaceTime[0], 1);
            Assert.Empty(tl.Gaps);
            Assert.True(attempts == tl.Attempts, $"{prefix}: つないだ試行 {tl.Attempts}");
        }
    }
}
