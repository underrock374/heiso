// パワーカーブ(性能モードの右の欄。摩擦円の下)。
// 横軸 = 回転数、縦軸 = 出力(hp)。カーブは C# の PowerAnalysis が、その車(車種と PI の組)の全開の行から作ったもの
// (回転数 250 rpm ごとの 75 パーセンタイル。変速の後 1 秒は除く)。今の点と、今すぐ次のギアへ変速した場合の点(回転数 × ギア比)を重ね、
// 今のギアの最適な範囲(変速の得失が ±3% 以内)を薄い帯と破線で、リミッターを赤の点線で出す。系列の perfCar が、行ごとの車(perf.cars の位置)
"use strict";

const PW_CURVE = "#e6e8eb";
const PW_NOW = "#ffcc33";
const PW_AFTER = "#e0a33a";

/** カーブ [[rpm, hp], ...] の rpm の出力(区切りの間は直線で。範囲の外は null) */
function pwAt(curve, rpm) {
  if (!curve || !curve.length || rpm == null) return null;
  const half = 125;
  if (rpm < curve[0][0] - half || rpm > curve[curve.length - 1][0] + half) return null;
  if (rpm <= curve[0][0]) return curve[0][1];
  for (let k = 1; k < curve.length; k++) {
    if (rpm <= curve[k][0]) {
      const [r0, p0] = curve[k - 1], [r1, p1] = curve[k];
      return p0 + (p1 - p0) * (rpm - r0) / (r1 - r0);
    }
  }
  return curve[curve.length - 1][1];
}

