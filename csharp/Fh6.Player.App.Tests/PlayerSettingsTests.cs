namespace Fh6.Player.App.Tests;

/// <summary>再生アプリの設定(PlayerSettings)のうち、記録ごとに最後に表示していた動画</summary>
public sealed class PlayerSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-settings-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_root, "player.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void 新しい順に覚え_録画にしたら忘れる()
    {
        var s = PlayerSettings.Load(File);
        s.SetLastVideo("20261107_210000", "R1");
        s.SetLastVideo("20261108_200000", "R2");
        s.SetLastVideo("20261107_210000", "R3");   // 同じ記録は置き換えて、いちばん前へ

        var loaded = PlayerSettings.Load(File);
        Assert.Equal(["20261107_210000", "20261108_200000"], loaded.LastVideos!.Select(v => v.Session));
        Assert.Equal("R3", loaded.LastVideoOf("20261107_210000"));

        s.SetLastVideo("20261107_210000", null);
        Assert.Null(PlayerSettings.Load(File).LastVideoOf("20261107_210000"));
        Assert.Equal("R2", PlayerSettings.Load(File).LastVideoOf("20261108_200000"));
    }

    [Fact]
    public void 覚えるのは200件まで()
    {
        var s = PlayerSettings.Load(File);
        for (int i = 0; i < PlayerSettings.LastVideosMax + 5; i++) s.SetLastVideo($"S{i:D3}", "R");
        Assert.Equal(PlayerSettings.LastVideosMax, s.LastVideos!.Count);
        Assert.Equal("R", s.LastVideoOf($"S{PlayerSettings.LastVideosMax + 4:D3}"));   // 新しいものは残る
        Assert.Null(s.LastVideoOf("S000"));                                         // 古いものから消える
    }
}
