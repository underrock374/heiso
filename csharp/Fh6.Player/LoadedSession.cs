using System.IO;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>そのまま画面に出してよい(利用者向けの)エラー</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>開いたセッション。session.json とグラフに使う列、開くときに一度だけ計算する値を持つ</summary>
public sealed class LoadedSession
{
    private static readonly string[] Wheels = { "fl", "fr", "rl", "rr" };

    private static readonly string[] Columns =
        new[]
        {
            TelemetryTable.RecvTime, "timestamp_ms", "is_race_on", "speed", "accel", "brake", "steer", "gear",
            "race_pos", "position_x", "position_y", "position_z", "acceleration_x", "acceleration_z", "angular_velocity_y",
            "lap_no", "cur_lap", "dist_traveled", "yaw", "current_engine_rpm", "power",
        }
        .Concat(Wheels.Select(w => "norm_suspension_travel_" + w))
        .Concat(Wheels.Select(w => "tire_combined_slip_" + w))
        .Concat(Wheels.Select(w => "surface_rumble_" + w))
        .Concat(Wheels.Select(w => "wheel_on_rumble_strip_" + w))
        .Concat(Wheels.Select(w => "wheel_in_puddle_" + w))
        .Concat(Wheels.Select(w => "tire_slip_ratio_" + w))   // ギア比(ホイールスピンの行を除く)
        .Union(RaceSample.Columns)
        .Union(RaceTimeline.Columns)
        .Union(ReplayDetector.Columns)   // リプレイの自動判別の検算(角速度)
        .ToArray();

    public required string Folder { get; init; }
    /// <summary>session.json。手動補正を保存したら読み直して差し替える</summary>
    public required SessionMeta Meta { get; set; }
    public required TelemetryTable Table { get; init; }
    public string? VideoPath { get; init; }

    /// <summary>配布パッケージを展開したフォルダなら、その package.json。普通の記録なら null</summary>
    public PackageInfo? Package { get; init; }

    /// <summary>録画ファイルのフレームレート(session.json の video.file_fps、無ければファイルから読む)。分からなければ null</summary>
    public double? Fps { get; init; }

    /// <summary>スタート(止まった状態から動き出した瞬間)</summary>
    public required List<StartEvent> Starts { get; init; }

    /// <summary>接触と着地の候補</summary>
    public required List<ContactEvent> Contacts { get; init; }

    /// <summary>周のスタートとゴール</summary>
    public required List<LapEvent> Laps { get; init; }

    /// <summary>レース区間(ロガー v3 の判定)と、そのコース名・車名・セッティング名(session.json の出来事から。無ければ null)</summary>
    public required List<(RaceSegment Segment, string? Course, string? Car, string? Setup)> Races { get; set; }

    /// <summary>レースのコース名・車名を session.json の出来事から付け直す(再生アプリで名前を付けた後に。パッケージは package.json のまま)</summary>
    public void RefreshNames()
    {
        if (Package != null || IsReplay) return;
        Races = Races.Select(r =>
        {
            var (car, setup) = RaceNames.Car(Meta.Events, r.Segment.StartRecvTime);
            return (r.Segment, RaceNames.Course(Meta.Events, r.Segment.StartRecvTime), car, setup);
        }).ToList();
    }

    /// <summary>レース開始の行の車両の組(車種 ID が無ければ null)</summary>
    public CarSignature? RaceCar(RaceSegment r)
    {
        var s = RaceSample.FromTable(Table, r.StartIndex);
        return s.Car.Ordinal > 0 ? s.Car : null;
    }

    /// <summary>記録アプリの保存先(このセッションフォルダの 1 つ上。登録ファイルと受け渡し用のファイルがある)</summary>
    public string SaveFolder => Path.GetDirectoryName(Path.GetFullPath(Folder).TrimEnd('\\', '/'))!;

    /// <summary>ジャンプ(空中の区間)</summary>
    public required List<AirEvent> Jumps { get; init; }

    /// <summary>行ごとに 4 輪とも伸び切っているか(空中)</summary>
    public required bool[] Extended { get; init; }

