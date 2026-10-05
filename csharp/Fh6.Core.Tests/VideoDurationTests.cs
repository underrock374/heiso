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
