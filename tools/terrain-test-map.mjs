// Writes a TMSpline map for testing the own terrain: every road shape in every orientation, plus special cases.
// Usage: node tools/terrain-test-map.mjs <out.json> straight|curves
// Two maps, each within the server's limits (64 tracks, 400 points): straight shapes, and curves with the special cases.
//
// The grid: one column per shape (west to east), one row per orientation (north to south). Each row is one track through all
// the shapes: plain road -> the shape in that orientation -> plain road, every other shape turned, climbed and banked the other way.
import fs from "node:fs";

const SHAPES = [
  { name: "flat", curve: 0, width: 26 },
  { name: "U 60", curve: 60, width: 26 },
  { name: "U 120", curve: 120, width: 30 },
  { name: "U 180", curve: 180, width: 38 },
  { name: "rolled 240", curve: 240, width: 44 },
  { name: "rolled 300", curve: 300, width: 48 },
  { name: "pipe 360", curve: 360, width: 50 },
  { name: "sausage", curve: -120, width: 30 },
  { name: "widening", curve: 0, width: 60 },
];

const part = process.argv[3] === "curves" ? "curves" : "straight";
const ORIENTATIONS = [
  { name: "level", heading: 0, pitch: 0, bank: 0 },
  { name: "zigzag 20", heading: 20, pitch: 0, bank: 0 },
  { name: "climb and dive 28", heading: 15, pitch: 28, bank: 0 },
  { name: "climb and dive 40", heading: 0, pitch: 40, bank: 0 },
  { name: "bank 45", heading: 0, pitch: 0, bank: 45 },
  { name: "bank 80", heading: 15, pitch: 8, bank: 80 },
  { name: "bank 105", heading: 0, pitch: 0, bank: 105 },
  { name: "curve, no bank", sweep: 32, pitch: 0, bank: 0 },
  { name: "banked curve 30", sweep: 32, pitch: 0, bank: 30 },
  { name: "banked curve 60", sweep: 32, pitch: 0, bank: 60 },
  { name: "banked curve 80", sweep: 32, pitch: 6, bank: 80 },
].filter((o) => !!o.sweep === (part === "curves"));

const COLUMN = 165, ROW = 110, CURVE_ROW = 135, BASE = 60;
const X0 = -((SHAPES.length - 1) * COLUMN) / 2 - 73;
const rowZ = [];
{
  let z = part === "curves" ? -320 : -330;
  for (const o of ORIENTATIONS) {
    rowZ.push(z);
    z += o.sweep ? CURVE_ROW : ROW;
  }
  rowZ.push(z + 15);
}

const point = (position, tangent, { bank = 0, width = 26, curve = 0 } = {}) => ({
  position,
  handleIn: tangent.map((v) => -v),
  handleOut: tangent,
  bank,
  width,
  curve,
  flat: 0,
  sausage: 0,
  edgeAnchor: 0,
  borderHeight: 1,
  borders: true,
  waypoint: "None",
});

const segment = () => ({
  surface: "RoadTech", borders: true, borderMaterial: "Rubber", underside: true, color: "Default", effect: "None", effectYaw: 0, effectPitch: 0,
  bumps: "None", bumpHeight: 0.5, bumpSize: 8, bumpDetail: 2, signs: "None", signSide: "Both", signSpacing: 16,
});

const track = (points) => ({ closed: false, points, segments: points.slice(1).map(segment), objects: [] });

/** One orientation as a single track through all the shapes. */
function row(z, o) {
  const stations = [0, 24, 48, 98, 122, 146];
  // How much of the slope applies between one station and the next: easing in, all of it, easing out.
  const riseShare = [0, 0.5, 1, 0.5, 0];
  const points = [];
  let [px, py, pz] = [X0, BASE, z];
  SHAPES.forEach((shape, c) => {
    const s = c % 2 ? -1 : 1;
    const slope = s * Math.tan((o.pitch * Math.PI) / 180);
    // A curve sweeps from one side to the other over the cell, so the row stays straight on average.
    const degrees = (d) => (o.sweep ? s * (-o.sweep + 2 * o.sweep * Math.min(1, Math.max(0, (d - 24) / 98))) : s * o.heading);
    const heading = (d) => (degrees(d) * Math.PI) / 180;
    const shaped = { width: shape.width, curve: shape.curve, bank: s * o.bank };
    stations.forEach((d, i) => {
      if (i > 0) {
        for (let t = stations[i - 1]; t < d; t += 2) {
          px += 2 * Math.cos(heading(t + 1));
          pz += 2 * Math.sin(heading(t + 1));
        }
        py += (d - stations[i - 1]) * slope * riseShare[i - 1];
      }
      const on = d >= 48 && d <= 98;
      const gap = i === 0 ? stations[1] : i === stations.length - 1 ? 24 : Math.min(d - stations[i - 1], stations[i + 1] - d);
      const len = gap / 3, h = heading(d);
      points.push(point([px, py, pz], [len * Math.cos(h), on ? len * slope : 0, len * Math.sin(h)], on ? shaped : { width: 26 }));
    });
    px += 19 * Math.cos(heading(146));
    pz += 19 * Math.sin(heading(146));
  });
  return track(points);
}

