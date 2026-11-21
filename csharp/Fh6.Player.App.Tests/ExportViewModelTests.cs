using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>書き出し(ExportViewModel)。合成の記録を開き、パッケージを書く処理と ffmpeg は偽物にする</summary>
public sealed class ExportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-export-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private readonly PlayerSettings _settings;
    private readonly OpenedSessionViewModel _opened;
    private readonly JobSlot _job = new();
    private FfmpegStatus _ffmpeg = new(@"C:\ffmpeg\ffmpeg.exe", "7.1", ["h264_nvenc", "libx264"], null);
    private VideoAlt? _alt;
    private readonly List<(double Start, string Dest, PackageReencodeOptions? Options, PackageSource? Source, bool NoAudio)> _written = [];
    private Func<CancellationToken, PackageResult>? _result;
    private readonly ExportViewModel _vm;

    public ExportViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        SyntheticSession.Write(SessionFolder);
        _settings = PlayerSettings.Load(Path.Combine(_root, "player.json"));
        _opened = new OpenedSessionViewModel(_shell, _settings, new ProfileStore(Path.Combine(_root, "profiles")), () => { });
        var prefs = new SettingsViewModel(_shell, _settings, _ => _ffmpeg);
        _vm = new ExportViewModel(_shell, _settings, _opened, prefs, _job, () => _alt,
            (folder, start, dest, options, progress, ct, source, noAudio) =>
            {
                _written.Add((start, dest, options, source, noAudio));
                return _result?.Invoke(ct) ?? new PackageResult(dest, 12_300_000, new PackageInfo(), 4321, 2.5);
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string SessionFolder => Path.Combine(_root, "save", "20261107_210000");
    private string Out(string name) => Path.Combine(_root, "out", name);

    private async Task OpenAsync()
    {
        await _opened.OpenAsync(SessionFolder);
        _shell.Posted.Clear();
    }

    private Task Export(int race = 0, string mode = "copy", bool noAudio = false) =>
        _vm.Handle(new ViewExport(race, mode, noAudio))!;

    [Fact]
    public async Task 記録を開いていなければ何もしない()
    {
        await Export();
        Assert.Empty(_shell.Posted);
        Assert.Null(_shell.AskedFileName);
    }

    [Fact]
    public async Task 保存先の画面は記録とレースの名前から始め_選ばなければ書かない()
    {
        await OpenAsync();
        await Export();

        Assert.Equal(PackageWriter.DefaultFileName("20261107_210000", null, null), _shell.AskedFileName);
        Assert.Equal(Path.Combine(_root, "save"), _shell.AskedInitialDir);   // 前に書き出していなければ、記録の 1 つ上
        Assert.Equal($"{Strings.T("HEISO のパッケージ")} (*{PackageInfo.Extension})|*{PackageInfo.Extension}", _shell.AskedFilter);
        Assert.Empty(_written);
        Assert.Empty(_shell.Posted);
        Assert.Equal("copy", PlayerSettings.Load(Path.Combine(_root, "player.json")).ExportMode);   // 選んだ書き方は覚える
    }

    [Theory]
    [InlineData("pkg", "pkg.heiso.zip")]
    [InlineData("pkg.zip", "pkg.heiso.zip")]
    [InlineData("pkg.heiso.zip", "pkg.heiso.zip")]
    public async Task 書き出すと名前の最後をそろえて書き_場所を覚えて知らせる(string chosen, string written)
    {
        await OpenAsync();
        _shell.SaveAs = Out(chosen);
        await Export(noAudio: true);

        var w = Assert.Single(_written);
        Assert.Equal(Out(written), w.Dest);
        Assert.Equal(_opened.Session!.Races[0].Segment.StartRecvTime, w.Start);
        Assert.Null(w.Options);   // 作り直さない
        Assert.Null(w.Source);    // 録画
        Assert.True(w.NoAudio);
        Assert.Equal(Path.Combine(_root, "out"), PlayerSettings.Load(Path.Combine(_root, "player.json")).LastExportDir);

        Assert.Equal(new HostJob(Strings.T("{n} レース目を書き出しています", ("n", 1)), 0, false), _shell.Posted[0]);
        Assert.Equal(new HostJob(null, 0, false), _shell.Posted[^2]);
        var done = Assert.IsType<HostToast>(_shell.Posted[^1]);
        Assert.Null(done.Error);
        Assert.Equal(12, done.Sec);
        Assert.Contains(written, done.Message);
        Assert.Null(_job.Current);
    }

    [Fact]
    public async Task 書き出しは720pなら選んだエンコーダで作り直し_中止できる()
    {
        await OpenAsync();
        _settings.ExportEncoder = "libx264";
        _shell.SaveAs = Out("a");
        await Export(mode: "720");

        Assert.EndsWith("_720p" + PackageInfo.Extension, _shell.AskedFileName);
        var w = Assert.Single(_written);
        Assert.Equal(@"C:\ffmpeg\ffmpeg.exe", w.Options!.Ffmpeg);
        Assert.Equal("libx264", w.Options.Encode.Encoder);
        Assert.Equal(Ffmpeg.DefaultVideoKbps(30), w.Options.Encode.VideoKbps);
        Assert.True(Assert.IsType<HostJob>(_shell.Posted[0]).Cancellable);
    }

    [Fact]
    public async Task 書き出しの途中で中止すると知らせる()
    {
        await OpenAsync();
        _shell.SaveAs = Out("a");
        using var started = new ManualResetEventSlim();
        _result = ct =>
        {
            started.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("中止されなかった");
        };
        var task = Export(mode: "copy");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        _job.Cancel();
        await task;

        Assert.Equal(new HostToast(Strings.T("書き出しを中止しました"), null, null), _shell.Posted[^1]);
        Assert.Null(_job.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 書き出せなければ理由を知らせる(bool packageError)
    {
        await OpenAsync();
        _shell.SaveAs = Out("a");
        _result = _ => throw (packageError ? new PackageException("動画が短すぎます") : new IOException("ディスクがいっぱい"));
        await Export();

        var toast = Assert.IsType<HostToast>(_shell.Posted[^1]);
        Assert.Equal(packageError ? "動画が短すぎます" : Strings.T("書き出せませんでした: {message}", ("message", "ディスクがいっぱい")), toast.Error);
        Assert.Null(PlayerSettings.Load(Path.Combine(_root, "player.json")).LastExportDir);
    }

    [Fact]
    public async Task 書き出しは720pでffmpegかエンコーダが無ければ保存先を聞かない()
    {
        await OpenAsync();
        _ffmpeg = new FfmpegStatus(null, null, [], null);
        await Export(mode: "720");
        Assert.Equal(Strings.T("ffmpeg が見つかりません。設定の「書き出し」で ffmpeg の場所を指定してください"), Assert.IsType<HostToast>(Assert.Single(_shell.Posted)).Error);

        _shell.Posted.Clear();
        _ffmpeg = new FfmpegStatus(@"C:\ffmpeg\ffmpeg.exe", "7.1", [], null);
        _settings.FfmpegPath = "other";   // 調べ直させる
        await Export(mode: "720");
        Assert.Equal(Strings.T("この PC で使える H.264 のエンコーダが ffmpeg にありません(設定の「書き出し」で確かめられます)"), Assert.IsType<HostToast>(Assert.Single(_shell.Posted)).Error);
        Assert.Null(_shell.AskedFileName);
        Assert.Empty(_written);
    }

    [Fact]
    public async Task 割り当てた動画が見つからなければ書き出さない()
    {
        await OpenAsync();
        _alt = new VideoAlt { Kind = VideoAlt.KindExternal, Content = VideoAlt.ContentLive, RaceStartRecvTime = _opened.Session!.Races[0].Segment.StartRecvTime };
        var video = Path.Combine(_root, "動かした動画.mp4");
        File.WriteAllBytes(video, [0]);
        _alt.SetPath(SessionFolder, video);
        File.Delete(video);   // 割り当てた後で動かした
        await Export();

        Assert.Equal(Strings.T("動画が見つかりません。右の欄「この動画」の「動画を選び直す…」で選び直してください"), Assert.IsType<HostToast>(Assert.Single(_shell.Posted)).Error);
        Assert.Null(_shell.AskedFileName);
    }

    [Fact]
    public void 書き出しのメッセージでなければ扱わない()
    {
        Assert.Null(_vm.Handle(new ViewExport(null, "copy", false)));
        Assert.Null(_vm.Handle(new ViewCancelJob()));
        Assert.Null(_vm.Handle(new ViewSessionList(false)));
    }
}
