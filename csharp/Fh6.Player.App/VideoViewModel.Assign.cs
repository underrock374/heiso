using System.Diagnostics;
using System.IO;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>VideoViewModel のうち、動画の割り当てと同期の操作(リプレイの自動判別、別に録った動画、同期を合わせる)</summary>
public sealed partial class VideoViewModel
{
    // ---- リプレイの自動判別 ----

    /// <summary>リプレイを探すレースの最短のレースタイム(これより短い断片は探さない)</summary>
    private const double ReplayMinRaceSec = 5;

    /// <summary>
    /// リプレイを探す。onlyRace を渡せばそのレースだけ(「探し直す」)、null なら記録の中の完走したレース全部。
    /// 各レースは、ゴールから次のレースのスタートまで(最後のレースは録画の終わりまで)だけを探す(リプレイはそのレースの直後にしか入らない)。
    /// 結果はレースごとに画面へ返し、利用者が選んで割り当てる
    /// </summary>
    private async Task DetectReplayAsync(int? onlyRace, int? jsRace)
    {
        var s = Session;
        if (s == null || _job.Current != null) return;
        if (s.VideoPath == null) { Toast(error: Strings.T("録画ファイルが見つからないため、リプレイを探せません")); return; }
        var map = VideoTimeMap.Create(s.Meta.Video, _opened.Method, out _);
        if (map == null) { Toast(error: Strings.T("動画との対応が取れないため、リプレイを探せません")); return; }
        var st = await _prefs.FfmpegStatusAsync(force: false);
        if (st.Path == null || st.Version == null)
        {
            Toast(error: Strings.T("リプレイを探すには ffmpeg が要ります。設定の「書き出し」で ffmpeg の場所を指定してください"), sec: 8);
            return;
        }

        var recv = s.Table[TelemetryTable.RecvTime];
        var targets = new List<(int Race, RaceTimeline Timeline, double Finish, double? SearchEnd)>();
        for (int k = 0; k < s.Races.Count; k++)
        {
            if (onlyRace is int only && k != only) continue;
            var seg = s.Races[k].Segment;
            if (onlyRace == null && !seg.LikelyFinished) continue;   // リプレイは完走したレースの後にしか見られない
            var tl = RaceTimeline.Build(s.Table, seg);
            if (tl.Rows.Count < 2 || tl.DurationSec < ReplayMinRaceSec) continue;
            double? end = k + 1 < s.Races.Count ? map.ToVideoSec(s.Races[k + 1].Segment.StartRecvTime) : null;
            targets.Add((k, tl, map.ToVideoSec(recv[seg.FinishIndex]), end));
        }
        if (targets.Count == 0) { Toast(error: Strings.T("リプレイを探せる完走したレースがありません")); return; }

        using var cts = new CancellationTokenSource();
        _job.Current = cts;
        var sw = Stopwatch.StartNew();
        var found = new List<ReplayFoundItem>();
        _lastDetect.Clear();
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (k, tl, finish, end) = targets[i];
                var what = targets.Count == 1
                    ? Strings.T("{n} レース目のリプレイを探しています", ("n", k + 1))
                    : Strings.T("{n} レース目のリプレイを探しています({i} / {total})", ("n", k + 1), ("i", i + 1), ("total", targets.Count));
                Job(what, (double)i / targets.Count, cancellable: true);
                double lastSent = -1;
                int index = i;
                var progress = new Progress<double>(v =>
                {
                    if (v < 1 && sw.Elapsed.TotalSeconds - lastSent < 0.5) return;
                    lastSent = sw.Elapsed.TotalSeconds;
                    Job(what, (index + v) / targets.Count, cancellable: true);
                });
                PlayerLog.Info($"リプレイを探す: レース {k + 1}、レースタイム {tl.DurationSec:F2} 秒(試行 {tl.Attempts})、ゴール 動画 {finish:F2} 秒、探す終わり {(end is double e ? $"{e:F2} 秒(次のレース)" : "録画の終わり")}");
                var video = s.VideoPath;
                var r = await Task.Run(() => ReplayDetector.Detect(st.Path, video, s.Table, tl, finish, progress, cts.Token, end));
                if (Session != s) return;   // 探している間に別の記録を開いた
                _lastDetect[k] = (s.Races[k].Segment.StartRecvTime, r, tl.DurationSec, finish);
                PlayerLog.Info(r == null ? $"レース {k + 1}: リプレイが見つからない"
                    : $"レース {k + 1}: リプレイ 先頭 {r.ZeroVideoSec:F3} 秒、終わり {r.EndVideoSec:F3} 秒、相関 {r.Corr}、遅れ {r.LagSec}、確度 {r.Confidence}、{r.Warning}");
                found.Add(new ReplayFoundItem(
                    Race: k, Found: r != null, Zero: r?.ZeroVideoSec, End: r?.EndVideoSec, Duration: tl.DurationSec,
                    Confidence: r?.Confidence, Corr: r?.Corr, Lag: r?.LagSec, Warning: r?.Warning));
            }
            Job(null);
            PlayerLog.Info($"リプレイを探し終えた: {targets.Count} レース、{sw.Elapsed.TotalSeconds:F1} 秒");
            _shell.Post(new HostReplayFound(found.ToArray(), onlyRace ?? jsRace, AssignedReplays(s)));
        }
        catch (OperationCanceledException)
        {
            Job(null);
            Toast(message: Strings.T("リプレイを探すのを中止しました"));
        }
        catch (Exception ex)
        {
            PlayerLog.Error("リプレイを探せない", ex);
            Job(null);
            Toast(error: Strings.T("リプレイを探せませんでした: {message}", ("message", ex.Message)));
        }
        finally
        {
            _job.Current = null;
        }
    }

    /// <summary>
    /// 録画の中のリプレイを割り当て済みのレースと、その今の合わせ方(「採用」はこれを作り直す)。
    /// spec「開き直したときの表示と、割り当て済みのリプレイ」の B
    /// </summary>
    public ReplayAssignedItem[] AssignedReplays() => Session is LoadedSession s ? AssignedReplays(s) : [];

    private ReplayAssignedItem[] AssignedReplays(LoadedSession s) =>
        (s.Meta.VideoAlt ?? [])
            .Where(a => a.Kind == VideoAlt.KindReplay)
            .Select(a => (Alt: a, Race: AltRaceIndex(a)))
            .Where(x => x.Race >= 0)
            .Select(x => new ReplayAssignedItem(
                Race: x.Race, Id: x.Alt.Id, Manual: x.Alt.Sync.Method == VideoAltSync.MethodManual,
                Zero: x.Alt.Sync.ReplayZeroVideoSec ?? 0, ShiftMs: Math.Round(x.Alt.Sync.ShiftSec * 1000),
                ShiftFrames: s.Fps is double f && f > 0 ? Math.Round(x.Alt.Sync.ShiftSec * f, 1) : null))
            .OrderBy(x => x.Race)
            .ToArray();

    /// <summary>
    /// そのレースにリプレイを割り当てる。自動判別の結果があればそれを、manual なら手動で合わせる
    /// (位置は自動判別の結果か、無ければゴールの少し後から)
    /// </summary>
    private async Task AdoptReplayAsync(int race, bool manual, double sec)
    {
        var s = Session;
        if (s == null || s.VideoPath == null || race < 0 || race >= s.Races.Count) return;
        var seg = s.Races[race].Segment;
        if (!_lastDetect.TryGetValue(race, out var d))
        {
            var map = VideoTimeMap.Create(s.Meta.Video, _opened.Method, out _);
            if (map == null) return;
            d = (seg.StartRecvTime, null, RaceTimeline.Build(s.Table, seg).DurationSec, map.ToVideoSec(s.Table[TelemetryTable.RecvTime][seg.FinishIndex]));
        }
        var (startRecv, r, duration, finish) = d;
        var alt = s.Meta.VideoAlt?.FirstOrDefault(a => a.Kind == VideoAlt.KindReplay && Math.Abs(a.RaceStartRecvTime - startRecv) < 0.01)
                  ?? new VideoAlt { Kind = VideoAlt.KindReplay, Content = VideoAlt.ContentReplay, RaceStartRecvTime = startRecv };
        alt.Sync = new VideoAltSync
        {
            Basis = VideoAltSync.BasisRaceTime,
            Method = manual || r == null ? VideoAltSync.MethodManual : r.Method,
            Confidence = manual || r == null ? null : r.Confidence,
            ReplayZeroVideoSec = r?.ZeroVideoSec ?? Math.Round(finish + ManualReplayGuessSec, 3),
            ReplayEndVideoSec = r?.EndVideoSec,
            CleanDurationSec = Math.Round(duration, 3),
            Check = r?.Corr is double c ? new VideoAltCheck { Corr = c, LagSec = r.LagSec ?? 0 } : null,
            // 映像(黒の始まり)で決めた先頭から、「同期の微調整」で設定の量だけ前へずらした状態にする(小窓に出る)
            ShiftSec = _settings.ReplayDefaultShiftSec(s.Fps),   // 録画の中のリプレイなので、録画のコマ
        };
        if (!await SetAltFileAsync(alt, s.VideoPath, s.Fps)) return;
        if (!SaveAlt(alt)) return;
        PlayerLog.Info($"リプレイを割り当てた: {alt.Id}、レース {race + 1}、{alt.Sync.Method}、先頭 {alt.Sync.ReplayZeroVideoSec:F3} 秒");
        await SelectVideoAsync(alt.Id, sec, race,
            manual || r == null ? Strings.T("リプレイの先頭のコマを出して「いまのコマ = リプレイの先頭」を押すと合います") : Strings.T("リプレイを割り当てました"));
    }

    // ---- 別の動画 ----

    private static string VideoFilter() =>
        $"{Strings.T("動画")} (*.mp4;*.m4v;*.mov;*.webm)|*.mp4;*.m4v;*.mov;*.webm|{Strings.T("すべてのファイル")} (*.*)|*.*";

    private async Task AddVideoFileAsync(string content, int raceIndex, double sec)
    {
        var s = Session;
        if (s == null || raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var title = content == VideoAlt.ContentReplay ? Strings.T("リプレイの動画を選んでください") : Strings.T("同じ走りを録った動画を選んでください");
        var path = await _shell.PickFileAsync(title, VideoFilter(), s.VideoPath != null ? Path.GetDirectoryName(s.VideoPath) : s.Folder);
        if (path == null || Session != s) return;
        var seg = s.Races[raceIndex].Segment;
        var alt = new VideoAlt { Kind = VideoAlt.KindExternal, Content = content, RaceStartRecvTime = seg.StartRecvTime };
        if (content == VideoAlt.ContentReplay)
        {
            var tl = RaceTimeline.Build(s.Table, seg);
            alt.Sync = new VideoAltSync
            {
                Basis = VideoAltSync.BasisRaceTime, Method = VideoAltSync.MethodManual,
                ReplayZeroVideoSec = 0, CleanDurationSec = Math.Round(tl.DurationSec, 3), ShiftSec = 0,
            };
        }
        else alt.Sync = new VideoAltSync { Basis = VideoAltSync.BasisRecvTime, Method = VideoAltSync.MethodManual };
        if (!await SetAltFileAsync(alt, path, null)) return;
        if (!SaveAlt(alt)) return;
        PlayerLog.Info($"動画を割り当てた: {alt.Id}({content})、レース {raceIndex + 1}、{path}");
        await SelectVideoAsync(alt.Id, sec, raceIndex, content == VideoAlt.ContentReplay
            ? Strings.T("リプレイの先頭のコマを出して「いまのコマ = リプレイの先頭」を押すと合います")
            : Strings.T("「同期の手動補正」で、映像とグラフの同じ瞬間を指して合わせてください"));
    }

    private async Task RelinkVideoAsync(string id, double sec, int? jsRace)
    {
        var s = Session;
        var alt = s?.Meta.VideoAlt?.FirstOrDefault(a => a.Id == id);
        if (s == null || alt == null) return;
        var path = await _shell.PickFileAsync(Strings.T("動画を選び直してください"), VideoFilter(), alt.Path != null ? Path.GetDirectoryName(alt.Path) : s.Folder);
        if (path == null || Session != s) return;
        if (!await SetAltFileAsync(alt, path, alt.Kind == VideoAlt.KindReplay ? s.Fps : null)) return;
        if (!SaveAlt(alt)) return;
        PlayerLog.Info($"動画を選び直した: {alt.Id} → {path}");
        _replay = null;
        await SelectVideoAsync(alt.Id, sec, jsRace, Strings.T("動画を選び直しました"));
    }

    private async Task RemoveVideoAsync(string id, double sec, int? jsRace)
    {
        var s = Session;
        if (s == null) return;
        bool active = _activeAltId == id;
        try
        {
            var error = SessionMeta.RemoveVideoAlt(s.Folder, id);
            if (error != null) { Toast(error: error); return; }
            s.Meta = SessionMeta.Load(s.Folder);
        }
        catch (Exception ex)
        {
            PlayerLog.Error("割り当てた動画を外せない", ex);
            Toast(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return;
        }
        PlayerLog.Info($"動画の割り当てを外した: {id}");
        if (active) await SelectVideoAsync(null, sec, jsRace, Strings.T("割り当てを外しました"));
        else { SendSession(keepVideo: true); Toast(message: Strings.T("割り当てを外しました")); }
    }

    /// <summary>動画の確認情報(サイズ・更新日時・長さ・fps)を入れる</summary>
    private async Task<bool> SetAltFileAsync(VideoAlt alt, string path, double? fps)
    {
        try
        {
            var folder = Session!.Folder;
            await Task.Run(() =>
            {
                alt.SetPath(folder, path);
                alt.File.DurationSec = VideoDuration.TryRead(path) is double d ? Math.Round(d, 3) : null;
                var f = fps ?? VideoDuration.TryReadFrameRate(path);
                alt.File.Fps = f is double ff ? Math.Round(ff, 3) : null;
            });
            return true;
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"動画を読めない: {path}", ex);
            Toast(error: Strings.T("動画を読めませんでした: {message}", ("message", ex.Message)));
            return false;
        }
    }

    /// <summary>session.json に保存して読み直す。保存できなければ知らせて false</summary>
    private bool SaveAlt(VideoAlt alt)
    {
        var s = Session!;
        try
        {
            var error = SessionMeta.SaveVideoAlt(s.Folder, alt);
            if (error != null) { Toast(error: error); return false; }
            s.Meta = SessionMeta.Load(s.Folder);
            return true;
        }
        catch (Exception ex)
        {
            PlayerLog.Error("割り当てた動画を session.json に書けない", ex);
            Toast(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return false;
        }
    }

    // ---- 同期を合わせる ----

    /// <summary>リプレイ: 今のコマを、リプレイの先頭(what = zero)かゴール(what = end)にする</summary>
    private void AltMark(string? what, double sec)
    {
        var view = CurrentView();
        var alt = view?.Alt;
        if (view == null || alt == null || !view.Session.IsReplay) return;
        double T = view.Session.Timeline!.DurationSec;
        // ゴール: 先頭 = ゴール − T にする。先頭: 先頭だけ変える(ゴールを決めてあれば、画面に間隔と T の差を出して検算する)
        alt.Sync.ReplayZeroVideoSec = Math.Round(what == "end" ? sec - T : sec, 3);
        if (what == "end") alt.Sync.ReplayEndVideoSec = Math.Round(sec, 3);
        alt.Sync.Method = VideoAltSync.MethodManual;
        alt.Sync.Confidence = null;
        // 映像のコマで決めたので、自動で見つけたときと同じく、設定の量だけ前へずらした状態から(小窓に出る)
        alt.Sync.ShiftSec = _settings.ReplayDefaultShiftSec(view.Fps);
        if (!SaveAlt(alt)) return;
        PlayerLog.Info($"リプレイの位置を手で決めた: {alt.Id}、{what}、先頭 {alt.Sync.ReplayZeroVideoSec:F3} 秒");
        SendSession(keepVideo: true);
        Notice(message: what == "end" ? Strings.T("このコマをゴールにしました") : Strings.T("このコマをリプレイの先頭にしました"));
    }

    /// <summary>
    /// 微調整: 対応を delta 秒ずらす(正なら、同じテレメトリーの瞬間に対応する動画の秒が増える。画面は映像を同じだけ送るので、グラフのカーソルの瞬間は変わらない)。
    /// リプレイは微調整(shift_sec)に足す(映像で決めた先頭・ゴールは変えない)。ライブは最後の同期の点の動画の秒を動かす(点が無ければ、今の仮の対応で点を作る)
    /// </summary>
    private void AltNudge(double delta)
    {
        var view = CurrentView();
        if (view?.Alt is not VideoAlt alt || view.Map == null || Math.Abs(delta) > 5) return;
        if (view.Session.IsReplay)
        {
            alt.Sync.ShiftSec = Math.Round(alt.Sync.ShiftSec + delta, 4);
            if (SaveAlt(alt)) SendSession(keepVideo: true);
            return;
        }
        if (alt.Sync.Points.Count > 0)
            alt.Sync.Points[^1].VideoSec = Math.Round(alt.Sync.Points[^1].VideoSec + delta, 4);
        else
            alt.Sync.Points.Add(new ManualSyncPoint
            {
                RecvTime = Math.Round(alt.RaceStartRecvTime, 4), VideoSec = Math.Round(view.Map.ToVideoSec(alt.RaceStartRecvTime) + delta, 4),
                Note = "微調整", CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            });
        alt.Sync.Method = VideoAltSync.MethodManual;
        alt.Sync.Confidence = null;
        if (SaveAlt(alt)) SendSession(keepVideo: true);
    }

    /// <summary>
    /// 「同期の手動補正」を、表示している割り当てた動画に保存する。ライブ: 同期の点を足す。リプレイ: 先頭の秒を直す。
    /// 録画を表示していれば false(録画の手動補正として扱う)
    /// </summary>
    private bool AltManualSync(double graphSec, double videoSec, string? note)
    {
        var view = CurrentView();
        if (view?.Alt is not VideoAlt alt || view.Map == null) return false;
        double recv = view.Map.ToRecvTime(graphSec);
        if (view.Session.IsReplay)
        {
            // 映像とグラフの同じ瞬間を指したので、ずれも含めてこの点で決まる(ずらし量は 0 から)
            double rt = view.Session.RaceTimeAtVirtual(recv);
            alt.Sync.ReplayZeroVideoSec = Math.Round(videoSec - rt, 3);
            alt.Sync.ShiftSec = 0;
        }
        else
            alt.Sync.Points.Add(new ManualSyncPoint
            {
                RecvTime = Math.Round(recv, 4), VideoSec = Math.Round(videoSec, 4),
                Note = note?.Trim() is { Length: > 0 } t ? t : null,
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            });
        alt.Sync.Method = VideoAltSync.MethodManual;
        alt.Sync.Confidence = null;
        if (SaveAlt(alt))
        {
            SendSession(keepVideo: true);
            Notice(message: Strings.T("手動補正を保存しました"));
        }
        return true;
    }

    /// <summary>「補正を消す」: ライブの割り当てた動画なら同期の点を消す。録画を表示していれば false</summary>
    private bool AltClearSync()
    {
        var view = CurrentView();
        if (view?.Alt is not VideoAlt alt) return false;
        if (view.Session.IsReplay) { Toast(error: Strings.T("リプレイは「探し直す」で自動の位置に戻せます")); return true; }
        alt.Sync.Points.Clear();
        if (SaveAlt(alt))
        {
            SendSession(keepVideo: true);
            Notice(message: Strings.T("手動補正を消しました"));
        }
        return true;
    }
}
