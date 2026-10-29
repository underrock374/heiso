using System.Globalization;
using System.Text.Json.Nodes;

namespace Fh6.Core;

/// <summary>一覧に出す車(車種ごと。PI・クラス・セッティングが違っても 1 つ)</summary>
public sealed record SessionCar(int Ordinal, string? Name);

/// <summary>一覧の 1 行(session.json だけから作る。テレメトリーは読まない)</summary>
public sealed class SessionSummary
{
    public string Folder { get; init; } = "";
    /// <summary>フォルダ名</summary>
    public string Name { get; init; } = "";
    /// <summary>フォルダ名の先頭の日時(yyyyMMdd_HHmmss)。無ければ ""</summary>
    public string Stamp { get; init; } = "";
    /// <summary>フォルダ名の日時より後ろ(先頭の _ を除く)</summary>
    public string Rest { get; init; } = "";
    public DateTime? Started { get; init; }
    public bool Complete { get; init; }
    public double? DurationSec { get; init; }
    public List<string> Courses { get; init; } = new();
    public List<SessionCar> Cars { get; init; } = new();
    public int Races { get; init; }
    public bool HasVideo { get; init; }
    public long Bytes { get; init; }
    /// <summary>session.json を最後に書いた時刻(UTC。記録アプリが止めた後に追記するのを見分ける)</summary>
    public DateTime MetaWrittenUtc { get; init; }
    /// <summary>session.json が読めなかったときの理由</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 記録の一覧とアーカイブ(docs/spec.md「記録の一覧とアーカイブ」)。保存先の中の記録を一覧にし、
/// 保存先の archive へフォルダごと移す・戻す、フォルダ名の日時より後ろを変える
/// </summary>
public static class SessionCatalog
{
    public const string ArchiveDirName = "archive";

    /// <summary>止めた後も記録アプリが session.json に追記するので、いちばん新しい記録はこの時間は移さない</summary>
    public static readonly TimeSpan RecentLimit = TimeSpan.FromMinutes(30);

    /// <summary>記録中は 30 秒ごとに session.json を書くので、これより新しければ記録中とみなす(complete が false のとき)</summary>
    public static readonly TimeSpan RecordingLimit = TimeSpan.FromMinutes(2);

    /// <summary>フォルダ名を日時と、それより後ろ(間の記号を除く。SessionFolderName)に分ける。日時で始まらなければ日時は ""</summary>
    public static (string Stamp, string Tail) SplitName(string name) =>
        SessionFolderName.TryParse(name, out _, out var comment) ? (name[..SessionFolderName.TimestampFormat.Length], comment ?? "") : ("", name);

    /// <summary>記録の保存先(記録の 1 つ上。アーカイブの中の記録なら、その上)</summary>
    public static string SaveRootOf(string sessionFolder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(sessionFolder).TrimEnd('\\', '/'))!;
        return string.Equals(Path.GetFileName(parent), ArchiveDirName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent) ?? parent
            : parent;
    }

