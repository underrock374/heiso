// 再生アプリの画面: レースの選択(選んでいるレースは C# の SelectionViewModel が持つ)、動画の下のレースの帯、レースの名前のダイアログ。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。app.js が先)。
"use strict";

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

function fillRaces(list) {
  const sel = $("race");
  races = list;
  sel.innerHTML = `<option value="">${list.length ? t("全体") : t("レースなし")}</option>`
    + list.map((r, i) => `<option value="${i}" title="${escapeHtml(t("終えた瞬間 {speed}。{why}", { speed: Units.fmtSpeed(r.endKmh), why: FINISHED_BY[r.finishedBy] || "" }))}">${escapeHtml(raceLabel(r, i))}</option>`).join("");
  sel.disabled = !list.length;
  $("exportBtn").disabled = !list.length;
  sel.value = raceSel == null ? "" : String(raceSel);
  if (raceSel == null) raceRange = null;
  renderRaceBar();
}

// ---- 動画の下のレースの帯 ----
// 動画の全体(動画が無ければ記録の全体)を横に並べ、レースの区間を色、レースの外を灰色で出す。「前 / 次」で止まる所に印、今の位置に線

/** 帯の全体の長さ(秒)。動画の長さ、無ければ記録の最後の時刻 */
function raceBarSpan() {
  if (video.src && isFinite(video.duration) && video.duration > 0) return video.duration;
  return tdata && tdata.t.length ? tdata.t[tdata.t.length - 1] : 0;
}

function renderRaceBar() {
  const bar = $("raceBar"), total = raceBarSpan();
  bar.hidden = !tdata || !(total > 0);
  if (bar.hidden) return;
  const pct = (sec) => `${(Math.min(total, Math.max(0, sec)) / total) * 100}%`;
  const sel = raceSel == null ? "" : String(raceSel);
  bar.innerHTML = races.map((r, i) => {
    const w = (Math.max(0, r.t1 - r.t0) / total) * 100;
    return `<div class="rbRace${String(i) === sel ? " on" : ""}" style="left:${pct(r.t0)};width:${w}%" title="${escapeHtml(raceLabel(r, i))}">${w > 3 ? `<span>${i + 1}</span>` : ""}</div>`;
  }).join("")
    + stopPoints().map((p) => `<div class="rbStop${p.lap ? " lap" : ""}" style="left:${pct(p.t)}"></div>`).join("")
    + `<div class="rbHead"></div>`;
  moveRaceBarHead(currentSec());
}

function moveRaceBarHead(sec) {
  const head = document.querySelector("#raceBar .rbHead"), total = raceBarSpan();
  if (head && total > 0) head.style.left = `${(Math.min(total, Math.max(0, sec)) / total) * 100}%`;
}

// 帯のクリック・ドラッグ: その位置へ(グラフもその位置に合わせる)
$("raceBar").addEventListener("mousedown", (e) => {
  if (e.button !== 0) return;
  e.preventDefault();
  const bar = $("raceBar");
  const at = (clientX) => {
    const b = bar.getBoundingClientRect(), total = raceBarSpan();
    return (Math.min(b.width, Math.max(0, clientX - b.left)) / Math.max(1, b.width)) * total;
  };
  let frame = 0, lastX = e.clientX;
  const go = () => { frame = 0; const sec = at(lastX); seek(sec); followTo(sec, true); };
  go();
  const move = (ev) => { lastX = ev.clientX; if (!frame) frame = requestAnimationFrame(go); };
  const up = () => {
    window.removeEventListener("mousemove", move);
    window.removeEventListener("mouseup", up);
    if (frame) { cancelAnimationFrame(frame); go(); }
  };
  window.addEventListener("mousemove", move);
  window.addEventListener("mouseup", up);
});

