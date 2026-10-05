using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

public enum SessionState { Idle, Starting, Running, Stopping }

/// <summary>ロギングと OBS 録画の開始・終了をまとめて制御する</summary>
public sealed class SessionController : IDisposable
{
    private readonly RecorderOptions _opt;
    private readonly TelemetryService _telemetry;
    private readonly ObsClient _obs;
    private readonly ObsQualityService _quality;
    private readonly ObsSceneService? _scene;
    private readonly RaceWatcher? _race;
    private readonly ILogger<SessionController> _log;

    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly object _lock = new();
    private readonly Timer _autosave;

    private SessionState _state = SessionState.Idle;
    private SessionWriter? _writer;
    private SessionMeta? _meta;
    private string? _folder;
    private string? _prevRecordDir;
    private CancellationTokenSource? _samplerCts;
    private Task? _samplerTask;
    private string? _warning;
    private LastSessionInfo? _last;

    public SessionController(RecorderOptions opt, TelemetryService telemetry, ObsClient obs, ObsQualityService quality,
                             ILogger<SessionController> log, RaceWatcher? race = null, ObsSceneService? scene = null)
    {
        _opt = opt;
        _telemetry = telemetry;
        _obs = obs;
        _quality = quality;
        _scene = scene;
        _race = race;
        _log = log;
        if (race != null) race.AppendToSession = AppendEvent;
        _autosave = new Timer(_ => Autosave(), null, Timeout.Infinite, Timeout.Infinite);
        _telemetry.PacketReceived += OnPacket;
        _obs.RecordStateChanged += OnObsRecordState;
    }

    // ---------------------------------------------------------------- packets

