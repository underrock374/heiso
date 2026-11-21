// グラフの系列と単位、段の組み合わせのプリセット、保存した段の組み合わせの読み込み。
// 系列は「段」に自由に入れられる。段の中の1つ目の単位は左、2つ目は右に目盛りを付け、3つ目以降は目盛りなしで描く。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。graphseries.js → graphs.js → graphsettings.js)。
"use strict";

// ---- 系列と単位 ----

// 後から回転数や G を足すときは、ここに1行足す(C# の LoadedSession.BuildSeries にも列を足す)
// mark = 印(線ではない)、strip = 帯(路面)
const SERIES = {
  speed:   { label: t("速度"),       unit: "kmh",  stroke: "#4ea1ff" },
  gear:    { label: t("ギア"),       unit: "gear", stroke: "#b8bec7", stepped: true, width: 1 },
  accel:   { label: t("アクセル"),   unit: "pct",  stroke: "#4cd07d" },
  brake:   { label: t("ブレーキ"),   unit: "pct",  stroke: "#ff5d5d" },
  steer:   { label: t("操舵"),       unit: "spct", stroke: "#c792ea" },
  turn:    { label: t("旋回"),       unit: "turn", stroke: "#f5c542", width: 1.2 },
  slipF:   { label: t("前輪スリップ"), unit: "slip", stroke: "#ff9f43" },
  slipR:   { label: t("後輪スリップ"), unit: "slip", stroke: "#54a0ff" },
  // 後輪 − 前輪の滑り角。0 より上(赤)= オーバー、下(青)= アンダー。塗りは 0 を境に色を変える(balance)
  balance: { label: t("オーバー / アンダー"), unit: "bal", stroke: "#ff6b6b", balance: true, width: 1.2 },
  counter: { label: t("カウンター"), mark: true },
  gLong:   { label: t("前後G"),      unit: "g",    stroke: "#2dd4bf" },
  gLat:    { label: t("横G"),        unit: "g",    stroke: "#f472b6" },
  // 荷重移動(サスペンションの縮みの差。FrictionCircle.loadSeries が選んでいるレースの基準で作る)。
  // G と同じ段に重ねて遅れを見るので、向きを G に合わせる(前後は正 = 後ろが沈む = 加速の側、左右は正 = 左が沈む = 右コーナーの側)
  loadLong: { label: t("前後の荷重"), unit: "load", stroke: "#fbbf24", width: 1.6 },
  loadLat:  { label: t("左右の荷重"), unit: "load", stroke: "#fde68a", width: 1.6 },
  // 合成 G ÷ その路面の摩擦円の円(FrictionCircle.gripSeries)。100 = この走りで出した最大(上位 2%)。グリップをどれだけ使えたか
  grip:     { label: t("摩擦円の %"), unit: "grip", stroke: "#c4b5fd", width: 1.3 },
  grade:   { label: t("勾配"),       unit: "grade", stroke: "#a3e635" },
  elev:    { label: t("標高"),       unit: "m",    stroke: "#c3ccd8", width: 1.5 },
  // 性能モード(docs/spec.md「性能モード」)。最適な範囲は今のギアのもの(破線)。
  // 変速の得失 = 今すぐ次のギアへ変速した場合の出力の差(最大出力に対する %)。全開の間だけ
  rpm:     { label: t("回転数"),     unit: "rpm",  stroke: "#4ea1ff" },
  // 最適な範囲(得失が ±3% 以内)の下と上。出力が平らな車では広く、リミッターまで引っ張る方が得なギアではリミッターの手前
  optRpm:  { label: t("最適な範囲(下)"), unit: "rpm", stroke: "#e0a33a", width: 1.2, dash: [6, 4] },
  optRpmHi: { label: t("最適な範囲(上)"), unit: "rpm", stroke: "#e0a33a", width: 1.2, dash: [2, 4] },
  gain:    { label: t("変速の得失"), unit: "gain", stroke: "#c3ccd8", width: 1.3 },
  shift:   { label: t("変速の点"),   mark: true },
  surface: { label: t("路面"),       strip: true },
  contact: { label: t("接触の印"),   mark: true },
  jump:    { label: t("ジャンプ"),   mark: true },
};

