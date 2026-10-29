using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Core;

// 計測モードの車のプロファイル(docs/spec.md「計測モード」、形式は docs/session-format.md「車のプロファイル」)。
// 記録ごと・レースごとの「部分」に度数を数えて持ち、同じ車の部分を合わせて、パワーカーブ・リミッター・ギア比・限界を出す。
// 記録から作り直せるので、置き場所は再生アプリの領域(%LOCALAPPDATA%\HEISO\Player\profiles)。

/// <summary>プロファイルのファイル(1 台の車両の組につき 1 つ)</summary>
public sealed class CarProfileFile
{
    public const string FormatId = "heiso-car-profile/1";

    public string Format { get; set; } = FormatId;
    public ProfileCar Car { get; set; } = new();
    public string UpdatedAt { get; set; } = "";
    public List<ProfilePart> Parts { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProfileCar
{
    public int Ordinal { get; set; }
    public int ClassRaw { get; set; }
    public int Pi { get; set; }
    public int DrivetrainRaw { get; set; }
    public int Cylinders { get; set; }
    public string? Name { get; set; }

    public static ProfileCar From(CarSignature c, string? name) => new()
    {
        Ordinal = c.Ordinal, ClassRaw = c.Class, Pi = c.Pi, DrivetrainRaw = c.Drivetrain, Cylinders = c.Cylinders, Name = name,
    };

    /// <summary>ファイル名に使う組(ordinal_class_pi_drivetrain_cylinders)</summary>
    public static string KeyOf(CarSignature c) => $"{c.Ordinal}_{c.Class}_{c.Pi}_{c.Drivetrain}_{c.Cylinders}";
}

/// <summary>1 つの記録の 1 レース(か、レースの外の走り)の集計。度数はキーが区切りの下の値(文字列)、値が行数</summary>
public sealed class ProfilePart
{
    public string Folder { get; set; } = "";
    /// <summary>どのレースか(race_start の recv_time)。レースの外の走りは null</summary>
    public double? RaceStartRecvTime { get; set; }
    public string? Course { get; set; }
    public long CsvSize { get; set; }
    public string CsvMtime { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public Assists? Assists { get; set; }
    /// <summary>変速回転数がそろう(自動変速か AutoDrive らしい)。true なら変速の判定に使わない</summary>
    public bool AutoShift { get; set; }
    public int Rows { get; set; }
    public double Seconds { get; set; }
    /// <summary>{"区切り(250 rpm)": {"出力 hp": 行数}}</summary>
    public Dictionary<string, Dictionary<string, int>> Power { get; set; } = new();
    /// <summary>{"ギア": {"区切り(500 rpm)": 行数}}</summary>
    public Dictionary<string, Dictionary<string, int>> Coverage { get; set; } = new();
    /// <summary>{"区切り(25 rpm)": 行数}</summary>
    public Dictionary<string, int> RpmFull { get; set; } = new();
    /// <summary>{"ギア": {"回転数 ÷ 速度 の区切り(0.5)": 行数}}</summary>
    public Dictionary<string, Dictionary<string, int>> GearK { get; set; } = new();
    public List<int> GearsSeen { get; set; } = new();
    public ProfileTop? Top { get; set; }
    /// <summary>{"路面": {"速度の区切り(50 km/h)": {"減速 G の区切り(0.02)": 行数}}}</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Brake { get; set; } = new();
    /// <summary>{"路面": {"left" / "right": {"横 G の区切り(0.02)": 行数}}}</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Corner { get; set; } = new();
    /// <summary>{"路面": {"前後 G の区切り(0.02)": 行数}}</summary>
    public Dictionary<string, Dictionary<string, int>> Launch { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>最後のギアで、全開なのに速度がほぼ上がらなくなった所(空気抵抗の頭打ち)</summary>
public sealed class ProfileTop
{
    public int Gear { get; set; }
    public double Kmh { get; set; }
    public double Rpm { get; set; }
}

/// <summary>度数(ヒストグラム)の操作。キーは区切りの下の値を、地域の形式によらない文字列で</summary>
public static class Histogram
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Key(double value, double step, string format) =>
        (Math.Floor(value / step) * step).ToString(format, Inv);

    public static void Add(Dictionary<string, int> h, string key, int n = 1) => h[key] = h.TryGetValue(key, out var v) ? v + n : n;

    public static Dictionary<string, int> Merge(IEnumerable<Dictionary<string, int>> hs)
    {
        var m = new Dictionary<string, int>();
        foreach (var h in hs) foreach (var (k, n) in h) Add(m, k, n);
        return m;
    }

    public static int Count(Dictionary<string, int>? h) => h == null ? 0 : h.Values.Sum();

    /// <summary>度数の p(0〜1)の位置の値。値は区切りの真ん中(下の値 + step / 2)。空なら null</summary>
    public static double? Percentile(Dictionary<string, int>? h, double p, double step)
    {
        if (h == null || h.Count == 0) return null;
        var sorted = h.Select(kv => (Value: double.Parse(kv.Key, Inv) + step / 2, N: kv.Value)).Where(x => x.N > 0).OrderBy(x => x.Value).ToList();
        int total = sorted.Sum(x => x.N);
        if (total == 0) return null;
        double target = (total - 1) * p;
        int seen = 0;
        foreach (var (value, n) in sorted)
        {
            if (seen + n > target) return value;
            seen += n;
        }
        return sorted[^1].Value;
    }
}

/// <summary>1 つの部分の度数を数える(性能モードの PowerAnalysis と同じ規則)</summary>
public static class ProfilePartBuilder
{
    public const double BrakePedal = 230;          // 90%
    public const double BrakeMinKmh = 50;
    public const double BrakeMaxSteerPct = 20;
    public const double CornerMinKmh = 40;
    public const double CornerSlipLo = 0.8;
    public const double CornerSlipHi = 1.2;
    public const double LaunchMaxKmh = 100;
    public const double GStep = 0.02;
    /// <summary>頭打ちとみなす、1 秒あたりの速度の伸び(km/h)</summary>
    public const double TopFlatKmhPerSec = 0.5;
    private const double WattsPerHp = 745.7;
    private const double G0 = 9.80665;

    /// <summary>
    /// rows はこの車・この区間の is_race_on の行を時刻の順に。tsMs は行の時刻(ms)。
    /// gExclude[行] が true の行(接触・着地の前後、空中)は G の度数に入れない
    /// </summary>
    public static ProfilePart Build(TelemetryTable t, IReadOnlyList<int> rows, double[] tsMs, bool[] gExclude)
    {
        var part = new ProfilePart { Rows = rows.Count };
        double[] rpm = t["current_engine_rpm"], power = t["power"], accel = t["accel"], brake = t["brake"], steer = t["steer"],
            gear = t["gear"], speed = t["speed"], ax = t["acceleration_x"], az = t["acceleration_z"];
        var slipRatio = Wheels.Select(w => t["tire_slip_ratio_" + w]).ToArray();
        var combined = Wheels.Select(w => t["tire_combined_slip_" + w]).ToArray();
        var rumble = Wheels.Select(w => t["surface_rumble_" + w]).ToArray();
        var seen = new SortedSet<int>();
        double lastChange = double.NegativeInfinity;

        for (int k = 0; k < rows.Count; k++)
        {
            int i = rows[k];
            if (k > 0)
            {
                double dt = tsMs[i] - tsMs[rows[k - 1]];
                if (dt > 0 && dt < 300) part.Seconds += dt / 1000;
                if (gear[i] != gear[rows[k - 1]]) lastChange = tsMs[i];
            }
            bool drive = gear[i] >= 1 && gear[i] <= 10;
            if (drive) seen.Add((int)gear[i]);
            bool full = accel[i] >= PowerAnalysis.FullThrottle && drive;
            double kmh = speed[i] * 3.6;

            if (full && rpm[i] > 0)
            {
                Histogram.Add(part.RpmFull, Histogram.Key(rpm[i], 25, "0"));
                if (power[i] > 0 && tsMs[i] - lastChange >= PowerAnalysis.AfterShiftMs)
                {
                    var bin = Histogram.Key(rpm[i], PowerAnalysis.BinRpm, "0");
                    if (!part.Power.TryGetValue(bin, out var h)) part.Power[bin] = h = new();
                    Histogram.Add(h, Math.Floor(power[i] / WattsPerHp).ToString(CultureInfo.InvariantCulture));
                    var g = ((int)gear[i]).ToString(CultureInfo.InvariantCulture);
                    if (!part.Coverage.TryGetValue(g, out var c)) part.Coverage[g] = c = new();
                    Histogram.Add(c, Histogram.Key(rpm[i], 500, "0"));
                }
                if (speed[i] > PowerAnalysis.MinSpeed && slipRatio.All(s => double.IsFinite(s[i]) && Math.Abs(s[i]) <= PowerAnalysis.MaxSlipRatio))
                {
                    var g = ((int)gear[i]).ToString(CultureInfo.InvariantCulture);
                    if (!part.GearK.TryGetValue(g, out var h)) part.GearK[g] = h = new();
                    Histogram.Add(h, Histogram.Key(rpm[i] / speed[i], 0.5, "0.0"));
                }
            }

            if (gExclude[i]) continue;
            string? surface = SurfaceOf(rumble, i);
            if (surface == null) continue;
            double gLong = az[i] / G0, gLat = ax[i] / G0;
            if (Math.Abs(gLong) > 4 || Math.Abs(gLat) > 4) continue;   // 衝撃のはね上がり

            if (brake[i] >= BrakePedal && kmh > BrakeMinKmh && Math.Abs(steer[i] / 1.27) < BrakeMaxSteerPct)
                Add3(part.Brake, surface, Histogram.Key(kmh, 50, "0"), Histogram.Key(-gLong, GStep, "0.00"));
            double slip = combined.Max(s => double.IsFinite(s[i]) ? s[i] : 0);
            if (kmh > CornerMinKmh && slip >= CornerSlipLo && slip <= CornerSlipHi && gLat != 0)
                Add3(part.Corner, surface, gLat > 0 ? "right" : "left", Histogram.Key(Math.Abs(gLat), GStep, "0.00"));
            if (full && (gear[i] == 1 || gear[i] == 2) && kmh < LaunchMaxKmh)
            {
                if (!part.Launch.TryGetValue(surface, out var h)) part.Launch[surface] = h = new();
                Histogram.Add(h, Histogram.Key(gLong, GStep, "0.00"));
            }
        }
        part.GearsSeen = seen.ToList();
        part.Top = FindTop(rows, tsMs, gear, accel, speed, rpm, power);
        return part;
    }

    private static readonly string[] Wheels = { "fl", "fr", "rl", "rr" };

    private static void Add3(Dictionary<string, Dictionary<string, Dictionary<string, int>>> d, string a, string b, string key)
    {
        if (!d.TryGetValue(a, out var x)) d[a] = x = new();
        if (!x.TryGetValue(b, out var h)) x[b] = h = new();
        Histogram.Add(h, key);
    }

    /// <summary>4 輪が同じ路面なら、その路面のキー(paved・dirt・grass・snow)。混在・空中・不明は null</summary>
    public static string? SurfaceOf(double[][] rumble, int i)
    {
        string? s = null;
        foreach (var r in rumble)
        {
            string? w = DerivedSeries.ClassifySurface(r[i]) switch
            {
                SurfaceKind.Paved or SurfaceKind.Ramp or SurfaceKind.Kerb => "paved",
                SurfaceKind.Dirt => "dirt",
                SurfaceKind.Grass => "grass",
                SurfaceKind.Snow => "snow",
                _ => null,
            };
            if (w == null || (s != null && s != w)) return null;
            s = w;
        }
        return s;
    }

    /// <summary>
    /// 最後のギア(全開の行がいちばん高いギア)で、全開のまま 1 秒の速度の伸びが TopFlatKmhPerSec 未満で、
    /// リミッターで出力が切れていない所の最高速。無ければ null
    /// </summary>
    private static ProfileTop? FindTop(IReadOnlyList<int> rows, double[] tsMs, double[] gear, double[] accel, double[] speed, double[] rpm, double[] power)
    {
        var full = rows.Where(i => accel[i] >= PowerAnalysis.FullThrottle && gear[i] >= 1 && gear[i] <= 10).ToList();
        if (full.Count == 0) return null;
        int top = (int)full.Max(i => gear[i]);
        var g = full.Where(i => gear[i] == top).ToList();
        ProfileTop? best = null;
        int b = 0;
        for (int a = 0; a < g.Count; a++)
        {
            while (b < g.Count && tsMs[g[b]] - tsMs[g[a]] < 1000) b++;
            if (b >= g.Count) break;
            int j = g[b];
            if (tsMs[j] - tsMs[g[a]] > 1200) continue;   // 途中で全開をやめた
            double rise = (speed[j] - speed[g[a]]) * 3.6 / ((tsMs[j] - tsMs[g[a]]) / 1000);
            if (rise >= TopFlatKmhPerSec || power[j] <= 0 || power[g[a]] <= 0) continue;
            double kmh = speed[j] * 3.6;
            if (kmh < 100) continue;
            if (best == null || kmh > best.Kmh) best = new ProfileTop { Gear = top, Kmh = Math.Round(kmh, 1), Rpm = Math.Round(rpm[j]) };
        }
        return best;
    }
}

/// <summary>
/// 部分を合わせたプロファイル。limitFilter(部分, 限界の種類)は、限界(brake / corner / launch)に使う部分の選び方
/// (その限界に効く補助の設定が同じ部分だけなど。ForAssists)。パワーカーブ・リミッター・ギア比は、補助の設定によらず全部の部分を使う。
/// 限界には、レースの部分だけを使う(レースの外の移動や自動運転は限界まで攻めないので、旋回の限界が 0.1〜0.24G 低く出た。2026-10-17 の 19 台の集計)
/// </summary>
public sealed class CarProfile(CarProfileFile file, Func<ProfilePart, string, bool>? limitFilter = null)
{
    /// <summary>限界の種類によらず、同じ選び方をする</summary>
    public CarProfile(CarProfileFile file, Func<ProfilePart, bool> limitParts) : this(file, (p, _) => limitParts(p)) { }

    /// <summary>限界ごとに、その限界に効く補助の設定(AssistKey.For)が a と同じ部分を使う</summary>
    public static CarProfile ForAssists(CarProfileFile file, Assists? a) =>
        new(file, (p, kind) => AssistKey.For(p.Assists, kind) == AssistKey.For(a, kind));

    public CarProfileFile File { get; } = file;
    private IEnumerable<ProfilePart> LimitParts(string kind) => File.Parts.Where(p => p.RaceStartRecvTime != null && (limitFilter == null || limitFilter(p, kind)));

    /// <summary>限界として扱うのに要る行数(30fps で 2 秒)</summary>
    public const int MinLimitRows = 60;

    public PowerCurve? Curve()
    {
        var used = MergedPower().Where(kv => Histogram.Count(kv.Value) >= PowerAnalysis.MinBinRows).OrderBy(kv => kv.Key).ToList();
        if (used.Count < PowerAnalysis.MinBins) return null;
        return new PowerCurve(
            used.Select(kv => kv.Key + PowerAnalysis.BinRpm / 2).ToArray(),
            used.Select(kv => Math.Round(Histogram.Percentile(kv.Value, PowerAnalysis.CurvePercentile, 1)!.Value, 1)).ToArray());
    }

    /// <summary>{区切りの下の回転数: 出力の度数}(全部の部分を合わせたもの)</summary>
    public Dictionary<int, Dictionary<string, int>> MergedPower() =>
        File.Parts.SelectMany(p => p.Power).GroupBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => Histogram.Merge(g.Select(kv => kv.Value)));

    public double? LimiterRpm()
    {
        var h = Histogram.Merge(File.Parts.Select(p => p.RpmFull));
        return Histogram.Count(h) < PowerAnalysis.LimiterMinRows ? null : Math.Round(Histogram.Percentile(h, PowerAnalysis.LimiterPercentile, 25)!.Value);
    }

    /// <summary>ギアごとの「回転数 ÷ 速度」の中央値(行が MinGearRows 以上のギアだけ)</summary>
    public Dictionary<int, double> GearK() =>
        File.Parts.SelectMany(p => p.GearK).GroupBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture))
            .Select(g => (Gear: g.Key, H: Histogram.Merge(g.Select(kv => kv.Value))))
            .Where(x => Histogram.Count(x.H) >= PowerAnalysis.MinGearRows)
            .ToDictionary(x => x.Gear, x => Histogram.Percentile(x.H, 0.5, 0.5)!.Value);

    /// <summary>ギア比(次のギア ÷ 今のギア)</summary>
    public Dictionary<int, double> Ratios()
    {
        var k = GearK();
        return k.Where(kv => k.ContainsKey(kv.Key + 1)).ToDictionary(kv => kv.Key, kv => Math.Round(k[kv.Key + 1] / kv.Value, 4));
    }

    public SortedSet<int> GearsSeen() => new(File.Parts.SelectMany(p => p.GearsSeen));

    public ProfileTop? Top() => File.Parts.Select(p => p.Top).Where(x => x != null).OrderByDescending(x => x!.Kmh).FirstOrDefault();

    /// <summary>ギア × 500 rpm の行数</summary>
    public Dictionary<int, Dictionary<int, int>> Coverage() =>
        File.Parts.SelectMany(p => p.Coverage).GroupBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => Histogram.Merge(g.Select(kv => kv.Value)).ToDictionary(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value));

