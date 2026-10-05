using System.IO.Compression;
using System.Text;

namespace Fh6.Core;

/// <summary>
/// telemetry.csv.gz(ロガー v2 互換: recv_time は epoch 秒、is_race_on==0 の連続区間は先頭行と末尾行のみ)
/// と telemetry.raw.gz(recv_time の double + 324 バイトの生パケットを全件)を書く。
/// UDP 受信スレッドから呼ばれる。スレッド安全性は呼び出し側の lock で確保する。
/// </summary>
public sealed class SessionWriter : IDisposable
{
    public const string CsvName = "telemetry.csv.gz";
    public const string RawName = "telemetry.raw.gz";

    private readonly StreamWriter _csv;
    private readonly BinaryWriter? _raw;
    private readonly StringBuilder _sb = new(2048);

    private bool _inIdle;
    private int _idleRun;
    private string? _pendingIdleLine;

    private double? _firstRecv, _lastRecv;
    private uint? _lastTs;
    private int? _lastRaceOn;
    private readonly List<float> _intervalsMs = new(1 << 16);
    private readonly HashSet<string> _carsSeen = new();

    public long PacketsReceived { get; private set; }
    public long RowsWritten { get; private set; }
    public long IdleRowsCollapsed { get; private set; }
    public long BadSizePackets { get; private set; }
    public int RaceStateTransitions { get; private set; }

    public SessionWriter(string folder, bool saveRaw)
    {
        var csvStream = new GZipStream(File.Create(Path.Combine(folder, CsvName)), CompressionLevel.Fastest);
        _csv = new StreamWriter(csvStream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
        _csv.WriteLine(Fh6Packet.CsvHeader());

        if (saveRaw)
        {
            var rawStream = new GZipStream(File.Create(Path.Combine(folder, RawName)), CompressionLevel.Fastest);
            _raw = new BinaryWriter(rawStream);
            // ヘッダ: マジック + パケット長。以降 [double recv_time][ushort len][len bytes] の繰り返し
            _raw.Write(Encoding.ASCII.GetBytes("FH6RAW01"));
            _raw.Write((ushort)Fh6Packet.Size);
        }
    }

    public void WriteBadPacket(double recvTime, ReadOnlySpan<byte> data)
    {
        BadSizePackets++;
        WriteRaw(recvTime, data);
    }

    public void Write(double recvTime, ReadOnlySpan<byte> data, double[] v)
    {
        PacketsReceived++;
        WriteRaw(recvTime, data);

        _firstRecv ??= recvTime;
        _lastRecv = recvTime;

        uint ts = (uint)v[Fh6Packet.TimestampMs];
        if (_lastTs is uint prev && ts > prev) _intervalsMs.Add(ts - prev);
        _lastTs = ts;

        int raceOn = (int)v[Fh6Packet.IsRaceOn];
        if (_lastRaceOn is int pr && pr != raceOn) RaceStateTransitions++;
        _lastRaceOn = raceOn;

        _sb.Clear();
        Fh6Packet.AppendCsvRow(_sb, recvTime, v);

        if (raceOn == 0)
        {
            if (!_inIdle)
            {
                _inIdle = true;
                _idleRun = 1;
                _pendingIdleLine = null;
                WriteLine(_sb);
            }
            else
            {
                _idleRun++;
                _pendingIdleLine = _sb.ToString();
            }
            return;
        }

        FlushIdle();
        WriteLine(_sb);

        _carsSeen.Add(
            $"{(int)v[Fh6Packet.CarOrdinal]}|{Fh6Packet.ClassName((int)v[Fh6Packet.CarClass])}{(int)v[Fh6Packet.CarPi]}|{Fh6Packet.DrivetrainName((int)v[Fh6Packet.Drivetrain])}");
    }

    private void FlushIdle()
    {
        if (!_inIdle) return;
        if (_pendingIdleLine != null)
        {
            _csv.WriteLine(_pendingIdleLine);
            RowsWritten++;
            IdleRowsCollapsed += _idleRun - 2;
        }
        else
        {
            IdleRowsCollapsed += _idleRun - 1;
        }
        _inIdle = false;
        _pendingIdleLine = null;
        _idleRun = 0;
    }

    private void WriteLine(StringBuilder sb)
    {
        _csv.WriteLine(sb);
        RowsWritten++;
    }

    private void WriteRaw(double recvTime, ReadOnlySpan<byte> data)
    {
        if (_raw == null) return;
        _raw.Write(recvTime);
        _raw.Write((ushort)data.Length);
        _raw.Write(data);
    }

    public WriterStats GetStats()
    {
        double duration = (_firstRecv.HasValue && _lastRecv.HasValue) ? _lastRecv.Value - _firstRecv.Value : 0;
        float median = 0, max = 0;
        int jumps = 0;
        if (_intervalsMs.Count > 0)
        {
            var sorted = _intervalsMs.ToArray();
            Array.Sort(sorted);
            median = sorted[sorted.Length / 2];
            max = sorted[^1];
            jumps = sorted.Count(x => x > 100);
        }
        return new WriterStats
        {
            PacketsReceived = PacketsReceived,
            RowsWritten = RowsWritten,
            IdleRowsCollapsed = IdleRowsCollapsed,
            EffectiveHz = duration > 0 ? Math.Round((PacketsReceived - 1) / duration, 3) : 0,
            MedianIntervalMs = median,
            IntervalJumps = jumps,
            MaxIntervalMs = max,
            BadSizePackets = BadSizePackets,
            RaceStateTransitions = RaceStateTransitions,
            DurationSec = Math.Round(duration, 3),
            CarsSeen = _carsSeen.OrderBy(x => x).ToList(),
        };
    }

    public void Dispose()
    {
        FlushIdle();
        _csv.Dispose();
        _raw?.Dispose();
    }
}

public sealed class WriterStats
{
    public long PacketsReceived { get; set; }
    public long RowsWritten { get; set; }
    public long IdleRowsCollapsed { get; set; }
    public double EffectiveHz { get; set; }
    public float MedianIntervalMs { get; set; }
    public int IntervalJumps { get; set; }
    public float MaxIntervalMs { get; set; }
    public long BadSizePackets { get; set; }
    public int RaceStateTransitions { get; set; }
    public double DurationSec { get; set; }
    /// <summary>"car_ordinal|クラスPI|駆動方式"(レース中/フリーローム中の行のみ)</summary>
    public List<string> CarsSeen { get; set; } = new();

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
