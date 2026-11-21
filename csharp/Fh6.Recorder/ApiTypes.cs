using System.Text.Json.Serialization;

namespace Fh6.Recorder;

// 記録アプリの画面(wwwroot/recorder.js)とのやりとりの型。JSON のキーは、プロパティの名前の頭を小文字にしたもの。
// JS の型定義(Fh6.Recorder/api.d.ts)はこれから作る。変えたら、環境変数 HEISO_UPDATE_GOLDEN=1 を付けて
// dotnet test csharp/Fh6.Recorder.Tests を回すと書き直す(ApiTypesTests)

// ---------------------------------------------------------------- 画面 → C#(POST の本文)

public sealed record StartRequest(bool? Video, string? Label, string? Quality);
public sealed record QualityRequest(string? Preset);
public sealed record SceneRequest(string? Scene);
public sealed record MarkerRequest(string? Label);
public sealed record CarRequest(string Id, string? Car, string? SetupChoice, string? SetupName, string? Note);
public sealed record CourseRequest(string Id, string? Choice, string? Name, string? Kind, string? Note);
public sealed record DismissRequest(string Id);
public sealed record LastRaceRequest(string? CourseChoice, string? CourseName, string? Kind, string? Car, string? SetupChoice, string? SetupName);

// ---------------------------------------------------------------- C# → 画面

/// <summary>うまくいかなかったときの答え(4xx)</summary>
public sealed record ApiError(string? Error);

/// <summary>/api/marker の答え: 打ったマーカーの数</summary>
public sealed record MarkerResult(int Count);

/// <summary>/api/connect-info の答え: 画面を開いたのが PC 自身か、スマホから開くアドレスの候補</summary>
public sealed record ConnectInfo(bool IsLocal, IReadOnlyList<ConnectUrl> Urls);
public sealed record ConnectUrl(string Address, string Name, string Url);

/// <summary>/api/status の答え(操作の POST の多くも、これを返す)</summary>
public sealed record RecorderStatus(
    // idle / starting / running / stopping
    string State,
    string? Warning,
    LiveSnapshot Telemetry,
    ObsStatus Obs,
    SessionStatus? Session,
    LastSessionInfo? Last,
    RaceStatus? Race,
    QualityStatus Quality,
    SceneStatus? Scene,
    string OutputDir,
    // 今の速度を画面の単位で(appsettings の SpeedUnit。自動なら Windows の地域)。受信していなければ null
    string? SpeedText);

public sealed record ObsStatus(bool Enabled, bool Connected, bool Recording, string? Version, string? Error);

/// <summary>記録中のセッション</summary>
public sealed record SessionStatus(
    string Name, string Folder, string? Label, double ElapsedSec, long Rows, long Packets, int Markers,
    bool VideoRequested, bool VideoActive, string? Quality, bool QualityApplied);

/// <summary>録画の画質の段階</summary>
public sealed record QualityStatus(string Selected, IReadOnlyList<QualityPresetStatus> Presets);
public sealed record QualityPresetStatus(
    string Id, string Name, int Height, int Fps, int MbPerMin, string? Note,
    // OBS にあるプロファイル(日本語・英語どちらの名前でも)。無ければ今の言語で作る名前
    string Profile,
    // OBS にプロファイルがあるか(OBS 未接続で分からなければ null)
    bool? Ready);

/// <summary>録画するシーン</summary>
public sealed record SceneStatus(
    string? Selected,
    // OBS で今選んでいる(プログラムの)シーン。未接続なら null
    string? Current,
    // OBS の上からの順。未接続なら null
    IReadOnlyList<string>? Scenes);

// ---------------------------------------------------------------- レースの見張り(RaceWatcher)

public sealed record RaceStatus(
    RaceCar? Car,
    CourseRef? Course,
    bool InRace,
    IReadOnlyList<RacePrompt> Prompts,
    KnownNames Known,
    // コースの種類 → 今の言語の名前
    Dictionary<string, string> Kinds,
    LastRaceStatus? LastRace,
    IReadOnlyList<RecentLine> Recent,
    string? RegistryWarning);

/// <summary>今の車両</summary>
public sealed record RaceCar(int Ordinal, string Cls, int Pi, string Drive, int Cyl, string? Name, string? Setup, bool Confirmed);

public sealed record CourseRef(string Id, string Name, string? Kind);

/// <summary>車両の見分け(クラス・PI・駆動・気筒)</summary>
public sealed record CarSig(int Ordinal, string Cls, int Pi, string Drive, int Cyl);

/// <summary>確認のカード。type で中身が決まる</summary>
[JsonDerivedType(typeof(CarPromptStatus))]
[JsonDerivedType(typeof(CoursePromptStatus))]
[JsonDerivedType(typeof(UnknownPromptStatus))]
public abstract record RacePrompt;

/// <summary>車両の確認(type = "car")</summary>
public sealed record CarPromptStatus(
    string Id, string Type, string Created, CarSig Sig,
    // 前の車両から変わったもの(今の言語の名前)
    IReadOnlyList<string> Changed,
    // 記録を始めたときの車両(変わったのではない)
    bool AtStart,
    // 車名は今の登録から引く(同じ車種の別のカードで名前を入れたら、残りのカードにも出る)
    string? KnownName,
    IReadOnlyList<SetupOption> Setups,
    string? GuessSetup) : RacePrompt;

/// <summary>コースの確認(type = "course")</summary>
public sealed record CoursePromptStatus(string Id, string Type, string Created, StartPoint Start) : RacePrompt;

public sealed record UnknownPromptStatus(string Id, string Type, string Created) : RacePrompt;

public sealed record SetupOption(string Id, string Name, int Variant);
public sealed record StartPoint(double X, double Z, double Yaw);

/// <summary>登録済みの車名とコース(入力の候補)</summary>
public sealed record KnownNames(IReadOnlyList<string> Cars, IReadOnlyList<KnownCourse> Courses);
public sealed record KnownCourse(string Id, string Name, string? Kind, int Seen);

/// <summary>直前のレース(後から直せる)</summary>
public sealed record LastRaceStatus(string StartedAt, bool Ended, string? Recorded, CourseRef? Course, LastRaceCar? Car);
public sealed record LastRaceCar(
    int Ordinal, string Cls, int Pi, string Drive, int Cyl, string? Name, string? SetupId, string? Setup,
    IReadOnlyList<SetupRef> Setups);
public sealed record SetupRef(string Id, string Name);

/// <summary>最近の出来事(新しい順)</summary>
public sealed record RecentLine(string At, string Text);
