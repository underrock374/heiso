using System.Net;
using System.Net.Sockets;
using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>UDP を常時受信し、最新値を保持する。記録中かどうかに関係なく動く</summary>
public sealed class TelemetryService : BackgroundService
{
    public delegate void PacketHandler(double recvTime, ReadOnlySpan<byte> data, double[]? values);

    private readonly RecorderOptions _opt;
    private readonly ILogger<TelemetryService> _log;
    private readonly object _lock = new();
    private readonly double[] _latest = new double[Fh6Packet.Fields.Length];
    private readonly double[] _work = new double[Fh6Packet.Fields.Length];

    private double _lastRecv;
    private bool _hasLatest;
    private double _hzWindowStart;
    private int _hzCount;
    private double _hz;

    public event PacketHandler? PacketReceived;

    public bool Listening { get; private set; }
    public string? Error { get; private set; }
    public int Port => _opt.UdpPort;
    public long TotalPackets { get; private set; }
    public long BadSizePackets { get; private set; }

    public TelemetryService(RecorderOptions opt, ILogger<TelemetryService> log)
    {
        _opt = opt;
        _log = log;
        (_forward, ForwardError) = ParseForward(opt.ForwardTo, opt.UdpPort);
        foreach (var f in _forward) _log.LogInformation("受信した UDP を {Target} へ送り直します(パススルー)", f.Label);
        if (ForwardError != null) _log.LogWarning("{Error}", ForwardError);
    }

    // ---------------------------------------------------------------- パススルー

    private readonly List<(string Label, IPEndPoint End)> _forward;
    private UdpClient? _forwardClient;

    /// <summary>送り直した数と、送れなかった数(送り先のアプリが動いていないなど。記録には影響しない)</summary>
    public long ForwardedPackets { get; private set; }
    public long ForwardFailures { get; private set; }

    /// <summary>送り先の設定の誤り(書式が違う、自分自身への送り直し)。無ければ null</summary>
    public string? ForwardError { get; }

    public IReadOnlyList<string> ForwardTargets => _forward.Select(f => f.Label).ToList();

