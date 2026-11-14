import * as THREE from "three";
import { BLOCK_CELL, MAP_HALF_EXTENT, type TerrainGrid, type TerrainMesher, type TerrainQuality, type TerrainSurface, type VanillaBlock, type VanillaItem } from "./track";
import { clearOfRoads, plantForest, type SceneryLandOptions, type SceneryTheme } from "./scenery";

/**
 * Own terrain: instead of the game's land blocks, a mesh of our own round the tracks (exported as custom items with the
 * hill blocks' materials, see TerrainMeshBuilder). Here the land's height map, on a 4 m grid:
 *
 * 1. The roads' shape extended outwards: every vertex takes the top (curbs, or the surface's edge) of the nearest road
 *    square, of the highest road there.
 * 2. That surface smoothed (a wide blur), blending from the unsmoothed surface at the road into the smoothed one over
 *    the first 32 m, so the land meets the road.
 * 3. Height noise on top, faded in over the same 32 m.
 *
 * The optional rise with distance is part of what gets smoothed (2), so it fades in with the blend and has no crease
 * halfway between two roads.
 *
 * Only upright road (its driving surface facing up more than sideways, and not climbing too steeply) guides the land,
 * and of roads over one another only the highest: walls, loops, tubes and whatever lies lower run through the land,
 * which is meshed as a solid the roads cut their space out of (VoxelTerrain). So the land needs no shaping for them.
 */

export interface OwnTerrainOptions {
  /** How far the land reaches from the roads, metres. */
  width: number;
  /** Radius of the smoothing, metres. */
  smoothing: number;
  /** Height of the noise's bumps, metres (0: none). */
  noise: number;
  /** Size of the noise's bumps, metres. */
  noiseSize: number;
  /** Land steeper than this is rock (the cliffs' material), degrees; 90: none. */
  rockAngle: number;
  /** The land climbs to this share of its width at its crest near the outer edge, percent (0: flat; negative: it falls, e.g. into the water), see RISE_PROFILE. */
  rise: number;
  /** How fine the mesh is (see TerrainGrid.quality). */
  quality?: TerrainQuality;
  /** How the mesh is made (see TerrainGrid.mesher). */
  mesher?: TerrainMesher;
  /**
   * The land bends down towards its outer edge and ends this far lower, metres (0: it ends level, and its edge shows
   * against the sky from the road). The bend takes twice this distance (at most half the width), so it ends at 45°.
   */
  edgeDrop?: number;
  /** Which hills the noise makes: a whole number, each with its own (missing or 0: those maps had before there was a seed). */
  seed?: number;
}

/** OwnTerrainOptions.edgeDrop when a map has none. */
export const EDGE_DROP = 24;

export const OWN_TERRAIN_DEFAULTS: OwnTerrainOptions = { width: 128, smoothing: 48, noise: 6, noiseSize: 96, rockAngle: 50, rise: 0, quality: "High" };

/** Grid spacing, metres (also the size of the road squares for the caps and distances). */
const CELL = 4;
/** Over this many metres from a road the land blends from the road's shape into the smoothed, noisy land. */
const BLEND = 32;
/** Land lower than the stadium's ground lies this deep, under it, metres. */
const SUNK = -2;
/** Land stays below this, metres (the export takes up to 1960). */
const MAX_LAND = 1950;
/** Under water the land goes down to this, metres. */
const MIN_LAND = -64;

/** Off the driving surface (curbs), road counts for the land up to this much over the surface in its square, metres (not gates, signs). */
const CURB_REACH = 3;
/** Road facing up less than this (the normal's y) or climbing steeper than MAX_PITCH (sine) holds no land (walls, loops). */
const UPRIGHT = 0.1;
const MAX_PITCH = 0.55;
/** On walls and upside-down road: how far off the driving surface a car needs room, metres. */
const DRIVE_ROOM = 4;
/** Land stays this much under walls, inverted road and the room they need, metres. */
const KEEP_CLEAR = 0.5;
/** Road whose two edges are nearer than this, though it is wider, is closed (a tube): as AdaptiveTerrainBuilder, metres. */
const SLOT_CLOSED = 3;

const key = (x: number, z: number) => `${x},${z}`;

