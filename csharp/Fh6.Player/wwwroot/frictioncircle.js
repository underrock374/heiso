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
//
// 荷重移動(kind = "load"): 同じ場所に、サスペンションの縮み(norm_suspension_travel。0 = 伸び切り、1 = 縮み切り)の差を描く。
//   横軸 = 右の 2 輪の縮み − 左の 2 輪、縦軸 = 前の 2 輪 − 後ろの 2 輪(上 = 前が沈む)。点は荷重が乗った側へ寄る(ブレーキで上、右コーナーで左)。
//   その車の、まっすぐ一定の速さで走っているときの差を基準(0)にする(前後のばねの硬さや車高の違いを除くため)。
//   G は力がかかった瞬間に出るが、縮みは車が沈んでから出るので、ブレーキの抜き方や姿勢の揺れが見える。荷重の量そのものではなく目安
//   (ばね・ダウンフォース・段差でも変わる)。円は摩擦円と同じ作り方で、その路面で出た差の上位 2%。
//   円のまわりに、4 輪それぞれのサスペンションの伸びを棒で出す(前後・左右の差だけでは、対角の 1 輪だけが浮くのが見えないため)。
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
const GG_LIMIT_COLOR = "#3cc68a";            // この車の限界(計測モードのプロファイル)
const GG_LIMIT_SURFACE = { 0: "paved", 1: "snow", 2: "dirt", 4: "grass" };   // 路面の番号(ggSurfaceGroup)→ プロファイルの路面のキー
const GG_NORMAL_COLOR = "#6cb2ff";
const LT_SMOOTH_SEC = 0.25;                // 縮みの差をならす幅の既定(前後この秒数。近い行ほど重い三角の重み。中心に置くので遅れない)。画面の「ならし」で変える
const GG_DOT_R = 7.5;                      // 今の点の半径(px。荷重移動・摩擦円とも。ならす幅によらない)
const LT_AIR = 0.03;                      // どれかの車輪がこれ未満なら伸び切り(空中。AirDetector と同じ境目)。荷重移動に使わない
const LT_CALM_G = 0.1;                    // 基準に使う行: 前後・横とも、この G 未満
const LT_CALM_KMH = 40;                   // 同じく、この速度以上
const LT_MIN_CALM = 60;                   // 基準を作るのに要る行数(足りなければ基準 0)
const LT_SANE = 1;                        // 縮みの差はこれより大きくならない
const LT_DEFAULT_MAX = 0.4;               // 円を作れなかったときの表示の上限(縮みの差)
const LT_WHEELS = ["fl", "fr", "rl", "rr"];
const LT_LIGHT = 0.05;                    // 4 輪の棒: この縮み未満は「浮きかけ」(赤)
const LT_BOTTOM = 0.95;                   // 同じく、これを超えたら「縮み切りに近い」(橙。底突きの判定は 0.98)
const LT_LIGHT_COLOR = "#ff6b6b";

/**
 * i 行目の [横 G, 前後 G]。sec が 0 なら前後 3 行の平均(フレームごとの揺れを軽くならす)、
 * sec > 0 なら前後 sec 秒の重み付き平均(荷重移動と同じ三角の重み。画面の「ならし」)。値が無ければ null
 */
function ggAt(data, i, sec = 0) {
  const lat = data.gLat, lon = data.gLong, t = data.t;
  if (lat[i] == null || lon[i] == null) return null;
  let a = lat[i], b = lon[i], n = 1;
  if (!(sec > 0)) {
    for (const j of [i - 1, i + 1]) {
      if (j < 0 || j >= t.length || lat[j] == null || lon[j] == null) continue;
      if (Math.abs(t[j] - t[i]) > 0.1) continue;
      a += lat[j]; b += lon[j]; n++;
    }
    return [a / n, b / n];
  }
  for (const dir of [-1, 1]) {
    for (let j = i + dir; j >= 0 && j < t.length; j += dir) {
      const dt = Math.abs(t[j] - t[i]);
      if (dt >= sec || lat[j] == null || lon[j] == null) break;
      const k = 1 - dt / sec;
      a += lat[j] * k; b += lon[j] * k; n += k;
    }
  }
  return [a / n, b / n];
}

