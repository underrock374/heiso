// 記録アプリの画面(スマホと PC のブラウザ)。状態は /api/status から読み、操作は /api/* に送る。
// 前に i18n-data.js・i18n.js・qrcode.js を読み込む(index.html)。
"use strict";
const K = new URLSearchParams(location.search).get("k") || "";
const $ = id => document.getElementById(id);
document.documentElement.lang = i18nLang;
applyI18n();
document.querySelector("section.control").setAttribute("aria-label", t("記録の操作"));
$("connect").setAttribute("aria-label", t("スマホで開く"));
/** 最後に受けた状態(/api/status の答え。型は Fh6.Recorder/api.d.ts) @type {RecorderStatus | null} */
let latest = null;
let busy = false, armedTimer = null, pollTimer = null;

async function api(path, body) {
  const init = { headers: { "X-Key": K } };
  if (body !== undefined) {
    init.method = "POST";
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  const r = await fetch(path, init);
  const j = await r.json().catch(() => ({}));
  if (!r.ok) { const e = new Error(j.error || `HTTP ${r.status}`); e.status = r.status; throw e; }
  return j;
}

function toast(msg) {
  const el = $("toast");
  el.textContent = msg;
  el.classList.add("show");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.remove("show"), 2200);
}

function fmtTime(sec) {
  sec = Math.max(0, Math.floor(sec));
  const h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
  const mm = String(m).padStart(2, "0"), ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

const STATE_NAMES = { race: t("レース中"), freeroam: t("フリーローム"), idle: t("停車中"), menu: t("メニュー/ロード"), none: t("受信なし") };
const num = n => Number(n).toLocaleString("en-US");   // 数は地域の形式によらず、3 桁ごとにカンマ

/** @param {RecorderStatus} s */
function render(s) {
  latest = s;
  const tel = s.telemetry, obs = s.obs, ses = s.session;
  const running = s.state === "running";
  const transitioning = s.state === "starting" || s.state === "stopping";

  // 信号
  const telOk = tel.listening && tel.hz > 0;
  $("sigTel").className = "sig " + (telOk ? "ok" : "bad");
  // UDP の送り直し(パススルー。SimHub などと併用するとき)。設定の誤りは赤で出す
  const fwd = tel.forward;
  const fwdText = !fwd ? "" : fwd.error ? `${t(" ・ ")}${fwd.error}`
    : t(" ・ 転送 {targets}{failed}", { targets: fwd.targets.join(", "), failed: fwd.failures ? t("(送れなかった {n})", { n: num(fwd.failures) }) : "" });
  $("telText").textContent = (tel.error ? tel.error
    : telOk ? t("テレメトリ {hz}Hz", { hz: Math.round(tel.hz) })
    : t("テレメトリ 受信なし(UDP {port})", { port: tel.port })) + fwdText;
  $("telText").title = fwd && !fwd.error ? t("送り直した数: {n}", { n: num(fwd.packets) }) : "";
  if (fwd && fwd.error) $("sigTel").className = "sig bad";
  $("sigObs").className = "sig " + (obs.connected ? "ok" : "bad");
  $("obsText").textContent = !obs.enabled ? t("OBS連携なし")
    : obs.connected ? (obs.recording ? t("OBS 録画中") : t("OBS 接続済み"))
    : t("OBS 未接続");
  $("sigObs").title = obs.error || "";

  // タリー
  document.body.classList.toggle("running", running || s.state === "stopping");
  document.body.classList.toggle("mode-rec", !!(ses && ses.videoActive));
  document.body.classList.toggle("mode-log", !!(ses && !ses.videoActive));

  // メインボタン
  const btn = $("mainBtn");
  btn.disabled = busy || transitioning;
  if (s.state === "starting") { $("mainTop").textContent = t("開始中"); $("mainSub").textContent = ""; }
  else if (s.state === "stopping") { $("mainTop").textContent = t("保存中"); $("mainSub").textContent = t("録画ファイルを閉じています"); }
  else if (running && ses) {
    $("mainTop").textContent = fmtTime(ses.elapsedSec);
    if (!btn.classList.contains("armed"))
      $("mainSub").textContent = ses.videoActive ? t("録画+ログ ・ タップで停止") : t("ログのみ ・ タップで停止");
  } else {
    btn.classList.remove("armed");
    $("mainTop").textContent = t("記録開始");
    $("mainSub").textContent = $("video").checked && obs.connected ? t("録画+ログ") : t("ログのみ");
  }

  // 警告
  const warn = s.warning || (!running && obs.enabled && !obs.connected && $("video").checked ? (obs.error || t("OBSに接続できません")) : null)
    || (s.race && s.race.registryWarning);
  $("warn").hidden = !warn;
  $("warn").textContent = warn || "";

  // オプション
  const videoBox = $("video");
  videoBox.disabled = running || transitioning || !obs.connected;
  $("videoHint").textContent = !obs.enabled ? t("設定でOBS連携が無効です")
    : !obs.connected ? t("OBSにつながると選べます。今はログだけ記録します") : "";
  $("label").disabled = running || transitioning;
  renderQuality(s, running || transitioning);
  renderScene(s, running || transitioning);
  $("markerBtn").disabled = !running;
  $("markerBtn").textContent = running && ses && ses.markers ? t("マーカーを打つ({n})", { n: ses.markers }) : t("マーカーを打つ");

  // ライブ値
  let st = STATE_NAMES[tel.state] || "—";
  if (tel.state === "race" && tel.racePos) st += t(" {pos}位", { pos: tel.racePos });
  $("lvState").textContent = st;
  $("lvSpeed").textContent = s.speedText ?? "—";   // 単位は C# が決めて書く(appsettings の SpeedUnit)
  renderRace(s.race, tel);
  $("lvRows").textContent = ses ? num(ses.rows) : "—";

  // 前回
  if (s.last) {
    const l = s.last;
    const sep = t(" ・ ");
    const video = l.video ? t("動画 {file}", { file: l.video.split(/[\\/]/).pop() }) : t("動画なし{why}", { why: l.videoError ? `(${l.videoError})` : "" });
    const sync = l.endCheckSec == null ? "" : sep + t("同期の終端誤差 {sec}秒", { sec: (l.endCheckSec >= 0 ? "+" : "") + l.endCheckSec.toFixed(2) });
    const size = l.mbPerMin == null ? "" : sep + t("{mb} MB/分", { mb: l.mbPerMin }) + (l.quality ? `(${qualityName(l.quality)})` : "");
    $("lastText").textContent = [l.name, fmtTime(l.durationSec), t("{n}行", { n: num(l.rows) }), t("マーカー{n}", { n: l.markers }), video].join(sep) + size + sync;
    $("last").hidden = false;
  }
}

// ---- 車両・コース(確認カード) ----
const esc = v => String(v == null ? "" : v).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
function renderRace(r, tel) {
  if (!r) { $("lvCar").textContent = tel.car ? `${tel.car.class}${tel.car.pi} ${tel.car.drivetrain}` : "—"; return; }
  const c = r.car;
  $("lvCar").textContent = c ? (c.name || t("未登録の車両")) : "—";
  $("lvCar").title = c ? `${c.cls}${c.pi} ${c.drive} ${t("{n}気筒", { n: c.cyl })}${t(" ・ ")}ID ${c.ordinal}` : "";
  $("lvSetup").textContent = c ? (c.setup || t("未確定")) : "—";
  $("lvCourse").textContent = r.course ? r.course.name + (r.course.kind ? `(${r.kinds[r.course.kind] || r.course.kind})` : "") : "—";
  $("carChips").innerHTML = c
    ? [`${c.cls} ${c.pi}`, c.drive, t("{n}気筒", { n: c.cyl })].map(x => `<span class="chip">${esc(x)}</span>`).join("")
      + (c.confirmed ? `<span class="chip ok">${t("確定済み")}</span>` : `<span class="chip warn">${t("未確定")}</span>`)
    : "";
  $("variantBtn").disabled = !c;
  renderPrompts(r);
  renderLastRace(r);
  $("recent").hidden = !r.recent.length;
  $("recentList").innerHTML = r.recent.map(e => `<li><time>${esc(e.at)}</time><span>${esc(e.text)}</span></li>`).join("");
}
function carCard(p, r) {
  const s = p.sig, known = p.knownName, guess = p.setups.find(x => x.id === p.guessSetup);
  const what = p.atStart ? t("記録開始時の車両") : t("{what} が変わりました", { what: p.changed.join(t("・")) });
  const opts = p.setups.map(x => `<option value="${esc(x.id)}"${x.id === p.guessSetup ? " selected" : ""}>${esc(x.name)}</option>`).join("")
    + `<option value="new"${guess ? "" : " selected"}>${t("新しいセッティングとして登録")}</option>`;
  return `<section class="card" data-pid="${p.id}">
    <h2>${t("車両の確認")}<small>${esc(p.created)}</small></h2>
    <div class="chips"><span class="chip warn">${esc(what)}</span><span class="chip">${esc(s.cls)} ${s.pi}</span><span class="chip">${esc(s.drive)}</span><span class="chip">${t("{n}気筒", { n: s.cyl })}</span><span class="chip">ID ${s.ordinal}</span></div>
    <label>${known ? t("車名(登録済み)") : t("車名(未登録の車両です)")}<input name="car" list="dl-${p.id}" value="${esc(known)}" placeholder="${esc(t("例: Honda Civic Type R"))}" autocomplete="off"></label>
    <datalist id="dl-${p.id}">${r.known.cars.map(n => `<option value="${esc(n)}">`).join("")}</datalist>
    <label>${t("セッティング")}<select name="setupChoice">${opts}</select></label>
    <label>${t("セッティング名")}<input name="setupName" value="${esc(guess ? guess.name : "")}" placeholder="${esc(t("例: 雨用 ソフト"))}" autocomplete="off"></label>
    <label>${t("メモ(任意)")}<input name="note" autocomplete="off"></label>
    <div class="row"><button class="btn primary" data-act="car">${known && guess ? t("この内容で確定") : t("登録する")}</button><button class="btn" data-act="skip">${t("あとで")}</button></div>
    <p class="err"></p>
  </section>`;
}
function courseCard(p, r) {
  const opts = `<option value="new" selected>${t("新しいコースとして登録")}</option>`
    + r.known.courses.map(c => `<option value="${esc(c.id)}">${esc(c.name)}</option>`).join("");
  const kinds = Object.entries(r.kinds).map(([k, v]) => `<option value="${k}">${esc(v)}</option>`).join("");
  return `<section class="card" data-pid="${p.id}">
    <h2>${t("未登録のコースでレースが始まりました")}<small>${esc(p.created)}</small></h2>
    <p>${t("スタート地点 x {x} / z {z}。登録すると、次から自動で見分けます(グリッドが後ろでも)。", { x: p.start.x.toFixed(0), z: p.start.z.toFixed(0) })}</p>
    <label>${t("登録先")}<select name="courseChoice">${opts}</select></label>
    <label>${t("レース名")}<input name="name" placeholder="${esc(t("例: 瑠璃光寺クロスカントリー"))}" autocomplete="off"></label>
    <label>${t("種類")}<select name="kind">${kinds}</select></label>
    <label>${t("メモ(任意)")}<input name="note" autocomplete="off"></label>
    <div class="row"><button class="btn primary" data-act="course">${t("登録する")}</button><button class="btn" data-act="skip">${t("あとで")}</button></div>
    <p class="err"></p>
  </section>`;
}
function renderPrompts(r) {
  const box = $("prompts"), ids = r.prompts.map(p => p.id);
  // 入力中のカードは作り直さない
  for (const el of [...box.children]) if (!ids.includes(el.dataset.pid)) el.remove();
  for (const p of r.prompts) {
    const shown = box.querySelector(`[data-pid="${p.id}"]`);
    if (shown) {
      // 同じ車種の別のカードで車名を登録したら、まだ何も入れていない車名の欄へ入れる(入れ途中の欄は変えない)
      const car = shown.querySelector("[name=car]");
      if (car && p.knownName && !car.value.trim() && document.activeElement !== car) {
        car.value = p.knownName;
        car.closest("label").firstChild.textContent = t("車名(登録済み)");
      }
      continue;
    }
    box.insertAdjacentHTML("beforeend", p.type === "car" ? carCard(p, r) : courseCard(p, r));
    const el = box.querySelector(`[data-pid="${p.id}"]`);
    const sel = el.querySelector("[name=setupChoice]");
    if (sel) sel.onchange = () => {
      const o = p.setups.find(x => x.id === sel.value);
      el.querySelector("[name=setupName]").value = o ? o.name : "";
    };
    const cc = el.querySelector("[name=courseChoice]");
    if (cc) cc.onchange = () => {
      const isNew = cc.value === "new";
      el.querySelector("[name=name]").closest("label").hidden = !isNew;
      el.querySelector("[name=kind]").closest("label").hidden = !isNew;
    };
    for (const b of el.querySelectorAll("button")) b.onclick = () => promptAction(p, el, b);
  }
}
async function promptAction(p, el, btn) {
  const err = el.querySelector(".err");
  const v = n => { const x = el.querySelector(`[name=${n}]`); return x ? x.value : null; };
  err.textContent = "";
  btn.disabled = true;
  try {
    const act = btn.dataset.act;
    if (act === "skip") await api("/api/dismiss", { id: p.id });
    else if (act === "car") await api("/api/car", { id: p.id, car: v("car"), setupChoice: v("setupChoice"), setupName: v("setupName"), note: v("note") });
    else await api("/api/course", { id: p.id, choice: v("courseChoice"), name: v("name"), kind: v("kind"), note: v("note") });
    toast(act === "skip" ? t("あとで確認します") : t("登録しました"));
    el.remove();
    poll();
  } catch (e) {
    err.textContent = e.message;
    btn.disabled = false;
  }
}
// ---- 直前のレース(レースが終わった後・記録を止めた後でも直せる) ----
let lastRaceEditing = false;
function renderLastRace(r) {
  const l = r.lastRace;
  $("lastRace").hidden = !l;
  if (!l) return;
  const car = l.car ? `${l.car.name || t("未登録の車両")}${l.car.setup ? ` ${l.car.setup}` : ""}` : t("車両不明");
  const course = l.course ? l.course.name : t("コース未登録");
  const sep = t(" ・ ");
  $("lastRaceSum").innerHTML = `${esc(course)}${sep}${esc(car)}`
    + `<small>${esc([t("{time} 開始", { time: l.startedAt }), l.ended ? t("終了") : t("走行中"), l.recorded ? t("記録 {name}", { name: l.recorded }) : t("記録していないレース")].join(sep))}</small>`;
  if (!lastRaceEditing) $("lastRaceEdit").hidden = false;
}
function openLastRaceForm() {
  const r = latest && latest.race, l = r && r.lastRace;
  if (!l) return;
  lastRaceEditing = true;
  $("lastRaceEdit").hidden = true;
  const courseOpts = (l.course ? "" : `<option value="" selected>${t("(未登録のまま)")}</option>`)
    + r.known.courses.map(c => `<option value="${esc(c.id)}"${l.course && l.course.id === c.id ? " selected" : ""}>${esc(c.name)}</option>`).join("")
    + `<option value="new">${t("新しいコースとして登録")}</option>`;
  const kinds = Object.entries(r.kinds).map(([k, v]) => `<option value="${k}">${esc(v)}</option>`).join("");
  const setupOpts = l.car
    ? (l.car.setupId ? "" : `<option value="" selected>${t("(未確定のまま)")}</option>`)
      + l.car.setups.map(x => `<option value="${esc(x.id)}"${x.id === l.car.setupId ? " selected" : ""}>${esc(x.name)}</option>`).join("")
      + `<option value="new">${t("新しいセッティングとして登録")}</option>`
    : "";
  const f = $("lastRaceForm");
  f.innerHTML = `
    <label>${t("コース")}<select name="courseChoice">${courseOpts}</select></label>
    <label>${t("レース名")}<input name="courseName" value="${esc(l.course ? l.course.name : "")}" autocomplete="off"></label>
    <label${l.course ? " hidden" : ""}>${t("種類(新しく登録するとき)")}<select name="kind">${kinds}</select></label>
    ${l.car ? `
    <label>${t("車名")}<input name="car" value="${esc(l.car.name)}" list="dl-lastrace" autocomplete="off"></label>
    <datalist id="dl-lastrace">${r.known.cars.map(n => `<option value="${esc(n)}">`).join("")}</datalist>
    <label>${t("セッティング")}<select name="setupChoice">${setupOpts}</select></label>
    <label>${t("セッティング名")}<input name="setupName" value="${esc(l.car.setup)}" autocomplete="off"></label>` : `<p>${t("このレースは車両を検知する前に始まったため、車名は直せません。")}</p>`}
    <p>${t("名前を変えると、登録している名前そのものが変わり、次からもその名前で出ます。")}</p>
    <div class="row"><button class="btn primary" data-act="save">${t("保存")}</button><button class="btn" data-act="cancel">${t("やめる")}</button></div>
    <p class="err"></p>`;
  f.hidden = false;
  const v = n => { const x = f.querySelector(`[name=${n}]`); return x ? x.value : null; };
  const cc = f.querySelector("[name=courseChoice]");
  cc.onchange = () => {
    const c = r.known.courses.find(x => x.id === cc.value);
    f.querySelector("[name=courseName]").value = c ? c.name : "";
    f.querySelector("[name=kind]").closest("label").hidden = cc.value !== "new" && cc.value !== "";
  };
  const sc = f.querySelector("[name=setupChoice]");
  if (sc) sc.onchange = () => {
    const o = l.car.setups.find(x => x.id === sc.value);
    f.querySelector("[name=setupName]").value = o ? o.name : "";
  };
  f.querySelector("[data-act=cancel]").onclick = closeLastRaceForm;
  f.querySelector("[data-act=save]").onclick = async (e) => {
    const err = f.querySelector(".err");
    err.textContent = "";
    e.target.disabled = true;
    try {
      render(await api("/api/last-race", {
        // 「未登録のまま」「未確定のまま」で名前を入れたら、新しく登録する
        courseChoice: v("courseChoice") || (v("courseName").trim() ? "new" : null), courseName: v("courseName"), kind: v("kind"),
        car: v("car"), setupChoice: v("setupChoice") || ((v("setupName") || "").trim() ? "new" : null), setupName: v("setupName"),
      }));
      toast(l.recorded ? t("直しました(記録にも書きました)") : t("直しました"));
      closeLastRaceForm();
    } catch (ex) {
      err.textContent = ex.message;
      e.target.disabled = false;
    }
  };
}
function closeLastRaceForm() {
  lastRaceEditing = false;
  $("lastRaceForm").hidden = true;
  $("lastRaceForm").innerHTML = "";
  $("lastRaceEdit").hidden = false;
  if (latest) render(latest);
}
$("lastRaceEdit").addEventListener("click", openLastRaceForm);

$("variantBtn").addEventListener("click", async () => {
  try {
    render(await api("/api/variant", {}));
    toast(t("セッティング名を入力してください"));
    window.scrollTo({ top: 0, behavior: "smooth" });
  } catch (e) { toast(e.message); }
});

// 録画の画質の段階。選んだものはアプリ側(保存先フォルダ)に覚え、次の起動でも使う
const OBS_PRESET = { id: "obs", name: t("OBS の設定のまま") };
function qualityName(id) {
  if (id === "obs") return OBS_PRESET.name;
  const p = latest && latest.quality.presets.find(x => x.id === id);
  return p ? p.name : id;
}
function renderQuality(s, locked) {
  const sel = $("quality"), q = s.quality;
  if (!sel.options.length) {
    for (const p of q.presets) {
      const o = document.createElement("option");
      o.value = p.id;
      sel.appendChild(o);
    }
    const o = document.createElement("option");
    o.value = OBS_PRESET.id; o.textContent = OBS_PRESET.name;
    sel.appendChild(o);
  }
  // OBS にプロファイルがまだ無い段階は「未作成」と出す
  for (const p of q.presets) {
    const o = sel.querySelector(`option[value="${p.id}"]`);
    o.textContent = t("{name} ・ {height}p {fps}fps ・ 約 {mb} MB/分", { name: p.name, height: p.height, fps: p.fps, mb: p.mbPerMin }) + (p.ready === false ? t("(OBS に未作成)") : "");
  }
  const ses = s.session;
  const current = ses && ses.quality ? ses.quality : q.selected;
  if (document.activeElement !== sel) sel.value = current;
  const videoOn = $("video").checked && s.obs.connected;
  sel.disabled = locked || !videoOn;
  $("qualityBox").hidden = !s.obs.enabled;
  const p = q.presets.find(x => x.id === sel.value);
  $("qualityHint").textContent = ses && ses.quality
    ? (ses.qualityApplied ? t("この画質で録画中(終わったら OBS のプロファイルを元に戻します)") : t("OBS の設定のまま録画中"))
    : sel.value === "obs" ? t("OBS の設定を変えずに録画します")
    : p && p.ready === false ? t("OBS にプロファイル「{profile}」がありません。OBS を閉じて「HeisoRecorder --setup-obs-profiles」を実行すると作れます。このままだと今の OBS の設定で録画します", { profile: p.profile })
    : t("録画の間だけ OBS のプロファイル「{profile}」に切り替え、終わったら元に戻します", { profile: p ? p.profile : "" }) + (p && p.note ? `(${p.note})` : "");
}

// 録画するシーン。選んだものはアプリ側(保存先フォルダ)に覚え、録画の間だけ OBS をそのシーンに切り替える。空 = OBS で今選んでいるシーンのまま
let sceneKey = null;
function renderScene(s, locked) {
  const sel = $("scene"), sc = s.scene;
  $("sceneBox").hidden = !s.obs.enabled || !sc;
  if (!sc) return;
  const scenes = sc.scenes || [];
  const missing = sc.selected && !scenes.includes(sc.selected) && sc.scenes != null;
  // 選択肢は、シーンの一覧か今のシーンが変わったときだけ作り直す(開いている一覧が閉じないように)
  const key = JSON.stringify([scenes, sc.current, sc.selected, missing]);
  if (key !== sceneKey && document.activeElement !== sel) {
    sceneKey = key;
    sel.innerHTML = "";
    const add = (value, text) => { const o = document.createElement("option"); o.value = value; o.textContent = text; sel.appendChild(o); };
    add("", sc.current ? t("OBS で今選んでいるシーン(今: {scene})", { scene: sc.current }) : t("OBS で今選んでいるシーン"));
    for (const n of scenes) add(n, n);
    if (sc.selected && !scenes.includes(sc.selected)) add(sc.selected, sc.scenes == null ? sc.selected : t("{scene}(OBS に無い)", { scene: sc.selected }));
    sel.value = sc.selected || "";
  }
  sel.disabled = locked || !s.obs.connected;
  $("sceneHint").textContent = !s.obs.connected ? ""
    : !sc.selected ? t("録画を始めたときに OBS で選んでいるシーンを録画します。画面キャプチャ用などに別のシーンにしたままだと、そのシーンが録画されます")
    : missing ? t("OBS にシーン「{scene}」がありません。このままだと今のシーンで録画します", { scene: sc.selected })
    : t("録画の間だけ OBS のシーンを「{scene}」に切り替え、終わったら元に戻します", { scene: sc.selected });
}

async function poll() {
  clearTimeout(pollTimer);
  try {
    render(await api("/api/status"));
  } catch (e) {
    if (e.status === 401) { fatal(e.message); return; }
    $("sigTel").className = "sig bad";
    $("telText").textContent = t("アプリに接続できません(PCで起動しているか確認してください)");
    $("mainBtn").disabled = true;
  }
  const fast = latest && (latest.state === "starting" || latest.state === "stopping");
  pollTimer = setTimeout(poll, fast ? 300 : 1000);
}

function fatal(msg) {
  document.body.innerHTML = "";
  const p = document.createElement("p");
  p.className = "fatal";
  p.textContent = msg;
  document.body.appendChild(p);
}

$("mainBtn").addEventListener("click", async () => {
  if (!latest || busy) return;
  const btn = $("mainBtn");

  if (latest.state === "running") {
    // 誤タップ防止: 1回目で構え、3秒以内の2回目で停止
    if (!btn.classList.contains("armed")) {
      btn.classList.add("armed");
      $("mainSub").textContent = t("もう一度タップで停止");
      clearTimeout(armedTimer);
      armedTimer = setTimeout(() => { btn.classList.remove("armed"); if (latest) render(latest); }, 3000);
      return;
    }
    clearTimeout(armedTimer);
    btn.classList.remove("armed");
    busy = true; render({ ...latest, state: "stopping" });
    try { render(await api("/api/stop", {})); toast(t("記録を保存しました")); }
    catch (e) { toast(e.message); }
    finally { busy = false; poll(); }
    return;
  }

  if (latest.state === "idle") {
    busy = true; render({ ...latest, state: "starting" });
    try {
      const video = $("video").checked && latest.obs.connected;
      render(await api("/api/start", { video, label: $("label").value, quality: $("quality").value }));
      toast(video ? t("録画とログを開始しました") : t("ログを開始しました"));
    } catch (e) { toast(e.message); }
    finally { busy = false; poll(); }
  }
});

$("markerBtn").addEventListener("click", async () => {
  try {
    const r = await api("/api/marker", {});
    toast(t("マーカー {n} を記録", { n: r.count }));
    if (navigator.vibrate) navigator.vibrate(40);
  } catch (e) { toast(e.message); }
  poll();
});

$("video").addEventListener("change", () => latest && render(latest));
$("scene").addEventListener("change", async () => {
  try { render(await api("/api/scene", { scene: $("scene").value || null })); }
  catch (e) { toast(e.message); }
});
$("quality").addEventListener("change", async () => {
  try { render(await api("/api/quality", { preset: $("quality").value })); }
  catch (e) { toast(e.message); }
});

// PC(このPC上のブラウザ)で開いたときだけ QR を出す
async function setupConnect() {
  try {
    const info = await api("/api/connect-info");
    if (!info.isLocal) return;
    const sel = $("ipSel");
    if (!info.urls.length) {
      $("url").textContent = t("ネットワークのアドレスが見つかりません。PCがWi-Fiか有線LANにつながっているか確認してください。");
    }
    info.urls.forEach((u, i) => {
      const o = document.createElement("option");
      o.value = u.url; o.textContent = `${u.address}(${u.name})`;
      if (i === 0) o.selected = true;
      sel.appendChild(o);
    });
    const draw = () => {
      const url = sel.value;
      if (!url) return;
      const qr = qrcode(0, "M");
      qr.addData(url);
      qr.make();
      $("qr").innerHTML = qr.createSvgTag({ cellSize: 6, margin: 0, scalable: true });
      $("url").textContent = url;
    };
    sel.addEventListener("change", draw);
    draw();
    $("connect").hidden = false;
    $("main").classList.add("has-connect");
  } catch (e) { /* 合言葉エラーは poll 側で表示 */ }
}

if (!K) fatal(t("このページは合言葉付きのURLで開いてください。PCで起動したときに開いた画面のQRコードを読み取ってください。"));
else { setupConnect(); poll(); }
