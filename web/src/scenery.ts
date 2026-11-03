import * as THREE from "three";
import { BLOCK_CELL, BLOCK_LEVEL, MAP_HALF_EXTENT, WORLD_HALF_EXTENT, wrapBank, type VanillaBlock, type VanillaItem } from "./track";
import { blockMatrix, blockSize, cellOf, type VanillaLibrary } from "./vanilla";

/**
 * Scenery generator: surrounds the map's tracks with deco blocks. The cliff themes raise everything at least a few cells
 * away from the roads and blocks to a plateau, and line its edge with the game's cliff walls facing the track: straight
 * runs, outer and inner corners, round the outside and round islands inside loops. It knows nothing about jumps (the
 * mapper removes what is in the way).
 *
 * Two modes. "Fill" works on the whole 32 m grid from the ground up, with cliffs as a backdrop. "Nestle" lays a band of
 * land round the tracks at their own height (a ski slope gets its slope, high up), as free blocks without columns, also
 * outside the grid. The cliff pieces are read from the block library: in each piece the tallest columns are the wall, the
 * others the space its face leans into (towards the track).
 */

/**
 * A scenery theme: its cliff pieces (prefix), the land's surface (the infix of the hill and platform pieces: "" grass,
 * "Dirt", "Ice") and the forest that suits it (FORESTS id).
 */
export interface SceneryTheme { id: string; label: string; prefix: string; land: "" | "Dirt" | "Ice"; forest: string }

export const SCENERY_THEMES: SceneryTheme[] = [
  { id: "cliff8", label: "Grass, cliffs 64 m", prefix: "DecoCliff8NoHill", land: "", forest: "spring" },
  { id: "cliff8dirt", label: "Dirt, cliffs 64 m", prefix: "DecoCliff8DirtNoHill", land: "Dirt", forest: "cacti" },
  { id: "cliff8ice", label: "Ice, cliffs 64 m", prefix: "DecoCliff8IceNoHill", land: "Ice", forest: "firsnow" },
  { id: "cliff10", label: "Grass, cliffs 80 m", prefix: "DecoCliff10", land: "", forest: "spring" },
  { id: "cliff10dirt", label: "Dirt, cliffs 80 m", prefix: "DecoCliff10Dirt", land: "Dirt", forest: "cacti" },
  { id: "cliff10ice", label: "Ice, cliffs 80 m", prefix: "DecoCliff10Ice", land: "Ice", forest: "firsnow" },
];

export interface SceneryInput {
  /** Lowest point of the roads per grid column ("x,z" → metres above the ground). */
  roads: Map<string, number>;
  /** Where the roads are, at any height: FOOTPRINT_CELL squares ("x,z") the road meshes cover (see roadFootprint). */
  footprint: Set<string>;
  /** Grid cells taken by blocks ("x,y,z", levels), the scenery's own excluded. */
  occupied: Set<string>;
}

export interface SceneryOptions {
  theme: SceneryTheme;
  /** Free cells between the track and the cliffs' faces. */
  gap: number;
}

type Cell = [number, number];
const key = (x: number, z: number) => `${x},${z}`;
const ORTHO: Cell[] = [[0, 1], [1, 0], [0, -1], [-1, 0]];

/** A cliff piece as modelled (North): its wall cells and the sides its face looks to. */
interface Piece { name: string; wall: Cell[]; size: [number, number, number]; height: number }

function piece(lib: VanillaLibrary, name: string): Piece | null {
  const v = lib.variant(name, true);
  if (!v?.units?.length) return null;
  const heights = new Map<string, number>();
  for (const [x, , z] of v.units) heights.set(key(x, z), (heights.get(key(x, z)) ?? 0) + 1);
  const top = Math.max(...heights.values());
  const wall = [...heights].filter(([, h]) => h === top).map(([k]) => k.split(",").map(Number) as Cell);
  return { name, wall, size: v.size, height: top };
}

/** A direction (x, z) turned like a block with direction `dir` (see blockMatrix). */
function turn(dir: number, [x, z]: Cell): Cell {
  const v = new THREE.Vector3(x, 0, z).applyMatrix4(new THREE.Matrix4().makeRotationY((-dir * Math.PI) / 2));
  return [Math.round(v.x), Math.round(v.z)];
}

