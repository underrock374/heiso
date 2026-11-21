// 自動で作ったファイル。手で直さない。
// 元は csharp/Fh6.Player.App の ViewMessages.cs と HostMessages.cs。C# を変えたら、
// 環境変数 HEISO_UPDATE_GOLDEN=1 を付けて dotnet test csharp/Fh6.Player.App.Tests を回すと書き直す(MessageTypesTests)

/** 画面 → C#(messaging.js の sendToHost で送る)。省いた値は C# で既定の値になる */
type ViewMessage =
  | ViewPosition
  | ViewLog
  | ViewSetLanguage
  | ViewCrashNoticeClosed
  | ViewOpenLogs
  | ViewReady
  | ViewOpen
  | ViewOpenPackage
  | ViewExport
  | ViewCancelJob
  | ViewProbeFfmpeg
  | ViewSetFfmpegPath
  | ViewBrowseFfmpeg
  | ViewSetExportEncoder
  | ViewSetReplayLead
  | ViewRememberWindow
  | ViewSaveSettings
  | ViewManualSync
  | ViewRaceNamesForm
  | ViewEditRaceNames
  | ViewClearManualSync
  | ViewMethod
  | ViewSelectVideo
  | ViewAddReplayAuto
  | ViewAdoptReplay
  | ViewAddVideoFile
  | ViewRelinkVideo
  | ViewRemoveVideo
  | ViewAltMark
  | ViewAltNudge
  | ViewSessionList
  | ViewSessionPickRoot
  | ViewSessionOpen
  | ViewSessionMove
  | ViewSessionRename
  | ViewSelectRace;

interface ViewPosition {
  type: "position";
  sec?: number | null;
  playing?: boolean | null;
}

interface ViewLog {
  type: "log";
  level?: string | null;
  message?: string | null;
}

interface ViewSetLanguage {
  type: "setLanguage";
  language?: string | null;
}

interface ViewCrashNoticeClosed {
  type: "crashNoticeClosed";
}

interface ViewOpenLogs {
  type: "openLogs";
}

interface ViewReady {
  type: "ready";
  method?: string | null;
}

interface ViewOpen {
  type: "open";
}

interface ViewOpenPackage {
  type: "openPackage";
}

interface ViewExport {
  type: "export";
  race?: number | null;
  mode?: string | null;
  noAudio?: boolean | null;
}

interface ViewCancelJob {
  type: "cancelJob";
}

interface ViewProbeFfmpeg {
  type: "probeFfmpeg";
  force?: boolean | null;
}

interface ViewSetFfmpegPath {
  type: "setFfmpegPath";
  path?: string | null;
}

interface ViewBrowseFfmpeg {
  type: "browseFfmpeg";
}

interface ViewSetExportEncoder {
  type: "setExportEncoder";
  encoder?: string | null;
}

interface ViewSetReplayLead {
  type: "setReplayLead";
  frames?: number | null;
}

interface ViewRememberWindow {
  type: "rememberWindow";
  on?: boolean | null;
}

interface ViewSaveSettings {
  type: "saveSettings";
  settings?: ViewSettings | null;
}

interface ViewManualSync {
  type: "manualSync";
  graphSec?: number | null;
  videoSec?: number | null;
  note?: string | null;
}

interface ViewRaceNamesForm {
  type: "raceNamesForm";
  race?: number | null;
}

interface ViewEditRaceNames {
  type: "editRaceNames";
  race?: number | null;
  courseId?: string | null;
  courseName?: string | null;
  courseKind?: string | null;
  carName?: string | null;
  setupId?: string | null;
  setupName?: string | null;
  assists?: ViewAssists | null;
}

interface ViewClearManualSync {
  type: "clearManualSync";
}

interface ViewMethod {
  type: "method";
  method?: string | null;
}

interface ViewSelectVideo {
  type: "selectVideo";
  id?: string | null;
  sec?: number | null;
  race?: number | null;
}

