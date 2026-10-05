using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// 録画の前に OBS のプロファイルを段階ごとの「HEISO ○○」へ切り替え、録画の後に元のプロファイルへ戻す。
/// プロファイルの切り替えは OBS 自身が録画用のエンコーダを作り直すので、設定値を外から書き換えるより確実。
/// 元のプロファイル名は保存先フォルダの .recorder_obs_restore.json にも書き、アプリが途中で落ちたら次に OBS へつながった時点で戻す。
/// </summary>
public sealed class ObsQualityService
{
    private readonly ObsClient _obs;
    private readonly RecorderOptions _opt;
    private readonly ILogger<ObsQualityService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>切り替えている間の、元のプロファイル名</summary>
    private string? _original;

    public ObsQualityService(ObsClient obs, RecorderOptions opt, ILogger<ObsQualityService> log)
    {
        _obs = obs;
        _opt = opt;
        _log = log;
        _obs.ConnectionOpened += () => _ = RestorePendingFromFileAsync();
    }

    private string RestorePath => Path.Combine(_opt.ResolveOutputDir(), RecorderFiles.RestoreName);
    private string PrefsPath => Path.Combine(_opt.ResolveOutputDir(), RecorderFiles.PrefsName);

    // ---------------------------------------------------------------- 最後に選んだ段階

    public string SelectedPreset
    {
        get
        {
            var id = RecorderFiles.Read<RecorderPrefs>(PrefsPath)?.Quality;
            return QualityPresets.IsKnown(id) ? id! : QualityPresets.Obs;
        }
    }

    public void SavePreset(string id)
    {
        if (!QualityPresets.IsKnown(id)) throw new ArgumentException(Strings.T("知らない画質の段階です"));
        // 同じファイルに録画するシーンも覚えているので、読んでから書き換える
        var prefs = RecorderFiles.Read<RecorderPrefs>(PrefsPath) ?? new RecorderPrefs();
        prefs.Quality = id;
        RecorderFiles.Write(PrefsPath, prefs);
    }

    /// <summary>最後に読んだ OBS のプロファイルの一覧(画面で「まだ作っていない段階」を出すため)。読めていなければ null</summary>
    public IReadOnlyList<string>? KnownProfiles { get; private set; }
    private DateTime _profilesReadAt = DateTime.MinValue;

