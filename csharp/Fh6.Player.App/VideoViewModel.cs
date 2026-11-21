using System.Diagnostics;
using System.IO;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>画面のページと動画を出す仮想ホストの名前(外枠が、どのフォルダをどのホストに割り当てるか)</summary>
public static class VirtualHosts
{
    public const string App = "app.heiso";
    /// <summary>開いている記録のフォルダ</summary>
    public const string Session = "session.heiso";
    /// <summary>録画が記録のフォルダの外にあるときの、録画のフォルダ</summary>
    public const string Video = "video.heiso";
    /// <summary>割り当てた動画が、記録のフォルダ・録画のフォルダのどちらでもないときの、その動画のフォルダ</summary>
    public const string Alt = "alt.heiso";
}

/// <summary>
/// 画面に出す動画と、画面へ送る記録のデータ(HostSession)。録画のほかに割り当てた動画(session.json の video_alt。作業指示 008 ステップ 4)も扱う。
/// 上の欄の「動画」で、録画と割り当てた動画を切り替える。リプレイ(中身が replay)は、レースタイムでつないだ最終の時間軸の
/// 仮想セッション(LoadedSession.ForReplay)で、グラフ・印・コース図を作り直す。ライブ(中身が live)は記録のまま、同期の点だけ替える
/// </summary>
public sealed partial class VideoViewModel
{
    /// <summary>割り当てた動画の同期の点がまだ無いとき、動画 0 秒をレース開始の何秒前と仮に置くか</summary>
    private const double AltLeadSec = 5;

    /// <summary>リプレイを見つけられなかったとき、手動で合わせる最初の位置(ゴールの何秒後)</summary>
    private const double ManualReplayGuessSec = 20;

    private readonly IShell _shell;
    private readonly PlayerSettings _settings;
    private readonly OpenedSessionViewModel _opened;
    private readonly SettingsViewModel _prefs;
    private readonly JobSlot _job;

    /// <summary>選んでいるレースと、摩擦円・荷重移動(送った系列と印から作る)</summary>
    private readonly SelectionViewModel _selection;

    /// <summary>表示している割り当てた動画の id(録画なら null)と、見つけた動画のパス</summary>
    private string? _activeAltId;
    private string? _altPath;
    private bool? _altVfr;         // 表示している動画が可変フレームレートらしいか(別に録った動画だけ調べる)

    /// <summary>開いたときに、覚えていた動画を表示する: 最初に記録のデータを送った後で、その動画の頭へ移す</summary>
    private bool _openStart;

    /// <summary>開いたときに表示する動画のフォルダ(記録のフォルダ・録画のフォルダの外なら。外枠がページを読み込む前に割り当てる)</summary>
    private string? _openAltDir;

    /// <summary>リプレイの仮想セッション(作るのに少しかかるので、同じ動画・同じレースのあいだ覚えておく)</summary>
    private LoadedSession? _replay;
    private string? _replayKey;

    /// <summary>直前の自動判別の結果(レースの番号ごと。「採用」「手動で合わせる」で使う)</summary>
    private readonly Dictionary<int, (double StartRecvTime, ReplayDetector.Result? Result, double DurationSec, double FinishVideoSec)> _lastDetect = new();

    public VideoViewModel(IShell shell, PlayerSettings settings, OpenedSessionViewModel opened, SettingsViewModel prefs, JobSlot job)
    {
        _shell = shell;
        _settings = settings;
        _opened = opened;
        _prefs = prefs;
        _job = job;
        _selection = new SelectionViewModel(shell, settings);
        prefs.ViewSaved += _selection.SettingsSaved;
        opened.Opened += Reset;
        opened.VideoFolder = () => _openAltDir;
    }

    /// <summary>画面に出すもの: どのセッション(記録か仮想)を、どの変換で、どの動画に</summary>
    private sealed record View(LoadedSession Session, VideoTimeMap? Map, string? MapError, string? VideoPath, double? Fps, VideoAlt? Alt, int? SourceRace);

