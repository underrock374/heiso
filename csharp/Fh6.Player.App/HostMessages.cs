using System.Text.Json.Serialization;
using Fh6.Core;

namespace Fh6.Player;

// C# から画面(JS)へのメッセージ。JS は messaging.js の onHostMessage で受ける。
// type の値は JsonDerivedType の名前、ほかのプロパティはキャメルケース(MessageJson.Options)。null の値も省かずに送る。
// 型の名前は Host + type の値の先頭を大文字にしたもの。JS の型定義(messages.d.ts)はここから作る(Fh6.Player.App.Tests の MessageTypesTests)

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HostLoading), "loading")]
[JsonDerivedType(typeof(HostError), "error")]
[JsonDerivedType(typeof(HostSession), "session")]
[JsonDerivedType(typeof(HostNotice), "notice")]
[JsonDerivedType(typeof(HostToast), "toast")]
[JsonDerivedType(typeof(HostRaceNamesForm), "raceNamesForm")]
[JsonDerivedType(typeof(HostFfmpegStatus), "ffmpegStatus")]
[JsonDerivedType(typeof(HostJob), "job")]
[JsonDerivedType(typeof(HostPrefs), "prefs")]
[JsonDerivedType(typeof(HostRestore), "restore")]
[JsonDerivedType(typeof(HostCrashNotice), "crashNotice")]
[JsonDerivedType(typeof(HostReplayFound), "replayFound")]
[JsonDerivedType(typeof(HostVideoMissing), "videoMissing")]
[JsonDerivedType(typeof(HostVideoSelected), "videoSelected")]
[JsonDerivedType(typeof(HostSessionList), "sessionList")]
[JsonDerivedType(typeof(HostFriction), "friction")]
[JsonDerivedType(typeof(HostRaceSelected), "raceSelected")]
public abstract record HostMessage;

// ---- 開く・知らせる ----

/// <summary>記録を読み込み始めた(name はフォルダ名かファイル名)</summary>
public sealed record HostLoading(string Name) : HostMessage;

/// <summary>開けなかった</summary>
public sealed record HostError(string Message) : HostMessage;

/// <summary>知らせ(手動補正の欄も片付ける)。error があればエラー</summary>
public sealed record HostNotice(string? Message, string? Error) : HostMessage;

/// <summary>画面の下に短く知らせる。sec は出しておく秒数(null なら画面の既定)</summary>
public sealed record HostToast(string? Message, string? Error, double? Sec) : HostMessage;

/// <summary>長くかかる処理の進み具合(text が null なら消す)</summary>
public sealed record HostJob(string? Text, double Progress, bool Cancellable) : HostMessage;

/// <summary>前回が正常に終わっていない(ログとクラッシュダンプの場所)</summary>
public sealed record HostCrashNotice(string StartedAt, string LogDir, string? Dump) : HostMessage;

/// <summary>画面の設定のダイアログに出す、C# 側で持っている設定</summary>
public sealed record HostPrefs(bool RememberWindow, string Language, int ReplayLeadFrames, int ReplayLeadDefault) : HostMessage;

/// <summary>画面を読み直した後に戻す位置。race は選ぶレース(null なら選ばない)。quiet なら「画面が止まった」と知らせない</summary>
public sealed record HostRestore(double Sec, bool Play, bool Quiet, int? Race) : HostMessage;

// ---- 記録 ----

/// <summary>開いている記録(画面の全部を作り直す)。keepVideo なら動画の位置はそのまま。race は選んでいるレース(SelectionViewModel。null なら「全体」)</summary>
public sealed record HostSession(
    bool KeepVideo, string Name, string Label, string? Comment, bool Package, string StartedAt, bool Complete,
    string? VideoUrl, VideoChoice[] Videos, string? ActiveVideo, AltVideoInfo? Alt, bool CanAddVideo, bool HasRecording, string? VideoError,
    string Method, string? MapError, SyncInfo Sync, int Rows, double? Fps,
    SessionSeries? Series, SessionMarks? Marks, MeasureData Measure, int? Race) : HostMessage;

/// <summary>上の欄の「動画」の一覧の 1 行(録画は画面の側で先頭に足す)。status は VideoAltStatus の小文字</summary>
public sealed record VideoChoice(string Id, string Kind, string Content, int? Race, string Label, string Status);

/// <summary>画面の「この動画」の欄。shiftMs はリプレイの保存したずらし量(ミリ秒。正なら映像を後ろへ)</summary>
public sealed record AltVideoInfo(
    string Id, string Kind, string Content, int? Race, string Method, string? Confidence, double? Corr, double? Lag,
    double? Zero, double? End, double? ShiftMs, double? Duration, int Points, bool Vfr);

/// <summary>動画との同期の状態</summary>
public sealed record SyncInfo(
    int SamplesTotal, int SamplesUsed, bool IsFallback, int ManualSync, double ManualShiftSec,
    double? EndCheckSec, double? ClockRate, double? FileDurationSec);

/// <summary>名前を付けるダイアログの中身。car は車が分からなければ null</summary>
public sealed record HostRaceNamesForm(int Race, bool Editable, string? Reason, RaceNamesCourse Course, RaceNamesCar? Car) : HostMessage;

