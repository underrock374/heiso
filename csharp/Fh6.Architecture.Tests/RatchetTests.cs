namespace Fh6.Architecture.Tests;

/// <summary>検査の道具そのものの確かめ</summary>
public class RatchetTests
{
    [Fact]
    public void 増えても減っても落ち_同じなら通る()
    {
        var allowed = new Dictionary<string, int> { ["a.js"] = 2 };
        Assert.Empty(Ratchet.Compare(allowed, new Dictionary<string, int> { ["a.js"] = 2 }));
        Assert.Contains("増やさない", Assert.Single(Ratchet.Compare(allowed, new Dictionary<string, int> { ["a.js"] = 3 })));
        Assert.Contains("減らす", Assert.Single(Ratchet.Compare(allowed, new Dictionary<string, int> { ["a.js"] = 1 })));
        Assert.Contains("減らす", Assert.Single(Ratchet.Compare(allowed, new Dictionary<string, int>())));
        Assert.Contains("増やさない", Assert.Single(Ratchet.Compare(new Dictionary<string, int>(), new Dictionary<string, int> { ["b.js"] = 1 })));
    }

    [Fact]
    public void コメントは消え_文字列の中は残る()
    {
        Assert.Equal("a \n b", SourceTree.StripComments("a // localStorage\n b"));
        Assert.Equal("a  b", SourceTree.StripComments("a /* x\n y */ b"));
        Assert.Equal("\"http://x\" y", SourceTree.StripComments("\"http://x\" y"));
        Assert.Equal("`a // b\nc`", SourceTree.StripComments("`a // b\nc`"));
        Assert.Equal("\"a\\\"// b\"", SourceTree.StripComments("\"a\\\"// b\""));
    }
}
