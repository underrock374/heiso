// HEISO Player の画面。データの読み込みと時刻の変換は C#(Fh6.Core)が行い、
// ここには横軸を「動画の秒」に直した系列が届く。ポインターの位置は動画の再生位置そのもの。
// グラフの段の組み合わせと描画は graphs.js、コース図は coursemap.js。
// 画面の部分ごとに races.js・axis.js・mapview.js・videos.js・dialogs.js に分けてある(この順に読み込む)。
// 横軸を「距離」にしたときは、選んだレースの距離(C# の RaceDistance)で系列と印を並べ直してグラフに渡す。
"use strict";

/** @type {(id: string) => any} */
const $ = (id) => document.getElementById(id);

// ---- ログ(落ちたときの原因を探るため。C# が %LOCALAPPDATA%\HEISO\Player\logs に書く) ----

/** 操作や出来事を C# のログに書く */
function logEvent(message, level = "info") {
  try { sendToHost({ type: "log", level, message: String(message).slice(0, 2000) }); } catch { }
}
window.addEventListener("error", (e) => {
  logEvent(`JS のエラー: ${e.message} (${e.filename}:${e.lineno}:${e.colno})${e.error && e.error.stack ? "\n" + e.error.stack : ""}`, "error");
});
window.addEventListener("unhandledrejection", (e) => {
  const r = e.reason;
  logEvent(`JS の拾われなかった Promise のエラー: ${r && r.stack ? r.stack : r}`, "error");
});

// 設定(settings・saveSettings)は settings.js

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
let friction = null;        // C# が作った摩擦円・荷重移動(HostFriction)。選んでいるレース・ならしの幅のもの
let raceSel = null;         // 選んでいるレースの番号(null なら「全体」)。C# の SelectionViewModel が持ち、HostSession・HostRaceSelected で届く
let raceAsked = false;      // レースの選択を C# に送って、答えを待っている(再生中に同じ切り替えを何度も送らない)
let axis = "time";          // グラフの横軸: "time" / "dist"
let skipOn = false;         // やり直し区間を飛ばすか(上の欄のスイッチ。この記録の間だけ。開いたときは設定の値)
let cutRows = null;         // 系列の位置 → 1 ならやり直し区間の行(コース図の色分け)
let surfaceColors = null;   // 系列の位置 → 路面の色(コース図の路面の色のモード。分類は C# から届き、色は coursemap.js の courseSurfaceRgb)
let breakRows = null;       // 系列の位置 → 1 なら、その行でコース図の線を切る(位置が飛んだ行)
let view3d = false;         // 3D のコース図を準備できたか(WebGL2 が使えないときは 2D のまま)
let distMap = null;         // 横軸が距離のとき: { all, kept, pos, d }(下の buildDistMap)
const frameGaps = [];
const METHOD_NAMES = { SyncSamples: t("録画中の対応表"), StartedEvent: t("録画開始イベント") };

// ---- C# とのやりとり ----

onHostMessage((m) => {
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
    replayLeadDefault = m.replayLeadDefault ?? 5;
    if (document.activeElement !== $("sReplayLead")) $("sReplayLead").value = m.replayLeadFrames ?? replayLeadDefault;
    $("sReplayLeadNote").textContent = t("既定は {n} コマ(30fps の動画で 167 ms。実際のリプレイで合わせた量)", { n: replayLeadDefault });
  }
  else if (m.type === "restore") onRestore(m);
  else if (m.type === "crashNotice") onCrashNotice(m);
  else if (m.type === "replayFound") onReplayFound(m);
  else if (m.type === "videoMissing") onVideoMissing(m);
  else if (m.type === "videoSelected") $("videoSel").value = m.active || "";
  else if (m.type === "sessionList") Sessions.onList(m);   // sessions.js
  else if (m.type === "friction") onFriction(m);
  else if (m.type === "raceSelected") onRaceSelected(m);
});

$("openPackage").onclick = () => sendToHost({ type: "openPackage" });

