// 再生アプリの画面: コマ送り、動画の切り替えと割り当て(録画・リプレイ・別に録った動画)、同期の微調整の小窓、同期の手動補正。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。app.js が先)。
"use strict";

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

// ---- 動画の切り替えと割り当て(録画・リプレイ・別に録った動画。session.json の video_alt) ----
// 切り替え・割り当て・同期の保存は C# で。切り替えたら、C# が同じ瞬間の位置を "restore" で返す

let altInfo = null;        // 表示している割り当てた動画(録画なら null)
let canAddVideo = false;   // 動画を割り当てられる記録か(パッケージでなく、記録を終えたもの)
let hasRecording = false;
let replayRace = null;     // 自動判別を頼んだレース(記録の中の番号)

const selectedRaceIndex = () => raceSel;

function fillVideos(m) {
  altInfo = m.alt || null;
  canAddVideo = !!m.canAddVideo;
  hasRecording = !!m.hasRecording;
  const sel = $("videoSel");
  const list = m.videos || [];
  const mark = (v) => (v.status === "missing" ? t("(見つかりません)") : v.status === "changed" ? t("(変わっています)") : "");
  sel.innerHTML = `<option value="">${escapeHtml(t("録画"))}</option>`
    + list.map((v) => `<option value="${escapeHtml(v.id)}">${escapeHtml(v.label + mark(v))}</option>`).join("");
  sel.value = m.activeVideo || "";
  sel.disabled = !list.length;
  $("addVideoBtn").hidden = !canAddVideo;
  showAlt();
  updateRaceNamesBtn();
}

function showAlt() {
  const a = altInfo;
  $("altBox").hidden = !a;
  $("msClear").hidden = !!(a && a.content === "replay");   // リプレイは「探し直す」で戻す
  if (!a) return;
  const replay = a.content === "replay";
  $("aKind").textContent = a.kind === "replay" ? t("録画の中のリプレイ") : replay ? t("別の動画(リプレイ)") : t("別の動画(ライブ走行)");
  $("aSync").textContent = a.method === "auto_black_motion" ? t("自動(黒い画面と動き)・確度 高(相関 {corr})", { corr: (a.corr ?? 0).toFixed(2) })
    : a.method === "auto_black" ? t("自動(黒い画面だけ)・確度 低。ゴールの瞬間で確かめてください")
    : replay ? t("手動(先頭 {sec})", { sec: a.zero == null ? "-" : fmtTime(a.zero) })
    : a.points ? t("手動({n} 点)", { n: a.points }) : t("まだ合わせていません(「同期の手動補正」で合わせてください)");
  $("aSync").classList.toggle("warn", a.method === "auto_black" || (!replay && !a.points));
  $("aReplayRow").hidden = !replay;
  $("aRedetect").hidden = a.kind !== "replay";
  // 先頭とゴールの両方があれば、間隔とレースタイムの差で検算
  const span = replay && a.zero != null && a.end != null && a.duration ? a.end - a.zero : null;
  $("aCheck").hidden = span == null;
  if (span != null) {
    const d = span - a.duration;
    $("aCheck").textContent = t("先頭からゴールまで {span} 秒(レースタイムとの差 {diff} 秒)", { span: span.toFixed(2), diff: (d >= 0 ? "+" : "") + d.toFixed(2) });
    $("aCheck").classList.toggle("warn", Math.abs(d) > 0.1);
  }
  $("aWarn").hidden = !a.vfr;
  $("aWarn").textContent = a.vfr ? t("この動画はコマの間隔が一定でない(可変フレームレート)ようです。時刻がずれることがあるので、ゴール付近でも合っているか確かめてください") : "";
  if (a.id !== nudge.id) { nudge.id = a.id; nudge.total = 0; }
  showNudge();
}

// ---- 同期の微調整の小窓 ----
// グラフ(テレメトリーの瞬間)はそのままで、映像のコマを替える: 映像を delta 秒送り、同じだけ対応をずらす(C# の altNudge)。
// こうすると、グラフのカーソルが指すテレメトリーの瞬間は変わらず、映像だけが前後のコマになる

const nudge = { id: null, total: 0 };
const floatState = settings.syncFloat || (settings.syncFloat = { closed: false, x: null, y: null });   // 小窓の位置と、閉じたか(設定に保存する)
const saveFloat = saveSettings;