/// <summary>登録済みのコース(スタートの位置で一致するもの matchId を先頭に)</summary>
public sealed record RaceNamesCourse(string? Current, string? MatchId, CourseChoice[] List);

public sealed record CourseChoice(string Id, string Name, string? Kind);

/// <summary>車種の名前と、同じ組のセッティング</summary>
public sealed record RaceNamesCar(int Ordinal, string Sig, string? Current, string? Registered, string? Setup, SetupChoice[] Setups);

public sealed record SetupChoice(string Id, string Name);

// ---- 書き出しと動画 ----

/// <summary>ffmpeg の状態。checking の間は、ほかは null</summary>
public sealed record HostFfmpegStatus(
    bool Checking, string? Configured, string? Path, string? Version, FfmpegEncoderChoice[]? Encoders, string? Encoder, string? Selected,
    string? ExportMode) : HostMessage;

public sealed record FfmpegEncoderChoice(string Id, string Name);

/// <summary>
/// リプレイを探した結果(レースごと)。selected は選ぶレース。
/// assigned は録画の中のリプレイを割り当て済みのレース(「採用」「手動で合わせる」で作り直す前に確かめる)
/// </summary>
public sealed record HostReplayFound(ReplayFoundItem[] Results, int? Selected, ReplayAssignedItem[] Assigned) : HostMessage;

/// <summary>
/// 割り当て済みのリプレイの今の合わせ方。manual は手動で合わせたか、zero は先頭の動画の秒、
/// shiftMs・shiftFrames は「同期の微調整」でずらした量(正なら映像を後ろへ。コマは録画のフレームレートで、小数 1 桁)
/// </summary>
public sealed record ReplayAssignedItem(int Race, string Id, bool Manual, double Zero, double ShiftMs, double? ShiftFrames);

public sealed record ReplayFoundItem(
    int Race, bool Found, double? Zero, double? End, double Duration, string? Confidence, double? Corr, double? Lag, string? Warning);

/// <summary>割り当てた動画が見つからない(changed なら、ファイルはあるが中身が変わった)</summary>
public sealed record HostVideoMissing(string? Id, bool Changed, string? Active) : HostMessage;

/// <summary>表示している動画(録画なら null)。切り替えられなかったとき、選ぶ欄を戻す</summary>
public sealed record HostVideoSelected(string? Active) : HostMessage;

// ---- 記録の一覧 ----

/// <summary>記録の一覧。root は保存先(分からなければ null)、result は直前の操作の結果</summary>
public sealed record HostSessionList(string? Root, bool Archive, SessionListItem[] Items, SessionListResult? Result) : HostMessage;

/// <summary>一覧の 1 行。blocked は移せない・名前を変えられない理由</summary>
public sealed record SessionListItem(
    string Path, string Name, string Stamp, string Rest, string? Started, bool Complete, string? Error, double? Duration,
    string[] Courses, SessionCar[] Cars, int Races, bool Video, long Bytes, string? Blocked, bool Open);

/// <summary>操作の結果。kind は archive・restore(moved と errors)か rename(renamed か error)</summary>
public sealed record SessionListResult(string Kind, int? Moved, MoveError[]? Errors, string? Renamed, string? Error);

public sealed record MoveError(string Name, string Reason);

// ---- メッセージでないもの ----


// ---- レースの選択と、摩擦円・荷重移動(SelectionViewModel、FrictionDynamic.Build) ----

/// <summary>選んでいるレースが変わった(race は記録の中の番号。null なら「全体」)。cause は ViewSelectRace の理由のまま</summary>
public sealed record HostRaceSelected(int? Race, string Cause) : HostMessage;

/// <summary>
/// 選んでいるレース(race。null なら記録全体)とならしの幅(sec)での摩擦円(gg)と荷重移動(load)。記録を送った後と、レース・幅が変わったときに送る(SelectionViewModel)。
/// baseWheels は荷重移動の基準の、4 輪それぞれの縮みの中央値(fl・fr・rl・rr。4 輪の棒の白の線。作れなければ null)。
/// loadLong・loadLat・grip はグラフの系列(全部の行。%、小数 1 桁)
/// </summary>
public sealed record HostFriction(
    int? Race, double? Sec, FrictionView Gg, FrictionView Load, double[]? BaseWheels, double?[] LoadLong, double?[] LoadLat, double?[] Grip) : HostMessage;

/// <summary>
/// 摩擦円か荷重移動の 1 つ。x・y は行ごとの点(摩擦円は [横 G, 前後 G]、荷重移動は基準からの縮みの差 [右 − 左, 前 − 後ろ]。丸めない。無ければ null)。
/// rings は路面ごとの円、scaleMax は表示の上限、cloud は点群に出す行(表示の上限で切る前)
/// </summary>
public sealed record FrictionView(double?[] X, double?[] Y, FrictionRing[] Rings, double ScaleMax, int[] Cloud);

/// <summary>路面のまとめ(FrictionStatic.SurfaceGroups の 0 以上の番号)ごとの円。r は半径、n は使った行数</summary>
public sealed record FrictionRing(int Group, double R, int N);
