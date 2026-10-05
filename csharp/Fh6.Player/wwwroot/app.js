// HEISO Player の画面。データの読み込みと時刻の変換は C#(Fh6.Core)が行い、
// ここには横軸を「動画の秒」に直した系列が届く。ポインターの位置は動画の再生位置そのもの。
// グラフの段の組み合わせと描画は graphs.js、コース図は coursemap.js。
// 横軸を「距離」にしたときは、選んだレースの距離(C# の RaceDistance)で系列と印を並べ直してグラフに渡す。
"use strict";

const host = window.chrome.webview;
const $ = (id) => document.getElementById(id);

// ---- ログ(落ちたときの原因を探るため。C# が %LOCALAPPDATA%\HEISO\Player\logs に書く) ----

/** 操作や出来事を C# のログに書く */
function logEvent(message, level = "info") {
  try { host.postMessage({ type: "log", level, message: String(message).slice(0, 2000) }); } catch { }
}
window.addEventListener("error", (e) => {
  logEvent(`JS のエラー: ${e.message} (${e.filename}:${e.lineno}:${e.colno})${e.error && e.error.stack ? "\n" + e.error.stack : ""}`, "error");
});
window.addEventListener("unhandledrejection", (e) => {
  const r = e.reason;
  logEvent(`JS の拾われなかった Promise のエラー: ${r && r.stack ? r.stack : r}`, "error");
});

// ---- 設定(どの記録にも共通。この PC のこのアプリの中に保存する) ----
// グラフの段の組み合わせは graphs.js が別に保存している

const SETTINGS_KEY = "heiso.playerSettings.v1";
const DEFAULT_SETTINGS = {
  contacts: true,        // 接触の候補の印
  launches: false,       // レース以外の発進も印にする
  autoStart: false,      // 開いたら最初のレースのスタート 3 秒前から再生
  skipRedo: false,       // やり直し区間を自動で飛ばす
  method: "StartedEvent", // 同期の方式
  rate: 1,               // 速さ(最後に使ったもの)
  span: 20,              // 表示幅(秒。最後に使ったもの)
  mapPos: "side",        // コース図の位置: "side"(右の欄)/ "graph"(グラフの横。右下)
  mapW: 0,               // グラフの横のときのコース図の幅(px。0 = グラフの高さに合わせた正方形)
  mapH: 0,               // 同じく高さ(px)
  mapMode: "2d",         // コース図: "2d"(真上から)/ "3d"(立体)
  mapColor: "lap",       // コース図の色: "lap"(今の周を青)/ "surface"(路面の色)
  map3dView: "tilt",     // 3D の視点: "top"(真上)/ "tilt"(斜め)/ "chase"(後ろから)
  map3dExag: "auto",     // 3D の高さの強調: "auto" / "1" / "3" / "5"
  mapFollow: "live",     // 拡大中の追従: "live"(いつも今の位置)/ "step"(直線で区切った区間ごと)
  mapZoom: 0,            // コース図の範囲(一辺の m。0 = 全体。最後に使ったもの)
  view: "all",           // 記録を開いたときの範囲: "all"(全体)/ "race"(レース別。1 レース目)
  axis: "time",          // 記録を開いたときの横軸: "time" / "dist"(距離なら 1 レース目を選ぶ)
  unitSpeed: "auto",     // 速度の単位: "auto"(Windows の地域で決める)/ "kmh" / "mph"
  unitDist: "auto",      // 距離の単位: "auto" / "m"(m・km)/ "ft"(ft・mi)
  unitElev: "auto",      // 標高の単位: "auto" / "m" / "ft"
  settingsTab: "graph",  // 設定のダイアログで最後に開いていたタブ: "graph" / "screen" / "play" / "sync"
};

function loadSettings() {
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(SETTINGS_KEY) || "null"); } catch { }
  const s = { ...DEFAULT_SETTINGS, ...(saved || {}) };
  if (!saved) {
    // 前の版で個別に覚えていたものを引き継ぐ
    try {
      if (localStorage.getItem("heiso.autoStart") === "1") s.autoStart = true;
      if (localStorage.getItem("heiso.skipRedo") === "1") s.skipRedo = true;
    } catch { }
  }
  return s;
}
const settings = loadSettings();
function saveSettings() {
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings)); } catch { /* 保存できなくても表示は続ける */ }
}

const video = $("video");
let span = settings.span; // 表示幅(秒)。0 = 全体
let raceRange = null;   // 「レース」で選んでいる間の表示範囲(再生中も送らない)
let pointerSec = 0;

const START_LEAD_SEC = 3;   // スタートの何秒前へ飛ぶか
const RESPAWN_LEAD_SEC = 1; // チェックポイント逃しの復帰は 1 秒前へ
const LAP_LEAD_SEC = 1;     // 周のスタートとゴールは 1 秒前へ(線を越える瞬間が見えるように)
const leadOf = (s) => (s.kind === "respawn" ? RESPAWN_LEAD_SEC : START_LEAD_SEC);
let frameSec = 1 / 30;      // 1 コマの長さ(録画ファイルのフレームレートから。分からなければ表示したフレームの間隔から)
let fps = null;             // 録画ファイルのフレームレート(C# が session.json かファイルから読む)
let tdata = null;           // 横軸が時間の系列(C# から届いたまま)。読み取り値とコース図はこれを使う
let tmarks = null;          // 横軸が時間の印
let axis = "time";          // グラフの横軸: "time" / "dist"
let skipOn = false;         // やり直し区間を飛ばすか(上の欄のスイッチ。この記録の間だけ。開いたときは設定の値)
let cutRows = null;         // 系列の位置 → 1 ならやり直し区間の行(コース図の色分け)
let surfaceColors = null;   // 系列の位置 → 路面の色(コース図の路面の色のモード。coursemap.js の buildSurfaceColors)
let view3d = false;         // 3D のコース図を準備できたか(WebGL2 が使えないときは 2D のまま)
let distMap = null;         // 横軸が距離のとき: { all, kept, pos, d }(下の buildDistMap)
const frameGaps = [];
const METHOD_NAMES = { SyncSamples: t("録画中の対応表"), StartedEvent: t("録画開始イベント") };

// ---- C# とのやりとり ----

host.addEventListener("message", (e) => {
  const m = e.data;
  if (m.type === "loading") show("loading");
  else if (m.type === "error") { $("errorText").textContent = m.message; show("error"); }
  else if (m.type === "session") onSession(m);
  else if (m.type === "notice") onNotice(m);
  else if (m.type === "toast") toast(m.error || m.message, !!m.error, m.sec);
  else if (m.type === "raceNamesForm") openRaceNames(m);
  else if (m.type === "ffmpegStatus") onFfmpegStatus(m);
  else if (m.type === "job") onJob(m);
  else if (m.type === "prefs") {
    rememberWindow = !!m.rememberWindow;
    for (const r of document.querySelectorAll("input[name=sLang]")) r.checked = r.value === (m.language || "auto");
  }
  else if (m.type === "restore") onRestore(m);
  else if (m.type === "crashNotice") onCrashNotice(m);
});