/** The block that puts the piece's local cell `anchor` on world cell `at`, turned by `dir`. */
function place(lib: VanillaLibrary, p: Piece, dir: number, anchor: Cell, at: Cell, theme: string): VanillaBlock {
  const b: VanillaBlock = { name: p.name, x: 0, y: 0, z: 0, dir, scenery: theme };
  const [ax, az] = cellOf(blockMatrix(b, blockSize(lib, b)), (anchor[0] + 0.5) * BLOCK_CELL, (anchor[1] + 0.5) * BLOCK_CELL);
  b.x = at[0] - ax;
  b.z = at[1] - az;
  return b;
}

const HALF = Math.round(MAP_HALF_EXTENT / BLOCK_CELL);
const inMap = (x: number, z: number) => x >= -HALF && x < HALF && z >= -HALF && z < HALF;
/** Cells where free blocks may stand (WORLD_HALF_EXTENT: 2 km round the grid). */
const WORLD = Math.floor(WORLD_HALF_EXTENT / BLOCK_CELL);
const inWorld = (x: number, z: number) => x >= -WORLD && x < WORLD && z >= -WORLD && z < WORLD;
const parse = (k: string) => k.split(",").map(Number) as Cell;

/**
 * Distance in cells (8 neighbours) from the source cells over the map (or the cells `within` allows, up to `reach`), and
 * the value of the nearest source (the lowest where several are equally near).
 */
function spread(sources: Map<string, number>, within: (x: number, z: number) => boolean = inMap, reach = Infinity):
  { dist: Map<string, number>; base: Map<string, number> } {
  const dist = new Map<string, number>(), base = new Map<string, number>();
  let frontier: Cell[] = [];
  for (const [k, v] of sources) {
    dist.set(k, 0);
    base.set(k, v);
    frontier.push(parse(k));
  }
  for (let d = 1; frontier.length && d <= reach; d++) {
    const next: Cell[] = [];
    for (const [x, z] of frontier)
      for (let dx = -1; dx <= 1; dx++)
        for (let dz = -1; dz <= 1; dz++) {
          const [nx, nz] = [x + dx, z + dz];
          const k = key(nx, nz);
          if (!within(nx, nz)) continue;
          const v = base.get(key(x, z))!;
          if (!dist.has(k)) {
            dist.set(k, d);
            base.set(k, v);
            next.push([nx, nz]);
          } else if (dist.get(k) === d && v < base.get(k)!) base.set(k, v);
        }
    frontier = next;
  }
  return { dist, base };
}

export interface SceneryLandOptions extends SceneryOptions {
  /**
   * "fill": the whole grid from the ground up (cliffs as a backdrop). "nestle": a band of land `band` cells wide beyond the
   * shoulder, at the tracks' own height, as free blocks without columns (floating), on the grid and outside it.
   */
  mode: "fill" | "nestle";
  /** Nestle: cells of land beyond the shoulder. */
  band: number;
  /** Hills: platform-base land and DecoHill slopes, up to just below the roads and rising away from them. */
  hills: boolean;
  /** Cells between a rise of the hills (2 levels) and the next. */
  hillStep: number;
  /** Highest the hills rise above the track's land, in levels (even). */
  hillRise: number;
  /** Cliff walls this many cells away from the track (0: none). */
  cliffs: number;
  /** Trees on the flat land (a FORESTS id, or "none"). */
  forest: string;
  /** Share of the flat cells that get trees, 0..1 (busier cells get more of them). */
  density: number;
  /** Trees keep at least this many metres from the roads (their curbs, gates ...), at any height. */
  clearance: number;
}

/** Size of the squares of the road footprint (SceneryInput.footprint), metres. */
export const FOOTPRINT_CELL = 2;

/** Whether a spot is at least `clearance` metres from every footprint square (measured to the square's nearest point). */
function clearOfRoads(footprint: Set<string>, px: number, pz: number, clearance: number): boolean {
  const reach = Math.ceil(clearance / FOOTPRINT_CELL) + 1;
  const [cx, cz] = [Math.floor(px / FOOTPRINT_CELL), Math.floor(pz / FOOTPRINT_CELL)];
  for (let dx = -reach; dx <= reach; dx++)
    for (let dz = -reach; dz <= reach; dz++) {
      if (!footprint.has(key(cx + dx, cz + dz))) continue;
      const nx = Math.max((cx + dx) * FOOTPRINT_CELL, Math.min(px, (cx + dx + 1) * FOOTPRINT_CELL));
      const nz = Math.max((cz + dz) * FOOTPRINT_CELL, Math.min(pz, (cz + dz + 1) * FOOTPRINT_CELL));
      if (Math.hypot(px - nx, pz - nz) < clearance) return false;
    }
  return true;
}

