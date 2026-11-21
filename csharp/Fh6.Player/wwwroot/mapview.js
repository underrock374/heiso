// 再生アプリの画面: コース図の場所(2D・3D・荷重移動・摩擦円)の切り替え、位置と大きさ、範囲・色・追従・ならしの操作。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。app.js が先)。
"use strict";

// ---- コース図 ----

CourseMap.init($("map"));
FrictionCircle.trailSec = settings.ggTrail;
FrictionCircle.cloud = settings.ggCloud;
FrictionCircle.kind = (settings.viewMode === "perf" ? settings.mapModePerf : settings.mapMode) === "gg" ? "gg" : "load";
FrictionCircle.init($("gg"));
// コース図の範囲: 全体 / 2km / 1km / 500m / 200m 四方(マイルなら 1mi / 0.5mi / 1500ft / 600ft。全体以外は今の位置を真ん中にして追従)
/** 範囲のボタンを今の距離の単位で作り直す(全体のボタンはそのまま) */
function buildMapZoomButtons() {
  const box = $("mapZoom");
  for (const b of box.querySelectorAll("button:not([data-zoom='0'])")) b.remove();
  for (const z of Units.mapZooms()) {
    const b = document.createElement("button");
    b.dataset.zoom = z.m;
    b.textContent = z.label;
    box.appendChild(b);
  }
  for (const b of box.querySelectorAll("button")) b.onclick = () => setMapZoom(Number(b.dataset.zoom));
}
function setMapZoom(m) {
  settings.mapZoom = m;
  saveSettings();
  CourseMap.setZoom(m || null);
  if (view3d) CourseView3D.setZoom(m || null);
  for (const b of document.querySelectorAll("#mapZoom button")) b.classList.toggle("on", Number(b.dataset.zoom) === m);
}
Units.use(settings);
buildMapZoomButtons();
setMapZoom(settings.mapZoom || 0);

// ---- コース図の位置と大きさ ----
// 右の欄(動画の横)か、グラフの横(右下。起点は画面の右端とグラフの最上段)。
// グラフの横のときの大きさは設定の幅・高さ(0 ならグラフの高さに合わせた正方形。見えている高さと幅の MAP_AUTO_MAX_W まで)。左下の角のドラッグでも変えられる

const MAP_MIN_PX = 120;
const MAP_AUTO_MAX_W = 0.35;   // グラフの横のとき、大きさを決めていなければ、グラフとコース図の行の幅のこの割合まで
/** コース図の場所に今出している要素(2D の canvas、3D の箱、摩擦円の canvas) */
/** 今のモードのコース図の場所に出すもの(見える化と性能で別に覚える) */
const mapModeKey = () => (settings.viewMode === "perf" ? "mapModePerf" : "mapMode");
const curMapMode = () => settings[mapModeKey()];
/** 荷重移動と摩擦円は、同じ図(FrictionCircle)の描き分け */
const isGgMode = (mode) => mode === "gg" || mode === "load";
const shownMapEl = () => (curMapMode() === "3d" ? $("map3dBox") : isGgMode(curMapMode()) ? $("gg") : $("map"));
const mapHome = $("mapBox").nextElementSibling;   // 右の欄に戻すときの位置(この要素の前)

function placeMap() {
  const box = $("mapBox"), dock = $("mapDock");
  if (settings.mapPos === "graph") {
    if (box.parentElement !== dock) dock.insertBefore(box, $("mapGrip"));
    dock.hidden = false;
    sizeMap();
  } else {
    if (box.parentElement !== mapHome.parentElement) mapHome.parentElement.insertBefore(box, mapHome);
    dock.hidden = true;
    dock.style.width = "";
    $("map").style.height = "";
    $("map3dBox").style.height = "";
    $("gg").style.height = "";
  }
}

