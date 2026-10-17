// 摩擦円(g-g ダイアグラム): 前後と横の加速度(G)を 1 つの点で描く。
// 横軸 = 横 G(右が正)、縦軸 = 前後 G(上 = 加速、下 = ブレーキ)。系列の gLat・gLong は C# の LoadedSession.BuildSeries が、
// FH6 のデータ出力の acceleration_x(横。+ = 右)・acceleration_z(前後。+ = 前)を G に直したもの(車体座標)。
//
// 円(グリップの目安)は、選んでいるレース(「全体」なら記録全体)の中で、その路面で出た合成加速度の上位 2% の大きさ。
// 車の限界そのものではなく、その走りで実際に使えた大きさ(複数の車を含む記録は、レースを選ぶと車ごとに合う)。
//   - 舗装(ジャンプ台・縁石を含む)の円を最大として、いつも薄く残す
//   - 今いる路面が舗装以外(雪・ダート・芝)なら、その路面の円を強調して描く。ほかの路面の円は薄い点線
//   - 円の外へは、表示の上限(最大の円の 1.3 倍)まで描く。それ以上は縁に丸めて、中を抜いた印にする
// 点: 直近の数秒の軌跡と今の点。スリップ(前後の大きい方)が 1(限界)を超えていればオレンジ。
// 接触・着地・ジャンプの前後と、速度が低い行は、円の推定にも点群にも使わない(はね上がりで円が膨らむため)。
"use strict";

const GG_PAVED = new Set([0, 3, 7]);      // 舗装・ジャンプ台(0.20)・縁石(0.24)。路面の逸脱の判定(SurfaceExcursionDetector)と同じ扱い
const GG_MIN_KMH = 20;                    // 円の推定に使う行の最低速度(止まっている間の揺れとグリッドの発進を除く)
const GG_CLOUD_KMH = 5;                   // 点群に出す行の最低速度
const GG_RING_MIN_ROWS = 150;             // 円を作るのに要る行数(30fps で 5 秒ぶん)
const GG_RING_PERCENTILE = 0.98;
const GG_SANE_G = 6;                      // これを超える加速度は、接触・着地・復帰のはね上がり(上限は 20G)。推定にも点群にも使わない
const GG_CAP_RATIO = 1.3;                 // 表示の上限 = 最大の円の何倍か
const GG_DEFAULT_MAX = 1.5;               // 円を作れなかったときの表示の上限(G)
const GG_AFTER_JUMP_SEC = 0.5;            // 着地の後に除く時間
const GG_CONTACT_SEC = 0.3;               // 接触の前後に除く時間
const GG_RING_COLOR = { 0: "#cfd6e4", 1: "#e8f1ff", 2: "#d99a52", 4: "#5fc470" };   // 舗装・雪など・ダート・芝(graphs.js の SURFACE の番号)
const GG_SLIP_COLOR = "#ff9f43";
const GG_NORMAL_COLOR = "#6cb2ff";

/** i 行目の前後 3 行の平均 [横 G, 前後 G](フレームごとの揺れを軽くならす)。値が無ければ null */
function ggAt(data, i) {
  const lat = data.gLat, lon = data.gLong, t = data.t;
  if (lat[i] == null || lon[i] == null) return null;
  let a = lat[i], b = lon[i], n = 1;
  for (const j of [i - 1, i + 1]) {
    if (j < 0 || j >= t.length || lat[j] == null || lon[j] == null) continue;
    if (Math.abs(t[j] - t[i]) > 0.1) continue;
    a += lat[j]; b += lon[j]; n++;
  }
  return [a / n, b / n];
}

/** 合成加速度(G)。値が無いか、はね上がり(GG_SANE_G 超)なら null */
function ggMag(data, i) {
  const v = ggAt(data, i);
  if (!v) return null;
  const m = Math.hypot(v[0], v[1]);
  return m > GG_SANE_G ? null : m;
}

/**
 * i 行目の路面の種類。舗装(ジャンプ台・縁石を含む)は 0、ほかは SurfaceKind の番号(1 雪など、2 ダート、4 芝・荒れ地、5 不明)。
 * -1 = 無し(停止区間)、-2 = 空中、-3 = 混在(4 輪の路面が違う)
 */
function ggSurfaceGroup(data, i) {
  const sf = data.surface;
  const c = [sf.fl[i], sf.fr[i], sf.rl[i], sf.rr[i]];
  if (c.some((v) => v == null)) return -1;
  const k = c.map((v) => v & 7);
  if (k.some((v) => v === 6)) return -2;
  const g = k.map((v) => (GG_PAVED.has(v) ? 0 : v));
  return g.every((v) => v === g[0]) ? g[0] : -3;
}

