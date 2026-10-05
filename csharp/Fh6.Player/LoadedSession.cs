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
            "lap_no", "cur_lap", "dist_traveled", "yaw",
        }
        .Concat(Wheels.Select(w => "norm_suspension_travel_" + w))
        .Concat(Wheels.Select(w => "tire_combined_slip_" + w))
        .Concat(Wheels.Select(w => "surface_rumble_" + w))
        .Concat(Wheels.Select(w => "wheel_on_rumble_strip_" + w))
        .Concat(Wheels.Select(w => "wheel_in_puddle_" + w))
        .Union(RaceSample.Columns)
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
        if (Package != null) return;
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

    public required double[] Grade { get; init; }
    public required double[] Turn { get; init; }

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

        // 判定の時刻は受信時刻(ms)。timestamp_ms は 60fps 近くでは約 32ms 刻みで、同じ値が続くため
        var ts = t[TelemetryTable.RecvTime].Select(r => r * 1000).ToArray();
        var on = t["is_race_on"];
        var extended = AirDetector.AllWheelsExtended(on, t["norm_suspension_travel_fl"], t["norm_suspension_travel_fr"],
                                                     t["norm_suspension_travel_rl"], t["norm_suspension_travel_rr"]);
        var (segment, distance) = TrackDistance.Compute(on, t["position_x"], t["position_z"]);
        var slip = Enumerable.Range(0, t.RowCount).Select(i => Math.Max(
            (t["tire_combined_slip_fl"][i] + t["tire_combined_slip_fr"][i]) / 2,
            (t["tire_combined_slip_rl"][i] + t["tire_combined_slip_rr"][i]) / 2)).ToArray();

        var video = meta.FindVideo(folder);
        var starts = StartDetector.Detect(ts, on, t["speed"], t["race_pos"], t["position_x"], t["position_z"], t["dist_traveled"]);
        return new LoadedSession
        {
            Folder = folder, Meta = meta, Table = t, VideoPath = video, Package = package,
            Fps = meta.Video.FileFps ?? (video != null ? VideoDuration.TryReadFrameRate(video) : null),
            Starts = starts,
            Races = package != null ? PackageRaces(t, package) : RaceSegmentDetector.Detect(t).Select(r =>
            {
                var (car, setup) = RaceNames.Car(meta.Events, r.StartRecvTime);
                return (r, RaceNames.Course(meta.Events, r.StartRecvTime), car, setup);
            }).ToList(),
            Laps = LapDetector.Detect(ts, on, t["race_pos"], t["lap_no"], t["cur_lap"], t["dist_traveled"],
                                      t["position_x"], t["position_z"], t["yaw"], starts),
            Contacts = ContactDetector.Detect(ts, on, t["speed"], t["acceleration_x"], t["acceleration_z"], extended),
            Jumps = AirDetector.Detect(ts, extended, t["speed"], t["position_y"]),
            Extended = extended,
            Excursions = SurfaceExcursionDetector.Detect(ts, on, t["speed"], extended,
                Wheels.Select(w => t["surface_rumble_" + w]).ToArray(), Wheels.Select(w => t["wheel_on_rumble_strip_" + w]).ToArray(), slip),
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
            surface = new { fl = Surface("fl"), fr = Surface("fr"), rl = Surface("rl"), rr = Surface("rr") },
        };
    }

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
                t0 = T(r.Segment.StartIndex), t1 = T(r.Segment.EndIndex), restart = r.Segment.Restart,
                finished = r.Segment.LikelyFinished, finishedBy = r.Segment.FinishedBy, endKmh = r.Segment.EndSpeedKmh, course = r.Course, car = r.Car, setup = r.Setup,
                dist = RaceDistanceSeries(r.Segment, pos),
            }).ToArray(),
            // 再生時に飛ばせる区間。kind = "redo": やり直し区間(リワインド・チェックポイント逃しで捨てた走りの始まりから、戻った先で走り直すまで)、
            // "pause": 一時停止(フォトモード・ポーズメニュー。経過時間が止まった停止区間)
            // (計算は Fh6.Core の RaceSkips。配布パッケージの package.json と同じ)
            skips = RaceSkips.Find(Table, Races.Select(r => r.Segment)).Select(g => new
            {
                t0 = Math.Round(map.ToVideoSec(g.FromRecvTime), 3), t1 = Math.Round(map.ToVideoSec(g.ToRecvTime), 3), kind = g.Kind,
            }).ToArray(),
            laps = Laps.Select(l => new { t = T(l.Index), lap = l.Lap, prev = l.PrevLapSec, finish = l.IsFinish, byPosition = !l.FromLapCounter }).ToArray(),
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
