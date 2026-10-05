// グラフの段の組み合わせ(系列の定義、プリセット、設定パネル、描画)。
// 系列は「段」に自由に入れられる。段の中の1つ目の単位は左、2つ目は右に目盛りを付け、3つ目以降は目盛りなしで描く。
"use strict";

// ---- 系列と単位 ----

// 後から回転数や G を足すときは、ここに1行足す(C# の LoadedSession.BuildSeries にも列を足す)
// mark = 印(線ではない)、strip = 帯(路面)
const SERIES = {
  speed:   { label: t("速度"),       unit: "kmh",  stroke: "#4ea1ff" },
  gear:    { label: t("ギア"),       unit: "gear", stroke: "#b8bec7", stepped: true, width: 1 },
  accel:   { label: t("アクセル"),   unit: "pct",  stroke: "#4cd07d" },
  brake:   { label: t("ブレーキ"),   unit: "pct",  stroke: "#ff5d5d" },
  steer:   { label: t("操舵"),       unit: "spct", stroke: "#c792ea" },
  turn:    { label: t("旋回"),       unit: "turn", stroke: "#f5c542", width: 1.2 },
  slipF:   { label: t("前輪スリップ"), unit: "slip", stroke: "#ff9f43" },
  slipR:   { label: t("後輪スリップ"), unit: "slip", stroke: "#54a0ff" },
  grade:   { label: t("勾配"),       unit: "grade", stroke: "#a3e635" },
  elev:    { label: t("標高"),       unit: "m",    stroke: "#c3ccd8", width: 1.5 },
  surface: { label: t("路面"),       strip: true },
  contact: { label: t("接触の印"),   mark: true },
  jump:    { label: t("ジャンプ"),   mark: true },
};

const UNITS = {
  kmh:   { get label() { return Units.speedLabel(); } },
  pct:   { label: "%", range: [0, 100] },
  spct:  { label: t("% 正=右"), range: [-100, 100] },
  gear:  { label: t("ギア"), range: (u, min, max) => [0, Math.max(6, max || 0) + 0.5] },
  turn:  { label: t("1000/R 正=右"), range: (u, min, max) => { const a = Math.max(10, Math.abs(min || 0), Math.abs(max || 0)); return [-a, a]; } },
  slip:  { label: t("スリップ 1=限界"), range: (u, min, max) => [0, Math.max(1.5, max || 0)] },
  grade: { label: t("勾配 % 正=上り"), range: (u, min, max) => { const a = Math.max(5, Math.abs(min || 0), Math.abs(max || 0)); return [-a, a]; } },
  // 標高: 上下はコース(選んだレース。全体なら記録)の最低〜最高で固定する(Graphs.elevRange。app.js が決める)
  m:     { get label() { return t("標高 {unit}", { unit: Units.elevLabel() }); }, range: (u, min, max) => Graphs.elevRange ? Graphs.elevRange.map((v) => Units.elevOf(v)) : [min, max] },
};

// 路面の帯の色(C# の SurfaceKind の番号。6 = 空中、7 = 縁石)。+8 = 縁石の印(FH6 では立たない)、+16 = 水たまり
const SURFACE = [
  { name: t("舗装"), color: "#5b6270" },
  { name: t("雪など"), color: "#e8f1ff" },        // 0.10。サーキットの縁の外などにも出る(雪以外も含む)
  { name: t("ダート"), color: "#b7793b" },
  { name: t("ジャンプ台など"), color: "#9b59b6" }, // 0.20。街中のコースやグリッドにも出る
  { name: t("芝・荒れ地"), color: "#3fa34d" },
  { name: t("不明"), color: "#2b2f36" },
  { name: t("空中"), color: null },
  { name: t("縁石"), color: "#c9a227" },            // 0.24(サーキットで確認)
];
const WHEEL_NAMES = { fl: t("左前"), fr: t("右前"), rl: t("左後"), rr: t("右後") };

// 段の高さ(描画域の高さ px)
const HEIGHTS = { s: 70, m: 110, l: 150 };
const HEIGHT_NAMES = { s: t("小"), m: t("中"), l: t("大") };

