using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Core.Tests;

/// <summary>割り当てた動画(session.json の video_alt)の読み書きと、動画の見つけ方</summary>
public sealed class VideoAltTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SessionJsonPath => Path.Combine(_dir, SessionMeta.FileName);

    private void WriteSession(bool complete = true, string extra = "") =>
        File.WriteAllText(SessionJsonPath, $$"""{ "format": "fh6-recorder/1", "complete": {{(complete ? "true" : "false")}}, "video": { "sync_samples": [] }, "future_top": 7{{extra}} }""");

    private static VideoAlt Replay() => new()
    {
        Kind = VideoAlt.KindReplay,
        Content = VideoAlt.ContentReplay,
        Label = "リプレイ(追従カメラ)",
        RaceStartRecvTime = 1791635814.2,
        Sync = new VideoAltSync
        {
            Basis = VideoAltSync.BasisRaceTime,
            Method = VideoAltSync.MethodAutoBlackMotion,
            Confidence = "high",
            ReplayZeroVideoSec = 276.57,
            ReplayEndVideoSec = 507.83,
            CleanDurationSec = 231.26,
            Check = new VideoAltCheck { Corr = 0.49, LagSec = -0.3 },
        },
    };

    [Fact]
    public void 保存すると形式どおりのキーで書き_読み戻せる()
    {
        WriteSession();
        Assert.Null(SessionMeta.SaveVideoAlt(_dir, Replay()));

        var node = JsonNode.Parse(File.ReadAllText(SessionJsonPath))!;
        var a = node["video_alt"]![0]!;
        Assert.Equal("alt-1", (string)a["id"]!);
        Assert.Equal("replay", (string)a["kind"]!);
        Assert.Equal("race_time", (string)a["sync"]!["basis"]!);
        Assert.Equal(276.57, (double)a["sync"]!["replay_zero_video_sec"]!);
        Assert.Equal(0.49, (double)a["sync"]!["check"]!["corr"]!);
        Assert.NotNull((string?)a["created_at"]);
        Assert.Equal(7, (int)node["future_top"]!);   // 知らないキーは残る

        var back = SessionMeta.Load(_dir).VideoAlt!.Single();
        Assert.Equal(231.26, back.Sync.CleanDurationSec);
        Assert.Equal("リプレイ(追従カメラ)", back.Label);
    }

    [Fact]
    public void 同じidなら置き換え_無ければ番号を振って足す_外すと最後はキーごと消える()
    {
        WriteSession();
        SessionMeta.SaveVideoAlt(_dir, Replay());
        SessionMeta.SaveVideoAlt(_dir, new VideoAlt { Content = VideoAlt.ContentLive });
        var list = SessionMeta.Load(_dir).VideoAlt!;
        Assert.Equal(new[] { "alt-1", "alt-2" }, list.Select(a => a.Id));

        var first = list[0];
        first.Sync.ReplayZeroVideoSec = 277;
        SessionMeta.SaveVideoAlt(_dir, first);
        list = SessionMeta.Load(_dir).VideoAlt!;
        Assert.Equal(2, list.Count);
        Assert.Equal(277, list[0].Sync.ReplayZeroVideoSec);

        SessionMeta.RemoveVideoAlt(_dir, "alt-1");
        Assert.Equal("alt-2", SessionMeta.Load(_dir).VideoAlt!.Single().Id);
        SessionMeta.RemoveVideoAlt(_dir, "alt-2");
        Assert.Null(JsonNode.Parse(File.ReadAllText(SessionJsonPath))!["video_alt"]);
    }

    [Fact]
    public void 割り当てが無い記録にはキーを書かない()
    {
        WriteSession();
        SessionMeta.Load(_dir).Save(_dir);
        Assert.Null(JsonNode.Parse(File.ReadAllText(SessionJsonPath))!["video_alt"]);
    }

    [Fact]
    public void 記録中の記録には書かない()
    {
        WriteSession(complete: false);
        Assert.NotNull(SessionMeta.SaveVideoAlt(_dir, Replay()));
        Assert.Null(SessionMeta.Load(_dir).VideoAlt);
    }

    [Fact]
    public void 知らないキーは割り当ての中でも残る()
    {
        WriteSession(extra: """, "video_alt": [ { "id": "alt-1", "future_alt": "x", "sync": { "basis": "recv_time", "future_sync": 1 }, "file": { "future_file": true } } ]""");
        var meta = SessionMeta.Load(_dir);
        meta.Save(_dir);
        var a = JsonNode.Parse(File.ReadAllText(SessionJsonPath))!["video_alt"]![0]!;
        Assert.Equal("x", (string)a["future_alt"]!);
        Assert.Equal(1, (int)a["sync"]!["future_sync"]!);
        Assert.True((bool)a["file"]!["future_file"]!);
    }

    [Fact]
    public void フォルダの中の動画は相対パスでも見つけ_フォルダごと動かしても見つかる()
    {
        var video = Path.Combine(_dir, "rec.mp4");
        File.WriteAllBytes(video, new byte[100]);
        var alt = new VideoAlt();
        alt.SetPath(_dir, video);
        alt.File.DurationSec = 10;
        Assert.Equal("rec.mp4", alt.PathRel);
        Assert.Equal(100, alt.File.SizeBytes);

        var moved = Directory.CreateTempSubdirectory("heiso-test-moved-").FullName;
        try
        {
            File.Move(video, Path.Combine(moved, "rec.mp4"));
            var loc = alt.Locate(moved, _ => 10);
            Assert.Equal(VideoAltStatus.Found, loc.Status);
            Assert.Equal(Path.Combine(moved, "rec.mp4"), loc.FoundPath);
        }
        finally { Directory.Delete(moved, true); }
    }

    [Fact]
    public void 無ければMissing_サイズか長さが違えばChanged()
    {
        var other = Directory.CreateTempSubdirectory("heiso-test-ext-").FullName;
        try
        {
            var video = Path.Combine(other, "replay.mp4");   // フォルダの外: path だけ
            File.WriteAllBytes(video, new byte[100]);
            var alt = new VideoAlt();
            alt.SetPath(_dir, video);
            alt.File.DurationSec = 10;
            Assert.Null(alt.PathRel);

            Assert.Equal(VideoAltStatus.Found, alt.Locate(_dir, _ => 10.2).Status);
            Assert.Equal(VideoAltStatus.Changed, alt.Locate(_dir, _ => 12).Status);
            File.WriteAllBytes(video, new byte[50]);
            Assert.Equal(VideoAltStatus.Changed, alt.Locate(_dir, _ => 10).Status);
            File.Delete(video);
            Assert.Equal(VideoAltStatus.Missing, alt.Locate(_dir, _ => 10).Status);
        }
        finally { Directory.Delete(other, true); }
    }

    [Fact]
    public void 割り当てた動画の時刻の変換()
    {
        // リプレイ: 仮想の起点 1000(レースタイム 0)、リプレイの先頭が動画 276.57 秒 → 動画 0 秒 = 1000 − 276.57
        var replay = VideoTimeMap.FromZero(1000 - 276.57);
        Assert.Equal(276.57, replay.ToVideoSec(1000), 6);
        Assert.Equal(1010, replay.ToRecvTime(286.57), 6);

        // ライブ: 「この時刻 = 動画のこの秒」の 1 点
        var live = VideoTimeMap.ForPoint(new ManualSyncPoint { RecvTime = 1790000123.5, VideoSec = 98.5 });
        Assert.Equal(98.5, live.ToVideoSec(1790000123.5), 6);
        Assert.Equal(108.5, live.ToVideoSec(1790000133.5), 6);
    }
}