function showNudge() {
  const open = !!altInfo && !floatState.closed;
  const box = $("syncFloat");
  const wasHidden = box.hidden;
  box.hidden = !open;
  if (open && wasHidden) placeFloat();
  // リプレイは保存したずらし量(新しく割り当てたときの設定の量を含む)、ライブはこの動画を表示している間にずらした量
  const ms = altInfo && altInfo.shiftMs != null ? Math.round(altInfo.shiftMs) : Math.round(nudge.total * 1000);
  // コマの数も出す(その動画のフレームレートで。小数は 1 桁まで)
  const f = Math.round(Math.abs(ms) * (fps || 1 / frameSec) / 100) / 10;
  $("sfShift").textContent = ms > 0 ? t("映像を {n} コマ({ms} ms)後ろへずらしました", { n: f, ms })
    : ms < 0 ? t("映像を {n} コマ({ms} ms)前へずらしました", { n: f, ms: -ms }) : "";
}

/** 小窓の位置: 覚えていればそこ、無ければ動画の左下。画面の外に出ないように */
function placeFloat() {
  const box = $("syncFloat");
  const vb = $("video").getBoundingClientRect();
  let x = floatState.x ?? vb.left + 12, y = floatState.y ?? vb.bottom - box.offsetHeight - 60;
  x = Math.min(Math.max(0, x), innerWidth - box.offsetWidth);
  y = Math.min(Math.max(0, y), innerHeight - box.offsetHeight);
  box.style.left = `${x}px`;
  box.style.top = `${y}px`;
}
window.addEventListener("resize", () => { if (!$("syncFloat").hidden) placeFloat(); });

$("sfHead").addEventListener("pointerdown", (e) => {
  if (e.target.tagName === "BUTTON") return;
  const box = $("syncFloat"), r = box.getBoundingClientRect();
  const dx = e.clientX - r.left, dy = e.clientY - r.top;
  $("sfHead").setPointerCapture(e.pointerId);
  const move = (ev) => { floatState.x = ev.clientX - dx; floatState.y = ev.clientY - dy; placeFloat(); };
  const up = () => { $("sfHead").removeEventListener("pointermove", move); $("sfHead").removeEventListener("pointerup", up); saveFloat(); };
  $("sfHead").addEventListener("pointermove", move);
  $("sfHead").addEventListener("pointerup", up);
});
$("sfClose").onclick = () => { floatState.closed = true; saveFloat(); showNudge(); };
$("aFloatOpen").onclick = () => { floatState.closed = false; saveFloat(); showNudge(); };

/** 映像を v("1f" = 1 コマ、"0.1" = 0.1 秒。負なら前)だけ送り、同じだけ対応をずらす(グラフのカーソルの瞬間はそのまま) */
function nudgeBy(v) {
  if (!altInfo || !video.src) return;
  const delta = v.endsWith("f") ? Number(v.slice(0, -1)) * frameSec : Number(v);
  video.pause();
  const target = video.currentTime + delta;
  if (target < 0) return;
  nudge.total += delta;
  video.currentTime = target;
  sendToHost({ type: "altNudge", delta });
  showNudge();
}
for (const b of document.querySelectorAll("#sfNudge button")) b.onclick = () => nudgeBy(b.dataset.nudge);

/** 前・次のブレーキの踏み始め(0 → 踏む)か変速(ギアが変わる動作の始まり)へ、グラフのカーソルと映像を移す */
function jumpEvent(kind, dir) {
  if (!tdata) return;
  const tt = tdata.t, list = [];
  if (kind === "brake") {
    const b = tdata.brake;
    for (let i = 1; i < tt.length; i++) if (b[i] != null && b[i] > 0 && b[i - 1] === 0) list.push(tt[i]);
  } else {
    // 変速: ギアの値が途切れた(変速動作中 = null)所。走行中(速度あり)だけ
    const g = tdata.gear, sp = tdata.speed;
    for (let i = 1; i < tt.length; i++) if (g[i] == null && g[i - 1] != null && sp[i] != null && sp[i] > 5) list.push(tt[i]);
  }
  const now = currentSec(), eps = 0.02;
  const target = dir > 0 ? list.find((x) => x > now + eps) : [...list].reverse().find((x) => x < now - eps);
  if (target == null) { toast(t("この先にはありません")); return; }
  video.pause();
  seek(target);
  followTo(target, true);
  updatePointer(target);
}
for (const b of document.querySelectorAll("#syncFloat [data-jump]")) b.onclick = () => jumpEvent(b.dataset.jump, Number(b.dataset.dir));

$("videoSel").onchange = (e) => {
  const id = e.target.value || null;
  logEvent(`動画を切り替える: ${id || "録画"}`);
  sendToHost({ type: "selectVideo", id, sec: currentSec(), race: selectedRaceIndex() });
  e.target.blur();
};

function onVideoMissing(m) {
  $("videoSel").value = m.active || "";
  const q = m.changed ? t("動画が、割り当てたときと変わっています(大きさか長さが違います)。動画を選び直しますか?")
    : t("動画が見つかりません(移動したか、消したかもしれません)。動画を選び直しますか?");
  if (confirm(q)) sendToHost({ type: "relinkVideo", id: m.id, sec: currentSec(), race: selectedRaceIndex() });
}

