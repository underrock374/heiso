using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 性能モードの読み取り値(PerfReadout)。期待値 golden/js/readout-expected.json は、移す前の画面の JS
/// (app.js の showPerfReadout と、powercurve.js の pwAt・info)を、同じ入力 golden/js/readout-input.json で動かした結果。
/// state は、前の JS が出した文言がどの理由だったか
/// </summary>
public sealed class PerfReadoutTests
{
    private static readonly JsonNode Input = Load("readout-input.json");
    private static readonly JsonNode Expected = Load("readout-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static double?[] Doubles(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<double>()).ToArray();
    private static int?[] Ints(string key) => Input[key]!.AsArray().Select(v => v?.GetValue<int>()).ToArray();

    private static PerfReadoutSeries Actual() => PerfReadout.Build(
        Doubles(Input["rpm"]), Doubles(Input["gear"]), Ints("full"), Doubles(Input["gain"]), Ints("perfCar"),
        Input["perf"]!["cars"]!.Deserialize<PerfCarInfo[]>(MessageJson.Options)!);

    [Fact]
    public void 判定と出せない理由が前のJSと同じ()
    {
        var expected = Expected["state"]!.AsArray().Select(v => v?.GetValue<string>()).ToArray();
        Assert.Equal(expected, Actual().State);
        // 入力に、全部の判定と理由が入っている
        Assert.Equal(10, expected.Distinct().Count());
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("rpmAfter")]
    [InlineData("hpAfter")]
    public void パワーカーブの値が前のJSと同じ(string key)
    {
        var a = Actual();
        var actual = key switch { "hp" => a.Hp, "rpmAfter" => a.RpmAfter, _ => a.HpAfter };
        Assert.Equal(Doubles(Expected[key]), actual);   // 丸めない値のまま同じ
        Assert.Contains(actual, v => v != null);
    }
}
