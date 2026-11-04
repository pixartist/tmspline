import { BLOCK_CELL, BLOCK_LEVEL, MAP_HALF_EXTENT, WORLD_HALF_EXTENT, type VanillaBlock, type VanillaItem } from "./track";
import { blockMatrix, blockSize, GROUND_ADAPTER_FLAG, type VanillaLibrary } from "./vanilla";
import * as THREE from "three";
import {
  clearOfRoads, freeBlock, plantForest, spread, TOP_LEVEL, type SceneryInput, type SceneryLandOptions, type SceneryTheme,
} from "./scenery";

/**
 * Terrain: land round the tracks in two steps.
 *
 * 1. A height map on the grid's corners (corner i, j at x = 32 i, z = 32 j; heights in levels, a level's top is 2 m above
 *    its floor). Corners within 16 m of a road may reach just below it (they are capped by the road's lowest point
 *    there); the others take the height of the nearest capped corner, rising away from it like the hills.
 * 2. Pieces from the corners: each cell's four corners, less their lowest, must match a piece shape (flat, 1- and
 *    2-level slopes, corners, saddles, transitions). Near the roads the cells share their corners, so the pieces fit
 *    without steps: where no shape matches, the highest corner comes down a level. Further out a cell that matches no
 *    shape keeps its own corners instead (lowered alone): a step to its neighbours, closed by columns.
 *
 * The game has no piece that slopes two ways at once, so beside a climbing road the land cannot also climb away from it:
 * there it follows the road, and the hills beyond it step.
 *
 * Pieces come in two families whose 2-level edges differ (measured on the meshes, levels along the edge): the smooth
 * DecoPlatform slopes (0.01 0.29 1.00 1.71 1.99) and the terraced DecoHill ones (0.29 0.69 1.00 1.69 2.00). Only the
 * smooth family has 1-level pieces and the 1-to-2 transitions, so the hill look uses hill pieces only for runs of 2-level
 * slopes whose every sloped edge meets another hill piece, or is open (a step or the band's edge: the Deadend pieces
 * close those); everything else is smooth.
 */

export interface TerrainOptions {
  /** Look of the 2-level slopes: "smooth" (DecoPlatform) or "hill" (DecoHill, terraced) where its pieces fit. */
  look: "smooth" | "hill";
  /** Metres between a road's lowest point and the land below it. */
  clearance: number;
  /**
   * Cells away from the roads that fit no piece: "steps" keep their own corners (a step to their neighbours, closed by
   * columns), "lower" bring the highest corner down (no steps; beside climbing roads the hills flatten).
   */
  far?: "steps" | "lower";
}

export interface TerrainDebug {
  /** Step 1: the wanted heights per corner ("i,j" → level). */
  target: Map<string, number>;
  /** Step 2: the shared corners' heights. */
  final: Map<string, number>;
  /** Corners held down by a road or a block. */
  capped: Set<string>;
  /** Cells that keep their own corners (steps), "x,z" → their four corners (order as CORNERS). */
  stepped: Map<string, number[]>;
}

const key = (x: number, z: number) => `${x},${z}`;
const parse = (k: string) => k.split(",").map(Number) as [number, number];
const HALF = Math.round(MAP_HALF_EXTENT / BLOCK_CELL);
const inMap = (x: number, z: number) => x >= -HALF && x < HALF && z >= -HALF && z < HALF;
const WORLD = Math.floor(WORLD_HALF_EXTENT / BLOCK_CELL);
const inWorld = (x: number, z: number) => x >= -WORLD && x <= WORLD && z >= -WORLD && z <= WORLD;
/** A cell's corners, in the order the shapes use: (x, z), (x+1, z), (x, z+1), (x+1, z+1). */
const CORNERS: [number, number][] = [[0, 0], [1, 0], [0, 1], [1, 1]];
/**
 * A cell's edges by their corners (indices into CORNERS), the neighbour across each and that neighbour's edge facing it:
 * S (z side), N (z+1), W (x side), E (x+1).
 */
const EDGES = {
  S: { corners: [0, 1], step: [0, -1], facing: "N" },
  N: { corners: [2, 3], step: [0, 1], facing: "S" },
  W: { corners: [0, 2], step: [-1, 0], facing: "E" },
  E: { corners: [1, 3], step: [1, 0], facing: "W" },
} as const;
type Edge = keyof typeof EDGES;
const EDGE_NAMES = Object.keys(EDGES) as Edge[];

/** The column piece under land in the air (the same for grass, dirt and ice). */
const COLUMN = "DecoWallBasePillar";

