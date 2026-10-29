using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Fh6.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Fh6.Player;

/// <summary>
/// 録画のほかに割り当てた動画(session.json の video_alt。作業指示 008 ステップ 4)。
/// 上の欄の「動画」で、録画と割り当てた動画を切り替える。リプレイ(中身が replay)は、レースタイムでつないだ最終の時間軸の
/// 仮想セッション(LoadedSession.ForReplay)で、グラフ・印・コース図を作り直す。ライブ(中身が live)は記録のまま、同期の点だけ替える
/// </summary>
public partial class MainWindow
{
    private const string AltHost = "alt.heiso";

    /// <summary>割り当てた動画の同期の点がまだ無いとき、動画 0 秒をレース開始の何秒前と仮に置くか</summary>
    private const double AltLeadSec = 5;

    /// <summary>リプレイを見つけられなかったとき、手動で合わせる最初の位置(ゴールの何秒後)</summary>
    private const double ManualReplayGuessSec = 20;

    /// <summary>表示している割り当てた動画の id(録画なら null)と、見つけた動画のパス</summary>
    private string? _activeAltId;
    private string? _altPath;
    private string? _altHostDir;   // alt.heiso に割り当てているフォルダ
    private bool? _altVfr;         // 表示している動画が可変フレームレートらしいか(別に録った動画だけ調べる)

    /// <summary>リプレイの仮想セッション(作るのに少しかかるので、同じ動画・同じレースのあいだ覚えておく)</summary>
    private LoadedSession? _replay;
    private string? _replayKey;

    /// <summary>直前の自動判別の結果(レースの番号ごと。「採用」「手動で合わせる」で使う)</summary>
    private readonly Dictionary<int, (double StartRecvTime, ReplayDetector.Result? Result, double DurationSec, double FinishVideoSec)> _lastDetect = new();

    /// <summary>画面に出すもの: どのセッション(記録か仮想)を、どの変換で、どの動画に</summary>
    private sealed record View(LoadedSession Session, VideoTimeMap? Map, string? MapError, string? VideoPath, double? Fps, VideoAlt? Alt, int? SourceRace);

    private VideoAlt? ActiveAlt() =>
        _activeAltId == null ? null : _session?.Meta.VideoAlt?.FirstOrDefault(a => a.Id == _activeAltId);

    /// <summary>その動画を割り当てたレースの、記録の中の番号(見つからなければ -1)</summary>
    private int AltRaceIndex(VideoAlt alt) =>
        _session?.Races.FindIndex(r => Math.Abs(r.Segment.StartRecvTime - alt.RaceStartRecvTime) < 0.01) ?? -1;

    /// <summary>新しく記録を開いたとき: 録画の表示に戻す</summary>
    private void ResetAlt()
    {
        _activeAltId = null;
        _altPath = null;
        _altVfr = null;
        _replay = null;
        _replayKey = null;
        _lastDetect.Clear();
        if (_altHostDir != null) Web.CoreWebView2?.ClearVirtualHostNameToFolderMapping(AltHost);
        _altHostDir = null;
    }

    private View? CurrentView()
    {
        var s = _session;
        if (s == null) return null;
        var alt = ActiveAlt();
        int race = alt == null ? -1 : AltRaceIndex(alt);
        if (alt == null || race < 0)
        {
            var map = VideoTimeMap.Create(s.Meta.Video, _method, out var error);
            return new View(s, map, error, s.VideoPath, s.Fps, null, null);
        }
        double? fps = alt.Kind == VideoAlt.KindReplay ? s.Fps : alt.File.Fps;
        if (alt.Content == VideoAlt.ContentReplay)
        {
            var r = ReplaySession(alt, race);
            // 動画の秒 = リプレイの先頭の秒 + レースタイム
            // 先頭の秒は、映像で決めた先頭に、保存したずらし量(「同期の微調整」の小窓に出る量)を足したもの
            var map = VideoTimeMap.FromZero(r.VirtualAtRaceTime(0) - alt.Sync.EffectiveReplayZero);
            return new View(r, map, null, _altPath, fps, alt, race);
        }
        var live = alt.Sync.Points.Count > 0
            ? VideoTimeMap.ForPoint(alt.Sync.Points[^1])
            : VideoTimeMap.FromZero(alt.RaceStartRecvTime - AltLeadSec);
        return new View(s, live, null, _altPath, fps, alt, race);
    }