/** sorted(昇順)の中で、v 以上になる最初の位置 */
function ggLowerBound(sorted, v) {
  let lo = 0, hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (sorted[mid] < v) lo = mid + 1; else hi = mid;
  }
  return lo;
}

/** 円の推定と点群から除く行(接触・着地の前後、ジャンプの間とその後)。1 ならその行を除く */
function ggExcludeMask(data, marks) {
  const t = data.t, mask = new Uint8Array(t.length);
  if (!marks) return mask;
  const spans = [];
  for (const c of marks.contacts || []) spans.push([c.t - GG_CONTACT_SEC, c.t + GG_CONTACT_SEC]);
  for (const j of marks.jumps || []) spans.push([j.t0 - 0.1, j.t1 + GG_AFTER_JUMP_SEC]);
  for (const [a, b] of spans) {
    for (let i = ggLowerBound(t, a); i < t.length && t[i] <= b; i++) mask[i] = 1;
  }
  return mask;
}

/** 昇順にしていない数の並びの p 番目(0〜1)の値 */
function ggPercentile(values, p) {
  const s = Float64Array.from(values).sort();
  return s[Math.min(s.length - 1, Math.max(0, Math.ceil(p * s.length) - 1))];
}

/**
 * 円の推定: 路面ごと(舗装は 1 つにまとめる)に、合成加速度の上位 2% の大きさ。行数が足りない路面は円を作らない。
 * 戻り値: { rings: Map(路面 → { r: 半径(G), n: 行数 }), rmax: いちばん大きい円の半径(円が 1 つも無ければ 0) }
 */
function ggEstimate(data, rows, mask) {
  const buckets = new Map();
  for (const i of rows) {
    if (mask && mask[i]) continue;
    if (data.speed[i] == null || data.speed[i] < GG_MIN_KMH) continue;
    const m = ggMag(data, i);
    if (m == null) continue;
    const g = ggSurfaceGroup(data, i);
    if (g < 0 || g === 5) continue;
    let list = buckets.get(g);
    if (!list) buckets.set(g, list = []);
    list.push(m);
  }
  const rings = new Map();
  let rmax = 0;
  for (const [g, list] of buckets) {
    if (list.length < GG_RING_MIN_ROWS) continue;
    const r = ggPercentile(list, GG_RING_PERCENTILE);
    rings.set(g, { r, n: list.length });
    rmax = Math.max(rmax, r);
  }
  return { rings, rmax };
}

