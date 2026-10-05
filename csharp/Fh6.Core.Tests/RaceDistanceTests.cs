using Fh6.Core;

namespace Fh6.Core.Tests;

public class RaceDistanceTests
{
    /// <summary>行を足していく(x, z と、走行中か)</summary>
    private sealed class Rows
    {
        public readonly List<double> On = new(), X = new(), Z = new();

        public Rows Drive(double x0, double z0, double x1, double z1, int steps)
        {
            for (int i = 1; i <= steps; i++)
                Add(x0 + (x1 - x0) * i / steps, z0 + (z1 - z0) * i / steps);
            return this;
        }

        public Rows Add(double x, double z, bool on = true)
        {
            On.Add(on ? 1 : 0); X.Add(on ? x : 0); Z.Add(on ? z : 0);
            return this;
        }

        public Rows Off(int count = 3)
        {
            for (int i = 0; i < count; i++) Add(0, 0, on: false);
            return this;
        }

        public (double[] D, bool[] K) Compute() =>
            RaceDistance.Compute(On.ToArray(), X.ToArray(), Z.ToArray(), 0, On.Count - 1);
    }

    [Fact]
    public void 直進で距離が合う()
    {
        var (d, k) = new Rows().Add(0, 0).Drive(0, 0, 300, 400, 100).Compute();
        Assert.Equal(0, d[0]);
        Assert.Equal(500, d[^1], 6);
        Assert.All(k, Assert.True);
    }

    [Fact]
    public void 一時停止で同じ所に戻ったら何も捨てない()
    {
        var (d, k) = new Rows().Add(0, 0).Drive(0, 0, 0, 300, 30).Off().Add(0, 300.5).Drive(0, 300.5, 0, 600, 30).Compute();
        Assert.Equal(600, d[^1], 6);
        Assert.Equal(d.Count(double.IsFinite), k.Count(x => x));
    }

    [Fact]
    public void リワインドで戻った先から数え直し_その後の行を捨てる()
    {
        // 0 → 400m 走ってリワインド、150m の所に戻ってまた 600m まで
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 400, 40).Off();
        int rewindRow = r.On.Count;
        r.Add(0, 150).Drive(0, 150, 0, 600, 45);
        var (d, k) = r.Compute();

