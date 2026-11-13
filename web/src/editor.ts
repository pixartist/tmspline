import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { TransformControls } from "three/examples/jsm/controls/TransformControls.js";
import { buildPreview, buildTerrain, dabTerrain, type ObjectPlacement, type PreviewMesh, type PreviewQuality, type TerrainChunk, type TerrainMesh } from "./core";
import { BlockLayer, blockMatrix, blockSize, freeRotation, GROUND_ADAPTER_FLAG, occupiedCells, pillarColumns, SHADOW_Y, VanillaLibrary } from "./vanilla";
import { generateScenery, landPieces, roadColumns, roadFootprint, type SceneryLandOptions } from "./scenery";
import { ItemLayer } from "./items";
import { generateTerrain, terrainPieces, type TerrainDebug } from "./terrain";
import { buildTerrainGrid, meshHeightAt, plantOnTerrain, terrainHeight } from "./ownTerrain";
import {
  add, appendPoint, GATES, gateLook, gatePanel, bezierAt, BLOCK_CELL, BLOCK_LEVEL, closestOnSegment, MAP_HALF_EXTENT, WORLD_HALF_EXTENT, newObject, newTrack, segmentBezier, splitSegment, wrapBank, normalize, setHandle, sub, syncSegments,
  type ItemColor, type TerrainGrid, type TerrainSurface, type Track, type TrackMap, type TrackObjectKind, type TrackPoint, type Vec3, type VanillaBlock,
} from "./track";

export type Selection =
  | { kind: "point"; index: number }
  | { kind: "handle"; index: number; which: "in" | "out" }
  | { kind: "segment"; index: number }
  | { kind: "object"; index: number }
  /** A vanilla block of the map (map-level, not part of a track). */
  | { kind: "block"; index: number }
  /** Several vanilla blocks (Shift+click, drag a rectangle): moved, raised and deleted together. Always two or more. */
  | { kind: "blocks"; indices: number[] }
  | null;

/**
 * Something under the cursor that can be hovered, clicked or dragged. Points and segments of other tracks than the
 * active one carry their track's index (clicking them makes that track active); everything else is on the active track.
 */
type Pick =
  | { kind: "point"; index: number; track?: number }
  | { kind: "handle"; index: number; which: "in" | "out" }
  | { kind: "bank"; index: number }
  | { kind: "segment"; index: number; track?: number }
  | { kind: "object"; index: number }
  | { kind: "block"; index: number };

/** The preview of one track, from its last build. */
interface TrackView {
  road: THREE.Mesh | null;
  roadSegments: Int32Array;
  segmentIndices: Map<number, Uint32Array>;
  /** Road frames at the spline points (see PreviewMesh.pointFrames), and the bank values they were built with. */
  pointFrames: Float32Array;
  frameBanks: number[];
  /** Per vertex (distance, across) on the driving surface, and segment start distances. */
  surfaceCoords: Float32Array;
  segmentStarts: Float32Array;
  /** Where the track's objects sit. */
  placements: ObjectPlacement[];
  arrows: THREE.Group;
  length: number;
  triangles: number;
  /** Quality of the mesh shown, or null before the first build. */
  quality: PreviewQuality | null;
}

const emptyView = (): TrackView => ({
  road: null, roadSegments: new Int32Array(), segmentIndices: new Map(), pointFrames: new Float32Array(), frameBanks: [],
  surfaceCoords: new Float32Array(), segmentStarts: new Float32Array(), placements: [], arrows: new THREE.Group(),
  length: 0, triangles: 0, quality: null,
});

type MoveMode = "screen" | "horizontal" | "vertical";

/** A block being moved (mouse drag or axis gizmo) as it was when the move started; position only for free blocks. */
interface MovedBlock { index: number; x: number; y: number; z: number; position?: Vec3 }

interface Drag {
  pick: Pick;
  pointerId: number;
  startX: number;
  startY: number;
  /** Becomes true once the pointer moved far enough to count as a drag (and an undo step was recorded). */
  moved: boolean;
  /** Move drags: free in the screen plane, height kept (Shift) or height only (Alt). */
  mode: MoveMode;
  plane: THREE.Plane;
  /** Move drags: grabbed position minus the plane hit, so the object does not jump to the cursor. */
  offset: THREE.Vector3;
  /** Block drags: where the drag plane was first hit, and the moved blocks (the selected ones) as they were then. */
  blockStart?: { hit: THREE.Vector3; blocks: MovedBlock[] };
  /** Bank drags: last ring-plane direction and the unsnapped bank. */
  lastDir?: THREE.Vector3;
  rawBank?: number;
  /** Object drags: the previous pointer ray, to move the object by the step between two rays on the road's plane. */
  lastRay?: THREE.Ray;
}

const SURFACE_COLORS: Record<string, number> = {
  RoadTech: 0x9a9ea6, RoadDirt: 0xc0703f, RoadBump: 0xb8bfcc, RoadIce: 0xa9dcf2, RoadWood: 0xb07a4a,
  PlatformTech: 0xd6d8dc, PlatformDirt: 0x9b6a45, PlatformGrass: 0x5a9e3c, PlatformIce: 0xc6ecfa,
  PlatformPlastic: 0xf0c040, Wood: 0x8a5a33, Snow: 0xf4f7fa, Sand: 0xd9c38c, Grass: 0x4f8f2f,
  Magnet: 0x10102a, MagnetAccel: 0x1a1640, SuperMagnet: 0x261436,
  Border: 0x3a3d44, BorderPlastic: 0xe8b020, Underside: 0x1a1b1e, Pillar: 0x8c9096, RoadWoodLine: 0xf4f4f4,
};

/** Tint for boost surfaces, keyed by the material name suffix from the core (e.g. "RoadTech_Turbo"). */
const EFFECT_COLORS: Record<string, number> = {
  Turbo: 0xf2c21b, Turbo2: 0xe8452c, ReactorBoost: 0xf29a1b, ReactorBoost2: 0xe86a2c, SlowMotion: 0x4fc3e8,
  NoSteering: 0xb04fe8, NoBrakes: 0xe84f8a, FreeWheeling: 0x8a8a8a, Cruise: 0x4fe88a, Fragile: 0xe8d84f,
  NoGrip: 0x9fd8ff, ForceAcceleration: 0xff7a1b, Reset: 0xffffff, Bumper: 0x1bf2c2, Bumper2: 0x1bc2f2,
};

/** Marker colors of track objects; markers of objects that cannot be built where they are turn red. */
const OBJECT_COLORS: Record<TrackObjectKind, number> = { Pillar: 0xd8dce4, Hole: 0xff5fa0, Gap: 0xff9f1c, Bump: 0x6fd36f, Gate: 0xffd23f };
/**
 * A gate block (GateSpecial..., GateGameplay...) is a ring 32 m across standing in the middle of its block, its
 * opening 26.2 m (from 2.9 m to 29.1 m up). The road runs through it at the object's height in the ring: 3 m, on its
 * lower rim (only a narrow road fits there), to 16 m, through its middle (26 m fit). Mirrors GateObjects.
 */
const GATE_MIDDLE = 16;
const OBJECT_BLOCKED = 0xff2020;

const GATE_COLORS: Record<string, number> = {
  Start: 0x20d040, Checkpoint: 0x2080ff, Finish: 0xe02020, StartFinish: 0xf0c020,
  // Effect and car gates of our own (Surfaces.EffectGate in the core).
  Turbo: 0xf0d020, Turbo2: 0xe03020, TurboRoulette: 0xf08020, ReactorBoost: 0xf0b030, ReactorBoostDown: 0xf0b030,
  ReactorBoost2: 0xe04030, ReactorBoost2Down: 0xe04030, SlowMotion: 0x40a0f0, NoSteering: 0xc050e0, NoBrakes: 0xe06090,
  FreeWheeling: 0x808890, Cruise: 0x30c0c0, Fragile: 0xf07030, Reset: 0x40d060,
  CarStadium: 0xe0e0e0, CarSnow: 0xa0d8ff, CarRally: 0xc08040, CarDesert: 0xe0c060,
  NoGrip: 0x70b0e0, ForceAcceleration: 0x60e060, Bumper: 0xe0a0c0, Bumper2: 0xe060a0,
};

const COLORS = {
  point: 0xffffff, start: 0x3fdc6a, selected: 0xffd23f, handle: 0x4fa3ff, otherTrack: 0x8a96a8,
  ring: 0xff8a3d, hoverTint: 0xffffff, segmentSelected: 0xffd23f,
};
/** On-screen size of points: world radius per meter of camera distance. */
/** Materials that are not the driving surface (their UVs are not along/across the road). */
/** Same as RoadMeshSettings.RoadTextureInset in the core (Road* surfaces only). */
const ROAD_TEXTURE_INSET = 0.1;
const NO_GRID_SURFACES = { Border: 1, BorderPlastic: 1, Underside: 1, Pillar: 1 };

/**
 * Draws grid lines on a road material from the "gridUv" attribute (U = distance / 32 m, V = 0..1 across): one line
 * every 4 m along the road and 8 lanes across. Lines are anti-aliased and fade out where they would get denser than
 * a few pixels, so distant road does not shimmer.
 */
function addRoadGrid(material: THREE.MeshStandardMaterial, inset: number): void {
  material.customProgramCacheKey = () => `roadGrid${inset}`;
  material.onBeforeCompile = (shader) => {
    shader.vertexShader = "attribute vec2 gridUv;\nvarying vec2 vGridUv;\n"
      + shader.vertexShader.replace("#include <begin_vertex>", "#include <begin_vertex>\nvGridUv = gridUv;");
    shader.fragmentShader = "varying vec2 vGridUv;\n" + shader.fragmentShader.replace("#include <color_fragment>", `#include <color_fragment>
      // The road textures leave out a share on each side (RoadMeshSettings.RoadTextureInset); lanes span the whole road.
      vec2 g = vec2(vGridUv.x, (vGridUv.y - ${inset.toFixed(4)}) / ${(1 - 2 * inset).toFixed(4)}) * vec2(8.0, 8.0);
      vec2 w = fwidth(g);
      vec2 d = abs(fract(g - 0.5) - 0.5) / max(w, vec2(1e-5));
      vec2 fade = clamp(1.0 - (w - 0.15) / 0.2, 0.0, 1.0);
      float line = max((1.0 - min(d.x, 1.0)) * fade.x, (1.0 - min(d.y, 1.0)) * fade.y);
      // Darker lines on light surfaces, lighter ones on dark surfaces (magnet).
      vec3 lineColor = dot(diffuseColor.rgb, vec3(0.3, 0.59, 0.11)) > 0.2 ? diffuseColor.rgb * 0.62 : diffuseColor.rgb + 0.22;
      diffuseColor.rgb = mix(diffuseColor.rgb, lineColor, line);`);
  };
}

/** Values of TmSpline.Core's Effect enum for the reactor boosts (PreviewMesh effect entries carry the enum value). */
const REACTOR_EFFECTS = [3, 4];
const SCREEN_SIZE = 0.011;
const OBJECT_MARKER = new THREE.OctahedronGeometry(1.1);
const DRAG_THRESHOLD_PX = 3;

/** Raise and lower move the land this many metres per dab at strength 1 (at the brush's middle). */
export const RAISE_PER_STRENGTH = 6;

/**
 * Terrain brushes. Raise and lower work on the land as a solid, where the cursor meets its mesh: on the ground, on a
 * cutting's side, on a tunnel's wall or roof (the roads still cut through whatever is added). Smooth and flatten (to the
 * height where the stroke started) work on the land's height map, on open ground.
 */
export type TerrainBrush = "raise" | "lower" | "smooth" | "flatten";
export interface TerrainPaint {
  brush: TerrainBrush;
  /** Radius, metres. */
  radius: number;
  /** Raise/lower: metres per dab at the middle; smooth/flatten: how far towards the target per dab (0..1). */
  strength: number;
  /** Raise/lower: the roads do not cut through what is painted (land worked into the road's own space). */
  uncut?: boolean;
}
/** Land lower than the stadium's ground lies this deep (ownTerrain's SUNK). */
const SUNK_LAND = -2;
const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
/** Height range of track points and free blocks, meters above the ground (the export server's limit; the exported map grows to fit). */
const Y_MIN = -8, Y_MAX = 1960;
/**
 * Height of the game's block roads and platform tops above their level (RoadTech, PlatformTech: 2 m): snapped track
 * points sit there, so the spline road meets blocks flush.
 */
const ROAD_SURFACE = 2;
/** Highest block level above the ground (the exported map grows to fit, up to 255 rows of 8 m; the ground is row 9). */
export const MAX_BLOCK_LEVEL = 240;
/** The water's surface in each environment with water, in the editor's heights (measured in game; MapExporter.WaterSurface in the core). Height 0 is one of the game's block levels. */
const WATER_SURFACE: Record<string, number> = { BlueBay: -1, GreenCoast: -0.8, RedIsland: -0.3, WhiteShore: -1 };
const DIRECTION_NAMES = ["North", "East", "South", "West"];
/** Touchpad wheel delta (pixels) per level step with Ctrl+wheel; a mouse wheel notch is about 100 and always one step. */
const WHEEL_LEVEL_STEP = 100;
/** R turns free blocks by this many degrees. */
const FREE_YAW_STEP = 15;

export class Editor {
  map: TrackMap;
  /** The track being edited: Add point, insert, objects and the selection all refer to it. */
  active = 0;
  selection: Selection = null;
  /** Called after any change to the map or selection (for the UI). */
  onChange: () => void = () => {};
  /** Called before a user edit starts, with a snapshot of the map (for undo). */
  onBeforeEdit: (snapshot: TrackMap) => void = () => {};
  /** What the editor is busy with for a while (generating, meshing the terrain), or null when nothing. */
  onBusy: (message: string | null) => void = () => {};
  private readonly busy = new Map<string, string>();
  private setBusy(key: string, message: string | null): void {
    if (message) this.busy.set(key, message);
    else this.busy.delete(key);
    this.onBusy([...this.busy.values()].at(-1) ?? null);
  }
  /** Optional axis-arrow gizmo for precise moves (toggled with G). */
  axisGizmoEnabled = false;
  /** Mesh debug view: the triangles of the roads and the land drawn as lines over them. */
  wireEnabled = false;
  private readonly wireMaterial = new THREE.LineBasicMaterial({ color: 0x0b0e12, transparent: true, opacity: 0.6 });

  setWire(on: boolean): void {
    this.wireEnabled = on;
    this.terrainMesh?.children.forEach((o) => this.wireOf(o as THREE.Mesh));
    for (const v of this.views.values()) if (v.road) this.wireOf(v.road);
    this.updateSeamDebug();
  }

  /** Seam debug: the stretches of road edge with no rim of the land near them (red), with the mesh debug view. */
  private seamDebug: THREE.LineSegments | null = null;
  private readonly seamDebugMaterial = new THREE.LineBasicMaterial({ color: 0xff2020, depthTest: false });

