import { BLOCK_CELL, BLOCK_LEVEL, MAP_HALF_EXTENT, WORLD_HALF_EXTENT, type VanillaBlock, type VanillaItem } from "./track";
import { blockMatrix, blockSize, type VanillaLibrary } from "./vanilla";
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
 * 2. Pieces from the corners: each cell's four corners, less their lowest, must match a piece of the catalogue (flat,
 *    1- and 2-level slopes, corners, saddles, transitions). Where none matches, the cell's highest corner is lowered a
 *    level until one does. Neighbouring cells share their corners, so the pieces always fit together.
 */

export interface TerrainOptions {
  /** Look of the 2-level slopes: "hill" (DecoHill, rounded) or "platform" (DecoPlatform, flat faces). */
  look: "hill" | "platform";
  /** Metres between a road's lowest point and the land below it. */
  clearance: number;
  /** How cells without a matching piece are fixed: "lower" their highest corner, or take the "nearest" piece shape. */
  fix?: "lower" | "nearest";
}

export interface TerrainDebug {
  /** Step 1: the wanted heights per corner ("i,j" → level). */
  target: Map<string, number>;
  /** Step 2: the heights the pieces got. */
  final: Map<string, number>;
  /** Corners held down by a road or a block. */
  capped: Set<string>;
}

const key = (x: number, z: number) => `${x},${z}`;
const parse = (k: string) => k.split(",").map(Number) as [number, number];
const HALF = Math.round(MAP_HALF_EXTENT / BLOCK_CELL);
const inMap = (x: number, z: number) => x >= -HALF && x < HALF && z >= -HALF && z < HALF;
const WORLD = Math.floor(WORLD_HALF_EXTENT / BLOCK_CELL);
const inWorld = (x: number, z: number) => x >= -WORLD && x <= WORLD && z >= -WORLD && z <= WORLD;
/** A cell's corners, in the order the catalogue uses: (x, z), (x+1, z), (x, z+1), (x+1, z+1). */
const CORNERS: [number, number][] = [[0, 0], [1, 0], [0, 1], [1, 1]];

/**
 * The pieces by their corner heights as modelled (North; order as CORNERS), measured on the meshes: the top surface at
 * each corner, in levels above the piece's floor. Earlier entries win where two match.
 */
function pieceShapes(theme: SceneryTheme, look: TerrainOptions["look"]): [string, number[]][] {
  const s = theme.land, p = `DecoPlatform${s}`, h = `DecoHill${s}`;
  const two: [string, number[]][] = look === "hill"
    ? [[`${h}Slope2Straight`, [0, 0, 2, 2]], [`${h}Slope2Curve1In`, [2, 2, 0, 2]], [`${h}Slope2Curve1Out`, [0, 0, 2, 0]]]
    : [[`${p}Slope2Base`, [0, 0, 2, 2]], [`${p}Slope2BaseCurve1InFull`, [2, 2, 0, 2]], [`${p}Slope2BaseCurve1OutFull`, [0, 0, 2, 0]]];
  return [
    [`${p}Base`, [0, 0, 0, 0]],
    [`${p}SlopeBase`, [0, 0, 1, 1]], [`${p}SlopeBaseCurve1InFull`, [1, 1, 0, 1]], [`${p}SlopeBaseCurve1OutFull`, [0, 0, 1, 0]],
    [`${p}SlopeBase5`, [1, 0, 0, 1]],
    ...two,
    // Both looks: the platform's 2-level pieces where the hill has none.
    [`${p}Slope2Base`, [0, 0, 2, 2]], [`${p}Slope2BaseCurve1InFull`, [2, 2, 0, 2]], [`${p}Slope2BaseCurve1OutFull`, [0, 0, 2, 0]],
    [`${p}Slope2Base5`, [2, 0, 0, 2]],
    [`${p}SlopeBaseToSlope2BaseLeft`, [0, 0, 2, 1]], [`${p}SlopeBaseToSlope2BaseRight`, [0, 0, 1, 2]],
  ];
}

/** Every piece terrain mode may place (e.g. to load their meshes for SceneryInput.surface). */
export function terrainPieces(theme: SceneryTheme): string[] {
  return [...new Set([...pieceShapes(theme, "hill"), ...pieceShapes(theme, "platform")].map(([n]) => n))];
}

/** Corner pattern ("a,b,c,d" in world order, lowest 0) → the piece and direction that has it. */
function catalogue(lib: VanillaLibrary, theme: SceneryTheme, look: TerrainOptions["look"]): Map<string, { name: string; dir: number }> {
  const out = new Map<string, { name: string; dir: number }>();
  for (const [name, local] of pieceShapes(theme, look)) {
    if (!lib.has(name)) continue;
    for (let dir = 0; dir < 4; dir++) {
      const b: VanillaBlock = { name, x: 0, y: 0, z: 0, dir };
      const m = blockMatrix(b, blockSize(lib, b));
      const world = [0, 0, 0, 0];
      CORNERS.forEach(([lx, lz], i) => {
        const v = new THREE.Vector3(lx * BLOCK_CELL, 0, lz * BLOCK_CELL).applyMatrix4(m);
        const wx = Math.round(v.x / BLOCK_CELL), wz = Math.round(v.z / BLOCK_CELL);
        world[CORNERS.findIndex(([cx, cz]) => cx === wx && cz === wz)] = local[i];
      });
      const k = world.join(",");
      if (!out.has(k)) out.set(k, { name, dir });
    }
  }
  return out;
}

