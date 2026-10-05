using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class RegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "heiso-registry-" + Guid.NewGuid().ToString("N"));
    private string PathOf => System.IO.Path.Combine(_dir, Registry.FileName);

    public RegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ロガー v3 が書いた形(Python の json.dump、ensure_ascii=False)
    private const string V3File = """
        {
          "registry_version": 1,
          "cars": {
            "2163": {"name": "Honda Civic Type R", "updated": "2026-09-22T21:00:00+09:00"}
          },
          "setups": [
            {"id": "S0001", "ordinal": 2163, "cls": 2, "pi": 585, "drive": 0, "cyl": 4, "variant": 1,
             "name": "B585 FWD", "note": null, "created": "2026-09-22T21:00:05+09:00"}
          ],
          "courses": [
            {"id": "R0001", "name": "東京鉄道スプリント", "kind": "street", "x": 248.2, "z": -3698.8, "yaw": -2.19,
             "note": null, "created": "2026-09-22T21:05:00+09:00", "seen": 3, "future_key": [1, 2]}
          ],
          "unknown_top": {"a": 1}
        }
        """;

    [Fact]
    public void v3の登録ファイルを読めて_知らないキーは残る()
    {
        File.WriteAllText(PathOf, V3File);
        var r = Registry.Load(PathOf, out var warning);
        Assert.Null(warning);
        Assert.Equal("Honda Civic Type R", r.CarName(2163));
        Assert.Null(r.CarName(1));
        var sig = new CarSignature(2163, 2, 585, 0, 4);
        Assert.Equal("S0001", Assert.Single(r.FindSetups(sig)).Id);
        Assert.Equal(3, r.Courses[0].Seen);

        r.AddSetup(sig, "雨用", "柔らかめ");
        r.Save(PathOf);

        var j = JsonNode.Parse(File.ReadAllText(PathOf))!;
        Assert.Equal(1, (int)j["unknown_top"]!["a"]!);
        Assert.Equal(2, (int)j["courses"]![0]!["future_key"]![1]!);
        var added = j["setups"]![1]!;
        Assert.Equal("S0002", (string)added["id"]!);
        Assert.Equal(2, (int)added["variant"]!);
        Assert.Equal(585, (int)added["pi"]!);
        Assert.Equal("Honda Civic Type R", (string)j["cars"]!["2163"]!["name"]!);
        Assert.False(File.Exists(PathOf + ".tmp"));
    }

    [Fact]
    public void 壊れた登録ファイルはbrokenを付けて残し_空から始める()
    {
        File.WriteAllText(PathOf, "{ \"cars\": { 壊れている");
        var r = Registry.Load(PathOf, out var warning);
        Assert.NotNull(warning);
        Assert.Empty(r.Cars);
        Assert.False(File.Exists(PathOf));
        Assert.True(File.Exists(PathOf + ".broken"));
    }

    [Fact]
    public void 無ければ空()
    {
        var r = Registry.Load(PathOf, out var warning);
        Assert.Null(warning);
        Assert.Empty(r.Courses);
        var c = r.AddCourse("新しいコース", "dirt", 1.234, 5.678, 0.123456, null);
        Assert.Equal(("R0001", 1.23, 5.68, 0.12346), (c.Id, c.X, c.Z, c.Yaw));
    }
}
