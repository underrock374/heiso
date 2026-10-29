// 計測モード(docs/spec.md「計測モード」): 車のプロファイルの埋まり具合・限界・次の 1 本の指示。
// 値は C# の LoadedSession.BuildMeasure(Fh6.Core の CarProfile・MeasurementEvaluator)が作る。ここは見せ方だけ。
// 選んでいるレースの車と、そのレースの補助の設定の組(限界は組ごと)を出す。レースが「全体」なら記録の最初の車
"use strict";

const MEASURE_SURFACES = { paved: () => t("舗装"), dirt: () => t("ダート"), grass: () => t("芝・荒れ地"), snow: () => t("雪など") };
const MEASURE_BAND = {
  enough: { color: "#3cc68a", name: () => t("足りている") },
  short: { color: "#ff9f43", name: () => t("足りない") },
  unused: { color: "#3a3f49", name: () => t("使わない") },
  unreachable: { color: "#22252b", name: () => t("届かない") },
};
/** 指示の上限(多すぎると、次の 1 本で何をするか分からなくなる) */
const MEASURE_MAX_ADVICE = 3;

const Measure = {
  data: null,      // { cars: [...], races: [...] }

  setData(m) { this.data = m || null; },

  /** raceIndex: 選んでいるレースの番号(全体なら null) */
  render(el, raceIndex) {
    const d = this.data;
    if (!d || !d.cars || !d.cars.length) {
      el.innerHTML = `<p class="muted">${escapeHtml(t("この記録には、計測に使える走り(車が分かる、レース中か走行中の行)がありません"))}</p>`;
      return;
    }
    const race = raceIndex != null ? d.races[raceIndex] : null;
    const car = d.cars.find((c) => race && c.key === race.car) || d.cars[0];
    const unknownKey = "abs=?,tcs=?,stm=?,lc=?";
    const key = race && car.groups[race.assistKey] ? race.assistKey : car.groups[unknownKey] ? unknownKey : Object.keys(car.groups)[0];
    const group = car.groups[key];
    const assists = race ? race.assists : null;

    el.innerHTML = `
      <div class="mHead">
        <b>${escapeHtml(car.name || t("車種 {key}", { key: car.key }))}</b>
        <span class="muted">${escapeHtml(t("PI {pi} ・ 記録 {n} 本({parts} 区間)・ 走った時間 {min} 分", { pi: car.pi, n: car.recordings, parts: car.parts, min: Math.round(car.seconds / 60) }))}</span>
        <span class="mAssist">${escapeHtml(this.assistText(assists))}</span>
      </div>
      <div class="mGrid">
        <section class="mBox"><h3>${escapeHtml(t("次の 1 本"))}</h3>${this.adviceHtml(group.advice, car)}</section>
        <section class="mBox"><h3>${escapeHtml(t("パワーカーブの埋まり具合"))}</h3>${this.curveHtml(car)}</section>
        <section class="mBox"><h3>${escapeHtml(t("限界"))}</h3>${this.limitsHtml(group.limits, key === unknownKey)}</section>
      </div>`;
  },

  assistText(a) {
    const v = (x, on, off) => (x === "on" ? on : x === "off" ? off : "?");
    if (!a) return t("補助の設定: 分からない(「名前を付ける」で付けると、限界を設定ごとに分けて出します)");
    const shift = a.shift === "auto" ? t("AT") : a.shift === "manual" ? t("手動") : "?";
    const oo = (x) => v(x, t("オン"), t("オフ"));
    return t("補助の設定: ABS {abs} ・ TC {tcs} ・ STM {stm} ・ LC {lc} ・ 変速 {shift}", { abs: oo(a.abs), tcs: oo(a.tcs), stm: oo(a.stm), lc: oo(a.lc), shift });
  },

  adviceHtml(list, car) {
    const items = (list || []).slice(0, MEASURE_MAX_ADVICE).map((a) => `<li>${escapeHtml(this.adviceText(a))}</li>`);
    const more = (list || []).length - items.length;
    if (!items.length) return `<p class="mDone">${escapeHtml(t("足りないものはありません。この車のプロファイルはそろっています"))}</p>`;
    return `<ol class="mAdvice">${items.join("")}</ol>`
      + (more > 0 ? `<p class="muted">${escapeHtml(t("ほかに {n} 件。上のものを測ると、次を出します", { n: more }))}</p>` : "")
      + `<p class="muted">${escapeHtml(t("走った記録を開くたびに、プロファイルに足して指示を出し直します"))}</p>`;
  },

  adviceText(a) {
    const surface = MEASURE_SURFACES[a.surface] ? MEASURE_SURFACES[a.surface]() : a.surface;
    const side = a.side === "left" ? t("左") : t("右");
    const rpm = (v) => Math.round(v).toLocaleString("en-US");
    switch (a.kind) {
      case "FirstRun": return t("まず、この車でふつうに 1 本走ってください(ギア比とパワーカーブを取ります)。AT なら、計測のあいだだけ手動の変速にすると、狙った回転数で測れます");
      case "RpmBand": return t("{from}〜{to} rpm が足りません。{gear} 速で {speed}({from} rpm)まで落としてから、平らな直線で全開にしてください", { from: rpm(a.from), to: rpm(a.to), gear: a.gear, speed: Units.fmtSpeed(a.kmh) });
      case "GearRows": return t("{gear} 速を全開で走った時間が足りません(ギア比を測るため)。長い直線で、{gear} 速の全開を数秒", { gear: a.gear });
      case "Brake": return t("{surface}のブレーキの限界がまだです。平らな{surface}の直線で、{speed} くらいからブレーキを奥まで踏んで止まってください(2〜3 回)", { surface, speed: Units.fmtSpeed(80) });
      case "Corner": return t("{surface}の{side}旋回の限界がまだです。一定の半径の{side}カーブで、少しずつ速度を上げて、タイヤが滑り出すまで回ってください", { surface, side });
      case "Launch": return t("{surface}の発進がまだです。止まった状態から、全開で発進してください(2〜3 回)", { surface });
      default: return a.kind;
    }
  },

  curveHtml(car) {
    const rpm = (v) => Math.round(v).toLocaleString("en-US");
    const facts = [];
    if (car.maxHp != null) facts.push(t("最大 {hp} hp({rpm} rpm)", { hp: Math.round(car.maxHp), rpm: rpm(car.peakRpm) }));
    if (car.limiter != null) facts.push(t("リミッター {rpm} rpm", { rpm: rpm(car.limiter) }));
    const ratios = Object.entries(car.ratios || {}).sort((a, b) => a[0] - b[0]).map(([g, r]) => `${g}→${Number(g) + 1} ${r.toFixed(2)}`);
    if (ratios.length) facts.push(t("ギア比 {list}", { list: ratios.join(" / ") }));
    if (car.top) facts.push(t("最高速 {speed}({gear} 速 {rpm} rpm)", { speed: Units.fmtSpeed(car.top.kmh), gear: car.top.gear, rpm: rpm(car.top.rpm) }));
    const bands = car.bands || [];
    if (!bands.length) return `<p class="muted">${escapeHtml(t("まだ全開の走りが少なく、帯を出せません"))}</p>`;
    // 帯の行: 500 rpm ごとの状態。下にギアごとの行数
    const gears = Object.keys(car.coverage || {}).map(Number).sort((a, b) => a - b);
    const head = bands.map((b) => `<th>${(b.from / 1000).toFixed(1)}k</th>`).join("");
    const state = bands.map((b) => `<td class="mBand" style="background:${MEASURE_BAND[b.state].color}" title="${escapeHtml(`${rpm(b.from)}〜${rpm(b.to)} rpm: ${MEASURE_BAND[b.state].name()}(${b.rows})`)}">${b.state === "unused" || b.state === "unreachable" ? "" : b.rows}</td>`).join("");
    const rows = gears.map((g) => `<tr><th>${escapeHtml(t("{gear} 速", { gear: g }))}</th>${bands.map((b) => { const n = (car.coverage[g] || {})[b.from] || 0; return `<td>${n || ""}</td>`; }).join("")}</tr>`).join("");
    const legend = Object.values(MEASURE_BAND).map((s) => `<span><i style="background:${s.color}"></i>${escapeHtml(s.name())}</span>`).join("");
    return `<p class="mFacts">${facts.map(escapeHtml).join(" ・ ")}</p>
      <div class="mTableBox"><table class="mTable"><tr><th></th>${head}</tr><tr><th>${escapeHtml(t("合計"))}</th>${state}</tr>${rows}</table></div>
      <p class="mLegend">${legend}</p>`;
  },

  limitsHtml(limits, unknown) {
    const val = (l) => (l && l.measured ? `<b>${l.g.toFixed(2)} G</b> <span class="muted">(${l.rows})</span>`
      : `<span class="mShort">${escapeHtml(t("まだ({rows} / 60 行)", { rows: l ? l.rows : 0 }))}</span>`);
    const rows = Object.entries(limits || {}).map(([s, l]) => {
      const name = MEASURE_SURFACES[s] ? MEASURE_SURFACES[s]() : s;
      const speeds = Object.entries(l.brakeBySpeed || {}).filter(([, v]) => v.measured).sort((a, b) => a[0] - b[0])
        .map(([k, v]) => `${Units.fmtSpeed(Number(k))}〜 ${v.g.toFixed(2)}`).join(" / ");
      return `<tr><th rowspan="4">${escapeHtml(name)}</th><td>${escapeHtml(t("ブレーキ"))}</td><td>${val(l.brake)}${speeds ? `<div class="muted mSmall">${escapeHtml(speeds)}</div>` : ""}</td></tr>
        <tr><td>${escapeHtml(t("旋回 左"))}</td><td>${val(l.left)}</td></tr>
        <tr><td>${escapeHtml(t("旋回 右"))}</td><td>${val(l.right)}</td></tr>
        <tr><td>${escapeHtml(t("発進"))}</td><td>${val(l.launch)}</td></tr>`;
    }).join("");
    return `<table class="mLimits">${rows}</table>`
      + `<p class="muted">${escapeHtml(t("ブレーキと旋回の値は中央値(ブレーキは奥まで踏んだ行、旋回はスリップが限界の付近の行)。60 行(2 秒)以上で「測れた」とします"))}</p>`
      + (unknown ? `<p class="muted">${escapeHtml(t("補助の設定が分からない記録の値です。ABS・TC・STM・LC で限界が変わるので、「名前を付ける」で設定を付けると分けて出します"))}</p>` : "")
      + `<p class="muted">${escapeHtml(t("限界は、それぞれに効く補助の設定が同じ記録だけで出します(ブレーキ = ABS、旋回 = TC と STM、発進 = TC と LC)"))}</p>`;
  },
};

if (typeof module !== "undefined" && module.exports) {
  module.exports = { Measure };
}
