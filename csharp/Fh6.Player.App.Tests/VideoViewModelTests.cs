using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 画面に出す動画と記録のデータ(VideoViewModel)。合成の記録に、中身のない録画のファイルを置いて開く。
/// ffmpeg は試さない(リプレイを探すのは、ffmpeg が無いときの知らせだけ)
/// </summary>
public sealed class VideoViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-video-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings;
    private readonly OpenedSessionViewModel _opened;
    private readonly VideoViewModel _vm;

    public VideoViewModelTests()
    {
        SyntheticSession.Write(SessionFolder);
        File.WriteAllBytes(Path.Combine(SessionFolder, "recording.mp4"), new byte[1000]);   // 録画(中身は読まない)
        _settings = PlayerSettings.Load(Path.Combine(_root, "player.json"));
        var prefs = new SettingsViewModel(_shell, _settings, _ => new FfmpegStatus(null, null, [], null));
        _opened = new OpenedSessionViewModel(_shell, _settings, new ProfileStore(Path.Combine(_root, "profiles")), () => _vm!.SendSession(keepVideo: true));
        _vm = new VideoViewModel(_shell, _settings, _opened, prefs, new JobSlot());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string SessionFolder => Path.Combine(_root, "save", "20261107_210000");

    private async Task OpenAsync()
    {
        await _opened.OpenAsync(SessionFolder);
        _shell.Posted.Clear();
    }

    private HostSession LastSession() => _shell.Posted.OfType<HostSession>().Last();

    private List<VideoAlt> SavedAlts() => SessionMeta.Load(SessionFolder).VideoAlt ?? [];

    /// <summary>1 レース目に、録画の中のリプレイを手動で合わせるつもりで割り当てる(自動判別の結果なし)</summary>
    private async Task<VideoAlt> AdoptManualReplayAsync()
    {
        await _vm.Handle(new ViewAdoptReplay(true, 12, 0))!;
        return Assert.Single(SavedAlts());
    }

    [Fact]
    public async Task 録画を表示しているときの記録のデータ()
    {
        await OpenAsync();
        _vm.SendSession(keepVideo: false);

        // 記録のデータの後に、選んでいるレースの摩擦円・荷重移動(SelectionViewModel)
        Assert.Equal(2, _shell.Posted.Count);
        var s = Assert.IsType<HostSession>(_shell.Posted[0]);
        Assert.Null(Assert.IsType<HostFriction>(_shell.Posted[1]).Race);
        Assert.Null(s.Race);
        Assert.False(s.KeepVideo);
        Assert.Equal("20261107_210000", s.Name);
        Assert.Equal("https://session.heiso/recording.mp4", s.VideoUrl);
        Assert.Empty(s.Videos);
        Assert.Null(s.ActiveVideo);
        Assert.Null(s.Alt);
        Assert.True(s.HasRecording);
        Assert.True(s.CanAddVideo);
        Assert.Null(s.VideoError);
        Assert.Equal("StartedEvent", s.Method);
        Assert.NotNull(s.Series);
        Assert.Null(_vm.ActiveAlt());
    }

    [Fact]
    public void 記録を開いていなければ送らない()
    {
        _vm.SendSession(keepVideo: false);
        Assert.Empty(_shell.Posted);
    }

    [Fact]
    public async Task リプレイを手動で合わせて割り当てると保存してリプレイを表示する()
    {
        await OpenAsync();
        var alt = await AdoptManualReplayAsync();

        Assert.Equal(VideoAlt.KindReplay, alt.Kind);
        Assert.Equal(VideoAltSync.MethodManual, alt.Sync.Method);
        Assert.Equal(_opened.Session!.Races[0].Segment.StartRecvTime, alt.RaceStartRecvTime, 6);
        Assert.Equal(_settings.ReplayDefaultShiftSec(30), alt.Sync.ShiftSec);   // 既定の量だけ前へずらしておく
        Assert.Equal(alt.Id, _vm.ActiveAlt()!.Id);

        var s = LastSession();
        Assert.Equal(alt.Id, s.ActiveVideo);
        Assert.Equal(VideoAlt.ContentReplay, s.Alt!.Content);
        Assert.Equal(0, s.Alt.Race);
        Assert.Equal(Math.Round(alt.Sync.ShiftSec * 1000), s.Alt.ShiftMs);
        Assert.Equal("https://session.heiso/recording.mp4", s.VideoUrl);   // 録画の中のリプレイ: 読み直さない
        Assert.Empty(_shell.VideoFolders);
        var restore = _shell.Posted.OfType<HostRestore>().Single();
        Assert.Equal(0, restore.Race);   // リプレイのレースは 1 本だけ
        Assert.Equal(Strings.T("リプレイの先頭のコマを出して「いまのコマ = リプレイの先頭」を押すと合います"), _shell.Posted.OfType<HostToast>().Last().Message);
        Assert.Single(s.Videos);
    }

    [Fact]
    public async Task 微調整はリプレイのずらし量に足して保存する()
    {
        await OpenAsync();
        var before = (await AdoptManualReplayAsync()).Sync.ShiftSec;
        _shell.Posted.Clear();

        await _vm.Handle(new ViewAltNudge(0.1))!;
        Assert.Equal(Math.Round(before + 0.1, 4), Assert.Single(SavedAlts()).Sync.ShiftSec);
        Assert.True(Assert.Single(_shell.Posted.OfType<HostSession>()).KeepVideo);

        await _vm.Handle(new ViewAltNudge(6))!;   // 大きすぎる量は無視する
        Assert.Equal(Math.Round(before + 0.1, 4), Assert.Single(SavedAlts()).Sync.ShiftSec);
    }

    [Fact]
    public async Task 割り当て済みのリプレイは今の合わせ方を出す()
    {
        await OpenAsync();
        Assert.Empty(_vm.AssignedReplays());   // 割り当てていなければ空(「採用」は確かめずに割り当てる)

        var alt = await AdoptManualReplayAsync();
        await _vm.Handle(new ViewAltNudge(0.1))!;
        var shift = Assert.Single(SavedAlts()).Sync.ShiftSec;

        var a = Assert.Single(_vm.AssignedReplays());
        Assert.Equal(0, a.Race);
        Assert.Equal(alt.Id, a.Id);
        Assert.True(a.Manual);
        Assert.Equal(alt.Sync.ReplayZeroVideoSec, a.Zero);
        Assert.Equal(Math.Round(shift * 1000), a.ShiftMs);
        Assert.Equal(Math.Round(shift * 30, 1), a.ShiftFrames);   // 録画のコマ(30fps)

        // 「採用」で作り直すと、微調整は設定の量に戻る(画面が確かめてから送る)
        await _vm.Handle(new ViewAdoptReplay(true, 12, 0))!;
        Assert.Equal(Math.Round(_settings.ReplayDefaultShiftSec(30) * 1000), Assert.Single(_vm.AssignedReplays()).ShiftMs);
    }

    [Fact]
    public async Task 手動補正は割り当てた動画を表示しているときだけ扱う()
    {
        await OpenAsync();
        Assert.Null(_vm.Handle(new ViewManualSync(10, 10.5, null)));   // 録画: 開いている記録の手動補正
        Assert.Null(_vm.Handle(new ViewClearManualSync()));

        await AdoptManualReplayAsync();
        _shell.Posted.Clear();
        await _vm.Handle(new ViewManualSync(100, 101, null))!;
        var alt = Assert.Single(SavedAlts());
        Assert.Equal(0, alt.Sync.ShiftSec);   // この点で決まるので、ずらし量は 0 から
        Assert.Empty(SessionMeta.Load(SessionFolder).Video.ManualSync);   // 録画の手動補正は変えない
        Assert.Equal(new HostNotice(Strings.T("手動補正を保存しました"), null), _shell.Posted[^1]);

        await _vm.Handle(new ViewClearManualSync())!;   // リプレイの補正は消せない
        Assert.Equal(Strings.T("リプレイは「探し直す」で自動の位置に戻せます"), Assert.IsType<HostToast>(_shell.Posted[^1]).Error);
    }

    [Fact]
    public async Task 録画に戻すと記録のデータを録画で送り直す()
    {
        await OpenAsync();
        await AdoptManualReplayAsync();
        _shell.Posted.Clear();

        await _vm.Handle(new ViewSelectVideo(null, 30, null))!;
        Assert.Null(_vm.ActiveAlt());
        var s = LastSession();
        Assert.Null(s.ActiveVideo);
        Assert.Null(s.Alt);
        Assert.Equal(0, _shell.Posted.OfType<HostRestore>().Single().Race);   // リプレイから戻るときは元のレース
    }

    // ---- 開き直したとき(spec「開き直したときの表示と、割り当て済みのリプレイ」の A) ----

    [Fact]
    public async Task 開き直すと最後に表示していた動画で開き_その動画の頭へ移す()
    {
        await OpenAsync();
        var alt = await AdoptManualReplayAsync();
        Assert.Equal(alt.Id, _settings.LastVideoOf("20261107_210000"));
        Assert.Equal(alt.Id, PlayerSettings.Load(Path.Combine(_root, "player.json")).LastVideoOf("20261107_210000"));   // 保存してある

        await OpenAsync();
        Assert.Equal(alt.Id, _vm.ActiveAlt()!.Id);
        Assert.Null(Assert.Single(_shell.ShownAltDirs.Skip(1)));   // 録画の中のリプレイ: 割り当てるフォルダは無い
        _vm.SendSession(keepVideo: false);
        Assert.Equal(alt.Id, LastSession().ActiveVideo);
        Assert.Single(LastSession().Videos);
        // 「動画」で選んだときと同じ位置(リプレイは先頭のコマ)。「スタートの 3 秒前から再生する」が OFF なので止めたまま
        var restore = Assert.Single(_shell.Posted.OfType<HostRestore>());
        Assert.Equal(new HostRestore(0, false, true, 0), restore with { Sec = 0 });
        Assert.Equal(LastSession().Marks!.Races[0].T0, restore.Sec, 3);
        Assert.Empty(_shell.Posted.OfType<HostToast>());

        _vm.SendSession(keepVideo: false);   // 移すのは最初の 1 回だけ(画面が落ちて読み直したときは、外枠が元の位置へ戻す)
        Assert.Single(_shell.Posted.OfType<HostRestore>());
    }

    [Fact]
    public async Task スタートの3秒前から再生するなら_覚えていた動画の頭から再生する()
    {
        await OpenAsync();
        await AdoptManualReplayAsync();
        _settings.View = new ViewSettings { AutoStart = true }.WithDefaults();

        await OpenAsync();
        _vm.SendSession(keepVideo: false);
        Assert.True(Assert.Single(_shell.Posted.OfType<HostRestore>()).Play);
    }

    [Fact]
    public async Task 録画に戻してから閉じれば_録画で開く()
    {
        await OpenAsync();
        await AdoptManualReplayAsync();
        await _vm.Handle(new ViewSelectVideo(null, 30, null))!;
        Assert.Null(_settings.LastVideoOf("20261107_210000"));

        await OpenAsync();
        Assert.Null(_vm.ActiveAlt());
        _vm.SendSession(keepVideo: false);
        Assert.Null(LastSession().ActiveVideo);
        Assert.Empty(_shell.Posted.OfType<HostRestore>());   // 位置は今までどおり画面が決める
    }

    [Fact]
    public async Task 覚えていた動画が使えなければ_知らせずに録画で開く()
    {
        await OpenAsync();
        await AdoptManualReplayAsync();
        _settings.SetLastVideo("20261107_210000", "no-such-video");   // 割り当てを外した後など

        await OpenAsync();
        Assert.Null(_vm.ActiveAlt());
        _vm.SendSession(keepVideo: false);
        Assert.Null(LastSession().ActiveVideo);
        Assert.Empty(_shell.Posted.OfType<HostToast>());
        Assert.Empty(_shell.Posted.OfType<HostRestore>());
    }

    [Fact]
    public async Task 記録の外のフォルダの動画で開くときは_そのフォルダを割り当ててからページを読み込む()
    {
        await OpenAsync();
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        _shell.File = Path.Combine(other, "camera.mp4");
        File.WriteAllBytes(_shell.File, new byte[500]);
        await _vm.Handle(new ViewAddVideoFile(VideoAlt.ContentLive, 10, 0))!;
        var alt = Assert.Single(SavedAlts());

        await OpenAsync();
        Assert.Equal(other, _shell.ShownAltDirs[^1]);   // 外枠が、ページを読み込む前に alt.heiso に割り当てる
        Assert.Empty(_shell.VideoFolders.Skip(1));      // 読み直しはしない(開くときの 1 回だけ)
        _vm.SendSession(keepVideo: false);
        Assert.Equal(alt.Id, LastSession().ActiveVideo);
        Assert.Equal($"https://{VirtualHosts.Alt}/camera.mp4", LastSession().VideoUrl);
        Assert.Equal(0, Assert.Single(_shell.Posted.OfType<HostRestore>()).Race);   // 割り当てたレース
    }

    [Fact]
    public async Task フォルダ名のメモを変えても_同じ記録として覚えている()
    {
        await OpenAsync();
        var alt = await AdoptManualReplayAsync();
        var renamed = Path.Combine(_root, "save", "20261107_210000#糠平");
        Directory.Move(SessionFolder, renamed);

        await _opened.OpenAsync(renamed);
        Assert.Equal(alt.Id, _vm.ActiveAlt()!.Id);
    }

    [Fact]
    public async Task 記録の外のフォルダの動画は_そのフォルダを割り当ててページを読み直す()
    {
        await OpenAsync();
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        _shell.File = Path.Combine(other, "camera.mp4");
        File.WriteAllBytes(_shell.File, new byte[500]);

        await _vm.Handle(new ViewAddVideoFile(VideoAlt.ContentLive, 10, 0))!;
        var alt = Assert.Single(SavedAlts());
        Assert.Equal(VideoAlt.KindExternal, alt.Kind);
        Assert.Equal(SessionFolder, _shell.AskedInitialDir);   // 録画のフォルダから選ぶ
        var (dir, _, race) = Assert.Single(_shell.VideoFolders);
        Assert.Equal(other, dir);
        Assert.Equal(0, race);
        Assert.Empty(_shell.Posted.OfType<HostSession>());   // 読み直した画面の準備ができてから、外枠が送る

        _vm.SendSession(keepVideo: false);
        Assert.Equal("https://alt.heiso/camera.mp4", LastSession().VideoUrl);
    }

    [Fact]
    public async Task 見つからない動画を選ぶと選び直しを出す()
    {
        await OpenAsync();
        var other = Path.Combine(_root, "camera.mp4");
        _shell.File = other;
        File.WriteAllBytes(other, new byte[500]);
        await _vm.Handle(new ViewAddVideoFile(VideoAlt.ContentLive, 10, 0))!;
        var id = Assert.Single(SavedAlts()).Id;
        await _vm.Handle(new ViewSelectVideo(null, 10, 0))!;
        File.Delete(other);
        _shell.Posted.Clear();

        await _vm.Handle(new ViewSelectVideo(id, 10, 0))!;
        Assert.Equal(new HostVideoMissing(id, false, null), Assert.Single(_shell.Posted));
        Assert.Null(_vm.ActiveAlt());
    }

    [Fact]
    public async Task 表示している動画の割り当てを外すと録画に戻る()
    {
        await OpenAsync();
        var alt = await AdoptManualReplayAsync();
        _shell.Posted.Clear();

        await _vm.Handle(new ViewRemoveVideo(alt.Id, 5, 0))!;
        Assert.Empty(SavedAlts());
        Assert.Null(_vm.ActiveAlt());
        Assert.Equal(Strings.T("割り当てを外しました"), _shell.Posted.OfType<HostToast>().Last().Message);
    }

    [Fact]
    public async Task リプレイを探すにはffmpegが要る()
    {
        await OpenAsync();
        await _vm.Handle(new ViewAddReplayAuto(null, 0))!;
        Assert.Equal(new HostToast(null, Strings.T("リプレイを探すには ffmpeg が要ります。設定の「書き出し」で ffmpeg の場所を指定してください"), 8),
            Assert.Single(_shell.Posted));
    }

    [Fact]
    public void 動画のメッセージでなければ扱わない()
    {
        Assert.Null(_vm.Handle(new ViewSessionList(false)));
        Assert.Null(_vm.Handle(new ViewExport(0, "copy", false)));
    }
}
