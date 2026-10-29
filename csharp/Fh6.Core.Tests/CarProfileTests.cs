using System.Globalization;
using System.Text;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class CarProfileTests
{
    private static readonly string[] Wheels = { "fl", "fr", "rl", "rr" };

    /// <summary>試しの行(33 ms ごと)。値は列名 → 行ごとの値の関数</summary>
    private static TelemetryTable Table(int n, Func<int, Dictionary<string, double>> row)
    {
        var cols = new List<string> { TelemetryTable.RecvTime, "is_race_on", "current_engine_rpm", "power", "accel", "brake", "steer", "gear", "speed", "acceleration_x", "acceleration_z" };
        cols.AddRange(Wheels.SelectMany(w => new[] { "tire_slip_ratio_" + w, "tire_combined_slip_" + w, "surface_rumble_" + w }));
        var sb = new StringBuilder(string.Join(",", cols) + "\n");
        for (int i = 0; i < n; i++)
        {
            var v = new Dictionary<string, double>
            {
                [TelemetryTable.RecvTime] = 1000 + i * 0.033, ["is_race_on"] = 1, ["current_engine_rpm"] = 0, ["power"] = 0, ["accel"] = 0, ["brake"] = 0,
                ["steer"] = 0, ["gear"] = 3, ["speed"] = 20, ["acceleration_x"] = 0, ["acceleration_z"] = 0,
            };
            foreach (var w in Wheels) { v["tire_slip_ratio_" + w] = 0.05; v["tire_combined_slip_" + w] = 0.1; v["surface_rumble_" + w] = 0; }
            foreach (var (k, x) in row(i)) v[k] = x;
            sb.Append(string.Join(",", cols.Select(c => v[c].ToString("R", CultureInfo.InvariantCulture)))).Append('\n');
        }
        return TelemetryTable.Load(new StringReader(sb.ToString()));
    }

    private static ProfilePart Build(TelemetryTable t, bool[]? exclude = null)
    {
        var rows = Enumerable.Range(0, t.RowCount).ToList();
        var ts = t[TelemetryTable.RecvTime].Select(r => r * 1000).ToArray();
        return ProfilePartBuilder.Build(t, rows, ts, exclude ?? new bool[t.RowCount]);
    }

    /// <summary>3 速の全開で 4,000〜7,500 rpm を上がる(出力は回転数 × 0.05 hp)。速度は回転数 ÷ 200 m/s</summary>
    private static Dictionary<string, double> Sweep(int i) => new()
    {
        ["accel"] = 255, ["gear"] = 3, ["current_engine_rpm"] = 4000 + i * 3, ["power"] = (4000 + i * 3) * 0.05 * 745.7, ["speed"] = (4000 + i * 3) / 200.0,
    };

    [Fact]
    public void 度数のキーは地域によらずピリオドで_パーセンタイルは区切りの真ん中()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.24", Histogram.Key(1.2499, 0.02, "0.00"));
            var h = new Dictionary<string, int> { ["1.00"] = 1, ["1.20"] = 2, ["1.40"] = 1 };
            Assert.Equal(1.21, Histogram.Percentile(h, 0.5, 0.02)!.Value, 6);
            Assert.Null(Histogram.Percentile(new Dictionary<string, int>(), 0.5, 0.02));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void 全開の行からパワーカーブ_埋まり具合_回転数_ギアの度数を数える()
    {
        var part = Build(Table(1200, Sweep));
        Assert.Equal(1200, part.Rows);
        Assert.Equal(new[] { 3 }, part.GearsSeen);
        // 4,000 rpm の区切り(4,000〜4,249)は 84 行。出力は 200〜212 hp
        var bin = part.Power["4000"];
        Assert.Equal(84, Histogram.Count(bin));
        Assert.True(bin.Keys.All(k => int.Parse(k) is >= 200 and < 213));
        Assert.Equal(167, part.Coverage["3"]["4000"]);
        Assert.Equal(1200, Histogram.Count(part.RpmFull));
        Assert.Equal(1200, Histogram.Count(part.GearK["3"]));
        Assert.True(part.GearK["3"].ContainsKey("200.0"));
    }

    [Fact]
    public void 変速の後1秒と_出力0以下と_部分開度はパワーカーブに入れない()
    {
        var part = Build(Table(300, i => new()
        {
            ["accel"] = i < 100 ? 150 : 255, ["gear"] = i < 150 ? 2 : 3, ["current_engine_rpm"] = 5000,
            ["power"] = i >= 280 ? -1000 : 200 * 745.7,
        }));
        // 全開は 100〜299。3 速に変わった 150 から 1 秒(31 行)を除き、出力が負の 280〜299 を除く → 100〜149 と 181〜279
        Assert.Equal(50 + 99, Histogram.Count(part.Power["5000"]));
        Assert.Equal(200, Histogram.Count(part.RpmFull));   // リミッターの度数は出力によらず全開の行
    }

    [Fact]
    public void ブレーキ_旋回_発進を路面ごとに数え_除く行は入れない()
    {
        var t = Table(400, i => i switch
        {
            < 100 => new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -1.5 * 9.80665 },                     // 108 km/h、舗装、1.5 G
            < 200 => new() { ["speed"] = 20, ["acceleration_x"] = 1.2 * 9.80665, ["tire_combined_slip_fl"] = 1.0,        // 右旋回、ダート
                             ["surface_rumble_fl"] = 0.12, ["surface_rumble_fr"] = 0.12, ["surface_rumble_rl"] = 0.12, ["surface_rumble_rr"] = 0.12 },
            < 300 => new() { ["accel"] = 255, ["gear"] = 1, ["speed"] = 10, ["acceleration_z"] = 0.6 * 9.80665 },        // 発進、舗装
            _ => new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -9 * 9.80665 },                           // 衝撃(4 G 超)は入れない
        });
        var exclude = new bool[400];
        for (int i = 0; i < 20; i++) exclude[i] = true;   // 接触の前後など
        var part = Build(t, exclude);
        Assert.Equal(80, Histogram.Count(part.Brake["paved"]["100"]));
        Assert.Equal(1.5, Histogram.Percentile(part.Brake["paved"]["100"], 0.5, 0.02)!.Value, 1);
        Assert.Equal(100, Histogram.Count(part.Corner["dirt"]["right"]));
        Assert.False(part.Corner.ContainsKey("paved"));
        Assert.Equal(100, Histogram.Count(part.Launch["paved"]));
    }

    [Fact]
    public void 最後のギアで速度が上がらなくなった所を最高速とする()
    {
        // 6 速で 250 km/h まで上がって、そこから 2 秒平ら
        var part = Build(Table(300, i => new()
        {
            ["accel"] = 255, ["gear"] = i < 50 ? 5 : 6, ["power"] = 300 * 745.7,
            ["speed"] = Math.Min(250, 200 + i * 0.5) / 3.6, ["current_engine_rpm"] = 6000,
        }));
        Assert.NotNull(part.Top);
        Assert.Equal(6, part.Top!.Gear);
        Assert.Equal(250, part.Top.Kmh, 0);
    }

    [Fact]
    public void 部分を合わせてカーブ_リミッター_ギア比を出す()
    {
        var a = Build(Table(1200, Sweep));
        var b = Build(Table(1200, i => new()
        {
            ["accel"] = 255, ["gear"] = 4, ["current_engine_rpm"] = 4000 + i * 3, ["power"] = (4000 + i * 3) * 0.05 * 745.7, ["speed"] = (4000 + i * 3) / 150.0,
        }));
        var f = new CarProfileFile();
        a.Folder = "A"; b.Folder = "B";
        ProfileStore.Upsert(f, a); ProfileStore.Upsert(f, b);
        var p = new CarProfile(f);
        var c = p.Curve()!;
        Assert.Equal(4125, c.Rpm[0]);
        Assert.InRange(c.At(6000)!.Value, 300, 306);   // 区切りの中の 75 パーセンタイルなので、真ん中の値より少し上
        Assert.InRange(p.LimiterRpm()!.Value, 7550, 7610);
        Assert.Equal(0.75, p.Ratios()[3], 2);   // 4 速 150 ÷ 3 速 200
        // 同じ記録・同じレースの部分は置き換える
        var a2 = Build(Table(100, Sweep)); a2.Folder = "A";
        ProfileStore.Upsert(f, a2);
        Assert.Equal(2, f.Parts.Count);
        Assert.Equal(100, f.Parts.Single(x => x.Folder == "A").Rows);
    }

    [Fact]
    public void 置き場所に書いて読み戻せる_形式が違えば作り直す()
    {
        var dir = Path.Combine(Path.GetTempPath(), "heiso-profile-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            var car = new CarSignature(2357, 2, 588, 2, 4);
            var f = store.Load(car);
            Assert.Empty(f.Parts);
            var part = Build(Table(200, Sweep));
            part.Folder = "20261016_201555"; part.RaceStartRecvTime = 1000; part.CsvSize = 123; part.CsvMtime = "t1";
            part.Assists = new Assists { Abs = "on", Tcs = "on" };
            ProfileStore.Upsert(f, part);
            store.Save(f);
            Assert.True(File.Exists(Path.Combine(dir, "2357_2_588_2_4.json")));
            var g = store.Load(car);
            Assert.Single(g.Parts);
            Assert.Equal("on", g.Parts[0].Assists!.Abs);
            Assert.Equal(Histogram.Count(part.Power["4000"]), Histogram.Count(g.Parts[0].Power["4000"]));
            Assert.True(ProfileStore.IsFresh(g, "20261016_201555", 1000, 123, "t1"));
            Assert.False(ProfileStore.IsFresh(g, "20261016_201555", 1000, 124, "t1"));
            File.WriteAllText(Path.Combine(dir, "2357_2_588_2_4.json"), "{\"format\":\"heiso-car-profile/9\",\"parts\":[{}]}");
            Assert.Empty(store.Load(car).Parts);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 限界は補助の設定で分けて_行が60以上で測れたとする()
    {
        var on = Build(Table(100, _ => new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -1.6 * 9.80665 }));
        on.Folder = "A"; on.RaceStartRecvTime = 1; on.Assists = new Assists { Abs = "on", Tcs = "on" };
        var off = Build(Table(40, _ => new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -1.2 * 9.80665 }));
        off.Folder = "B"; off.RaceStartRecvTime = 2; off.Assists = new Assists { Abs = "off", Tcs = "on" };
        var f = new CarProfileFile { Parts = { on, off } };
        var onlyOn = CarProfile.ForAssists(f, new Assists { Abs = "on", Tcs = "off" });   // ブレーキは ABS だけで選ぶ(TC が違っても同じ組)
        Assert.True(onlyOn.BrakeAll("paved").Measured);
        Assert.Equal(1.61, onlyOn.BrakeAll("paved").G!.Value, 2);
        var onlyOff = CarProfile.ForAssists(f, new Assists { Abs = "off", Tcs = "on" });
        Assert.False(onlyOff.BrakeAll("paved").Measured);   // 40 行
        Assert.Equal("abs=?,tcs=?,stm=?,lc=?", AssistKey.Of(null));
        Assert.Equal("abs=on", AssistKey.For(new Assists { Abs = "on", Tcs = "off", Stm = "on" }, "brake"));
        Assert.Equal("tcs=off,stm=on", AssistKey.For(new Assists { Abs = "on", Tcs = "off", Stm = "on" }, "corner"));
        Assert.Equal("tcs=off,lc=?", AssistKey.For(new Assists { Abs = "on", Tcs = "off", Stm = "on" }, "launch"));
        Assert.False(AssistKey.Known(new Assists { Abs = "on" }));
    }

    [Fact]
    public void 限界はレースの部分だけで出す_外の走りはパワーカーブにだけ使う()
    {
        var race = Build(Table(100, _ => new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -1.6 * 9.80665 }));
        race.Folder = "A"; race.RaceStartRecvTime = 1;
        // レースの外: 弱いブレーキの行が多い(移動・自動運転)。ダートも走っている
        var free = Build(Table(300, i =>
        {
            double r = i < 200 ? 0 : 0.12;   // 200 行は舗装、100 行はダート
            return new() { ["brake"] = 255, ["speed"] = 30, ["acceleration_z"] = -0.8 * 9.80665,
                           ["surface_rumble_fl"] = r, ["surface_rumble_fr"] = r, ["surface_rumble_rl"] = r, ["surface_rumble_rr"] = r };
        }));
        free.Folder = "A"; free.RaceStartRecvTime = null;
        var p = new CarProfile(new CarProfileFile { Parts = { race, free } });
        Assert.Equal(1.61, p.BrakeAll("paved").G!.Value, 2);
        Assert.Equal(100, p.BrakeAll("paved").Rows);
        Assert.Equal(0, p.BrakeAll("dirt").Rows);
        Assert.False(p.HasSurface("dirt"));
        Assert.Equal(300, Histogram.Count(Histogram.Merge(free.Brake["paved"].Values)) + Histogram.Count(Histogram.Merge(free.Brake["dirt"].Values)));   // 外の行は数えてある(使わないだけ)
    }

    [Fact]
    public void 判定_初めての車はまずふつうに1本()
    {
        var r = MeasurementEvaluator.Evaluate(new CarProfile(new CarProfileFile()));
        Assert.Equal(AdviceKind.FirstRun, r.Advice[0].Kind);
        Assert.Contains(r.Advice, a => a.Kind == AdviceKind.Brake && a.Surface == "paved");
        Assert.DoesNotContain(r.Advice, a => a.Surface == "dirt");   // 走っていない路面の指示は出さない
    }

    [Fact]
    public void 判定_足りない帯をギアと速度に直す_使わない帯と届かない帯は除く()
    {
        // 1・2 速の比 0.6 と、4,000〜7,500 rpm(3 速)。4,500〜5,000 rpm だけ行が少ない
        var full = Build(Table(1200, i =>
        {
            int rpm = 4000 + i * 3;
            return new() { ["accel"] = 255, ["gear"] = 3, ["current_engine_rpm"] = rpm >= 4500 && rpm < 5000 ? 5000 : rpm, ["power"] = rpm * 0.05 * 745.7, ["speed"] = rpm / 200.0 };
        }));
        var g1 = Build(Table(100, i => new() { ["accel"] = 255, ["gear"] = 1, ["current_engine_rpm"] = 6000, ["power"] = 300 * 745.7, ["speed"] = 6000 / 400.0 }));
        var g2 = Build(Table(100, i => new() { ["accel"] = 255, ["gear"] = 2, ["current_engine_rpm"] = 6000, ["power"] = 300 * 745.7, ["speed"] = 6000 / 240.0 }));
        full.Folder = "A"; g1.Folder = "B"; g2.Folder = "C";
        var p = new CarProfile(new CarProfileFile { Parts = { full, g1, g2 } });
        var r = MeasurementEvaluator.Evaluate(p);
        Assert.NotNull(r.Limiter);
        Assert.Contains(r.Bands, b => b.State == BandState.Unused);
        Assert.Contains(r.Bands, b => b.State == BandState.Unreachable || b.To <= r.Limiter + 500);
        var short4500 = r.Bands.Single(b => b.From == 4500);
        Assert.Equal(BandState.Short, short4500.State);
        var adv = Assert.Single(r.Advice, a => a.Kind == AdviceKind.RpmBand);
        Assert.Equal(4500, adv.From);
        Assert.Equal(5000, adv.To);
        Assert.Equal(3, adv.Gear);          // 4,500 rpm は 3 速で 81 km/h(50〜130 に入るいちばん上のギア)
        Assert.Equal(80, adv.Kmh);
    }

    [Fact]
    public void 判定_ギアの選び方()
    {
        // 回転数 ÷ 速度(m/s)。3,000 rpm は 2 速 36 km/h、3 速 54、4 速 72、5 速 90 → 50〜130 に入るいちばん上の 5 速
        var k = new Dictionary<int, double> { [2] = 300, [3] = 200, [4] = 150, [5] = 120 };
        Assert.Equal((5, 90.0), MeasurementEvaluator.PickGear(k, 3000));
        // どのギアも範囲に入らなければ、90 km/h にいちばん近いギア(1,000 rpm は 5 速で 30 km/h)
        Assert.Equal((5, 30.0), MeasurementEvaluator.PickGear(k, 1000));
    }
}