$("open").onclick = () => host.postMessage({ type: "open" });
$("openPackage").onclick = () => host.postMessage({ type: "openPackage" });

// 書き出す: 選んでいるレースを配布パッケージに(保存先は C# で選ぶ)
let isPackage = false;
$("exportBtn").onclick = () => {
  const r = selectedRace();
  if (!r) { toast(t("書き出すレースを、上の「レース」で選んでください"), true); return; }
  $("exRace").textContent = raceLabel(r, races.indexOf(r));
  $("exportDlg").showModal();
  host.postMessage({ type: "probeFfmpeg" });   // ffmpeg が使えるか(C# が覚えているので、2 回目からはすぐ)
};
$("exCancel").onclick = () => $("exportDlg").close();
$("exGo").onclick = () => {
  const r = selectedRace();
  const mode = document.querySelector("input[name=exMode]:checked")?.value || "copy";
  $("exportDlg").close();
  if (!r) return;
  logEvent(`書き出す: ${races.indexOf(r) + 1} レース目(${mode === "720" ? "720p" : "元の画質"})`);
  host.postMessage({ type: "export", race: races.indexOf(r), mode });
};

// ---- ffmpeg(720p の書き出し)の状態。書き出しの画面と、設定の「書き出し」タブに出す ----

let ffmpegStatus = null;

function onFfmpegStatus(m) {
  if (m.checking) {
    $("exFfmpeg").textContent = t("ffmpeg を確かめています…");
    $("sFfmpegStatus").textContent = t("ffmpeg を確かめています…(エンコーダを試すので数秒かかります)");
    return;
  }
  ffmpegStatus = m;
  const ok = !!(m.path && m.selected);
  const name = (id) => (m.encoders.find((e) => e.id === id) || {}).name || id;
  const text = !m.path ? t("ffmpeg が見つかりません。設定の「書き出し」で場所を指定してください")
    : !m.version ? t("ffmpeg を動かせません: {path}", { path: m.path })
    : !m.selected ? t("使える H.264 のエンコーダがありません(ffmpeg {version})", { version: m.version })
    : t("ffmpeg {version} ・ {encoder} で作ります", { version: m.version, encoder: name(m.selected) });
  $("exFfmpeg").textContent = text;
  const r720 = document.querySelector("input[name=exMode][value='720']");
  r720.disabled = !ok;
  const want = ok && m.exportMode === "720" ? "720" : "copy";
  for (const r of document.querySelectorAll("input[name=exMode]")) r.checked = r.value === want;
  // 設定の「書き出し」タブ
  if (document.activeElement !== $("sFfmpeg")) $("sFfmpeg").value = m.configured || "";
  $("sFfmpegStatus").textContent = m.path
    ? (() => {
      const p = { path: m.path, version: m.version || t("動かせない"), encoders: m.encoders.map((e) => e.name).join(t("、")) || t("なし") };
      return m.configured ? t("指定した ffmpeg: {path}({version})。使えるエンコーダ: {encoders}", p) : t("見つけた ffmpeg: {path}({version})。使えるエンコーダ: {encoders}", p);
    })()
    : t("ffmpeg が見つかりません");
  const sel = $("sEncoder");
  sel.innerHTML = `<option value="auto">${escapeHtml(t("自動({encoder})", { encoder: m.encoders.length ? name(m.encoders[0].id) : t("使えるものなし") }))}</option>`
    + m.encoders.map((e) => `<option value="${e.id}">${escapeHtml(e.name)}</option>`).join("");
  sel.value = m.encoders.some((e) => e.id === m.encoder) ? m.encoder : "auto";
}

$("sFfmpeg").onchange = () => host.postMessage({ type: "setFfmpegPath", path: $("sFfmpeg").value });
$("sFfmpegBrowse").onclick = () => host.postMessage({ type: "browseFfmpeg" });
$("sFfmpegCheck").onclick = () => host.postMessage({ type: "probeFfmpeg", force: true });
$("sEncoder").onchange = () => host.postMessage({ type: "setExportEncoder", encoder: $("sEncoder").value });

// ---- 長くかかる処理(720p の書き出し)の進み具合 ----

function onJob(m) {
  $("jobBar").hidden = !m.text;
  if (!m.text) return;
  $("jobText").textContent = m.text;
  $("jobProg").value = m.progress || 0;
  $("jobPct").textContent = `${Math.round((m.progress || 0) * 100)}%`;
  $("jobCancel").hidden = !m.cancellable;
}
$("jobCancel").onclick = () => host.postMessage({ type: "cancelJob" });
$("method").onchange = (e) => {
  settings.method = e.target.value;
  saveSettings();
  host.postMessage({ type: "method", method: e.target.value });
};

/** 画面(WebView2 の描画のプロセス)が落ちて読み直した後: 元の動画の位置へ */
function onRestore(m) {
  pendingStart = null;
  const go = () => {
    seek(m.sec);
    followTo(m.sec, true);
    if (m.play) video.play().catch(() => { /* 再生が止められても、位置は合っている */ });
  };
  if (video.readyState >= 1) go(); else video.addEventListener("loadedmetadata", go, { once: true });
  if (!m.quiet) toast(t("画面が止まったので読み直しました(元の位置に戻しました)"), true);   // 言語を変えて読み直したときは知らせない
}

/** 前回、正常に終わらなかった: ログとクラッシュダンプの場所を知らせる */
function onCrashNotice(m) {
  $("crashText").textContent = t("前回({at} に起動)は正常に終了しませんでした。ログ: {dir}", { at: m.startedAt, dir: m.logDir })
    + (m.dump ? t(" ・ ") + t("クラッシュダンプ: {file}", { file: m.dump }) : "");
  $("crashNotice").hidden = false;
}
$("openLogs").onclick = () => host.postMessage({ type: "openLogs" });
$("crashClose").onclick = () => { $("crashNotice").hidden = true; host.postMessage({ type: "crashNoticeClosed" }); };

// 動画の位置と再生中かを約 2 秒ごとに C# へ(画面が落ちたときに、読み直してそこへ戻し、再生していたら続けるため)
let lastReported = "";
setInterval(() => {
  const sec = video.src ? video.currentTime : pointerSec;
  const playing = !!video.src && !video.paused;
  const key = `${sec.toFixed(2)} ${playing}`;
  if (key === lastReported) return;
  lastReported = key;
  host.postMessage({ type: "position", sec, playing });
}, 2000);

function show(id) {
  for (const s of ["empty", "loading", "error", "player"]) $(s).hidden = s !== id;
}

