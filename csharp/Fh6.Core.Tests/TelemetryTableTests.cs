using System.IO.Compression;
using System.Text;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class TelemetryTableTests
{
    private static TelemetryTable Read(string csv, params string[]? columns) =>
        TelemetryTable.Load(new StringReader(csv), columns is { Length: > 0 } ? columns : null);

    [Fact]
    public void 列名で読める()
    {
        var t = Read("recv_time,speed,gear\n1.5,10.25,3\n2.5,,11\n");
        Assert.Equal(2, t.RowCount);
        Assert.Equal(new[] { 1.5, 2.5 }, t["recv_time"]);
        Assert.Equal(10.25, t["speed"][0]);
        Assert.True(double.IsNaN(t["speed"][1]));
        Assert.Equal(11, t["gear"][1]);
    }

    [Fact]
    public void 列の順番が違っても同じ結果()
    {
        var a = Read("recv_time,speed,gear\n1,2,3\n4,5,6\n");
        var b = Read("gear,recv_time,speed\n3,1,2\n6,4,5\n");
        foreach (var c in new[] { "recv_time", "speed", "gear" })
            Assert.Equal(a[c], b[c]);
    }

    [Fact]
    public void 指定しない列は読まない()
    {
        var t = Read("recv_time,speed,gear\n1,2,3\n", "speed", "missing");
        Assert.True(t.Contains("speed"));
        Assert.False(t.Contains("gear"));
        Assert.False(t.Contains("missing"));
        Assert.Throws<KeyNotFoundException>(() => t["gear"]);
    }

    [Fact]
    public void 列が足りない行はNaN()
    {
        var t = Read("a,b,c\n1,2\n");
        Assert.Equal(1, t["a"][0]);
        Assert.True(double.IsNaN(t["c"][0]));
    }

    [Fact]
    public void gzのファイルをそのまま読める()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heiso-test-{Guid.NewGuid():N}.csv.gz");
        try
        {
            using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
            {
                var bytes = Encoding.UTF8.GetBytes(Fh6Packet.CsvHeader() + "\n");
                gz.Write(bytes);
            }
            var t = TelemetryTable.Load(path, new[] { TelemetryTable.RecvTime, "speed" });
            Assert.Equal(0, t.RowCount);
            Assert.True(t.Contains("speed"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