    /// <summary>
    /// 送り先を読む("ホスト:ポート")。自分の受信ポートを同じ PC へ送る設定は、ぐるぐる回るので使わない。
    /// 読めないものは飛ばして、理由を返す
    /// </summary>
    public static (List<(string Label, IPEndPoint End)> Targets, string? Error) ParseForward(IEnumerable<string>? items, int ownPort)
    {
        var list = new List<(string, IPEndPoint)>();
        var errors = new List<string>();
        foreach (var raw in items ?? Enumerable.Empty<string>())
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(s[(colon + 1)..], out var port) || port is < 1 or > 65535)
            {
                errors.Add(Strings.T("「{value}」(ホスト:ポート の形で書いてください。例: 127.0.0.1:5301)", ("value", s)));
                continue;
            }
            var host = s[..colon];
            IPAddress? addr = null;
            if (!IPAddress.TryParse(host, out addr))
            {
                try { addr = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                catch { addr = null; }
            }
            if (addr == null) { errors.Add(Strings.T("「{value}」(ホスト名が分かりません)", ("value", s))); continue; }
            if (port == ownPort && (IPAddress.IsLoopback(addr) || IsLocalAddress(addr)))
            {
                errors.Add(Strings.T("「{value}」(自分の受信ポートなので、ぐるぐる回ってしまいます)", ("value", s)));
                continue;
            }
            list.Add((s, new IPEndPoint(addr, port)));
        }
        return (list, errors.Count == 0 ? null : Strings.T("UDP の送り直し(ForwardTo)の設定を使えません: {errors}", ("errors", string.Join(Strings.T("、"), errors))));
    }

    private static bool IsLocalAddress(IPAddress a)
    {
        try { return Dns.GetHostAddresses(Dns.GetHostName()).Any(x => x.Equals(a)); }
        catch { return false; }
    }

    /// <summary>受けたバイト列をそのまま送り先へ(読み取りより先に。送れなくても記録は続ける)</summary>
    private void Forward(byte[] buf)
    {
        if (_forward.Count == 0) return;
        _forwardClient ??= new UdpClient(AddressFamily.InterNetwork);
        foreach (var (_, end) in _forward)
        {
            try
            {
                _forwardClient.Send(buf, buf.Length, end);
                ForwardedPackets++;
            }
            catch
            {
                ForwardFailures++;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _opt.UdpPort));
                udp.Client.ReceiveBufferSize = 1 << 20;
                if (OperatingSystem.IsWindows())
                {
                    // SIO_UDP_CONNRESET を無効化(ICMP 到達不能で ReceiveAsync が例外になるのを防ぐ)
                    try { udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
                }
                Listening = true;
                Error = null;
                _log.LogInformation("UDP {Port} で受信待ち", _opt.UdpPort);

                while (!ct.IsCancellationRequested)
                {
                    var r = await udp.ReceiveAsync(ct);
                    Handle(Clock.Now(), r.Buffer);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Error = Strings.T("UDPポート {port} は別のプログラムが使っています(同じポートを使うテレメトリーのアプリが動いていれば止めてください)", ("port", _opt.UdpPort));
                _log.LogWarning("{Error}", Error);
            }
            catch (Exception ex)
            {
                Error = Strings.T("UDP受信エラー: {error}", ("error", ex.Message));
                _log.LogWarning(ex, "UDP受信エラー");
            }

            Listening = false;
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
        Listening = false;
    }

    private void Handle(double recv, byte[] buf)
    {
        Forward(buf);
        TotalPackets++;
        if (recv - _hzWindowStart >= 1.0)
        {
            _hz = _hzWindowStart > 0 ? _hzCount / (recv - _hzWindowStart) : 0;
            _hzWindowStart = recv;
            _hzCount = 0;
        }
        _hzCount++;

        if (buf.Length < Fh6Packet.Size)
        {
            BadSizePackets++;
            PacketReceived?.Invoke(recv, buf, null);
            return;
        }

        Fh6Packet.Parse(buf, _work);
        lock (_lock)
        {
            Array.Copy(_work, _latest, _work.Length);
            _lastRecv = recv;
            _hasLatest = true;
        }

        try
        {
            PacketReceived?.Invoke(recv, buf.AsSpan(0, Fh6Packet.Size), _work);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "パケット処理でエラー");
        }
    }

    private ForwardInfo? ForwardInfo() => _forward.Count == 0 && ForwardError == null ? null : new ForwardInfo
    {
        Targets = ForwardTargets.ToList(), Packets = ForwardedPackets, Failures = ForwardFailures, Error = ForwardError,
    };

    public LiveSnapshot Snapshot()
    {
        var now = Clock.Now();
        lock (_lock)
        {
            if (!_hasLatest) return new LiveSnapshot { Listening = Listening, Error = Error, Port = Port, Forward = ForwardInfo() };

            var v = _latest;
            var ago = now - _lastRecv;
            int raceOn = (int)v[Fh6Packet.IsRaceOn];
            int pos = (int)v[Fh6Packet.RacePos];
            double speed = v[Fh6Packet.Speed];
            string state =
                ago > 2 ? "none" :
                raceOn == 0 ? "menu" :
                pos >= 1 ? "race" :
                speed > 1.0 ? "freeroam" : "idle";

            return new LiveSnapshot
            {
                Listening = Listening,
                Error = Error,
                Port = Port,
                Hz = ago > 2 ? 0 : Math.Round(_hz, 1),
                LastPacketAgoSec = Math.Round(ago, 2),
                State = state,
                SpeedKmh = raceOn == 0 ? null : Math.Round(speed * 3.6, 0),
                RacePos = pos,
                RaceTime = raceOn == 0 ? null : Math.Round(v[Fh6Packet.CurRaceTime], 1),
                Car = raceOn == 0 ? null : new CarInfo
                {
                    Ordinal = (int)v[Fh6Packet.CarOrdinal],
                    Class = Fh6Packet.ClassName((int)v[Fh6Packet.CarClass]),
                    Pi = (int)v[Fh6Packet.CarPi],
                    Drivetrain = Fh6Packet.DrivetrainName((int)v[Fh6Packet.Drivetrain]),
                },
                TimestampMs = (uint)v[Fh6Packet.TimestampMs],
                Forward = ForwardInfo(),
            };
        }
    }
}

public sealed class LiveSnapshot
{
    /// <summary>UDP の送り直し(パススルー)の状態。設定していなければ null</summary>
    public ForwardInfo? Forward { get; set; }

    public bool Listening { get; set; }
    public string? Error { get; set; }
    public int Port { get; set; }
    public double Hz { get; set; }
    public double? LastPacketAgoSec { get; set; }
    /// <summary>none / menu / race / freeroam / idle</summary>
    public string State { get; set; } = "none";
    public double? SpeedKmh { get; set; }
    public int RacePos { get; set; }
    public double? RaceTime { get; set; }
    public CarInfo? Car { get; set; }
    public uint? TimestampMs { get; set; }
}

public sealed class ForwardInfo
{
    public List<string> Targets { get; set; } = new();
    public long Packets { get; set; }
    public long Failures { get; set; }
    public string? Error { get; set; }
}

public sealed class CarInfo
{
    public int Ordinal { get; set; }
    public string Class { get; set; } = "";
    public int Pi { get; set; }
    public string Drivetrain { get; set; } = "";
}
