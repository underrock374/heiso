using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fh6.Core;

/// <summary>
/// 画面とメッセージの言語(日本語 / 英語)。訳は i18n/strings.json の 1 ファイルだけに置き、このクラスから取る。
/// 鍵は日本語の文そのもの(gettext と同じ考え方)。辞書に無ければ日本語のまま返すので、訳し漏れがあっても壊れない。
/// 差し込む値は {名前} で書き、{名前:書式} で数値の書式も付けられる。数値は地域の形式によらず、いつもピリオドで書く。
/// 画面(HTML/JS)には、各アプリがこの辞書を渡す(i18n/i18n.js の t())。ログと記録データ(session.json の説明文など)は訳さない
/// </summary>
public static partial class Strings
{
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>設定の値: "auto"(Windows の表示言語が日本語なら日本語、それ以外は英語)/ "ja" / "en"</summary>
    public const string Auto = "auto";

    /// <summary>今の言語("ja" / "en")。起動時に Use で決める</summary>
    public static string Language { get; private set; } = Resolve(Auto);

    /// <summary>設定の値(auto / ja / en)から言語を決める</summary>
    public static string Resolve(string? setting) => setting switch
    {
        Japanese => Japanese,
        English => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Japanese ? Japanese : English,
    };

    /// <summary>設定の値から言語を決めて使う</summary>
    public static void Use(string? setting) => Language = Resolve(setting);

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> All = new(Load);

    /// <summary>strings.json(鍵 → { 言語: 訳 })。Core の dll に埋め込んである</summary>
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Fh6.Core.i18n.strings.json")
                      ?? throw new InvalidOperationException("i18n/strings.json が dll に入っていません");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s) ?? new();
    }

    /// <summary>ある言語の辞書(鍵 → 訳)。日本語なら空(鍵がそのまま日本語の文なので)。画面へ渡す用</summary>
    public static Dictionary<string, string> Dictionary(string language)
    {
        var d = new Dictionary<string, string>();
        if (language == Japanese) return d;
        foreach (var (key, tr) in All.Value)
            if (tr.TryGetValue(language, out var text) && !string.IsNullOrEmpty(text)) d[key] = text;
        return d;
    }

    /// <summary>strings.json のすべての鍵と訳(テスト用)</summary>
    public static IReadOnlyDictionary<string, Dictionary<string, string>> Entries => All.Value;

    /// <summary>
    /// 今の言語の文。key は日本語の文。args で {名前} に値を差し込む(例: T("{n} レース目", ("n", 3)))。
    /// 数値は地域の形式によらずピリオドで書く({名前:F1} のように書式も付けられる)
    /// </summary>
    public static string T(string key, params (string Name, object? Value)[] args) => TIn(Language, key, args);

    /// <summary>言語を指定して引く(テストなど。ふだんは T)</summary>
    public static string TIn(string language, string key, params (string Name, object? Value)[] args)
    {
        var text = key;
        if (language != Japanese && All.Value.TryGetValue(key, out var tr) && tr.TryGetValue(language, out var t) && !string.IsNullOrEmpty(t))
            text = t;
        return args.Length == 0 ? text : Fill(text, args);
    }

    [GeneratedRegex(@"\{(\w+)(?::([^{}]+))?\}")]
    private static partial Regex Placeholder();

    private static string Fill(string text, (string Name, object? Value)[] args) =>
        Placeholder().Replace(text, m =>
        {
            foreach (var (name, value) in args)
            {
                if (name != m.Groups[1].Value) continue;
                var format = m.Groups[2].Success ? m.Groups[2].Value : null;
                return value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
            }
            return m.Value;   // 渡されていない名前はそのまま
        });

    /// <summary>文の中の差し込み口の名前(テストで、訳と日本語で同じ口があるかを確かめる)</summary>
    public static IEnumerable<string> PlaceholderNames(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order();
}