/** A road's preview mesh and its vertices' driving-surface coordinates (NaN off the driving surface: curbs, sides, underside). */
export interface TerrainRoad { mesh: THREE.Mesh; surface: Float32Array }

/**
 * The roads sampled for the land: per CELL square the top of its upright road (what the land continues), and the height
 * the land must stay under (walls and inverted road, and the room to drive on them). Closed road (a tube) has no say in
 * the land's shape, the land runs over it (AdaptiveTerrainBuilder buries it): only its squares, in `tunnels`.
 */
function roadShape(roads: TerrainRoad[]): { tops: Map<string, number>; caps: Map<string, number>; tunnels: Set<string> } {
  const tunnels = new Set<string>();
  // Upright road's heights per square; the land continues the lowest road in each (the top of its curbs).
  const heights = new Map<string, number[]>(), caps = new Map<string, number>();
  const tops = {
    get: (k: string) => { const l = heights.get(k); return l ? Math.max(...l) : undefined; },
    set: (k: string, y: number) => { const l = heights.get(k); if (l) l.push(y); else heights.set(k, [y]); },
  };
  const column = (x: number, z: number) => key(Math.floor(x / CELL), Math.floor(z / CELL));
  const cap = (k: string, y: number) => caps.set(k, Math.min(caps.get(k) ?? Infinity, y - KEEP_CLEAR));
  // Driving surface first: upright or not, by its normal; remembered in 3D squares for the rest of the road.
  const VOXEL = CELL;
  const voxel = (p: THREE.Vector3) => `${Math.floor(p.x / VOXEL)},${Math.floor(p.y / VOXEL)},${Math.floor(p.z / VOXEL)}`;
  const votes = new Map<string, number>(); // > 0: mostly upright
  const a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3(), p = new THREE.Vector3();
  const n = new THREE.Vector3(), q = new THREE.Vector3();
  const triangles = (road: TerrainRoad, fn: (ia: number, ib: number, ic: number) => void) => {
    const index = road.mesh.geometry.getIndex();
    const count = index ? index.count : road.mesh.geometry.getAttribute("position").count;
    for (let i = 0; i + 2 < count; i += 3) fn(...([0, 1, 2].map((j) => (index ? index.getX(i + j) : i + j)) as [number, number, number]));
  };
  const points = (road: TerrainRoad, ia: number, ib: number, ic: number, step: number, mark: (v: THREE.Vector3) => void) => {
    const pos = road.mesh.geometry.getAttribute("position");
    a.fromBufferAttribute(pos, ia).applyMatrix4(road.mesh.matrixWorld);
    b.fromBufferAttribute(pos, ib).applyMatrix4(road.mesh.matrixWorld);
    c.fromBufferAttribute(pos, ic).applyMatrix4(road.mesh.matrixWorld);
    const longest = Math.max(a.distanceTo(b), b.distanceTo(c), c.distanceTo(a));
    const steps = Math.min(64, Math.max(1, Math.ceil(longest / step)));
    for (let u = 0; u <= steps; u++)
      for (let v = 0; u + v <= steps; v++) {
        p.copy(a).multiplyScalar(1 - (u + v) / steps).addScaledVector(b, u / steps).addScaledVector(c, v / steps);
        mark(p);
      }
  };
  const onSurface = (road: TerrainRoad, i: number) => !Number.isNaN(road.surface[i * 2]);
  for (const road of roads) {
    road.mesh.updateMatrixWorld();
    const normals = road.mesh.geometry.getAttribute("normal");
    // Upright or not per metre along the road, by its cross-section as a whole: the driving surface's normal in its
    // middle (on a bent cross-section the sides face further sideways) and its climb along the middle a few metres
    // either way. AdaptiveTerrainBuilder decides by the same, its rings' up and tangent.
    const middle = new Map<number, { across: number; n: THREE.Vector3; p: THREE.Vector3 }>();
    const position = road.mesh.geometry.getAttribute("position");
    for (let i = 0; i < position.count; i++) {
      if (!onSurface(road, i)) continue;
      const b = Math.round(road.surface[i * 2]), across = Math.abs(road.surface[i * 2 + 1]);
      const m = middle.get(b);
      if (m && m.across <= across) continue;
      middle.set(b, { across, n: new THREE.Vector3().fromBufferAttribute(normals, i).transformDirection(road.mesh.matrixWorld),
        p: new THREE.Vector3().fromBufferAttribute(position, i).applyMatrix4(road.mesh.matrixWorld) });
    }
    const along = [...middle.keys()].sort((u, w) => u - w);
    const uprightAt = new Map<number, boolean>();
    along.forEach((b, j) => {
      const m = middle.get(b)!;
      const t = middle.get(along[Math.min(along.length - 1, j + 3)])!.p.clone().sub(middle.get(along[Math.max(0, j - 3)])!.p);
      const pitch = t.lengthSq() > 1e-9 ? Math.abs(t.normalize().y) : 0;
      uprightAt.set(b, m.n.y >= UPRIGHT && pitch <= MAX_PITCH);
    });
    // Closed or not per metre along the road, by how far apart its two edges are.
    const ends = new Map<number, { low: number; high: number; a: THREE.Vector3; b: THREE.Vector3 }>();
    for (let i = 0; i < position.count; i++) {
      if (!onSurface(road, i)) continue;
      const d = Math.round(road.surface[i * 2]), across = road.surface[i * 2 + 1];
      const at = new THREE.Vector3().fromBufferAttribute(position, i).applyMatrix4(road.mesh.matrixWorld);
      const e = ends.get(d);
      if (!e) { ends.set(d, { low: across, high: across, a: at, b: at.clone() }); continue; }
      if (across < e.low) { e.low = across; e.a = at; }
      if (across > e.high) { e.high = across; e.b = at; }
    }
    const closedNear = (d: number) => {
      const b = Math.round(d);
      for (let k = 0; k <= 4; k++) {
        const e = ends.get(b + k) ?? ends.get(b - k);
        if (e) return e.high - e.low > 2 * SLOT_CLOSED && e.a.distanceTo(e.b) < SLOT_CLOSED;
      }
      return false;
    };
    const uprightNear = (d: number, fallback: boolean) => {
      const b = Math.round(d);
      for (let k = 0; k <= 4; k++) { const u = uprightAt.get(b + k) ?? uprightAt.get(b - k); if (u !== undefined) return u; }
      return fallback;
    };
    triangles(road, (ia, ib, ic) => {
      if (!onSurface(road, ia) || !onSurface(road, ib) || !onSurface(road, ic)) return;
      n.set(0, 0, 0);
      for (const i of [ia, ib, ic]) n.add(q.fromBufferAttribute(normals, i));
      n.transformDirection(road.mesh.matrixWorld);
      const along = (road.surface[ia * 2] + road.surface[ib * 2] + road.surface[ic * 2]) / 3;
      if (closedNear(along)) {
        points(road, ia, ib, ic, 1, (v) => tunnels.add(column(v.x, v.z)));
        return;
      }
      const upright = uprightNear(along, n.y >= UPRIGHT);
      points(road, ia, ib, ic, 1, (v) => {
        const k = voxel(v);
        votes.set(k, (votes.get(k) ?? 0) + (upright ? 1 : -1));
        if (upright) {
          const t = column(v.x, v.z);
          tops.set(t, v.y);
          return;
        }
        // The room to drive: off the surface along its normal, kept clear a square either side.
        for (let d = 0; d <= DRIVE_ROOM; d++) {
          q.copy(v).addScaledVector(n, d);
          const cx = Math.floor(q.x / CELL), cz = Math.floor(q.z / CELL);
          for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) cap(key(cx + dx, cz + dz), q.y);
        }
      });
    });
  }
  // The rest of the road (curbs, sides, underside) goes with the driving surface round it: by upright road its curbs
  // (not what stands higher: gates, signs), by walls and inverted road their structure. Further from any driving
  // surface (pillars) it does not count.
  const surfaceTops = new Map([...heights].map(([k, l]) => [k, Math.max(...l)]));
  const nearTop = (x: number, z: number) => {
    const cx = Math.floor(x / CELL), cz = Math.floor(z / CELL);
    let top = -Infinity;
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) top = Math.max(top, surfaceTops.get(key(cx + dx, cz + dz)) ?? -Infinity);
    return top;
  };
  const vote = (v: THREE.Vector3) => {
    let sum = 0;
    const [x, y, z] = [v.x, v.y, v.z].map((u) => Math.floor(u / VOXEL));
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) for (let dz = -1; dz <= 1; dz++) sum += votes.get(`${x + dx},${y + dy},${z + dz}`) ?? 0;
    return sum;
  };
  for (const road of roads)
    triangles(road, (ia, ib, ic) => {
      if (onSurface(road, ia) && onSurface(road, ib) && onSurface(road, ic)) return;
      points(road, ia, ib, ic, 1, (v) => {
        const t = column(v.x, v.z), side = vote(v);
        if (side > 0) { if (v.y <= nearTop(v.x, v.z) + CURB_REACH) tops.set(t, v.y); }
        else if (side < 0) cap(t, v.y);
      });
    });
  // Per square the top of its highest road: that one guides the land. A road under it (a bridge over a road) has no say:
  // the land lies over it, and it cuts its tunnel through (VoxelTerrain).
  const highest = new Map<string, number>();
  for (const [k, list] of heights) highest.set(k, Math.max(...list));
  return { tops: highest, caps, tunnels };
}