    private void OnPacket(double recv, ReadOnlySpan<byte> data, double[]? values)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            if (values == null) _writer.WriteBadPacket(recv, data);
            else _writer.Write(recv, data, values);
        }
    }

    private void OnObsRecordState(RecordStateEvent ev)
    {
        lock (_lock)
        {
            if (_meta == null || _state != SessionState.Running) return;
            if (ev.State == ObsClient.Stopped && _meta.Video.Active)
            {
                _meta.Video.Active = false;
                _meta.Video.StoppedEventRecvTime = ev.RecvTime;
                if (ev.OutputPath != null) SetVideoPath(_meta, ev.OutputPath);
                _meta.AddEvent("obs_record_stopped_externally", ev.OutputPath);
                _samplerCts?.Cancel();
                _warning = Strings.T("OBS側で録画が止まりました。ログの記録は続いています");
            }
        }
    }

    // ---------------------------------------------------------------- start

    public async Task<(bool Ok, string? Error)> StartAsync(bool video, string? label, string? quality = null)
    {
        if (!await _op.WaitAsync(0)) return (false, Strings.T("ほかの操作を処理中です"));
        try
        {
            if (_state != SessionState.Idle) return (false, Strings.T("すでに記録中です"));
            _state = SessionState.Starting;
            _warning = null;

            var root = _opt.ResolveOutputDir();
            // メモがあればフォルダ名にも付ける(日時#メモ)。一覧でどの記録か分かるように
            var folder = Path.Combine(root, SessionFolderName.Create(DateTime.Now, label));
            Directory.CreateDirectory(folder);

            var now = Clock.Now();
            var meta = new SessionMeta();
            meta.Session.Label = label?.Trim() ?? "";
            meta.Session.StartedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
            meta.Session.StartedRecvTime = now;
            meta.Files.TelemetryRaw = _opt.SaveRawPackets ? SessionWriter.RawName : null;
            meta.Video.Requested = video;

            lock (_lock)
            {
                _writer = new SessionWriter(folder, _opt.SaveRawPackets);
                _meta = meta;
                _folder = folder;
                meta.AddEvent("logging_started");
            }
            // 車両・レース・コースの出来事を、この記録の events に書く
            _race?.AttachSession(e =>
            {
                lock (_lock) if (ReferenceEquals(_meta, meta)) meta.Events.Add(e);
            }, folder);
            _log.LogInformation("記録開始: {Folder}", folder);

            if (video)
            {
                if (!_obs.Connected)
                {
                    meta.Video.Error = _obs.LastError ?? "OBS未接続";
                    _warning = Strings.T("OBSに接続できないため、ログだけ記録しています");
                }
                else
                {
                    try
                    {
                        var preset = QualityPresets.IsKnown(quality) ? quality! : _quality.SelectedPreset;
                        if (quality != null && QualityPresets.IsKnown(quality)) _quality.SavePreset(quality);
                        await StartObsAsync(folder, meta, preset);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "録画を開始できませんでした");
                        lock (_lock) meta.Video.Error = ex.Message;
                        _warning = Strings.T("録画を開始できませんでした: {error}(ログは記録中)", ("error", ex.Message));
                    }
                }
            }

            lock (_lock)
            {
                _state = SessionState.Running;
                meta.Save(folder);
            }
            _autosave.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "記録を開始できませんでした");
            _race?.DetachSession();
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
                _meta = null;
                _folder = null;
                _state = SessionState.Idle;
            }
            return (false, ex.Message);
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task StartObsAsync(string folder, SessionMeta meta, string preset)
    {
        var status = await _obs.RequestAsync("GetRecordStatus");
        if (status.GetProperty("outputActive").GetBoolean())
            throw new InvalidOperationException(Strings.T("OBSはすでに録画中です。OBS側で止めてから開始してください"));

        lock (_lock) meta.Video.ObsVersion = _obs.ObsVersion;

        // 録画の画質を切り替える(切り替えられなくても録画は続ける)
        var q = await _quality.ApplyAsync(preset);
        lock (_lock)
        {
            meta.Video.Quality = q;
            if (q.Applied) meta.AddEvent("obs_quality_applied", $"{q.Profile}: {q.OutputWidth}x{q.OutputHeight} {q.Fps}fps {q.RecQuality}{(q.BitrateKbps is int b ? $" {b}kbps" : "")}");
            else if (q.Error != null) meta.AddEvent("obs_quality_failed", q.Error);
        }
        if (q.Error != null) _warning = q.Error;

        // 録画するシーンに切り替える(切り替えられなくても、今のシーンで録画は続ける)
        if (_scene != null)
        {
            var sc = await _scene.ApplyAsync();
            lock (_lock)
            {
                meta.Video.Scene = sc;
                if (sc.Applied) meta.AddEvent("obs_scene_applied", $"{sc.Name}(元: {sc.Previous})");
                else if (sc.Error != null) meta.AddEvent("obs_scene_failed", sc.Error);
            }
            if (sc.Error != null) _warning = sc.Error;
        }

        _prevRecordDir = null;
        if (_opt.Obs.RecordIntoSessionFolder)
        {
            try
            {
                var d = await _obs.RequestAsync("GetRecordDirectory");
                var prev = d.GetProperty("recordDirectory").GetString();
                await _obs.RequestAsync("SetRecordDirectory", new JsonObject { ["recordDirectory"] = folder });
                _prevRecordDir = prev;
                lock (_lock) meta.Video.RecordedIntoSessionFolder = true;
            }
            catch (Exception ex)
            {
                // OBS 30 未満は SetRecordDirectory が無い。OBS の既定フォルダに録画し、パスだけ記録する
                lock (_lock) meta.AddEvent("obs_set_record_directory_failed", ex.Message);
            }
        }

        try
        {
            var wait = _obs.WaitForRecordState(ObsClient.Started, TimeSpan.FromSeconds(15));
            lock (_lock) meta.Video.StartRequestedRecvTime = Clock.Now();
            await _obs.RequestAsync("StartRecord");
            var ev = await wait;
            lock (_lock)
            {
                meta.Video.Active = true;
                meta.Video.StartedEventRecvTime = ev.RecvTime;
                meta.Video.VideoZeroRecvTime = ev.RecvTime;
                meta.Video.VideoZeroMethod = SyncCalc.ZeroFromStartedEvent;
                if (ev.OutputPath != null) SetVideoPath(meta, ev.OutputPath);
                meta.AddEvent("obs_record_started", ev.OutputPath);
            }
            _samplerCts = new CancellationTokenSource();
            _samplerTask = SyncSamplerAsync(meta, _samplerCts.Token);
        }
        catch
        {
            await RestoreRecordDirectoryAsync();
            await RestoreSceneAsync(meta);
            await RestoreQualityAsync(meta);
            throw;
        }
    }

    private async Task RestoreSceneAsync(SessionMeta meta)
    {
        if (_scene == null) return;
        var restored = await _scene.RestoreAsync();
        if (restored == null) return;
        lock (_lock) meta.AddEvent(restored == true ? "obs_scene_restored" : "obs_scene_restore_failed");
    }

    private async Task RestoreQualityAsync(SessionMeta meta)
    {
        var restored = await _quality.RestoreAsync();
        if (restored == null) return;
        lock (_lock) meta.AddEvent(restored == true ? "obs_quality_restored" : "obs_quality_restore_failed");
    }

    /// <summary>
    /// 録画中ずっと GetRecordStatus.outputDuration(その時点までの動画の長さ)を記録する。
    /// 開始直後は細かく5回、その後は2秒ごと。これが「テレメトリ時刻 ↔ 動画時刻」の対応表になる。
    /// </summary>
    private async Task SyncSamplerAsync(SessionMeta meta, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);
            for (int i = 0; i < 5; i++)
            {
                await TakeSyncSampleAsync(meta);
                await Task.Delay(300, ct);
            }
            while (true)
            {
                await Task.Delay(2000, ct);
                await TakeSyncSampleAsync(meta);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TakeSyncSampleAsync(SessionMeta meta)
    {
        try
        {
            var t0 = Clock.Now();
            var r = await _obs.RequestAsync("GetRecordStatus", timeout: TimeSpan.FromSeconds(2));
            var t1 = Clock.Now();
            if (!r.GetProperty("outputActive").GetBoolean()) return false;
            var videoSec = r.GetProperty("outputDuration").GetDouble() / 1000.0;
            lock (_lock)
            {
                meta.Video.SyncSamples.Add(new[]
                {
                    Math.Round((t0 + t1) / 2, 4),
                    Math.Round(videoSec, 3),
                    Math.Round((t1 - t0) * 1000, 1),
                });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- stop

    public async Task<(bool Ok, string? Error)> StopAsync()
    {
        if (!await _op.WaitAsync(TimeSpan.FromSeconds(1))) return (false, Strings.T("ほかの操作を処理中です"));
        try
        {
            SessionMeta? meta;
            string? folder;
            lock (_lock)
            {
                if (_state != SessionState.Running || _meta == null || _folder == null) return (false, Strings.T("記録していません"));
                _state = SessionState.Stopping;
                meta = _meta;
                folder = _folder;
            }
            _autosave.Change(Timeout.Infinite, Timeout.Infinite);
            _race?.DetachSession();

            _samplerCts?.Cancel();
            if (_samplerTask != null)
            {
                try { await _samplerTask; } catch { }
            }
            _samplerCts = null;
            _samplerTask = null;

            if (meta.Video.Active)
            {
                await TakeSyncSampleAsync(meta);
                try
                {
                    var wait = _obs.WaitForRecordState(ObsClient.Stopped, TimeSpan.FromSeconds(60));
                    lock (_lock) meta.Video.StopRequestedRecvTime = Clock.Now();
                    var r = await _obs.RequestAsync("StopRecord", timeout: TimeSpan.FromSeconds(60));
                    string? path = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("outputPath", out var p) ? p.GetString() : null;
                    var ev = await wait;
                    lock (_lock)
                    {
                        meta.Video.Active = false;
                        meta.Video.StoppedEventRecvTime = ev.RecvTime;
                        var videoPath = path ?? ev.OutputPath;
                        if (videoPath != null) SetVideoPath(meta, videoPath);
                        meta.AddEvent("obs_record_stopped", videoPath);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "録画の停止でエラー");
                    lock (_lock)
                    {
                        meta.Video.Error = $"停止時: {ex.Message}";
                        meta.AddEvent("obs_stop_failed", ex.Message);
                    }
                }
            }
            await RestoreRecordDirectoryAsync();
            await RestoreSceneAsync(meta);
            await RestoreQualityAsync(meta);

            if (meta.Video.OutputPath is string recordedPath && meta.Video.StoppedEventRecvTime != null)
            {
                var dur = await VideoDuration.TryReadWithRetryAsync(recordedPath);
                var fps = dur == null ? null : VideoDuration.TryReadFrameRate(recordedPath);
                lock (_lock)
                {
                    meta.Video.FileDurationSec = dur is double d ? Math.Round(d, 3) : null;
                    meta.Video.FileFps = fps is double f ? Math.Round(f, 3) : null;
                    if (dur == null) meta.AddEvent("video_duration_unavailable", recordedPath);
                }
            }

            lock (_lock)
            {
                SyncCalc.Finalize(meta.Video);
                meta.AddEvent("logging_stopped");
                _writer?.Dispose();
                meta.Stats = _writer?.GetStats();
                _writer = null;

                meta.Complete = true;
                meta.Session.EndedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                meta.Session.EndedRecvTime = Clock.Now();
                meta.Save(folder);

                _last = new LastSessionInfo
                {
                    Folder = folder,
                    Name = Path.GetFileName(folder),
                    DurationSec = Math.Round(meta.Session.EndedRecvTime.Value - meta.Session.StartedRecvTime, 1),
                    Rows = meta.Stats?.RowsWritten ?? 0,
                    Markers = meta.Markers.Count,
                    Video = meta.Files.Video,
                    VideoError = meta.Video.Requested && meta.Files.Video == null ? meta.Video.Error ?? "動画なし" : null,
                    FileDurationSec = meta.Video.FileDurationSec,
                    EndCheckSec = meta.Video.EndCheckSec,
                    ClockRate = meta.Video.ClockRate,
                    Quality = meta.Video.Quality?.Preset,
                    MbPerMin = MbPerMin(meta.Video.OutputPath, meta.Video.FileDurationSec),
                };
                _meta = null;
                _folder = null;
                _warning = null;
                _state = SessionState.Idle;
            }
            _log.LogInformation("記録終了: {Folder}", folder);
            return (true, null);
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task RestoreRecordDirectoryAsync()
    {
        var prev = _prevRecordDir;
        _prevRecordDir = null;
        if (prev == null || !_obs.Connected) return;
        try
        {
            await _obs.RequestAsync("SetRecordDirectory", new JsonObject { ["recordDirectory"] = prev });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OBSの録画フォルダを元に戻せませんでした(元: {Prev})", prev);
        }
    }

    /// <summary>録画ファイルの実際の容量(MB/分)</summary>
    private static double? MbPerMin(string? path, double? durationSec)
    {
        if (path == null || durationSec is not > 0) return null;
        try { return Math.Round(new FileInfo(path).Length / 1_000_000.0 / (durationSec.Value / 60), 1); }
        catch { return null; }
    }

    private void SetVideoPath(SessionMeta meta, string path)
    {
        meta.Video.OutputPath = path;
        var folder = _folder;
        if (folder != null)
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (dir != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                meta.Files.Video = Path.GetFileName(full);
                return;
            }
        }
        meta.Files.Video = path;
    }

    // ---------------------------------------------------------------- markers

    public (bool Ok, int Count) AddMarker(string? label)
    {
        var ts = _telemetry.Snapshot().TimestampMs;
        lock (_lock)
        {
            if (_meta == null || _state != SessionState.Running) return (false, 0);
            var n = _meta.Markers.Count + 1;
            _meta.Markers.Add(new Marker
            {
                RecvTime = Clock.Now(),
                TimestampMs = ts,
                Label = string.IsNullOrWhiteSpace(label) ? $"marker {n}" : label.Trim(),
            });
            return (true, n);
        }
    }

    /// <summary>
    /// セッションに出来事を足す(直前のレースを後から直したとき)。記録中のセッションならその記録に、
    /// 止めた後なら session.json を読んで足して書き戻す(知らないキーは残る)
    /// </summary>
    private bool AppendEvent(string folder, SessionEvent ev)
    {
        lock (_lock)
        {
            if (_meta != null && _folder != null && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _meta.Events.Add(ev);
                return true;
            }
            if (!File.Exists(Path.Combine(folder, SessionMeta.FileName))) return false;
            var meta = SessionMeta.Load(folder);
            meta.Events.Add(ev);
            meta.Save(folder);
            return true;
        }
    }

    private void Autosave()
    {
        lock (_lock)
        {
            if (_meta == null || _folder == null || _state != SessionState.Running) return;
            try
            {
                _meta.Stats = _writer?.GetStats();
                _meta.Save(_folder);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "途中保存に失敗");
            }
        }
    }

    // ---------------------------------------------------------------- status

    public object GetStatus()
    {
        var live = _telemetry.Snapshot();
        object? session = null;
        lock (_lock)
        {
            if (_meta != null && _folder != null)
            {
                session = new
                {
                    name = Path.GetFileName(_folder),
                    folder = _folder,
                    label = _meta.Session.Label,
                    elapsedSec = Math.Round(Clock.Now() - _meta.Session.StartedRecvTime, 1),
                    rows = _writer?.RowsWritten ?? 0,
                    packets = _writer?.PacketsReceived ?? 0,
                    markers = _meta.Markers.Count,
                    videoRequested = _meta.Video.Requested,
                    videoActive = _meta.Video.Active,
                    quality = _meta.Video.Quality?.Preset,
                    qualityApplied = _meta.Video.Quality?.Applied ?? false,
                };
            }
        }

        return new
        {
            state = _state.ToString().ToLowerInvariant(),
            warning = _warning,
            telemetry = live,
            obs = new
            {
                enabled = _obs.Enabled,
                connected = _obs.Connected,
                recording = _obs.Recording,
                version = _obs.ObsVersion,
                error = _obs.LastError,
            },
            session,
            last = _last,
            race = _race?.Snapshot(),
            quality = QualityStatus(),
            scene = SceneStatus(),
            outputDir = _opt.ResolveOutputDir(),
            speedUnit = _opt.ResolveSpeedUnit(),
        };
    }

    private object QualityStatus()
    {
        _quality.RefreshProfilesIfStale();
        var known = _quality.KnownProfiles;
        return new
        {
            selected = _quality.SelectedPreset,
            presets = QualityPresets.All.Select(p => new
            {
                id = p.Id, name = p.DisplayName, height = p.Height, fps = p.Fps, mbPerMin = p.MbPerMin, note = p.DisplayNote,
                // OBS にあるプロファイル(日本語・英語どちらの名前でも)。無ければ今の言語で作る名前
                profile = (known == null ? null : p.FindProfile(known)) ?? p.ProfileName,
                // OBS にプロファイルがあるか(OBS 未接続で分からなければ null)
                ready = known == null ? (bool?)null : p.FindProfile(known) != null,
            }),
        };
    }

    private object? SceneStatus()
    {
        if (_scene == null) return null;
        _scene.RefreshScenesIfStale();
        return new
        {
            selected = _scene.SelectedScene,
            current = _scene.CurrentScene,       // OBS で今選んでいる(プログラムの)シーン。未接続なら null
            scenes = _scene.KnownScenes,         // OBS の上からの順。未接続なら null
        };
    }

    public bool IsActive => _state != SessionState.Idle;

    public void Dispose() => _autosave.Dispose();
}

public sealed class LastSessionInfo
{
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public double DurationSec { get; set; }
    public long Rows { get; set; }
    public int Markers { get; set; }
    public string? Video { get; set; }
    public string? VideoError { get; set; }
    public double? FileDurationSec { get; set; }
    public double? EndCheckSec { get; set; }
    public double? ClockRate { get; set; }
    public string? Quality { get; set; }
    public double? MbPerMin { get; set; }
}
