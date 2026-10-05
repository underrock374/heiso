// コース図: 真上から見た走行軌跡と今の位置。
// x = 東を右、z = 北を上に描く。FH6 の座標は左手系(x = 東、y = 上、z = 北)なので、この向きで左右は正しい
// (docs/telemetry-field-notes.md「座標系は左手系」)。
// 拡大(zoom に m を入れる)すると、今の位置を真ん中にして追従する(北が上のまま)。
// 色: やり直し区間(リワインド・チェックポイント逃しで捨てた走り)は暗い赤の点線、それ以外は灰色。
// 今いる周(周の無いレースはレース全体)を青、その中でグラフに出している範囲をさらに明るく(周回で重なる線のうち、今の周だけ分かるように)。
// 路面の色のモード(colorMode = "surface")では、青の代わりに路面の色で塗る(ほかの周は薄く、グラフの範囲は白い線を重ねる)。
"use strict";

// ---- 路面の色(2D と 3D で使う) ----
// 4 輪の路面が同じ行だけその路面の色、違う行は「混在」。空中の間は直前の色を続け、ごく短い(3 行未満の)ちらつきはならす
const COURSE_SURFACE = [
  { name: t("舗装"), rgb: [0.48, 0.51, 0.57] },
  { name: t("雪など"), rgb: [0.91, 0.95, 1.0] },
  { name: t("ダート"), rgb: [0.72, 0.47, 0.23] },
  { name: t("ジャンプ台など"), rgb: [0.61, 0.35, 0.71] },
  { name: t("芝・荒れ地"), rgb: [0.25, 0.64, 0.30] },
  { name: t("不明"), rgb: [0.33, 0.36, 0.40] },
  null,                                             // 空中
  { name: t("縁石"), rgb: [0.79, 0.64, 0.15] },
];
const COURSE_MIXED = { name: t("混在(4 輪の路面が違う)"), rgb: [0.85, 0.27, 0.94] };

/** 系列の位置ごとの路面の色 [r, g, b](0〜1)。停止区間などは null */
function buildSurfaceColors(data) {
  const sf = data.surface, n = data.t.length;
  const kind = new Int16Array(n).fill(-1);      // 0〜7 = 路面、8 = 混在、-1 = 無し、-2 = 空中
  for (let i = 0; i < n; i++) {
    const c = [sf.fl[i], sf.fr[i], sf.rl[i], sf.rr[i]];
    if (c.some((v) => v == null)) continue;
    const k = c.map((v) => v & 7);
    if (k.some((v) => v === 6)) { kind[i] = -2; continue; }
    kind[i] = k.every((v) => v === k[0]) ? k[0] : 8;
  }
  // 空中は直前の路面を続ける
  let prev = -1;
  for (let i = 0; i < n; i++) {
    if (kind[i] === -2) kind[i] = prev;
    else if (kind[i] >= 0) prev = kind[i];
    else prev = -1;
  }
  // 3 行未満の短い区間は、前の路面にならす
  let runStart = 0;
  for (let i = 1; i <= n; i++) {
    if (i < n && kind[i] === kind[runStart]) continue;
    if (i - runStart < 3 && runStart > 0 && kind[runStart] >= 0 && kind[runStart - 1] >= 0)
      for (let j = runStart; j < i; j++) kind[j] = kind[runStart - 1];
    runStart = i;
  }
  return Array.from(kind, (k) => (k < 0 ? null : k === 8 ? COURSE_MIXED.rgb : COURSE_SURFACE[k].rgb));
}

/** 1 サンプルでこれを超えて動いたら、つながっていない(ファストトラベル・復帰・リワインド) */
const COURSE_JUMP_M = 50;

/**
 * rows の k 番目の行の進む向き(x = 東、z = 北の平面の単位ベクトル [dx, dz])。
 * 前後に span m ずつ離れた点を結んだ方向にする(隣の点の方向だと、位置の細かいぶれで向きが左右に振れる)。分からなければ null
 */