export function terrainSurface(theme: SceneryTheme): TerrainSurface {
  return theme.land === "Dirt" ? "Dirt" : theme.land === "Ice" ? "Ice" : "Grass";
}

/** Smooth 0..1 ramp. */
const smooth = (t: number) => { const x = Math.min(1, Math.max(0, t)); return x * x * (3 - 2 * x); };

/**
 * The rise across the land, as [share of the width from the road, share of the crest's height]: almost flat by the road,
 * curving up into a steep climb, a rounded crest near the outer edge, falling off a little behind it.
 */
const RISE_PROFILE = [[0, 0], [0.21, 0.033], [0.41, 0.083], [0.51, 0.158], [0.61, 0.264], [0.656, 0.353], [0.74, 0.54], [0.787, 0.71],
  [0.836, 0.833], [0.87, 0.936], [0.918, 0.986], [0.967, 1], [0.98, 0.97], [1, 0.897]];

/** The distance the rise climbs over at `d` from the road: RISE_PROFILE as a smooth curve (cubic Hermite), its crest at the land's width. */
function riseDistance(d: number, width: number): number {
  const t = Math.min(1, Math.max(0, d / width)), p = RISE_PROFILE;
  let i = 0;
  while (i < p.length - 2 && t > p[i + 1][0]) i++;
  const slope = (k: number) => (p[Math.min(p.length - 1, k + 1)][1] - p[Math.max(0, k - 1)][1]) / (p[Math.min(p.length - 1, k + 1)][0] - p[Math.max(0, k - 1)][0]);
  const span = p[i + 1][0] - p[i][0], u = (t - p[i][0]) / span;
  const h = (2 * u ** 3 - 3 * u ** 2 + 1) * p[i][1] + (u ** 3 - 2 * u ** 2 + u) * span * slope(i)
    + (-2 * u ** 3 + 3 * u ** 2) * p[i + 1][1] + (u ** 3 - u ** 2) * span * slope(i + 1);
  return h * width;
}