interface ViewAddReplayAuto {
  type: "addReplayAuto";
  only?: number | null;
  race?: number | null;
}

interface ViewAdoptReplay {
  type: "adoptReplay";
  manual?: boolean | null;
  sec?: number | null;
  race?: number | null;
}

interface ViewAddVideoFile {
  type: "addVideoFile";
  content?: string | null;
  sec?: number | null;
  race?: number | null;
}

interface ViewRelinkVideo {
  type: "relinkVideo";
  id?: string | null;
  sec?: number | null;
  race?: number | null;
}

interface ViewRemoveVideo {
  type: "removeVideo";
  id?: string | null;
  sec?: number | null;
  race?: number | null;
}

interface ViewAltMark {
  type: "altMark";
  what?: string | null;
  sec?: number | null;
}

interface ViewAltNudge {
  type: "altNudge";
  delta?: number | null;
}

interface ViewSessionList {
  type: "sessionList";
  archive?: boolean | null;
}

interface ViewSessionPickRoot {
  type: "sessionPickRoot";
  archive?: boolean | null;
}

interface ViewSessionOpen {
  type: "sessionOpen";
  path?: string | null;
}

interface ViewSessionMove {
  type: "sessionMove";
  paths?: (string | null)[] | null;
  archive?: boolean | null;
}

interface ViewSessionRename {
  type: "sessionRename";
  path?: string | null;
  rest?: string | null;
  archive?: boolean | null;
}

interface ViewSelectRace {
  type: "selectRace";
  race?: number | null;
  cause?: string | null;
}

interface ViewSettings {
  contacts?: boolean | null;
  launches?: boolean | null;
  autoStart?: boolean | null;
  skipRedo?: boolean | null;
  method?: string | null;
  rate?: number | null;
  span?: number | null;
  mapPos?: string | null;
  mapW?: number | null;
  mapH?: number | null;
  viewMode?: string | null;
  mapMode?: string | null;
  mapModePerf?: string | null;
  mapColor?: string | null;
  map3dView?: string | null;
  map3dExag?: string | null;
  mapFollow?: string | null;
  mapZoom?: number | null;
  ggTrail?: number | null;
  ggCloud?: boolean | null;
  ltSmooth?: number | null;
  view?: string | null;
  axis?: string | null;
  unitSpeed?: string | null;
  unitDist?: string | null;
  unitElev?: string | null;
  settingsTab?: string | null;
  layouts?: GraphLayouts | null;
  customLanes?: CustomLanes | null;
  syncFloat?: SyncFloatState | null;
}

interface ViewAssists {
  abs?: string | null;
  tcs?: string | null;
  stm?: string | null;
  lc?: string | null;
  shift?: string | null;
}

interface GraphLayouts {
  vis?: GraphLayout | null;
  perf?: GraphLayout | null;
}

interface CustomLanes {
  vis?: GraphLane[] | null;
  perf?: GraphLane[] | null;
}

interface SyncFloatState {
  closed?: boolean | null;
  x?: number | null;
  y?: number | null;
}

interface GraphLayout {
  preset?: string | null;
  lanes?: GraphLane[] | null;
}

interface GraphLane {
  h?: string | null;
  s?: string[] | null;
}

/** ViewMessage のうち、type に合う型に無いプロパティ(never にして、書くと型の検査で落ちる) */
type ExtraKeys<T extends ViewMessage> = Record<Exclude<keyof T, keyof Extract<ViewMessage, { type: T["type"] }>>, never>;

/** C# → 画面(messaging.js の onHostMessage で受ける) */
type HostMessage =
  | HostLoading
  | HostError
  | HostSession
  | HostNotice
  | HostToast
  | HostRaceNamesForm
  | HostFfmpegStatus
  | HostJob
  | HostPrefs
  | HostRestore
  | HostCrashNotice
  | HostReplayFound
  | HostVideoMissing
  | HostVideoSelected
  | HostSessionList
  | HostFriction
  | HostRaceSelected;

