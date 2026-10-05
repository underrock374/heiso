using System.Text;

namespace Fh6.Recorder.Tests;

public class IniFileTests
{
    [Fact]
    public void 他の行を崩さずに値を書き換える()
    {
        var text = "[General]\nName=無題\n\n[SimpleOutput]\nRecQuality=Small\nRecEncoder=nvenc\n\n[AdvOut]\nRecEncoder=none\n";
        var ini = IniFile.Parse(text);
        Assert.Equal("nvenc", ini.Get("SimpleOutput", "RecEncoder"));
        Assert.Equal("none", ini.Get("AdvOut", "RecEncoder"));   // 同じ名前でも節ごとに別

        ini.Set("SimpleOutput", "RecQuality", "Stream");
        ini.Set("SimpleOutput", "VBitrate", "6000");              // 無い値は節の末尾に足す
        ini.Set("Video", "OutputCX", "1280");                     // 無い節は最後に足す
        Assert.Equal(
            "[General]\nName=無題\n\n[SimpleOutput]\nRecQuality=Stream\nRecEncoder=nvenc\nVBitrate=6000\n\n[AdvOut]\nRecEncoder=none\n\n[Video]\nOutputCX=1280\n",
            ini.ToString());
    }

    [Fact]
    public void 改行とBOMはそのまま保つ()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heiso-ini-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(path, "[A]\r\nx=1\r\n", new UTF8Encoding(true));
            var ini = IniFile.Load(path);
            ini.Set("A", "x", "2");
            ini.Save(path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("[A]\r\nx=2\r\n", Encoding.UTF8.GetString(bytes[3..]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ObsProfileSetupTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("heiso-obs-config-").FullName;

    public ObsProfileSetupTests()
    {
        // ユーザーの OBS と同じ形の設定フォルダ(BOM 付き UTF-8、LF)
        var src = Path.Combine(_config, "basic", "profiles", "無題");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "basic.ini"),
            "[General]\nName=無題\n\n[Output]\nMode=Simple\n\n[SimpleOutput]\nFilePath=D:\\\\MyUser\\\\Video\nRecFormat2=hybrid_mp4\nVBitrate=2500\nABitrate=160\nRecQuality=Small\nRecEncoder=nvenc\nNVENCPreset2=p5\n\n[AdvOut]\nRecEncoder=none\n\n[Video]\nBaseCX=3840\nBaseCY=2160\nOutputCX=1920\nOutputCY=1080\nFPSType=0\nFPSCommon=30\n",
            new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(src, "streamEncoder.json"), "{}");
        File.WriteAllText(Path.Combine(_config, "user.ini"), "[General]\nX=1\n\n[Basic]\nProfile=無題\nProfileDir=無題\n", new UTF8Encoding(true));
    }

    public void Dispose() => Directory.Delete(_config, true);

    private IniFile Profile(string name) => IniFile.Load(Path.Combine(_config, "basic", "profiles", name, "basic.ini"));

    private System.Text.Json.Nodes.JsonNode Encoder(string name) =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_config, "basic", "profiles", name, "recordEncoder.json")))!;

    [Fact]
    public void 段階ごとのプロファイルを作る()
    {
        var lines = ObsProfileSetup.Create(_config);
        Assert.Contains("無題", lines[0]);

        var light = Profile("HEISO 軽量");
        Assert.Equal("HEISO 軽量", light.Get("General", "Name"));
        Assert.Equal(("1280", "720", "0", "30"), (light.Get("Video", "OutputCX"), light.Get("Video", "OutputCY"), light.Get("Video", "FPSType"), light.Get("Video", "FPSCommon")));
        Assert.Equal(("Stream", "6000", "nvenc"), (light.Get("SimpleOutput", "RecQuality"), light.Get("SimpleOutput", "VBitrate"), light.Get("SimpleOutput", "StreamEncoder")));
        // それ以外の設定は元と同じ
        Assert.Equal("hybrid_mp4", light.Get("SimpleOutput", "RecFormat2"));
        Assert.Equal(@"D:\\MyUser\\Video", light.Get("SimpleOutput", "FilePath"));
        Assert.True(File.Exists(Path.Combine(_config, "basic", "profiles", "HEISO 軽量", "streamEncoder.json")));

        // 「詳細」モードにして、録画はキーフレーム 1 秒
        Assert.Equal("Advanced", light.Get("Output", "Mode"));
        Assert.Equal(("Standard", "obs_nvenc_h264_tex", "hybrid_mp4", @"D:\\MyUser\\Video"),
            (light.Get("AdvOut", "RecType"), light.Get("AdvOut", "RecEncoder"), light.Get("AdvOut", "RecFormat2"), light.Get("AdvOut", "RecFilePath")));
        Assert.Equal(("ffmpeg_aac", "1", "160", "0"),
            (light.Get("AdvOut", "RecAudioEncoder"), light.Get("AdvOut", "RecTracks"), light.Get("AdvOut", "Track1Bitrate"), light.Get("AdvOut", "RecRescaleFilter")));
        var lightEnc = Encoder("HEISO 軽量");
        Assert.Equal(("CBR", 6000, 1, "p5"), ((string)lightEnc["rate_control"]!, (int)lightEnc["bitrate"]!, (int)lightEnc["keyint_sec"]!, (string)lightEnc["preset"]!));

        var high = Profile("HEISO 高画質");
        Assert.Equal(("1920", "1080", "60", "HQ"), (high.Get("Video", "OutputCX"), high.Get("Video", "OutputCY"), high.Get("Video", "FPSCommon"), high.Get("SimpleOutput", "RecQuality")));
        Assert.Equal("2500", high.Get("SimpleOutput", "VBitrate"));   // 配信と同じでない段階はビットレートを変えない
        Assert.Null(high.Get("SimpleOutput", "StreamEncoder"));
        var highEnc = Encoder("HEISO 高画質");
        Assert.Equal(("CQP", 16, 1, "high"), ((string)highEnc["rate_control"]!, (int)highEnc["cqp"]!, (int)highEnc["keyint_sec"]!, (string)highEnc["profile"]!));
        Assert.Equal("192", high.Get("AdvOut", "Track1Bitrate"));
        Assert.Equal(23, (int)Encoder("HEISO 標準")["cqp"]!);

        Assert.Equal(("960", "540"), (Profile("HEISO 最小").Get("Video", "OutputCX"), Profile("HEISO 最小").Get("Video", "OutputCY")));

        // 元のプロファイルには触れない
        var orig = Profile("無題");
        Assert.Equal(("1920", "Small", "無題"), (orig.Get("Video", "OutputCX"), orig.Get("SimpleOutput", "RecQuality"), orig.Get("General", "Name")));
    }

    [Fact]
    public void 英語ならプロファイルを英語の名前で作り_日本語の名前のものがあれば作らない()
    {
        var dir = Path.Combine(_config, "basic", "profiles", "HEISO 軽量");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "basic.ini"), "[General]\nName=HEISO 軽量\n\n[Output]\nMode=Advanced\n");

        ObsProfileSetup.Create(_config, language: "en");
        Assert.Equal("HEISO Minimum", Profile("HEISO Minimum").Get("General", "Name"));
        Assert.Equal("1080", Profile("HEISO High").Get("Video", "OutputCY"));
        Assert.False(Directory.Exists(Path.Combine(_config, "basic", "profiles", "HEISO Light")));   // 日本語の名前のものを使う
    }

    [Fact]
    public void すでにあるプロファイルには触れない()
    {
        var dir = Path.Combine(_config, "basic", "profiles", "HEISO 軽量");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "basic.ini"), "[General]\nName=HEISO 軽量\n\n[Output]\nMode=Advanced\n\n[Video]\nOutputCX=1111\n");

        var lines = ObsProfileSetup.Create(_config);
        Assert.Contains(lines, l => l.Contains("HEISO 軽量") && l.Contains("触れません"));
        Assert.Equal("1111", Profile("HEISO 軽量").Get("Video", "OutputCX"));
        Assert.False(File.Exists(Path.Combine(dir, "recordEncoder.json")));
        Assert.Equal("540", Profile("HEISO 最小").Get("Video", "OutputCY"));
    }

    [Fact]
    public void 前の版で作った基本モードのプロファイルは詳細モードに直す()
    {
        // 前の版で作った「HEISO 中間」を、利用者が 8000 kbps・HEVC に直していた
        var dir = Path.Combine(_config, "basic", "profiles", "HEISO 中間");
        Directory.CreateDirectory(dir);
        var before = "[General]\nName=HEISO 中間\n\n[Output]\nMode=Simple\n\n[SimpleOutput]\nFilePath=E:\\\\Rec\nRecFormat2=mkv\nVBitrate=8000\nRecQuality=Stream\nStreamEncoder=nvenc_hevc\nRecEncoder=nvenc\n\n[Video]\nOutputCX=1920\nOutputCY=1080\n";
        File.WriteAllText(Path.Combine(dir, "basic.ini"), before);
        File.WriteAllText(Path.Combine(dir, "streamEncoder.json"), "{}");

        var lines = ObsProfileSetup.Create(_config);
        Assert.Contains(lines, l => l.Contains("HEISO 中間") && l.Contains("詳細"));
        var ini = Profile("HEISO 中間");
        Assert.Equal(("Advanced", "obs_nvenc_hevc_tex", "mkv", @"E:\\Rec"),
            (ini.Get("Output", "Mode"), ini.Get("AdvOut", "RecEncoder"), ini.Get("AdvOut", "RecFormat2"), ini.Get("AdvOut", "RecFilePath")));
        Assert.Equal("1920", ini.Get("Video", "OutputCX"));   // ほかの値はそのまま
        var enc = Encoder("HEISO 中間");
        Assert.Equal(("CBR", 8000, 1), ((string)enc["rate_control"]!, (int)enc["bitrate"]!, (int)enc["keyint_sec"]!));
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "basic.ini" + ObsProfileSetup.BackupSuffix)));

        // もう一度実行しても触れない
        lines = ObsProfileSetup.Create(_config);
        Assert.Contains(lines, l => l.Contains("HEISO 中間") && l.Contains("触れません"));
    }

    [Fact]
    public void 知らないエンコーダなら作らない()
    {
        var path = Path.Combine(_config, "basic", "profiles", "無題", "basic.ini");
        var ini = IniFile.Load(path);
        ini.Set("SimpleOutput", "RecEncoder", "unknown_hw");
        ini.Save(path);
        var lines = ObsProfileSetup.Create(_config);
        Assert.Contains(lines, l => l.Contains("HEISO 軽量") && l.Contains("作れませんでした"));
        Assert.False(Directory.Exists(Path.Combine(_config, "basic", "profiles", "HEISO 軽量")));
    }

    [Theory]
    [InlineData(23, 1920, 1080, 23)]
    [InlineData(23, 1280, 720, 21)]
    [InlineData(16, 960, 540, 12)]
    public void 画質の値はOBSの基本モードと同じ(int crf, int cx, int cy, int expected)
        => Assert.Equal(expected, ObsProfileSetup.CalcCrf(crf, cx, cy));

    [Fact]
    public void 詳細モードのプロファイルからは作らない()
    {
        var path = Path.Combine(_config, "basic", "profiles", "無題", "basic.ini");
        var ini = IniFile.Load(path);
        ini.Set("Output", "Mode", "Advanced");
        ini.Save(path);
        var ex = Assert.Throws<InvalidOperationException>(() => ObsProfileSetup.Create(_config));
        Assert.Contains("基本", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_config, "basic", "profiles", "HEISO 軽量")));
    }

    [Fact]
    public void 元にするプロファイルを指定できる()
    {
        File.Delete(Path.Combine(_config, "user.ini"));
        Assert.Throws<InvalidOperationException>(() => ObsProfileSetup.Create(_config));
        ObsProfileSetup.Create(_config, "無題");
        Assert.NotNull(Profile("HEISO 標準").Get("Video", "OutputCX"));
    }
}