/** Value noise in -1..1 (smooth, about `size` metres per bump), by position; another `seed` gives other bumps (0: those of maps without one). */
function noise2(x: number, z: number, size: number, seed: number): number {
  const hash = (i: number, j: number) => {
    let h = Math.imul(i, 374761393) ^ Math.imul(j, 668265263) ^ Math.imul(seed, 0x9e3779b1);
    h = Math.imul(h ^ (h >>> 13), 1274126177);
    return ((h ^ (h >>> 16)) >>> 0) / 4294967295 * 2 - 1;
  };
  const fx = x / size, fz = z / size;
  const i = Math.floor(fx), j = Math.floor(fz);
  const u = smooth(fx - i), v = smooth(fz - j);
  const a = hash(i, j), b = hash(i + 1, j), c = hash(i, j + 1), d = hash(i + 1, j + 1);
  return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
}

/** Noise with detail: the bumps of `size`, smaller ones on them. */
const fractal = (x: number, z: number, size: number, seed: number) =>
  0.6 * noise2(x, z, size, seed) + 0.28 * noise2(x + 517, z - 211, size / 2.3, seed) + 0.12 * noise2(x - 89, z + 733, size / 5.1, seed);

/** Box blur of a grid with gaps (null): each value the mean of the values within `radius` cells on one axis. */
function blurAxis(values: Float64Array, valid: Uint8Array, columns: number, rows: number, radius: number, alongX: boolean): Float64Array {
  const out = new Float64Array(values.length);
  const [n, lines] = alongX ? [columns, rows] : [rows, columns];
  const index = (line: number, i: number) => (alongX ? line * columns + i : i * columns + line);
  for (let line = 0; line < lines; line++) {
    let sum = 0, count = 0;
    // A sliding window over the line.
    for (let i = -radius; i < n; i++) {
      const add = i + radius, drop = i - radius - 1;
      if (add < n && valid[index(line, add)]) { sum += values[index(line, add)]; count++; }
      if (drop >= 0 && valid[index(line, drop)]) { sum -= values[index(line, drop)]; count--; }
      if (i >= 0) out[index(line, i)] = count > 0 ? sum / count : values[index(line, i)];
    }
  }
  return out;
}