function onSession(m) {
  if (!m.keepVideo) logEvent(`開いた記録を表示: ${m.name}(${m.rows} 行、動画 ${m.videoUrl ? "あり" : "なし"})`);
  // フォルダ名にコメントが付いていれば名前に含まれる。記録時のメモが同じなら重ねて出さない
  $("name").textContent = (m.label && m.label !== m.comment ? `${m.name}  ${m.label}` : m.name) + (m.package ? t("(パッケージ)") : "");
  isPackage = !!m.package;
  $("exportBtn").hidden = isPackage;   // パッケージからは書き出さない
  $("method").value = m.method;
  show("player");

  if (!m.keepVideo) {
    video.removeAttribute("src");
    if (m.videoUrl) video.src = m.videoUrl;
    video.load();
  }
  $("videoError").hidden = !m.videoError;
  $("videoError").textContent = m.videoError || "";

  showSync(m);

  if (!m.series) {
    tdata = null;
    tmarks = { starts: [], contacts: [] };
    CourseMap.setRows(null, null);
    Graphs.setData(null, null);
    Graphs.destroy();
    showMarks();
    $("videoError").hidden = false;
    $("videoError").textContent = m.mapError || t("動画との対応が取れないため、グラフを出せません");
    return;
  }
  const keepRange = m.keepVideo ? Graphs.xRange() : null;
  tdata = m.series;
  tmarks = m.marks;
  fps = m.fps || null;
  if (fps) frameSec = 1 / fps;
  $("fpsText").textContent = fps ? `${+fps.toFixed(2)} fps` : "";
  fillRaces(m.marks.races || [], m.keepVideo);
  cutRows = buildCutRows();
  surfaceColors = buildSurfaceColors(tdata);
  if (!m.keepVideo) {
    setSkip(settings.skipRedo);
    // 新しく開いたときは、設定の「記録を開いたときの表示」で。レース別なら 1 レース目(距離でも 1 レース目を選ぶ)
    axis = settings.axis;
    if (settings.view === "race" && races.length) {
      $("race").value = "0";
      setRaceRange(races[0]);
    }
  }
  renderAxis(keepRange);
  showMarks();

  if (keepRange) {
    updatePointer(video.currentTime || 0);
    return;
  }
  // 開いたときは動画の先頭。スタートなどへは「次 ▶」で移る。
  // 「自動でスタート3秒前にシーク」がオンなら、最初のレースのスタートの 3 秒前から再生する
  const first = (m.marks ? m.marks.starts : []).find((s) => s.kind === "race");
  const target = settings.autoStart && first ? Math.max(0, first.t - leadOf(first)) : 0;
  followTo(target, true);
  updatePointer(target);
  pendingStart = video.src && target > 0 ? target : null;
}

let pendingStart = null;   // 動画の準備ができたらここへ飛んで再生する(自動でスタート3秒前)
video.addEventListener("loadedmetadata", () => {
  // 再生速度は動画を読み直すと 1 倍に戻るので、選んでいる値をかけ直す
  const on = document.querySelector("#rates button.on");
  if (on) video.playbackRate = Number(on.dataset.rate);
  if (pendingStart != null) {
    video.currentTime = Math.min(pendingStart, video.duration || pendingStart);
    pendingStart = null;
    video.play().catch(() => { /* 再生が止められても、位置は合っている */ });
  }
});

function showSync(m) {
  const s = m.sync;
  $("sMethod").textContent = METHOD_NAMES[m.method] || m.method;
  $("sSamples").textContent = s.samplesUsed
    ? t("{used} / {total} 点を使用", { used: s.samplesUsed, total: s.samplesTotal }) + (s.isFallback ? t("(録画開始イベントが無いため代用。約1秒ずれる)") : "")
    : (s.isFallback ? t("使える点なし(動画0秒の時刻で代用)") : t("使わない({total} 点あり)", { total: s.samplesTotal }));
  $("sSamples").classList.toggle("warn", s.isFallback);
  $("sEnd").textContent = s.endCheckSec == null ? t("未計算") : t("{sec} 秒", { sec: (s.endCheckSec >= 0 ? "+" : "") + s.endCheckSec.toFixed(3) });
  $("sEnd").classList.toggle("warn", s.endCheckSec != null && Math.abs(s.endCheckSec) > 0.1);
  $("sRate").textContent = s.clockRate == null ? "-" : s.clockRate.toFixed(5);
  $("sManual").textContent = s.manualSync ? t("{n} 点({shift} 秒)", { n: s.manualSync, shift: (s.manualShiftSec >= 0 ? "+" : "") + s.manualShiftSec.toFixed(3) }) : t("なし");
}

function showMarks() {
  const mk = tmarks || Graphs.marks;
  const races = mk.starts.filter((s) => s.race).length;
  $("mStarts").textContent = t("レース {races} / 発進 {launches}", { races, launches: mk.starts.length - races });
  const landings = mk.contacts.filter((c) => c.landing).length;
  $("mContacts").textContent = t("{n} 件", { n: mk.contacts.length - landings });
  $("mExcursions").textContent = t("{n} 件", { n: (mk.excursions || []).length });
  const laps = mk.laps || [];
  const done = laps.filter((l) => l.prev != null);
  const best = done.length ? Math.min(...done.map((l) => l.prev)) : null;
  $("mLaps").textContent = laps.length ? t("{n} 回", { n: laps.length }) + (best != null ? t("(最速 {time})", { time: fmtLap(best) }) : "") : t("なし");
  $("mJumps").textContent = t("{jumps} 回 / 着地の衝撃 {landings} 回", { jumps: (mk.jumps || []).length, landings });
  const skips = mk.skips || [];
  const sum = (list) => fmtTime(list.reduce((a, s) => a + s.t1 - s.t0, 0));
  const redo = skips.filter((s) => s.kind !== "pause"), pause = skips.filter((s) => s.kind === "pause");
  $("mSkips").textContent = skips.length
    ? [redo.length ? t("やり直し {n} 件({time})", { n: redo.length, time: sum(redo) }) : "", pause.length ? t("一時停止 {n} 件({time})", { n: pause.length, time: sum(pause) }) : ""].filter(Boolean).join(" / ")
    : t("なし");
}

// ---- 時刻の表示 ----

function fmtTime(sec) {
  if (sec == null || !isFinite(sec)) return "-";
  const sign = sec < 0 ? "-" : "";
  sec = Math.abs(sec);
  const m = Math.floor(sec / 60);
  const s = sec - m * 60;
  return `${sign}${m}:${s.toFixed(2).padStart(5, "0")}`;
}

// ---- 表示範囲 ----

/** 再生位置が表示範囲から外れそうなら、表示範囲を送る */
function followTo(sec, force) {
  const d = Graphs.data;
  if (!Graphs.charts.length || !d || !d.t.length) return;
  if (axis === "dist") {
    // 距離: 表示幅(秒)を時間軸と同じ拡大率の距離に直した幅(下の distSpanWidth)。「全体」ならレース全体。
    // ポインターが右の 8 割を越えるか表示範囲から外れたら、幅を保ったまま送る
    const x = xOf(sec), r = Graphs.xRange(), w = distSpanWidth();
    const first = d.t[0], last = d.t[d.t.length - 1];
    if (w == null) {
      if (force || !r) Graphs.setRange(first, last);
      else if (isFinite(x) && (x < r.min || x > r.max)) {
        const rw = r.max - r.min;
        Graphs.setRange(x - rw * 0.2, x + rw * 0.8);
      }
      return;
    }
    // 距離の無い位置(リワインドの画面などの停止区間)では、走りに戻った所(無ければ手前)の距離に
    let at = isFinite(x) ? x : nearestDist(sec);
    if (!isFinite(at)) at = first;
    if (force || !r || at < r.min || at > r.min + (r.max - r.min) * 0.8) Graphs.setRange(at - w * 0.2, at + w * 0.8);
    return;
  }
  if (raceRange) {
    if (force) Graphs.setRange(raceRange.min, raceRange.max);
    return;
  }
  if (span === 0) {
    if (force) Graphs.setRange(d.t[0], d.t[d.t.length - 1]);
    return;
  }
  const r = Graphs.xRange();
  if (force || !r || sec < r.min || sec > r.min + (r.max - r.min) * 0.8) {
    Graphs.setRange(sec - span * 0.2, sec + span * 0.8);
  }
}

