using System.Text.RegularExpressions;

namespace Fh6.Architecture.Tests;

/// <summary>ルール 6・8: 画面に渡すものは名前の付いた型で。MainWindow は薄く</summary>
public partial class CsharpRuleTests
{
    // 画面とやり取りするコード: 再生アプリの ViewModel の全部と MainWindow、記録アプリの画面の入出力。ObsClient など、画面に関わらないものは対象にしない
    private static IEnumerable<string> ScreenFacing =>
        SourceTree.Files("Fh6.Player.App", ".cs").Concat(MainWindowFiles).Concat(
        [
            "Fh6.Recorder/Program.cs",
            "Fh6.Recorder/SessionController.cs",
            "Fh6.Recorder/RaceWatcher.cs",
        ]);

    private static IEnumerable<string> MainWindowFiles =>
        SourceTree.Files("Fh6.Player", ".cs").Where(f => Path.GetFileName(f).StartsWith("MainWindow", StringComparison.Ordinal));

    [GeneratedRegex(@"\bnew\s*\{")]
    private static partial Regex AnonymousType();

    [GeneratedRegex(@"\bcase\s+""")]
    private static partial Regex StringCase();

    // ファイルの読み書き、記録データの更新、処理の本体。MainWindow から直接触らない
    [GeneratedRegex(@"\b(?:File|Directory|SessionMeta|SessionCatalog|Package|Ffmpeg|VideoAlt|Registry|ProfileStore|ReplayDetector)\.")]
    private static partial Regex MainWindowForbiddenCall();

    [Fact]
    public void 画面に渡すデータに匿名型を使わない() =>
        Ratchet.Check("ルール 6: メッセージと /api の入出力は、名前の付いた型(record)で定義する", ExceptionList.AnonymousTypes, Ratchet.Count(ScreenFacing, AnonymousType()));

    [Fact]
    public void メッセージの振り分けを文字列の直書きにしない() =>
        Ratchet.Check("ルール 6: メッセージの type を文字列で直書きしない", ExceptionList.MessageSwitchCases, Ratchet.Count(MainWindowFiles, StringCase()));

    [Fact]
    public void MainWindowでファイルと記録データを直接触らない() =>
        Ratchet.Check("ルール 8: ファイルの読み書きと session.json の更新は、ViewModel か Core で行う", ExceptionList.MainWindowForbidden, Ratchet.Count(MainWindowFiles, MainWindowForbiddenCall()));
}
