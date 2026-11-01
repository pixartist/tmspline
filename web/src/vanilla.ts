import * as THREE from "three";
import { acceleratedRaycast, computeBoundsTree, disposeBoundsTree } from "three-mesh-bvh";

// Fast raycasts (hover, placing, zoom) against block and road meshes: a bounds tree per geometry, built once.
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
THREE.Mesh.prototype.raycast = acceleratedRaycast;
import { BLOCK_CELL, BLOCK_LEVEL, ITEM_COLORS, type ItemColor, type VanillaBlock } from "./track";

/**
 * Vanilla block meshes for the preview. They are the game's own assets, converted locally by tools/VanillaLib from
 * files extracted with Openplanet, and only the development server serves them (/api/vanilla). Without them blocks
 * show as boxes of their size; placing and exporting works either way (the game supplies the real meshes).
 */

interface MeshGroup { m: number; v: number; i: number }
interface MeshEntry { groups: MeshGroup[]; bounds: [number[], number[]] }
/** A column the game's editor puts under an air block: pillar block, the unit it is under (cells) and its direction. */
export interface PillarParam { block: string; offset: [number, number, number]; dir: number }
/**
 * A piece the game adds on a free face of a unit (side walls, caps, the walls of columns): unit offset, face (0..5 =
 * North (+Z), East (-X), South (-Z), West (+X), Top, Bottom) and its mesh. Side pieces are modelled on the North face.
 */
export interface ClipEntry {
  unit: [number, number, number]; face: number;
  /** The free clip, shown unless the face is connected (null: the face only offers clip groups). */
  mesh: { file: string } | null;
  /** Clip groups of the neighbouring face that connect to this one (and hide the free clip). */
  connects?: string[] | null;
  /** Clip groups this face offers to its neighbour. */
  offers?: string[] | null;
  /** Top/bottom clips: quarter turns Ry(turn·90°) about the cell's centre. */
  turn?: number;
  /** Vertical clips (walls): shapes as the middle, top, bottom of a wall over several levels, or alone. */
  stack?: ({ file: string } | null)[] | null;
  /** Vertical clips: walls of the same group continue each other (e.g. a column's wall into its block's adapter). */
  stackGroup?: string | null;
  /** Every free clip of the face (the first one is `mesh`), see faceOptions. */
  options?: {
    name: string; mesh: { file: string } | null; connects: string[];
    stack?: ({ file: string } | null)[] | null; stackGroup?: string | null;
  }[] | null;
  /** Set by visibleClips: drawn without its floor plate (a flat bottom piece on the ground keeps only its trims). */
  floorless?: boolean;
}

/** Materials of the flat floor plates and lids in bottom clips (see VanillaLibrary.hasTrims). */
const FLOOR_MATERIALS = new Set(["Grass", "TrackWallClips"]);

export interface BlockVariant {
  size: [number, number, number]; units: number[][]; mesh: { file: string } | null; pillars?: PillarParam[] | null;
  clips?: ClipEntry[] | null;
}
interface BlockEntry {
  folder: string; ground: BlockVariant | null; air: BlockVariant | null;
  /** The game's catalog icon (path in the library), catalog page (e.g. "RoadTech/Main/") and position on it. */
  icon?: string | null; page?: string; catalog?: number;
  /** Pillar blocks are only placed as columns, so the palette leaves them out. */
  pillar?: boolean;
  /** The game's surface modifier (e.g. "PlatformGrass"): swaps materials of the shared mesh (see LibraryIndex.modifiers). */
  modifier?: string | null;
  /** The air variant used with a column below (its clips: e.g. a slope's wedge walls); null: same as air. */
  airPillar?: BlockVariant | null;
  /** The ground variant with the same "column below" adapter, which the game also shows on the ground. */
  groundPillar?: BlockVariant | null;
  /** The look of Default for this block, when not the materials' own (DecoWall columns: concrete, not wood). */
  defaultColor?: ItemColor | null;
}

/** What the palette shows for a block. */
export interface BlockCatalogEntry { name: string; icon: string | null; page: string; catalog: number }
/**
 * A material's diffuse texture (tools/VanillaLib Textures.cs): mapped by the mesh's UV0 ("uv"), or projected in world
 * space from above ("py") or from above and the sides ("pypxz"), with scale = repeats per UV unit or per metre.
 */
export interface TextureEntry {
  texture: string | null; projection: "uv" | "py" | "pypxz"; scale: [number, number]; alpha: "opaque" | "blend" | "decal";
  /** The texture in the colours White, Green, Blue, Red, Black (`texture` is Default); absent when it has no colour table. */
  colors?: (string | null)[] | null;
}
interface LibraryIndex {
  /** 2: mesh files carry UV0 after the positions. */
  format?: number;
  materials: string[]; textures?: Record<string, TextureEntry>;
  /** Per surface modifier: game material -> key of its replacement in textures ("PlatformGrass/PlatformTech"). */
  modifiers?: Record<string, Record<string, string>>; meshes: Record<string, MeshEntry>; blocks: Record<string, BlockEntry>;
}

export const DIRECTIONS = ["North", "East", "South", "West"] as const;

/** Simplified looks per game material name (first match wins); null hides the material (decals, collision helpers). */
const MATERIAL_LOOKS: [RegExp, number | null][] = [
  [/^(Collision|Trigger|Invisible|Shadow|Occ)|FX/i, null],
  [/^Decal/i, null], // without a texture (with one, decals are drawn as overlays)
  [/Light|Neon|Emissive/i, 0xfff4d0],
  [/Glass|Water/i, 0x7fc4e8],
  [/^RoadTech|^PlatformTech|^OpenTech|Asphalt/i, 0x9a9ea6],
  [/^RoadDirt|^PlatformDirt|^OpenDirt|Dirt/i, 0xc0703f],
  [/^RoadBump/i, 0xb8bfcc],
  [/^RoadIce|^PlatformIce|Ice/i, 0xa9dcf2],
  [/Grass/i, 0x5a9e3c],
  [/Plastic/i, 0xf0c040],
  [/Wood/i, 0x8a5a33],
  [/BordersOff/i, 0x1d1f23],
  [/TrackBorder|TrackWall/i, 0xe9e9ea],
  [/Trims/i, 0x3d4047],
  [/Technics|Structure|Pylon|Pillar|Support|Metal/i, 0x6c7078],
  [/Rubber/i, 0x2c2e33],
];

