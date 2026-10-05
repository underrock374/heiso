// コース図の 3D 表示(WebGL2。外部の部品は使わない)。
// 走行軌跡を幅のある帯(道)として、標高(position_y)を高さに描く。使い方は 2D の CourseMap と同じ
// (setRows で描く行、draw で今の位置と範囲、クリックでその場所を通った時刻へ)。
//
// 座標: FH6 は左手系(x = 東、y = 上、z = 北)。ここで使う右手系の行列に渡すときは z の符号を反転する
// (反転しないと左右が逆に見える。docs/telemetry-field-notes.md「座標系は左手系」)。
// 視点: 左ドラッグで回転、右ドラッグで平行移動、ホイールで拡大・縮小、ダブルクリックで元の視点へ。
// 視点の種類: top(真上。北が上)/ tilt(斜め上から)/ chase(車の後ろ上方から。進む向きに合わせて回る)。
// 範囲(zoom): null = コース全体を収める、数値 = 今の位置を真ん中に、その一辺(m)が見える距離で追従する。
"use strict";

const CourseView3D = {
  canvas: null,       // WebGL2 の canvas
  overlay: null,      // 上に重ねる 2D の canvas(今の位置の矢印・縮尺・操作)
  gl: null,
  data: null,
  rows: null,         // 描く行(系列の中の位置、昇順)
  cut: null,          // 系列の位置 → 1 ならやり直し区間
  colors: null,       // 系列の位置 → 路面の色 [r, g, b](0〜1)か null
  colorMode: "lap",   // "lap"(今の周を青)/ "surface"(路面の色)
  zoom: null,
  view: "tilt",       // "top" / "tilt" / "chase"
  followMode: "live", // 拡大中の追従: "live"(いつも今の位置)/ "step"(区間ごとに区切って、区間が変わったら移る)
  _sections: null,
  _sectionKey: null,
  _anim: null,        // { from: {tx, ty, tz, yaw}, to: {...}, t0 }
  _stepTo: null,
  exaggeration: "auto", // 高さの強調: "auto" か数値
  onSeekTime: null,
  getNow: null,
  onViewChange: null, // (view) => void。マウスで視点を動かしたとき(設定の表示を合わせる)

  JUMP_M: 50,
  ROAD_PX: 6,         // 道の帯の幅(画面の px。近づくと実際の道幅 8m より細くはしない)
  ROAD_MIN_M: 8,

  _prog: null,
  _vao: null,
  _count: 0,
  _idxOffset: null,   // 行の番号 k → その行の最初の頂点の番号を指す、インデックス列の中の位置
  _world: null,       // 行の番号 k → [X, Y, Z](右手系の世界座標)
  _center: null,      // 全体の中心と大きさ { x, y, z, r, exag }
  _cam: { yaw: 0, pitch: 0.6, dist: 1000, tx: 0, ty: 0, tz: 0, manual: false },
  _last: { pos: null, t0: null, t1: null, l0: null, l1: null },
  _mvp: null,
  _chaseYaw: null,

  init(canvas, overlay) {
    this.canvas = canvas;
    this.overlay = overlay;
    this.gl = canvas.getContext("webgl2", { antialias: true, premultipliedAlpha: false });
    if (!this.gl) return false;
    this._program();
    new ResizeObserver(() => { this._resize(); this._redraw(); }).observe(canvas);
    canvas.addEventListener("webglcontextlost", (e) => e.preventDefault());
    canvas.addEventListener("webglcontextrestored", () => { this._program(); this._upload(); this._redraw(); });
    this._mouse();
    return true;
  },

  setRows(data, rows, cut, colors) {
    this.data = data;
    this.cut = cut || null;
    this.colors = colors || null;
    if (!data) { this.rows = null; this._count = 0; this._redraw(); return; }
    const all = rows || data.t.map((_, i) => i);
    this.rows = all.filter((i) => data.x[i] != null && data.z[i] != null && data.elev[i] != null);
    this._upload();
    this._cam.manual = false;
    this._redraw();
  },

  setZoom(m) { this.zoom = m || null; this._cam.manual = false; this._redraw(); },
  setView(v) { this.view = v; this._cam.manual = false; this._chaseYaw = null; this._stepTo = null; this._redraw(); },
  setFollowMode(m) { this.followMode = m; this._stepTo = null; this._anim = null; this._chaseYaw = null; this._redraw(); },
  setExaggeration(e) { this.exaggeration = e; this._upload(); this._redraw(); },
  setColors(colors, mode) { this.colors = colors; this.colorMode = mode; this._upload(); this._redraw(); },

  // ---- 形(頂点)を作る ----

  _exag() {
    if (this.exaggeration !== "auto") return Number(this.exaggeration) || 1;
    // 標高差がコースの広さの 8% くらいに見えるように(1〜8 倍)
    const c = this._bounds;
    if (!c) return 1;
    const span = Math.max(1, c.maxY - c.minY);
    return Math.max(1, Math.min(8, (Math.max(c.maxX - c.minX, c.maxZ - c.minZ) * 0.08) / span));
  },

  _upload() {
    const gl = this.gl;
    if (!gl || !this.rows || this.rows.length < 2) { this._count = 0; return; }
    const { x, z, elev } = this.data, rows = this.rows, n = rows.length;
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const i of rows) {
      minX = Math.min(minX, x[i]); maxX = Math.max(maxX, x[i]);
      minZ = Math.min(minZ, z[i]); maxZ = Math.max(maxZ, z[i]);
      minY = Math.min(minY, elev[i]); maxY = Math.max(maxY, elev[i]);
    }
    this._bounds = { minX, maxX, minZ, maxZ, minY, maxY };
    const exag = this._exag();
    const cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2, cy = minY;
    this._center = { x: 0, y: ((maxY - minY) * exag) / 2, z: 0, r: Math.hypot(maxX - minX, maxZ - minZ) / 2, exag, cx, cy, cz };

    // 行ごとの世界座標(右手系: X = 東、Y = 上、Z = 南)
    const W = new Float32Array(n * 3);
    for (let k = 0; k < n; k++) {
      const i = rows[k];
      W[k * 3] = x[i] - cx;
      W[k * 3 + 1] = (elev[i] - cy) * exag;
      W[k * 3 + 2] = -(z[i] - cz);
    }
    this._world = W;

    // 頂点: 行ごとに左右 2 つ。中心・横向き(水平の単位ベクトル)・左右・行の番号・路面の色・やり直し区間か
    const verts = new Float32Array(n * 2 * 10);
    const idx = [];
    this._idxOffset = new Int32Array(n);
    const RESTART = 0xffffffff;
    const jumped = (k) => k > 0 && Math.hypot(x[rows[k]] - x[rows[k - 1]], z[rows[k]] - z[rows[k - 1]]) > this.JUMP_M;
    for (let k = 0; k < n; k++) {
      const i = rows[k];
      // 進む向き(前後の行から。飛びの所は片側だけ)
      const a = k > 0 && !jumped(k) ? k - 1 : k, b = k + 1 < n && !jumped(k + 1) ? k + 1 : k;
      let dx = W[b * 3] - W[a * 3], dz = W[b * 3 + 2] - W[a * 3 + 2];
      const len = Math.hypot(dx, dz) || 1;
      dx /= len; dz /= len;
      const sx = -dz, sz = dx;   // 水平に直角
      const col = this.colors && this.colors[i] ? this.colors[i] : [0.36, 0.39, 0.44];
      const cut = this.cut && this.cut[i] === 1 ? 1 : 0;
      // 行の番号は、やり直し区間なら -1 - k にして印にする(シェーダーで色と幅を変える)
      const row = cut ? -1 - k : k;
      for (let s = 0; s < 2; s++) {
        verts.set([W[k * 3], W[k * 3 + 1], W[k * 3 + 2], sx, sz, s ? 1 : -1, row, col[0], col[1], col[2]], (k * 2 + s) * 10);
      }
      if (jumped(k)) idx.push(RESTART);
      this._idxOffset[k] = idx.length;
      idx.push(k * 2, k * 2 + 1);
    }
    this._count = idx.length;

    gl.bindVertexArray(this._vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this._vbo);
    gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);
    const F = 4, stride = 10 * F;
    const attr = (name, size, off) => {
      const loc = gl.getAttribLocation(this._prog, name);
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, size, gl.FLOAT, false, stride, off * F);
    };
    attr("aCenter", 3, 0);
    attr("aSide", 2, 3);
    attr("aSign", 1, 5);
    attr("aRow", 1, 6);
    attr("aColor", 3, 7);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this._ibo);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint32Array(idx), gl.STATIC_DRAW);
    gl.bindVertexArray(null);
  },

  _program() {
    const gl = this.gl;
    const vs = `#version 300 es
      in vec3 aCenter; in vec2 aSide; in float aSign; in float aRow; in vec3 aColor;
      uniform mat4 uMVP; uniform float uHalfW; uniform float uLift; uniform int uPass; uniform int uMode;
      uniform vec2 uLap; uniform vec2 uRange;
      out vec4 vColor;
      void main() {
        bool cut = aRow < 0.0;
        float row = cut ? -1.0 - aRow : aRow;
        bool inLap = row >= uLap.x && row <= uLap.y;
        bool inRange = row >= uRange.x && row <= uRange.y;
        float w = uHalfW;
        vec3 gray = vec3(0.31, 0.34, 0.38);
        vec3 c; float a = 1.0;
        if (cut) {
          c = vec3(0.66, 0.28, 0.28); a = 0.55; w *= 0.6;
          if (uPass > 0) w = 0.0;            // 今の周・グラフの範囲の重ね描きには出さない
        } else if (uMode == 1) {             // 路面の色。グラフの範囲は帯の真ん中の白い細い線
          if (uPass == 2) { c = vec3(1.0); w *= 0.25; }
          else c = inLap ? aColor : aColor * 0.45;
        } else {                             // 今の周を青
          c = inRange && uPass == 2 ? vec3(0.81, 0.88, 1.0) : (inLap ? vec3(0.36, 0.56, 0.85) : gray);
        }
        vec3 p = aCenter + vec3(aSide.x, 0.0, aSide.y) * aSign * w;
        p.y += uLift;
        gl_Position = uMVP * vec4(p, 1.0);
        vColor = vec4(c, a);
      }`;
    const fs = `#version 300 es
      precision mediump float;
      in vec4 vColor; out vec4 o;
      void main() { o = vColor; }`;
    const sh = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, sh(gl.VERTEX_SHADER, vs));
    gl.attachShader(p, sh(gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    this._prog = p;
    this._vao = gl.createVertexArray();
    this._vbo = gl.createBuffer();
    this._ibo = gl.createBuffer();
  },

  _resize() {
    const dpr = devicePixelRatio || 1;
    for (const c of [this.canvas, this.overlay]) {
      c.width = Math.max(1, Math.round(c.clientWidth * dpr));
      c.height = Math.max(1, Math.round(c.clientHeight * dpr));
    }
  },

  // ---- カメラ ----

  _findTime(sec) {
    const rows = this.rows, t = this.data.t;
    let lo = 0, hi = rows.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (t[rows[m]] < sec) lo = m + 1; else hi = m; }
    return lo;
  },

  _pointerK(pos) {
    const rows = this.rows, t = this.data.t;
    if (pos == null || pos < 0 || !rows) return -1;
    let lo = 0, hi = rows.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (rows[m] < pos) lo = m + 1; else hi = m; }
    let k = lo;
    if (k >= rows.length || rows[k] !== pos) k = Math.min(rows.length - 1, Math.max(0, k - 1));
    return Math.abs(t[rows[k]] - t[pos]) > 1 ? -1 : k;
  },

  /** 行 k の進む向き(世界座標の水平の角度)。前後 6m ずつ離れた点を結んだ方向(位置の細かいぶれで振れないように) */
  _heading(k) {
    const h = courseHeading(this.data, this.rows, k);
    return h ? Math.atan2(h[0], -h[1]) : null;   // 世界座標は Z = 南なので、北向きの成分を反転
  },

  /** 区切って追従するときの注視点と向き(区間が変わったら 0.4 秒かけて移る)。{ tx, ty, tz, yaw } */
  _stepCamera(k) {
    const key = `${this.zoom}|${this.rows.length}|${this.rows[0]}`;
    if (this._sectionKey !== key) {
      this._sections = buildFollowSections(this.data, this.rows, this.zoom);
      this._sectionKey = key;
      this._stepTo = null;
    }
    const sec = sectionAt(this._sections, k);
    if (!sec) return null;
    const c = this._center, W = this._world;
    let y = 0;
    for (let j = sec.k0; j <= sec.k1; j++) y += W[j * 3 + 1];
    // 後ろからのときは、区間の始まりから終わりへの向きに固定(スピンしても回らない)
    const yaw = this.view === "chase" ? Math.atan2(sec.dir[0], -sec.dir[1]) + Math.PI : this.view === "top" ? 0 : this._cam.yaw;
    const to = { tx: sec.cx - c.cx, ty: y / (sec.k1 - sec.k0 + 1), tz: -(sec.cz - c.cz), yaw, id: sec.k0 };
    const now = performance.now();
    if (!this._stepTo) { this._stepTo = to; this._anim = null; return to; }
    if (to.id !== this._stepTo.id || to.yaw !== this._stepTo.yaw) {
      const from = this._anim ? this._animPos(now) : this._stepTo;
      this._anim = { from, to, t0: now };
      this._stepTo = to;
    }
    if (!this._anim) return to;
    const p = this._animPos(now);
    if (now - this._anim.t0 >= FOLLOW_ANIM_MS) this._anim = null;
    else requestAnimationFrame(() => this._redraw());
    return p;
  },

  _animPos(now) {
    const a = this._anim, u = easeInOut(Math.min(1, (now - a.t0) / FOLLOW_ANIM_MS));
    const lerp = (p, q) => p + (q - p) * u;
    let dy = a.to.yaw - a.from.yaw;
    dy = Math.atan2(Math.sin(dy), Math.cos(dy));   // 近い回り方で
    return { tx: lerp(a.from.tx, a.to.tx), ty: lerp(a.from.ty, a.to.ty), tz: lerp(a.from.tz, a.to.tz), yaw: a.from.yaw + dy * u, id: a.to.id };
  },

  _fov: 45 * Math.PI / 180,

  /** 今の位置と設定から、カメラの注視点・距離・向きを決める */
  _updateCamera(k) {
    const cam = this._cam, c = this._center, W = this._world;
    const aspect = this.canvas.width / Math.max(1, this.canvas.height);
    const fitDist = (half) => half / Math.tan(this._fov / 2) / Math.min(1, aspect);
    if (!cam.manual) {
      // 区切って追従: 区間の中心と向き(区間が変わるまで動かない)
      if (this.followMode === "step" && this.zoom && k >= 0) {
        const s = this._stepCamera(k);
        if (s) {
          cam.tx = s.tx; cam.ty = s.ty; cam.tz = s.tz;
          cam.dist = fitDist(this.zoom / 2);
          cam.pitch = this.view === "top" ? 1.55 : this.view === "tilt" ? 0.75 : 0.32;
          cam.yaw = s.yaw;
          return;
        }
      }
      if (this.zoom && k >= 0) {
        cam.tx = W[k * 3]; cam.ty = W[k * 3 + 1]; cam.tz = W[k * 3 + 2];
        cam.dist = fitDist(this.zoom / 2);
      } else {
        cam.tx = c.x; cam.ty = c.y; cam.tz = c.z;
        cam.dist = fitDist(c.r * 1.02);
      }
      if (this.view === "top") { cam.pitch = 1.55; cam.yaw = 0; }
      else if (this.view === "tilt") { cam.pitch = 0.75; if (cam.yawSet !== true) { cam.yaw = 0; cam.yawSet = true; } }
      else if (this.view === "chase") {
        cam.pitch = 0.32;
        const h = k >= 0 ? this._heading(k) : null;
        if (h != null) {
          // 車の後ろから(進む向きの反対側にカメラ)。細かい揺れはならす
          const target = h + Math.PI;
          if (this._chaseYaw == null) this._chaseYaw = target;
          let d = target - this._chaseYaw;
          d = Math.atan2(Math.sin(d), Math.cos(d));
          this._chaseYaw += d * 0.15;
          cam.yaw = this._chaseYaw;
        }
        if (!this.zoom) cam.dist = fitDist(250);   // 後ろからは全体だと遠すぎるので 500m 相当
        if (k >= 0) { cam.tx = W[k * 3]; cam.ty = W[k * 3 + 1]; cam.tz = W[k * 3 + 2]; }
      }
    } else if (this.zoom && k >= 0 && cam.follow) {
      cam.tx = W[k * 3]; cam.ty = W[k * 3 + 1]; cam.tz = W[k * 3 + 2];
    }
  },

  _matrices() {
    const cam = this._cam;
    const eye = [
      cam.tx + cam.dist * Math.cos(cam.pitch) * Math.sin(cam.yaw),
      cam.ty + cam.dist * Math.sin(cam.pitch),
      cam.tz + cam.dist * Math.cos(cam.pitch) * Math.cos(cam.yaw),
    ];
    const aspect = this.canvas.width / Math.max(1, this.canvas.height);
    const near = Math.max(0.5, cam.dist * 0.01), far = cam.dist * 4 + this._center.r * 4;
    const P = M4.perspective(this._fov, aspect, near, far);
    const V = M4.lookAt(eye, [cam.tx, cam.ty, cam.tz], [0, 1, 0]);
    return { mvp: M4.mul(P, V), eye };
  },

  // ---- 描く ----

  _redraw() {
    const l = this._last;
    this.draw(l.pos, l.t0, l.t1, l.l0, l.l1);
  },

  draw(pointerPos, t0, t1, l0, l1) {
    this._last = { pos: pointerPos, t0, t1, l0, l1 };
    const gl = this.gl;
    if (!gl || !this.canvas.offsetParent) return;   // 隠れているときは描かない
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.clearColor(0.063, 0.071, 0.086, 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    const og = this.overlay.getContext("2d");
    og.clearRect(0, 0, this.overlay.width, this.overlay.height);
    if (!this._count) return;

    const k = this._pointerK(pointerPos);
    this._updateCamera(k);
    const { mvp } = this._matrices();
    this._mvp = mvp;

    const n = this.rows.length;
    const lap = l0 != null && l1 != null ? [this._findTime(l0), Math.min(n - 1, this._findTime(l1))] : [-1, -1];
    let range = t0 != null && t1 != null ? [this._findTime(t0), Math.min(n - 1, this._findTime(t1))] : [-1, -1];
    if (lap[0] >= 0) range = [Math.max(range[0], lap[0]), Math.min(range[1], lap[1])];

    // 帯の幅: 画面で ROAD_PX くらい(近づいたら実際の道幅より細くしない)
    const worldPerPx = (2 * this._cam.dist * Math.tan(this._fov / 2)) / this.canvas.height;
    const halfW = Math.max(this.ROAD_MIN_M, this.ROAD_PX * (devicePixelRatio || 1) * worldPerPx) / 2;

    gl.useProgram(this._prog);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LEQUAL);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    const u = (name) => gl.getUniformLocation(this._prog, name);
    gl.uniformMatrix4fv(u("uMVP"), false, mvp);
    gl.uniform1f(u("uHalfW"), halfW);
    gl.uniform1i(u("uMode"), this.colorMode === "surface" ? 1 : 0);
    gl.uniform2f(u("uLap"), lap[0], lap[1]);
    gl.uniform2f(u("uRange"), range[0], range[1]);
    gl.bindVertexArray(this._vao);

    const drawRows = (a, b, pass, lift) => {
      if (a < 0 || b <= a) return;
      gl.uniform1i(u("uPass"), pass);
      gl.uniform1f(u("uLift"), lift);
      const start = this._idxOffset[a], end = this._idxOffset[b] + 2;
      gl.drawElements(gl.TRIANGLE_STRIP, end - start, gl.UNSIGNED_INT, start * 4);
    };
    // 1: 全部(ほかの周・やり直し区間を含む)、2: 今の周を少し上に、3: グラフの範囲をさらに上に
    drawRows(0, n - 1, 0, 0);
    drawRows(lap[0], lap[1], 1, halfW * 0.05 + 0.2);
    drawRows(range[0], range[1], 2, halfW * 0.1 + 0.4);
    gl.bindVertexArray(null);

    // 今の位置: 重ねた canvas に黄色の矢印(進む向き)
    if (k >= 0) this._arrow(og, k);
    this._scaleText(og, worldPerPx);
  },

  /** 世界座標 → 重ねた canvas の px */
  _project(X, Y, Z) {
    const m = this._mvp;
    const x = m[0] * X + m[4] * Y + m[8] * Z + m[12];
    const y = m[1] * X + m[5] * Y + m[9] * Z + m[13];
    const w = m[3] * X + m[7] * Y + m[11] * Z + m[15];
    if (w <= 0) return null;
    return [(x / w * 0.5 + 0.5) * this.overlay.width, (1 - (y / w * 0.5 + 0.5)) * this.overlay.height];
  },

  _arrow(g, k) {
    const W = this._world, dpr = devicePixelRatio || 1;
    const p = this._project(W[k * 3], W[k * 3 + 1], W[k * 3 + 2]);
    if (!p) return;
    let dir = null;
    const h = this._heading(k);
    if (h != null) {
      const q = this._project(W[k * 3] + Math.sin(h) * 20, W[k * 3 + 1], W[k * 3 + 2] + Math.cos(h) * 20);
      if (q) dir = Math.atan2(q[1] - p[1], q[0] - p[0]);
    }
    g.fillStyle = "#ffcc33";
    g.strokeStyle = "#000";
    g.lineWidth = 1.5 * dpr;
    g.beginPath();
    if (dir != null) {
      const r = 10 * dpr;
      g.moveTo(p[0] + Math.cos(dir) * r, p[1] + Math.sin(dir) * r);
      g.lineTo(p[0] + Math.cos(dir + 2.5) * r * 0.7, p[1] + Math.sin(dir + 2.5) * r * 0.7);
      g.lineTo(p[0] + Math.cos(dir - 2.5) * r * 0.7, p[1] + Math.sin(dir - 2.5) * r * 0.7);
      g.closePath();
    } else {
      g.arc(p[0], p[1], 5 * dpr, 0, Math.PI * 2);
    }
    g.fill();
    g.stroke();
  },

  _scaleText(g, worldPerPx) {
    const dpr = devicePixelRatio || 1;
    g.fillStyle = "#cfd3d8";
    g.font = `${11 * dpr}px "Yu Gothic UI", sans-serif`;
    const e = this._center.exag;
    const names = { top: t("真上"), tilt: t("斜め"), chase: t("後ろから") };
    g.fillText(t("{view} ・ 高さ ×{exag}", { view: this._cam.manual ? t("自由") : names[this.view], exag: e.toFixed(e < 2 ? 1 : 0) }), 8 * dpr, this.overlay.height - 8 * dpr);
  },

  // ---- マウス ----

  _mouse() {
    const el = this.overlay;
    let drag = null;
    el.addEventListener("contextmenu", (e) => e.preventDefault());
    el.addEventListener("mousedown", (e) => {
      // 回転・拡大しても、範囲を選んでいれば今の位置への追従は続ける(平行移動したら止める)
      if (!this._cam.manual) this._cam.follow = true;
      drag = { x: e.clientX, y: e.clientY, button: e.button, moved: false, cam: { ...this._cam } };
    });
    window.addEventListener("mousemove", (e) => {
      if (!drag) return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 3) return;
      drag.moved = true;
      const cam = this._cam;
      cam.manual = true;
      if (drag.button === 0) {
        // 回転
        cam.yaw = drag.cam.yaw - dx * 0.008;
        cam.pitch = Math.max(0.05, Math.min(1.55, drag.cam.pitch + dy * 0.008));
      } else {
        // 平行移動(画面の左右・奥行き方向)
        const s = (2 * cam.dist * Math.tan(this._fov / 2)) / el.clientHeight;
        const cy = Math.cos(cam.yaw), sy = Math.sin(cam.yaw);
        cam.tx = drag.cam.tx - (dx * cy + dy * sy) * s;
        cam.tz = drag.cam.tz - (-dx * sy + dy * cy) * s;
        cam.follow = false;
      }
      if (this.onViewChange) this.onViewChange("free");
      this._redraw();
    });
    window.addEventListener("mouseup", (e) => {
      if (!drag) return;
      const d = drag;
      drag = null;
      if (!d.moved && d.button === 0) this._click(e);
    });
    el.addEventListener("wheel", (e) => {
      e.preventDefault();
      const cam = this._cam;
      if (!cam.manual) { cam.manual = true; cam.follow = true; }
      cam.dist = Math.max(20, Math.min(50000, cam.dist * (e.deltaY > 0 ? 1.15 : 0.87)));
      if (this.onViewChange) this.onViewChange("free");
      this._redraw();
    }, { passive: false });
    el.addEventListener("dblclick", () => {
      this._cam.manual = false;
      this._chaseYaw = null;
      if (this.onViewChange) this.onViewChange(this.view);
      this._redraw();
    });
  },

  /** クリック: その近くを通った時刻へ(何度も通っていれば、今の時刻に一番近いもの。やり直し区間は除く) */
  _click(e) {
    if (!this._mvp || !this.rows || !this.onSeekTime) return;
    const dpr = devicePixelRatio || 1, r = this.overlay.getBoundingClientRect();
    const cx = (e.clientX - r.left) * dpr, cy = (e.clientY - r.top) * dpr;
    const near = 12 * dpr, now = this.getNow ? this.getNow() : 0, t = this.data.t, W = this._world;
    let best = null, bestDt = Infinity, runBest = -1, runD = Infinity;
    const flush = () => {
      if (runBest < 0) return;
      const dt = Math.abs(t[this.rows[runBest]] - now);
      if (dt < bestDt) { best = runBest; bestDt = dt; }
      runBest = -1; runD = Infinity;
    };
    for (let k = 0; k < this.rows.length; k++) {
      if (this.cut && this.cut[this.rows[k]] === 1) { flush(); continue; }
      const p = this._project(W[k * 3], W[k * 3 + 1], W[k * 3 + 2]);
      const d = p ? Math.hypot(p[0] - cx, p[1] - cy) : Infinity;
      if (d <= near) { if (d < runD) { runBest = k; runD = d; } } else flush();
    }
    flush();
    if (best != null) this.onSeekTime(t[this.rows[best]]);
  },
};

// ---- 4x4 の行列(列優先。WebGL にそのまま渡せる) ----
const M4 = {
  perspective(fovy, aspect, near, far) {
    const f = 1 / Math.tan(fovy / 2), nf = 1 / (near - far);
    return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0]);
  },
  lookAt(eye, target, up) {
    let zx = eye[0] - target[0], zy = eye[1] - target[1], zz = eye[2] - target[2];
    let l = Math.hypot(zx, zy, zz) || 1; zx /= l; zy /= l; zz /= l;
    let xx = up[1] * zz - up[2] * zy, xy = up[2] * zx - up[0] * zz, xz = up[0] * zy - up[1] * zx;
    l = Math.hypot(xx, xy, xz) || 1; xx /= l; xy /= l; xz /= l;
    const yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
    return new Float32Array([
      xx, yx, zx, 0, xy, yy, zy, 0, xz, yz, zz, 0,
      -(xx * eye[0] + xy * eye[1] + xz * eye[2]), -(yx * eye[0] + yy * eye[1] + yz * eye[2]), -(zx * eye[0] + zy * eye[1] + zz * eye[2]), 1,
    ]);
  },
  mul(a, b) {
    const o = new Float32Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
      o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    }
    return o;
  },
};
