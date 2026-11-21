// 摩擦円(g-g ダイアグラム): 前後と横の加速度(G)を 1 つの点で描く。
// 横軸 = 横 G(右が正)、縦軸 = 前後 G(上 = 加速、下 = ブレーキ)。系列の gLat・gLong は C# の LoadedSession.BuildSeries が、
// FH6 のデータ出力の acceleration_x(横。+ = 右)・acceleration_z(前後。+ = 前)を G に直したもの(車体座標)。
//
// 点の位置・円・表示の上限・点群に出す行は、C# の FrictionDynamic が、選んでいるレースとならしの幅で作って HostFriction で送る
// (幅かレースを変えたときに app.js が ViewFriction で頼む)。ここでは、それをキャンバスのどこに描くかだけを決める。
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

const GG_RING_COLOR = { 0: "#cfd6e4", 1: "#e8f1ff", 2: "#d99a52", 4: "#5fc470" };   // 舗装・雪など・ダート・芝(graphs.js の SURFACE の番号)
const GG_SLIP_COLOR = "#ff9f43";
const GG_LIMIT_COLOR = "#3cc68a";            // この車の限界(計測モードのプロファイル)
const GG_LIMIT_SURFACE = { 0: "paved", 1: "snow", 2: "dirt", 4: "grass" };   // 路面の番号(C# の FrictionStatic.SurfaceGroups)→ プロファイルの路面のキー
const GG_NORMAL_COLOR = "#6cb2ff";
const GG_EMPTY_SCALE = { gg: 1.5, load: 0.4 };   // 記録が無い・C# からまだ届いていないときの目盛りの上限(G・縮みの差。C# の DefaultMaxG・DefaultMaxLoad と同じ)
const GG_DOT_R = 7.5;                      // 今の点の半径(px。荷重移動・摩擦円とも。ならす幅によらない)
const LT_WHEELS = ["fl", "fr", "rl", "rr"];
const LT_LIGHT_COLOR = "#ff6b6b";