    /// <summary>路面の逸脱(舗装から外れた瞬間)と、それに続くスリップ</summary>
    public required List<SurfaceExcursion> Excursions { get; init; }

    /// <summary>車ごとの性能(パワーカーブ・ギア比・最適な変速回転数)と、その車の行</summary>
    public required List<(PowerProfile Profile, List<int> Rows)> Power { get; init; }

    /// <summary>全開のシフトアップ(性能モードの変速の点)</summary>
    public required List<ShiftEvent> Shifts { get; init; }

    /// <summary>行ごとに、リミッターに張り付いているか(変速の得失を「早い」にしない)</summary>
    public required bool[] LimiterBounce { get; init; }

    public required double[] Grade { get; init; }
    public required double[] Turn { get; init; }

    /// <summary>
    /// リプレイの仮想セッションか(ForReplay で作ったもの)。行は最終の時間軸の行だけで、recv_time は連続した仮想の時刻。
    /// 停止区間もやり直しも無いので、スキップ区間は出さない
    /// </summary>
    public bool IsReplay { get; init; }

    /// <summary>リプレイの仮想セッションの、元のレースの最終の時間軸(普通の記録なら null)</summary>
    public RaceTimeline? Timeline { get; init; }

    /// <summary>リプレイの仮想セッションの先頭の行の仮想の時刻(= 元の記録での、最終の時間軸の先頭の行の recv_time)</summary>
    public double VirtualStart { get; init; }

    /// <summary>リプレイの仮想の時刻 → レースタイム(仮想の時刻は、レースタイムと同じ速さで進む)</summary>
    public double RaceTimeAtVirtual(double recv) => Timeline!.RaceTime[0] + (recv - VirtualStart);

    /// <summary>リプレイの、レースタイム → 仮想の時刻</summary>
    public double VirtualAtRaceTime(double raceTime) => VirtualStart + (raceTime - Timeline!.RaceTime[0]);

    public static LoadedSession Load(string folder)
    {
        if (!Directory.Exists(folder))
            throw new UserFacingException(Strings.T("フォルダがありません: {folder}", ("folder", folder)));
        if (!File.Exists(Path.Combine(folder, SessionMeta.FileName)))
            throw new UserFacingException(Strings.T("このフォルダには session.json がありません。記録アプリが作ったセッションフォルダ(yyyyMMdd_HHmmss。後ろにコメントが付いていてもよい)を選んでください"));

        SessionMeta meta;
        try { meta = SessionMeta.Load(folder); }
        catch (Exception ex) { throw new UserFacingException(Strings.T("session.json を読めませんでした: {message}", ("message", ex.Message))); }

        PackageInfo? package;
        try { package = PackageInfo.TryLoad(folder); }
        catch (Exception ex) { throw new UserFacingException(Strings.T("package.json を読めませんでした: {message}", ("message", ex.Message))); }

        var csv = Path.Combine(folder, string.IsNullOrEmpty(meta.Files.TelemetryCsv) ? SessionWriter.CsvName : meta.Files.TelemetryCsv);
        if (!File.Exists(csv))
            throw new UserFacingException(Strings.T("テレメトリーのファイルがありません: {file}", ("file", Path.GetFileName(csv))));

        var t = TelemetryTable.Load(csv, Columns);
        foreach (var c in Columns)
            if (!t.Contains(c)) throw new UserFacingException(Strings.T("テレメトリーに {column} の列がありません", ("column", c)));

        var video = meta.FindVideo(folder);
        return Build(folder, meta, package, t, video, meta.Video.FileFps ?? (video != null ? VideoDuration.TryReadFrameRate(video) : null),
            table => package != null ? PackageRaces(table, package) : RaceSegmentDetector.Detect(table).Select(r =>
            {
                var (car, setup) = RaceNames.Car(meta.Events, r.StartRecvTime);
                return (r, RaceNames.Course(meta.Events, r.StartRecvTime), car, setup);
            }).ToList());
    }

