using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 摩擦円・荷重移動の、ならしの幅と選んだレースで変わる値(FrictionDynamic)。期待値 golden/js/friction-dynamic-expected.json は、移す前の画面の JS
/// (frictioncircle.js の ggAt・ltRaw・ltBaseline・ggEstimate・loadSeries・gripSeries と、点群に出す行の選び方)を、
/// 同じ入力 golden/js/friction-dynamic-input.json で動かした結果
/// </summary>
public sealed class FrictionDynamicTests
{
    private static readonly JsonNode Input = Load("friction-dynamic-input.json");
    private static readonly JsonNode Expected = Load("friction-dynamic-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static int?[] Ints(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<int>()).ToArray();
    private static double?[] Doubles(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<double>()).ToArray();

    private static readonly double[] T = Input["t"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
    private static readonly SuspensionSeries Susp = Suspension(Input["susp"]!);
    private static SuspensionSeries Suspension(JsonNode s) => new(Doubles(s["fl"]), Doubles(s["fr"]), Doubles(s["rl"]), Doubles(s["rr"]));

    /// <summary>入力から、計算に使う部分だけの系列(ほかは使わないので null)</summary>
    private static SessionSeries Series()
    {
        var s = Input["surface"]!;
        return new SessionSeries(
            T: T, Speed: Doubles(Input["speed"]), Accel: null!, Brake: null!, Steer: null!, Gear: null!, SlipF: null!, SlipR: null!,
            Balance: null!, Counter: null!, Grade: null!, Elev: null!, X: null!, Z: null!, Turn: null!, GLong: Doubles(Input["gLong"]), GLat: Doubles(Input["gLat"]),
            Susp: Susp, Rpm: null!, OptRpm: null!, OptRpmHi: null!, Gain: null!, Full: null!, PerfCar: null!, Perf: null!, Readout: null!,
            Surface: new SurfaceSeries(Ints(s["fl"]), Ints(s["fr"]), Ints(s["rl"]), Ints(s["rr"])), Course: null!,
            Friction: new FrictionSeries(Ints(Input["group"]).Select(g => g!.Value).ToArray(), null!));
    }

    /// <summary>選ぶ行を 1 つのレースにした印(rows が null なら、レースを選ばない)</summary>
    private static (SessionMarks Marks, int? Race) Marks(JsonNode? rows)
    {
        RaceMark[] races = rows == null ? [] : [new RaceMark(
            T0: 0, T1: 0, Tf: 0, Restart: false, Finished: true, FinishedBy: "", EndKmh: 0, Course: null, Car: null, Setup: null,
            Dist: new RaceDistanceSeries(rows.AsArray().Select(v => v!.GetValue<int>()).ToList(), [], [], []), ElevRange: null, Auto: null, AutoLo: null, AutoHi: null)];
        var marks = new SessionMarks(Starts: [], Contacts: [], Excursions: [], Races: races, Skips: [], Laps: [], Shifts: [], Jumps: [], ElevRange: null, Cut: [],
            GgExcluded: Input["excluded"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray());
        return (marks, rows == null ? null : 0);
    }

    private static IEnumerable<(JsonNode Case, JsonNode Expected)> Cases() =>
        Input["cases"]!.AsArray().Zip(Expected["cases"]!.AsArray(), (c, e) => (c!, e!));

    /// <summary>JS と C# の計算の順は同じなので、違うのは Math.hypot と Math.Sqrt の丸めぐらい</summary>
    private static void Near(double? expected, double? actual)
    {
        if (expected == null || actual == null) { Assert.Equal(expected, actual); return; }
        Assert.True(Math.Abs(expected.Value - actual.Value) <= 1e-12, $"{expected} と {actual}");
    }

    [Fact]
    public void 入力に確かめたい形が入っている()
    {
        var all = Expected["cases"]![0]!;
        Assert.Equal(2, all["gg"]!["rings"]!.AsArray().Count);                                    // 舗装とダートは円を作れる。雪・芝は行が足りない
        Assert.NotNull(all["base"]!["wheels"]);
        Assert.Null(Expected["cases"]![4]!["base"]!["wheels"]);                                    // 基準を作れない
        Assert.Contains(Doubles(all["load"]!["x"]).Zip(Doubles(all["load"]!["y"])),                // 基準を引くと 1 を超える(円の推定に使わない)
            p => p.First is { } x && p.Second is { } y && Math.Sqrt(x * x + y * y) > FrictionDynamic.SaneLoad);
        Assert.Contains(FrictionStatic.GroupMixed, Ints(Input["group"]));
    }

    [Fact]
    public void ならしたGが前のJSと同じ()
    {
        foreach (var sec in Input["secs"]!.AsArray().Select(v => v!.GetValue<double>()))
        {
            var (x, y) = FrictionDynamic.SmoothG(T, Doubles(Input["gLat"]), Doubles(Input["gLong"]), sec);
            var e = Expected["gg"]![sec.ToString(System.Globalization.CultureInfo.InvariantCulture)]!;
            Assert.All(Doubles(e["x"]).Zip(x), p => Near(p.First, p.Second));
            Assert.All(Doubles(e["y"]).Zip(y), p => Near(p.First, p.Second));
        }
    }

    [Fact]
    public void 荷重移動の点と基準が前のJSと同じ()
    {
        var series = Series();
        foreach (var (c, e) in Cases())
        {
            double sec = c["sec"]!.GetValue<double>();
            var rows = c["rows"] is JsonArray a ? a.Select(v => v!.GetValue<int>()).ToArray() : Enumerable.Range(0, T.Length).ToArray();
            var excluded = new bool[T.Length];
            foreach (var i in Input["excluded"]!.AsArray()) excluded[i!.GetValue<int>()] = true;
            var raw = FrictionDynamic.SmoothLoad(T, Susp, sec);
            var b = FrictionDynamic.BuildBaseline(rows, excluded, series.Speed, FrictionDynamic.SmoothG(T, series.GLat, series.GLong, 0), raw, Susp);
            var eb = e["base"]!;
            Near(eb["x"]!.GetValue<double>(), b.X);
            Near(eb["y"]!.GetValue<double>(), b.Y);
            Assert.Equal(eb["n"]!.GetValue<int>(), b.N);
            if (eb["wheels"] is JsonObject w) Assert.All(new[] { "fl", "fr", "rl", "rr" }.Zip(b.Wheels!), p => Near(w[p.First]!.GetValue<double>(), p.Second));
            else Assert.Null(b.Wheels);
            if (e["load"]!["x"] is not JsonArray ex) continue;
            Assert.All(Doubles(ex).Zip(raw.X), p => Near(p.First, p.Second - b.X));
            Assert.All(Doubles(e["load"]!["y"]).Zip(raw.Y), p => Near(p.First, p.Second - b.Y));
        }
    }

    [Fact]
    public void 円と表示の上限と点群とグラフの系列が前のJSと同じ()
    {
        var series = Series();
        foreach (var (c, e) in Cases())
        {
            var (marks, race) = Marks(c["rows"]);
            var f = FrictionDynamic.Build(series, marks, race, c["sec"]!.GetValue<double>());
            foreach (var (kind, view) in new[] { ("gg", f.Gg), ("load", f.Load) })
            {
                var ek = e[kind]!;
                var rings = ek["rings"]!.AsArray();
                Assert.Equal(rings.Count, view.Rings.Length);
                foreach (var (er, r) in rings.Zip(view.Rings.OrderBy(r => r.Group)))
                {
                    Assert.Equal(er!["group"]!.GetValue<int>(), r.Group);
                    Assert.Equal(er["n"]!.GetValue<int>(), r.N);
                    Near(er["r"]!.GetValue<double>(), r.R);
                }
                Near(ek["scaleMax"]!.GetValue<double>(), view.ScaleMax);
                Assert.Equal(ek["cloud"]!.ToJsonString(), JsonSerializer.Serialize(view.Cloud));
            }
            Assert.Equal(e["loadLong"]!.ToJsonString(), JsonSerializer.Serialize(f.LoadLong));
            Assert.Equal(e["loadLat"]!.ToJsonString(), JsonSerializer.Serialize(f.LoadLat));
            Assert.Equal(e["grip"]!.ToJsonString(), JsonSerializer.Serialize(f.Grip));
            // 点は丸めずに送る
            if (e["load"]!["x"] is JsonArray lx)
            {
                Assert.All(Doubles(lx).Zip(f.Load.X), p => Near(p.First, p.Second));
                Assert.All(Doubles(e["load"]!["y"]).Zip(f.Load.Y), p => Near(p.First, p.Second));
            }
        }
    }
}