/** Materials drawn with a plain look even when they have a texture: helpers, and the "Off" borders (dark in game). */
const UNTEXTURED = /^(Collision|Trigger|Invisible|Shadow|Occ)|FX|BordersOff/i;

const lookOf = (name: string): number | null | undefined => MATERIAL_LOOKS.find(([re]) => re.test(name))?.[1];

export class VanillaLibrary {
  readonly names: string[];
  private readonly materials: THREE.Material[];
  private readonly geometries = new Map<string, Promise<THREE.BufferGeometry>>();

  private constructor(private readonly index: LibraryIndex) {
    this.names = Object.keys(index.blocks).sort((a, b) => a.localeCompare(b));
    const hidden = new THREE.MeshBasicMaterial({ visible: false });
    this.materials = index.materials.map((name) => {
      const tex = index.textures?.[name];
      // "Off" borders are the unlit look of the light strips: dark in game, whatever their texture's white parts.
      if (tex?.texture && (index.format ?? 1) >= 2 && !UNTEXTURED.test(name))
        return texturedMaterial(name, tex, this.textureCache);
      const look = lookOf(name);
      if (look === null) return hidden;
      const transparent = /Glass|Water/i.test(name);
      return new THREE.MeshStandardMaterial({
        color: look ?? 0xb8bcc4, roughness: 0.8, side: THREE.DoubleSide,
        transparent, opacity: transparent ? 0.45 : 1, depthWrite: !transparent,
        emissive: /Light|Neon|Emissive/i.test(name) ? 0x8a7a50 : 0x000000,
      });
    });
  }

  /** The library from the development server, or null when it has none (not converted, or a production build). */
  static async load(): Promise<VanillaLibrary | null> {
    try {
      const response = await fetch("/api/vanilla/index.json", { cache: "no-cache" });
      if (!response.ok || !(response.headers.get("content-type") ?? "").includes("json")) return null;
      return new VanillaLibrary((await response.json()) as LibraryIndex);
    } catch {
      return null;
    }
  }

  /**
   * All blocks with their catalog data, in the game's catalog order (page, then position, then name). Pillar blocks
   * (column pieces) are grouped under "Pillars", by the page the game files them under.
   */
  get catalog(): BlockCatalogEntry[] {
    return this.names.map((name) => {
      const b = this.index.blocks[name];
      const page = (b.page ?? "").split("/").filter(Boolean).join("/") || "Other";
      return { name, icon: b.icon ? `/api/vanilla/${b.icon}` : null, page: b.pillar ? `Pillars/${page}` : page, catalog: b.catalog ?? 0 };
    }).sort((a, b) => a.page.localeCompare(b.page) || a.catalog - b.catalog || a.name.localeCompare(b.name));
  }

  /** The block's surface modifier (e.g. "PlatformGrass"), if it has one. */
  modifierOf(name: string): string | undefined {
    return this.index.blocks[name]?.modifier ?? undefined;
  }

  /** How Default looks on a block (the colour its materials are drawn in when it has none of its own). */
  defaultColorOf(name: string): ItemColor {
    return this.index.blocks[name]?.defaultColor ?? "Default";
  }

  /** Pillar blocks are column pieces: placed with the pillar flag, grid only. */
  isPillar(name: string): boolean {
    return !!this.index.blocks[name]?.pillar;
  }

  has(name: string): boolean {
    return name in this.index.blocks;
  }

  /** The clips of a block: those of its variant, or of the "column below" variant when a column stands under it. */
  /**
   * The variant the game shows: on the ground and over a column, the one with the column adapter (its own mesh, e.g. a
   * loop's back side, and clips, e.g. a slope's wedge walls); otherwise the ground or air variant.
   */
  shownVariant(name: string, ground: boolean, columnBelow: boolean): BlockVariant | null {
    const b = this.index.blocks[name];
    if (ground && b?.groundPillar) return b.groundPillar;
    if (columnBelow && !ground && b?.airPillar) return b.airPillar;
    return this.variant(name, ground);
  }

  clipsOf(name: string, ground: boolean, columnBelow: boolean): ClipEntry[] {
    return this.shownVariant(name, ground, columnBelow)?.clips ?? [];
  }

  /** Whether a mesh is only a plain cap (the flat TrackWallClips lids on top of and under walls). */
  isCap(file: string): boolean {
    const groups = this.index.meshes[file]?.groups ?? [];
    return groups.length > 0 && groups.every((g) => this.index.materials[g.m] === "TrackWallClips");
  }

  /** Whether a mesh is flat (at most 1.5 m high): a plate or edge trim that lies flush on the ground there. */
  isFlat(file: string): boolean {
    const b = this.index.meshes[file]?.bounds;
    return !!b && b[1][1] - b[0][1] <= 1.5;
  }

  /** Whether a mesh lies within one cell's footprint (x and z within 0..32), as single-cell trims do. */
  inOneCell(file: string): boolean {
    const b = this.index.meshes[file]?.bounds;
    return !!b && b[0][0] >= -1 && b[0][2] >= -1 && b[1][0] <= 33 && b[1][2] <= 33;
  }

  /** Whether a mesh has parts besides floor plates and lids (Grass, TrackWallClips): trims, borders, decals. */
  hasTrims(file: string): boolean {
    return (this.index.meshes[file]?.groups ?? []).some((g) => !FLOOR_MATERIALS.has(this.index.materials[g.m]));
  }