    /// <summary>限界の値: 中央値と行数(行が無ければ値は null)</summary>
    public readonly record struct Limit(double? G, int Rows)
    {
        public bool Measured => Rows >= MinLimitRows && G != null;
    }

    private static Limit LimitOf(Dictionary<string, int> h) => new(
        Histogram.Count(h) == 0 ? null : Math.Round(Histogram.Percentile(h, 0.5, ProfilePartBuilder.GStep)!.Value, 2), Histogram.Count(h));

    /// <summary>ブレーキの限界: 速度の区切り(50 km/h の下の値)ごと</summary>
    public SortedDictionary<int, Limit> Brake(string surface) => new(
        LimitParts("brake").Where(p => p.Brake.ContainsKey(surface)).SelectMany(p => p.Brake[surface])
            .GroupBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => LimitOf(Histogram.Merge(g.Select(kv => kv.Value)))));

    /// <summary>ブレーキの限界: 速度によらず全部を合わせたもの</summary>
    public Limit BrakeAll(string surface) => LimitOf(Histogram.Merge(
        LimitParts("brake").Where(p => p.Brake.ContainsKey(surface)).SelectMany(p => p.Brake[surface].Values)));

    public Limit Corner(string surface, string side) => LimitOf(Histogram.Merge(
        LimitParts("corner").Where(p => p.Corner.TryGetValue(surface, out var s) && s.ContainsKey(side)).Select(p => p.Corner[surface][side])));

    public Limit Launch(string surface) => LimitOf(Histogram.Merge(
        LimitParts("launch").Where(p => p.Launch.ContainsKey(surface)).Select(p => p.Launch[surface])));

    /// <summary>
    /// その路面をちゃんと走ったか(ブレーキ・旋回・発進の度数の行を合わせて MinLimitRows 以上)。
    /// 舗装のコースで少しはみ出した芝・ダートで、その路面の限界の指示を出さないため。限界と同じく、レースの部分だけで数える
    /// </summary>
    public bool HasSurface(string surface) =>
        File.Parts.Where(p => p.RaceStartRecvTime != null).Sum(p =>
            (p.Brake.TryGetValue(surface, out var b) ? b.Values.Sum(Histogram.Count) : 0)
            + (p.Corner.TryGetValue(surface, out var c) ? c.Values.Sum(Histogram.Count) : 0)
            + (p.Launch.TryGetValue(surface, out var l) ? Histogram.Count(l) : 0)) >= MinLimitRows;

    /// <summary>250 rpm の区切りの、部分ごとの出力の 75 パーセンタイル(その部分に MinBinRows 以上の行があるものだけ)</summary>
    public List<double> PartValues(int bin) =>
        File.Parts.Where(p => p.Power.TryGetValue(bin.ToString(CultureInfo.InvariantCulture), out var h) && Histogram.Count(h) >= PowerAnalysis.MinBinRows)
            .Select(p => Histogram.Percentile(p.Power[bin.ToString(CultureInfo.InvariantCulture)], PowerAnalysis.CurvePercentile, 1)!.Value).ToList();
}

