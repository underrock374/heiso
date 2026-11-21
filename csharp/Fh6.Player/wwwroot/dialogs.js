// 再生アプリの画面: 設定のダイアログと、表示のモード(見える化 / 性能 / 計測)。最後に読み込むので、ここで画面の準備ができたことを C# に知らせる。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。app.js が先)。
"use strict";

// ---- 設定のダイアログ ----

Graphs.showContacts = settings.contacts;
Graphs.showLaunches = settings.launches;
$("method").value = settings.method;

const CHECKS = { sContacts: "contacts", sLaunches: "launches", sAutoStart: "autoStart", sSkipRedo: "skipRedo" };
for (const [id, key] of Object.entries(CHECKS)) {
  $(id).onchange = (e) => {
    settings[key] = e.target.checked;
    saveSettings();
    if (key === "skipRedo") setSkip(settings.skipRedo);
    Graphs.showContacts = settings.contacts;
    Graphs.showLaunches = settings.launches;
    Graphs.redrawMarks();
    renderRaceBar();   // 「前 / 次」で止まる所が変わる
  };
}

// 記録を開いたときの表示(範囲・横軸)。次に開く記録から使う
for (const [name, key] of [["sView", "view"], ["sAxis", "axis"]]) {
  for (const r of document.querySelectorAll(`input[name=${name}]`)) {
    r.onchange = () => { if (r.checked) { settings[key] = r.value; saveSettings(); } };
  }
}

// 単位(速度・距離・標高)。変えたら、グラフ・読み取り値・コース図・レースの一覧をすぐ直す
for (const [name, key] of [["sUnitSpeed", "unitSpeed"], ["sUnitDist", "unitDist"], ["sUnitElev", "unitElev"]]) {
  for (const r of document.querySelectorAll(`input[name=${name}]`)) {
    r.onchange = () => {
      if (!r.checked) return;
      settings[key] = r.value;
      saveSettings();
      applyUnits();
    };
  }
}
function applyUnits() {
  const oldDist = Units.dist;
  Units.use(settings);
  buildMapZoomButtons();
  if (Units.dist !== oldDist && settings.mapZoom) {
    // 範囲は、いちばん近い長さのボタンに合わせる
    const near = Units.mapZooms().reduce((a, b) => (Math.abs(b.m - settings.mapZoom) < Math.abs(a.m - settings.mapZoom) ? b : a));
    setMapZoom(near.m);
  } else {
    setMapZoom(settings.mapZoom || 0);
  }
  if (tdata) {
    const range = Graphs.xRange();
    Graphs.build();
    if (range) Graphs.setRange(range.min, range.max);
    fillRaces(races);
    updatePointer(pointerSec);
  }
}

// コース図の位置と大きさ
for (const r of document.querySelectorAll("input[name=sMapPos]")) {
  r.onchange = () => { if (r.checked) { settings.mapPos = r.value; saveSettings(); placeMap(); } };
}
for (const [id, key] of [["sMapW", "mapW"], ["sMapH", "mapH"]]) {
  $(id).onchange = (e) => {
    const v = Math.round(Number(e.target.value) || 0);
    settings[key] = v > 0 ? Math.max(MAP_MIN_PX, v) : 0;
    e.target.value = settings[key];
    saveSettings();
    sizeMap();
  };
}
$("sMapAuto").onclick = () => {
  settings.mapW = settings.mapH = 0;
  saveSettings();
  $("sMapW").value = $("sMapH").value = 0;
  sizeMap();
};

// ウィンドウの大きさと位置を覚えるか(C# 側の player.json に持つ)
let rememberWindow = false;
$("sRememberWindow").onchange = (e) => {
  rememberWindow = e.target.checked;
  sendToHost({ type: "rememberWindow", on: rememberWindow });
};

$("settingsBtn").onclick = () => {
  for (const [id, key] of Object.entries(CHECKS)) $(id).checked = settings[key];
  for (const r of document.querySelectorAll("input[name=sMapPos]")) r.checked = r.value === settings.mapPos;
  $("sMapW").value = settings.mapW;
  $("sMapH").value = settings.mapH;
  $("sRememberWindow").checked = rememberWindow;
  for (const r of document.querySelectorAll("input[name=sView]")) r.checked = r.value === settings.view;
  for (const r of document.querySelectorAll("input[name=sAxis]")) r.checked = r.value === settings.axis;
  for (const [name, key] of [["sUnitSpeed", "unitSpeed"], ["sUnitDist", "unitDist"], ["sUnitElev", "unitElev"]]) {
    for (const r of document.querySelectorAll(`input[name=${name}]`)) r.checked = r.value === settings[key];
  }
  $("method").value = settings.method;
  showSettingsTab(settings.settingsTab);
  sendToHost({ type: "probeFfmpeg" });   // 「書き出し」タブの ffmpeg の状態
  $("settingsDlg").showModal();
  renderSettings();
};