  /** A material list (by material index) with the floor plates hidden, see hasTrims. */
  floorless(list: THREE.Material[]): THREE.Material[] {
    let out = this.floorlessLists.get(list);
    if (!out) {
      const hidden = new THREE.MeshBasicMaterial({ visible: false });
      out = list.map((m, i) => (FLOOR_MATERIALS.has(this.index.materials[i]) ? hidden : m));
      this.floorlessLists.set(list, out);
    }
    return out;
  }

  /** Whether a block has an adapter for a column below (which encloses the column's top). */
  hasColumnAdapter(name: string): boolean {
    return !!this.index.blocks[name]?.airPillar;
  }

  /** The variant used at a height: ground variant on level 0, air above (each falls back to the other). */
  variant(name: string, ground: boolean): BlockVariant | null {
    const b = this.index.blocks[name];
    if (!b) return null;
    return ground ? b.ground ?? b.air : b.air ?? b.ground;
  }

  /** Resolves once every mesh asked for so far and every texture has loaded (for previews and screenshots). */
  async settled(): Promise<void> {
    for (let seen = -1; seen !== this.geometries.size; ) {
      seen = this.geometries.size;
      await Promise.allSettled([...this.geometries.values()]);
      // Meshes arrive in callbacks that may ask for more (clips): give them a frame.
      await new Promise((r) => setTimeout(r, 50));
    }
    while ([...this.textureCache.values()].some((t) => !t.image)) await new Promise((r) => setTimeout(r, 50));
  }

  /** Mesh of a variant (cached; shared by all blocks that use it), with one group per game material. */
  geometry(file: string): Promise<THREE.BufferGeometry> {
    let g = this.geometries.get(file);
    if (!g) {
      g = this.loadGeometry(file);
      this.geometries.set(file, g);
    }
    return g;
  }

  get materialList(): THREE.Material[] {
    return this.materials;
  }

  private readonly modifiedMaterials = new Map<string, THREE.Material[]>();
  private readonly floorlessLists = new Map<THREE.Material[], THREE.Material[]>();

  /**
   * The materials of a block's mesh: the game's surface modifier (PlatformGrass, PlatformDirt, ...) swaps some of them
   * for its own, the others stay; a block colour (not Default) paints the materials that have a colour table.
   */
  materialsOf(name: string, modifierOverride?: string, color: ItemColor = "Default"): THREE.Material[] {
    const modifier = modifierOverride ?? this.index.blocks[name]?.modifier;
    const swaps = modifier ? this.index.modifiers?.[modifier] : undefined;
    const shade = ITEM_COLORS.indexOf(color) - 1; // index into TextureEntry.colors; -1 for Default
    if ((!modifier || !swaps) && shade < 0) return this.materials;
    const cacheKey = `${swaps ? modifier : ""}|${color}`;
    let list = this.modifiedMaterials.get(cacheKey);
    if (!list) {
      list = this.index.materials.map((m, i) => {
        const key = swaps?.[m] ?? m;
        const tex = this.textureOf(key);
        // Not the materials drawn plain on purpose (the dark unlit borders, helpers).
        const painted = shade >= 0 && !UNTEXTURED.test(m) ? tex?.colors?.[shade] : null;
        if (painted && tex) return texturedMaterial(key, { ...tex, texture: painted }, this.textureCache);
        return key !== m && tex ? texturedMaterial(key, tex, this.textureCache) : this.materials[i];
      });
      this.modifiedMaterials.set(cacheKey, list);
    }
    return list;
  }

  /**
   * The game's texture for a material (by its name, e.g. "RoadTech" or "TrackBorders"), for other meshes with UV0 (the
   * spline road): its texture and how it is mapped, or null when the library has none.
   */
  textureOf(material: string): TextureEntry | null {
    const t = this.index.textures?.[material];
    return t?.texture && (this.index.format ?? 1) >= 2 ? t : null;
  }

  /** The library's (shared) texture image for an entry from textureOf. */
  textureImage(t: TextureEntry): THREE.Texture {
    return loadTexture(t, this.textureCache);
  }

  private readonly textureCache = new Map<string, THREE.Texture>();

  private async loadGeometry(file: string): Promise<THREE.BufferGeometry> {
    const entry = this.index.meshes[file];
    const response = await fetch(`/api/vanilla/${file}`);
    if (!entry || !response.ok) throw new Error(`Missing block mesh ${file}`);
    const buffer = await response.arrayBuffer();
    // Layout (tools/VanillaLib): all groups' positions (Float32 xyz), from format 2 all groups' UV0 (Float32 uv), then all
    // groups' indices (Uint32, per group).
    const vertices = entry.groups.reduce((n, g) => n + g.v, 0);
    const indexCount = entry.groups.reduce((n, g) => n + g.i, 0);
    const positions = new Float32Array(buffer, 0, vertices * 3);
    const hasUv = (this.index.format ?? 1) >= 2;
    const source = new Uint32Array(buffer, vertices * (hasUv ? 20 : 12), indexCount);
    const indices = new Uint32Array(indexCount);
    const geo = new THREE.BufferGeometry();
    let vertexBase = 0, start = 0;
    for (const g of entry.groups) {
      for (let k = 0; k < g.i; k++) indices[start + k] = source[start + k] + vertexBase;
      geo.addGroup(start, g.i, g.m);
      vertexBase += g.v;
      start += g.i;
    }
    geo.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    if (hasUv) geo.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(buffer, vertices * 12, vertices * 2), 2));
    geo.setIndex(new THREE.BufferAttribute(indices, 1));
    geo.computeVertexNormals();
    geo.computeBoundingSphere();
    geo.computeBoundingBox();
    geo.computeBoundsTree({ indirect: true }); // keeps the material groups' triangle ranges as they are
    return geo;
  }
}

