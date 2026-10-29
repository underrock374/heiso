using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class SessionCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-catalog-" + Guid.NewGuid().ToString("N"));

    public SessionCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>記録のフォルダを作る(session.json と、あれば動画)</summary>
    private string Session(string name, bool complete = true, bool video = false, params SessionEvent[] events)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var meta = new SessionMeta { Complete = complete, Events = events.ToList() };
        meta.Session.StartedRecvTime = 1000;
        meta.Session.EndedRecvTime = 1300;
        File.WriteAllText(Path.Combine(dir, SessionMeta.FileName), JsonSerializer.Serialize(meta, SessionMeta.JsonOptions));
        if (video) File.WriteAllBytes(Path.Combine(dir, "2026-10-18 01-53-40.mp4"), new byte[100]);
        return dir;
    }

    private static SessionEvent Ev(string type, double t, JsonObject data) => new() { RecvTime = t, Type = type, Data = data };

    private static SessionEvent Car(double t, int ordinal, int pi, string? name = null) =>
        Ev("car_detected", t, new JsonObject { ["ordinal"] = ordinal, ["pi"] = pi, ["name"] = name });

    [Theory]
    [InlineData("20261018_015340_Horizonスタジアム・スクランブル", "20261018_015340", "Horizonスタジアム・スクランブル")]
    [InlineData("20260927_211710#夏_鳥野山", "20260927_211710", "夏_鳥野山")]
    [InlineData("20261018_020532", "20261018_020532", "")]
    [InlineData("video", "", "video")]
    public void フォルダ名を日時と後ろに分ける(string name, string stamp, string tail)
    {
        Assert.Equal((stamp, tail), SessionCatalog.SplitName(name));
    }

    [Fact]
    public void 一覧は新しい順で_車は車種ごと_コースとレースの数と動画を出す()
    {
        Session("20261018_002105_計測コース1_FocusRS", events: Car(1000, 2357, 588, "Ford Focus RS"));
        Session("20261018_015340_スタジアム", video: true, events: new[]
        {
            Car(1000, 2357, 588),
            Ev("race_start", 1010, new JsonObject { ["course_name"] = "Horizon スタジアム・スクランブル" }),
            Car(1200, 2357, 600),   // PI が違っても同じ車種
            Car(1210, 320, 465),
            Ev("race_start", 1220, new JsonObject()),
        });
        Directory.CreateDirectory(Path.Combine(_root, SessionCatalog.ArchiveDirName));   // session.json の無いフォルダは出さない

        var list = SessionCatalog.List(_root, o => o == 320 ? "Honda CR-X SiR" : null);
        Assert.Equal(new[] { "20261018_015340_スタジアム", "20261018_002105_計測コース1_FocusRS" }, list.Select(s => s.Name));
        var s = list[0];
        Assert.Equal("スタジアム", s.Rest);
        Assert.Equal(new DateTime(2026, 10, 18, 1, 53, 40), s.Started);
        Assert.Equal(2, s.Races);
        Assert.Equal(new[] { "Horizon スタジアム・スクランブル" }, s.Courses);
        Assert.Equal(new[] { new SessionCar(2357, null), new SessionCar(320, "Honda CR-X SiR") }, s.Cars);
        Assert.True(s.HasVideo);
        Assert.Equal(300, s.DurationSec);
        Assert.True(s.Bytes > 100);
        Assert.Equal("Ford Focus RS", list[1].Cars.Single().Name);
        Assert.False(list[1].HasVideo);
    }

    [Fact]
    public void 読めない記録も理由を付けて出す()
    {
        var dir = Path.Combine(_root, "20261018_010000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SessionMeta.FileName), "{ 壊れている");
        var s = Assert.Single(SessionCatalog.List(_root));
        Assert.NotNull(s.Error);
        Assert.Equal(new DateTime(2026, 10, 18, 1, 0, 0), s.Started);
    }

    [Fact]
    public void 開いている記録と記録中と止めてすぐのいちばん新しい記録は変えられない()
    {
        var dir = Session("20261018_020532");
        var s = SessionCatalog.Summarize(dir);
        var now = s.MetaWrittenUtc;
        Assert.NotNull(SessionCatalog.CannotChange(s, dir + "\\", newest: false, now));                    // 開いている
        Assert.Null(SessionCatalog.CannotChange(s, null, newest: false, now));                              // いちばん新しくなければよい
        Assert.NotNull(SessionCatalog.CannotChange(s, null, newest: true, now.AddMinutes(10)));              // 止めて 10 分
        Assert.Null(SessionCatalog.CannotChange(s, null, newest: true, now.AddMinutes(31)));

        var rec = SessionCatalog.Summarize(Session("20261018_030000", complete: false));
        Assert.NotNull(SessionCatalog.CannotChange(rec, null, newest: false, rec.MetaWrittenUtc.AddSeconds(30)));   // 記録中
        Assert.Null(SessionCatalog.CannotChange(rec, null, newest: false, rec.MetaWrittenUtc.AddMinutes(10)));      // 途中で終わったまま
    }

    [Fact]
    public void アーカイブへ移して戻す_同じ名前があれば移さない()
    {
        var dir = Session("20261018_012153_枝豆サーキット", video: true);
        var archive = Path.Combine(_root, SessionCatalog.ArchiveDirName);
        var moved = SessionCatalog.Move(dir, archive);
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(moved, "2026-10-18 01-53-40.mp4")));
        Assert.Equal(_root, SessionCatalog.SaveRootOf(moved));
        Assert.Single(SessionCatalog.List(archive));

        Session("20261018_012153_枝豆サーキット");   // 保存先に同じ名前ができた
        Assert.Throws<IOException>(() => SessionCatalog.Move(moved, _root));
        Assert.True(Directory.Exists(moved));
    }

    [Fact]
    public void 名前の変更は日時を残す_使えない文字と同じ名前は断る()
    {
        var dir = Session("20261018_020532");
        var renamed = SessionCatalog.Rename(dir, " スタジアム・スクランブル_ブレーキ ");
        Assert.Equal("20261018_020532_スタジアム・スクランブル_ブレーキ", Path.GetFileName(renamed));
        Assert.Equal("20261018_020532", Path.GetFileName(SessionCatalog.Rename(renamed, "")));

        Assert.NotNull(SessionCatalog.CheckRest("a/b"));
        Assert.NotNull(SessionCatalog.CheckRest("a?"));
        Assert.NotNull(SessionCatalog.CheckRest("末尾."));
        Assert.Null(SessionCatalog.CheckRest("計測コース1_FocusRS"));
        Session("20261018_020532_重複");
        Assert.Throws<IOException>(() => SessionCatalog.Rename(Path.Combine(_root, "20261018_020532"), "重複"));
    }

    [Fact]
    public void 名前を変えたらプロファイルの中の名前も直す()
    {
        var store = new ProfileStore(Path.Combine(_root, "profiles"));
        var car = new CarSignature(2357, 2, 588, 2, 4);
        var f = store.Load(car);
        f.Parts.Add(new ProfilePart { Folder = "20261018_020532", RaceStartRecvTime = 1 });
        f.Parts.Add(new ProfilePart { Folder = "20261018_015340", RaceStartRecvTime = 2 });
        store.Save(f);
        Assert.Equal(1, store.RenameFolder("20261018_020532", "20261018_020532_ブレーキ"));
        Assert.Equal(new[] { "20261018_020532_ブレーキ", "20261018_015340" }, store.Load(car).Parts.Select(p => p.Folder));
        Assert.Equal(0, store.RenameFolder("無い", "x"));
    }
}
