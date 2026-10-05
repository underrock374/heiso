using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Core;

/// <summary>
/// 登録ファイル fh6_registry.json(車名・セッティング名・コース)。ロガー v3 と同じ形式・同じ名前で、v3 のファイルをそのまま読める。
/// 知らないキーは消さずに残す。スレッドセーフではない(使う側で排他する)。
/// </summary>
public sealed class Registry
{
    public const string FileName = "fh6_registry.json";

    public int RegistryVersion { get; set; } = 1;
    public Dictionary<string, RegisteredCar> Cars { get; set; } = new();
    public List<RegisteredSetup> Setups { get; set; } = new();
    public List<RegisteredCourse> Courses { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(SessionMeta.JsonOptions)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 読む。無ければ空。読めない・壊れているときは元のファイルに .broken を付けて残し、空として扱う(warning に理由)
    /// </summary>
    public static Registry Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new Registry();
        try
        {
            var r = JsonSerializer.Deserialize<Registry>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException(Strings.T("中身が空です"));
            r.Cars ??= new();
            r.Setups ??= new();
            r.Courses ??= new();
            return r;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            var broken = path + ".broken";
            if (File.Exists(broken)) broken = $"{path}.{DateTime.Now:yyyyMMdd_HHmmss}.broken";
            try { File.Move(path, broken); } catch { broken = path; }
            warning = Strings.T("登録ファイルを読めないため、新しく作ります(元のファイルは {file}): {error}", ("file", Path.GetFileName(broken)), ("error", ex.Message));
            return new Registry();
        }
    }

    /// <summary>読むだけ(再生アプリ用)。無い・読めないときは null。Load と違い、壊れたファイルの名前は変えない(直すのは記録アプリ)</summary>
    public static Registry? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Registry>(fs, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>一時ファイルに書いてから置き換える</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

    // ---- 車両 ----

    public string? CarName(int ordinal) => Cars.TryGetValue(ordinal.ToString(), out var c) ? c.Name : null;

    public void SetCarName(int ordinal, string name) =>
        Cars[ordinal.ToString()] = Cars.TryGetValue(ordinal.ToString(), out var c)
            ? c with { Name = name, Updated = Now() }
            : new RegisteredCar { Name = name, Updated = Now() };

    public List<RegisteredSetup> FindSetups(CarSignature sig) => Setups.Where(s => s.Matches(sig)).ToList();

    public RegisteredSetup? SetupById(string id) => Setups.FirstOrDefault(s => s.Id == id);

    public RegisteredSetup AddSetup(CarSignature sig, string name, string? note)
    {
        var s = new RegisteredSetup
        {
            Id = NextId("S", Setups.Select(x => x.Id)),
            Ordinal = sig.Ordinal, Cls = sig.Class, Pi = sig.Pi, Drive = sig.Drivetrain, Cyl = sig.Cylinders,
            Variant = FindSetups(sig).Count + 1,
            Name = name, Note = note, Created = Now(),
        };
        Setups.Add(s);
        return s;
    }

    // ---- コース ----

    public RegisteredCourse? CourseById(string id) => Courses.FirstOrDefault(c => c.Id == id);

    public RegisteredCourse AddCourse(string name, string? kind, double x, double z, double yaw, string? note)
    {
        var c = new RegisteredCourse
        {
            Id = NextId("R", Courses.Select(x => x.Id)),
            Name = name, Kind = kind,
            X = Math.Round(x, 2), Z = Math.Round(z, 2), Yaw = Math.Round(yaw, 5),
            Note = note, Created = Now(), Seen = 1,
        };
        Courses.Add(c);
        return c;
    }

    /// <summary>
    /// レース開始の位置と向きを登録済みのコースと照合する(CourseMatcher)。一致したら見た回数を数え、
    /// より前方(1 番グリッドに近い)スタートなら基準点をそこへ動かす。登録ファイルの保存は呼ぶ側で行う
    /// </summary>
    public CourseMatch? MatchAndUpdate(double x, double z, double yaw)
    {
        var m = CourseMatcher.Match(Courses, x, z, yaw);
        if (m is not CourseMatch hit) return null;
        hit.Course.Seen = (hit.Course.Seen ?? 0) + 1;
        if (hit.AlongM > CourseMatcher.MoveForwardM)
        {
            hit.Course.X = Math.Round(x, 2);
            hit.Course.Z = Math.Round(z, 2);
        }
        return hit;
    }

    /// <summary>v3 と同じ「S0001」形式。v3 は件数 + 1 で振っていたが、消した後でも重ならないよう最大の番号 + 1 にする</summary>
    private static string NextId(string prefix, IEnumerable<string> ids)
    {
        int max = 0;
        foreach (var id in ids)
            if (id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(prefix.Length), out var n))
                max = Math.Max(max, n);
        return $"{prefix}{max + 1:D4}";
    }
}