    private LoadedSession? Session => _opened.Session;

    /// <summary>表示している割り当てた動画(録画なら null)</summary>
    public VideoAlt? ActiveAlt() =>
        _activeAltId == null ? null : Session?.Meta.VideoAlt?.FirstOrDefault(a => a.Id == _activeAltId);

    /// <summary>その動画を割り当てたレースの、記録の中の番号(見つからなければ -1)</summary>
    private int AltRaceIndex(VideoAlt alt) =>
        Session?.Races.FindIndex(r => Math.Abs(r.Segment.StartRecvTime - alt.RaceStartRecvTime) < 0.01) ?? -1;

    /// <summary>新しく記録を開いたとき: 録画の表示に戻し、その記録で最後に表示していた動画があれば、それを表示する</summary>
    private void Reset()
    {
        _activeAltId = null;
        _altPath = null;
        _altVfr = null;
        _replay = null;
        _replayKey = null;
        _lastDetect.Clear();
        _openStart = false;
        _openAltDir = null;
        OpenRemembered();
    }

    /// <summary>
    /// 記録ごとに覚えるときの名前: 記録を始めた日時(フォルダ名の頭)。アーカイブへ移しても、メモを変えても同じ。
    /// 配布パッケージは割り当てた動画を持たないので覚えない(null)
    /// </summary>
    private static string? RememberKey(LoadedSession s)
    {
        if (s.Package != null) return null;
        var name = Path.GetFileName(s.Folder.TrimEnd('\\', '/'));
        return SessionFolderName.TryParse(name, out var started, out _) ? started.ToString(SessionFolderName.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture) : name;
    }

    /// <summary>表示している動画を覚える(録画なら忘れる)</summary>
    private void RememberShown()
    {
        if (Session is LoadedSession s && RememberKey(s) is string key) _settings.SetLastVideo(key, _activeAltId);
    }

