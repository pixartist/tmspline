import * as THREE from "three";
import { BLOCK_CELL, MAP_HALF_EXTENT, type TerrainGrid, type TerrainSurface, type VanillaBlock, type VanillaItem } from "./track";
import { clearOfRoads, FOOTPRINT_CELL, plantForest, roadColumns, roadFootprint, sampleRoads, type SceneryLandOptions, type SceneryTheme } from "./scenery";

/**
 * Own terrain: instead of the game's land blocks, a mesh of our own round the tracks (exported as custom items with the
 * hill blocks' materials, see TerrainMeshBuilder). Here its height map, on a fine grid:
 *
 * 1. The roads' shape extended outwards: every vertex takes the top (curbs, or the surface's edge) of the nearest road.
 * 2. That surface smoothed (a wide blur), blending from the unsmoothed surface at the road into the smoothed one over
 *    the first 32 m, so the land meets the road.
 * 3. Height noise on top, faded in over the same 32 m.
 *
 * Under the roads the land stays below them. The export simplifies the mesh (flat land in big triangles) and adds strips
 * from the roads' edges to the land, so it meets them exactly.
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
}

export const OWN_TERRAIN_DEFAULTS: OwnTerrainOptions = { width: 128, smoothing: 48, noise: 6, noiseSize: 96 };

/** Grid spacing, metres (also the size of the road squares for the caps and distances). */
const CELL = 4;
/** Over this many metres from a road the land blends from the road's shape into the smoothed, noisy land. */
const BLEND = 32;
/**
 * Metres between the roads' lowest point and the land under them: deep enough that a triangle from a vertex on a road's
 * edge (the curb's top, see TerrainMeshBuilder's snapping) to one under the road stays inside the curb and below the
 * surface.
 */
const CLEARANCE = 2.5;
/** Skirt under the land's outer edge, metres. */
const SKIRT = 16;

const key = (x: number, z: number) => `${x},${z}`;

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

/** The height map round the roads (`meshes`, world space); null without roads or when it would be too big. */
export function buildTerrainGrid(meshes: THREE.Mesh[], options: SceneryLandOptions, own: OwnTerrainOptions = OWN_TERRAIN_DEFAULTS): TerrainGrid | null {
  // Per road square its lowest point (the land under the road stays below it) and its highest (the curbs' tops or the
  // surface's edge: the road's shape the land continues).
  const lows = roadColumns(meshes, CELL);
  if (lows.size === 0) return null;
  const tops = new Map<string, number>();
  sampleRoads(meshes, 1, (v) => {
    const k = key(Math.floor(v.x / CELL), Math.floor(v.z / CELL));
    tops.set(k, Math.max(tops.get(k) ?? -Infinity, v.y));
  });
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

  // The nearest road square in a straight line, for every vertex (vertex (c, r) stands on square (c, r)'s corner): a
  // two-pass Euclidean distance transform, each vertex taking its neighbours' nearest road square where that is nearer.
  const n = columns * rows;
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

  // 1. The roads' shape extended: each vertex at the top of its nearest road square.
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
  // 2. Smoothed: three box blurs each way (close to a Gaussian of the smoothing's radius).
  let smoothed: Float64Array = extended;
  const radius = Math.max(1, Math.round(own.smoothing / CELL / 1.7));
  for (let pass = 0; pass < 3; pass++)
    smoothed = blurAxis(blurAxis(smoothed, valid, columns, rows, radius, true), valid, columns, rows, radius, false);

  // The roads' footprint (2 m squares they cover at any height): a vertex is under a road where a square round it is.
  const footprint = roadFootprint(meshes);
  const under = (x: number, z: number) => {
    const fx = Math.round(x / FOOTPRINT_CELL), fz = Math.round(z / FOOTPRINT_CELL);
    return footprint.has(key(fx, fz)) || footprint.has(key(fx - 1, fz)) || footprint.has(key(fx, fz - 1)) || footprint.has(key(fx - 1, fz - 1));
  };
  // Blend and noise, and under the roads the caps.
  const heights: (number | null)[] = new Array(n).fill(null);
  for (let i = 0; i < n; i++) {
    if (!valid[i] || distance[i] > width) continue;
    const c = i % columns, r = Math.floor(i / columns);
    const x = x0 + c * CELL, z = z0 + r * CELL;
    const w = smooth(distance[i] / BLEND);
    let h = extended[i] * (1 - w) + smoothed[i] * w;
    h += own.noise * w * fractal(x, z, own.noiseSize);
    // Under a road (the roads' footprint at the vertex) no higher than the lowest point of the road there; the land beside
    // the roads is free (the export moves the vertices next to them onto their edges).
    if (under(x, z)) {
      let cap = Infinity;
      for (let dx = -1; dx <= 0; dx++)
        for (let dz = -1; dz <= 0; dz++) {
          const y = lows.get(key(c + sx0 + dx, r + sz0 + dz));
          if (y !== undefined) cap = Math.min(cap, y - CLEARANCE);
        }
      h = Math.min(h, cap);
    }
    // On the stadium's grid the ground is there: land at its height would flicker with it.
    if (Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT && h < 0.5) continue;
    heights[i] = Math.max(0.05, h);
  }
  return { surface: terrainSurface(options.theme), cell: CELL, x0, z0, columns, rows, heights, skirt: SKIRT };
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
  return plantForest(new Map(), options.forest, options.density, options.theme.id, clear, cells, (_b, x, z) => terrainHeight(t, x, z));
}
