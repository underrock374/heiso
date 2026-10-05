using Fh6.Core;

namespace Fh6.Core.Tests;

public class SessionFolderNameTests
{
    [Theory]
    [InlineData("20260927_211710", null)]
    [InlineData("20260927_211710#夏_鳥野山", "夏_鳥野山")]
    [InlineData("20260927_211710＃夏_鳥野山", "夏_鳥野山")]
    [InlineData("20260927_211710夏_鳥野山", "夏_鳥野山")]
    [InlineData("20260927_211710_夏", "夏")]
    [InlineData("20260927_211710 鳥野山 AutoDrive", "鳥野山 AutoDrive")]
    [InlineData("20260927_211710#", null)]
    public void 先頭が日時ならコメント付きでも有効(string name, string? comment)
    {
        Assert.True(SessionFolderName.TryParse(name, out var started, out var c));
        Assert.Equal(new DateTime(2026, 9, 27, 21, 17, 10), started);
        Assert.Equal(comment, c);
    }

    [Theory]
    [InlineData("夏_20260927_211710")]
    [InlineData("2026-09-27_211710")]
    [InlineData("20261399_211710")]   // 日付として正しくない
    [InlineData("video")]
    public void 先頭が日時でなければ無効(string name)
    {
        Assert.False(SessionFolderName.TryParse(name, out _, out _));
    }

    [Fact]
    public void 記録を始めるときの名前()
    {
        var t = new DateTime(2026, 9, 27, 21, 17, 10);
        Assert.Equal("20260927_211710", SessionFolderName.Create(t, null));
        Assert.Equal("20260927_211710", SessionFolderName.Create(t, "  "));
        Assert.Equal("20260927_211710#夏 鳥野山", SessionFolderName.Create(t, " 夏 鳥野山 "));
        // フォルダ名に使えない文字は _ に、末尾の . は取り除く
        Assert.Equal("20260927_211710#A_B_C", SessionFolderName.Create(t, "A/B:C."));
        // 長いコメントは抑える
        Assert.Equal(15 + 1 + SessionFolderName.MaxCommentLength, SessionFolderName.Create(t, new string('あ', 100)).Length);
        // 作った名前は読み戻せる
        Assert.True(SessionFolderName.TryParse(SessionFolderName.Create(t, "冬 173311 CPミス"), out _, out var c));
        Assert.Equal("冬 173311 CPミス", c);
    }
}
