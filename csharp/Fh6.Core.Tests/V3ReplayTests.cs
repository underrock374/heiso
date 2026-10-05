using System.Diagnostics;
using System.Text.Json;
using Fh6.Core;

namespace Fh6.Core.Tests;

/// <summary>環境変数 HEISO_TEST_LOGS(記録アプリの保存先フォルダ)があるときだけ動かす</summary>
public sealed class RealLogsFactAttribute : FactAttribute
{
    public const string EnvName = "HEISO_TEST_LOGS";

    public RealLogsFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable(EnvName);
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d))
            Skip = $"{EnvName} に記録のフォルダを指定したときだけ動かす(実データは大きいのでリポジトリに入れていない)";
    }
}

/// <summary>ロガー v3 の判定(python/tools/replay_v3.py)と、C# に移した RaceTracker の結果が一致するか</summary>
public class V3ReplayTests
{
    [RealLogsFact]
    public void 実データでv3と出来事が一致する()
    {
        var root = Environment.GetEnvironmentVariable(RealLogsFactAttribute.EnvName)!;
        var script = FindScript();
        var folders = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, SessionWriter.CsvName))).ToList();
        Assert.NotEmpty(folders);

        foreach (var folder in folders)
        {
            var expected = RunV3(script, folder);
            var t = TelemetryTable.Load(Path.Combine(folder, SessionWriter.CsvName), RaceSample.Columns);
            var actual = RaceTracker.Replay(t).Select(e => e.Event).ToList();

            var name = Path.GetFileName(folder);
            Assert.True(expected.Count == actual.Count, $"{name}: 出来事の数 v3 {expected.Count} / C# {actual.Count}");
            for (int i = 0; i < expected.Count; i++)
            {
                var x = expected[i];
                var a = actual[i];
                var where = $"{name} の {i + 1} 件目({x.GetProperty("kind").GetString()})";
                // 時刻は timestamp_ms で比べる。60fps 近くでは同じ値が続くので recv_time(v3 は 3 桁に丸める)も比べる
                Assert.True(x.GetProperty("timestamp_ms").GetDouble() == a.TimestampMs, $"{where}: timestamp_ms");
                Assert.True(Math.Abs(x.GetProperty("recv_time").GetDouble() - a.RecvTime) < 0.0006, $"{where}: recv_time");
                switch (x.GetProperty("kind").GetString())
                {
                    case "car_detected":
                        var c = Assert.IsType<CarChanged>(a);
                        Assert.Equal(x.GetProperty("ordinal").GetInt32(), c.Car.Ordinal);
                        Assert.Equal(x.GetProperty("pi").GetInt32(), c.Car.Pi);
                        Assert.Equal(x.GetProperty("cyl").GetInt32(), c.Car.Cylinders);
                        Assert.Equal(x.GetProperty("changed").EnumerateArray().Select(e => e.GetString()!), c.Changes);
                        break;
                    case "race_start":
                        var s = Assert.IsType<RaceStarted>(a);
                        Assert.Equal(x.GetProperty("restart").GetBoolean(), s.Restart);
                        var st = x.GetProperty("start");
                        Assert.Equal(st.GetProperty("x").GetDouble(), Math.Round(s.X, 2));
                        Assert.Equal(st.GetProperty("z").GetDouble(), Math.Round(s.Z, 2));
                        Assert.Equal(st.GetProperty("yaw").GetDouble(), Math.Round(s.Yaw, 5));
                        break;
                    case "race_end":
                        var e = Assert.IsType<RaceEnded>(a);
                        Assert.Equal(x.GetProperty("last_speed_kmh").GetDouble(), e.LastSpeedKmh);
                        break;
                    default:
                        Assert.Fail($"{where}: 知らない出来事");
                        break;
                }
            }
        }
    }

    private static List<JsonElement> RunV3(string script, string folder)
    {
        var psi = new ProcessStartInfo("python", new[] { script, folder })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"replay_v3.py が失敗しました: {stderr.Result}");
        return JsonDocument.Parse(stdout.Result).RootElement.EnumerateArray().ToList();
    }

    private static string FindScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "python", "tools", "replay_v3.py");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("python/tools/replay_v3.py が見つかりません");
    }
}
