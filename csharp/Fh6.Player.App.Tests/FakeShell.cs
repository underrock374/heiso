namespace Fh6.Player.App.Tests;

/// <summary>ViewModel のテスト用の外枠。送ったメッセージを控え、選ぶ画面は決めておいた答えを返す</summary>
internal sealed class FakeShell : IShell
{
    public List<HostMessage> Posted { get; } = [];
    public string? Folder { get; set; }
    public string? File { get; set; }
    public string? AskedInitialDir { get; private set; }
    public string? AskedFilter { get; private set; }
    public string? SaveAs { get; set; }
    public string? AskedFileName { get; private set; }
    public List<LoadedSession> Shown { get; } = [];
    public List<string?> ShownAltDirs { get; } = [];
    public List<(string Dir, double Sec, int? Race)> VideoFolders { get; } = [];

    public void Post(HostMessage message) => Posted.Add(message);

    public void ShowSession(LoadedSession session, string? altDir) { Shown.Add(session); ShownAltDirs.Add(altDir); }

    public bool ShowVideoFolder(string dir, double sec, int? race)
    {
        if (VideoFolders.Count > 0 && VideoFolders[^1].Dir == dir) return false;
        VideoFolders.Add((dir, sec, race));
        return true;
    }

    public Task AfterMessageAsync() => Task.CompletedTask;

    /// <summary>ページへ渡すものを登録し直した回数</summary>
    public int PageDataRefreshed { get; private set; }

    public void RefreshPageData() => PageDataRefreshed++;

    public Task<string?> PickSaveFileAsync(string title, string filter, string fileName, string? initialDir)
    {
        AskedInitialDir = initialDir;
        AskedFilter = filter;
        AskedFileName = fileName;
        return Task.FromResult(SaveAs);
    }

    public Task<string?> PickFolderAsync(string title, string? initialDir)
    {
        AskedInitialDir = initialDir;
        return Task.FromResult(Folder);
    }

    public Task<string?> PickFileAsync(string title, string filter, string? initialDir)
    {
        AskedInitialDir = initialDir;
        AskedFilter = filter;
        return Task.FromResult(File);
    }
}

/// <summary>決めた時刻を返す時計</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

    /// <summary>日時を書くときの地域の時差(既定はこの PC のもの)</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public override TimeZoneInfo LocalTimeZone => Zone;
}
