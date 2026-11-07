import * as THREE from "three";
import { BLOCK_CELL, MAP_HALF_EXTENT, type TerrainGrid, type TerrainSurface, type VanillaBlock, type VanillaItem } from "./track";
import { clearOfRoads, plantForest, type SceneryLandOptions, type SceneryTheme } from "./scenery";

/**
 * Own terrain: instead of the game's land blocks, a mesh of our own round the tracks (exported as custom items with the
 * hill blocks' materials, see TerrainMeshBuilder). Here its height map, on a fine grid, the lowest of three:
 *
 * 1. The land: the roads' shape extended outwards (every vertex at the top, curbs or the surface's edge, of the nearest
 *    road that faces up), smoothed with a wide blur, blending from the road's shape at the road into the smoothed land
 *    over the first 32 m, height noise on top faded in over the same 32 m.
 * 2. The track: the lowest road at each vertex (with the room to drive below upside-down road), and from it at most 45
 *    degrees up, so land ramps up under a bridge instead of walling in the road below.
 * 3. The steepness: the track lowered where its driving surface faces away from up (nothing where it faces up, most on
 *    walls and upside-down road), widened and blurred, so the land gives way to steep road smoothly.
 *
 * The export builds the mesh from the roads outwards (rows of points along their edges where the land meets them, then
 * this grid, sparser further out) and leaves the roads themselves out, so the land meets them exactly.
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
}

export const OWN_TERRAIN_DEFAULTS: OwnTerrainOptions = { width: 128, smoothing: 48, noise: 6, noiseSize: 96, rockAngle: 50 };

/** Grid spacing, metres (also the size of the road squares). */
const CELL = 4;
/** Over this many metres from a road the land blends from the road's shape into the smoothed, noisy land. */
const BLEND = 32;
/** Skirt under the land's outer edge, metres. */
const SKIRT = 16;
/** Land lower than the stadium's ground lies this deep, under it, metres. */
const SUNK = -2;
/** Off the driving surface (curbs), road counts for the land's shape up to this much over the surface, metres (not gates, signs). */
const CURB_REACH = 3;
/** Roads in one square further apart in height than this are separate (a bridge over a road), metres. */
const LAYER_GAP = 4;
/** Near a road the land rises at most this steeply over it (1: 45 degrees). */
const MAX_RISE = 1;
/** Below upside-down road (along its surface's normal, wherever that points down or sideways): room to drive, metres. */
const DRIVE_ROOM = 4;
/** The land stays this much under that room, metres. */
const KEEP_CLEAR = 0.5;
/**
 * Steepness of the driving surface by its normal's y: from STEEP_FROM (up to about 25 degrees: none) to STEEP_FULL (about
 * 72 degrees and beyond: full), the land kept up to STEEP_DEPTH metres under it.
 */
const STEEP_FROM = 0.9, STEEP_FULL = 0.3, STEEP_DEPTH = 8;
/** Road facing up less than this does not shape the land (walls, upside down; their steepness keeps it away). */
const SHAPES_LAND = 0.3;
/** The steepness map widened by this many cells, then blurred over this radius in cells (three box blurs). */
const STEEP_WIDEN = 1, STEEP_BLUR = 2;

const key = (x: number, z: number) => `${x},${z}`;

/** A road's preview mesh and its vertices' driving-surface coordinates (NaN off the driving surface: curbs, sides, underside). */
export interface TerrainRoad { mesh: THREE.Mesh; surface: Float32Array }

export function terrainSurface(theme: SceneryTheme): TerrainSurface {
  return theme.land === "Dirt" ? "Dirt" : theme.land === "Ice" ? "Ice" : "Grass";
}

/** Smooth 0..1 ramp. */
const smooth = (t: number) => { const x = Math.min(1, Math.max(0, t)); return x * x * (3 - 2 * x); };