const UNITS = {
  kmh:   { get label() { return Units.speedLabel(); } },
  pct:   { label: "%", range: [0, 100] },
  spct:  { label: t("% 正=右"), range: [-100, 100] },
  gear:  { label: t("ギア"), range: (u, min, max) => [0, Math.max(6, max || 0) + 0.5] },
  turn:  { label: t("1000/R 正=右"), range: (u, min, max) => { const a = Math.max(10, Math.abs(min || 0), Math.abs(max || 0)); return [-a, a]; } },
  slip:  { label: t("スリップ 1=限界"), range: (u, min, max) => [0, Math.max(1.5, max || 0)] },
  bal:   { label: t("後輪 − 前輪 正=オーバー"), range: (u, min, max) => { const a = Math.min(4, Math.max(1, Math.abs(min || 0), Math.abs(max || 0))); return [-a, a]; } },
  g:     { label: t("G 前後は正=加速・横は正=右"), range: (u, min, max) => { const a = Math.max(1, Math.abs(min || 0), Math.abs(max || 0)); return [-a, a]; } },
  // 0 を真ん中にそろえる(G の段に重ねたとき、両方の 0 が同じ高さになるように)
  grip:  { label: t("% 100=その路面の円"), range: (u, min, max) => [0, Math.min(150, Math.max(120, max || 0))] },
  load:  { label: t("% 前後は正=後ろが沈む・左右は正=左が沈む"), range: (u, min, max) => { const a = Math.min(100, Math.max(20, Math.abs(min || 0), Math.abs(max || 0))); return [-a, a]; } },
  grade: { label: t("勾配 % 正=上り"), range: (u, min, max) => { const a = Math.max(5, Math.abs(min || 0), Math.abs(max || 0)); return [-a, a]; } },
  // 標高: 上下はコース(選んだレース。全体なら記録)の最低〜最高で固定する(Graphs.elevRange。app.js が決める)
  rpm:   { label: "rpm", range: (u, min, max) => [Math.max(0, Math.floor(((min ?? 0) - 300) / 1000) * 1000), Math.ceil(((max ?? 8000) + 300) / 1000) * 1000] },
  gain:  { label: t("% 正=変速が遅い"), range: (u, min, max) => { const a = Math.min(30, Math.max(10, Math.abs(min || 0), Math.abs(max || 0))); return [-a, a]; } },
  m:     { get label() { return t("標高 {unit}", { unit: Units.elevLabel() }); }, range: (u, min, max) => Graphs.elevRange ? Graphs.elevRange.map((v) => Units.elevOf(v)) : [min, max] },
};

// 路面の帯の色(C# の SurfaceKind の番号。6 = 空中、7 = 縁石)。+8 = 縁石の印(FH6 では立たない)、+16 = 水たまり
const SURFACE = [
  { name: t("舗装"), color: "#5b6270" },
  { name: t("雪など"), color: "#e8f1ff" },        // 0.10。サーキットの縁の外などにも出る(雪以外も含む)
  { name: t("ダート"), color: "#b7793b" },
  { name: t("ジャンプ台など"), color: "#9b59b6" }, // 0.20。街中のコースやグリッドにも出る
  { name: t("芝・荒れ地"), color: "#3fa34d" },
  { name: t("不明"), color: "#2b2f36" },
  { name: t("空中"), color: null },
  { name: t("縁石"), color: "#c9a227" },            // 0.24(サーキットで確認)
];
const WHEEL_NAMES = { fl: t("左前"), fr: t("右前"), rl: t("左後"), rr: t("右後") };

// 段の高さ(描画域の高さ px)
const HEIGHTS = { s: 70, m: 110, l: 150 };
const HEIGHT_NAMES = { s: t("小"), m: t("中"), l: t("大") };

const PRESETS = {
  p4: { name: t("4段(今までの形)"), lanes: [
    { h: "l", s: ["speed", "contact", "jump"] }, { h: "m", s: ["accel", "brake"] }, { h: "m", s: ["steer"] }, { h: "s", s: ["gear"] }] },
  p3: { name: t("3段"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "s", s: ["accel", "brake"] }, { h: "s", s: ["steer"] }] },
  p2: { name: t("2段"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "m", s: ["accel", "brake", "steer"] }] },
  p6: { name: t("詳細"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "s", s: ["accel", "brake"] }, { h: "m", s: ["steer", "turn", "counter"] },
    { h: "s", s: ["slipF", "slipR"] }, { h: "s", s: ["balance"] }, { h: "s", s: ["surface"] }, { h: "s", s: ["elev"] }] },
  // G(力がかかった)と荷重(車が沈んだ)を同じ段に重ね、荷重が遅れて乗る・戻るのを見る
  pLoad: { name: t("荷重移動"), lanes: [
    { h: "m", s: ["speed", "gear", "contact", "jump"] }, { h: "s", s: ["accel", "brake"] }, { h: "m", s: ["gLong", "loadLong"] }, { h: "m", s: ["gLat", "loadLat"] }, { h: "s", s: ["steer", "counter"] }] },
};
// 性能モードの段の組み合わせ(見える化のプリセットとは別に覚える)
const PERF_PRESETS = {
  // ギアは回転数の段に重ねると最適な範囲の破線と紛れるので、細い段に分ける
  perf: { name: t("性能(標準)"), lanes: [
    { h: "l", s: ["rpm", "optRpm", "optRpmHi"] }, { h: "s", s: ["gear"] }, { h: "m", s: ["gain", "shift"] }, { h: "s", s: ["accel", "brake"] }, { h: "s", s: ["slipF", "slipR"] }, { h: "s", s: ["balance", "counter"] }] },
  perfSpeed: { name: t("性能(速度つき)"), lanes: [
    { h: "l", s: ["rpm", "optRpm", "optRpmHi"] }, { h: "s", s: ["gear"] }, { h: "m", s: ["gain", "shift"] }, { h: "s", s: ["speed"] }, { h: "s", s: ["accel", "brake"] }, { h: "s", s: ["slipF", "slipR"] }, { h: "s", s: ["balance", "counter"] }] },
  perfLoad: { name: t("性能(荷重移動)"), lanes: [
    { h: "m", s: ["speed", "gear"] }, { h: "s", s: ["accel", "brake"] }, { h: "m", s: ["gLong", "loadLong"] }, { h: "m", s: ["gLat", "loadLat"] }, { h: "s", s: ["grip"] }, { h: "s", s: ["slipF", "slipR"] }, { h: "s", s: ["balance", "counter"] }] },
};
const DEFAULT_PERF_PRESET = "perf";

