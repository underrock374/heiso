using System.Runtime.CompilerServices;

namespace Fh6.Core.Tests;

internal static class TestSetup
{
    /// <summary>テストは日本語の画面で動かす(CI の英語の Windows でも、手元と同じ結果にするため。言語の決め方そのもののテストは StringsTests で行う)</summary>
    [ModuleInitializer]
    internal static void UseJapanese() => Strings.Use(Strings.Japanese);
}
