import * as THREE from "three";
import { BLOCK_CELL, BLOCK_LEVEL, MAP_HALF_EXTENT, type TerrainGrid, type TerrainSurface, type VanillaBlock, type VanillaItem } from "./track";
import { clearOfRoads, plantForest, roadColumns, sampleRoads, type SceneryLandOptions, type SceneryTheme } from "./scenery";

/**
 * Own terrain: instead of the game's land blocks, a mesh of our own round the tracks (exported as custom items with the
 * hill blocks' materials, see TerrainMeshBuilder). Here its height map: a vertex grid (`cell` metres) whose heights stay
 * just below the roads near them and rise smoothly into hills away from them, with a little noise. The export adds
 * strips from the roads' edges (curb tops, or the surface's edge) to the grid, so the land meets the roads exactly.
 */

export interface OwnTerrainOptions {
  /** Grid spacing, metres. */
  cell: number;
  /** Metres between the roads (their lowest point near a vertex) and the land under and beside them. */
  clearance: number;
  /** Height of the bumps of the noise, metres (0: none). */
  noise: number;
  /** Skirt under the land's outer edge, metres. */
  skirt: number;
}

export const OWN_TERRAIN_DEFAULTS: OwnTerrainOptions = { cell: 8, clearance: 0.5, noise: 4, skirt: 16 };

/** The road squares' size for the caps and distances, metres. */
const SQUARE = 4;

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

/**
 * The height map round the roads (`meshes`, world space). Shoulder, band, hill step, hill height and rise over distance
 * come from the scenery options (cells of 32 m, levels of 8 m), as for the block scenery.
 */
