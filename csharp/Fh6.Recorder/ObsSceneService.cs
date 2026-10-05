using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// 録画の前に OBS のプログラムのシーンを、記録アプリで選んだシーンに切り替え、録画の後に元のシーンへ戻す(2026-10-05)。
/// 画面キャプチャ用などに OBS で別のシーンを選んだまま戻し忘れても、決めたシーン(ゲームキャプチャのあるシーン)で録画するため。
/// シーンの中身(ソース)は変えない。選んだシーンは保存先フォルダの .recorder_prefs.json に覚える(null = 今のシーンのまま)。
/// 元のシーン名は .recorder_obs_scene_restore.json にも書き、アプリが途中で落ちたら次に OBS へつながった時点で戻す。
/// </summary>
public sealed class ObsSceneService
{
    public const string RestoreName = ".recorder_obs_scene_restore.json";

    private readonly ObsClient _obs;
    private readonly RecorderOptions _opt;
    private readonly ILogger<ObsSceneService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>切り替えている間の、元のシーン名</summary>
    private string? _original;

    public ObsSceneService(ObsClient obs, RecorderOptions opt, ILogger<ObsSceneService> log)
    {
        _obs = obs;
        _opt = opt;
        _log = log;
        _obs.ConnectionOpened += () => _ = RestorePendingFromFileAsync();
    }

    private string RestorePath => Path.Combine(_opt.ResolveOutputDir(), RestoreName);
    private string PrefsPath => Path.Combine(_opt.ResolveOutputDir(), RecorderFiles.PrefsName);

    // ---------------------------------------------------------------- 選んだシーン

    /// <summary>記録アプリで選んだシーン。null なら OBS で今選んでいるシーンのまま録画する</summary>
    public string? SelectedScene => RecorderFiles.Read<RecorderPrefs>(PrefsPath)?.Scene;

    public void SaveScene(string? scene)
    {
        var prefs = RecorderFiles.Read<RecorderPrefs>(PrefsPath) ?? new RecorderPrefs();
        prefs.Scene = string.IsNullOrWhiteSpace(scene) ? null : scene;
        RecorderFiles.Write(PrefsPath, prefs);
    }

    /// <summary>最後に読んだシーンの一覧と、OBS で今選んでいる(プログラムの)シーン。OBS 未接続なら null</summary>
    public IReadOnlyList<string>? KnownScenes { get; private set; }
    public string? CurrentScene { get; private set; }
    private DateTime _scenesReadAt = DateTime.MinValue;

    /// <summary>シーンの一覧を読み直す(3 秒に 1 回まで。画面の更新から呼ぶ)</summary>
    public void RefreshScenesIfStale()
    {
        if (!_obs.Connected) { KnownScenes = null; CurrentScene = null; return; }
        if (DateTime.UtcNow - _scenesReadAt < TimeSpan.FromSeconds(3)) return;
        _scenesReadAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await GetScenesAsync(); }
            catch { }
        });
    }

    // ---------------------------------------------------------------- 切り替え

    /// <summary>選んだシーンに切り替える。切り替えられなくても例外にせず、理由を VideoScene.Error に入れて返す(そのときは今のシーンで録画する)</summary>
    public async Task<VideoScene> ApplyAsync()
    {
        var v = new VideoScene { Selected = SelectedScene };
        await _lock.WaitAsync();
        try
        {
            var (current, scenes) = await GetScenesAsync();
            v.Name = current;
            if (v.Selected == null || v.Selected == current) return v;
            if (!scenes.Contains(v.Selected))
            {
                v.Error = Strings.T("OBS にシーン「{scene}」がありません(今のシーン「{current}」で録画します)", ("scene", v.Selected), ("current", current));
                return v;
            }
            // 先に元のシーン名を残してから切り替える(途中で落ちても戻せるように)
            RecorderFiles.Write(RestorePath, new ObsSceneRestore { Scene = current, SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") });
            _original = current;
            try
            {
                await SwitchAsync(v.Selected);
            }
            catch (Exception ex)
            {
                v.Error = Strings.T("OBS のシーンを切り替えられませんでした: {error}(今のシーン「{current}」で録画します)", ("error", ex.Message), ("current", current));
                await RestoreCoreAsync();
                return v;
            }
            v.Applied = true;
            v.Previous = current;
            v.Name = v.Selected;
            _log.LogInformation("OBS のシーンを「{Scene}」に切り替えました(元: {Previous})", v.Selected, current);
            return v;
        }
        catch (Exception ex)
        {
            v.Error = Strings.T("OBS のシーンを読めませんでした: {error}(今のシーンで録画します)", ("error", ex.Message));
            return v;
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
            var saved = RecorderFiles.Read<ObsSceneRestore>(RestorePath);
            if (saved == null || string.IsNullOrEmpty(saved.Scene)) return;
            _log.LogInformation("前回戻せなかった OBS のシーン「{Scene}」に戻します({SavedAt} に保存)", saved.Scene, saved.SavedAt);
            _original = saved.Scene;
            await RestoreCoreAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OBS のシーンを戻せませんでした");
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
            var (current, scenes) = await GetScenesAsync();
            // 元のシーンが消されていたら戻さない(ファイルも消す)
            if (current != original && scenes.Contains(original)) await SwitchAsync(original);
            try { File.Delete(RestorePath); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            // ファイルは残す(次に OBS へつながったときにもう一度戻す)
            _log.LogWarning(ex, "OBS のシーンを「{Scene}」に戻せませんでした", original);
            return false;
        }
    }

    private async Task SwitchAsync(string scene)
    {
        await _obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene });
        for (int i = 0; i < 20; i++)
        {
            var (current, _) = await GetScenesAsync();
            if (current == scene) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(Strings.T("シーン「{scene}」への切り替えが終わりません", ("scene", scene)));
    }

    private async Task<(string Current, List<string> Scenes)> GetScenesAsync()
    {
        var r = await _obs.RequestAsync("GetSceneList");
        var current = r.GetProperty("currentProgramSceneName").GetString() ?? "";
        // OBS は下から順(sceneIndex 0 が一覧の一番下)に返すので、OBS の画面と同じ上からの順に並べ直す
        var scenes = r.GetProperty("scenes").EnumerateArray()
            .Select(x => (Name: x.GetProperty("sceneName").GetString() ?? "", Index: x.TryGetProperty("sceneIndex", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0))
            .OrderByDescending(x => x.Index).Select(x => x.Name).ToList();
        KnownScenes = scenes;
        CurrentScene = current;
        _scenesReadAt = DateTime.UtcNow;
        return (current, scenes);
    }
}

/// <summary>録画の前に使っていた OBS のシーン(録画の後に戻す)</summary>
public sealed class ObsSceneRestore
{
    public string Scene { get; set; } = "";
    public string SavedAt { get; set; } = "";
}
