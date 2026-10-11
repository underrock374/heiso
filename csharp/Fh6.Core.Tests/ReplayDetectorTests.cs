using System.Globalization;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class ReplayDetectorTests
{
    [Fact]
    public void blackdetectの出力から黒の区間を読む()
    {
        var stderr = """
        [blackdetect @ 000001] black_start:3.96667 black_end:8.1 black_duration:4.13333
        frame= 100 fps=0.0 q=-0.0 size=N/A
        [blackdetect @ 000001] black_start:264.2 black_end:278.3 black_duration:14.1
        """;
        var b = ReplayDetector.ParseBlackDetect(stderr);
        Assert.Equal(2, b.Count);
        Assert.Equal((3.96667, 8.1), b[0]);
        Assert.Equal(264.2, b[1].Start, 6);
    }

    [Fact]
    public void ゴール直後の短い黒は外し_リプレイの窓がリザルトの後に収まる最初の黒をとる()
    {
        // AD: ゴール 243.5 秒、T = 231.26。246.7 の黒はゴール直後(e − T がゴールより前)、507.83 がリプレイの終わり
        var blacks = new[] { (246.7, 250.8), (507.83, 521.9), (740.0, 754.0) };
        Assert.Equal(507.83, ReplayDetector.PickBlackStart(blacks, 243.5, 231.26));
        // リプレイの途中で録画を止めた(黒が無い)
        Assert.Null(ReplayDetector.PickBlackStart(new[] { (246.7, 250.8) }, 243.5, 231.26));
    }

    [Fact]
    public void 次のレースのスタートより後の黒はとらない()
    {
        // 164031 の 1 レース目: ゴール 213.1、T 161.87、次のレースのスタート 276.5。480.25 の黒は 2 レース目の映像の中
        var blacks = new[] { (241.0, 241.2), (480.25, 492.0) };
        Assert.Equal(480.25, ReplayDetector.PickBlackStart(blacks, 213.1, 161.87));
        Assert.Null(ReplayDetector.PickBlackStart(blacks, 213.1, 161.87, searchEndVideoSec: 276.5));
    }

    [Fact]
    public void signalstatsの出力からコマの時刻と動きを読む()
    {
        var stdout = """
        frame:0    pts:1      pts_time:0.1
        lavfi.signalstats.YAVG=3.25
        frame:1    pts:2      pts_time:0.2
        lavfi.signalstats.YAVG=12
        """;
        var m = ReplayDetector.ParseSignalStats(stdout);
        Assert.Equal(new[] { (0.1, 3.25), (0.2, 12.0) }, m);
    }

    [Fact]
    public void 動きの相関から遅れを見つける()
    {
        // テレメトリーの信号: レースタイム 0〜60 秒の不規則な波。動画は先頭が 100.4 秒(候補の 100 より 0.4 秒遅い)
        var rnd = new Random(1);
        var time = Enumerable.Range(0, 60 * 30).Select(i => i / 30.0).ToArray();
        var knots = Enumerable.Range(0, 61).Select(_ => rnd.NextDouble()).ToArray();
        var signal = time.Select(t => knots[(int)t] + (knots[Math.Min(60, (int)t + 1)] - knots[(int)t]) * (t - (int)t)).ToArray();
        var motion = Enumerable.Range(0, 70 * 10).Select(k => 95 + k / 10.0)
            .Select(v => (v, v - 100.4 is var r && r >= 0 && r <= 59.9 ? signal[(int)Math.Round(r * 30)] * 20 + rnd.NextDouble() : rnd.NextDouble())).ToList();

        var (corr, lag) = ReplayDetector.BestLag(motion, time, signal, 100);
        Assert.True(corr > 0.9, $"corr {corr}");
        Assert.Equal(0.4, lag!.Value, 1);
    }

    [Fact]
    public void 検算が通れば確度は高く_通らなければ黒だけで決めて低い()
    {
        var high = ReplayDetector.Decide(507.83, 231.26, 0.49, -0.3);
        Assert.Equal(("auto_black_motion", "high"), (high.Method, high.Confidence));
        Assert.Equal(507.83 - 231.26, high.ZeroVideoSec, 3);   // 先頭は黒で決める(遅れでは直さない)
        Assert.Equal(-0.3, high.LagSec);

        var weak = ReplayDetector.Decide(507.83, 231.26, 0.12, 0.1);
        Assert.Equal(("auto_black", "low"), (weak.Method, weak.Confidence));
        Assert.Equal(507.83 - 231.26, weak.ZeroVideoSec, 3);

        var far = ReplayDetector.Decide(507.83, 231.26, 0.6, 2.5);   // 遅れが大きすぎる
        Assert.Equal("low", far.Confidence);
        Assert.Null(ReplayDetector.Decide(507.83, 231.26, null, null).Corr);
    }
}

