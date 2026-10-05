using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Fh6.Core;

namespace Fh6.Core.Tests;

public sealed class SessionWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Packet(int isRaceOn, uint timestampMs)
    {
        var buf = new byte[Fh6Packet.Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, isRaceOn);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), timestampMs);
        return buf;
    }

    /// <summary>is_race_on の並びを 1/30 秒間隔で書き、CSV のデータ行(ヘッダを除く)と統計を返す</summary>
    private (List<string> Rows, WriterStats Stats) Write(IEnumerable<int> raceOn)
    {
        const double t0 = 1_790_000_000.0;
        var v = new double[Fh6Packet.Fields.Length];
        var w = new SessionWriter(_dir, saveRaw: false);
        int i = 0;
        foreach (var r in raceOn)
        {
            var p = Packet(r, (uint)(1000 + i * 33));
            Fh6Packet.Parse(p, v);
            w.Write(t0 + i / 30.0, p, v);
            i++;
        }
        // 停止区間の末尾行は Dispose で書き出されるので、統計は閉じてから取る
        w.Dispose();
        var stats = w.GetStats();

        using var gz = new GZipStream(File.OpenRead(Path.Combine(_dir, SessionWriter.CsvName)), CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal(Fh6Packet.CsvHeader(), lines[0]);
        return (lines.Skip(1).ToList(), stats);
    }

    private static uint TimestampOf(string row) => uint.Parse(row.Split(',')[2]);

    [Fact]
    public void 停止中が40行続くと先頭行と末尾行だけ残る()
    {
        var (rows, stats) = Write(Enumerable.Repeat(0, 40));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1000u, TimestampOf(rows[0]));
        Assert.Equal(1000u + 39 * 33, TimestampOf(rows[1]));
        Assert.Equal(38, stats.IdleRowsCollapsed);
        Assert.Equal(40, stats.PacketsReceived);
        Assert.Equal(2, stats.RowsWritten);

        // session.json の stats には idle_rows_collapsed として出る
        var json = JsonSerializer.SerializeToElement(stats, SessionMeta.JsonOptions);
        Assert.Equal(38, json.GetProperty("idle_rows_collapsed").GetInt64());
    }

    [Fact]
    public void 走行中の行はそのまま残り停止区間だけ圧縮される()
    {
        var input = Enumerable.Repeat(1, 5).Concat(Enumerable.Repeat(0, 40)).Concat(Enumerable.Repeat(1, 5));
        var (rows, stats) = Write(input);

        Assert.Equal(5 + 2 + 5, rows.Count);
        Assert.Equal(1000u + 5 * 33, TimestampOf(rows[5]));
        Assert.Equal(1000u + 44 * 33, TimestampOf(rows[6]));
        Assert.Equal(38, stats.IdleRowsCollapsed);
        Assert.Equal(2, stats.RaceStateTransitions);
    }
}
