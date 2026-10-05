using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// OBS の設定ファイル(ini)を、他の行を崩さずに読み書きする最小のクラス。
/// 文字コードは UTF-8(BOM の有無と改行の種類はそのまま保つ)。
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _bom;

    private IniFile(List<string> lines, string newline, bool bom)
    {
        _lines = lines;
        _newline = newline;
        _bom = bom;
    }

    public static IniFile Parse(string text, bool bom = false)
    {
        if (text.Length > 0 && text[0] == '﻿') { text = text[1..]; bom = true; }
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl).ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return new IniFile(lines, nl, bom);
    }

    public static IniFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Parse(new UTF8Encoding(false).GetString(bom ? bytes[3..] : bytes), bom);
    }

    public void Save(string path) => File.WriteAllText(path, ToString(), new UTF8Encoding(_bom));

    public override string ToString() => string.Join(_newline, _lines) + _newline;

    public string? Get(string section, string key)
    {
        var (start, end) = SectionRange(section);
        if (start < 0) return null;
        for (int i = start + 1; i < end; i++)
            if (KeyOf(_lines[i]) == key) return _lines[i][(key.Length + 1)..];
        return null;
    }

    public void Set(string section, string key, string value)
    {
        var (start, end) = SectionRange(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1] != "") _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }
        for (int i = start + 1; i < end; i++)
        {
            if (KeyOf(_lines[i]) != key) continue;
            _lines[i] = $"{key}={value}";
            return;
        }
        // 節の最後の値の行の後ろ(空行の前)に足す
        int at = end;
        while (at > start + 1 && _lines[at - 1].Trim() == "") at--;
        _lines.Insert(at, $"{key}={value}");
    }

    private (int Start, int End) SectionRange(string section)
    {
        int start = _lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (start < 0) return (-1, -1);
        int end = _lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        return (start, end < 0 ? _lines.Count : end);
    }

    private static string? KeyOf(string line)
    {
        int eq = line.IndexOf('=');
        return eq > 0 ? line[..eq] : null;
    }
}

