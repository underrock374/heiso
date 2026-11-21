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
        .Concat(Wheels.Select(w => "tire_slip_angle_" + w))   // オーバー / アンダー
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

    /// <summary>記録アプリの保存先(このセッションフォルダの 1 つ上。アーカイブの中なら、その上。登録ファイルと受け渡し用のファイルがある)</summary>
    public string SaveFolder => SessionCatalog.SaveRootOf(Folder);

    /// <summary>ジャンプ(空中の区間)</summary>
    public required List<AirEvent> Jumps { get; init; }

    /// <summary>行ごとに 4 輪とも伸び切っているか(空中)</summary>
    public required bool[] Extended { get; init; }

    /// <summary>路面の逸脱(舗装から外れた瞬間)と、それに続くスリップ</summary>
    public required List<SurfaceExcursion> Excursions { get; init; }

    /// <summary>車ごとの性能(パワーカーブ・ギア比・最適な変速回転数)と、その車の行</summary>
    public List<(PowerProfile Profile, List<int> Rows)> Power { get; private set; } = new();

    /// <summary>Power の車ごとに、パワーカーブを何本の記録の車のプロファイルから作ったか(ApplyProfiles の後。この記録だけなら null)</summary>
    public Dictionary<(int Ordinal, int Pi), PowerSource> PowerSources { get; } = new();

    /// <summary>車のプロファイルの元: 記録の数、そのうちのレースの数、レースの外の走りがあるか</summary>
    public sealed record PowerSource(int Recordings, int Races, bool Other);

    /// <summary>全開のシフトアップ(性能モードの変速の点)</summary>
    public List<ShiftEvent> Shifts { get; private set; } = new();

    /// <summary>行ごとに、リミッターに張り付いているか(変速の得失を「早い」にしない)</summary>
    public bool[] LimiterBounce { get; private set; } = Array.Empty<bool>();

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

    /// <summary>全開のシフトアップと、リミッターに張り付いている行(Power が変わったら作り直す)</summary>
    private static (List<ShiftEvent> Shifts, bool[] Bounce) ShiftsOf(TelemetryTable t, List<int> raceRows, double[] ts, List<(PowerProfile Profile, List<int> Rows)> power)
    {
        var profileOf = new Dictionary<int, PowerProfile>();
        foreach (var (p, rows) in power) foreach (int i in rows) profileOf[i] = p;
        var shifts = PowerAnalysis.Upshifts(raceRows, ts, t["gear"], t["current_engine_rpm"], t["accel"], t["power"],
            (i, g, rpm) => profileOf.TryGetValue(i, out var p) ? p.Gain(g, rpm) : null);
        // リミッターに張り付いている行と、変速の前に張り付いていた時間
        var bounce = PowerAnalysis.LimiterBounce(raceRows, t.RowCount, ts, t["gear"], t["current_engine_rpm"], t["accel"], t["power"],
            i => profileOf.TryGetValue(i, out var p) ? p.LimiterRpm : null);
        shifts = shifts.Select(s => s with { LimiterMs = PowerAnalysis.TimeAtLimiterMs(raceRows, s.Index, ts, t["gear"], bounce) }).ToList();
        return (shifts, bounce);
    }

    /// <summary>この記録の車のプロファイル(UpdateProfiles で読み書きしたもの。リプレイの仮想セッションに渡すため)</summary>
    public IReadOnlyDictionary<CarSignature, CarProfileFile> ProfileFiles => _profiles;

    /// <summary>
    /// 性能モードのパワーカーブ・ギア比・リミッターを、車のプロファイル(同じ車の記録を全部合わせたもの)から作り直す。
    /// プロファイルにカーブが無い車は、この記録だけのまま。変速の点と、リミッターに張り付いている行も作り直す
    /// </summary>
    public void ApplyProfiles(IReadOnlyDictionary<CarSignature, CarProfileFile> files)
    {
        bool changed = false;
        for (int k = 0; k < Power.Count; k++)
        {
            var (old, rows) = Power[k];
            var file = files.FirstOrDefault(f => f.Key.Ordinal == old.Ordinal && f.Key.Pi == old.Pi).Value;
            if (file == null) continue;
            var cp = new CarProfile(file);
            var curve = cp.Curve();
            if (curve == null) continue;
            var ratios = cp.Ratios();
            var limiter = cp.LimiterRpm();
            var opt = ratios.ToDictionary(r => r.Key, r => PowerAnalysis.OptimalShiftRpm(curve, r.Value));
            var range = new Dictionary<int, (double, double)>();
            foreach (var (g, r) in ratios)
                if (PowerAnalysis.OptimalRange(curve, r, limiter) is { } x) range[g] = x;
            Power[k] = (new PowerProfile(old.Ordinal, old.Pi, curve, ratios, opt, limiter, range), rows);
            PowerSources[(old.Ordinal, old.Pi)] = new PowerSource(
                file.Parts.Select(p => p.Folder).Distinct().Count(),
                file.Parts.Where(p => p.RaceStartRecvTime != null).Select(p => (p.Folder, p.RaceStartRecvTime)).Distinct().Count(),
                file.Parts.Any(p => p.RaceStartRecvTime == null));
            changed = true;
        }
        if (!changed) return;
        var ts = Table[TelemetryTable.RecvTime].Select(r => r * 1000).ToArray();
        var on = Table["is_race_on"];
        var raceRows = Enumerable.Range(0, Table.RowCount).Where(i => on[i] == 1 && double.IsFinite(ts[i])).OrderBy(i => ts[i]).ToList();
        (Shifts, LimiterBounce) = ShiftsOf(Table, raceRows, ts, Power);
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
        var (shifts, bounce) = ShiftsOf(t, raceRows, ts, power);

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
    public SessionSeries BuildSeries(VideoTimeMap map)
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

        double?[] x = Col(i => C("position_x")[i], 1, 1), z = Col(i => C("position_z")[i], 1, 1);
        var surface = new SurfaceSeries(Fl: Surface("fl"), Fr: Surface("fr"), Rl: Surface("rl"), Rr: Surface("rr"));
        var series = new SessionSeries(
            T: t,
            Speed: Col(i => C("speed")[i], 3.6, 1),
            Accel: Col(i => C("accel")[i], 100.0 / 255, 1),
            Brake: Col(i => C("brake")[i], 100.0 / 255, 1),
            Steer: Col(i => C("steer")[i], 100.0 / 127, 1),
            Gear: Col(i => C("gear")[i] == 11 ? double.NaN : C("gear")[i], 1, 0),
            SlipF: Col(i => Avg("tire_combined_slip_fl", "tire_combined_slip_fr", i), 1, 2),
            SlipR: Col(i => Avg("tire_combined_slip_rl", "tire_combined_slip_rr", i), 1, 2),
            // オーバー / アンダー(後輪 − 前輪の滑り角。正 = オーバー)とカウンター(1 = ハンドルと車の回る向きが逆)。遅いときは出さない
            Balance: Col(i => C("speed")[i] > DerivedSeries.BalanceMinSpeed
                ? DerivedSeries.SlipBalance(C("tire_slip_angle_fl")[i], C("tire_slip_angle_fr")[i], C("tire_slip_angle_rl")[i], C("tire_slip_angle_rr")[i])
                : double.NaN, 1, 2),
            Counter: idx.Select(i => on[i] != 0 && DerivedSeries.IsCounterSteer(C("steer")[i], C("angular_velocity_y")[i], C("speed")[i]) ? 1 : (int?)null).ToArray(),
            Grade: Col(i => Grade[i], 1, 1),
            Elev: ElevSeries(idx),
            // コース図用(x = 東、z = 北。m)
            X: x,
            Z: z,
            Turn: Col(i => Turn[i], 1, 2),
            // 車体座標の加速度を G にしたもの(摩擦円と、グラフの「前後G」「横G」)。FH6 のデータ出力は x = 右、z = 前(Forza 共通の約束)。
            // 前後は + = 加速、横は + = 右。軸の向きは実機のデータで確かめること(docs/telemetry-field-notes.md「加速度の軸」)
            GLong: Col(i => C("acceleration_z")[i], 1 / 9.80665, 2),
            GLat: Col(i => C("acceleration_x")[i], 1 / 9.80665, 2),
            // サスペンションの縮み(0 = 伸び切り、1 = 縮み切り)。荷重移動の図(前後・左右の縮みの差)に使う
            Susp: new SuspensionSeries(
                Fl: Col(i => C("norm_suspension_travel_fl")[i], 1, 3), Fr: Col(i => C("norm_suspension_travel_fr")[i], 1, 3),
                Rl: Col(i => C("norm_suspension_travel_rl")[i], 1, 3), Rr: Col(i => C("norm_suspension_travel_rr")[i], 1, 3)),
            // 性能モード(docs/spec.md「性能モード」): 回転数、今のギアの最適な変速回転数、今すぐ変速した場合の得失(全開の行だけ。%)、
            // 車の番号(perf.cars の位置。性能を出せない行は null)
            Rpm: Col(i => C("current_engine_rpm")[i], 1, 0),
            // 最適な範囲(得失が ±3% 以内)の下と上。出力が平らな車では広く、リミッターまで引っ張る方が得なギアではリミッターの手前
            OptRpm: Col(i => RangeAt(i) is { } x ? x.Lo : double.NaN, 1, 0),
            OptRpmHi: Col(i => RangeAt(i) is { } x ? x.Hi : double.NaN, 1, 0),
            // リミッターに張り付いている間は、回転数がリミッターの下まで上下しても「早い」にしない(負を 0 に)
            Gain: Col(i => C("accel")[i] >= PowerAnalysis.FullThrottle && C("gear")[i] != 11 && ProfileAt(i)?.Gain((int)C("gear")[i], C("current_engine_rpm")[i]) is double g
                ? (LimiterBounce[i] && g < 0 ? 0 : g) : double.NaN, 1, 1),
            // 全開か(1 / 0。アクセルの無い行は null)。得失の段の灰色の帯、パワーカーブの今の点、読み取り値の「全開でない」に使う
            Full: idx.Select(i => on[i] == 0 || !double.IsFinite(C("accel")[i]) ? (int?)null : C("accel")[i] >= PowerAnalysis.FullThrottle ? 1 : 0).ToArray(),
            PerfCar: idx.Select(i => on[i] == 1 && _carOfRow.TryGetValue(i, out var k) ? k : (int?)null).ToArray(),
            Perf: new PerfData(
                Cars: Power.Select(x => new PerfCarInfo(
                    Ordinal: x.Profile.Ordinal, Pi: x.Profile.Pi,
                    Curve: x.Profile.Curve == null ? null : x.Profile.Curve.Rpm.Select((r, k) => new[] { r, x.Profile.Curve.Hp[k] }).ToArray(),
                    MaxHp: x.Profile.Curve?.MaxHp,
                    // カーブを何本の記録の車のプロファイルから作ったか(この記録だけなら null)
                    Source: PowerSources.TryGetValue((x.Profile.Ordinal, x.Profile.Pi), out var src) ? src : null,
                    Limiter: x.Profile.LimiterRpm,
                    // この記録で使ったギア(全開でなくても)。次のギアの比が無いとき、「上のギアを使っていない」か「行が足りない」かを分ける
                    Gears: x.Rows.Select(i => C("gear")[i]).Where(g => g >= 1 && g <= 10).Distinct().OrderBy(g => g).Select(g => (int)g).ToArray(),
                    Range: (x.Profile.OptimalRange ?? new Dictionary<int, (double Lo, double Hi)>()).ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.Lo, kv.Value.Hi }),
                    Ratios: x.Profile.Ratios.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    Opt: x.Profile.OptimalRpm.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value))).ToArray()),
            Readout: null!,
            Friction: null!,
            Surface: surface,
            // コース図の線(路面の色のもとになる分類と、線を切る行)
            Course: new CourseSeries(Surface: CourseLine.SurfaceKinds(surface), Breaks: CourseLine.BreakRows(x, z)));
        // 性能モードの読み取り値は、出来た系列の値(画面が見るのと同じ丸めた値)から出す
        return series with
        {
            Readout = PerfReadout.Build(series),
            // 摩擦円・荷重移動の、ならしの幅とレースの選び方によらない値
            Friction = new FrictionSeries(Group: FrictionStatic.SurfaceGroups(series.Surface), Wheels: FrictionStatic.WheelBars(series.T, series.Susp)),
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
    public SessionMarks BuildMarks(VideoTimeMap map)
    {
        var recv = Table[TelemetryTable.RecvTime];
        double T(int i) => Math.Round(map.ToVideoSec(recv[i]), 3);
        var pos = SeriesPositions();
        var elev = ElevSeries(SeriesRows());
        var races = Races.Select(r => (Race: r, Dist: BuildRaceDistance(r.Segment, pos))).ToList();
        return new SessionMarks(
            Starts: Starts.Select(s => new StartMark(T: T(s.Index), Race: s.IsRace, Kind: s.Kind.ToString().ToLowerInvariant())).ToArray(),
            Contacts: Contacts.Select(c => new ContactMark(
                T: T(c.Index), Landing: c.Kind == ContactKind.Landing, G: Math.Round(c.PeakG, 1),
                Before: c.SpeedBeforeKmh, After: c.SpeedAfterKmh)).ToArray(),
            // 画面で強調するのは、スリップか停止区間(リワインドなど)を伴うものだけ
            Excursions: Excursions.Where(e => e.Consequential).Select(e =>
            {
                double t0 = T(e.StartIndex), t1 = T(e.EndIndex);
                double? slipT = e.SlipIndex >= 0 ? T(e.SlipIndex) : null, slipEndT = e.SlipEndIndex >= 0 ? T(e.SlipEndIndex) : null;
                return new ExcursionMark(
                    T0: t0, T1: t1,
                    Wheels: e.Wheels.Select(w => Wheels[w]).ToArray(),
                    Surfaces: e.Surfaces.Select(k => (int)k).ToArray(),
                    SlipT: slipT, SlipEndT: slipEndT,
                    // グラフのスリップの段の帯の終わり
                    SlipBandT1: SlipBand.End(t0, t1, slipT, slipEndT),
                    SlipPeak: e.SlipPeak,
                    Gap: e.EndsInGap);
            }).ToArray(),
            Races: Races.Select((r, n) => new RaceMark(
                T0: T(r.Segment.StartIndex), T1: T(r.Segment.EndIndex), Tf: T(r.Segment.FinishIndex), Restart: r.Segment.Restart,
                Finished: r.Segment.LikelyFinished, FinishedBy: r.Segment.FinishedBy, EndKmh: r.Segment.EndSpeedKmh, Course: r.Course, Car: r.Car, Setup: r.Setup,
                Dist: races[n].Dist,
                // 標高の段の上下(このレースの行で)
                ElevRange: DistanceAxis.ElevRange(races[n].Dist.I.Select(p => elev[p])),
                // 自動変速(か AutoDrive)らしいか: true / false / null(分からない)と、変速回転数の 10〜90 パーセンタイル
                Auto: AutoOf(r.Segment) is { } a ? a.Auto : (bool?)null,
                AutoLo: AutoOf(r.Segment)?.Lo, AutoHi: AutoOf(r.Segment)?.Hi)).ToArray(),
            // 再生時に飛ばせる区間。kind = "redo": やり直し区間(リワインド・チェックポイント逃しで捨てた走りの始まりから、戻った先で走り直すまで)、
            // "pause": 一時停止(フォトモード・ポーズメニュー。経過時間が止まった停止区間)
            // (計算は Fh6.Core の RaceSkips。配布パッケージの package.json と同じ)
            Skips: (IsReplay ? new List<SkipRange>() : RaceSkips.Find(Table, Races.Select(r => r.Segment))).Select(g => new SkipMark(
                T0: Math.Round(map.ToVideoSec(g.FromRecvTime), 3), T1: Math.Round(map.ToVideoSec(g.ToRecvTime), 3), Kind: g.Kind)).ToArray(),
            Laps: Laps.Select(l => new LapMark(T: T(l.Index), Lap: l.Lap, Prev: l.PrevLapSec, Finish: l.IsFinish, ByPosition: !l.FromLapCounter)).ToArray(),
            // 全開のシフトアップ。verdict: early / ok / late(得失を出せなければ null)
            Shifts: Shifts.Select(s => new ShiftMark(
                T: T(s.Index), From: s.FromGear, Rpm: s.Rpm, Gain: s.GainPct, Verdict: s.Verdict?.ToString().ToLowerInvariant(),
                LimiterSec: Math.Round(s.LimiterMs / 1000, 1),
                Auto: Races.Any(r => s.Index >= r.Segment.StartIndex && s.Index <= r.Segment.EndIndex && AutoOf(r.Segment) is { Auto: true }))).ToArray(),
            Jumps: Jumps.Select(j => new JumpMark(
                T0: T(j.StartIndex), T1: T(j.EndIndex), Sec: j.DurationSec, Kmh: j.TakeoffKmh, Drop: j.DropM)).ToArray(),
            // 標高の段の上下(レースを選んでいないとき。記録全体の走行中の行で)
            ElevRange: DistanceAxis.ElevRange(elev),
            // やり直した走りの行(系列の中の位置)。コース図で薄く描く
            Cut: DistanceAxis.CutRows(races.Select(x => x.Dist)),
            // 摩擦円・荷重移動の円の推定と点群から除く行(系列の中の位置。接触・ジャンプのまわり)
            GgExcluded: FrictionStatic.ExcludedRows(
                SeriesRows().Select(i => Math.Round(map.ToVideoSec(recv[i]), 4)).ToArray(),
                Contacts.Select(c => T(c.Index)), Jumps.Select(j => (T(j.StartIndex), T(j.EndIndex)))));
    }

    /// <summary>
    /// 距離軸: レースの行の、系列の中の位置 i、スタートからの距離 d(m)、最後まで残った走りか k(1/0)。
    /// 停止区間の行は含めない。系列の位置は BuildSeries の t の並び(recv_time の順)
    /// </summary>
    private RaceDistanceSeries BuildRaceDistance(RaceSegment seg, int[] pos)
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
        return new RaceDistanceSeries(I: i, D: d, K: k, Graph: DistanceAxis.GraphRows(d, k));
    }

    // ---- 計測モード(docs/spec.md「計測モード」): 車のプロファイル ----

    /// <summary>この記録の車ごとのプロファイル(UpdateProfiles で読み書きしたもの)</summary>
    private readonly Dictionary<CarSignature, CarProfileFile> _profiles = new();

    /// <summary>
    /// プロファイルを更新する: 記録の車ごと・レースごと(とレースの外)の部分を作って、プロファイルに足す(同じ部分は置き換える)。
    /// csv が変わっていなければ作り直さない。リプレイの仮想セッションと、テレメトリーの時刻を振り直したパッケージは足さない
    /// (同じ走りの元の記録と二重になるため)。書けなくても表示は続ける(失敗の理由を返す)
    /// </summary>
    public string? UpdateProfiles(ProfileStore store)
    {
        if (IsReplay) return null;
        var csv = Path.Combine(Folder, string.IsNullOrEmpty(Meta.Files.TelemetryCsv) ? SessionWriter.CsvName : Meta.Files.TelemetryCsv);
        var fi = new FileInfo(csv);
        if (!fi.Exists) return null;
        string folder = Package?.SourceSession is { Length: > 0 } src ? src : Path.GetFileName(Path.GetFullPath(Folder).TrimEnd('\\', '/'));
        string mtime = fi.LastWriteTimeUtc.ToString("o");
        bool readOnly = Package?.TelemetryRetimed == true;

        var T = Table;
        var ts = T[TelemetryTable.RecvTime].Select(r => r * 1000).ToArray();
        var on = T["is_race_on"];
        // G の度数に入れない行: 空中、接触の前後 0.3 秒、着地の後 0.5 秒(摩擦円の円の推定と同じ)
        var exclude = (bool[])Extended.Clone();
        void Mark(double fromMs, double toMs)
        {
            int i = Array.FindIndex(ts, x => x >= fromMs);
            for (; i >= 0 && i < ts.Length && ts[i] <= toMs; i++) exclude[i] = true;
        }
        foreach (var c in Contacts) Mark(ts[c.Index] - 300, ts[c.Index] + 300);
        foreach (var j in Jumps) Mark(ts[j.StartIndex], ts[j.EndIndex] + 500);

        var groups = new Dictionary<(CarSignature Car, RaceSegment? Race), List<int>>();
        foreach (int i in Enumerable.Range(0, T.RowCount).Where(i => on[i] == 1 && double.IsFinite(ts[i])).OrderBy(i => ts[i]))
        {
            var car = RaceSample.FromTable(T, i).Car;
            if (car.Ordinal <= 0) continue;
            var race = Races.Select(r => r.Segment).FirstOrDefault(s => i >= s.StartIndex && i <= s.EndIndex);
            var key = (car, race);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(i);
        }
        string? error = null;
        foreach (var carGroup in groups.GroupBy(g => g.Key.Car))
        {
            var car = carGroup.Key;
            var file = store.Load(car);
            bool changed = false;
            foreach (var ((_, race), rows) in carGroup)
            {
                double? start = race?.StartRecvTime;
                if (readOnly) continue;
                var assists = race == null ? null : RaceNames.Assists(Meta.Events, race.StartRecvTime, Meta.Session.Assists);
                if (ProfileStore.IsFresh(file, folder, start, fi.Length, mtime))
                {
                    // csv は同じ。補助の設定だけ付け直した(名前を付ける)なら、部分の設定を直す
                    var old = file.Parts.First(p => p.Folder == folder && p.RaceStartRecvTime == start);
                    if (AssistKey.Of(old.Assists) != AssistKey.Of(assists) || old.Assists?.Shift != assists?.Shift) { old.Assists = assists; changed = true; }
                    continue;
                }
                var part = ProfilePartBuilder.Build(T, rows, ts, exclude);
                part.Folder = folder;
                part.RaceStartRecvTime = start;
                part.Course = race == null ? null : Races.First(r => r.Segment == race).Course;
                part.CsvSize = fi.Length;
                part.CsvMtime = mtime;
                part.CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
                part.Assists = assists;
                part.AutoShift = race != null && AutoOf(race) is { Auto: true };
                ProfileStore.Upsert(file, part);
                changed = true;
            }
            var name = Races.Where(r => RaceCar(r.Segment) == car).Select(r => r.Car).LastOrDefault(n => n != null);
            if (name != null && file.Car.Name != name) { file.Car.Name = name; changed = true; }
            if (changed)
            {
                try { store.Save(file); }
                catch (Exception ex) { error = ex.Message; }
            }
            _profiles[car] = file;
        }
        return error;
    }

    /// <summary>レースの補助の設定と、その組(限界を分けるため)</summary>
    private Assists? AssistsOf(RaceSegment seg) => RaceNames.Assists(Meta.Events, seg.StartRecvTime, Meta.Session.Assists);

    /// <summary>
    /// 計測モードの画面に渡す値: この記録の車ごとに、プロファイルの値・回転数の帯の判定・補助の設定の組ごとの限界と次の 1 本の指示。
    /// races は記録のレースごとの車と補助の設定(画面で、選んでいるレースの車と組を出すため)
    /// </summary>
    public MeasureData BuildMeasure()
    {
        MeasureLimit Lim(CarProfile.Limit l) => new(G: l.G, Rows: l.Rows, Measured: l.Measured);
        var cars = _profiles.Select(kv =>
        {
            var (car, file) = (kv.Key, kv.Value);
            var all = new CarProfile(file);
            var eval = MeasurementEvaluator.Evaluate(all);
            var curve = all.Curve();
            // 組はレースの補助の設定の全部(ABS・TC・STM・LC)で作り、限界はそれぞれに効く補助だけで部分を選ぶ(CarProfile.ForAssists)
            var reps = file.Parts.Select(p => p.Assists).Append(null).GroupBy(a => AssistKey.Of(a)).ToDictionary(g => g.Key, g => g.First());
            var groups = reps.Keys.ToDictionary(k => k, k =>
            {
                var p = CarProfile.ForAssists(file, reps[k]);
                var e = MeasurementEvaluator.Evaluate(p);
                var surfaces = new[] { "paved", "dirt", "grass", "snow" }.Where(s => s == "paved" || p.HasSurface(s));
                return new MeasureGroup(
                    Parts: file.Parts.Count(x => AssistKey.Of(x.Assists) == k),
                    Limits: surfaces.ToDictionary(s => s, s => new MeasureLimits(
                        Brake: Lim(p.BrakeAll(s)),
                        BrakeBySpeed: p.Brake(s).ToDictionary(b => b.Key.ToString(), b => Lim(b.Value)),
                        Left: Lim(p.Corner(s, "left")),
                        Right: Lim(p.Corner(s, "right")),
                        Launch: Lim(p.Launch(s)))),
                    Advice: e.Advice.Select(a => new MeasureAdvice(Kind: a.Kind.ToString(), From: a.From, To: a.To, Gear: a.Gear, Kmh: a.Kmh, Surface: a.Surface, Side: a.Side)).ToArray());
            });
            var top = all.Top();
            return new MeasureCar(
                Key: ProfileCar.KeyOf(car), Name: file.Car.Name, Pi: car.Pi, Drivetrain: car.Drivetrain, Cylinders: car.Cylinders,
                Parts: file.Parts.Count, Recordings: file.Parts.Select(p => p.Folder).Distinct().Count(),
                Seconds: Math.Round(file.Parts.Sum(p => p.Seconds)),
                Curve: curve?.Rpm.Select((r, i) => new[] { r, curve.Hp[i] }).ToArray(),
                MaxHp: curve?.MaxHp, PeakRpm: curve?.PeakRpm,
                Limiter: eval.Limiter,
                Ratios: all.Ratios().ToDictionary(r => r.Key.ToString(), r => r.Value),
                Gears: all.GearsSeen().ToArray(),
                Top: top == null ? null : new MeasureTop(Gear: top.Gear, Kmh: top.Kmh, Rpm: top.Rpm),
                Coverage: all.Coverage().ToDictionary(g => g.Key.ToString(), g => g.Value.ToDictionary(b => b.Key.ToString(), b => b.Value)),
                UsedFrom: eval.UsedFrom,
                Bands: eval.Bands.Select(b => new MeasureBand(From: b.From, To: b.To, Rows: b.Rows, State: b.State.ToString().ToLowerInvariant())).ToArray(),
                Groups: groups);
        }).ToArray();
        var races = Races.Select(r =>
        {
            var a = AssistsOf(r.Segment);
            var car = RaceCar(r.Segment);
            return new MeasureRace(Car: car is CarSignature c ? ProfileCar.KeyOf(c) : null, Assists: a, AssistKey: AssistKey.Of(a));
        }).ToArray();
        return new MeasureData(Cars: cars, Races: races);
    }

    /// <summary>系列に入れる行の番号(recv_time のある行を recv_time の順に。同じ時刻なら行の順)</summary>
    private int[] SeriesRows()
    {
        var recv = Table[TelemetryTable.RecvTime];
        return Enumerable.Range(0, Table.RowCount).Where(i => double.IsFinite(recv[i])).OrderBy(i => recv[i]).ToArray();
    }

    /// <summary>系列の標高(m、小数 1 桁)。走行中でない行は null</summary>
    private double?[] ElevSeries(int[] idx)
    {
        var on = Table["is_race_on"];
        var y = Table["position_y"];
        return idx.Select(i => on[i] == 0 || !double.IsFinite(y[i]) ? (double?)null : Math.Round(y[i], 1)).ToArray();
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