Graphs.onScale = () => updatePointer(pointerSec);
Graphs.onSeek = (x) => seek(Graphs.xToSec(x));
Graphs.onSeekTime = (sec) => seek(sec);

new ResizeObserver(() => { Graphs.resize(); updatePointer(pointerSec); }).observe($("charts"));

// ---- ポインターと読み取り値 ----

function indexAt(sec) {
  // sec 以下で最大の t の位置(二分探索)
  if (!tdata) return -1;
  const t = tdata.t;
  let lo = 0, hi = t.length - 1;
  if (!t.length || sec < t[0]) return -1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (t[mid] <= sec) lo = mid; else hi = mid - 1;
  }
  return lo;
}

function updatePointer(sec) {
  pointerSec = sec;
  const xv = xOf(sec);
  for (const u of Graphs.charts) {
    const x = isFinite(xv) ? u.valToPos(xv, "x") : -1;
    const visible = x >= 0 && x <= u.over.clientWidth;
    u._pointer.style.display = visible ? "block" : "none";
    if (visible) u._pointer.style.left = `${x}px`;
  }
  $("rTime").textContent = axis === "dist" && isFinite(xv) ? `${fmtTime(sec)}${t(" ・ ")}${fmtDist(xv)}` : fmtTime(sec);
  const d = tdata;
  if (!d) return;
  const i = indexAt(sec);
  drawMap(i);
  const v = (col, unit, digits = 0) => (i < 0 || d[col][i] == null ? "-" : `${d[col][i].toFixed(digits)}${unit}`);
  $("rSpeed").textContent = i < 0 ? "-" : Units.fmtSpeed(d.speed[i]);
  $("rGear").textContent = i >= 0 && d.gear[i] == null && d.speed[i] != null ? t("変速中") : v("gear", "");
  $("rPedal").textContent = `${v("accel", "%")} / ${v("brake", "%")}`;
  $("rSteer").textContent = v("steer", "%");
  const turn = i < 0 ? null : d.turn[i];
  $("rTurn").textContent = turn == null ? "-"
    : Math.abs(turn) < 1 ? t("ほぼ直進") : `R ${Units.fmtLen(1000 / Math.abs(turn))} ${turn > 0 ? t("右") : t("左")}`;
  $("rSlip").textContent = `${v("slipF", "", 2)} / ${v("slipR", "", 2)}`;
  $("rSurface").textContent = i < 0 || !d.surface ? "-" : surfaceText(i);
  $("rGrade").textContent = `${i < 0 || d.grade[i] == null ? "-" : (d.grade[i] > 0 ? "+" : "") + d.grade[i].toFixed(1) + "%"} / ${i < 0 ? "-" : Units.fmtElev(d.elev[i])}`;
}

/** 4輪の路面を短く(同じなら1つに) */
function surfaceText(i) {
  const sf = tdata.surface;
  const name = (code) => (code == null ? "-" : SURFACE[code & 7].name + (code & 8 ? t("・縁石") : "") + (code & 16 ? t("・水") : ""));
  const all = ["fl", "fr", "rl", "rr"].map((w) => name(sf[w][i]));
  if (all.every((x) => x === all[0])) return t("4輪 {surface}", { surface: all[0] });
  return ["fl", "fr", "rl", "rr"].map((w, k) => `${WHEEL_NAMES[w]} ${all[k]}`).join("、");
}

// 表示中のフレームの時刻は requestVideoFrameCallback で取る(currentTime はフレーム精度でないため)
let lastMediaTime = null;
function onFrame(now, meta) {
  // 1 コマの長さ: 再生中の表示フレームの間隔の中央値
  if (!fps && !video.paused && lastMediaTime != null) {
    const d = meta.mediaTime - lastMediaTime;
    if (d > 0.005 && d < 0.1) {
      frameGaps.push(d);
      if (frameGaps.length > 60) frameGaps.shift();
      frameSec = [...frameGaps].sort((a, b) => a - b)[frameGaps.length >> 1];
    }
  }
  lastMediaTime = meta.mediaTime;
  if (!video.paused && skipOn && skipRedo(meta.mediaTime)) {
    video.requestVideoFrameCallback(onFrame);
    return;
  }
  followRace(meta.mediaTime);
  updatePointer(meta.mediaTime);
  if (!video.paused) followTo(meta.mediaTime, false);
  video.requestVideoFrameCallback(onFrame);
}
video.requestVideoFrameCallback(onFrame);

// ---- やり直し区間の自動スキップ ----
// 再生中にやり直し区間(C# の RaceDistance.RedoRanges)に入ったら、戻った先で走り直す所へ飛ぶ。
// 一時停止中のシークやコマ送りでは飛ばさない(区間の中を見たいときのため)

/** 飛んだら true */
function skipRedo(sec) {
  const s = ((tmarks && tmarks.skips) || []).find((g) => sec >= g.t0 - 0.02 && sec < g.t1 - 0.05);
  if (!s) return false;
  video.currentTime = s.t1;
  followTo(s.t1, false);
  const skipped = (s.t1 - s.t0).toFixed(1);
  toast(s.kind === "pause" ? t("一時停止(フォトモード・メニュー)を飛ばしました({sec} 秒)", { sec: skipped }) : t("やり直し区間を飛ばしました({sec} 秒)", { sec: skipped }));
  return true;
}


// ---- 操作 ----

function seek(sec) {
  sec = Math.max(0, sec);
  if (!video.src) { followTo(sec, false); updatePointer(sec); return; }
  video.currentTime = sec;
  followTo(sec, false);
}

const currentSec = () => (video.src ? video.currentTime : pointerSec);

/** 今の位置から sec 秒動かす(グラフの表示範囲も合わせる) */
function seekBy(sec) {
  const target = Math.max(0, currentSec() + sec);
  seek(target);
  followTo(target, false);
}

// 動画・グラフの上のホイール: 1 目盛りで 1 秒(Shift で 1 コマ)。手前に回すと進む。Ctrl+ホイールはグラフの拡大・縮小(graphs.js)
function onWheelSeek(e) {
  const dir = Math.sign(e.deltaY || e.deltaX);
  if (!dir) return;
  if (e.shiftKey) frameStep(dir);
  else seekBy(dir);
}
Graphs.onWheelSeek = onWheelSeek;
video.addEventListener("wheel", (e) => {
  e.preventDefault();
  if (!e.ctrlKey) onWheelSeek(e);
}, { passive: false });

