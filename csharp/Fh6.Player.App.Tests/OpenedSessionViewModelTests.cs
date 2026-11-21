using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>開いている記録(OpenedSessionViewModel)。合成の記録を一時フォルダの保存先に書いて開く</summary>
public sealed class OpenedSessionViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-opened-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings;
    private readonly OpenedSessionViewModel _vm;
    private int _refreshed;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 21, 30, 15, TimeSpan.FromHours(9));

    public OpenedSessionViewModelTests()
    {
        Directory.CreateDirectory(SaveRoot);
        SyntheticSession.Write(SessionFolder);
        _settings = PlayerSettings.Load(SettingsFile);
        _vm = new OpenedSessionViewModel(_shell, _settings, new ProfileStore(Path.Combine(_root, "profiles")), () => _refreshed++,
            new FixedClock(Now) { Zone = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST") });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string SaveRoot => Path.Combine(_root, "save");
    private string SessionFolder => Path.Combine(SaveRoot, "20261107_210000");
    private string SettingsFile => Path.Combine(_root, "player.json");

    private async Task<LoadedSession> OpenAsync()
    {
        await _vm.OpenAsync(SessionFolder);
        _shell.Posted.Clear();
        return _vm.Session!;
    }

    [Fact]
    public async Task 記録のフォルダを選ぶと開き_外枠に画面へ出してもらい_保存先を覚える()
    {
        _shell.Folder = SessionFolder;
        await _vm.Handle(new ViewOpen())!;

        Assert.Equal(new HostLoading("20261107_210000"), Assert.Single(_shell.Posted));
        var s = Assert.Single(_shell.Shown);
        Assert.Same(s, _vm.Session);
        Assert.Equal(SessionFolder, s.Folder);
        Assert.Single(s.Races);
        Assert.Null(_vm.PackageFile);
        Assert.Equal("20261107_210000", _vm.DisplayName);
        Assert.Equal(SaveRoot, PlayerSettings.Load(SettingsFile).InitialFolder());
        Assert.Equal(0, _refreshed);   // 送るのは、読み直した画面の準備ができてから(外枠)
    }

    [Fact]
    public async Task 記録の中のファイルを指しても_そのフォルダを開く()
    {
        await _vm.Handle(new ViewSessionOpen(Path.Combine(SessionFolder, "session.json")))!;
        Assert.Equal(SessionFolder, Assert.Single(_shell.Shown).Folder);
    }

    [Fact]
    public async Task 開けなければエラーを出し_前の記録も閉じる()
    {
        await OpenAsync();
        await _vm.OpenAsync(Path.Combine(_root, "無い記録"));

        Assert.IsType<HostLoading>(_shell.Posted[0]);
        Assert.IsType<HostError>(_shell.Posted[1]);
        Assert.Null(_vm.Session);
        Assert.Equal("", _vm.DisplayName);
        Assert.Single(_shell.Shown);   // 最初に開いたときだけ
    }

    [Fact]
    public async Task 選ぶ画面で選ばなければ何もしない()
    {
        _settings.LastRoot = SaveRoot;
        await _vm.Handle(new ViewOpen())!;
        Assert.Equal(SaveRoot, _shell.AskedInitialDir);   // 前回開いた記録の 1 つ上から

        _settings.LastPackageDir = _root;
        await _vm.Handle(new ViewOpenPackage())!;
        Assert.Equal(_root, _shell.AskedInitialDir);
        Assert.StartsWith($"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}|", _shell.AskedFilter);

        Assert.Empty(_shell.Posted);
        Assert.Empty(_shell.Shown);
        Assert.Null(_vm.Session);
    }

    [Fact]
    public async Task 同期の方式は読める名前のときだけ変えて描き直す()
    {
        await _vm.Handle(new ViewMethod("どれでもない"))!;
        Assert.Equal(VideoSyncMethod.StartedEvent, _vm.Method);
        Assert.Equal(0, _refreshed);

        var other = Enum.GetValues<VideoSyncMethod>().First(m => m != VideoSyncMethod.StartedEvent);
        await _vm.Handle(new ViewMethod(other.ToString()))!;
        Assert.Equal(other, _vm.Method);
        Assert.Equal(1, _refreshed);
    }

    [Fact]
    public async Task 手動補正はsessionjsonに保存して描き直す_消すと空になる()
    {
        var s = await OpenAsync();
        await _vm.Handle(new ViewManualSync(10, 10.5, "  スタートの瞬間  "))!;

        var p = Assert.Single(SessionMeta.Load(SessionFolder).Video.ManualSync);
        Assert.Equal(SyntheticSession.Zero + 10, p.RecvTime, 4);   // 動画 0 秒 = 録画開始のイベント
        Assert.Equal(10.5, p.VideoSec);
        Assert.Equal("スタートの瞬間", p.Note);
        Assert.Equal("2026-11-07T21:30:15+09:00", p.CreatedAt);
        Assert.Single(s.Meta.Video.ManualSync);   // 開いている記録も読み直す
        Assert.Equal(1, _refreshed);
        Assert.Equal(new HostNotice(Strings.T("手動補正を保存しました"), null), Assert.Single(_shell.Posted));

        await _vm.Handle(new ViewClearManualSync())!;
        Assert.Empty(SessionMeta.Load(SessionFolder).Video.ManualSync);
        Assert.Empty(s.Meta.Video.ManualSync);
        Assert.Equal(2, _refreshed);
        Assert.Equal(new HostNotice(Strings.T("手動補正を消しました"), null), _shell.Posted[^1]);
    }

    [Fact]
    public async Task 記録を開いていなければ手動補正も名前も扱うが何もしない()
    {
        await _vm.Handle(new ViewManualSync(10, 10.5, null))!;
        await _vm.Handle(new ViewClearManualSync())!;
        await _vm.Handle(new ViewRaceNamesForm(0))!;
        Assert.Empty(_shell.Posted);
        Assert.Equal(0, _refreshed);
        Assert.Null(_vm.Handle(new ViewManualSync(null, 10.5, null)));   // 位置が無ければ扱わない
    }

    [Fact]
    public async Task 名前を付けるダイアログの中身を送る()
    {
        await OpenAsync();
        await _vm.Handle(new ViewRaceNamesForm(0))!;
        var form = Assert.IsType<HostRaceNamesForm>(Assert.Single(_shell.Posted));
        Assert.Equal(0, form.Race);
        Assert.True(form.Editable);   // 記録を終えた記録
        Assert.Null(form.Reason);
        Assert.Empty(form.Course.List);   // 登録ファイルが無い

        _shell.Posted.Clear();
        await _vm.Handle(new ViewRaceNamesForm(1))!;   // 無いレース
        Assert.Empty(_shell.Posted);
    }

    [Fact]
    public async Task 名前を保存するとsessionjsonと登録の受け渡し用のファイルに書く()
    {
        var s = await OpenAsync();
        await _vm.Handle(new ViewEditRaceNames(0, RegistryInbox.NewId, "  テストのサーキット ", "circuit", "テストの車", null, null, null))!;

        var ev = Assert.Single(SessionMeta.Load(SessionFolder).Events, e => e.Type == "race_edited");
        Assert.Equal("テストのサーキット", (string?)ev.Data!["course_name"]);
        Assert.Null(ev.Data["course_id"]);   // 新しいコースの id は記録アプリが付ける
        Assert.Equal("player", (string?)ev.Data["source"]);
        Assert.False(ev.Data.ContainsKey("assists"));
        Assert.Equal("テストのサーキット", s.Races[0].Course);   // レースの一覧にすぐ出る
        Assert.Equal("テストの車", s.Races[0].Car);

        var inbox = System.Text.RegularExpressions.Regex.Unescape(File.ReadAllText(Path.Combine(SaveRoot, RegistryInbox.FileName)));   // JSON の \u の書き方を戻す
        Assert.Contains("テストのサーキット", inbox);
        Assert.Contains("2026-11-07T21:30:15+09:00", inbox);
        Assert.Equal(1, _refreshed);
        var toast = Assert.IsType<HostToast>(Assert.Single(_shell.Posted));
        Assert.Null(toast.Error);
        Assert.Equal(6, toast.Sec);
    }

    [Fact]
    public async Task 新しいコースに名前が無ければ保存しない()
    {
        await OpenAsync();
        await _vm.Handle(new ViewEditRaceNames(0, RegistryInbox.NewId, "  ", null, null, null, null, null))!;

        Assert.Equal(new HostToast(null, Strings.T("レース名を入れてください"), null), Assert.Single(_shell.Posted));
        Assert.DoesNotContain(SessionMeta.Load(SessionFolder).Events, e => e.Type == "race_edited");
        Assert.False(File.Exists(Path.Combine(SaveRoot, RegistryInbox.FileName)));
        Assert.Equal(0, _refreshed);
    }

    [Fact]
    public void 開く_手動補正_名前のメッセージでなければ扱わない()
    {
        Assert.Null(_vm.Handle(new ViewSessionList(false)));
        Assert.Null(_vm.Handle(new ViewSessionOpen("")));
        Assert.Null(_vm.Handle(new ViewProbeFfmpeg(false)));
    }
}
