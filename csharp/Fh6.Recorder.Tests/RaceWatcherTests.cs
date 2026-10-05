using System.Text.Json.Nodes;
using Fh6.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fh6.Recorder.Tests;

/// <summary>車両・レース・コースの判定と確認カード、session.json への書き込み</summary>
public sealed class RaceWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-race-test-").FullName;
    private string RegistryPath => Path.Combine(_dir, Registry.FileName);
    private double _t = 1_790_000_000;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private RaceWatcher NewWatcher() => new(RegistryPath, NullLogger<RaceWatcher>.Instance);

    /// <summary>パケットを count 個送る</summary>
    private void Send(RaceWatcher w, int count, bool on = true, int pos = 0, double raceTime = 0, int ordinal = 2163, int pi = 585,
                      double x = 100, double z = 200, double yaw = 0.6, double kmh = 0)
    {
        for (int i = 0; i < count; i++)
        {
            _t += 1 / 60.0;
            var v = new double[Fh6Packet.Fields.Length];
            v[Fh6Packet.TimestampMs] = Math.Floor(_t * 1000 % 4e9);
            if (on)
            {
                v[Fh6Packet.IsRaceOn] = 1;
                v[Fh6Packet.RacePos] = pos;
                v[Fh6Packet.CurRaceTime] = raceTime + i / 60.0;
                v[Fh6Packet.Speed] = kmh / 3.6;
                v[Fh6Packet.PositionX] = x;
                v[Fh6Packet.PositionZ] = z;
                v[Fh6Packet.Yaw] = yaw;
                v[Fh6Packet.CarOrdinal] = ordinal;
                v[Fh6Packet.CarClass] = 2;
                v[Fh6Packet.CarPi] = pi;
                v[Fh6Packet.Drivetrain] = 0;
                v[Fh6Packet.NumCylinders] = 4;
            }
            w.OnPacket(_t, v);
        }
    }

    private static JsonNode Status(RaceWatcher w) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(w.Snapshot()))!;

    [Fact]
    public void 車両を登録すると次から名前で出て_コースを登録すると次から見分ける()
    {
        var w = NewWatcher();
        var events = new List<SessionEvent>();
        w.AttachSession(events.Add);

        // 記録開始時の車両 → カードが出る
        Send(w, 30);
        var ev = Assert.Single(events);
        Assert.Equal("car_detected", ev.Type);
        Assert.Equal(new[] { "記録開始時の車両" }, ev.Data!["changed"]!.AsArray().Select(n => (string)n!));
        var prompt = Status(w)["prompts"]![0]!;
        Assert.Equal("car", (string)prompt["type"]!);

        // 車名とセッティング名を入れて確定
        w.ResolveCar((string)prompt["id"]!, "Honda Civic Type R", "new", "街乗り", null);
        Assert.Equal("car_confirmed", events[^1].Type);
        var st = Status(w);
        Assert.Empty(st["prompts"]!.AsArray());
        Assert.Equal("Honda Civic Type R", (string)st["car"]!["name"]!);
        Assert.Equal("街乗り", (string)st["car"]!["setup"]!);
        Assert.True((bool)st["car"]!["confirmed"]!);

        // 未登録のコースでレース開始 → コースのカード
        Send(w, 10, on: false);
        Send(w, 120, pos: 8);
        var start = events.Single(e => e.Type == "race_start");
        Assert.False((bool)start.Data!["matched"]!);
        Assert.NotNull(start.Data!["car_setup_id"]);
        var cp = Status(w)["prompts"]![0]!;
        Assert.Equal("course", (string)cp["type"]!);
        w.ResolveCourse((string)cp["id"]!, "new", "テストのコース", "street", null);
        var cc = events[^1];
        Assert.Equal("course_confirmed", cc.Type);
        Assert.Equal(start.RecvTime, (double)cc.Data!["race_start_recv_time"]!, 5);
        Assert.Equal("テストのコース", (string)Status(w)["course"]!["name"]!);

        Send(w, 5, pos: 0, kmh: 30);
        Assert.Equal("race_end", events[^1].Type);
        w.DetachSession();

        // 別の日(アプリを起動し直した): 登録済みの車は名前で出て、確定済みのセッティングを推定する
        var w2 = NewWatcher();
        var events2 = new List<SessionEvent>();
        w2.AttachSession(events2.Add);
        Send(w2, 30);
        Assert.Equal("Honda Civic Type R", (string)events2[0].Data!["name"]!);
        Assert.Equal("街乗り", (string)events2[0].Data!["setup_name"]!);

        // グリッドの後方 60m からのスタートでも同じコースと分かる
        Send(w2, 10, on: false);
        Send(w2, 60, pos: 8, x: 100 - 60 * Math.Sin(0.6), z: 200 - 60 * Math.Cos(0.6));
        var s2 = events2.Single(e => e.Type == "race_start");
        Assert.True((bool)s2.Data!["matched"]!);
        Assert.Equal("テストのコース", (string)s2.Data!["course_name"]!);
        Assert.Equal(60, (double)s2.Data!["grid_offset_m"]!, 0);
        Assert.DoesNotContain(Status(w2)["prompts"]!.AsArray(), p => (string)p!["type"]! == "course");
    }

    [Fact]
    public void 記録していない間は書かず_確定済みなら記録開始時にカードを出さない()
    {
        var w = NewWatcher();
        Send(w, 30);
        var p = Status(w)["prompts"]![0]!;
        w.ResolveCar((string)p["id"]!, "Honda Civic Type R", "new", "", null);   // セッティング名が空なら「B585 FWD」

        var events = new List<SessionEvent>();
        w.AttachSession(events.Add);
        var ev = Assert.Single(events);
        Assert.Equal("car_detected", ev.Type);
        Assert.Equal("B585 FWD", (string)ev.Data!["setup_name"]!);
        Assert.Empty(Status(w)["prompts"]!.AsArray());
    }

    [Fact]
    public void セッティングを変えたら名前を聞く()
    {
        var w = NewWatcher();
        Send(w, 30);
        w.ResolveCar((string)Status(w)["prompts"]![0]!["id"]!, "Honda Civic Type R", "new", "街乗り", null);

        w.NewVariant();
        var p = Status(w)["prompts"]![0]!;
        Assert.Equal("セッティング(手動)", (string)p["changed"]![0]!);
        Assert.Null(p["guessSetup"]);
        w.ResolveCar((string)p["id"]!, "Honda Civic Type R", "new", "ギア比を変えた", null);

        var reg = Registry.Load(RegistryPath, out _);
        Assert.Equal(new[] { 1, 2 }, reg.Setups.Select(s => s.Variant));
    }

    [Fact]
    public void 入力が足りなければ理由を返す()
    {
        var w = NewWatcher();
        Send(w, 30);
        var id = (string)Status(w)["prompts"]![0]!["id"]!;
        Assert.Equal("車名を入力してください", Assert.Throws<UserError>(() => w.ResolveCar(id, " ", "new", "", null)).Message);
        Assert.Throws<UserError>(() => w.ResolveCourse("P9999", "new", "x", "road", null));
        w.Dismiss(id);
        Assert.Empty(Status(w)["prompts"]!.AsArray());
    }

    [Fact]
    public async Task 記録の出来事がsession_jsonに書かれる()
    {
        var opt = new RecorderOptions { OutputDir = _dir, SaveRawPackets = false, Obs = new ObsOptions { Enabled = false } };
        var telemetry = new TelemetryService(opt, NullLogger<TelemetryService>.Instance);
        var obs = new ObsClient(opt, NullLogger<ObsClient>.Instance);
        var quality = new ObsQualityService(obs, opt, NullLogger<ObsQualityService>.Instance);
        var w = NewWatcher();
        var controller = new SessionController(opt, telemetry, obs, quality, NullLogger<SessionController>.Instance, w);

        Assert.True((await controller.StartAsync(video: false, label: null)).Ok);
        Send(w, 30);
        Send(w, 5, on: false);
        Send(w, 30, pos: 3);
        Assert.True((await controller.StopAsync()).Ok);
        Send(w, 5, pos: 0);   // 記録を止めた後の出来事は書かない

        var folder = Directory.GetDirectories(_dir).Single();
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, SessionMeta.FileName)))!;
        var types = json["events"]!.AsArray().Select(e => (string)e!["type"]!).ToList();
        Assert.Contains("car_detected", types);
        Assert.Contains("race_start", types);
        Assert.DoesNotContain("race_end", types);
        var rs = json["events"]!.AsArray().First(e => (string)e!["type"]! == "race_start")!;
        Assert.Equal(100, (double)rs["data"]!["start"]!["x"]!);
        Assert.NotNull(rs["data"]!["timestamp_ms"]);
        // data の無い出来事には data キーを書かない
        Assert.Null(json["events"]!.AsArray().First(e => (string)e!["type"]! == "logging_started")!["data"]);
    }

    private SessionController NewController(RaceWatcher w)
    {
        var opt = new RecorderOptions { OutputDir = _dir, SaveRawPackets = false, Obs = new ObsOptions { Enabled = false } };
        var telemetry = new TelemetryService(opt, NullLogger<TelemetryService>.Instance);
        var obs = new ObsClient(opt, NullLogger<ObsClient>.Instance);
        var quality = new ObsQualityService(obs, opt, NullLogger<ObsQualityService>.Instance);
        return new SessionController(opt, telemetry, obs, quality, NullLogger<SessionController>.Instance, w);
    }

    [Fact]
    public async Task 記録を止めた後でも直前のレースのコースと車を直せて_記録に書かれる()
    {
        var w = NewWatcher();
        var controller = NewController(w);
        Assert.True((await controller.StartAsync(video: false, label: null)).Ok);
        Send(w, 30);
        Send(w, 5, on: false);
        Send(w, 60, pos: 3);
        Send(w, 5, pos: 0, kmh: 200);
        Assert.True((await controller.StopAsync()).Ok);

        // コースのカードは「あとで」にした。記録を止めた後で、直前のレースとして登録する
        var st = Status(w);
        w.Dismiss((string)st["prompts"]!.AsArray().First(p => (string)p!["type"]! == "course")!["id"]!);
        Assert.True((bool)st["lastRace"]!["ended"]!);
        Assert.NotNull(st["lastRace"]!["recorded"]);
        w.EditLastRace("new", "鳥野山サーキット", "road", "Honda S600", "new", "雪用");

        st = Status(w);
        Assert.Equal("鳥野山サーキット", (string)st["lastRace"]!["course"]!["name"]!);
        Assert.Equal("Honda S600", (string)st["lastRace"]!["car"]!["name"]!);
        Assert.Equal("雪用", (string)st["lastRace"]!["car"]!["setup"]!);

        // 止めた記録の session.json に足され、再生アプリの名前の取り方でも直した名前になる
        var folder = Directory.GetDirectories(_dir).Single();
        var meta = SessionMeta.Load(folder);
        Assert.True(meta.Complete);
        var edited = meta.Events.Single(e => e.Type == "race_edited");
        var start = meta.Events.Single(e => e.Type == "race_start");
        Assert.Equal(start.RecvTime, (double)edited.Data!["race_start_recv_time"]!, 5);
        Assert.Equal("鳥野山サーキット", RaceNames.Course(meta.Events, start.RecvTime));
        Assert.Equal(("Honda S600", "雪用"), RaceNames.Car(meta.Events, start.RecvTime));

        // 名前だけ変える(登録済みのコースの名前そのものを変える)
        var courseId = (string)st["lastRace"]!["course"]!["id"]!;
        w.EditLastRace(courseId, "鳥野山サーキット(冬)", null, null, null, null);
        Assert.Equal("鳥野山サーキット(冬)", Registry.Load(RegistryPath, out _).CourseById(courseId)!.Name);
        Assert.Equal("鳥野山サーキット(冬)", RaceNames.Course(SessionMeta.Load(folder).Events, start.RecvTime));

        // 何も変えずに保存しても、今の名前をこのレースのものとして記録に書く
        w.EditLastRace(null, null, null, null, null, null);
        var last = SessionMeta.Load(folder).Events[^1];
        Assert.Equal("race_edited", last.Type);
        Assert.Equal("確認", (string)last.Data!["changes"]![0]!);
        Assert.Equal("Honda S600", (string)last.Data!["car_name"]!);
    }

    [Fact]
    public void 記録していないレースも直せて_最近のできごとに残る()
    {
        var w = NewWatcher();
        Send(w, 30);
        Send(w, 5, on: false);
        Send(w, 60, pos: 3);
        var st = Status(w);
        Assert.Null(st["lastRace"]!["recorded"]);
        Assert.False((bool)st["lastRace"]!["ended"]!);
        var texts = st["recent"]!.AsArray().Select(r => (string)r!["text"]!).ToList();
        Assert.StartsWith("レース開始", texts[0]);        // 新しい順
        Assert.StartsWith("車両を検出", texts[1]);

        w.EditLastRace("new", "練習コース", "dirt", "練習車", null, null);
        Assert.Equal("練習車", Registry.Load(RegistryPath, out _).CarName(2163));
        Assert.Contains(Status(w)["prompts"]!.AsArray(), p => (string)p!["type"]! == "car");   // セッティングが決まるまでは残す
        w.EditLastRace(null, null, null, null, "new", "練習用");
        Assert.DoesNotContain(Status(w)["prompts"]!.AsArray(), p => (string)p!["type"]! == "car");
        Assert.True((bool)Status(w)["car"]!["confirmed"]!);
        // 今もそのレースのコースにいるので、状態の欄のコースも変わる
        Assert.Equal("練習コース", (string)Status(w)["course"]!["name"]!);
        Assert.Empty(Directory.GetDirectories(_dir));
    }

    [Fact]
    public void レースが無ければ直せない()
    {
        var w = NewWatcher();
        Assert.Throws<UserError>(() => w.EditLastRace("new", "x", null, null, null, null));
    }

    [Fact]
    public void 再生アプリで付けた名前を取り込み_次からその名前で出る()
    {
        // 再生アプリが受け渡し用のファイルに足した頼み(車両 2163・PI 585、スタート (100, 200) 向き 0.6)
        RegistryInbox.Append(_dir, new RegistryRequest
        {
            CreatedAt = "2026-09-28T21:00:00+09:00", Session = "20260928_210000", RaceStartRecvTime = 1000,
            CourseId = RegistryInbox.NewId, CourseName = "再生アプリのコース", CourseKind = "road", StartX = 100, StartZ = 200, StartYaw = 0.6,
            CarOrdinal = 2163, ClassRaw = 2, Pi = 585, DrivetrainRaw = 0, Cylinders = 4,
            CarName = "再生アプリの車", SetupId = RegistryInbox.NewId, SetupName = "再生アプリの設定",
        });

        var w = NewWatcher();
        var texts = Status(w)["recent"]!.AsArray().Select(r => (string)r!["text"]!).ToList();   // 画面の更新で取り込む
        Assert.Contains(texts, t => t.Contains("再生アプリから") && t.Contains("再生アプリのコース"));
        Assert.False(File.Exists(Path.Combine(_dir, RegistryInbox.FileName)));   // 取り込んだら消す
        var reg = Registry.Load(RegistryPath, out _);
        Assert.Equal("再生アプリの車", reg.CarName(2163));
        Assert.Equal("再生アプリの設定", Assert.Single(reg.FindSetups(new CarSignature(2163, 2, 585, 0, 4))).Name);

        // 次にその車でそのコースを走ると、登録した名前で出る
        Send(w, 30);
        Assert.Equal("再生アプリの車", (string)Status(w)["car"]!["name"]!);
        Send(w, 5, on: false);
        Send(w, 30, pos: 3);
        Assert.Equal("再生アプリのコース", (string)Status(w)["course"]!["name"]!);
    }
}