$("addVideoBtn").onclick = () => {
  const r = selectedRace();
  if (!r) { toast(t("動画を割り当てるレースを、上の「レース」で選んでください"), true); return; }
  $("avRace").textContent = raceLabel(r, races.indexOf(r));
  $("avAuto").disabled = !hasRecording;
  $("avAutoNote").classList.toggle("warn", !hasRecording);
  $("addVideoDlg").showModal();
};
$("avCancel").onclick = () => $("addVideoDlg").close();
$("avAuto").onclick = () => {
  $("addVideoDlg").close();
  logEvent("リプレイを探す: 全部のレース");
  sendToHost({ type: "addReplayAuto", race: selectedRaceIndex() });
};
for (const [id, content] of [["avLive", "live"], ["avReplay", "replay"]]) {
  $(id).onclick = () => {
    $("addVideoDlg").close();
    sendToHost({ type: "addVideoFile", content, race: selectedRaceIndex(), sec: currentSec() });
  };
}

/** 割り当て済みのリプレイの今の合わせ方(ダイアログに出す文) */
function assignedText(a) {
  const ms = Math.round(a.shiftMs);
  const n = a.shiftFrames != null ? Math.abs(a.shiftFrames) : null;
  const shift = ms === 0 ? t("微調整なし")
    : n != null ? (ms > 0 ? t("映像を {n} コマ({ms} ms)後ろへずらしています", { n, ms }) : t("映像を {n} コマ({ms} ms)前へずらしています", { n, ms: -ms }))
      : (ms > 0 ? t("映像を {ms} ms 後ろへずらしています", { ms }) : t("映像を {ms} ms 前へずらしています", { ms: -ms }));
  return t("割り当て済み: {how}、先頭は動画の {zero}。{shift}", {
    how: a.manual ? t("手動で合わせた") : t("自動で合わせた"), zero: fmtTime(a.zero), shift,
  });
}

/** 割り当て済みのレースを作り直してよいか(割り当てていなければ確かめない) */
function confirmReassign(assigned, race) {
  if (!assigned.has(race)) return true;
  return confirm(t("{n} レース目のリプレイを作り直します。今の合わせ方(微調整を含む)は消えます。よろしいですか?", { n: race + 1 }));
}

/**
 * 自動判別の結果(レースごと)。見つかったレースに「採用」を出す。手動で合わせるのは、選んでいるレース(無ければ結果の最後のレース)。
 * 割り当て済みのレースは今の合わせ方と「表示する」を出し、「採用」「手動で合わせる」は確かめてから作り直す
 */
let replayAssigned = new Map();
function onReplayFound(m) {
  const list = m.results || [];
  replayAssigned = new Map((m.assigned || []).map((a) => [a.race, a]));
  replayRace = m.selected ?? (list.length ? list[list.length - 1].race : null);
  $("rfRace").textContent = "";
  const box = $("rfList");
  box.innerHTML = "";
  for (const r of list) {
    const row = document.createElement("div");
    row.className = "rfRow" + (r.found ? " found" : "");
    const head = document.createElement("div");
    head.className = "rfHead";
    head.textContent = races[r.race] ? raceLabel(races[r.race], r.race) : t("{n} レース目", { n: r.race + 1 });
    const text = document.createElement("div");
    text.className = "rfText";
    text.textContent = r.found
      ? t("リプレイを見つけました: 動画の {zero} から {end} まで(レースタイム {len} 秒)。確度: {conf}", {
        zero: fmtTime(r.zero), end: fmtTime(r.end), len: r.duration.toFixed(2),
        conf: r.confidence === "high" ? t("高(動きの相関 {corr})", { corr: (r.corr ?? 0).toFixed(2) }) : t("低(切り替えた後、ゴールの瞬間で確かめてください)"),
      })
      : t("このレースの後にはリプレイがありません");
    row.append(head, text);
    const btns = document.createElement("div");
    btns.className = "rfBtns";
    const a = replayAssigned.get(r.race);
    if (a) {
      const p = document.createElement("div");
      p.className = "rfText";
      p.textContent = assignedText(a);
      const show = document.createElement("button");
      show.className = "cmd";
      show.textContent = t("表示する");
      show.onclick = () => {
        $("replayFoundDlg").close();
        logEvent(`割り当て済みのリプレイを表示する: ${a.id}`);
        sendToHost({ type: "selectVideo", id: a.id, sec: currentSec(), race: r.race });
      };
      row.append(p);
      btns.append(show);
    }
    if (r.warning) {
      const w = document.createElement("div");
      w.className = "error";
      w.textContent = r.warning;
      row.append(w);
    }
    if (r.found) {
      const b = document.createElement("button");
      b.className = "cmd primary";
      b.textContent = t("採用");
      b.onclick = () => {
        if (!confirmReassign(replayAssigned, r.race)) return;
        $("replayFoundDlg").close();
        // 割り当てた後、「同期の微調整」の小窓を出す(設定の量だけ前へずらした状態を見せる)
        floatState.closed = false;
        saveFloat();
        sendToHost({ type: "adoptReplay", manual: false, sec: currentSec(), race: r.race });
      };
      btns.append(b);
    }
    if (btns.childElementCount) row.append(btns);
    box.append(row);
  }
  if (!list.some((r) => r.found)) {
    const p = document.createElement("p");
    p.className = "muted";
    p.textContent = t("どのレースの後にも、リプレイの終わりの黒い画面が見つかりませんでした。手動で合わせることはできます。");
    box.append(p);
  }
  $("rfManual").hidden = replayRace == null;
  $("rfManual").textContent = replayRace == null ? t("手動で合わせる") : t("{n} レース目に手動で合わせる", { n: replayRace + 1 });
  $("replayFoundDlg").showModal();
}
$("rfCancel").onclick = () => $("replayFoundDlg").close();
$("rfManual").onclick = () => {
  if (replayRace == null || !confirmReassign(replayAssigned, replayRace)) return;
  $("replayFoundDlg").close();
  sendToHost({ type: "adoptReplay", manual: true, sec: currentSec(), race: replayRace });
};

