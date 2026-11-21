namespace Fh6.Architecture.Tests;

/// <summary>ルール 9: ファイルの大きさ。1 つ 800 行まで、MainWindow は部分クラスも合わせて 360 行まで</summary>
public class SizeRuleTests
{
    private const int FileLimit = 800;
    private const int MainWindowLimit = 360;

    private static readonly string[] Products = ["Fh6.Core", "Fh6.Recorder", "Fh6.Player", "Fh6.Player.App"];

    private static bool IsMainWindow(string file) => Path.GetFileName(file).StartsWith("MainWindow", StringComparison.Ordinal);

    [Fact]
    public void ファイルは800行まで()
    {
        var problems = new List<string>();
        var files = Products.Where(p => Directory.Exists(Path.Combine(SourceTree.CsharpRoot, p)))
            .SelectMany(p => SourceTree.Files(p, ".cs", ".js", ".html", ".css")).Where(f => !IsMainWindow(f));
        foreach (var f in files)
        {
            var lines = SourceTree.LineCount(f);
            if (ExceptionList.OversizeCeilings.TryGetValue(f, out var ceiling))
            {
                if (lines <= FileLimit) problems.Add($"{f}: {lines} 行になった。ExceptionList.OversizeCeilings から消す");
                else if (lines > ceiling) problems.Add($"{f}: {lines} 行で、一覧の {ceiling} 行を超えた。増やさずに分ける");
            }
            else if (lines > FileLimit) problems.Add($"{f}: {lines} 行。{FileLimit} 行を超えたら、機能を足す前に分ける");
        }
        foreach (var f in ExceptionList.OversizeCeilings.Keys.Where(k => !File.Exists(Path.Combine(SourceTree.CsharpRoot, k))))
            problems.Add($"{f}: もう無い。ExceptionList.OversizeCeilings から消す");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void MainWindowは部分クラスも合わせて360行まで()
    {
        var total = SourceTree.Files("Fh6.Player", ".cs").Where(IsMainWindow).Sum(SourceTree.LineCount);
        if (ExceptionList.MainWindowCeiling is not { } ceiling)
            Assert.True(total <= MainWindowLimit, $"MainWindow が {total} 行。{MainWindowLimit} 行までにする");
        else
        {
            Assert.True(total <= ceiling, $"MainWindow が {total} 行で、一覧の {ceiling} 行を超えた。増やさずに移す");
            Assert.True(total > MainWindowLimit, $"MainWindow が {total} 行になった。ExceptionList.MainWindowCeiling を null にする");
        }
    }
}