/**
 * A material with the game's diffuse texture (loaded in the background; until then it is plain white). Textures are
 * shared between materials that use the same image. Projected materials sample the texture by world position instead of
 * UVs: from above (X/Z) for "py", blended with side projections on walls for "pypxz".
 */
function loadTexture(tex: TextureEntry, cache: Map<string, THREE.Texture>): THREE.Texture {
  const key = `${tex.texture}|${tex.projection === "uv" ? tex.scale.join(",") : "world"}`;
  let map = cache.get(key);
  if (!map) {
    map = new THREE.TextureLoader().load(`/api/vanilla/${tex.texture}`);
    map.flipY = false; // DirectX texture coordinates: v = 0 at the top of the image
    map.wrapS = map.wrapT = THREE.RepeatWrapping;
    map.colorSpace = THREE.SRGBColorSpace;
    map.anisotropy = 8;
    if (tex.projection === "uv") map.repeat.set(tex.scale[0], tex.scale[1]);
    cache.set(key, map);
  }
  return map;
}

function texturedMaterial(name: string, tex: TextureEntry, cache: Map<string, THREE.Texture>): THREE.Material {
  const map = loadTexture(tex, cache);
  const decal = tex.alpha === "decal", blend = tex.alpha === "blend";
  const material = new THREE.MeshStandardMaterial({
    map, roughness: 0.75, side: THREE.DoubleSide,
    // Decals use their alpha as a cut-out mask (it is 0 or ~1 in the game's images), not as see-through blending.
    transparent: blend, depthWrite: !blend, alphaTest: decal ? 0.5 : 0,
    opacity: blend && /Glass/i.test(name) ? 0.6 : 1,
    // Decals lie on the surfaces they mark: pulled towards the camera so they win the depth test.
    polygonOffset: decal, polygonOffsetFactor: decal ? -2 : 0, polygonOffsetUnits: decal ? -4 : 0,
    emissive: /Light|Neon|Emissive|Screen/i.test(name) ? 0x8a7a50 : 0x000000,
  });
  if (tex.projection !== "uv") {
    const sides = tex.projection === "pypxz";
    material.onBeforeCompile = (shader) => {
      shader.uniforms.projScale = { value: new THREE.Vector2(tex.scale[0], tex.scale[1]) };
      shader.vertexShader = shader.vertexShader
        .replace("#include <common>", "#include <common>\nvarying vec3 vProjPos;\nvarying vec3 vProjNormal;")
        .replace("#include <project_vertex>", "#include <project_vertex>\nvProjPos = (modelMatrix * vec4(transformed, 1.0)).xyz;\nvProjNormal = normalize(mat3(modelMatrix) * objectNormal);");
      shader.fragmentShader = shader.fragmentShader
        .replace("#include <common>", "#include <common>\nvarying vec3 vProjPos;\nvarying vec3 vProjNormal;\nuniform vec2 projScale;")
        .replace("#include <map_fragment>", sides
          ? `vec3 projW = pow(abs(normalize(vProjNormal)), vec3(4.0));
             projW /= projW.x + projW.y + projW.z;
             vec4 projColor = texture2D(map, vProjPos.zy * projScale) * projW.x + texture2D(map, vProjPos.xz * projScale) * projW.y
               + texture2D(map, vProjPos.xy * projScale) * projW.z;
             diffuseColor *= projColor;`
          : "diffuseColor *= texture2D(map, vProjPos.xz * projScale);");
    };
    material.customProgramCacheKey = () => `projected-${tex.projection}`;
  }
  return material;
}

/** Grid blocks on level 0 use the ground variant; free blocks are always placed as air blocks (as the game does). */
export const isGroundBlock = (b: VanillaBlock) => !b.free && b.y === 0;

/** Footprint in cells (x, levels, z) as modelled (North). */
export function blockSize(lib: VanillaLibrary | null, b: VanillaBlock): [number, number, number] {
  return lib?.variant(b.name, isGroundBlock(b))?.size ?? [1, 1, 1];
}

/** Rotation of a free block: yaw about Y, then pitch about X, then roll about Z (degrees), like items. */
export function freeRotation(yaw: number, pitch = 0, roll = 0): THREE.Matrix4 {
  const r = Math.PI / 180;
  return new THREE.Matrix4().makeRotationFromEuler(new THREE.Euler(pitch * r, yaw * r, roll * r, "YXZ"));
}

/**
 * Block-local metres to editor coordinates. Verified in game (tools/BlockProbe): each direction step turns the block
 * another 90° clockwise seen from above (East points the modelled +Z to -X), and the cell is the min corner of the
 * turned footprint.
 */
export function blockMatrix(b: VanillaBlock, size: [number, number, number]): THREE.Matrix4 {
  // Free blocks turn about their own origin, which sits at `position` (checked against free blocks the game saved).
  if (b.free && b.position) {
    const [x, y, z] = b.position;
    return new THREE.Matrix4().makeTranslation(x, y, z).multiply(freeRotation(b.yaw ?? 0, b.pitch ?? 0, b.roll ?? 0));
  }
  const rotation = new THREE.Matrix4().makeRotationY((-b.dir * Math.PI) / 2);
  const [sx, , sz] = size;
  const corners = [[0, 0], [sx, 0], [0, sz], [sx, sz]].map(([x, z]) =>
    new THREE.Vector3(x * BLOCK_CELL, 0, z * BLOCK_CELL).applyMatrix4(rotation));
  const minX = Math.min(...corners.map((c) => c.x)), minZ = Math.min(...corners.map((c) => c.z));
  return new THREE.Matrix4().makeTranslation(b.x * BLOCK_CELL - minX, b.y * BLOCK_LEVEL, b.z * BLOCK_CELL - minZ).multiply(rotation);
}