    /// <summary>
    /// 開いたとき: その記録で最後に表示していた動画が使えれば、それを表示する。使えなければ録画のまま(知らせない。
    /// 割り当てを外した・動画が見つからない・割り当てたレースが無い)。spec「開き直したときの表示と、割り当て済みのリプレイ」の A
    /// </summary>
    private void OpenRemembered()
    {
        if (Session is not LoadedSession s || RememberKey(s) is not string key || _settings.LastVideoOf(key) is not string id) return;
        var alt = s.Meta.VideoAlt?.FirstOrDefault(a => a.Id == id);
        if (alt == null || AltRaceIndex(alt) < 0) return;
        string path;
        try
        {
            var loc = alt.Locate(s.Folder);
            if (loc.Status != VideoAltStatus.Found || loc.FoundPath == null) return;
            path = loc.FoundPath;
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"覚えていた動画を探せない: {id}", ex);
            return;
        }
        _activeAltId = alt.Id;
        _altPath = path;
        _altVfr = alt.Kind == VideoAlt.KindExternal ? VideoDuration.TryReadIsVariableFrameRate(path) : null;
        _openAltDir = VideoUrl(path)!.StartsWith($"https://{VirtualHosts.Alt}/", StringComparison.Ordinal) ? Path.GetDirectoryName(path) : null;
        _openStart = true;
        PlayerLog.Info($"前に表示していた動画で開く: {alt.Id}({alt.Kind}/{alt.Content}) {path}");
    }

    private View? CurrentView()
    {
        var s = Session;
        if (s == null) return null;
        var alt = ActiveAlt();
        int race = alt == null ? -1 : AltRaceIndex(alt);
        if (alt == null || race < 0)
        {
            var map = VideoTimeMap.Create(s.Meta.Video, _opened.Method, out var error);
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
        var s = Session!;
        var key = $"{alt.Id}|{race}|{_altPath}";
        if (_replay == null || _replayKey != key)
        {
            _replay = s.ForReplay(race, _altPath, alt.Kind == VideoAlt.KindReplay ? s.Fps : alt.File.Fps);
            _replay.ApplyProfiles(s.ProfileFiles);   // リプレイの性能モードも、車のプロファイルのカーブで
            _replayKey = key;
        }
        return _replay;
    }

    /// <summary>動画の URL(仮想ホスト)。記録のフォルダ・録画のフォルダのどちらでもなければ alt.heiso(割り当ては SelectVideoAsync で)</summary>
    private string? VideoUrl(string? path)
    {
        var s = Session;
        if (s == null || path == null) return null;
        var dir = Path.GetDirectoryName(path);
        var host = string.Equals(dir, s.Folder, StringComparison.OrdinalIgnoreCase) ? VirtualHosts.Session
            : s.VideoPath != null && string.Equals(dir, Path.GetDirectoryName(s.VideoPath), StringComparison.OrdinalIgnoreCase) ? VirtualHosts.Video
            : VirtualHosts.Alt;
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    /// <summary>上の欄の「動画」の一覧(録画は画面の側で先頭に足す)</summary>
    private VideoChoice[] VideoList()
    {
        var s = Session;
        if (s?.Meta.VideoAlt == null) return Array.Empty<VideoChoice>();
        return s.Meta.VideoAlt.Select(a =>
        {
            int race = AltRaceIndex(a);
            var status = a.Locate(s.Folder).Status;
            return new VideoChoice(a.Id, a.Kind, a.Content, race < 0 ? null : race, AltLabel(a, race), status.ToString().ToLowerInvariant());
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
    private AltVideoInfo? AltInfo(View v)
    {
        if (v.Alt is not VideoAlt a) return null;
        return new AltVideoInfo(
            Id: a.Id, Kind: a.Kind, Content: a.Content, Race: v.SourceRace,
            Method: a.Sync.Method, Confidence: a.Sync.Confidence,
            Corr: a.Sync.Check?.Corr, Lag: a.Sync.Check?.LagSec,
            Zero: a.Sync.ReplayZeroVideoSec, End: a.Sync.ReplayEndVideoSec,
            // リプレイの保存したずらし量(ミリ秒。正なら映像を後ろへ)。「同期の微調整」の小窓に出す
            ShiftMs: a.Content == VideoAlt.ContentReplay ? Math.Round(a.Sync.ShiftSec * 1000) : null,
            Duration: v.Session.Timeline?.DurationSec ?? a.Sync.CleanDurationSec,
            Points: a.Sync.Points.Count,
            Vfr: a.Kind == VideoAlt.KindExternal && _altVfr == true);
    }

    /// <summary>開いている記録のデータを画面へ送る(keepVideo なら、画面は動画の位置をそのままにする)。記録を開いていなければ何もしない</summary>
    public void SendSession(bool keepVideo)
    {
        if (Session is not LoadedSession s) return;
        View view;
        try { view = CurrentView()!; }
        catch (Exception ex)
        {
            // 割り当てた動画で作れなければ、録画に戻す
            PlayerLog.Error($"割り当てた動画の表示を作れない: {_activeAltId}", ex);
            Toast(error: ex is UserFacingException ? ex.Message : Strings.T("動画を切り替えられませんでした: {message}", ("message", ex.Message)));
            _activeAltId = null;
            _altPath = null;
            _openStart = false;
            RememberShown();
            view = CurrentView()!;
        }
        var map = view.Map;
        var shown = view.Session;
        var v = s.Meta.Video;
        var series = map == null ? null : shown.BuildSeries(map);
        var marks = map == null ? null : shown.BuildMarks(map);
        int? race = _selection.Shown(series, marks, keepVideo);
        _shell.Post(new HostSession(
            KeepVideo: keepVideo,
            Name: _opened.DisplayName,
            Label: s.Meta.Session.Label,
            Comment: SessionFolderName.TryParse(s.Package?.SourceSession ?? Path.GetFileName(s.Folder), out _, out var comment) ? comment : null,
            // 配布パッケージを開いているか(書き出すボタンを出さない)
            Package: s.Package != null,
            StartedAt: s.Meta.Session.StartedAt,
            Complete: s.Meta.Complete,
            VideoUrl: VideoUrl(view.VideoPath),
            // 録画と割り当てた動画(上の欄の「動画」)。割り当てられるのは記録(パッケージでなく、記録を終えたもの)だけ
            Videos: VideoList(),
            ActiveVideo: view.Alt?.Id,
            Alt: AltInfo(view),
            CanAddVideo: s.Package == null && s.Meta.Complete,
            HasRecording: s.VideoPath != null,
            VideoError: view.VideoPath == null ? Strings.T("録画ファイルが見つかりません(テレメトリーだけ表示します)") : null,
            Method: _opened.Method.ToString(),
            MapError: view.MapError,
            Sync: new SyncInfo(
                SamplesTotal: v.SyncSamples.Count,
                SamplesUsed: map?.SamplesUsed ?? 0,
                IsFallback: map?.IsFallback ?? false,
                ManualSync: v.ManualSync.Count,
                ManualShiftSec: map?.ManualShiftSec ?? 0,
                EndCheckSec: v.EndCheckSec,
                ClockRate: v.ClockRate,
                FileDurationSec: v.FileDurationSec),
            Rows: shown.Table.RowCount,
            Fps: view.Fps,
            Series: series,
            Marks: marks,
            // 計測モード(車のプロファイル)。リプレイを見ているときも、元の記録の車のもの
            Measure: s.BuildMeasure(),
            Race: race));
        _selection.SendFriction();
        if (!keepVideo && _openStart)
        {
            // 覚えていた動画で開いた: 上の欄の「動画」でその動画を選んだときと同じ位置(リプレイは先頭のコマ、ライブはそのレースのスタートの少し前)。
            // 「最初のレースのスタートの 3 秒前から再生する」なら、そこから再生する
            _openStart = false;
            int? openRace = view.Session.IsReplay ? 0 : view.SourceRace;
            _shell.Post(new HostRestore(Lead(view, openRace), _settings.View?.AutoStart == true, true, openRace));
        }
    }

    /// <summary>
    /// 画面からのメッセージのうち、割り当てた動画のものを扱う。扱わないメッセージなら null。sec が無ければ 0、id は空なら null。
    /// 同期の手動補正は、割り当てた動画を表示しているときだけ扱う(録画なら null。開いている記録の手動補正になる)
    /// </summary>
    public Task? Handle(ViewMessage msg)
    {
        static string? Id(string? id) => id is { Length: > 0 } ? id : null;
        if (_selection.Handle(msg) is { } selected) return selected;   // 送った系列から作るので、ここで扱う
        switch (msg)
        {
            case ViewSelectVideo m:
                {
                    var (id, sec, race) = (Id(m.Id), m.Sec ?? 0, m.Race);
                    return Later(() => SelectVideoAsync(id, sec, race));   // ページを読み直すことがあるので、メッセージの処理を終えてから
                }
            case ViewAddReplayAuto m:
                // only があればそのレースだけ(「探し直す」)、無ければ全部のレース
                return DetectReplayAsync(m.Only, m.Race);
            case ViewAdoptReplay m:
                {
                    var (manual, sec) = (m.Manual == true, m.Sec ?? 0);
                    return m.Race is int rr ? Later(() => AdoptReplayAsync(rr, manual, sec)) : Task.CompletedTask;
                }
            case ViewAddVideoFile m:
                {
                    var content = m.Content == VideoAlt.ContentReplay ? VideoAlt.ContentReplay : VideoAlt.ContentLive;
                    var sec = m.Sec ?? 0;
                    return m.Race is int r ? Later(() => AddVideoFileAsync(content, r, sec)) : Task.CompletedTask;
                }
            case ViewRelinkVideo m:
                {
                    var (id, sec, race) = (Id(m.Id), m.Sec ?? 0, m.Race);
                    return id != null ? Later(() => RelinkVideoAsync(id, sec, race)) : Task.CompletedTask;
                }
            case ViewRemoveVideo m:
                {
                    var (id, sec, race) = (Id(m.Id), m.Sec ?? 0, m.Race);
                    return id != null ? Later(() => RemoveVideoAsync(id, sec, race)) : Task.CompletedTask;
                }
            case ViewAltMark m:
                AltMark(m.What, m.Sec ?? 0);
                return Task.CompletedTask;
            case ViewAltNudge m:
                if (m.Delta is double nd) AltNudge(nd);
                return Task.CompletedTask;
            case ViewManualSync { GraphSec: double graphSec, VideoSec: double videoSec } m:
                return AltManualSync(graphSec, videoSec, m.Note) ? Task.CompletedTask : null;
            case ViewClearManualSync:
                return AltClearSync() ? Task.CompletedTask : null;
        }
        return null;
    }

    /// <summary>画面からのメッセージの処理を終えてから動かす(ページを読み直す・選ぶ画面を出すことがあるもの)</summary>
    private async Task Later(Func<Task> action)
    {
        await _shell.AfterMessageAsync();
        await action();
    }

    // ---- 切り替え ----

    /// <summary>
    /// 表示する動画を切り替える(id が null なら録画)。同じ瞬間に合わせる: ライブどうしは時刻で、リプレイが絡むときはレースタイムで。
    /// 動画のフォルダの割り当てが変わるときはページを読み直す(割り当ては読み込み済みのページに効かないため)
    /// </summary>
    private async Task SelectVideoAsync(string? id, double sec, int? jsRace, string? done = null)
    {
        var s = Session;
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
                _shell.Post(new HostVideoMissing(id, loc.Status == VideoAltStatus.Changed, _activeAltId));
                return;
            }
            if (AltRaceIndex(alt) < 0)
            {
                Toast(error: Strings.T("この動画を割り当てたレースが、この記録に見つかりません"));
                _shell.Post(new HostVideoSelected(_activeAltId));
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
            _shell.Post(new HostVideoSelected(_activeAltId));
            return;
        }

        var (target, race2) = MapPosition(from, to!, sec, jsRace);
        RememberShown();
        PlayerLog.Info($"動画を切り替えた: {(alt == null ? "録画" : $"{alt.Id}({alt.Kind}/{alt.Content})")} {path ?? s.VideoPath}、{sec:F2} 秒 → {target:F2} 秒");

        // 記録のフォルダ・録画のフォルダの外の動画は、そのフォルダを割り当てる(変わればページを読み直して、その位置へ戻す)
        bool needAltHost = path != null && VideoUrl(path)!.StartsWith($"https://{VirtualHosts.Alt}/", StringComparison.Ordinal);
        if (!needAltHost || !_shell.ShowVideoFolder(Path.GetDirectoryName(path!)!, target, race2))
        {
            SendSession(keepVideo: false);
            _shell.Post(new HostRestore(target, false, true, race2));
        }
        if (done != null) Toast(message: done);
    }

    /// <summary>切り替えの前の位置 → 切り替えの後の位置(動画の秒)と、選ぶレース(画面の一覧の番号。null なら選ばない)</summary>
    private (double Sec, int? Race) MapPosition(View? from, View to, double sec, int? jsRace)
    {
        var s = Session!;
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

    /// <summary>長くかかる処理の進み具合を画面の下に出す(text が null なら消す)</summary>
    private void Job(string? text, double progress = 0, bool cancellable = false) =>
        _shell.Post(new HostJob(text, Math.Round(progress, 3), cancellable));

    /// <summary>画面の下に短く知らせる(手動補正の欄には触れない)。sec は出しておく秒数</summary>
    private void Toast(string? message = null, string? error = null, double? sec = null) => _shell.Post(new HostToast(message, error, sec));

    /// <summary>手動補正の欄の下に知らせる</summary>
    private void Notice(string? message = null, string? error = null) => _shell.Post(new HostNotice(message, error));
}