/// <summary>
/// --setup-obs-profiles: 今の OBS のプロファイルを元に、画質の段階ごとのプロファイル「HEISO ○○」を作る。
/// 元のプロファイルのフォルダを丸ごとコピーし、basic.ini の出力解像度・fps・録画画質(・ビットレート・配信用エンコーダ)を書き換えたうえで、
/// 出力モードを「詳細」にして、録画のキーフレーム間隔を 1 秒にする(配布用に動画を切り出すとき、切り出しの頭をキーフレームに合わせるため)。
/// 「基本」モードではキーフレーム間隔を決められず、エンコーダ任せ(NVENC で 250 コマ = 30fps で約 8.3 秒)になる。
/// 「詳細」モードの録画設定は、「基本」モードの値(録画先・形式・エンコーダ・画質・音声)から、OBS の「基本」モードと同じ中身になるように作る。
/// プロファイル名は画面の言語で作る(英語なら「HEISO Light」など)。日本語・英語どちらの名前のものがあっても、作り直さずにそれを使う。
/// 元のプロファイルには触れない。すでにある「HEISO ○○」は、「基本」モードのもの(前の版で作ったもの)だけ「詳細」に直す(元のファイルは控えを残す)。
/// OBS が起動中なら何もしない(OBS が終了時に上書きするため)。
/// </summary>
public static class ObsProfileSetup
{
    public static int Run(IEnumerable<string> args)
    {
        string? configDir = null, from = null;
        var a = args.ToList();
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] == "--obs-config" && i + 1 < a.Count) configDir = a[++i];
            else if (a[i] == "--from" && i + 1 < a.Count) from = a[++i];
        }
        configDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

        if (Process.GetProcessesByName("obs64").Length > 0 || Process.GetProcessesByName("obs32").Length > 0)
        {
            Console.WriteLine(Strings.T("OBS が起動しています。OBS を終了してから、もう一度実行してください(起動中に作ると OBS が終了時に上書きするため)。"));
            return 1;
        }

        try
        {
            var result = Create(configDir, from);
            foreach (var line in result) Console.WriteLine(line);
            Console.WriteLine();
            Console.WriteLine(Strings.T("OBS を起動すると、プロファイルのメニューに「HEISO ○○」が並びます。中身は OBS の設定画面で自由に直せます。"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(Strings.T("プロファイルを作れませんでした: {error}", ("error", ex.Message)));
            return 1;
        }
    }

    /// <summary>
    /// プロファイルを作り、結果の説明を1段階1行で返す(テストから呼べるよう、OBS の起動確認と分ける)。
    /// language: 新しく作るプロファイルの名前の言語(省略すると画面の言語)
    /// </summary>
    public static List<string> Create(string configDir, string? fromDirName = null, string? language = null)
    {
        language ??= Strings.Language;
        var profilesDir = Path.Combine(configDir, "basic", "profiles");
        if (!Directory.Exists(profilesDir)) throw new DirectoryNotFoundException(Strings.T("OBS のプロファイルのフォルダがありません: {path}", ("path", profilesDir)));

        fromDirName ??= CurrentProfileDir(configDir)
                        ?? throw new InvalidOperationException(Strings.T("今の OBS のプロファイルが分かりません。--from <プロファイルのフォルダ名> で指定してください"));
        var sourceDir = Path.Combine(profilesDir, fromDirName);
        var sourceIni = Path.Combine(sourceDir, "basic.ini");
        if (!File.Exists(sourceIni)) throw new FileNotFoundException(Strings.T("元にするプロファイルがありません: {path}", ("path", sourceDir)));

        var src = IniFile.Load(sourceIni);
        if (!string.Equals(src.Get("Output", "Mode"), "Simple", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Strings.T("元のプロファイル「{profile}」の出力モードが「基本」ではありません。OBS の「設定 → 出力」で出力モードを「基本」にしてから実行してください", ("profile", fromDirName)));

        int.TryParse(src.Get("Video", "BaseCX"), out int baseW);
        int.TryParse(src.Get("Video", "BaseCY"), out int baseH);
        var recEncoder = src.Get("SimpleOutput", "RecEncoder");

        var result = new List<string> { Strings.T("元にするプロファイル: {profile}(基本解像度 {w}x{h}、録画エンコーダ {encoder})", ("profile", fromDirName), ("w", baseW), ("h", baseH), ("encoder", recEncoder ?? Strings.T("不明"))) };
        foreach (var p in QualityPresets.All)
        {
            // 日本語・英語どちらの名前でもすでにあれば、それを使う
            var existing = p.ProfileNames.FirstOrDefault(n => Directory.Exists(Path.Combine(profilesDir, n)));
            if (existing != null)
            {
                result.Add($"  {existing}: {UpgradeExisting(Path.Combine(profilesDir, existing))}");
                continue;
            }
            var name = p.ProfileNameIn(language);
            var dir = Path.Combine(profilesDir, name);

            CopyDirectory(sourceDir, dir);
            var iniPath = Path.Combine(dir, "basic.ini");
            var ini = IniFile.Load(iniPath);
            var (w, h) = QualityPresets.OutputSize(p, baseW, baseH);
            ini.Set("General", "Name", name);
            ini.Set("Video", "OutputCX", w.ToString());
            ini.Set("Video", "OutputCY", h.ToString());
            ini.Set("Video", "FPSType", "0");
            ini.Set("Video", "FPSCommon", p.Fps.ToString());
            ini.Set("SimpleOutput", "RecQuality", p.RecQuality);
            if (p.RecQuality == "Stream")
            {
                if (p.BitrateKbps is int kbps) ini.Set("SimpleOutput", "VBitrate", kbps.ToString());
                // 「配信と同じ」は配信用のエンコーダで録画する。x264(CPU)だとゲーム中に重いので、録画用と同じエンコーダにする
                if (!string.IsNullOrEmpty(recEncoder)) ini.Set("SimpleOutput", "StreamEncoder", recEncoder);
            }
            string json;
            try
            {
                json = ToAdvanced(ini);
            }
            catch (InvalidOperationException ex)
            {
                Directory.Delete(dir, true);
                result.Add($"  {name}: " + Strings.T("作れませんでした: {error}", ("error", ex.Message)));
                continue;
            }
            ini.Save(iniPath);
            File.WriteAllText(Path.Combine(dir, "recordEncoder.json"), json);
            result.Add($"  {name}: " + Strings.T("作りました({w}x{h} {fps}fps、{quality}、キーフレーム {sec} 秒)", ("w", w), ("h", h), ("fps", p.Fps), ("quality", RecQualityName(p)), ("sec", KeyintSec)));
        }
        return result;
    }

    /// <summary>録画のキーフレーム間隔(秒)</summary>
    public const int KeyintSec = 1;

    /// <summary>控えのファイル名に付ける名前</summary>
    public const string BackupSuffix = ".simple.bak";

    /// <summary>すでにある「HEISO ○○」: 「基本」モードなら「詳細」に直す。「詳細」ならそのまま</summary>
    private static string UpgradeExisting(string dir)
    {
        var iniPath = Path.Combine(dir, "basic.ini");
        if (!File.Exists(iniPath)) return Strings.T("すでにあるので触れません(basic.ini がありません)");
        var ini = IniFile.Load(iniPath);
        var mode = ini.Get("Output", "Mode");
        if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "Simple", StringComparison.OrdinalIgnoreCase))
            return Strings.T("すでに「詳細」モードなので触れません");
        string json;
        try
        {
            json = ToAdvanced(ini);
        }
        catch (InvalidOperationException ex)
        {
            return Strings.T("直せませんでした: {error}", ("error", ex.Message));
        }
        File.Copy(iniPath, iniPath + BackupSuffix, overwrite: true);
        var encPath = Path.Combine(dir, "recordEncoder.json");
        if (File.Exists(encPath)) File.Copy(encPath, encPath + BackupSuffix, overwrite: true);
        ini.Save(iniPath);
        File.WriteAllText(encPath, json);
        return Strings.T("「詳細」モードに直し、キーフレームを {sec} 秒にしました(元の basic.ini は basic.ini{suffix})", ("sec", KeyintSec), ("suffix", BackupSuffix));
    }

    /// <summary>
    /// 「基本」モードの値から「詳細」モードの録画設定を作り、ini を「詳細」にする。戻り値は recordEncoder.json の中身。
    /// 中身は OBS の SimpleOutput(frontend/utility/SimpleOutput.cpp)に合わせる:
    /// 録画画質が「配信と同じ」なら配信用エンコーダの CBR、それ以外は録画用エンコーダの CQP / CRF(標準 23、区別のつかない画質 16。低い解像度では下げる)。
    /// 知らないエンコーダなら ini を書き換えずに例外にする。
    /// </summary>
    public static string ToAdvanced(IniFile ini)
    {
        string quality = ini.Get("SimpleOutput", "RecQuality") ?? "Stream";
        bool stream = quality == "Stream";
        string simpleEncoder = (stream ? ini.Get("SimpleOutput", "StreamEncoder") : ini.Get("SimpleOutput", "RecEncoder")) ?? "x264";
        string encoder = AdvancedEncoderId(simpleEncoder)
                         ?? throw new InvalidOperationException(Strings.T("エンコーダ「{encoder}」を「詳細」モードの名前に直せません", ("encoder", simpleEncoder)));

        var settings = new JsonObject();
        if (stream)
        {
            int.TryParse(ini.Get("SimpleOutput", "VBitrate"), out int kbps);
            settings["rate_control"] = "CBR";
            settings["bitrate"] = kbps > 0 ? kbps : 2500;
        }
        else
        {
            int.TryParse(ini.Get("Video", "OutputCX"), out int cx);
            int.TryParse(ini.Get("Video", "OutputCY"), out int cy);
            int q = CalcCrf(quality == "HQ" ? 16 : 23, cx, cy);
            if (encoder == "obs_x264") { settings["rate_control"] = "CRF"; settings["crf"] = q; }
            else { settings["rate_control"] = "CQP"; settings["cqp"] = q; }
            if (encoder is "obs_nvenc_h264_tex" or "obs_x264" or "obs_qsv11_v2" or "h264_texture_amf") settings["profile"] = "high";
        }
        if (encoder.StartsWith("obs_nvenc_", StringComparison.Ordinal) && ini.Get("SimpleOutput", "NVENCPreset2") is { Length: > 0 } nvPreset)
            settings["preset"] = nvPreset;
        if (encoder == "obs_x264" && ini.Get("SimpleOutput", "Preset") is { Length: > 0 } x264Preset)
            settings["preset"] = x264Preset;
        settings["keyint_sec"] = KeyintSec;

        // 音声: 「配信と同じ」は配信の音声ビットレート、それ以外は OBS の「基本」モードと同じ 192 kbps
        int.TryParse(ini.Get("SimpleOutput", "ABitrate"), out int aKbps);
        if (!stream) aKbps = 192;
        else if (aKbps <= 0) aKbps = 160;
        int.TryParse(ini.Get("SimpleOutput", "RecTracks"), out int tracks);
        if (tracks <= 0) tracks = 1;

        ini.Set("Output", "Mode", "Advanced");
        ini.Set("AdvOut", "RecType", "Standard");
        ini.Set("AdvOut", "RecFilePath", ini.Get("SimpleOutput", "FilePath") ?? "");
        ini.Set("AdvOut", "RecFormat2", ini.Get("SimpleOutput", "RecFormat2") ?? "hybrid_mp4");
        ini.Set("AdvOut", "RecEncoder", encoder);
        ini.Set("AdvOut", "RecAudioEncoder", "ffmpeg_aac");
        ini.Set("AdvOut", "RecTracks", tracks.ToString());
        ini.Set("AdvOut", "RecRescaleFilter", "0");   // 0 = 拡大縮小しない(出力解像度のまま)
        ini.Set("AdvOut", "RecSplitFile", "false");
        for (int t = 1; t <= 6; t++)
            if ((tracks & (1 << (t - 1))) != 0) ini.Set("AdvOut", $"Track{t}Bitrate", aKbps.ToString());
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>「基本」モードのエンコーダ名 → 「詳細」モードのエンコーダの ID(OBS 31 以降)</summary>
    public static string? AdvancedEncoderId(string simple) => simple switch
    {
        "x264" => "obs_x264",
        "nvenc" => "obs_nvenc_h264_tex",
        "nvenc_hevc" => "obs_nvenc_hevc_tex",
        "nvenc_av1" => "obs_nvenc_av1_tex",
        "qsv" => "obs_qsv11_v2",
        "qsv_hevc" => "obs_qsv11_hevc",
        "qsv_av1" => "obs_qsv11_av1",
        "amd" => "h264_texture_amf",
        "amd_hevc" => "h265_texture_amf",
        "amd_av1" => "av1_texture_amf",
        _ => null,
    };

    /// <summary>OBS の SimpleOutput::CalcCRF と同じ(対角 2000px 未満の解像度では値を下げて画質を上げる)</summary>
    public static int CalcCrf(int crf, int cx, int cy)
    {
        double cross = Math.Sqrt((double)cx * cx + (double)cy * cy);
        double reduction = (1.0 - Math.Min(2000.0, cross) / 2000.0) * 10.0;
        return crf - (int)reduction;
    }

    private static string RecQualityName(QualityPreset p) => p.RecQuality switch
    {
        "Stream" => Strings.T("配信と同じ {kbps} kbps", ("kbps", p.BitrateKbps)),
        "Small" => Strings.T("高画質・ファイルサイズ中"),
        "HQ" => Strings.T("区別のつかない画質"),
        _ => p.RecQuality,
    };

    /// <summary>今の OBS のプロファイルのフォルダ名(OBS 31 以降は user.ini、それより前は global.ini の [Basic] ProfileDir)</summary>
    public static string? CurrentProfileDir(string configDir)
    {
        foreach (var name in new[] { "user.ini", "global.ini" })
        {
            var path = Path.Combine(configDir, name);
            if (!File.Exists(path)) continue;
            var ini = IniFile.Load(path);
            var dir = ini.Get("Basic", "ProfileDir") ?? ini.Get("Basic", "Profile");
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return null;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