interface HostLoading {
  type: "loading";
  name: string;
}

interface HostError {
  type: "error";
  message: string;
}

interface HostSession {
  type: "session";
  keepVideo: boolean;
  name: string;
  label: string;
  comment: string | null;
  package: boolean;
  startedAt: string;
  complete: boolean;
  videoUrl: string | null;
  videos: VideoChoice[];
  activeVideo: string | null;
  alt: AltVideoInfo | null;
  canAddVideo: boolean;
  hasRecording: boolean;
  videoError: string | null;
  method: string;
  mapError: string | null;
  sync: SyncInfo;
  rows: number;
  fps: number | null;
  series: SessionSeries | null;
  marks: SessionMarks | null;
  measure: MeasureData;
  race: number | null;
}

interface HostNotice {
  type: "notice";
  message: string | null;
  error: string | null;
}

interface HostToast {
  type: "toast";
  message: string | null;
  error: string | null;
  sec: number | null;
}

interface HostRaceNamesForm {
  type: "raceNamesForm";
  race: number;
  editable: boolean;
  reason: string | null;
  course: RaceNamesCourse;
  car: RaceNamesCar | null;
}

interface HostFfmpegStatus {
  type: "ffmpegStatus";
  checking: boolean;
  configured: string | null;
  path: string | null;
  version: string | null;
  encoders: FfmpegEncoderChoice[] | null;
  encoder: string | null;
  selected: string | null;
  exportMode: string | null;
}

interface HostJob {
  type: "job";
  text: string | null;
  progress: number;
  cancellable: boolean;
}

interface HostPrefs {
  type: "prefs";
  rememberWindow: boolean;
  language: string;
  replayLeadFrames: number;
  replayLeadDefault: number;
}

interface HostRestore {
  type: "restore";
  sec: number;
  play: boolean;
  quiet: boolean;
  race: number | null;
}

interface HostCrashNotice {
  type: "crashNotice";
  startedAt: string;
  logDir: string;
  dump: string | null;
}

interface HostReplayFound {
  type: "replayFound";
  results: ReplayFoundItem[];
  selected: number | null;
  assigned: ReplayAssignedItem[];
}

interface HostVideoMissing {
  type: "videoMissing";
  id: string | null;
  changed: boolean;
  active: string | null;
}

interface HostVideoSelected {
  type: "videoSelected";
  active: string | null;
}

interface HostSessionList {
  type: "sessionList";
  root: string | null;
  archive: boolean;
  items: SessionListItem[];
  result: SessionListResult | null;
}

interface HostFriction {
  type: "friction";
  race: number | null;
  sec: number | null;
  gg: FrictionView;
  load: FrictionView;
  baseWheels: number[] | null;
  loadLong: (number | null)[];
  loadLat: (number | null)[];
  grip: (number | null)[];
}

interface HostRaceSelected {
  type: "raceSelected";
  race: number | null;
  cause: string;
}

interface VideoChoice {
  id: string;
  kind: string;
  content: string;
  race: number | null;
  label: string;
  status: string;
}

interface AltVideoInfo {
  id: string;
  kind: string;
  content: string;
  race: number | null;
  method: string;
  confidence: string | null;
  corr: number | null;
  lag: number | null;
  zero: number | null;
  end: number | null;
  shiftMs: number | null;
  duration: number | null;
  points: number;
  vfr: boolean;
}

interface SyncInfo {
  samplesTotal: number;
  samplesUsed: number;
  isFallback: boolean;
  manualSync: number;
  manualShiftSec: number;
  endCheckSec: number | null;
  clockRate: number | null;
  fileDurationSec: number | null;
}