        Assert.Equal(150, d[rewindRow], 6);
        Assert.Equal(600, d[^1], 6);
        // 150m より先の最初の走りは捨て、150m までは残す
        for (int i = 0; i < rewindRow; i++)
        {
            if (double.IsNaN(d[i])) continue;
            Assert.Equal(d[i] <= 150 + 1e-9, k[i]);
        }
        // 残った行の距離は行の順に減らない
        var keptD = d.Where((_, i) => k[i]).ToArray();
        Assert.True(keptD.Zip(keptD.Skip(1)).All(p => p.First <= p.Second));
    }

    [Fact]
    public void 止まっている行も最後まで残った走りに含める()
    {
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 100, 10);
        for (int i = 0; i < 5; i++) r.Add(0, 100);   // 停止
        r.Drive(0, 100, 0, 200, 10);
        var (d, k) = r.Compute();
        Assert.All(k, Assert.True);
        Assert.Equal(200, d[^1], 6);
    }

    [Fact]
    public void やり直し区間は捨てた走りの最初から走り直す行まで()
    {
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 400, 40).Off();
        int rewindRow = r.On.Count;
        r.Add(0, 150).Drive(0, 150, 0, 600, 45);
        var (d, k) = r.Compute();
        var (start, resume) = Assert.Single(RaceDistance.RedoRanges(d, k));
        Assert.Equal(16, start);            // 150m(15 行目)の次の行から
        Assert.Equal(rewindRow, resume);    // リワインドの画面(停止区間)を挟んで、戻った先の行まで

        var (d2, k2) = new Rows().Add(0, 0).Drive(0, 0, 0, 300, 30).Off().Add(0, 300.5).Drive(0, 300.5, 0, 600, 30).Compute();
        Assert.Empty(RaceDistance.RedoRanges(d2, k2));   // 一時停止だけなら無し
    }

    [Fact]
    public void 停止区間なしの位置の飛び_チェックポイント逃しも同じ()
    {
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 500, 50);
        int respawn = r.On.Count;
        r.Add(0, 400).Drive(0, 400, 0, 700, 30);   // 100m 戻った所へ(停止区間なし)
        var (d, k) = r.Compute();
        Assert.Equal(400, d[respawn], 6);
        Assert.Equal(700, d[^1], 6);
        Assert.False(k[respawn - 1]);   // 400m より先の最初の走り
    }

    [Fact]
    public void 道筋から離れた所への復帰は距離を足さず何も捨てない()
    {
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 300, 30).Off();
        int back = r.On.Count;
        r.Add(500, 300).Drive(500, 300, 500, 400, 10);   // 横へ 500m 離れた所
        var (d, k) = r.Compute();
        Assert.Equal(300, d[back], 6);
        Assert.Equal(400, d[^1], 6);
        Assert.True(k[back - 4]);
    }

    [Fact]
    public void 周回で前の周の同じ場所には戻らない()
    {
        // 1 周 2,400m の四角いコースを 2 周。2 周目の終わりの近くで、100m 手前にリワインド
        var r = new Rows().Add(0, 0);
        for (int lap = 0; lap < 2; lap++)
            r.Drive(0, 0, 600, 0, 60).Drive(600, 0, 600, 600, 60).Drive(600, 600, 0, 600, 60).Drive(0, 600, 0, 0, 60);
        r.Off();
        int rewindRow = r.On.Count;
        r.Add(0, 100).Drive(0, 100, 0, 0, 10);
        var (d, _) = r.Compute();
        Assert.Equal(4700, d[rewindRow], 6);   // 1 周目の 2,300m ではなく、2 周目の 4,700m
        Assert.Equal(4800, d[^1], 6);
    }

    [Fact]
    public void コースを外れた周の復帰は前の周の道筋ではなく今の周の近く()
    {
        // 1 周 2,400m を 1 周走り、2 周目は 1 辺目を横に 12m ずれて走る(コース外)。500m の所で 100m 手前(x=400)の道の上へ戻される
        var r = new Rows().Add(0, 0);
        r.Drive(0, 0, 600, 0, 60).Drive(600, 0, 600, 600, 60).Drive(600, 600, 0, 600, 60).Drive(0, 600, 0, 0, 60);
        r.Add(0, 12).Drive(0, 12, 500, 12, 50);
        int respawn = r.On.Count;
        r.Add(400, 0).Drive(400, 0, 600, 0, 20);
        var (d, _) = r.Compute();
        // 1 周目の 400m ではなく 2 周目の約 400m(2 周目は横へ 12m 移った分だけ長い)
        Assert.InRange(d[respawn], 2400 + 400, 2400 + 440);
    }

    [Fact]
    public void ヘアピンの反対側の道よりリワインドで戻った道筋そのもの()
    {
        // 北へ 300m、20m 横へ、南へ 300m(反対側の道は 20m 離れている)。北へ向かう道の 150m の所へリワインド
        var r = new Rows().Add(0, 0).Drive(0, 0, 0, 300, 30).Drive(0, 300, 20, 300, 2).Drive(20, 300, 20, 0, 30).Off();
        int rewind = r.On.Count;
        r.Add(0, 150).Drive(0, 150, 0, 300, 15);
        var (d, _) = r.Compute();
        Assert.Equal(150, d[rewind], 6);
    }
}

public class ManualSyncSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-manual-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Write(bool complete) => File.WriteAllText(Path.Combine(_dir, SessionMeta.FileName), $$$"""
        { "format": "fh6-recorder/1", "complete": {{{(complete ? "true" : "false")}}}, "future": {"a": 1},
          "video": { "manual_sync": [], "someday": 2 }, "events": [ {"recv_time": 1, "type": "race_edited", "data": {"x": 1}} ] }
        """);

    [Fact]
    public void 手動補正の点を足しても知らないキーと出来事は残り_消せる()
    {
        Write(complete: true);
        Assert.Null(SessionMeta.AppendManualSync(_dir, new ManualSyncPoint { RecvTime = 100.5, VideoSec = 12.25, Note = "着地" }));
        Assert.Null(SessionMeta.AppendManualSync(_dir, new ManualSyncPoint { RecvTime = 200, VideoSec = 30 }));
        var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SessionMeta.FileName)))!;
        Assert.Equal(1, (int)j["future"]!["a"]!);
        Assert.Equal(2, (int)j["video"]!["someday"]!);
        Assert.Equal(1, (int)j["events"]![0]!["data"]!["x"]!);
        Assert.Equal(2, j["video"]!["manual_sync"]!.AsArray().Count);
        Assert.Equal("着地", (string)j["video"]!["manual_sync"]![0]!["note"]!);

        Assert.Null(SessionMeta.ClearManualSync(_dir));
        Assert.Empty(SessionMeta.Load(_dir).Video.ManualSync);
    }

    [Fact]
    public void 記録中の記録には書かない()
    {
        Write(complete: false);
        Assert.NotNull(SessionMeta.AppendManualSync(_dir, new ManualSyncPoint { RecvTime = 1, VideoSec = 1 }));
        Assert.Empty(SessionMeta.Load(_dir).Video.ManualSync);
    }
}