    /// <summary>保存先(またはアーカイブ)の中の、session.json がある記録を新しい順に</summary>
    public static List<SessionSummary> List(string dir, Func<int, string?>? registeredCarName = null)
    {
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateDirectories(dir)
            .Where(d => File.Exists(Path.Combine(d, SessionMeta.FileName)))
            .Select(d => Summarize(d, registeredCarName))
            .OrderByDescending(s => s.Started ?? DateTime.MinValue)
            .ThenByDescending(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static SessionSummary Summarize(string folder, Func<int, string?>? registeredCarName = null)
    {
        var name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
        var (stamp, rest) = SplitName(name);
        DateTime? started = SessionFolderName.TryParse(name, out var st, out _) ? st : null;
        long bytes = 0;
        try { bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { /* 大きさが分からなくても一覧には出す */ }
        var metaPath = Path.Combine(folder, SessionMeta.FileName);
        var written = File.Exists(metaPath) ? File.GetLastWriteTimeUtc(metaPath) : DateTime.MinValue;
        SessionMeta meta;
        try { meta = SessionMeta.Load(folder); }
        catch (Exception ex)
        {
            return new SessionSummary { Folder = folder, Name = name, Stamp = stamp, Rest = rest, Started = started, Bytes = bytes, MetaWrittenUtc = written, Error = ex.Message };
        }
        if (started == null && DateTimeOffset.TryParse(meta.Session.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sa)) started = sa.LocalDateTime;

        var races = meta.Events.Where(e => e.Type == "race_start").ToList();
        var courses = races.Select(r => RaceNames.Course(meta.Events, r.RecvTime)).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct().ToList();
        // 車は車種(ordinal)ごと。名前は登録ファイル → 記録の中の名前
        var cars = new List<SessionCar>();
        foreach (var e in meta.Events.Where(e => e.Type is "car_detected" or "car_confirmed"))
        {
            if (e.Data?["ordinal"] is not JsonValue ov || !ov.TryGetValue<int>(out var ordinal) || ordinal <= 0) continue;
            string? evName = e.Data["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && !string.IsNullOrEmpty(n) ? n : null;
            int k = cars.FindIndex(c => c.Ordinal == ordinal);
            if (k < 0) cars.Add(new SessionCar(ordinal, evName));
            else if (evName != null) cars[k] = cars[k] with { Name = evName };
        }
        if (registeredCarName != null)
            cars = cars.Select(c => registeredCarName(c.Ordinal) is { Length: > 0 } rn ? c with { Name = rn } : c).ToList();
        double? duration = meta.Session.EndedRecvTime is double end && meta.Session.StartedRecvTime > 0 ? end - meta.Session.StartedRecvTime : null;
        string? video = null;
        try { video = meta.FindVideo(folder); }
        catch (Exception) { /* 動画が無い扱い */ }
        return new SessionSummary
        {
            Folder = folder, Name = name, Stamp = stamp, Rest = rest, Started = started, Complete = meta.Complete,
            DurationSec = duration, Courses = courses, Cars = cars, Races = races.Count, HasVideo = video != null,
            Bytes = bytes, MetaWrittenUtc = written,
        };
    }

    /// <summary>
    /// 移す・名前を変えることができない理由(できるなら null)。openFolder は今開いている記録、
    /// newest は保存先でいちばん新しい記録か
    /// </summary>
    public static string? CannotChange(SessionSummary s, string? openFolder, bool newest, DateTime nowUtc)
    {
        if (openFolder != null && SamePath(s.Folder, openFolder)) return Strings.T("今開いている記録です");
        if (!s.Complete && s.Error == null && nowUtc - s.MetaWrittenUtc < RecordingLimit) return Strings.T("記録中です");
        if (newest && nowUtc - s.MetaWrittenUtc < RecentLimit) return Strings.T("止めてから 30 分たっていません(記録アプリが追記することがあるため)");
        return null;
    }

    /// <summary>フォルダ名の日時より後ろとして使えるか(使えないなら理由)</summary>
    public static string? CheckRest(string rest)
    {
        if (rest.Length > 120) return Strings.T("名前が長すぎます");
        if (rest.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Strings.T("フォルダ名に使えない文字があります");
        if (rest.EndsWith('.') || rest.EndsWith(' ')) return Strings.T("名前の最後に、点や空白は使えません");
        return null;
    }

    /// <summary>フォルダごと destDir へ移す。移した先のパス。同じ名前があれば IOException</summary>
    public static string Move(string folder, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/')));
        if (Directory.Exists(dest) || File.Exists(dest)) throw new IOException(Strings.T("移す先に同じ名前の記録があります"));
        Directory.Move(folder, dest);
        return dest;
    }

    /// <summary>フォルダ名の日時より後ろを rest にする(日時は変えない)。変えた後のパス</summary>
    public static string Rename(string folder, string rest)
    {
        rest = rest.Trim();
        if (CheckRest(rest) is { } reason) throw new IOException(reason);
        var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var (stamp, _) = SplitName(Path.GetFileName(full));
        var name = stamp == "" ? rest : rest == "" ? stamp : stamp + "_" + rest;
        if (name == "") throw new IOException(Strings.T("名前を入れてください"));
        var dest = Path.Combine(Path.GetDirectoryName(full)!, name);
        if (SamePath(dest, full)) return full;
        if (Directory.Exists(dest) || File.Exists(dest)) throw new IOException(Strings.T("同じ名前の記録があります"));
        Directory.Move(full, dest);
        return dest;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
