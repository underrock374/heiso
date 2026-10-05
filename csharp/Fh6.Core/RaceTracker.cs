namespace Fh6.Core;

/// <summary>車両を見分ける組: 車種・クラス・PI・駆動方式・気筒数(ロガー v3 と同じ)</summary>
public readonly record struct CarSignature(int Ordinal, int Class, int Pi, int Drivetrain, int Cylinders)
{
    public string ClassName => Fh6Packet.ClassName(Class);
    public string DrivetrainName => Fh6Packet.DrivetrainName(Drivetrain);

    /// <summary>「S1 800 AWD 6気筒」</summary>
    public override string ToString() => $"{ClassName} {Pi} {DrivetrainName} {Cylinders}気筒";

    /// <summary>前の車両から何が変わったか(画面と session.json に出す言葉。v3 と同じ)</summary>
    public static List<string> Changes(CarSignature? prev, CarSignature now)
    {
        var what = new List<string>();
        if (prev is not CarSignature p) what.Add("記録開始時の車両");
        else if (p.Ordinal != now.Ordinal) what.Add("車種");
        else
        {
            if (p.Class != now.Class || p.Pi != now.Pi) what.Add("PI");
            if (p.Drivetrain != now.Drivetrain) what.Add("駆動方式");
            if (p.Cylinders != now.Cylinders) what.Add("気筒数");
        }
        return what;
    }
}

/// <summary>判定に使うパケット1つ分の値(受信したパケットにも、CSV の行にも使う)</summary>
public readonly record struct RaceSample(
    double RecvTime, double TimestampMs, bool IsRaceOn, int RacePos, double CurRaceTime, double Speed,
    double X, double Z, double Yaw, CarSignature Car)
{
    public static RaceSample FromPacket(double recvTime, double[] v) => new(
        recvTime, v[Fh6Packet.TimestampMs], v[Fh6Packet.IsRaceOn] == 1, (int)v[Fh6Packet.RacePos], v[Fh6Packet.CurRaceTime],
        v[Fh6Packet.Speed], v[Fh6Packet.PositionX], v[Fh6Packet.PositionZ], v[Fh6Packet.Yaw],
        new CarSignature((int)v[Fh6Packet.CarOrdinal], (int)v[Fh6Packet.CarClass], (int)v[Fh6Packet.CarPi],
                         (int)v[Fh6Packet.Drivetrain], (int)v[Fh6Packet.NumCylinders]));

    /// <summary>CSV から読むときに要る列</summary>
    public static readonly string[] Columns =
    {
        TelemetryTable.RecvTime, "timestamp_ms", "is_race_on", "race_pos", "cur_race_time", "speed",
        "position_x", "position_z", "yaw", "car_ordinal", "car_class", "car_pi", "drivetrain", "num_cylinders",
    };

    public static RaceSample FromTable(TelemetryTable t, int i)
    {
        static int I(double v) => double.IsNaN(v) ? 0 : (int)v;
        return new(
            t[TelemetryTable.RecvTime][i], t["timestamp_ms"][i], t["is_race_on"][i] == 1, I(t["race_pos"][i]), t["cur_race_time"][i],
            t["speed"][i], t["position_x"][i], t["position_z"][i], t["yaw"][i],
            new CarSignature(I(t["car_ordinal"][i]), I(t["car_class"][i]), I(t["car_pi"][i]), I(t["drivetrain"][i]), I(t["num_cylinders"][i])));
    }
}

/// <summary>RaceTracker が見つけた出来事</summary>
public abstract record RaceTrackerEvent(double RecvTime, double TimestampMs);

/// <summary>車両が切り替わった(同じ組が CarStablePackets 続いた)。Previous が null なら最初の車両</summary>
public sealed record CarChanged(double RecvTime, double TimestampMs, CarSignature? Previous, CarSignature Car)
    : RaceTrackerEvent(RecvTime, TimestampMs)
{
    public List<string> Changes => CarSignature.Changes(Previous, Car);
}

/// <summary>レースが始まった。Restart は2通り目(停止区間の明けにレースの経過時間が 0 に戻った)の検出</summary>
public sealed record RaceStarted(double RecvTime, double TimestampMs, bool Restart, double X, double Z, double Yaw)
    : RaceTrackerEvent(RecvTime, TimestampMs);