function courseHeading(data, rows, k, span = 6) {
  const { x, z } = data, n = rows.length, i = rows[k];
  let a = k, b = k;
  for (let j = k - 1; j >= 0 && k - j < 400; j--) {
    if (Math.hypot(x[rows[j]] - x[rows[j + 1]], z[rows[j]] - z[rows[j + 1]]) > COURSE_JUMP_M) break;
    a = j;
    if (Math.hypot(x[rows[j]] - x[i], z[rows[j]] - z[i]) >= span) break;
  }
  for (let j = k + 1; j < n && j - k < 400; j++) {
    if (Math.hypot(x[rows[j]] - x[rows[j - 1]], z[rows[j]] - z[rows[j - 1]]) > COURSE_JUMP_M) break;
    b = j;
    if (Math.hypot(x[rows[j]] - x[i], z[rows[j]] - z[i]) >= span) break;
  }
  const dx = x[rows[b]] - x[rows[a]], dz = z[rows[b]] - z[rows[a]], len = Math.hypot(dx, dz);
  return len < 1 ? null : [dx / len, dz / len];
}

/**
 * 区切って追従するときの区間。rows を、直線の真ん中で区切る(コーナーの途中やスピン中では区切らない)。
 * - 直線: 曲がりが緩く(1000/R の絶対値が 3 未満 = 半径 330m より大きい)、50 km/h 以上。1 秒以上続く所の真ん中を区切りの候補にする。
 *   スピン中は曲がりが急で遅いので、候補にならない
 * - 1 つの区間は、一辺 limitM の 8 割の四角に収まる長さ。収まる候補が無いときは、その中の後ろ半分で一番曲がりの緩い所で区切る
 * - 位置の飛び(リワインド・復帰)でも区切る
 * 戻り値: [{ k0, k1, cx, cz, dir: [dx, dz] }](rows の中の番号の範囲、四角の中心、区間の始まりから終わりへの向き)
 */
function buildFollowSections(data, rows, limitM) {
  const { x, z, t, turn, speed } = data, n = rows.length;
  if (n < 2) return [];
  const straight = rows.map((i) => turn[i] != null && Math.abs(turn[i]) < 3 && speed[i] != null && speed[i] > 50);
  const cand = new Uint8Array(n);
  let s0 = -1;
  for (let k = 0; k <= n; k++) {
    if (k < n && straight[k]) { if (s0 < 0) s0 = k; continue; }
    if (s0 >= 0 && t[rows[k - 1]] - t[rows[s0]] >= 1.0) cand[(s0 + k - 1) >> 1] = 1;
    s0 = -1;
  }
  const lim = limitM * 0.8;
  const sections = [];
  let start = 0;
  while (start < n - 1) {
    let minX = x[rows[start]], maxX = minX, minZ = z[rows[start]], maxZ = minZ;
    let lastCand = -1, k = start + 1, end = -1, next = -1;
    for (; k < n; k++) {
      const i = rows[k], p = rows[k - 1];
      if (Math.hypot(x[i] - x[p], z[i] - z[p]) > COURSE_JUMP_M) { end = k - 1; next = k; break; }
      minX = Math.min(minX, x[i]); maxX = Math.max(maxX, x[i]);
      minZ = Math.min(minZ, z[i]); maxZ = Math.max(maxZ, z[i]);
      if (Math.max(maxX - minX, maxZ - minZ) > lim) {
        if (lastCand > start) end = lastCand;
        else {
          // 収まる直線が無い: 後ろ半分で一番曲がりの緩い所(遅すぎる所 = スピン・停止は避ける)
          let best = k - 1, bestTurn = Infinity;
          for (let j = Math.max(start + 1, (start + k) >> 1); j < k; j++) {
            const i2 = rows[j], tv = turn[i2] == null ? Infinity : Math.abs(turn[i2]);
            if ((speed[i2] ?? 0) > 30 && tv < bestTurn) { bestTurn = tv; best = j; }
          }
          end = best;
        }
        break;
      }
      if (cand[k]) lastCand = k;
    }
    if (end < 0) end = n - 1;
    if (end <= start) end = Math.min(n - 1, start + 1);
    // 区間の四角の中心と、始まりから終わりへの向き
    let ax = Infinity, bx = -Infinity, az = Infinity, bz = -Infinity;
    for (let j = start; j <= end; j++) {
      const i = rows[j];
      ax = Math.min(ax, x[i]); bx = Math.max(bx, x[i]); az = Math.min(az, z[i]); bz = Math.max(bz, z[i]);
    }
    let dx = x[rows[end]] - x[rows[start]], dz = z[rows[end]] - z[rows[start]];
    let len = Math.hypot(dx, dz);
    let dir = len >= 20 ? [dx / len, dz / len] : courseHeading(data, rows, start) || [0, 1];
    sections.push({ k0: start, k1: end, cx: (ax + bx) / 2, cz: (az + bz) / 2, dir });
    start = next >= 0 ? next : end;
  }
  return sections;
}

