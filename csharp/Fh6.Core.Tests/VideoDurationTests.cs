using Fh6.Core;

namespace Fh6.Core.Tests;

public class VideoDurationTests
{
    // testdata/video/make_test_videos.py で作ったファイル。映像トラックの長さを期待値にしている
    private static string TestFile(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "video", name);

    [Fact]
    public void 通常のMP4は映像トラックの長さ()
    {
        Assert.Equal(4.5, VideoDuration.TryRead(TestFile("normal.mp4"))!.Value, 6);
    }

    [Fact]
    public void FragmentedMP4は各moofの映像の終了時刻()
    {
        Assert.Equal(3.0, VideoDuration.TryRead(TestFile("fragmented.mp4"))!.Value, 6);
    }

    [Fact]
    public void フレームレートは映像トラックのサンプル数と長さから()
    {
        // どちらも 30fps(timescale 15360、1 フレーム 512)
        Assert.Equal(30.0, VideoDuration.TryReadFrameRate(TestFile("normal.mp4"))!.Value, 6);
        Assert.Equal(30.0, VideoDuration.TryReadFrameRate(TestFile("fragmented.mp4"))!.Value, 6);
        Assert.Null(VideoDuration.TryReadFrameRate(TestFile("missing.mp4")));
    }

    [Fact]
    public void コマの間隔が一定ならVFRでない()
    {
        Assert.False(VideoDuration.TryReadIsVariableFrameRate(TestFile("normal.mp4")));
        Assert.Null(VideoDuration.TryReadIsVariableFrameRate(TestFile("fragmented.mp4")));   // Fragmented は調べない
        Assert.Null(VideoDuration.TryReadIsVariableFrameRate(TestFile("missing.mp4")));
        // OBS の録画: 一定の間隔と、最後の 1 コマだけ違う
        Assert.False(VideoDuration.LooksVariable(new (uint, uint)[] { (18000, 512), (1, 300) }));
    }

    [Fact]
    public void コマの間隔がばらつけばVFR()
    {
        // Game Bar などの可変フレームレート: 間隔がばらつくコマが 1% を超える
        Assert.True(VideoDuration.LooksVariable(new (uint, uint)[] { (900, 512), (30, 1024), (40, 400), (900, 512) }));
        // 1% 以下のばらつきは一定とみなす
        Assert.False(VideoDuration.LooksVariable(new (uint, uint)[] { (1000, 512), (5, 1024), (1000, 512) }));
    }

    [Fact]
    public void MKVはnull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heiso-test-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(path, new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 });
        try
        {
            Assert.Null(VideoDuration.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 存在しないファイルはnull()
    {
        Assert.Null(VideoDuration.TryRead(TestFile("missing.mp4")));
    }
}
