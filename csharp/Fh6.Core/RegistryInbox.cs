using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Core;

/// <summary>
/// 登録の受け渡し用のファイル fh6_registry_inbox.json(保存先フォルダの直下。登録ファイル fh6_registry.json の隣)。
/// 再生アプリはレースに付けた名前を、登録ファイルを直接書き換えずにここへ足す(記録アプリが起動中に登録ファイルを読み書きしているため)。
/// 記録アプリは起動時と動いている間に、ここの頼みを登録ファイルに取り込んで(Apply)、ファイルを消す。
/// 形式は docs/session-format.md「登録の受け渡し用のファイル」
/// </summary>
public sealed class RegistryInbox
{
    public const string FileName = "fh6_registry_inbox.json";

    public List<RegistryRequest> Requests { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>頼みを 1 つ足す(読んで足して、一時ファイルに書いてから置き換える)</summary>
    public static void Append(string saveFolder, RegistryRequest request)
    {
        var path = Path.Combine(saveFolder, FileName);
        var inbox = Read(path) ?? new RegistryInbox();
        inbox.Requests.Add(request);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(inbox, SessionMeta.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 取り込むために全部取り出す。先に名前を変えてから読む(読んでいる間に再生アプリが足しても、新しいファイルになって失われない)。
    /// 無ければ空。読めなければ、ファイルを .broken にして空
    /// </summary>
    public static List<RegistryRequest> Take(string saveFolder, out string? warning)
    {
        warning = null;
        var path = Path.Combine(saveFolder, FileName);
        if (!File.Exists(path)) return new();
        var taking = path + ".taking";
        try { File.Move(path, taking, overwrite: true); }
        catch (IOException) { return new(); }   // 再生アプリが書いている途中: 次の機会に
        try
        {
            var inbox = Read(taking) ?? new RegistryInbox();
            File.Delete(taking);
            return inbox.Requests;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var broken = $"{path}.{DateTime.Now:yyyyMMdd_HHmmss}.broken";
            try { File.Move(taking, broken); } catch { }
            warning = Strings.T("登録の受け渡し用のファイルを読めませんでした({file} に残しました): {error}", ("file", Path.GetFileName(broken)), ("error", ex.Message));
            return new();
        }
    }

    private static RegistryInbox? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<RegistryInbox>(File.ReadAllText(path), SessionMeta.JsonOptions) : null;

    /// <summary>
    /// 頼みを登録ファイルに取り込む(記録アプリの「直前のレース」の直し方と同じ決まり)。何を変えたかを返す(何も変えなければ空)。
    /// - コース: course_id があればそのコース(名前が違えば名前を変える)。"new" なら新しく登録するが、スタートの位置で一致する同じ名前のコースがあれば登録しない
    /// - 車名: その車種の名前にする
    /// - セッティング: setup_id があればそのセッティング(名前が違えば変える)。"new" なら新しく登録するが、同じ組(車種・クラス・PI・駆動・気筒)で同じ名前があれば登録しない
    /// 見つからない id は飛ばす(消されたなど)
    /// </summary>
    public static List<string> Apply(Registry reg, RegistryRequest r)
    {
        var changes = new List<string>();
        var courseName = r.CourseName?.Trim();
        if (r.CourseId == NewId)
        {
            if (!string.IsNullOrEmpty(courseName) && r.StartX is double x && r.StartZ is double z && r.StartYaw is double yaw)
            {
                var hit = CourseMatcher.Match(reg.Courses, x, z, yaw);
                if (hit is CourseMatch m && m.Course.Name == courseName) { /* 同じコースが登録済み */ }
                else
                {
                    reg.AddCourse(courseName, r.CourseKind, x, z, yaw, null);
                    changes.Add(Strings.T("コースを登録: {name}", ("name", courseName)));
                }
            }
        }
        else if (!string.IsNullOrEmpty(r.CourseId) && reg.CourseById(r.CourseId) is RegisteredCourse c
                 && !string.IsNullOrEmpty(courseName) && c.Name != courseName)
        {
            changes.Add(Strings.T("コース名を変更: {old} → {name}", ("old", c.Name), ("name", courseName)));
            c.Name = courseName;
        }

        if (r.CarOrdinal is int ordinal && ordinal > 0)
        {
            var carName = r.CarName?.Trim();
            if (!string.IsNullOrEmpty(carName) && reg.CarName(ordinal) != carName)
            {
                reg.SetCarName(ordinal, carName);
                changes.Add(Strings.T("車名: {name}", ("name", carName)));
            }
            var setupName = r.SetupName?.Trim();
            var sig = new CarSignature(ordinal, r.ClassRaw ?? 0, r.Pi ?? 0, r.DrivetrainRaw ?? 0, r.Cylinders ?? 0);
            if (r.SetupId == NewId)
            {
                if (!string.IsNullOrEmpty(setupName) && !reg.FindSetups(sig).Any(s => s.Name == setupName))
                {
                    reg.AddSetup(sig, setupName, null);
                    changes.Add(Strings.T("セッティングを登録: {name}", ("name", setupName)));
                }
            }
            else if (!string.IsNullOrEmpty(r.SetupId) && reg.SetupById(r.SetupId) is RegisteredSetup s
                     && !string.IsNullOrEmpty(setupName) && s.Name != setupName)
            {
                changes.Add(Strings.T("セッティング名を変更: {old} → {name}", ("old", s.Name), ("name", setupName)));
                s.Name = setupName;
            }
        }
        return changes;
    }

    /// <summary>「新しく登録する」を表す course_id・setup_id の値</summary>
    public const string NewId = "new";
}

/// <summary>
/// 登録の頼み 1 つ(1 つのレースに付けた名前)。コースは course_id(登録済みの id か "new")と名前・種類・スタートの位置、
/// 車は車両の組と車名、セッティングは setup_id(登録済みの id か "new")と名前
/// </summary>
public sealed class RegistryRequest
{
    public string CreatedAt { get; set; } = "";
    /// <summary>頼んだアプリ("player")</summary>
    public string Source { get; set; } = "player";
    /// <summary>どの記録のどのレースについてか(確認用)</summary>
    public string? Session { get; set; }
    public double? RaceStartRecvTime { get; set; }

    public string? CourseId { get; set; }
    public string? CourseName { get; set; }
    public string? CourseKind { get; set; }
    public double? StartX { get; set; }
    public double? StartZ { get; set; }
    public double? StartYaw { get; set; }

    public int? CarOrdinal { get; set; }
    public int? ClassRaw { get; set; }
    public int? Pi { get; set; }
    public int? DrivetrainRaw { get; set; }
    public int? Cylinders { get; set; }
    public string? CarName { get; set; }
    public string? SetupId { get; set; }
    public string? SetupName { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
