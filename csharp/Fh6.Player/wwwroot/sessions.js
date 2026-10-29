// 記録の一覧とアーカイブ(docs/spec.md「記録の一覧とアーカイブ」)。一覧は C# が session.json だけから作って送る
"use strict";

const Sessions = {
  archive: false,   // アーカイブを出しているか
  items: [],
  selected: null,   // 選んでいる行(開く・名前を変える)
  checked: new Set(),   // チェックした行(移す)

  open() {
    this.selected = null;
    this.checked.clear();
    $("slRenameRow").hidden = true;
    $("slMsg").textContent = "";
    if (!$("sessionsDlg").open) $("sessionsDlg").showModal();
    this.request();
  },

  request() {
    $("slBody").innerHTML = `<tr><td colspan="9" class="muted">${escapeHtml(t("読み込み中…"))}</td></tr>`;
    host.postMessage({ type: "sessionList", archive: this.archive });
  },

  setArchive(on) {
    if (this.archive === on) return;
    this.archive = on;
    this.selected = null;
    this.checked.clear();
    $("slRenameRow").hidden = true;
    $("slMsg").textContent = "";
    this.request();
  },

  onList(m) {
    if (m.archive !== this.archive) return;   // 切り替えた後に届いた古い一覧
    this.items = m.items || [];
    $("slRoot").textContent = m.root ? (this.archive ? m.root + "\\archive" : m.root) : t("保存先がまだありません(「保存先を選ぶ」で選んでください)");
    $("slRoot").title = $("slRoot").textContent;
    for (const b of document.querySelectorAll("[data-sltab]")) b.classList.toggle("on", (b.dataset.sltab === "archive") === this.archive);
    $("slMove").textContent = this.archive ? t("保存先へ戻す") : t("アーカイブへ移す");
    // 消えた行の選択は外す
    const paths = new Set(this.items.map((s) => s.path));
    for (const p of [...this.checked]) if (!paths.has(p)) this.checked.delete(p);
    if (this.selected && !paths.has(this.selected)) this.selected = null;
    if (m.result) this.showResult(m.result);
    this.fillCars();
    this.render();
  },

  showResult(r) {
    const lines = [];
    if (r.kind === "rename") {
      if (r.error) lines.push(t("名前を変えられませんでした: {reason}", { reason: r.error }));
      else {
        lines.push(t("名前を変えました: {name}", { name: r.renamed }));
        const s = this.items.find((x) => x.name === r.renamed);
        if (s) this.selected = s.path;
      }
    } else {
      if (r.moved) lines.push(r.kind === "archive" ? t("{n} 本をアーカイブへ移しました", { n: r.moved }) : t("{n} 本を保存先へ戻しました", { n: r.moved }));
      for (const e of r.errors || []) lines.push(t("移せませんでした: {name}({reason})", { name: e.name, reason: e.reason }));
    }
    $("slMsg").textContent = lines.join("\n");
    $("slMsg").classList.toggle("error", !!(r.error || (r.errors && r.errors.length)));
  },

  carName(c) { return c.name || t("車種 #{n}", { n: c.ordinal }); },

  fillCars() {
    const sel = $("slCar"), keep = sel.value;
    const cars = new Map();
    for (const s of this.items) for (const c of s.cars || []) if (!cars.has(c.ordinal) || (!cars.get(c.ordinal).name && c.name)) cars.set(c.ordinal, c);
    const list = [...cars.values()].sort((a, b) => this.carName(a).localeCompare(this.carName(b)));
    sel.innerHTML = `<option value="">${escapeHtml(t("すべての車"))}</option>` +
      list.map((c) => `<option value="${c.ordinal}">${escapeHtml(this.carName(c))}</option>`).join("");
    sel.value = list.some((c) => String(c.ordinal) === keep) ? keep : "";
  },

  visible() {
    const q = $("slText").value.trim().toLowerCase();
    const car = $("slCar").value;
    return this.items.filter((s) => {
      if (car && !(s.cars || []).some((c) => String(c.ordinal) === car)) return false;
      if (!q) return true;
      const text = [s.name, ...(s.courses || []), ...(s.cars || []).map((c) => this.carName(c))].join(" ").toLowerCase();
      return text.includes(q);
    });
  },

  render() {
    const rows = this.visible();
    $("slCount").textContent = t("{n} 本", { n: rows.length }) + (rows.length !== this.items.length ? ` / ${this.items.length}` : "");
    if (!rows.length) {
      $("slBody").innerHTML = `<tr><td colspan="9" class="muted">${escapeHtml(this.items.length ? t("当てはまる記録がありません") : t("記録がありません"))}</td></tr>`;
    } else {
      $("slBody").innerHTML = rows.map((s) => {
        const cars = s.cars || [];
        const carText = cars.length === 0 ? "" : cars.length === 1 ? this.carName(cars[0]) : t("{name} ほか {n} 台", { name: this.carName(cars[0]), n: cars.length - 1 });
        const courses = s.courses || [];
        const courseText = courses.length <= 1 ? (courses[0] || "") : t("{name} ほか {n}", { name: courses[0], n: courses.length - 1 });
        const notes = [];
        if (s.open) notes.push(t("開いている"));
        if (s.error) notes.push(t("読めない"));
        else if (!s.complete) notes.push(t("記録中か、途中で終わった記録"));
        const title = [s.name, s.blocked ? t("移せない・名前を変えられない: {reason}", { reason: s.blocked }) : "", s.error || ""].filter((x) => x).join("\n");
        return `<tr data-path="${escapeHtml(s.path)}" class="${s.path === this.selected ? "sel" : ""}" title="${escapeHtml(title)}">
          <td><input type="checkbox" ${this.checked.has(s.path) ? "checked" : ""} ${s.blocked ? "disabled" : ""}></td>
          <td class="num">${escapeHtml(s.started || "")}</td>
          <td>${escapeHtml(s.rest || (s.stamp ? "" : s.name))}${notes.length ? ` <span class="muted">(${escapeHtml(notes.join("、"))})</span>` : ""}</td>
          <td>${escapeHtml(courseText)}</td>
          <td>${escapeHtml(carText)}</td>
          <td class="num">${s.races || ""}</td>
          <td class="num">${s.duration != null ? escapeHtml(fmtDuration(s.duration)) : ""}</td>
          <td>${s.video ? escapeHtml(t("あり")) : ""}</td>
          <td class="num">${escapeHtml(fmtBytes(s.bytes))}</td>
        </tr>`;
      }).join("");
    }
    const sel = this.items.find((s) => s.path === this.selected);
    $("slOpen").disabled = !sel;
    $("slRename").disabled = !sel || !!sel.blocked;
    $("slMove").disabled = this.checked.size === 0;
  },

  rowOf(e) { const tr = e.target.closest("tr[data-path]"); return tr ? tr.dataset.path : null; },

  openSelected() {
    if (!this.selected) return;
    $("sessionsDlg").close();
    host.postMessage({ type: "sessionOpen", path: this.selected });
  },

  startRename() {
    const s = this.items.find((x) => x.path === this.selected);
    if (!s || s.blocked) return;
    $("slRenameStamp").textContent = s.stamp ? s.stamp + "_" : "";
    $("slRenameText").value = s.rest;
    $("slRenameRow").hidden = false;
    $("slRenameText").focus();
    $("slRenameText").select();
  },

  doRename() {
    const s = this.items.find((x) => x.path === this.selected);
    if (!s) return;
    const rest = $("slRenameText").value.trim();
    if (/[\\/:*?"<>|]/.test(rest)) { $("slMsg").textContent = t("フォルダ名に使えない文字があります"); $("slMsg").classList.add("error"); return; }
    $("slRenameRow").hidden = true;
    host.postMessage({ type: "sessionRename", path: s.path, rest, archive: this.archive });
  },

  doMove() {
    const paths = [...this.checked];
    if (!paths.length) return;
    const q = this.archive
      ? t("{n} 本の記録を保存先へ戻しますか？", { n: paths.length })
      : t("{n} 本の記録を、動画も含めてアーカイブ(保存先の archive)へ移しますか？", { n: paths.length });
    if (!confirm(q)) return;
    host.postMessage({ type: "sessionMove", paths, archive: this.archive });
  },
};

/** 長さ(秒)を「1:02:03」か「12:34」に */
function fmtDuration(sec) {
  const s = Math.round(sec), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  const p = (n) => String(n).padStart(2, "0");
  return h ? `${h}:${p(m)}:${p(r)}` : `${m}:${p(r)}`;
}

/** 大きさ(バイト)を MB か GB に(小数はピリオド) */
function fmtBytes(b) {
  if (!b) return "";
  return b >= 1e9 ? `${(b / 1e9).toFixed(2)} GB` : `${Math.max(0.1, b / 1e6).toFixed(b >= 1e8 ? 0 : 1)} MB`;
}

$("open").onclick = () => Sessions.open();
$("slFolder").onclick = () => { $("sessionsDlg").close(); host.postMessage({ type: "open" }); };
$("slPickRoot").onclick = () => host.postMessage({ type: "sessionPickRoot", archive: Sessions.archive });
for (const b of document.querySelectorAll("[data-sltab]")) b.onclick = () => Sessions.setArchive(b.dataset.sltab === "archive");
$("slText").oninput = () => Sessions.render();
$("slCar").onchange = () => Sessions.render();
$("slBody").onclick = (e) => {
  const path = Sessions.rowOf(e);
  if (!path) return;
  if (e.target.matches("input[type=checkbox]")) {
    if (e.target.checked) Sessions.checked.add(path); else Sessions.checked.delete(path);
  } else Sessions.selected = path;
  Sessions.render();
};
$("slBody").ondblclick = (e) => {
  const path = Sessions.rowOf(e);
  if (!path || e.target.matches("input")) return;
  Sessions.selected = path;
  Sessions.openSelected();
};
$("slOpen").onclick = () => Sessions.openSelected();
$("slRename").onclick = () => Sessions.startRename();
$("slRenameOk").onclick = () => Sessions.doRename();
$("slRenameCancel").onclick = () => { $("slRenameRow").hidden = true; };
$("slRenameText").onkeydown = (e) => { if (e.key === "Enter") Sessions.doRename(); else if (e.key === "Escape") { e.preventDefault(); $("slRenameRow").hidden = true; } };
$("slMove").onclick = () => Sessions.doMove();
$("slClose").onclick = () => $("sessionsDlg").close();