/** グラフの横のときの大きさ。w, h を渡せばその大きさ(ドラッグ中)。渡さなければ設定の値か、グラフの高さに合わせた正方形 */
function sizeMap(w, h) {
  if (settings.mapPos !== "graph") return;
  if (w == null || h == null) {
    // コース図の下の行(2D / 3D、凡例、範囲のボタン)の高さを除いて、グラフの高さに合わせる
    const shown = shownMapEl();
    // 段が多いと大きくなりすぎるので、画面に見えている高さと、グラフとコース図の行の幅の MAP_AUTO_MAX_W までにする
    const zoomRow = $("mapBox").offsetHeight - shown.offsetHeight;
    const rowW = $("mapDock").parentElement.clientWidth;
    const visible = document.querySelector("main").clientHeight;
    const auto = Math.min($("charts").offsetHeight, visible) - zoomRow;
    h = settings.mapH || Math.max(MAP_MIN_PX, Math.min(auto, rowW * MAP_AUTO_MAX_W));
    w = settings.mapW || h;
  }
  $("mapDock").style.width = `${w}px`;
  $("map").style.height = `${h}px`;
  $("map3dBox").style.height = `${h}px`;
  $("gg").style.height = `${h}px`;
}

// グラフの段の数や高さが変わったら、合わせる大きさも変わる
// 大きさの見張りは、全部のファイルを読み込んでから始める(dialogs.js の終わり)

// 左下の角のドラッグで大きさを変える(右上が起点なので、左へ引くと幅が、下へ引くと高さが増える)
$("mapGrip").addEventListener("mousedown", (e) => {
  e.preventDefault();
  const x0 = e.clientX, y0 = e.clientY;
  const w0 = $("mapDock").offsetWidth, h0 = shownMapEl().offsetHeight;
  let w = w0, h = h0;
  const move = (ev) => {
    w = Math.max(MAP_MIN_PX, Math.round(w0 - (ev.clientX - x0)));
    h = Math.max(MAP_MIN_PX, Math.round(h0 + (ev.clientY - y0)));
    sizeMap(w, h);
  };
  const up = () => {
    document.removeEventListener("mousemove", move);
    document.removeEventListener("mouseup", up);
    settings.mapW = w;
    settings.mapH = h;
    saveSettings();
  };
  document.addEventListener("mousemove", move);
  document.addEventListener("mouseup", up);
});

placeMap();

// ---- 2D / 3D と色のモード ----


function setMapMode(mode) {
  if (mode === "3d" && !view3d) {
    try {
      view3d = CourseView3D.init($("map3d"), $("map3dOverlay"));
    } catch (e) {
      logEvent(`3D のコース図を準備できない: ${e.message}`, "error");
      view3d = false;
    }
    if (!view3d) { toast(t("この PC では 3D のコース図を出せません(WebGL2 が使えない)"), true); mode = "2d"; }
    else {
      CourseView3D.onSeekTime = (sec) => seek(sec);
      CourseView3D.getNow = () => currentSec();
      CourseView3D.onViewChange = (v) => markMap3dView(v);
      CourseView3D.view = settings.map3dView;
      CourseView3D.exaggeration = settings.map3dExag;
      CourseView3D.colorMode = settings.mapColor;
      CourseView3D.zoom = settings.mapZoom || null;
      CourseView3D.followMode = settings.mapFollow;
      const r = selectedRace();
      if (tdata) CourseView3D.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors, breakRows);
    }
  }
  settings[mapModeKey()] = mode;
  saveSettings();
  syncMapChrome();
  sizeMap();
  drawMap(indexAt(pointerSec));
}

