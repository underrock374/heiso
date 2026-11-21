// 再生アプリの画面: グラフの横軸(時間 / 距離)。距離のときの系列と印の並べ直し、摩擦円・荷重移動の受け取り。
// 全部のファイルが 1 つの大域を共有する(index.html の読み込み順。app.js が先)。
"use strict";

// ---- 横軸(時間 / 距離) ----

$("axis").onchange = (e) => {
  logEvent(`横軸: ${e.target.value}`);
  axis = e.target.value;   // この記録の中だけ(開いたときの横軸は設定のダイアログで)
  renderAxis(null);        // 「全体」から距離にしたら、1 レース目を選ぶ(C# の答えを受けて onRaceSelected が続ける)
  e.target.blur();
};

function fmtDist(m) {
  return Units.fmtDist(m);
}

/** 横軸を今の設定(時間 / 距離、選んでいるレース)で作り直す。keepRange があればその範囲を保つ */
function renderAxis(keepRange) {
  if (!tdata) return;
  const r = selectedRace();
  if (axis === "dist" && !r) {
    // 距離は 1 つのレースについて。「全体」のままなら 1 レース目を選ぶ(C# の答えを受けて、もう一度ここへ来る。レースが無ければ時間)
    if (races.length) {
      selectRace(0, "axis");
      return;
    }
    axis = "time";
  }
  $("axis").value = axis;
  $("axis").disabled = !races.length;
  // 摩擦円・荷重移動とグラフの系列(前後の荷重・左右の荷重・摩擦円の %)は、選んでいるレースで決まる。
  // C# がレースか幅が変わるたびに作り直して送ってくる。届くまでは前のもの(新しく開いたときは無し)で出す
  applyFriction();
  if (axis === "dist") {
    distMap = distLookup(r);
    Graphs.setData(distView(), distMarks(tmarks));
    Graphs.fmtX = fmtDist;
    Graphs.xTicks = (min, max, count) => Units.distTicks(min, max, count);
    Graphs.xToSec = distToSec;
  } else {
    distMap = null;
    Graphs.setData(tdata, tmarks);
    Graphs.fmtX = fmtTime;
    Graphs.xTicks = null;
    Graphs.xToSec = (x) => x;
  }
  Graphs.elevRange = r ? r.elevRange : tmarks.elevRange;   // 標高の段の上下(C# が決める)
  Graphs.build();
  markSpan();
  updateRaceNamesBtn();
  CourseMap.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors, breakRows);
  FrictionCircle.setLimits(measureLimits(selectedRaceIndex()));
  PowerChart.setData(tdata);
  showShifts(r);
  renderMeasure();
  if (view3d) CourseView3D.setRows(tdata, r ? r.dist.i : null, cutRows, surfaceColors, breakRows);
  if (keepRange) Graphs.setRange(keepRange.min, keepRange.max);
  else followTo(currentSec(), true);
  updatePointer(currentSec());
  renderRaceBar();   // 選んでいるレースを明るく
}

/**
 * 距離の引き表(C# が送ったレースの距離を、系列の位置で引けるように並べ直す)。all: 系列の位置 → 距離(やり直した走りも含む。ポインター用)、
 * kept: 最後まで残った走りだけ(印用)、pos / d: グラフに出す行(どの行を出すかは C# が決めた dist.graph)
 */
function distLookup(race) {
  const n = tdata.t.length;
  const all = new Float64Array(n).fill(NaN), kept = new Float64Array(n).fill(NaN);
  const { i, d: dd, k, graph } = race.dist;
  for (let j = 0; j < i.length; j++) {
    all[i[j]] = dd[j];
    if (k[j]) kept[i[j]] = dd[j];
  }
  return { all, kept, pos: graph.map((j) => i[j]), d: graph.map((j) => dd[j]) };
}

/** 持っている摩擦円・荷重移動を、図とグラフの系列に入れる(グラフを作り直すのは呼ぶ側) */
function applyFriction() {
  FrictionCircle.setData(tdata, friction);
  tdata.loadLong = friction ? friction.loadLong : null;
  tdata.loadLat = friction ? friction.loadLat : null;
  tdata.grip = friction ? friction.grip : null;
}