const FrictionCircle = {
  canvas: null,
  data: null,          // 系列(横軸が時間のもの。t・slipF・slipR・susp・friction を使う)
  f: null,             // C# が作った摩擦円・荷重移動(HostFriction。選んでいるレース・ならしの幅のもの。届くまで null)
  view: null,          // 今描くものの分(f.gg か f.load)
  rings: new Map(),    // 今描くものの、路面 → 円の半径
  scaleMax: GG_EMPTY_SCALE.load,   // 表示の上限
  trailSec: 3,         // 軌跡の長さ(秒。0 = 出さない)
  cloud: true,         // 点群(選んでいる範囲で出た加速度の全部)を薄く出すか
  kind: "load",        // 描くもの: "load"(荷重移動。サスペンションの縮みの差)/ "gg"(摩擦円。G)

  _bg: null,           // 下絵(目盛り・円・点群)
  _geom: null,         // { w, h, cx, cy, s }(s = 1G あたりの px)
  _last: null,

  init(canvas) {
    this.canvas = canvas;
    new ResizeObserver(() => this._rebuild()).observe(canvas);
  },

  /**
   * この車の限界(計測モードの車のプロファイル)。路面のキー(paved・dirt・grass・snow)ごとに { left, right, brake, launch }(G。測れていなければ null)。
   * 今いる路面の分を、緑の破線で描く(上 = 発進、下 = ブレーキ、左右 = 旋回。4 つの楕円の 1/4 をつなぐ。両方の軸が測れた 1/4 だけ)
   */
  limits: null,
  setLimits(bySurface) { this.limits = bySurface || null; this._redraw(); },

  /** 描くものを決める。data は系列(null なら記録なし)、friction は C# から届いた HostFriction(まだなら null) */
  setData(data, friction) {
    this.data = data && data.friction ? data : null;
    this.f = this.data && friction ? friction : null;
    this._pick();
    this._rebuild();
  },

  /** 今の描くもの(kind)の分を選ぶ */
  _pick() {
    this.view = this.f ? (this.kind === "load" ? this.f.load : this.f.gg) : null;
    this.rings = new Map(this.view ? this.view.rings.map((r) => [r.group, r.r]) : []);
    this.scaleMax = this.view ? this.view.scaleMax : GG_EMPTY_SCALE[this.kind];
  },

  /** 今の描くものでの、行 i の [横, 縦](摩擦円は G、荷重移動は基準からの縮みの差)。値が無ければ null */
  _at(i) {
    const v = this.view;
    return v && v.x[i] != null ? [v.x[i], v.y[i]] : null;
  },

  setTrail(sec) { this.trailSec = sec; this._redraw(); },
  setCloud(on) { this.cloud = on; this._rebuild(); },
  setKind(kind) {
    this.kind = kind === "gg" ? "gg" : "load";
    this._pick();
    this._rebuild();
  },

  /** 読み取り値用: 今の行の { lat, lon }(G。「ならし」の幅でならしたもの)。値が無ければ null */
  info(pos) {
    if (!this.f || pos == null || pos < 0 || this.f.gg.x[pos] == null) return null;
    return { lat: this.f.gg.x[pos], lon: this.f.gg.y[pos] };
  },

  /** 読み取り値用: 今の行の荷重移動 { side(右 − 左), front(前 − 後ろ)}(縮みの差。基準からの差)。値が無ければ null */
  loadInfo(pos) {
    if (!this.f || pos == null || pos < 0 || this.f.load.x[pos] == null) return null;
    return { side: this.f.load.x[pos], front: this.f.load.y[pos] };
  },

  /** 読み取り値用: 今の行の 4 輪の縮み { fl, fr, rl, rr }(0 = 伸び切り、1 = 縮み切り。ならさない。伸び切りの行も含める)。値が無ければ null */
  wheelInfo(pos) {
    if (!this.data || pos == null || pos < 0) return null;
    const s = this.data.susp;
    if (!s || LT_WHEELS.some((w) => s[w][pos] == null)) return null;
    return { fl: s.fl[pos], fr: s.fr[pos], rl: s.rl[pos], rr: s.rr[pos] };
  },

  /** 今の描くものでの、今の行の { mag(点の中心からの長さ), group, ring(その路面の円の半径。無ければ null) }(円の強調と割合に使う) */
  _ringInfo(pos) {
    const v = this._at(pos);
    if (!v) return null;
    const group = this.data.friction.group[pos];
    const ring = this.rings.has(group) ? this.rings.get(group) : null;
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
    for (const [grp, r] of this.rings) {
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
      circle(r); g.stroke();
      g.globalAlpha = 1;
    }
    g.setLineDash([]);

    // 点群(C# が選んだ行。表示の上限より外は描かない)
    if (this.cloud && this.view) {
      g.fillStyle = "rgba(150,170,200,0.22)";
      const px = Math.max(1, Math.round(1.6 * dpr));
      for (const i of this.view.cloud) {
        const v = this._at(i);
        if (Math.hypot(v[0], v[1]) > this.scaleMax) continue;
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
      if (this.kind === "load" && this.wheelInfo(pos)) {
        this._drawWheels(g, dpr, pos);
        if (d.friction.group[pos] === -2) {
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
   * 棒の長さ = サスペンションの伸び(実際のばねのように、荷重がかかって沈むほど短くなる。下の端が地面)。
   * 値は C# の FrictionStatic.WheelBars(「ならし」によらず、いつも前後 0.25 秒でならす)。
   * 黄の細い線 = その幅の中でいちばん縮んだ所(ならさない記録の値。ならした棒が低く出ても、荷重の山がどこまで行ったかが分かる。伸びた側は線が混むので描かない)。
   * 白の横線 = まっすぐ一定の速さのときの長さ(基準)。幅の中で一度でも浮きかけなら赤、縮み切りに近ければ橙。
   * 点と軌跡より先に描き、半透明にする(円に重なっても点が見えるように)
   */
  _drawWheels(g, dpr, pos) {
    const bars = this.data.friction.wheels;
    if (bars.fl.avg[pos] == null) return;
    const G = this._geom, R = this.scaleMax * G.s;
    const bw = Math.max(9 * dpr, R * 0.13), bh = R * 0.55;
    const dx = R * 0.5, dy = R * 0.68;                 // 左右の間(中心から)は前後より狭く
    const at = { fl: [-dx, -dy], fr: [dx, -dy], rl: [-dx, dy], rr: [dx, dy] };
    const clamp = (x) => Math.max(0, Math.min(1, x));
    LT_WHEELS.forEach((w, k) => {
      const x = G.cx + at[w][0] - bw / 2, yGround = G.cy + at[w][1] + bh / 2;
      // ならした縮みは 1 万分の 1、いちばん縮んだ値は 1000 分の 1 の整数で届く。状態は 4 輪で 1 つの整数(車輪ごとに 2 ビット: 浮きかけ・縮み切りに近い)
      const bar = bars[w], s = clamp(bar.avg[pos] / 10000), len = bh * (1 - s);
      const bits = bars.state[pos] >> (2 * k), light = (bits & 1) !== 0, bottom = (bits & 2) !== 0;
      // 縮み切りに近いと棒はほとんど 0 の長さになるので、下地も橙にして目立たせる
      g.globalAlpha = bottom ? 0.45 : 0.5;
      g.fillStyle = bottom ? GG_SLIP_COLOR : "#3a3f47";
      g.fillRect(x, yGround - bh, bw, bh);
      g.globalAlpha = 0.75;
      g.fillStyle = light ? LT_LIGHT_COLOR : bottom ? GG_SLIP_COLOR : GG_NORMAL_COLOR;
      g.fillRect(x, yGround - len, bw, Math.max(dpr, len));
      g.globalAlpha = 1;
      // 山の印。棒の端とほとんど重なるなら描かない(ならした値と変わらない)
      const yPeak = yGround - bh * (1 - clamp(bar.hi[pos] / 1000));
      if (Math.abs(yPeak - (yGround - len)) >= 1.5 * dpr) {
        g.fillStyle = "#ffcc33";
        g.fillRect(x, yPeak - dpr, bw, 2 * dpr);
      }
      const b = this.f && this.f.baseWheels ? this.f.baseWheels[k] : null;   // 基準の長さ(C# の FrictionDynamic.BuildBaseline)
      if (b != null) {
        g.fillStyle = "#e6e8eb";
        g.fillRect(x - 2 * dpr, yGround - bh * (1 - b) - dpr / 2, bw + 4 * dpr, Math.max(1, dpr));
      }
    });
  },
};

if (typeof module !== "undefined" && module.exports) {
  module.exports = { FrictionCircle };
}