/**
 * Forests: the game's tree items by kind, smallest first. The SpringTree names are confirmed from maps saved by the
 * game; the others follow the game's files (VegetTreeModel) and still need a check in game.
 */
export const FORESTS: { id: string; label: string; trees: string[] }[] = [
  { id: "spring", label: "Spring trees", trees: ["SpringTreeVerySmall", "SpringTreeSmall", "SpringTreeMedium", "SpringTreeBig", "SpringTreeTall"] },
  { id: "fall", label: "Fall trees", trees: ["FallTreeVerySmall", "FallTreeSmall", "FallTreeMedium", "FallTreeBig", "FallTreeTall"] },
  { id: "fir", label: "Firs", trees: ["FirMedium", "FirTall"] },
  { id: "firsnow", label: "Snowy firs", trees: ["FirSnowMedium", "FirSnowTall"] },
  { id: "palm", label: "Palms", trees: ["PalmTreeSmall", "PalmTreeMedium"] },
  { id: "frozen", label: "Frozen trees", trees: ["FrozenTreeVerySmall", "FrozenTreeSmall", "FrozenTreeMedium", "FrozenTreeBig", "FrozenTreeTall"] },
  // Names from the game's files (Static\Vegetation\Cactus..E); not yet checked in game.
  { id: "cacti", label: "Cacti", trees: ["Cactus", "CactusB", "CactusC", "CactusD", "CactusE"] },
  { id: "mixed", label: "Mixed (spring, fall, firs)", trees: ["SpringTreeSmall", "SpringTreeMedium", "SpringTreeTall", "FallTreeMedium", "FallTreeBig", "FirMedium", "FirTall"] },
];