const blockKey = (b: VanillaBlock) => b.free
  ? `${b.name}|free|${b.position?.join(",")}|${b.yaw}|${b.pitch}|${b.roll}|${b.color ?? ""}`
  : `${b.name}|${b.x}|${b.y}|${b.z}|${b.dir}|${b.color ?? ""}`;

/** Height of ground shadows and the ends of drop lines (just above the ground grid). */
export const SHADOW_Y = 0.06;

const cellKey = (x: number, y: number, z: number) => `${x},${y},${z}`;

/** Grid cell (x, z) of a point in block-local metres, through a block matrix. */
function cellOf(m: THREE.Matrix4, x: number, z: number): [number, number] {
  const p = new THREE.Vector3(x, 0, z).applyMatrix4(m);
  return [Math.floor(p.x / BLOCK_CELL), Math.floor(p.z / BLOCK_CELL)];
}

/**
 * The columns the game's editor puts under air grid blocks: for each of the block's pillar params, one pillar block per
 * level from just below the block down to the ground (or down to whatever block is in the way). Level 0 pieces are
 * placed as ground blocks. Free blocks and blocks with pillars: false get none.
 */
/** The grid cells the units of grid blocks take (see cellKey). */
export function occupiedCells(blocks: VanillaBlock[], lib: VanillaLibrary | null): Set<string> {
  const occupied = new Set<string>();
  for (const b of blocks) {
    if (b.free) continue;
    const size = blockSize(lib, b);
    const m = blockMatrix(b, size);
    for (const [ux, uy, uz] of lib?.variant(b.name, isGroundBlock(b))?.units ?? [[0, 0, 0]]) {
      const [x, z] = cellOf(m, (ux + 0.5) * BLOCK_CELL, (uz + 0.5) * BLOCK_CELL);
      occupied.add(cellKey(x, b.y + uy, z));
    }
  }
  return occupied;
}

/** Face directions in block-local cells (North, East, South, West, Top, Bottom), as the blocks' directions turn. */
const FACE_DIRS: [number, number, number][] = [[0, 0, 1], [-1, 0, 0], [0, 0, -1], [1, 0, 0], [0, 1, 0], [0, -1, 0]];

/** A clip's unit cell and the world direction of its face. */
function clipFace(b: VanillaBlock, m: THREE.Matrix4, rotation: THREE.Matrix4, c: ClipEntry): { cell: [number, number, number]; dir: [number, number, number] } {
  const [x, z] = cellOf(m, (c.unit[0] + 0.5) * BLOCK_CELL, (c.unit[2] + 0.5) * BLOCK_CELL);
  const d = new THREE.Vector3(...FACE_DIRS[c.face]).applyMatrix4(rotation);
  return { cell: [x, b.y + c.unit[1], z], dir: [Math.round(d.x), Math.round(d.y), Math.round(d.z)] };
}

/**
 * Who takes each grid cell, the clip groups each unit face offers and the wall (vertical clip) group on each face (key:
 * cell|direction), for visibleClips.
 */
export interface ClipNeighbours { owners: Map<string, VanillaBlock>; offers: Map<string, string[]>; walls: Map<string, string> }

export function clipNeighbours(blocks: VanillaBlock[], lib: VanillaLibrary | null): ClipNeighbours {
  const owners = new Map<string, VanillaBlock>(), offers = new Map<string, string[]>(), walls = new Map<string, string>();
  for (const b of blocks) {
    if (b.free) continue;
    const m = blockMatrix(b, blockSize(lib, b));
    for (const [ux, uy, uz] of lib?.variant(b.name, isGroundBlock(b))?.units ?? [[0, 0, 0]]) {
      const [x, z] = cellOf(m, (ux + 0.5) * BLOCK_CELL, (uz + 0.5) * BLOCK_CELL);
      owners.set(cellKey(x, b.y + uy, z), b);
    }
  }
  for (const b of blocks) {
    if (b.free) continue;
    const m = blockMatrix(b, blockSize(lib, b));
    const rotation = new THREE.Matrix4().extractRotation(m);
    for (const c of blockClips(b, lib, owners)) {
      if (!c.offers?.length && !c.stackGroup) continue;
      const f = clipFace(b, m, rotation, c);
      if (c.offers?.length) offers.set(`${f.cell}|${f.dir}`, c.offers);
      if (c.stackGroup) walls.set(`${f.cell}|${f.dir}`, c.stackGroup);
    }
  }
  return { owners, offers, walls };
}

/**
 * The clips of a block that the game shows, as the game connects them: a free clip is hidden when the neighbouring
 * unit face offers a clip group it connects to (a platform's underside on a column's top, two platforms side by side)
 * or belongs to the same block; below level 0 is the ground. Faces next to empty cells or to blocks without a matching
 * clip keep theirs (a column's cap under a slope, a platform's edge next to a road). Free blocks show all of theirs
 * except the bottom ones.
 */
/** The variant the game shows for a block (see VanillaLibrary.shownVariant): column below = a column piece right under it. */
function blockVariant(b: VanillaBlock, lib: VanillaLibrary | null, owners: Map<string, VanillaBlock>): BlockVariant | null {
  if (!lib) return null;
  if (b.free) return lib.variant(b.name, false);
  if (isGroundBlock(b)) return lib.shownVariant(b.name, true, false);
  const m = blockMatrix(b, blockSize(lib, b));
  // A column under any of its bottom units (a loop's column stands under its second cell).
  const bottom = lib.variant(b.name, false)?.units?.filter((u) => u[1] === 0) ?? [[0, 0, 0]];
  const onColumn = bottom.some((u) => {
    const [x, z] = cellOf(m, (u[0] + 0.5) * BLOCK_CELL, (u[2] + 0.5) * BLOCK_CELL);
    const below = owners.get(cellKey(x, b.y - 1, z));
    return !!below?.pillar && below !== b;
  });
  return lib.shownVariant(b.name, false, onColumn);
}

