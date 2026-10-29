using System.IO;
using System.Text.Json;
using Fh6.Core;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace Fh6.Player;

// 記録の一覧とアーカイブ(docs/spec.md「記録の一覧とアーカイブ」)
public partial class MainWindow
{
    /// <summary>一覧に出している保存先</summary>
    private string? _sessionsRoot;

    /// <summary>画面からのメッセージのうち、記録の一覧のもの。扱ったら true</summary>
    private bool OnSessionsMessage(string? type, JsonElement msg)
    {
        bool archive = msg.TryGetProperty("archive", out var ar) && ar.ValueKind == JsonValueKind.True;
        string? Str(string key) => msg.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (type)
        {
            case "sessionList":
                _ = SendSessionListAsync(archive);
                return true;
            case "sessionPickRoot":
                _ = Dispatcher.InvokeAsync(() => PickSessionsRootAsync(archive));   // 閉じるまで待つ画面は、メッセージの処理を終えてから
                return true;
            case "sessionOpen":
                if (Str("path") is { Length: > 0 } open) _ = Dispatcher.InvokeAsync(() => OpenAsync(open));
                return true;
            case "sessionMove":
                {
                    var paths = msg.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array
                        ? ps.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList()
                        : new List<string>();
                    _ = MoveSessionsAsync(paths, toArchive: !archive);
                }
                return true;
            case "sessionRename":
                if (Str("path") is { Length: > 0 } path) _ = RenameSessionAsync(path, Str("rest") ?? "", archive);
                return true;
        }
        return false;
    }

    /// <summary>保存先: 選び直したもの → 前回開いた記録の保存先</summary>
    private string? SessionsRoot() => _sessionsRoot is { } r && Directory.Exists(r) ? r : _settings.InitialFolder();

    private async Task SendSessionListAsync(bool archive, object? result = null)
    {
        var root = SessionsRoot();
        if (root == null)
        {
            Post(new { type = "sessionList", root = (string?)null, archive, items = Array.Empty<object>(), result });
            return;
        }
        var dir = archive ? Path.Combine(root, SessionCatalog.ArchiveDirName) : root;
        var openFolder = _session?.Folder;
        var items = await Task.Run(() =>
        {
            var reg = Registry.TryRead(Path.Combine(root, Registry.FileName)) ?? new Registry();
            var list = SessionCatalog.List(dir, reg.CarName);
            var now = DateTime.UtcNow;
            // 止めてすぐに記録アプリが追記するのは、保存先のいちばん新しい記録だけ
            return list.Select((s, i) => new
            {
                path = s.Folder, name = s.Name, stamp = s.Stamp, rest = s.Rest,
                started = s.Started?.ToString("yyyy-MM-dd HH:mm"),
                complete = s.Complete, error = s.Error, duration = s.DurationSec, courses = s.Courses,
                cars = s.Cars.Select(c => new { ordinal = c.Ordinal, name = c.Name }), races = s.Races, video = s.HasVideo, bytes = s.Bytes,
                blocked = SessionCatalog.CannotChange(s, openFolder, newest: !archive && i == 0, now),
                open = openFolder != null && string.Equals(Path.GetFullPath(s.Folder), Path.GetFullPath(openFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
            }).ToList();
        });
        PlayerLog.Info($"記録の一覧: {dir}({items.Count} 本)");
        Post(new { type = "sessionList", root, archive, items, result });
    }

    private async Task PickSessionsRootAsync(bool archive)
    {
        if (_choosing) return;
        _choosing = true;
        try
        {
            var dlg = new OpenFolderDialog { Title = Strings.T("記録の保存先を選んでください"), InitialDirectory = SessionsRoot() ?? "" };
            if (dlg.ShowDialog(this) != true) return;
            // アーカイブの中を選んだら、その上を保存先にする
            var chosen = Path.GetFullPath(dlg.FolderName).TrimEnd('\\', '/');
            _sessionsRoot = string.Equals(Path.GetFileName(chosen), SessionCatalog.ArchiveDirName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(chosen) ?? chosen : chosen;
            _settings.LastRoot = _sessionsRoot;
            _settings.Save();
        }
        finally
        {
            _choosing = false;
        }
        await SendSessionListAsync(archive);
    }

    /// <summary>保存先 ⇄ アーカイブへ移す。移せない記録は理由を返し、ほかの記録だけ移す</summary>
    private async Task MoveSessionsAsync(List<string> paths, bool toArchive)
    {
        var root = SessionsRoot();
        if (root == null || paths.Count == 0) return;
        var archiveDir = Path.Combine(root, SessionCatalog.ArchiveDirName);
        var openFolder = _session?.Folder;
        var (moved, errors) = await Task.Run(() =>
        {
            var newest = toArchive ? SessionCatalog.List(root).FirstOrDefault()?.Folder : null;
            var errs = new List<object>();
            int n = 0;
            foreach (var p in paths)
            {
                var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                try
                {
                    var s = SessionCatalog.Summarize(p);
                    bool isNewest = newest != null && string.Equals(Path.GetFullPath(newest), Path.GetFullPath(p).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                    if (SessionCatalog.CannotChange(s, openFolder, isNewest, DateTime.UtcNow) is { } why) { errs.Add(new { name, reason = why }); continue; }
                    var dest = SessionCatalog.Move(p, toArchive ? archiveDir : root);
                    PlayerLog.Info($"記録を移した: {p} → {dest}");
                    n++;
                }
                catch (Exception ex)
                {
                    PlayerLog.Error($"記録を移せない: {p}", ex);
                    errs.Add(new { name, reason = ex.Message });
                }
            }
            return (n, errs);
        });
        await SendSessionListAsync(archive: !toArchive, new { kind = toArchive ? "archive" : "restore", moved, errors });
    }

    /// <summary>フォルダ名の日時より後ろを変え、車のプロファイルの中の名前も直す</summary>
    private async Task RenameSessionAsync(string path, string rest, bool archive)
    {
        var openFolder = _session?.Folder;
        var root = SessionsRoot();
        var (renamed, error) = await Task.Run<(string? Name, string? Error)>(() =>
        {
            try
            {
                var s = SessionCatalog.Summarize(path);
                var newest = !archive && root != null ? SessionCatalog.List(root).FirstOrDefault()?.Folder : null;
                bool isNewest = newest != null && string.Equals(Path.GetFullPath(newest), Path.GetFullPath(path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                if (SessionCatalog.CannotChange(s, openFolder, isNewest, DateTime.UtcNow) is { } why) return (null, why);
                var dest = SessionCatalog.Rename(path, rest);
                var (oldName, newName) = (Path.GetFileName(path.TrimEnd('\\', '/')), Path.GetFileName(dest));
                int n = Profiles.RenameFolder(oldName, newName);
                PlayerLog.Info($"記録の名前を変えた: {oldName} → {newName}(車のプロファイル {n} 件を直した)");
                return (newName, null);
            }
            catch (Exception ex)
            {
                PlayerLog.Error($"記録の名前を変えられない: {path}", ex);
                return (null, ex.Message);
            }
        });
        await SendSessionListAsync(archive, new { kind = "rename", renamed, error });
    }
}
