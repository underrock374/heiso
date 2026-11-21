using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 距離軸と標高の段(DistanceAxis)。期待値 golden/js/distance-expected.json は、移す前の画面の JS
/// (app.js の buildDistMap・buildCutRows・elevRange)を、同じ入力 golden/js/distance-input.json で動かした結果
/// </summary>
public sealed class DistanceAxisTests
{
    private static readonly JsonNode Input = Load("distance-input.json");
    private static readonly JsonNode Expected = Load("distance-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static double?[] Elev => Input["elev"]!.AsArray().Select(v => v?.GetValue<double>()).ToArray();

    private static List<RaceDistanceSeries> Races => Input["races"]!.AsArray().Select(r => new RaceDistanceSeries(
        I: r!["i"]!.AsArray().Select(v => v!.GetValue<int>()).ToList(),
        D: r["d"]!.AsArray().Select(v => v!.GetValue<double>()).ToList(),
        K: r["k"]!.AsArray().Select(v => v!.GetValue<int>()).ToList(),
        Graph: [])).ToList();

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public void 入力に確かめたい形が入っている()
    {
        var a = Races[0];
        Assert.Contains(0, a.K);                                                        // やり直した走り
        Assert.Contains(a.D.Zip(a.D.Skip(1)), p => p.Second <= p.First);               // 距離が前の行より大きくない行
        Assert.Equal(3, Expected["races"]!.AsArray().Count);
        Assert.Null(Expected["races"]![2]!["elevRange"]);                              // 標高の無いレース
    }

    [Fact]
    public void グラフに出す行が前のJSと同じ()
    {
        var races = Races;
        for (int n = 0; n < races.Count; n++)
        {
            var r = races[n];
            var g = DistanceAxis.GraphRows(r.D, r.K);
            Assert.Equal(Expected["races"]![n]!["pos"]!.ToJsonString(), Json(g.Select(j => r.I[j])));
            Assert.Equal(Expected["races"]![n]!["d"]!.ToJsonString(), Json(g.Select(j => r.D[j])));
        }
    }

    [Fact]
    public void 標高の段の上下が前のJSと同じ()
    {
        var elev = Elev;
        var races = Races;
        for (int n = 0; n < races.Count; n++)
            Assert.Equal(Expected["races"]![n]!["elevRange"]?.ToJsonString() ?? "null", Json(DistanceAxis.ElevRange(races[n].I.Select(p => elev[p]))));
        Assert.Equal(Expected["elevRange"]!.ToJsonString(), Json(DistanceAxis.ElevRange(elev)));
    }

    [Fact]
    public void やり直した走りの行が前のJSと同じ() =>
        Assert.Equal(Expected["cut"]!.ToJsonString(), Json(DistanceAxis.CutRows(Races)));
}
