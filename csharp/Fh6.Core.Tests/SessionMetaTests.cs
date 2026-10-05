using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Core.Tests;

public sealed class SessionMetaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void 知らないキーは読んで書き戻しても残る()
    {
        var json = """
        {
          "format": "fh6-recorder/1",
          "future_top": { "a": 1 },
          "session": { "label": "x", "future_session": "s" },
          "files": { "telemetry_csv": "telemetry.csv.gz", "future_files": true },
          "video": { "sync_samples": [[1.0, 0.5, 20]], "future_video": [1, 2, 3] },
          "stats": { "packets_received": 10, "future_stats": 1.5 }
        }
        """;
        File.WriteAllText(Path.Combine(_dir, SessionMeta.FileName), json);

        var meta = SessionMeta.Load(_dir);
        meta.Save(_dir);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SessionMeta.FileName)))!;
        Assert.Equal(1, (int)saved["future_top"]!["a"]!);
        Assert.Equal("s", (string)saved["session"]!["future_session"]!);
        Assert.True((bool)saved["files"]!["future_files"]!);
        Assert.Equal(3, saved["video"]!["future_video"]!.AsArray().Count);
        Assert.Equal(1.5, (double)saved["stats"]!["future_stats"]!);
        // 既知のキーもそのまま
        Assert.Equal("x", (string)saved["session"]!["label"]!);
        Assert.Single(saved["video"]!["sync_samples"]!.AsArray());
    }

    [Fact]
    public void manual_syncを読み書きできる()
    {
        var meta = new SessionMeta();
        meta.Video.ManualSync.Add(new ManualSyncPoint { RecvTime = 1790000123.456, VideoSec = 98.733, Note = "着地", CreatedAt = "2026-09-28T21:03:00+09:00" });
        meta.Save(_dir);

        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SessionMeta.FileName)))!;
        var p = node["video"]!["manual_sync"]![0]!;
        Assert.Equal(1790000123.456, (double)p["recv_time"]!);
        Assert.Equal(98.733, (double)p["video_sec"]!);
        Assert.Equal("着地", (string)p["note"]!);

        var back = SessionMeta.Load(_dir);
        Assert.Equal(98.733, back.Video.ManualSync.Single().VideoSec);
    }

    [Fact]
    public void manual_syncが無い古い記録も読める()
    {
        File.WriteAllText(Path.Combine(_dir, SessionMeta.FileName), """{ "video": { "sync_samples": [] } }""");
        var meta = SessionMeta.Load(_dir);
        Assert.Empty(meta.Video.ManualSync);
    }

    [Fact]
    public void 動画はfiles_videoを優先しなければフォルダ内のmp4()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.mp4"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(_dir, "b.mp4"), new byte[] { 0 });

        var meta = new SessionMeta();
        meta.Files.Video = "b.mp4";
        Assert.Equal(Path.Combine(_dir, "b.mp4"), meta.FindVideo(_dir));

        meta.Files.Video = "missing.mp4";
        Assert.EndsWith(".mp4", meta.FindVideo(_dir));
    }
}
