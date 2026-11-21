using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fh6.Player.App.Tests;

/// <summary>
/// コース図の線(CourseLine)。期待値 golden/js/course-expected.json は、移す前の画面の JS
/// (coursemap.js の buildSurfaceColors と、coursemap.js・courseview3d.js の JUMP_M で線を切る判定)を、同じ入力 golden/js/course-input.json で動かした結果
/// </summary>
public sealed class CourseLineTests
{
    private static readonly JsonNode Input = Load("course-input.json");
    private static readonly JsonNode Expected = Load("course-expected.json");

    private static JsonNode Load(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return JsonNode.Parse(File.ReadAllText(Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden", "js", name)))!;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    private static int?[] Ints(JsonNode? a) => a!.AsArray().Select(v => v?.GetValue<int>()).ToArray();
    private static double?[] Doubles(string key) => Input[key]!.AsArray().Select(v => v?.GetValue<double>()).ToArray();

    [Fact]
    public void 路面の分類が前のJSと同じ()
    {
        var s = Input["surface"]!;
        var kinds = CourseLine.SurfaceKinds(new SurfaceSeries(Ints(s["fl"]), Ints(s["fr"]), Ints(s["rl"]), Ints(s["rr"])));
        Assert.Equal(Expected["surface"]!.ToJsonString(), JsonSerializer.Serialize(kinds));
        Assert.Contains(CourseLine.Mixed, kinds.OfType<int>());   // 入力に混在が入っている
    }

    [Fact]
    public void 線を切る行が前のJSと同じ()
    {
        double?[] x = Doubles("x"), z = Doubles("z");
        var brk = CourseLine.BreakRows(x, z).ToHashSet();
        // 前の JS は、描く行(記録全体か、レースの行。位置の無い行を除く)の隣どうしで比べていた。その並びの 2 行目から先で、印のある行
        var lists = new[] { Enumerable.Range(0, x.Length).ToArray() }.Concat(Input["races"]!.AsArray().Select(r => r!.AsArray().Select(v => v!.GetValue<int>()).ToArray()));
        var expected = Expected["breaks"]!.AsArray();
        int n = 0;
        foreach (var all in lists)
        {
            var rows = all.Where(i => x[i] != null && z[i] != null).ToArray();
            Assert.Equal(expected[n++]!.ToJsonString(), JsonSerializer.Serialize(rows.Skip(1).Where(brk.Contains)));
        }
        Assert.Equal(3, n);
    }

    [Fact]
    public void ちょうど50mの移動では線を切らない() =>
        Assert.Empty(CourseLine.BreakRows([0.0, 50.0, 100.0], [0.0, 0.0, 0.0]));
}
