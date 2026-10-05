using System.Globalization;
using System.Text;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class RaceSegmentTests
{
    /// <summary>CSV の行を組み立てて TelemetryTable にする</summary>
    private sealed class Csv
    {
        private static readonly string[] Columns = RaceSample.Columns.Append("lap_no").ToArray();
        private readonly StringBuilder _sb = new(string.Join(",", Columns) + "\n");
        private double _t = 1_790_000_000;

        public Csv Add(double sec, bool on = true, int pos = 8, double raceTime = 0, double kmh = 100, bool raceTimeRuns = true, int lap = 0)
        {
            int n = (int)Math.Round(sec * 60);
            for (int i = 0; i < Math.Max(n, 1); i++)
            {
                _t += 1 / 60.0;
                var rt = raceTime + (raceTimeRuns ? i / 60.0 : 0);
                _sb.AppendLine(on
                    ? string.Join(",", F(_t), F(_t * 1000 % 1e9), "1", pos, F(rt), F(kmh / 3.6), "0", "0", "0", "2163", "2", "585", "0", "4", lap)
                    : string.Join(",", F(_t), F(_t * 1000 % 1e9), "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0"));
            }
            return this;
        }

        private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        public TelemetryTable Table() => TelemetryTable.Load(new StringReader(_sb.ToString()), Columns);
    }

    [Fact]
    public void 完走らしい_速いままゴールしてリザルトが長い()
    {
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(1, on: false).Add(60, kmh: 220).Add(20, on: false).Add(1, pos: 0, kmh: 0).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.Equal("race_end", s.EndReason);
        Assert.Equal(220, s.EndSpeedKmh, 0);
        Assert.Equal(20, s.GapAfterSec, 0);
        Assert.True(s.LikelyFinished);
    }

    [Fact]
    public void 中断らしい_減速してメニューを開いた()
    {
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 180).Add(3, kmh: 40).Add(6, on: false).Add(1, pos: 0, kmh: 0).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.False(s.LikelyFinished);
    }

    [Fact]
    public void ゴール直後の断片は終わりの判断から外す()
    {
        // ゴール(220 km/h)→ リザルト 15 秒 → 0.05 秒だけレース中の行(周回数が 1 増えた断片、78 km/h)→ 停止区間で記録の終わり
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 220).Add(15, on: false).Add(0.05, raceTime: 183, kmh: 78).Add(2, on: false).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.Equal(220, s.EndSpeedKmh, 0);
        Assert.Equal(15, s.GapAfterSec, 0);
        Assert.True(s.LikelyFinished);
        Assert.Equal(t.RowCount - 121, s.EndIndex);   // 区間の終わりは断片まで含む
    }

    [Fact]
    public void 断片がリスタートに見えても直前の区間にまとめる()
    {
        // 停止区間の明けに経過時間 0・順位 1 以上で 0.2 秒だけ出る
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 220).Add(15, on: false).Add(0.2, raceTime: 0, kmh: 5).Add(2, on: false).Table();
        var events = RaceTracker.Replay(t);
        Assert.Equal(2, events.Count(e => e.Event is RaceStarted));
        var s = Assert.Single(RaceSegmentDetector.Detect(t, events));
        Assert.True(s.LikelyFinished);
    }

    [Fact]
    public void 続けて2本走ると2区間()
    {
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 220).Add(20, on: false).Add(50, raceTime: 0, kmh: 210).Add(20, on: false).Table();
        var segs = RaceSegmentDetector.Detect(t);
        Assert.Equal(2, segs.Count);
        Assert.Equal("next_start", segs[0].EndReason);
        Assert.True(segs[1].Restart);
        Assert.Equal("log_end", segs[1].EndReason);
        Assert.True(segs[1].GapReachesLogEnd);
        Assert.True(segs.All(s => s.LikelyFinished));
        Assert.Equal(("speed", "log_end"), (segs[0].FinishedBy, segs[1].FinishedBy));
    }

    [Fact]
    public void リザルト画面で時計が進んでいれば遅くても完走()
    {
        // 001930: 146 km/h でゴール → 34.5 秒の停止区間で時計が 30.5 秒進んだ。ここでは 70 km/h、15 秒の間に 14 秒進む
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 70).Add(15, on: false).Add(1, pos: 0, kmh: 0, raceTime: 74).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.True(s.LikelyFinished);
        Assert.Equal("results", s.FinishedBy);
    }

    [Fact]
    public void メニューで時計が止まっていれば速くても中断()
    {
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 220).Add(15, on: false)
            .Add(1, pos: 0, kmh: 0, raceTime: 59.99, raceTimeRuns: false).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.False(s.LikelyFinished);
        Assert.Equal("menu", s.FinishedBy);
    }

    [Fact]
    public void 停止区間のまま記録が終われば遅くても完走()
    {
        // 211710: 69 km/h でゴールして、リザルト画面で記録を止めた
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 69).Add(10, on: false).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.True(s.LikelyFinished);
        Assert.Equal("log_end", s.FinishedBy);
    }

    [Fact]
    public void 次の周に入った直後にメニューからやめたら中断()
    {
        // 091446: 周回数が 1 → 2 に増えた 1.6 秒後にポーズメニューを開き(8.4 秒)、レースをやめた。
        // 明けは順位 0 で、時計は 1.05 秒しか進んでいない(リザルト画面なら半分以上進む)
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 150, lap: 1).Add(1.6, kmh: 139, raceTime: 60, lap: 2).Add(8.4, on: false)
            .Add(1, pos: 0, kmh: 0, raceTime: 62.65, raceTimeRuns: false).Table();
        var s = Assert.Single(RaceSegmentDetector.Detect(t));
        Assert.False(s.LikelyFinished);
        Assert.Equal("menu", s.FinishedBy);
    }

    [Fact]
    public void ゴールで周回数が増えれば完走()
    {
        // 000503 の 2 本目: ゴールで周回数が 0 → 1、すぐ次のレースが始まった(時計が 0 に戻る)
        var t = new Csv().Add(2, pos: 0, kmh: 0).Add(60, kmh: 120).Add(0.3, kmh: 120, raceTime: 60, lap: 1).Add(5, on: false)
            .Add(50, raceTime: 0, kmh: 210).Add(20, on: false).Table();
        var segs = RaceSegmentDetector.Detect(t);
        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].LikelyFinished);
        Assert.Equal("lap", segs[0].FinishedBy);
    }
}

