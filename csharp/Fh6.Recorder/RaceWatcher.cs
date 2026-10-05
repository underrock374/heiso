using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// 受信したパケットでロガー v3 の判定(車両の切り替わり、レースの開始と終了、コースの照合)を常に動かし、
/// 画面の確認カード(車名・セッティング・コースの登録)と登録ファイル fh6_registry.json を扱う。
/// 記録中は、出来事を session.json の events に書く(AttachSession で渡された書き込み先へ)。
/// 直前のレースのコースと車は、レースが終わった後・記録を止めた後でも直せる(EditLastRace)。
/// session.json に書く出来事の説明(detail)は日本語のまま(記録データは訳さない)。画面の「最近のできごと」には、今の言語の文を別に作って出す。
/// </summary>
public sealed class RaceWatcher
{
    public static readonly IReadOnlyDictionary<string, string> CourseKinds = new Dictionary<string, string>
    {
        ["road"] = "ロード", ["dirt"] = "ダート", ["xc"] = "クロスカントリー", ["street"] = "ストリート", ["drag"] = "ドラッグ", ["other"] = "その他",
    };

    private readonly ILogger<RaceWatcher> _log;
    private readonly object _lock = new();
    private readonly RaceTracker _tracker = new();
    private readonly string _registryPath;
    private readonly List<Prompt> _prompts = new();

    private Registry? _registry;
    private string? _registryWarning;
    private Action<SessionEvent>? _sink;
    private string? _sessionFolder;
    private RaceRecord? _lastRace;
    private readonly List<(string At, string Text)> _recent = new();
    /// <summary>画面の速度の単位("kmh" / "mph")</summary>
    private readonly string _speedUnit = "kmh";
    private int _seq;
    private double _lastRecv;

    private string? _setupId;
    private string? _courseId;

    /// <summary>最近のできごとを画面に出す件数</summary>
    public const int RecentCount = 12;

    /// <summary>
    /// 記録を止めた後のセッションの session.json に出来事を足す(記録アプリの SessionController が設定する)。
    /// 引数はセッションフォルダと出来事。書けたら true
    /// </summary>
    public Func<string, SessionEvent, bool>? AppendToSession { get; set; }

    public RaceWatcher(RecorderOptions opt, TelemetryService telemetry, ILogger<RaceWatcher> log)
        : this(Path.Combine(opt.ResolveOutputDir(), Registry.FileName), log)
    {
        _speedUnit = opt.ResolveSpeedUnit();
        telemetry.PacketReceived += (recv, _, values) =>
        {
            if (values != null) OnPacket(recv, values);
        };
    }

    /// <summary>テスト用: 受信とつながず、登録ファイルの場所だけ決める</summary>
    public RaceWatcher(string registryPath, ILogger<RaceWatcher> log)
    {
        _registryPath = registryPath;
        _log = log;
    }

    private Registry Reg
    {
        get
        {
            if (_registry != null) return _registry;
            _registry = Registry.Load(_registryPath, out _registryWarning);
            if (_registryWarning != null) _log.LogWarning("{Warning}", _registryWarning);
            return _registry;
        }
    }