    private LoadedSession ReplaySession(VideoAlt alt, int race)
    {
        var key = $"{alt.Id}|{race}|{_altPath}";
        if (_replay == null || _replayKey != key)
        {
            _replay = _session!.ForReplay(race, _altPath, alt.Kind == VideoAlt.KindReplay ? _session.Fps : alt.File.Fps);
            _replay.ApplyProfiles(_session.ProfileFiles);   // リプレイの性能モードも、車のプロファイルのカーブで
            _replayKey = key;
        }
        return _replay;
    }

    /// <summary>動画の URL(仮想ホスト)。記録のフォルダ・録画のフォルダのどちらでもなければ alt.heiso(割り当ては SelectVideoAsync で)</summary>
    private string? VideoUrl(string? path)
    {
        var s = _session;
        if (s == null || path == null) return null;
        var dir = Path.GetDirectoryName(path);
        var host = string.Equals(dir, s.Folder, StringComparison.OrdinalIgnoreCase) ? SessionHost
            : s.VideoPath != null && string.Equals(dir, Path.GetDirectoryName(s.VideoPath), StringComparison.OrdinalIgnoreCase) ? VideoHost
            : AltHost;
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    /// <summary>上の欄の「動画」の一覧(録画は画面の側で先頭に足す)</summary>
    private object[] VideoList()
    {
        var s = _session;
        if (s?.Meta.VideoAlt == null) return Array.Empty<object>();
        return s.Meta.VideoAlt.Select(a =>
        {
            int race = AltRaceIndex(a);
            var status = a.Locate(s.Folder).Status;
            return (object)new
            {
                id = a.Id, kind = a.Kind, content = a.Content, race = race < 0 ? (int?)null : race,
                label = AltLabel(a, race),
                status = status.ToString().ToLowerInvariant(),
            };
        }).ToArray();
    }

    private static string AltLabel(VideoAlt a, int race)
    {
        if (!string.IsNullOrWhiteSpace(a.Label)) return a.Label!;
        var what = a.Kind == VideoAlt.KindReplay ? Strings.T("リプレイ")
            : a.Content == VideoAlt.ContentReplay ? Strings.T("リプレイの動画") : Strings.T("別の動画");
        return race < 0 ? what : Strings.T("{what}({n} レース目)", ("what", what), ("n", race + 1));
    }

    /// <summary>画面の「この動画」の欄に出すもの</summary>
    private object? AltInfo(View v)
    {
        if (v.Alt is not VideoAlt a) return null;
        return new
        {
            id = a.Id, kind = a.Kind, content = a.Content, race = v.SourceRace,
            method = a.Sync.Method, confidence = a.Sync.Confidence,
            corr = a.Sync.Check?.Corr, lag = a.Sync.Check?.LagSec,
            zero = a.Sync.ReplayZeroVideoSec, end = a.Sync.ReplayEndVideoSec,
            // リプレイの保存したずらし量(ミリ秒。正なら映像を後ろへ)。「同期の微調整」の小窓に出す
            shiftMs = a.Content == VideoAlt.ContentReplay ? Math.Round(a.Sync.ShiftSec * 1000) : (double?)null,
            duration = v.Session.Timeline?.DurationSec ?? a.Sync.CleanDurationSec,
            points = a.Sync.Points.Count,
            vfr = a.Kind == VideoAlt.KindExternal && _altVfr == true,
        };
    }

    /// <summary>画面からのメッセージのうち、割り当てた動画のもの。扱ったら true</summary>
    private bool OnAltMessage(string? type, JsonElement msg)
    {
        double Sec() => msg.TryGetProperty("sec", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        int? Race() => msg.TryGetProperty("race", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? Id() => msg.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
        switch (type)
        {
            case "selectVideo":
                {
                    var (id, sec, race) = (Id(), Sec(), Race());
                    _ = Dispatcher.InvokeAsync(() => SelectVideoAsync(id, sec, race));   // ページを読み直すことがあるので、メッセージの処理を終えてから
                    return true;
                }
            case "addReplayAuto":
                {
                    // only があればそのレースだけ(「探し直す」)、無ければ全部のレース
                    int? only = msg.TryGetProperty("only", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null;
                    _ = DetectReplayAsync(only, Race());
                    return true;
                }
            case "adoptReplay":
                {
                    bool manual = msg.TryGetProperty("manual", out var mm) && mm.ValueKind == JsonValueKind.True;
                    var (sec, race) = (Sec(), Race());
                    if (race is int rr) _ = Dispatcher.InvokeAsync(() => AdoptReplayAsync(rr, manual, sec));
                    return true;
                }
            case "addVideoFile":
                {
                    var content = msg.TryGetProperty("content", out var c) && c.GetString() == VideoAlt.ContentReplay ? VideoAlt.ContentReplay : VideoAlt.ContentLive;
                    var (sec, race) = (Sec(), Race());
                    if (race is int r) _ = Dispatcher.InvokeAsync(() => AddVideoFileAsync(content, r, sec));   // ファイルを選ぶ画面は、メッセージの処理を終えてから
                    return true;
                }
            case "relinkVideo":
                {
                    var (id, sec, race) = (Id(), Sec(), Race());
                    if (id != null) _ = Dispatcher.InvokeAsync(() => RelinkVideoAsync(id, sec, race));
                    return true;
                }
            case "removeVideo":
                {
                    var (id, sec, race) = (Id(), Sec(), Race());
                    if (id != null) _ = Dispatcher.InvokeAsync(() => RemoveVideoAsync(id, sec, race));
                    return true;
                }
            case "altMark":
                AltMark(msg.TryGetProperty("what", out var w) ? w.GetString() : null, Sec());
                return true;
            case "altNudge":
                if (msg.TryGetProperty("delta", out var nd) && nd.ValueKind == JsonValueKind.Number) AltNudge(nd.GetDouble());
                return true;
        }
        return false;
    }

    // ---- 切り替え ----

    /// <summary>
    /// 表示する動画を切り替える(id が null なら録画)。同じ瞬間に合わせる: ライブどうしは時刻で、リプレイが絡むときはレースタイムで。
    /// 動画のフォルダの割り当てが変わるときはページを読み直す(割り当ては読み込み済みのページに効かないため)。WebView2 のイベントの外で呼ぶこと
    /// </summary>
    private async Task SelectVideoAsync(string? id, double sec, int? jsRace, string? done = null)
    {
        var s = _session;
        if (s == null) return;
        var from = CurrentView();
        VideoAlt? alt = id == null ? null : s.Meta.VideoAlt?.FirstOrDefault(a => a.Id == id);
        if (id != null && alt == null) return;

        string? path = null;
        if (alt != null)
        {
            var loc = alt.Locate(s.Folder);
            if (loc.Status != VideoAltStatus.Found)
            {
                Post(new { type = "videoMissing", id, changed = loc.Status == VideoAltStatus.Changed, active = _activeAltId });
                return;
            }
            if (AltRaceIndex(alt) < 0)
            {
                Toast(error: Strings.T("この動画を割り当てたレースが、この記録に見つかりません"));
                Post(new { type = "videoSelected", active = _activeAltId });
                return;
            }
            path = loc.FoundPath;
        }

        var prev = (_activeAltId, _altPath);
        _activeAltId = alt?.Id;
        _altPath = path;
        _altVfr = alt?.Kind == VideoAlt.KindExternal && path != null ? await Task.Run(() => VideoDuration.TryReadIsVariableFrameRate(path)) : null;
        View? to;
        try
        {
            if (alt?.Content == VideoAlt.ContentReplay)
            {
                var a = alt;
                int race = AltRaceIndex(a);
                await Task.Run(() => ReplaySession(a, race));
            }
            to = CurrentView();
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"動画を切り替えられない: {id}", ex);
            (_activeAltId, _altPath) = prev;
            Toast(error: ex is UserFacingException ? ex.Message : Strings.T("動画を切り替えられませんでした: {message}", ("message", ex.Message)));
            Post(new { type = "videoSelected", active = _activeAltId });
            return;
        }

        var (target, race2) = MapPosition(from, to!, sec, jsRace);
        PlayerLog.Info($"動画を切り替えた: {(alt == null ? "録画" : $"{alt.Id}({alt.Kind}/{alt.Content})")} {path ?? s.VideoPath}、{sec:F2} 秒 → {target:F2} 秒");

        // 動画のフォルダの割り当て
        var dir = path == null ? null : Path.GetDirectoryName(path);
        bool needAltHost = path != null && VideoUrl(path)!.StartsWith($"https://{AltHost}/", StringComparison.Ordinal);
        bool reload = needAltHost && !string.Equals(_altHostDir, dir, StringComparison.OrdinalIgnoreCase);
        if (reload)
        {
            var core = Web.CoreWebView2;
            if (_altHostDir != null) core.ClearVirtualHostNameToFolderMapping(AltHost);
            core.SetVirtualHostNameToFolderMapping(AltHost, dir!, CoreWebView2HostResourceAccessKind.Allow);
            _altHostDir = dir;
            _restoreSec = target;
            _restorePlay = false;
            _restoreQuiet = true;
            _restoreRace = race2;
            core.Reload();
        }
        else
        {
            SendSession(keepVideo: false);
            Post(new { type = "restore", sec = target, play = false, quiet = true, race = race2 });
        }
        if (done != null) Toast(message: done);
    }

    /// <summary>切り替えの前の位置 → 切り替えの後の位置(動画の秒)と、選ぶレース(画面の一覧の番号。null なら選ばない)</summary>
    private (double Sec, int? Race) MapPosition(View? from, View to, double sec, int? jsRace)
    {
        var s = _session!;
        // 選ぶレース: リプレイは 1 本だけ(0)。リプレイから戻るときは元のレース
        int? race = to.Session.IsReplay ? 0 : from?.Session.IsReplay == true ? from.SourceRace : jsRace ?? to.SourceRace;
        if (from?.Map == null || to.Map == null) return (Lead(to, race), race);

        double recv = from.Map.ToRecvTime(sec);
        if (!from.Session.IsReplay && !to.Session.IsReplay)
            return (Math.Max(0, to.Map.ToVideoSec(recv)), race);   // ライブどうし: 同じ時刻

        // リプレイが絡む: レースタイムで合わせる
        int srcRace = from.Session.IsReplay ? from.SourceRace!.Value : to.SourceRace!.Value;
        double? rt = from.Session.IsReplay ? from.Session.RaceTimeAtVirtual(recv) : RaceTimeAt(s, srcRace, recv);
        // スタートより前に離れすぎ(前のレースの中など)・ゴールより後なら、同じ瞬間が無いのでレースの頭へ
        // (164031: 2 本目の中から 3 本目のリプレイへ切り替えて、リプレイの数分前に飛んでいた)
        if (rt is not double x || x < -SwitchLeadSec || x > (from.Session.Timeline ?? to.Session.Timeline)?.DurationSec + 1) return (Lead(to, race), race);
        double outRecv;
        if (to.Session.IsReplay) outRecv = to.Session.VirtualAtRaceTime(x);
        else
        {
            var seg = s.Races[srcRace].Segment;
            var tl = RaceTimeline.Build(s.Table, seg);
            if (tl.Rows.Count == 0) return (Lead(to, race), race);
            var recvCol = s.Table[TelemetryTable.RecvTime];
            if (x < tl.RaceTime[0]) outRecv = recvCol[tl.Rows[0]] + (x - tl.RaceTime[0]);
            else
            {
                int k = tl.IndexAtRaceTime(x);
                outRecv = recvCol[tl.Rows[k]] + Math.Min(x - tl.RaceTime[k], 0.1);
            }
        }
        return (Math.Max(0, to.Map.ToVideoSec(outRecv)), race);
    }

    /// <summary>切り替えの前の位置が、スタートよりこの秒数以上前なら、同じ瞬間は無いとみなす</summary>
    private const double SwitchLeadSec = 3;

    /// <summary>
    /// 切り替えた先で位置が決められないとき: そのレースの開始の 3 秒前。リプレイは先頭のコマ
    /// (リプレイの前はリザルトの画面なので)
    /// </summary>
    private static double Lead(View v, int? race)
    {
        if (v.Map == null) return 0;
        var races = v.Session.Races;
        int i = race is int r && r >= 0 && r < races.Count ? r : v.SourceRace is int sr && !v.Session.IsReplay ? sr : 0;
        if (i >= races.Count) return 0;
        // ライブは 3 秒前。ちょうど 3 秒前だと、画面のレースの自動の切り替え(スタートの 3 秒前から次のレース)で前のレースに戻ることがあるので、少しだけ後ろに
        return Math.Max(0, v.Map.ToVideoSec(races[i].Segment.StartRecvTime) - (v.Session.IsReplay ? 0 : SwitchLeadSec - 0.05));
    }

    /// <summary>記録のその時刻の、レースのレースタイム(レース前なら負。停止区間・レースの外なら null)</summary>
    private static double? RaceTimeAt(LoadedSession s, int race, double recv)
    {
        var seg = s.Races[race].Segment;
        var recvCol = s.Table[TelemetryTable.RecvTime];
        if (recv < seg.StartRecvTime) return recv - seg.StartRecvTime;
        if (recv > recvCol[seg.FinishIndex] + 1) return null;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = seg.StartIndex; i <= seg.FinishIndex; i++)
        {
            double d = Math.Abs(recvCol[i] - recv);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0 || bestD > 0.5 || s.Table["is_race_on"][best] != 1) return null;
        double rt = s.Table["cur_race_time"][best];
        return double.IsFinite(rt) ? rt + (recv - recvCol[best]) : null;
    }

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
        var s = _session;
        if (s == null || _jobCts != null) return;
        if (s.VideoPath == null) { Toast(error: Strings.T("録画ファイルが見つからないため、リプレイを探せません")); return; }
        var map = VideoTimeMap.Create(s.Meta.Video, _method, out _);
        if (map == null) { Toast(error: Strings.T("動画との対応が取れないため、リプレイを探せません")); return; }
        var st = await GetFfmpegStatusAsync(force: false);
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
        _jobCts = cts;
        var sw = Stopwatch.StartNew();
        var found = new List<object>();
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
                if (_session != s) return;   // 探している間に別の記録を開いた
                _lastDetect[k] = (s.Races[k].Segment.StartRecvTime, r, tl.DurationSec, finish);
                PlayerLog.Info(r == null ? $"レース {k + 1}: リプレイが見つからない"
                    : $"レース {k + 1}: リプレイ 先頭 {r.ZeroVideoSec:F3} 秒、終わり {r.EndVideoSec:F3} 秒、相関 {r.Corr}、遅れ {r.LagSec}、確度 {r.Confidence}、{r.Warning}");
                found.Add(new
                {
                    race = k, found = r != null, zero = r?.ZeroVideoSec, end = r?.EndVideoSec, duration = tl.DurationSec,
                    confidence = r?.Confidence, corr = r?.Corr, lag = r?.LagSec, warning = r?.Warning,
                });
            }
            Job(null);
            PlayerLog.Info($"リプレイを探し終えた: {targets.Count} レース、{sw.Elapsed.TotalSeconds:F1} 秒");
            Post(new { type = "replayFound", results = found, selected = onlyRace ?? jsRace });
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
            _jobCts = null;
        }
    }

    /// <summary>
    /// そのレースにリプレイを割り当てる。自動判別の結果があればそれを、manual なら手動で合わせる
    /// (位置は自動判別の結果か、無ければゴールの少し後から)
    /// </summary>
    private async Task AdoptReplayAsync(int race, bool manual, double sec)
    {
        var s = _session;
        if (s == null || s.VideoPath == null || race < 0 || race >= s.Races.Count) return;
        var seg = s.Races[race].Segment;
        if (!_lastDetect.TryGetValue(race, out var d))
        {
            var map = VideoTimeMap.Create(s.Meta.Video, _method, out _);
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

    private string? ChooseVideoFile(string title, string? initial)
    {
        if (_choosing) return null;
        _choosing = true;
        try
        {
            var dlg = new OpenFileDialog { Title = title, Filter = VideoFilter(), InitialDirectory = initial ?? "" };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        finally
        {
            _choosing = false;
        }
    }

    private async Task AddVideoFileAsync(string content, int raceIndex, double sec)
    {
        var s = _session;
        if (s == null || raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var title = content == VideoAlt.ContentReplay ? Strings.T("リプレイの動画を選んでください") : Strings.T("同じ走りを録った動画を選んでください");
        var path = ChooseVideoFile(title, s.VideoPath != null ? Path.GetDirectoryName(s.VideoPath) : s.Folder);
        if (path == null) return;
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
        var s = _session;
        var alt = s?.Meta.VideoAlt?.FirstOrDefault(a => a.Id == id);
        if (s == null || alt == null) return;
        var path = ChooseVideoFile(Strings.T("動画を選び直してください"), alt.Path != null ? Path.GetDirectoryName(alt.Path) : s.Folder);
        if (path == null) return;
        if (!await SetAltFileAsync(alt, path, alt.Kind == VideoAlt.KindReplay ? s.Fps : null)) return;
        if (!SaveAlt(alt)) return;
        PlayerLog.Info($"動画を選び直した: {alt.Id} → {path}");
        _replay = null;
        await SelectVideoAsync(alt.Id, sec, jsRace, Strings.T("動画を選び直しました"));
    }

    private async Task RemoveVideoAsync(string id, double sec, int? jsRace)
    {
        var s = _session;
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
            var folder = _session!.Folder;
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
        var s = _session!;
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
    private bool AltManualSync(JsonElement msg)
    {
        var view = CurrentView();
        if (view?.Alt is not VideoAlt alt || view.Map == null) return false;
        double recv = view.Map.ToRecvTime(msg.GetProperty("graphSec").GetDouble());
        double videoSec = msg.GetProperty("videoSec").GetDouble();
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
                Note = msg.TryGetProperty("note", out var n) && n.GetString() is string note && note.Trim().Length > 0 ? note.Trim() : null,
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
