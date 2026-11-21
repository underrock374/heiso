using System.Reflection;

namespace Fh6.Player.App.Tests;

/// <summary>画面とのメッセージの型(ViewMessages.cs と HostMessages.cs)と、そこから作る JS の型定義</summary>
public sealed class MessageTypesTests
{
    /// <summary>
    /// JS の型定義(Fh6.Player.App/messages.d.ts)が C# の型と同じか。C# を変えたら、
    /// 環境変数 HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと書き直す
    /// </summary>
    [Fact]
    public void JSの型定義が最新()
    {
        var path = Path.Combine(RepoCsharp(), "Fh6.Player.App", "messages.d.ts");
        var generated = TypeScriptDefinitions.Generate();
        if (Environment.GetEnvironmentVariable("HEISO_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        Assert.True(File.Exists(path), $"{path} がありません。HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと作ります");
        var current = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(current == generated, "messages.d.ts が C# のメッセージの型と違う。HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと書き直す");
    }

    public static TheoryData<Type, string> Directions => new() { { typeof(ViewMessage), "View" }, { typeof(HostMessage), "Host" } };

    [Theory]
    [MemberData(nameof(Directions))]
    public void 型の名前はtypeの値から決まる(Type baseType, string prefix)
    {
        foreach (var (type, name) in MessageJson.Kinds(baseType))
            Assert.Equal(prefix + char.ToUpperInvariant(name[0]) + name[1..], type.Name);
        // 定義した型は全部、振り分けの一覧に載っている
        var listed = MessageJson.Kinds(baseType).Select(k => k.Type).ToHashSet();
        var defined = baseType.Assembly.GetTypes().Where(t => t.IsSubclassOf(baseType) && !t.IsAbstract);
        Assert.All(defined, t => Assert.Contains(t, listed));
    }

    /// <summary>JS は値を省くことがあり、NaN は null で届く。null を許さない値があると、読めずに捨てることになる</summary>
    [Fact]
    public void 画面からのメッセージの値は全部nullを許す()
    {
        var context = new NullabilityInfoContext();
        var notNull = new List<string>();
        var seen = new HashSet<Type>();
        void Check(Type t)
        {
            if (!seen.Add(t)) return;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var nullable = p.PropertyType.IsValueType ? Nullable.GetUnderlyingType(p.PropertyType) != null
                    : context.Create(p).ReadState == NullabilityState.Nullable;
                if (!nullable) notNull.Add($"{t.Name}.{p.Name}");
                var inner = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (inner.IsClass && inner != typeof(string) && !inner.IsArray) Check(inner);
            }
        }
        foreach (var (type, _) in MessageJson.Kinds(typeof(ViewMessage))) Check(type);
        Assert.Empty(notNull);
    }

    [Fact]
    public void 画面からのメッセージを読む()
    {
        var m = Assert.IsType<ViewExport>(MessageJson.Parse("""{"type":"export","race":2,"mode":"720","noAudio":true}""", out var error));
        Assert.Null(error);
        Assert.Equal(new ViewExport(2, "720", true), m);
        // 省いた値は null。type は先頭でなくてもよい。知らないプロパティは無視する
        Assert.Equal(new ViewSelectVideo(null, 12.5, null), MessageJson.Parse("""{"sec":12.5,"type":"selectVideo","extra":1}""", out _));
        var move = Assert.IsType<ViewSessionMove>(MessageJson.Parse("""{"type":"sessionMove","paths":["a",null],"archive":true}""", out _));
        Assert.Equal(new string?[] { "a", null }, move.Paths);
        Assert.True(move.Archive);
        Assert.Equal(new ViewEditRaceNames(0, "new", "コース", null, null, null, null, new ViewAssists("on", null, null, null, "manual")),
            MessageJson.Parse("""{"type":"editRaceNames","race":0,"courseId":"new","courseName":"コース","assists":{"abs":"on","shift":"manual"}}""", out _));
        Assert.IsType<ViewCancelJob>(MessageJson.Parse("""{"type":"cancelJob"}""", out _));
    }

    [Theory]
    [InlineData("""{"type":"noSuchMessage"}""")]
    [InlineData("""{"race":1}""")]
    [InlineData("""{"type":"export","race":"1"}""")]
    [InlineData("""{"type":"export","race":1.5}""")]
    [InlineData("""not json""")]
    [InlineData("""null""")]
    public void 読めないメッセージは例外にせずnullを返す(string json)
    {
        Assert.Null(MessageJson.Parse(json, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void 画面へのメッセージはtypeが先頭でnullも送る()
    {
        Assert.Equal("""{"type":"toast","message":"saved","error":null,"sec":6}""", MessageJson.Serialize(new HostToast("saved", null, 6)));
        Assert.Equal("""{"type":"restore","sec":1.5,"play":false,"quiet":true,"race":null}""", MessageJson.Serialize(new HostRestore(1.5, false, true, null)));
        Assert.Equal("toast", MessageJson.TypeName(new HostToast(null, null, null)));
        Assert.Equal("cancelJob", MessageJson.TypeName(new ViewCancelJob()));
    }

    private static string RepoCsharp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx"))) return Path.Combine(d.FullName, "csharp");
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }
}