public sealed record RegisteredCar
{
    public string Name { get; set; } = "";
    public string? Updated { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RegisteredSetup
{
    public string Id { get; set; } = "";
    public int Ordinal { get; set; }
    public int Cls { get; set; }
    public int Pi { get; set; }
    public int Drive { get; set; }
    public int Cyl { get; set; }
    public int Variant { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public string? Created { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool Matches(CarSignature s) =>
        Ordinal == s.Ordinal && Cls == s.Class && Pi == s.Pi && Drive == s.Drivetrain && Cyl == s.Cylinders;
}

public sealed class RegisteredCourse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>road / dirt / xc / street / drag / other</summary>
    public string? Kind { get; set; }
    public double X { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public string? Note { get; set; }
    public string? Created { get; set; }
    public int? Seen { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>コースの照合の結果。AlongM は登録済みのスタート地点から進行方向へのずれ(負ならグリッドの後方)</summary>
public readonly record struct CourseMatch(RegisteredCourse Course, double AlongM, double LateralM)
{
    /// <summary>グリッドの後方への距離(v3 の grid_offset_m)</summary>
    public double GridOffsetM => Math.Round(-AlongM, 1);
}

/// <summary>
/// スタート地点と向きでコースを照合する(ロガー v3 の match_course と同じ)。
/// スタート地点は日・車種が違っても 0.1m 以内で一致し、グリッドが後ろほど進行方向の真後ろにずれる(8 番手で約 71m)。
/// </summary>
public static class CourseMatcher
{
    public const double MaxAlongM = 110;
    public const double MaxLateralM = 15;
    public const double MaxYawDeg = 20;
    /// <summary>横のずれは進行方向のずれの 3 倍に数える</summary>
    public const double LateralWeight = 3;
    /// <summary>基準点より前方にこれを超えてずれたスタートを見たら、基準点をそこへ動かす</summary>
    public const double MoveForwardM = 3;

    public static CourseMatch? Match(IEnumerable<RegisteredCourse> courses, double x, double z, double yaw)
    {
        CourseMatch? best = null;
        double bestScore = double.MaxValue;
        foreach (var c in courses)
        {
            // FH6 の yaw は左手系で、前方ベクトルは (sin yaw, cos yaw)
            var dyaw = Math.Abs(Mod(RadToDeg(yaw - c.Yaw) + 180, 360) - 180);
            if (dyaw > MaxYawDeg) continue;
            double fx = Math.Sin(c.Yaw), fz = Math.Cos(c.Yaw);
            double dx = x - c.X, dz = z - c.Z;
            double along = dx * fx + dz * fz;
            double lat = dx * fz - dz * fx;
            if (Math.Abs(along) > MaxAlongM || Math.Abs(lat) > MaxLateralM) continue;
            var score = Math.Abs(along) + LateralWeight * Math.Abs(lat);
            if (score < bestScore)
            {
                best = new CourseMatch(c, along, lat);
                bestScore = score;
            }
        }
        return best;
    }

    private static double RadToDeg(double r) => r * 180 / Math.PI;

    /// <summary>Python の % と同じく、結果が常に 0 以上</summary>
    private static double Mod(double a, double m) => a - m * Math.Floor(a / m);
}