/**
 * i 行目の前後 sec 秒の重み付き平均の、縮みの差 [右 − 左, 前 − 後ろ](基準は引かない。段差やばねの細かい揺れをならす)。
 * 値が無いか、どれかの車輪が伸び切り(空中)なら null
 */
function ltRaw(data, i, sec = LT_SMOOTH_SEC) {
  const s = data.susp, t = data.t;
  if (!s) return null;
  const at = (j) => {
    const fl = s.fl[j], fr = s.fr[j], rl = s.rl[j], rr = s.rr[j];
    if (fl == null || fr == null || rl == null || rr == null || Math.min(fl, fr, rl, rr) < LT_AIR) return null;
    return [(fr + rr - fl - rl) / 2, (fl + fr - rl - rr) / 2];
  };
  const v = at(i);
  if (!v) return null;
  let x = v[0], y = v[1], n = 1;
  for (const dir of [-1, 1]) {
    for (let j = i + dir; j >= 0 && j < t.length; j += dir) {
      const dt = Math.abs(t[j] - t[i]);
      if (dt >= sec) break;
      const w = at(j);
      if (!w) break;   // 空中の行から先はまたがない(着地の前後を混ぜない)
      const k = 1 - dt / sec;
      x += w[0] * k; y += w[1] * k; n += k;
    }
  }
  return [x / n, y / n];
}

/**
 * i 行目の 4 輪の縮み { fl, fr, rl, rr }(ならさない。読み取り値に使う。伸び切りの行も含める)。
 * 値が無ければ null
 */
function ltWheels(data, i) {
  const s = data.susp;
  if (!s || LT_WHEELS.some((w) => s[w][i] == null)) return null;
  return { fl: s.fl[i], fr: s.fr[i], rl: s.rl[i], rr: s.rr[i] };
}

/**
 * i 行目の 4 輪の縮みを、前後 sec 秒でならしたもの(4 輪の棒)。
 * 戻り値 { avg(三角の重みの平均), lo(幅の中でいちばん伸びた値), hi(いちばん縮んだ値) }。それぞれ { fl, fr, rl, rr }。
 * lo・hi はならさない記録の値(着地の一瞬の縮み切りや、浮いた車輪を、ならした棒が低く出ても印で残すため)。
 * 伸び切りの行もまたいでならす(浮いた車輪の棒も出すため)。i 行目の値が無ければ null
 */
function ltWheelsSmooth(data, i, sec = LT_SMOOTH_SEC) {
  const v = ltWheels(data, i);
  if (!v) return null;
  const s = data.susp, t = data.t, avg = {}, lo = {}, hi = {}, sum = {};
  let n = 1;
  for (const w of LT_WHEELS) { sum[w] = v[w]; lo[w] = v[w]; hi[w] = v[w]; }
  for (const dir of [-1, 1]) {
    for (let j = i + dir; j >= 0 && j < t.length; j += dir) {
      const dt = Math.abs(t[j] - t[i]);
      if (dt >= sec || LT_WHEELS.some((w) => s[w][j] == null)) break;
      const k = 1 - dt / sec;
      for (const w of LT_WHEELS) {
        const x = s[w][j];
        sum[w] += x * k;
        if (x < lo[w]) lo[w] = x;
        if (x > hi[w]) hi[w] = x;
      }
      n += k;
    }
  }
  for (const w of LT_WHEELS) avg[w] = sum[w] / n;
  return { avg, lo, hi };
}

/**
 * 荷重移動の基準: 選んでいる行のうち、まっすぐ一定の速さ(前後・横とも 0.1G 未満、40 km/h 以上)で走っている行の、縮みの差の中央値。
 * 戻り値 { x, y, n(使った行数), wheels(4 輪それぞれの縮みの中央値 { fl, fr, rl, rr }。4 輪の棒の目盛り) }。
 * 行が足りなければ x・y は 0、wheels は null
 */
