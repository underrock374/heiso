using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>
/// 開いている記録(または配布パッケージ): 開くこと、録画の同期の手動補正、レースのコース名・車名。
/// 画面へ送る記録のデータ(HostSession)は、割り当てた動画の表示と合わせて外枠が作る(MainWindow.Alt.cs と一緒に、ステップ 3 の 5 本目で移す)。
/// 記録を書き換えたら refresh を呼んで、外枠に送り直してもらう(動画の位置はそのまま)
/// </summary>
public sealed class OpenedSessionViewModel(IShell shell, PlayerSettings settings, ProfileStore profiles, Action refresh, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _choosing;   // 開く画面から開いている途中(続けて押されても、画面は 1 つだけ)

    /// <summary>記録を開けたとき(外枠が画面に出す前)。表示していた動画などを、録画の表示に戻すのに使う</summary>
    public event Action? Opened;

    /// <summary>開いたときに表示する動画のフォルダ(記録のフォルダ・録画のフォルダの外なら。VideoViewModel が Opened で決める)</summary>
    public Func<string?>? VideoFolder { get; set; }

    /// <summary>開いている記録(開いていなければ null)</summary>
    public LoadedSession? Session { get; private set; }

    /// <summary>開いている配布パッケージのファイル(記録のフォルダを開いているときは null)</summary>
    public string? PackageFile { get; private set; }

    /// <summary>動画との同期の方式(画面の設定。どの記録にも共通)</summary>
    public VideoSyncMethod Method { get; private set; } = VideoSyncMethod.StartedEvent;

    /// <summary>画面に出す名前: 記録ならフォルダ名、パッケージならファイル名(.heiso.zip を除く)</summary>
    public string DisplayName =>
        PackageFile != null ? Path.GetFileName(PackageFile)[..^PackageInfo.Extension.Length]
        : Session != null ? Path.GetFileName(Session.Folder) : "";

    /// <summary>画面からのメッセージのうち、開くこと・録画の手動補正・レースの名前を扱う。扱わないメッセージなら null</summary>
    public Task? Handle(ViewMessage msg)
    {
        switch (msg)
        {
            case ViewOpen:
                return ChooseAndOpenAsync(() => shell.PickFolderAsync(Strings.T("セッションフォルダを選んでください"), settings.InitialFolder()));
            case ViewOpenPackage:
                return ChooseAndOpenAsync(() => shell.PickFileAsync(Strings.T("配布パッケージを選んでください"),
                    $"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}|{Strings.T("すべてのファイル")} (*.*)|*.*",
                    settings.PackageFolder()));
            case ViewSessionOpen { Path: { Length: > 0 } open }:
                return OpenAsync(open);   // 記録の一覧から開く
            case ViewMethod m:
                if (SetMethod(m.Method)) refresh();
                return Task.CompletedTask;
            case ViewManualSync { GraphSec: double graphSec, VideoSec: double videoSec } m:
                SaveManualSync(graphSec, videoSec, m.Note);
                return Task.CompletedTask;
            case ViewClearManualSync:
                ClearManualSync();
                return Task.CompletedTask;
            case ViewRaceNamesForm { Race: int formRace }:
                SendRaceNamesForm(formRace);
                return Task.CompletedTask;
            case ViewEditRaceNames { Race: int } m:
                EditRaceNames(m);
                return Task.CompletedTask;
        }
        return null;
    }

    /// <summary>同期の方式を変える(名前が読めなければ変えずに false)</summary>
    public bool SetMethod(string? name)
    {
        if (!Enum.TryParse<VideoSyncMethod>(name, out var method)) return false;
        Method = method;
        return true;
    }

    /// <summary>選ぶ画面を出して開く(毎回、前回開いた記録の 1 つ上・前回のパッケージのフォルダから始める)</summary>
    private async Task ChooseAndOpenAsync(Func<Task<string?>> pick)
    {
        if (_choosing) return;
        _choosing = true;
        try
        {
            if (await pick() is string path) await OpenAsync(path);
        }
        finally
        {
            _choosing = false;
        }
    }

    private static bool IsPackageFile(string path) =>
        File.Exists(path) && path.EndsWith(PackageInfo.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// セッションフォルダ(またはその中のファイル)か、配布パッケージ(.heiso.zip)を開く。
    /// 開けたら外枠に画面へ出してもらう(動画のフォルダの割り当てと、ページの読み直し)。開けなければ画面にエラーを出す
    /// </summary>
    public async Task OpenAsync(string path)
    {
        string? package = IsPackageFile(path) ? Path.GetFullPath(path) : null;
        var folder = package != null ? "" : File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        shell.Post(new HostLoading(Path.GetFileName(package ?? folder)));
        PlayerLog.Info($"開く: {package ?? folder}");
        LoadedSession loaded;
        try
        {
            var sw = Stopwatch.StartNew();
            if (package != null)
            {
                folder = await Task.Run(() => PackageReader.Extract(package, PlayerPaths.PackagesDir));
                PlayerLog.Info($"パッケージを展開した: {folder}({sw.ElapsedMilliseconds} ms)");
            }
            loaded = await Task.Run(() => LoadedSession.Load(folder));
            // 計測モード: 車のプロファイルにこの記録を足す(作り直しが要る部分だけ)。書けなくても表示は続ける
            var swp = Stopwatch.StartNew();
            var profileError = await Task.Run(() =>
            {
                var e = loaded.UpdateProfiles(profiles);
                loaded.ApplyProfiles(loaded.ProfileFiles);   // 性能モードは、同じ車の記録を合わせたカーブで
                return e;
            });
            if (profileError != null) PlayerLog.Error($"車のプロファイルを保存できない: {profileError}");
            PlayerLog.Info($"車のプロファイル: {swp.ElapsedMilliseconds} ms({profiles.Folder})");
            Session = loaded;
            PackageFile = package;
            if (package != null)
            {
                settings.LastPackageDir = Path.GetDirectoryName(package);
                settings.Save();
            }
            else settings.Remember(folder);
            PlayerLog.Info($"開いた: {loaded.Table.RowCount} 行、レース {loaded.Races.Count} 本、動画 {loaded.VideoPath ?? "なし"}、{loaded.Fps?.ToString("0.###") ?? "-"} fps、{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            PlayerLog.Error($"開けない: {package ?? folder}", ex);
            Session = null;
            PackageFile = null;
            shell.Post(new HostError(ex is UserFacingException or PackageException ? ex.Message : Strings.T("開けませんでした: {message}", ("message", ex.Message))));
            return;
        }
        Opened?.Invoke();
        shell.ShowSession(loaded, VideoFolder?.Invoke());
    }

    // ---- 録画の同期の手動補正 ----

    /// <summary>
    /// 手動補正の点を保存する。graphSec はグラフで指した瞬間(今の変換での動画の秒)、videoSec は映像のコマの秒。
    /// グラフの位置をテレメトリー時刻に直すのは VideoTimeMap だけで行う
    /// </summary>
    private void SaveManualSync(double graphSec, double videoSec, string? note)
    {
        if (Session is not LoadedSession s) return;
        var map = VideoTimeMap.Create(s.Meta.Video, Method, out _);
        if (map == null) { Notice(error: Strings.T("動画との対応が取れないため、補正できません")); return; }
        var point = new ManualSyncPoint
        {
            RecvTime = Math.Round(map.ToRecvTime(graphSec), 4),
            VideoSec = Math.Round(videoSec, 4),
            Note = Clean(note),
            CreatedAt = Stamp(),
        };
        UpdateManualSync(s, () => SessionMeta.AppendManualSync(s.Folder, point), Strings.T("手動補正を保存しました"));
    }

    private void ClearManualSync()
    {
        if (Session is not LoadedSession s) return;
        UpdateManualSync(s, () => SessionMeta.ClearManualSync(s.Folder), Strings.T("手動補正を消しました"));
    }

    /// <summary>session.json を書き換え、読み直して描き直す(動画の位置はそのまま)</summary>
    private void UpdateManualSync(LoadedSession s, Func<string?> write, string done)
    {
        try
        {
            var error = write();
            if (error != null) { Notice(error: error); return; }
            s.Meta = SessionMeta.Load(s.Folder);
        }
        catch (Exception ex)
        {
            PlayerLog.Error("手動補正を session.json に書けない", ex);
            Notice(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return;
        }
        refresh();
        Notice(message: done);
    }

    // ---- レースのコース名・車名(記録アプリの「直前のレース」の直し方と同じ) ----

    /// <summary>
    /// 名前を付けるダイアログの中身: 登録済みのコース(スタートの位置で一致するものを先頭)、その車種の名前、同じ組のセッティング。
    /// 登録ファイルは読むだけ(書くのは記録アプリ。RegistryInbox 経由)
    /// </summary>
    private void SendRaceNamesForm(int raceIndex)
    {
        var s = Session;
        if (s == null || raceIndex < 0 || raceIndex >= s.Races.Count) return;
        var (seg, course, car, setup) = s.Races[raceIndex];
        var reg = Registry.TryRead(Path.Combine(s.SaveFolder, Registry.FileName)) ?? new Registry();
        var match = CourseMatcher.Match(reg.Courses, seg.StartX, seg.StartZ, seg.StartYaw);
        var sig = s.RaceCar(seg);
        var setups = sig is CarSignature cs ? reg.FindSetups(cs) : new List<RegisteredSetup>();
        shell.Post(new HostRaceNamesForm(
            Race: raceIndex,
            Editable: s.Package == null && s.Meta.Complete,
            Reason: s.Package != null ? Strings.T("パッケージの名前は変えられません") : !s.Meta.Complete ? Strings.T("記録中の記録には保存できません") : null,
            Course: new RaceNamesCourse(
                Current: course,
                MatchId: match?.Course.Id,
                List: reg.Courses
                    .OrderByDescending(c => c.Id == match?.Course.Id).ThenBy(c => c.Name, StringComparer.CurrentCulture)
                    .Select(c => new CourseChoice(c.Id, c.Name, c.Kind)).ToArray()),
            Car: sig is CarSignature g
                ? new RaceNamesCar(g.Ordinal, g.ToString(), car, reg.CarName(g.Ordinal), setup, setups.Select(x => new SetupChoice(x.Id, x.Name)).ToArray())
                : null));
    }

    /// <summary>
    /// レースに付けた名前を保存する: この記録の session.json に race_edited を足し(レースの一覧と書き出しにすぐ出る)、
    /// 保存先の登録の受け渡し用のファイルに頼みを足す(記録アプリが登録ファイルに取り込む)
    /// </summary>
    private void EditRaceNames(ViewEditRaceNames msg)
    {
        var s = Session;
        if (s == null || msg.Race is not int raceIndex) return;
        if (raceIndex < 0 || raceIndex >= s.Races.Count) return;
        if (s.Package != null) { Toast(error: Strings.T("パッケージの名前は変えられません")); return; }

        var seg = s.Races[raceIndex].Segment;
        var sig = s.RaceCar(seg);
        var reg = Registry.TryRead(Path.Combine(s.SaveFolder, Registry.FileName)) ?? new Registry();
        string? courseId = Clean(msg.CourseId), courseName = Clean(msg.CourseName), courseKind = Clean(msg.CourseKind);
        string? carName = Clean(msg.CarName), setupId = Clean(msg.SetupId), setupName = Clean(msg.SetupName);
        if (courseId == RegistryInbox.NewId && courseName == null) { Toast(error: Strings.T("レース名を入れてください")); return; }
        if (courseId != null && courseId != RegistryInbox.NewId && courseName == null) courseName = reg.CourseById(courseId)?.Name;
        if (setupId == RegistryInbox.NewId && setupName == null && sig is CarSignature g0) setupName = $"{g0.ClassName}{g0.Pi} {g0.DrivetrainName}";
        if (setupId != null && setupId != RegistryInbox.NewId && setupName == null) setupName = reg.SetupById(setupId)?.Name;

        var data = new JsonObject
        {
            ["race_start_recv_time"] = Math.Round(seg.StartRecvTime, 6),
            ["changes"] = new JsonArray("再生アプリで名前を付けた"),
            ["source"] = "player",
            ["course_id"] = courseId == RegistryInbox.NewId ? null : courseId,
            ["course_name"] = courseName, ["course_kind"] = courseKind,
            ["car_ordinal"] = sig?.Ordinal, ["car_name"] = carName,
            ["setup_id"] = setupId == RegistryInbox.NewId ? null : setupId, ["setup_name"] = setupName,
        };
        // 補助の設定(計測モード)。変えたときだけ画面から来る。値は on / off、変速は auto / manual、分からなければ null
        if (msg.Assists is ViewAssists asg)
        {
            static string? A(string? v) => v is { Length: > 0 } ? v : null;
            data["assists"] = new JsonObject { ["abs"] = A(asg.Abs), ["tcs"] = A(asg.Tcs), ["stm"] = A(asg.Stm), ["lc"] = A(asg.Lc), ["shift"] = A(asg.Shift) };
            ((JsonArray)data["changes"]!).Add("補助の設定");
        }
        var ev = new SessionEvent
        {
            RecvTime = Math.Round(Clock.Now(), 6), Type = "race_edited",
            Detail = $"再生アプリで名前を付けた: {courseName ?? "コース不明"} / {carName ?? "車両不明"}{(setupName != null ? $" {setupName}" : "")}",
            Data = data,
        };
        try
        {
            var error = SessionMeta.AppendEvent(s.Folder, ev);
            if (error != null) { Toast(error: error); return; }
            s.Meta = SessionMeta.Load(s.Folder);
            s.RefreshNames();
        }
        catch (Exception ex)
        {
            PlayerLog.Error("レースの名前を session.json に書けない", ex);
            Toast(error: Strings.T("session.json に書けませんでした: {message}", ("message", ex.Message)));
            return;
        }

        // 登録ファイルへは、受け渡し用のファイル経由で(記録アプリが取り込む)
        string registryNote;
        try
        {
            RegistryInbox.Append(s.SaveFolder, new RegistryRequest
            {
                CreatedAt = Stamp(),
                Session = Path.GetFileName(s.Folder), RaceStartRecvTime = Math.Round(seg.StartRecvTime, 6),
                CourseId = courseId, CourseName = courseName, CourseKind = courseKind,
                StartX = Math.Round(seg.StartX, 3), StartZ = Math.Round(seg.StartZ, 3), StartYaw = Math.Round(seg.StartYaw, 5),
                CarOrdinal = sig?.Ordinal, ClassRaw = sig?.Class, Pi = sig?.Pi, DrivetrainRaw = sig?.Drivetrain, Cylinders = sig?.Cylinders,
                CarName = carName, SetupId = setupId, SetupName = setupName,
            });
            registryNote = Strings.T("記録アプリを次に起動したとき(起動中なら数秒で)登録します");
        }
        catch (Exception ex)
        {
            PlayerLog.Error("登録の受け渡し用のファイルに書けない", ex);
            registryNote = Strings.T("登録には回せませんでした: {message}", ("message", ex.Message));
        }
        PlayerLog.Info($"レース {raceIndex + 1} の名前: {ev.Detail}");
        // 補助の設定を変えたら、プロファイルの部分の設定も付け直す(csv は同じなので、設定だけ)
        if (data.ContainsKey("assists"))
        {
            var profileError = s.UpdateProfiles(profiles);
            if (profileError != null) PlayerLog.Error($"車のプロファイルを保存できない: {profileError}");
        }
        refresh();
        Toast(message: Strings.T("名前を保存しました。{note}", ("note", registryNote)), sec: 6);
    }

    /// <summary>画面からの文字列: 前後の空白を除き、空なら null</summary>
    private static string? Clean(string? s) => s?.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>session.json などに書く、いまの日時(地域の時差つき)</summary>
    private string Stamp() => _clock.GetLocalNow().ToString("yyyy-MM-ddTHH:mm:sszzz");

    /// <summary>手動補正の欄の下に知らせる</summary>
    private void Notice(string? message = null, string? error = null) => shell.Post(new HostNotice(message, error));

    /// <summary>画面の下に短く知らせる。sec は出しておく秒数</summary>
    private void Toast(string? message = null, string? error = null, double? sec = null) => shell.Post(new HostToast(message, error, sec));
}