const PRESETS = {
  p4: { name: t("4段(今までの形)"), lanes: [
    { h: "l", s: ["speed", "contact", "jump"] }, { h: "m", s: ["accel", "brake"] }, { h: "m", s: ["steer"] }, { h: "s", s: ["gear"] }] },
  p3: { name: t("3段"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "s", s: ["accel", "brake"] }, { h: "s", s: ["steer"] }] },
  p2: { name: t("2段"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "m", s: ["accel", "brake", "steer"] }] },
  p6: { name: t("詳細"), lanes: [
    { h: "l", s: ["speed", "gear", "contact", "jump"] }, { h: "s", s: ["accel", "brake"] }, { h: "m", s: ["steer", "turn"] },
    { h: "s", s: ["slipF", "slipR"] }, { h: "s", s: ["surface"] }, { h: "s", s: ["elev"] }] },
};
// 前の版の「詳細」(最後の段が勾配)。そのまま使っていたら、今の「詳細」(標高)に置き換える
const OLD_P6_LAST = ["grade"];
const DEFAULT_PRESET = "p3";

const MARK_COLORS = { start: "#36cfc9", lap: "#e6e6e6", contact: "#ff8a3d", jump: "#b388ff", cause: "#ff4d4f", skip: "#9aa1ab" };

// ---- 設定の保存(この PC のこのアプリの中だけ) ----

const LAYOUT_KEY = "heiso.graphLayout.v1";
const CUSTOM_KEY = "heiso.graphCustom.v1";

function cloneLanes(lanes) { return lanes.map((l) => ({ h: l.h, s: [...l.s] })); }

function validLanes(lanes) {
  if (!Array.isArray(lanes)) return false;
  const seen = new Set();
  for (const l of lanes) {
    if (!l || !HEIGHTS[l.h] || !Array.isArray(l.s)) return false;
    for (const id of l.s) { if (!SERIES[id] || seen.has(id)) return false; seen.add(id); }
  }
  return true;
}

function storageGet(key) {
  try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; }
}
function storageSet(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* 保存できなくても表示は続ける */ }
}

function loadLayout() {
  const saved = storageGet(LAYOUT_KEY);
  if (saved && validLanes(saved.lanes)) {
    if (saved.preset === "p6" && saved.lanes.length === PRESETS.p6.lanes.length
        && JSON.stringify(saved.lanes[saved.lanes.length - 1].s) === JSON.stringify(OLD_P6_LAST)) {
      return { preset: "p6", lanes: cloneLanes(PRESETS.p6.lanes) };
    }
    return { preset: saved.preset ?? null, lanes: cloneLanes(saved.lanes) };
  }
  return { preset: DEFAULT_PRESET, lanes: cloneLanes(PRESETS[DEFAULT_PRESET].lanes) };
}

function customLanes() {
  const c = storageGet(CUSTOM_KEY);
  return c && validLanes(c) ? c : null;
}

// ---- グラフ本体 ----

