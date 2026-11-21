using System.Text;

namespace Fh6.Architecture.Tests;

/// <summary>検査するソースの読み出し。パスはリポジトリの csharp/ からの相対で、区切りは / にそろえる</summary>
internal static class SourceTree
{
    public static string CsharpRoot { get; } = FindCsharpRoot();

    private static string FindCsharpRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx"))) return Path.Combine(d.FullName, "csharp");
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    /// <summary>folder の下(bin / obj / 同梱のライブラリを除く)から、拡張子の合うファイルを探す</summary>
    public static IEnumerable<string> Files(string folder, params string[] extensions)
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(CsharpRoot, folder), "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(CsharpRoot, f).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/lib/")) continue;
            if (rel.EndsWith("/qrcode.js", StringComparison.Ordinal)) continue;   // 同梱の部品
            if (extensions.Any(e => rel.EndsWith(e, StringComparison.Ordinal))) yield return rel;
        }
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(CsharpRoot, relative), Encoding.UTF8);

    /// <summary>コメントを除いた中身。検査がコメントの中の語に反応しないように</summary>
    public static string ReadCode(string relative) => StripComments(Read(relative));

    public static int LineCount(string relative) => Read(relative).Count(c => c == '\n');

    /// <summary>
    /// C# と JS の // と /* */ を消す。文字列の中は残す。文字列の引用符が閉じないまま改行したら文字列の外へ戻るので
    /// (JS の正規表現の中の引用符など)、読み違えても影響はその行で止まる
    /// </summary>
    public static string StripComments(string src)
    {
        var o = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                if (i < src.Length) o.Append('\n');
            }
            else if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 1;
            }
            else if (c is '"' or '\'' or '`')
            {
                o.Append(c);
                for (i++; i < src.Length; i++)
                {
                    var s = src[i];
                    o.Append(s);
                    if (s == '\\' && i + 1 < src.Length) { o.Append(src[++i]); continue; }
                    if (s == c || (s == '\n' && c != '`')) break;
                }
            }
            else o.Append(c);
        }
        return o.ToString();
    }
}
