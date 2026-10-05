namespace Fh6.Core;

/// <summary>テレメトリー時刻 → 動画時刻の変換方式</summary>
public enum VideoSyncMethod
{
    /// <summary>OBS の録画開始イベントの受信時刻を動画 0 秒とする(既定。2026-09-28 確定)</summary>
    StartedEvent,
    /// <summary>sync_samples の区分線形補間(比較用。OBS の報告が約 1 秒遅れるので、約 1 秒ずれる)</summary>
    SyncSamples,
}

/// <summary>
/// テレメトリー時刻(recv_time)と動画内の秒の相互変換。変換はこのクラスだけで行う。
/// 方法は docs/session-format.md の「video」の節のとおり。manual_sync があれば最後の1点で全体を平行にずらす。
/// 録画開始イベントの時刻が無い記録では、sync_samples で代用する(IsFallback)。
/// </summary>
public sealed class VideoTimeMap
{
    /// <summary>往復がこれより遅い問い合わせは、時刻の誤差が大きいので使わない(SyncCalc と同じ)</summary>
    public const double MaxRttMs = 150;

    // 対応点(recv_time 昇順、動画秒も狭義単調増加)。範囲外は傾き 1 で延長する
    private readonly double[] _recv;
    private readonly double[] _video;

    public VideoSyncMethod Method { get; }

    /// <summary>変換に使った sync_samples の点数(録画開始イベントを使ったときは 0)</summary>
    public int SamplesUsed { get; }

    /// <summary>選んだ方式の基準が無く、別の基準で代用した</summary>
    public bool IsFallback { get; }

    /// <summary>manual_sync によるずらし量(秒)。補正なしなら 0</summary>
    public double ManualShiftSec { get; }

    public bool HasManualSync { get; }

    private VideoTimeMap(VideoSyncMethod method, double[] recv, double[] video, int samplesUsed, bool fallback,
                         double manualShift, bool hasManual)
    {
        Method = method;
        _recv = recv;
        _video = video;
        SamplesUsed = samplesUsed;
        IsFallback = fallback;
        ManualShiftSec = manualShift;
        HasManualSync = hasManual;
    }

    /// <summary>変換の基準が何も無ければ null(error に理由)</summary>
    public static VideoTimeMap? Create(VideoInfo v, VideoSyncMethod method, out string? error)
    {
        error = null;
        double[] recv, video;
        int used = 0;
        bool fallback = false;

        var pts = UsablePoints(v.SyncSamples);
        if (method == VideoSyncMethod.StartedEvent && v.StartedEventRecvTime is double se)
        {
            recv = new[] { se };
            video = new[] { 0.0 };
        }
        else if (method == VideoSyncMethod.SyncSamples && pts.Count > 0)
        {
            recv = pts.Select(p => p.Recv).ToArray();
            video = pts.Select(p => p.Video).ToArray();
            used = pts.Count;
        }
        else if (method == VideoSyncMethod.StartedEvent && pts.Count > 0)
        {
            // 録画開始イベントの時刻が無い: sync_samples で代用(約 1 秒ずれる)
            recv = pts.Select(p => p.Recv).ToArray();
            video = pts.Select(p => p.Video).ToArray();
            used = pts.Count;
            fallback = true;
        }
        else if ((v.StartedEventRecvTime ?? v.VideoZeroRecvTime) is double zero)
        {
            recv = new[] { zero };
            video = new[] { 0.0 };
            fallback = true;
        }
        else
        {
            error = Strings.T("録画開始イベントの時刻も、動画との対応(sync_samples)も記録されていません");
            return null;
        }

        var auto = new VideoTimeMap(method, recv, video, used, fallback, 0, false);
        var last = v.ManualSync.LastOrDefault();
        if (last == null) return auto;

        double shift = last.VideoSec - auto.ToVideoSec(last.RecvTime);
        return new VideoTimeMap(method, recv, video, used, fallback, shift, true);
    }

    /// <summary>往復 150ms 以下(往復が無い古い点は採用)で、recv_time と動画秒がともに前の点より増えている点だけ</summary>
    private static List<(double Recv, double Video)> UsablePoints(List<double[]> samples)
    {
        var sorted = samples
            .Where(s => s.Length >= 2 && double.IsFinite(s[0]) && double.IsFinite(s[1]))
            .Where(s => s.Length < 3 || s[2] <= MaxRttMs)
            .OrderBy(s => s[0]);
        var list = new List<(double, double)>();
        foreach (var s in sorted)
        {
            if (list.Count > 0 && (s[0] <= list[^1].Item1 || s[1] <= list[^1].Item2)) continue;
            list.Add((s[0], s[1]));
        }
        return list;
    }

    public double ToVideoSec(double recvTime) => Interp(_recv, _video, recvTime) + ManualShiftSec;

    public double ToRecvTime(double videoSec) => Interp(_video, _recv, videoSec - ManualShiftSec);

    /// <summary>xs は狭義単調増加。範囲外は傾き 1 で延長</summary>
    private static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0] + (x - xs[0]);
        if (x >= xs[^1]) return ys[^1] + (x - xs[^1]);
        int i = Array.BinarySearch(xs, x);
        if (i >= 0) return ys[i];
        i = ~i; // xs[i-1] < x < xs[i]
        double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[i - 1] + t * (ys[i] - ys[i - 1]);
    }
}