interface Shape {
  name: string;
  /** Corner heights as modelled (North; order as CORNERS), in levels above the piece's floor. */
  corners: number[];
  family: "smooth" | "hill";
  /** Hill Deadends: the modelled edges that end the slope (a plain side, for a step or the band's edge). */
  open?: Edge[];
  /** The game's sub-variant to place (the hill's outer corner: 1, the one that meets its straights; per the user). */
  subVariant?: number;
}

/** The pieces by their corner heights, measured on the meshes. Earlier entries win where two match. */
function pieceShapes(theme: SceneryTheme): Shape[] {
  const s = theme.land, p = `DecoPlatform${s}`, h = `DecoHill${s}`;
  const smooth = (name: string, corners: number[]): Shape => ({ name, corners, family: "smooth" });
  const hill = (name: string, corners: number[], open?: Edge[], subVariant?: number): Shape => ({ name, corners, family: "hill", open, subVariant });
  return [
    smooth(`${p}Base`, [0, 0, 0, 0]),
    smooth(`${p}SlopeBase`, [0, 0, 1, 1]), smooth(`${p}SlopeBaseCurve1InFull`, [1, 1, 0, 1]),
    smooth(`${p}SlopeBaseCurve1OutFull`, [0, 0, 1, 0]), smooth(`${p}SlopeBase5`, [1, 0, 0, 1]),
    smooth(`${p}Slope2Base`, [0, 0, 2, 2]), smooth(`${p}Slope2BaseCurve1InFull`, [2, 2, 0, 2]),
    smooth(`${p}Slope2BaseCurve1OutFull`, [0, 0, 2, 0]), smooth(`${p}Slope2Base5`, [2, 0, 0, 2]),
    smooth(`${p}SlopeBaseToSlope2BaseLeft`, [0, 0, 2, 1]), smooth(`${p}SlopeBaseToSlope2BaseRight`, [0, 0, 1, 2]),
    // The hill's 2-level edges: the straight's W and E, the corners' N and W.
    hill(`${h}Slope2Straight`, [0, 0, 2, 2]), hill(`${h}Slope2Curve1In`, [2, 2, 0, 2]),
    hill(`${h}Slope2Curve1Out`, [0, 0, 2, 0], undefined, 1),
    hill(`${h}DeadendSlope2StraightLeft`, [0, 0, 2, 2], ["E"]), hill(`${h}DeadendSlope2StraightRight`, [0, 0, 2, 2], ["W"]),
    hill(`${h}DeadendSlope2Curve1OutLeft`, [0, 0, 2, 0], ["N"]), hill(`${h}DeadendSlope2Curve1OutRight`, [0, 0, 2, 0], ["W"]),
  ];
}

/** Every piece terrain mode may place (e.g. to load their meshes for SceneryInput.surface). */
export function terrainPieces(theme: SceneryTheme): string[] {
  return pieceShapes(theme).map((s) => s.name);
}

/** A shape as placed: its direction and the world edges it leaves open. */
interface Placed { shape: Shape; dir: number; open: Edge[] }

/** Corner pattern ("a,b,c,d" in world order, lowest 0) → the shapes that have it, turned. */
function catalogue(lib: VanillaLibrary, theme: SceneryTheme): Map<string, Placed[]> {
  const out = new Map<string, Placed[]>();
  for (const shape of pieceShapes(theme)) {
    if (!lib.has(shape.name)) continue;
    for (let dir = 0; dir < 4; dir++) {
      const b: VanillaBlock = { name: shape.name, x: 0, y: 0, z: 0, dir };
      const m = blockMatrix(b, blockSize(lib, b));
      // Modelled corner i lands on world corner toWorld[i].
      const toWorld = CORNERS.map(([lx, lz]) => {
        const v = new THREE.Vector3(lx * BLOCK_CELL, 0, lz * BLOCK_CELL).applyMatrix4(m);
        const wx = Math.round(v.x / BLOCK_CELL), wz = Math.round(v.z / BLOCK_CELL);
        return CORNERS.findIndex(([cx, cz]) => cx === wx && cz === wz);
      });
      const world = [0, 0, 0, 0];
      shape.corners.forEach((h, i) => (world[toWorld[i]] = h));
      const open = (shape.open ?? []).map((e) => {
        const ends = EDGES[e].corners.map((i) => toWorld[i]).sort();
        return EDGE_NAMES.find((w) => EDGES[w].corners.join() === ends.join())!;
      });
      const k = world.join(",");
      if (!out.has(k)) out.set(k, []);
      // One of each kind (family, open edges) per pattern; the first wins.
      const list = out.get(k)!;
      if (!list.some((p) => p.shape.family === shape.family && p.open.join() === open.sort().join()))
        list.push({ shape, dir, open: open.sort() });
    }
  }
  return out;
}