function blockClips(b: VanillaBlock, lib: VanillaLibrary | null, owners: Map<string, VanillaBlock>): ClipEntry[] {
  return blockVariant(b, lib, owners)?.clips ?? [];
}

function visibleClips(b: VanillaBlock, lib: VanillaLibrary, n: ClipNeighbours): ClipEntry[] {
  const clips = blockClips(b, lib, n.owners).flatMap((c) => faceOptions(c, lib)).filter((c) => c.mesh);
  if (b.free) return clips.filter((c) => c.face !== 5);
  const m = blockMatrix(b, blockSize(lib, b));
  const rotation = new THREE.Matrix4().extractRotation(m);
  const shown: ClipEntry[] = [];
  for (const c of clips) {
    const { cell, dir } = clipFace(b, m, rotation, c);
    const next: [number, number, number] = [cell[0] + dir[0], cell[1] + dir[1], cell[2] + dir[2]];
    // Towards the ground only real pieces show (the foot of a slope's start is a bottom clip), not the plain lids; of
    // the flat floor pieces only their trims (wall feet), not the grass floor plate, which the game does not show there.
    if (next[1] < 0) {
      if (c.face !== 5 || !c.mesh || lib.isCap(c.mesh.file)) continue;
      if (lib.isFlat(c.mesh.file)) {
        if (!lib.hasTrims(c.mesh.file)) continue;
        shown.push({ ...c, floorless: true });
        continue;
      }
    }
    const owner = n.owners.get(cellKey(...next));
    if (owner === b) continue;
    if (owner) {
      // A column's top is enclosed by the adapter of the block standing on it.
      if (b.pillar && c.face === 4 && !owner.pillar && lib.hasColumnAdapter(owner.name)) continue;
      // ... and the block's own plain underside there with it (not real pieces stored as bottom clips).
      if (!b.pillar && c.face === 5 && owner.pillar && lib.hasColumnAdapter(b.name) && c.mesh && lib.isCap(c.mesh.file)) continue;
      const offered = n.offers.get(`${next}|${dir.map((v) => -v)}`);
      if (offered && c.connects?.some((g) => offered.includes(g))) continue;
    }
    // Walls take the shape for their place in a wall of the same group over several levels.
    if (c.stack && c.stackGroup) {
      // It continues into the same wall group, or into another unit of the same block (whose own clips end it).
      const continues = (dy: number) => n.walls.get(`${[cell[0], cell[1] + dy, cell[2]]}|${dir}`) === c.stackGroup
        || n.owners.get(cellKey(cell[0], cell[1] + dy, cell[2])) === b;
      const above = continues(1), below = continues(-1);
      const shape = c.stack[above && below ? 0 : below ? 1 : above ? 2 : 3] ?? c.mesh;
      shown.push({ ...c, mesh: shape });
    } else shown.push(c);
  }
  return shown;
}

/**
 * All free clips of a face, each as its own entry hidden by its own connection: a face often has several that go
 * together (the two halves of a loop's side wall, inside and outside walls). A cap listed twice is the same trim on
 * both edges (the two rims of a half-pipe): the second turns the other way.
 */
function faceOptions(c: ClipEntry, lib: VanillaLibrary): ClipEntry[] {
  if (!c.options?.length) return [c];
  const seen = new Map<string, number>();
  return c.options.filter((o) => o.mesh).map((o) => {
    const repeat = seen.get(o.name) ?? 0;
    seen.set(o.name, repeat + 1);
    return {
      ...c,
      mesh: o.mesh,
      connects: o.connects.length ? o.connects : null,
      stack: o.stack ?? null,
      stackGroup: o.stackGroup ?? null,
      turn: ((c.turn ?? 0) + (c.face >= 4 && lib.inOneCell(o.mesh!.file) ? repeat * 2 : 0)) % 4,
      options: null,
    };
  });
}

/** Where a clip's mesh goes in its block (block-local): on its unit, turned to its face; caps in the cell above/below. */
function clipMatrix(c: ClipEntry): THREE.Matrix4 {
  const m = new THREE.Matrix4().makeTranslation(c.unit[0] * BLOCK_CELL, c.unit[1] * BLOCK_LEVEL, c.unit[2] * BLOCK_CELL);
  const half = BLOCK_CELL / 2;
  if (c.face >= 4)
    // Caps are modelled in the cell above/below; shared ones are turned to the block (e.g. a slope's underside).
    return m.multiply(new THREE.Matrix4().makeTranslation(half, c.face === 4 ? BLOCK_LEVEL : -BLOCK_LEVEL, half))
      .multiply(new THREE.Matrix4().makeRotationY(((c.turn ?? 0) * Math.PI) / 2))
      .multiply(new THREE.Matrix4().makeTranslation(-half, 0, -half));
  return m.multiply(new THREE.Matrix4().makeTranslation(half, 0, half))
    // Side pieces are modelled on the North face (+Z) and turn like block directions (East = -X).
    .multiply(new THREE.Matrix4().makeRotationY((-c.face * Math.PI) / 2))
    .multiply(new THREE.Matrix4().makeTranslation(-half, 0, -half))
    // The game models side pieces from the neighbouring cell's side (just outside the face, z 32..33), as that cell
    // sees them: turned around the face's centre they lie inside the block (lip flush with the edge, trims under it,
    // sloped and curved trims running the right way; checked on Slope2 and LoopStartCurve1In).
    .multiply(new THREE.Matrix4().makeTranslation(half, 0, BLOCK_CELL))
    .multiply(new THREE.Matrix4().makeRotationY(Math.PI))
    .multiply(new THREE.Matrix4().makeTranslation(-half, 0, -BLOCK_CELL));
}