public class RaceNamesTests
{
    private static SessionEvent Ev(double t, string type, string json) =>
        new() { RecvTime = t, Type = type, Data = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject() };

    [Fact]
    public void コース名は一致したレース開始か_後から登録したものか_直したもの()
    {
        var events = new List<SessionEvent>
        {
            new() { RecvTime = 99, Type = "logging_started" },
            Ev(100.000001, "race_start", """{"matched": true, "course_name": "東京鉄道スプリント"}"""),
            Ev(200, "race_start", """{"matched": false, "course_name": null}"""),
            Ev(210, "course_confirmed", """{"course_name": "鳥野山サーキット", "race_start_recv_time": 200.0}"""),
            Ev(300, "race_start", """{"matched": false, "course_name": null}"""),
            Ev(900, "race_edited", """{"course_name": "東京鉄道スプリント(逆)", "race_start_recv_time": 100.0}"""),
        };
        Assert.Equal("東京鉄道スプリント(逆)", RaceNames.Course(events, 100));
        Assert.Equal("鳥野山サーキット", RaceNames.Course(events, 200));
        Assert.Null(RaceNames.Course(events, 300));
        Assert.Null(RaceNames.Course(events, 400));
    }

    [Fact]
    public void 車名はレース開始の時点の車両に_確定と修正を重ねる()
    {
        var events = new List<SessionEvent>
        {
            Ev(10, "car_detected", """{"ordinal": 2163, "name": null, "setup_name": null}"""),
            Ev(100, "race_start", """{"matched": false}"""),
            Ev(260, "car_confirmed", """{"ordinal": 2163, "name": "Civic", "setup_name": "街乗り"}"""),   // レースの後(250 で終了)に確定
            Ev(300, "car_detected", """{"ordinal": 3937, "name": "S600", "setup_name": "雪用"}"""),       // 次のレースの前に乗り換え
            Ev(400, "race_start", """{"matched": false}"""),
            Ev(900, "race_edited", """{"race_start_recv_time": 400.0, "car_name": "Honda S600", "setup_name": null}"""),
        };
        Assert.Equal(("Civic", "街乗り"), RaceNames.Car(events, 100));
        Assert.Equal(("Honda S600", "雪用"), RaceNames.Car(events, 400));
        Assert.Equal((null, null), RaceNames.Car(events, 5));

        // 車両が変わった後の確定は、前のレースには重ねない
        var changed = new List<SessionEvent>
        {
            Ev(10, "car_detected", """{"ordinal": 2163, "name": null}"""),
            Ev(100, "race_start", """{"matched": false}"""),
            Ev(300, "car_detected", """{"ordinal": 3937, "name": null}"""),
            Ev(310, "car_confirmed", """{"ordinal": 2163, "name": "Civic", "setup_name": "街乗り"}"""),
        };
        Assert.Equal((null, null), RaceNames.Car(changed, 100));
    }
}