/** C# から摩擦円・荷重移動が届いた(レースか幅が変わったとき)。グラフを見ている範囲のまま作り直す */
function onFriction(m) {
  if (!tdata || m.gg.x.length !== tdata.t.length) return;   // 前の記録のもの
  friction = m;
  applyFriction();
  const keep = Graphs.xRange();
  if (axis === "dist") Graphs.setData(distView(), distMarks(tmarks)); else Graphs.setData(tdata, tmarks);
  Graphs.build();
  if (keep) Graphs.setRange(keep.min, keep.max);
  updatePointer(pointerSec);
}

/** 横軸を距離にした系列(グラフに出す行だけを抜き出す) */
function distView() {
  const v = { t: distMap.d };
  for (const [key, col] of Object.entries(tdata)) {
    if (key === "t") continue;
    if (key === "surface") {
      v.surface = {};
      for (const [w, c] of Object.entries(col)) v.surface[w] = distMap.pos.map((p) => c[p]);
    } else {
      // 摩擦円の %・荷重の系列は、C# から届くまで null(記録を開いた直後。届いたら onFriction で並べ直す)
      v[key] = col == null ? null : distMap.pos.map((p) => col[p]);
    }
  }
  return v;
}

/** 動画の秒 → 横軸の値(時間ならそのまま、距離ならその行の距離。レースの外は NaN) */
function xOf(sec) {
  if (axis !== "dist" || !distMap) return sec;
  const p = indexAt(sec);
  return p < 0 ? NaN : distMap.all[p];
}

/** 距離 → その距離を通った時刻(最後まで残った走りで) */
function distToSec(x) {
  const d = distMap.d;
  let lo = 0, hi = d.length - 1;
  while (lo < hi) { const m = (lo + hi) >> 1; if (d[m] < x) lo = m + 1; else hi = m; }
  return tdata.t[distMap.pos[lo]];
}

/** その時刻に一番近い、距離のある行の距離(停止区間の中なら、明けて走りに戻った所。無ければ手前) */
function nearestDist(sec) {
  const p = indexAt(sec);
  if (p < 0) return NaN;
  const all = distMap.all;
  for (let q = p; q < all.length; q++) if (isFinite(all[q])) return all[q];
  for (let q = p - 1; q >= 0; q--) if (isFinite(all[q])) return all[q];
  return NaN;
}

/** 最後まで残った走りでの、その時刻の距離。やり直した走りの中なら NaN */
function keptAt(sec) {
  const p = indexAt(sec);
  if (p < 0) return NaN;
  const v = distMap.kept[p];
  return isFinite(v) ? v : (p + 1 < distMap.kept.length ? distMap.kept[p + 1] : NaN);
}

/** 印を距離に直す(元の時刻は src_ に残す)。やり直した走りの中の印は出さない */
function distMarks(mk) {
  const conv = (m, need, opt = []) => {
    const o = { ...m };
    for (const key of need) {
      const v = keptAt(m[key]);
      if (!isFinite(v)) return null;
      o[key] = v;
      o["src_" + key] = m[key];
    }
    for (const key of opt) {
      if (m[key] == null) continue;
      const v = keptAt(m[key]);
      o["src_" + key] = m[key];
      o[key] = isFinite(v) ? v : null;
    }
    return o;
  };
  const list = (arr, need, opt) => (arr || []).map((m) => conv(m, need, opt)).filter(Boolean);
  return {
    ...mk,
    starts: list(mk.starts, ["t"]),
    contacts: list(mk.contacts, ["t"]),
    laps: list(mk.laps, ["t"]),
    jumps: list(mk.jumps, ["t0", "t1"]),
    shifts: list(mk.shifts, ["t"]),
    excursions: list(mk.excursions, ["t0", "t1"], ["slipT", "slipEndT", "slipBandT1"]),
    skips: [],   // やり直した走りは距離軸に出さない
  };
}