    /// <summary>
    /// リプレイの仮想セッションを作る: そのレースの最終の時間軸(RaceTimeline)の行だけを並べ、recv_time を連続した仮想の時刻に振り直す。
    /// 仮想の時刻の起点は、元の記録での最終の時間軸の先頭の行の時刻。レースは 1 本(元のレースの名前のまま)。
    /// グラフ・印・距離軸・コース図は、普通の記録と同じ作り方でこの表から作る
    /// </summary>
    public LoadedSession ForReplay(int raceIndex, string? videoPath, double? fps)
    {
        var (seg, course, car, setup) = Races[raceIndex];
        var timeline = RaceTimeline.Build(Table, seg);
        if (timeline.Rows.Count < 2) throw new UserFacingException(Strings.T("このレースには、リプレイに合わせられる走行がありません"));
        double start = Table[TelemetryTable.RecvTime][timeline.Rows[0]];
        var vt = timeline.VirtualTable(Table, start);
        var recv = vt[TelemetryTable.RecvTime];
        int last = vt.RowCount - 1;
        var race = seg with
        {
            StartIndex = 0, EndIndex = last, FinishIndex = last, StartRecvTime = recv[0], EndRecvTime = recv[last],
            EndSpeedKmh = Math.Round(vt["speed"][last] * 3.6, 1),
        };
        return Build(Folder, Meta, Package, vt, videoPath, fps, _ => new() { (race, course, car, setup) }, isReplay: true, timeline, start);
    }

    private static LoadedSession Build(string folder, SessionMeta meta, PackageInfo? package, TelemetryTable t, string? video, double? fps,
        Func<TelemetryTable, List<(RaceSegment, string?, string?, string?)>> races, bool isReplay = false, RaceTimeline? timeline = null, double virtualStart = 0)
    {
        // 判定の時刻は受信時刻(ms)。timestamp_ms は 60fps 近くでは約 32ms 刻みで、同じ値が続くため
        var ts = t[TelemetryTable.RecvTime].Select(r => r * 1000).ToArray();
        var on = t["is_race_on"];
        var extended = AirDetector.AllWheelsExtended(on, t["norm_suspension_travel_fl"], t["norm_suspension_travel_fr"],
                                                     t["norm_suspension_travel_rl"], t["norm_suspension_travel_rr"]);
        var (segment, distance) = TrackDistance.Compute(on, t["position_x"], t["position_z"]);
        var slip = Enumerable.Range(0, t.RowCount).Select(i => Math.Max(
            (t["tire_combined_slip_fl"][i] + t["tire_combined_slip_fr"][i]) / 2,
            (t["tire_combined_slip_rl"][i] + t["tire_combined_slip_rr"][i]) / 2)).ToArray();

        // 性能モード: 車ごとのパワーカーブとギア比、全開のシフトアップ(is_race_on の行を時刻の順に)
        var raceRows = Enumerable.Range(0, t.RowCount).Where(i => on[i] == 1 && double.IsFinite(ts[i])).OrderBy(i => ts[i]).ToList();
        var power = PowerAnalysis.Profiles(raceRows, i => ((int)t["car_ordinal"][i], (int)t["car_pi"][i]), ts,
            t["current_engine_rpm"], t["power"], t["accel"], t["gear"], t["speed"], Wheels.Select(w => t["tire_slip_ratio_" + w]).ToArray());
        var profileOf = new Dictionary<int, PowerProfile>();
        foreach (var (p, rows) in power) foreach (int i in rows) profileOf[i] = p;
        var shifts = PowerAnalysis.Upshifts(raceRows, ts, t["gear"], t["current_engine_rpm"], t["accel"], t["power"],
            (i, g, rpm) => profileOf.TryGetValue(i, out var p) ? p.Gain(g, rpm) : null);
        // リミッターに張り付いている行と、変速の前に張り付いていた時間
        var bounce = PowerAnalysis.LimiterBounce(raceRows, t.RowCount, ts, t["gear"], t["current_engine_rpm"], t["accel"], t["power"],
            i => profileOf.TryGetValue(i, out var p) ? p.LimiterRpm : null);
        shifts = shifts.Select(s => s with { LimiterMs = PowerAnalysis.TimeAtLimiterMs(raceRows, s.Index, ts, t["gear"], bounce) }).ToList();

        var starts = StartDetector.Detect(ts, on, t["speed"], t["race_pos"], t["position_x"], t["position_z"], t["dist_traveled"]);
        // リプレイは先頭の行(レースタイム 0)がスタート。前に停止区間が無いので、止まっていた時間が短いと見つからない
        // (リプレイを書き出したパッケージも同じ。テレメトリーは最終の時間軸だけ)
        if ((isReplay || package?.Capture == PackageWriter.CaptureReplay) && !starts.Any(x => x.Kind == StartKind.Race && x.Index < 60))
            starts.Insert(0, new StartEvent(0, StartKind.Race));
        return new LoadedSession
        {
            Folder = folder, Meta = meta, Table = t, VideoPath = video, Package = package, Fps = fps,
            IsReplay = isReplay, Timeline = timeline, VirtualStart = virtualStart,
            Starts = starts,
            Races = races(t),
            Laps = LapDetector.Detect(ts, on, t["race_pos"], t["lap_no"], t["cur_lap"], t["dist_traveled"],
                                      t["position_x"], t["position_z"], t["yaw"], starts),
            Contacts = ContactDetector.Detect(ts, on, t["speed"], t["acceleration_x"], t["acceleration_z"], extended),
            Jumps = AirDetector.Detect(ts, extended, t["speed"], t["position_y"]),
            Extended = extended,
            Excursions = SurfaceExcursionDetector.Detect(ts, on, t["speed"], extended,
                Wheels.Select(w => t["surface_rumble_" + w]).ToArray(), Wheels.Select(w => t["wheel_on_rumble_strip_" + w]).ToArray(), slip),
            Power = power, Shifts = shifts, LimiterBounce = bounce,
            Grade = DerivedSeries.Grade(segment, distance, t["position_y"]),
            Turn = DerivedSeries.TurnCurvature(on, t["angular_velocity_y"], t["speed"]),
        };
    }