  /**
   * Marks the road edges the land's rim does not come near (within SEAM_REACH): there the seam finds nothing to join.
   * The rim is the land's open boundary: the edges of its triangles that only one triangle has (the chunks' own
   * borders cancel out, their corners lie in the same places). The road's edges are its driving surface's outermost
   * vertices every 2 m along it.
   */
  private updateSeamDebug(): void {
    if (this.seamDebug) {
      this.scene.remove(this.seamDebug);
      this.seamDebug.geometry.dispose();
      this.seamDebug = null;
    }
    const group = this.terrainMesh;
    if (!this.wireEnabled || !group) return;
    const SEAM_REACH = 12, CELL = 4;
    const key = (x: number, y: number, z: number) => `${Math.round(x * 50)},${Math.round(y * 50)},${Math.round(z * 50)}`;
    const edges = new Map<string, { n: number; a: THREE.Vector3; b: THREE.Vector3 }>();
    for (const child of group.children) {
      const mesh = child as THREE.Mesh;
      const pos = mesh.geometry?.getAttribute("position") as THREE.BufferAttribute | undefined;
      if (!pos) continue;
      for (let t = 0; t + 2 < pos.count; t += 3)
        for (let k = 0; k < 3; k++) {
          const a = new THREE.Vector3().fromBufferAttribute(pos, t + k), b = new THREE.Vector3().fromBufferAttribute(pos, t + (k + 1) % 3);
          const ka = key(a.x, a.y, a.z), kb = key(b.x, b.y, b.z);
          const id = ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`;
          const e = edges.get(id);
          if (e) e.n++;
          else edges.set(id, { n: 1, a, b });
        }
    }
    // The rim's points in a grid of squares, for looking up what is near.
    const rim = new Map<string, THREE.Vector3[]>();
    for (const e of edges.values()) {
      if (e.n !== 1) continue;
      for (const p of [e.a, e.b, e.a.clone().lerp(e.b, 0.5)]) {
        const cell = `${Math.floor(p.x / CELL)},${Math.floor(p.z / CELL)}`;
        (rim.get(cell) ?? rim.set(cell, []).get(cell)!).push(p);
      }
    }
    const near = (p: THREE.Vector3) => {
      const r = Math.ceil(SEAM_REACH / CELL), cx = Math.floor(p.x / CELL), cz = Math.floor(p.z / CELL);
      for (let x = cx - r; x <= cx + r; x++)
        for (let z = cz - r; z <= cz + r; z++)
          for (const q of rim.get(`${x},${z}`) ?? []) if (q.distanceToSquared(p) < SEAM_REACH * SEAM_REACH) return true;
      return false;
    };
    const lines: number[] = [];
    for (const v of this.views.values()) {
      if (!v.road) continue;
      const pos = v.road.geometry.getAttribute("position") as THREE.BufferAttribute;
      const sc = v.surfaceCoords;
      // Per 2 m along the road: its outermost surface vertices left and right.
      const sides = new Map<number, { l?: [number, THREE.Vector3]; r?: [number, THREE.Vector3] }>();
      for (let i = 0; i < pos.count && i * 2 + 1 < sc.length; i++) {
        const d = sc[i * 2], across = sc[i * 2 + 1];
        if (!Number.isFinite(d) || !Number.isFinite(across)) continue;
        const slot = Math.round(d / 2);
        const s = sides.get(slot) ?? sides.set(slot, {}).get(slot)!;
        const p = new THREE.Vector3().fromBufferAttribute(pos, i);
        if (across < 0 && (!s.l || across < s.l[0])) s.l = [across, p];
        if (across > 0 && (!s.r || across > s.r[0])) s.r = [across, p];
      }
      const slots = [...sides.keys()].sort((a, b) => a - b);
      for (const side of ["l", "r"] as const) {
        let previous: THREE.Vector3 | null = null;
        for (const slot of slots) {
          const p = sides.get(slot)![side]?.[1];
          if (!p) { previous = null; continue; }
          const missing = !near(p);
          if (missing && previous) lines.push(previous.x, previous.y + 0.4, previous.z, p.x, p.y + 0.4, p.z);
          previous = missing ? p : null;
        }
      }
    }
    if (lines.length === 0) return;
    const geo = new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(lines, 3));
    this.seamDebug = new THREE.LineSegments(geo, this.seamDebugMaterial);
    this.seamDebug.renderOrder = 20;
    this.seamDebug.raycast = () => {};
    this.scene.add(this.seamDebug);
  }

  /** A mesh's triangles as lines on it (its "wire" child), there while the debug view is on. */
  private wireOf(mesh: THREE.Mesh): void {
    const old = mesh.getObjectByName("wire") as THREE.LineSegments | undefined;
    if (old) { mesh.remove(old); old.geometry.dispose(); }
    if (!this.wireEnabled || !(mesh as THREE.Mesh).isMesh) return;
    const lines = new THREE.LineSegments(new THREE.WireframeGeometry(mesh.geometry), this.wireMaterial);
    lines.name = "wire";
    lines.raycast = () => {};
    mesh.add(lines);
  }
  /** Insert mode: hovering the road previews where a click inserts a point. */
  insertMode = false;
  /** Place mode: the next click on the road places an object of this kind there. */
  placeMode: TrackObjectKind | null = null;
  /** Block mode: clicks place this vanilla block (dir and level change with R and PgUp/PgDn). */
  blockPlace: { name: string; dir: number; level: number; free: boolean; yaw: number; lift: number; color: ItemColor } | null = null;
  /**
   * Snap mode: grid step in meters (0 = off). Track points, their handles (by grid steps) and free blocks snap to it
   * horizontally, and to min(step, 8 m) vertically (8 m = one block level); points at the heights of block roads (see
   * ROAD_SURFACE). Ctrl while dragging ignores it.
   */
  snapStep = 0;
  private snapGrid: THREE.GridHelper | null = null;
  /** Vanilla block meshes (development server only; null: blocks show as boxes). */
  library: VanillaLibrary | null = null;
  private readonly blocks = new BlockLayer();
  /** Stand-ins for the map's vanilla items (trees). */
  private readonly items = new ItemLayer();
  /** Vertical lines from every track point down to the ground, with a small cross where they land. */
  private readonly dropLines = new THREE.LineSegments(new THREE.BufferGeometry(),
    new THREE.LineBasicMaterial({ color: 0x1c2228, transparent: true, opacity: 0.55 }));
  /** Green bars joining the tops of the gates of each linked checkpoint group (across all tracks). */
  private readonly checkpointLinks = new THREE.Group();
  /** Nestle's height map (debug view), see showHeightMap. */
  private readonly heightMap = new THREE.Group();
  private lastHeightMap: TerrainDebug | null = null;
  private heightMapShown = false;
  private readonly checkpointLinkMaterial = new THREE.MeshBasicMaterial({ color: 0x3fe07a, transparent: true, opacity: 0.85 });
  private blockTarget: VanillaBlock | null = null;
  private lastBlockPlace: Editor["blockPlace"] = null;

  readonly renderer: THREE.WebGLRenderer;
  readonly camera: THREE.PerspectiveCamera;
  readonly orbit: OrbitControls;
  private readonly scene = new THREE.Scene();
  private readonly axisGizmo: TransformControls;
  /** What the axis gizmo moves when blocks are selected: sits on the first selected block's bottom centre. */
  private readonly blockAnchor = new THREE.Object3D();
  /** Axis gizmo moves of blocks: the anchor and the selected blocks when the move started. */
  private gizmoBlocks: { anchor: THREE.Vector3; blocks: MovedBlock[] } | null = null;
  private readonly raycaster = new THREE.Raycaster();
  private ground: THREE.Mesh;
  private readonly label: HTMLDivElement;

  /** Preview per track (keyed by the track object, so undo and deleting tracks need no bookkeeping). */
  private readonly views = new Map<Track, TrackView>();
  private readonly roadMaterials = new Map<string, THREE.Material>();
  private readonly insertMarker: THREE.Mesh;
  private insertTarget: { segment: number; at: number } | null = null;
  private placeTarget: { segment: number; at: number; offset: number } | null = null;
  private objectMeshes: THREE.Mesh[] = [];
  /** Points of the other tracks: dimmer, clicking one makes its track active. */
  private otherPointMeshes: THREE.Mesh[] = [];
  private readonly objectMarkers = new THREE.Group();
  private readonly hoverOverlay: THREE.Mesh;
  private readonly selectOverlay: THREE.Mesh;

  private readonly controls = new THREE.Group();
  private pointMeshes: THREE.Mesh[] = [];
  private handleMeshes: THREE.Mesh[] = [];
  private handleLine: THREE.LineSegments | null = null;
  private bankRing: THREE.Group | null = null;
  private bankPick: THREE.Mesh | null = null;
  private bankMarker: THREE.Mesh | null = null;

  private hover: Pick | null = null;
  private drag: Drag | null = null;
  private clickStart: { x: number; y: number } | null = null;
  /** Rectangle selection of blocks: a left drag that started on nothing draggable (Shift adds, Ctrl removes). */
  private marquee: { pointerId: number; x: number; y: number; el: HTMLDivElement } | null = null;
  private rightClickStart: { x: number; y: number } | null = null;
  /** Ctrl+wheel amount not yet turned into a level step. */
  private wheelLevel = 0;
  private lastPointer: PointerEvent | null = null;
  private hoverQueued = false;
  /** Modifier state from keyboard events, in case pointer events do not report it. */
  private shiftDown = false;
  private altDown = false;
  private lastMerge: { key?: string; time: number } = { time: 0 };
  /** WASD keys currently held, and the time of the last rendered frame (for frame-rate independent movement). */
  private readonly moveKeys = new Set<string>();
  private lastFrameAt = performance.now();
  private rebuildQueued = false;
  private rebuildRunning = false;
  private queuedQuality: PreviewQuality = 1;
  /** Tracks whose preview is out of date. */
  private readonly dirty = new Set<Track>();
  private lastChangeAt = 0;
  private refineTimer = 0;

  constructor(private readonly container: HTMLElement, map: TrackMap) {
    this.map = map;

    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(window.devicePixelRatio);
    container.appendChild(this.renderer.domElement);

    this.label = document.createElement("div");
    this.label.className = "drag-label";
    this.label.hidden = true;
    container.appendChild(this.label);

    this.scene.background = new THREE.Color(0x9cc7ee);
    // Far enough for tracks 2 km outside the grid (WORLD_HALF_EXTENT) to stay visible.
    this.scene.fog = new THREE.Fog(0x9cc7ee, 3000, 9000);
    this.camera = new THREE.PerspectiveCamera(55, 1, 0.5, 14000);
    this.camera.position.set(220, 180, 260);

    this.scene.add(new THREE.HemisphereLight(0xdfefff, 0x4a6b3a, 1.6));
    const sun = new THREE.DirectionalLight(0xffffff, 1.8);
    sun.position.set(300, 600, 200);
    this.scene.add(sun);

    this.ground = this.createGround();
    this.scene.add(this.ground);
    this.scene.add(this.controls);
    this.scene.add(this.objectMarkers);
    this.scene.add(this.blocks.group);
    this.scene.add(this.items.group);
    this.dropLines.raycast = () => {};
    this.dropLines.frustumCulled = false;
    this.scene.add(this.dropLines);
    this.scene.add(this.checkpointLinks);
    this.scene.add(this.heightMap);
    void VanillaLibrary.load().then((lib) => {
      this.library = lib;
      this.blocks.setLibrary(lib);
      // The roads' materials change with the library's textures.
      this.roadMaterials.clear();
      for (const v of this.views.values())
        if (v.road) v.road.material = (v.road.userData.materials as string[]).map((n) => this.roadMaterial(n));
      this.syncBlocks();
      this.onChange();
    });

    this.insertMarker = new THREE.Mesh(new THREE.SphereGeometry(1, 16, 12),
      new THREE.MeshBasicMaterial({ color: 0x7dffb0, depthTest: false, transparent: true, opacity: 0.9 }));
    this.insertMarker.renderOrder = 11;
    this.insertMarker.visible = false;
    this.scene.add(this.insertMarker);

    this.hoverOverlay = this.createOverlay(COLORS.hoverTint, 0.18);
    this.selectOverlay = this.createOverlay(COLORS.segmentSelected, 0.3);

    this.orbit = new OrbitControls(this.camera, this.renderer.domElement);
    this.orbit.target.set(0, 16, 0);
    // Zoom is our own (onWheel): towards the cursor, by the distance to what is under it.
    this.orbit.enableZoom = false;
    this.orbit.maxPolarAngle = Math.PI * 0.495;
    this.orbit.update();

    this.axisGizmo = new TransformControls(this.camera, this.renderer.domElement);
    this.axisGizmo.setSize(0.8);
    this.axisGizmo.addEventListener("dragging-changed", (e) => {
      this.orbit.enabled = !e.value;
      if (e.value) this.onBeforeEdit(structuredClone(this.map));
      this.gizmoBlocks = e.value && this.axisGizmo.object === this.blockAnchor
        ? { anchor: this.blockAnchor.position.clone(), blocks: this.movedBlocks(this.selectedBlocks()) }
        : null;
      if (!e.value) this.placeBlockAnchor();
    });
    this.blockAnchor.userData = { kind: "blockAnchor" };
    this.scene.add(this.blockAnchor);
    this.axisGizmo.addEventListener("objectChange", () => this.onAxisGizmoMoved());
    this.scene.add(this.axisGizmo.getHelper());

    // Capture phase: runs before OrbitControls' own listener, so a drag on a point never also orbits.
    const el = this.renderer.domElement;
    el.addEventListener("pointerdown", (e) => this.onPointerDown(e), { capture: true });
    el.addEventListener("pointermove", (e) => this.onPointerMove(e));
    el.addEventListener("pointerup", (e) => this.onPointerUp(e));
    el.addEventListener("pointerleave", () => this.setHover(null));
    el.addEventListener("wheel", (e) => this.onWheel(e), { passive: false });
    window.addEventListener("keydown", (e) => this.onModifierChange(e));
    window.addEventListener("keyup", (e) => this.onModifierChange(e));
    window.addEventListener("keydown", (e) => this.onMoveKey(e, true));
    window.addEventListener("keyup", (e) => this.onMoveKey(e, false));
    window.addEventListener("blur", () => this.moveKeys.clear());

    new ResizeObserver(() => this.resize()).observe(container);
    this.resize();
    this.renderer.setAnimationLoop(() => this.render());

    this.syncControls();
    this.requestRebuild();
  }

  // ---- public API used by the UI -------------------------------------------------------------

  get track(): Track {
    return this.map.tracks[this.active];
  }

  /** Length and triangle count of the whole map, from the last previews. */
  get stats(): { length: number; triangles: number } {
    const views = this.map.tracks.map((t) => this.views.get(t)).filter((v): v is TrackView => !!v);
    return { length: views.reduce((s, v) => s + v.length, 0), triangles: views.reduce((s, v) => s + v.triangles, 0) };
  }

  /** Replaces the whole map (load, new, undo). */
  setMap(map: TrackMap): void {
    this.map = map;
    this.mapChanged();
  }

  /**
   * Loads another map in place of this one, with undo: every field of the old map goes, also those the new one has not
   * (its terrain, say).
   */
  replaceMap(map: TrackMap): void {
    this.setTerrainPaint(null);
    this.editMap((m) => {
      for (const key of Object.keys(m)) delete (m as unknown as Record<string, unknown>)[key];
      Object.assign(m, map);
    });
  }

  /** Makes another track the one being edited (clears the selection). */
  setActive(index: number): void {
    if (index === this.active || index < 0 || index >= this.map.tracks.length) return;
    this.active = index;
    this.selection = null;
    this.setHover(null);
    this.syncControls();
    this.updateSelectOverlay();
    this.onChange();
  }

  /**
   * Map-level edit with undo support (adding or removing tracks, map name, laps). Every track whose preview is missing
   * gets built.
   */
  editMap(fn: (m: TrackMap) => void): void {
    this.onBeforeEdit(structuredClone(this.map));
    this.lastMerge = { time: 0 };
    fn(this.map);
    this.mapChanged();
  }

  /** Adds a straight track at the middle of the view, heading where the camera looks, and makes it active. */
  addTrack(): void {
    const t = this.orbit.target;
    const forward = this.camera.getWorldDirection(new THREE.Vector3());
    const center: Vec3 = [clampMap(t.x), clamp(Math.max(t.y, 16), Y_MIN, Y_MAX), clampMap(t.z)];
    this.editMap((m) => m.tracks.push(newTrack(center, [forward.x, 0, forward.z])));
    this.setActive(this.map.tracks.length - 1);
  }

  /** Deletes the active track (not the last one). */
  deleteActiveTrack(): boolean {
    if (this.map.tracks.length <= 1) return false;
    const index = this.active;
    this.editMap((m) => m.tracks.splice(index, 1));
    return true;
  }

  private mapChanged(): void {
    for (const t of this.map.tracks) syncSegments(t);
    this.active = clamp(this.active, 0, this.map.tracks.length - 1);
    // Previews of tracks that are gone.
    for (const [t, v] of this.views)
      if (!this.map.tracks.includes(t)) {
        this.disposeView(v);
        this.views.delete(t);
      }
    if (!this.isSelectionValid()) this.selection = null;
    this.syncControls();
    this.syncBlocks();
    this.requestRebuild([]);
    this.updateSelectOverlay();
    this.onChange();
  }

  private viewOf(track: Track): TrackView {
    let v = this.views.get(track);
    if (!v) {
      v = emptyView();
      this.views.set(track, v);
      this.scene.add(v.arrows);
    }
    return v;
  }

  private disposeView(v: TrackView): void {
    if (v.road) {
      v.road.geometry.dispose();
      this.scene.remove(v.road);
    }
    for (const child of v.arrows.children) (child as THREE.ArrowHelper).dispose();
    this.scene.remove(v.arrows);
  }

  // The active track's preview, under the names the editing code uses.
  private get view(): TrackView { return this.viewOf(this.track); }
  private get road() { return this.view.road; }
  private get roadSegments() { return this.view.roadSegments; }
  private get pointFrames() { return this.view.pointFrames; }
  private get frameBanks() { return this.view.frameBanks; }
  private get surfaceCoords() { return this.view.surfaceCoords; }
  private get segmentStarts() { return this.view.segmentStarts; }
  private get placements() { return this.view.placements; }

  /** Indices of the selected blocks (none, one or several). */
  selectedBlocks(): number[] {
    const s = this.selection;
    return s?.kind === "block" ? [s.index] : s?.kind === "blocks" ? s.indices : [];
  }

  /** Selects blocks: replaces the selection, or adds to, removes from or toggles in the selected blocks. */
  selectBlocks(indices: Iterable<number>, mode: "replace" | "add" | "remove" | "toggle" = "replace"): void {
    const set = new Set(mode === "replace" ? [] : this.selectedBlocks());
    for (const i of indices) {
      if (mode === "remove" || (mode === "toggle" && set.has(i))) set.delete(i);
      else set.add(i);
    }
    const list = [...set].sort((a, b) => a - b);
    this.select(list.length === 0 ? null : list.length === 1 ? { kind: "block", index: list[0] } : { kind: "blocks", indices: list });
  }

  /** Deletes the selected blocks (one undo step); returns whether there were any. */
  deleteSelectedBlocks(): boolean {
    const doomed = new Set(this.selectedBlocks());
    if (doomed.size === 0) return false;
    this.editBlocks((blocks) => {
      const keep = blocks.filter((_, i) => !doomed.has(i));
      blocks.length = 0;
      blocks.push(...keep);
    });
    this.select(null);
    return true;
  }

  select(selection: Selection): void {
    this.selection = selection;
    this.syncControls();
    this.syncBlocks();
    this.updateSelectOverlay();
    this.onChange();
  }

  /**
   * Applies an edit with undo support. `structural` = points added/removed. Consecutive edits with the
   * same `mergeKey` within a second (slider drags, repeated key presses) share one undo step.
   */
  edit(fn: (t: Track) => void, structural = false, mergeKey?: string): void {
    const now = performance.now();
    if (!mergeKey || mergeKey !== this.lastMerge.key || now - this.lastMerge.time > 1000)
      this.onBeforeEdit(structuredClone(this.map));
    this.lastMerge = { key: mergeKey, time: now };
    fn(this.track);
    syncSegments(this.track);
    if (!this.isSelectionValid()) this.selection = null;
    this.changed(structural);
  }

  /** Insert mode: the next click on the road inserts a point there (Esc or toggling again cancels). */
  setInsertMode(enabled: boolean): void {
    this.placeMode = null;
    if (enabled) this.setBlockPlaceMode(null);
    this.placeTarget = null;
    this.insertMode = enabled;
    this.insertTarget = null;
    this.insertMarker.visible = false;
    this.renderer.domElement.style.cursor = enabled ? "crosshair" : "";
    if (!enabled) this.label.hidden = true;
    this.onChange();
  }

  /** Place mode: the next click on the road places an object of this kind (null, Esc or the same kind again cancels). */
  setPlaceMode(kind: TrackObjectKind | null): void {
    if (kind) this.setInsertMode(false);
    if (kind) this.setBlockPlaceMode(null);
    this.placeMode = kind;
    this.placeTarget = null;
    this.insertMarker.visible = false;
    this.renderer.domElement.style.cursor = kind ? "crosshair" : "";
    if (!kind) this.label.hidden = true;
    this.onChange();
  }

  /**
   * Block mode: clicks place the named vanilla block on the grid, at the cell under the cursor on the current level
   * (null or Esc ends it). Keeps the direction and level of the previous block mode.
   */
  setBlockPlaceMode(name: string | null, like?: { free: boolean; dir?: number; yaw?: number; color?: ItemColor }): void {
    if (name) {
      this.placeMode = null;
      this.placeTarget = null;
      this.insertMode = false;
      this.insertTarget = null;
      this.insertMarker.visible = false;
    }
    // Keeps direction, level and grid/free of the last block mode (or takes them from `like`).
    if (this.blockPlace) this.lastBlockPlace = this.blockPlace;
    const keep = this.lastBlockPlace;
    this.blockPlace = name ? {
      name, dir: like?.dir ?? keep?.dir ?? 0, level: keep?.level ?? 0,
      free: !this.library?.isPillar(name) && (like?.free ?? keep?.free ?? false),
      yaw: like?.yaw ?? keep?.yaw ?? 0, lift: keep?.lift ?? 0, color: like?.color ?? keep?.color ?? "Default",
    } : null;
    this.blockTarget = null;
    this.blocks.setGhost(null);
    this.renderer.domElement.style.cursor = name ? "crosshair" : "";
    if (!name) this.label.hidden = true;
    this.onChange();
  }

  /** R: turns the selected block (or the one being placed) a quarter clockwise; Shift+R the other way. */
  rotateBlock(step: number): boolean {
    const place = this.blockPlace;
    if (place) {
      if (place.free) place.yaw = wrapBank(place.yaw - step * FREE_YAW_STEP);
      else place.dir = (((place.dir + step) % 4) + 4) % 4;
      this.refreshBlockPreview();
      this.onChange();
      return true;
    }
    const s = this.selection;
    if (s?.kind !== "block") return false;
    // Clockwise seen from above, like the grid directions (yaw is counter-clockwise).
    this.editBlocks((blocks) => {
      const b = blocks[s.index];
      if (b.free) b.yaw = wrapBank((b.yaw ?? 0) - step * FREE_YAW_STEP);
      else b.dir = (((b.dir + step) % 4) + 4) % 4;
    }, `blockDir:${s.index}`);
    return true;
  }

  /** PgUp/PgDn: one level (8 m) up or down for the selected blocks or the one being placed. */
  raiseBlock(step: number): boolean {
    const place = this.blockPlace;
    if (place) {
      if (place.free) place.lift = clamp(place.lift + step * this.freeLiftStep, -BLOCK_LEVEL, MAX_BLOCK_LEVEL * BLOCK_LEVEL);
      else place.level = clamp(place.level + step, 0, MAX_BLOCK_LEVEL);
      this.refreshBlockPreview();
      this.onChange();
      return true;
    }
    const selected = this.selectedBlocks();
    if (selected.length === 0) return false;
    this.editBlocks((blocks) => {
      for (const i of selected) {
        const b = blocks[i];
        if (b.free && b.position) b.position = [b.position[0], clamp(b.position[1] + step * this.freeLiftStep, Y_MIN, MAX_BLOCK_LEVEL * BLOCK_LEVEL), b.position[2]];
        else b.y = clamp(b.y + step, 0, MAX_BLOCK_LEVEL);
      }
    }, `blockLevel:${selected.join(",")}`);
    return true;
  }

  /** Height step of PgUp/PgDn for free blocks: the vertical snap step, or 1 m without snapping. */
  private get freeLiftStep(): number {
    return this.snapStep > 0 ? this.snapVertical : 1;
  }

  get snapVertical(): number {
    return Math.min(this.snapStep, BLOCK_LEVEL);
  }

  /** Snap mode on (step in meters) or off (0). Shows a finer ground grid for steps below a block cell. */
  setSnap(step: number): void {
    this.snapStep = Math.max(0, step);
    if (this.snapGrid) {
      this.scene.remove(this.snapGrid);
      this.snapGrid.geometry.dispose();
      this.snapGrid = null;
    }
    if (this.snapStep > 0 && this.snapStep < BLOCK_CELL) {
      const size = MAP_HALF_EXTENT * 2;
      this.snapGrid = new THREE.GridHelper(size, Math.round(size / this.snapStep), 0x77a85a, 0x77a85a);
      const m = this.snapGrid.material as THREE.Material;
      m.transparent = true;
      m.opacity = 0.35;
      this.snapGrid.position.y = 0.01;
      this.scene.add(this.snapGrid);
    }
    this.refreshBlockPreview();
    this.onChange();
  }

  /** A position snapped to the grid (snap mode), or as is. */
  private snapped(v: THREE.Vector3): THREE.Vector3 {
    const h = this.snapStep, y = this.snapVertical;
    if (h <= 0) return v;
    return v.set(Math.round(v.x / h) * h, Math.round(v.y / y) * y, Math.round(v.z / h) * h);
  }

  /**
   * A track point snapped to the grid (snap mode): on the grid horizontally, and at the heights of the game's block roads
   * and platforms (2 m above each level, then every vertical step), so the road meets blocks flush.
   */
  private snappedPoint(v: THREE.Vector3): THREE.Vector3 {
    this.snapped(v);
    if (this.snapStep > 0) {
      const y = this.snapVertical;
      v.y = Math.round((v.y - ROAD_SURFACE) / y) * y + ROAD_SURFACE;
    }
    return v;
  }

  /** A handle (relative to its point) snapped to the grid steps (snap mode), or as is. */
  private snappedHandle(h: Vec3): Vec3 {
    const s = this.snapStep, y = this.snapVertical;
    if (s <= 0) return h;
    return [Math.round(h[0] / s) * s, Math.round(h[1] / y) * y, Math.round(h[2] / s) * s];
  }

  /** Tab: switches block mode between grid and free placement, or turns the selected block into the other kind in place. */
  toggleFreeBlock(): boolean {
    const place = this.blockPlace;
    // Pillars stay on the grid (how the game treats free pillar blocks is unknown).
    const s0 = this.selection;
    if (place && this.library?.isPillar(place.name)) return true;
    if (!place && s0?.kind === "block" && this.map.blocks[s0.index]?.pillar) return true;
    if (place) {
      place.free = !place.free;
      if (place.free) { place.yaw = -place.dir * 90; place.lift = 0; }
      else place.dir = (((Math.round(-place.yaw / 90)) % 4) + 4) % 4;
      this.refreshBlockPreview();
      this.onChange();
      return true;
    }
    const s = this.selection;
    if (s?.kind !== "block") return false;
    this.editBlocks((blocks) => (blocks[s.index] = this.convertBlock(blocks[s.index])));
    return true;
  }

  /** The same block as a free block (exactly where it is), or as a grid block (nearest cell and direction). */
  private convertBlock(b: VanillaBlock): VanillaBlock {
    if (!b.free) {
      const m = blockMatrix(b, blockSize(this.library, b));
      const origin = new THREE.Vector3().setFromMatrixPosition(m);
      return { ...b, free: true, position: [origin.x, origin.y, origin.z], yaw: wrapBank(-b.dir * 90), pitch: 0, roll: 0 };
    }
    const dir = (((Math.round(-(b.yaw ?? 0) / 90)) % 4) + 4) % 4;
    const grid: VanillaBlock = { name: b.name, x: 0, y: 0, z: 0, dir };
    // The cell under the free block's footprint corner nearest to its turned min corner.
    const [sx, , sz] = blockSize(this.library, grid);
    const rotation = freeRotation(b.yaw ?? 0);
    const [px, py, pz] = b.position ?? [0, 0, 0];
    const corners = [[0, 0], [sx, 0], [0, sz], [sx, sz]].map(([x, z]) => new THREE.Vector3(x * BLOCK_CELL, 0, z * BLOCK_CELL).applyMatrix4(rotation));
    const [fx, , fz] = this.blockFootprint(grid);
    grid.x = this.clampCell(Math.round((px + Math.min(...corners.map((c) => c.x))) / BLOCK_CELL), fx);
    grid.z = this.clampCell(Math.round((pz + Math.min(...corners.map((c) => c.z))) / BLOCK_CELL), fz);
    grid.y = clamp(Math.round(py / BLOCK_LEVEL), 0, MAX_BLOCK_LEVEL);
    return grid;
  }

  /** Edit of the map's blocks with undo (consecutive edits with the same merge key within a second share a step). */
  editBlocks(fn: (blocks: VanillaBlock[]) => void, mergeKey?: string): void {
    const now = performance.now();
    if (!mergeKey || mergeKey !== this.lastMerge.key || now - this.lastMerge.time > 1000)
      this.onBeforeEdit(structuredClone(this.map));
    this.lastMerge = { key: mergeKey, time: now };
    fn(this.map.blocks);
    if (!this.isSelectionValid()) this.selection = null;
    this.syncBlocks();
    this.onChange();
  }

  /** Footprint of a block in cells, turned by its direction (x, levels, z). */
  blockFootprint(b: VanillaBlock): [number, number, number] {
    const [sx, sy, sz] = blockSize(this.library, b);
    return b.dir % 2 === 0 ? [sx, sy, sz] : [sz, sy, sx];
  }

  /**
   * The tracks' Gate objects as the game's gate blocks, placed freely round the road where each sits: upright to the
   * surface in its middle, facing along the road (as the export places them: GateObjects.GameBlocks in the core).
   */
  private gateBlocks(): VanillaBlock[] {
    const blocks: VanillaBlock[] = [];
    for (const t of this.map.tracks) {
      const placements = this.views.get(t)?.placements ?? [];
      if (placements.length !== t.objects.length) continue;
      t.objects.forEach((o, i) => {
        const gate = o.kind === "Gate" ? GATES[o.gate ?? "Turbo"] : undefined;
        const look = gate && gateLook(o);
        // (A gate of our own is part of the road's preview mesh.)
        if (!gate?.block || !look) return;
        const p = placements[i];
        const up = new THREE.Vector3(...p.normal).normalize();
        const tangent = new THREE.Vector3(...p.tangent);
        // (A gate that faces backwards: the reactor boosts that press the car down.)
        const forward = tangent.addScaledVector(up, -tangent.dot(up)).normalize().multiplyScalar(gate?.back ? -1 : 1);
        const right = new THREE.Vector3().crossVectors(up, forward);
        const turn = new THREE.Matrix4().makeBasis(right, up, forward);
        const angles = new THREE.Euler().setFromRotationMatrix(turn, "YXZ");
        const deg = 180 / Math.PI;
        // A block with the point `inBlock` of it (metres from its origin corner) at `at`.
        const put = (name: string, at: THREE.Vector3, inBlock: THREE.Vector3) => {
          const origin = at.clone().sub(inBlock.applyMatrix4(turn));
          blocks.push({ name, x: 0, y: 0, z: 0, dir: 0, free: true, position: [origin.x, origin.y, origin.z], yaw: angles.y * deg, pitch: angles.x * deg, roll: angles.z * deg });
        };
        const at = new THREE.Vector3(...p.position);
        if (look === "ring") return put(gate.block, at, new THREE.Vector3(GATE_MIDDLE, clamp(o.height, 0, 32), GATE_MIDDLE));
        // The game's flat gate (it stands in the middle of its block, its sill 1 m over the block's floor): the
        // placement is the road's lowest place in the middle of its width, and the road runs just over the sill.
        put(gatePanel(o.gate ?? "Turbo"), at, new THREE.Vector3(GATE_MIDDLE, 1.05, GATE_MIDDLE));
      });
    }
    return blocks;
  }

  private syncBlocks(): void {
    this.syncGround();
    const h = this.drag?.pick ?? this.hover;
    const pillars = pillarColumns(this.map.blocks, this.library);
    // Clips (side walls, caps) show on faces without a neighbour, columns included.
    this.blocks.setNeighbours([...this.map.blocks, ...pillars]);
    this.blocks.sync(this.map.blocks, new Set(this.selectedBlocks()), h?.kind === "block" ? h.index : null);
    this.blocks.syncPillars([...pillars, ...this.gateBlocks()]);
    this.placeBlockAnchor();
    this.items.sync(this.map.items);
    this.syncTerrain();
  }

  /** The own terrain's preview mesh (see syncTerrain), and the grid it shows. */
  /** The land: a mesh per chunk (named by its place, "x,z"). */
  private terrainMesh: THREE.Group | null = null;
  private terrainMaterials: THREE.Material[] = [];
  /** The server's name for the land shown (dabs can be built into it there), with the roads it was built for; or null. */
  private terrainKey: { key: string; tracks: string } | null = null;
  private terrainShown: TerrainGrid | undefined;
  private terrainBuild = 0;
  /** The triangles of each chunk of the land shown (by its name); null while it is being built. */
  private terrainTriangles: Map<string, number> | null = null;
  /** The land's size has changed (or is being measured again). */
  onTerrainSize: () => void = () => {};

  /**
   * What the land shown adds to an exported map: its triangles, and bytes as the server estimates them
   * (MeshQualities.EstimateTerrainBytes: an item per chunk and 24.3 bytes per triangle). Null: no land, or being built.
   */
  terrainSize(): { triangles: number; bytes: number } | null {
    if (!this.terrainTriangles) return null;
    let triangles = 0, bytes = 0;
    for (const n of this.terrainTriangles.values()) if (n > 0) { triangles += n; bytes += 1480 + 24.3 * n; }
    return { triangles, bytes };
  }

  /**
   * Shows the map's own terrain as the export builds it (the core in the worker: grid chunks and the strips to the
   * roads' edges), with the hill material's texture. `force` rebuilds it for the same grid (the roads changed).
   */
  private syncTerrain(force = false): void {
    const grid = this.map.terrain;
    if (grid === this.terrainShown && !force) return;
    this.terrainShown = grid;
    const build = ++this.terrainBuild;
    this.terrainTriangles = null;
    this.onTerrainSize();
    if (!grid) { this.setTerrainMesh(null); this.setQuickTerrain(false); this.setBusy("terrain", null); return; }
    this.setBusy("terrain", "Meshing the terrain…");
    const tracks = JSON.stringify(this.map.tracks);
    void buildTerrain(this.map).then(({ chunks, key }) => {
      if (build !== this.terrainBuild) return;
      this.setBusy("terrain", null);
      const textured = (name: string, fallback: number) => {
        const m = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.95, side: THREE.DoubleSide });
        const texture = this.library?.textureOf(name);
        if (texture) {
          m.map = this.library!.textureImage(texture);
          m.map.wrapS = m.map.wrapT = THREE.RepeatWrapping;
        } else m.color.setHex(fallback);
        return m;
      };
      const materials = [
        textured(TERRAIN_MATERIALS[grid.surface], grid.surface === "Ice" ? 0xdfe8ee : grid.surface === "Dirt" ? 0xb08a5a : 0x5f9a45),
        textured(ROCK_MATERIALS[grid.surface], 0x8a8076),
      ];
      const group = new THREE.Group();
      for (const chunk of chunks) if (chunk.mesh.indices.length) group.add(this.chunkMesh(chunk, materials));
      this.setTerrainMesh(group, materials);
      this.updateSeamDebug();
      this.terrainKey = key ? { key, tracks } : null;
      this.terrainTriangles = new Map(chunks.map((c) => [`${c.x},${c.z}`, c.mesh.indices.length / 3]));
      this.onTerrainSize();
      // All of it as one mesh, for the trees.
      let vertices = 0, corners = 0;
      for (const c of chunks) { vertices += c.mesh.positions.length / 3; corners += c.mesh.indices.length; }
      const positions = new Float32Array(vertices * 3), indices = new Uint32Array(corners);
      let v = 0, i = 0;
      for (const c of chunks) {
        positions.set(c.mesh.positions, v * 3);
        for (let k = 0; k < c.mesh.indices.length; k++) indices[i++] = c.mesh.indices[k] + v;
        v += c.mesh.positions.length / 3;
      }
      this.settleTrees({ positions, indices });
    }).catch((err) => {
      if (build === this.terrainBuild) this.setBusy("terrain", null);
      console.error("Terrain preview failed", err);
    });
  }

  /** A chunk of the land as a mesh. */
  private chunkMesh(chunk: TerrainChunk, materials: THREE.Material[]): THREE.Mesh {
    const mesh: TerrainMesh = chunk.mesh;
    {
      // The game projects both materials' textures by position: from above for the land, from the side for the rock
      // (each rock triangle on its own vertices, projected along its steeper side).
      const triangles = mesh.indices.length / 3, land = triangles - mesh.rockTriangles;
      const positions: number[] = [], normals: number[] = [], uvs: number[] = [];
      for (let t = 0; t < triangles; t++) {
        const rock = t >= land;
        const [a, b, c] = [0, 1, 2].map((k) => mesh.indices[t * 3 + k]);
        const p = (i: number, k: number) => mesh.positions[i * 3 + k];
        const ux = (p(b, 1) - p(a, 1)) * (p(c, 2) - p(a, 2)) - (p(b, 2) - p(a, 2)) * (p(c, 1) - p(a, 1));
        const uz = (p(b, 0) - p(a, 0)) * (p(c, 1) - p(a, 1)) - (p(b, 1) - p(a, 1)) * (p(c, 0) - p(a, 0));
        for (const i of [a, b, c]) {
          positions.push(p(i, 0), p(i, 1), p(i, 2));
          normals.push(mesh.normals[i * 3], mesh.normals[i * 3 + 1], mesh.normals[i * 3 + 2]);
          if (!rock) uvs.push(p(i, 0) / 32, p(i, 2) / 32);
          else if (Math.abs(ux) > Math.abs(uz)) uvs.push(p(i, 2) / 32, p(i, 1) / 32);
          else uvs.push(p(i, 0) / 32, p(i, 1) / 32);
        }
      }
      const geo = new THREE.BufferGeometry();
      geo.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
      geo.setAttribute("normal", new THREE.Float32BufferAttribute(normals, 3));
      geo.setAttribute("uv", new THREE.Float32BufferAttribute(uvs, 2));
      geo.addGroup(0, land * 3, 0);
      geo.addGroup(land * 3, mesh.rockTriangles * 3, 1);
      const m = new THREE.Mesh(geo, materials);
      m.name = `${chunk.x},${chunk.z}`;
      this.wireOf(m);
      m.receiveShadow = true;
      m.castShadow = true;
      return m;
    }
  }

  /**
   * Stands the generated trees on the finished mesh (they were placed on the grid, before the land was fitted to the
   * roads), not below the stadium's ground; a tree where the mesh has no land (cut out at a road) goes. Not an undo step:
   * it only follows from the land.
   */
  private settleTrees(mesh: { positions: Float32Array; indices: Uint32Array }): void {
    if (mesh.indices.length === 0 || !this.map.items) return;
    const surface = meshHeightAt(mesh);
    let moved = false;
    const kept = this.map.items.filter((item) => {
      if (!item.scenery) return true;
      const [x, y, z] = item.position;
      const land = surface(x, z);
      if (land === null) { moved = true; return false; }
      const h = Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT ? Math.max(0, land) : land;
      if (Math.abs(h - y) >= 0.01) { item.position = [x, h, z]; moved = true; }
      return true;
    });
    if (!moved) return;
    if (kept.length) this.map.items = kept;
    else delete this.map.items;
    this.items.sync(this.map.items);
    this.onChange();
  }

  private setTerrainMesh(mesh: THREE.Group | null, materials: THREE.Material[] = []): void {
    if (this.terrainMesh) {
      this.scene.remove(this.terrainMesh);
      for (const m of this.terrainMesh.children as THREE.Mesh[]) m.geometry.dispose();
      for (const m of this.terrainMaterials) m.dispose();
    }
    this.terrainMesh = mesh;
    this.terrainMaterials = materials;
    this.terrainKey = null;
    this.clearDabMarks();
    if (mesh) this.scene.add(mesh);
    // Built from the painted land: the quick mesh is done, unless a stroke is on (then this one is already behind).
    if (mesh && this.stroke && !this.sculpting) mesh.visible = false;
    else this.setQuickTerrain(false);
  }

  // ---- terrain painting --------------------------------------------------------------------------

  /** The brush while painting the terrain (left drags paint), or null. */
  terrainPaint: TerrainPaint | null = null;
  private stroke: { pointerId: number; last: THREE.Vector3 | null; lastTime: number; timer: number; flat: number; x0: number; z0: number; x1: number; z1: number } | null = null;
  /** A brush held still dabs again this often, milliseconds. */
  private static readonly HOLD_DAB_MS = 120;
  private brushRing: THREE.LineLoop | null = null;
  /** While painting: the land's grid as a plain mesh, updated with every dab (the real mesh follows after a stroke). */
  private quickTerrain: THREE.Mesh | null = null;

  setTerrainPaint(paint: TerrainPaint | null): void {
    this.terrainPaint = paint;
    if (!paint && this.brushRing) this.brushRing.visible = false;
    this.renderer.domElement.style.cursor = paint ? "crosshair" : "";
  }

  /** Where the pointer's ray meets the land (the grid's triangles), or null. */
  private terrainHit(e: PointerEvent): THREE.Vector3 | null {
    const grid = this.map.terrain;
    if (!grid) return null;
    const ray = this.rayFrom(e).ray;
    const p = new THREE.Vector3();
    let prev = 0;
    for (let t = 0; t < 4000; t += 1) {
      ray.at(t, p);
      const h = terrainHeight(grid, p.x, p.z);
      if (h !== null && p.y <= h) {
        // Refine between the last step above and this one.
        let lo = prev, hi = t;
        for (let k = 0; k < 12; k++) {
          const mid = (lo + hi) / 2;
          ray.at(mid, p);
          const hm = terrainHeight(grid, p.x, p.z);
          if (hm !== null && p.y <= hm) hi = mid; else lo = mid;
        }
        ray.at(hi, p);
        return p.clone();
      }
      prev = t;
    }
    return null;
  }

  /**
   * Clears the roads: what was painted into their space ("Into the roads") is cut by them again. The paint itself
   * stays, beside and over the roads. One undo step. False when nothing was painted into the roads.
   */
  clearRoads(): boolean {
    const grid = this.map.terrain;
    if (!grid?.editsOver?.length && !grid?.smoothsOver?.length) return false;
    this.onBeforeEdit(structuredClone(this.map));
    this.lastMerge = { time: 0 };
    grid.edits = [...(grid.edits ?? []), ...(grid.editsOver ?? [])];
    grid.editsOver = [];
    grid.smooths = [...(grid.smooths ?? []), ...(grid.smoothsOver ?? [])];
    grid.smoothsOver = [];
    this.onChange();
    this.syncTerrain(true);
    return true;
  }

  /** Whether the brush works on the solid (raise, lower, smooth) and not on the height map. */
  private get sculpting(): boolean {
    return this.terrainPaint?.brush === "raise" || this.terrainPaint?.brush === "lower" || this.terrainPaint?.brush === "smooth";
  }

  /** Where the brush is: on the land's mesh for the solid's brushes (walls and roofs too), else on the height map. */
  private brushHit(e: PointerEvent): THREE.Vector3 | null {
    if (!this.sculpting || !this.terrainMesh?.visible) return this.terrainHit(e);
    const hit = this.rayFrom(e).intersectObject(this.terrainMesh, true)[0];
    return hit ? hit.point.clone() : null;
  }

  /** Dabs not yet built into the land shown (sent to the server one batch at a time), and whether a batch is on its way. */
  private pendingDabs: { dabs: number[]; over: boolean; smooth: boolean } | null = null;
  private dabbing = false;
  /** Whether the stroke's dabs all reached the land shown: else the whole is built again when the stroke ends. */
  private liveDabs = false;

  /** Builds the waiting dabs into the land shown: the server builds the chunks they reach again, and those are swapped in. */
  private flushDabs(): void {
    const key = this.terrainKey, pending = this.pendingDabs, group = this.terrainMesh;
    if (this.dabbing || !pending) return;
    if (!key || !group || !this.liveDabs) { this.pendingDabs = null; this.liveDabs = false; return; }
    this.pendingDabs = null;
    this.dabbing = true;
    const build = this.terrainBuild;
    dabTerrain(key.key, pending.dabs, pending.over, pending.smooth).then((chunks) => {
      this.dabbing = false;
      // Another land since (an undo, Generate), or the server has lost this one: the whole is built again.
      if (build !== this.terrainBuild || this.terrainMesh !== group) return;
      if (!chunks) { this.liveDabs = false; this.pendingDabs = null; if (!this.stroke) this.syncTerrain(true); return; }
      for (const chunk of chunks) {
        const old = group.getObjectByName(`${chunk.x},${chunk.z}`) as THREE.Mesh | undefined;
        if (old) { group.remove(old); old.geometry.dispose(); }
        if (chunk.mesh.indices.length) group.add(this.chunkMesh(chunk, this.terrainMaterials));
        this.terrainTriangles?.set(`${chunk.x},${chunk.z}`, chunk.mesh.indices.length / 3);
      }
      this.onTerrainSize();
      if (this.pendingDabs) this.flushDabs();
      else this.clearDabMarks();
    }).catch(() => { this.dabbing = false; this.liveDabs = false; if (!this.stroke) this.syncTerrain(true); });
  }

  /** The dabs of the stroke so far, shown until the land's new mesh is there. */
  private dabMarks: THREE.Group | null = null;

  private clearDabMarks(): void {
    if (!this.dabMarks) return;
    this.scene.remove(this.dabMarks);
    for (const m of this.dabMarks.children as THREE.Mesh[]) { m.geometry.dispose(); (m.material as THREE.Material).dispose(); }
    this.dabMarks = null;
  }

  private showBrush(at: THREE.Vector3 | null): void {
    const paint = this.terrainPaint, grid = this.map.terrain;
    if (!this.brushRing) {
      this.brushRing = new THREE.LineLoop(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({ color: 0xffe066, depthTest: false }));
      this.brushRing.renderOrder = 11;
      this.scene.add(this.brushRing);
    }
    if (!paint || !grid || !at) { this.brushRing.visible = false; return; }
    const points: number[] = [];
    for (let k = 0; k < 64; k++) {
      const a = (k / 64) * Math.PI * 2;
      const x = at.x + Math.cos(a) * paint.radius, z = at.z + Math.sin(a) * paint.radius;
      points.push(x, this.sculpting ? at.y : (terrainHeight(grid, x, z) ?? at.y) + 0.3, z);
    }
    this.brushRing.geometry.dispose();
    this.brushRing.geometry = new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(points, 3));
    this.brushRing.visible = true;
  }

  private startStroke(e: PointerEvent): void {
    const at = this.brushHit(e);
    if (!at || !this.map.terrain) return;
    this.onBeforeEdit(structuredClone(this.map));
    this.lastMerge = { time: 0 };
    // Held still, the brush keeps painting where it is (moving it dabs by the distance covered, as before).
    const timer = window.setInterval(() => {
      const s = this.stroke;
      if (s?.last && performance.now() - s.lastTime >= Editor.HOLD_DAB_MS) { this.dab(s.last); this.showBrush(s.last); }
    }, Editor.HOLD_DAB_MS / 2);
    this.stroke = { pointerId: e.pointerId, last: null, lastTime: 0, timer, flat: at.y, x0: at.x, z0: at.z, x1: at.x, z1: at.z };
    this.orbit.enabled = false;
    try { this.renderer.domElement.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
    if (!this.sculpting) this.setQuickTerrain(true);
    // The solid's brushes build each dab into the land shown, when the server has it as it is here (the same roads).
    this.liveDabs = this.sculpting && !!this.terrainKey && this.terrainKey.tracks === JSON.stringify(this.map.tracks);
    this.dab(at);
  }

  private continueStroke(e: PointerEvent): void {
    const at = this.brushHit(e);
    this.showBrush(at);
    const stroke = this.stroke, paint = this.terrainPaint;
    if (!at || !stroke || !paint) return;
    if (stroke.last && stroke.last.distanceTo(at) < Math.max(1, paint.radius / 4)) return;
    this.dab(at);
  }

  private endStroke(): void {
    const stroke = this.stroke, grid = this.map.terrain;
    if (!stroke) return;
    try { this.renderer.domElement.releasePointerCapture(stroke.pointerId); } catch { /* not captured */ }
    window.clearInterval(stroke.timer);
    this.stroke = null;
    this.orbit.enabled = true;
    // The trees in the painted area stand on the new land.
    const r = (this.terrainPaint?.radius ?? 0) + 1;
    if (grid && !this.sculpting)
      for (const item of this.map.items ?? []) {
        const [x, , z] = item.position;
        if (!item.scenery || x < stroke.x0 - r || x > stroke.x1 + r || z < stroke.z0 - r || z > stroke.z1 + r) continue;
        const h = terrainHeight(grid, x, z);
        if (h !== null) item.position = [x, h, z];
      }
    this.items.sync(this.map.items);
    this.onChange();
    // The solid's dabs are in the land shown already (or on their way); else the whole is built again.
    if (this.sculpting && this.liveDabs) this.terrainShown = this.map.terrain;
    else this.syncTerrain(true);
  }

  /** One dab of the brush at a point on the land. */
  private dab(at: THREE.Vector3): void {
    const paint = this.terrainPaint, grid = this.map.terrain, stroke = this.stroke;
    if (!paint || !grid || !stroke) return;
    stroke.last = at.clone();
    stroke.lastTime = performance.now();
    stroke.x0 = Math.min(stroke.x0, at.x); stroke.x1 = Math.max(stroke.x1, at.x);
    stroke.z0 = Math.min(stroke.z0, at.z); stroke.z1 = Math.max(stroke.z1, at.z);
    if (paint.brush === "raise" || paint.brush === "lower" || paint.brush === "smooth") {
      // The solid's brushes: a dab is kept with the land (the mesh follows when the stroke ends). One on the spot of
      // the last (the brush held still) adds to it. A smoothing dab's amount is how much it blurs.
      const smooth = paint.brush === "smooth";
      const amount = smooth ? paint.strength : (paint.brush === "raise" ? 1 : -1) * paint.strength * RAISE_PER_STRENGTH;
      const edits = smooth ? (paint.uncut ? (grid.smoothsOver ??= []) : (grid.smooths ??= [])) : paint.uncut ? (grid.editsOver ??= []) : (grid.edits ??= []);
      const n = edits.length;
      if (this.liveDabs) {
        const over = !!paint.uncut;
        if (this.pendingDabs && this.pendingDabs.over === over && this.pendingDabs.smooth === smooth) this.pendingDabs.dabs.push(at.x, at.y, at.z, paint.radius, amount);
        else this.pendingDabs = { dabs: [at.x, at.y, at.z, paint.radius, amount], over, smooth };
        this.flushDabs();
      }
      if (n >= 5 && edits[n - 2] === paint.radius && Math.hypot(edits[n - 5] - at.x, edits[n - 4] - at.y, edits[n - 3] - at.z) < 0.01) edits[n - 1] += amount;
      else {
        edits.push(at.x, at.y, at.z, paint.radius, amount);
        if (!this.dabMarks) { this.dabMarks = new THREE.Group(); this.scene.add(this.dabMarks); }
        const mark = new THREE.Mesh(new THREE.SphereGeometry(Math.max(1, paint.radius / 6), 12, 8),
          new THREE.MeshBasicMaterial({ color: paint.brush === "raise" ? 0xffe066 : paint.brush === "lower" ? 0x66b3ff : 0x7be08a, transparent: true, opacity: 0.5, depthTest: false }));
        mark.position.copy(at);
        mark.renderOrder = 10;
        this.dabMarks.add(mark);
      }
      return;
    }
    const { cell, x0, z0, columns, rows } = grid;
    const c0 = Math.max(0, Math.floor((at.x - paint.radius - x0) / cell)), c1 = Math.min(columns - 1, Math.ceil((at.x + paint.radius - x0) / cell));
    const r0 = Math.max(0, Math.floor((at.z - paint.radius - z0) / cell)), r1 = Math.min(rows - 1, Math.ceil((at.z + paint.radius - z0) / cell));
    const before = grid.heights.slice();
    // (With water, heights below 0 are the land under it; in Stadium land that low is sunk under the ground.)
    const water = !!this.map.environment;
    const land = (h: number | null) => (h === null ? null : !water && h <= SUNK_LAND + 0.01 ? 0 : h);
    const rate = Math.min(1, Math.max(0, paint.strength));
    for (let r = r0; r <= r1; r++)
      for (let c = c0; c <= c1; c++) {
        const k = r * columns + c;
        const h = land(before[k]);
        if (h === null) continue;
        const x = x0 + c * cell, z = z0 + r * cell;
        const d = Math.hypot(x - at.x, z - at.z);
        if (d > paint.radius) continue;
        const w = 0.5 * (1 + Math.cos(Math.PI * d / paint.radius));
        let next = h;
        if (paint.brush === "flatten") next = h + (stroke.flat - h) * w * rate;
        else {
          let sum = 0, n = 0;
          for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
            const cc = c + dc, rr = r + dr;
            if (cc < 0 || rr < 0 || cc >= columns || rr >= rows) continue;
            const v = land(before[rr * columns + cc]);
            if (v !== null) { sum += v; n++; }
          }
          next = h + (sum / n - h) * w * rate;
        }
        next = Math.max(water ? -64 : -16, Math.min(1990, next));
        // On the stadium's ground, land below it sinks under it (as generated).
        const inside = Math.abs(x) < MAP_HALF_EXTENT && Math.abs(z) < MAP_HALF_EXTENT;
        grid.heights[k] = !water && inside && next < 0.5 ? SUNK_LAND : next;
      }
    this.updateQuickTerrain(c0, r0, c1, r1);
  }

  /** Shows (or drops) the quick mesh of the land's grid; the real mesh hides while it shows. */
  private setQuickTerrain(on: boolean): void {
    const grid = this.map.terrain;
    if (!on || !grid) {
      if (this.quickTerrain) {
        this.scene.remove(this.quickTerrain);
        this.quickTerrain.geometry.dispose();
        (this.quickTerrain.material as THREE.Material).dispose();
        this.quickTerrain = null;
      }
      if (this.terrainMesh) this.terrainMesh.visible = true;
      return;
    }
    if (this.terrainMesh) this.terrainMesh.visible = false;
    if (this.quickTerrain) return;
    const { cell, x0, z0, columns, rows, heights } = grid;
    const positions = new Float32Array(columns * rows * 3), uvs = new Float32Array(columns * rows * 2);
    for (let r = 0; r < rows; r++)
      for (let c = 0; c < columns; c++) {
        const k = r * columns + c, x = x0 + c * cell, z = z0 + r * cell;
        positions.set([x, heights[k] ?? 0, z], k * 3);
        uvs.set([x / 32, z / 32], k * 2);
      }
    const index: number[] = [];
    for (let r = 0; r + 1 < rows; r++)
      for (let c = 0; c + 1 < columns; c++) {
        const a = r * columns + c, b = a + 1, d = a + columns, e = d + 1;
        if (heights[a] === null || heights[b] === null || heights[d] === null || heights[e] === null) continue;
        index.push(a, d, b, b, d, e);
      }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    geo.setAttribute("uv", new THREE.BufferAttribute(uvs, 2));
    geo.setIndex(index);
    geo.computeVertexNormals();
    const material = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.95, side: THREE.DoubleSide });
    const texture = this.library?.textureOf(TERRAIN_MATERIALS[grid.surface]);
    if (texture) {
      material.map = this.library!.textureImage(texture);
      material.map.wrapS = material.map.wrapT = THREE.RepeatWrapping;
    } else material.color.setHex(0x5f9a45);
    this.quickTerrain = new THREE.Mesh(geo, material);
    this.quickTerrain.receiveShadow = true;
    this.scene.add(this.quickTerrain);
  }

  private updateQuickTerrain(c0: number, r0: number, c1: number, r1: number): void {
    const grid = this.map.terrain, mesh = this.quickTerrain;
    if (!grid || !mesh) return;
    const pos = mesh.geometry.getAttribute("position") as THREE.BufferAttribute;
    for (let r = r0; r <= r1; r++)
      for (let c = c0; c <= c1; c++) {
        const k = r * grid.columns + c;
        pos.setY(k, grid.heights[k] ?? 0);
      }
    pos.needsUpdate = true;
    mesh.geometry.computeVertexNormals();
  }

  /**
   * The map as it is exported: with the columns under air blocks as pillar blocks (computed here, from the local block
   * library; without one there are no columns). Ground blocks with a "column below" version get the game's flag for it,
   * as the preview shows them (a slope's pillar sides; otherwise the game leaves them open, floating).
   */
  exportMap(): TrackMap {
    const lib = this.library;
    const blocks = this.map.blocks.map((b) =>
      !b.free && !b.pillar && b.y === 0 && lib?.hasGroundAdapter(b.name) ? { ...b, extraFlags: GROUND_ADAPTER_FLAG } : b);
    return { ...this.map, blocks: [...blocks, ...pillarColumns(this.map.blocks, lib)] };
  }

  /**
   * Replaces the generated scenery (blocks and trees) with a new one round the tracks and the other blocks (one undo
   * step). Returns how many blocks and items it placed, or null when the block library lacks the theme's pieces. When it
   * placed them, the map keeps `saved` (the panel's settings) in that same step.
   */
  async generateScenery(options: SceneryLandOptions, saved: Record<string, unknown>): Promise<{ blocks: number; items: number } | null> {
    this.setBusy("generate", options.mode === "terrain" ? "Building the height map…" : "Placing the scenery…");
    try {
      await nextFrame();
      return await this.generateSceneryNow(options, saved);
    } finally {
      this.setBusy("generate", null);
    }
  }

  private async generateSceneryNow(options: SceneryLandOptions, saved: Record<string, unknown>): Promise<{ blocks: number; items: number } | null> {
    const lib = this.library;
    if (!lib) return null;
    if (options.mode === "terrain") {
      // Own terrain: a mesh of our own instead of land blocks (trees on it), replacing the generated scenery.
      const meshes = this.map.tracks.map((t) => this.views.get(t)?.road).filter((r): r is THREE.Mesh => !!r);
      const roads = this.map.tracks.flatMap((t) => { const v = this.views.get(t); return v?.road ? [{ mesh: v.road, surface: v.surfaceCoords }] : []; });
      const grid = buildTerrainGrid(roads, options, options.ownTerrain, !!this.map.environment);
      if (!grid) return null;
      const trees = plantOnTerrain(grid, options, roadFootprint(meshes));
      this.lastHeightMap = null;
      this.drawHeightMap();
      this.editBlocks((blocks) => {
        const keep = blocks.filter((b) => !b.scenery);
        blocks.length = 0;
        blocks.push(...keep);
        const items = [...(this.map.items ?? []).filter((i) => !i.scenery), ...trees];
        if (items.length) this.map.items = items;
        else delete this.map.items;
        this.map.terrain = grid;
        this.map.sceneryOptions = saved;
      });
      return { blocks: 0, items: trees.length };
    }
    // The land pieces' meshes, so trees can stand on slopes at their real surface height.
    const pieceMeshes = new Map<string, THREE.BufferGeometry>();
    await Promise.all([...new Set([...landPieces(options.theme), ...terrainPieces(options.theme)])].flatMap((name) => [true, false].map(async (ground) => {
      const file = lib.variant(name, ground)?.mesh?.file;
      if (file && !pieceMeshes.has(file)) pieceMeshes.set(file, await lib.geometry(file));
    })));
    const ray = new THREE.Raycaster();
    const probe = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide });
    const surface = (b: VanillaBlock, x: number, z: number): number | null => {
      const file = lib.variant(b.name, !b.free && b.y === 0)?.mesh?.file;
      const geo = file ? pieceMeshes.get(file) : undefined;
      if (!geo) return null;
      const mesh = new THREE.Mesh(geo, probe);
      mesh.matrixAutoUpdate = false;
      mesh.matrix.copy(blockMatrix(b, blockSize(lib, b)));
      mesh.updateMatrixWorld(true);
      ray.set(new THREE.Vector3(x, Y_MAX + 100, z), new THREE.Vector3(0, -1, 0));
      return ray.intersectObject(mesh, false)[0]?.point.y ?? null;
    };
    const own = this.map.blocks.filter((b) => !b.scenery);
    const meshes = this.map.tracks.map((t) => this.views.get(t)?.road).filter((r): r is THREE.Mesh => !!r);
    const roads = roadColumns(meshes);
    const footprint = roadFootprint(meshes);
    // The blocks themselves, not their columns: land may rise under an air block (its columns then stop on it).
    const occupied = occupiedCells(own, lib);
    const input = { roads, footprint, occupied, surface, roadsFine: roadColumns(meshes, BLOCK_CELL / 2) };
    const terrain = options.mode === "nestle"
      ? generateTerrain(lib, input, options, { look: options.look ?? "alone", clearance: 0.5, far: options.far ?? "fit" }) : null;
    const generated = options.mode === "nestle" ? terrain : generateScenery(lib, input, options);
    if (!generated) return null;
    this.lastHeightMap = terrain?.debug ?? null;
    this.drawHeightMap();
    this.editBlocks((blocks) => {
      const keep = blocks.filter((b) => !b.scenery);
      blocks.length = 0;
      blocks.push(...keep, ...generated.blocks);
      const items = [...(this.map.items ?? []).filter((i) => !i.scenery), ...generated.items];
      if (items.length) this.map.items = items;
      else delete this.map.items;
      delete this.map.terrain;
      this.map.sceneryOptions = saved;
    });
    return { blocks: generated.blocks.length, items: generated.items.length };
  }

  /** Shows or hides the last terrain height map (corners as dots, cells as a grid; see drawHeightMap). */
  showHeightMap(on: boolean): void {
    this.heightMapShown = on;
    this.drawHeightMap();
  }

  get hasHeightMap(): boolean { return !!this.lastHeightMap; }

  /**
   * The height map's corners at their final heights (a level's top, 2 m above its floor) joined by grid lines. Dots:
   * blue where a road or block holds the corner down, green where it got its wish, orange where it was lowered for a
   * piece to fit, with a red line up to the wished height.
   */
  private drawHeightMap(): void {
    for (const child of this.heightMap.children) {
      const o = child as THREE.Points | THREE.LineSegments;
      o.geometry.dispose();
      (o.material as THREE.Material).dispose();
    }
    this.heightMap.clear();
    const map = this.lastHeightMap;
    if (!map || !this.heightMapShown) { return; }
    const y = (level: number) => level * BLOCK_LEVEL + 2 + 0.4;
    const dots: number[] = [], colors: number[] = [], grid: number[] = [], wish: number[] = [];
    const c = new THREE.Color();
    for (const [k, level] of map.final) {
      const [i, j] = k.split(",").map(Number);
      const x = i * BLOCK_CELL, z = j * BLOCK_CELL;
      dots.push(x, y(level), z);
      const wanted = map.target.get(k) ?? level;
      c.setHex(map.capped.has(k) && wanted === level ? 0x3080ff : wanted > level ? 0xff9020 : 0x40d040);
      colors.push(c.r, c.g, c.b);
      if (wanted > level) wish.push(x, y(level), z, x, y(wanted), z);
      for (const [di, dj] of [[1, 0], [0, 1]]) {
        const n = map.final.get(`${i + di},${j + dj}`);
        if (n !== undefined) grid.push(x, y(level), z, (i + di) * BLOCK_CELL, y(n), (j + dj) * BLOCK_CELL);
      }
    }
    const pointGeo = new THREE.BufferGeometry();
    pointGeo.setAttribute("position", new THREE.Float32BufferAttribute(dots, 3));
    pointGeo.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
    const points = new THREE.Points(pointGeo, new THREE.PointsMaterial({ size: 7, sizeAttenuation: false, vertexColors: true, depthTest: false }));
    points.renderOrder = 10;
    const lines = (data: number[], color: number) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute("position", new THREE.Float32BufferAttribute(data, 3));
      const l = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color, depthTest: false, transparent: true, opacity: 0.8 }));
      l.renderOrder = 9;
      return l;
    };
    // Cells with their own corners (steps): their outline at those heights, a little inside the cell, in purple.
    const steps: number[] = [];
    for (const [k, hs] of map.stepped) {
      const [i, j] = k.split(",").map(Number);
      const at = (n: number) => {
        const [dx, dz] = [[0, 0], [1, 0], [0, 1], [1, 1]][n];
        return [i * BLOCK_CELL + (dx ? BLOCK_CELL - 3 : 3), y(hs[n]), j * BLOCK_CELL + (dz ? BLOCK_CELL - 3 : 3)];
      };
      for (const [a, b] of [[0, 1], [1, 3], [3, 2], [2, 0]]) steps.push(...at(a), ...at(b));
    }
    this.heightMap.add(lines(grid, 0xffffff), lines(wish, 0xff3020), lines(steps, 0xc040ff), points);

  }

  /** Removes the generated scenery, blocks and trees (one undo step); returns how many there were. */
  removeScenery(): number {
    const count = this.map.blocks.filter((b) => b.scenery).length + (this.map.items ?? []).filter((i) => i.scenery).length
      + (this.map.terrain ? 1 : 0);
    if (count)
      this.editBlocks((blocks) => {
        const keep = blocks.filter((b) => !b.scenery);
        blocks.length = 0;
        blocks.push(...keep);
        const items = (this.map.items ?? []).filter((i) => !i.scenery);
        if (items.length) this.map.items = items;
        else delete this.map.items;
        delete this.map.terrain;
      });
    return count;
  }

  /** Road meshes of all tracks, each with a bounds tree for fast raycasts (built on first use after a rebuild). */
  private roadMeshes(): THREE.Mesh[] {
    const roads = this.map.tracks.map((t) => this.views.get(t)?.road).filter((r): r is THREE.Mesh => !!r);
    // Indirect: the triangle order must stay as built (face indices map to segments, groups to materials).
    for (const r of roads) if (!r.geometry.boundsTree) r.geometry.computeBoundsTree({ indirect: true });
    return roads;
  }

  /** Block under the cursor (the nearest hit). */
  private blockHit(ray: THREE.Ray): { index: number; distance: number; point: THREE.Vector3 } | null {
    const caster = new THREE.Raycaster(ray.origin, ray.direction, 0, 20000);
    caster.firstHitOnly = true;
    const hit = caster.intersectObjects(this.blocks.pickables, true)[0];
    const index = hit ? BlockLayer.indexOf(hit.object) : null;
    return hit && index !== null ? { index, distance: hit.distance, point: hit.point } : null;
  }

  private refreshBlockPreview(): void {
    if (this.lastPointer) this.updateBlockPreview(this.lastPointer);
  }

  /** Block mode: the cell under the cursor on the current level (the block's footprint centred on the cursor). */
  private updateBlockPreview(e: PointerEvent): void {
    const place = this.blockPlace;
    if (!place) return;
    if (place.free) {
      this.updateFreeBlockPreview(e, place);
      return;
    }
    const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -place.level * BLOCK_LEVEL);
    const hit = this.rayFrom(e).ray.intersectPlane(plane, new THREE.Vector3());
    if (!hit || hit.distanceTo(this.camera.position) > 6000) {
      this.blockTarget = null;
      this.blocks.setGhost(null);
      this.label.hidden = true;
      return;
    }
    const block: VanillaBlock = { name: place.name, x: 0, y: place.level, z: 0, dir: place.dir, ...(place.color !== "Default" ? { color: place.color } : {}) };
    const [fx, , fz] = this.blockFootprint(block);
    block.x = this.clampCell(Math.round(hit.x / BLOCK_CELL - fx / 2), fx);
    block.z = this.clampCell(Math.round(hit.z / BLOCK_CELL - fz / 2), fz);
    this.blockTarget = block;
    this.blocks.setGhost(block);
    this.showLabel(e, `${place.name} · ${DIRECTION_NAMES[place.dir]} · level ${place.level} — click to place, R rotate, PgUp/PgDn or Ctrl+wheel level, Tab free, Esc done`);
  }

  /**
   * Free block mode: the block stands on whatever is under the cursor (ground, road, other blocks), centred on it,
   * raised by PgUp/PgDn; in snap mode its origin snaps to the grid.
   */
  private updateFreeBlockPreview(e: PointerEvent, place: NonNullable<Editor["blockPlace"]>): void {
    const hit = this.sceneHit(this.rayFrom(e).ray);
    if (!hit || hit.distance > 6000) {
      this.blockTarget = null;
      this.blocks.setGhost(null);
      this.label.hidden = true;
      return;
    }
    const block: VanillaBlock = { name: place.name, x: 0, y: 0, z: 0, dir: 0, free: true, yaw: place.yaw, pitch: 0, roll: 0, ...(place.color !== "Default" ? { color: place.color } : {}) };
    const [sx, , sz] = blockSize(this.library, block);
    const center = new THREE.Vector3(sx * BLOCK_CELL / 2, 0, sz * BLOCK_CELL / 2).applyMatrix4(freeRotation(place.yaw));
    const origin = this.snapped(hit.point.clone().sub(center).add(new THREE.Vector3(0, place.lift, 0)));
    block.position = [clampMap(origin.x), clamp(origin.y, Y_MIN, Y_MAX), clampMap(origin.z)];
    this.blockTarget = block;
    this.blocks.setGhost(block);
    this.showLabel(e, `${place.name} · free · yaw ${place.yaw.toFixed(0)}° · height ${block.position[1].toFixed(1)} m — click to place, `
      + `R rotate ${FREE_YAW_STEP}°, PgUp/PgDn or Ctrl+wheel height, Tab grid, Esc done`);
  }

  /** Keeps a footprint of `size` cells inside the map. */
  private clampCell(cell: number, size: number): number {
    const half = MAP_HALF_EXTENT / BLOCK_CELL;
    return clamp(cell, -half, half - size);
  }

  /** Where an object currently is (from the last preview), if the preview has caught up with it. */
  placement(index: number): ObjectPlacement | undefined {
    return this.placements.length === this.track.objects.length ? this.placements[index] : undefined;
  }

  setAxisGizmo(enabled: boolean): void {
    this.axisGizmoEnabled = enabled;
    this.syncControls();
    this.onChange();
  }

  /** Shows the whole map (all tracks). */
  frameTrack(): void {
    const box = new THREE.Box3();
    for (const t of this.map.tracks) for (const p of t.points) box.expandByPoint(new THREE.Vector3(...p.position));
    // Blocks too: the corners of their boxes.
    for (const b of this.map.blocks) {
      const size = blockSize(this.library, b);
      const m = blockMatrix(b, size);
      box.expandByPoint(new THREE.Vector3().applyMatrix4(m));
      box.expandByPoint(new THREE.Vector3(size[0] * BLOCK_CELL, size[1] * BLOCK_LEVEL, size[2] * BLOCK_CELL).applyMatrix4(m));
    }
    const center = box.getCenter(new THREE.Vector3());
    const size = Math.max(64, box.getSize(new THREE.Vector3()).length());
    const dir = this.camera.position.clone().sub(this.orbit.target).normalize();
    this.orbit.target.copy(center);
    this.camera.position.copy(center).addScaledVector(dir, size * 0.9);
    this.orbit.update();
  }

  // ---- scene setup ---------------------------------------------------------------------------

  /** The environment the ground is drawn for (undefined: Stadium). */
  private groundFor: string | undefined;

  /** The ground follows the map's environment: grass on the stadium's grid, or water (see-through, the land goes on under it). */
  private syncGround(): void {
    if (this.groundFor === this.map.environment) return;
    this.groundFor = this.map.environment;
    this.scene.remove(this.ground);
    this.ground.traverse((o) => {
      const m = o as THREE.Mesh;
      m.geometry?.dispose();
      for (const material of [m.material].flat()) material?.dispose();
    });
    this.ground = this.createGround(this.map.environment);
    this.scene.add(this.ground);
  }

  private createGround(environment?: string): THREE.Mesh {
    const water = !!environment;
    // The other environments' maps are 64 cells wide, and water all over.
    const half = water ? 1024 : MAP_HALF_EXTENT;
    const size = half * 2;
    // The clickable ground reaches as far as tracks may go; beyond the grid it is darker: the game has no ground there.
    const ground = new THREE.Mesh(
      new THREE.PlaneGeometry(WORLD_HALF_EXTENT * 2 + 1024, WORLD_HALF_EXTENT * 2 + 1024),
      // Pushed back in depth: the grid's own ground lies right on top of it and must win at every distance
      // (1 cm apart, the two would flicker against each other far from the camera).
      water
        ? new THREE.MeshStandardMaterial({ color: 0x1f4f78, roughness: 0.6, transparent: true, opacity: 0.5, depthWrite: false, polygonOffset: true, polygonOffsetFactor: 4, polygonOffsetUnits: 16 })
        : new THREE.MeshStandardMaterial({ color: 0x46553f, roughness: 1, polygonOffset: true, polygonOffsetFactor: 4, polygonOffsetUnits: 16 }),
    );
    if (water) ground.renderOrder = 2;
    ground.rotation.x = -Math.PI / 2;
    // (The water lies a little under height 0, by the environment.)
    ground.position.y = water ? (WATER_SURFACE[environment!] ?? -0.85) - 0.01 : -0.05;
    const inside = new THREE.Mesh(new THREE.PlaneGeometry(size, size), water
      ? new THREE.MeshStandardMaterial({ color: 0x3f9bd6, roughness: 0.4, transparent: true, opacity: 0.45, depthWrite: false })
      : new THREE.MeshStandardMaterial({ color: 0x6f9e4f, roughness: 1 }));
    if (water) inside.renderOrder = 2;
    inside.position.z = 0.01;
    ground.add(inside);

    // 32 m block grid inside the map, plus a brighter map boundary.
    const grid = new THREE.GridHelper(size, size / 32, water ? 0x2f7fb8 : 0x5c8a40, water ? 0x2f7fb8 : 0x5c8a40);
    (grid.material as THREE.Material).transparent = true;
    (grid.material as THREE.Material).opacity = 0.6;
    grid.position.y = 0.02;
    ground.add(grid);
    grid.rotation.x = Math.PI / 2;

    const h = half;
    const border = new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(-h, -h, 0.1), new THREE.Vector3(h, -h, 0.1),
        new THREE.Vector3(h, h, 0.1), new THREE.Vector3(-h, h, 0.1),
      ]),
      new THREE.LineBasicMaterial({ color: 0xffffff }),
    );
    ground.add(border);
    // How far tracks, free blocks and items may go outside the grid.
    const w = WORLD_HALF_EXTENT;
    const limit = new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(-w, -w, 0.1), new THREE.Vector3(w, -w, 0.1),
        new THREE.Vector3(w, w, 0.1), new THREE.Vector3(-w, w, 0.1),
      ]),
      new THREE.LineBasicMaterial({ color: 0xc9a86a, transparent: true, opacity: 0.7 }),
    );
    ground.add(limit);
    return ground;
  }

  /** Transparent copy of one road segment's triangles, drawn on top for hover/selection feedback. */
  private createOverlay(color: number, opacity: number): THREE.Mesh {
    const mesh = new THREE.Mesh(
      new THREE.BufferGeometry(),
      new THREE.MeshBasicMaterial({
        color, transparent: true, opacity, depthWrite: false, side: THREE.DoubleSide,
        polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -4,
      }),
    );
    mesh.visible = false;
    mesh.renderOrder = 5;
    this.scene.add(mesh);
    return mesh;
  }

  private resize(): void {
    const w = this.container.clientWidth, h = this.container.clientHeight;
    this.renderer.setSize(w, h);
    this.camera.aspect = w / Math.max(1, h);
    this.camera.updateProjectionMatrix();
  }

  /** Camera keys: W/S along the view, A/D sideways, Space up, Shift down. */
  private onMoveKey(e: KeyboardEvent, down: boolean): void {
    const key = e.code === "Space" ? "space" : e.key === "Shift" ? "shift" : e.key.toLowerCase();
    if (!["w", "a", "s", "d", "space", "shift"].includes(key)) {
      // Shift used with another key (Shift+M, Shift+[) is a modifier, not "down".
      if (down) this.moveKeys.delete("shift");
      return;
    }
    if (!down) {
      this.moveKeys.delete(key);
      if (key === "space" && this.isCameraKeyTarget(e)) e.preventDefault();
      return;
    }
    if (!this.isCameraKeyTarget(e) || e.ctrlKey || e.metaKey || e.altKey) return;
    // Space would otherwise scroll the page or press the focused toolbar button.
    if (key === "space") e.preventDefault();
    this.moveKeys.add(key);
  }

  /** Camera keys only count when nobody is typing and no dialog is open. */
  private isCameraKeyTarget(e: KeyboardEvent): boolean {
    return !(e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement || document.querySelector("dialog[open]"));
  }

  /**
   * Flies camera and orbit target together. Speed follows the distance to what is in front of the camera (road or
   * ground), so it is slow close to the track and fast high above it, wherever the orbit target happens to be.
   */
  private applyKeyboardMovement(dt: number): void {
    if (this.moveKeys.size === 0) return;
    const forward = this.camera.getWorldDirection(new THREE.Vector3());
    const flat = forward.clone().setY(0);
    if (flat.lengthSq() < 1e-6) flat.set(0, 0, -1).applyQuaternion(this.camera.quaternion).setY(0);
    const right = new THREE.Vector3().crossVectors(flat.normalize(), new THREE.Vector3(0, 1, 0));
    const move = new THREE.Vector3();
    if (this.moveKeys.has("w")) move.add(forward);
    if (this.moveKeys.has("s")) move.sub(forward);
    if (this.moveKeys.has("d")) move.add(right);
    if (this.moveKeys.has("a")) move.sub(right);
    if (this.moveKeys.has("space")) move.y += 1;
    // Shift is also the "keep height" modifier while dragging a point.
    if (this.moveKeys.has("shift") && !this.drag) move.y -= 1;
    if (move.lengthSq() === 0) return;
    const speed = Math.max(10, this.distanceAhead() * 0.9);
    move.normalize().multiplyScalar(speed * Math.min(dt, 0.1));
    this.camera.position.add(move);
    // Orbit around what is ahead (road or ground), not around a center that stayed behind.
    this.setOrbitDepth(this.distanceAhead());
  }

  /** What is in the middle of the view (null: sky), refreshed at most every tenth of a second; raycasts are not free. */
  private aheadCache: { at: number; point: THREE.Vector3 | null } = { at: 0, point: null };

  /** Distance along the view to the road or ground in the middle of the view; over the sky the current orbit distance. */
  private distanceAhead(): number {
    const now = performance.now();
    // The camera may just have moved: rays and directions must use where it is now, not where it was at the last render.
    this.camera.updateMatrixWorld();
    if (now - this.aheadCache.at > 100) {
      this.raycaster.setFromCamera(new THREE.Vector2(0, 0), this.camera);
      this.aheadCache = { at: now, point: this.sceneHit(this.raycaster.ray)?.point ?? null };
    }
    const point = this.aheadCache.point;
    if (!point) return Math.max(20, this.camera.position.distanceTo(this.orbit.target));
    // Measured from the camera's current position, so the cached point stays right while flying towards it.
    return Math.max(1, this.camera.getWorldDirection(new THREE.Vector3()).dot(point.clone().sub(this.camera.position)));
  }

  /** Nearest road (of any track) or ground along a ray. */
  private sceneHit(ray: THREE.Ray): { point: THREE.Vector3; distance: number } | null {
    const caster = new THREE.Raycaster(ray.origin, ray.direction, 0, 20000);
    caster.firstHitOnly = true;
    const roads = this.roadMeshes();
    const hit = caster.intersectObjects([...roads, this.ground], false)[0];
    const block = this.blockHit(ray);
    if (block && (!hit || block.distance < hit.distance)) return { point: block.point, distance: block.distance };
    return hit ? { point: hit.point, distance: hit.distance } : null;
  }

  /**
   * Scroll wheel: zooms towards (or away from) what is under the cursor, by a share of the distance to it. Unlike
   * OrbitControls' zoom (a share of the distance to the orbit target) this never crawls just because the target is
   * close, e.g. after flying around with the keys.
   */
  private onWheel(e: WheelEvent): void {
    e.preventDefault();
    const lines = e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 33 : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? 400 : 1;
    const delta = e.deltaY * lines;
    if (delta === 0) return;
    // Ctrl+wheel: level of the block being placed or the selected block (wheel up = higher). Touchpads send many small
    // steps, so they add up to one level per notch-sized amount.
    if ((e.ctrlKey || e.metaKey) && (this.blockPlace || this.selectedBlocks().length > 0)) {
      // A mouse wheel notch (one event of 50+ px) is one level; touchpad deltas add up to the same amount.
      if (Math.abs(delta) >= WHEEL_LEVEL_STEP / 2) {
        this.wheelLevel = 0;
        this.raiseBlock(delta < 0 ? 1 : -1);
        return;
      }
      this.wheelLevel += delta;
      if (Math.abs(this.wheelLevel) >= WHEEL_LEVEL_STEP) {
        this.raiseBlock(this.wheelLevel < 0 ? 1 : -1);
        this.wheelLevel = 0;
      }
      return;
    }
    const ray = this.rayFrom(e as unknown as PointerEvent).ray;
    const hit = this.sceneHit(ray);
    // Over the sky: zoom towards a point at the orbit distance.
    const anchor = hit?.point ?? ray.at(Math.max(50, this.camera.position.distanceTo(this.orbit.target)), new THREE.Vector3());
    const share = Math.min(0.5, (Math.abs(delta) / 100) * 0.18);
    const camera = this.camera.position;
    if (delta < 0) {
      // Never closer than a metre to what is under the cursor.
      if (camera.distanceTo(anchor) * (1 - share) < 1) return;
      camera.lerp(anchor, share);
    } else {
      camera.sub(anchor).multiplyScalar(1 / (1 - share)).add(anchor);
    }
    // The orbit center goes on what is in the middle of the view (the view does not turn), so orbiting afterwards
    // turns around the area in front instead of a center left behind somewhere.
    this.aheadCache.at = 0;
    this.setOrbitDepth(this.distanceAhead());
  }

  /** Moves the orbit target along the view axis to the given distance in front of the camera (keeps the view as is). */
  private setOrbitDepth(depth: number): void {
    const forward = this.camera.getWorldDirection(new THREE.Vector3());
    this.orbit.target.copy(this.camera.position).addScaledVector(forward, Math.max(1, depth));
    this.orbit.update();
  }

  private render(): void {
    const now = performance.now();
    this.applyKeyboardMovement((now - this.lastFrameAt) / 1000);
    this.lastFrameAt = now;
    // Keep points, handles and object markers the same size on screen, regardless of zoom.
    for (const m of [...this.pointMeshes, ...this.handleMeshes, ...this.objectMeshes, ...this.otherPointMeshes]) {
      const distance = this.camera.position.distanceTo(m.position);
      const hovered = this.isHovered(m.userData as Pick);
      m.scale.setScalar(Math.max(0.6, distance * SCREEN_SIZE) * (hovered ? 1.4 : 1));
    }
    if (this.insertMarker.visible)
      this.insertMarker.scale.setScalar(Math.max(0.6, this.camera.position.distanceTo(this.insertMarker.position) * SCREEN_SIZE * 1.2));
    this.renderer.render(this.scene, this.camera);
  }

  // ---- control handles -----------------------------------------------------------------------

  /** Recreates point/handle/bank-ring meshes for the current track and selection. */
  private syncControls(): void {
    this.controls.clear();
    this.pointMeshes = [];
    this.otherPointMeshes = [];
    this.handleMeshes = [];
    this.handleLine = null;
    this.bankRing = null;
    this.bankPick = null;
    this.bankMarker = null;

    const pointGeo = new THREE.SphereGeometry(1, 16, 12);
    this.track.points.forEach((_, i) => {
      const mesh = new THREE.Mesh(pointGeo, new THREE.MeshBasicMaterial({ depthTest: false }));
      mesh.renderOrder = 10;
      mesh.userData = { kind: "point", index: i } satisfies Pick;
      this.controls.add(mesh);
      this.pointMeshes.push(mesh);
    });
    const otherMaterial = new THREE.MeshBasicMaterial({ color: COLORS.otherTrack, depthTest: false, transparent: true, opacity: 0.7 });
    this.map.tracks.forEach((t, track) => {
      if (track === this.active) return;
      t.points.forEach((p, i) => {
        const mesh = new THREE.Mesh(pointGeo, otherMaterial);
        mesh.renderOrder = 9;
        mesh.scale.setScalar(0.8);
        mesh.position.set(...p.position);
        mesh.userData = { kind: "point", index: i, track } satisfies Pick;
        this.controls.add(mesh);
        this.otherPointMeshes.push(mesh);
      });
    });

    const index = this.selectedPointIndex();
    if (index !== null) {
      const handleGeo = new THREE.SphereGeometry(0.7, 12, 8);
      for (const which of ["in", "out"] as const) {
        const mesh = new THREE.Mesh(handleGeo, new THREE.MeshBasicMaterial({ depthTest: false }));
        mesh.renderOrder = 10;
        mesh.userData = { kind: "handle", index, which } satisfies Pick;
        this.controls.add(mesh);
        this.handleMeshes.push(mesh);
      }
      this.handleLine = new THREE.LineSegments(
        new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(new Float32Array(12), 3)),
        new THREE.LineBasicMaterial({ color: COLORS.handle, depthTest: false }),
      );
      this.handleLine.renderOrder = 9;
      this.controls.add(this.handleLine);
      this.createBankRing(index);
    }

    this.updateControls();

    const s = this.selection;
    const target = !this.axisGizmoEnabled ? undefined
      : s?.kind === "point" ? this.pointMeshes[s.index]
      : s?.kind === "handle" ? this.handleMeshes.find((m) => m.userData.which === s.which)
      : this.selectedBlocks().length > 0 ? this.blockAnchor
      : undefined;
    this.placeBlockAnchor();
    if (target) this.axisGizmo.attach(target);
    else this.axisGizmo.detach();
  }

  /** A ring around the selected point, perpendicular to the road; dragging it changes the banking. */
  private createBankRing(index: number): void {
    const ring = new THREE.Group();
    ring.userData = { kind: "bank", index } satisfies Pick;
    const radius = this.ringRadius(index);

    const visible = new THREE.Mesh(
      new THREE.TorusGeometry(radius, 0.3, 8, 96),
      new THREE.MeshBasicMaterial({ color: COLORS.ring, depthTest: false, transparent: true, opacity: 0.85 }),
    );
    visible.renderOrder = 8;
    ring.add(visible);

    // A much fatter invisible torus makes the thin ring easy to grab.
    const pick = new THREE.Mesh(new THREE.TorusGeometry(radius, 2.2, 6, 64), new THREE.MeshBasicMaterial({ visible: false }));
    pick.userData = ring.userData;
    ring.add(pick);

    // Marker on the ring pointing to the road's "up" — shows the current banking at a glance.
    const marker = new THREE.Mesh(new THREE.SphereGeometry(1.1, 12, 8), new THREE.MeshBasicMaterial({ color: COLORS.ring, depthTest: false }));
    marker.renderOrder = 9;
    this.controls.add(marker);

    this.controls.add(ring);
    this.bankRing = ring;
    this.bankPick = pick;
    this.bankMarker = marker;
  }

  /**
   * Road frame at a spline point. Uses the core's exact frame from the last preview (correct even upside down at
   * the top of a loop), re-applying the current bank so the marker follows a bank drag before the rebuild lands.
   */
  private frameAt(index: number): { tangent: THREE.Vector3; up: THREE.Vector3 } {
    const f = this.pointFrames;
    const p = this.track.points[index];
    if (f.length !== this.track.points.length * 6 || this.frameBanks[index] === undefined) return pointFrame(p);
    const o = index * 6;
    const tangent = new THREE.Vector3(f[o], f[o + 1], f[o + 2]).normalize();
    const up = new THREE.Vector3(f[o + 3], f[o + 4], f[o + 5]).normalize();
    up.applyAxisAngle(tangent, ((p.bank - this.frameBanks[index]) * Math.PI) / 180);
    return { tangent, up };
  }

  private ringRadius(index: number): number {
    return this.track.points[index].width / 2 + 4;
  }

  private selectedPointIndex(): number | null {
    const s = this.selection;
    return s && (s.kind === "point" || s.kind === "handle") ? s.index : null;
  }

  /**
   * One marker per track object, floating just above its center on the road: a diamond to click, drag and select.
   * Positions come from the last preview, so they follow the road exactly.
   */
  private updateObjectMarkers(): void {
    // Until the preview has caught up with an added or removed object, keep the markers as they are.
    if (this.placements.length !== this.track.objects.length) return;
    const count = this.placements.length;
    while (this.objectMeshes.length > count) {
      const m = this.objectMeshes.pop()!;
      (m.material as THREE.Material).dispose();
      this.objectMarkers.remove(m);
    }
    while (this.objectMeshes.length < count) {
      const m = new THREE.Mesh(OBJECT_MARKER, new THREE.MeshBasicMaterial({ depthTest: false }));
      m.renderOrder = 10;
      this.objectMarkers.add(m);
      this.objectMeshes.push(m);
    }
    const s = this.selection;
    this.objectMeshes.forEach((m, i) => {
      const p = this.placements[i];
      m.userData = { kind: "object", index: i } satisfies Pick;
      m.position.set(...p.position).addScaledVector(new THREE.Vector3(...p.normal), 1.5);
      const selected = s?.kind === "object" && s.index === i;
      (m.material as THREE.MeshBasicMaterial).color.setHex(
        selected ? COLORS.selected : p.placed ? OBJECT_COLORS[this.track.objects[i].kind] : OBJECT_BLOCKED);
    });
  }

  /** Drop lines of all tracks' points: down to the ground, with a 2 m cross where they land. */
  private updateDropLines(): void {
    const points = this.map.tracks.flatMap((t) => t.points.map((p) => p.position));
    const v = new Float32Array(points.length * 18);
    points.forEach(([x, y, z], i) => {
      v.set([x, y, z, x, SHADOW_Y, z, x - 1, SHADOW_Y, z, x + 1, SHADOW_Y, z, x, SHADOW_Y, z - 1, x, SHADOW_Y, z + 1], i * 18);
    });
    const geo = this.dropLines.geometry;
    geo.setAttribute("position", new THREE.BufferAttribute(v, 3));
    geo.computeBoundingSphere();
  }

  /** Joins the gates of each linked checkpoint group with green bars, in track order, at the tops of the gates. */
  private updateCheckpointLinks(): void {
    const groups = new Map<number, THREE.Vector3[]>();
    for (const t of this.map.tracks) {
      const placements = this.views.get(t)?.placements ?? [];
      if (placements.length !== t.objects.length) continue;
      t.objects.forEach((o, i) => {
        if (o.kind !== "Gate" || o.gate !== "Checkpoint" || !((o.checkpointGroup ?? 0) > 0)) return;
        const list = groups.get(o.checkpointGroup!) ?? [];
        list.push(new THREE.Vector3(...placements[i].position).addScaledVector(new THREE.Vector3(...placements[i].normal), o.height));
        groups.set(o.checkpointGroup!, list);
      });
    }
    for (const child of this.checkpointLinks.children) (child as THREE.Mesh).geometry.dispose();
    this.checkpointLinks.clear();
    const up = new THREE.Vector3(0, 1, 0);
    for (const list of groups.values())
      for (let i = 1; i < list.length; i++) {
        const [a, b] = [list[i - 1], list[i]];
        const length = a.distanceTo(b);
        if (length < 0.01) continue;
        const bar = new THREE.Mesh(new THREE.CylinderGeometry(0.45, 0.45, length, 8, 1, true), this.checkpointLinkMaterial);
        bar.position.copy(a).add(b).multiplyScalar(0.5);
        bar.quaternion.setFromUnitVectors(up, b.clone().sub(a).normalize());
        this.checkpointLinks.add(bar);
      }
  }

  /** Updates positions/colors of the control meshes from the track (cheap; called on every change). */
  private updateControls(): void {
    this.updateObjectMarkers();
    this.updateDropLines();
    this.updateCheckpointLinks();
    const s = this.selection;
    this.track.points.forEach((p, i) => {
      const m = this.pointMeshes[i];
      if (!m) return;
      m.position.set(...p.position);
      const selected = s?.kind === "point" && s.index === i;
      (m.material as THREE.MeshBasicMaterial).color.setHex(
        selected ? COLORS.selected : i === 0 ? COLORS.start : COLORS.point);
    });

    const i = this.selectedPointIndex();
    if (i === null) return;
    const p = this.track.points[i];
    const hin = add(p.position, p.handleIn), hout = add(p.position, p.handleOut);
    for (const m of this.handleMeshes) {
      const which = m.userData.which as "in" | "out";
      m.position.set(...(which === "in" ? hin : hout));
      const selected = s?.kind === "handle" && s.which === which;
      (m.material as THREE.MeshBasicMaterial).color.setHex(selected ? COLORS.selected : COLORS.handle);
    }
    if (this.handleLine) {
      const attr = this.handleLine.geometry.getAttribute("position") as THREE.BufferAttribute;
      attr.set([...p.position, ...hin, ...p.position, ...hout]);
      attr.needsUpdate = true;
    }

    if (this.bankRing && this.bankMarker) {
      const { tangent, up } = this.frameAt(i);
      this.bankRing.position.set(...p.position);
      this.bankRing.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), tangent);
      this.bankMarker.position.set(...p.position).addScaledVector(up, this.ringRadius(i));
      const hovered = this.hover?.kind === "bank" || this.drag?.pick.kind === "bank";
      const color = hovered ? 0xffc28f : COLORS.ring;
      ((this.bankRing.children[0] as THREE.Mesh).material as THREE.MeshBasicMaterial).color.setHex(color);
      (this.bankMarker.material as THREE.MeshBasicMaterial).color.setHex(color);
    }
  }

  // ---- picking & hover -----------------------------------------------------------------------

  private rayFrom(e: PointerEvent): THREE.Raycaster {
    const rect = this.renderer.domElement.getBoundingClientRect();
    const ndc = new THREE.Vector2(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
    this.raycaster.setFromCamera(ndc, this.camera);
    return this.raycaster;
  }

  /** What is under the cursor. Handles beat points, points beat the bank ring, everything beats the road. */
  private pick(e: PointerEvent, includeRoad: boolean): Pick | null {
    const ray = this.rayFrom(e);
    const handle = ray.intersectObjects(this.handleMeshes, false)[0];
    if (handle) return handle.object.userData as Pick;
    const point = ray.intersectObjects(this.pointMeshes, false)[0];
    if (point) return point.object.userData as Pick;
    const object = ray.intersectObjects(this.objectMeshes, false)[0];
    if (object) return object.object.userData as Pick;
    if (this.bankPick && ray.intersectObject(this.bankPick, false)[0]) return this.bankPick.userData as Pick;
    const other = ray.intersectObjects(this.otherPointMeshes, false)[0];
    if (other) return other.object.userData as Pick;
    if (includeRoad) {
      // The nearest road of any track.
      let best: { distance: number; pick: Pick } | null = null;
      this.roadMeshes();
      this.map.tracks.forEach((t, track) => {
        const v = this.views.get(t);
        const r = v?.road ? ray.intersectObject(v.road, false)[0] : undefined;
        if (r?.faceIndex == null || (best && best.distance <= r.distance)) return;
        const pick: Pick = { kind: "segment", index: v!.roadSegments[r.faceIndex] };
        if (track !== this.active) pick.track = track;
        best = { distance: r.distance, pick };
      });
      const block = this.blockHit(ray.ray);
      if (block && (!best || block.distance < (best as { distance: number }).distance)) return { kind: "block", index: block.index };
      if (best) return (best as { pick: Pick }).pick;
    }
    return null;
  }

  /** Track of a pick: its own for points and segments of other tracks, otherwise the active one. */
  private trackOf(p: Pick): number {
    return (p.kind === "point" || p.kind === "segment") && p.track !== undefined ? p.track : this.active;
  }

  private samePick(a: Pick, b: Pick): boolean {
    if (a.kind !== b.kind || a.index !== b.index || this.trackOf(a) !== this.trackOf(b)) return false;
    return a.kind !== "handle" || (b.kind === "handle" && a.which === b.which);
  }

  private isHovered(p: Pick): boolean {
    const h = this.drag?.pick ?? this.hover;
    return !!h && this.samePick(h, p);
  }

  private setHover(pick: Pick | null): void {
    const same = pick === this.hover || (pick && this.hover && this.samePick(pick, this.hover));
    if (same) return;
    this.hover = pick;
    this.renderer.domElement.style.cursor = !pick ? "" : pick.kind === "segment" ? "pointer"
      : pick.kind === "block" ? (this.selectedBlocks().includes(pick.index) ? "grab" : "pointer") : "grab";
    this.syncBlocks();
    this.showSegmentOverlay(this.hoverOverlay, pick?.kind === "segment" ? pick.index : null, pick ? this.trackOf(pick) : this.active);
    this.updateControls();
  }

  private showSegmentOverlay(overlay: THREE.Mesh, segment: number | null, track = this.active): void {
    const t = this.map.tracks[track];
    const view = t ? this.views.get(t) : undefined;
    const indices = segment === null ? undefined : view?.segmentIndices.get(segment);
    if (!indices || !view?.road) {
      overlay.visible = false;
      return;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", view.road.geometry.getAttribute("position"));
    geo.setIndex(new THREE.BufferAttribute(indices, 1));
    overlay.geometry.dispose();
    overlay.geometry = geo;
    overlay.visible = true;
  }

  private updateSelectOverlay(): void {
    this.showSegmentOverlay(this.selectOverlay, this.selection?.kind === "segment" ? this.selection.index : null);
  }

  // ---- pointer interaction -------------------------------------------------------------------

  private onPointerDown(e: PointerEvent): void {
    if (e.button === 2) this.rightClickStart = { x: e.clientX, y: e.clientY };
    // The left button orbits only with Alt held (Alt+Shift pans), so plain left drags can select and move things.
    // Runs before OrbitControls' listener (capture phase), which reads the button mapping on pointer down.
    this.orbit.mouseButtons.LEFT = e.altKey ? THREE.MOUSE.ROTATE : (-1 as THREE.MOUSE);
    if (e.button !== 0 || e.altKey) return;
    if (this.terrainPaint && this.map.terrain) { this.startStroke(e); return; }
    // The optional axis gizmo handles its own drags.
    if (this.axisGizmoEnabled && this.axisGizmo.axis) return;
    // Insert/place mode: clicks insert (handled on pointer up).
    if (this.insertMode || this.placeMode || this.blockPlace) {
      this.clickStart = { x: e.clientX, y: e.clientY };
      return;
    }

    let pick = this.pick(e, false);
    // Selected blocks can be dragged (all of them together); other blocks are selected with a click, so a drag that
    // starts on them (scenery covers the ground) still draws a selection rectangle.
    const selected = this.selectedBlocks();
    if (!pick && selected.length > 0) {
      const hit = this.blockHit(this.rayFrom(e).ray);
      if (hit && selected.includes(hit.index)) pick = { kind: "block", index: hit.index };
    }
    if (!pick || pick.kind === "segment") {
      this.clickStart = { x: e.clientX, y: e.clientY };
      // Touch keeps its one-finger orbit (OrbitControls), so only mouse and pen draw rectangles.
      if (e.pointerType !== "touch") this.marquee = { pointerId: e.pointerId, x: e.clientX, y: e.clientY, el: this.marqueeElement() };
      return;
    }
    // A point of another track: that track becomes the active one, and the drag goes on with its point.
    if (pick.kind === "point" && pick.track !== undefined) {
      this.setActive(pick.track);
      pick = { kind: "point", index: pick.index };
    }

    // Grab a point, handle or the bank ring. Disabling the orbit here (capture phase) stops it from rotating.
    this.orbit.enabled = false;
    // Keep receiving moves when the cursor leaves the canvas. Fails for pointers that are not active (synthetic events).
    try { this.renderer.domElement.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
    this.renderer.domElement.style.cursor = "grabbing";
    if (pick.kind === "point") this.select({ kind: "point", index: pick.index });
    else if (pick.kind === "handle") this.select({ kind: "handle", index: pick.index, which: pick.which });
    else if (pick.kind === "object") this.select({ kind: "object", index: pick.index });

    this.drag = {
      pick, pointerId: e.pointerId, startX: e.clientX, startY: e.clientY, moved: false,
      mode: this.moveMode(e), plane: new THREE.Plane(), offset: new THREE.Vector3(),
    };
    this.beginDragPlane(e);
  }

  private onPointerMove(e: PointerEvent): void {
    this.lastPointer = e;
    if (this.terrainPaint && this.map.terrain && (!this.stroke || e.pointerId === this.stroke.pointerId)) {
      if (this.stroke) this.continueStroke(e);
      else if (!this.hoverQueued && !e.buttons) {
        this.hoverQueued = true;
        requestAnimationFrame(() => { this.hoverQueued = false; if (this.lastPointer) this.showBrush(this.brushHit(this.lastPointer)); });
      }
      return;
    }
    const marquee = this.marquee;
    if (marquee && e.pointerId === marquee.pointerId) {
      if (marquee.el.hidden && Math.hypot(e.clientX - marquee.x, e.clientY - marquee.y) < DRAG_THRESHOLD_PX) return;
      if (marquee.el.hidden) {
        marquee.el.hidden = false;
        this.setHover(null);
        try { this.renderer.domElement.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
      }
      const r = this.container.getBoundingClientRect();
      Object.assign(marquee.el.style, {
        left: `${Math.min(marquee.x, e.clientX) - r.left}px`, top: `${Math.min(marquee.y, e.clientY) - r.top}px`,
        width: `${Math.abs(e.clientX - marquee.x)}px`, height: `${Math.abs(e.clientY - marquee.y)}px`,
      });
      return;
    }
    const drag = this.drag;
    if (drag && e.pointerId === drag.pointerId) {
      if (!drag.moved && Math.hypot(e.clientX - drag.startX, e.clientY - drag.startY) < DRAG_THRESHOLD_PX) return;
      if (!drag.moved) {
        drag.moved = true;
        this.onBeforeEdit(structuredClone(this.map));
      }
      this.continueDrag(e);
      return;
    }
    // Hover picking against the road can be costly; do it at most once per frame.
    if (!this.hoverQueued && !e.buttons) {
      this.hoverQueued = true;
      requestAnimationFrame(() => {
        this.hoverQueued = false;
        if (this.lastPointer && !this.drag) {
          if (this.insertMode) this.updateInsertPreview(this.lastPointer);
          else if (this.placeMode) this.updatePlacePreview(this.lastPointer);
          else if (this.blockPlace) this.updateBlockPreview(this.lastPointer);
          else this.setHover(this.pick(this.lastPointer, true));
        }
      });
    }
  }

  private onPointerUp(e: PointerEvent): void {
    if (e.button === 2) {
      // A right click (not a pan): puts the block in hand away, or deselects the selected block.
      const start = this.rightClickStart;
      this.rightClickStart = null;
      if (start && Math.hypot(e.clientX - start.x, e.clientY - start.y) <= DRAG_THRESHOLD_PX) {
        if (this.blockPlace) this.setBlockPlaceMode(null);
        else if (this.selectedBlocks().length > 0) this.select(null);
      }
      return;
    }
    if (e.button !== 0) return;
    if (this.stroke && e.pointerId === this.stroke.pointerId) { this.endStroke(); return; }
    const marquee = this.marquee;
    this.marquee = null;
    if (marquee && !marquee.el.hidden) {
      try { this.renderer.domElement.releasePointerCapture(marquee.pointerId); } catch { /* not captured */ }
      marquee.el.remove();
      this.clickStart = null;
      const inside = this.blocksInRect(marquee.x, marquee.y, e.clientX, e.clientY);
      this.selectBlocks(inside, e.ctrlKey || e.metaKey ? "remove" : e.shiftKey ? "add" : "replace");
      return;
    }
    marquee?.el.remove();
    if (this.drag) {
      try { this.renderer.domElement.releasePointerCapture(this.drag.pointerId); } catch { /* not captured */ }
      this.drag = null;
      this.orbit.enabled = true;
      this.label.hidden = true;
      this.renderer.domElement.style.cursor = this.hover && this.hover.kind !== "segment" ? "grab" : "";
      this.updateControls();
      return;
    }

    const start = this.clickStart;
    this.clickStart = null;
    if (!start || Math.hypot(e.clientX - start.x, e.clientY - start.y) > DRAG_THRESHOLD_PX) return;
    if (this.axisGizmoEnabled && this.axisGizmo.dragging) return;

    if (this.insertMode) {
      this.updateInsertPreview(e);
      const target = this.insertTarget;
      if (!target) return;
      let index = 0;
      this.edit((t) => (index = splitSegment(t, target.segment, target.at)), true);
      this.setInsertMode(false);
      this.select({ kind: "point", index });
      return;
    }

    if (this.placeMode) {
      this.updatePlacePreview(e);
      const target = this.placeTarget, kind = this.placeMode;
      if (!target) return;
      this.edit((t) => t.objects.push(newObject(kind, target.segment, target.at, target.offset)), true);
      this.setPlaceMode(null);
      this.select({ kind: "object", index: this.track.objects.length - 1 });
      return;
    }

    if (this.blockPlace) {
      this.updateBlockPreview(e);
      const target = this.blockTarget;
      if (!target) return;
      // Stays in block mode, so a row of blocks is a few clicks.
      // Pillar blocks keep the game's pillar flag, as the game's own columns.
      const pillar = !target.free && this.library?.isPillar(target.name);
      this.editBlocks((blocks) => blocks.push(pillar ? { ...target, pillar: true } : { ...target }));
      this.select({ kind: "block", index: this.map.blocks.length - 1 });
      return;
    }

    if (e.ctrlKey || e.metaKey) {
      // Ctrl+click on the ground appends a point at the height of the last point.
      const g = this.rayFrom(e).intersectObject(this.ground, false)[0];
      if (g) {
        const last = this.track.points[this.track.points.length - 1];
        const p = this.snapStep > 0 ? this.snappedPoint(new THREE.Vector3(g.point.x, last.position[1], g.point.z)) : g.point.clone().setY(last.position[1]);
        const at: Vec3 = [clampMap(p.x), p.y, clampMap(p.z)];
        let index = 0;
        this.edit((t) => (index = appendPoint(t, at)), true);
        this.select({ kind: "point", index });
      }
      return;
    }

    const pick = this.pick(e, true);
    if (pick?.kind === "block") {
      // Shift+click adds the block to the selection or takes it out again.
      this.selectBlocks([pick.index], e.shiftKey ? "toggle" : "replace");
      return;
    }
    // Shift+click elsewhere keeps the selected blocks (a missed shift+click should not lose a large selection).
    if (e.shiftKey && this.selection?.kind === "blocks") return;
    if (pick?.kind === "segment" && pick.track !== undefined) this.setActive(pick.track);
    this.select(pick?.kind === "segment" ? { kind: "segment", index: pick.index } : null);
  }

  private marqueeElement(): HTMLDivElement {
    const el = document.createElement("div");
    el.className = "select-rect";
    el.hidden = true;
    this.container.appendChild(el);
    return el;
  }

  /** Blocks whose box centre is inside a screen rectangle (client coordinates) and in front of the camera. */
  private blocksInRect(x0: number, y0: number, x1: number, y1: number): number[] {
    const r = this.renderer.domElement.getBoundingClientRect();
    const [left, right] = [Math.min(x0, x1), Math.max(x0, x1)], [top, bottom] = [Math.min(y0, y1), Math.max(y0, y1)];
    const result: number[] = [];
    this.map.blocks.forEach((b, i) => {
      const size = blockSize(this.library, b);
      const center = new THREE.Vector3(size[0] * BLOCK_CELL / 2, size[1] * BLOCK_LEVEL / 2, size[2] * BLOCK_CELL / 2)
        .applyMatrix4(blockMatrix(b, size)).project(this.camera);
      if (center.z < -1 || center.z > 1) return;
      const sx = r.left + (center.x + 1) / 2 * r.width, sy = r.top + (1 - center.y) / 2 * r.height;
      if (sx >= left && sx <= right && sy >= top && sy <= bottom) result.push(i);
    });
    return result;
  }

  private moveMode(e: { shiftKey: boolean; altKey: boolean }): MoveMode {
    if (e.altKey || this.altDown) return "vertical";
    if (e.shiftKey || this.shiftDown) return "horizontal";
    return "screen";
  }

  /** Shift / Alt switch the move constraint mid-drag. */
  private onModifierChange(e: KeyboardEvent): void {
    const down = e.type === "keydown";
    if (e.key === "Shift") this.shiftDown = down;
    else if (e.key === "Alt") {
      this.altDown = down;
      // Keep the browser from focusing its menu when Alt is released (Alt is the camera key and a drag modifier).
      e.preventDefault();
    } else return;

    const drag = this.drag;
    if (!drag || drag.pick.kind === "bank") return;
    const mode = this.moveMode({ shiftKey: this.shiftDown, altKey: this.altDown });
    if (mode === drag.mode) return;
    drag.mode = mode;
    if (this.lastPointer) this.beginDragPlane(this.lastPointer);
  }

  private dragTarget(pick: Pick): THREE.Vector3 {
    if (pick.kind === "block") {
      const b = this.map.blocks[pick.index];
      if (b.free && b.position) return new THREE.Vector3(...b.position);
      return new THREE.Vector3(b.x * BLOCK_CELL, b.y * BLOCK_LEVEL, b.z * BLOCK_CELL);
    }
    if (pick.kind === "object") return this.objectMeshes[pick.index]?.position.clone() ?? new THREE.Vector3();
    const p = this.track.points[pick.index];
    if (pick.kind === "handle") return new THREE.Vector3(...add(p.position, pick.which === "in" ? p.handleIn : p.handleOut));
    return new THREE.Vector3(...p.position);
  }

  private beginDragPlane(e: PointerEvent): void {
    const drag = this.drag!;
    const target = this.dragTarget(drag.pick);
    const ray = this.rayFrom(e).ray;

    if (drag.pick.kind === "object") {
      drag.lastRay = ray.clone();
      return;
    }
    if (drag.pick.kind === "block") {
      // Moves on the block's own level, by whole cells.
      drag.plane.setFromNormalAndCoplanarPoint(new THREE.Vector3(0, 1, 0), target);
      const hit = ray.intersectPlane(drag.plane, new THREE.Vector3());
      const indices = this.selectedBlocks().includes(drag.pick.index) ? this.selectedBlocks() : [drag.pick.index];
      drag.blockStart = hit ? {
        hit,
        blocks: this.movedBlocks(indices),
      } : undefined;
      return;
    }
    if (drag.pick.kind === "bank") {
      const { tangent } = this.frameAt(drag.pick.index);
      drag.plane.setFromNormalAndCoplanarPoint(tangent, target);
      const hit = ray.intersectPlane(drag.plane, new THREE.Vector3());
      drag.lastDir = hit ? hit.sub(target).normalize() : undefined;
      drag.rawBank = this.track.points[drag.pick.index].bank;
      return;
    }

    if (drag.mode === "screen") {
      // Parallel to the screen through the grabbed object: it follows the cursor without moving in depth.
      drag.plane.setFromNormalAndCoplanarPoint(this.camera.getWorldDirection(new THREE.Vector3()), target);
    } else if (drag.mode === "vertical") {
      // A vertical plane facing the camera through the grabbed object.
      const facing = new THREE.Vector3().subVectors(this.camera.position, target).setY(0);
      if (facing.lengthSq() < 1e-6) facing.set(0, 0, 1);
      drag.plane.setFromNormalAndCoplanarPoint(facing.normalize(), target);
    } else {
      drag.plane.setFromNormalAndCoplanarPoint(new THREE.Vector3(0, 1, 0), target);
    }
    const hit = ray.intersectPlane(drag.plane, new THREE.Vector3());
    drag.offset = hit ? target.clone().sub(hit) : new THREE.Vector3();
  }

  private continueDrag(e: PointerEvent): void {
    const drag = this.drag!;
    if (drag.pick.kind === "object") {
      this.dragObject(e, drag.pick.index);
      return;
    }
    if (drag.pick.kind === "block") {
      this.dragBlocks(e, drag);
      return;
    }
    const hit = this.rayFrom(e).ray.intersectPlane(drag.plane, new THREE.Vector3());
    if (!hit) return;
    const p = this.track.points[drag.pick.index];

    if (drag.pick.kind === "bank") {
      const { tangent } = this.frameAt(drag.pick.index);
      const dir = hit.sub(new THREE.Vector3(...p.position)).normalize();
      if (drag.lastDir && dir.lengthSq() > 0) {
        const delta = Math.atan2(new THREE.Vector3().crossVectors(drag.lastDir, dir).dot(tangent), drag.lastDir.dot(dir));
        // The ring turns freely; the stored bank wraps around at ±180°.
        drag.rawBank = (drag.rawBank ?? p.bank) + (delta * 180) / Math.PI;
        p.bank = wrapBank(e.ctrlKey ? Math.round(drag.rawBank / 5) * 5 : drag.rawBank);
      }
      drag.lastDir = dir;
      this.showLabel(e, `Bank ${p.bank.toFixed(e.ctrlKey ? 0 : 1)}°`);
      this.changed(false);
      return;
    }

    const target = this.dragTarget(drag.pick);
    const moved = hit.add(drag.offset);
    // A plane seen nearly edge-on (horizontal moves from a low camera) can throw the hit far away; ignore those.
    if (moved.distanceTo(target) > this.camera.position.distanceTo(target) * 3) return;
    if (drag.mode === "vertical") moved.set(target.x, moved.y, target.z);
    else if (drag.mode === "horizontal") moved.y = target.y;
    // Snap mode moves points on the grid (at block road heights) and handles by grid steps; Ctrl ignores it. Without
    // snap mode Ctrl snaps to 1 m.
    const snap = this.snapStep > 0 && !e.ctrlKey;
    if (drag.pick.kind === "point" && snap) {
      const keepY = drag.mode === "horizontal" ? moved.y : null;
      this.snappedPoint(moved);
      if (keepY !== null) moved.y = keepY;
      if (drag.mode === "vertical") moved.set(target.x, moved.y, target.z);
    } else if (e.ctrlKey && this.snapStep === 0) moved.set(Math.round(moved.x), Math.round(moved.y), Math.round(moved.z));
    const pos: Vec3 = [clampMap(moved.x), clamp(moved.y, Y_MIN, Y_MAX), clampMap(moved.z)];

    if (drag.pick.kind === "point") {
      p.position = pos;
      this.showLabel(e, drag.mode === "horizontal" ? `Height locked ${pos[1].toFixed(1)} m` : `Height ${pos[1].toFixed(1)} m`);
    } else if (drag.pick.kind === "handle") {
      let handle = sub(pos, p.position);
      if (snap) {
        // Locked axes keep the handle's current value on them.
        const before = drag.pick.which === "in" ? p.handleIn : p.handleOut;
        handle = this.snappedHandle(handle);
        if (drag.mode === "horizontal") handle[1] = before[1];
        if (drag.mode === "vertical") [handle[0], handle[2]] = [before[0], before[2]];
      }
      setHandle(p, drag.pick.which, handle);
      if (snap) this.showLabel(e, `Handle ${handle.map((v) => v.toFixed(1)).join(", ")} m`);
      else this.label.hidden = true;
    }
    this.changed(false);
  }

  /**
   * Moves the grabbed block, and the other selected ones by the same step. With only free blocks the step is free
   * (snap mode: the grabbed block's origin lands on the grid; Ctrl ignores snapping); as soon as a grid block moves,
   * everything moves by whole cells. The step is limited so that no block leaves the map.
   */
  private dragBlocks(e: PointerEvent, drag: Drag): void {
    const start = drag.blockStart;
    const hit = this.rayFrom(e).ray.intersectPlane(drag.plane, new THREE.Vector3());
    if (!start || !hit || hit.distanceTo(this.camera.position) > 6000) return;
    const { dx, dz, count, cells } = this.moveBlocks(start.blocks, drag.pick.index, hit.x - start.hit.x, 0, hit.z - start.hit.z,
      !e.ctrlKey && this.snapStep > 0);
    const b = this.map.blocks[drag.pick.index];
    if (!b) return;
    const where = b.free && b.position
      ? `${b.position[0].toFixed(1)}, ${b.position[1].toFixed(1)}, ${b.position[2].toFixed(1)} m`
      : `cell ${b.x}, ${b.z} · level ${b.y}`;
    this.showLabel(e, count > 1 ? `${count} blocks · moved ${dx.toFixed(cells ? 0 : 1)}, ${dz.toFixed(cells ? 0 : 1)} m` : `${b.name} · ${where}`);
    this.syncBlocks();
    this.onChange();
  }

  /** The given blocks as they are now, to move them from there. */
  private movedBlocks(indices: number[]): MovedBlock[] {
    return indices.filter((i) => this.map.blocks[i]).map((index) => {
      const b = this.map.blocks[index];
      return { index, x: b.x, y: b.y, z: b.z, position: b.free && b.position ? [...b.position] as Vec3 : undefined };
    });
  }

  /**
   * Moves blocks from where they were (`moving`) by a step in meters. With only free blocks the step is free (`snap`:
   * the reference block's origin lands on the grid); as soon as a grid block moves, everything moves by whole cells
   * and levels. The step is limited so that no block leaves the map or goes below the ground. Returns the step taken.
   */
  private moveBlocks(moving: MovedBlock[], ref: number, dx: number, dy: number, dz: number, snap: boolean):
    { dx: number; dy: number; dz: number; count: number; cells: boolean } {
    moving = moving.filter((m) => this.map.blocks[m.index]);
    const cells = moving.some((m) => !m.position);
    if (cells) {
      [dx, dy, dz] = [Math.round(dx / BLOCK_CELL) * BLOCK_CELL, Math.round(dy / BLOCK_LEVEL) * BLOCK_LEVEL, Math.round(dz / BLOCK_CELL) * BLOCK_CELL];
    } else if (snap) {
      const p = (moving.find((m) => m.index === ref) ?? moving[0])?.position;
      if (p) {
        const moved = this.snapped(new THREE.Vector3(p[0] + dx, p[1] + dy, p[2] + dz));
        [dx, dy, dz] = [moved.x - p[0], dy === 0 ? 0 : moved.y - p[1], moved.z - p[2]];
      }
    }
    // The largest step (towards the cursor) that keeps every block inside the map: free blocks may leave the grid.
    const half = WORLD_HALF_EXTENT, top = MAX_BLOCK_LEVEL * BLOCK_LEVEL;
    let [minX, maxX, minY, maxY, minZ, maxZ] = [-Infinity, Infinity, -Infinity, Infinity, -Infinity, Infinity];
    for (const m of moving) {
      if (m.position) {
        [minX, maxX] = [Math.max(minX, -half - m.position[0]), Math.min(maxX, half - m.position[0])];
        [minY, maxY] = [Math.max(minY, Y_MIN - m.position[1]), Math.min(maxY, top - m.position[1])];
        [minZ, maxZ] = [Math.max(minZ, -half - m.position[2]), Math.min(maxZ, half - m.position[2])];
      } else {
        const [fx, , fz] = this.blockFootprint(this.map.blocks[m.index]);
        [minX, maxX] = [Math.max(minX, -half - m.x * BLOCK_CELL), Math.min(maxX, half - (m.x + fx) * BLOCK_CELL)];
        [minY, maxY] = [Math.max(minY, -m.y * BLOCK_LEVEL), Math.min(maxY, top - m.y * BLOCK_LEVEL)];
        [minZ, maxZ] = [Math.max(minZ, -half - m.z * BLOCK_CELL), Math.min(maxZ, half - (m.z + fz) * BLOCK_CELL)];
      }
    }
    dx = clamp(dx, Math.min(minX, 0), Math.max(maxX, 0));
    dy = clamp(dy, Math.min(minY, 0), Math.max(maxY, 0));
    dz = clamp(dz, Math.min(minZ, 0), Math.max(maxZ, 0));
    if (cells) [dx, dy, dz] = [Math.trunc(dx / BLOCK_CELL) * BLOCK_CELL, Math.trunc(dy / BLOCK_LEVEL) * BLOCK_LEVEL, Math.trunc(dz / BLOCK_CELL) * BLOCK_CELL];
    for (const m of moving) {
      const b = this.map.blocks[m.index];
      if (m.position) b.position = [m.position[0] + dx, m.position[1] + dy, m.position[2] + dz];
      else {
        b.x = m.x + Math.round(dx / BLOCK_CELL);
        b.y = m.y + Math.round(dy / BLOCK_LEVEL);
        b.z = m.z + Math.round(dz / BLOCK_CELL);
      }
    }
    return { dx, dy, dz, count: moving.length, cells };
  }

  /** Puts the gizmo's block anchor on the first selected block's bottom centre (not while the gizmo moves it). */
  private placeBlockAnchor(): void {
    if (this.gizmoBlocks) return;
    const b = this.map.blocks[this.selectedBlocks()[0]];
    if (!b) return;
    const size = blockSize(this.library, b);
    this.blockAnchor.position.set(size[0] * BLOCK_CELL / 2, 0, size[2] * BLOCK_CELL / 2).applyMatrix4(blockMatrix(b, size));
  }

  /**
   * Moves an object along the road surface: the step between the previous and the current pointer ray, on the road's
   * tangent plane at the object, split into along and across the road. Each step uses the latest preview, so long drags
   * follow curves and banking.
   */
  private dragObject(e: PointerEvent, index: number): void {
    const drag = this.drag!;
    const o = this.track.objects[index];
    const at = this.placement(index);
    const ray = this.rayFrom(e).ray.clone();
    const last = drag.lastRay;
    drag.lastRay = ray;
    if (!o || !at || !last || this.segmentStarts.length < 2) return;
    const point = new THREE.Vector3(...at.position);
    const normal = new THREE.Vector3(...at.normal).normalize();
    const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(normal, point);
    const from = last.intersectPlane(plane, new THREE.Vector3()), to = ray.intersectPlane(plane, new THREE.Vector3());
    if (!from || !to) return;
    const step = to.sub(from);
    if (step.length() > this.camera.position.distanceTo(point)) return; // plane seen edge-on
    const tangent = new THREE.Vector3(...at.tangent).normalize();
    const right = new THREE.Vector3().crossVectors(tangent, normal).normalize();
    const distance = this.objectDistance(index) + step.dot(tangent);
    const { segment, at: fraction } = this.segmentAt(distance);
    o.segment = segment;
    o.at = fraction;
    if (o.kind !== "Gap" && o.kind !== "Gate") o.offset = clamp(o.offset + step.dot(right), -256, 256);
    this.showLabel(e, o.kind === "Gap" || o.kind === "Gate" ? `${o.kind} at ${distance.toFixed(1)} m` : `${o.kind} at ${distance.toFixed(1)} m, ${o.offset >= 0 ? "right" : "left"} ${Math.abs(o.offset).toFixed(1)} m`);
    this.changed(false);
  }

  /** Distance from the track start to an object's center (from the last preview's segment lengths). */
  private objectDistance(index: number): number {
    const o = this.track.objects[index], starts = this.segmentStarts;
    if (o.segment + 1 >= starts.length) return 0;
    return starts[o.segment] + o.at * (starts[o.segment + 1] - starts[o.segment]);
  }

  /** Segment and arc-length fraction at a distance along the track (clamped to the track). */
  private segmentAt(distance: number): { segment: number; at: number } {
    const starts = this.segmentStarts;
    const count = starts.length - 1;
    const d = clamp(distance, 0, starts[count]);
    let segment = 0;
    while (segment < count - 1 && starts[segment + 1] <= d) segment++;
    const span = starts[segment + 1] - starts[segment];
    return { segment, at: span > 0 ? clamp((d - starts[segment]) / span, 0, 1) : 0 };
  }

  /** Where the cursor hits the driving surface, in (distance, across); null off the surface (borders, underside, holes). */
  private surfaceHit(e: PointerEvent): { distance: number; across: number; point: THREE.Vector3 } | null {
    if (!this.road || this.segmentStarts.length < 2) return null;
    this.roadMeshes();
    const hit = this.rayFrom(e).intersectObject(this.road, false)[0];
    if (!hit?.face) return null;
    const pos = this.road.geometry.getAttribute("position") as THREE.BufferAttribute;
    const { a, b, c } = hit.face;
    const corner = (i: number) => new THREE.Vector3().fromBufferAttribute(pos, i);
    const w = THREE.Triangle.getBarycoord(hit.point, corner(a), corner(b), corner(c), new THREE.Vector3());
    if (!w) return null;
    const sc = this.surfaceCoords;
    const distance = w.x * sc[a * 2] + w.y * sc[b * 2] + w.z * sc[c * 2];
    const across = w.x * sc[a * 2 + 1] + w.y * sc[b * 2 + 1] + w.z * sc[c * 2 + 1];
    if (!Number.isFinite(distance) || !Number.isFinite(across)) return null;
    return { distance, across, point: hit.point };
  }

  private updatePlacePreview(e: PointerEvent): void {
    const hit = this.surfaceHit(e);
    if (!hit || !this.placeMode) {
      this.placeTarget = null;
      this.insertMarker.visible = false;
      this.label.hidden = true;
      return;
    }
    const { segment, at } = this.segmentAt(hit.distance);
    this.placeTarget = { segment, at, offset: hit.across };
    this.insertMarker.position.copy(hit.point);
    this.insertMarker.visible = true;
    this.showLabel(e, `Click to place a ${this.placeMode.toLowerCase()}`);
  }

  /** Moves the insert marker to the point on the track's center line closest to where the cursor hits the road. */
  private updateInsertPreview(e: PointerEvent): void {
    this.roadMeshes();
    const hit = this.road ? this.rayFrom(e).intersectObject(this.road, false)[0] : undefined;
    const segment = hit?.faceIndex != null ? this.roadSegments[hit.faceIndex] : -1;
    if (!hit || segment < 0 || segment >= this.track.segments.length) {
      this.insertTarget = null;
      this.insertMarker.visible = false;
      this.label.hidden = true;
      return;
    }
    const at = closestOnSegment(this.track, segment, [hit.point.x, hit.point.y, hit.point.z]);
    this.insertTarget = { segment, at };
    this.insertMarker.position.set(...bezierAt(segmentBezier(this.track, segment), at));
    this.insertMarker.visible = true;
    this.showLabel(e, "Click to insert a point");
  }

  private showLabel(e: PointerEvent, text: string): void {
    const rect = this.container.getBoundingClientRect();
    this.label.textContent = text;
    this.label.style.left = `${e.clientX - rect.left + 16}px`;
    this.label.style.top = `${e.clientY - rect.top + 12}px`;
    this.label.hidden = false;
  }

  private onAxisGizmoMoved(): void {
    const obj = this.axisGizmo.object;
    if (!obj) return;
    if (obj === this.blockAnchor) {
      const start = this.gizmoBlocks;
      if (!start) return;
      const d = obj.position.clone().sub(start.anchor);
      const ref = this.selectedBlocks()[0];
      const moved = this.moveBlocks(start.blocks, ref, d.x, d.y, d.z, this.snapStep > 0);
      // The gizmo shows where the blocks went (whole cells, levels, snapped), not where the cursor is.
      obj.position.copy(start.anchor).add(new THREE.Vector3(moved.dx, moved.dy, moved.dz));
      this.syncBlocks();
      this.onChange();
      return;
    }
    const pick = obj.userData as Pick;
    if (pick.kind === "point" && this.snapStep > 0) this.snappedPoint(obj.position);
    const pos: Vec3 = [obj.position.x, obj.position.y, obj.position.z];
    const p = this.track.points[pick.index];
    if (pick.kind === "point") p.position = pos;
    else if (pick.kind === "handle") setHandle(p, pick.which, this.snappedHandle(sub(pos, p.position)));
    this.changed(false);
  }

  // ---- changes & road rebuild ----------------------------------------------------------------

  private changed(structural: boolean): void {
    if (structural) this.syncControls();
    else this.updateControls();
    this.requestRebuild();
    this.onChange();
  }

  private isSelectionValid(): boolean {
    const s = this.selection;
    if (!s) return true;
    if (s.kind === "segment") return s.index < this.track.segments.length;
    if (s.kind === "object") return s.index < this.track.objects.length;
    if (s.kind === "block") return s.index < this.map.blocks.length;
    if (s.kind === "blocks") return s.indices.every((i) => i < this.map.blocks.length);
    return s.index < this.track.points.length;
  }

  /**
   * Schedules a rebuild of the given tracks' previews (default: the active track; tracks without a preview are always
   * built). Rapid successive changes (drags, slider scrubbing) get the coarse mesh so the preview keeps up; a single
   * edit goes straight to full quality; and shortly after the last change coarse meshes are replaced by exact ones.
   */
  requestRebuild(tracks: Track[] = [this.track]): void {
    for (const t of tracks) this.dirty.add(t);
    for (const t of this.map.tracks) if (this.views.get(t)?.quality == null) this.dirty.add(t);
    const now = performance.now();
    const rapid = this.drag !== null || now - this.lastChangeAt < 300;
    this.lastChangeAt = now;
    this.queuedQuality = rapid ? 0 : 1;
    this.rebuildQueued = true;
    window.clearTimeout(this.refineTimer);
    this.refineTimer = window.setTimeout(() => this.refine(), 250);
    if (!this.rebuildRunning) void this.runRebuild();
  }

  private refine(): void {
    const coarse = this.map.tracks.filter((t) => this.views.get(t)?.quality !== 1);
    if (coarse.length === 0 && !(this.rebuildQueued && this.queuedQuality === 0)) return;
    for (const t of coarse) this.dirty.add(t);
    this.queuedQuality = 1;
    this.rebuildQueued = true;
    if (!this.rebuildRunning) void this.runRebuild();
  }

  /** Rebuilds the dirty tracks' previews; coalesces requests so slow builds never pile up while dragging. */
  private async runRebuild(): Promise<void> {
    this.rebuildRunning = true;
    try {
      while (this.rebuildQueued) {
        this.rebuildQueued = false;
        const quality = this.queuedQuality;
        const tracks = [...this.dirty];
        this.dirty.clear();
        for (const track of tracks) {
          if (!this.map.tracks.includes(track)) continue;
          try {
            const banks = track.points.map((p) => p.bank);
            const mesh = await buildPreview(track, quality);
            // Deleted (or replaced by undo) while it was building.
            if (this.map.tracks.includes(track)) this.setRoad(track, mesh, banks, quality);
          } catch (err) {
            console.error("Preview failed", err);
          }
        }
      }
    } finally {
      this.rebuildRunning = false;
    }
    // The terrain's strips follow the roads' edges.
    if (this.map.terrain && this.queuedQuality === 1) this.syncTerrain(true);
  }

  private setRoad(track: Track, mesh: PreviewMesh, banks: number[], quality: PreviewQuality): void {
    // Sort triangles by material so each material is one draw group.
    const triCount = mesh.indices.length / 3;
    const order = Array.from({ length: triCount }, (_, i) => i).sort((a, b) => mesh.triangleMaterial[a] - mesh.triangleMaterial[b]);
    const indices = new Uint32Array(mesh.indices.length);
    const segments = new Int32Array(triCount);
    order.forEach((src, dst) => {
      indices.set(mesh.indices.subarray(src * 3, src * 3 + 3), dst * 3);
      segments[dst] = mesh.triangleSegment[src];
    });

    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(mesh.positions, 3));
    geo.setAttribute("normal", new THREE.BufferAttribute(mesh.normals, 3));
    geo.setAttribute("gridUv", new THREE.BufferAttribute(mesh.uvs, 2));
    // The same UV0 as the exported item: the game's textures (from the local block library) map as in game.
    geo.setAttribute("uv", geo.getAttribute("gridUv"));
    geo.setAttribute("color", new THREE.BufferAttribute(segmentTints(track, mesh), 3));
    geo.setIndex(new THREE.BufferAttribute(indices, 1));

    const materials: THREE.Material[] = [];
    let start = 0;
    for (let m = 0; m < mesh.materials.length; m++) {
      let count = 0;
      while (start + count < triCount && mesh.triangleMaterial[order[start + count]] === m) count++;
      geo.addGroup(start * 3, count * 3, m);
      materials.push(this.roadMaterial(mesh.materials[m]));
      start += count;
    }
    geo.computeBoundingSphere();

    const v = this.viewOf(track);
    if (v.road) {
      (v.road.getObjectByName("wire") as THREE.LineSegments | undefined)?.geometry.dispose();
      v.road.geometry.dispose();
      this.scene.remove(v.road);
    }
    v.road = new THREE.Mesh(geo, materials);
    this.wireOf(v.road);
    v.road.userData.materials = mesh.materials;
    this.scene.add(v.road);
    v.roadSegments = segments;
    v.segmentIndices = groupBySegment(indices, segments);
    v.pointFrames = mesh.pointFrames;
    v.frameBanks = banks;
    v.length = mesh.trackLength;
    v.triangles = triCount;
    v.surfaceCoords = mesh.surface;
    v.segmentStarts = mesh.segmentStarts;
    v.placements = mesh.objects;
    // (The gates stand where their objects now are.)
    if (track.objects.some((o) => o.kind === "Gate")) this.blocks.syncPillars([...pillarColumns(this.map.blocks, this.library), ...this.gateBlocks()]);
    v.quality = quality;
    this.updateEffectArrows(v, mesh);

    // Overlays reference the old geometry; rebuild them for the new road.
    this.updateControls();
    const hover = this.hover;
    this.showSegmentOverlay(this.hoverOverlay, hover?.kind === "segment" ? hover.index : null, hover ? this.trackOf(hover) : this.active);
    this.updateSelectOverlay();
    this.onChange();
  }

  /**
   * One arrow per boost piece, floating above the road, pointing where the boost pushes. Reactor boosts show their real
   * direction; turbos lie on the road, shortened to their strength (the tilt only scales the push along the road).
   */
  private updateEffectArrows(view: TrackView, mesh: PreviewMesh): void {
    for (const child of view.arrows.children) (child as THREE.ArrowHelper).dispose();
    view.arrows.clear();
    for (const e of mesh.effects) {
      const push = new THREE.Vector3(...e.direction).normalize();
      const turbo = REACTOR_EFFECTS.indexOf(e.effect) < 0;
      const dir = turbo ? new THREE.Vector3(...e.roadDirection).normalize() : push;
      const length = turbo ? 8 * Math.max(0.15, push.dot(dir)) : 8;
      // White on the yellow/red road reads best; the road tint already tells the boost type.
      const at = new THREE.Vector3(...e.center).addScaledVector(dir, -length / 2);
      at.y += 0.6;
      const arrow = new THREE.ArrowHelper(dir, at, length, 0xffffff, Math.min(3.2, length * 0.4), 2.6);
      for (const part of [arrow.line, arrow.cone]) {
        (part.material as THREE.Material).depthTest = false;
        part.renderOrder = 7;
      }
      view.arrows.add(arrow);
    }
  }

  private roadMaterial(name: string): THREE.Material {
    let m = this.roadMaterials.get(name);
    if (!m) {
      const [surface, effect] = name.split("_");
      // See-through plane inside checkpoint/finish gates.
      if (surface === "GateCurtain" || surface === "GateCurtainFinish" || surface === "GateFx") {
        // (An effect gate's plane, "GateFx_Turbo" ...: in its gate's colour.)
        const curtain = new THREE.MeshBasicMaterial({ color: surface === "GateFx" ? GATE_COLORS[effect] ?? 0x9fd8ff : surface === "GateCurtain" ? 0x9fd8ff : 0xff8080, transparent: true, opacity: 0.25, depthWrite: false, side: THREE.DoubleSide });
        this.roadMaterials.set(name, curtain);
        return curtain;
      }
      // Race gates: "Gate_Start", "Gate_Checkpoint", ... colored like in game.
      if (surface === "Gate") {
        const gate = new THREE.MeshStandardMaterial({ color: GATE_COLORS[effect] ?? 0xffffff, roughness: 0.6 });
        this.roadMaterials.set(name, gate);
        return gate;
      }
      // Boost decals only matter in game; the tinted road and the arrows already show the boost here.
      if (surface === "Decal") {
        const hidden = new THREE.MeshBasicMaterial({ visible: false });
        this.roadMaterials.set(name, hidden);
        return hidden;
      }
      const color = new THREE.Color(SURFACE_COLORS[surface] ?? 0xff00ff);
      if (effect && EFFECT_COLORS[effect] !== undefined) color.lerp(new THREE.Color(EFFECT_COLORS[effect]), 0.75);
      // Parts the game's item color tints take their color from the vertices (see segmentTints).
      const tinted = isTinted(surface);
      // With the local block library: the game's texture of the item's material (see GAME_MATERIALS), tinted for effects.
      const texture = this.library?.textureOf(GAME_MATERIALS[surface] ?? "");
      const road = new THREE.MeshStandardMaterial({ color: tinted ? 0xffffff : color, vertexColors: tinted, roughness: 0.85, side: THREE.DoubleSide });
      if (texture) {
        road.map = this.library!.textureImage(texture);
        road.color.set(effect && EFFECT_COLORS[effect] !== undefined ? new THREE.Color(0xffffff).lerp(new THREE.Color(EFFECT_COLORS[effect]), 0.6) : 0xffffff);
      }
      // Only the driving surface gets a grid; it makes banking and the cross-section's curvature readable.
      if (!(surface in NO_GRID_SURFACES)) addRoadGrid(road, surface.startsWith("Road") ? ROAD_TEXTURE_INSET : 0);
      this.roadMaterials.set(name, road);
      m = road;
    }
    return m;
  }
}

/**
 * The game material each preview surface's item material links to (TmSpline.Core Surfaces), where the local block
 * library has its texture. Platform variants are PlatformTech with the game's surface modifier.
 */
/** The own terrain's game materials (TmSpline.Core Surfaces.Terrain), where the block library has their textures. */
const TERRAIN_MATERIALS: Record<TerrainSurface, string> = { Grass: "DecoHill", Dirt: "PlatformDirt/DecoHill", Ice: "PlatformIce/DecoHill" };
/** The own terrain's rock (Surfaces.TerrainRock: the cliffs' material). */
const ROCK_MATERIALS: Record<TerrainSurface, string> = { Grass: "DecoCliff", Dirt: "PlatformDirt/DecoCliff", Ice: "PlatformIce/DecoCliff" };

const GAME_MATERIALS: Record<string, string> = {
  RoadTech: "RoadTech", RoadDirt: "RoadDirt", RoadBump: "RoadBump", RoadIce: "RoadIce", PlatformTech: "PlatformTech",
  PlatformDirt: "PlatformDirt/PlatformTech", PlatformGrass: "PlatformGrass/PlatformTech", PlatformIce: "PlatformIce/PlatformTech",
  PlatformPlastic: "PlatformPlastic/PlatformTech", Border: "TrackBorders", RoadWood: "ThemeSnowRoad",
};

/** Simplified look of the game's item colors (per segment) on the parts they tint. */
const ITEM_TINTS: Partial<Record<ItemColor, number>> = { White: 0xf2f2f2, Green: 0x3fc46e, Blue: 0x2e8fd6, Red: 0xe0393e, Black: 0x26272b };

/** Preview materials the item color tints: borders, side walls and underside, plastic surfaces. */
const isTinted = (surface: string) => surface === "Border" || surface === "BorderPlastic" || surface === "Underside" || surface === "PlatformPlastic";

/**
 * Vertex colors for the preview: on tinted materials (see isTinted) the segment's item color, or the material's own
 * color for Default; white elsewhere (those materials ignore vertex colors).
 */
function segmentTints(track: Track, mesh: PreviewMesh): Float32Array {
  const colors = new Float32Array(mesh.positions.length).fill(1);
  const c = new THREE.Color();
  for (let t = 0; t < mesh.triangleMaterial.length; t++) {
    const surface = mesh.materials[mesh.triangleMaterial[t]].split("_")[0];
    if (!isTinted(surface)) continue;
    const tint = ITEM_TINTS[track.segments[mesh.triangleSegment[t]]?.color ?? "Default"];
    c.setHex(tint ?? SURFACE_COLORS[surface]);
    for (let k = 0; k < 3; k++) colors.set([c.r, c.g, c.b], mesh.indices[t * 3 + k] * 3);
  }
  return colors;
}

/**
 * Approximate road frame at a spline point: tangent from the handles, level lateral axis, then banking.
 * Matches the core's frame at points that are not near-vertical (where the road is levelled exactly).
 */
function pointFrame(p: TrackPoint): { tangent: THREE.Vector3; up: THREE.Vector3 } {
  let t = normalize(p.handleOut);
  if (t[0] === 0 && t[1] === 0 && t[2] === 0) t = normalize(sub([0, 0, 0], p.handleIn));
  const tangent = new THREE.Vector3(...t);
  if (tangent.lengthSq() === 0) tangent.set(0, 0, 1);
  let right = new THREE.Vector3().crossVectors(tangent, new THREE.Vector3(0, 1, 0));
  if (right.lengthSq() < 1e-4) right = new THREE.Vector3().crossVectors(tangent, new THREE.Vector3(0, 0, 1));
  right.normalize();
  const up = new THREE.Vector3().crossVectors(right, tangent).normalize();
  up.applyAxisAngle(tangent, (p.bank * Math.PI) / 180);
  return { tangent, up };
}

function groupBySegment(indices: Uint32Array, segments: Int32Array): Map<number, Uint32Array> {
  const lists = new Map<number, number[]>();
  for (let t = 0; t < segments.length; t++) {
    let list = lists.get(segments[t]);
    if (!list) lists.set(segments[t], (list = []));
    list.push(indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]);
  }
  return new Map([...lists].map(([s, l]) => [s, Uint32Array.from(l)]));
}

const clamp = (v: number, min: number, max: number) => Math.max(min, Math.min(max, v));
/** Track points, free blocks and objects may leave the grid (see WORLD_HALF_EXTENT); grid blocks use clampCell. */
const clampMap = (v: number) => clamp(v, -WORLD_HALF_EXTENT, WORLD_HALF_EXTENT);
