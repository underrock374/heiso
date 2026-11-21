using System.Diagnostics;
using System.IO;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>長くかかる処理(書き出し・リプレイを探す)を、画面の「中止」で止めるための札。処理の間だけ Current に入れる</summary>
public sealed class JobSlot
{
    public CancellationTokenSource? Current { get; set; }

    public void Cancel() => Current?.Cancel();
}

/// <summary>配布パッケージを書く処理(PackageWriter.Write と同じ形。テストでは偽物に差し替える)</summary>
public delegate PackageResult PackageWrite(string sessionFolder, double raceStartRecvTime, string destPath,
    PackageReencodeOptions? reencode, IProgress<double>? progress, CancellationToken ct, PackageSource? source, bool noAudio);

/// <summary>
/// 選んでいるレースを配布パッケージに書き出す(書き出しのダイアログ)。reencode なら ffmpeg で 720p に作り直す(数分かかるので、進み具合と中止を画面に出す)。
/// activeAlt は表示している割り当てた動画(録画なら null。MainWindow.Alt.cs と一緒に、ステップ 3 の 5 本目で移す)
/// </summary>
public sealed class ExportViewModel(IShell shell, PlayerSettings settings, OpenedSessionViewModel opened, SettingsViewModel prefs,
    JobSlot job, Func<VideoAlt?> activeAlt, PackageWrite? write = null)
{
    private readonly PackageWrite _write = write ?? ((f, t, d, r, p, ct, s, n) => PackageWriter.Write(f, t, d, r, p, ct, s, n));
    private bool _exporting;

    /// <summary>画面からのメッセージのうち、書き出しを扱う。扱わないメッセージなら null</summary>
    public Task? Handle(ViewMessage msg) => msg switch
    {
        ViewExport { Race: int race } m => ExportAsync(race, m.Mode == "720", m.NoAudio == true),
        _ => null,
    };

    private async Task ExportAsync(int raceIndex, bool reencode, bool noAudio)
    {
        var s = opened.Session;
        if (s == null || _exporting) return;
        if (s.Package != null) { Toast(error: Strings.T("パッケージから、もう一度パッケージは作れません。元の記録を開いてください")); return; }
        // 割り当てた動画を表示していれば、その動画を書き出す(レースは割り当てたレース。リプレイの表示ではレースが 1 本だけなので)
        PackageSource? source = null;
        var alt = activeAlt();
        if (alt != null)
        {
            raceIndex = s.Races.FindIndex(r => Math.Abs(r.Segment.StartRecvTime - alt.RaceStartRecvTime) < 0.01);
            var loc = alt.Locate(s.Folder);
            if (loc.Status != VideoAltStatus.Found || raceIndex < 0) { Toast(error: Strings.T("動画が見つかりません。右の欄「この動画」の「動画を選び直す…」で選び直してください")); return; }
            source = PackageSource.FromAlt(alt, loc.FoundPath!);
        }
        if (raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var race = s.Races[raceIndex];
        settings.ExportMode = reencode ? "720" : "copy";
        settings.Save();

        // 720p: ffmpeg とエンコーダを先に決める(保存先を選んだ後で「使えない」と言わないように)
        PackageReencodeOptions? options = null;
        if (reencode)
        {
            var st = await prefs.FfmpegStatusAsync(force: false);
            if (st.Path == null) { Toast(error: Strings.T("ffmpeg が見つかりません。設定の「書き出し」で ffmpeg の場所を指定してください")); return; }
            var enc = st.Selected;
            if (enc == null) { Toast(error: Strings.T("この PC で使える H.264 のエンコーダが ffmpeg にありません(設定の「書き出し」で確かめられます)")); return; }
            double fps = (source != null ? source.Fps : s.Fps) ?? 30;
            options = new PackageReencodeOptions(st.Path, new Ffmpeg.Options(enc, VideoKbps: Ffmpeg.DefaultVideoKbps(fps)));
        }

        var name = PackageWriter.DefaultFileName(Path.GetFileName(s.Folder), race.Course, race.Car);
        if (source?.IsReplay == true) name = name[..^PackageInfo.Extension.Length] + "_replay" + PackageInfo.Extension;
        if (reencode) name = name[..^PackageInfo.Extension.Length] + "_720p" + PackageInfo.Extension;
        var path = await shell.PickSaveFileAsync(
            reencode ? Strings.T("配布パッケージ(720p)の保存先") : Strings.T("配布パッケージの保存先"),
            $"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}",
            name, settings.ExportFolder(s.Folder));
        if (path == null || _exporting) return;
        // 名前の最後を .heiso.zip にそろえる(「abc」や「abc.zip」と入れられても)
        if (!path.EndsWith(PackageInfo.Extension, StringComparison.OrdinalIgnoreCase))
            path = (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path) + PackageInfo.Extension;

        _exporting = true;
        using var cts = new CancellationTokenSource();
        job.Current = cts;
        var what = reencode ? Strings.T("{n} レース目を 720p で書き出しています({encoder})", ("n", raceIndex + 1), ("encoder", Ffmpeg.EncoderName(options!.Encode.Encoder)))
                            : Strings.T("{n} レース目を書き出しています", ("n", raceIndex + 1));
        Job(what, 0, cancellable: reencode);
        PlayerLog.Info($"書き出す: {s.Folder} のレース {raceIndex + 1}(開始 {race.Segment.StartRecvTime:F3})→ {path}{(reencode ? $"(720p、{options!.Encode.Encoder}、{options.Encode.VideoKbps} kbps)" : "")}"
                       + $"{(source != null ? $"、動画 {alt!.Id}({source.Content})" : "")}{(noAudio ? "、音声なし" : "")}");
        // 進み具合は 0.5 秒に 1 回だけ画面へ(ffmpeg の知らせは細かいので)
        double lastSent = -1;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(v =>
        {
            if (v < 1 && sw.Elapsed.TotalSeconds - lastSent < 0.5) return;
            lastSent = sw.Elapsed.TotalSeconds;
            Job(what, v, cancellable: reencode);
        });
        var dest = path;
        try
        {
            var r = await Task.Run(() => _write(s.Folder, race.Segment.StartRecvTime, dest, options, progress, cts.Token, source, noAudio));
            settings.LastExportDir = Path.GetDirectoryName(dest);
            settings.Save();
            PlayerLog.Info($"書き出した: {r.Bytes / 1e6:F1} MB、動画 {r.Info.Video.CutFromSec:F2} 秒から {r.Info.Video.DurationSec:F1} 秒、テレメトリー {r.TelemetryRows} 行、{r.ElapsedSec:F1} 秒");
            Job(null);
            var done = Strings.T("書き出しました: {file}({mb:F0} MB、{sec:F0} 秒)。場所: {dir}", ("file", Path.GetFileName(dest)), ("mb", r.Bytes / 1e6), ("sec", r.ElapsedSec), ("dir", Path.GetDirectoryName(dest)));
            // 作り直さずに切り出したリプレイの頭が、キーフレームまで大きく遅れた
            if (r.ReplayLateSec is double late && late >= PackageWriter.ReplayLateWarnSec)
                done += " " + Strings.T("動画の頭がリプレイの先頭より {sec:F1} 秒遅れています(キーフレームの間隔のため)。「720p に縮める」で書き出すと、先頭のコマから始まります", ("sec", late));
            Toast(message: done, sec: 12);
        }
        catch (OperationCanceledException)
        {
            PlayerLog.Info($"書き出しを中止した: {dest}");
            Job(null);
            Toast(message: Strings.T("書き出しを中止しました"));
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"書き出せない: {dest}", ex);
            Job(null);
            Toast(error: ex is PackageException ? ex.Message : Strings.T("書き出せませんでした: {message}", ("message", ex.Message)));
        }
        finally
        {
            _exporting = false;
            job.Current = null;
        }
    }

    /// <summary>長くかかる処理の進み具合を画面の下に出す(text が null なら消す)</summary>
    private void Job(string? text, double progress = 0, bool cancellable = false) =>
        shell.Post(new HostJob(text, Math.Round(progress, 3), cancellable));

    /// <summary>画面の下に短く知らせる。sec は出しておく秒数</summary>
    private void Toast(string? message = null, string? error = null, double? sec = null) => shell.Post(new HostToast(message, error, sec));
}