/** The height map round the roads (world space); null without upright roads or when it would be too big. */
export function buildTerrainGrid(roads: TerrainRoad[], options: SceneryLandOptions, own: OwnTerrainOptions = OWN_TERRAIN_DEFAULTS, water = false): TerrainGrid | null {
  // Per road square its highest point (the curbs' tops or the surface's edge: the road's shape the land continues), and
  // under walls and inverted road the height the land stays below.
  const { tops, tunnels } = roadShape(roads);
  if (tops.size === 0) return null;
  const width = Math.max(BLEND, own.width);
  // The smoothing reaches past the land's edge, so the land there is smoothed like everywhere.
  const reach = width + own.smoothing;

  // The grid: a vertex at each road square's lower corner, over the roads and `reach` round them.
  let sx0 = Infinity, sz0 = Infinity, sx1 = -Infinity, sz1 = -Infinity;
  for (const k of [...tops.keys(), ...tunnels]) {
    const [x, z] = k.split(",").map(Number);
    sx0 = Math.min(sx0, x); sz0 = Math.min(sz0, z); sx1 = Math.max(sx1, x); sz1 = Math.max(sz1, z);
  }
  const pad = Math.ceil(reach / CELL) + 1;
  sx0 -= pad; sz0 -= pad; sx1 += pad; sz1 += pad;
  const columns = sx1 - sx0 + 1, rows = sz1 - sz0 + 1;
  if (columns * rows > 2_000_000) return null;
  const x0 = sx0 * CELL, z0 = sz0 * CELL;

  // The nearest road square in a straight line, for every vertex (vertex (c, r) stands on square (c, r)'s corner): a
  // two-pass Euclidean distance transform, each vertex taking its neighbours' nearest road square where that is nearer.
  const n = columns * rows;
  const nearestSquare = (squares: Iterable<string>) => {
    const srcX = new Int32Array(n).fill(-1), srcZ = new Int32Array(n).fill(-1);
    for (const k of squares) {
      const [x, z] = k.split(",").map(Number);
      srcX[(z - sz0) * columns + (x - sx0)] = x - sx0;
      srcZ[(z - sz0) * columns + (x - sx0)] = z - sz0;
    }
    const dist2 = (i: number, x: number, z: number) => srcX[i] < 0 ? Infinity : (srcX[i] - x) ** 2 + (srcZ[i] - z) ** 2;
    const relax = (x: number, z: number, dx: number, dz: number) => {
      const nx = x + dx, nz = z + dz;
      if (nx < 0 || nz < 0 || nx >= columns || nz >= rows) return;
      const i = z * columns + x, j = nz * columns + nx;
      if (dist2(j, x, z) < dist2(i, x, z)) { srcX[i] = srcX[j]; srcZ[i] = srcZ[j]; }
    };
    for (let z = 0; z < rows; z++) {
      for (let x = 0; x < columns; x++) { relax(x, z, -1, 0); relax(x, z, 0, -1); relax(x, z, -1, -1); relax(x, z, 1, -1); }
      for (let x = columns - 1; x >= 0; x--) relax(x, z, 1, 0);
    }
    for (let z = rows - 1; z >= 0; z--) {
      for (let x = columns - 1; x >= 0; x--) { relax(x, z, 1, 0); relax(x, z, 0, 1); relax(x, z, 1, 1); relax(x, z, -1, 1); }
      for (let x = 0; x < columns; x++) relax(x, z, -1, 0);
    }
    return { srcX, srcZ };
  };
  const { srcX, srcZ } = nearestSquare(tops.keys());
  const tunnel = nearestSquare(tunnels);

  // 1. The roads' shape extended: each vertex at the top of its nearest road square. The land also reaches round tunnels,
  // though they do not shape it.
  const extended = new Float64Array(n), distance = new Float64Array(n), toLand = new Float64Array(n), valid = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    if (srcX[i] < 0) continue;
    const c = i % columns, r = Math.floor(i / columns);
    const d = Math.hypot(srcX[i] + 0.5 - c, srcZ[i] + 0.5 - r) * CELL;
    const near = tunnel.srcX[i] < 0 ? d : Math.min(d, Math.hypot(tunnel.srcX[i] + 0.5 - c, tunnel.srcZ[i] + 0.5 - r) * CELL);
    if (near > reach) continue;
    distance[i] = d;
    toLand[i] = near;
    extended[i] = tops.get(key(srcX[i] + sx0, srcZ[i] + sz0))!;
    valid[i] = 1;
  }
  // 2. Smoothed: three box blurs each way (close to a Gaussian of the smoothing's radius).
  const slope = own.rise / 100;
  let smoothed: Float64Array = slope !== 0 ? extended.map((h, i) => h + slope * riseDistance(distance[i], width)) : extended;
  const radius = Math.max(1, Math.round(own.smoothing / CELL / 1.7));
  for (let pass = 0; pass < 3; pass++)
    smoothed = blurAxis(blurAxis(smoothed, valid, columns, rows, radius, true), valid, columns, rows, radius, false);

  const seed = Math.round(own.seed ?? 0) | 0;
  const drop = Math.max(0, own.edgeDrop ?? EDGE_DROP), bend = Math.min(2 * drop, width / 2);
  // Blend and noise (under the roads too: the roads cut their space out of the land when it is meshed).
  const heights: (number | null)[] = new Array(n).fill(null);
  for (let i = 0; i < n; i++) {
    if (!valid[i] || toLand[i] > width) continue;
    const c = i % columns, r = Math.floor(i / columns);
    const x = x0 + c * CELL, z = z0 + r * CELL;
    const w = smooth(distance[i] / BLEND);
    let h = extended[i] * (1 - w) + smoothed[i] * w;
    h += own.noise * w * fractal(x, z, own.noiseSize, seed);
    // The bend down to the outer edge: a parabola over the last `bend` metres.
    if (drop > 0 && toLand[i] > width - bend) h -= drop * ((toLand[i] - (width - bend)) / bend) ** 2;
    h = Math.min(h, MAX_LAND);
    // On the stadium's grid the ground is there: land at its height would flicker with it, so land that low sinks under
    // it (no hole in the land). Outside it there is no ground: land ends there.
    // In the environments with water the land goes on under it (shores, the sea's bed); its surface lies at -0.85 m.
    if (water) heights[i] = Math.max(h, MIN_LAND);
    else if (Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT) heights[i] = h < 0.5 ? SUNK : h;
    else if (h >= 0.05) heights[i] = h;
  }
  // No walls under the land's outer edge: they cost triangles and nobody drives there.
  return { surface: terrainSurface(options.theme), cell: CELL, x0, z0, columns, rows, heights, skirt: 0, rockAngle: own.rockAngle, ...(own.quality && own.quality !== "High" ? { quality: own.quality } : {}), ...(own.mesher === "seamless" ? { mesher: own.mesher } : {}) };
}

