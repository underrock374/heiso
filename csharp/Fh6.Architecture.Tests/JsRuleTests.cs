using System.Text.RegularExpressions;

namespace Fh6.Architecture.Tests;

/// <summary>
/// ルール 3・4・6: JS に書いてはいけないもの。
/// 計算かどうかを機械で完全には見分けられないので、はっきり分かる形だけを検査する。残りはレビューで見る(docs/architecture.md「レビューで見ること」)
/// </summary>
public partial class JsRuleTests
{
    /// <summary>
    /// 数値の定数のうち、表示や操作の見た目にしか効かないもの(ルール 2)。
    /// 足すときは「画面を別の技術で作り直したら変わってよいか」を確かめる。判定と閾値はここに足さず C# に置く
    /// </summary>
    internal static readonly HashSet<string> DisplayConstants = new(StringComparer.Ordinal)
    {
        "START_LEAD_SEC", "RESPAWN_LEAD_SEC", "LAP_LEAD_SEC",   // 飛ぶ先を何秒手前にするか
        "MAP_MIN_PX", "MAP_AUTO_MAX_W", "FOLLOW_ANIM_MS",       // コース図の大きさと動き
        "RESTART",                                              // WebGL の線の区切りの番号
        "GG_DOT_R",                                             // 今の点の半径(px)
        "MEASURE_MAX_ADVICE",                                   // 指示を何件まで見せるか
        "MI_M", "FT_M", "KMH_PER_MPH",                          // 単位の換算
    };

    /// <summary>ステップ 4 で C# へ移す計算の関数。移した後に JS へ戻さない</summary>
    private const string CalculationFunctions =
        "buildDistMap|buildCutRows|elevRange|buildSurfaceColors|excursionEnd|"
        + "ggAt|ggMag|ggSurfaceGroup|ggExcludeMask|ggPercentile|ggEstimate|ltRaw|ltWheels|ltWheelsSmooth|ltBaseline";

    private static IEnumerable<string> PlayerJs => SourceTree.Files("Fh6.Player/wwwroot", ".js");
    private static IEnumerable<string> AllJs => PlayerJs.Concat(SourceTree.Files("Fh6.Recorder/wwwroot", ".js", ".html"));

    [GeneratedRegex(@"^[ \t]*(?:const|let|var)[ \t]+([A-Z][A-Z0-9_]+)[ \t]*=[ \t]*[-+]?(?:\d|\.\d)", RegexOptions.Multiline)]
    private static partial Regex NumericConstant();

    [GeneratedRegex(@"\bfunction[ \t]+(?:" + CalculationFunctions + @")\b")]
    private static partial Regex CalculationFunction();

    // テレメトリーの値を、0 以外の数か定数と比べている(符号で表示を変えるだけの > 0 は対象外)
    [GeneratedRegex(@"(?:\.(?:speed|accel|brake|steer|gear|slipF|slipR|balance|counter|grade|elev|turn|gLong|gLat|rpm|gain)\[[^\]]+\]|\b(?:speed|accel|brake|steer|slipF|slipR|gLat|gLong|gain))[ \t]*(?:<=?|>=?)[ \t]*(?:-?0*[1-9]|-?\d*\.\d|[A-Z][A-Z0-9_]{2,})")]
    private static partial Regex ThresholdComparison();

    [GeneratedRegex(@"\blocalStorage\b")]
    private static partial Regex LocalStorage();

    [GeneratedRegex(@"\.postMessage\(|\baddEventListener\(\s*""message""")]
    private static partial Regex MessageCall();

    [Fact]
    public void 判定の閾値を数値の定数でJSに置かない()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in AllJs)
        {
            var n = NumericConstant().Matches(SourceTree.ReadCode(f)).Count(m => !DisplayConstants.Contains(m.Groups[1].Value));
            if (n > 0) found[f] = n;
        }
        Ratchet.Check("ルール 3: JS に閾値の定数を置かない(表示だけの定数は JsRuleTests.DisplayConstants)", ExceptionList.JsNumericConstants, found);
    }

    [Fact]
    public void C_シャープへ移す計算の関数をJSに置かない() =>
        Ratchet.Check("ルール 3: テレメトリーからの計算は C# に置く", ExceptionList.JsCalculationFunctions, Ratchet.Count(AllJs, CalculationFunction()));

    [Fact]
    public void テレメトリーの値を閾値と比べる判定をJSに書かない() =>
        Ratchet.Check("ルール 3: 判定は C# で行い、結果を受け取る", ExceptionList.JsThresholdComparisons, Ratchet.Count(AllJs, ThresholdComparison()));

    [Fact]
    public void 設定をlocalStorageに置かない() =>
        Ratchet.Check("ルール 4: 利用者の設定は PlayerSettings に保存する", ExceptionList.JsLocalStorage, Ratchet.Count(AllJs, LocalStorage()));

    [Fact]
    public void メッセージの送受信は通信用のファイルだけに書く() =>
        Ratchet.Check("ルール 6: host.postMessage と message の受信は、JS の通信用のファイル 1 つだけに書く", ExceptionList.JsMessageCalls,
            Ratchet.Count(PlayerJs.Where(f => Path.GetFileName(f) != "messaging.js"), MessageCall()));
}