/// <summary>
/// 補助の設定の組(限界を分けるため)。分からないものは "?"。
/// 限界ごとに効く補助だけで分ける(For): ブレーキ = ABS、旋回 = TC と STM、発進 = TC と LC。全部で分けると組が細かくなりすぎるため
/// </summary>
public static class AssistKey
{
    /// <summary>レースの補助の設定の組(画面で、レースと組を結ぶ)</summary>
    public static string Of(Assists? a) => $"abs={V(a?.Abs)},tcs={V(a?.Tcs)},stm={V(a?.Stm)},lc={V(a?.Lc)}";

    /// <summary>限界の種類(brake / corner / launch)に効く補助の設定の組</summary>
    public static string For(Assists? a, string kind) => kind switch
    {
        "brake" => $"abs={V(a?.Abs)}",
        "corner" => $"tcs={V(a?.Tcs)},stm={V(a?.Stm)}",
        "launch" => $"tcs={V(a?.Tcs)},lc={V(a?.Lc)}",
        _ => Of(a),
    };

    public static bool Known(Assists? a) => a?.Abs != null && a.Tcs != null;

    private static string V(string? v) => v ?? "?";
}

/// <summary>プロファイルのファイルの読み書き(置き場所のフォルダごと)</summary>
public sealed class ProfileStore(string folder)
{
    public string Folder { get; } = folder;