/** Small deterministic random numbers (mulberry32), seeded per cell so regenerating gives the same forest. */
function random(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Trees on flat cells (level → its surface): a share `density` of them get 1..4 trees at random spots, kept 7 m apart,
 * 3 m from the cell's edges and `clear` of the roads, random sizes and turns.
 */
function plantForest(surfaces: Map<string, number>, forest: string, density: number, theme: string,
  clear: (x: number, z: number) => boolean): VanillaItem[] {
  const kind = FORESTS.find((f) => f.id === forest);
  if (!kind || density <= 0) return [];
  const items: VanillaItem[] = [];
  for (const [k, level] of surfaces) {
    const [x, z] = parse(k);
    const rnd = random((x * 73856093) ^ (z * 19349663) ^ 0x5eed);
    if (rnd() > density) continue;
    const count = 1 + Math.floor(rnd() * 4 * Math.min(1, density + 0.25));
    const spots: [number, number][] = [];
    for (let tries = 0; spots.length < count && tries < 20; tries++) {
      const px = x * BLOCK_CELL + 3 + rnd() * (BLOCK_CELL - 6), pz = z * BLOCK_CELL + 3 + rnd() * (BLOCK_CELL - 6);
      if (spots.some(([sx, sz]) => Math.hypot(sx - px, sz - pz) < 7) || !clear(px, pz)) continue;
      spots.push([px, pz]);
    }
    // On the ground (level 0) the item stands on the terrain; on land on the platform's top, 2 m above its level.
    const y = level > 0 ? level * BLOCK_LEVEL + 2 : 0;
    for (const [px, pz] of spots)
      items.push({ name: kind.trees[Math.floor(rnd() * kind.trees.length)], position: [px, y, pz], yaw: Math.round(rnd() * 360), scenery: theme });
  }
  return items;
}

/**
 * Generates the scenery blocks: land under and round the tracks with hills rising away from them, and cliff walls further
 * out as a backdrop. Null when the library lacks a piece the options need.
 */
export function generateScenery(lib: VanillaLibrary, input: SceneryInput, options: SceneryLandOptions):
  { blocks: VanillaBlock[]; items: VanillaItem[] } | null {
  // The track: roads (land up to just below their underside: the platform's top is 2 m above its level) and blocks (land
  // up to the level below them), in levels.
  const track = new Map<string, number>();
  const lower = (k: string, h: number) => track.set(k, Math.min(track.get(k) ?? Infinity, Math.max(0, h)));
  for (const [k, y] of input.roads) lower(k, Math.floor((y - 3) / BLOCK_LEVEL));
  for (const k of input.occupied) {
    const [x, y, z] = k.split(",").map(Number);
    lower(key(x, z), y - 1);
  }
  if (track.size === 0) return { blocks: [], items: [] };
  const nestle = options.mode === "nestle";
  // Nestle: only the band round the tracks, wherever they are (also outside the grid).
  const { dist, base } = nestle ? spread(track, inWorld, Math.max(0, options.gap) + Math.max(1, options.band)) : spread(track);

  const blocks: VanillaBlock[] = [];
  let cliff: Cliffs | null = null;
  if (options.cliffs > 0 && !nestle) {
    const walls = cliffWalls(lib, options.theme, dist, options.cliffs);
    if (!walls) return null;
    blocks.push(...walls.blocks);
    // Vertical faces (the 64 m cliffs: a few metres past their wall cell at most) take land right up to them; faces that
    // lean out over the next row (80 m: 64 m deep) keep it free.
    const small = lib.variant(`${options.theme.prefix}StraightSmall`, true)?.mesh?.file;
    const depth = small ? lib.meshBounds(small)?.[1][2] ?? Infinity : Infinity;
    // The land meets the face where it is still vertical: 3 levels below the top (64 m: level 5, per the user).
    cliff = { high: walls.high, front: walls.front, top: walls.top - CLIFF_LAND_BELOW_TOP, leaning: depth > BLOCK_CELL * 1.25 };
  }
  // Where trees may stand: flat land (or the ground) away from the cliffs, not on cells with blocks, and each tree far
  // enough from the roads (checked per tree, so they also grow right beside the road and on the land under it).
  let surfaces = new Map<string, number>();
  if (options.hills || nestle) {
    const cells = nestle ? [...dist.keys()] : gridCells();
    const land = hills(lib, track, dist, base, cliff, options, cells);
    if (!land) return null;
    // Nestle floats: every piece becomes the same block placed freely (free blocks get no columns, and may stand outside
    // the grid).
    blocks.push(...(nestle ? [...land.blocks, ...columns(lib, land.height, options.theme.id)].map((b) => freeBlock(lib, b)) : land.blocks));
    surfaces = land.flat;
  } else
    for (let x = -HALF; x < HALF; x++)
      for (let z = -HALF; z < HALF; z++)
        if (!cliff?.high.has(key(x, z)) && !cliff?.front.has(key(x, z))) surfaces.set(key(x, z), 0);
  for (const k of input.occupied) {
    const [x, , z] = k.split(",").map(Number);
    surfaces.delete(key(x, z));
  }
  const clear = (x: number, z: number) => clearOfRoads(input.footprint, x, z, Math.max(0, options.clearance));
  return { blocks, items: plantForest(surfaces, options.forest, options.density, options.theme.id, clear) };
}

function gridCells(): string[] {
  const cells: string[] = [];
  for (let x = -HALF; x < HALF; x++) for (let z = -HALF; z < HALF; z++) cells.push(key(x, z));
  return cells;
}

/** The column piece the game puts under land tiles in the air (the same for grass, dirt and ice). */
const COLUMN = "DecoWallBasePillar";

/**
 * Nestle's free blocks get no columns from the game, so where a cell is higher than a neighbour the step would stay open.
 * Each cell gets column pieces from the level of its lowest neighbour (all eight) up to just below its own tile: the
 * piece under level v reaches v's floor, the tile's plate sits 2 m above it, and the lowest piece starts below the
 * neighbour's plate (checked in game: stacked free DecoWallBasePillar close such a step).
 */
function columns(lib: VanillaLibrary, height: Map<string, number>, theme: string): VanillaBlock[] {
  if (!lib.has(COLUMN)) return [];
  const out: VanillaBlock[] = [];
  for (const [k, v] of height) {
    const [x, z] = parse(k);
    let low = v;
    for (let dx = -1; dx <= 1; dx++)
      for (let dz = -1; dz <= 1; dz++) {
        const n = height.get(key(x + dx, z + dz));
        if (n !== undefined && n < low) low = n;
      }
    for (let level = low; level < v; level++) out.push({ name: COLUMN, x, y: level, z, dir: 0, scenery: theme });
  }
  return out;
}

/** The same block placed freely, exactly where it stands on the grid (as the editor's Tab converts it). */
function freeBlock(lib: VanillaLibrary, b: VanillaBlock): VanillaBlock {
  const origin = new THREE.Vector3().setFromMatrixPosition(blockMatrix(b, blockSize(lib, b)));
  return { ...b, free: true, position: [origin.x, origin.y, origin.z], yaw: wrapBank(-b.dir * 90), pitch: 0, roll: 0 };
}

/** Levels between a cliff's top and the land against it: above that the face is no longer vertical. */
const CLIFF_LAND_BELOW_TOP = 3;

/** The cliff walls as the land sees them: behind them, the cells in front of their faces, the top level, leaning faces. */
interface Cliffs { high: Set<string>; front: Set<string>; top: number; leaning: boolean }

/**
 * Land and hills: every cell gets a height in levels. Under the track the highest that stays below it; beside it, for the
 * gap, the same. With cliffs the land climbs to their top: in front of the faces it is level with it, and from there it
 * steps down by 2 every `hillStep` cells towards the track. Without cliffs it rises away from the track by 2 every
 * `hillStep` cells, up to `hillRise` above it. Neighbours differ by one slope at most (lower cells are pulled down, not
 * walls built up). Flat cells get a DecoPlatformBase (its columns come with it), cells below a higher neighbour a slope,
 * inner and outer corners the curved ones.
 */
function hills(lib: VanillaLibrary, track: Map<string, number>, dist: Map<string, number>, base: Map<string, number>,
  cliff: Cliffs | null, options: SceneryLandOptions, cells: Iterable<string>):
  { blocks: VanillaBlock[]; flat: Map<string, number>; height: Map<string, number> } | null {
  const flatCells = new Map<string, number>();
  const cliffHigh = cliff?.high ?? new Set<string>(), cliffFront = cliff?.front ?? new Set<string>();
  const toCliff = cliff ? spread(new Map([...cliff.high].map((k) => [k, 0]))).dist : null;
  // Slopes by the height they climb: DecoHill for 2 levels, the deco platform's own for 1 (as modelled: straight up to
  // +Z, the inner corner high but at (-X, +Z), the outer corner high only there).
  // The theme's surface: grass, dirt or ice versions of the same pieces (DecoHillDirt..., DecoPlatformDirt...).
  const s = options.theme.land;
  const flat = `DecoPlatform${s}Base`;
  const slopes: Record<number, { straight: string; inner: string; outer: string }> = {
    2: { straight: `DecoHill${s}Slope2Straight`, inner: `DecoHill${s}Slope2Curve1In`, outer: `DecoHill${s}Slope2Curve1Out` },
    1: { straight: `DecoPlatform${s}SlopeBase`, inner: `DecoPlatform${s}SlopeBaseCurve1InFull`, outer: `DecoPlatform${s}SlopeBaseCurve1OutFull` },
  };
  if ([flat, ...Object.values(slopes).flatMap((s) => Object.values(s))].some((n) => !lib.has(n))) return null;
  const id = options.theme.id;

  const height = new Map<string, number>();
  for (const k of cells) {
      if (cliffHigh.has(k)) continue;
      if (track.has(k)) { height.set(k, track.get(k)!); continue; }
      if (cliff && cliffFront.has(k)) { height.set(k, cliff.top); continue; }
      const d = dist.get(k) ?? Infinity, b = base.get(k) ?? 0;
      const step = Math.max(1, options.hillStep);
      // The shoulder: `gap` cells beside the track at its height, the outermost already sloping up (the land starts
      // right next to the track with the default of 1).
      if (d <= options.gap) height.set(k, b);
      else {
        // Rising away from the track by 2 every `step` cells...
        const rise = 2 * (Math.floor((d - options.gap - 1) / step) + 1);
        if (cliff && toCliff) {
          // ...and up to the cliffs: the cells in front of the faces are 1 away, the top terrace `step` cells deep, then
          // 2 lower every `step`. Whichever is higher, never above the cliff's land (so loops far from the cliffs get
          // hills too).
          const c = toCliff.get(k) ?? Infinity;
          height.set(k, Math.min(cliff.top, Math.max(b + rise, cliff.top - 2 * Math.floor((c - 1) / step))));
        } else height.set(k, b + Math.min(rise, even2(options.hillRise)));
      }
    }
  const h = (x: number, z: number) => height.get(key(x, z));
  const fixed = (k: string) => track.has(k) || cliffFront.has(k);

  // Neighbours (all eight) at most one slope apart: pull cells down to their lowest neighbour + 2. Cells that a slope
  // cannot shape (higher on opposite sides, on three sides, or only on two opposite corners) are raised instead.
  for (let round = 0; round < 50; round++) {
    let changed = false;
    for (const [k, v] of height) {
      if (fixed(k)) continue;
      const [x, z] = parse(k);
      let low = Infinity;
      for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) if (dx || dz) low = Math.min(low, h(x + dx, z + dz) ?? Infinity);
      if (v > low + 2) { height.set(k, low + 2); changed = true; }
    }
    for (const [k, v] of height) {
      if (fixed(k)) continue;
      const [x, z] = parse(k);
      const up = ORTHO.map(([dx, dz]) => (h(x + dx, z + dz) ?? -1) > v);
      const ups = up.filter(Boolean).length;
      const diag = [[1, 1], [-1, -1], [1, -1], [-1, 1]].map(([dx, dz]) => (h(x + dx, z + dz) ?? -1) > v);
      const saddle = ups === 0 && ((diag[0] && diag[1]) || (diag[2] && diag[3]));
      if (ups >= 3 || (up[0] && up[2]) || (up[1] && up[3]) || saddle) { height.set(k, v + 2); changed = true; continue; }
      // A corner slope climbs one height: two sides that rise by different amounts raise the cell to the lower one.
      const rises = ORTHO.map(([dx, dz]) => (h(x + dx, z + dz) ?? -1) - v).filter((d) => d > 0);
      if (rises.length === 2 && rises[0] !== rises[1]) { height.set(k, v + Math.min(...rises)); changed = true; }
    }
    if (!changed) break;
  }

  const blocks: VanillaBlock[] = [];
  const dirFor = (want: (dir: number) => boolean) => [0, 1, 2, 3].find(want) ?? 0;
  const same = (a: Cell, b: Cell) => a[0] === b[0] && a[1] === b[1];
  const at = (name: string, x: number, z: number, level: number, dir: number): VanillaBlock => ({ name, x, y: level, z, dir, scenery: id });
  // Ridges in narrow pockets (inside small loops of the track): two flat cells side by side with the track on both outer
  // sides get slopes climbing towards each other, meeting 2 levels up, instead of staying bare.
  const flatCell = (x: number, z: number) => {
    const k = key(x, z), v = height.get(k);
    if (v === undefined || track.has(k) || cliffFront.has(k)) return false;
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) if ((h(x + dx, z + dz) ?? -1) > v) return false;
    return true;
  };
  const ridge = new Map<string, Cell>();
  for (const [k, v] of height) {
    const [x, z] = parse(k);
    if (ridge.has(k) || !flatCell(x, z)) continue;
    for (const [ux, uz] of ORTHO) {
      const n = key(x + ux, z + uz);
      const sameLevel = height.get(n) === v && flatCell(x + ux, z + uz) && !ridge.has(n);
      // Road behind both: the slopes start at its edge, so no wall faces it.
      if (sameLevel && track.has(key(x - ux, z - uz)) && track.has(key(x + 2 * ux, z + 2 * uz))) {
        ridge.set(k, [ux, uz]);
        ridge.set(n, [-ux, -uz]);
        break;
      }
    }
  }

  for (const [k, v] of height) {
    // Under a leaning cliff face: nothing (its height still shapes the land round it).
    if (cliff?.leaning && cliffFront.has(k)) continue;
    const [x, z] = parse(k);
    const towards = ridge.get(k);
    if (towards) {
      blocks.push(at(slopes[2].straight, x, z, v, dirFor((d) => same(turn(d, [0, 1]), towards))));
      continue;
    }
    // Slopes rise to higher neighbours, 1 or 2 levels (track cells stay flat under the road).
    const up = ORTHO.filter(([dx, dz]) => (h(x + dx, z + dz) ?? -1) > v);
    const rise = up.length ? h(x + up[0][0], z + up[0][1])! - v : 0;
    const set = !track.has(k) && up.every(([dx, dz]) => h(x + dx, z + dz)! - v === rise) ? slopes[rise] : undefined;
    if (set && up.length === 1) {
      blocks.push(at(set.straight, x, z, v, dirFor((d) => same(turn(d, [0, 1]), up[0]))));
    } else if (set && up.length === 2 && !same(up[0], [-up[1][0], -up[1][1]])) {
      // The inner corner is modelled high on its -Z and +X sides.
      blocks.push(at(set.inner, x, z, v, dirFor((d) => up.some((u) => same(u, turn(d, [0, -1]))) && up.some((u) => same(u, turn(d, [1, 0]))))));
    } else {
      const corner = !track.has(k) && up.length === 0
        ? ([[1, 1], [-1, -1], [1, -1], [-1, 1]] as Cell[]).map(([dx, dz]) => ({ c: [dx, dz] as Cell, d: (h(x + dx, z + dz) ?? -1) - v }))
          .find((o) => o.d === 1 || o.d === 2) : undefined;
      // The outer corner is modelled high at its (-X, +Z) corner.
      if (corner) blocks.push(at(slopes[corner.d].outer, x, z, v, dirFor((d) => same(turn(d, [-1, 1]), corner.c))));
      else {
        // On the grid the ground is there at level 0; outside it (nestle) the land needs its own floor.
        if (v > 0 || !inMap(x, z)) blocks.push(at(flat, x, z, v, 0));
        // Flat land or ground (also under the road, beside it: each tree keeps its distance from the road).
        flatCells.set(k, v);
      }
    }
  }
  return { blocks, flat: flatCells, height };
}