/** 2D / 3D / 荷重移動 / 摩擦円で、出す部品を切り替える(路面の色・区切り・範囲は、コース図だけの部品なので荷重移動・摩擦円では隠す) */
function syncMapChrome() {
  const mode = curMapMode(), gg = isGgMode(mode);
  if (gg && FrictionCircle.kind !== mode) FrictionCircle.setKind(mode);
  $("map").hidden = mode !== "2d";
  $("map3dBox").hidden = mode !== "3d";
  $("gg").hidden = !gg;
  $("gg").title = mode === "gg"
    ? t("摩擦円。横軸 = 横 G(右が正)、縦軸 = 前後 G(上 = 加速、下 = ブレーキ)。点は車が押されている向き(体が振られる向きの反対。右コーナーで右、ブレーキで下)。円は、選んでいるレースでその路面に出た加速度の上位 2% の大きさ(車の限界そのものではない)。点がオレンジのときは、スリップが限界(1)を超えている。円の外へ大きく飛ぶのは、壁・着地などタイヤ以外の力")
    : t("荷重移動。サスペンションの縮みの差で、点は沈んでいる側へ寄る(ブレーキで上 = 前輪、右コーナーで左 = 外側の左のタイヤ)。横軸 = 右の 2 輪 − 左の 2 輪、縦軸 = 前の 2 輪 − 後ろの 2 輪(縮みきるまでの長さに対する %)。まっすぐ一定の速さで走っているときを 0 にする。G より遅れて動くので、ブレーキの抜き方や姿勢の揺れが見える。荷重の量そのものではなく目安(ばね・段差でも変わる)。円は、選んでいるレースでその路面に出た差の上位 2%。点がオレンジのときは、スリップが限界(1)を超えている。円のまわりの 4 本の棒は 4 輪それぞれのサスペンション(タイヤの並び。沈むほど短くなる。白の線 = まっすぐ一定の速さのとき、赤 = 浮きかけ、橙 = 縮み切りに近い)");
  $("map3dCtl").hidden = mode !== "3d";
  $("ggCtl").hidden = !gg;
  $("mapSurface").hidden = gg;
  $("mapStep").hidden = gg;
  $("mapZoom").hidden = gg;
  $("mapLegend").hidden = gg || settings.mapColor !== "surface";
  for (const b of document.querySelectorAll("[data-mapmode]")) b.classList.toggle("on", b.dataset.mapmode === mode);
}

function markMap3dView(v) {
  for (const b of document.querySelectorAll("[data-view]")) b.classList.toggle("on", b.dataset.view === v);
}

function setMapColor(mode) {
  settings.mapColor = mode;
  saveSettings();
  $("mapSurface").classList.toggle("on", mode === "surface");
  CourseMap.setColors(surfaceColors, mode);
  if (view3d) CourseView3D.setColors(surfaceColors, mode);
  // 凡例(路面の色のとき。摩擦円を出している間は隠す)
  syncMapChrome();
  $("mapLegend").innerHTML = [...COURSE_SURFACE.filter(Boolean), COURSE_MIXED]
    .map((k) => `<i style="background:${rgbCss(k.rgb)}"></i>${k.name}`).join("");
  drawMap(indexAt(pointerSec));
}

for (const b of document.querySelectorAll("[data-mapmode]")) b.onclick = () => setMapMode(b.dataset.mapmode);

// 拡大中の追従のしかた: いつも今の位置 / 区間ごとに区切る
function setMapFollow(mode) {
  settings.mapFollow = mode;
  saveSettings();
  $("mapStep").classList.toggle("on", mode === "step");
  CourseMap.setFollowMode(mode);
  if (view3d) CourseView3D.setFollowMode(mode);
}
$("mapStep").onclick = () => setMapFollow(settings.mapFollow === "step" ? "live" : "step");
setMapFollow(settings.mapFollow);
$("mapSurface").onclick = () => setMapColor(settings.mapColor === "surface" ? "lap" : "surface");
for (const b of document.querySelectorAll("[data-view]")) {
  b.onclick = () => {
    settings.map3dView = b.dataset.view;
    saveSettings();
    markMap3dView(b.dataset.view);
    if (view3d) CourseView3D.setView(b.dataset.view);
  };
}
$("mapExag").value = settings.map3dExag;
$("mapExag").onchange = (e) => {
  settings.map3dExag = e.target.value;
  saveSettings();
  if (view3d) CourseView3D.setExaggeration(e.target.value === "auto" ? "auto" : Number(e.target.value));
  e.target.blur();
};
markMap3dView(settings.map3dView);
setMapColor(settings.mapColor);
setMapMode(curMapMode());
CourseMap.onSeekTime = (sec) => seek(sec);
CourseMap.getNow = () => currentSec();

