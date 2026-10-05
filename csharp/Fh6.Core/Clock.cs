namespace Fh6.Core;

public static class Clock
{
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>epoch 秒(float)。ロガー v2 の recv_time と同じ形式</summary>
    public static double Now() => (DateTime.UtcNow.Ticks - EpochTicks) / 1e7;
}
