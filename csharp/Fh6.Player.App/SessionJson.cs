using Fh6.Core;

namespace Fh6.Player;

// 画面へ渡すデータの型(LoadedSession の BuildSeries・BuildMarks・BuildMeasure が作る)。
// プロパティの名前は JSON ではキャメルケースになる(MessageJson.Options)。値の意味は Build の側のコメント

// ---- 系列(BuildSeries) ----

public sealed record SessionSeries(
    double[] T, double?[] Speed, double?[] Accel, double?[] Brake, double?[] Steer, double?[] Gear, double?[] SlipF, double?[] SlipR,
    double?[] Balance, int?[] Counter, double?[] Grade, double?[] Elev, double?[] X, double?[] Z, double?[] Turn, double?[] GLong, double?[] GLat,
    SuspensionSeries Susp, double?[] Rpm, double?[] OptRpm, double?[] OptRpmHi, double?[] Gain, int?[] Full, int?[] PerfCar, PerfData Perf, PerfReadoutSeries Readout, SurfaceSeries Surface, CourseSeries Course, FrictionSeries Friction);

public sealed record SuspensionSeries(double?[] Fl, double?[] Fr, double?[] Rl, double?[] Rr);

/// <summary>摩擦円・荷重移動の、記録を開いたときに決まる値: 行ごとの路面のまとめ(FrictionStatic.SurfaceGroups)と 4 輪の棒(FrictionStatic.WheelBars)</summary>
public sealed record FrictionSeries(int[] Group, WheelBarsSeries Wheels);

/// <summary>
/// 4 輪の棒。State は行ごとの 4 輪の状態を 1 つの整数にまとめたもの: 車輪 k(fl・fr・rl・rr の順に 0〜3)の 2k ビット目 = 浮きかけ、2k+1 ビット目 = 縮み切りに近い
/// </summary>
public sealed record WheelBarsSeries(WheelBar Fl, WheelBar Fr, WheelBar Rl, WheelBar Rr, int?[] State);

/// <summary>
/// 4 輪の棒の 1 輪: ならした縮み(1 万分の 1 の整数)、幅の中でいちばん縮んだ値(1000 分の 1 の整数)。
/// 整数で送るのは、行の数 × 8 の小数を JSON にする時間と大きさを減らすため
/// </summary>
public sealed record WheelBar(int?[] Avg, int?[] Hi);

public sealed record SurfaceSeries(int?[] Fl, int?[] Fr, int?[] Rl, int?[] Rr);

/// <summary>コース図の線: 行ごとの路面の分類(CourseLine.SurfaceKinds)と、線を切る行(CourseLine.BreakRows)</summary>
public sealed record CourseSeries(int?[] Surface, int[] Breaks);

public sealed record PerfData(PerfCarInfo[] Cars);

/// <summary>
/// 性能モードの読み取り値(PerfReadout.Build): 今の回転数の全開の出力 hp、今すぐ変速した後の回転数と出力(全開の行だけ)、
/// 状態(得失があれば early / ok / late、無ければ出せない理由。PerfReadout.States。分からなければ null)
/// </summary>
public sealed record PerfReadoutSeries(double?[] Hp, double?[] RpmAfter, double?[] HpAfter, string?[] State);

public sealed record PerfCarInfo(
    int Ordinal, int Pi, double[][]? Curve, double? MaxHp, LoadedSession.PowerSource? Source, double? Limiter, int[] Gears,
    Dictionary<string, double[]> Range, Dictionary<string, double> Ratios, Dictionary<string, double?> Opt);

// ---- 印(BuildMarks) ----

public sealed record SessionMarks(
    StartMark[] Starts, ContactMark[] Contacts, ExcursionMark[] Excursions, RaceMark[] Races, SkipMark[] Skips, LapMark[] Laps, ShiftMark[] Shifts, JumpMark[] Jumps,
    int[]? ElevRange, int[] Cut, int[] GgExcluded);

public sealed record StartMark(double T, bool Race, string Kind);

public sealed record ContactMark(double T, bool Landing, double G, double Before, double After);

public sealed record ExcursionMark(double T0, double T1, string[] Wheels, int[] Surfaces, double? SlipT, double? SlipEndT, double SlipBandT1, double SlipPeak, bool Gap);

public sealed record RaceMark(
    double T0, double T1, double Tf, bool Restart, bool Finished, string FinishedBy, double EndKmh, string? Course, string? Car, string? Setup,
    RaceDistanceSeries Dist, int[]? ElevRange, bool? Auto, double? AutoLo, double? AutoHi);

public sealed record RaceDistanceSeries(List<int> I, List<double> D, List<int> K, int[] Graph);

public sealed record SkipMark(double T0, double T1, string Kind);

public sealed record LapMark(double T, int Lap, double? Prev, bool Finish, bool ByPosition);

public sealed record ShiftMark(double T, int From, double Rpm, double? Gain, string? Verdict, double LimiterSec, bool Auto);

public sealed record JumpMark(double T0, double T1, double Sec, double Kmh, double Drop);

// ---- 計測モード(BuildMeasure) ----

public sealed record MeasureData(MeasureCar[] Cars, MeasureRace[] Races);

public sealed record MeasureCar(
    string Key, string? Name, int Pi, int Drivetrain, int Cylinders, int Parts, int Recordings, double Seconds, double[][]? Curve, double? MaxHp, double? PeakRpm,
    double? Limiter, Dictionary<string, double> Ratios, int[] Gears, MeasureTop? Top, Dictionary<string, Dictionary<string, int>> Coverage, int? UsedFrom,
    MeasureBand[] Bands, Dictionary<string, MeasureGroup> Groups);

public sealed record MeasureTop(int Gear, double Kmh, double Rpm);

public sealed record MeasureBand(int From, int To, int Rows, string State);

public sealed record MeasureGroup(int Parts, Dictionary<string, MeasureLimits> Limits, MeasureAdvice[] Advice);

public sealed record MeasureLimits(MeasureLimit Brake, Dictionary<string, MeasureLimit> BrakeBySpeed, MeasureLimit Left, MeasureLimit Right, MeasureLimit Launch);

public sealed record MeasureLimit(double? G, int Rows, bool Measured);

public sealed record MeasureAdvice(string Kind, int? From, int? To, int? Gear, double? Kmh, string? Surface, string? Side);

public sealed record MeasureRace(string? Car, Assists? Assists, string AssistKey);