const Graphs = {
  layout: loadLayout(),
  charts: [],
  data: null,          // { t, speed, ... }
  marks: { starts: [], contacts: [] },
  showContacts: true,
  showLaunches: false, // 発進(レース以外のスタート)の印も出すか
  onSeek: null,        // (横軸の値) => void。横軸が距離なら app.js が時刻に直す
  onSeekTime: null,    // (sec) => void。印をクリックしたとき(印は元の時刻を持つ)
  onScale: null,       // () => void
  fmtX: (v) => fmtTime(v),   // 横軸の目盛りの書き方
  xToSec: (x) => x,          // 横軸の値 → 動画の秒
  pickMode: false,     // 手動補正で「グラフで指す」の間は、クリックでシークせず onPick を呼ぶ
  onPick: null,        // (sec) => void
  hoverContact: null,
  _syncing: false,
  elevRange: null,     // 標高の段の上下 [最低, 最高](m)
  _range: null,        // 最後に決めた表示範囲(uPlot は範囲の変更を後でまとめて反映するので、直後に読むと空のことがある)

  setData(data, marks) {
    this._range = null;
    this.data = data;
    this._conv = null;
    this.marks = marks || { starts: [], contacts: [] };
  },

  applyLanes(lanes, preset) {
    const range = this.xRange();
    this.layout = { preset, lanes: cloneLanes(lanes) };
    storageSet(LAYOUT_KEY, this.layout);
    this.build();
    if (range) this.setRange(range.min, range.max);
    renderSettings();
  },

  xTicks: null,        // (min, max, 本数) => { splits, fmt } か null。横軸が距離のとき app.js が Units.distTicks を入れる

  /** 系列の値(速度と標高は今の単位に直す。直したものは単位が変わるまで使い回す) */
  _col(id) {
    const conv = id === "speed" && Units.speed !== "kmh" ? (v) => Units.speedOf(v)
      : id === "elev" && Units.elev !== "m" ? (v) => Units.elevOf(v) : null;
    if (!conv) return this.data[id];
    const key = `${Units.speed} ${Units.elev}`;
    if (!this._conv || this._conv.key !== key) this._conv = { key, cols: {} };
    return this._conv.cols[id] ||= this.data[id].map((v) => (v == null ? null : conv(v)));
  },

  xRange() {
    // 最後に決めた範囲を優先する(uPlot は範囲の変更を後でまとめて反映するので、グラフから読むと直前の範囲が返ることがある。
    // 表示幅のボタンの直後にシークすると、古い範囲で「表示範囲の中」と判断して送らなかった)
    if (this._range) return this._range;
    const x = this.charts.length ? this.charts[0].scales.x : null;
    return x && x.min != null ? { min: x.min, max: x.max } : null;
  },

  setRange(min, max) {
    this._range = { min, max };
    if (this.charts.length) this.charts[0].setScale("x", { min, max });
  },

  destroy() {
    for (const c of this.charts) c.destroy();
    this.charts = [];
    document.getElementById("charts").innerHTML = "";
  },

  build() {
    this.destroy();
    if (!this.data) return;
    const box = document.getElementById("charts");
    const width = box.clientWidth;
    const lanes = this.layout.lanes;

    lanes.forEach((lane, li) => {
      const isLast = li === lanes.length - 1;
      const ids = lane.s.filter((id) => !SERIES[id].mark && !SERIES[id].strip);
      const flags = {
        contact: lane.s.includes("contact"), jump: lane.s.includes("jump"),
        surface: lane.s.includes("surface"), lines: ids.length > 0,
        slip: lane.s.includes("slipF") || lane.s.includes("slipR"),
      };
      const units = [...new Set(ids.map((id) => SERIES[id].unit))];

      const el = document.createElement("div");
      el.className = "chart";
      const title = document.createElement("div");
      title.className = "laneTitle";
      // 段の名前: 各系列の前にその線の色の印を付ける(凡例)
      if (lane.s.length) {
        title.innerHTML = lane.s.map((id) => {
          const color = SERIES[id].stroke || (id === "contact" ? MARK_COLORS.contact : id === "jump" ? MARK_COLORS.jump : null);
          return (color ? `<i class="sw" style="background:${color}"></i>` : "") + SERIES[id].label;
        }).join(" / ") + (units.length ? t("({units})", { units: units.map((u) => UNITS[u].label).join(" / ") }) : "");
      } else {
        title.textContent = t("(空の段)");
      }
      if (flags.surface) {
        // 路面の色の凡例と、帯の並び
        const legend = document.createElement("span");
        legend.className = "legend";
        legend.innerHTML = SURFACE.filter((k) => k.color).map((k) => `<i style="background:${k.color}"></i>${k.name}`).join("")
          + '<i class="puddle"></i>' + t("水 ・ 帯は上から 左前・右前・左後・右後");
        title.appendChild(legend);
      }
      el.appendChild(title);
      box.appendChild(el);

      const scales = { x: { time: false } };
      for (const u of units) scales[u] = UNITS[u].range ? { range: UNITS[u].range } : {};
      if (!units.length) scales.none = { range: [0, 1] };

      const axisStyle = { stroke: "#9aa1ab", grid: { stroke: "#2e323a" }, ticks: { stroke: "#2e323a" } };
      // 横軸の目盛り。距離でマイルのときは ft・mi の切りのよい所に(Graphs.xTicks。units.js)
      let ticks = null;
      const axes = [{
        ...axisStyle,
        size: isLast ? 30 : 4,
        splits: (u, ai, min, max, incr) => {
          ticks = Graphs.xTicks ? Graphs.xTicks(min, max, Math.max(2, Math.floor(u.bbox.width / devicePixelRatio / 90))) : null;
          if (ticks) return ticks.splits;
          const out = [];
          for (let v = Math.ceil(min / incr) * incr; v <= max + incr * 1e-6; v += incr) out.push(+v.toFixed(10));
          return out;
        },
        values: isLast ? (u, vals) => vals.map((v) => (ticks ? ticks.fmt(v) : Graphs.fmtX(v))) : () => [],
      }];
      units.forEach((u, i) => {
        if (i === 0) axes.push({ ...axisStyle, scale: u, size: 50 });
        else if (i === 1) axes.push({ ...axisStyle, scale: u, side: 1, size: 44, grid: { show: false } });
      });
      // 線の無い段(路面・印だけ)も、左の目盛りの幅を他の段と揃える(横位置がずれないように)
      if (!units.length) axes.push({ scale: "none", size: 50, values: () => [], grid: { show: false }, ticks: { show: false } });
      // 右側の目盛りが無い段も幅を揃える
      if (units.length < 2) axes.push({ scale: units[0] || "none", side: 1, size: 44, show: true, values: () => [], grid: { show: false }, ticks: { show: false } });

      const series = [{}, ...ids.map((id) => {
        const s = SERIES[id];
        return {
          label: s.label, stroke: s.stroke, width: s.width || 1.5, scale: s.unit, spanGaps: false,
          ...(s.stepped ? { paths: uPlot.paths.stepped({ align: 1 }) } : {}),
        };
      })];

      const self = this;
      const opts = {
        width,
        height: HEIGHTS[lane.h] + (isLast ? 30 : 4),
        legend: { show: false },
        cursor: { sync: { key: "heiso" }, drag: { x: true, y: false } },
        scales, axes, series,
        hooks: {
          setScale: [(u, key) => { if (key === "x") self._scaleChanged(u); }],
          draw: [(u) => self._drawMarks(u, flags)],
        },
      };
      const u = new uPlot(opts, [this.data.t, ...ids.map((id) => this._col(id))], el);

      const p = document.createElement("div");
      p.className = "pointer";
      u.over.appendChild(p);
      u._pointer = p;
      u._flags = flags;
      this._wireMouse(u);
      this.charts.push(u);
    });

    // 作った直後の最初の描画では uPlot がデータ全体の範囲にする(非同期)ので、次の描画フレームで覚えている範囲をかけ直す
    const built = this.charts;
    requestAnimationFrame(() => {
      if (this.charts !== built) return;
      for (const c of built) c._ready = true;
      if (this._range) {
        this._syncing = true;
        for (const c of built) c.setScale("x", { min: this._range.min, max: this._range.max });
        this._syncing = false;
      }
      if (this.onScale) this.onScale();
    });
  },

  _scaleChanged(src) {
    if (this._syncing) return;
    this._syncing = true;
    const { min, max } = src.scales.x;
    if (!src._ready) { this._syncing = false; return; }   // 最初の描画の自動の範囲は覚えない
    if (min != null) this._range = { min, max };
    for (const c of this.charts) if (c !== src) c.setScale("x", { min, max });
    this._syncing = false;
    if (this.onScale) this.onScale();
  },

  resize() {
    const w = document.getElementById("charts").clientWidth;
    if (!this.charts.length || this.charts[0].width === w) return;
    for (const c of this.charts) c.setSize({ width: w, height: c.height });
  },

  visibleStarts() {
    return this.marks.starts.filter((s) => s.race || this.showLaunches);
  },

  /** 表示範囲の行の番号 [i0, i1] */
  _visibleRows(u) {
    const t = this.data.t, { min, max } = u.scales.x;
    const find = (v) => { let lo = 0, hi = t.length; while (lo < hi) { const m = (lo + hi) >> 1; if (t[m] < v) lo = m + 1; else hi = m; } return lo; };
    return [Math.max(0, find(min) - 1), Math.min(t.length - 1, find(max) + 1)];
  },

  _drawMarks(u, flags) {
    const ctx = u.ctx, dpr = devicePixelRatio || 1;
    const { top, height, left, width } = u.bbox;
    const inside = (x) => x >= left && x <= left + width;
    const X = (t) => u.valToPos(t, "x", true);
    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top, width, height);
    ctx.clip();

    // やり直し区間(リワインド・チェックポイント逃しで捨てた走り): 全段に灰色の薄い帯
    ctx.fillStyle = MARK_COLORS.skip + "26";
    for (const s of this.marks.skips || []) {
      const x0 = X(s.t0), x1 = X(s.t1);
      if (x1 < left || x0 > left + width) continue;
      ctx.fillRect(x0, top, Math.max(1, x1 - x0), height);
    }

    // ジャンプ: 空中の区間を薄い帯で
    if (flags.jump) {
      ctx.fillStyle = MARK_COLORS.jump + "33";
      for (const j of this.marks.jumps || []) {
        const x0 = X(j.t0), x1 = X(j.t1);
        if (x1 < left || x0 > left + width) continue;
        ctx.fillRect(x0, top, Math.max(1, x1 - x0), height);
      }
    }

    // 路面の逸脱に続くスリップ: スリップの段に薄い赤の帯(逸脱の始まりから、スリップが限界を下回るまで)
    if (flags.slip) {
      ctx.fillStyle = MARK_COLORS.cause + "33";
      for (const e of this.marks.excursions || []) {
        const x0 = X(e.t0), x1 = X(excursionEnd(e));
        if (x1 < left || x0 > left + width) continue;
        ctx.fillRect(x0, top, Math.max(2, x1 - x0), height);
      }
    }

    // スリップの限界(1.0)の目安線
    if (u.scales.slip) {
      const y = u.valToPos(1, "slip", true);
      ctx.strokeStyle = "#ffffff55";
      ctx.lineWidth = 1 * dpr;
      ctx.setLineDash([6 * dpr, 4 * dpr]);
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(left + width, y); ctx.stroke();
      ctx.setLineDash([]);
    }

    // 路面: 4本の帯(線と同じ段なら下の 4 割に)
    if (flags.surface && this.data.surface) this._drawSurface(u, flags.lines ? top + height * 0.6 : top, flags.lines ? height * 0.4 : height);

    // スタート: 段の上端に小さな三角(レースは塗り、発進は線)
    for (const s of this.visibleStarts()) {
      const x = X(s.t);
      if (!inside(x)) continue;
      const r = 5 * dpr;
      ctx.strokeStyle = ctx.fillStyle = MARK_COLORS.start;
      ctx.lineWidth = 1.5 * dpr;
      ctx.beginPath();
      if (s.kind === "respawn") {
        // チェックポイント逃しの復帰は小さな丸
        ctx.arc(x, top + r, r * 0.8, 0, Math.PI * 2);
        ctx.stroke();
        continue;
      }
      ctx.moveTo(x - r, top);
      ctx.lineTo(x + r, top);
      ctx.lineTo(x, top + r * 1.6);
      ctx.closePath();
      if (s.race) ctx.fill(); else ctx.stroke();
    }

    // 周のスタートとゴール: 段の上端に白い菱形(ゴールは塗り)
    for (const l of this.marks.laps || []) {
      const x = X(l.t);
      if (!inside(x)) continue;
      const r = 4.5 * dpr;
      ctx.beginPath();
      ctx.moveTo(x, top);
      ctx.lineTo(x + r, top + r);
      ctx.lineTo(x, top + r * 2);
      ctx.lineTo(x - r, top + r);
      ctx.closePath();
      ctx.strokeStyle = ctx.fillStyle = MARK_COLORS.lap;
      ctx.lineWidth = 1.5 * dpr;
      if (l.finish) ctx.fill(); else ctx.stroke();
    }

    const contacts = this.showContacts ? this.marks.contacts : [];
    // 着地: ジャンプの段に、下端から伸びる短い線と丸
    if (flags.jump) {
      ctx.strokeStyle = ctx.fillStyle = MARK_COLORS.jump;
      ctx.lineWidth = 2 * dpr;
      for (const c of contacts.filter((c) => c.landing)) {
        const x = X(c.t);
        if (!inside(x)) continue;
        ctx.beginPath(); ctx.moveTo(x, top + height); ctx.lineTo(x, top + height - 14 * dpr); ctx.stroke();
        ctx.beginPath(); ctx.arc(x, top + height - 14 * dpr, 3.5 * dpr, 0, Math.PI * 2); ctx.fill();
      }
    }

    // 接触の印: 点線の縦線と上端の丸
    if (flags.contact) {
      const hits = contacts.filter((c) => !c.landing);
      ctx.strokeStyle = ctx.fillStyle = MARK_COLORS.contact;
      ctx.lineWidth = 1.5 * dpr;
      ctx.setLineDash([4 * dpr, 3 * dpr]);
      for (const c of hits) {
        const x = X(c.t);
        if (!inside(x)) continue;
        ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, top + height); ctx.stroke();
      }
      ctx.setLineDash([]);
      for (const c of hits) {
        const x = X(c.t);
        if (!inside(x)) continue;
        ctx.beginPath(); ctx.arc(x, top + 4 * dpr, 3.5 * dpr, 0, Math.PI * 2); ctx.fill();
      }
    }
    ctx.restore();
  },

  _drawSurface(u, y0, h) {
    const ctx = u.ctx, dpr = devicePixelRatio || 1;
    const t = this.data.t, sf = this.data.surface;
    const [i0, i1] = this._visibleRows(u);
    const rowH = h / 4;
    ["fl", "fr", "rl", "rr"].forEach((w, r) => {
      const codes = sf[w], y = y0 + r * rowH;
      // 同じ路面が続く所はまとめて描く
      let runStart = i0;
      for (let i = i0 + 1; i <= i1 + 1; i++) {
        if (i <= i1 && codes[i] === codes[runStart]) continue;
        const code = codes[runStart];
        if (code != null) {
          const x0 = u.valToPos(t[runStart], "x", true);
          const x1 = u.valToPos(t[Math.min(i, t.length - 1)], "x", true);
          const wpx = Math.max(1, x1 - x0);
          const kind = SURFACE[code & 7];
          if (kind && kind.color) { ctx.fillStyle = kind.color; ctx.fillRect(x0, y + 1 * dpr, wpx, rowH - 2 * dpr); }
          if (code & 8) { ctx.fillStyle = "#ffd400"; ctx.fillRect(x0, y + rowH - 3 * dpr, wpx, 2 * dpr); }
          if (code & 16) { ctx.fillStyle = "#3aa0ff"; ctx.fillRect(x0, y + 1 * dpr, wpx, 2 * dpr); }
        }
        runStart = i;
      }
    });

    // スリップ(やリワインド)につながった路面の逸脱: 段全体に薄い赤、外れた車輪の帯を赤い枠で
    const order = ["fl", "fr", "rl", "rr"];
    for (const e of this.marks.excursions || []) {
      const x0 = u.valToPos(e.t0, "x", true), x1 = u.valToPos(e.t1, "x", true);
      const { left, width } = u.bbox;
      if (x1 < left || x0 > left + width) continue;
      const w = Math.max(3 * dpr, x1 - x0);
      ctx.fillStyle = MARK_COLORS.cause + "2e";
      ctx.fillRect(x0, y0, w, h);
      ctx.strokeStyle = MARK_COLORS.cause;
      ctx.lineWidth = 2 * dpr;
      for (const wheel of e.wheels) {
        const r = order.indexOf(wheel);
        ctx.strokeRect(x0, y0 + r * rowH + 1 * dpr, w, rowH - 2 * dpr);
      }
    }
  },

  /** 画面上の x(px、描画域の左端から)の近くにある接触の印 */
  _contactNear(u, px) {
    if (!u._flags.contact || !this.showContacts) return null;
    let best = null, bestD = 7;
    for (const c of this.marks.contacts.filter((c) => !c.landing)) {
      const d = Math.abs(u.valToPos(c.t, "x") - px);
      if (d < bestD) { best = c; bestD = d; }
    }
    return best;
  },

  /** 画面上の x の近くにある周の印 */
  _lapNear(u, px) {
    let best = null, bestD = 7;
    for (const l of this.marks.laps || []) {
      const d = Math.abs(u.valToPos(l.t, "x") - px);
      if (d < bestD) { best = l; bestD = d; }
    }
    return best;
  },

  /** 画面上の x の位置が、路面の逸脱(〜続くスリップ)の帯の中ならその逸脱 */
  _excursionNear(u, px) {
    if (!u._flags.surface && !u._flags.slip) return null;
    const t = u.posToVal(px, "x"), pad = 4 / Math.max(1, u.bbox.width / (devicePixelRatio || 1)) * (u.scales.x.max - u.scales.x.min);
    return (this.marks.excursions || []).find((e) => t >= e.t0 - pad && t <= (u._flags.slip ? excursionEnd(e) : e.t1) + pad) || null;
  },

  /** 画面上の x の位置が空中の帯の中か、着地の近くならそのジャンプ */
  _jumpNear(u, px) {
    if (!u._flags.jump) return null;
    const t = u.posToVal(px, "x");
    return (this.marks.jumps || []).find((j) =>
      (t >= j.t0 && t <= j.t1) || Math.abs(u.valToPos(j.t1, "x") - px) < 7) || null;
  },

  _wireMouse(u) {
    const tip = document.getElementById("tip");
    let downX = 0;
    u.over.addEventListener("mousemove", (e) => {
      const c = this._contactNear(u, e.offsetX);
      const j = c ? null : this._jumpNear(u, e.offsetX);
      const lp = c || j ? null : this._lapNear(u, e.offsetX);
      const x = c || j || lp ? null : this._excursionNear(u, e.offsetX);
      this.hoverContact = c;
      u.over.style.cursor = c || j || lp || x ? "pointer" : "";
      if (c || j || lp || x) {
        tip.textContent = c
          ? t("接触 最大 {g}G / {before}→{after} {unit}", { g: c.g.toFixed(1), before: Units.speedOf(c.before).toFixed(0), after: Units.speedOf(c.after).toFixed(0), unit: Units.speedLabel() })
          : j ? t("ジャンプ {sec} 秒 / {speed} / 落差 {drop}", { sec: j.sec.toFixed(1), speed: Units.fmtSpeed(j.kmh), drop: Units.fmtElev(j.drop) })
          : lp ? lapText(lp)
          : excursionText(x);
        tip.style.left = `${e.clientX + 12}px`;
        tip.style.top = `${e.clientY + 12}px`;
        tip.hidden = false;
      } else {
        tip.hidden = true;
      }
    });
    u.over.addEventListener("mouseleave", () => { tip.hidden = true; this.hoverContact = null; });
    // Ctrl+ホイールでマウスの位置を中心に拡大・縮小。それ以外のホイールは動画のシーク(app.js)
    u.over.addEventListener("wheel", (e) => {
      e.preventDefault();
      if (e.ctrlKey) {
        const r = this.xRange();
        if (!r) return;
        const c = u.posToVal(e.offsetX, "x"), f = e.deltaY > 0 ? 1.25 : 0.8;
        const min = c - (c - r.min) * f, max = c + (r.max - c) * f;
        if (max - min < 0.5) return;   // 0.5 秒(距離なら 0.5m)より狭くしない
        this.setRange(min, max);
        if (this.onZoom) this.onZoom(max - min);
      } else if (this.onWheelSeek) {
        this.onWheelSeek(e);
      }
    }, { passive: false });
    // クリック(ドラッグでなければ)でシーク。接触の印の上なら、その1秒前へ。ドラッグは拡大、ダブルクリックで戻す
    u.over.addEventListener("mousedown", (e) => { downX = e.clientX; });
    u.over.addEventListener("mouseup", (e) => {
      if (Math.abs(e.clientX - downX) >= 3 || !this.onSeek) return;
      const c = this._contactNear(u, e.offsetX);
      const j = c ? null : this._jumpNear(u, e.offsetX);
      const lp = c || j ? null : this._lapNear(u, e.offsetX);
      const x = c || j || lp ? null : this._excursionNear(u, e.offsetX);
      // 印の時刻(横軸が距離のときは、印が元の時刻を src_ に持っている)
      const mt = (m, key) => m["src_" + key] ?? m[key];
      if (this.pickMode && this.onPick) {
        // 手動補正: 接触・着地・周の印の近くなら、その瞬間に合わせる
        if (c) this.onPick(mt(c, "t"));
        else if (j) this.onPick(mt(j, "t1"));
        else if (lp) this.onPick(mt(lp, "t"));
        else if (u.cursor.left >= 0) this.onPick(this.xToSec(u.posToVal(u.cursor.left, "x")));
        return;
      }
      if (c) this.onSeekTime(mt(c, "t") - 1);
      else if (j) this.onSeekTime(mt(j, "t0") - 1);
      else if (lp) this.onSeekTime(mt(lp, "t") - 1);
      else if (x) this.onSeekTime(mt(x, "t0") - 1);
      else if (u.cursor.left >= 0) this.onSeek(u.posToVal(u.cursor.left, "x"));
    });
  },

  redrawMarks() {
    for (const c of this.charts) c.redraw(false, false);
  },
};