// グラフを拡大・縮小したら、その幅を表示幅にする(再生中に送るときの幅)。距離なら、同じ拡大率の秒に直して覚える
Graphs.onZoom = (w) => {
  if (axis === "dist") {
    const k = distPerSec();
    if (!k) return;
    span = w / k;
  } else {
    span = w;
    leaveRace();
  }
  for (const o of document.querySelectorAll("#spans button")) o.classList.remove("on");
};

function togglePlay() {
  if (!video.src) return;
  if (video.paused) video.play(); else video.pause();
}

$("play").onclick = togglePlay;

// Space は動画やボタンにフォーカスがあっても1回だけ効くよう、先に拾って既定の動作を止める
const typingTarget = (e) => e.target.tagName === "SELECT" || e.target.tagName === "INPUT";
document.addEventListener("keydown", (e) => {
  if ($("settingsDlg").open || $("raceNamesDlg").open || $("exportDlg").open) return;   // ダイアログの中では、キーはダイアログの操作に使う
  if (e.code === "Space" && !typingTarget(e)) {
    e.preventDefault();
    e.stopPropagation();
    if (!e.repeat) togglePlay();
  }
  // , / .: 1 コマ戻る・進む
  if ((e.code === "Comma" || e.code === "Period") && !typingTarget(e) && !e.ctrlKey && !e.altKey) {
    e.preventDefault();
    frameStep(e.code === "Period" ? 1 : -1);
  }
  // Ctrl+← / →: 10 秒、Ctrl+Alt+← / →: 30 秒
  if (e.ctrlKey && (e.code === "ArrowLeft" || e.code === "ArrowRight") && !typingTarget(e)) {
    e.preventDefault();
    e.stopPropagation();
    seekBy((e.code === "ArrowRight" ? 1 : -1) * (e.altKey ? 30 : 10));
  }
}, true);
document.addEventListener("keyup", (e) => {
  if (e.code === "Space" && !typingTarget(e)) e.preventDefault();
}, true);

for (const b of document.querySelectorAll("#rates button")) {
  b.classList.toggle("on", Number(b.dataset.rate) === settings.rate);
  b.onclick = () => {
    video.playbackRate = Number(b.dataset.rate);
    for (const o of document.querySelectorAll("#rates button")) o.classList.toggle("on", o === b);
    settings.rate = Number(b.dataset.rate);
    saveSettings();
  };
}

for (const b of document.querySelectorAll("#spans button")) {
  b.onclick = () => {
    span = Number(b.dataset.span);
    settings.span = span;
    saveSettings();
    for (const o of document.querySelectorAll("#spans button")) o.classList.toggle("on", o === b);
    if (axis !== "dist") leaveRace();   // 距離はレースを選んでいる間だけなので、選択は外さない
    followTo(pointerSec, true);
  };
}

// ---- レースの選択 ----
// 記録の中のレース区間(ロガー v3 の判定)。選ぶとスタートの 3 秒前へ移り、グラフをその区間に合わせる

let races = [];

/** 完走・中断を何で決めたか(Fh6.Core の RaceSegment.FinishedBy) */
const FINISHED_BY = {
  lap: t("ゴールで周回数が増えた"),
  results: t("直後のリザルト画面の間、レースの時計が進んだ"),
  menu: t("直後の停止区間で、レースの時計が止まっていた(メニュー)"),
  log_end: t("リザルト画面のまま記録が終わった"),
  speed: t("終えた瞬間の速度と、直後の停止区間の長さから"),
};

function raceLabel(r, i) {
  const state = r.finished ? t("完走らしい") : t("中断らしい");
  const car = r.car ? `${t(" ・ ")}${r.car}${r.setup ? ` ${r.setup}` : ""}` : "";
  return t("{no}. {time} {course}{car}({state})", { no: i + 1, time: fmtTime(r.t0), course: r.course || t("コース不明"), car, state: (r.restart ? t("リスタート・") : "") + state });
}