function ltBaseline(data, rows, mask, sec = LT_SMOOTH_SEC) {
  const xs = [], ys = [], ws = { fl: [], fr: [], rl: [], rr: [] };
  for (const i of rows) {
    if (mask && mask[i]) continue;
    if (data.speed[i] == null || data.speed[i] < LT_CALM_KMH) continue;
    const g = ggAt(data, i);
    if (!g || Math.abs(g[0]) >= LT_CALM_G || Math.abs(g[1]) >= LT_CALM_G) continue;
    const v = ltRaw(data, i, sec);
    if (!v) continue;
    xs.push(v[0]); ys.push(v[1]);
    for (const w of LT_WHEELS) ws[w].push(data.susp[w][i]);
  }
  if (xs.length < LT_MIN_CALM) return { x: 0, y: 0, n: xs.length, wheels: null };
  const wheels = {};
  for (const w of LT_WHEELS) wheels[w] = ggPercentile(ws[w], 0.5);
  return { x: ggPercentile(xs, 0.5), y: ggPercentile(ys, 0.5), n: xs.length, wheels };
}

/** 合成加速度(G)。値が無いか、はね上がり(GG_SANE_G 超)なら null。sec は ggAt と同じ */
function ggMag(data, i, sec = 0) {
  const v = ggAt(data, i, sec);
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
 * at は行 → [横, 縦](既定は G。荷重移動では縮みの差)、sane はそれより大きい値を除く境目。
 * 戻り値: { rings: Map(路面 → { r: 半径, n: 行数 }), rmax: いちばん大きい円の半径(円が 1 つも無ければ 0) }
 */
function ggEstimate(data, rows, mask, at = ggAt, sane = GG_SANE_G) {
  const buckets = new Map();
  for (const i of rows) {
    if (mask && mask[i]) continue;
    if (data.speed[i] == null || data.speed[i] < GG_MIN_KMH) continue;
    const v = at(data, i);
    if (!v) continue;
    const m = Math.hypot(v[0], v[1]);
    if (m > sane) continue;
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
  smoothSec: LT_SMOOTH_SEC, // 荷重移動・摩擦円をならす幅(前後の秒)
  kind: "load",        // 描くもの: "load"(荷重移動。サスペンションの縮みの差)/ "gg"(摩擦円。G)
  base: { x: 0, y: 0, n: 0, wheels: null },   // 荷重移動の基準(ltBaseline)
  ggEst: { rings: new Map(), rmax: 0 },        // 摩擦円(G)の円。描いているものによらず作る(グラフの「摩擦円の %」に使う)

  _mask: null,
  _bg: null,           // 下絵(目盛り・円・点群)
  _geom: null,         // { w, h, cx, cy, s }(s = 1G あたりの px)
  _last: null,

  init(canvas) {
    this.canvas = canvas;
    new ResizeObserver(() => this._rebuild()).observe(canvas);
  },

  /** 使う行を決める。rows が null なら記録全体。marks は接触・ジャンプの印(そのまわりを除く) */
  /**
   * この車の限界(計測モードの車のプロファイル)。路面のキー(paved・dirt・grass・snow)ごとに { left, right, brake, launch }(G。測れていなければ null)。
   * 今いる路面の分を、緑の破線で描く(上 = 発進、下 = ブレーキ、左右 = 旋回。4 つの楕円の 1/4 をつなぐ。両方の軸が測れた 1/4 だけ)
   */
  limits: null,
  setLimits(bySurface) { this.limits = bySurface || null; this._redraw(); },

  setRows(data, rows, marks) {
    if (!data || !data.gLat || !data.gLong || !data.surface) {
      this.data = null; this.rows = null; this._mask = null;
      this.est = { rings: new Map(), rmax: 0 };
      this.base = { x: 0, y: 0, n: 0, wheels: null };
      this.scaleMax = this.kind === "load" ? LT_DEFAULT_MAX : GG_DEFAULT_MAX;
      this._rebuild();
      return;
    }
    this.data = data;
    this.marks = marks || null;
    this.rows = rows || data.t.map((_, i) => i);
    this._mask = ggExcludeMask(data, this.marks);
    this.base = ltBaseline(data, this.rows, this._mask, this.smoothSec);
    this.ggEst = ggEstimate(data, this.rows, this._mask, (d, i) => this._ggAt(i));
    this._estimate();
    this._rebuild();
  },

  /** 今の描くもの(kind)で、円と表示の上限を作り直す */
  _estimate() {
    if (!this.data) return;
    if (this.kind === "load") {
      this.est = ggEstimate(this.data, this.rows, this._mask, (d, i) => this._loadAt(i), LT_SANE);
      this.scaleMax = this.est.rmax > 0 ? Math.max(0.2, this.est.rmax * GG_CAP_RATIO) : LT_DEFAULT_MAX;
    } else {
      this.est = this.ggEst;
      this.scaleMax = this.est.rmax > 0 ? Math.max(1, this.est.rmax * GG_CAP_RATIO) : GG_DEFAULT_MAX;
    }
  },

  /** 行 i の荷重移動 [右 − 左, 前 − 後ろ](基準からの差)。値が無ければ null */
  _loadAt(i) {
    const v = ltRaw(this.data, i, this.smoothSec);
    return v ? [v[0] - this.base.x, v[1] - this.base.y] : null;
  },

  /** 行 i の [横 G, 前後 G](「ならし」の幅でならす) */
  _ggAt(i) { return ggAt(this.data, i, this.smoothSec); },

  /** 今の描くものでの、行 i の [横, 縦] */
  _at(i) { return this.kind === "load" ? this._loadAt(i) : this._ggAt(i); },

  setTrail(sec) { this.trailSec = sec; this._redraw(); },
  setCloud(on) { this.cloud = on; this._rebuild(); },
  /** 荷重移動・摩擦円をならす幅(秒)を変える。基準と円も、その幅でならした値で作り直す(グラフの系列は呼ぶ側で loadSeries・gripSeries から作り直す) */
  setSmooth(sec) {
    this.smoothSec = sec > 0 ? sec : LT_SMOOTH_SEC;
    if (this.data) {
      this.base = ltBaseline(this.data, this.rows, this._mask, this.smoothSec);
      this.ggEst = ggEstimate(this.data, this.rows, this._mask, (d, i) => this._ggAt(i));
      this._estimate();
    }
    this._rebuild();
  },
  setKind(kind) {
    this.kind = kind === "gg" ? "gg" : "load";
    if (this.data) this._estimate();
    else this.scaleMax = this.kind === "load" ? LT_DEFAULT_MAX : GG_DEFAULT_MAX;
    this._rebuild();
  },

  /** 読み取り値用: 今の行の { lat, lon, mag, group, ring(その路面の G の円の半径。摩擦円を描いていないときと、無いときは null) }。値が無ければ null */
  info(pos) {
    if (!this.data || pos == null || pos < 0) return null;
    const v = this._ggAt(pos);
    if (!v) return null;
    const group = ggSurfaceGroup(this.data, pos);
    const ring = this.kind === "gg" && group >= 0 && this.est.rings.has(group) ? this.est.rings.get(group).r : null;
    return { lat: v[0], lon: v[1], mag: Math.hypot(v[0], v[1]), group, ring };
  },

  /** 読み取り値用: 今の行の荷重移動 { side(右 − 左), front(前 − 後ろ)}(縮みの差。基準からの差)。値が無ければ null */
  loadInfo(pos) {
    if (!this.data || pos == null || pos < 0) return null;
    const v = this._loadAt(pos);
    return v ? { side: v[0], front: v[1] } : null;
  },

  /**
   * グラフ用の荷重移動の系列(全部の行。基準は選んでいるレースのもの)。{ long, lat }(%。小数 1 桁。出せない行は null)。
   * G と同じ段に重ねるので、向きを G に合わせる: long は正 = 後ろが沈む(加速の側)、lat は正 = 左が沈む(右コーナーの側)
   */
  loadSeries() {
    const d = this.data;
    if (!d) return { long: null, lat: null };
    const long = new Array(d.t.length), lat = new Array(d.t.length);
    for (let i = 0; i < d.t.length; i++) {
      const v = this._loadAt(i);
      long[i] = v ? Math.round(-v[1] * 1000) / 10 : null;
      lat[i] = v ? Math.round(-v[0] * 1000) / 10 : null;
    }
    return { long, lat };
  },

  /**
   * グラフ用の「摩擦円の %」(全部の行)。合成 G ÷ その路面の円(摩擦円の左上の「円の %」と同じ)。
   * 4 輪の路面が混ざっている行(舗装とダートの境など)は、円がある路面の車輪の円の平均で割る(_ringOfRow)。
   * 円が無い路面・空中・値が無い行は null。はね上がり(GG_SANE_G 超)も null
   */
  gripSeries() {
    const d = this.data;
    if (!d) return null;
    const out = new Array(d.t.length);
    for (let i = 0; i < d.t.length; i++) {
      const m = ggMag(d, i, this.smoothSec), ring = this._ringOfRow(i);
      out[i] = m == null || ring == null || !(ring > 0) ? null : Math.round((m / ring) * 1000) / 10;
    }
    return out;
  },

  /**
   * 行 i の摩擦円(G)の円の半径。路面が混ざっていれば、4 輪のうち円がある路面の車輪の、円の平均
   * (雪・芝は円を作れるほど走らないことが多いので、その車輪は数えない)。無ければ null
   */
  _ringOfRow(i) {
    const d = this.data, grp = ggSurfaceGroup(d, i), rings = this.ggEst.rings;
    if (grp >= 0) return rings.has(grp) ? rings.get(grp).r : null;
    if (grp !== -3) return null;
    let sum = 0, n = 0;
    for (const w of LT_WHEELS) {
      const k = d.surface[w][i] & 7, g = GG_PAVED.has(k) ? 0 : k;
      if (rings.has(g)) { sum += rings.get(g).r; n++; }
    }
    return n ? sum / n : null;
  },

  /** 読み取り値用: 今の行の 4 輪の縮み { fl, fr, rl, rr }(0 = 伸び切り、1 = 縮み切り)。値が無ければ null */
  wheelInfo(pos) {
    if (!this.data || pos == null || pos < 0) return null;
    return ltWheels(this.data, pos);
  },

  /** 今の描くものでの、今の行の { mag, group, ring }(円の強調と割合に使う) */
  _ringInfo(pos) {
    const v = this._at(pos);
    if (!v) return null;
    const group = ggSurfaceGroup(this.data, pos);
    const ring = group >= 0 && this.est.rings.has(group) ? this.est.rings.get(group).r : null;
    return { mag: Math.hypot(v[0], v[1]), group, ring };
  },

  /** [横, 縦] → 画面の位置 [x, y, 上限で丸めたか]。横は右が正、縦は上が正(摩擦円は加速、荷重移動は前が沈む) */
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

    // 目盛りの円(摩擦円は 0.5G か 1G ごと、荷重移動は縮みの差の 10% か 20% ごと)と軸
    const load = this.kind === "load";
    let step = load ? (this.scaleMax <= 0.6 ? 0.1 : 0.2) : (this.scaleMax <= 2.2 ? 0.5 : 1);
    while (step * s < 30 * dpr) step *= 2;   // 図が小さいと目盛りの数字が重なるので、間隔を広げる
    g.lineWidth = dpr;
    g.strokeStyle = "rgba(255,255,255,0.07)";
    g.fillStyle = "rgba(154,161,171,0.7)";
    g.font = font(10);
    g.textAlign = "left";
    for (let v = step; v < this.scaleMax - step * 0.1; v += step) {
      circle(v); g.stroke();
      g.fillText(load ? `${Math.round(v * 100)}%` : `${v.toFixed(1)}G`, cx + v * s + 2 * dpr, cy + 11 * dpr);
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
        const v = this._at(i);
        if (!v || Math.hypot(v[0], v[1]) > this.scaleMax) continue;
        const [x, y] = this._xy(v[0], v[1]);
        g.fillRect(x - px / 2, y - px / 2, px, px);
      }
    }

    // 向きの名前
    g.fillStyle = "rgba(154,161,171,0.9)";
    g.font = font(11);
    g.textAlign = "center";
    // 荷重移動では、点が寄った側のタイヤが沈んでいる(ブレーキで前輪、加速で後輪、右コーナーで左のタイヤ)
    g.fillText(load ? t("前輪") : t("加速"), cx, 13 * dpr);
    g.fillText(load ? t("後輪") : t("ブレーキ"), cx, G.h - 5 * dpr);
    g.textAlign = "left";
    g.fillText(load ? t("左のタイヤ") : t("左"), 5 * dpr, cy - 5 * dpr);
    g.textAlign = "right";
    g.fillText(load ? t("右のタイヤ") : t("右"), G.w - 5 * dpr, cy - 5 * dpr);
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
    if (!d || pos == null || pos < 0) return;
    if (!this._at(pos)) {
      // 荷重移動の点が出せない行(空中: どれかの車輪が伸び切り)でも、4 輪の棒は出す(浮いて伸び切った車輪が見える)
      if (this.kind === "load" && ltWheels(d, pos)) {
        this._drawWheels(g, dpr, pos);
        if (ggSurfaceGroup(d, pos) === -2) {
          g.fillStyle = LT_LIGHT_COLOR;
          g.font = `${11 * dpr}px "Yu Gothic UI", sans-serif`;
          g.textAlign = "left";
          g.fillText(t("空中"), 6 * dpr, 28 * dpr);
        }
      }
      return;
    }
    const slipOf = (i) => {
      const a = d.slipF[i], b = d.slipR[i];
      return a == null && b == null ? 0 : Math.max(a ?? 0, b ?? 0);
    };

    // 今いる路面の円を強調(舗装以外なら、舗装の円の内側にこの円が出る)
    const info = this._ringInfo(pos);
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

    // この車の限界(計測モード)。今いる路面の分。G の限界なので摩擦円だけ
    const lim = this.kind === "gg" && this.limits && info && GG_LIMIT_SURFACE[info.group] ? this.limits[GG_LIMIT_SURFACE[info.group]] : null;
    if (lim) {
      g.strokeStyle = GG_LIMIT_COLOR;
      g.lineWidth = 2 * dpr;
      g.setLineDash([6 * dpr, 4 * dpr]);
      // [横の半径, 縦の半径, 始まりの角, 終わりの角](canvas の角: 0 = 右、π/2 = 下)
      const [onR, onL, onU, onD] = [lim.right, lim.left, lim.launch, lim.brake];
      const quads = [[onR, onU, -Math.PI / 2, 0], [onR, onD, 0, Math.PI / 2],
                     [onL, onD, Math.PI / 2, Math.PI], [onL, onU, Math.PI, Math.PI * 1.5]];
      let drew = false;
      for (const [rx, ry, a0, a1] of quads) {
        if (rx == null || ry == null) continue;
        g.beginPath(); g.ellipse(G.cx, G.cy, rx * G.s, ry * G.s, 0, a0, a1); g.stroke();
        drew = true;
      }
      g.setLineDash([]);
      if (drew) {
        g.fillStyle = GG_LIMIT_COLOR;
        g.font = `${10 * dpr}px "Yu Gothic UI", sans-serif`;
        g.textAlign = "right";
        g.fillText(t("この車の限界"), G.w - 6 * dpr, 28 * dpr);
        g.textAlign = "left";
      }
    }

    // 4 輪の棒(荷重移動のとき。点と軌跡の下に)
    if (this.kind === "load") this._drawWheels(g, dpr, pos);

    // 軌跡(つながっている直近の数秒。古いほど薄く)
    if (this.trailSec > 0) {
      let k = pos;
      while (k > 0 && d.t[pos] - d.t[k - 1] <= this.trailSec && d.t[k] - d.t[k - 1] <= 0.3 && this._at(k - 1)) k--;
      for (let j = k + 1; j <= pos; j++) {
        const a = this._at(j - 1), b = this._at(j);
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
    const v = this._at(pos), [px, py, capped] = this._xy(v[0], v[1]);
    const over = slipOf(pos) > 1;
    g.lineWidth = 1.5 * dpr;
    g.beginPath();
    g.arc(px, py, GG_DOT_R * dpr, 0, Math.PI * 2);
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

    // 合成 G(荷重移動なら縮みの差)と、その路面の円に対する割合(左上。円と重ならない隅)
    g.fillStyle = "#e6e8eb";
    g.font = `${11 * dpr}px "Yu Gothic UI", sans-serif`;
    g.textAlign = "left";
    g.fillText(this.kind === "load" ? t("縮みの差 {pct}%", { pct: Math.round(info.mag * 100) }) : t("合成 {g} G", { g: info.mag.toFixed(2) }),
      6 * dpr, 28 * dpr);
    if (ring != null && ring > 0) {
      g.fillStyle = info.mag > ring ? GG_SLIP_COLOR : "#9aa1ab";
      g.fillText(t("円の {pct}%", { pct: Math.round((info.mag / ring) * 100) }), 6 * dpr, 42 * dpr);
    }

  },

  /**
   * 4 輪の棒。車を上から見たタイヤの並びで、円のまわりに置く(上が前輪、左が左のタイヤ。前後より左右を近く)。
   * 棒の長さ = サスペンションの伸び(実際のばねのように、荷重がかかって沈むほど短くなる。下の端が地面)。「ならし」によらず、いつも前後 LT_SMOOTH_SEC(0.25 秒)でならす。
   * 黄の細い線 = その幅の中でいちばん縮んだ所(ならさない記録の値。ならした棒が低く出ても、荷重の山がどこまで行ったかが分かる。伸びた側は線が混むので描かない)。
   * 白の横線 = まっすぐ一定の速さのときの長さ(基準)。幅の中で一度でも浮きかけ(LT_LIGHT 未満)なら赤、縮み切りに近い(LT_BOTTOM 超)なら橙。
   * 点と軌跡より先に描き、半透明にする(円に重なっても点が見えるように)
   */
  _drawWheels(g, dpr, pos) {
    const v = ltWheelsSmooth(this.data, pos, LT_SMOOTH_SEC);
    if (!v) return;
    const G = this._geom, R = this.scaleMax * G.s;
    const bw = Math.max(9 * dpr, R * 0.13), bh = R * 0.55;
    const dx = R * 0.5, dy = R * 0.68;                 // 左右の間(中心から)は前後より狭く
    const at = { fl: [-dx, -dy], fr: [dx, -dy], rl: [-dx, dy], rr: [dx, dy] };
    const clamp = (x) => Math.max(0, Math.min(1, x));
    for (const w of LT_WHEELS) {
      const x = G.cx + at[w][0] - bw / 2, yGround = G.cy + at[w][1] + bh / 2;
      const s = clamp(v.avg[w]), len = bh * (1 - s);
      const light = v.lo[w] < LT_LIGHT, bottom = v.hi[w] > LT_BOTTOM;
      // 縮み切りに近いと棒はほとんど 0 の長さになるので、下地も橙にして目立たせる
      g.globalAlpha = bottom ? 0.45 : 0.5;
      g.fillStyle = bottom ? GG_SLIP_COLOR : "#3a3f47";
      g.fillRect(x, yGround - bh, bw, bh);
      g.globalAlpha = 0.75;
      g.fillStyle = light ? LT_LIGHT_COLOR : bottom ? GG_SLIP_COLOR : GG_NORMAL_COLOR;
      g.fillRect(x, yGround - len, bw, Math.max(dpr, len));
      g.globalAlpha = 1;
      // 山の印。棒の端とほとんど重なるなら描かない(ならした値と変わらない)
      const yPeak = yGround - bh * (1 - clamp(v.hi[w]));
      if (Math.abs(yPeak - (yGround - len)) >= 1.5 * dpr) {
        g.fillStyle = "#ffcc33";
        g.fillRect(x, yPeak - dpr, bw, 2 * dpr);
      }
      const b = this.base.wheels && this.base.wheels[w];
      if (b != null) {
        g.fillStyle = "#e6e8eb";
        g.fillRect(x - 2 * dpr, yGround - bh * (1 - b) - dpr / 2, bw + 4 * dpr, Math.max(1, dpr));
      }
    }
  },
};

if (typeof module !== "undefined" && module.exports) {
  module.exports = { FrictionCircle, ggAt, ggMag, ggSurfaceGroup, ggExcludeMask, ggPercentile, ggEstimate, ltRaw, ltWheels, ltWheelsSmooth, ltBaseline, GG_RING_MIN_ROWS, GG_CAP_RATIO };
}
