using System.Text.Json;
using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>記録の一覧とアーカイブの操作の流れ(SessionsViewModel)。記録は一時フォルダに作り、画面とダイアログは偽物</summary>
public sealed class SessionsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings;
    private string? _open;

    /// <summary>記録の書き込みから 1 日後(止めてすぐの記録の制限にかからない)</summary>
    private readonly TimeProvider _later = new FixedClock(DateTimeOffset.UtcNow.AddDays(1));

    public SessionsViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _settings = PlayerSettings.Load(Path.Combine(_root, "player.json"));
        _settings.LastRoot = _root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SessionsViewModel Vm(TimeProvider? clock = null) =>
        new(_shell, _settings, new ProfileStore(Path.Combine(_root, "profiles")), () => _open, clock ?? _later);

    private string Session(string name, string? dir = null)
    {
        var folder = Path.Combine(dir ?? _root, name);
        Directory.CreateDirectory(folder);
        var meta = new SessionMeta { Complete = true };
        meta.Session.StartedRecvTime = 1000;
        meta.Session.EndedRecvTime = 1300;
        File.WriteAllText(Path.Combine(folder, SessionMeta.FileName), JsonSerializer.Serialize(meta, SessionMeta.JsonOptions));
        return folder;
    }

    private HostSessionList LastList() => Assert.IsType<HostSessionList>(_shell.Posted.Last());

    [Fact]
    public async Task 一覧は新しい順で_開いている記録には印と理由を付ける()
    {
        Session("20261018_002105_計測");
        _open = Session("20261018_015340_スタジアム");

        await Vm().Handle(new ViewSessionList(false))!;

        var list = LastList();
        Assert.Equal(_root, list.Root);
        Assert.False(list.Archive);
        Assert.Null(list.Result);
        Assert.Equal(new[] { "20261018_015340_スタジアム", "20261018_002105_計測" }, list.Items.Select(i => i.Name));
        Assert.True(list.Items[0].Open);
        Assert.Equal(Strings.T("今開いている記録です"), list.Items[0].Blocked);
        Assert.False(list.Items[1].Open);
        Assert.Null(list.Items[1].Blocked);
        Assert.Equal("2026-10-18 01:53", list.Items[0].Started);
    }

    [Fact]
    public async Task アーカイブの一覧はアーカイブのフォルダを読む()
    {
        Session("20261018_002105_計測");
        Session("20261001_100000_古い", Path.Combine(_root, SessionCatalog.ArchiveDirName));
        await Vm().Handle(new ViewSessionList(true))!;
        var list = LastList();
        Assert.True(list.Archive);
        Assert.Equal(new[] { "20261001_100000_古い" }, list.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task 止めてから30分たたない一番新しい記録は変えられない()
    {
        var newest = Session("20261018_015340_スタジアム");
        Session("20261018_002105_計測");
        await Vm(TimeProvider.System).Handle(new ViewSessionList(false))!;
        var items = LastList().Items;
        Assert.NotNull(items[0].Blocked);
        Assert.Null(items[1].Blocked);

        await Vm(TimeProvider.System).Handle(new ViewSessionRename(newest, "新しい名前", false))!;
        Assert.Equal(items[0].Blocked, LastList().Result!.Error);
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public async Task アーカイブへ移す_移せない記録は理由を返してほかだけ移す()
    {
        var a = Session("20261018_002105_計測");
        _open = Session("20261018_015340_スタジアム");

        await Vm().Handle(new ViewSessionMove([a, _open, null, ""], false))!;

        var list = LastList();
        Assert.False(list.Archive);   // 移した後は、移した元の一覧を出す
        Assert.Equal("archive", list.Result!.Kind);
        Assert.Equal(1, list.Result.Moved);
        Assert.Equal<MoveError>([new MoveError("20261018_015340_スタジアム", Strings.T("今開いている記録です"))], list.Result.Errors!);
        Assert.True(Directory.Exists(Path.Combine(_root, SessionCatalog.ArchiveDirName, "20261018_002105_計測")));
        Assert.False(Directory.Exists(a));
        Assert.Equal(new[] { "20261018_015340_スタジアム" }, list.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task アーカイブから戻す()
    {
        var old = Session("20261001_100000_古い", Path.Combine(_root, SessionCatalog.ArchiveDirName));
        await Vm().Handle(new ViewSessionMove([old], true))!;
        var list = LastList();
        Assert.True(list.Archive);
        Assert.Equal("restore", list.Result!.Kind);
        Assert.Equal(1, list.Result.Moved);
        Assert.Empty(list.Items);
        Assert.True(Directory.Exists(Path.Combine(_root, "20261001_100000_古い")));
    }

    [Fact]
    public async Task 名前を変えると日時は残して後ろだけ変える()
    {
        Session("20261018_015340_スタジアム");
        var target = Session("20261018_002105_計測");
        await Vm().Handle(new ViewSessionRename(target, " 計測コース1 ", false))!;
        var result = LastList().Result!;
        Assert.Equal(new SessionListResult("rename", null, null, "20261018_002105_計測コース1", null), result);
        Assert.True(Directory.Exists(Path.Combine(_root, "20261018_002105_計測コース1")));
    }

    [Fact]
    public async Task 名前を変えられないときは理由を返す()
    {
        Session("20261018_015340_スタジアム");
        var target = Session("20261018_002105_計測");
        await Vm().Handle(new ViewSessionRename(target, "a/b", false))!;
        var result = LastList().Result!;
        Assert.Null(result.Renamed);
        Assert.Equal(Strings.T("フォルダ名に使えない文字があります"), result.Error);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task 保存先を選び直すと覚えて一覧を出す_アーカイブの中を選んだらその上()
    {
        var other = Path.Combine(_root, "other");
        Session("20261018_002105_計測", other);
        _shell.Folder = Path.Combine(other, SessionCatalog.ArchiveDirName);
        var vm = Vm();
        await vm.Handle(new ViewSessionPickRoot(true))!;

        Assert.Equal(_root, _shell.AskedInitialDir);
        Assert.Equal(other, vm.Root());
        Assert.Equal(other, _settings.LastRoot);
        Assert.Equal(other, PlayerSettings.Load(Path.Combine(_root, "player.json")).LastRoot);   // 読んだファイルへ保存する
        var list = LastList();
        Assert.Equal(other, list.Root);
        Assert.True(list.Archive);
    }

    [Fact]
    public async Task 保存先を選ばなければ何もしない()
    {
        var vm = Vm();
        await vm.Handle(new ViewSessionPickRoot(false))!;
        Assert.Empty(_shell.Posted);
        Assert.Equal(_root, vm.Root());
    }

    [Fact]
    public void 記録の一覧のメッセージでなければ扱わない()
    {
        Assert.Null(Vm().Handle(new ViewOpen()));
        Assert.Null(Vm().Handle(new ViewSessionOpen(_root)));   // 開くのは外枠
        Assert.Null(Vm().Handle(new ViewSessionRename(null, "x", false)));
    }
}
