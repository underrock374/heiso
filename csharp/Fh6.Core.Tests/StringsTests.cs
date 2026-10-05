using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fh6.Core.Tests;

/// <summary>画面とメッセージの言語(Fh6.Core の Strings と i18n/strings.json)</summary>
public partial class StringsTests
{
    [Fact]
    public void すべての文に英訳があり_差し込み口が日本語と同じ()
    {
        Assert.NotEmpty(Strings.Entries);
        foreach (var (key, tr) in Strings.Entries)
        {
            Assert.True(tr.TryGetValue(Strings.English, out var en) && !string.IsNullOrWhiteSpace(en), $"英訳がありません: {key}");
            Assert.Equal(Strings.PlaceholderNames(key), Strings.PlaceholderNames(en!));
        }
    }

    [Fact]
    public void 英語なら訳を_日本語なら鍵をそのまま_辞書に無ければ日本語のまま返す()
    {
        Assert.Equal("HEISO Solo", Strings.TIn(Strings.English, "HEISO 単走版"));
        Assert.Equal("HEISO 単走版", Strings.TIn(Strings.Japanese, "HEISO 単走版"));
        Assert.Equal("辞書に無い文", Strings.TIn(Strings.English, "辞書に無い文"));
        Assert.Equal("Exporting race 3", Strings.TIn(Strings.English, "{n} レース目を書き出しています", ("n", 3)));
        Assert.Empty(Strings.Dictionary(Strings.Japanese));
        Assert.Equal("HEISO Solo", Strings.Dictionary(Strings.English)["HEISO 単走版"]);
    }

    [Fact]
    public void 数値はカンマの地域の形式でもピリオドで書く()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1,5", 1.5.ToString());   // この形式では、ふつうに書くとカンマになる
            Assert.Equal("Exported: a.heiso.zip (138 MB, 16 s). Location: D:\\x",
                Strings.TIn(Strings.English, "書き出しました: {file}({mb:F0} MB、{sec:F0} 秒)。場所: {dir}",
                    ("file", "a.heiso.zip"), ("mb", 137.6), ("sec", 15.9), ("dir", "D:\\x")));
            Assert.Equal("x 1.25 y", Strings.TIn(Strings.Japanese, "x {v} y", ("v", 1.25)));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void 言語の自動は_Windowsの表示言語が日本語なら日本語_それ以外は英語()
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
            Assert.Equal(Strings.Japanese, Strings.Resolve(Strings.Auto));
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Assert.Equal(Strings.English, Strings.Resolve(null));
            Assert.Equal(Strings.Japanese, Strings.Resolve(Strings.Japanese));   // 設定で固定
            Assert.Equal(Strings.English, Strings.Resolve(Strings.English));
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    /// <summary>
    /// 訳し漏れと、使われていない訳が無いか。コードの中の Strings.T("…")(C#)・t("…")(画面の JS)・data-i18n の付いた HTML の文言を集め、
    /// strings.json と突き合わせる(日本語の文を直したのに辞書を直し忘れると、ここで分かる)
    /// </summary>
    [Fact]
    public void コードで使う文と辞書が一致する()
    {
        var root = RepoRoot();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "csharp"), "*.*", SearchOption.AllDirectories))
        {
            if (!(f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))) continue;
            if (f.Contains($"{sep}bin{sep}") || f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}.vs{sep}")
                || f.Contains(".Tests") || f.EndsWith(".min.js", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(f, Encoding.UTF8);
            if (f.EndsWith(".cs", StringComparison.Ordinal))
                foreach (Match m in CsKey().Matches(text)) used.Add(Unescape(m.Groups[1].Value));
            else if (f.EndsWith(".js", StringComparison.Ordinal))
                foreach (Match m in JsKey().Matches(text)) used.Add(Unescape(m.Groups[1].Value));
            else if (f.EndsWith(".html", StringComparison.Ordinal))
            {
                // 記録アプリの画面は JS を HTML の中に書いているので、t("…") も拾う
                foreach (Match m in JsKey().Matches(text)) used.Add(Unescape(m.Groups[1].Value));
                foreach (Match m in HtmlText().Matches(text)) used.Add(m.Groups[1].Value.Trim());
                foreach (Match m in HtmlAttr().Matches(text))
                {
                    var tag = m.Value;
                    foreach (var attr in new[] { "title", "placeholder" })
                        if (tag.Contains($"data-i18n-{attr}") && Regex.Match(tag, $"\\b{attr}=\"([^\"]*)\"") is { Success: true } a)
                            used.Add(a.Groups[1].Value);
                }
            }
        }
        var dict = Strings.Entries.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = used.Where(k => !dict.Contains(k)).ToList();
        var unused = dict.Where(k => !used.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "strings.json に無い文:\n" + string.Join("\n", missing));
        Assert.True(unused.Count == 0, "どこでも使われていない訳:\n" + string.Join("\n", unused));
    }

    [GeneratedRegex(@"Strings\.T\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CsKey();
    [GeneratedRegex(@"(?<![\w.$])t\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex JsKey();
    [GeneratedRegex(@"<[^>]*\bdata-i18n(?![-\w])[^>]*>([^<]*)<")]
    private static partial Regex HtmlText();
    [GeneratedRegex(@"<[^>]*\bdata-i18n-(?:title|placeholder)\b[^>]*>")]
    private static partial Regex HtmlAttr();

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx"))) return d.FullName;
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }
}