// 摩擦円: 軌跡の長さと点群
$("ggTrail").value = String(settings.ggTrail);
if (!$("ggTrail").value) $("ggTrail").value = "3";
$("ggTrail").onchange = (e) => {
  settings.ggTrail = Number(e.target.value);
  saveSettings();
  FrictionCircle.setTrail(settings.ggTrail);
  e.target.blur();
};
$("ggCloud").classList.toggle("on", settings.ggCloud);
$("ggCloud").onclick = () => {
  settings.ggCloud = !settings.ggCloud;
  saveSettings();
  $("ggCloud").classList.toggle("on", settings.ggCloud);
  FrictionCircle.setCloud(settings.ggCloud);
};
// 荷重移動・摩擦円: ならす幅。グラフの前後・左右の荷重と摩擦円の % も同じ値なので、見ている範囲のまま作り直す
$("ltSmooth").value = String(settings.ltSmooth);
if (!$("ltSmooth").value) $("ltSmooth").value = "0.25";
$("ltSmooth").onchange = (e) => {
  settings.ltSmooth = Number(e.target.value);
  saveSettings();
  // C# が設定の保存を受けて作り直し、送ってくる(届いたら onFriction が、見ている範囲のまま描き直す)
  e.target.blur();
};

/** コース図を描く。今いる周を青、グラフに出している範囲(動画の秒)をさらに明るく */
function drawMap(i) {
  const r = Graphs.xRange();
  let t0 = null, t1 = null;
  if (r) {
    if (axis === "dist" && distMap) { t0 = distToSec(r.min); t1 = distToSec(r.max); }
    else { t0 = r.min; t1 = r.max; }
  }
  if (settings.viewMode === "perf") PowerChart.draw(i);
  if (isGgMode(curMapMode())) { FrictionCircle.draw(i); return; }
  const lap = lapAt(pointerSec);
  if (curMapMode() === "3d" && view3d) CourseView3D.draw(i, t0, t1, lap ? lap[0] : null, lap ? lap[1] : null);
  else CourseMap.draw(i, t0, t1, lap ? lap[0] : null, lap ? lap[1] : null);
}

/** 行の印: 系列の位置ごとに、rows(C# が送った位置の一覧)に入っていれば 1。やり直し区間の行(marks.cut)に使う */
function rowFlags(n, rows) {
  const flags = new Uint8Array(n);
  for (const i of rows) flags[i] = 1;
  return flags;
}

/** 今いる周の [始まり, 終わり](動画の秒)。レースの中なら、周のスタートの印で区切る(周の無いレースはレース全体)。レースの外は null */
function lapAt(sec) {
  const r = races.find((x) => sec >= x.t0 - START_LEAD_SEC && sec <= x.t1 + START_LEAD_SEC);
  if (!r) return null;
  const cuts = ((tmarks && tmarks.laps) || []).map((l) => l.t).filter((t) => t > r.t0 && t < r.t1).sort((a, b) => a - b);
  let a = r.t0, b = r.t1;
  for (const t of cuts) {
    if (t <= sec) a = t; else { b = t; break; }
  }
  return [a, b];
}

// 上の欄の「スキップ ON / OFF」: 押すたびに切り替える。この記録を開いている間だけ(設定には保存しない)
function setSkip(on) {
  skipOn = !!on;
  $("skipNow").textContent = skipOn ? t("スキップ ON") : t("スキップ OFF");
  $("skipNow").classList.toggle("on", skipOn);
}
$("skipNow").onclick = (e) => { setSkip(!skipOn); logEvent(`スキップ: ${skipOn ? "ON" : "OFF"}`); e.target.blur(); };