    public string PathOf(CarSignature car) => System.IO.Path.Combine(Folder, ProfileCar.KeyOf(car) + ".json");

    /// <summary>読む。無いか、形式が違うか、読めなければ空のプロファイル(作り直す)</summary>
    public CarProfileFile Load(CarSignature car)
    {
        var path = PathOf(car);
        try
        {
            if (System.IO.File.Exists(path))
            {
                var f = JsonSerializer.Deserialize<CarProfileFile>(System.IO.File.ReadAllText(path), SessionMeta.JsonOptions);
                if (f != null && f.Format == CarProfileFile.FormatId) return f;
            }
        }
        catch (Exception) { /* 壊れていたら作り直す */ }
        return new CarProfileFile { Car = ProfileCar.From(car, null) };
    }

    /// <summary>部分を足す(同じ記録・同じレースの部分は置き換える)</summary>
    public static void Upsert(CarProfileFile f, ProfilePart part)
    {
        f.Parts.RemoveAll(p => p.Folder == part.Folder && p.RaceStartRecvTime == part.RaceStartRecvTime);
        f.Parts.Add(part);
    }

    /// <summary>その記録・レースの部分が、今の csv から作ったものか(作り直さなくてよいか)</summary>
    public static bool IsFresh(CarProfileFile f, string folder, double? raceStart, long csvSize, string csvMtime) =>
        f.Parts.Any(p => p.Folder == folder && p.RaceStartRecvTime == raceStart && p.CsvSize == csvSize && p.CsvMtime == csvMtime);

