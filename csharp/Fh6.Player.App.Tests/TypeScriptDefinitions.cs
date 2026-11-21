using Fh6.TestTools;

namespace Fh6.Player.App.Tests;

/// <summary>
/// メッセージの C# の型(ViewMessages.cs と HostMessages.cs)から、JS の型定義(Fh6.Player.App/messages.d.ts)を作る。
/// JS は型の検査(tools/dev/typecheck.mjs)でこれを読み、送るメッセージ・受けるメッセージのプロパティを確かめる。
/// 書き方は csharp/TestTools/TypeScriptWriter.cs(記録アプリのテストと共通)
/// </summary>
internal static class TypeScriptDefinitions
{
    public static string Generate()
    {
        var g = new TypeScriptWriter(
            "自動で作ったファイル。手で直さない。",
            "元は csharp/Fh6.Player.App の ViewMessages.cs と HostMessages.cs。C# を変えたら、",
            "環境変数 HEISO_UPDATE_GOLDEN=1 を付けて dotnet test csharp/Fh6.Player.App.Tests を回すと書き直す(MessageTypesTests)");
        g.Union(typeof(ViewMessage), MessageJson.Kinds(typeof(ViewMessage)), "画面 → C#(messaging.js の sendToHost で送る)。省いた値は C# で既定の値になる", optional: true);
        g.Union(typeof(HostMessage), MessageJson.Kinds(typeof(HostMessage)), "C# → 画面(messaging.js の onHostMessage で受ける)", optional: false);
        g.Root(typeof(Fh6.Core.I18nData), "画面の言語の辞書(window.HEISO_I18N。型は Fh6.Core の Strings.cs)");
        g.Root(typeof(PageSettings), "画面の設定(window.HEISO_SETTINGS)");
        return g.ToString();
    }
}
