namespace Fh6.Player;

/// <summary>
/// グラフのスリップの段の、路面の逸脱に続くスリップの帯(前は画面の JS の excursionEnd)。
/// 帯は逸脱の始まりから、スリップが限界を下回るまで
/// </summary>
public static class SlipBand
{
    /// <summary>スリップの時刻が無いとき、帯を逸脱の始まりからこの秒数までは伸ばす</summary>
    public const double FallbackSec = 2;

    /// <summary>帯の終わり(動画の秒): スリップが限界を下回った時刻。無ければスリップの始まり、それも無ければ始まりから FallbackSec 秒。逸脱の終わりより前にはしない</summary>
    public static double End(double t0, double t1, double? slipT, double? slipEndT) =>
        Math.Max(t1, slipEndT ?? slipT ?? t0 + FallbackSec);
}
