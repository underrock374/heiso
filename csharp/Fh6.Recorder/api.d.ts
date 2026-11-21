// 自動で作ったファイル。手で直さない。
// 元は csharp/Fh6.Recorder/ApiTypes.cs。C# を変えたら、
// 環境変数 HEISO_UPDATE_GOLDEN=1 を付けて dotnet test csharp/Fh6.Recorder.Tests を回すと書き直す(ApiTypesTests)

/** 画面 → C#(POST の本文) */
interface StartRequest {
  video?: boolean | null;
  label?: string | null;
  quality?: string | null;
}

/** 画面 → C#(POST の本文) */
interface QualityRequest {
  preset?: string | null;
}

/** 画面 → C#(POST の本文) */
interface SceneRequest {
  scene?: string | null;
}

/** 画面 → C#(POST の本文) */
interface MarkerRequest {
  label?: string | null;
}

/** 画面 → C#(POST の本文) */
interface CarRequest {
  id: string;
  car?: string | null;
  setupChoice?: string | null;
  setupName?: string | null;
  note?: string | null;
}

/** 画面 → C#(POST の本文) */
interface CourseRequest {
  id: string;
  choice?: string | null;
  name?: string | null;
  kind?: string | null;
  note?: string | null;
}

/** 画面 → C#(POST の本文) */
interface DismissRequest {
  id: string;
}

/** 画面 → C#(POST の本文) */
interface LastRaceRequest {
  courseChoice?: string | null;
  courseName?: string | null;
  kind?: string | null;
  car?: string | null;
  setupChoice?: string | null;
  setupName?: string | null;
}

/** /api/status の答え(操作の POST の多くも、これを返す) */
interface RecorderStatus {
  state: string;
  warning: string | null;
  telemetry: LiveSnapshot;
  obs: ObsStatus;
  session: SessionStatus | null;
  last: LastSessionInfo | null;
  race: RaceStatus | null;
  quality: QualityStatus;
  scene: SceneStatus | null;
  outputDir: string;
  speedText: string | null;
}

interface LiveSnapshot {
  forward: ForwardInfo | null;
  listening: boolean;
  error: string | null;
  port: number;
  hz: number;
  lastPacketAgoSec: number | null;
  state: string;
  speedKmh: number | null;
  racePos: number;
  raceTime: number | null;
  car: CarInfo | null;
  timestampMs: number | null;
}

interface ObsStatus {
  enabled: boolean;
  connected: boolean;
  recording: boolean;
  version: string | null;
  error: string | null;
}

interface SessionStatus {
  name: string;
  folder: string;
  label: string | null;
  elapsedSec: number;
  rows: number;
  packets: number;
  markers: number;
  videoRequested: boolean;
  videoActive: boolean;
  quality: string | null;
  qualityApplied: boolean;
}

interface LastSessionInfo {
  folder: string;
  name: string;
  durationSec: number;
  rows: number;
  markers: number;
  video: string | null;
  videoError: string | null;
  fileDurationSec: number | null;
  endCheckSec: number | null;
  clockRate: number | null;
  quality: string | null;
  mbPerMin: number | null;
}

interface RaceStatus {
  car: RaceCar | null;
  course: CourseRef | null;
  inRace: boolean;
  prompts: RacePrompt[];
  known: KnownNames;
  kinds: Record<string, string>;
  lastRace: LastRaceStatus | null;
  recent: RecentLine[];
  registryWarning: string | null;
}

interface QualityStatus {
  selected: string;
  presets: QualityPresetStatus[];
}

interface SceneStatus {
  selected: string | null;
  current: string | null;
  scenes: string[] | null;
}

interface ForwardInfo {
  targets: string[];
  packets: number;
  failures: number;
  error: string | null;
}

interface CarInfo {
  ordinal: number;
  class: string;
  pi: number;
  drivetrain: string;
}

interface RaceCar {
  ordinal: number;
  cls: string;
  pi: number;
  drive: string;
  cyl: number;
  name: string | null;
  setup: string | null;
  confirmed: boolean;
}

interface CourseRef {
  id: string;
  name: string;
  kind: string | null;
}

type RacePrompt =
  | CarPromptStatus
  | CoursePromptStatus
  | UnknownPromptStatus;

interface KnownNames {
  cars: string[];
  courses: KnownCourse[];
}

interface LastRaceStatus {
  startedAt: string;
  ended: boolean;
  recorded: string | null;
  course: CourseRef | null;
  car: LastRaceCar | null;
}

interface RecentLine {
  at: string;
  text: string;
}

interface QualityPresetStatus {
  id: string;
  name: string;
  height: number;
  fps: number;
  mbPerMin: number;
  note: string | null;
  profile: string;
  ready: boolean | null;
}

interface CarPromptStatus {
  id: string;
  type: string;
  created: string;
  sig: CarSig;
  changed: string[];
  atStart: boolean;
  knownName: string | null;
  setups: SetupOption[];
  guessSetup: string | null;
}

interface CoursePromptStatus {
  id: string;
  type: string;
  created: string;
  start: StartPoint;
}

interface UnknownPromptStatus {
  id: string;
  type: string;
  created: string;
}

interface KnownCourse {
  id: string;
  name: string;
  kind: string | null;
  seen: number;
}

interface LastRaceCar {
  ordinal: number;
  cls: string;
  pi: number;
  drive: string;
  cyl: number;
  name: string | null;
  setupId: string | null;
  setup: string | null;
  setups: SetupRef[];
}

interface CarSig {
  ordinal: number;
  cls: string;
  pi: number;
  drive: string;
  cyl: number;
}

interface SetupOption {
  id: string;
  name: string;
  variant: number;
}

interface StartPoint {
  x: number;
  z: number;
  yaw: number;
}

interface SetupRef {
  id: string;
  name: string;
}

/** /api/marker の答え */
interface MarkerResult {
  count: number;
}

/** /api/connect-info の答え */
interface ConnectInfo {
  isLocal: boolean;
  urls: ConnectUrl[];
}

interface ConnectUrl {
  address: string;
  name: string;
  url: string;
}

/** うまくいかなかったときの答え(4xx) */
interface ApiError {
  error: string | null;
}

/** 画面の言語の辞書(window.HEISO_I18N。型は Fh6.Core の Strings.cs) */
interface I18nData {
  lang: string;
  strings: Record<string, string>;
}