export function generateTerrain(lib: VanillaLibrary, input: SceneryInput, options: SceneryLandOptions, terrain: TerrainOptions):
  { blocks: VanillaBlock[]; items: VanillaItem[]; debug: TerrainDebug } | null {
  const shapes = catalogue(lib, options.theme);
  if (!shapes.has("0,0,0,0")) return null;
  const fits = (hs: number[]) => { const low = Math.min(...hs); return shapes.has(hs.map((v) => v - low).join(",")); };
  const fine = input.roadsFine ?? new Map<string, number>();
  const empty = { blocks: [], items: [], debug: { target: new Map(), final: new Map(), capped: new Set<string>(), stepped: new Map() } };

  // Step 1: caps from the roads (16 m squares round each corner) and the blocks (the level below them).
  const caps = new Map<string, number>();
  const cap = (k: string, level: number) => caps.set(k, Math.min(caps.get(k) ?? Infinity, Math.max(0, Math.min(TOP_LEVEL, level))));
  for (const [k, y] of fine) {
    const [sx, sz] = parse(k);
    // Square s covers corner floor((s + 1) / 2) (its nearest corner on each axis).
    cap(key(Math.floor((sx + 1) / 2), Math.floor((sz + 1) / 2)), Math.floor((y - 2 - terrain.clearance) / BLOCK_LEVEL));
  }
  for (const k of input.occupied) {
    const [x, y, z] = k.split(",").map(Number);
    for (const [dx, dz] of CORNERS) cap(key(x + dx, z + dz), y - 1);
  }
  if (caps.size === 0) return empty;
  const reach = Math.max(0, options.gap) + Math.max(1, options.band);
  const { dist, base } = spread(caps, inWorld, reach);
  const target = new Map<string, number>();
  const step = Math.max(1, options.hillStep);
  for (const [k, d] of dist) {
    if (caps.has(k)) { target.set(k, caps.get(k)!); continue; }
    let rise = 0;
    if (options.hills && d > options.gap) {
      rise = Math.min(2 * (Math.floor((d - options.gap - 1) / step) + 1), Math.max(0, 2 * Math.floor(options.hillRise / 2)));
      if (options.farRise > 0) rise += Math.round(options.farRise * (d - options.gap - 1));
    }
    target.set(k, Math.min(TOP_LEVEL, base.get(k)! + rise));
  }

  // Step 2: the cells whose four corners are all in the height map. Near ones (a corner at most the shoulder plus one
  // away from a road) share their corners: lowered until they fit. The others keep their own where they do not fit.
  const cells: [number, number][] = [];
  for (const k of target.keys()) {
    const [i, j] = parse(k);
    if (CORNERS.every(([dx, dz]) => target.has(key(i + dx, j + dz)))) cells.push([i, j]);
  }
  const cornerKeys = (x: number, z: number) => CORNERS.map(([dx, dz]) => key(x + dx, z + dz));
  const follow = Math.max(0, options.gap) + 1;
  const near = (x: number, z: number) => cornerKeys(x, z).some((k) => (dist.get(k) ?? Infinity) <= follow);
  const final = new Map(target);
  // (Lifting the lowest corner instead was tried: it kept no more of the hills.)
  const far = terrain.far ?? "lower";
  for (let round = 0; round < 200; round++) {
    let changed = false;
    for (const [x, z] of cells) {
      if (far === "steps" && !near(x, z)) continue;
      const ks = cornerKeys(x, z);
      const hs = ks.map((k) => final.get(k)!);
      if (fits(hs)) continue;
      let top = 0;
      for (let i = 1; i < 4; i++) if (hs[i] > hs[top]) top = i;
      final.set(ks[top], hs[top] - 1);
      changed = true;
    }
    if (!changed) break;
  }
  // Each cell's corners: the shared ones, or its own (a step) where those fit no piece.
  const corners = new Map<string, number[]>();
  const stepped = new Map<string, number[]>();
  for (const [x, z] of cells) {
    const hs = cornerKeys(x, z).map((k) => final.get(k)!);
    while (!fits(hs)) {
      let top = 0;
      for (let i = 1; i < 4; i++) if (hs[i] > hs[top]) top = i;
      hs[top]--;
      stepped.set(key(x, z), hs);
    }
    corners.set(key(x, z), hs);
  }

  // Edges: joined where the neighbour across has the same two corner heights; open otherwise (a step, or no land).
  const edgeHeights = (k: string, e: Edge) => EDGES[e].corners.map((i) => corners.get(k)![i]);
  const across = (k: string, e: Edge) => { const [x, z] = parse(k); return key(x + EDGES[e].step[0], z + EDGES[e].step[1]); };
  const joined = (k: string, e: Edge) => {
    const n = across(k, e);
    if (!corners.has(n)) return false;
    const mine = edgeHeights(k, e), theirs = edgeHeights(n, EDGES[e].facing);
    return mine[0] === theirs[0] && mine[1] === theirs[1];
  };
  const sloped2 = (k: string, e: Edge) => { const [a, b] = edgeHeights(k, e); return Math.abs(a - b) === 2; };
  const pattern = (k: string) => { const hs = corners.get(k)!; const low = Math.min(...hs); return hs.map((v) => v - low).join(","); };

  // Hill look: start with every cell that has a hill piece, drop those that cannot be one until it settles: a hill
  // piece's sloped edges must meet hill pieces, or be open where it has a Deadend for that.
  const choice = new Map<string, Placed>();
  const hill = new Set<string>();
  const hillFor = (k: string): Placed | undefined => {
    const open = EDGE_NAMES.filter((e) => sloped2(k, e) && !joined(k, e)).sort().join();
    return shapes.get(pattern(k))?.find((p) => p.shape.family === "hill" && p.open.join() === open);
  };
  if (terrain.look === "hill") {
    for (const k of corners.keys()) if (shapes.get(pattern(k))?.some((p) => p.shape.family === "hill")) hill.add(k);
    for (let round = 0; round < 100; round++) {
      let changed = false;
      for (const k of [...hill]) {
        const meetsSmooth = EDGE_NAMES.some((e) => sloped2(k, e) && joined(k, e) && !hill.has(across(k, e)));
        if (meetsSmooth || !hillFor(k)) { hill.delete(k); changed = true; }
      }
      if (!changed) break;
    }
  }
  for (const k of corners.keys()) {
    const pick = hill.has(k) ? hillFor(k) : shapes.get(pattern(k))?.find((p) => p.shape.family === "smooth");
    if (pick) choice.set(k, pick);
  }

  // Blocks: each cell's piece on its lowest corner, and columns where a neighbour's edge is lower (a step): from the
  // lowest such neighbour's floor up to this cell's.
  const blocks: VanillaBlock[] = [];
  const flat = new Map<string, number>(), slopes = new Map<string, VanillaBlock>();
  const id = options.theme.id;
  for (const [k, placed] of choice) {
    const [x, z] = parse(k);
    const hs = corners.get(k)!;
    const low = Math.min(...hs);
    let columnsFrom = low;
    for (const e of EDGE_NAMES) {
      const n = across(k, e);
      if (!corners.has(n) || joined(k, e)) continue;
      const theirs = edgeHeights(n, EDGES[e].facing);
      if (Math.min(...theirs) < Math.min(...edgeHeights(k, e))) columnsFrom = Math.min(columnsFrom, Math.min(...corners.get(n)!));
    }
    const hasColumns = columnsFrom < low && lib.has(COLUMN);
    const isFlat = hs.every((v) => v === low);
    // On the grid the ground is there at level 0.
    if (isFlat && low === 0 && inMap(x, z)) { flat.set(k, 0); continue; }
    const b: VanillaBlock = { name: placed.shape.name, x, y: low, z, dir: placed.dir, scenery: id };
    if (placed.shape.subVariant) b.subVariant = placed.shape.subVariant;
    // A 1-level slope on columns shows its "column below" sides with the adapter bit (see nestle).
    if (hasColumns && lib.hasColumnAdapter(b.name)) b.extraFlags = GROUND_ADAPTER_FLAG;
    blocks.push(freeBlock(lib, b));
    if (isFlat) flat.set(k, low);
    else slopes.set(k, blocks[blocks.length - 1]);
    if (hasColumns)
      for (let level = columnsFrom; level < low; level++) blocks.push(freeBlock(lib, { name: COLUMN, x, y: level, z, dir: 0, scenery: id }));
  }
  for (const k of input.occupied) {
    const [x, , z] = k.split(",").map(Number);
    flat.delete(key(x, z));
    slopes.delete(key(x, z));
  }
  const clear = (px: number, pz: number) => clearOfRoads(input.footprint, px, pz, Math.max(0, options.clearance));
  const items = plantForest(flat, options.forest, options.density, id, clear, slopes, input.surface);
  return { blocks, items, debug: { target, final, capped: new Set(caps.keys()), stepped } };
}