    /// <summary>
    /// 記録のフォルダ名が変わったら、プロファイルの中の名前も直す(記録をフォルダ名で見分けるので、直さないと二重に足す)。
    /// 直したプロファイルの数
    /// </summary>
    public int RenameFolder(string oldName, string newName)
    {
        if (!Directory.Exists(Folder) || oldName == newName) return 0;
        int n = 0;
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            CarProfileFile? f;
            try { f = JsonSerializer.Deserialize<CarProfileFile>(System.IO.File.ReadAllText(path), SessionMeta.JsonOptions); }
            catch (Exception) { continue; }   // 壊れたものは開いたときに作り直す
            if (f == null || f.Format != CarProfileFile.FormatId) continue;
            var parts = f.Parts.Where(p => p.Folder == oldName).ToList();
            if (parts.Count == 0) continue;
            foreach (var p in parts) p.Folder = newName;
            Save(f);
            n++;
        }
        return n;
    }

    public void Save(CarProfileFile f)
    {
        Directory.CreateDirectory(Folder);
        f.UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(Folder, ProfileCar.KeyOf(new CarSignature(f.Car.Ordinal, f.Car.ClassRaw, f.Car.Pi, f.Car.DrivetrainRaw, f.Car.Cylinders)) + ".json");
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(f, SessionMeta.JsonOptions));
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>回転数の帯(500 rpm)の状態</summary>
public enum BandState { Enough, Short, Unused, Unreachable }

