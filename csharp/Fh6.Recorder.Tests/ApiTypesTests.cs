using System.Text.Json;
using Fh6.TestTools;

namespace Fh6.Recorder.Tests;

/// <summary>記録アプリの画面とのやりとりの型(ApiTypes.cs)と、そこから作る JS の型定義(Fh6.Recorder/api.d.ts)</summary>
public sealed class ApiTypesTests
{
    private static string Generate()
    {
        var g = new TypeScriptWriter(
            "自動で作ったファイル。手で直さない。",
            "元は csharp/Fh6.Recorder/ApiTypes.cs。C# を変えたら、",
            "環境変数 HEISO_UPDATE_GOLDEN=1 を付けて dotnet test csharp/Fh6.Recorder.Tests を回すと書き直す(ApiTypesTests)");
        foreach (var t in new[] { typeof(StartRequest), typeof(QualityRequest), typeof(SceneRequest), typeof(MarkerRequest),
                                  typeof(CarRequest), typeof(CourseRequest), typeof(DismissRequest), typeof(LastRaceRequest) })
            g.Root(t, "画面 → C#(POST の本文)", optional: true);
        g.Root(typeof(RecorderStatus), "/api/status の答え(操作の POST の多くも、これを返す)");
        g.Root(typeof(MarkerResult), "/api/marker の答え");
        g.Root(typeof(ConnectInfo), "/api/connect-info の答え");
        g.Root(typeof(ApiError), "うまくいかなかったときの答え(4xx)");
        g.Root(typeof(Fh6.Core.I18nData), "画面の言語の辞書(window.HEISO_I18N。型は Fh6.Core の Strings.cs)");
        return g.ToString();
    }

    /// <summary>JS の型定義が C# の型と同じか。C# を変えたら、環境変数 HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと書き直す</summary>
    [Fact]
    public void JSの型定義が最新()
    {
        var path = Path.Combine(RepoCsharp(), "Fh6.Recorder", "api.d.ts");
        var generated = Generate();
        if (Environment.GetEnvironmentVariable("HEISO_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        Assert.True(File.Exists(path), $"{path} がありません。HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと作ります");
        var current = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(current == generated, "api.d.ts が C# の型と違う。HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと書き直す");
    }

    [Fact]
    public void 確認のカードは型の印なしで_種類ごとのキーだけを書く()
    {
        IReadOnlyList<RacePrompt> prompts =
        [
            new CoursePromptStatus("P0001", "course", "12:00", new StartPoint(1, 2, 3)),
            new UnknownPromptStatus("P0002", "unknown", "12:01"),
        ];
        var json = JsonSerializer.Serialize(prompts, JsonSerializerOptions.Web);
        Assert.Equal("""[{"id":"P0001","type":"course","created":"12:00","start":{"x":1,"z":2,"yaw":3}},{"id":"P0002","type":"unknown","created":"12:01"}]""", json);
    }

    private static string RepoCsharp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx"))) return Path.Combine(d.FullName, "csharp");
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }
}
