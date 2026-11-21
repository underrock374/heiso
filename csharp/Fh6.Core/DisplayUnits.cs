using System.Globalization;

namespace Fh6.Core;

/// <summary>
/// 画面の単位(記録アプリと再生アプリで共通)。記録のデータはメートル法(速度 km/h、距離と標高 m)のまま持ち、画面に出すときだけ直す。
/// 単位の設定の「自動」は、Windows の地域が米国か英国ならマイル、それ以外はメートル
/// (.NET の RegionInfo.IsMetric は英国をメートル法とするので、国の名前で決める)
/// </summary>
public static class DisplayUnits
{
    public const double KmhPerMph = 1.609344;

    private static readonly string[] ImperialRegions = ["US", "GB"];

    /// <summary>単位の「自動」がマイルになる地域か(2 文字の国の名前)</summary>
    public static bool AutoImperial(string? region) =>
        region != null && ImperialRegions.Contains(region.ToUpperInvariant());

    /// <summary>この PC(Windows の地域)で、単位の「自動」がマイルになるか</summary>
    public static bool AutoImperial() => AutoImperial(RegionInfo.CurrentRegion.TwoLetterISORegionName);

    /// <summary>速度の単位の設定("auto" / "kmh" / "mph")から、今使う単位("kmh" か "mph")</summary>
    public static string ResolveSpeed(string? setting, bool autoImperial) =>
        setting is "kmh" or "mph" ? setting : autoImperial ? "mph" : "kmh";

    /// <summary>速度の文字(整数に丸める。地域の形式によらない)。例: "294 km/h"、"183 mph"</summary>
    public static string SpeedText(double kmh, string unit) => unit == "mph"
        ? $"{(kmh / KmhPerMph).ToString("0", CultureInfo.InvariantCulture)} mph"
        : $"{kmh.ToString("0", CultureInfo.InvariantCulture)} km/h";
}