/// <summary>次の 1 本の指示の種類(文は画面で作る)</summary>
public enum AdviceKind { FirstRun, RpmBand, GearRows, Brake, Corner, Launch }

/// <summary>次の 1 本の指示。RpmBand は From〜To の帯を、Gear 速で Kmh(From rpm)から全開で。Corner は Side(left / right)</summary>
public sealed record Advice(AdviceKind Kind, int? From = null, int? To = null, int? Gear = null, double? Kmh = null, string? Surface = null, string? Side = null);

/// <summary>計測モードの判定: 帯ごとの状態、限界、次の 1 本の指示(docs/spec.md「足りている・使わない・届かないの決め方」)</summary>
public static class MeasurementEvaluator
{
    /// <summary>250 rpm の区切りが足りているとみなす行数</summary>
    public const int EnoughBinRows = 40;
    /// <summary>2 本の部分の値がこの割合以内でそろえば足りているとみなす</summary>
    public const double AgreeRatio = 0.01;
    /// <summary>「使わない」の下の線: いちばん下がる変速の後の回転数から、この幅だけ下</summary>
    public const int UnusedMarginRpm = 500;
    /// <summary>ギアと速度に直すとき、全開にしやすい速度の幅(km/h)</summary>
    public const double AdviceMinKmh = 50, AdviceMaxKmh = 130;

