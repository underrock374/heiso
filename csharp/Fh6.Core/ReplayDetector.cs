using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fh6.Core;

/// <summary>
/// 記録の動画の後半に入っているゲームのリプレイ(追従カメラ)の位置を、自動で見つける(作業指示 008 §4.2)。ffmpeg が要る。
///
/// 1. 選んだレースのゴールより後で、黒の区間を探す(リプレイはゴールの瞬間に真っ暗になって先頭に戻る)。
///    黒の始まり e のうち、e − T がゴールの MinAfterFinishSec 秒後より後(リプレイの窓がリザルト画面の後に収まる)の最初のものを、リプレイの終わりとする。
///    T は最終の時間軸(RaceTimeline)の長さ。リプレイの先頭(レースタイム 0)の動画の秒は e − T
/// 2. 検算: 窓 [e − T − 5, e + 1] の動画の動き(フレーム差)と、最終の時間軸の角速度の大きさ(1 秒の移動平均)の相関を、遅れ ±LagSearchSec で探す。
///    ピークが CorrMin 以上で |遅れ| が LagMaxSec 以下なら確度「高」、通らなければ「低」。
///    先頭はどちらも黒で決めた e − T のまま(遅れでは直さない)。リプレイはゴールの瞬間に黒になるので e はコマの単位で正確で、
///    遅れは動きと角速度の出方の差(実記録で −0.2〜−0.3 秒)を含み、先頭を直すとかえってずれる(2026-10-10、作業指示 008 から変えた)
///
/// 閾値は実記録 2 本(2026-10-10、糠平トレイル AD・MD)で決めた暫定値。docs/telemetry-field-notes.md「リプレイ録画」
/// </summary>
public static partial class ReplayDetector
{
    /// <summary>黒の検出(ffmpeg の blackdetect の pic_th と d)</summary>
    public const double BlackPictureThreshold = 0.92;
    public const double BlackMinSec = 0.1;
    /// <summary>リプレイの先頭は、ゴールのこの秒数より後(ゴール直後の短い黒 = リザルトへの遷移を外す)</summary>
    public const double MinAfterFinishSec = 5;
    /// <summary>ゴールの後、黒を探す長さ = T + これ(リザルト画面とメニューの分)</summary>
    public const double SearchExtraSec = 300;
    /// <summary>動きの検算: 相関のピークがこれ以上、遅れがこれ以下なら確度「高」</summary>
    public const double CorrMin = 0.30;
    public const double LagMaxSec = 1.0;
    /// <summary>遅れを探す範囲(±秒)と刻み</summary>
    public const double LagSearchSec = 3;
    public const double LagStepSec = 0.1;
    /// <summary>動きを取るフレームレートと大きさ</summary>
    public const int MotionFps = 10;
    /// <summary>リプレイの長さと T の差がこれを超えたら警告</summary>
    public const double LengthToleranceSec = 1.0;
    /// <summary>角速度の大きさの移動平均の幅(秒)</summary>
    public const double SmoothSec = 1.0;

    /// <summary>使う列(RaceTimeline の列に足す)</summary>
    public static readonly string[] Columns = { "angular_velocity_x", "angular_velocity_y", "angular_velocity_z" };

    public sealed record Result(
        double ZeroVideoSec, double EndVideoSec, double CleanDurationSec,
        string Method, string Confidence, double? Corr, double? LagSec, string? Warning);