const FrictionCircle = {
  canvas: null,
  data: null,          // 系列(横軸が時間のもの。gLat・gLong・speed・slipF・slipR・surface・t を使う)
  rows: null,          // 円の推定と点群に使う行(選んでいるレースの行。全体なら全部)
  marks: null,
  est: { rings: new Map(), rmax: 0 },
  scaleMax: GG_DEFAULT_MAX,   // 表示の上限(G)
  trailSec: 3,         // 軌跡の長さ(秒。0 = 出さない)
  cloud: true,         // 点群(選んでいる範囲で出た加速度の全部)を薄く出すか

  _mask: null,
  _bg: null,           // 下絵(目盛り・円・点群)
  _geom: null,         // { w, h, cx, cy, s }(s = 1G あたりの px)
  _last: null,

  init(canvas) {
    this.canvas = canvas;
    new ResizeObserver(() => this._rebuild()).observe(canvas);
  },

  /** 使う行を決める。rows が null なら記録全体。marks は接触・ジャンプの印(そのまわりを除く) */
  setRows(data, rows, marks) {
    if (!data || !data.gLat || !data.gLong || !data.surface) {
      this.data = null; this.rows = null; this._mask = null;
      this.est = { rings: new Map(), rmax: 0 };
      this.scaleMax = GG_DEFAULT_MAX;
      this._rebuild();
      return;
    }
    this.data = data;
    this.marks = marks || null;
    this.rows = rows || data.t.map((_, i) => i);
    this._mask = ggExcludeMask(data, this.marks);
    this.est = ggEstimate(data, this.rows, this._mask);
    this.scaleMax = this.est.rmax > 0 ? Math.max(1, this.est.rmax * GG_CAP_RATIO) : GG_DEFAULT_MAX;
    this._rebuild();
  },

  setTrail(sec) { this.trailSec = sec; this._redraw(); },
  setCloud(on) { this.cloud = on; this._rebuild(); },

  /** 読み取り値用: 今の行の { lat, lon, mag, group, ring(その路面の円の半径。無ければ null) }。値が無ければ null */
  info(pos) {
    if (!this.data || pos == null || pos < 0) return null;
    const v = ggAt(this.data, pos);
    if (!v) return null;
    const group = ggSurfaceGroup(this.data, pos);
    const ring = group >= 0 && this.est.rings.has(group) ? this.est.rings.get(group).r : null;
    return { lat: v[0], lon: v[1], mag: Math.hypot(v[0], v[1]), group, ring };
  },

  /** G → 画面の位置 [x, y, 上限で丸めたか]。横 G は右が正、前後 G は上が加速 */
  _xy(lat, lon) {
    const G = this._geom, m = Math.hypot(lat, lon);
    const cap = this.scaleMax * 0.985;
    const k = m > cap ? cap / m : 1;
    return [G.cx + lat * k * G.s, G.cy - lon * k * G.s, m > cap];
  },

  _rebuild() {
    const c = this.canvas;
    if (!c) return;
    const dpr = devicePixelRatio || 1;
    c.width = Math.max(1, Math.round(c.clientWidth * dpr));
    c.height = Math.max(1, Math.round(c.clientHeight * dpr));
    this._bg = null;
    this._geom = null;
    if (c.clientWidth < 2 || c.clientHeight < 2) return;   // 隠れている間は作らない(出したときに、大きさの変化でここへ来る)

    const side = Math.min(c.width, c.height), pad = 22 * dpr;
    this._geom = { w: c.width, h: c.height, cx: c.width / 2, cy: c.height / 2, s: Math.max(1, (side / 2 - pad) / this.scaleMax) };
    const bg = document.createElement("canvas");
    bg.width = c.width;
    bg.height = c.height;
    this._drawStatic(bg.getContext("2d"), dpr);
    this._bg = bg;
    this._redraw();
  },

  /** 下絵: 目盛りの円・軸・表示の上限・路面ごとの円・点群・向きの名前 */
  _drawStatic(g, dpr) {
    const G = this._geom, { cx, cy, s } = G;
    const font = (px) => `${px * dpr}px "Yu Gothic UI", sans-serif`;
    const circle = (r) => { g.beginPath(); g.arc(cx, cy, r * s, 0, Math.PI * 2); };

    // 目盛りの円(0.5G か 1G ごと)と軸
    const step = this.scaleMax <= 2.2 ? 0.5 : 1;
    g.lineWidth = dpr;
    g.strokeStyle = "rgba(255,255,255,0.07)";
    g.fillStyle = "rgba(154,161,171,0.7)";
    g.font = font(10);
    g.textAlign = "left";
    for (let v = step; v < this.scaleMax - 0.05; v += step) {
      circle(v); g.stroke();
      g.fillText(`${v.toFixed(1)}G`, cx + v * s + 2 * dpr, cy + 11 * dpr);
    }
    g.strokeStyle = "rgba(255,255,255,0.18)";
    g.beginPath();
    g.moveTo(cx - this.scaleMax * s, cy); g.lineTo(cx + this.scaleMax * s, cy);
    g.moveTo(cx, cy - this.scaleMax * s); g.lineTo(cx, cy + this.scaleMax * s);
    g.stroke();

    // 表示の上限(これより外は縁に丸める)
    g.setLineDash([3 * dpr, 4 * dpr]);
    g.strokeStyle = "rgba(255,110,110,0.3)";
    circle(this.scaleMax); g.stroke();

    // 路面ごとの円。舗装は実線で薄く、ほかは点線
    for (const [grp, ring] of this.est.rings) {
      if (grp === 0) {
        g.setLineDash([]);
        g.strokeStyle = "rgba(207,214,228,0.4)";
        g.lineWidth = 1.5 * dpr;
      } else {
        g.setLineDash([2 * dpr, 3 * dpr]);
        g.strokeStyle = GG_RING_COLOR[grp] || "#999";
        g.globalAlpha = 0.4;
        g.lineWidth = dpr;
      }
      circle(ring.r); g.stroke();
      g.globalAlpha = 1;
    }
    g.setLineDash([]);

    // 点群
    if (this.cloud && this.data) {
      const d = this.data, mask = this._mask;
      g.fillStyle = "rgba(150,170,200,0.22)";
      const px = Math.max(1, Math.round(1.6 * dpr));
      for (const i of this.rows) {
        if (mask && mask[i]) continue;
        if (d.speed[i] == null || d.speed[i] < GG_CLOUD_KMH) continue;
        const v = ggAt(d, i);
        if (!v || Math.hypot(v[0], v[1]) > this.scaleMax) continue;
        g.fillRect(cx + v[0] * s - px / 2, cy - v[1] * s - px / 2, px, px);
      }
    }

    // 向きの名前
    g.fillStyle = "rgba(154,161,171,0.9)";
    g.font = font(11);
    g.textAlign = "center";
    g.fillText(t("加速"), cx, 13 * dpr);
    g.fillText(t("ブレーキ"), cx, G.h - 5 * dpr);
    g.textAlign = "left";
    g.fillText(t("左"), 5 * dpr, cy - 5 * dpr);
    g.textAlign = "right";
    g.fillText(t("右"), G.w - 5 * dpr, cy - 5 * dpr);
    g.textAlign = "left";
  },

  _redraw() {
    this.draw(this._last);
  },

  /** pos: 今の行(系列の中の位置)。範囲の外や値が無いときは下絵だけ */
  draw(pos) {
    this._last = pos;
    const c = this.canvas;
    if (!c || !this._bg || !this._geom) return;
    const g = c.getContext("2d"), dpr = devicePixelRatio || 1, G = this._geom;
    g.clearRect(0, 0, c.width, c.height);
    g.drawImage(this._bg, 0, 0);
    const d = this.data;
    if (!d || pos == null || pos < 0 || !ggAt(d, pos)) return;
    const slipOf = (i) => {
      const a = d.slipF[i], b = d.slipR[i];
      return a == null && b == null ? 0 : Math.max(a ?? 0, b ?? 0);
    };

    // 今いる路面の円を強調(舗装以外なら、舗装の円の内側にこの円が出る)
    const info = this.info(pos);
    const ring = info && info.ring != null ? info.ring : null;
    if (ring != null) {
      g.strokeStyle = GG_RING_COLOR[info.group] || "#ccc";
      g.lineWidth = 2.5 * dpr;
      g.beginPath(); g.arc(G.cx, G.cy, ring * G.s, 0, Math.PI * 2); g.stroke();
      const name = typeof SURFACE !== "undefined" && SURFACE[info.group] ? SURFACE[info.group].name : "";
      g.fillStyle = g.strokeStyle;
      g.font = `${10 * dpr}px "Yu Gothic UI", sans-serif`;
      g.textAlign = "left";
      g.fillText(name, G.cx - ring * G.s + 4 * dpr, G.cy - 5 * dpr);
    }

    // 軌跡(つながっている直近の数秒。古いほど薄く)
    if (this.trailSec > 0) {
      let k = pos;
      while (k > 0 && d.t[pos] - d.t[k - 1] <= this.trailSec && d.t[k] - d.t[k - 1] <= 0.3 && ggAt(d, k - 1)) k--;
      for (let j = k + 1; j <= pos; j++) {
        const a = ggAt(d, j - 1), b = ggAt(d, j);
        if (!a || !b) continue;
        const [x0, y0] = this._xy(a[0], a[1]), [x1, y1] = this._xy(b[0], b[1]);
        g.globalAlpha = 0.12 + 0.75 * ((j - k) / Math.max(1, pos - k));
        g.strokeStyle = g.fillStyle = slipOf(j) > 1 ? GG_SLIP_COLOR : GG_NORMAL_COLOR;
        g.lineWidth = 1.5 * dpr;
        g.beginPath(); g.moveTo(x0, y0); g.lineTo(x1, y1); g.stroke();
        g.beginPath(); g.arc(x1, y1, 1.8 * dpr, 0, Math.PI * 2); g.fill();
      }
      g.globalAlpha = 1;
    }

    // 今の点(スリップが限界を超えていればオレンジ。表示の上限で丸めたときは中を抜く)
    const v = ggAt(d, pos), [px, py, capped] = this._xy(v[0], v[1]);
    const over = slipOf(pos) > 1;
    g.lineWidth = 1.5 * dpr;
    g.beginPath();
    g.arc(px, py, 5 * dpr, 0, Math.PI * 2);
    if (capped) {
      g.strokeStyle = "#ff6b6b";
      g.lineWidth = 2 * dpr;
      g.stroke();
    } else {
      g.fillStyle = over ? GG_SLIP_COLOR : "#ffcc33";
      g.strokeStyle = "#000";
      g.fill();
      g.stroke();
    }

    // 合成 G と、その路面の円に対する割合(左上。円と重ならない隅)
    g.fillStyle = "#e6e8eb";
    g.font = `${11 * dpr}px "Yu Gothic UI", sans-serif`;
    g.textAlign = "left";
    g.fillText(t("合成 {g} G", { g: info.mag.toFixed(2) }), 6 * dpr, 28 * dpr);
    if (ring != null && ring > 0) {
      g.fillStyle = info.mag > ring ? GG_SLIP_COLOR : "#9aa1ab";
      g.fillText(t("円の {pct}%", { pct: Math.round((info.mag / ring) * 100) }), 6 * dpr, 42 * dpr);
    }
  },
};

if (typeof module !== "undefined" && module.exports) {
  module.exports = { FrictionCircle, ggAt, ggMag, ggSurfaceGroup, ggExcludeMask, ggPercentile, ggEstimate, GG_RING_MIN_ROWS, GG_CAP_RATIO };
}