/// <summary>レースが終わった(順位が 1 以上 → 0)</summary>
public sealed record RaceEnded(double RecvTime, double TimestampMs, double LastSpeedKmh)
    : RaceTrackerEvent(RecvTime, TimestampMs);

/// <summary>
/// ロガー v3 の LiveState の判定(車両の切り替わり、レースの開始2通りと終了)を、パケット1つずつ受けて出来事を返す。
/// 記録中の受信パケットにも、記録済みの CSV の行にも使える(CSV の停止区間は先頭と末尾の行に縮めてあるが、判定の結果は変わらない)。
/// </summary>
public sealed class RaceTracker
{
    /// <summary>同じ車両の組がこの数だけ続いたら切り替わりとする(約 1 秒)</summary>
    public const int CarStablePackets = 30;
    /// <summary>2通り目のレース開始: 停止区間の明けのレースの経過時間がこれ未満</summary>
    public const double RestartMaxRaceTimeSec = 0.5;
    /// <summary>2通り目のレース開始: 直前のレース中の経過時間がこれより大きい(リワインドは 0 より大きい値に戻るので区別できる)</summary>
    public const double RestartMinPrevRaceTimeSec = 3.0;

    private static readonly RaceTrackerEvent[] None = Array.Empty<RaceTrackerEvent>();

    private CarSignature? _candidate;
    private int _candidateCount;
    private int _prevRacePos;
    private bool _prevRaceOn;
    private double _lastRaceTime;

    /// <summary>今の車両(まだ確定していなければ null)</summary>
    public CarSignature? Car { get; private set; }

    /// <summary>レース中か(順位が 1 以上)</summary>
    public bool InRace => _prevRacePos >= 1;

    public IReadOnlyList<RaceTrackerEvent> Feed(in RaceSample s)
    {
        List<RaceTrackerEvent>? events = null;
        if (s.IsRaceOn)
        {
            if (CheckCar(s) is CarChanged c) (events ??= new()).Add(c);

            var rt = s.CurRaceTime;
            if (_prevRacePos == 0 && s.RacePos >= 1)
                (events ??= new()).Add(new RaceStarted(s.RecvTime, s.TimestampMs, false, s.X, s.Z, s.Yaw));
            else if (s.RacePos >= 1 && !_prevRaceOn && rt < RestartMaxRaceTimeSec && _lastRaceTime > RestartMinPrevRaceTimeSec)
                // 停止区間の明けに経過時間が 0 に戻った = リスタートか、続けて次のレース
                (events ??= new()).Add(new RaceStarted(s.RecvTime, s.TimestampMs, true, s.X, s.Z, s.Yaw));
            else if (_prevRacePos >= 1 && s.RacePos == 0)
                (events ??= new()).Add(new RaceEnded(s.RecvTime, s.TimestampMs, Math.Round(s.Speed * 3.6, 1)));

            _prevRacePos = s.RacePos;
            if (s.RacePos >= 1) _lastRaceTime = rt;
        }
        _prevRaceOn = s.IsRaceOn;
        return events ?? (IReadOnlyList<RaceTrackerEvent>)None;
    }

    private CarChanged? CheckCar(in RaceSample s)
    {
        var sig = s.Car;
        if (sig.Ordinal <= 0) return null;
        if (sig == Car)
        {
            _candidate = null;
            _candidateCount = 0;
            return null;
        }
        if (sig != _candidate)
        {
            _candidate = sig;
            _candidateCount = 1;
            return null;
        }
        if (++_candidateCount < CarStablePackets) return null;

        var prev = Car;
        Car = sig;
        _candidate = null;
        _candidateCount = 0;
        return new CarChanged(s.RecvTime, s.TimestampMs, prev, sig);
    }

    /// <summary>記録済みの CSV を最初から流して、出来事と、それが起きた行の番号を返す</summary>
    public static List<(int Index, RaceTrackerEvent Event)> Replay(TelemetryTable t)
    {
        var tracker = new RaceTracker();
        var result = new List<(int, RaceTrackerEvent)>();
        for (int i = 0; i < t.RowCount; i++)
            foreach (var e in tracker.Feed(RaceSample.FromTable(t, i)))
                result.Add((i, e));
        return result;
    }
}