/** 区間の一覧から、rows の k 番目の行を含む区間 */
function sectionAt(sections, k) {
  let lo = 0, hi = sections.length - 1, found = null;
  while (lo <= hi) {
    const m = (lo + hi) >> 1;
    if (sections[m].k0 <= k) { found = sections[m]; lo = m + 1; } else hi = m - 1;
  }
  return found;
}

/** 0.4 秒かけて移る(区切って追従するとき、区間が変わったら) */
const FOLLOW_ANIM_MS = 400;
const easeInOut = (u) => (u < 0.5 ? 2 * u * u : 1 - Math.pow(-2 * u + 2, 2) / 2);

const rgbCss = (c, a = 1) => `rgba(${Math.round(c[0] * 255)},${Math.round(c[1] * 255)},${Math.round(c[2] * 255)},${a})`;

const CourseMap = {
  canvas: null,
  data: null,         // 系列(横軸が時間のもの。x, z, t を使う)
  rows: null,         // 描く行(系列の中の位置、昇順)
  cut: null,          // 系列の位置 → 1 ならやり直し区間の行(Uint8Array)。無ければ null
  colors: null,       // 系列の位置 → 路面の色 [r, g, b](buildSurfaceColors)
  colorMode: "lap",   // "lap"(今の周を青)/ "surface"(路面の色)
  followMode: "live", // 拡大中の追従: "live"(いつも今の位置が真ん中)/ "step"(区間ごとに区切って、区間が変わったら移る)
  _sections: null,
  _sectionKey: null,
  _anim: null,        // { from: [cx, cz], to: [cx, cz], t0 }
  _target: null,      // 今の中心 [cx, cz](区切って追従するとき)
  zoom: null,         // null = 全体、数値 = 描く範囲の一辺(m)。今の位置を真ん中にして追従する
  onSeekTime: null,   // (sec) => void
  getNow: null,       // () => 今の動画の秒

  _fit: null,         // 全体を収める { cx, cz, s }
  _bg: null,          // 全体のときの下絵(軌跡全体)
  _view: null,        // 最後に描いたときの { cx, cz, s }(クリックの位置を戻すため)
  _last: { pos: null, t0: null, t1: null, l0: null, l1: null },

  /** 1 サンプルでこれを超えて動いたら線を切る(ファストトラベル・復帰) */
  JUMP_M: 50,

  init(canvas) {
    this.canvas = canvas;
    new ResizeObserver(() => this._rebuild()).observe(canvas);
    canvas.addEventListener("click", (e) => this._click(e));
  },

  /** 描く行を決める。rows が null なら記録全体。cut: やり直し区間の行の印(系列の位置ごと) */
  setRows(data, rows, cut, colors) {
    this.data = data;
    this.cut = cut || null;
    this.colors = colors || null;
    if (!data) { this.rows = null; this._rebuild(); return; }
    const all = rows || data.t.map((_, i) => i);
    this.rows = all.filter((i) => data.x[i] != null && data.z[i] != null);
    this._rebuild();
  },

  /** 色のモードを変える("lap" / "surface") */
  setColors(colors, mode) {
    this.colors = colors;
    this.colorMode = mode;
    this._rebuild();
  },

  _surface() {
    return this.colorMode === "surface" && this.colors != null;
  },

  /** rows[a..b] を行ごとの色で(同じ色が続く所はまとめて線を引く)。keep が false の行と画面の外は飛ばす */
  _pathColored(g, view, a, b, keep, alpha) {
    const { x, z } = this.data, rows = this.rows, c = this.canvas, margin = 50;
    let cur = null, prev = null;
    const flush = () => { if (cur) g.stroke(); cur = null; };
    for (let k = a; k <= b; k++) {
      const i = rows[k];
      const col = this.colors[i];
      const [px, py] = this._P(view, i);
      const out = px < -margin || px > c.width + margin || py < -margin || py > c.height + margin;
      const jump = k > a && Math.hypot(x[i] - x[rows[k - 1]], z[i] - z[rows[k - 1]]) > this.JUMP_M;
      if (out || !col || !keep(i) || jump) { flush(); prev = out || !col || !keep(i) ? null : [px, py]; continue; }
      if (col !== cur) {
        flush();
        cur = col;
        g.strokeStyle = rgbCss(col, alpha);
        g.beginPath();
        if (prev) g.moveTo(prev[0], prev[1]); else g.moveTo(px, py);
      }
      g.lineTo(px, py);
      prev = [px, py];
    }
    flush();
  },

  /** 追従のしかたを変える("live" / "step") */
  setFollowMode(mode) {
    this.followMode = mode;
    this._target = null;
    this._anim = null;
    this._redraw();
  },

  /** 区切って追従するときの中心(区間が変わったら、0.4 秒かけて移る) */
  _stepCenter(k) {
    const key = `${this.zoom}|${this.rows.length}|${this.rows[0]}`;
    if (this._sectionKey !== key) {
      this._sections = buildFollowSections(this.data, this.rows, this.zoom);
      this._sectionKey = key;
      this._target = null;
    }
    const sec = sectionAt(this._sections, k);
    if (!sec) return null;
    const to = [sec.cx, sec.cz];
    const now = performance.now();
    if (!this._target) { this._target = to; this._anim = null; return to; }
    if (to[0] !== this._target[0] || to[1] !== this._target[1]) {
      const from = this._anim ? this._animPos(now) : this._target;
      this._anim = { from, to, t0: now };
      this._target = to;
    }
    if (!this._anim) return to;
    const p = this._animPos(now);
    if (now - this._anim.t0 >= FOLLOW_ANIM_MS) this._anim = null;
    else requestAnimationFrame(() => this._redraw());
    return p;
  },

  _animPos(now) {
    const a = this._anim, u = easeInOut(Math.min(1, (now - a.t0) / FOLLOW_ANIM_MS));
    return [a.from[0] + (a.to[0] - a.from[0]) * u, a.from[1] + (a.to[1] - a.from[1]) * u];
  },

  /** 拡大の段を変える(null = 全体、数値 = 一辺の m) */
  setZoom(meters) {
    this.zoom = meters || null;
    this._redraw();
  },

  _rebuild() {
    const c = this.canvas;
    if (!c) return;
    const dpr = devicePixelRatio || 1;
    c.width = Math.max(1, Math.round(c.clientWidth * dpr));
    c.height = Math.max(1, Math.round(c.clientHeight * dpr));
    this._bg = null;
    this._fit = null;
    if (!this.data || !this.rows || this.rows.length < 2) { this._clear(); return; }

    const { x, z } = this.data;
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const i of this.rows) {
      minX = Math.min(minX, x[i]); maxX = Math.max(maxX, x[i]);
      minZ = Math.min(minZ, z[i]); maxZ = Math.max(maxZ, z[i]);
    }
    const pad = 10 * dpr;
    const s = Math.min((c.width - pad * 2) / Math.max(1, maxX - minX), (c.height - pad * 2) / Math.max(1, maxZ - minZ));
    this._fit = { cx: (minX + maxX) / 2, cz: (minZ + maxZ) / 2, s };

    // 全体のときの下絵: 軌跡全体を暗い線で
    const bg = document.createElement("canvas");
    bg.width = c.width;
    bg.height = c.height;
    const g = bg.getContext("2d");
    this._background(g, this._fit);
    this._bg = bg;
    this._redraw();
  },

  _clear() {
    const g = this.canvas.getContext("2d");
    g.fillStyle = "#101216";
    g.fillRect(0, 0, this.canvas.width, this.canvas.height);
  },

  _background(g, view) {
    const dpr = devicePixelRatio || 1;
    g.fillStyle = "#101216";
    g.fillRect(0, 0, g.canvas.width, g.canvas.height);
    const w = (view === this._fit ? 1.5 : 3) * dpr;
    g.lineJoin = "round";
    g.lineCap = "round";
    // 本命の走り(やり直し区間以外): 灰色(路面の色のモードでは、路面の色を薄く)
    g.lineWidth = w;
    if (this._surface()) {
      this._pathColored(g, view, 0, this.rows.length - 1, (i) => !this._isCut(i), 0.45);
    } else {
      g.strokeStyle = "#4f5661";
      this._path(g, view, 0, this.rows.length - 1, (i) => !this._isCut(i));
    }
    // やり直し区間: 暗い赤の点線(本命の走りと分けて見えるように)
    if (this.cut) {
      g.strokeStyle = "#a8484888";
      g.lineWidth = w * 0.8;
      g.setLineDash([4 * dpr, 4 * dpr]);
      this._path(g, view, 0, this.rows.length - 1, (i) => this._isCut(i));
      g.setLineDash([]);
    }
  },

  _isCut(i) {
    return this.cut != null && this.cut[i] === 1;
  },

  /** 世界の座標 → 画面の位置 */
  _P(view, i) {
    const c = this.canvas;
    return [c.width / 2 + (this.data.x[i] - view.cx) * view.s, c.height / 2 - (this.data.z[i] - view.cz) * view.s];
  },

  /** rows[a..b] の軌跡を線で(飛びで切る。拡大中は画面の外の点を飛ばす)。keep があれば、それが true の行だけ */
  _path(g, view, a, b, keep) {
    const { x, z } = this.data, rows = this.rows, c = this.canvas;
    const margin = 50;
    let pen = false;
    g.beginPath();
    for (let k = a; k <= b; k++) {
      const i = rows[k];
      const [px, py] = this._P(view, i);
      const out = px < -margin || px > c.width + margin || py < -margin || py > c.height + margin;
      const jump = k > a && Math.hypot(x[i] - x[rows[k - 1]], z[i] - z[rows[k - 1]]) > this.JUMP_M;
      if (out || (keep && !keep(i))) { pen = false; continue; }
      if (jump) pen = false;
      if (!pen) { g.moveTo(px, py); pen = true; } else g.lineTo(px, py);
    }
    g.stroke();
  },

  /** rows の中で、系列の位置が pos 以上の最初の番号 */
  _find(pos) {
    const rows = this.rows;
    let lo = 0, hi = rows.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (rows[m] < pos) lo = m + 1; else hi = m; }
    return lo;
  },

  /** rows の中で、動画の秒が t 以上の最初の番号 */
  _findTime(sec) {
    const rows = this.rows, t = this.data.t;
    let lo = 0, hi = rows.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (t[rows[m]] < sec) lo = m + 1; else hi = m; }
    return lo;
  },

  /** 今の行の rows の中の番号(描いている範囲の外なら -1) */
  _pointerK(pos) {
    const rows = this.rows, t = this.data.t;
    if (pos == null || pos < 0) return -1;
    let k = this._find(pos);
    if (k >= rows.length || rows[k] !== pos) k = Math.min(rows.length - 1, Math.max(0, k - 1));
    return Math.abs(t[rows[k]] - t[pos]) > 1 ? -1 : k;
  },

  _redraw() {
    const l = this._last;
    this.draw(l.pos, l.t0, l.t1, l.l0, l.l1);
  },

  /**
   * pointerPos: 今の行(系列の中の位置)。t0〜t1(動画の秒): グラフに出している範囲。
   * l0〜l1(動画の秒): 今いる周(周の無いレースはレース全体)。無ければ null(レースの外)
   */
  draw(pointerPos, t0, t1, l0, l1) {
    this._last = { pos: pointerPos, t0, t1, l0, l1 };
    const c = this.canvas;
    if (!c || !this._fit) return;
    const g = c.getContext("2d"), dpr = devicePixelRatio || 1;
    const k = this._pointerK(pointerPos);

    // 描き方: 全体なら下絵を使う。拡大中は今の位置を真ん中に(位置が無ければ全体の真ん中)
    let view = this._fit;
    if (this.zoom) {
      const i = k >= 0 ? this.rows[k] : null;
      // 区切って追従するときは区間の中心、いつも追従するときは今の位置
      const step = this.followMode === "step" && k >= 0 ? this._stepCenter(k) : null;
      view = {
        cx: step ? step[0] : i != null ? this.data.x[i] : this._fit.cx,
        cz: step ? step[1] : i != null ? this.data.z[i] : this._fit.cz,
        s: Math.min(c.width, c.height) / this.zoom,
      };
      this._background(g, view);
    } else {
      g.drawImage(this._bg, 0, 0);
    }
    this._view = view;

    const range = (s0, s1) => [this._findTime(s0), Math.min(this.rows.length - 1, this._findTime(s1))];
    const keep = (i) => !this._isCut(i);
    g.lineJoin = "round";
    const surface = this._surface();
    // 今いる周: 青(路面の色のモードでは、路面の色をはっきり)
    let lap = null;
    if (l0 != null && l1 != null) {
      lap = range(l0, l1);
      if (lap[1] > lap[0]) {
        g.lineWidth = (this.zoom ? 3.5 : 2.5) * dpr;
        if (surface) this._pathColored(g, view, lap[0], lap[1], keep, 1);
        else { g.strokeStyle = "#5b8fd9"; this._path(g, view, lap[0], lap[1], keep); }
      }
    }
    // グラフに出している範囲: 明るく(今いる周があれば、その中だけ。路面の色のモードでは白い細い線を重ねる)
    if (t0 != null && t1 != null) {
      let [a, b] = range(t0, t1);
      if (lap) { a = Math.max(a, lap[0]); b = Math.min(b, lap[1]); }
      if (b > a) {
        g.strokeStyle = surface ? "rgba(255,255,255,0.75)" : lap ? "#cfe0ff" : "#8fb8ff";
        g.lineWidth = (surface ? 1.2 : this.zoom ? 4 : 3) * dpr;
        this._path(g, view, a, b, keep);
      }
    }

    if (this.zoom) this._scale(g, view);
    if (k < 0) return;

    const [px, py] = this._P(view, this.rows[k]);
    // 向き: 前後 6m ずつ離れた点を結んだ方向(画面では x = 東が右、北が上)
    const h = courseHeading(this.data, this.rows, k);
    const dir = h ? Math.atan2(-h[1], h[0]) : null;
    g.fillStyle = "#ffcc33";
    g.strokeStyle = "#000";
    g.lineWidth = 1.5 * dpr;
    g.beginPath();
    if (dir != null) {
      const r = 9 * dpr;
      g.moveTo(px + Math.cos(dir) * r, py + Math.sin(dir) * r);
      g.lineTo(px + Math.cos(dir + 2.5) * r * 0.7, py + Math.sin(dir + 2.5) * r * 0.7);
      g.lineTo(px + Math.cos(dir - 2.5) * r * 0.7, py + Math.sin(dir - 2.5) * r * 0.7);
      g.closePath();
    } else {
      g.arc(px, py, 5 * dpr, 0, Math.PI * 2);
    }
    g.fill();
    g.stroke();
  },

  /** 拡大中の縮尺: 左下に目盛りの線と長さ */
  _scale(g, view) {
    const dpr = devicePixelRatio || 1, c = this.canvas;
    const target = (c.width * 0.25) / view.s;   // 画面の幅の 4 分の 1 くらいの長さ
    const nice = Units.mapScale(target);   // m・km か ft・mi の切りのよい長さ
    const w = nice.m * view.s, x0 = 8 * dpr, y0 = c.height - 8 * dpr;
    g.strokeStyle = "#cfd3d8";
    g.lineWidth = 2 * dpr;
    g.beginPath();
    g.moveTo(x0, y0 - 5 * dpr); g.lineTo(x0, y0); g.lineTo(x0 + w, y0); g.lineTo(x0 + w, y0 - 5 * dpr);
    g.stroke();
    g.fillStyle = "#cfd3d8";
    g.font = `${11 * dpr}px "Yu Gothic UI", sans-serif`;
    g.fillText(nice.label, x0 + 4 * dpr, y0 - 8 * dpr);
  },

  /** クリック: その近くを通った時刻へ(何度も通っていれば、今の時刻に一番近いもの) */
  _click(e) {
    if (!this._view || !this.rows || !this.onSeekTime) return;
    const dpr = devicePixelRatio || 1;
    const r = this.canvas.getBoundingClientRect();
    const cx = (e.clientX - r.left) * dpr, cy = (e.clientY - r.top) * dpr;
    const near = 12 * dpr;
    const now = this.getNow ? this.getNow() : 0;
    const t = this.data.t, view = this._view;
    let best = null, bestDt = Infinity;
    // 近くを通った一続きの所ごとに、一番近い点を候補にする
    let runBest = -1, runD = Infinity;
    const flush = () => {
      if (runBest < 0) return;
      const dt = Math.abs(t[this.rows[runBest]] - now);
      if (dt < bestDt) { best = runBest; bestDt = dt; }
      runBest = -1; runD = Infinity;
    };
    this.rows.forEach((i, k) => {
      const [px, py] = this._P(view, i);
      const d = Math.hypot(px - cx, py - cy);
      if (d <= near) { if (d < runD) { runBest = k; runD = d; } } else flush();
    });
    flush();
    if (best != null) this.onSeekTime(t[this.rows[best]]);
  },
};