// 書き出す: 選んでいるレースを配布パッケージに(保存先は C# で選ぶ)
let isPackage = false;
$("exportBtn").onclick = () => {
  const r = selectedRace();
  if (!r) { toast(t("書き出すレースを、上の「レース」で選んでください"), true); return; }
  $("exRace").textContent = raceLabel(r, races.indexOf(r));
  // 割り当てた動画を表示していれば、その動画を書き出す
  const vopt = altInfo ? [...$("videoSel").options].find((o) => o.value === altInfo.id) : null;
  $("exVideo").hidden = !altInfo;
  $("exVideo").textContent = altInfo
    ? t("書き出す動画: {label}({content})", { label: vopt ? vopt.text : "", content: altInfo.content === "replay" ? t("リプレイ。最後まで残った走りだけ、スキップ区間なし") : t("ライブ走行。リワインドも含み、スキップ区間あり") })
    : "";
  $("exNoAudio").checked = false;
  $("exportDlg").showModal();
  sendToHost({ type: "probeFfmpeg" });   // ffmpeg が使えるか(C# が覚えているので、2 回目からはすぐ)
};
$("exCancel").onclick = () => $("exportDlg").close();
$("exGo").onclick = () => {
  const r = selectedRace();
  const mode = document.querySelector("input[name=exMode]:checked")?.value || "copy";
  $("exportDlg").close();
  if (!r) return;
  logEvent(`書き出す: ${races.indexOf(r) + 1} レース目(${mode === "720" ? "720p" : "元の画質"})`);
  sendToHost({ type: "export", race: races.indexOf(r), mode, noAudio: $("exNoAudio").checked });
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

$("sFfmpeg").onchange = () => sendToHost({ type: "setFfmpegPath", path: $("sFfmpeg").value });
$("sFfmpegBrowse").onclick = () => sendToHost({ type: "browseFfmpeg" });
$("sFfmpegCheck").onclick = () => sendToHost({ type: "probeFfmpeg", force: true });
$("sEncoder").onchange = () => sendToHost({ type: "setExportEncoder", encoder: $("sEncoder").value });

// ---- 長くかかる処理(720p の書き出し)の進み具合 ----

function onJob(m) {
  $("jobBar").hidden = !m.text;
  if (!m.text) return;
  $("jobText").textContent = m.text;
  $("jobProg").value = m.progress || 0;
  $("jobPct").textContent = `${Math.round((m.progress || 0) * 100)}%`;
  $("jobCancel").hidden = !m.cancellable;
}
$("jobCancel").onclick = () => sendToHost({ type: "cancelJob" });
$("method").onchange = (e) => {
  settings.method = e.target.value;
  saveSettings();
  sendToHost({ type: "method", method: e.target.value });
};

/** 画面(WebView2 の描画のプロセス)が落ちて読み直した後: 元の動画の位置へ */
function onRestore(m) {
  pendingStart = null;
  // 動画を切り替えたとき: 切り替えの前と同じレースを選ぶ(リプレイはレースが 1 本だけ)
  if (m.race != null && races[m.race]) selectRace(m.race, "restore");
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
$("openLogs").onclick = () => sendToHost({ type: "openLogs" });
$("crashClose").onclick = () => { $("crashNotice").hidden = true; sendToHost({ type: "crashNoticeClosed" }); };

// 動画の位置と再生中かを約 2 秒ごとに C# へ(画面が落ちたときに、読み直してそこへ戻し、再生していたら続けるため)
let lastReported = "";
setInterval(() => {
  const sec = video.src ? video.currentTime : pointerSec;
  const playing = !!video.src && !video.paused;
  const key = `${sec.toFixed(2)} ${playing}`;
  if (key === lastReported) return;
  lastReported = key;
  sendToHost({ type: "position", sec, playing });
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
  fillVideos(m);
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
    friction = raceSel = null;
    FrictionCircle.setData(null, null);
    FrictionCircle.setLimits(null);
    PowerChart.setData(null);
    Measure.setData(null);
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
  friction = null;            // 系列が変わったので、C# が作り直して送ってくる
  raceSel = m.race ?? null;   // 選ぶレースは C# が決める(新しく開いたときは設定の「記録を開いたときの表示」と横軸で)
  Measure.setData(m.measure);
  fps = m.fps || null;
  if (fps) frameSec = 1 / fps;
  $("fpsText").textContent = fps ? `${+fps.toFixed(2)} fps` : "";
  fillRaces(m.marks.races || []);
  cutRows = rowFlags(tdata.t.length, m.marks.cut);
  surfaceColors = courseSurfaceRgb(tdata.course.surface);
  breakRows = rowFlags(tdata.t.length, tdata.course.breaks);
  if (!m.keepVideo) {
    setSkip(settings.skipRedo);
    // 新しく開いたときは、設定の「記録を開いたときの表示」と横軸で(レース別か距離なら、C# が 1 レース目を選んでいる)
    axis = settings.axis;
    if (selectedRace()) setRaceRange(selectedRace());
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

// 動画を再生できない(形式に対応していない・壊れている)
video.addEventListener("error", () => {
  if (!video.getAttribute("src")) return;
  const code = video.error ? video.error.code : 0;
  logEvent(`動画を再生できない: ${video.src}(MediaError ${code})`, "error");
  $("videoError").hidden = false;
  $("videoError").textContent = code === 4
    ? t("この動画は再生できません(形式に対応していません。H.264 の MP4 にしてください)")
    : t("この動画は再生できません(読み込めませんでした。ファイルが壊れているかもしれません)");
});

let pendingStart = null;   // 動画の準備ができたらここへ飛んで再生する(自動でスタート3秒前)
video.addEventListener("loadedmetadata", () => {
  renderRaceBar();   // 帯の全体は動画の長さ
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

/** 見つけたものの「変速(全開)」: 選んでいるレース(全体なら記録全体)の全開のシフトアップの数と内訳 */
function showShifts(race) {
  const all = (tmarks && tmarks.shifts) || [];
  const list = race ? all.filter((s) => s.t >= race.t0 && s.t <= race.t1) : all;
  const n = (v) => list.filter((s) => s.verdict === v).length;
  // 自動変速らしいレース(変速回転数がギアによらずそろう)は、そう添える。全体なら、該当するレースの番号
  const fmt = (v) => Math.round(v).toLocaleString("en-US");
  const autoNote = race
    ? (race.auto ? t(" ・ 自動変速らしい({lo}〜{hi} rpm にそろう)", { lo: fmt(race.autoLo), hi: fmt(race.autoHi) }) : "")
    : (() => { const k = races.map((r, i) => (r.auto ? i + 1 : null)).filter((x) => x != null); return k.length ? t(" ・ 自動変速らしいレース: {list}", { list: k.join(", ") }) : ""; })();
  $("mShifts").textContent = list.length
    ? t("{n} 回(早い {early} / 適正 {ok} / 遅い {late})", { n: list.length, early: n("early"), ok: n("ok"), late: n("late") }) + autoNote
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
// グラフのドラッグ: 再生位置の線を止めたまま、グラフをずらした分だけ再生位置を動かす(graphs.js)。ずらしている間は再生を止める
Graphs.getPointerX = () => xOf(currentSec());
Graphs.onDragStart = () => { if (video.src && !video.paused) video.pause(); };
Graphs.onDragSeek = (x) => {
  const sec = Math.max(0, Graphs.xToSec(x));
  if (!isFinite(sec)) return;
  if (video.src) video.currentTime = Math.min(sec, video.duration || sec);
  updatePointer(sec);   // 表示範囲は送らない(followTo を呼ばない)。線はすぐに描き直す
};
Graphs.onSeek = (x) => seek(Graphs.xToSec(x));
Graphs.onSeekTime = (sec) => seek(sec);

// グラフの欄の大きさの見張りは、全部のファイルを読み込んでから始める(dialogs.js の終わり)

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
  moveRaceBarHead(sec);
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
  if (settings.viewMode === "perf") { showPerfReadout(i); $("pwNote").textContent = PowerChart.note(i); }
  $("rPedal").textContent = `${v("accel", "%")} / ${v("brake", "%")}`;
  $("rSteer").textContent = v("steer", "%");
  const turn = i < 0 ? null : d.turn[i];
  $("rTurn").textContent = turn == null ? "-"
    : Math.abs(turn) < 1 ? t("ほぼ直進") : `R ${Units.fmtLen(1000 / Math.abs(turn))} ${turn > 0 ? t("右") : t("左")}`;
  $("rSlip").textContent = `${v("slipF", "", 2)} / ${v("slipR", "", 2)}`;
  // 後輪 − 前輪の滑り角(正 = オーバー)。カウンター中は書き添える
  const bal = i < 0 || !d.balance ? null : d.balance[i];
  $("rBalance").textContent = bal == null ? "-"
    : `${bal >= 0 ? "+" : ""}${bal.toFixed(2)}`
      + (Math.abs(bal) < 0.3 ? "" : bal > 0 ? t("(オーバー)") : t("(アンダー)"))
      + (d.counter && d.counter[i] === 1 ? t("・カウンター") : "");
  const gi = FrictionCircle.info(i);
  const signed = (x) => `${x >= 0 ? "+" : ""}${x.toFixed(2)}`;
  $("rG").textContent = gi ? `${signed(gi.lon)} / ${signed(gi.lat)} G` : "-";
  const li = FrictionCircle.loadInfo(i), pct = (x) => `${x >= 0 ? "+" : ""}${Math.round(x * 100)}`;
  $("rLoad").textContent = li ? `${pct(li.front)} / ${pct(li.side)} %` : "-";
  const wi = FrictionCircle.wheelInfo(i), w100 = (x) => Math.round(x * 100);
  $("rWheels").textContent = wi ? `${w100(wi.fl)} / ${w100(wi.fr)}・${w100(wi.rl)} / ${w100(wi.rr)} %` : "-";
  $("rSurface").textContent = i < 0 || !d.surface ? "-" : surfaceText(i);
  $("rGrade").textContent = `${i < 0 || d.grade[i] == null ? "-" : (d.grade[i] > 0 ? "+" : "") + d.grade[i].toFixed(1) + "%"} / ${i < 0 ? "-" : Units.fmtElev(d.elev[i])}`;
}

/** 性能モードの読み取り値: 回転数 / 全開の出力、変速の得失(と最適な範囲) */
function showPerfReadout(i) {
  const d = tdata;
  const rpm = i < 0 || d.rpm[i] == null ? null : d.rpm[i];
  const pi = PowerChart.info(i);
  $("rRpm").textContent = rpm == null ? "-"
    : `${Math.round(rpm).toLocaleString("en-US")} rpm` + (d.readout.hp[i] != null ? ` / ${Math.round(d.readout.hp[i])} hp` : "");
  const gain = i < 0 ? null : d.gain[i];
  // 最適な範囲(得失が ±3% 以内)。範囲が 1 点なら 1 つの回転数で。範囲が無く、最適な変速回転数も無ければリミッターまで
  const fmtRpm = (v) => Math.round(v).toLocaleString("en-US");
  const range = pi ? pi.range : null;
  const optText = range
    ? (range[1] - range[0] >= 50 ? t("(最適 {lo}〜{hi} rpm)", { lo: fmtRpm(range[0]), hi: fmtRpm(range[1]) }) : t("(最適 {rpm} rpm)", { rpm: fmtRpm(range[0]) }))
    : pi && pi.opt === null ? t("(最適: リミッターまで)") : "";
  // 判定と、得失を出せない理由は C# の PerfReadout が出した状態
  const state = i < 0 ? null : d.readout.state[i];
  if (gain == null) {
    // 次のギアの比が無い: 次のギアを使っていなければ最後のギアかもしれない(ギアの数はテレメトリーに無い)。使っていれば、全開の行が足りない
    const why = state === "notFull" ? t("全開でない")
      : state === "noCurve" ? t("出せない(パワーカーブが無い)")
      : state === "nextFewRows" ? t("出せない(次のギアの全開の行が足りない)")
      : state === "nextUnused" ? t("このギアより上を使っていない(最後のギアなら変速の判定は無し)")
      : state === "rpmOutside" ? t("出せない(今の回転数がパワーカーブの外)")
      : state === "afterOutside" ? t("出せない(変速した後の {rpm} rpm がパワーカーブの外)", { rpm: Math.round(d.readout.rpmAfter[i]).toLocaleString("en-US") })
      : "-";
    $("rShift").textContent = why + optText;
    return;
  }
  $("rShift").textContent = `${gain > 0 ? "+" : ""}${gain.toFixed(1)}%(${SHIFT_NAMES[state]})${optText}`;
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
  // [ / ]: 割り当てた動画の同期の微調整(映像を 1 コマ前・後のコマに。Shift で 0.1 秒。グラフのカーソルの瞬間はそのまま)。
  // キーの位置(e.code)ではなく入力される文字(e.key)で見る(日本語キーボードでは、英語の [ ] の位置に @ [ がある)。
  // Shift を押すと文字が { } になる
  const bracket = { "[": -1, "{": -1, "]": 1, "}": 1 }[e.key];
  if (bracket && altInfo && !typingTarget(e) && !e.ctrlKey && !e.altKey) {
    e.preventDefault();
    nudgeBy(e.shiftKey ? String(bracket * 0.1) : `${bracket}f`);
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