interface SessionSeries {
  t: number[];
  speed: (number | null)[];
  accel: (number | null)[];
  brake: (number | null)[];
  steer: (number | null)[];
  gear: (number | null)[];
  slipF: (number | null)[];
  slipR: (number | null)[];
  balance: (number | null)[];
  counter: (number | null)[];
  grade: (number | null)[];
  elev: (number | null)[];
  x: (number | null)[];
  z: (number | null)[];
  turn: (number | null)[];
  gLong: (number | null)[];
  gLat: (number | null)[];
  susp: SuspensionSeries;
  rpm: (number | null)[];
  optRpm: (number | null)[];
  optRpmHi: (number | null)[];
  gain: (number | null)[];
  full: (number | null)[];
  perfCar: (number | null)[];
  perf: PerfData;
  readout: PerfReadoutSeries;
  surface: SurfaceSeries;
  course: CourseSeries;
  friction: FrictionSeries;
}

interface SessionMarks {
  starts: StartMark[];
  contacts: ContactMark[];
  excursions: ExcursionMark[];
  races: RaceMark[];
  skips: SkipMark[];
  laps: LapMark[];
  shifts: ShiftMark[];
  jumps: JumpMark[];
  elevRange: number[] | null;
  cut: number[];
  ggExcluded: number[];
}

interface MeasureData {
  cars: MeasureCar[];
  races: MeasureRace[];
}

interface RaceNamesCourse {
  current: string | null;
  matchId: string | null;
  list: CourseChoice[];
}

interface RaceNamesCar {
  ordinal: number;
  sig: string;
  current: string | null;
  registered: string | null;
  setup: string | null;
  setups: SetupChoice[];
}

interface FfmpegEncoderChoice {
  id: string;
  name: string;
}

interface ReplayFoundItem {
  race: number;
  found: boolean;
  zero: number | null;
  end: number | null;
  duration: number;
  confidence: string | null;
  corr: number | null;
  lag: number | null;
  warning: string | null;
}

interface ReplayAssignedItem {
  race: number;
  id: string;
  manual: boolean;
  zero: number;
  shiftMs: number;
  shiftFrames: number | null;
}

interface SessionListItem {
  path: string;
  name: string;
  stamp: string;
  rest: string;
  started: string | null;
  complete: boolean;
  error: string | null;
  duration: number | null;
  courses: string[];
  cars: SessionCar[];
  races: number;
  video: boolean;
  bytes: number;
  blocked: string | null;
  open: boolean;
}

interface SessionListResult {
  kind: string;
  moved: number | null;
  errors: MoveError[] | null;
  renamed: string | null;
  error: string | null;
}

interface FrictionView {
  x: (number | null)[];
  y: (number | null)[];
  rings: FrictionRing[];
  scaleMax: number;
  cloud: number[];
}

interface SuspensionSeries {
  fl: (number | null)[];
  fr: (number | null)[];
  rl: (number | null)[];
  rr: (number | null)[];
}

interface PerfData {
  cars: PerfCarInfo[];
}

interface PerfReadoutSeries {
  hp: (number | null)[];
  rpmAfter: (number | null)[];
  hpAfter: (number | null)[];
  state: (string | null)[];
}

interface SurfaceSeries {
  fl: (number | null)[];
  fr: (number | null)[];
  rl: (number | null)[];
  rr: (number | null)[];
}

interface CourseSeries {
  surface: (number | null)[];
  breaks: number[];
}

interface FrictionSeries {
  group: number[];
  wheels: WheelBarsSeries;
}

interface StartMark {
  t: number;
  race: boolean;
  kind: string;
}

interface ContactMark {
  t: number;
  landing: boolean;
  g: number;
  before: number;
  after: number;
}

interface ExcursionMark {
  t0: number;
  t1: number;
  wheels: string[];
  surfaces: number[];
  slipT: number | null;
  slipEndT: number | null;
  slipBandT1: number;
  slipPeak: number;
  gap: boolean;
}