function escapeHtml(s) {
  return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

/** 表示幅やグラフの拡大で、レースの選択を外す(表示範囲はすぐ外す。摩擦円・荷重移動は前のレースのまま) */
function leaveRace() {
  raceRange = null;
  if (raceSel != null) selectRace(null, "leave");
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
  selectRace(e.target.value === "" ? null : Number(e.target.value), "pick");
  if (e.target.value !== "") e.target.blur();   // 選んだ後の ← → や Space を、選択の切り替えではなく再生の操作にする
};

const selectedRace = () => (raceSel == null ? null : races[raceSel]);

/** レースを選ぶ(C# に送る。答えの HostRaceSelected を onRaceSelected が受ける)。cause は理由(C# の SelectionViewModel.Cause…) */
function selectRace(race, cause) {
  raceAsked = true;
  sendToHost({ type: "selectRace", race, cause });
}

/** C# から、選んでいるレースが届いた。理由ごとに、選んだ後の動き(表示範囲・シーク・知らせ)をする */
function onRaceSelected(m) {
  raceAsked = false;
  raceSel = m.race;
  $("race").value = raceSel == null ? "" : String(raceSel);
  const r = selectedRace();
  if (m.cause === "leave") {
    renderRaceBar();
    updateRaceNamesBtn();
    return;
  }
  if (!r) {
    raceRange = null;
    // 距離は 1 つのレースについてなので、「全体」にしたら横軸は時間に戻す
    if (axis === "dist") axis = "time";
    renderAxis(null);
    followTo(currentSec(), true);
    return;
  }
  const start = setRaceRange(r);
  renderAxis(null);
  if (m.cause === "pick") {
    seek(start);
    followTo(start, true);
  } else if (m.cause === "follow") {
    toast(t("{n} レース目に切り替えました", { n: raceSel + 1 }));
  } else if (m.cause === "axis" && (currentSec() < r.t0 - START_LEAD_SEC || currentSec() > r.t1)) {
    // 「全体」から距離にして 1 レース目を選んだとき、今の位置がそのレースの外ならスタートの 3 秒前へ
    seek(start);
    followTo(start, true);
  }
}

/** レースの範囲(スタートの 3 秒前から、終わりの 3 秒後まで)を表示範囲にする。スタートの 3 秒前の時刻を返す */
function setRaceRange(r) {
  const t = Math.max(0, r.t0 - START_LEAD_SEC);
  raceRange = { min: t, max: r.t1 + START_LEAD_SEC };
  return t;
}

/**
 * レースを選んでいるとき、再生位置が別のレースに入ったら、選択とグラフ・コース図をそのレースに切り替える
 * (続けて何本も走った記録で、次のレースが始まっても前のレースのグラフのままにならないように)。
 * 位置が属するレース = スタートの 3 秒前を過ぎた一番新しいレース。レースとレースの間は前のレースのまま。
 * sec は表示しているコマの始まりの時刻で、シークした先の時刻よりコマ 1 つ分まで前になる。
 * 選択欄でレースを選ぶと、そのスタートの 3 秒前へシークするので、境目にコマ 1 つ分の余裕を持たせて前のレースに戻さない
 */
function followRace(sec) {
  const cur = selectedRace();
  if (!cur || races.length < 2 || raceAsked) return;
  let owner = null;
  for (const r of races) if (r.t0 - START_LEAD_SEC <= sec + frameSec) owner = r;
  if (!owner || owner === cur) return;
  selectRace(races.indexOf(owner), "follow");   // 切り替えと知らせは、C# の答えを受けて onRaceSelected が
}

// ---- レースの名前(コース名・車名・セッティング名) ----
// 記録アプリの「直前のレース」の直し方と同じ。保存は C# で(session.json の race_edited と、登録の受け渡し用のファイル)

function updateRaceNamesBtn() {
  $("raceNamesBtn").disabled = !selectedRace() || isPackage || !!altInfo;
  $("addVideoBtn").disabled = !canAddVideo || !!altInfo;
}

$("raceNamesBtn").onclick = () => {
  const r = selectedRace();
  if (!r) return;
  sendToHost({ type: "raceNamesForm", race: races.indexOf(r) });
};

let rnForm = null;   // C# から届いたダイアログの中身

function fillSelect(sel, items, value) {
  sel.innerHTML = items.map(([v, label]) => `<option value="${escapeHtml(v)}">${escapeHtml(label)}</option>`).join("");
  sel.value = value;
}

/** 名前を付けるダイアログを開く(m = C# の raceNamesForm) */
let rnAssists = null;   // 開いたときの補助の設定(変えたかどうかを見る)
const RN_ASSIST_KEYS = ["abs", "tcs", "stm", "lc", "shift"];
const RN_ASSIST_IDS = { abs: "rnAbs", tcs: "rnTcs", stm: "rnStm", lc: "rnLc", shift: "rnShift" };
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
  // 補助の設定: 今の設定(session.json の race_edited の assists)。変えたときだけ保存する
  const a = Measure.data && Measure.data.races[m.race] ? Measure.data.races[m.race].assists : null;
  rnAssists = {};
  for (const k of RN_ASSIST_KEYS) { rnAssists[k] = (a && a[k]) || ""; $(RN_ASSIST_IDS[k]).value = rnAssists[k]; }
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
  sendToHost({
    type: "editRaceNames", race: rnForm.race,
    courseId, courseName, courseKind: courseId === "new" ? $("rnKind").value : null,
    carName: rnForm.car ? $("rnCarName").value.trim() : null,
    setupId: rnForm.car ? $("rnSetup").value : null,
    setupName: rnForm.car ? $("rnSetupName").value.trim() : null,
    // 補助の設定は、変えたときだけ送る(付けていないレースに、分からないを書き足さない)
    assists: RN_ASSIST_KEYS.some((k) => $(RN_ASSIST_IDS[k]).value !== rnAssists[k])
      ? Object.fromEntries(RN_ASSIST_KEYS.map((k) => [k, $(RN_ASSIST_IDS[k]).value || null])) : null,
  });
  logEvent(`レースの名前を保存: ${rnForm.race + 1} レース目`);
  $("raceNamesDlg").close();
};