function fillRaces(list, keep) {
  const sel = $("race");
  const prev = keep ? sel.value : "";
  races = list;
  sel.innerHTML = `<option value="">${list.length ? t("全体") : t("レースなし")}</option>`
    + list.map((r, i) => `<option value="${i}" title="${escapeHtml(t("終えた瞬間 {speed}。{why}", { speed: Units.fmtSpeed(r.endKmh), why: FINISHED_BY[r.finishedBy] || "" }))}">${escapeHtml(raceLabel(r, i))}</option>`).join("");
  sel.disabled = !list.length;
  $("exportBtn").disabled = !list.length;
  sel.value = prev !== "" && prev < list.length ? prev : "";
  if (sel.value === "") raceRange = null;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

function leaveRace() {
  raceRange = null;
  $("race").value = "";
  updateRaceNamesBtn();
}

// 表示幅のボタン: 時間ではレースを選んでいる間はどれも選ばない(レースの範囲を出すため)。距離では今の表示幅を選ぶ
function markSpan() {
  for (const o of document.querySelectorAll("#spans button")) {
    o.classList.toggle("on", (axis === "dist" || !raceRange) && Number(o.dataset.span) === span);
  }
}

/**
 * 距離のときの 1 秒あたりの距離(m/s)= レースの全長 ÷ レースの長さ(秒)。
 * 時間軸で「レースの長さのうち何割を出しているか」を、そのまま距離に当てはめるための比(速度によらず一定)
 */
function distPerSec() {
  const r = selectedRace();
  if (!r || !distMap || distMap.d.length < 2) return null;
  const sec = r.t1 - r.t0;
  const len = distMap.d[distMap.d.length - 1] - distMap.d[0];
  return sec > 0 && len > 0 ? len / sec : null;
}

/** 距離のときの表示幅(m)。表示幅が「全体」なら null(レース全体を出す) */
function distSpanWidth() {
  const k = distPerSec();
  return span > 0 && k ? span * k : null;
}

$("race").onchange = (e) => {
  logEvent(`レースを選んだ: ${e.target.value === "" ? "全体" : Number(e.target.value) + 1}`);
  if (e.target.value === "") {
    raceRange = null;
    // 距離は 1 つのレースについてなので、「全体」にしたら横軸は時間に戻す
    if (axis === "dist") axis = "time";
    renderAxis(null);
    followTo(currentSec(), true);
    return;
  }
  const r = races[Number(e.target.value)];
  const t = setRaceRange(r);
  renderAxis(null);
  seek(t);
  followTo(t, true);
  e.target.blur();   // 選んだ後の ← → や Space を、選択の切り替えではなく再生の操作にする
};

const selectedRace = () => ($("race").value === "" ? null : races[Number($("race").value)]);

/** レースの範囲(スタートの 3 秒前から、終わりの 3 秒後まで)を表示範囲にする。スタートの 3 秒前の時刻を返す */
function setRaceRange(r) {
  const t = Math.max(0, r.t0 - START_LEAD_SEC);
  raceRange = { min: t, max: r.t1 + START_LEAD_SEC };
  return t;
}

/**
 * レースを選んでいるとき、再生位置が別のレースに入ったら、選択とグラフ・コース図をそのレースに切り替える
 * (続けて何本も走った記録で、次のレースが始まっても前のレースのグラフのままにならないように)。
 * 位置が属するレース = スタートの 3 秒前を過ぎた一番新しいレース。レースとレースの間は前のレースのまま
 */
function followRace(sec) {
  const cur = selectedRace();
  if (!cur || races.length < 2) return;
  let owner = null;
  for (const r of races) if (r.t0 - START_LEAD_SEC <= sec) owner = r;
  if (!owner || owner === cur) return;
  const i = races.indexOf(owner);
  $("race").value = String(i);
  setRaceRange(owner);
  renderAxis(null);
  toast(t("{n} レース目に切り替えました", { n: i + 1 }));
}

// ---- レースの名前(コース名・車名・セッティング名) ----
// 記録アプリの「直前のレース」の直し方と同じ。保存は C# で(session.json の race_edited と、登録の受け渡し用のファイル)

function updateRaceNamesBtn() {
  $("raceNamesBtn").disabled = !selectedRace() || isPackage;
}

$("raceNamesBtn").onclick = () => {
  const r = selectedRace();
  if (!r) return;
  host.postMessage({ type: "raceNamesForm", race: races.indexOf(r) });
};

let rnForm = null;   // C# から届いたダイアログの中身

function fillSelect(sel, items, value) {
  sel.innerHTML = items.map(([v, label]) => `<option value="${escapeHtml(v)}">${escapeHtml(label)}</option>`).join("");
  sel.value = value;
}

/** 名前を付けるダイアログを開く(m = C# の raceNamesForm) */
function openRaceNames(m) {
  rnForm = m;
  const r = races[m.race];
  $("rnRace").textContent = r ? raceLabel(r, m.race) : "";
  const c = m.course;
  // コース: 登録済み(スタートの位置で一致したものに印)と「新しく登録」。一致したものが無ければ「新しく登録」を選ぶ
  fillSelect($("rnCourse"),
    c.list.map((x) => [x.id, x.id === c.matchId ? t("{name}(スタートの位置が一致)", { name: x.name }) : x.name]).concat([["new", t("(新しく登録)")]]),
    c.matchId || "new");
  $("rnCourseName").value = c.matchId ? (c.list.find((x) => x.id === c.matchId)?.name || "") : (c.current || "");
  onCourseChoice();
  // 車: 車種の名前と、同じ組のセッティング
  $("rnCarBox").hidden = !m.car;
  if (m.car) {
    $("rnSig").textContent = m.car.sig;
    $("rnCarName").value = m.car.registered || m.car.current || "";
    const cur = m.car.setups.find((x) => x.name === m.car.setup);
    fillSelect($("rnSetup"),
      [["", t("(付けない)")]].concat(m.car.setups.map((x) => [x.id, x.name])).concat([["new", t("(新しく登録)")]]),
      cur ? cur.id : (m.car.setups.length ? m.car.setups[m.car.setups.length - 1].id : ""));
    onSetupChoice();
  }
  $("rnError").hidden = !m.reason;
  $("rnError").textContent = m.reason || "";
  $("rnSave").disabled = !m.editable;
  $("raceNamesDlg").showModal();
}

function onCourseChoice() {
  const v = $("rnCourse").value, c = rnForm.course;
  const isNew = v === "new";
  $("rnKind").hidden = !isNew;
  if (!isNew) $("rnCourseName").value = c.list.find((x) => x.id === v)?.name || "";
  else if (!$("rnCourseName").value) $("rnCourseName").value = c.current || "";
  $("rnCourseNote").textContent = isNew
    ? t("新しいコースとして、このレースのスタートの位置と向きで登録します(次からはスタートの位置で見分けます)")
    : t("名前を書き換えると、登録しているコースの名前そのものが変わります");
}

function onSetupChoice() {
  const v = $("rnSetup").value;
  $("rnSetupName").hidden = v === "";
  if (v !== "new" && v !== "") $("rnSetupName").value = rnForm.car.setups.find((x) => x.id === v)?.name || "";
  else if (v === "new") $("rnSetupName").value = "";
}

$("rnCourse").onchange = onCourseChoice;
$("rnSetup").onchange = onSetupChoice;
$("rnCancel").onclick = () => $("raceNamesDlg").close();
$("rnSave").onclick = () => {
  const courseId = $("rnCourse").value;
  const courseName = $("rnCourseName").value.trim();
  if (courseId === "new" && !courseName) {
    $("rnError").textContent = t("レース名を入れてください");
    $("rnError").hidden = false;
    return;
  }
  host.postMessage({
    type: "editRaceNames", race: rnForm.race,
    courseId, courseName, courseKind: courseId === "new" ? $("rnKind").value : null,
    carName: rnForm.car ? $("rnCarName").value.trim() : null,
    setupId: rnForm.car ? $("rnSetup").value : null,
    setupName: rnForm.car ? $("rnSetupName").value.trim() : null,
  });
  logEvent(`レースの名前を保存: ${rnForm.race + 1} レース目`);
  $("raceNamesDlg").close();
};

// ---- 横軸(時間 / 距離) ----

$("axis").onchange = (e) => {
  logEvent(`横軸: ${e.target.value}`);
  axis = e.target.value;   // この記録の中だけ(開いたときの横軸は設定のダイアログで)
  const had = selectedRace();
  renderAxis(null);
  // 「全体」から距離にして 1 レース目を選んだとき、今の位置がそのレースの外ならスタートの 3 秒前へ
  const r = selectedRace();
  if (!had && r && (currentSec() < r.t0 - START_LEAD_SEC || currentSec() > r.t1)) {
    const t = Math.max(0, r.t0 - START_LEAD_SEC);
    seek(t);
    followTo(t, true);
  }
  e.target.blur();
};

function fmtDist(m) {
  return Units.fmtDist(m);
}

/** 横軸を今の設定(時間 / 距離、選んでいるレース)で作り直す。keepRange があればその範囲を保つ */
function renderAxis(keepRange) {
  if (!tdata) return;
  let r = selectedRace();
  if (axis === "dist" && !r) {
    // 距離は 1 つのレースについて。「全体」のままなら 1 レース目を選ぶ(レースが無ければ時間)
    if (races.length) {
      $("race").value = "0";
      r = races[0];
      setRaceRange(r);
    } else {
      axis = "time";
    }
  }
  $("axis").value = axis;
  $("axis").disabled = !races.length;
  if (axis === "dist") {
    distMap = buildDistMap(r);
    Graphs.setData(distView(), distMarks(tmarks));
    Graphs.fmtX = fmtDist;
    Graphs.xTicks = (min, max, count) => Units.distTicks(min, max, count);
    Graphs.xToSec = distToSec;
  } else {
    distMap = null;
    Graphs.setData(tdata, tmarks);
    Graphs.fmtX = fmtTime;
    Graphs.xTicks = null;
    Graphs.xToSec = (x) => x;
  }
  Graphs.elevRange = elevRange(r ? r.dist.i : null);
  Graphs.build();
  markSpan();
  updateRaceNamesBtn();
  CourseMap.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors);
  if (view3d) CourseView3D.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors);
  if (keepRange) Graphs.setRange(keepRange.min, keepRange.max);
  else followTo(currentSec(), true);
  updatePointer(currentSec());
}