/** The terrain's height at (x, z) on its grid's triangles (TerrainMeshBuilder.SurfaceHeight). */
export function terrainHeight(t: TerrainGrid, x: number, z: number): number | null {
  const fx = (x - t.x0) / t.cell, fz = (z - t.z0) / t.cell;
  const c = Math.floor(fx), r = Math.floor(fz);
  if (c < 0 || r < 0 || c + 1 >= t.columns || r + 1 >= t.rows) return null;
  const h = (cc: number, rr: number) => t.heights[rr * t.columns + cc];
  const h00 = h(c, r), h10 = h(c + 1, r), h01 = h(c, r + 1), h11 = h(c + 1, r + 1);
  if (h00 === null || h10 === null || h01 === null || h11 === null) return null;
  const u = fx - c, v = fz - r;
  if (Math.abs(h00 - h11) <= Math.abs(h10 - h01))
    return u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v : h00 + (h11 - h01) * u + (h01 - h00) * v;
  return u + v <= 1 ? h00 + (h10 - h00) * u + (h01 - h00) * v : h11 + (h01 - h11) * (1 - u) + (h10 - h11) * (1 - v);
}

/**
 * The finished mesh's height at (x, z), null where it has no land (cut out at a road, off the land). The grid is only the
 * land before it is fitted to the roads: near them the mesh can be metres higher or lower. Skirts have no area from above.
 */
