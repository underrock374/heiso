using System.Runtime.CompilerServices;
using Fh6.Core;

namespace Fh6.Recorder.Tests;

internal static class TestSetup
{
    /// <summary>テストは日本語の画面で動かす(文言や OBS のプロファイル名を日本語で確かめるため。Windows の表示言語によらない)</summary>
    [ModuleInitializer]
    internal static void UseJapanese() => Strings.Use(Strings.Japanese);
}