const PowerChart = {
  canvas: null,
  data: null,       // 系列(rpm・gear・accel・perfCar・perf を使う)
  _last: null,

  init(canvas) {
    this.canvas = canvas;
    new ResizeObserver(() => this.draw(this._last)).observe(canvas);
  },

  setData(data) {
    this.data = data && data.perf ? data : null;
    this.draw(this._last);
  },

  /** 見出しの横に、カーブの元(この車の記録を合わせたものか、この記録だけか) */
  note(pos) {
    const car = this.carAt(pos);
    if (!car || !car.curve) return "";
    const s = car.source;
    if (!s) return t("この記録から");
    // 記録の数とレースの数(1 本の記録に何回もレースが入ることがあるため、両方出す)
    if (!s.races) return t("この車の記録 {n} 本から", { n: s.recordings });
    return s.other ? t("この車の記録 {n} 本(レース {r} 回ほか)から", { n: s.recordings, r: s.races })
                   : t("この車の記録 {n} 本(レース {r} 回)から", { n: s.recordings, r: s.races });
  },

  /** 行 pos の車の性能(perf.cars の 1 つ)。無ければ、カーブのある最初の車 */
  carAt(pos) {
    const d = this.data;
    if (!d) return null;
    const k = pos != null && pos >= 0 ? d.perfCar[pos] : null;
    if (k != null && d.perf.cars[k]) return d.perf.cars[k];
    return d.perf.cars.find((c) => c.curve) || null;
  },

  /**
   * 読み取り値用: { hp(今の回転数の全開の出力), opt(今のギアの最適な変速回転数。null = リミッターまで / undefined = 分からない),
   * range(今のギアの最適な範囲 [下, 上]。無ければ null), limiter(リミッターの回転数),
   * ratio(次のギアとの比。分からなければ null), curve(カーブがあるか) }
   */
  info(pos) {
    const car = this.carAt(pos), d = this.data;
    if (!car || pos == null || pos < 0) return null;
    const g = d.gear[pos];
    return {
      hp: car.curve ? pwAt(car.curve, d.rpm[pos]) : null,
      opt: g != null && g in car.opt ? car.opt[g] : undefined,
      ratio: g != null && car.ratios[g] ? car.ratios[g] : null,
      range: g != null && car.range && car.range[g] ? car.range[g] : null,
      limiter: car.limiter ?? null,
      nextUsed: g != null && !!car.gears && car.gears.includes(g + 1),   // 次のギアをこの記録で使ったか
      curve: !!car.curve,
    };
  },

  draw(pos) {
    this._last = pos;
    const c = this.canvas;
    if (!c || c.clientWidth < 2) return;
    const dpr = devicePixelRatio || 1;
    c.width = Math.round(c.clientWidth * dpr);
    c.height = Math.round(c.clientHeight * dpr);
    const g = c.getContext("2d");
    g.clearRect(0, 0, c.width, c.height);
    const font = (px) => `${px * dpr}px "Yu Gothic UI", sans-serif`;
    const car = this.carAt(pos);
    if (!car || !car.curve) {
      g.fillStyle = "#9aa1ab";
      g.font = font(11);
      g.fillText(t("全開の区間が少なく、パワーカーブを作れません"), 8 * dpr, 20 * dpr);
      return;
    }
    const d = this.data, curve = car.curve;
    const L = 38 * dpr, R = 8 * dpr, T = 10 * dpr, B = 18 * dpr;
    const r0 = Math.max(0, Math.floor((curve[0][0] - 500) / 1000) * 1000);
    const r1 = Math.ceil((curve[curve.length - 1][0] + 500) / 1000) * 1000;
    const pTop = Math.ceil(car.maxHp * 1.1 / 100) * 100;
    const X = (r) => L + (c.width - L - R) * (r - r0) / (r1 - r0);
    const Y = (p) => c.height - B - (c.height - B - T) * p / pTop;

    // 目盛り
    g.lineWidth = dpr;
    g.strokeStyle = "rgba(255,255,255,0.08)";
    g.fillStyle = "rgba(154,161,171,0.9)";
    g.font = font(10);
    const pStep = pTop > 600 ? 200 : 100;
    g.textAlign = "right";
    for (let p = 0; p <= pTop; p += pStep) {
      g.beginPath(); g.moveTo(L, Y(p)); g.lineTo(c.width - R, Y(p)); g.stroke();
      g.fillText(String(p), L - 4 * dpr, Y(p) + 3 * dpr);
    }
    g.textAlign = "center";
    const rStep = r1 - r0 > 6000 ? 2000 : 1000;
    for (let r = Math.ceil(r0 / rStep) * rStep; r <= r1; r += rStep) g.fillText(`${r / 1000}k`, X(r), c.height - 4 * dpr);
    g.textAlign = "left";
    g.fillText("hp", 2 * dpr, T + 2 * dpr);

    // カーブ
    g.strokeStyle = PW_CURVE;
    g.lineWidth = 1.8 * dpr;
    g.beginPath();
    curve.forEach(([r, p], k) => (k ? g.lineTo(X(r), Y(p)) : g.moveTo(X(r), Y(p))));
    g.stroke();

    if (pos == null || pos < 0 || d.rpm[pos] == null) return;
    const rpm = d.rpm[pos], gear = d.gear[pos];

    // リミッター(赤の点線)と、今のギアの最適な範囲(得失が ±3% 以内。薄い帯と、両端の破線)
    if (car.limiter != null && car.limiter <= r1) {
      g.strokeStyle = "rgba(255,93,93,0.6)";
      g.setLineDash([2 * dpr, 3 * dpr]);
      g.lineWidth = dpr;
      g.beginPath(); g.moveTo(X(car.limiter), T); g.lineTo(X(car.limiter), c.height - B); g.stroke();
      g.setLineDash([]);
    }
    const range = gear != null && car.range ? car.range[gear] : null;
    if (range) {
      const [lo, hi] = range;
      g.fillStyle = "rgba(224,163,58,0.15)";
      g.fillRect(X(lo), T, Math.max(1, X(hi) - X(lo)), c.height - B - T);
      g.strokeStyle = PW_AFTER;
      g.setLineDash([4 * dpr, 3 * dpr]);
      g.lineWidth = dpr;
      for (const r of hi - lo >= 50 ? [lo, hi] : [lo]) { g.beginPath(); g.moveTo(X(r), T); g.lineTo(X(r), c.height - B); g.stroke(); }
      g.setLineDash([]);
    }

    // 今の点と、今すぐ変速した場合の点(全開のときだけ意味がある。部分開度は今の点を灰色の輪に)
    const now = pwAt(curve, rpm);
    if (now == null) return;
    const full = d.accel[pos] != null && d.accel[pos] >= PERF_FULL_PCT;
    const ratio = gear != null ? car.ratios[gear] : null;
    if (full && ratio) {
      const r2 = rpm * ratio, p2 = pwAt(curve, r2);
      if (p2 != null) {
        g.strokeStyle = PW_AFTER;
        g.setLineDash([3 * dpr, 3 * dpr]);
        g.lineWidth = dpr;
        g.beginPath(); g.moveTo(X(rpm), Y(now)); g.lineTo(X(r2), Y(p2)); g.stroke();
        g.setLineDash([]);
        g.lineWidth = 2 * dpr;
        g.beginPath(); g.arc(X(r2), Y(p2), 4.5 * dpr, 0, Math.PI * 2); g.stroke();
      }
    }
    g.beginPath();
    g.arc(X(rpm), Y(now), 5 * dpr, 0, Math.PI * 2);
    if (full) {
      g.fillStyle = PW_NOW;
      g.strokeStyle = "#000";
      g.lineWidth = 1.5 * dpr;
      g.fill(); g.stroke();
    } else {
      g.strokeStyle = "#9aa1ab";
      g.lineWidth = 1.5 * dpr;
      g.stroke();
    }
  },
};

if (typeof module !== "undefined" && module.exports) {
  module.exports = { PowerChart, pwAt };
}
