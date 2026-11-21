using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 全開の判定(系列の full)と、逸脱に続くスリップの帯の終わり(SlipBand)。期待値 golden/js/perf-expected.json は、移す前の画面の JS
/// (graphs.js の PERF_FULL_PCT との比べ方と excursionEnd)を、同じ入力 golden/js/perf-input.json で動かした結果
/// </summary>
public sealed class PerfMarksTests
{
    private static readonly JsonNode Input = Load("perf-input.json");
    private static readonly JsonNode Expected = Load("perf-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    [Fact]
    public void 全開の判定が前のJSと同じ()
    {
        var pct = Input["accelPct"]!.AsArray();
        var expected = Expected["full"]!.AsArray();
        Assert.Equal(256, pct.Count);
        for (int v = 0; v < 256; v++)
        {
            // 入力は、系列のアクセル(%)と同じ式で作ってある
            Assert.Equal(Math.Round(v * (100.0 / 255), 1), pct[v]!.GetValue<double>());
            Assert.Equal(expected[v]!.GetValue<int>() == 1, v >= PowerAnalysis.FullThrottle);
        }
    }

    [Fact]
    public void スリップの帯の終わりが前のJSと同じ()
    {
        var ex = Input["excursions"]!.AsArray();
        var expected = Expected["band"]!.AsArray();
        Assert.Equal(ex.Count, expected.Count);
        for (int n = 0; n < ex.Count; n++)
        {
            var e = ex[n]!;
            double end = SlipBand.End(e["t0"]!.GetValue<double>(), e["t1"]!.GetValue<double>(), e["slipT"]?.GetValue<double>(), e["slipEndT"]?.GetValue<double>());
            Assert.Equal(expected[n]!.ToJsonString(), JsonSerializer.Serialize(end));
        }
    }
}
