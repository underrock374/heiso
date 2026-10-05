using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fh6.Core;

/// <summary>
/// セッションフォルダの名前: 先頭が yyyyMMdd_HHmmss(記録を始めた日時)。その後ろに、記号(# など)を挟んでも挟まなくても、
/// 日本語などのコメントを付けてよい。例: 20260927_211710、20260927_211710#夏_鳥野山、20260927_211710夏_鳥野山。
/// </summary>
public static partial class SessionFolderName
{
    public const string TimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>記録アプリがコメントの前に挟む記号</summary>
    public const char Separator = '#';

    /// <summary>コメントの前に挟んでよい記号(1 文字だけ取り除く)</summary>
    private const string Separators = "#＃_-－ 　・:：";

    [GeneratedRegex(@"^(\d{8}_\d{6})(.*)$")]
    private static partial Regex Pattern();

    /// <summary>先頭が日時の形なら true。Started は記録を始めた日時、Comment は後ろのコメント(無ければ null)</summary>
    public static bool TryParse(string name, out DateTime started, out string? comment)
    {
        started = default;
        comment = null;
        var m = Pattern().Match(name);
        if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out started))
            return false;
        var rest = m.Groups[2].Value;
        if (rest.Length > 0 && Separators.Contains(rest[0])) rest = rest[1..];
        rest = rest.Trim();
        comment = rest.Length > 0 ? rest : null;
        return true;
    }

    /// <summary>記録を始めるときのフォルダ名。コメントがあれば「日時#コメント」(フォルダ名に使えない文字は _ に置き換え、長さを抑える)</summary>
    public static string Create(DateTime started, string? comment)
    {
        var name = started.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var c = Sanitize(comment);
        return c.Length > 0 ? name + Separator + c : name;
    }

    public const int MaxCommentLength = 40;

    private static string Sanitize(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in comment.Trim())
            sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.');   // Windows ではフォルダ名の末尾の . と空白は使えない
        if (s.Length > MaxCommentLength) s = s[..MaxCommentLength].TrimEnd().TrimEnd('.');
        return s;
    }
}