    public sealed record Band(int From, int To, int Rows, BandState State);

    public sealed record Result(List<Band> Bands, int? UsedFrom, double? Limiter, List<Advice> Advice);

    public static Result Evaluate(CarProfile p, IEnumerable<string>? surfaces = null)
    {
        var curve = p.Curve();
        var ratios = p.Ratios();
        var limiter = p.LimiterRpm();
        var power = p.MergedPower();
        var advice = new List<Advice>();

        // 使う回転数の下の線: 1→2 速の最適な範囲の下(無ければリミッター)で上げた後の回転数の 500 rpm 下
        int? usedFrom = null;
        if (curve != null && ratios.TryGetValue(1, out var r1))
        {
            double shiftAt = PowerAnalysis.OptimalRange(curve, r1, limiter)?.Lo ?? limiter ?? curve.Rpm[^1];
            usedFrom = (int)(Math.Floor((shiftAt * r1 - UnusedMarginRpm) / 500) * 500);
        }
        var bands = new List<Band>();
        if (limiter is double lim)
        {
            int top = (int)(Math.Ceiling(lim / 500) * 500);
            int start = usedFrom is int u ? Math.Min(u, 2000) : 2000;
            for (int b = Math.Max(1000, start); b < top; b += 500)
            {
                int rows = Rows(power, b) + Rows(power, b + 250);
                var state = b >= lim ? BandState.Unreachable
                    : usedFrom is int uf && b + 500 <= uf ? BandState.Unused
                    : Enough(p, power, b, lim) && (b + 250 >= lim || Enough(p, power, b + 250, lim)) ? BandState.Enough
                    : BandState.Short;
                bands.Add(new Band(b, b + 500, rows, state));
            }
        }

        // 指示 1: ギア比もパワーカーブも無ければ、まずふつうに 1 本
        if (curve == null || ratios.Count == 0)
            advice.Add(new Advice(AdviceKind.FirstRun));
        else
        {
            // 指示 2: 足りない帯(続いている帯はまとめる)を、ギアと速度に直す
            var k = p.GearK();
            foreach (var run in Runs(bands.Where(b => b.State == BandState.Short).ToList()))
            {
                var (gear, kmh) = PickGear(k, run.From);
                advice.Add(new Advice(AdviceKind.RpmBand, run.From, run.To, gear, kmh));
            }
        }
        // 指示 3: 使ったのにギア比を測れていないギア(全開の行が足りない)。いちばん上のギアは、上が無いかもしれないので除く
        var seen = p.GearsSeen();
        var known = p.GearK();
        foreach (var g in seen.Where(g => !known.ContainsKey(g) && g != seen.Max)) advice.Add(new Advice(AdviceKind.GearRows, Gear: g));

        // 指示 4: 限界(舗装はいつも。ほかの路面は走ったことがあれば)
        var list = (surfaces ?? new[] { "paved", "dirt", "grass", "snow" }).Where(s => s == "paved" || p.HasSurface(s));
        foreach (var s in list)
        {
            if (!p.BrakeAll(s).Measured) advice.Add(new Advice(AdviceKind.Brake, Surface: s));
            foreach (var side in new[] { "left", "right" })
                if (!p.Corner(s, side).Measured) advice.Add(new Advice(AdviceKind.Corner, Surface: s, Side: side));
            if (!p.Launch(s).Measured) advice.Add(new Advice(AdviceKind.Launch, Surface: s));
        }
        return new Result(bands, usedFrom, limiter, advice);
    }