export function meshHeightAt(mesh: { positions: Float32Array; indices: Uint32Array }): (x: number, z: number) => number | null {
  const SIZE = 16;
  const { positions: p, indices } = mesh;
  const bucket = (c: number, r: number) => (c + 32768) * 65536 + (r + 32768);
  const buckets = new Map<number, number[]>();
  for (let t = 0; t < indices.length; t += 3) {
    const [a, b, c] = [indices[t] * 3, indices[t + 1] * 3, indices[t + 2] * 3];
    const c0 = Math.floor(Math.min(p[a], p[b], p[c]) / SIZE), c1 = Math.floor(Math.max(p[a], p[b], p[c]) / SIZE);
    const r0 = Math.floor(Math.min(p[a + 2], p[b + 2], p[c + 2]) / SIZE), r1 = Math.floor(Math.max(p[a + 2], p[b + 2], p[c + 2]) / SIZE);
    for (let r = r0; r <= r1; r++)
      for (let col = c0; col <= c1; col++) {
        const k = bucket(col, r);
        const list = buckets.get(k);
        if (list) list.push(t);
        else buckets.set(k, [t]);
      }
  }
  return (x, z) => {
    let best: number | null = null;
    for (const t of buckets.get(bucket(Math.floor(x / SIZE), Math.floor(z / SIZE))) ?? []) {
      const [a, b, c] = [indices[t] * 3, indices[t + 1] * 3, indices[t + 2] * 3];
      const det = (p[b + 2] - p[c + 2]) * (p[a] - p[c]) + (p[c] - p[b]) * (p[a + 2] - p[c + 2]);
      if (Math.abs(det) < 1e-3) continue;
      const l1 = ((p[b + 2] - p[c + 2]) * (x - p[c]) + (p[c] - p[b]) * (z - p[c + 2])) / det;
      const l2 = ((p[c + 2] - p[a + 2]) * (x - p[c]) + (p[a] - p[c]) * (z - p[c + 2])) / det;
      const l3 = 1 - l1 - l2;
      if (l1 < -1e-4 || l2 < -1e-4 || l3 < -1e-4) continue;
      const y = l1 * p[a + 1] + l2 * p[b + 1] + l3 * p[c + 1];
      if (best === null || y > best) best = y;
    }
    return best;
  };
}

/** Trees on the terrain: per 32 m cell with land, at the land's height, kept clear of the roads. */
export function plantOnTerrain(t: TerrainGrid, options: SceneryLandOptions, footprint: Set<string>): VanillaItem[] {
  const cells = new Map<string, VanillaBlock>();
  const cx0 = Math.floor(t.x0 / BLOCK_CELL), cz0 = Math.floor(t.z0 / BLOCK_CELL);
  const cx1 = Math.ceil((t.x0 + t.cell * (t.columns - 1)) / BLOCK_CELL), cz1 = Math.ceil((t.z0 + t.cell * (t.rows - 1)) / BLOCK_CELL);
  for (let x = cx0; x < cx1; x++)
    for (let z = cz0; z < cz1; z++)
      if (terrainHeight(t, (x + 0.5) * BLOCK_CELL, (z + 0.5) * BLOCK_CELL) !== null)
        cells.set(key(x, z), { name: "terrain", x, y: 0, z, dir: 0 });
  const clear = (px: number, pz: number) => clearOfRoads(footprint, px, pz, Math.max(0, options.clearance));
  // Not on land sunk under the stadium's ground (the trees stand on the ground there anyway, but not in the roads' way).
  const height = (x: number, z: number) => { const h = terrainHeight(t, x, z); return h === null || h < 0.3 ? null : h; };
  return plantForest(new Map(), options.forest, options.density, options.theme.id, clear, cells, (_b, x, z) => height(x, z));
}