    private void SaveRegistry()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
            Reg.Save(_registryPath);
        }
        catch (Exception ex)
        {
            // 保存できなくても記録は止めない
            _registryWarning = Strings.T("登録ファイルを保存できません: {error}", ("error", ex.Message));
            _log.LogWarning(ex, "登録ファイルを保存できません");
        }
    }

    // ---------------------------------------------------------------- 受信

    // ---------------------------------------------------------------- 再生アプリからの登録の取り込み

    /// <summary>登録の受け渡し用のファイル(RegistryInbox)を見る間隔(秒)</summary>
    public const double InboxCheckSec = 3;
    private DateTime _inboxCheckedAt = DateTime.MinValue;

    /// <summary>
    /// 再生アプリがレースに付けた名前(保存先の fh6_registry_inbox.json)を登録ファイルに取り込み、受け渡し用のファイルを消す。
    /// 画面の更新と受信のついでに InboxCheckSec ごとに呼ぶ(force なら間隔によらず)
    /// </summary>
    public void MergeInbox(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _inboxCheckedAt).TotalSeconds < InboxCheckSec) return;
        lock (_lock)
        {
            _inboxCheckedAt = DateTime.UtcNow;
            List<RegistryRequest> requests;
            try
            {
                requests = RegistryInbox.Take(Path.GetDirectoryName(_registryPath)!, out var warning);
                if (warning != null)
                {
                    _registryWarning = warning;
                    _log.LogWarning("{Warning}", warning);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "登録の受け渡し用のファイルを読めません");
                return;
            }
            if (requests.Count == 0) return;
            var changes = requests.SelectMany(r => RegistryInbox.Apply(Reg, r)).ToList();
            if (changes.Count == 0) return;
            SaveRegistry();
            foreach (var c in changes)
            {
                _recent.Add((NowText(), Strings.T("再生アプリから {change}", ("change", c))));
                if (_recent.Count > RecentCount) _recent.RemoveAt(0);
            }
            _log.LogInformation("再生アプリで付けた名前を登録ファイルに取り込みました: {Changes}", string.Join("、", changes));
        }
    }

    public void OnPacket(double recv, double[] values)
    {
        MergeInbox();
        var sample = RaceSample.FromPacket(recv, values);
        lock (_lock)
        {
            _lastRecv = recv;
            foreach (var e in _tracker.Feed(sample))
            {
                switch (e)
                {
                    case CarChanged c: OnCarChanged(c.RecvTime, c.TimestampMs, c.Car, c.Changes, guess: true); break;
                    case RaceStarted s: OnRaceStarted(s); break;
                    case RaceEnded r:
                        if (_lastRace != null) _lastRace.EndRecv = r.RecvTime;
                        Write(r.RecvTime, "race_end", $"{r.LastSpeedKmh:0} km/h で終了", new JsonObject
                        {
                            ["timestamp_ms"] = r.TimestampMs, ["last_speed_kmh"] = r.LastSpeedKmh, ["course_id"] = _courseId,
                        }, Strings.T("{speed} で終了", ("speed", SpeedText(r.LastSpeedKmh))));
                        break;
                }
            }
        }
    }

    private void OnCarChanged(double recv, double timestampMs, CarSignature sig, List<string> changed, bool guess)
    {
        var name = Reg.CarName(sig.Ordinal);
        var setups = Reg.FindSetups(sig);
        var setup = guess ? setups.LastOrDefault() : null;
        _setupId = setup?.Id;

        // 同じ車両の未確定のカードは作り直す(別の車両のカードは、確定するまで残す)
        _prompts.RemoveAll(p => p is CarPrompt cp && cp.Car == sig);
        _prompts.Add(new CarPrompt(NextId(), NowText(), sig, changed, name,
            setups.Select(s => new SetupChoice(s.Id, s.Name, s.Variant)).ToList(), setup?.Id));

        Write(recv, "car_detected", $"{name ?? "未登録の車両"} {sig}({string.Join("・", changed)})", new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["ordinal"] = sig.Ordinal, ["class_raw"] = sig.Class, ["pi"] = sig.Pi, ["drivetrain_raw"] = sig.Drivetrain, ["cylinders"] = sig.Cylinders,
            ["changed"] = new JsonArray(changed.Select(x => (JsonNode?)x).ToArray()),
            ["name"] = name, ["setup_id"] = setup?.Id, ["setup_name"] = setup?.Name,
        }, CarText(name, sig, changed));
    }

    private void OnRaceStarted(RaceStarted s)
    {
        var start = new JsonObject { ["x"] = Math.Round(s.X, 2), ["z"] = Math.Round(s.Z, 2), ["yaw"] = Math.Round(s.Yaw, 5) };
        var hit = Reg.MatchAndUpdate(s.X, s.Z, s.Yaw);
        var data = new JsonObject
        {
            ["timestamp_ms"] = s.TimestampMs, ["restart"] = s.Restart, ["start"] = start, ["matched"] = hit != null,
            ["course_id"] = hit?.Course.Id, ["course_name"] = hit?.Course.Name,
            ["grid_offset_m"] = hit?.GridOffsetM, ["car_setup_id"] = _setupId,
        };
        var what = s.Restart ? "リスタート/次のレース" : "レース開始";
        var shownWhat = s.Restart ? Strings.T("リスタート/次のレース") : Strings.T("レース開始");
        _lastRace = new RaceRecord
        {
            StartRecv = s.RecvTime, StartedAt = NowText(), X = s.X, Z = s.Z, Yaw = s.Yaw,
            Car = _tracker.Car, SetupId = _setupId, CourseId = hit?.Course.Id, Recorded = _sink != null, Folder = _sessionFolder,
        };
        if (hit is CourseMatch m)
        {
            _courseId = m.Course.Id;
            SaveRegistry();
            Write(s.RecvTime, "race_start", $"{what}: {m.Course.Name}{(m.GridOffsetM > 5 ? $"(グリッド後方 {m.GridOffsetM:0}m)" : "")}", data,
                $"{shownWhat}: {m.Course.Name}" + (m.GridOffsetM > 5 ? Strings.T("(グリッド後方 {m:F0}m)", ("m", m.GridOffsetM)) : ""));
        }
        else
        {
            _courseId = null;
            _prompts.RemoveAll(p => p is CoursePrompt);
            _prompts.Add(new CoursePrompt(NextId(), NowText(), Math.Round(s.X, 2), Math.Round(s.Z, 2), Math.Round(s.Yaw, 5), s.RecvTime));
            Write(s.RecvTime, "race_start", $"{what}: 未登録のコース", data, Strings.T("{what}: 未登録のコース", ("what", shownWhat)));
        }
    }

    // ---------------------------------------------------------------- 記録との接続

    /// <summary>記録を始めた。出来事の書き込み先を受け取り、今の車両を「記録開始時の車両」として書く</summary>
    public void AttachSession(Action<SessionEvent> sink, string? folder = null)
    {
        lock (_lock)
        {
            _sink = sink;
            _sessionFolder = folder;
            if (_tracker.Car is not CarSignature sig) return;   // まだ車両が分からない: 最初の検知で書く
            bool confirmed = _setupId != null && !_prompts.Any(p => p is CarPrompt cp && cp.Car == sig);
            var setup = _setupId != null ? Reg.SetupById(_setupId) : null;
            if (confirmed)
            {
                // 確定済みならカードは出さず、出来事だけ書く
                var name = Reg.CarName(sig.Ordinal);
                Write(_lastRecv, "car_detected", $"{name} {sig}(記録開始時の車両)", CarData(sig, new() { "記録開始時の車両" }, name, setup),
                    CarText(name, sig, new() { "記録開始時の車両" }));
            }
            else
            {
                OnCarChanged(_lastRecv, double.NaN, sig, new() { "記録開始時の車両" }, guess: true);
            }
        }
    }

    public void DetachSession()
    {
        lock (_lock)
        {
            _sink = null;
            _sessionFolder = null;
        }
    }

    private static JsonObject CarData(CarSignature sig, List<string> changed, string? name, RegisteredSetup? setup) => new()
    {
        ["ordinal"] = sig.Ordinal, ["class_raw"] = sig.Class, ["pi"] = sig.Pi, ["drivetrain_raw"] = sig.Drivetrain, ["cylinders"] = sig.Cylinders,
        ["changed"] = new JsonArray(changed.Select(x => (JsonNode?)x).ToArray()),
        ["name"] = name, ["setup_id"] = setup?.Id, ["setup_name"] = setup?.Name,
    };

    /// <summary>出来事を書く。detail は記録に書く日本語の説明、shown は画面の「最近のできごと」に出す今の言語の文(省略すると detail)</summary>
    private void Write(double recv, string type, string detail, JsonObject data, string? shown = null)
    {
        if (data["timestamp_ms"] is JsonValue v && v.TryGetValue<double>(out var ts) && double.IsNaN(ts)) data.Remove("timestamp_ms");
        _recent.Add((NowText(), RecentText(type, shown ?? detail)));
        if (_recent.Count > RecentCount) _recent.RemoveAt(0);
        var sink = _sink;
        if (sink == null) return;
        try
        {
            sink(new SessionEvent { RecvTime = Math.Round(recv, 6), Type = type, Detail = detail, Data = data });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "出来事を書けませんでした");
        }
    }

    /// <summary>そのレースを記録したセッションに書く(記録中ならその記録へ、止めた後なら session.json に足す)。記録していないレースなら書かない</summary>
    private void WriteForRace(RaceRecord race, string type, string detail, JsonObject data, string? shown = null)
    {
        _recent.Add((NowText(), RecentText(type, shown ?? detail)));
        if (_recent.Count > RecentCount) _recent.RemoveAt(0);
        if (!race.Recorded) return;
        var ev = new SessionEvent { RecvTime = Math.Round(Clock.Now(), 6), Type = type, Detail = detail, Data = data };
        try
        {
            if (race.Folder == _sessionFolder && _sink != null) _sink(ev);
            else if (race.Folder == null || AppendToSession?.Invoke(race.Folder, ev) != true)
                _log.LogWarning("記録 {Folder} に修正を書けませんでした", race.Folder);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "記録 {Folder} に修正を書けませんでした", race.Folder);
        }
    }

    private static string RecentText(string type, string text) => type switch
    {
        "car_detected" => Strings.T("車両を検出: {detail}", ("detail", text)),
        "car_confirmed" => Strings.T("車両を確定: {detail}", ("detail", text)),
        "race_end" => Strings.T("レース終了({detail})", ("detail", text)),
        "course_confirmed" => Strings.T("コースを登録: {detail}", ("detail", text)),
        _ => text,
    };

    /// <summary>画面に出す車両の説明(今の言語): 名前 クラス PI 駆動 気筒数(変わったもの)</summary>
    private static string CarText(string? name, CarSignature sig, List<string> changed) =>
        Strings.T("{name} {car}({changed})", ("name", name ?? Strings.T("未登録の車両")), ("car", SigText(sig)),
            ("changed", string.Join(Strings.T("・"), changed.Select(ChangeName))));

    /// <summary>クラス PI 駆動 気筒数(今の言語)</summary>
    private static string SigText(CarSignature sig) =>
        Strings.T("{cls} {pi} {drive} {cyl}気筒", ("cls", sig.ClassName), ("pi", sig.Pi), ("drive", sig.DrivetrainName), ("cyl", sig.Cylinders));

    /// <summary>車両の変わったもの(Fh6.Core の CarSignature.Changes と、手動のセッティング変更)の、今の言語の名前</summary>
    private static string ChangeName(string ja) => ja switch
    {
        "記録開始時の車両" => Strings.T("記録開始時の車両"),
        "車種" => Strings.T("車種"),
        "PI" => "PI",
        "駆動方式" => Strings.T("駆動方式"),
        "気筒数" => Strings.T("気筒数"),
        "セッティング(手動)" => Strings.T("セッティング(手動)"),
        _ => ja,
    };

    /// <summary>コースの種類の、今の言語の名前(画面の選択肢)</summary>
    private static Dictionary<string, string> CourseKindNames() => new()
    {
        ["road"] = Strings.T("ロード"), ["dirt"] = Strings.T("ダート"), ["xc"] = Strings.T("クロスカントリー"),
        ["street"] = Strings.T("ストリート"), ["drag"] = Strings.T("ドラッグ"), ["other"] = Strings.T("その他"),
    };

    /// <summary>速度(今の単位)</summary>
    private string SpeedText(double kmh) => _speedUnit == "mph"
        ? $"{(kmh / 1.609344).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} mph"
        : $"{kmh.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} km/h";

    // ---------------------------------------------------------------- 画面から

    /// <summary>車両の確認カード: 車名とセッティングを確定する。setupChoice は既存のセッティングの id か "new"</summary>
    public void ResolveCar(string promptId, string? carName, string? setupChoice, string? setupName, string? note)
    {
        lock (_lock)
        {
            var p = _prompts.OfType<CarPrompt>().FirstOrDefault(x => x.Id == promptId)
                    ?? throw new UserError(Strings.T("この確認はすでに閉じられています"));
            carName = carName?.Trim();
            if (string.IsNullOrEmpty(carName)) throw new UserError(Strings.T("車名を入力してください"));
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            setupName = setupName?.Trim();

            Reg.SetCarName(p.Car.Ordinal, carName);
            RegisteredSetup st;
            if (!string.IsNullOrEmpty(setupChoice) && setupChoice != "new")
            {
                st = Reg.SetupById(setupChoice) ?? throw new UserError(Strings.T("セッティングが見つかりません"));
                if (!string.IsNullOrEmpty(setupName)) st.Name = setupName;
            }
            else
            {
                st = Reg.AddSetup(p.Car, string.IsNullOrEmpty(setupName) ? $"{p.Car.ClassName}{p.Car.Pi} {p.Car.DrivetrainName}" : setupName, note);
            }
            SaveRegistry();
            if (_tracker.Car == p.Car) _setupId = st.Id;
            // 走っている(走り終えたばかりの)レースの車なら、そのレースのセッティングにする
            if (_lastRace != null && _lastRace.Car == p.Car && (_lastRace.EndRecv == null || _lastRace.SetupId == null))
                _lastRace.SetupId = st.Id;
            _prompts.Remove(p);

            var data = CarData(p.Car, p.Changed, carName, st);
            data.Remove("changed");
            data["note"] = note;
            Write(_lastRecv, "car_confirmed", $"{carName} / {st.Name}", data);
        }
    }

    /// <summary>PI の変わらないセッティングの変更を知らせる(セッティング名を聞くカードを出す)</summary>
    public void NewVariant()
    {
        lock (_lock)
        {
            if (_tracker.Car is not CarSignature sig) throw new UserError(Strings.T("まだ車両を受信していません"));
            OnCarChanged(_lastRecv, double.NaN, sig, new() { "セッティング(手動)" }, guess: false);
        }
    }

    /// <summary>コースの確認カード: 新しく登録する(choice = "new")か、登録済みのコースを選ぶ</summary>
    public void ResolveCourse(string promptId, string? choice, string? name, string? kind, string? note)
    {
        lock (_lock)
        {
            var p = _prompts.OfType<CoursePrompt>().FirstOrDefault(x => x.Id == promptId)
                    ?? throw new UserError(Strings.T("この確認はすでに閉じられています"));
            RegisteredCourse c;
            if (!string.IsNullOrEmpty(choice) && choice != "new")
            {
                c = Reg.CourseById(choice) ?? throw new UserError(Strings.T("コースが見つかりません"));
                c.Seen = (c.Seen ?? 0) + 1;
            }
            else
            {
                name = name?.Trim();
                if (string.IsNullOrEmpty(name)) throw new UserError(Strings.T("レース名を入力してください"));
                if (kind != null && !CourseKinds.ContainsKey(kind)) kind = "other";
                c = Reg.AddCourse(name, kind, p.X, p.Z, p.Yaw, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
            }
            SaveRegistry();
            _courseId = c.Id;
            _prompts.Remove(p);
            var data = new JsonObject
            {
                ["course_id"] = c.Id, ["course_name"] = c.Name, ["course_kind"] = c.Kind,
                ["race_start_recv_time"] = Math.Round(p.RaceStartRecvTime, 6),
                ["start"] = new JsonObject { ["x"] = p.X, ["z"] = p.Z, ["yaw"] = p.Yaw },
            };
            if (_lastRace != null && _lastRace.StartRecv == p.RaceStartRecvTime)
            {
                // レースを記録したセッションへ(記録を止めた後でも)
                _lastRace.CourseId = c.Id;
                WriteForRace(_lastRace, "course_confirmed", $"コース: {c.Name}", data, c.Name);
            }
            else
            {
                Write(_lastRecv, "course_confirmed", $"コース: {c.Name}", data, c.Name);
            }
        }
    }

    /// <summary>
    /// 直前のレースのコースと車を直す(レースが終わった後・記録を止めた後でも)。
    /// courseChoice: 登録済みのコースの id か "new"(新しく登録)。courseName を変えると、そのコースの名前を変える(登録ファイルの名前が変わる)。
    /// carName を変えると、その車種の名前を変える。setupChoice: 登録済みのセッティングの id か "new"。
    /// </summary>
    public void EditLastRace(string? courseChoice, string? courseName, string? kind, string? carName, string? setupChoice, string? setupName)
    {
        lock (_lock)
        {
            var race = _lastRace ?? throw new UserError(Strings.T("まだレースを見つけていません"));
            courseName = courseName?.Trim();
            carName = carName?.Trim();
            setupName = setupName?.Trim();
            var changes = new List<string>();

            // コース
            RegisteredCourse? course = null;
            if (courseChoice == "new")
            {
                if (string.IsNullOrEmpty(courseName)) throw new UserError(Strings.T("レース名を入力してください"));
                if (kind != null && !CourseKinds.ContainsKey(kind)) kind = "other";
                course = Reg.AddCourse(courseName, kind, race.X, race.Z, race.Yaw, null);
                changes.Add("コースを登録");
            }
            else if (!string.IsNullOrEmpty(courseChoice))
            {
                course = Reg.CourseById(courseChoice) ?? throw new UserError(Strings.T("コースが見つかりません"));
                if (course.Id != race.CourseId) changes.Add("コースを変更");
                if (!string.IsNullOrEmpty(courseName) && courseName != course.Name)
                {
                    course.Name = courseName;
                    changes.Add("コース名を変更");
                }
            }
            if (course != null)
            {
                if (race.CourseId == _courseId) _courseId = course.Id;   // 今もそのレースのコースにいる
                race.CourseId = course.Id;
                // このレースについてのコースのカードは閉じる
                _prompts.RemoveAll(p => p is CoursePrompt cp && cp.RaceStartRecvTime == race.StartRecv);
            }

            // 車
            RegisteredSetup? setup = null;
            if (race.Car is CarSignature sig)
            {
                if (!string.IsNullOrEmpty(carName) && carName != Reg.CarName(sig.Ordinal))
                {
                    Reg.SetCarName(sig.Ordinal, carName);
                    changes.Add("車名を変更");
                }
                if (setupChoice == "new")
                {
                    setup = Reg.AddSetup(sig, string.IsNullOrEmpty(setupName) ? $"{sig.ClassName}{sig.Pi} {sig.DrivetrainName}" : setupName, null);
                    changes.Add("セッティングを登録");
                }
                else if (!string.IsNullOrEmpty(setupChoice))
                {
                    setup = Reg.SetupById(setupChoice);
                    if (setup == null || !setup.Matches(sig)) throw new UserError(Strings.T("セッティングが見つかりません"));
                    if (setup.Id != race.SetupId) changes.Add("セッティングを変更");
                    if (!string.IsNullOrEmpty(setupName) && setupName != setup.Name)
                    {
                        setup.Name = setupName;
                        changes.Add("セッティング名を変更");
                    }
                }
                if (setup != null)
                {
                    if (_tracker.Car == sig && _setupId == race.SetupId) _setupId = setup.Id;
                    race.SetupId = setup.Id;
                    // 車名とセッティングが決まったので、同じ車両の確認カードは閉じる
                    if (Reg.CarName(sig.Ordinal) != null) _prompts.RemoveAll(p => p is CarPrompt cp && cp.Car == sig);
                }
            }
            else if (!string.IsNullOrEmpty(carName) || !string.IsNullOrEmpty(setupChoice))
            {
                throw new UserError(Strings.T("このレースの車両が分かりません(車両を検知する前に始まったレースです)"));
            }

            // 変更が無くても、今の名前をそのレースの記録に書く(カードで確定した名前を、このレースのものとして残す)
            if (changes.Count == 0) changes.Add("確認");
            else SaveRegistry();

            var c = race.CourseId != null ? Reg.CourseById(race.CourseId) : null;
            var st = race.SetupId != null ? Reg.SetupById(race.SetupId) : null;
            var name = race.Car is CarSignature s2 ? Reg.CarName(s2.Ordinal) : null;
            WriteForRace(race, "race_edited", $"直前のレースを修正: {c?.Name ?? "コース不明"} / {name ?? "車両不明"}{(st != null ? $" {st.Name}" : "")}", new JsonObject
            {
                ["race_start_recv_time"] = Math.Round(race.StartRecv, 6),
                ["changes"] = new JsonArray(changes.Select(x => (JsonNode?)x).ToArray()),
                ["course_id"] = c?.Id, ["course_name"] = c?.Name, ["course_kind"] = c?.Kind,
                ["car_ordinal"] = race.Car?.Ordinal, ["car_name"] = name,
                ["setup_id"] = st?.Id, ["setup_name"] = st?.Name,
            }, Strings.T("直前のレースを修正: {course} / {car}{setup}", ("course", c?.Name ?? Strings.T("コース不明")), ("car", name ?? Strings.T("車両不明")), ("setup", st != null ? $" {st.Name}" : "")));
        }
    }

    /// <summary>「あとで」: カードを閉じる</summary>
    public void Dismiss(string promptId)
    {
        lock (_lock) _prompts.RemoveAll(p => p.Id == promptId);
    }

    // ---------------------------------------------------------------- 状態

    public object Snapshot()
    {
        MergeInbox();
        lock (_lock)
        {
            object? car = null;
            if (_tracker.Car is CarSignature sig)
            {
                var st = _setupId != null ? Reg.SetupById(_setupId) : null;
                car = new
                {
                    ordinal = sig.Ordinal, cls = sig.ClassName, pi = sig.Pi, drive = sig.DrivetrainName, cyl = sig.Cylinders,
                    name = Reg.CarName(sig.Ordinal), setup = st?.Name,
                    confirmed = st != null && !_prompts.Any(p => p is CarPrompt cp && cp.Car == sig),
                };
            }
            var course = _courseId != null ? Reg.CourseById(_courseId) : null;
            return new
            {
                car,
                course = course == null ? null : new { id = course.Id, name = course.Name, kind = course.Kind },
                inRace = _tracker.InRace,
                prompts = _prompts.Select(p => p switch
                {
                    CarPrompt c => (object)new
                    {
                        id = c.Id, type = "car", created = c.Created,
                        sig = new { ordinal = c.Car.Ordinal, cls = c.Car.ClassName, pi = c.Car.Pi, drive = c.Car.DrivetrainName, cyl = c.Car.Cylinders },
                        // changed は今の言語の名前。atStart = 記録を始めたときの車両(変わったのではない)
                        changed = c.Changed.Select(ChangeName).ToList(), atStart = c.Changed.Contains("記録開始時の車両"), knownName = c.KnownName,
                        setups = c.Setups.Select(s => new { id = s.Id, name = s.Name, variant = s.Variant }),
                        guessSetup = c.GuessSetup,
                    },
                    CoursePrompt c => new
                    {
                        id = c.Id, type = "course", created = c.Created,
                        start = new { x = c.X, z = c.Z, yaw = c.Yaw },
                    },
                    _ => new { id = p.Id, type = "unknown", created = p.Created },
                }).ToList(),
                known = new
                {
                    cars = Reg.Cars.Values.Select(c => c.Name).Distinct().Order().ToList(),
                    courses = Reg.Courses.Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, seen = c.Seen ?? 0 }).ToList(),
                },
                kinds = CourseKindNames(),
                lastRace = LastRaceSnapshot(),
                recent = _recent.AsEnumerable().Reverse().Select(r => new { at = r.At, text = r.Text }).ToList(),
                registryWarning = _registryWarning,
            };
        }
    }

    private object? LastRaceSnapshot()
    {
        var r = _lastRace;
        if (r == null) return null;
        var c = r.CourseId != null ? Reg.CourseById(r.CourseId) : null;
        var st = r.SetupId != null ? Reg.SetupById(r.SetupId) : null;
        return new
        {
            startedAt = r.StartedAt,
            ended = r.EndRecv != null,
            recorded = r.Recorded ? Path.GetFileName(r.Folder ?? "") : null,
            course = c == null ? null : new { id = c.Id, name = c.Name, kind = c.Kind },
            car = r.Car is CarSignature sig ? new
            {
                ordinal = sig.Ordinal, cls = sig.ClassName, pi = sig.Pi, drive = sig.DrivetrainName, cyl = sig.Cylinders,
                name = Reg.CarName(sig.Ordinal), setupId = st?.Id, setup = st?.Name,
                setups = Reg.FindSetups(sig).Select(s => new { id = s.Id, name = s.Name }).ToList(),
            } : null,
        };
    }

    private string NextId() => $"P{++_seq:D4}";
    private static string NowText() => DateTime.Now.ToString("HH:mm");

    private abstract record Prompt(string Id, string Created);
    private sealed record CarPrompt(string Id, string Created, CarSignature Car, List<string> Changed, string? KnownName,
                                    List<SetupChoice> Setups, string? GuessSetup) : Prompt(Id, Created);
    private sealed record CoursePrompt(string Id, string Created, double X, double Z, double Yaw, double RaceStartRecvTime) : Prompt(Id, Created);
    private sealed record SetupChoice(string Id, string Name, int Variant);

    /// <summary>直前のレース(後から直せるように、見つけた時点の値を持つ)</summary>
    private sealed class RaceRecord
    {
        public double StartRecv;
        public string StartedAt = "";
        public double? EndRecv;
        public double X, Z, Yaw;
        public CarSignature? Car;
        public string? SetupId;
        public string? CourseId;
        /// <summary>記録中に始まったレースか。そうならそのセッションフォルダ</summary>
        public bool Recorded;
        public string? Folder;
    }
}

/// <summary>画面にそのまま出してよい、利用者の操作の誤り</summary>
public sealed class UserError(string message) : Exception(message);