    private static int Rows(Dictionary<int, Dictionary<string, int>> power, int bin) =>
        power.TryGetValue(bin, out var h) ? Histogram.Count(h) : 0;

    /// <summary>
    /// 250 rpm の区切りが足りているか: 行が EnoughBinRows 以上か、2 つ以上の部分の値が AgreeRatio 以内でそろう。
    /// リミッターを含む区切りは、カーブに使える行数(MinBinRows)でよい(リミッターの近くは出力が切れる行が多く、行が少なくなる)
    /// </summary>
    private static bool Enough(CarProfile p, Dictionary<int, Dictionary<string, int>> power, int bin, double limiter)
    {
        if (Rows(power, bin) >= (bin + 250 > limiter ? PowerAnalysis.MinBinRows : EnoughBinRows)) return true;
        var v = p.PartValues(bin);
        return v.Count >= 2 && (v.Max() - v.Min()) <= v.Max() * AgreeRatio;
    }

    private static IEnumerable<(int From, int To)> Runs(List<Band> shortBands)
    {
        int? from = null, to = null;
        foreach (var b in shortBands.OrderBy(b => b.From))
        {
            if (from != null && b.From == to) { to = b.To; continue; }
            if (from != null) yield return (from.Value, to!.Value);
            from = b.From; to = b.To;
        }
        if (from != null) yield return (from.Value, to!.Value);
    }

    /// <summary>その回転数で全開にしやすいギアと速度: 速度が AdviceMinKmh〜AdviceMaxKmh に入るいちばん上のギア。無ければ 90 km/h にいちばん近いギア</summary>
    public static (int Gear, double Kmh) PickGear(Dictionary<int, double> rpmPerMps, int rpm)
    {
        var options = rpmPerMps.Select(kv => (Gear: kv.Key, Kmh: rpm / kv.Value * 3.6)).ToList();
        var inRange = options.Where(o => o.Kmh >= AdviceMinKmh && o.Kmh <= AdviceMaxKmh).OrderByDescending(o => o.Gear).ToList();
        var pick = inRange.Count > 0 ? inRange[0] : options.OrderBy(o => Math.Abs(o.Kmh - 90)).First();
        return (pick.Gear, Math.Round(pick.Kmh / 5) * 5);
    }
}
