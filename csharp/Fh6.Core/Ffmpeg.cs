using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Fh6.Core;

/// <summary>
/// ffmpeg で動画を作り直す(配布パッケージの 720p の書き出し)。ffmpeg は同梱しない(利用者が各自で入れる。docs/install.md)。
/// エンコーダはその PC で試して選ぶ: NVIDIA(h264_nvenc)→ AMD(h264_amf)→ Intel(h264_qsv)→ CPU(libx264)
/// </summary>
public static class Ffmpeg
{
    public const string ExeName = "ffmpeg.exe";

    /// <summary>試す順のエンコーダと、画面に出す名前</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Encoders = new[]
    {
        ("h264_nvenc", "NVIDIA(NVENC)"),
        ("h264_amf", "AMD(AMF)"),
        ("h264_qsv", "Intel(Quick Sync)"),
        ("libx264", "CPU(x264。遅い)"),
    };

    /// <summary>画面に出すエンコーダの名前(今の言語)</summary>
    public static string EncoderName(string id) => id == "libx264" ? Strings.T("CPU(x264。遅い)") : Encoders.FirstOrDefault(e => e.Id == id).Name ?? id;

    /// <summary>
    /// ffmpeg.exe を探す: 指定された場所(ファイルかフォルダ)→ PATH → 探す場所(アプリのフォルダの近くの ffmpeg フォルダなど。その下の bin も見る)。
    /// 見つからなければ null
    /// </summary>
    public static string? Find(string? configured, IEnumerable<string> searchDirs)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (File.Exists(c)) return Path.GetFullPath(c);
            if (Directory.Exists(c) && FindUnder(c) is string under) return under;
            return null;   // 指定された場所に無ければ、ほかは探さない(どれを使っているか分からなくならないように)
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), ExeName);
                if (dir.Trim().Length > 0 && File.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* PATH の中の変な項目は飛ばす */ }
        }
        foreach (var d in searchDirs)
            if (Directory.Exists(d) && FindUnder(d) is string found) return found;
        return null;
    }

    /// <summary>フォルダの中の ffmpeg.exe(そのまま、bin の中、1 段下のフォルダ(展開した版のフォルダ)の bin の中)</summary>
    private static string? FindUnder(string dir)
    {
        foreach (var p in new[] { Path.Combine(dir, ExeName), Path.Combine(dir, "bin", ExeName) })
            if (File.Exists(p)) return Path.GetFullPath(p);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                foreach (var p in new[] { Path.Combine(sub, ExeName), Path.Combine(sub, "bin", ExeName) })
                    if (File.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        return null;
    }

    /// <summary>ffmpeg の版(1 行目の「ffmpeg version ...」)。動かなければ null</summary>
    public static string? Version(string ffmpeg)
    {
        var (code, output) = Run(ffmpeg, "-hide_banner -version", TimeSpan.FromSeconds(10));
        if (code != 0) return null;
        var line = output.Split('\n').FirstOrDefault() ?? "";
        return line.StartsWith("ffmpeg version ", StringComparison.Ordinal) ? line["ffmpeg version ".Length..].Split(' ')[0] : line.Trim();
    }

    /// <summary>
    /// その PC で使えるエンコーダ(Encoders の順)。小さな真っ黒の動画を実際に作って確かめる
    /// (ffmpeg に入っていても、GPU が無い・ドライバが古いと使えないため)
    /// </summary>
    public static List<string> UsableEncoders(string ffmpeg)
    {
        var list = new List<string>();
        foreach (var (id, _) in Encoders)
        {
            var (code, _) = Run(ffmpeg, $"-v error -hide_banner -f lavfi -i color=black:s=320x180:r=30:d=0.2 -c:v {id} -f null -", TimeSpan.FromSeconds(20));
            if (code == 0) list.Add(id);
        }
        return list;
    }

    /// <summary>作り直しの中身</summary>
    /// <param name="Encoder">エンコーダの ID(Encoders)</param>
    /// <param name="Height">出力の高さ(元がこれより小さければ縮めない)</param>
    /// <param name="VideoKbps">映像のビットレート(平均)。最大はその 1.5 倍</param>
    /// <param name="AudioKbps">音声のビットレート(AAC)</param>
    public sealed record Options(string Encoder, int Height = 720, int VideoKbps = 5000, int AudioKbps = 128, double KeyintSec = 1);

    /// <summary>
    /// 仕様書の配布動画の目安(720p・H.264・キーフレーム 1 秒・5〜8 Mbps)から、元の fps に合わせたビットレート。
    /// 60fps は 6 Mbps、30fps は 4 Mbps
    /// </summary>
    public static int DefaultVideoKbps(double fps) => fps > 40 ? 6000 : 4000;

    /// <summary>
    /// source の fromSec から durationSec 秒を作り直して dest に書く。fromSec のコマから始まり、新しい動画の 0 秒がそのコマになる。
    /// progress には 0〜1 を知らせる。中止されたら ffmpeg を止めて OperationCanceledException。失敗したら InvalidOperationException(ffmpeg の最後の出力を含む)
    /// </summary>
    public static void Transcode(string ffmpeg, string source, string dest, double fromSec, double durationSec, double fps,
                                 Options o, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        int gop = Math.Max(1, (int)Math.Round(fps * o.KeyintSec));
        int max = o.VideoKbps * 3 / 2;
        var rate = o.Encoder switch
        {
            "h264_nvenc" => $"-preset p5 -rc vbr -b:v {o.VideoKbps}k -maxrate {max}k -bufsize {max * 2}k",
            "h264_amf" => $"-quality quality -rc vbr_peak -b:v {o.VideoKbps}k -maxrate {max}k -bufsize {max * 2}k",
            "h264_qsv" => $"-preset slower -b:v {o.VideoKbps}k -maxrate {max}k -bufsize {max * 2}k",
            _ => $"-preset veryfast -b:v {o.VideoKbps}k -maxrate {max}k -bufsize {max * 2}k -sc_threshold 0",
        };
        // -ss を入力の前に置くと、手前のキーフレームから読んで fromSec のコマまで捨てる(作り直すのでコマの単位で正確)
        var args = string.Join(' ',
            "-y -hide_banner -nostats -v error -progress pipe:1",
            $"-ss {fromSec.ToString("0.######", inv)} -i \"{source}\" -t {durationSec.ToString("0.######", inv)}",
            "-map 0:v:0 -map 0:a:0? -sn -dn",
            $"-vf \"scale=-2:'min({o.Height},ih)':flags=lanczos,format=yuv420p\"",
            $"-c:v {o.Encoder} {rate} -g {gop} -keyint_min {gop} -profile:v high",
            $"-c:a aac -b:a {o.AudioKbps}k",
            "-movflags +faststart",
            $"\"{dest}\"");

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Strings.T("ffmpeg を起動できませんでした"));
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });

        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            // -progress の「out_time_us=」(出力の今の時刻、マイクロ秒)
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, inv, out var us) && durationSec > 0)
                progress?.Report(Math.Clamp(us / 1e6 / durationSec, 0, 1));
        }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (err) tail = string.Join(" / ", err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            throw new InvalidOperationException(Strings.T("ffmpeg が失敗しました(終了コード {code}): {detail}", ("code", p.ExitCode), ("detail", tail)));
        }
        progress?.Report(1);
    }

    private static (int Code, string Output) Run(string exe, string args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "");
            }
            return (p.ExitCode, outTask.Result + errTask.Result);
        }
        catch
        {
            return (-1, "");
        }
    }
}