    /// <summary>
    /// リプレイを探す。見つからなければ null(リプレイの途中で録画を止めた、など。手動で合わせてもらう)。
    /// searchEndVideoSec は探す範囲の終わり(次のレースのスタートの動画の秒。最後のレースなら null = 録画の終わりまで)。
    /// リプレイはそのレースの直後(次のレースが始まる前)にしか入らないので、次のレースの映像の黒をリプレイと取り違えないように区切る(2026-10-11、164031)。
    /// progress は 0〜1(黒の検出が 0〜0.7、動きが 0.7〜1)。中止されたら OperationCanceledException
    /// </summary>
    public static Result? Detect(string ffmpeg, string video, TelemetryTable t, RaceTimeline timeline, double finishVideoSec,
                                 IProgress<double>? progress = null, CancellationToken ct = default, double? searchEndVideoSec = null)
    {
        double T = timeline.DurationSec;
        if (T <= 0) return null;

        double searchSec = T + SearchExtraSec;
        if (searchEndVideoSec is double end)
        {
            searchSec = Math.Min(searchSec, end - finishVideoSec);
            // リザルトの画面の後にリプレイがまるごと入る長さが無い
            if (searchSec < T + MinAfterFinishSec) { progress?.Report(1); return null; }
        }
        var blackArgs = string.Join(' ',
            "-hide_banner -nostats -v info -progress pipe:1",
            $"-ss {F(finishVideoSec)} -i \"{video}\" -t {F(searchSec)}",
            $"-map 0:v:0 -an -sn -dn -vf \"scale=320:180,blackdetect=d={F(BlackMinSec)}:pic_th={F(BlackPictureThreshold)}\" -f null -");
        var (_, blackErr) = Run(ffmpeg, blackArgs, searchSec, p => progress?.Report(p * 0.7), ct);
        var blacks = ParseBlackDetect(blackErr).Select(b => (b.Start + finishVideoSec, b.End + finishVideoSec)).ToList();
        if (PickBlackStart(blacks, finishVideoSec, T, searchEndVideoSec) is not double e) return null;

        // 動きの検算
        double w0 = Math.Max(0, e - T - 5), w1 = e + 1;
        var motionArgs = string.Join(' ',
            "-hide_banner -nostats -v error",
            $"-ss {F(w0)} -i \"{video}\" -t {F(w1 - w0)}",
            $"-map 0:v:0 -an -sn -dn -vf \"fps={MotionFps},scale=160:90,format=gray,tblend=all_mode=difference,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-\" -f null -");
        var (motionOut, _) = Run(ffmpeg, motionArgs, w1 - w0, p => progress?.Report(0.7 + p * 0.3), ct, ptsFromMetadata: true);
        var motion = ParseSignalStats(motionOut).Select(m => (m.Time + w0, m.Value)).ToList();

        var (times, signal) = AngularSignal(t, timeline);
        var (corr, lag) = BestLag(motion, times, signal, e - T);
        progress?.Report(1);
        return Decide(e, T, corr, lag);
    }

    /// <summary>黒と相関から結果を決める(テストできるように分けた)</summary>
    public static Result Decide(double blackStart, double T, double? corr, double? lag)
    {
        bool high = corr is double c && lag is double l && c >= CorrMin && Math.Abs(l) <= LagMaxSec;
        double zero = blackStart - T;
        double length = blackStart - zero;
        string? warning = Math.Abs(length - T) > LengthToleranceSec
            ? Strings.T("リプレイの長さ {len:F1} 秒が、レースタイム {t:F2} 秒と合いません", ("len", length), ("t", T))
            : null;
        return new Result(Math.Round(zero, 3), Math.Round(blackStart, 3), Math.Round(T, 3),
            high ? VideoAltSync.MethodAutoBlackMotion : VideoAltSync.MethodAutoBlack,
            high && warning == null ? "high" : "low",
            corr is double cc ? Math.Round(cc, 3) : null, lag is double ll ? Math.Round(ll, 2) : null, warning);
    }

    /// <summary>ffmpeg の blackdetect の出力(標準エラー)から、黒の区間(入力の先頭からの秒)</summary>
    public static List<(double Start, double End)> ParseBlackDetect(string stderr)
    {
        var list = new List<(double, double)>();
        foreach (Match m in BlackLine().Matches(stderr))
            list.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        return list;
    }

    /// <summary>リプレイの終わり(黒の始まり)。e − T ≥ ゴール + MinAfterFinishSec の最初の黒。無ければ null</summary>
    public static double? PickBlackStart(IEnumerable<(double Start, double End)> blacks, double finishVideoSec, double T, double? searchEndVideoSec = null)
    {
        foreach (var (s, _) in blacks.OrderBy(b => b.Start))
            if (s - T >= finishVideoSec + MinAfterFinishSec && !(s > searchEndVideoSec)) return s;
        return null;
    }