/** Value noise in -1..1 (smooth, about `size` metres per bump), seeded by position. */
function noise2(x: number, z: number, size: number): number {
  const hash = (i: number, j: number) => {
    let h = Math.imul(i, 374761393) ^ Math.imul(j, 668265263);
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
const fractal = (x: number, z: number, size: number) =>
  0.6 * noise2(x, z, size) + 0.28 * noise2(x + 517, z - 211, size / 2.3) + 0.12 * noise2(x - 89, z + 733, size / 5.1);

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

/**
 * The roads sampled for the land:
 * - `tops`: per CELL square the top of its lowest road that faces up (what the land continues);
 * - `floor`: per grid vertex (x, z multiples of CELL) the lowest road there, and the room to drive below upside-down road;
 * - `steep`: per grid vertex the lowest of the driving surface's heights less its steepness's depth.
 */
function roadShape(roads: TerrainRoad[]): { tops: Map<string, number>; floor: Map<string, number>; steep: Map<string, number> } {
  const layers = new Map<string, number[]>(), floor = new Map<string, number>(), steep = new Map<string, number>();
  const square = (v: THREE.Vector3) => key(Math.floor(v.x / CELL), Math.floor(v.z / CELL));
  const vertex = (v: THREE.Vector3) => key(Math.round(v.x / CELL), Math.round(v.z / CELL));
  const lowest = (map: Map<string, number>, k: string, y: number) => map.set(k, Math.min(map.get(k) ?? Infinity, y));
  const shape = (k: string, y: number) => { const l = layers.get(k); if (l) l.push(y); else layers.set(k, [y]); };
  // Where driving surface is (3D squares), and the top of the surface that shapes the land per square.
  const VOXEL = CELL;
  const voxel = (x: number, y: number, z: number) => `${Math.floor(x / VOXEL)},${Math.floor(y / VOXEL)},${Math.floor(z / VOXEL)}`;
  const surfaceAt = new Set<string>(), surfaceTop = new Map<string, number>();
  const a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3(), p = new THREE.Vector3();
  const n = new THREE.Vector3(), q = new THREE.Vector3();
  const triangles = (road: TerrainRoad, fn: (ia: number, ib: number, ic: number) => void) => {
    const index = road.mesh.geometry.getIndex();
    const count = index ? index.count : road.mesh.geometry.getAttribute("position").count;
    for (let i = 0; i + 2 < count; i += 3) fn(...([0, 1, 2].map((j) => (index ? index.getX(i + j) : i + j)) as [number, number, number]));
  };
  const points = (road: TerrainRoad, ia: number, ib: number, ic: number, mark: (v: THREE.Vector3) => void) => {
    const pos = road.mesh.geometry.getAttribute("position");
    a.fromBufferAttribute(pos, ia).applyMatrix4(road.mesh.matrixWorld);
    b.fromBufferAttribute(pos, ib).applyMatrix4(road.mesh.matrixWorld);
    c.fromBufferAttribute(pos, ic).applyMatrix4(road.mesh.matrixWorld);
    const longest = Math.max(a.distanceTo(b), b.distanceTo(c), c.distanceTo(a));
    const steps = Math.min(64, Math.max(1, Math.ceil(longest)));
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
    triangles(road, (ia, ib, ic) => {
      if (!onSurface(road, ia) || !onSurface(road, ib) || !onSurface(road, ic)) return;
      n.set(0, 0, 0);
      for (const i of [ia, ib, ic]) n.add(q.fromBufferAttribute(normals, i));
      n.transformDirection(road.mesh.matrixWorld);
      const depth = STEEP_DEPTH * smooth((STEEP_FROM - n.y) / (STEEP_FROM - STEEP_FULL));
      points(road, ia, ib, ic, (v) => {
        surfaceAt.add(voxel(v.x, v.y, v.z));
        if (n.y >= SHAPES_LAND) {
          const s = square(v);
          shape(s, v.y);
          surfaceTop.set(s, Math.max(surfaceTop.get(s) ?? -Infinity, v.y));
        }
        lowest(floor, vertex(v), v.y);
        lowest(steep, vertex(v), v.y - depth);
        // The room to drive, off the surface along its normal (only lower than the surface where it faces down).
        if (n.y < 0)
          for (let d = 1; d <= DRIVE_ROOM; d++) {
            q.copy(v).addScaledVector(n, d);
            lowest(floor, vertex(q), q.y - KEEP_CLEAR);
          }
      });
    });
  }
  // The rest of the road (curbs, sides, underside) next to driving surface: the curbs shape the land (not what stands
  // higher: gates, signs), and the land stays under all of it. Further from any driving surface (pillars) it does not count.
  const near = (v: THREE.Vector3) => {
    const [x, y, z] = [v.x, v.y, v.z].map((u) => Math.floor(u / VOXEL));
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) for (let dz = -1; dz <= 1; dz++)
      if (surfaceAt.has(`${x + dx},${y + dy},${z + dz}`)) return true;
    return false;
  };
  const nearTop = (v: THREE.Vector3) => {
    const cx = Math.floor(v.x / CELL), cz = Math.floor(v.z / CELL);
    let top = -Infinity;
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) top = Math.max(top, surfaceTop.get(key(cx + dx, cz + dz)) ?? -Infinity);
    return top;
  };
  for (const road of roads)
    triangles(road, (ia, ib, ic) => {
      if (onSurface(road, ia) && onSurface(road, ib) && onSurface(road, ic)) return;
      points(road, ia, ib, ic, (v) => {
        if (!near(v)) return;
        lowest(floor, vertex(v), v.y);
        if (v.y <= nearTop(v) + CURB_REACH && nearTop(v) > -Infinity) shape(square(v), v.y);
      });
    });
  // Per square the top of its lowest road (the heights up to the first gap of LAYER_GAP).
  const tops = new Map<string, number>();
  for (const [k, list] of layers) {
    list.sort((u, w) => u - w);
    let top = list[0];
    for (let i = 1; i < list.length && list[i] - top <= LAYER_GAP; i++) top = list[i];
    tops.set(k, top);
  }
  return { tops, floor, steep };
}