/**
 * 距離の対応表。all: 系列の位置 → 距離(やり直した走りも含む。ポインター用)、kept: 最後まで残った走りだけ(印用)、
 * pos / d: グラフに出す行(残った走りで、距離が前の行より大きいもの)
 */
function buildDistMap(race) {
  const n = tdata.t.length;
  const all = new Float64Array(n).fill(NaN), kept = new Float64Array(n).fill(NaN);
  const pos = [], d = [];
  let last = -Infinity;
  const { i, d: dd, k } = race.dist;
  for (let j = 0; j < i.length; j++) {
    all[i[j]] = dd[j];
    if (!k[j]) continue;
    kept[i[j]] = dd[j];
    if (dd[j] > last) { pos.push(i[j]); d.push(dd[j]); last = dd[j]; }
  }
  return { all, kept, pos, d };
}

/** 横軸を距離にした系列(グラフに出す行だけを抜き出す) */
function distView() {
  const v = { t: distMap.d };
  for (const [key, col] of Object.entries(tdata)) {
    if (key === "t") continue;
    if (key === "surface") {
      v.surface = {};
      for (const [w, c] of Object.entries(col)) v.surface[w] = distMap.pos.map((p) => c[p]);
    } else {
      v[key] = distMap.pos.map((p) => col[p]);
    }
  }
  return v;
}

/** 動画の秒 → 横軸の値(時間ならそのまま、距離ならその行の距離。レースの外は NaN) */
function xOf(sec) {
  if (axis !== "dist" || !distMap) return sec;
  const p = indexAt(sec);
  return p < 0 ? NaN : distMap.all[p];
}

/** 距離 → その距離を通った時刻(最後まで残った走りで) */
function distToSec(x) {
  const d = distMap.d;
  let lo = 0, hi = d.length - 1;
  while (lo < hi) { const m = (lo + hi) >> 1; if (d[m] < x) lo = m + 1; else hi = m; }
  return tdata.t[distMap.pos[lo]];
}

/** その時刻に一番近い、距離のある行の距離(停止区間の中なら、明けて走りに戻った所。無ければ手前) */
function nearestDist(sec) {
  const p = indexAt(sec);
  if (p < 0) return NaN;
  const all = distMap.all;
  for (let q = p; q < all.length; q++) if (isFinite(all[q])) return all[q];
  for (let q = p - 1; q >= 0; q--) if (isFinite(all[q])) return all[q];
  return NaN;
}

/** 最後まで残った走りでの、その時刻の距離。やり直した走りの中なら NaN */
function keptAt(sec) {
  const p = indexAt(sec);
  if (p < 0) return NaN;
  const v = distMap.kept[p];
  return isFinite(v) ? v : (p + 1 < distMap.kept.length ? distMap.kept[p + 1] : NaN);
}

/** 印を距離に直す(元の時刻は src_ に残す)。やり直した走りの中の印は出さない */
function distMarks(mk) {
  const conv = (m, need, opt = []) => {
    const o = { ...m };
    for (const key of need) {
      const v = keptAt(m[key]);
      if (!isFinite(v)) return null;
      o[key] = v;
      o["src_" + key] = m[key];
    }
    for (const key of opt) {
      if (m[key] == null) continue;
      const v = keptAt(m[key]);
      o["src_" + key] = m[key];
      o[key] = isFinite(v) ? v : null;
    }
    return o;
  };
  const list = (arr, need, opt) => (arr || []).map((m) => conv(m, need, opt)).filter(Boolean);
  return {
    ...mk,
    starts: list(mk.starts, ["t"]),
    contacts: list(mk.contacts, ["t"]),
    laps: list(mk.laps, ["t"]),
    jumps: list(mk.jumps, ["t0", "t1"]),
    excursions: list(mk.excursions, ["t0", "t1"], ["slipT", "slipEndT"]),
    skips: [],   // やり直した走りは距離軸に出さない
  };
}

/** 標高の段の上下は、この幅(m)より狭くしない(都内など標高差の小さいコースで、小さな凹凸を大げさに見せない) */
const ELEV_MIN_SPAN_M = 50;

/** 標高の段の上下: コース(rows。null なら記録全体の走行中)の最低〜最高に 5% の余白。50m 未満なら真ん中を中心に 50m */
function elevRange(rows) {
  const e = tdata.elev;
  let lo = Infinity, hi = -Infinity;
  const each = rows || e.keys();
  for (const i of each) {
    const v = e[i];
    if (v == null) continue;
    if (v < lo) lo = v;
    if (v > hi) hi = v;
  }
  if (!isFinite(lo)) return null;
  const span = hi - lo;
  if (span < ELEV_MIN_SPAN_M) {
    const c = (lo + hi) / 2;
    return [Math.floor(c - ELEV_MIN_SPAN_M / 2), Math.ceil(c + ELEV_MIN_SPAN_M / 2)];
  }
  return [Math.floor(lo - span * 0.05), Math.ceil(hi + span * 0.05)];
}

// ---- コース図 ----

CourseMap.init($("map"));
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
// グラフの横のときの大きさは設定の幅・高さ(0 ならグラフの高さに合わせた正方形)。左下の角のドラッグでも変えられる

const MAP_MIN_PX = 120;
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
  }
}

/** グラフの横のときの大きさ。w, h を渡せばその大きさ(ドラッグ中)。渡さなければ設定の値か、グラフの高さに合わせた正方形 */
function sizeMap(w, h) {
  if (settings.mapPos !== "graph") return;
  if (w == null || h == null) {
    // コース図の下の行(2D / 3D、凡例、範囲のボタン)の高さを除いて、グラフの高さに合わせる
    const shown = settings.mapMode === "3d" ? $("map3dBox") : $("map");
    const zoomRow = $("mapBox").offsetHeight - shown.offsetHeight;
    const auto = Math.max(MAP_MIN_PX, $("charts").offsetHeight - zoomRow);
    h = settings.mapH || auto;
    w = settings.mapW || h;
  }
  $("mapDock").style.width = `${w}px`;
  $("map").style.height = `${h}px`;
  $("map3dBox").style.height = `${h}px`;
}

// グラフの段の数や高さが変わったら、合わせる大きさも変わる
new ResizeObserver(() => sizeMap()).observe($("charts"));

// 左下の角のドラッグで大きさを変える(右上が起点なので、左へ引くと幅が、下へ引くと高さが増える)
$("mapGrip").addEventListener("mousedown", (e) => {
  e.preventDefault();
  const x0 = e.clientX, y0 = e.clientY;
  const w0 = $("mapDock").offsetWidth, h0 = $("map").offsetHeight;
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
      if (tdata) CourseView3D.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors);
    }
  }
  settings.mapMode = mode;
  saveSettings();
  $("map").hidden = mode === "3d";
  $("map3dBox").hidden = mode !== "3d";
  $("map3dCtl").hidden = mode !== "3d";
  for (const b of document.querySelectorAll("[data-mapmode]")) b.classList.toggle("on", b.dataset.mapmode === mode);
  sizeMap();
  drawMap(indexAt(pointerSec));
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
  // 凡例(路面の色のとき)
  $("mapLegend").hidden = mode !== "surface";
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
setMapMode(settings.mapMode);
CourseMap.onSeekTime = (sec) => seek(sec);
CourseMap.getNow = () => currentSec();