const even2 = (n: number) => Math.max(0, 2 * Math.floor(n / 2));

/** Cliff walls `gap` cells (plus their face) away from the track; null when the theme's pieces are missing. */
function cliffWalls(lib: VanillaLibrary, theme: SceneryTheme, dist: Map<string, number>, gap: number):
  { blocks: VanillaBlock[]; high: Set<string>; front: Set<string>; top: number } | null {
  const { prefix, id } = theme;
  const straight = piece(lib, `${prefix}Straight`), small = piece(lib, `${prefix}StraightSmall`);
  const outer = piece(lib, `${prefix}CornerOut`), inner = piece(lib, `${prefix}CornerInSmall`);
  if (!straight || !small || !outer || !inner) return null;
  const half = HALF;
  const reach = gap + 2;
  const high = new Set<string>();
  for (let x = -half; x < half; x++)
    for (let z = -half; z < half; z++)
      if ((dist.get(key(x, z)) ?? Infinity) >= reach) high.add(key(x, z));
  const isHigh = (x: number, z: number) => !inMap(x, z) || high.has(key(x, z));
  const isLow = (x: number, z: number) => !isHigh(x, z);

  // Walls need a clear face: no strips one cell wide, no tips, no cells joined only at a corner.
  for (let changed = true; changed; ) {
    changed = false;
    for (const k of [...high]) {
      const [x, z] = k.split(",").map(Number);
      const low = ORTHO.map(([dx, dz]) => isLow(x + dx, z + dz));
      const lowCount = low.filter(Boolean).length;
      const strip = (low[0] && low[2]) || (low[1] && low[3]);
      const pinch = (isLow(x + 1, z + 1) && isLow(x - 1, z - 1)) || (isLow(x + 1, z - 1) && isLow(x - 1, z + 1));
      // An outer corner's face leans into the diagonal between its two open sides: that cell must be open too.
      const blockedCorner = ORTHO.some(([ax, az], i) => {
        const [bx, bz] = ORTHO[(i + 1) % 4];
        return low[i] && low[(i + 1) % 4] && isHigh(x + ax + bx, z + az + bz);
      });
      if (lowCount >= 3 || strip || pinch || blockedCorner) {
        high.delete(k);
        changed = true;
      }
    }
  }

  const blocks: VanillaBlock[] = [];
  const used = new Set<string>();
  // Pieces are matched by turning their modelled faces (+Z, and +X for corners) onto the world sides.
  const dirFor = (want: (dir: number) => boolean) => [0, 1, 2, 3].find(want) ?? 0;
  const same = (a: Cell, b: Cell) => a[0] === b[0] && a[1] === b[1];

  // Corners first: outer corners (two low sides) and inner corners (low only diagonally), which also take the two walls
  // next to them.
  const straightCells: { cell: Cell; face: Cell }[] = [];
  for (const k of high) {
    const [x, z] = k.split(",").map(Number) as Cell;
    const lowSides = ORTHO.filter(([dx, dz]) => isLow(x + dx, z + dz));
    if (lowSides.length === 2 && !used.has(k)) {
      const dir = dirFor((d) => lowSides.some((s) => same(s, turn(d, [0, 1]))) && lowSides.some((s) => same(s, turn(d, [1, 0]))));
      blocks.push(place(lib, outer, dir, outer.wall[0], [x, z], id));
      used.add(k);
    } else if (lowSides.length === 0) {
      for (const [dx, dz] of [[1, 1], [1, -1], [-1, 1], [-1, -1]] as Cell[]) {
        if (!isLow(x + dx, z + dz)) continue;
        const sides = [key(x + dx, z), key(x, z + dz)];
        if (used.has(k) || sides.some((s) => used.has(s) || !high.has(s))) continue;
        const dir = dirFor((d) => same(turn(d, [1, 1]), [dx, dz]));
        blocks.push(place(lib, inner, dir, [0, 0], [x, z], id));
        used.add(k);
        sides.forEach((s) => used.add(s));
      }
    }
  }
  for (const k of high) {
    if (used.has(k)) continue;
    const [x, z] = k.split(",").map(Number) as Cell;
    const lowSides = ORTHO.filter(([dx, dz]) => isLow(x + dx, z + dz));
    if (lowSides.length === 1) straightCells.push({ cell: [x, z], face: lowSides[0] });
  }

  // Straight walls: runs along each face direction, the long piece where four cells fit, the small one elsewhere.
  for (const face of ORTHO) {
    const dir = dirFor((d) => same(turn(d, [0, 1]), face));
    const along = turn(dir, [1, 0]); // the long piece's length, as placed
    const cells = new Set(straightCells.filter((s) => same(s.face, face)).map((s) => key(...s.cell)));
    const sorted = [...cells].map((k) => k.split(",").map(Number) as Cell)
      .sort((a, b) => (a[0] * along[0] + a[1] * along[1]) - (b[0] * along[0] + b[1] * along[1]));
    for (const start of sorted) {
      if (!cells.has(key(...start))) continue;
      // Walk the run from its start along the piece's length.
      let length = 0;
      while (cells.has(key(start[0] + along[0] * length, start[1] + along[1] * length))) length++;
      for (let i = 0; i < length; ) {
        const at: Cell = [start[0] + along[0] * i, start[1] + along[1] * i];
        const long = length - i >= straight.wall.length;
        const p = long ? straight : small;
        // The modelled first wall cell (smallest x) sits at the run's current cell.
        const anchor = p.wall.reduce((a, b) => (b[0] < a[0] ? b : a));
        blocks.push(place(lib, p, dir, anchor, at, id));
        const n = long ? straight.wall.length : 1;
        for (let j = 0; j < n; j++) cells.delete(key(at[0] + along[0] * j, at[1] + along[1] * j));
        i += n;
      }
    }
  }
  // The open cells next to the walls: the faces lean into them (or have room reserved there).
  const front = new Set<string>();
  for (const k of high) {
    const [x, z] = parse(k);
    for (let dx = -1; dx <= 1; dx++)
      for (let dz = -1; dz <= 1; dz++)
        if (inMap(x + dx, z + dz) && isLow(x + dx, z + dz)) front.add(key(x + dx, z + dz));
  }
  // Their top: the walls are a level taller in units than the land level that meets their top edge (64 m: level 8).
  return { blocks, high, front, top: outer.height - 1 };
}

