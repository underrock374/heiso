using System.Diagnostics;
using System.IO;
using System.Text;

namespace Fh6.Player;

/// <summary>
/// 再生アプリのログ(落ちたときの原因を探るため)。%LOCALAPPDATA%\HEISO\Player\logs\player-yyyyMMdd.log に 1 行ずつ足す。
/// 7 日より古いログは消す。起動中の印(running-<プロセス番号>.flag)で、前回が正常に終わったかも調べる。
/// どのスレッドから呼んでもよい。書けなくてもアプリは止めない。
/// </summary>
public static class PlayerLog
{
    public const int KeepDays = 7;

    private static readonly object Lock = new();
    private static string? _file;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HEISO", "Player", "logs");

    /// <summary>Windows がクラッシュダンプを置く場所(Windows エラー報告のローカルダンプが有効な PC だけ)</summary>
    public static string CrashDumpDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");

    /// <summary>起動中の印。起動ごとに別のファイルにする(2 つ同時に開いても、互いの印を異常終了と見ないように)</summary>
    private static string FlagPath => Path.Combine(Dir, $"running-{Environment.ProcessId}.flag");

    /// <summary>
    /// 前回、正常に終わらなかったなら、そのときの起動時刻。印が残っていて、そのプロセスがもう動いていないもの。正常なら null
    /// </summary>
    public static DateTime? PreviousUncleanStart { get; private set; }

    /// <summary>起動時に 1 回呼ぶ。古いログを消し、前回の起動中の印を調べ、今回の印を置く</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            FindUncleanExits();
            using var me = Process.GetCurrentProcess();
            File.WriteAllText(FlagPath, $"{Environment.ProcessId} {me.StartTime.Ticks}");
            foreach (var f in Directory.EnumerateFiles(Dir, "player-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
        }
        catch { /* ログのための準備ができなくても起動は続ける */ }

        Info("---- 起動 ----");
        Info($"HEISO Player {typeof(PlayerLog).Assembly.GetName().Version?.ToString(3)} / .NET {Environment.Version} / {Environment.OSVersion.VersionString} / {Environment.ProcessId}");
        if (PreviousUncleanStart is DateTime t) Warn($"前回({t:yyyy-MM-dd HH:mm:ss} に起動)は正常に終わっていない");
    }

    /// <summary>
    /// 残っている起動中の印を調べる。そのプロセスが今も動いていれば(もう 1 つの Player)そのまま、
    /// 動いていなければ正常に終わらなかったものとして、一番新しい起動時刻を覚えて印を消す
    /// </summary>
    private static void FindUncleanExits()
    {
        foreach (var f in Directory.EnumerateFiles(Dir, "running*.flag"))
        {
            try
            {
                var parts = File.ReadAllText(f).Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var ticks) && IsAlive(pid, ticks))
                    continue;
                var started = File.GetLastWriteTime(f);
                if (PreviousUncleanStart is not DateTime t || started > t) PreviousUncleanStart = started;
                File.Delete(f);
            }
            catch { /* 読めない印は飛ばす */ }
        }
    }

    /// <summary>その番号のプロセスが、印を書いたときと同じ起動のまま動いているか(番号は使い回されるので起動時刻も比べる)</summary>
    private static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.StartTime.Ticks == startTicks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>正常に終わるときに呼ぶ(起動中の印を消す)</summary>
    public static void Stop()
    {
        Info("---- 終了 ----");
        try { File.Delete(FlagPath); } catch { }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static bool _writeErrorReported;

    /// <summary>
    /// 1 行足す。ほかのプロセス(もう 1 つの Player、エディター、ウイルス対策など)がファイルを開いていても書けるよう、
    /// 読み書き・削除を許す開き方で追記する(File.AppendAllText は書き込みを許さない開き方なので、同時に開かれていると失敗する)。
    /// それでも書けなければプロセスごとの別ファイルに書き、失敗の理由を write-error-<プロセス番号>.txt に残す
    /// (2026-09-28、Visual Studio から起動した Player でログが 1 行も書かれなかった。その理由を突き止めるため)
    /// </summary>
    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            _file = Path.Combine(Dir, $"player-{now:yyyyMMdd}.log");
            if (TryAppend(_file, line, out var first)) return;
            // 少し待ってもう一度
            Thread.Sleep(20);
            if (TryAppend(_file, line, out _)) return;
            var own = Path.Combine(Dir, $"player-{now:yyyyMMdd}-{Environment.ProcessId}.log");
            TryAppend(own, line, out _);
            if (!_writeErrorReported)
            {
                _writeErrorReported = true;
                try { File.WriteAllText(Path.Combine(Dir, $"write-error-{Environment.ProcessId}.txt"), $"{now:O} {_file}\n{first}"); } catch { }
            }
        }
    }

    private static bool TryAppend(string path, string line, out Exception? error)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Utf8NoBom.GetBytes(line + Environment.NewLine);
            fs.Write(bytes, 0, bytes.Length);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>このアプリの一番新しいクラッシュダンプ(since より後のもの)。無ければ null</summary>
    public static string? LatestCrashDump(DateTime since)
    {
        try
        {
            if (!Directory.Exists(CrashDumpDir)) return null;
            return new DirectoryInfo(CrashDumpDir).EnumerateFiles("HeisoPlayer.exe.*.dmp")
                .Where(f => f.LastWriteTime >= since)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
