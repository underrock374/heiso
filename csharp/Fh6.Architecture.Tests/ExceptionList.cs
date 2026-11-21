namespace Fh6.Architecture.Tests;

/// <summary>
/// docs/architecture.md のルールに合っていない所の一覧。値はファイルごとの違反の数。**減らすだけで、増やさない。**
/// 2026-11-05 に、その時点の違反を全部載せて作り、改修(docs/tasks/009-architecture-restructure.md)で 2026-11-21 にほぼ空にした。
/// 残っているのは、前の版の設定を一度だけ引き継ぐ所(JsLocalStorage)だけで、次のリリースの後に消す。
/// これからは、新しい違反を入れないためだけに使う(ここに足して検査を通すことはしない)
/// </summary>
internal static class ExceptionList
{
    /// <summary>ルール 3: JS にある閾値の定数</summary>
    public static readonly Dictionary<string, int> JsNumericConstants = new();

    /// <summary>ルール 3: JS にある計算の関数</summary>
    public static readonly Dictionary<string, int> JsCalculationFunctions = new();

    /// <summary>ルール 3: JS でテレメトリーの値を閾値と比べている所</summary>
    public static readonly Dictionary<string, int> JsThresholdComparisons = new();

    /// <summary>
    /// ルール 4: localStorage を使っている所。設定は C# の PlayerSettings に移した(2026-11-16。app.js 6・graphs.js 2 → 0)。
    /// 残りの 1 か所は、前の版が置いた値を一度だけ引き継ぐ所(settings.js の importOldSettings)。前の版から上げた人がいなくなったら消す
    /// </summary>
    public static readonly Dictionary<string, int> JsLocalStorage = new()
    {
        ["Fh6.Player/wwwroot/settings.js"] = 1,
    };

    /// <summary>ルール 6: 通信用のファイル(messaging.js)の外にある、メッセージの送受信</summary>
    public static readonly Dictionary<string, int> JsMessageCalls = new();

    /// <summary>ルール 6: 画面に渡すデータの匿名型(2026-11-21 に記録アプリの /api の型を付けて、空になった)</summary>
    public static readonly Dictionary<string, int> AnonymousTypes = new();

    /// <summary>ルール 6: メッセージの振り分けの文字列の直書き</summary>
    public static readonly Dictionary<string, int> MessageSwitchCases = new();

    /// <summary>ルール 8: MainWindow が直接触っているファイルと記録データ</summary>
    public static readonly Dictionary<string, int> MainWindowForbidden = new();

    /// <summary>
    /// ルール 9: 800 行を超えているファイル。値はこれ以上増やせない行数。
    /// 不具合の修正で数行増えても落ちないよう、2026-11-05 の行数(app.js 2,009、graphs.js 984)より少しだけ多くしていた。
    /// 2026-11-16 に両方を分けて、空になった
    /// </summary>
    public static readonly Dictionary<string, int> OversizeCeilings = new();

    /// <summary>ルール 9: MainWindow の部分クラスの合計で、これ以上増やせない行数(2026-11-05 は 1,795、2026-11-06 は 1,733、2026-11-07 は 1,600 → 1,507 → 1,226 → 1,130 → 355)。null のときは上限の 360 行を守る(上限は 2026-11-07 に 300 行から直した)</summary>
    public static readonly int? MainWindowCeiling = null;
}
