// グラフの描画(uPlot)。段の組み合わせは graphseries.js、設定パネルは graphsettings.js。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。graphseries.js → graphs.js → graphsettings.js)。
"use strict";

// ---- グラフ本体 ----

const Graphs = {
  mode: "vis",         // "vis"(見える化)/ "perf"(性能)。段の組み合わせはモードごとに覚える
  layout: loadLayout(),
  charts: [],
  data: null,          // { t, speed, ... }
  marks: { starts: [], contacts: [] },
  showContacts: true,
  showLaunches: false, // 発進(レース以外のスタート)の印も出すか
  onSeek: null,        // (横軸の値) => void。横軸が距離なら app.js が時刻に直す
  onSeekTime: null,    // (sec) => void。印をクリックしたとき(印は元の時刻を持つ)
  // ドラッグでグラフを左右にずらす(動画編集ソフトのように、再生位置の線は画面の同じ所に止めたまま)。app.js が渡す:
  getPointerX: null,   // () => 今の再生位置(横軸の値)
  onDragStart: null,   // () => void。ずらし始めたとき(再生を止める)
  onDragSeek: null,    // (横軸の値) => void。ずらした分だけ再生位置を動かす(グラフの表示範囲は送らない)
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

  /** モードを切り替える(そのモードで最後に使った段の組み合わせにする) */
  setMode(mode) {
    if (mode === this.mode) return;
    this.mode = mode;
    const range = this.xRange();
    this.layout = loadLayout(mode);
    this.build();
    if (range) this.setRange(range.min, range.max);
    renderSettings();
  },

  applyLanes(lanes, preset) {
    const range = this.xRange();
    this.layout = { preset, lanes: cloneLanes(lanes) };
    (settings.layouts ||= {})[this.mode] = { preset, lanes: cloneLanes(lanes) };
    saveSettings();
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
        shift: lane.s.includes("shift"), gain: lane.s.includes("gain"),
        counter: lane.s.includes("counter"),
      };
      const units = [...new Set(ids.map((id) => SERIES[id].unit))];

      const el = document.createElement("div");
      el.className = "chart";
      const title = document.createElement("div");
      title.className = "laneTitle";
      // 段の名前: 各系列の前にその線の色の印を付ける(凡例)
      if (lane.s.length) {
        title.innerHTML = lane.s.map((id) => {
          const color = SERIES[id].stroke || (id === "contact" ? MARK_COLORS.contact : id === "jump" ? MARK_COLORS.jump : id === "shift" ? SHIFT_COLORS.ok
            : id === "counter" ? MARK_COLORS.counter : null);
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
          label: s.label, stroke: s.stroke, width: s.width || 1.5, scale: s.unit, spanGaps: false, ...(s.dash ? { dash: s.dash } : {}),
          ...(s.stepped ? { paths: uPlot.paths.stepped({ align: 1 }) } : {}),
          // オーバー / アンダー: 0 までを塗り、0 より上を赤、下を青にする(線も同じ色の切り替え)
          ...(s.balance ? { fill: (u) => balanceGradient(u, s.unit, 0.45), stroke: (u) => balanceGradient(u, s.unit, 1), fillTo: 0 } : {}),
        };
      })];

      const self = this;
      const opts = {
        width,
        height: HEIGHTS[lane.h] + (isLast ? 30 : 4),
        legend: { show: false },
        // ドラッグはグラフをずらす(_wireMouse)。範囲を選んで拡大するのは Shift+ドラッグだけ(uPlot の選択に渡す)。ダブルクリックで全体に戻す
        cursor: {
          sync: { key: "heiso" }, drag: { x: true, y: false },
          bind: { mousedown: (u, targ, handler) => (e) => { if (e.shiftKey) handler(e); return null; } },
        },
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
      u._isLast = isLast;
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

    // 路面の逸脱に続くスリップ: スリップの段に薄い赤の帯(逸脱の始まりから、スリップが限界を下回るまで。終わりは C# の SlipBand)
    if (flags.slip) {
      ctx.fillStyle = MARK_COLORS.cause + "33";
      for (const e of this.marks.excursions || []) {
        const x0 = X(e.t0), x1 = X(slipBandEnd(e));
        if (x1 < left || x0 > left + width) continue;
        ctx.fillRect(x0, top, Math.max(2, x1 - x0), height);
      }
    }

    // 変速の得失の段: アクセルが全開でない区間を灰色の帯で、0 の線。
    // 全開なのに得失が無い所(パワーカーブや次のギアの比が無い)は帯にせず、線を切るだけ(理由は読み取り値に出す)
    if (flags.gain && this.data.gain) {
      const d = this.data, [i0, i1] = this._visibleRows(u);
      ctx.fillStyle = "#3a3f4999";
      let run = -1;
      for (let i = i0; i <= i1 + 1; i++) {
        const partial = i <= i1 && d.gain[i] == null && d.gear[i] != null && d.full[i] === 0;
        if (partial && run < 0) run = i;
        if (!partial && run >= 0) {
          const x0 = X(d.t[run]), x1 = X(d.t[Math.min(i, d.t.length - 1)]);
          ctx.fillRect(x0, top, Math.max(1, x1 - x0), height);
          run = -1;
        }
      }
      if (u.scales.gain) {
        const y = u.valToPos(0, "gain", true);
        ctx.strokeStyle = "#ffffff44";
        ctx.lineWidth = 1 * dpr;
        ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(left + width, y); ctx.stroke();
      }
    }

    // オーバー / アンダー: 0 の線
    if (u.scales.bal) {
      const y = u.valToPos(0, "bal", true);
      ctx.strokeStyle = "#ffffff55";
      ctx.lineWidth = 1 * dpr;
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(left + width, y); ctx.stroke();
    }

    // カウンター: ハンドルと車の回る向きが逆の所を、段の下に帯で
    if (flags.counter && this.data.counter) {
      const c = this.data.counter, tt = this.data.t, h = 6 * dpr;
      ctx.fillStyle = MARK_COLORS.counter;
      for (let i = 0; i < c.length; i++) {
        if (c[i] !== 1) continue;
        let j = i;
        while (j + 1 < c.length && c[j + 1] === 1) j++;
        const x0 = X(tt[i]), x1 = Math.max(X(tt[j]), x0 + 2 * dpr);
        if (x1 >= left && x0 <= left + width) ctx.fillRect(x0, top + height - h, x1 - x0, h);
        i = j;
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

    // 変速の点: 全開のシフトアップ。得失の段なら得失の高さに、それ以外の段は上端に。色は早い・適正・遅い
    if (flags.shift) {
      const onGain = !!u.scales.gain;
      for (const s of this.marks.shifts || []) {
        const x = X(s.t);
        if (!inside(x)) continue;
        const sc = onGain ? u.scales.gain : null;
        const y = sc && s.gain != null ? u.valToPos(Math.max(sc.min, Math.min(sc.max, s.gain)), "gain", true) : top + 6 * dpr;
        ctx.beginPath(); ctx.arc(x, y, 5 * dpr, 0, Math.PI * 2);
        if (s.auto) {
          // 自動変速らしいレースの変速は、中を抜いた輪(運転の良し悪しではないので参考)
          ctx.strokeStyle = SHIFT_COLORS[s.verdict || "none"];
          ctx.lineWidth = 2 * dpr;
          ctx.stroke();
        } else {
          ctx.fillStyle = SHIFT_COLORS[s.verdict || "none"];
          ctx.strokeStyle = "#16181c";
          ctx.lineWidth = 1.5 * dpr;
          ctx.fill(); ctx.stroke();
        }
      }
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

  /** 画面上の x の近くにある変速の点 */
  _shiftNear(u, px) {
    if (!u._flags.shift) return null;
    let best = null, bestD = 7;
    for (const s of this.marks.shifts || []) {
      const d = Math.abs(u.valToPos(s.t, "x") - px);
      if (d < bestD) { best = s; bestD = d; }
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
    return (this.marks.excursions || []).find((e) => t >= e.t0 - pad && t <= (u._flags.slip ? slipBandEnd(e) : e.t1) + pad) || null;
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
      const sh = c ? null : this._shiftNear(u, e.offsetX);
      const j = c || sh ? null : this._jumpNear(u, e.offsetX);
      const lp = c || sh || j ? null : this._lapNear(u, e.offsetX);
      const x = c || sh || j || lp ? null : this._excursionNear(u, e.offsetX);
      this.hoverContact = c;
      u.over.style.cursor = c || sh || j || lp || x ? "pointer" : "";
      if (c || sh || j || lp || x) {
        tip.textContent = sh ? shiftText(sh) : c
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
    // ドラッグ(Shift なし): 再生位置の線を画面の同じ所に止めたまま、グラフを左右にずらす。ずらした分だけ再生位置が動く
    // (右へずらすと前の時刻が見え、再生位置は戻る)。3px 未満はクリック(下の mouseup でシーク)
    u.over.addEventListener("mousedown", (e) => {
      if (e.button !== 0 || e.shiftKey) return;
      const r = this.xRange(), p0 = this.getPointerX ? this.getPointerX() : null;
      if (!r || p0 == null || !isFinite(p0)) return;
      const startX = e.clientX;
      let panning = false, frame = 0, lastDx = 0;
      const apply = () => { frame = 0; this._panTo(r, p0, lastDx, u); };
      const move = (ev) => {
        lastDx = ev.clientX - startX;
        if (!panning) {
          if (Math.abs(lastDx) < 3) return;
          panning = true;
          if (this.onDragStart) this.onDragStart();
          document.body.style.cursor = "grabbing";
        }
        if (!frame) frame = requestAnimationFrame(apply);
      };
      const up = () => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", up);
        if (!panning) return;
        if (frame) { cancelAnimationFrame(frame); apply(); }
        document.body.style.cursor = "";
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", up);
    });
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
      } else if (!e.shiftKey && Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
        // タッチパッドの横の 2 本指: ドラッグと同じく、線を止めたままグラフをずらす(指を左へ動かすと先へ進む)。
        // Shift+ホイールは Chromium では横の動きとして届くので、コマ送り(onWheelSeek)のまま
        this._panBy(-e.deltaX * (e.deltaMode === 1 ? 16 : 1), u);
      } else if (this.onWheelSeek) {
        this.onWheelSeek(e);
      }
    }, { passive: false });
    // 最後の段の下の目盛り(時間・距離): クリックとドラッグで、線がマウスについて動く(動画編集ソフトの時間の目盛りと同じ。右へ動かすと進む)
    if (u._isLast) this._wireRuler(u);
    // クリック(ドラッグでなければ)でシーク。接触の印の上なら、その1秒前へ。ドラッグはずらす(上)、Shift+ドラッグは拡大、ダブルクリックで戻す
    u.over.addEventListener("mousedown", (e) => { downX = e.clientX; });
    u.over.addEventListener("mouseup", (e) => {
      if (Math.abs(e.clientX - downX) >= 3 || !this.onSeek) return;
      const c = this._contactNear(u, e.offsetX);
      const sh = c ? null : this._shiftNear(u, e.offsetX);
      const j = c || sh ? null : this._jumpNear(u, e.offsetX);
      const lp = c || sh || j ? null : this._lapNear(u, e.offsetX);
      const x = c || sh || j || lp ? null : this._excursionNear(u, e.offsetX);
      // 印の時刻(横軸が距離のときは、印が元の時刻を src_ に持っている)
      const mt = (m, key) => m["src_" + key] ?? m[key];
      if (this.pickMode && this.onPick) {
        // 手動補正: 接触・着地・周の印の近くなら、その瞬間に合わせる
        if (c) this.onPick(mt(c, "t"));
        else if (sh) this.onPick(mt(sh, "t"));
        else if (j) this.onPick(mt(j, "t1"));
        else if (lp) this.onPick(mt(lp, "t"));
        else this.onPick(this.xToSec(u.posToVal(e.offsetX, "x")));
        return;
      }
      if (c) this.onSeekTime(mt(c, "t") - 1);
      else if (sh) this.onSeekTime(mt(sh, "t") - 1);
      else if (j) this.onSeekTime(mt(j, "t0") - 1);
      else if (lp) this.onSeekTime(mt(lp, "t") - 1);
      else if (x) this.onSeekTime(mt(x, "t0") - 1);
      else this.onSeek(u.posToVal(e.offsetX, "x"));   // クリックした所(カーソルの位置は直前の mousemove のものなので使わない)
    });
  },

  /** 表示範囲 r・再生位置 p0 の状態から、dx px ずらす(線は画面の同じ所のまま、再生位置も同じだけ動く) */
  _panTo(r, p0, dx, u) {
    const dv = dx * (r.max - r.min) / Math.max(1, u.over.clientWidth);
    if (this.onDragSeek) this.onDragSeek(p0 - dv);   // 先に再生位置を動かす(範囲を変えたときの線の描き直しが、新しい位置になるように)
    this.setRange(r.min - dv, r.max - dv);
  },

  /** 今の状態から dx px ずらす(タッチパッドの横の動き。動くたびに届くので、そのつど今の範囲から) */
  _panBy(dx, u) {
    const r = this.xRange(), p0 = this.getPointerX ? this.getPointerX() : null;
    if (!r || p0 == null || !isFinite(p0) || !dx) return;
    if (this.onDragStart) this.onDragStart();
    this._panTo(r, p0, dx, u);
  },

  /** 最後の段の下の目盛りの上: マウスのある所へ線と再生位置を動かす(押している間はついて動く)。表示範囲は動かさない */
  _wireRuler(u) {
    const inRuler = (e) => {
      const o = u.over.getBoundingClientRect();
      return e.clientY > o.bottom && e.clientX >= o.left && e.clientX <= o.right;
    };
    const seekAt = (clientX) => {
      const o = u.over.getBoundingClientRect(), x = Math.min(o.width, Math.max(0, clientX - o.left));
      if (this.onDragSeek) this.onDragSeek(u.posToVal(x, "x"));
    };
    u.root.addEventListener("mousemove", (e) => { if (!e.buttons) u.root.style.cursor = inRuler(e) ? "ew-resize" : ""; });
    u.root.addEventListener("mousedown", (e) => {
      if (e.button !== 0 || !inRuler(e)) return;
      e.preventDefault();
      if (this.onDragStart) this.onDragStart();
      seekAt(e.clientX);
      let frame = 0, lastX = e.clientX;
      const move = (ev) => { lastX = ev.clientX; if (!frame) frame = requestAnimationFrame(() => { frame = 0; seekAt(lastX); }); };
      const up = () => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", up);
        if (frame) { cancelAnimationFrame(frame); seekAt(lastX); }
        document.body.style.cursor = "";
      };
      document.body.style.cursor = "ew-resize";
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", up);
    });
  },

  redrawMarks() {
    for (const c of this.charts) c.redraw(false, false);
  },
};

// ---- 路面の逸脱の説明 ----

/** 逸脱に続くスリップの帯の終わり(C# が出した slipBandT1。横軸が距離で、その時刻がやり直した走りの中なら逸脱の終わり) */
function slipBandEnd(e) {
  return e.slipBandT1 ?? e.t1;
}

/** 変速の点の説明: 「3→4 速 6,890 rpm ・ 得失 -5.2%(早い)」 */
function shiftText(s) {
  const rpm = Math.round(s.rpm).toLocaleString("en-US");
  const text = s.gain == null
    ? t("{from}→{to} 速 {rpm} rpm(得失を出せない)", { from: s.from, to: s.from + 1, rpm })
    : t("{from}→{to} 速 {rpm} rpm ・ 得失 {gain}%({verdict})", {
      from: s.from, to: s.from + 1, rpm, gain: (s.gain > 0 ? "+" : "") + s.gain.toFixed(1), verdict: SHIFT_NAMES[s.verdict],
    });
  // リミッターに張り付いてから上げたなら、その時間(張り付いている間は、そのギアでは速度が上がらない)
  const lim = s.limiterSec >= 0.1 ? t(" ・ リミッター {sec} 秒", { sec: s.limiterSec.toFixed(1) }) : "";
  return text + lim + (s.auto ? t(" ・ 自動変速らしい走り(参考)") : "");
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