/** Min (or max) over the cells within `radius` cells (a square), of a full grid. */
function spread(values: Float64Array, columns: number, rows: number, radius: number, pick: (u: number, w: number) => number): Float64Array {
  let out = values;
  for (const alongX of [true, false]) {
    const next = new Float64Array(out.length);
    for (let r = 0; r < rows; r++)
      for (let c = 0; c < columns; c++) {
        let best = out[r * columns + c];
        for (let d = -radius; d <= radius; d++) {
          const cc = alongX ? c + d : c, rr = alongX ? r : r + d;
          if (cc >= 0 && rr >= 0 && cc < columns && rr < rows) best = pick(best, out[rr * columns + cc]);
        }
        next[r * columns + c] = best;
      }
    out = next;
  }
  return out;
}

/** The height map round the roads (world space); null without roads facing up or when it would be too big. */
export function buildTerrainGrid(roads: TerrainRoad[], options: SceneryLandOptions, own: OwnTerrainOptions = OWN_TERRAIN_DEFAULTS): TerrainGrid | null {
  const { tops, floor, steep } = roadShape(roads);
  if (tops.size === 0) return null;
  const width = Math.max(BLEND, own.width);
  // The smoothing reaches past the land's edge, so the land there is smoothed like everywhere.
  const reach = width + own.smoothing;

  // The grid: a vertex at each road square's lower corner, over the roads and `reach` round them.
  let sx0 = Infinity, sz0 = Infinity, sx1 = -Infinity, sz1 = -Infinity;
  for (const k of tops.keys()) {
    const [x, z] = k.split(",").map(Number);
    sx0 = Math.min(sx0, x); sz0 = Math.min(sz0, z); sx1 = Math.max(sx1, x); sz1 = Math.max(sz1, z);
  }
  const pad = Math.ceil(reach / CELL) + 1;
  sx0 -= pad; sz0 -= pad; sx1 += pad; sz1 += pad;
  const columns = sx1 - sx0 + 1, rows = sz1 - sz0 + 1;
  if (columns * rows > 2_000_000) return null;
  const x0 = sx0 * CELL, z0 = sz0 * CELL;
  const n = columns * rows;
  const at = (k: string) => { const [x, z] = k.split(",").map(Number); const c = x - sx0, r = z - sz0; return c >= 0 && r >= 0 && c < columns && r < rows ? r * columns + c : -1; };

  // 1. The land. The nearest road square in a straight line, for every vertex (vertex (c, r) stands on square (c, r)'s
  // corner): a two-pass Euclidean distance transform, each vertex taking its neighbours' nearest road square where that is nearer.
  const srcX = new Int32Array(n).fill(-1), srcZ = new Int32Array(n).fill(-1);
  for (const k of tops.keys()) {
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
  // The roads' shape extended: each vertex at the top of its nearest road square...
  const extended = new Float64Array(n), distance = new Float64Array(n), valid = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    if (srcX[i] < 0) continue;
    const c = i % columns, r = Math.floor(i / columns);
    const d = Math.hypot(srcX[i] + 0.5 - c, srcZ[i] + 0.5 - r) * CELL;
    if (d > reach) continue;
    distance[i] = d;
    extended[i] = tops.get(key(srcX[i] + sx0, srcZ[i] + sz0))!;
    valid[i] = 1;
  }
  // ...smoothed: three box blurs each way (close to a Gaussian of the smoothing's radius).
  let smoothed: Float64Array = extended;
  const radius = Math.max(1, Math.round(own.smoothing / CELL / 1.7));
  for (let pass = 0; pass < 3; pass++)
    smoothed = blurAxis(blurAxis(smoothed, valid, columns, rows, radius, true), valid, columns, rows, radius, false);

  // 2. The track: its lowest road per vertex, and at most MAX_RISE up from it round about (a chamfer distance).
  const track = new Float64Array(n).fill(Infinity);
  for (const [k, y] of floor) { const i = at(k); if (i >= 0) track[i] = Math.min(track[i], y); }
  const axial = MAX_RISE * CELL, diagonal = MAX_RISE * CELL * Math.SQRT2;
  const lower = (i: number, j: number, cost: number) => { if (track[j] + cost < track[i]) track[i] = track[j] + cost; };
  for (let r = 0; r < rows; r++)
    for (let c = 0; c < columns; c++) {
      const i = r * columns + c;
      if (c > 0) lower(i, i - 1, axial);
      if (r > 0) { lower(i, i - columns, axial); if (c > 0) lower(i, i - columns - 1, diagonal); if (c + 1 < columns) lower(i, i - columns + 1, diagonal); }
    }
  for (let r = rows - 1; r >= 0; r--)
    for (let c = columns - 1; c >= 0; c--) {
      const i = r * columns + c;
      if (c + 1 < columns) lower(i, i + 1, axial);
      if (r + 1 < rows) { lower(i, i + columns, axial); if (c + 1 < columns) lower(i, i + columns + 1, diagonal); if (c > 0) lower(i, i + columns - 1, diagonal); }
    }

  // 3. The steepness: how far under the track the land must stay (0 where the road faces up), widened and blurred.
  let push: Float64Array = new Float64Array(n);
  for (const [k, y] of steep) { const i = at(k); if (i >= 0 && Number.isFinite(track[i])) push[i] = Math.max(push[i], track[i] - y); }
  push = spread(push, columns, rows, STEEP_WIDEN, Math.max);
  const all = new Uint8Array(n).fill(1);
  for (let pass = 0; pass < 3; pass++)
    push = blurAxis(blurAxis(push, all, columns, rows, STEEP_BLUR, true), all, columns, rows, STEEP_BLUR, false);

  // The lowest of the three (the land's noise and blend under the roads too: the export's mesh ends at their edges).
  const heights: (number | null)[] = new Array(n).fill(null);
  for (let i = 0; i < n; i++) {
    if (!valid[i] || distance[i] > width) continue;
    const c = i % columns, r = Math.floor(i / columns);
    const x = x0 + c * CELL, z = z0 + r * CELL;
    const w = smooth(distance[i] / BLEND);
    let h = extended[i] * (1 - w) + smoothed[i] * w;
    h += own.noise * w * fractal(x, z, own.noiseSize);
    h = Math.min(h, track[i] - push[i]);
    // On the stadium's grid the ground is there: land at its height would flicker with it, so land that low sinks under
    // it (no hole in the land, so no skirt). Outside it there is no ground: land ends there.
    if (Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT) heights[i] = h < 0.5 ? SUNK : h;
    else if (h >= 0.05) heights[i] = h;
  }
  return { surface: terrainSurface(options.theme), cell: CELL, x0, z0, columns, rows, heights, skirt: SKIRT, rockAngle: own.rockAngle };
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