    /// <summary>
    /// 配布パッケージのレース: package.json の範囲とコース名・車名を使う
    /// (切り出したテレメトリーで判定をやり直すと、頭が切れていてレースの開始を見落とすことがあるため)
    /// </summary>
    private static List<(RaceSegment, string?, string?, string?)> PackageRaces(TelemetryTable t, PackageInfo p)
    {
        const double Eps = 1e-4;
        var recv = t[TelemetryTable.RecvTime];
        int start = Array.FindIndex(recv, r => r >= p.Race.StartRecvTime - Eps);
        int end = Array.FindLastIndex(recv, r => r <= p.Race.EndRecvTime + Eps);
        if (start < 0 || end <= start) return new();
        var seg = new RaceSegment
        {
            StartIndex = start, EndIndex = end, FinishIndex = end, StartRecvTime = recv[start], EndRecvTime = recv[end],
            Restart = p.Race.Restart, EndReason = p.Race.EndReason, EndSpeedKmh = Math.Round(t["speed"][end] * 3.6, 1),
            LikelyFinished = p.Race.Finished, FinishedBy = p.Race.FinishedBy, StartX = p.Course.StartX, StartZ = p.Course.StartZ, StartYaw = p.Course.StartYaw,
        };
        return new() { (seg, p.Course.Name, p.Car.Name, p.Car.Setup) };
    }

    /// <summary>
    /// グラフ用の系列。横軸 t は動画の秒(変換は VideoTimeMap だけで行い、JS では計算しない)。
    /// is_race_on == 0 の行と gear == 11(変速動作中)は null にして線を切る。
    /// 路面は車輪ごとの数値: 0〜5 = SurfaceKind、6 = 空中。+8 = 縁石、+16 = 水たまり。停止区間は null
    /// </summary>
    public object BuildSeries(VideoTimeMap map)
    {
        var T = Table;
        var recv = T[TelemetryTable.RecvTime];
        var on = T["is_race_on"];

