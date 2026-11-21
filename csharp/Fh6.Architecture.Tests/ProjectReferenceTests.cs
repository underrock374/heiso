using System.Xml.Linq;

namespace Fh6.Architecture.Tests;

/// <summary>ルール 1: 依存の向き。View → 外枠 → ViewModel → Core の向きだけ</summary>
public class ProjectReferenceTests
{
    private sealed record Allowed(string[] Projects, string[] Packages, bool Wpf);

    private static readonly Dictionary<string, Allowed> Products = new()
    {
        ["Fh6.Core"] = new([], [], false),
        ["Fh6.Recorder"] = new(["Fh6.Core"], [], false),
        ["Fh6.Player.App"] = new(["Fh6.Core"], [], false),
        ["Fh6.Player"] = new(["Fh6.Core", "Fh6.Player.App"], ["Microsoft.Web.WebView2"], true),
    };

    [Fact]
    public void 製品のプロジェクトは決まった向きにだけ参照する()
    {
        var problems = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(SourceTree.CsharpRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
            if (!Products.TryGetValue(name, out var allowed))
            {
                problems.Add($"{name}: 知らないプロジェクト。docs/architecture.md の層を決めて、この表に足す");
                continue;
            }
            var xml = XDocument.Load(csproj);
            var refs = xml.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')));
            foreach (var r in refs.Except(allowed.Projects))
                problems.Add($"{name}: {r} を参照してはいけない");
            var packages = xml.Descendants("PackageReference").Select(e => e.Attribute("Include")!.Value);
            foreach (var p in packages.Except(allowed.Packages))
                problems.Add($"{name}: パッケージ {p} はこの層に入れない(増やすなら理由を PR に書き、この表を直す)");
            var wpf = xml.Descendants("UseWPF").Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            if (wpf && !allowed.Wpf) problems.Add($"{name}: WPF を使わない");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("Fh6.Core")]
    [InlineData("Fh6.Player.App")]
    public void CoreとViewModelは画面の部品をソースでも使わない(string project)
    {
        if (!Directory.Exists(Path.Combine(SourceTree.CsharpRoot, project))) return;   // Fh6.Player.App はステップ 1 で作る
        var bad = SourceTree.Files(project, ".cs")
            .Where(f => SourceTree.ReadCode(f) is var s && (s.Contains("System.Windows") || s.Contains("Microsoft.Web.WebView2")))
            .ToList();
        Assert.True(bad.Count == 0, "WPF と WebView2 を使っている: " + string.Join(", ", bad));
    }
}