// 変速の点の色(早い・適正・遅い。得失を出せないものは灰色)
const SHIFT_COLORS = { early: "#9d8cff", ok: "#3cc68a", late: "#ff7a45", none: "#9aa1ab" };
const SHIFT_NAMES = { early: t("早い"), ok: t("適正"), late: t("遅い") };

// 前の版の「詳細」(最後の段が勾配)。そのまま使っていたら、今の「詳細」(標高)に置き換える
const OLD_P6_LAST = ["grade"];
const DEFAULT_PRESET = "p3";

const MARK_COLORS = { start: "#36cfc9", lap: "#e6e6e6", contact: "#ff8a3d", jump: "#b388ff", cause: "#ff4d4f", skip: "#9aa1ab", counter: "#ffd166" };
// オーバー / アンダーの塗り(0 より上 = オーバー、下 = アンダー)
const BALANCE_COLORS = { over: "#ff6b6b", under: "#54a0ff" };

/** オーバー / アンダーの色: 0 の高さで赤(上)と青(下)を切り替える縦のグラデーション */
function balanceGradient(u, scale, alpha) {
  const { top, height } = u.bbox;
  if (!u.scales[scale] || height <= 0) return BALANCE_COLORS.over;
  const y0 = u.valToPos(0, scale, true);
  const f = Math.min(1, Math.max(0, (y0 - top) / height));
  const a = Math.round(alpha * 255).toString(16).padStart(2, "0");
  const g = u.ctx.createLinearGradient(0, top, 0, top + height);
  g.addColorStop(0, BALANCE_COLORS.over + a); g.addColorStop(f, BALANCE_COLORS.over + a);
  g.addColorStop(f, BALANCE_COLORS.under + a); g.addColorStop(1, BALANCE_COLORS.under + a);
  return g;
}

// ---- 設定の保存(この PC のこのアプリの中だけ) ----

// 見える化と性能で別に覚える
const presetsOf = (mode) => (mode === "perf" ? PERF_PRESETS : PRESETS);

function cloneLanes(lanes) { return lanes.map((l) => ({ h: l.h, s: [...l.s] })); }

function validLanes(lanes) {
  if (!Array.isArray(lanes)) return false;
  const seen = new Set();
  for (const l of lanes) {
    if (!l || !HEIGHTS[l.h] || !Array.isArray(l.s)) return false;
    for (const id of l.s) { if (!SERIES[id] || seen.has(id)) return false; seen.add(id); }
  }
  return true;
}

function loadLayout(mode = "vis") {
  const saved = (settings.layouts || {})[mode];   // 設定に保存した段の組み合わせ(settings.js)
  if (saved && validLanes(saved.lanes)) {
    if (mode === "vis" && saved.preset === "p6" && saved.lanes.length === PRESETS.p6.lanes.length
        && JSON.stringify(saved.lanes[saved.lanes.length - 1].s) === JSON.stringify(OLD_P6_LAST)) {
      return { preset: "p6", lanes: cloneLanes(PRESETS.p6.lanes) };
    }
    return { preset: saved.preset ?? null, lanes: cloneLanes(saved.lanes) };
  }
  const def = mode === "perf" ? DEFAULT_PERF_PRESET : DEFAULT_PRESET;
  return { preset: def, lanes: cloneLanes(presetsOf(mode)[def].lanes) };
}

function customLanes(mode) {
  const c = (settings.customLanes || {})[mode];
  return c && validLanes(c) ? c : null;
}