/** Lowest road point per grid column, from road meshes (world space). Triangles are sampled every few metres. */
export function roadColumns(meshes: THREE.Mesh[]): Map<string, number> {
  const columns = new Map<string, number>();
  sampleRoads(meshes, 4, (v) => {
    const k = key(Math.floor(v.x / BLOCK_CELL), Math.floor(v.z / BLOCK_CELL));
    columns.set(k, Math.min(columns.get(k) ?? Infinity, v.y));
  });
  return columns;
}

/** The FOOTPRINT_CELL squares the road meshes cover (world space, any height); see SceneryInput.footprint. */
export function roadFootprint(meshes: THREE.Mesh[]): Set<string> {
  const squares = new Set<string>();
  sampleRoads(meshes, FOOTPRINT_CELL, (v) => squares.add(key(Math.floor(v.x / FOOTPRINT_CELL), Math.floor(v.z / FOOTPRINT_CELL))));
  return squares;
}

/** Calls `mark` with points on every triangle of the meshes (world space), at most `step` metres apart. */
function sampleRoads(meshes: THREE.Mesh[], step: number, mark: (v: THREE.Vector3) => void): void {
  const a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3(), p = new THREE.Vector3();
  for (const mesh of meshes) {
    mesh.updateMatrixWorld();
    const pos = mesh.geometry.getAttribute("position");
    const index = mesh.geometry.getIndex();
    const count = index ? index.count : pos.count;
    for (let i = 0; i + 2 < count; i += 3) {
      const [ia, ib, ic] = [0, 1, 2].map((j) => (index ? index.getX(i + j) : i + j));
      a.fromBufferAttribute(pos, ia).applyMatrix4(mesh.matrixWorld);
      b.fromBufferAttribute(pos, ib).applyMatrix4(mesh.matrixWorld);
      c.fromBufferAttribute(pos, ic).applyMatrix4(mesh.matrixWorld);
      const longest = Math.max(a.distanceTo(b), b.distanceTo(c), c.distanceTo(a));
      const n = Math.min(64, Math.max(1, Math.ceil(longest / step)));
      for (let u = 0; u <= n; u++)
        for (let v = 0; u + v <= n; v++) {
          p.copy(a).multiplyScalar(1 - (u + v) / n).addScaledVector(b, u / n).addScaledVector(c, v / n);
          mark(p);
        }
    }
  }
}
