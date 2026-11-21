using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 摩擦円・荷重移動の、記録を開いたときに決まる値(FrictionStatic)。期待値 golden/js/friction-static-expected.json は、移す前の画面の JS
/// (frictioncircle.js の ggSurfaceGroup・ggExcludeMask・ltWheelsSmooth)を、同じ入力 golden/js/friction-static-input.json で動かした結果
/// </summary>
public sealed class FrictionStaticTests
{
    private static readonly JsonNode Input = Load("friction-static-input.json");
    private static readonly JsonNode Expected = Load("friction-static-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static double[] T => Input["t"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
    private static int?[] Ints(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<int>()).ToArray();
    private static double?[] Doubles(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<double>()).ToArray();

    [Fact]
    public void 路面のまとめが前のJSと同じ()
    {
        var s = Input["surface"]!;
        var groups = FrictionStatic.SurfaceGroups(new SurfaceSeries(Ints(s["fl"]), Ints(s["fr"]), Ints(s["rl"]), Ints(s["rr"])));
        Assert.Equal(Expected["group"]!.ToJsonString(), JsonSerializer.Serialize(groups));
        // 入力に、全部のまとめ方が入っている
        Assert.Subset(groups.ToHashSet(), new HashSet<int> { 0, 1, 2, 4, 5, FrictionStatic.GroupNone, FrictionStatic.GroupAir, FrictionStatic.GroupMixed });
    }

    [Fact]
    public void 除く行が前のJSと同じ()
    {
        var rows = FrictionStatic.ExcludedRows(T,
            Input["contacts"]!.AsArray().Select(v => v!.GetValue<double>()),
            Input["jumps"]!.AsArray().Select(j => (j![0]!.GetValue<double>(), j[1]!.GetValue<double>())));
        Assert.Equal(Expected["excluded"]!.ToJsonString(), JsonSerializer.Serialize(rows));
    }

    [Fact]
    public void 四輪の棒が前のJSと同じ()
    {
        var s = Input["susp"]!;
        var bars = FrictionStatic.WheelBars(T, new SuspensionSeries(Doubles(s["fl"]), Doubles(s["fr"]), Doubles(s["rl"]), Doubles(s["rr"])));
        var named = new[] { ("fl", bars.Fl), ("fr", bars.Fr), ("rl", bars.Rl), ("rr", bars.Rr) };
        for (int w = 0; w < 4; w++)
        {
            var (name, bar) = named[w];
            var e = Expected["wheels"]![name]!;
            // いちばん縮んだ値は 1000 分の 1 の整数(記録の値は小数 3 桁なので、割れば元の値に戻る)
            var hi = Doubles(e["hi"]);
            Assert.Equal(hi.Select(x => x is { } v ? (int?)Math.Round(v * 1000) : null), bar.Hi);
            Assert.All(hi.Zip(bar.Hi), p => Assert.Equal(p.First, p.Second / 1000.0));
            // 状態は 4 輪で 1 つの整数(車輪 w の分は 2w ビット目から)
            Assert.Equal(Ints(e["state"]), bars.State.Select(s => s is { } x ? (x >> (2 * w)) & 3 : (int?)null));
            // ならした値は 1 万分の 1 の整数(棒の長さで 0.01 px 未満の違い)
            Assert.Equal(Doubles(e["avg"]).Select(x => x is { } v ? (int?)Math.Round(v * 10000) : null), bar.Avg);
        }
        // 入力に、浮きかけ・縮み切りに近い・その両方が入っている
        var states = Expected["wheels"]!["rr"]!["state"]!.AsArray().Select(v => v?.GetValue<int>());
        Assert.Contains(3, states);
        Assert.Contains(1, Ints(Expected["wheels"]!["fr"]!["state"]));
        Assert.Contains(2, Ints(Expected["wheels"]!["fl"]!["state"]));
    }
}