// ---- 路面の逸脱の説明 ----

/** 逸脱に続くスリップの帯の終わり(スリップが限界を下回るまで。無ければ逸脱の終わりか 2 秒) */
function excursionEnd(e) {
  return Math.max(e.t1, e.slipEndT ?? e.slipT ?? e.t0 + 2);
}

function fmtLap(sec) {
  const m = Math.floor(sec / 60);
  return m > 0 ? `${m}:${(sec - m * 60).toFixed(2).padStart(5, "0")}` : t("{sec} 秒", { sec: sec.toFixed(2) });
}

function lapText(l) {
  const p = { lap: l.lap - 1, time: l.prev != null ? fmtLap(l.prev) : "", how: l.byPosition ? t("、スタート地点の通過から") : "" };
  const prev = l.prev == null ? "" : l.finish ? t("(最後の周 {lap} 周目 {time}{how})", p) : t("({lap} 周目 {time}{how})", p);
  return l.finish ? t("ゴール{prev}", { prev }) : t("{lap} 周目のスタート{prev}", { lap: l.lap, prev });
}

function excursionText(e) {
  const wheels = e.wheels.map((w) => WHEEL_NAMES[w]).join(t("・"));
  const surfaces = [...new Set(e.surfaces.map((k) => SURFACE[k] ? SURFACE[k].name : t("不明")))].join(t("・"));
  let text = t("{wheels}が{surfaces}に落ちた", { wheels, surfaces });
  if (e.slipT != null) {
    const d = e.slipT - e.t0;
    text += d < 0.15 ? t(" → ほぼ同時にスリップ {slip}", { slip: e.slipPeak.toFixed(2) }) : t(" → {sec} 秒後にスリップ {slip}", { sec: d.toFixed(1), slip: e.slipPeak.toFixed(2) });
  }
  if (e.gap) text += t(" → リワインドなど(停止区間)");
  return text;
}

// ---- 設定パネル ----

function renderSettings() {
  const panel = document.getElementById("graphSettings");
  if (!document.getElementById("settingsDlg").open) return;   // 設定のダイアログを開いているときだけ作る
  const layout = Graphs.layout;
  const custom = customLanes();
  const used = new Set(layout.lanes.flatMap((l) => l.s));

  const presetOpts = Object.entries(PRESETS).map(([k, p]) =>
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
    else if (PRESETS[v]) Graphs.applyLanes(PRESETS[v].lanes, v);
  };
  panel.querySelector("#saveCustom").onclick = () => {
    storageSet(CUSTOM_KEY, Graphs.layout.lanes);
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
