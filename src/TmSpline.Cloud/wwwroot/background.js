// The website's background: banked spline roads with red/white curbs at dusk, seen from a slowly circling camera,
// with a few cars' lights running along them. Plain canvas 2D, nothing loaded.
(() => {
  const canvas = document.getElementById("scene");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const still = matchMedia("(prefers-reduced-motion: reduce)").matches;

  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
  const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const norm = (a) => mul(a, 1 / Math.hypot(a[0], a[1], a[2]));
  const UP = [0, 1, 0];
  const SUN = norm([-0.55, 0.45, -0.7]);
  const SKY = [22, 18, 46];

  // Closed curves (θ = 0..2π): position in meters, y up.
  const curves = [
    (t) => [300 * Math.sin(t), 46 + 34 * Math.sin(t + 1.2), 170 * Math.sin(2 * t)],
    (t) => [210 * Math.cos(t) + 70 * Math.cos(3 * t), 110 + 60 * Math.sin(2 * t), 240 * Math.sin(t) - 40],
    (t) => [-120 + 150 * Math.cos(t), 18 + 10 * Math.sin(3 * t), 60 + 300 * Math.sin(t) * Math.cos(t)],
  ];
  const WIDTH = 26, CURB = 2.2, STEPS = 260;

  // Each road: rings of [left curb out, left edge, right edge, right curb out], banked into the curves.
  const roads = curves.map((f) => {
    const pts = [], sides = [];
    for (let i = 0; i < STEPS; i++) {
      const t = (i / STEPS) * Math.PI * 2, h = 1e-3;
      const p = f(t), a = f(t - h), b = f(t + h);
      const tangent = norm(sub(b, a));
      const acc = mul(add(sub(b, p), sub(a, p)), 1 / (h * h));
      const flatSide = norm(cross(tangent, UP));
      // Bank against the lateral acceleration (like a car would want), at most ~40°.
      const bank = Math.max(-0.7, Math.min(0.7, dot(acc, flatSide) / 2600));
      const side = norm(add(mul(flatSide, Math.cos(bank)), mul(cross(flatSide, tangent), Math.sin(bank))));
      pts.push(p);
      sides.push(side);
    }
    const rings = pts.map((p, i) => {
      const s = sides[i];
      return [add(p, mul(s, -(WIDTH / 2 + CURB))), add(p, mul(s, -WIDTH / 2)), add(p, mul(s, WIDTH / 2)), add(p, mul(s, WIDTH / 2 + CURB))];
    });
    const normals = pts.map((p, i) => norm(cross(sides[i], sub(pts[(i + 1) % STEPS], p))));
    return { pts, rings, normals };
  });

  let W = 0, H = 0, F = 0;
  const resize = () => {
    const dpr = Math.min(2, devicePixelRatio || 1);
    W = canvas.width = Math.round(innerWidth * dpr);
    H = canvas.height = Math.round(innerHeight * dpr);
    F = H * 0.95;
  };
  addEventListener("resize", resize);
  resize();

  const shade = (rgb, light, depth) => {
    const fog = Math.min(1, Math.max(0, (depth - 120) / 1100));
    return `rgb(${rgb.map((c, i) => Math.round((c * light) * (1 - fog) + SKY[i] * fog)).join(",")})`;
  };

  const frame = (ms) => {
    const time = still ? 20 : ms / 1000;
    const angle = time * 0.035;
    const cam = [Math.cos(angle) * 540, 34 + 22 * Math.sin(time * 0.05), Math.sin(angle) * 540];
    const target = [0, 105, 0];
    const f = norm(sub(target, cam)), r = norm(cross(f, UP)), u = cross(r, f);
    const view = (p) => { const d = sub(p, cam); return [dot(d, r), dot(d, u), dot(d, f)]; };
    const screen = (v) => [W / 2 + (v[0] * F) / v[2], H / 2 - (v[1] * F) / v[2]];

    // Sky with the sun's glow at the horizon.
    const fh = norm([f[0], 0, f[2]]);
    const horizon = H / 2 - (F * dot(fh, u)) / dot(fh, f);
    const sky = ctx.createLinearGradient(0, 0, 0, horizon);
    sky.addColorStop(0, "#090c1f");
    sky.addColorStop(0.55, "#231a46");
    sky.addColorStop(0.85, "#7a2e52");
    sky.addColorStop(1, "#e56a3c");
    ctx.fillStyle = sky;
    ctx.fillRect(0, 0, W, Math.max(0, horizon));
    const ground = ctx.createLinearGradient(0, horizon, 0, H);
    ground.addColorStop(0, "#2a1d3a");
    ground.addColorStop(0.25, "#111525");
    ground.addColorStop(1, "#070a14");
    ctx.fillStyle = ground;
    ctx.fillRect(0, horizon, W, H - horizon);

    // The 32 m grid on the ground, fading into the distance.
    ctx.lineWidth = Math.max(1, H / 900);
    for (let k = -30; k <= 30; k++) {
      for (const [a, b] of [[[k * 32, 0, -960], [k * 32, 0, 960]], [[-960, 0, k * 32], [960, 0, k * 32]]]) {
        let va = view(a), vb = view(b);
        if (va[2] < 5 && vb[2] < 5) continue;
        if (va[2] < 5) va = add(va, mul(sub(vb, va), (5 - va[2]) / (vb[2] - va[2])));
        if (vb[2] < 5) vb = add(vb, mul(sub(va, vb), (5 - vb[2]) / (va[2] - vb[2])));
        const [x1, y1] = screen(va), [x2, y2] = screen(vb);
        const g = ctx.createLinearGradient(x1, y1, x2, y2);
        const alpha = (z) => Math.max(0, 0.28 - z / 3600);
        g.addColorStop(0, `rgba(120,160,255,${alpha(va[2])})`);
        g.addColorStop(1, `rgba(120,160,255,${alpha(vb[2])})`);
        ctx.strokeStyle = g;
        ctx.beginPath();
        ctx.moveTo(x1, y1);
        ctx.lineTo(x2, y2);
        ctx.stroke();
      }
    }

    // Road quads, far to near.
    const quads = [];
    for (const road of roads) {
      const rings = road.rings.map((ring) => ring.map(view));
      for (let i = 0; i < STEPS; i++) {
        const a = rings[i], b = rings[(i + 1) % STEPS];
        if (a.some((v) => v[2] < 4) || b.some((v) => v[2] < 4)) continue;
        const light = 0.35 + 0.65 * Math.abs(dot(road.normals[i], SUN));
        const depth = (a[1][2] + a[2][2] + b[1][2] + b[2][2]) / 4;
        const white = (i >> 1) % 2 === 0;
        quads.push({ depth, pts: [a[1], a[2], b[2], b[1]], color: shade(i % 8 === 0 ? [120, 128, 150] : [84, 90, 110], light, depth) });
        for (const [o, e] of [[0, 1], [3, 2]])
          quads.push({ depth: depth - 0.01, pts: [a[o], a[e], b[e], b[o]], color: shade(white ? [235, 235, 240] : [214, 38, 52], light, depth) });
      }
    }
    quads.sort((p, q) => q.depth - p.depth);
    for (const q of quads) {
      ctx.fillStyle = q.color;
      ctx.beginPath();
      q.pts.forEach((v, k) => { const [x, y] = screen(v); k ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
      ctx.closePath();
      ctx.fill();
      ctx.strokeStyle = q.color;
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    // Cars: a glowing light running along each road.
    ctx.globalCompositeOperation = "lighter";
    roads.forEach((road, n) => {
      for (let c = 0; c < 2; c++) {
        const s = ((time * (0.035 + n * 0.008) + c / 2 + n * 0.3) % 1) * STEPS;
        const i = Math.floor(s), k = s - i;
        const p = add(mul(road.pts[i], 1 - k), mul(road.pts[(i + 1) % STEPS], k));
        const v = view(add(p, [0, 1.5, 0]));
        if (v[2] < 10) continue;
        const [x, y] = screen(v), radius = Math.max(2, (F * 7) / v[2]);
        const glow = ctx.createRadialGradient(x, y, 0, x, y, radius);
        glow.addColorStop(0, "rgba(255,240,200,0.95)");
        glow.addColorStop(0.3, "rgba(255,170,90,0.45)");
        glow.addColorStop(1, "rgba(255,120,60,0)");
        ctx.fillStyle = glow;
        ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2);
      }
    });
    ctx.globalCompositeOperation = "source-over";

    if (!still) requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
})();