/// <summary>同じコースを何本も走り、最後にリプレイを録った記録(164031)と ffmpeg があるときだけ動かす</summary>
public sealed class MultiRaceReplayLogFactAttribute : FactAttribute
{
    public const string Folder = "20261011_164031";

    public MultiRaceReplayLogFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName);
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d) || ReplayLogsFactAttribute.Find(d, Folder) == null)
            Skip = $"{RealLogsFactAttribute.EnvName} のフォルダに、記録 {Folder} があるときだけ動かす";
        else Skip = new FfmpegFactAttribute().Skip;
    }
}

/// <summary>リプレイの実記録と ffmpeg の両方があるときだけ動かす</summary>
public sealed class ReplayLogsWithFfmpegFactAttribute : FactAttribute
{
    public ReplayLogsWithFfmpegFactAttribute()
    {
        Skip = new ReplayLogsFactAttribute().Skip ?? new FfmpegFactAttribute().Skip;
    }
}

public class ReplayDetectorRealLogTests
{
    [ReplayLogsWithFfmpegFact]
    public void 実記録でリプレイの位置を見つける()
    {
        var root = Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName)!;
        var ffmpeg = Environment.GetEnvironmentVariable(FfmpegFactAttribute.EnvName)!;
        // 作業指示 008 §7: 先頭 ±0.5 秒、終わり ±0.1 秒、相関 0.30 以上、確度「高」
        foreach (var (prefix, zero, end) in new[] { ("20261010_213641", 276.57, 507.83), ("20261010_214545", 393.84, 630.97) })
        {
            var folder = ReplayLogsFactAttribute.Find(root, prefix)!;
            var meta = SessionMeta.Load(folder);
            var t = TelemetryTable.Load(Path.Combine(folder, SessionWriter.CsvName),
                RaceSample.Columns.Concat(RaceTimeline.Columns).Concat(ReplayDetector.Columns).Distinct());
            var race = RaceSegmentDetector.Detect(t).First(s => s.LikelyFinished);
            var timeline = RaceTimeline.Build(t, race);
            var map = VideoTimeMap.Create(meta.Video, VideoSyncMethod.StartedEvent, out _)!;
            double finish = map.ToVideoSec(t[TelemetryTable.RecvTime][race.FinishIndex]);

            var r = ReplayDetector.Detect(ffmpeg, meta.FindVideo(folder)!, t, timeline, finish);
            Assert.NotNull(r);
            var msg = string.Create(CultureInfo.InvariantCulture, $"{prefix}: zero {r!.ZeroVideoSec} end {r.EndVideoSec} corr {r.Corr} lag {r.LagSec} {r.Confidence}");
            Assert.True(Math.Abs(r.ZeroVideoSec - zero) <= 0.5, msg);
            Assert.True(Math.Abs(r.EndVideoSec - end) <= 0.1, msg);
            Assert.True(r.Corr >= 0.30, msg);
            Assert.True(r.Confidence == "high", msg);
        }
    }

    [MultiRaceReplayLogFact]
    public void 何本も走った記録で_リプレイは最後のレースの直後だけに見つける()
    {
        // 164031: 同じコースを 3 本(すべて完走)。リプレイは 3 本目の後にだけ録った。1・2 本目の後を探すと、次のレースの映像の黒をとり違えていた
        var ffmpeg = Environment.GetEnvironmentVariable(FfmpegFactAttribute.EnvName)!;
        var folder = ReplayLogsFactAttribute.Find(Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName)!, MultiRaceReplayLogFactAttribute.Folder)!;
        var meta = SessionMeta.Load(folder);
        var t = TelemetryTable.Load(Path.Combine(folder, SessionWriter.CsvName),
            RaceSample.Columns.Concat(RaceTimeline.Columns).Concat(ReplayDetector.Columns).Distinct());
        var races = RaceSegmentDetector.Detect(t);
        Assert.Equal(3, races.Count);
        Assert.All(races, r => Assert.True(r.LikelyFinished));
        var map = VideoTimeMap.Create(meta.Video, VideoSyncMethod.StartedEvent, out _)!;
        var recv = t[TelemetryTable.RecvTime];
        var found = new List<ReplayDetector.Result?>();
        for (int k = 0; k < races.Count; k++)
        {
            double? end = k + 1 < races.Count ? map.ToVideoSec(races[k + 1].StartRecvTime) : null;
            found.Add(ReplayDetector.Detect(ffmpeg, meta.FindVideo(folder)!, t, RaceTimeline.Build(t, races[k]),
                map.ToVideoSec(recv[races[k].FinishIndex]), searchEndVideoSec: end));
        }
        Assert.Null(found[0]);
        Assert.Null(found[1]);
        var r3 = Assert.IsType<ReplayDetector.Result>(found[2]);
        Assert.Equal(764.42, r3.ZeroVideoSec, 0.5);
        Assert.Equal("high", r3.Confidence);
    }
}
