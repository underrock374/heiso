using System.Text.Json.Serialization;

namespace Fh6.Player;

// 画面(JS)から C# へのメッセージ。JS は messaging.js の sendToHost で送る。
// type の値は JsonDerivedType の名前、ほかのプロパティはキャメルケース(MessageJson.Options)。
// JS は値を省くことがあり、NaN は null で届くので、プロパティは全部 null を許す(受けた側で既定の値にする)。
// 型の名前は View + type の値の先頭を大文字にしたもの。JS の型定義(messages.d.ts)はここから作る(Fh6.Player.App.Tests の MessageTypesTests)

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ViewPosition), "position")]
[JsonDerivedType(typeof(ViewLog), "log")]
[JsonDerivedType(typeof(ViewSetLanguage), "setLanguage")]
[JsonDerivedType(typeof(ViewCrashNoticeClosed), "crashNoticeClosed")]
[JsonDerivedType(typeof(ViewOpenLogs), "openLogs")]
[JsonDerivedType(typeof(ViewReady), "ready")]
[JsonDerivedType(typeof(ViewOpen), "open")]
[JsonDerivedType(typeof(ViewOpenPackage), "openPackage")]
[JsonDerivedType(typeof(ViewExport), "export")]
[JsonDerivedType(typeof(ViewCancelJob), "cancelJob")]
[JsonDerivedType(typeof(ViewProbeFfmpeg), "probeFfmpeg")]
[JsonDerivedType(typeof(ViewSetFfmpegPath), "setFfmpegPath")]
[JsonDerivedType(typeof(ViewBrowseFfmpeg), "browseFfmpeg")]
[JsonDerivedType(typeof(ViewSetExportEncoder), "setExportEncoder")]
[JsonDerivedType(typeof(ViewSetReplayLead), "setReplayLead")]
[JsonDerivedType(typeof(ViewRememberWindow), "rememberWindow")]
[JsonDerivedType(typeof(ViewSaveSettings), "saveSettings")]
[JsonDerivedType(typeof(ViewManualSync), "manualSync")]
[JsonDerivedType(typeof(ViewRaceNamesForm), "raceNamesForm")]
[JsonDerivedType(typeof(ViewEditRaceNames), "editRaceNames")]
[JsonDerivedType(typeof(ViewClearManualSync), "clearManualSync")]
[JsonDerivedType(typeof(ViewMethod), "method")]
[JsonDerivedType(typeof(ViewSelectVideo), "selectVideo")]
[JsonDerivedType(typeof(ViewAddReplayAuto), "addReplayAuto")]
[JsonDerivedType(typeof(ViewAdoptReplay), "adoptReplay")]
[JsonDerivedType(typeof(ViewAddVideoFile), "addVideoFile")]
[JsonDerivedType(typeof(ViewRelinkVideo), "relinkVideo")]
[JsonDerivedType(typeof(ViewRemoveVideo), "removeVideo")]
[JsonDerivedType(typeof(ViewAltMark), "altMark")]
[JsonDerivedType(typeof(ViewAltNudge), "altNudge")]
[JsonDerivedType(typeof(ViewSessionList), "sessionList")]
[JsonDerivedType(typeof(ViewSessionPickRoot), "sessionPickRoot")]
[JsonDerivedType(typeof(ViewSessionOpen), "sessionOpen")]
[JsonDerivedType(typeof(ViewSessionMove), "sessionMove")]
[JsonDerivedType(typeof(ViewSessionRename), "sessionRename")]
[JsonDerivedType(typeof(ViewSelectRace), "selectRace")]
public abstract record ViewMessage;

// ---- 画面の状態と設定 ----

/// <summary>動画の位置(約 2 秒ごと。画面が落ちたときに戻すため)</summary>
public sealed record ViewPosition(double? Sec, bool? Playing) : ViewMessage;

/// <summary>画面のエラーや操作の記録。level は info か error</summary>
public sealed record ViewLog(string? Level, string? Message) : ViewMessage;

/// <summary>画面の言語(ja / en / auto)</summary>
public sealed record ViewSetLanguage(string? Language) : ViewMessage;

public sealed record ViewCrashNoticeClosed : ViewMessage;

public sealed record ViewOpenLogs : ViewMessage;

/// <summary>画面の準備ができた。method は画面の設定の同期の方式(VideoSyncMethod の名前)</summary>
public sealed record ViewReady(string? Method) : ViewMessage;

public sealed record ViewOpen : ViewMessage;

public sealed record ViewOpenPackage : ViewMessage;

