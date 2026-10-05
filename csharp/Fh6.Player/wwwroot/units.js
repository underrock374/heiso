// 表示の単位(速度・距離・標高)。記録のデータはメートル法(速度 km/h、距離と標高 m)のまま持ち、画面に出すときだけ直す。
// 設定の「画面」→「単位」で、それぞれ 自動 / メートル / マイル を選ぶ。自動は、Windows の地域が米国か英国ならマイル、それ以外はメートル
// (地域は C# が window.HEISO_I18N.region で渡す。.NET の RegionInfo.IsMetric は英国をメートル法とするので、国の名前で決める)。
"use strict";

const MI_M = 1609.344;
const FT_M = 0.3048;
const KMH_PER_MPH = 1.609344;
const IMPERIAL_REGIONS = ["US", "GB"];

const Units = {
  speed: "kmh",   // "kmh" / "mph"
  dist: "m",      // "m"(m・km)/ "ft"(ft・mi)
  elev: "m",      // "m" / "ft"

  /** 自動のときの単位がマイルか(Windows の地域が米国か英国) */
  autoImperial() {
    const region = ((window.HEISO_I18N || {}).region || "").toUpperCase();
    return IMPERIAL_REGIONS.includes(region);
  },

  /** 設定(unitSpeed / unitDist / unitElev: "auto" か単位)から、今使う単位を決める */
  use(settings) {
    const imp = this.autoImperial();
    const pick = (v, metric, imperial) => (v === metric || v === imperial ? v : imp ? imperial : metric);
    this.speed = pick(settings.unitSpeed, "kmh", "mph");
    this.dist = pick(settings.unitDist, "m", "ft");
    this.elev = pick(settings.unitElev, "m", "ft");
  },

  // ---- 速度(元は km/h) ----
  speedOf(kmh) { return kmh == null ? null : this.speed === "mph" ? kmh / KMH_PER_MPH : kmh; },
  speedLabel() { return this.speed === "mph" ? "mph" : "km/h"; },
  fmtSpeed(kmh) { return kmh == null || !isFinite(kmh) ? "-" : `${this.speedOf(kmh).toFixed(0)} ${this.speedLabel()}`; },

  // ---- 標高・高さ(元は m) ----
  elevOf(m) { return m == null ? null : this.elev === "ft" ? m / FT_M : m; },
  elevLabel() { return this.elev === "ft" ? "ft" : "m"; },
  fmtElev(m) { return m == null || !isFinite(m) ? "-" : `${this.elevOf(m).toFixed(0)} ${this.elevLabel()}`; },

  // ---- 距離(元は m) ----
  /** 距離: メートルなら 1000 m 未満は m、以上は km。マイルなら 1000 ft 未満は ft、以上は mi */
  fmtDist(m) {
    if (m == null || !isFinite(m)) return "-";
    if (this.dist === "ft") {
      const ft = m / FT_M;
      return Math.abs(ft) >= 1000 ? `${(m / MI_M).toFixed(2)} mi` : `${Math.round(ft)} ft`;
    }
    return Math.abs(m) >= 1000 ? `${(m / 1000).toFixed(2)} km` : `${Math.round(m)} m`;
  },
  /** 短い長さ(曲がりの半径など): m か ft */
  fmtLen(m) {
    if (m == null || !isFinite(m)) return "-";
    return this.dist === "ft" ? `${Math.round(m / FT_M)} ft` : `${Math.round(m)} m`;
  },

  /**
   * 距離の横軸の目盛り(マイルのときだけ。メートルは uPlot のままで切りのよい数になる)。
   * 表示範囲 [min, max](m)に count 本くらい、ft か mi の切りのよい間隔で。{ splits: 目盛りの位置(m), fmt: 位置 → 文字 }
   */
  distTicks(min, max, count) {
    if (this.dist !== "ft" || !(max > min)) return null;
    const useMi = (max - min) / FT_M > 3000;
    const unit = useMi ? MI_M : FT_M;
    const raw = (max - min) / unit / Math.max(1, count);
    const p = Math.pow(10, Math.floor(Math.log10(raw)));
    const step = [1, 2, 5, 10].map((k) => k * p).find((s) => s >= raw) * unit;
    const splits = [];
    for (let v = Math.ceil(min / step) * step; v <= max + 1e-6; v += step) splits.push(v);
    const digits = useMi ? Math.max(0, -Math.floor(Math.log10(step / unit) + 1e-9)) : 0;
    return { splits, fmt: (v) => useMi ? `${(v / MI_M).toFixed(digits)} mi` : `${Math.round(v / FT_M)} ft` };
  },

  /** コース図の縮尺の線: 目安の長さ(m)に近い、切りのよい長さ。{ m: 長さ(m), label } */
  mapScale(target) {
    const nice = this.dist === "ft"
      ? [[50, "ft"], [100, "ft"], [200, "ft"], [500, "ft"], [1000, "ft"], [2000, "ft"], [0.5, "mi"], [1, "mi"]]
        .map(([v, u]) => ({ m: u === "mi" ? v * MI_M : v * FT_M, label: `${v} ${u}` }))
      : [10, 20, 50, 100, 200, 250, 500, 1000, 2000].map((v) => ({ m: v, label: v >= 1000 ? `${v / 1000} km` : `${v} m` }));
    return nice.reduce((a, b) => (Math.abs(b.m - target) < Math.abs(a.m - target) ? b : a));
  },

  /** コース図の範囲のボタン(全体以外): [{ m: 一辺(m), label }] */
  mapZooms() {
    return this.dist === "ft"
      ? [{ m: 1609, label: "1 mi" }, { m: 805, label: "0.5 mi" }, { m: 457, label: "1500 ft" }, { m: 183, label: "600 ft" }]
      : [{ m: 2000, label: "2 km" }, { m: 1000, label: "1 km" }, { m: 500, label: "500 m" }, { m: 200, label: "200 m" }];
  },
};
