using System.IO;
using Fh6.Core;

namespace Fh6.Player;

/// <summary>
/// 記録の一覧とアーカイブ(docs/spec.md「記録の一覧とアーカイブ」)。保存先の記録を一覧にし、アーカイブへ移す・戻す、名前を変える。
/// openFolder は今開いている記録のフォルダ(開いている記録は移せない)
/// </summary>
public sealed class SessionsViewModel(IShell shell, PlayerSettings settings, ProfileStore profiles, Func<string?> openFolder, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>一覧に出している保存先(選び直したもの)</summary>
    private string? _root;

    /// <summary>画面からのメッセージのうち、記録の一覧のものを扱う。扱わないメッセージなら null</summary>
    public Task? Handle(ViewMessage msg) => msg switch
    {
        ViewSessionList m => SendListAsync(m.Archive == true),
        ViewSessionPickRoot m => PickRootAsync(m.Archive == true),
        ViewSessionMove m => MoveAsync((m.Paths ?? []).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList(), toArchive: m.Archive != true),
        ViewSessionRename { Path: { Length: > 0 } path } m => RenameAsync(path, m.Rest ?? "", m.Archive == true),
        _ => null,
    };

    /// <summary>保存先: 選び直したもの → 前回開いた記録の保存先</summary>
    public string? Root() => _root is { } r && Directory.Exists(r) ? r : settings.InitialFolder();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task SendListAsync(bool archive, SessionListResult? result = null)
    {
        var root = Root();
        if (root == null)
        {
            shell.Post(new HostSessionList(null, archive, Array.Empty<SessionListItem>(), result));
            return;
        }
        var dir = archive ? Path.Combine(root, SessionCatalog.ArchiveDirName) : root;
        var open = openFolder();
        var now = Now;
        var items = await Task.Run(() =>
        {
            var reg = Registry.TryRead(Path.Combine(root, Registry.FileName)) ?? new Registry();
            var list = SessionCatalog.List(dir, reg.CarName);
            // 止めてすぐに記録アプリが追記するのは、保存先のいちばん新しい記録だけ
            return list.Select((s, i) => new SessionListItem(
                Path: s.Folder, Name: s.Name, Stamp: s.Stamp, Rest: s.Rest,
                Started: s.Started?.ToString("yyyy-MM-dd HH:mm"),
                Complete: s.Complete, Error: s.Error, Duration: s.DurationSec, Courses: s.Courses.ToArray(),
                Cars: s.Cars.ToArray(), Races: s.Races, Video: s.HasVideo, Bytes: s.Bytes,
                Blocked: SessionCatalog.CannotChange(s, open, newest: !archive && i == 0, now),
                Open: open != null && SamePath(s.Folder, open)
            )).ToArray();
        });
        PlayerLog.Info($"記録の一覧: {dir}({items.Length} 本)");
        shell.Post(new HostSessionList(root, archive, items, result));
    }

    private async Task PickRootAsync(bool archive)
    {
        var picked = await shell.PickFolderAsync(Strings.T("記録の保存先を選んでください"), Root());
        if (picked == null) return;
        // アーカイブの中を選んだら、その上を保存先にする
        var chosen = Path.GetFullPath(picked).TrimEnd('\\', '/');
        _root = string.Equals(Path.GetFileName(chosen), SessionCatalog.ArchiveDirName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(chosen) ?? chosen : chosen;
        settings.LastRoot = _root;
        settings.Save();
        await SendListAsync(archive);
    }

    /// <summary>保存先 ⇄ アーカイブへ移す。移せない記録は理由を返し、ほかの記録だけ移す</summary>
    private async Task MoveAsync(List<string> paths, bool toArchive)
    {
        var root = Root();
        if (root == null || paths.Count == 0) return;
        var archiveDir = Path.Combine(root, SessionCatalog.ArchiveDirName);
        var open = openFolder();
        var now = Now;
        var (moved, errors) = await Task.Run(() =>
        {
            var newest = toArchive ? SessionCatalog.List(root).FirstOrDefault()?.Folder : null;
            var errs = new List<MoveError>();
            int n = 0;
            foreach (var p in paths)
            {
                var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                try
                {
                    var s = SessionCatalog.Summarize(p);
                    bool isNewest = newest != null && SamePath(newest, p);
                    if (SessionCatalog.CannotChange(s, open, isNewest, now) is { } why) { errs.Add(new MoveError(name, why)); continue; }
                    var dest = SessionCatalog.Move(p, toArchive ? archiveDir : root);
                    PlayerLog.Info($"記録を移した: {p} → {dest}");
                    n++;
                }
                catch (Exception ex)
                {
                    PlayerLog.Error($"記録を移せない: {p}", ex);
                    errs.Add(new MoveError(name, ex.Message));
                }
            }
            return (n, errs);
        });
        await SendListAsync(archive: !toArchive, new SessionListResult(toArchive ? "archive" : "restore", moved, errors.ToArray(), null, null));
    }

    /// <summary>フォルダ名の日時より後ろを変え、車のプロファイルの中の名前も直す</summary>
    private async Task RenameAsync(string path, string rest, bool archive)
    {
        var open = openFolder();
        var root = Root();
        var now = Now;
        var (renamed, error) = await Task.Run<(string? Name, string? Error)>(() =>
        {
            try
            {
                var s = SessionCatalog.Summarize(path);
                var newest = !archive && root != null ? SessionCatalog.List(root).FirstOrDefault()?.Folder : null;
                bool isNewest = newest != null && SamePath(newest, path);
                if (SessionCatalog.CannotChange(s, open, isNewest, now) is { } why) return (null, why);
                var dest = SessionCatalog.Rename(path, rest);
                var (oldName, newName) = (Path.GetFileName(path.TrimEnd('\\', '/')), Path.GetFileName(dest));
                int n = profiles.RenameFolder(oldName, newName);
                PlayerLog.Info($"記録の名前を変えた: {oldName} → {newName}(車のプロファイル {n} 件を直した)");
                return (newName, null);
            }
            catch (Exception ex)
            {
                PlayerLog.Error($"記録の名前を変えられない: {path}", ex);
                return (null, ex.Message);
            }
        });
        await SendListAsync(archive, new SessionListResult("rename", null, null, renamed, error));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