    /// <summary>プロファイルの一覧を読み直す(10 秒に 1 回まで。画面の更新から呼ぶ)</summary>
    public void RefreshProfilesIfStale()
    {
        if (!_obs.Connected) { KnownProfiles = null; return; }
        if (DateTime.UtcNow - _profilesReadAt < TimeSpan.FromSeconds(10)) return;
        _profilesReadAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { KnownProfiles = (await GetProfilesAsync()).Profiles; }
            catch { }
        });
    }

    // ---------------------------------------------------------------- 切り替え

    /// <summary>選んだ段階のプロファイルに切り替える。切り替えられなくても例外にせず、理由を VideoQuality.Error に入れて返す</summary>
    public async Task<VideoQuality> ApplyAsync(string presetId)
    {
        var q = new VideoQuality { Preset = presetId };
        var p = QualityPresets.Find(presetId);
        if (p == null) return q;   // OBS の設定のまま: 何も変えない

        await _lock.WaitAsync();
        try
        {
            var (current, profiles) = await GetProfilesAsync();
            // 日本語で作ったプロファイルも、英語で作ったものも使う(今の言語の名前が先)
            if (p.FindProfile(profiles) is not string profile)
            {
                q.Error = Strings.T("OBS にプロファイル「{profile}」がありません。OBS を閉じて「HeisoRecorder --setup-obs-profiles」を実行すると作れます(今の OBS の設定で録画します)", ("profile", p.ProfileName));
                return q;
            }

            if (current != profile)
            {
                // 先に元のプロファイル名を残してから切り替える(途中で落ちても戻せるように)
                RecorderFiles.Write(RestorePath, new ObsProfileRestore { Profile = current, SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") });
                _original = current;
                try
                {
                    await SwitchAsync(profile);
                }
                catch (Exception ex)
                {
                    q.Error = Strings.T("OBS のプロファイルを切り替えられませんでした: {error}(今の OBS の設定で録画します)", ("error", ex.Message));
                    await RestoreCoreAsync();
                    return q;
                }
            }

            q.Applied = true;
            q.Profile = profile;
            await ReadActualAsync(q);
            _log.LogInformation("OBS のプロファイルを「{Profile}」に切り替えました({W}x{H} {Fps}fps {Q})", profile, q.OutputWidth, q.OutputHeight, q.Fps, q.RecQuality);
            return q;
        }
        catch (Exception ex)
        {
            q.Error = Strings.T("OBS のプロファイルを読めませんでした: {error}(今の OBS の設定で録画します)", ("error", ex.Message));
            return q;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>切り替えていれば元に戻す。戻せたら true、戻すものが無ければ null、失敗したら false</summary>
    public async Task<bool?> RestoreAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_original == null) return null;
            return await RestoreCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>前回アプリが落ちて戻し損ねていたら戻す(OBS につながったときに呼ぶ。切り替え中は何もしない)</summary>
    public async Task RestorePendingFromFileAsync()
    {
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            if (_original != null) return;
            var saved = RecorderFiles.Read<ObsProfileRestore>(RestorePath);
            if (saved == null || string.IsNullOrEmpty(saved.Profile)) return;
            _log.LogInformation("前回戻せなかった OBS のプロファイル「{Profile}」に戻します({SavedAt} に保存)", saved.Profile, saved.SavedAt);
            _original = saved.Profile;
            await RestoreCoreAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OBS のプロファイルを戻せませんでした");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> RestoreCoreAsync()
    {
        var original = _original;
        _original = null;
        if (original == null) return true;
        try
        {
            var (current, _) = await GetProfilesAsync();
            if (current != original) await SwitchAsync(original);
            try { File.Delete(RestorePath); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            // ファイルは残す(次に OBS へつながったときにもう一度戻す)
            _log.LogWarning(ex, "OBS のプロファイルを「{Profile}」に戻せませんでした", original);
            return false;
        }
    }

    /// <summary>プロファイルを切り替え、OBS が切り替え終わるまで待つ</summary>
    private async Task SwitchAsync(string profile)
    {
        await _obs.RequestAsync("SetCurrentProfile", new JsonObject { ["profileName"] = profile });
        for (int i = 0; i < 50; i++)
        {
            var (current, _) = await GetProfilesAsync();
            if (current == profile) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(Strings.T("プロファイル「{profile}」への切り替えが終わりません", ("profile", profile)));
    }

    private async Task<(string Current, List<string> Profiles)> GetProfilesAsync()
    {
        var r = await _obs.RequestAsync("GetProfileList");
        var current = r.GetProperty("currentProfileName").GetString() ?? "";
        var profiles = r.GetProperty("profiles").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        KnownProfiles = profiles;
        _profilesReadAt = DateTime.UtcNow;
        return (current, profiles);
    }

    /// <summary>切り替えた後の実際の値(出力解像度・fps・録画画質)を session.json 用に読む</summary>
    private async Task ReadActualAsync(VideoQuality q)
    {
        try
        {
            var vs = await _obs.RequestAsync("GetVideoSettings");
            q.OutputWidth = vs.GetProperty("outputWidth").GetInt32();
            q.OutputHeight = vs.GetProperty("outputHeight").GetInt32();
            q.Fps = Math.Round(vs.GetProperty("fpsNumerator").GetDouble() / vs.GetProperty("fpsDenominator").GetDouble(), 3);
            q.OutputMode = await GetParamAsync("Output", "Mode");
            q.RecQuality = await GetParamAsync("SimpleOutput", "RecQuality");
            if (q.RecQuality == "Stream" && int.TryParse(await GetParamAsync("SimpleOutput", "VBitrate"), out var kbps)) q.BitrateKbps = kbps;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "切り替え後の設定を読めませんでした");
        }
    }

    private async Task<string?> GetParamAsync(string category, string name)
    {
        var r = await _obs.RequestAsync("GetProfileParameter", new JsonObject
        {
            ["parameterCategory"] = category, ["parameterName"] = name,
        });
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("parameterValue", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return r.TryGetProperty("defaultParameterValue", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
    }
}