export function pillarColumns(blocks: VanillaBlock[], lib: VanillaLibrary | null): VanillaBlock[] {
  if (!lib) return [];
  const occupied = occupiedCells(blocks, lib);
  const columns: VanillaBlock[] = [];
  const placed = new Set<string>();
  for (const b of blocks) {
    if (b.free || b.y === 0 || b.pillars === false || b.pillar) continue;
    const m = blockMatrix(b, blockSize(lib, b));
    for (const p of lib.variant(b.name, false)?.pillars ?? []) {
      // The pillar sits in the block's own grid (offset, turned by its direction), which the block's matrix turns again.
      const pillar: VanillaBlock = { name: p.block, x: p.offset[0], y: 0, z: p.offset[2], dir: p.dir };
      const psize = blockSize(lib, pillar);
      const pm = m.clone().multiply(blockMatrix(pillar, psize));
      const cells: [number, number][] = [];
      for (let i = 0; i < psize[0]; i++) for (let j = 0; j < psize[2]; j++) cells.push(cellOf(pm, (i + 0.5) * BLOCK_CELL, (j + 0.5) * BLOCK_CELL));
      const x = Math.min(...cells.map((c) => c[0])), z = Math.min(...cells.map((c) => c[1]));
      const top = b.y + p.offset[1] - 1;
      let bottom = top;
      while (bottom >= 0 && cells.every(([cx, cz]) => !occupied.has(cellKey(cx, bottom, cz)))) bottom--;
      for (let level = bottom + 1; level <= top; level++) {
        const key = `${p.block}|${x}|${level}|${z}`;
        if (placed.has(key)) continue;
        placed.add(key);
        const modifier = lib.modifierOf(b.name);
        columns.push({ name: p.block, x, y: level, z, dir: (b.dir + p.dir) % 4, pillar: true, ...(modifier ? { modifier } : {}), ...(b.color ? { color: b.color } : {}) });
      }
    }
  }
  return columns;
}

const OUTLINE_HOVER = 0xffffff, OUTLINE_SELECTED = 0xffd23f;

/** The placed blocks in the scene, plus the ghost shown while placing one. */
export class BlockLayer {
  readonly group = new THREE.Group();
  /** Ground shadows and drop lines (world space), so blocks above the ground can be located. */
  private readonly shadows = new THREE.Group();
  private readonly shadowMaterial = new THREE.MeshBasicMaterial({
    color: 0x000000, transparent: true, opacity: 0.28, depthWrite: false, side: THREE.DoubleSide,
    polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -2,
  });
  private readonly dropMaterial = new THREE.LineBasicMaterial({ color: 0x1c2228, transparent: true, opacity: 0.6 });

  /** Shows or hides the ground shadows and drop lines (the block preview hides them). */
  set shadowsVisible(on: boolean) {
    this.shadows.visible = on;
  }

  constructor() {
    this.group.add(this.shadows);
  }
  private items: THREE.Object3D[] = [];
  private keys: string[] = [];
  private lib: VanillaLibrary | null = null;
  private ghost: THREE.Object3D | null = null;
  private ghostKey = "";
  private readonly ghostMaterial = new THREE.MeshBasicMaterial({ color: 0x7dffb0, transparent: true, opacity: 0.45, depthWrite: false });
  private readonly placeholderMaterial = new THREE.MeshStandardMaterial({ color: 0xb8bcc4, transparent: true, opacity: 0.55, roughness: 0.9 });

  setLibrary(lib: VanillaLibrary | null): void {
    this.lib = lib;
    this.keys = [];
  }

  /** Cells and clip groups of all grid blocks and columns: which clips show depends on them (see visibleClips). */
  private neighbours: ClipNeighbours = { owners: new Map(), offers: new Map(), walls: new Map() };

  setNeighbours(blocks: VanillaBlock[]): void {
    this.neighbours = clipNeighbours(blocks, this.lib);
  }

  /** Cache key of a block's look: where it is and which of its clips show. */
  private keyOf(b: VanillaBlock): string {
    const clips = this.lib ? visibleClips(b, this.lib, this.neighbours).map((c) => `${c.unit}/${c.face}/${c.mesh?.file}`).join(";") : "";
    const mesh = blockVariant(b, this.lib, this.neighbours.owners)?.mesh?.file ?? "";
    return `${blockKey(b)}|${b.modifier ?? ""}|${this.lib ? 1 : 0}|${mesh}|${clips}`;
  }

  get library(): VanillaLibrary | null {
    return this.lib;
  }

  /** Meshes to raycast against; hits carry the block index in userData.blockIndex of an ancestor. */
  get pickables(): THREE.Object3D[] {
    return this.items;
  }

  static indexOf(object: THREE.Object3D | null): number | null {
    for (let o = object; o; o = o.parent) if (typeof o.userData.blockIndex === "number") return o.userData.blockIndex as number;
    return null;
  }

  private pillarItems: THREE.Object3D[] = [];
  private pillarKeys: string[] = [];

  /** The columns under blocks (see pillarColumns): drawn like blocks, but not pickable and without shadows. */
  syncPillars(pillars: VanillaBlock[]): void {
    pillars.forEach((b, i) => {
      const key = this.keyOf(b);
      if (this.pillarKeys[i] === key && this.pillarItems[i]) return;
      if (this.pillarItems[i]) this.group.remove(this.pillarItems[i]);
      const item = this.build(b, false, false);
      item.traverse((o) => (o.raycast = () => {}));
      // Meshes arrive later; they must not be pickable either.
      item.userData.unpickable = true;
      this.pillarItems[i] = item;
      this.pillarKeys[i] = key;
      this.group.add(item);
    });
    for (const extra of this.pillarItems.splice(pillars.length)) this.group.remove(extra);
    this.pillarKeys.length = pillars.length;
  }