        // recv_time の順が崩れた行があっても、横軸は昇順にする(変換は単調増加なので、recv_time の順 = 動画の秒の順)
        var idx = SeriesRows();
        var t = idx.Select(i => Math.Round(map.ToVideoSec(recv[i]), 4)).ToArray();

        double?[] Col(Func<int, double> value, double scale, int digits) => idx.Select(i =>
        {
            double v = value(i);
            return on[i] == 0 || !double.IsFinite(v) ? (double?)null : Math.Round(v * scale, digits);
        }).ToArray();
        double[] C(string name) => T[name];
        double Avg(string a, string b, int i) => (C(a)[i] + C(b)[i]) / 2;

        int?[] Surface(string w) => idx.Select(i =>
        {
            if (on[i] == 0) return (int?)null;
            int code = Extended[i] ? 6 : (int)DerivedSeries.ClassifySurface(C("surface_rumble_" + w)[i]);
            if (C("wheel_on_rumble_strip_" + w)[i] != 0) code += 8;
            if (C("wheel_in_puddle_" + w)[i] != 0) code += 16;
            return code;
        }).ToArray();

        return new
        {
            t,
            speed = Col(i => C("speed")[i], 3.6, 1),
            accel = Col(i => C("accel")[i], 100.0 / 255, 1),
            brake = Col(i => C("brake")[i], 100.0 / 255, 1),
            steer = Col(i => C("steer")[i], 100.0 / 127, 1),
            gear = Col(i => C("gear")[i] == 11 ? double.NaN : C("gear")[i], 1, 0),
            slipF = Col(i => Avg("tire_combined_slip_fl", "tire_combined_slip_fr", i), 1, 2),
            slipR = Col(i => Avg("tire_combined_slip_rl", "tire_combined_slip_rr", i), 1, 2),
            grade = Col(i => Grade[i], 1, 1),
            elev = Col(i => C("position_y")[i], 1, 1),
            // コース図用(x = 東、z = 北。m)
            x = Col(i => C("position_x")[i], 1, 1),
            z = Col(i => C("position_z")[i], 1, 1),
            turn = Col(i => Turn[i], 1, 2),
            // 車体座標の加速度を G にしたもの(摩擦円と、グラフの「前後G」「横G」)。FH6 のデータ出力は x = 右、z = 前(Forza 共通の約束)。
            // 前後は + = 加速、横は + = 右。軸の向きは実機のデータで確かめること(docs/telemetry-field-notes.md「加速度の軸」)
            gLong = Col(i => C("acceleration_z")[i], 1 / 9.80665, 2),
            gLat = Col(i => C("acceleration_x")[i], 1 / 9.80665, 2),
            // 性能モード(docs/spec.md「性能モード」): 回転数、今のギアの最適な変速回転数、今すぐ変速した場合の得失(全開の行だけ。%)、
            // 車の番号(perf.cars の位置。性能を出せない行は null)
            rpm = Col(i => C("current_engine_rpm")[i], 1, 0),
            // 最適な範囲(得失が ±3% 以内)の下と上。出力が平らな車では広く、リミッターまで引っ張る方が得なギアではリミッターの手前
            optRpm = Col(i => RangeAt(i) is { } x ? x.Lo : double.NaN, 1, 0),
            optRpmHi = Col(i => RangeAt(i) is { } x ? x.Hi : double.NaN, 1, 0),
            // リミッターに張り付いている間は、回転数がリミッターの下まで上下しても「早い」にしない(負を 0 に)
            gain = Col(i => C("accel")[i] >= PowerAnalysis.FullThrottle && C("gear")[i] != 11 && ProfileAt(i)?.Gain((int)C("gear")[i], C("current_engine_rpm")[i]) is double g
                ? (LimiterBounce[i] && g < 0 ? 0 : g) : double.NaN, 1, 1),
            perfCar = idx.Select(i => on[i] == 1 && _carOfRow.TryGetValue(i, out var k) ? k : (int?)null).ToArray(),
            perf = new
            {
                cars = Power.Select(x => new
                {
                    ordinal = x.Profile.Ordinal, pi = x.Profile.Pi,
                    curve = x.Profile.Curve == null ? null : x.Profile.Curve.Rpm.Select((r, k) => new[] { r, x.Profile.Curve.Hp[k] }).ToArray(),
                    maxHp = x.Profile.Curve?.MaxHp,
                    limiter = x.Profile.LimiterRpm,
                    // この記録で使ったギア(全開でなくても)。次のギアの比が無いとき、「上のギアを使っていない」か「行が足りない」かを分ける
                    gears = x.Rows.Select(i => C("gear")[i]).Where(g => g >= 1 && g <= 10).Distinct().OrderBy(g => g).Select(g => (int)g).ToArray(),
                    range = (x.Profile.OptimalRange ?? new Dictionary<int, (double Lo, double Hi)>()).ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.Lo, kv.Value.Hi }),
                    ratios = x.Profile.Ratios.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    opt = x.Profile.OptimalRpm.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                }).ToArray(),
            },
            surface = new { fl = Surface("fl"), fr = Surface("fr"), rl = Surface("rl"), rr = Surface("rr") },
        };
    }

    private Dictionary<int, int>? _carOfRowCache;
    /// <summary>行番号 → Power の位置</summary>
    private Dictionary<int, int> _carOfRow => _carOfRowCache ??= Power.SelectMany((x, k) => x.Rows.Select(i => (i, k))).ToDictionary(x => x.i, x => x.k);
    /// <summary>レースの全開のシフトアップが、自動変速らしいか(PowerAnalysis.LooksAutomatic)</summary>
    private (bool Auto, double Lo, double Hi)? AutoOf(RaceSegment seg) =>
        PowerAnalysis.LooksAutomatic(Shifts.Where(s => s.Index >= seg.StartIndex && s.Index <= seg.EndIndex).ToList());

    /// <summary>行 i の、今のギアの最適な範囲(無ければ null)</summary>
    private (double Lo, double Hi)? RangeAt(int i)
    {
        double g = Table["gear"][i];
        return g != 11 && ProfileAt(i)?.OptimalRange is { } r && r.TryGetValue((int)g, out var x) ? x : null;
    }

    private PowerProfile? ProfileAt(int i) => _carOfRow.TryGetValue(i, out var k) ? Power[k].Profile : null;

    /// <summary>スタート・接触・着地・ジャンプ。時刻 t は動画の秒</summary>
    public object BuildMarks(VideoTimeMap map)
    {
        var recv = Table[TelemetryTable.RecvTime];
        double T(int i) => Math.Round(map.ToVideoSec(recv[i]), 3);
        var pos = SeriesPositions();
        return new
        {
            starts = Starts.Select(s => new { t = T(s.Index), race = s.IsRace, kind = s.Kind.ToString().ToLowerInvariant() }).ToArray(),
            contacts = Contacts.Select(c => new
            {
                t = T(c.Index), landing = c.Kind == ContactKind.Landing, g = Math.Round(c.PeakG, 1),
                before = c.SpeedBeforeKmh, after = c.SpeedAfterKmh,
            }).ToArray(),
            // 画面で強調するのは、スリップか停止区間(リワインドなど)を伴うものだけ
            excursions = Excursions.Where(e => e.Consequential).Select(e => new
            {
                t0 = T(e.StartIndex), t1 = T(e.EndIndex),
                wheels = e.Wheels.Select(w => Wheels[w]).ToArray(),
                surfaces = e.Surfaces.Select(k => (int)k).ToArray(),
                slipT = e.SlipIndex >= 0 ? T(e.SlipIndex) : (double?)null,
                slipEndT = e.SlipEndIndex >= 0 ? T(e.SlipEndIndex) : (double?)null,
                slipPeak = e.SlipPeak,
                gap = e.EndsInGap,
            }).ToArray(),
            races = Races.Select(r => new
            {
                t0 = T(r.Segment.StartIndex), t1 = T(r.Segment.EndIndex), tf = T(r.Segment.FinishIndex), restart = r.Segment.Restart,
                finished = r.Segment.LikelyFinished, finishedBy = r.Segment.FinishedBy, endKmh = r.Segment.EndSpeedKmh, course = r.Course, car = r.Car, setup = r.Setup,
                dist = RaceDistanceSeries(r.Segment, pos),
                // 自動変速(か AutoDrive)らしいか: true / false / null(分からない)と、変速回転数の 10〜90 パーセンタイル
                auto = AutoOf(r.Segment) is { } a ? a.Auto : (bool?)null,
                autoLo = AutoOf(r.Segment)?.Lo, autoHi = AutoOf(r.Segment)?.Hi,
            }).ToArray(),
            // 再生時に飛ばせる区間。kind = "redo": やり直し区間(リワインド・チェックポイント逃しで捨てた走りの始まりから、戻った先で走り直すまで)、
            // "pause": 一時停止(フォトモード・ポーズメニュー。経過時間が止まった停止区間)
            // (計算は Fh6.Core の RaceSkips。配布パッケージの package.json と同じ)
            skips = (IsReplay ? new List<SkipRange>() : RaceSkips.Find(Table, Races.Select(r => r.Segment))).Select(g => new
            {
                t0 = Math.Round(map.ToVideoSec(g.FromRecvTime), 3), t1 = Math.Round(map.ToVideoSec(g.ToRecvTime), 3), kind = g.Kind,
            }).ToArray(),
            laps = Laps.Select(l => new { t = T(l.Index), lap = l.Lap, prev = l.PrevLapSec, finish = l.IsFinish, byPosition = !l.FromLapCounter }).ToArray(),
            // 全開のシフトアップ。verdict: early / ok / late(得失を出せなければ null)
            shifts = Shifts.Select(s => new
            {
                t = T(s.Index), from = s.FromGear, rpm = s.Rpm, gain = s.GainPct, verdict = s.Verdict?.ToString().ToLowerInvariant(),
                limiterSec = Math.Round(s.LimiterMs / 1000, 1),
                auto = Races.Any(r => s.Index >= r.Segment.StartIndex && s.Index <= r.Segment.EndIndex && AutoOf(r.Segment) is { Auto: true }),
            }).ToArray(),
            jumps = Jumps.Select(j => new
            {
                t0 = T(j.StartIndex), t1 = T(j.EndIndex), sec = j.DurationSec, kmh = j.TakeoffKmh, drop = j.DropM,
            }).ToArray(),
        };
    }

    /// <summary>
    /// 距離軸: レースの行の、系列の中の位置 i、スタートからの距離 d(m)、最後まで残った走りか k(1/0)。
    /// 停止区間の行は含めない。系列の位置は BuildSeries の t の並び(recv_time の順)
    /// </summary>
    private object RaceDistanceSeries(RaceSegment seg, int[] pos)
    {
        var (dist, kept) = RaceDistance.Compute(Table["is_race_on"], Table["position_x"], Table["position_z"], seg.StartIndex, seg.EndIndex);
        var i = new List<int>();
        var d = new List<double>();
        var k = new List<int>();
        for (int j = 0; j < dist.Length; j++)
        {
            int p = pos[seg.StartIndex + j];
            if (!double.IsFinite(dist[j]) || p < 0) continue;
            i.Add(p);
            d.Add(Math.Round(dist[j], 1));
            k.Add(kept[j] ? 1 : 0);
        }
        return new { i, d, k };
    }

    /// <summary>系列に入れる行の番号(recv_time のある行を recv_time の順に。同じ時刻なら行の順)</summary>
    private int[] SeriesRows()
    {
        var recv = Table[TelemetryTable.RecvTime];
        return Enumerable.Range(0, Table.RowCount).Where(i => double.IsFinite(recv[i])).OrderBy(i => recv[i]).ToArray();
    }

    /// <summary>行番号 → 系列(BuildSeries の t)の中の位置。recv_time が無い行は -1</summary>
    private int[] SeriesPositions()
    {
        var idx = SeriesRows();
        var pos = new int[Table.RowCount];
        Array.Fill(pos, -1);
        for (int p = 0; p < idx.Length; p++) pos[idx[p]] = p;
        return pos;
    }

}
