using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Player.App.Tests;

internal static class TestSetup
{
    /// <summary>テストは日本語の画面で動かす(CI の英語の Windows でも同じ結果にするため)</summary>
    [ModuleInitializer]
    internal static void UseJapanese() => Strings.Use(Strings.Japanese);

    /// <summary>
    /// ログと設定を、普段の %LOCALAPPDATA%\HEISO\Player ではなく一時フォルダに書く(PlayerLog と PlayerSettings はそこへ書く)。
    /// PlayerPaths を初めて使う前に決める必要があるので、ここで
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateDataDir() =>
        Environment.SetEnvironmentVariable(PlayerPaths.DataDirVariable, Path.Combine(Path.GetTempPath(), $"heiso-app-tests-{Environment.ProcessId}"));
}

/// <summary>
/// 画面へ渡す JSON を固定する。改修(docs/tasks/009-architecture-restructure.md)の途中で、計算の結果と JSON の形が変わっていないことを確かめる。
/// JSON の期待値は golden/ にある。わざと変えたときだけ、環境変数 HEISO_UPDATE_GOLDEN=1 を付けてテストを回すと書き直す
/// </summary>
public sealed class SessionJsonGoldenTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = MessageJson.Options;   // 画面へ送るときと同じ
    private const double Tolerance = 1e-9;   // 計算の結果の、桁の末尾だけの違いは見ない

    private readonly string _root = Path.Combine(Path.GetTempPath(), "heiso-golden-" + Guid.NewGuid().ToString("N"));
    private readonly LoadedSession _session;
    private readonly VideoTimeMap _map = VideoTimeMap.FromZero(SyntheticSession.Zero);

    public SessionJsonGoldenTests()
    {
        var folder = Path.Combine(_root, "20260928_120000");
        SyntheticSession.Write(folder);
        _session = LoadedSession.Load(folder);
        _session.UpdateProfiles(new ProfileStore(Path.Combine(_root, "profiles")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 系列のJSONが変わらない() => Check("series", _session.BuildSeries(_map));

    [Fact]
    public void 印のJSONが変わらない() => Check("marks", _session.BuildMarks(_map));

    [Fact]
    public void 計測のJSONが変わらない() => Check("measure", _session.BuildMeasure());

    [Fact]
    public void リプレイの系列のJSONが変わらない()
    {
        var replay = Replay(out var map);
        Check("replay-series", replay.BuildSeries(map));
    }

    [Fact]
    public void リプレイの印のJSONが変わらない()
    {
        var replay = Replay(out var map);
        Check("replay-marks", replay.BuildMarks(map));
    }

    /// <summary>
    /// リプレイを割り当てて表示したときの仮想セッションと変換(画面の外枠の作り方と同じ)。
    /// リプレイの先頭は動画の 5 秒、録画の fps は 30
    /// </summary>
    private LoadedSession Replay(out VideoTimeMap map)
    {
        var replay = _session.ForReplay(0, null, 30);
        replay.ApplyProfiles(_session.ProfileFiles);
        map = VideoTimeMap.FromZero(replay.VirtualAtRaceTime(0) - 5);
        return replay;
    }

    /// <summary>合成データが、確かめたいものを実際に含んでいるか。検出されなければ、JSON を固定しても守りにならない</summary>
    [Fact]
    public void 合成データに検出の対象が入っている()
    {
        Assert.Single(_session.Races);
        Assert.Contains(_session.Starts, s => s.IsRace);
        Assert.NotEmpty(_session.Contacts);
        Assert.NotEmpty(_session.Jumps);
        Assert.Contains(_session.Excursions, e => e.Consequential);
        Assert.NotEmpty(_session.Power);
        Assert.NotEmpty(_session.Shifts);
        Assert.NotEmpty(_session.Laps);
        Assert.NotEmpty(RaceSkips.Find(_session.Table, _session.Races.Select(r => r.Segment)));
        var measure = JsonSerializer.SerializeToNode(_session.BuildMeasure(), JsonOptions)!;
        Assert.NotNull(Assert.Single(measure["cars"]!.AsArray())!["curve"]);
    }

    /// <summary>合成データでは null になり、golden で名前を確かめられない入れ物</summary>
    [Fact]
    public void 合成データで空になる入れ物の名前が変わらない()
    {
        Assert.Equal("""{"recordings":2,"races":3,"other":true}""", JsonSerializer.Serialize(new LoadedSession.PowerSource(2, 3, true), JsonOptions));
        Assert.Equal("""{"gear":4,"kmh":250.5,"rpm":7000}""", JsonSerializer.Serialize(new MeasureTop(4, 250.5, 7000), JsonOptions));
    }

    private static void Check(string name, object value)
    {
        var actual = JsonSerializer.SerializeToNode(value, JsonOptions)!;
        var path = Path.Combine(GoldenFolder(), name + ".json");
        if (Environment.GetEnvironmentVariable("HEISO_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenFolder());
            File.WriteAllText(path, actual.ToJsonString() + "\n");
            return;
        }
        var expected = JsonNode.Parse(File.ReadAllText(path))!;
        var differences = new List<string>();
        Compare(name, expected, actual, differences);
        Assert.True(differences.Count == 0, $"{name} の JSON が {differences.Count} か所変わった。最初の 20 か所:\n" + string.Join("\n", differences.Take(20)));
    }

    private static void Compare(string at, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(key)) differences.Add($"{at}.{key}: 増えた");
                    else if (!a.ContainsKey(key)) differences.Add($"{at}.{key}: 無くなった");
                    else Compare($"{at}.{key}", e[key], a[key], differences);
                }
                return;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count) { differences.Add($"{at}: 長さが {e.Count} から {a.Count} になった"); return; }
                for (int i = 0; i < e.Count; i++) Compare($"{at}[{i}]", e[i], a[i], differences);
                return;
            case (JsonValue e, JsonValue a):
                if (e.GetValueKind() == JsonValueKind.Number && a.GetValueKind() == JsonValueKind.Number)
                {
                    double x = e.GetValue<double>(), y = a.GetValue<double>();
                    if (Math.Abs(x - y) > Tolerance * Math.Max(1, Math.Abs(x))) differences.Add($"{at}: {x} から {y} になった");
                }
                else if (e.ToJsonString() != a.ToJsonString()) differences.Add($"{at}: {e.ToJsonString()} から {a.ToJsonString()} になった");
                return;
            default:
                differences.Add($"{at}: {expected?.ToJsonString() ?? "null"} から {actual?.ToJsonString() ?? "null"} になった");
                return;
        }
    }

    private static string GoldenFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "csharp", "HEISO.slnx")))
                return Path.Combine(d.FullName, "csharp", "Fh6.Player.App.Tests", "golden");
        throw new InvalidOperationException("リポジトリの場所が分かりません");
    }
}
