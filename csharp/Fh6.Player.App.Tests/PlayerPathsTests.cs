using System.IO;

namespace Fh6.Player.App.Tests;

public class PlayerPathsTests
{
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 指定が無ければ既定の場所(string? variable) =>
        Assert.Equal(@"C:\Users\someone\AppData\Local\HEISO\Player", PlayerPaths.ResolveDataDir(variable, LocalAppData));

    [Fact]
    public void 指定があればそのフォルダ() =>
        Assert.Equal(@"D:\work\heiso", PlayerPaths.ResolveDataDir(@"D:\work\heiso", LocalAppData));

    [Fact]
    public void 相対パスの指定は絶対パスにする() =>
        Assert.Equal(Path.GetFullPath("work"), PlayerPaths.ResolveDataDir("work", LocalAppData));

    [Fact]
    public void 書き込む場所はすべて同じフォルダの下()
    {
        Assert.Equal(Path.Combine(PlayerPaths.DataDir, "player.json"), PlayerSettings.DefaultPath);
        Assert.Equal(Path.Combine(PlayerPaths.DataDir, "logs"), PlayerLog.Dir);
        Assert.All(new[] { PlayerPaths.SettingsFile, PlayerPaths.LogsDir, PlayerPaths.ProfilesDir, PlayerPaths.PackagesDir, PlayerPaths.WebViewDir },
            p => Assert.Equal(PlayerPaths.DataDir, Path.GetDirectoryName(p)));
    }
}
