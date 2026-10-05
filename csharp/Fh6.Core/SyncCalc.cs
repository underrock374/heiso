namespace Fh6.Core;

/// <summary>
/// 録画開始イベント・sync_samples・録画ファイルの長さから、同期情報をまとめる。
/// 動画 0 秒は録画開始イベントの受信時刻(2026-09-28 確定。sync_samples は約 1 秒遅れて報告される)。
/// </summary>
public static class SyncCalc
{
    /// <summary>往復がこれより遅い問い合わせは、時刻の誤差が大きいので使わない</summary>
    private const double MaxRttMs = 150;

    public const string ZeroFromStartedEvent = "started_event";
    public const string ZeroFromSyncSamples = "sync_samples";

    public static void Finalize(VideoInfo v)
    {
        var good = v.SyncSamples.Where(x => x.Length >= 3 && x[2] <= MaxRttMs).ToList();
        if (good.Count == 0) good = v.SyncSamples.Where(x => x.Length >= 2).ToList();

        if (v.StartedEventRecvTime is double se)
        {
            v.VideoZeroRecvTime = Math.Round(se, 4);
            v.VideoZeroMethod = ZeroFromStartedEvent;
        }
        else if (good.Count > 0)
        {
            // 録画開始イベントが無いときだけ: 問い合わせ時刻 − その時点の動画の長さ(最初の5点の中央値)。約 1 秒遅い値になる
            var early = good.Take(5).Select(x => x[0] - x[1]).OrderBy(x => x).ToList();
            v.VideoZeroRecvTime = Math.Round(early[early.Count / 2], 4);
            v.VideoZeroMethod = ZeroFromSyncSamples;
        }

        v.ClockRate = good.Count >= 3 ? Math.Round(Slope(good), 5) : null;

        // 終端の検算: 動画 0 秒から停止要求までの実時間と、ファイルの実際の長さの差
        var end = v.StopRequestedRecvTime ?? v.StoppedEventRecvTime;
        v.EndCheckSec = null;
        if (v.FileDurationSec is double fd && end is double e && v.VideoZeroRecvTime is double z)
            v.EndCheckSec = Math.Round((e - z) - fd, 3);
    }

    private static double Slope(List<double[]> pts)
    {
        double mx = pts.Average(p => p[0]), my = pts.Average(p => p[1]);
        double sxy = 0, sxx = 0;
        foreach (var p in pts)
        {
            sxy += (p[0] - mx) * (p[1] - my);
            sxx += (p[0] - mx) * (p[0] - mx);
        }
        return sxx > 0 ? sxy / sxx : 1.0;
    }
}
