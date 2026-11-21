// グラフの設定パネル(設定のダイアログの「グラフの段」)。段の組み合わせを変えると Graphs.applyLanes に渡す。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。graphseries.js → graphs.js → graphsettings.js)。
"use strict";

// ---- 設定パネル ----

function renderSettings() {
  const panel = document.getElementById("graphSettings");
  if (!document.getElementById("settingsDlg").open) return;   // 設定のダイアログを開いているときだけ作る
  const layout = Graphs.layout;
  const mode = Graphs.mode;
  const custom = customLanes(mode);
  const used = new Set(layout.lanes.flatMap((l) => l.s));

  const presetOpts = Object.entries(presetsOf(mode)).map(([k, p]) =>
    `<option value="${k}"${layout.preset === k ? " selected" : ""}>${p.name}</option>`).join("")
    + (custom ? `<option value="custom"${layout.preset === "custom" ? " selected" : ""}>${t("自分の設定")}</option>` : "")
    + (layout.preset == null ? `<option value="" selected>${t("(変更あり)")}</option>` : "");

  const lanesHtml = layout.lanes.map((lane, i) => {
    const chips = lane.s.map((id) =>
      `<span class="chipItem" draggable="true" data-series="${id}" data-lane="${i}">${SERIES[id].label}<button class="x" data-remove="${id}" title="${t("外す")}">×</button></span>`).join("");
    const unused = Object.keys(SERIES).filter((id) => !used.has(id));
    const add = unused.length
      ? `<select class="add" data-lane="${i}"><option value="">${t("+ 追加")}</option>${unused.map((id) => `<option value="${id}">${SERIES[id].label}</option>`).join("")}</select>`
      : "";
    const heights = Object.keys(HEIGHTS).map((h) => `<option value="${h}"${lane.h === h ? " selected" : ""}>${HEIGHT_NAMES[h]}</option>`).join("");
    return `<div class="laneRow" data-lane="${i}">
      <span class="laneNo">${t("段{n}", { n: i + 1 })}</span>
      <select class="height" data-lane="${i}" title="${t("高さ")}">${heights}</select>
      <span class="chipsBox">${chips || `<span class="muted">${t("(空)")}</span>`}</span>
      ${add}
      <span class="laneOps">
        <button data-up="${i}" title="${t("上へ")}"${i === 0 ? " disabled" : ""}>▲</button>
        <button data-down="${i}" title="${t("下へ")}"${i === layout.lanes.length - 1 ? " disabled" : ""}>▼</button>
        <button data-del="${i}" title="${t("段を消す")}">${t("削除")}</button>
      </span>
    </div>`;
  }).join("");

  panel.innerHTML = `
    <div class="settingsHead">
      <span>${mode === "perf" ? t("性能モードの段") : t("見える化の段")}</span>
      <label>${t("プリセット")} <select id="presetSel">${presetOpts}</select></label>
      <button id="saveCustom">${t("自分の設定として保存")}</button>
      <span class="muted">${t("札をドラッグして別の段へ移せます。変更はすぐ反映され、次回も使います。")}</span>
    </div>
    ${lanesHtml}
    <button id="addLane">${t("+ 段を追加")}</button>`;

  const edit = (fn) => {
    const lanes = cloneLanes(Graphs.layout.lanes);
    fn(lanes);
    Graphs.applyLanes(lanes, null);
  };

  panel.querySelector("#presetSel").onchange = (e) => {
    const v = e.target.value;
    if (v === "custom" && custom) Graphs.applyLanes(custom, "custom");
    else if (presetsOf(mode)[v]) Graphs.applyLanes(presetsOf(mode)[v].lanes, v);
  };
  panel.querySelector("#saveCustom").onclick = () => {
    (settings.customLanes ||= {})[mode] = cloneLanes(Graphs.layout.lanes);
    saveSettings();
    Graphs.applyLanes(Graphs.layout.lanes, "custom");
  };
  panel.querySelector("#addLane").onclick = () => edit((l) => l.push({ h: "m", s: [] }));
  panel.querySelectorAll("[data-remove]").forEach((b) => b.onclick = () =>
    edit((l) => { for (const lane of l) lane.s = lane.s.filter((id) => id !== b.dataset.remove); }));
  panel.querySelectorAll("select.add").forEach((s) => s.onchange = () => {
    if (s.value) edit((l) => l[Number(s.dataset.lane)].s.push(s.value));
  });
  panel.querySelectorAll("select.height").forEach((s) => s.onchange = () =>
    edit((l) => { l[Number(s.dataset.lane)].h = s.value; }));
  panel.querySelectorAll("[data-up]").forEach((b) => b.onclick = () => edit((l) => {
    const i = Number(b.dataset.up); [l[i - 1], l[i]] = [l[i], l[i - 1]];
  }));
  panel.querySelectorAll("[data-down]").forEach((b) => b.onclick = () => edit((l) => {
    const i = Number(b.dataset.down); [l[i], l[i + 1]] = [l[i + 1], l[i]];
  }));
  panel.querySelectorAll("[data-del]").forEach((b) => b.onclick = () => edit((l) => l.splice(Number(b.dataset.del), 1)));

  // 札のドラッグ
  panel.querySelectorAll(".chipItem").forEach((c) => {
    c.addEventListener("dragstart", (e) => { e.dataTransfer.setData("text/plain", c.dataset.series); e.dataTransfer.effectAllowed = "move"; });
  });
  panel.querySelectorAll(".laneRow").forEach((row) => {
    row.addEventListener("dragover", (e) => { e.preventDefault(); row.classList.add("drop"); });
    row.addEventListener("dragleave", () => row.classList.remove("drop"));
    row.addEventListener("drop", (e) => {
      e.preventDefault();
      const id = e.dataTransfer.getData("text/plain");
      if (!SERIES[id]) return;
      edit((l) => {
        for (const lane of l) lane.s = lane.s.filter((x) => x !== id);
        l[Number(row.dataset.lane)].s.push(id);
      });
    });
  });
}