$("aZero").onclick = () => {
  if (!video.src) return;
  video.pause();
  sendToHost({ type: "altMark", what: "zero", sec: lastMediaTime ?? video.currentTime });
};
$("aEnd").onclick = () => {
  if (!video.src) return;
  video.pause();
  sendToHost({ type: "altMark", what: "end", sec: lastMediaTime ?? video.currentTime });
};
$("aGoFinish").onclick = () => {
  // リプレイはレースが 1 本だけ。ライブは割り当てたレース
  const r = races[altInfo && altInfo.content !== "replay" ? altInfo.race : 0];
  if (!r || r.tf == null) return;
  video.pause();
  seek(r.tf);
  followTo(r.tf, true);
};
$("aRedetect").onclick = () => {
  if (!altInfo) return;
  replayRace = altInfo.race;
  sendToHost({ type: "addReplayAuto", only: altInfo.race, race: altInfo.race });
};
$("aRelink").onclick = () => {
  if (altInfo) sendToHost({ type: "relinkVideo", id: altInfo.id, sec: currentSec(), race: selectedRaceIndex() });
};
$("aRemove").onclick = () => {
  if (altInfo && confirm(t("この動画の割り当てを外しますか?(動画のファイルは消しません)")))
    sendToHost({ type: "removeVideo", id: altInfo.id, sec: currentSec(), race: selectedRaceIndex() });
};

// ---- リプレイを割り当てたときのずらし量(設定の「同期」。コマ。C# の player.json に保存し、新しく割り当てるときに使う) ----

let replayLeadDefault = 5;
function setReplayLead(frames) {
  if (!Number.isFinite(frames)) return;
  frames = Math.max(-60, Math.min(60, Math.round(frames)));
  $("sReplayLead").value = frames;
  logEvent(`リプレイを割り当てたときのずらし量: 前へ ${frames} コマ`);
  sendToHost({ type: "setReplayLead", frames });
}
$("sReplayLead").onchange = () => setReplayLead(Number($("sReplayLead").value));
$("sReplayLeadReset").onclick = () => setReplayLead(replayLeadDefault);

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
  sendToHost({ type: "manualSync", graphSec: ms.graph, videoSec: ms.frame, note: $("msNote").value });
};
$("msClear").onclick = () => {
  if (confirm(t("この記録の手動補正を全部消して、自動の同期に戻しますか?"))) sendToHost({ type: "clearManualSync" });
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
/** 「◀ 前 / 次 ▶」で止まる所(動画の秒、昇順)。{ t, lap(周の印なら true) } */
function stopPoints() {
  return [
    ...(tmarks ? tmarks.starts : []).filter((s) => s.race || Graphs.showLaunches).map((s) => ({ t: s.t - leadOf(s), lap: false })),
    ...((tmarks && tmarks.laps) || []).map((l) => ({ t: l.t - LAP_LEAD_SEC, lap: true })),
  ].map((p) => ({ ...p, t: Math.max(0, p.t) })).sort((a, b) => a.t - b.t);
}

function jumpStart(dir) {
  const list = stopPoints().map((p) => p.t);
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
