using System.Text.RegularExpressions;

namespace Fh6.Architecture.Tests;

/// <summary>
/// C# と JS のメッセージの名前の一致。C# の側は、メッセージの型から作った JS の型定義(Fh6.Player.App/messages.d.ts)から読む
/// (型定義が C# の型と同じかは Fh6.Player.App.Tests の MessageTypesTests が確かめる)。
/// プロパティの一致は、JS の型の検査(tools/dev/typecheck.mjs)が確かめる
/// </summary>
public partial class MessageContractTests
{
    [GeneratedRegex(@"\bsendToHost\(\s*\{\s*type:\s*""(\w+)""")]
    private static partial Regex JsSend();

    [GeneratedRegex(@"\bsendToHost\(")]
    private static partial Regex JsSendAny();

    [GeneratedRegex(@"\bm\.type\s*===\s*""(\w+)""")]
    private static partial Regex JsReceive();

    /// <summary>messages.d.ts の type の値。View… は C# が受けるもの、Host… は C# が送るもの</summary>
    [GeneratedRegex(@"^interface View\w+ \{\n  type: ""(\w+)"";", RegexOptions.Multiline)]
    private static partial Regex CsharpReceive();

    [GeneratedRegex(@"^interface Host\w+ \{\n  type: ""(\w+)"";", RegexOptions.Multiline)]
    private static partial Regex CsharpSend();

    private static SortedSet<string> Names(IEnumerable<string> files, Regex regex) =>
        new(files.SelectMany(f => regex.Matches(SourceTree.ReadCode(f))).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static IEnumerable<string> Js => SourceTree.Files("Fh6.Player/wwwroot", ".js");

    private static SortedSet<string> Defined(Regex regex) =>
        new(regex.Matches(SourceTree.Read("Fh6.Player.App/messages.d.ts").Replace("\r\n", "\n")).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static void AssertSame(string sender, SortedSet<string> sent, string receiver, SortedSet<string> received)
    {
        var problems = sent.Except(received).Select(n => $"{sender} が送る {n} を {receiver} が受けていない")
            .Concat(received.Except(sent).Select(n => $"{receiver} が受ける {n} を {sender} が送っていない")).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void JSが送るメッセージとC_シャープが受けるメッセージが一致する()
    {
        var callers = Js.Where(f => Path.GetFileName(f) != "messaging.js").ToList();   // messaging.js には sendToHost の定義がある
        var sendCalls = callers.Sum(f => JsSendAny().Matches(SourceTree.ReadCode(f)).Count);
        var named = callers.Sum(f => JsSend().Matches(SourceTree.ReadCode(f)).Count);
        Assert.True(sendCalls == named, $"sendToHost が {sendCalls} か所、type を直接書いたものが {named} か所。この検査で読めない書き方をしている");
        AssertSame("JS", Names(Js, JsSend()), "C#", Defined(CsharpReceive()));
    }

    [Fact]
    public void C_シャープが送るメッセージとJSが受けるメッセージが一致する() =>
        AssertSame("C#", Defined(CsharpSend()), "JS", Names(Js, JsReceive()));
}
