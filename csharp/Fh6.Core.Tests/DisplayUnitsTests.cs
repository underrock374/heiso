using Fh6.Core;

namespace Fh6.Core.Tests;

public sealed class DisplayUnitsTests
{
    [Theory]
    [InlineData("US", true)]
    [InlineData("gb", true)]
    [InlineData("JP", false)]
    [InlineData("DE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 自動がマイルになるのは米国と英国(string? region, bool expected) => Assert.Equal(expected, DisplayUnits.AutoImperial(region));

    [Theory]
    [InlineData("kmh", true, "kmh")]
    [InlineData("mph", false, "mph")]
    [InlineData("auto", true, "mph")]
    [InlineData("auto", false, "kmh")]
    [InlineData(null, true, "mph")]
    [InlineData("knots", false, "kmh")]   // 知らない値は自動
    public void 設定で選んだ単位が先_自動なら地域で決める(string? setting, bool autoImperial, string expected) =>
        Assert.Equal(expected, DisplayUnits.ResolveSpeed(setting, autoImperial));

    [Theory]
    [InlineData(293.6, "kmh", "294 km/h")]
    [InlineData(293.6, "mph", "182 mph")]
    [InlineData(0.4, "kmh", "0 km/h")]
    [InlineData(2.5, "kmh", "3 km/h")]   // JS の Math.round と同じく、ちょうど半分は上げる
    public void 速度の文字(double kmh, string unit, string expected) => Assert.Equal(expected, DisplayUnits.SpeedText(kmh, unit));
}