/** 設定のダイアログのタブ(グラフ / 画面 / 再生 / 同期)。選んだタブの section だけを出し、最後に開いたタブを覚える */
function showSettingsTab(tab) {
  const tabs = [...document.querySelectorAll("#settingsTabs button")];
  if (!tabs.some((b) => b.dataset.tab === tab)) tab = tabs[0].dataset.tab;
  for (const b of tabs) b.classList.toggle("on", b.dataset.tab === tab);
  for (const s of document.querySelectorAll("#settingsDlg section[data-tab]")) s.hidden = s.dataset.tab !== tab;
  if (settings.settingsTab !== tab) {
    settings.settingsTab = tab;
    saveSettings();
  }
}
for (const b of document.querySelectorAll("#settingsTabs button")) b.onclick = () => showSettingsTab(b.dataset.tab);

// 画面の言語(C# が保存し、辞書を渡し直して画面を読み直す)
for (const r of document.querySelectorAll("input[name=sLang]")) {
  r.onchange = () => {
    if (!r.checked) return;
    // 読み直した後に同じ位置へ戻すため、今の位置を先に知らせる(ふだんは 2 秒ごとなので、直前に動かした位置が伝わっていないことがある)
    sendToHost({ type: "position", sec: video.src ? video.currentTime : pointerSec, playing: false });
    sendToHost({ type: "setLanguage", language: r.value });
  };
}
$("settingsClose").onclick = () => $("settingsDlg").close();
// 枠の外(暗い所)をクリックしても閉じる
$("settingsDlg").addEventListener("click", (e) => {
  if (e.target !== $("settingsDlg")) return;   // 中の部品のクリック(外の暗い所をクリックすると、ダイアログそのものに届く)
  const r = $("settingsDlg").getBoundingClientRect();
  if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) $("settingsDlg").close();
});

// ---- 表示のモード(見える化 / 性能) ----
// グラフの段の組み合わせと、コース図の場所に出すもの(2D / 3D / 摩擦円)は、モードごとに最後に使ったものを覚える。
// 性能モードでは、右の欄にパワーカーブと、読み取り値に回転数・変速の得失を足す(.perfOnly)
function setViewMode(mode) {
  settings.viewMode = mode;
  saveSettings();
  document.body.classList.toggle("perf", mode === "perf");
  document.body.classList.toggle("measure", mode === "measure");
  for (const b of document.querySelectorAll("[data-viewmode]")) b.classList.toggle("on", b.dataset.viewmode === mode);
  // 計測モードは、グラフの場所にプロファイルの欄を出す(グラフの段の組み合わせは見える化・性能のまま)
  $("measurePanel").hidden = mode !== "measure";
  $("charts").hidden = mode === "measure";
  if (mode === "measure") renderMeasure();
  else Graphs.setMode(mode);
  setMapMode(curMapMode());
  updatePointer(pointerSec);
}

/**
 * 摩擦円に描く、この車の限界(計測モードの車のプロファイル)。計測モードの欄と同じ車と補助の設定の組(選んでいるレースのもの。全体なら最初の車)の、
 * 路面ごとの { left, right, brake, launch }(測れたものだけ。測れていなければ null)。プロファイルが無ければ null
 */
function measureLimits(raceIndex) {
  const d = Measure.data;
  if (!d || !d.cars || !d.cars.length || !d.races) return null;
  const race = raceIndex != null ? d.races[raceIndex] : null;
  const car = d.cars.find((c) => race && c.key === race.car) || d.cars[0];
  const group = (race && car.groups[race.assistKey]) || car.groups["abs=?,tcs=?,stm=?,lc=?"] || Object.values(car.groups)[0];
  if (!group || !group.limits) return null;
  const v = (l) => (l && l.measured ? l.g : null);
  const out = {};
  for (const [s, l] of Object.entries(group.limits)) out[s] = { left: v(l.left), right: v(l.right), brake: v(l.brake), launch: v(l.launch) };
  return out;
}

/** 計測モードの欄を描く(選んでいるレースの車と、補助の設定の組) */
function renderMeasure() {
  if (settings.viewMode === "measure") Measure.render($("measurePanel"), selectedRaceIndex());
}
for (const b of document.querySelectorAll("[data-viewmode]")) b.onclick = () => { setViewMode(b.dataset.viewmode); b.blur(); };
PowerChart.init($("pw"));
setViewMode(["perf", "measure"].includes(settings.viewMode) ? settings.viewMode : "vis");

// ---- 画面の準備ができた ----
// 大きさの変化の見張りは、全部のファイルを読み込んでから始める(読み込みの合間に描画が入ると、まだ読み込んでいないファイルの関数を呼んでしまうため)
new ResizeObserver(() => { Graphs.resize(); updatePointer(pointerSec); }).observe($("charts"));
new ResizeObserver(() => sizeMap()).observe($("charts"));
new ResizeObserver(() => sizeMap()).observe(document.querySelector("main"));   // 窓の高さが変われば、見えている高さも変わる

sendToHost({ type: "ready", method: settings.method });