const tracks = ORIENTATIONS.map((o, r) => row(rowZ[r], o));

// Special cases, along the last row of the curves map.
const zs = rowZ[ORIENTATIONS.length] + 40;
if (part === "curves") {
const flatLine = (x0, z0, x1, z1, y0, y1, options = {}) => {
  const [dx, dz] = [(x1 - x0) / 3, (z1 - z0) / 3];
  return track([point([x0, y0, z0], [dx, 0, dz], options), point([x1, y1, z1], [dx, 0, dz], options)]);
};

// 1. A road that overhangs itself: a helix passing over its own lower turn, 14 m apart.
{
  const cx = X0 + 90, R = 55, k = R * 0.5523, rise = 3.5;
  const points = [];
  for (let i = 0; i <= 7; i++) {
    const a = (i * Math.PI) / 2;
    points.push(point([cx + R * Math.cos(a), BASE + i * rise, zs + R * Math.sin(a)], [-k * Math.sin(a), rise / 3, k * Math.cos(a)]));
  }
  tracks.push(track(points));
}
// 2. A road crossing over another, low (8 m) and high (24 m).
tracks.push(flatLine(X0 + 230, zs, X0 + 400, zs, BASE, BASE), flatLine(X0 + 275, zs - 60, X0 + 275, zs + 60, BASE + 8, BASE + 8), flatLine(X0 + 355, zs - 60, X0 + 355, zs + 60, BASE + 24, BASE + 24));
// 3. A road crossing over a pipe.
tracks.push(flatLine(X0 + 450, zs, X0 + 620, zs, BASE, BASE, { width: 50, curve: 360 }), flatLine(X0 + 535, zs - 60, X0 + 535, zs + 60, BASE + 26, BASE + 26));
// 4. A barrel roll.
{
  const x = X0 + 670, L = 18;
  tracks.push(track([0, 50, 100, 150, 200, 250].map((d, i) => point([x + d, BASE, zs], [L, 0, 0], { bank: [0, 90, 180, -90, 0, 0][i] }))));
}
// 5. A vertical loop.
{
  const x = X0 + 1010;
  tracks.push(track([
    point([x - 80, BASE, zs], [30, 0, 0]),
    point([x, BASE, zs], [30, 0, 0]),
    point([x + 45, BASE + 45, zs + 6], [0, 25, 3]),
    point([x, BASE + 90, zs + 12], [-25, 0, 3]),
    point([x - 45, BASE + 45, zs + 18], [0, -25, 3]),
    point([x, BASE, zs + 24], [30, 0, 0]),
    point([x + 80, BASE, zs + 24], [30, 0, 0]),
  ]));
}
}

const map = {
  version: 3,
  name: `Terrain test ${part}`,
  laps: 3,
  tracks,
  blocks: [],
  sceneryOptions: {
    mode: "terrain", look: "alone", far: "fit",
    ownTerrain: { width: 36, smoothing: 16, noise: 3, noiseSize: 96, rockAngle: 50, rise: 0 },
    band: 4, theme: "cliff8", gap: 1, hills: true, hillStep: 3, hillRise: 6, farRise: 0.5, cliffs: 10, forest: "none", density: 0.5, clearance: 5,
  },
};

fs.writeFileSync(process.argv[2], JSON.stringify(map));
console.log(`${tracks.length} tracks, ${tracks.reduce((n, t) => n + t.points.length, 0)} points, x ${X0} to ${X0 + SHAPES.length * COLUMN}, z ${rowZ[0]} to ${zs + 60}`);