/** コース図を描く。今いる周を青、グラフに出している範囲(動画の秒)をさらに明るく */
function drawMap(i) {
  const r = Graphs.xRange();
  let t0 = null, t1 = null;
  if (r) {
    if (axis === "dist" && distMap) { t0 = distToSec(r.min); t1 = distToSec(r.max); }
    else { t0 = r.min; t1 = r.max; }
  }
  const lap = lapAt(pointerSec);
  if (settings.mapMode === "3d" && view3d) CourseView3D.draw(i, t0, t1, lap ? lap[0] : null, lap ? lap[1] : null);
  else CourseMap.draw(i, t0, t1, lap ? lap[0] : null, lap ? lap[1] : null);
}

/** やり直し区間の行(全レースの距離の計算で「最後まで残らなかった」行)。系列の位置ごとに 1 */
function buildCutRows() {
  const cut = new Uint8Array(tdata.t.length);
  for (const r of races) {
    const { i, k } = r.dist;
    for (let j = 0; j < i.length; j++) if (!k[j]) cut[i[j]] = 1;
  }
  return cut;
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

// ---- コマ送り ----

/** 1 コマ進む / 戻る(一時停止する)。コマの真ん中の時刻へ(境目に止まって前後のコマが出るのを避ける) */
function frameStep(dir) {
  if (!video.src) return;
  video.pause();
  const f = fps || 1 / frameSec;
  const now = lastMediaTime ?? video.currentTime;
  const k = Math.round(now * f);
  const target = Math.max(0, (k + dir + 0.5) / f);
  video.currentTime = target;
  followTo(target, false);
}
$("prevFrame").onclick = () => frameStep(-1);
$("nextFrame").onclick = () => frameStep(1);

// ---- 同期の手動補正 ----
// 映像のコマの秒(このコマを使う)と、グラフで指した瞬間(今の変換での動画の秒)を C# に送り、
// C# がテレメトリー時刻に直して session.json の video.manual_sync に足す

const ms = { frame: null, graph: null };

function renderManual() {
  $("msFrameText").textContent = ms.frame == null ? "-" : t("映像 {time}({frac})", { time: fmtTime(ms.frame), frac: (ms.frame % 1).toFixed(3).slice(1) });
  $("msPickText").textContent = Graphs.pickMode ? t("グラフをクリックしてください")
    : ms.graph == null ? "-" : t("グラフ {time}({frac})", { time: fmtTime(ms.graph), frac: (ms.graph % 1).toFixed(3).slice(1) });
  $("msPick").classList.toggle("on", Graphs.pickMode);
  $("charts").classList.toggle("picking", Graphs.pickMode);
  const ok = ms.frame != null && ms.graph != null;
  const diff = ok ? ms.frame - ms.graph : null;
  $("msDiff").textContent = !ok ? ""
    : Math.abs(diff) < 0.0005 ? t("ずれはありません")
    : diff > 0 ? t("グラフを {sec} 秒後ろへずらして映像に合わせます(約 {frames} コマ)", { sec: Math.abs(diff).toFixed(3), frames: Math.round(Math.abs(diff) / frameSec) })
      : t("グラフを {sec} 秒前へずらして映像に合わせます(約 {frames} コマ)", { sec: Math.abs(diff).toFixed(3), frames: Math.round(Math.abs(diff) / frameSec) });
  $("msSave").disabled = !ok;
}

$("msFrame").onclick = () => {
  if (!video.src) return;
  video.pause();
  ms.frame = lastMediaTime ?? video.currentTime;
  renderManual();
};
$("msPick").onclick = () => {
  Graphs.pickMode = !Graphs.pickMode;
  renderManual();
};
Graphs.onPick = (sec) => {
  Graphs.pickMode = false;
  ms.graph = sec;
  renderManual();
};
$("msCancel").onclick = () => {
  ms.frame = ms.graph = null;
  Graphs.pickMode = false;
  renderManual();
};
$("msSave").onclick = () => {
  if (ms.frame == null || ms.graph == null) return;
  host.postMessage({ type: "manualSync", graphSec: ms.graph, videoSec: ms.frame, note: $("msNote").value });
};
$("msClear").onclick = () => {
  if (confirm(t("この記録の手動補正を全部消して、自動の同期に戻しますか?"))) host.postMessage({ type: "clearManualSync" });
};

function onNotice(m) {
  toast(m.error || m.message, !!m.error);
  if (!m.error) {
    ms.frame = ms.graph = null;
    $("msNote").value = "";
    renderManual();
  }
}

/** 画面の下に短く知らせる。sec は出しておく秒数(省けばエラー 5 秒、それ以外 2.5 秒) */
function toast(text, isError, sec) {
  const t = $("toast");
  t.textContent = text;
  t.classList.toggle("error", !!isError);
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, sec ? sec * 1000 : isError ? 5000 : 2500);
}

// 前 / 次の印へ飛ぶ:

// 前 / 次の印へ飛ぶ: スタート(3 秒前。チェックポイント逃しの復帰は 1 秒前)と、周のスタート・ゴール(1 秒前)。
// 前に印が無ければ動画の先頭へ
function jumpStart(dir) {
  const list = [
    ...(tmarks ? tmarks.starts : []).filter((s) => s.race || Graphs.showLaunches).map((s) => s.t - leadOf(s)),
    ...((tmarks && tmarks.laps) || []).map((l) => l.t - LAP_LEAD_SEC),
  ].map((t) => Math.max(0, t)).sort((a, b) => a - b);
  const now = currentSec();
  let target = dir > 0
    ? list.find((t) => t > now + 0.05)
    : [...list].reverse().find((t) => t < now - 0.5);
  if (target == null) {
    if (dir > 0) return;
    target = 0;
  }
  seek(target);
  followTo(target, true);
}
$("prevStart").onclick = () => jumpStart(-1);
$("nextStart").onclick = () => jumpStart(1);
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
    fillRaces(races, true);
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
  host.postMessage({ type: "rememberWindow", on: rememberWindow });
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
  host.postMessage({ type: "probeFfmpeg" });   // 「書き出し」タブの ffmpeg の状態
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
    host.postMessage({ type: "position", sec: video.src ? video.currentTime : pointerSec, playing: false });
    host.postMessage({ type: "setLanguage", language: r.value });
  };
}
$("settingsClose").onclick = () => $("settingsDlg").close();
// 枠の外(暗い所)をクリックしても閉じる
$("settingsDlg").addEventListener("click", (e) => {
  if (e.target !== $("settingsDlg")) return;   // 中の部品のクリック(外の暗い所をクリックすると、ダイアログそのものに届く)
  const r = $("settingsDlg").getBoundingClientRect();
  if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) $("settingsDlg").close();
});

host.postMessage({ type: "ready", method: settings.method });