export function buildTerrainGrid(meshes: THREE.Mesh[], options: SceneryLandOptions, own: OwnTerrainOptions = OWN_TERRAIN_DEFAULTS): TerrainGrid | null {
  // Per road square its lowest point (the land under the road stays below it) and its highest (the curbs' tops or the
  // surface's edge: the land beside the road starts there, as the strips to the road's edges do).
  const roads = roadColumns(meshes, SQUARE);
  if (roads.size === 0) return null;
  const tops = new Map<string, number>();
  sampleRoads(meshes, 1, (v) => {
    const k = key(Math.floor(v.x / SQUARE), Math.floor(v.z / SQUARE));
    tops.set(k, Math.max(tops.get(k) ?? -Infinity, v.y));
  });
  const shoulder = Math.max(0, options.gap) * BLOCK_CELL;
  const reach = shoulder + Math.max(1, options.band) * BLOCK_CELL;
  // The nearest road square in a straight line, and its top (a step-counting spread picked squares far up or down a steep
  // road, so the land beside it sank or rose): a two-pass Euclidean distance transform over the squares round the roads,
  // each square passing on its neighbours' nearest road square where that is nearer.
  let sx0 = Infinity, sz0 = Infinity, sx1 = -Infinity, sz1 = -Infinity;
  for (const k of tops.keys()) {
    const [x, z] = k.split(",").map(Number);
    sx0 = Math.min(sx0, x); sz0 = Math.min(sz0, z); sx1 = Math.max(sx1, x); sz1 = Math.max(sz1, z);
  }
  const pad = Math.ceil(reach / SQUARE) + 2;
  sx0 -= pad; sz0 -= pad; sx1 += pad; sz1 += pad;
  const w = sx1 - sx0 + 1, hgt = sz1 - sz0 + 1;
  const srcX = new Int32Array(w * hgt).fill(-1), srcZ = new Int32Array(w * hgt).fill(-1);
  for (const k of tops.keys()) {
    const [x, z] = k.split(",").map(Number);
    srcX[(z - sz0) * w + (x - sx0)] = x - sx0;
    srcZ[(z - sz0) * w + (x - sx0)] = z - sz0;
  }
  const dist2 = (i: number, x: number, z: number) => srcX[i] < 0 ? Infinity : (srcX[i] - x) ** 2 + (srcZ[i] - z) ** 2;
  const relax = (x: number, z: number, dx: number, dz: number) => {
    const nx = x + dx, nz = z + dz;
    if (nx < 0 || nz < 0 || nx >= w || nz >= hgt) return;
    const i = z * w + x, j = nz * w + nx;
    if (dist2(j, x, z) < dist2(i, x, z)) { srcX[i] = srcX[j]; srcZ[i] = srcZ[j]; }
  };
  for (let z = 0; z < hgt; z++) {
    for (let x = 0; x < w; x++) { relax(x, z, -1, 0); relax(x, z, 0, -1); relax(x, z, -1, -1); relax(x, z, 1, -1); }
    for (let x = w - 1; x >= 0; x--) relax(x, z, 1, 0);
  }
  for (let z = hgt - 1; z >= 0; z--) {
    for (let x = w - 1; x >= 0; x--) { relax(x, z, 1, 0); relax(x, z, 0, 1); relax(x, z, 1, 1); relax(x, z, -1, 1); }
    for (let x = 0; x < w; x++) relax(x, z, -1, 0);
  }
  const nearest = (x: number, z: number): { d: number; top: number } | null => {
    const gx = Math.floor(x / SQUARE) - sx0, gz = Math.floor(z / SQUARE) - sz0;
    if (gx < 0 || gz < 0 || gx >= w || gz >= hgt) return null;
    const i = gz * w + gx;
    if (srcX[i] < 0) return null;
    const rx = srcX[i] + sx0, rz = srcZ[i] + sz0;
    return { d: Math.hypot((rx + 0.5) * SQUARE - x, (rz + 0.5) * SQUARE - z), top: tops.get(key(rx, rz))! };
  };

  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const k of roads.keys()) {
    const [x, z] = k.split(",").map(Number);
    minX = Math.min(minX, x * SQUARE); maxX = Math.max(maxX, (x + 1) * SQUARE);
    minZ = Math.min(minZ, z * SQUARE); maxZ = Math.max(maxZ, (z + 1) * SQUARE);
  }
  const cell = own.cell;
  const x0 = Math.floor((minX - reach) / cell) * cell, z0 = Math.floor((minZ - reach) / cell) * cell;
  const columns = Math.ceil((maxX + reach - x0) / cell) + 1, rows = Math.ceil((maxZ + reach - z0) / cell) + 1;
  if (columns * rows > 400_000) return null;

  // Hills: rising 2 levels every hill step beyond the shoulder, up to the hill height, smoothly; then the rise over
  // distance, and the noise (none on the shoulder).
  const hillHeight = Math.max(0, options.hillRise) * BLOCK_LEVEL;
  const hillLength = Math.max(1, options.hillStep) * BLOCK_CELL * Math.max(1, options.hillRise) / 2;
  const heights: (number | null)[] = new Array(columns * rows).fill(null);
  const capRadius = Math.ceil(cell / SQUARE);
  const caps: (number | null)[] = new Array(columns * rows).fill(null);
  for (let r = 0; r < rows; r++)
    for (let c = 0; c < columns; c++) {
      const x = x0 + c * cell, z = z0 + r * cell;
      const sx = Math.floor(x / SQUARE), sz = Math.floor(z / SQUARE);
      const near = nearest(x, z);
      if (!near || near.d > reach) continue;
      const d = near.d;
      // Under the roads the land stays below their lowest point: every vertex of a triangle that reaches under a road
      // (the road within a cell of the vertex). Beyond that it may rise above the road: the strips from the roads' edges
      // reach over this zone onto the land.
      let cap = Infinity;
      for (let dx = -capRadius; dx <= capRadius; dx++)
        for (let dz = -capRadius; dz <= capRadius; dz++) {
          const y = roads.get(key(sx + dx, sz + dz));
          if (y !== undefined) cap = Math.min(cap, y);
        }
      const out = Math.max(0, d - shoulder);
      let h = near.top;
      h += hillHeight * smooth(out / hillLength);
      if (options.farRise > 0) h += options.farRise * BLOCK_LEVEL * out / BLOCK_CELL;
      h += own.noise * smooth(out / BLOCK_CELL) * noise2(x, z, 64);
      if (cap < Infinity) caps[r * columns + c] = cap - own.clearance;
      heights[r * columns + c] = h;
    }
  // A little smoothing (the hills' foot, the noise) of the free land only: the vertices held below the roads neither
  // move nor pull their neighbours down (that dug a ditch beside the strips).
  for (let pass = 0; pass < 2; pass++) {
    const next = heights.slice();
    for (let r = 1; r < rows - 1; r++)
      for (let c = 1; c < columns - 1; c++) {
        const i = r * columns + c;
        if (heights[i] === null || caps[i] !== null) continue;
        const n = [i - 1, i + 1, i - columns, i + columns].filter((j) => heights[j] !== null && caps[j] === null).map((j) => heights[j]!);
        next[i] = (heights[i]! * 2 + n.reduce((s, v) => s + v, 0)) / (2 + n.length);
      }
    for (let i = 0; i < next.length; i++) heights[i] = next[i];
  }
  for (let i = 0; i < heights.length; i++) {
    if (heights[i] === null) continue;
    if (caps[i] !== null) heights[i] = Math.min(heights[i]!, caps[i]!);
    // On the stadium's grid the ground is there: land at its height would flicker with it.
    const x = x0 + (i % columns) * cell, z = z0 + Math.floor(i / columns) * cell;
    if (Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT && heights[i]! < 0.5) heights[i] = null;
    else heights[i] = Math.max(0.05, heights[i]!);
  }
  return { surface: terrainSurface(options.theme), cell, x0, z0, columns, rows, heights, skirt: own.skirt };
}

/** The terrain's height at (x, z) on its triangles as the export builds them (TerrainMeshBuilder.SurfaceHeight). */
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
