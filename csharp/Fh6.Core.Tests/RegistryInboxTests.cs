namespace Fh6.Core.Tests;

public sealed class RegistryInboxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-inbox-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly CarSignature Civic = new(2163, 2, 585, 0, 4);

    private static RegistryRequest Req(string? courseId, string? courseName, string? carName, string? setupId, string? setupName) => new()
    {
        CreatedAt = "2026-09-28T21:00:00+09:00", Session = "20260928_210000", RaceStartRecvTime = 1000,
        CourseId = courseId, CourseName = courseName, CourseKind = "dirt", StartX = 100, StartZ = 200, StartYaw = 0.5,
        CarOrdinal = Civic.Ordinal, ClassRaw = Civic.Class, Pi = Civic.Pi, DrivetrainRaw = Civic.Drivetrain, Cylinders = Civic.Cylinders,
        CarName = carName, SetupId = setupId, SetupName = setupName,
    };

    [Fact]
    public void 足した頼みを全部取り出して_ファイルを消す()
    {
        RegistryInbox.Append(_dir, Req("new", "鳥野山", "Civic", "new", "街乗り"));
        RegistryInbox.Append(_dir, Req("new", "鳥野山2", null, null, null));
        Assert.True(File.Exists(Path.Combine(_dir, RegistryInbox.FileName)));

        var taken = RegistryInbox.Take(_dir, out var warning);
        Assert.Null(warning);
        Assert.Equal(new[] { "鳥野山", "鳥野山2" }, taken.Select(r => r.CourseName));
        Assert.Equal((100.0, 2163, "街乗り"), (taken[0].StartX!.Value, taken[0].CarOrdinal!.Value, taken[0].SetupName!));
        Assert.Empty(Directory.GetFiles(_dir));   // 取り込んだら消す
        Assert.Empty(RegistryInbox.Take(_dir, out _));   // 無ければ空
    }

    [Fact]
    public void 読めないファイルは残して空にする()
    {
        File.WriteAllText(Path.Combine(_dir, RegistryInbox.FileName), "{ 壊れている");
        Assert.Empty(RegistryInbox.Take(_dir, out var warning));
        Assert.Contains("読めません", warning);
        Assert.Single(Directory.GetFiles(_dir, "*.broken"));
    }

    [Fact]
    public void 新しいコース_車名_セッティングを登録する()
    {
        var reg = new Registry();
        var changes = RegistryInbox.Apply(reg, Req("new", "鳥野山", "Civic", "new", "街乗り"));
        Assert.Equal(3, changes.Count);
        var c = Assert.Single(reg.Courses);
        Assert.Equal(("鳥野山", "dirt", 100.0, 200.0, 0.5), (c.Name, c.Kind, c.X, c.Z, c.Yaw));
        Assert.Equal("Civic", reg.CarName(2163));
        Assert.Equal("街乗り", Assert.Single(reg.FindSetups(Civic)).Name);

        // 同じ頼みをもう一度取り込んでも、二重には登録しない(同じ場所・同じ名前のコース、同じ組・同じ名前のセッティング)
        Assert.Empty(RegistryInbox.Apply(reg, Req("new", "鳥野山", "Civic", "new", "街乗り")));
        Assert.Single(reg.Courses);
        Assert.Single(reg.FindSetups(Civic));
    }

    [Fact]
    public void 登録済みのものは名前を変える_見つからない_idは飛ばす()
    {
        var reg = new Registry();
        var course = reg.AddCourse("鳥野山", "road", 100, 200, 0.5, null);
        var setup = reg.AddSetup(Civic, "街乗り", null);
        reg.SetCarName(2163, "Civic");

        var changes = RegistryInbox.Apply(reg, Req(course.Id, "鳥野山サーキット", "Honda Civic Type R", setup.Id, "サーキット"));
        Assert.Equal(3, changes.Count);
        Assert.Equal("鳥野山サーキット", course.Name);
        Assert.Equal("Honda Civic Type R", reg.CarName(2163));
        Assert.Equal("サーキット", setup.Name);

        Assert.Empty(RegistryInbox.Apply(reg, Req("R9999", "無い", null, "S9999", "無い")));   // 消されたなど
        Assert.Single(reg.Courses);
    }
}
