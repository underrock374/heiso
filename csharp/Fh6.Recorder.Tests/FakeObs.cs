using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Fh6.Recorder.Tests;

/// <summary>
/// テスト用の偽の OBS(obs-websocket v5 の最小のサーバー)。認証なし。
/// プロファイル(プロファイルごとの映像・出力の設定)と録画の状態を持ち、届いた要求を記録する。
/// </summary>
public sealed class FakeObs : IAsyncDisposable
{
    public sealed class Profile
    {
        public int OutputWidth = 1920, OutputHeight = 1080, Fps = 30;
        public string Mode = "Simple", RecQuality = "Small", VBitrate = "6000";
    }

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<JsonObject> _events = new();
    private bool _recording;

    public int Port { get; }

    public readonly Dictionary<string, Profile> Profiles = new() { ["無題"] = new Profile() };
    public string CurrentProfile = "無題";
    /// <summary>シーンの一覧(OBS の画面の上からの順)と、プログラムのシーン</summary>
    public readonly List<string> Scenes = new() { "シーン" };
    public string CurrentScene = "シーン";
    public string RecordDirectory = @"C:\Videos";
    /// <summary>録画を止めたときに返す録画ファイルのパス</summary>
    public string? RecordPath;

    /// <summary>届いた要求("SetCurrentProfile HEISO 軽量"、"StartRecord" など)</summary>
    public readonly List<string> Log = new();

    /// <summary>要求の種類を渡すと、失敗させるときのコードを返す(null なら成功)</summary>
    public Func<string, int?>? Fail;

    public FakeObs()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public Profile Current => Profiles[CurrentProfile];

    public List<string> Snapshot()
    {
        lock (Log) return Log.ToList();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
            var wsCtx = await ctx.AcceptWebSocketAsync("obswebsocket.json");
            _ = Task.Run(() => Serve(wsCtx.WebSocket));
        }
    }

    private async Task Serve(WebSocket ws)
    {
        try
        {
            await Send(ws, new JsonObject { ["op"] = 0, ["d"] = new JsonObject { ["obsWebSocketVersion"] = "5.7.4", ["rpcVersion"] = 1 } });
            await Receive(ws);   // Identify
            await Send(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var msg = await Receive(ws);
                if (msg == null) return;
                var d = msg["d"]!;
                var type = (string)d["requestType"]!;
                var data = d["requestData"] as JsonObject;
                JsonObject? resp = null;
                int? fail;
                List<JsonObject> events;
                lock (Log)
                {
                    fail = Fail?.Invoke(type);
                    if (fail == null)
                    {
                        try { resp = Handle(type, data); }
                        catch (KeyNotFoundException) { fail = 600; }   // ResourceNotFound
                    }
                    events = _events.ToList();
                    _events.Clear();
                }
                await Send(ws, new JsonObject
                {
                    ["op"] = 7,
                    ["d"] = new JsonObject
                    {
                        ["requestType"] = type,
                        ["requestId"] = (string)d["requestId"]!,
                        ["requestStatus"] = new JsonObject { ["result"] = fail == null, ["code"] = fail ?? 100 },
                        ["responseData"] = resp,
                    },
                });
                // 録画の開始・停止のイベントは、要求の応答の後に送る(本物の OBS と同じ順)
                foreach (var ev in events) await Send(ws, ev);
            }
        }
        catch { }
    }

    /// <summary>Log のロックの中で呼ぶ</summary>
    private JsonObject? Handle(string type, JsonObject? data)
    {
        switch (type)
        {
            case "GetVersion":
                return new JsonObject { ["obsVersion"] = "32.2.2" };
            case "GetRecordStatus":
                return new JsonObject { ["outputActive"] = _recording, ["outputDuration"] = 0 };
            case "GetProfileList":
                return new JsonObject
                {
                    ["currentProfileName"] = CurrentProfile,
                    ["profiles"] = new JsonArray(Profiles.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                };
            case "SetCurrentProfile":
            {
                var name = (string)data!["profileName"]!;
                if (!Profiles.ContainsKey(name)) throw new KeyNotFoundException(name);
                CurrentProfile = name;
                Log.Add($"SetCurrentProfile {name}");
                return null;
            }
            case "GetSceneList":
                return new JsonObject
                {
                    ["currentProgramSceneName"] = CurrentScene,
                    // 本物の OBS と同じく、下から順(sceneIndex 0 が一覧の一番下)
                    ["scenes"] = new JsonArray(Scenes.Select((n, i) => (JsonNode)new JsonObject
                    {
                        ["sceneName"] = n, ["sceneIndex"] = Scenes.Count - 1 - i,
                    }).Reverse().ToArray()),
                };
            case "SetCurrentProgramScene":
            {
                var name = (string)data!["sceneName"]!;
                if (!Scenes.Contains(name)) throw new KeyNotFoundException(name);
                CurrentScene = name;
                Log.Add($"SetCurrentProgramScene {name}");
                return null;
            }
            case "GetVideoSettings":
                return new JsonObject
                {
                    ["baseWidth"] = 3840, ["baseHeight"] = 2160,
                    ["outputWidth"] = Current.OutputWidth, ["outputHeight"] = Current.OutputHeight,
                    ["fpsNumerator"] = Current.Fps, ["fpsDenominator"] = 1,
                };
            case "GetProfileParameter":
            {
                var key = $"{data!["parameterCategory"]}/{data["parameterName"]}";
                string? v = key switch
                {
                    "Output/Mode" => Current.Mode,
                    "SimpleOutput/RecQuality" => Current.RecQuality,
                    "SimpleOutput/VBitrate" => Current.VBitrate,
                    _ => null,
                };
                return new JsonObject { ["parameterValue"] = v };
            }
            case "GetRecordDirectory":
                return new JsonObject { ["recordDirectory"] = RecordDirectory };
            case "SetRecordDirectory":
                RecordDirectory = (string)data!["recordDirectory"]!;
                Log.Add($"SetRecordDirectory {RecordDirectory}");
                return null;
            case "StartRecord":
                _recording = true;
                Log.Add($"StartRecord ({CurrentProfile})");
                Log.Add($"RecordScene {CurrentScene}");
                _events.Add(RecordEvent("OBS_WEBSOCKET_OUTPUT_STARTED", true, null));
                return null;
            case "StopRecord":
                _recording = false;
                Log.Add("StopRecord");
                _events.Add(RecordEvent("OBS_WEBSOCKET_OUTPUT_STOPPED", false, RecordPath));
                return new JsonObject { ["outputPath"] = RecordPath };
            default:
                // 設定値を直接書き換える要求は使わない約束(OBS の録画用エンコーダが壊れるため)
                Log.Add(type);
                return null;
        }
    }

    private static JsonObject RecordEvent(string state, bool active, string? path) => new()
    {
        ["op"] = 5,
        ["d"] = new JsonObject
        {
            ["eventType"] = "RecordStateChanged",
            ["eventIntent"] = 1 << 6,
            ["eventData"] = new JsonObject { ["outputActive"] = active, ["outputState"] = state, ["outputPath"] = path },
        },
    };

    private static async Task Send(WebSocket ws, JsonObject msg) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonNode?> Receive(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _listener.Close();
    }
}