  sync(blocks: VanillaBlock[], selected: number | null, hovered: number | null): void {
    blocks.forEach((b, i) => {
      const key = this.keyOf(b);
      if (this.keys[i] === key && this.items[i]) return;
      if (this.items[i]) this.remove(this.items[i]);
      const item = this.build(b, false);
      item.userData.blockIndex = i;
      this.items[i] = item;
      this.keys[i] = key;
      this.group.add(item);
    });
    for (const extra of this.items.splice(blocks.length)) this.remove(extra);
    this.keys.length = blocks.length;
    this.items.forEach((item, i) => {
      const outline = item.userData.outline as THREE.LineSegments;
      outline.visible = i === selected || i === hovered;
      (outline.material as THREE.LineBasicMaterial).color.setHex(i === selected ? OUTLINE_SELECTED : OUTLINE_HOVER);
    });
  }

  /** Shows a translucent copy of a block where it would be placed (null hides it). */
  setGhost(b: VanillaBlock | null): void {
    const key = b ? blockKey(b) : "";
    if (key === this.ghostKey) return;
    this.ghostKey = key;
    if (this.ghost) this.remove(this.ghost);
    this.ghost = b ? this.build(b, true) : null;
    if (this.ghost) {
      (this.ghost.userData.outline as THREE.LineSegments).visible = true;
      this.group.add(this.ghost);
    }
  }

  private remove(item: THREE.Object3D): void {
    this.group.remove(item);
    const shadow = item.userData.shadow as THREE.Object3D | undefined;
    if (shadow) {
      this.shadows.remove(shadow);
      shadow.traverse((o) => (o as THREE.Mesh).geometry?.dispose());
    }
  }

  /**
   * Where a block is, seen on the ground: its footprint as a soft shadow with an outline, and lines from the corners of
   * its base down to the ground (only when it is above it).
   */
  private shadowOf(matrix: THREE.Matrix4, size: [number, number, number]): THREE.Object3D {
    const w = size[0] * BLOCK_CELL, d = size[2] * BLOCK_CELL;
    const corners = [[0, 0], [w, 0], [w, d], [0, d]].map(([x, z]) => new THREE.Vector3(x, 0, z).applyMatrix4(matrix));
    const flat = corners.map((c) => new THREE.Vector3(c.x, SHADOW_Y, c.z));
    const g = new THREE.Group();
    const quad = new THREE.BufferGeometry().setFromPoints([flat[0], flat[1], flat[2], flat[0], flat[2], flat[3]]);
    const shade = new THREE.Mesh(quad, this.shadowMaterial);
    shade.renderOrder = 1;
    const outline = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(flat), this.dropMaterial);
    g.add(shade, outline);
    const lifted = corners.filter((c) => c.y > SHADOW_Y + 0.5);
    if (lifted.length) {
      const drops = lifted.flatMap((c) => [c, new THREE.Vector3(c.x, SHADOW_Y, c.z)]);
      g.add(new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(drops), this.dropMaterial));
    }
    g.traverse((o) => (o.raycast = () => {})); // never picked
    return g;
  }

  private build(b: VanillaBlock, ghost: boolean, withShadow = true): THREE.Object3D {
    const size = blockSize(this.lib, b);
    const item = new THREE.Group();
    item.matrixAutoUpdate = false;
    item.matrix.copy(blockMatrix(b, size));
    item.matrixWorldNeedsUpdate = true;
    if (withShadow) {
      const shadow = this.shadowOf(item.matrix, size);
      this.shadows.add(shadow);
      item.userData.shadow = shadow;
    }

    // Outline of the occupied cells (hover / selection / ghost).
    const box = new THREE.BoxGeometry(size[0] * BLOCK_CELL, size[1] * BLOCK_LEVEL, size[2] * BLOCK_CELL);
    box.translate((size[0] * BLOCK_CELL) / 2, (size[1] * BLOCK_LEVEL) / 2, (size[2] * BLOCK_CELL) / 2);
    const outline = new THREE.LineSegments(new THREE.EdgesGeometry(box),
      new THREE.LineBasicMaterial({ color: ghost ? 0x7dffb0 : OUTLINE_HOVER, depthTest: false, transparent: true }));
    outline.renderOrder = 6;
    outline.visible = false;
    outline.raycast = () => {}; // not pickable
    item.add(outline);
    item.userData.outline = outline;

    const file = blockVariant(b, this.lib, this.neighbours.owners)?.mesh?.file;
    const clips = this.lib ? visibleClips(b, this.lib, this.neighbours) : [];
    if ((!file && !clips.length && !this.lib?.variant(b.name, isGroundBlock(b))?.clips?.length) || !this.lib) {
      // No mesh (no library, or a block it does not know): a box of the block's size.
      const placeholder = new THREE.Mesh(box, ghost ? this.ghostMaterial : this.placeholderMaterial);
      item.add(placeholder);
      return item;
    }
    const lib = this.lib;
    const parts = [
      ...(file ? [{ file, matrix: null as THREE.Matrix4 | null, floorless: false }] : []),
      ...clips.map((c) => ({ file: c.mesh!.file, matrix: clipMatrix(c), floorless: !!c.floorless })),
    ];
    for (const part of parts)
      lib.geometry(part.file).then((geo) => {
        const materials = lib.materialsOf(b.name, b.modifier, b.color ?? lib.defaultColorOf(b.name));
        const mesh = new THREE.Mesh(geo, ghost ? this.ghostMaterial : part.floorless ? lib.floorless(materials) : materials);
        if (part.matrix) {
          mesh.matrixAutoUpdate = false;
          mesh.matrix.copy(part.matrix);
        }
        if (item.userData.unpickable) mesh.raycast = () => {};
        item.add(mesh);
      }).catch((err) => {
        console.warn(err);
        if (!part.matrix) item.add(new THREE.Mesh(box, ghost ? this.ghostMaterial : this.placeholderMaterial));
      });
    return item;
  }
}
