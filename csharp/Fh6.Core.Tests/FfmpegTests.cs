using System.Diagnostics;

namespace Fh6.Core.Tests;

/// <summary>環境変数 HEISO_FFMPEG(ffmpeg.exe の場所)があるときだけ動かす</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public const string EnvName = "HEISO_FFMPEG";

    public FfmpegFactAttribute()
    {
        var p = Environment.GetEnvironmentVariable(EnvName);
        if (string.IsNullOrEmpty(p) || !File.Exists(p))
            Skip = $"{EnvName} に ffmpeg.exe の場所を指定したときだけ動かす(ffmpeg はリポジトリに入れていない)";
    }
}

public sealed class FfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("heiso-ffmpeg-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void 指定した場所のffmpegを探す_展開したフォルダのbinの中も()
    {
        // gyan.dev の zip を展開した形: <指定>\ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "ffmpeg", "ffmpeg-9.0.2-essentials_build", "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, Ffmpeg.ExeName), "");
        var expected = Path.Combine(bin, Ffmpeg.ExeName);

        Assert.Equal(expected, Ffmpeg.Find(Path.Combine(_dir, "ffmpeg"), Array.Empty<string>()));          // フォルダを指定
        Assert.Equal(expected, Ffmpeg.Find(expected, Array.Empty<string>()));                                // ファイルを指定
        Assert.Null(Ffmpeg.Find(Path.Combine(_dir, "無い"), new[] { Path.Combine(_dir, "ffmpeg") }));        // 指定した場所に無ければ、ほかは探さない
    }

    [Fact]
    public void ビットレートの目安は元のfpsに合わせる()
    {
        Assert.Equal(4000, Ffmpeg.DefaultVideoKbps(30));
        Assert.Equal(6000, Ffmpeg.DefaultVideoKbps(60));
    }

    [FfmpegFact]
    public void 指定した秒から720pに作り直し_キーフレームは1秒ごと()
    {
        var ffmpeg = Environment.GetEnvironmentVariable(FfmpegFactAttribute.EnvName)!;
        var src = Path.Combine(_dir, "src.mp4");
        var dst = Path.Combine(_dir, "dst.mp4");
        // 1080p・30fps・6 秒の元の動画(ffmpeg の試験用の絵と音)
        var p = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-v error -y -f lavfi -i testsrc2=s=1920x1080:r=30:d=6 -f lavfi -i sine=d=6 -c:v libx264 -g 250 -c:a aac -shortest \"{src}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);

        var reported = new List<double>();
        Ffmpeg.Transcode(ffmpeg, src, dst, 1.0, 3.0, 30, new Ffmpeg.Options("libx264"), new SyncProgress(reported.Add));

        Assert.Equal(3.0, VideoDuration.TryRead(dst)!.Value, 1);
        Assert.Equal(30, VideoDuration.TryReadFrameRate(dst)!.Value, 1);
        var v = Mp4Cut.Inspect(dst).Single(t => t.Handler == "vide");
        Assert.Equal(new[] { 0, 30, 60 }, v.KeyFrames);   // キーフレーム 1 秒ごと
        Assert.Contains(Mp4Cut.Inspect(dst), t => t.Handler == "soun");
        Assert.Equal(1.0, reported[^1]);
    }

    [FfmpegFact]
    public void 中止したらffmpegを止める()
    {
        var ffmpeg = Environment.GetEnvironmentVariable(FfmpegFactAttribute.EnvName)!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Ffmpeg.Transcode(ffmpeg, "lavfi-dummy.mp4", Path.Combine(_dir, "x.mp4"), 0, 1, 30, new Ffmpeg.Options("libx264"), null, cts.Token));
    }

    /// <summary>Progress&lt;T&gt; は別スレッドで知らせるので、テストではその場で受ける</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
