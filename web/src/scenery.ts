import * as THREE from "three";
import { BLOCK_CELL, BLOCK_LEVEL, MAP_HALF_EXTENT, type VanillaBlock } from "./track";
import { blockMatrix, blockSize, cellOf, type VanillaLibrary } from "./vanilla";

/**
 * Scenery generator: surrounds the map's tracks with deco blocks. The cliff themes raise everything at least a few cells
 * away from the roads and blocks to a plateau, and line its edge with the game's cliff walls facing the track: straight
 * runs, outer and inner corners, round the outside and round islands inside loops. It knows nothing about jumps (the
 * mapper removes what is in the way).
 *
 * Works on the 32 m grid, ground level only. The cliff pieces are read from the block library: in each piece the tallest
 * columns are the wall, the others the space its face leans into (towards the track).
 */

export interface SceneryTheme { id: string; label: string; prefix: string }

export const SCENERY_THEMES: SceneryTheme[] = [
  { id: "cliff8", label: "Cliffs (64 m)", prefix: "DecoCliff8NoHill" },
  { id: "cliff8dirt", label: "Cliffs, dirt (64 m)", prefix: "DecoCliff8DirtNoHill" },
  { id: "cliff8ice", label: "Cliffs, ice (64 m)", prefix: "DecoCliff8IceNoHill" },
  { id: "cliff10", label: "Cliffs (80 m)", prefix: "DecoCliff10" },
  { id: "cliff10dirt", label: "Cliffs, dirt (80 m)", prefix: "DecoCliff10Dirt" },
  { id: "cliff10ice", label: "Cliffs, ice (80 m)", prefix: "DecoCliff10Ice" },
];

export interface SceneryInput {
  /** Lowest point of the roads per grid column ("x,z" → metres above the ground). */
  roads: Map<string, number>;
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

/** Generates the scenery blocks (null when the library lacks the theme's pieces). */
export function generateCliffs(lib: VanillaLibrary, input: SceneryInput, options: SceneryOptions): VanillaBlock[] | null {
  const { prefix, id } = options.theme;
  const straight = piece(lib, `${prefix}Straight`), small = piece(lib, `${prefix}StraightSmall`);
  const outer = piece(lib, `${prefix}CornerOut`), inner = piece(lib, `${prefix}CornerInSmall`);
  if (!straight || !small || !outer || !inner) return null;

  const half = Math.round(MAP_HALF_EXTENT / BLOCK_CELL);
  const inMap = (x: number, z: number) => x >= -half && x < half && z >= -half && z < half;
  // What the cliffs must keep clear: roads below their top (with a level to spare) and blocks up to that height.
  const clearance = (outer.height + 1) * BLOCK_LEVEL;
  const track = new Set<string>();
  for (const [k, y] of input.roads) if (y < clearance) track.add(k);
  for (const k of input.occupied) {
    const [x, y, z] = k.split(",").map(Number);
    if (y * BLOCK_LEVEL < clearance) track.add(key(x, z));
  }
  if (track.size === 0) return [];

  // Distance from the track in cells (8 neighbours), over the map.
  const dist = new Map<string, number>();
  let frontier: Cell[] = [...track].map((k) => k.split(",").map(Number) as Cell);
  for (const [x, z] of frontier) dist.set(key(x, z), 0);
  for (let d = 1; frontier.length; d++) {
    const next: Cell[] = [];
    for (const [x, z] of frontier)
      for (let dx = -1; dx <= 1; dx++)
        for (let dz = -1; dz <= 1; dz++) {
          const [nx, nz] = [x + dx, z + dz];
          if (!inMap(nx, nz) || dist.has(key(nx, nz))) continue;
          dist.set(key(nx, nz), d);
          next.push([nx, nz]);
        }
    frontier = next;
  }

  // High ground: the gap, then the row the cliff faces lean into, then the walls. Outside the map counts as high, so
  // there are no walls along its edge.
  const reach = options.gap + 2;
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
  return blocks;
}

/** Lowest road point per grid column, from road meshes (world space). Triangles are sampled every few metres. */
export function roadColumns(meshes: THREE.Mesh[]): Map<string, number> {
  const columns = new Map<string, number>();
  const a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3(), p = new THREE.Vector3();
  const mark = (v: THREE.Vector3) => {
    const k = key(Math.floor(v.x / BLOCK_CELL), Math.floor(v.z / BLOCK_CELL));
    columns.set(k, Math.min(columns.get(k) ?? Infinity, v.y));
  };
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
      const n = Math.min(64, Math.max(1, Math.ceil(longest / 4)));
      for (let u = 0; u <= n; u++)
        for (let v = 0; u + v <= n; v++) {
          p.copy(a).multiplyScalar(1 - (u + v) / n).addScaledVector(b, u / n).addScaledVector(c, v / n);
          mark(p);
        }
    }
  }
  return columns;
}
