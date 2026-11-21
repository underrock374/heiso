// 画面の設定(どの記録にも共通)。値は C# の PlayerSettings(player.json の View。型と既定の値は Fh6.Player.App の ViewSettings)にあり、
// ページの読み込みより前に window.HEISO_SETTINGS として届く。変えたら saveSettings で丸ごと C# へ送る(次に読み込むページにも渡る)。
// グラフの段の組み合わせ(layouts・customLanes)と、同期の微調整の小窓(syncFloat)もここに入れる。
"use strict";

/** @type {Record<string, any>} */
const settings = loadSettings();

function loadSettings() {
  const boot = window.HEISO_SETTINGS || { saved: true, settings: {} };
  const s = { ...boot.settings };
  // まだ C# に保存が無ければ(この版を初めて開いたとき)、前の版の値を一度だけ引き継いで保存する
  if (!boot.saved && importOldSettings(s)) sendToHost({ type: "saveSettings", settings: s });
  return s;
}

/** C# に最後に送った(届いた)設定。同じなら送らない(画面を作るときに、変わっていない値で何度も呼ばれるため) */
let savedSettingsJson = JSON.stringify(settings);

/** 設定を C# に保存する(丸ごと送る。前に送った中身と同じなら送らない) */
function saveSettings() {
  const json = JSON.stringify(settings);
  if (json === savedSettingsJson) return;
  savedSettingsJson = json;
  sendToHost({ type: "saveSettings", settings });
}

/**
 * 前の版が WebView2 の保存領域(localStorage)に置いていた設定を、s に重ねる。何か引き継いだら true。
 * 型が既定の値と違うものは捨てる(C# が読めずに、保存が全部止まらないように)。この関数は、前の版から上げた人がいなくなったら消す
 */
function importOldSettings(s) {
  const get = (key) => { try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; } };
  let found = false;
  const old = get("heiso.playerSettings.v1");
  if (old) {
    for (const [k, v] of Object.entries(old)) if (k in s && typeof v === typeof s[k]) { s[k] = v; found = true; }
  } else {
    // もっと前の版で個別に覚えていたもの
    if (get("heiso.autoStart") === 1) { s.autoStart = true; found = true; }
    if (get("heiso.skipRedo") === 1) { s.skipRedo = true; found = true; }
  }
  // グラフの段: { h: 高さ, s: [系列の名前] } の並び(中の名前が今もあるかは graphs.js の validLanes が見る)
  const lanesOk = (l) => Array.isArray(l) && l.every((x) => x && typeof x.h === "string" && Array.isArray(x.s) && x.s.every((n) => typeof n === "string"));
  const layouts = { vis: get("heiso.graphLayout.v1"), perf: get("heiso.graphLayout.perf.v1") };
  const custom = { vis: get("heiso.graphCustom.v1"), perf: get("heiso.graphCustom.perf.v1") };
  for (const mode of ["vis", "perf"]) {
    const l = layouts[mode];
    if (l && lanesOk(l.lanes) && (l.preset == null || typeof l.preset === "string")) {
      (s.layouts ||= {})[mode] = { preset: l.preset ?? null, lanes: l.lanes };
      found = true;
    }
    if (lanesOk(custom[mode])) { (s.customLanes ||= {})[mode] = custom[mode]; found = true; }
  }
  const f = get("heiso.syncFloat.v1");
  if (f && typeof f === "object") {
    const num = (v) => (typeof v === "number" ? v : null);
    s.syncFloat = { closed: f.closed === true, x: num(f.x), y: num(f.y) };
    found = true;
  }
  return found;
}