/// <summary>レースを書き出す。mode が "720" なら 720p に作り直す</summary>
public sealed record ViewExport(int? Race, string? Mode, bool? NoAudio) : ViewMessage;

public sealed record ViewCancelJob : ViewMessage;

/// <summary>ffmpeg の状態を調べる(force なら覚えている結果を使わない)</summary>
public sealed record ViewProbeFfmpeg(bool? Force) : ViewMessage;

public sealed record ViewSetFfmpegPath(string? Path) : ViewMessage;

public sealed record ViewBrowseFfmpeg : ViewMessage;

public sealed record ViewSetExportEncoder(string? Encoder) : ViewMessage;

/// <summary>リプレイを割り当てたときのずらし量(コマ)</summary>
public sealed record ViewSetReplayLead(double? Frames) : ViewMessage;

public sealed record ViewRememberWindow(bool? On) : ViewMessage;

/// <summary>画面の設定(丸ごと)。変えるたびに送る</summary>
public sealed record ViewSaveSettings(ViewSettings? Settings) : ViewMessage;

// ---- 同期とレースの名前 ----

/// <summary>同期の手動補正の点。graphSec はグラフで指した瞬間(今の変換での動画の秒)、videoSec は映像のコマの秒</summary>
public sealed record ViewManualSync(double? GraphSec, double? VideoSec, string? Note) : ViewMessage;

public sealed record ViewClearManualSync : ViewMessage;

/// <summary>同期の方式を変える(VideoSyncMethod の名前)</summary>
public sealed record ViewMethod(string? Method) : ViewMessage;

/// <summary>名前を付けるダイアログの中身を頼む</summary>
public sealed record ViewRaceNamesForm(int? Race) : ViewMessage;

/// <summary>レースに付けた名前。id が "new" なら新しく登録する。assists は変えたときだけ</summary>
public sealed record ViewEditRaceNames(
    int? Race, string? CourseId, string? CourseName, string? CourseKind, string? CarName, string? SetupId, string? SetupName,
    ViewAssists? Assists) : ViewMessage;

/// <summary>補助の設定(on / off、変速は auto / manual。分からなければ null)</summary>
public sealed record ViewAssists(string? Abs, string? Tcs, string? Stm, string? Lc, string? Shift);

// ---- 割り当てた動画(sec は今の動画の秒、race は画面で選んでいるレース) ----

/// <summary>表示する動画を切り替える(id が null なら録画)</summary>
public sealed record ViewSelectVideo(string? Id, double? Sec, int? Race) : ViewMessage;

/// <summary>リプレイを探す。only があればそのレースだけ</summary>
public sealed record ViewAddReplayAuto(int? Only, int? Race) : ViewMessage;

public sealed record ViewAdoptReplay(bool? Manual, double? Sec, int? Race) : ViewMessage;

/// <summary>別に録った動画を割り当てる。content は live か replay</summary>
public sealed record ViewAddVideoFile(string? Content, double? Sec, int? Race) : ViewMessage;

public sealed record ViewRelinkVideo(string? Id, double? Sec, int? Race) : ViewMessage;

public sealed record ViewRemoveVideo(string? Id, double? Sec, int? Race) : ViewMessage;

/// <summary>リプレイの今のコマを、先頭(what = zero)かゴール(what = end)にする</summary>
public sealed record ViewAltMark(string? What, double? Sec) : ViewMessage;

/// <summary>同期の微調整(秒)</summary>
public sealed record ViewAltNudge(double? Delta) : ViewMessage;

// ---- 記録の一覧(archive はアーカイブの中を見ているか) ----

public sealed record ViewSessionList(bool? Archive) : ViewMessage;

public sealed record ViewSessionPickRoot(bool? Archive) : ViewMessage;

public sealed record ViewSessionOpen(string? Path) : ViewMessage;

public sealed record ViewSessionMove(string?[]? Paths, bool? Archive) : ViewMessage;

/// <summary>フォルダ名の日時より後ろ(rest)を変える</summary>
public sealed record ViewSessionRename(string? Path, string? Rest, bool? Archive) : ViewMessage;

// ---- レースの選択 ----

/// <summary>
/// レースを選ぶ(race は記録の中の番号。null なら「全体」)。cause は理由(SelectionViewModel の Cause…。pick: 選択欄で選んだ、follow: 再生位置が別のレースに入った、
/// axis: 横軸を距離にした、restore: 動画を切り替えて読み直した、leave: 表示幅やグラフの拡大で選択を外した)。答えは HostRaceSelected
/// </summary>
public sealed record ViewSelectRace(int? Race, string? Cause) : ViewMessage;
