using System.Text;
using System.Text.RegularExpressions;

namespace Fh6.Architecture.Tests;

/// <summary>
/// 例外の一覧との照合。今の違反の数と一覧が完全に一致しなければ落とす。
/// 増えたら違反を直す。減ったら一覧も減らす(一覧は減らすだけで、増やさない。docs/architecture.md のルール 11)
/// </summary>
internal static class Ratchet
{
    public static Dictionary<string, int> Count(IEnumerable<string> files, Regex regex)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var n = regex.Matches(SourceTree.ReadCode(f)).Count;
            if (n > 0) found[f] = n;
        }
        return found;
    }

    public static void Check(string rule, IReadOnlyDictionary<string, int> allowed, IReadOnlyDictionary<string, int> found)
    {
        var problems = Compare(allowed, found);
        if (problems.Count == 0) return;
        var msg = new StringBuilder($"{rule}\n");
        foreach (var p in problems) msg.Append("  ").Append(p).Append('\n');
        Assert.Fail(msg.ToString());
    }

    public static List<string> Compare(IReadOnlyDictionary<string, int> allowed, IReadOnlyDictionary<string, int> found)
    {
        var problems = new List<string>();
        foreach (var f in allowed.Keys.Union(found.Keys).Order(StringComparer.Ordinal))
        {
            var a = allowed.GetValueOrDefault(f);
            var n = found.GetValueOrDefault(f);
            if (n > a) problems.Add($"{f}: 違反が {n} か所(一覧は {a})。ルールに合う形に直す。一覧を増やさない");
            else if (n < a) problems.Add($"{f}: 違反が {n} か所に減った(一覧は {a})。ExceptionList.cs を {n} に減らす");
        }
        return problems;
    }
}