    /// <summary>ffmpeg の metadata=print の出力から、各コマの時刻(入力の先頭からの秒)と YAVG</summary>
    public static List<(double Time, double Value)> ParseSignalStats(string stdout)
    {
        var list = new List<(double, double)>();
        double? pts = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            var m = PtsLine().Match(line);
            if (m.Success) { pts = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (pts is double p && line.StartsWith("lavfi.signalstats.YAVG=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan("lavfi.signalstats.YAVG=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                list.Add((p, v));
                pts = null;
            }
        }
        return list;
    }

    /// <summary>最終の時間軸の、角速度の大きさ √(ωx²+ωy²+ωz²) の 1 秒の移動平均。時刻はレースタイム</summary>
    public static (double[] RaceTime, double[] Value) AngularSignal(TelemetryTable t, RaceTimeline timeline)
    {
        var wx = t["angular_velocity_x"]; var wy = t["angular_velocity_y"]; var wz = t["angular_velocity_z"];
        int n = timeline.Rows.Count;
        var time = timeline.RaceTime.ToArray();
        var raw = new double[n];
        for (int k = 0; k < n; k++)
        {
            int i = timeline.Rows[k];
            raw[k] = Math.Sqrt(wx[i] * wx[i] + wy[i] * wy[i] + wz[i] * wz[i]);
        }
        // 時刻で ±SmoothSec/2 の平均(行の間隔は一定でないので、行の数ではなく時刻で)
        var smooth = new double[n];
        int lo = 0, hi = 0;
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            while (hi < n && time[hi] <= time[k] + SmoothSec / 2) { sum += raw[hi]; hi++; }
            while (time[lo] < time[k] - SmoothSec / 2) { sum -= raw[lo]; lo++; }
            smooth[k] = sum / (hi - lo);
        }
        return (time, smooth);
    }

    /// <summary>
    /// 動画の動き(動画の秒, 値)と、テレメトリーの信号(レースタイム, 値)の相関を、先頭の候補 zeroGuess から遅れ ±LagSearchSec で探す。
    /// 遅れ L のとき、動画の秒 v はレースタイム v − (zeroGuess + L) に当たる。相関が出せなければ (null, null)
    /// </summary>
    public static (double? Corr, double? Lag) BestLag(IReadOnlyList<(double Time, double Value)> motion, double[] raceTime, double[] signal, double zeroGuess)
    {
        if (motion.Count < 10 || raceTime.Length < 10) return (null, null);
        double T = raceTime[^1];
        double? best = null, bestLag = null;
        for (double lag = -LagSearchSec; lag <= LagSearchSec + 1e-9; lag += LagStepSec)
        {
            double zero = zeroGuess + lag;
            var xs = new List<double>(); var ys = new List<double>();
            foreach (var (v, m) in motion)
            {
                double r = v - zero;
                if (r < raceTime[0] || r > T) continue;
                xs.Add(m);
                ys.Add(Interp(raceTime, signal, r));
            }
            if (xs.Count < 10) continue;
            var c = Pearson(xs, ys);
            if (c is double cc && (best == null || cc > best)) { best = cc; bestLag = Math.Round(lag, 2); }
        }
        return (best, bestLag);
    }

    private static double? Pearson(List<double> x, List<double> y)
    {
        double mx = x.Average(), my = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Count; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : null;
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        int i = Array.BinarySearch(xs, x);
        if (i >= 0) return ys[i];
        i = ~i;
        if (i <= 0) return ys[0];
        if (i >= xs.Length) return ys[^1];
        double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[i - 1] + t * (ys[i] - ys[i - 1]);
    }

    /// <summary>ffmpeg を動かして標準出力と標準エラーを集める。進み具合は -progress の out_time_us か、metadata の pts_time から</summary>
    private static (string Out, string Err) Run(string ffmpeg, string args, double durationSec, Action<double> progress,
                                               CancellationToken ct, bool ptsFromMetadata = false)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Strings.T("ffmpeg を起動できませんでした"));
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });

        var outText = new StringBuilder();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            outText.AppendLine(line);
            double? at = null;
            if (!ptsFromMetadata && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                at = us / 1e6;
            else if (ptsFromMetadata && PtsLine().Match(line) is { Success: true } m)
                at = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (at is double a && durationSec > 0) progress(Math.Clamp(a / durationSec, 0, 1));
        }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        string errText;
        lock (err) errText = err.ToString();
        if (p.ExitCode != 0)
        {
            var tail = string.Join(" / ", errText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            throw new InvalidOperationException(Strings.T("ffmpeg が失敗しました(終了コード {code}): {detail}", ("code", p.ExitCode), ("detail", tail)));
        }
        return (outText.ToString(), errText);
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"black_start:\s*([0-9.]+)\s+black_end:\s*([0-9.]+)")]
    private static partial Regex BlackLine();
    [GeneratedRegex(@"pts_time:\s*([0-9.]+)")]
    private static partial Regex PtsLine();
}