interface RaceMark {
  t0: number;
  t1: number;
  tf: number;
  restart: boolean;
  finished: boolean;
  finishedBy: string;
  endKmh: number;
  course: string | null;
  car: string | null;
  setup: string | null;
  dist: RaceDistanceSeries;
  elevRange: number[] | null;
  auto: boolean | null;
  autoLo: number | null;
  autoHi: number | null;
}

interface SkipMark {
  t0: number;
  t1: number;
  kind: string;
}

interface LapMark {
  t: number;
  lap: number;
  prev: number | null;
  finish: boolean;
  byPosition: boolean;
}

interface ShiftMark {
  t: number;
  from: number;
  rpm: number;
  gain: number | null;
  verdict: string | null;
  limiterSec: number;
  auto: boolean;
}

interface JumpMark {
  t0: number;
  t1: number;
  sec: number;
  kmh: number;
  drop: number;
}

interface MeasureCar {
  key: string;
  name: string | null;
  pi: number;
  drivetrain: number;
  cylinders: number;
  parts: number;
  recordings: number;
  seconds: number;
  curve: number[][] | null;
  maxHp: number | null;
  peakRpm: number | null;
  limiter: number | null;
  ratios: Record<string, number>;
  gears: number[];
  top: MeasureTop | null;
  coverage: Record<string, Record<string, number>>;
  usedFrom: number | null;
  bands: MeasureBand[];
  groups: Record<string, MeasureGroup>;
}

interface MeasureRace {
  car: string | null;
  assists: Assists | null;
  assistKey: string;
}

interface CourseChoice {
  id: string;
  name: string;
  kind: string | null;
}

interface SetupChoice {
  id: string;
  name: string;
}

interface SessionCar {
  ordinal: number;
  name: string | null;
}

interface MoveError {
  name: string;
  reason: string;
}

interface FrictionRing {
  group: number;
  r: number;
  n: number;
}

interface PerfCarInfo {
  ordinal: number;
  pi: number;
  curve: number[][] | null;
  maxHp: number | null;
  source: PowerSource | null;
  limiter: number | null;
  gears: number[];
  range: Record<string, number[]>;
  ratios: Record<string, number>;
  opt: Record<string, number | null>;
}

interface WheelBarsSeries {
  fl: WheelBar;
  fr: WheelBar;
  rl: WheelBar;
  rr: WheelBar;
  state: (number | null)[];
}

interface RaceDistanceSeries {
  i: number[];
  d: number[];
  k: number[];
  graph: number[];
}

interface MeasureTop {
  gear: number;
  kmh: number;
  rpm: number;
}

interface MeasureBand {
  from: number;
  to: number;
  rows: number;
  state: string;
}

interface MeasureGroup {
  parts: number;
  limits: Record<string, MeasureLimits>;
  advice: MeasureAdvice[];
}

interface Assists {
  abs: string | null;
  tcs: string | null;
  stm: string | null;
  line: string | null;
  shift: string | null;
  lc: string | null;
}

interface PowerSource {
  recordings: number;
  races: number;
  other: boolean;
}

interface WheelBar {
  avg: (number | null)[];
  hi: (number | null)[];
}

interface MeasureLimits {
  brake: MeasureLimit;
  brakeBySpeed: Record<string, MeasureLimit>;
  left: MeasureLimit;
  right: MeasureLimit;
  launch: MeasureLimit;
}

interface MeasureAdvice {
  kind: string;
  from: number | null;
  to: number | null;
  gear: number | null;
  kmh: number | null;
  surface: string | null;
  side: string | null;
}

interface MeasureLimit {
  g: number | null;
  rows: number;
  measured: boolean;
}

/** 画面の言語の辞書(window.HEISO_I18N。型は Fh6.Core の Strings.cs) */
interface I18nData {
  lang: string;
  strings: Record<string, string>;
}

/** 画面の設定(window.HEISO_SETTINGS) */
interface PageSettings {
  saved: boolean;
  settings: ViewSettings;
  autoImperial: boolean;
}