export function generateTerrain(lib: VanillaLibrary, input: SceneryInput, options: SceneryLandOptions, terrain: TerrainOptions):
  { blocks: VanillaBlock[]; items: VanillaItem[]; debug: TerrainDebug } | null {
  const shapes = catalogue(lib, options.theme, terrain.look);
  if (!shapes.has("0,0,0,0")) return null;
  const fine = input.roadsFine ?? new Map<string, number>();

  // Step 1: caps from the roads (16 m squares round each corner) and the blocks (the level below them).
  const caps = new Map<string, number>();
  const cap = (k: string, level: number) => caps.set(k, Math.min(caps.get(k) ?? Infinity, Math.max(0, Math.min(TOP_LEVEL, level))));
  for (const [k, y] of fine) {
    const [sx, sz] = parse(k);
    const level = Math.floor((y - 2 - terrain.clearance) / BLOCK_LEVEL);
    // Square s covers corner floor((s + 1) / 2) (its nearest corner on each axis).
    cap(key(Math.floor((sx + 1) / 2), Math.floor((sz + 1) / 2)), level);
  }
  for (const k of input.occupied) {
    const [x, y, z] = k.split(",").map(Number);
    for (const [dx, dz] of CORNERS) cap(key(x + dx, z + dz), y - 1);
  }
  if (caps.size === 0) return { blocks: [], items: [], debug: { target: new Map(), final: new Map(), capped: new Set() } };
  const reach = Math.max(0, options.gap) + Math.max(1, options.band);
  const { dist, base } = spread(caps, inWorld, reach);
  const target = new Map<string, number>();
  const step = Math.max(1, options.hillStep);
  for (const [k, d] of dist) {
    if (caps.has(k)) { target.set(k, caps.get(k)!); continue; }
    const b = base.get(k)!;
    let rise = 0;
    if (options.hills && d > options.gap) {
      rise = Math.min(2 * (Math.floor((d - options.gap - 1) / step) + 1), Math.max(0, 2 * Math.floor(options.hillRise / 2)));
      if (options.farRise > 0) rise += Math.round(options.farRise * (d - options.gap - 1));
    }
    target.set(k, Math.min(TOP_LEVEL, b + rise));
  }

  // Step 2: cells whose four corners are all in the height map; lower corners until every cell has a piece.
  const cells: [number, number][] = [];
  for (const k of target.keys()) {
    const [i, j] = parse(k);
    if (CORNERS.every(([dx, dz]) => target.has(key(i + dx, j + dz)))) cells.push([i, j]);
  }
  const final = new Map(target);
  const corner = (x: number, z: number) => CORNERS.map(([dx, dz]) => key(x + dx, z + dz));
  // "nearest": a cell without a piece takes the piece shape closest to its corners (fewest levels moved, then closest
  // to the wishes), raising corners too where no road or block holds them down. Rounds until nothing changes; what is
  // still left (neighbours pushing back and forth) is settled by lowering.
  if (terrain.fix === "nearest") {
    const patterns = [...shapes.keys()].map((k) => k.split(",").map(Number));
    for (let round = 0; round < 60; round++) {
      let changed = false;
      for (const [x, z] of cells) {
        const ks = corner(x, z);
        const hs = ks.map((k) => final.get(k)!);
        const low = Math.min(...hs);
        if (shapes.has(hs.map((v) => v - low).join(","))) continue;
        let best: number[] | null = null, bestCost = Infinity;
        for (const p of patterns)
          for (let m = low - 2; m <= Math.max(...hs); m++) {
            const next = p.map((v) => m + v);
            if (next.some((v, i) => v < 0 || v > TOP_LEVEL || (caps.has(ks[i]) && v > caps.get(ks[i])!))) continue;
            const cost = next.reduce((s, v, i) => s + Math.abs(v - hs[i]) + 0.25 * Math.abs(v - target.get(ks[i])!), 0);
            if (cost < bestCost) { bestCost = cost; best = next; }
          }
        if (!best) continue;
        best.forEach((v, i) => final.set(ks[i], v));
        changed = true;
      }
      if (!changed) break;
    }
  }
  for (let round = 0; round < 200; round++) {
    let changed = false;
    for (const [x, z] of cells) {
      const ks = corner(x, z);
      const hs = ks.map((k) => final.get(k)!);
      const low = Math.min(...hs);
      if (shapes.has(hs.map((v) => v - low).join(","))) continue;
      // The highest corner (the one furthest above its wish last, so lowering spreads evenly) comes down a level.
      let top = 0;
      for (let i = 1; i < 4; i++) if (hs[i] > hs[top]) top = i;
      final.set(ks[top], hs[top] - 1);
      changed = true;
    }
    if (!changed) break;
  }

  const blocks: VanillaBlock[] = [];
  const flat = new Map<string, number>(), slopes = new Map<string, VanillaBlock>();
  const id = options.theme.id;
  for (const [x, z] of cells) {
    const hs = corner(x, z).map((k) => final.get(k)!);
    const low = Math.min(...hs);
    const shape = shapes.get(hs.map((v) => v - low).join(","));
    if (!shape) continue;
    const b: VanillaBlock = { name: shape.name, x, y: low, z, dir: shape.dir, scenery: id };
    const isFlat = hs.every((v) => v === low);
    // On the grid the ground is there at level 0.
    if (isFlat && low === 0 && inMap(x, z)) { flat.set(key(x, z), 0); continue; }
    blocks.push(freeBlock(lib, b));
    if (isFlat) flat.set(key(x, z), low);
    else slopes.set(key(x, z), blocks[blocks.length - 1]);
  }
  for (const k of input.occupied) {
    const [x, , z] = k.split(",").map(Number);
    flat.delete(key(x, z));
    slopes.delete(key(x, z));
  }
  const clear = (px: number, pz: number) => clearOfRoads(input.footprint, px, pz, Math.max(0, options.clearance));
  const items = plantForest(flat, options.forest, options.density, id, clear, slopes, input.surface);
  return { blocks, items, debug: { target, final, capped: new Set(caps.keys()) } };
}
