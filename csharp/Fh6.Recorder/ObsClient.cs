using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fh6.Core;

namespace Fh6.Recorder;

public sealed class ObsRequestException : Exception
{
    public int Code { get; }
    public ObsRequestException(string type, int code, string? comment)
        : base(Strings.T("OBS の {type} に失敗しました(code {code}{comment})", ("type", type), ("code", code), ("comment", string.IsNullOrEmpty(comment) ? "" : ": " + comment))) => Code = code;
}

public readonly record struct RecordStateEvent(string State, bool Active, string? OutputPath, double RecvTime);

/// <summary>
/// obs-websocket v5 (OBS 28 以降に内蔵) の最小クライアント。
/// 切断されたら 3 秒ごとに再接続する。
/// </summary>
public sealed class ObsClient : BackgroundService
{
    public const string Started = "OBS_WEBSOCKET_OUTPUT_STARTED";
    public const string Stopped = "OBS_WEBSOCKET_OUTPUT_STOPPED";
    private const int EventSubOutputs = 1 << 6;

    private readonly ObsOptions _opt;
    private readonly ILogger<ObsClient> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private ClientWebSocket? _ws;

    public bool Enabled => _opt.Enabled;
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }
    public string? ObsVersion { get; private set; }
    public bool Recording { get; private set; }

    public event Action<RecordStateEvent>? RecordStateChanged;

    /// <summary>OBS につながった(認証まで済んだ)。再接続のたびに呼ばれる</summary>
    public event Action? ConnectionOpened;

    public ObsClient(RecorderOptions opt, ILogger<ObsClient> log)
    {
        _opt = opt.Obs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            LastError = Strings.T("設定で無効になっています");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var msg = Describe(ex);
                if (msg != LastError) _log.LogInformation("OBS: {Msg}", msg);
                LastError = msg;
            }

            Connected = false;
            Recording = false;
            _ws = null;
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new InvalidOperationException(Strings.T("OBSとの接続が切れました")));

            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException =>
            Strings.T("OBSに接続できません(OBSが起動していないか、WebSocketサーバーが無効です)"),
        _ => ex.Message
    };

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri($"ws://{_opt.Host}:{_opt.Port}"), connectCts.Token);
        }

        // Hello (op 0)
        var hello = await ReceiveAsync(ws, ct) ?? throw new InvalidOperationException(Strings.T("OBSから応答がありません"));
        var hd = hello.GetProperty("d");

        var identify = new JsonObject
        {
            ["rpcVersion"] = 1,
            ["eventSubscriptions"] = EventSubOutputs,
        };
        if (hd.TryGetProperty("authentication", out var auth))
        {
            if (string.IsNullOrEmpty(_opt.Password))
                throw new InvalidOperationException(Strings.T("OBSがパスワードを要求しています。appsettings.Local.json の Obs.Password を設定してください"));
            identify["authentication"] = MakeAuth(_opt.Password,
                auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
        }
        await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, ct);

        var identified = await ReceiveAsync(ws, ct);
        if (identified is null || identified.Value.GetProperty("op").GetInt32() != 2)
        {
            if ((int?)ws.CloseStatus == 4009)
                throw new InvalidOperationException(Strings.T("OBSのパスワードが違います(appsettings.Local.json の Obs.Password)"));
            throw new InvalidOperationException(Strings.T("OBSとの認証に失敗しました({reason})", ("reason", ws.CloseStatusDescription)));
        }

        _ws = ws;
        Connected = true;
        LastError = null;
        _log.LogInformation("OBS に接続しました");
        try { ConnectionOpened?.Invoke(); }
        catch (Exception ex) { _log.LogError(ex, "ConnectionOpened ハンドラでエラー"); }

        var readTask = ReadLoopAsync(ws, ct);
        _ = Task.Run(async () =>
        {
            try
            {
                var v = await RequestAsync("GetVersion");
                ObsVersion = v.GetProperty("obsVersion").GetString();
                var rs = await RequestAsync("GetRecordStatus");
                Recording = rs.GetProperty("outputActive").GetBoolean();
            }
            catch (Exception ex) { _log.LogDebug(ex, "OBS 初期問い合わせ失敗"); }
        }, ct);

        await readTask;

        if ((int?)ws.CloseStatus == 4009)
            throw new InvalidOperationException(Strings.T("OBSのパスワードが違います"));
        throw new InvalidOperationException(Strings.T("OBSとの接続が切れました"));
    }

    private static string MakeAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await ReceiveAsync(ws, ct);
            if (msg is null) return;
            var m = msg.Value;
            int op = m.GetProperty("op").GetInt32();
            var d = m.GetProperty("d");

            if (op == 7)
            {
                var id = d.GetProperty("requestId").GetString();
                if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(d);
            }
            else if (op == 5)
            {
                var type = d.GetProperty("eventType").GetString();
                if (type == "RecordStateChanged" && d.TryGetProperty("eventData", out var ed))
                {
                    var ev = new RecordStateEvent(
                        ed.GetProperty("outputState").GetString() ?? "",
                        ed.GetProperty("outputActive").GetBoolean(),
                        ed.TryGetProperty("outputPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                        Clock.Now());
                    Recording = ev.Active;
                    try { RecordStateChanged?.Invoke(ev); }
                    catch (Exception ex) { _log.LogError(ex, "RecordStateChanged ハンドラでエラー"); }
                }
            }
        }
    }

    public async Task<JsonElement> RequestAsync(string type, JsonObject? data = null, TimeSpan? timeout = null)
    {
        var ws = _ws;
        if (!Connected || ws == null) throw new InvalidOperationException(Strings.T("OBSに接続していません"));

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data != null) d["requestData"] = data;

        try
        {
            await SendAsync(ws, new JsonObject { ["op"] = 6, ["d"] = d }, CancellationToken.None);
            var resp = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            var status = resp.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
            {
                throw new ObsRequestException(type, status.GetProperty("code").GetInt32(),
                    status.TryGetProperty("comment", out var c) ? c.GetString() : null);
            }
            return resp.TryGetProperty("responseData", out var rd) ? rd : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>StartRecord/StopRecord を送る前に呼んで、完了イベントを待つ</summary>
    public Task<RecordStateEvent> WaitForRecordState(string state, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<RecordStateEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RecordStateEvent ev)
        {
            if (ev.State == state) tcs.TrySetResult(ev);
        }
        RecordStateChanged += Handler;
        var task = tcs.Task.WaitAsync(timeout);
        task.ContinueWith(_ => RecordStateChanged -= Handler, TaskScheduler.Default);
        return task;
    }

    private async Task SendAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<JsonElement?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            if (ws.State != WebSocketState.Open) return null;
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                return null;
            }
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }
}
