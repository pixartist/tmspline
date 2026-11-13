// Track document — mirrors TrackJson.TrackDto in TmSpline.Core (camelCase JSON, version 2: a map with several tracks).
// Coordinates are meters relative to the map center at ground level, Y up.

export type Vec3 = [number, number, number];

export const SURFACES = [
  "RoadTech", "RoadDirt", "RoadBump", "RoadIce", "RoadWood",
  "PlatformTech", "PlatformDirt", "PlatformGrass", "PlatformIce", "PlatformPlastic",
  "Wood", "Snow", "Sand", "Grass",
  "Magnet", "MagnetAccel", "SuperMagnet",
] as const;
export type Surface = (typeof SURFACES)[number];

export const BORDER_MATERIALS = ["Rubber", "Plastic"] as const;
export type BorderMaterial = (typeof BORDER_MATERIALS)[number];

export const EFFECTS = [
  "None", "Turbo", "Turbo2", "ReactorBoost", "ReactorBoost2", "SlowMotion", "NoSteering", "NoBrakes", "FreeWheeling",
  "Cruise", "Fragile", "NoGrip", "ForceAcceleration", "Reset", "Bumper", "Bumper2",
] as const;
export type Effect = (typeof EFFECTS)[number];

/** Display names where the importer's name is not self-explanatory. */
export const EFFECT_LABELS: Partial<Record<Effect, string>> = {
  Turbo2: "Turbo (red)", ReactorBoost: "Reactor boost", ReactorBoost2: "Reactor boost (red)", FreeWheeling: "No engine",
  Bumper2: "Bumper (strong)", ForceAcceleration: "Force acceleration",
};

/** Effects that push along a direction; their pieces are aimed (Direction / Tilt in the inspector). */
export const DIRECTIONAL_EFFECTS: ReadonlySet<Effect> = new Set<Effect>(["Turbo", "Turbo2", "ReactorBoost", "ReactorBoost2"]);

/** The game's item colors (per segment); Default keeps each material's own look (e.g. yellow plastic). */
export const ITEM_COLORS = ["Default", "White", "Green", "Blue", "Red", "Black"] as const;
export type ItemColor = (typeof ITEM_COLORS)[number];

export const WAYPOINTS = ["None", "Start", "Checkpoint", "Finish", "StartFinish"] as const;
export type Waypoint = (typeof WAYPOINTS)[number];

/** Surfaces whose material accepts a gameplay effect (same list as Surfaces.SupportsEffects in C#). */
export const EFFECT_SURFACES: ReadonlySet<Surface> = new Set<Surface>([
  "RoadTech", "RoadDirt", "RoadBump", "RoadIce", "PlatformTech", "PlatformDirt", "PlatformGrass", "PlatformIce",
]);

export interface TrackPoint {
  position: Vec3;
  handleIn: Vec3;
  handleOut: Vec3;
  bank: number;
  /** Road width in meters (measured along the surface); blended smoothly towards the neighbouring points. */
  width: number;
  /**
   * Cross-section bend in degrees: 0 = flat, negative = convex (sausage; -360 = a closed tube driven on its outside),
   * positive = concave (90 = gutter, 180 = half-pipe, 360 = closed pipe).
   */
  curve: number;
  /** Share of the width (0..0.9) that stays flat in the middle; only the sides bend. Curve 180 + flat 0.5 = U profile. */
  flat: number;
  /**
   * "Sausageness" 0..1: compresses each side's bend towards its inner end, the rest straight at the final angle.
   * 0 = even round bend, 1 = triangle with a sharp crown, in between a rounded triangle (Trackmania's sausage roads).
   */
  sausage: number;
  /**
   * "Keep edges" 0..1, blended between points: 0 = a bent cross-section keeps its middle on the spline (width along
   * the surface; pipes), 1 = it keeps its edges at the point's height and exactly `width` apart, like the game's own
   * pieces, with upright curbs. Above 0 the shape is limited to ±180° (see clampShape). Missing in older files = 0.
   */
  edgeAnchor: number;
  /** Height of the road borders in meters; blended smoothly towards the neighbouring points. */
  borderHeight: number;
  /**
   * Road borders at this point: they blend from point to point (full here, gone at a point without), so a segment has
   * them when either of its points does. Segment.borders follows from these (see syncSegments).
   */
  borders: boolean;
  /** Race gate at this point. */
  waypoint: Waypoint;
  /** How high its gate stands over the road, meters (missing: DEFAULT_GATE_HEIGHT, as in the core). */
  gateHeight?: number;
  /** The see-through plane in its gate (checkpoints, finish): there unless false. (Files from 0.5.2 have true where it was ticked.) */
  gatePlane?: boolean;
  /**
   * Linked checkpoint group (Checkpoint only): checkpoints with the same number, on any track of the map, count as one
   * in the game. Missing: not linked (TrackPoint.CheckpointGroup in the core).
   */
  checkpointGroup?: number;
}

/** Border height of new points: as the game's road curbs (matched in game by the user). */
export const DEFAULT_BORDER_HEIGHT = 1.0;
/** Border height of points in files that stored none (the default before it matched the game's curbs). */
const LEGACY_BORDER_HEIGHT = 0.8;
/**
 * Road width of new tracks: the driving surface between the curbs of the game's road blocks (25.61 m; the road texture
 * matches theirs exactly, checked in game by the user). The curbs sit outside the width.
 */
export const DEFAULT_ROAD_WIDTH = 25.61;
/** Gate height over the road when a point sets none (TrackPoint.DefaultGateHeight in the core). */
export const DEFAULT_GATE_HEIGHT = 6;
/**
 * Until file version 3 the curbs stood this far inside a point's width (TrackPoint.LegacyCurbInset in the core): older
 * files' points with borders are read with their width less twice this, so curbs and road stay where they were.
 */
const LEGACY_CURB_INSET = 0.32;

/** Named cross-section shapes for the inspector. */
/** Points that keep their edges bend at most ±180° (no pipes); the core applies the same limit. */
export function clampShape(p: TrackPoint): void {
  if (p.edgeAnchor > 0) p.curve = Math.max(-180, Math.min(180, p.curve));
}

export const SHAPE_PRESETS: { label: string; curve: number; flat: number; sausage: number }[] = [
  { label: "Sausage", curve: -40, flat: 0, sausage: 0.8 },
  { label: "Flat", curve: 0, flat: 0, sausage: 0 },
  { label: "U", curve: 180, flat: 0.5, sausage: 0 },
  { label: "Half-pipe", curve: 180, flat: 0, sausage: 0 },
  { label: "Pipe", curve: 360, flat: 0, sausage: 0 },
];

export interface TrackSegment {
  surface: Surface;
  borders: boolean;
  /** Rubber = native borders (slow you down); Plastic = grindable without losing speed, bouncy. */
  borderMaterial: BorderMaterial;
  /** Whether the road slab is closed below (underside and side walls); off saves about half the triangles. */
  underside: boolean;
  /** The game's item color for the segment's pieces: tints borders, side walls and plastic surfaces. */
  color: ItemColor;
  effect: Effect;
  /** Boost direction relative to the road, degrees: 0 = forward, 90 = right. */
  effectYaw: number;
  /** Boost tilt out of the road plane, degrees: positive = up. */
  effectPitch: number;
  /** Bumpiness of the driving surface (real geometry, mirrors BumpPattern in C#). */
  bumps: BumpPattern;
  /** Bump height in meters (negative: dips). */
  bumpHeight: number;
  /** Wavelength (waves along, ridges across) or feature size (noise) in meters. */
  bumpSize: number;
  /** Meters between the cross-sections / columns of the bumpy surface (smaller: finer, more triangles). */
  bumpDetail: number;
  /** Variant of the noise pattern (missing / 0: the segment's own; BumpSeed in the core). */
  bumpSeed?: number;
  /** The game's curve signs along the borders (item name; RoadSign red, B yellow, C green), see SignBuilder in C#. */
  signs: RoadSignKind;
  signSide: SignSide;
  /** Meters between signs. */
  signSpacing: number;
}

export const ROAD_SIGNS = ["None", "RoadSign", "RoadSignB", "RoadSignC"] as const;
export type RoadSignKind = (typeof ROAD_SIGNS)[number];
/** How the game shows them (checked in game by the user): RoadSign red, RoadSignB yellow, RoadSignC green. */
export const ROAD_SIGN_LABELS: Record<RoadSignKind, string> = { None: "None", RoadSign: "Red", RoadSignB: "Yellow", RoadSignC: "Green" };
export const SIGN_SIDES = ["Left", "Right", "Both"] as const;
export type SignSide = (typeof SIGN_SIDES)[number];

export const BUMP_PATTERNS = ["None", "Waves", "Noise", "Ridges"] as const;
export type BumpPattern = (typeof BUMP_PATTERNS)[number];

export const defaultSegment = (): TrackSegment => ({
  surface: "RoadTech", borders: true, borderMaterial: "Rubber", underside: true, color: "Default", effect: "None", effectYaw: 0, effectPitch: 0,
  bumps: "None", bumpHeight: 0.5, bumpSize: 8, bumpDetail: 2,
  signs: "None", signSide: "Both", signSpacing: 16,
});

export const OBJECT_KINDS = ["Pillar", "Hole", "Gap", "Bump", "Gate"] as const;
/** A ring gate's height: where the road runs in the ring (its middle, where a road of 26 m fits). */
export const GATE_RING_HEIGHT = 16;
/** The game's flat gate for a gate (mirrors GateObjects.Panel in the core). */
export const gatePanel = (gate: string) => gate.startsWith("Car")
  ? `GateExpandableGameplay${gate.slice(3)}`
  : `GateExpandable${(GATES[gate].block ?? "").slice("Gate".length).replace("Oriented", "")}`;
/** A gate of our own: its height over the road when new. */
export const GATE_HEIGHT = 8;
/** How a gate looks: a gate of our own fitted to the road (missing), or one of the game's blocks. */
export type GateLook = "flat" | "ring";
/** The look a gate gets: the game's blocks only where the game has one for its effect. */
export const gateLook = (o: TrackObject): GateLook | undefined => (GATES[o.gate ?? "Turbo"]?.block ? o.look : undefined);
/**
 * The gates a Gate object can be, by the key kept in the file: the race's own (start, checkpoint, finish: until
 * 0.7.0 those stood on the spline's points), then the effects and the cars (with the game's gate block where it has
 * one). Mirrors WaypointBuilder.RaceGate and GateObjects in the core.
 */
export const GATES: Record<string, { block?: string; label: string; race?: true; back?: true }> = {
  Start: { label: "Start", race: true },
  Checkpoint: { label: "Checkpoint", race: true },
  Finish: { label: "Finish", race: true },
  StartFinish: { label: "Start / finish (laps)", race: true },
  Turbo: { block: "GateSpecialTurbo", label: "Turbo" },
  Turbo2: { block: "GateSpecialTurbo2", label: "Turbo (red)" },
  TurboRoulette: { block: "GateSpecialTurboRoulette", label: "Turbo roulette" },
  // Up and down, as in the game: the same directed boost, facing along the road (up) or against it (down).
  ReactorBoost: { block: "GateSpecialBoostOriented", label: "Reactor boost up" },
  ReactorBoostDown: { block: "GateSpecialBoostOriented", label: "Reactor boost down", back: true },
  ReactorBoost2: { block: "GateSpecialBoost2Oriented", label: "Reactor boost up (red)" },
  ReactorBoost2Down: { block: "GateSpecialBoost2Oriented", label: "Reactor boost down (red)", back: true },
  SlowMotion: { block: "GateSpecialSlowMotion", label: "Slow motion" },
  NoSteering: { block: "GateSpecialNoSteering", label: "No steering" },
  NoBrakes: { block: "GateSpecialNoBrake", label: "No brakes" },
  FreeWheeling: { block: "GateSpecialNoEngine", label: "No engine" },
  Cruise: { block: "GateSpecialCruise", label: "Cruise" },
  Fragile: { block: "GateSpecialFragile", label: "Fragile" },
  Reset: { block: "GateSpecialReset", label: "Reset" },
  CarStadium: { block: "GateGameplayStadium", label: "Car: Stadium" },
  CarSnow: { block: "GateGameplaySnow", label: "Car: Snow" },
  CarRally: { block: "GateGameplayRally", label: "Car: Rally" },
  CarDesert: { block: "GateGameplayDesert", label: "Car: Desert" },
  // The road effects the game has no gate block for: gates of our own only.
  NoGrip: { label: "No grip" },
  ForceAcceleration: { label: "Force acceleration" },
  Bumper: { label: "Bumper" },
  Bumper2: { label: "Bumper (red)" },
};
export type TrackObjectKind = (typeof OBJECT_KINDS)[number];
export const OBJECT_SHAPES = ["Round", "Rect"] as const;
export type ObjectShape = (typeof OBJECT_SHAPES)[number];

/**
 * Something placed on the track (mirrors TrackObject in C#). Anchored to a segment, so edits elsewhere do not move it.
 * Pillar: an obstacle standing on the road. Hole: a cut through the road. Gap: the road left out (a jump).
 */
export interface TrackObject {
  kind: TrackObjectKind;
  segment: number;
  /** Arc-length fraction (0..1) of the object's center within its segment. */
  at: number;
  /** Center across the driving surface in meters, measured along the surface (positive = right). Not used by gaps. */
  offset: number;
  /** Outline of holes and pillars. */
  shape: ObjectShape;
  /** Size along the road in meters. */
  length: number;
  /** Size across the road in meters (holes, pillars). */
  width: number;
  /** Pillars: how far they stand up from the road; bumps: how high the hump is (negative: a dip), in meters. */
  height: number;
  /** Gates: which (a key of GATES). */
  gate?: string;
  /**
   * Gates: missing, a gate of our own fitted to the road (the height is the gate's); "flat" or "ring", the game's own
   * gate blocks (the ring's height is how high in it the road runs).
   */
  look?: GateLook;
  /** Race gates (checkpoint, finish): false for no see-through plane in the gate. */
  plane?: false;
  /**
   * Checkpoint gates: the group of linked checkpoints it belongs to. The checkpoints of a group count as one (passing
   * any of them is enough, e.g. on alternative routes); they may sit on different tracks.
   */
  checkpointGroup?: number;
  /** Pillars: the game item's colour (absent: Default). */
  color?: ItemColor;
}

export function newObject(kind: TrackObjectKind, segment: number, at: number, offset: number): TrackObject {
  if (kind === "Bump") return { kind, segment, at, offset, shape: "Round", length: 10, width: 8, height: 1 };
  if (kind === "Gate") return { kind, segment, at, offset: 0, shape: "Round", length: 1, width: 1, height: DEFAULT_GATE_HEIGHT, gate: "Checkpoint" };
  const size = kind === "Pillar" ? 2 : kind === "Hole" ? 6 : 16;
  return { kind, segment, at, offset: kind === "Gap" ? 0 : offset, shape: "Round", length: size, width: kind === "Gap" ? 0 : size, height: 4 };
}

/** One spline road. A map holds one or more of them (e.g. pieces of a route the car jumps between). */
export interface Track {
  closed: boolean;
  points: TrackPoint[];
  segments: TrackSegment[];
  objects: TrackObject[];
}

/** The document: a map with its tracks. Start, checkpoints and finish are only where the mapper puts them. */
/** The environments besides Stadium, by the game's names for them. */
export const MAP_ENVIRONMENTS = ["BlueBay", "GreenCoast", "RedIsland", "WhiteShore"] as const;
export type MapEnvironment = (typeof MAP_ENVIRONMENTS)[number];

export interface TrackMap {
  /** 3: point widths no longer include the curbs' inset (see LEGACY_CURB_INSET). */
  version: 3;
  name: string;
  /** Laps when the map has a start/finish. */
  laps: number;
  /** Without the stadium around the map (the game's "Day (no stadium)" decoration); missing = with. */
  noStadium?: true;
  /** The game's environment the map is made for; missing = Stadium. The others are water all over, 64 x 64 cells. */
  environment?: MapEnvironment;
  tracks: Track[];
  /** Vanilla game blocks on the map grid (exported by name; their meshes come from the game). */
  blocks: VanillaBlock[];
  /** Vanilla game items (trees, ...), exported by name; the editor draws stand-ins (their models are not converted). */
  items?: VanillaItem[];
  /** Own terrain round the tracks (exported as custom items; see TerrainGrid). */
  terrain?: TerrainGrid;
  /** The scenery panel's settings when it last generated here (only the panel reads them; checked there). */
  sceneryOptions?: Record<string, unknown>;
}

/** The terrain's look and surface: the game's hill materials (grass, dirt with sand physics, ice). */
export type TerrainSurface = "Grass" | "Dirt" | "Ice";

/**
 * Own terrain: heights (metres) on a square grid of vertices, vertex (c, r) at x = x0 + c cell, z = z0 + r cell, rows one
 * after another, null where there is none. A grid square is land where its four corners have heights; land at its outer
 * edge gets a skirt down by `skirt` metres. The export adds strips from the roads' edges to it (TerrainMeshBuilder).
 */
export interface TerrainGrid {
  surface: TerrainSurface;
  cell: number;
  x0: number;
  z0: number;
  columns: number;
  rows: number;
  heights: (number | null)[];
  skirt: number;
  /** Land steeper than this (degrees) is rock, as are the skirts; missing or 90: none. */
  rockAngle?: number;
  /**
   * The 3D brush's dabs, five numbers each: centre x, y, z, radius, amount (metres the land's surface moves out at the
   * centre, in when negative, fading to nothing at the radius). They work on walls and ceilings as on the ground.
   */
  edits?: number[];
  /** Dabs like `edits` that the roads do not cut through: land worked into the road's own space. */
  editsOver?: number[];
  /** The smoothing brush's dabs (five numbers each, as edits): the solid is blurred there; cut by the roads, and not. */
  smooths?: number[];
  smoothsOver?: number[];
  /** How fine the mesh is (the cells at the roads' edges: 1, 1.5, 2, 2 m, and Low's grow sooner away from them); missing: High. */
  quality?: TerrainQuality;
}

export const TERRAIN_QUALITIES = ["Fine", "High", "Medium", "Low"] as const;
export type TerrainQuality = (typeof TERRAIN_QUALITIES)[number];

/**
 * A vanilla item of the game's Stadium collection (e.g. "SpringTreeTall") standing at `position` (metres, its base),
 * turned by `yaw` degrees.
 */
export interface VanillaItem {
  name: string;
  position: Vec3;
  yaw: number;
  /** Placed by the scenery generator (its theme id), see VanillaBlock.scenery. */
  scenery?: string;
}

/**
 * A vanilla block on the map grid: x/z count 32 m cells from the map center (cell 0 spans 0..32 m), y counts 8 m
 * levels above the ground (0 = on the ground). dir is the game's block direction: 0 North (as modelled), 1 East,
 * 2 South, 3 West, each a further 90° clockwise seen from above.
 */
export interface VanillaBlock {
  name: string;
  x: number;
  y: number;
  z: number;
  dir: number;
  /**
   * Free block (the game's free block mode): at `position` (meters, the block's own origin corner), turned by yaw,
   * pitch and roll in degrees (yaw about Y, then pitch about X, then roll about Z); x/y/z/dir are then unused.
   */
  free?: boolean;
  position?: Vec3;
  yaw?: number;
  pitch?: number;
  roll?: number;
  /** false: no columns under this block (by default air grid blocks get the game editor's columns). */
  pillars?: boolean;
  /**
   * A pillar block (column piece), placed with the game's pillar flag: either placed by hand (saved with the map) or
   * generated under an air block (only in exports).
   */
  pillar?: boolean;
  /** The game's block colour (absent: Default, the block's own look, e.g. wooden road columns). Columns take it too. */
  color?: ItemColor;
  /** Placed by the scenery generator (its theme id): generating again replaces these, "Remove scenery" deletes them. */
  scenery?: string;
  /** Extra block flag bits for the game (see GROUND_ADAPTER_FLAG). */
  extraFlags?: number;
  /** The game's sub-variant of the block (absent: 0), e.g. 1 on the hill's outer corner. */
  subVariant?: number;
  /**
   * Generated columns only (preview, never saved): the surface modifier of the block they stand under, whose materials
   * they take (concrete walls under grass platforms, ...), as in game.
   */
  modifier?: string;
}

export const BLOCK_CELL = 32;
export const BLOCK_LEVEL = 8;

/**
 * Wraps a bank angle into (-180°, 180°]. Between two points the road rolls the shorter way round, so a corkscrew is
 * a few points stepping round (e.g. 0 → 120 → -120 → 0) and closed tracks close by themselves.
 */
export function wrapBank(degrees: number): number {
  let a = degrees % 360;
  if (a > 180) a -= 360;
  if (a <= -180) a += 360;
  return Math.round(a * 10) / 10;
}

/** Half the size of a 48×48 map: the game's grid and ground. Grid blocks and the generated scenery stay inside it. */
export const MAP_HALF_EXTENT = 768;
/**
 * How far from the centre track points, free blocks and items may go: 2 km beyond each edge of the grid. The game
 * keeps and drives items and free blocks out there (tested to 4 km from the grid's corner); only its editor's camera
 * stays above the grid, and there is no ground. The grid itself cannot grow: maps wider than 48 crash the game.
 */
export const WORLD_HALF_EXTENT = MAP_HALF_EXTENT + 2048;

export const add = (a: Vec3, b: Vec3): Vec3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
export const sub = (a: Vec3, b: Vec3): Vec3 => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
export const scale = (a: Vec3, s: number): Vec3 => [a[0] * s, a[1] * s, a[2] * s];
export const length = (a: Vec3): number => Math.hypot(a[0], a[1], a[2]);
export const lerp = (a: Vec3, b: Vec3, t: number): Vec3 => add(a, scale(sub(b, a), t));
export const normalize = (a: Vec3): Vec3 => {
  const l = length(a);
  return l < 1e-9 ? [0, 0, 0] : scale(a, 1 / l);
};

export const segmentCount = (t: Track) => (t.closed ? t.points.length : Math.max(0, t.points.length - 1));

/** Keeps exactly one segment entry per spline segment (same rule as Track.SyncSegments in C#). */
export function syncSegments(t: Track): void {
  const n = segmentCount(t);
  while (t.segments.length < n) {
    const last = t.segments[t.segments.length - 1];
    t.segments.push(last ? { ...last } : defaultSegment());
  }
  t.segments.length = n;
  t.objects = t.objects.filter((o) => o.segment >= 0 && o.segment < n);
  // Borders are set on the points: a segment has them when either end does.
  t.segments.forEach((s, i) => (s.borders = !!t.points[i]?.borders || !!t.points[(i + 1) % t.points.length]?.borders));
}

/** A straight 128 m track through `center` (default: the map center, 16 m up), heading along the horizontal `direction`. */
export function newTrack(center: Vec3 = [0, 16, 0], direction: Vec3 = [0, 0, 1]): Track {
  let dir = normalize([direction[0], 0, direction[2]]);
  if (length(dir) === 0) dir = [0, 0, 1];
  const point = (along: number): TrackPoint => ({
    position: add(center, scale(dir, along)), handleIn: scale(dir, -24), handleOut: scale(dir, 24),
    bank: 0, width: DEFAULT_ROAD_WIDTH, curve: 0, flat: 0, sausage: 0, edgeAnchor: 0, borderHeight: DEFAULT_BORDER_HEIGHT, borders: true, waypoint: "None",
  });
  return { closed: false, points: [point(-64), point(64)], segments: [defaultSegment()], objects: [] };
}

export function newMap(): TrackMap {
  return { version: 3, name: "My Track", laps: 3, tracks: [newTrack()], blocks: [], items: [] };
}

export const cloneMap = (m: TrackMap): TrackMap => structuredClone(m);

/** Appends a point after the last one, continuing in its outgoing direction. */
export function appendPoint(t: Track, at?: Vec3): number {
  const last = t.points[t.points.length - 1];
  let dir = normalize(last.handleOut);
  if (length(dir) === 0) dir = [0, 0, 1];
  const position: Vec3 = at ?? add(last.position, scale(dir, 64));
  const toNew = sub(position, last.position);
  const handleLen = Math.max(4, length(toNew) / 3);
  // Arrive along the direction from the last point's outgoing control point, so the new segment
  // continues the existing curve instead of kinking towards the click.
  let newDir = normalize(sub(position, add(last.position, last.handleOut)));
  if (length(newDir) === 0) newDir = normalize(toNew);
  t.points.push({
    position,
    handleIn: scale(newDir, -handleLen),
    handleOut: scale(newDir, handleLen),
    bank: 0,
    width: last.width,
    curve: last.curve,
    flat: last.flat,
    sausage: last.sausage,
    edgeAnchor: last.edgeAnchor,
    borderHeight: last.borderHeight,
    borders: last.borders,
    waypoint: "None",
  });
  syncSegments(t);
  return t.points.length - 1;
}

/** Control points of segment `s` as a cubic Bézier. */
export function segmentBezier(t: Track, s: number): [Vec3, Vec3, Vec3, Vec3] {
  const a = t.points[s];
  const b = t.points[(s + 1) % t.points.length];
  return [a.position, add(a.position, a.handleOut), add(b.position, b.handleIn), b.position];
}

export function bezierAt([p0, p1, p2, p3]: [Vec3, Vec3, Vec3, Vec3], u: number): Vec3 {
  const q0 = lerp(p0, p1, u), q1 = lerp(p1, p2, u), q2 = lerp(p2, p3, u);
  return lerp(lerp(q0, q1, u), lerp(q1, q2, u), u);
}

/** Bézier parameter of the point on segment `s` closest to `target`. */
export function closestOnSegment(t: Track, s: number, target: Vec3): number {
  const curve = segmentBezier(t, s);
  const dist = (u: number) => length(sub(bezierAt(curve, u), target));
  let best = 0;
  for (let i = 1; i <= 128; i++) if (dist(i / 128) < dist(best)) best = i / 128;
  // Refine around the best sample.
  let lo = Math.max(0, best - 1 / 128), hi = Math.min(1, best + 1 / 128);
  for (let i = 0; i < 30; i++) {
    const m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
    if (dist(m1) < dist(m2)) hi = m2; else lo = m1;
  }
  return (lo + hi) / 2;
}

/** Arc-length fraction (0..1) of Bézier parameter `u` on segment `s`. */
function arcFraction(t: Track, s: number, u: number): number {
  const curve = segmentBezier(t, s);
  let total = 0, upTo = 0;
  let prev = curve[0];
  for (let i = 1; i <= 256; i++) {
    const v = i / 256;
    const p = bezierAt(curve, v);
    const d = length(sub(p, prev));
    total += d;
    if (v <= u) upTo += d;
    prev = p;
  }
  return total > 0 ? upTo / total : u;
}

/**
 * Splits segment `s` at Bézier parameter `at` (default: middle) with de Casteljau, so the curve shape does not
 * change. The new point takes the width, shape, bank ... the road has there (same blending as the core).
 */
export function splitSegment(t: Track, s: number, at = 0.5): number {
  const a = t.points[s];
  const b = t.points[(s + 1) % t.points.length];
  const [p0, p1, p2, p3] = segmentBezier(t, s);
  const q0 = lerp(p0, p1, at), q1 = lerp(p1, p2, at), q2 = lerp(p2, p3, at);
  const r0 = lerp(q0, q1, at), r1 = lerp(q1, q2, at);
  const mid = lerp(r0, r1, at);

  // The core blends width and shape with a smoothstep over the arc length, banks along the shorter way.
  const fraction = arcFraction(t, s, at);
  const smooth = fraction * fraction * (3 - 2 * fraction);
  const blend = (key: "width" | "curve" | "flat" | "sausage" | "edgeAnchor" | "borderHeight") => a[key] + (b[key] - a[key]) * smooth;

  a.handleOut = sub(q0, p0);
  b.handleIn = sub(q2, p3);
  const point: TrackPoint = {
    position: mid, handleIn: sub(r0, mid), handleOut: sub(r1, mid),
    bank: wrapBank(a.bank + wrapBank(b.bank - a.bank) * fraction),
    width: blend("width"), curve: blend("curve"), flat: blend("flat"), sausage: blend("sausage"), edgeAnchor: blend("edgeAnchor"),
    borderHeight: blend("borderHeight"),
    // On if either end is: the segment keeps its borders, fading where it did.
    borders: a.borders || b.borders,
    waypoint: "None",
  };
  t.points.splice(s + 1, 0, point);
  t.segments.splice(s + 1, 0, { ...t.segments[s] });
  // Objects keep their place: the two halves split the segment's arc length at `fraction`.
  for (const o of t.objects) {
    if (o.segment > s) o.segment++;
    else if (o.segment === s && o.at > fraction) {
      o.segment = s + 1;
      o.at = (o.at - fraction) / (1 - fraction);
    } else if (o.segment === s) o.at = fraction > 0 ? o.at / fraction : 0;
  }
  syncSegments(t);
  return s + 1;
}

/**
 * One smoothing pass over the given points (all points if omitted), applied with `amount` (0..1, 1 = full pass):
 * - positions: a full Taubin pass (smoothing step plus a slightly stronger inverse step, so kinks go away without
 *   the track shrinking), then each point moves `amount` of the way towards that result (open-track ends stay put);
 * - handles: blended towards smooth, aligned Catmull-Rom style tangents through the neighbours;
 * - bank: blended towards the neighbour average via the shortest angle, so screws mostly survive.
 * Width, cross-section shape, flat share and border height are left alone: smoothing only changes the path. All values are computed from the state before
 * the pass, so the result does not depend on point order.
 */
export function smoothPoints(t: Track, indices?: number[], amount = 1): void {
  const n = t.points.length;
  if (n < 3) return;
  const k = Math.max(0, Math.min(1, amount));
  const before = t.points.map((p) => structuredClone(p));
  const neighbour = (i: number, d: number): TrackPoint | null => {
    const j = i + d;
    if (t.closed) return before[(j + n) % n];
    return j >= 0 && j < n ? before[j] : null;
  };
  const targets = [...new Set(indices ?? before.map((_, i) => i))].filter((i) => i >= 0 && i < n);
  const movable = (i: number) => targets.includes(i) && neighbour(i, -1) !== null && neighbour(i, 1) !== null;
  const wrapIndex = (i: number) => (t.closed ? (i + n) % n : i);

  // Full-strength Taubin pass (λ = 0.5, μ = -0.53), then blended by the requested amount. Scaling λ/μ instead would
  // make low strengths almost do nothing, because the inverse step nearly cancels small smoothing steps.
  const step = (from: Vec3[], factor: number): Vec3[] => from.map((pos, i) => {
    if (!movable(i)) return pos;
    return lerp(pos, lerp(from[wrapIndex(i - 1)], from[wrapIndex(i + 1)], 0.5), factor);
  });
  const full = step(step(before.map((p) => p.position), 0.5), -0.53);
  const smoothed = before.map((p, i) => (movable(i) ? lerp(p.position, full[i], k) : p.position));

  for (const i of targets) {
    if (!movable(i)) continue; // open-track ends keep their position, handles and values
    const prev = neighbour(i, -1)!, next = neighbour(i, 1)!, self = before[i], p = t.points[i];

    p.position = smoothed[i];

    // Smooth tangent through the neighbours' new positions, blended with the current handles and kept aligned.
    const prevPos = smoothed[wrapIndex(i - 1)], nextPos = smoothed[wrapIndex(i + 1)];
    const dir = normalize(sub(nextPos, prevPos));
    if (length(dir) > 0) {
      const out = lerp(self.handleOut, scale(dir, length(sub(nextPos, p.position)) / 3), k);
      const inn = lerp(self.handleIn, scale(dir, -length(sub(p.position, prevPos)) / 3), k);
      const aligned = normalize(sub(out, inn));
      if (length(aligned) > 0) {
        p.handleOut = scale(aligned, length(out));
        p.handleIn = scale(aligned, -length(inn));
      }
    }

    const half = k * 0.5;
    const toPrev = wrapBank(prev.bank - self.bank), toNext = wrapBank(next.bank - self.bank);
    p.bank = wrapBank(self.bank + half * (toPrev + toNext) / 2);
  }
}

export function deletePoint(t: Track, i: number): boolean {
  if (t.points.length <= 2) return false;
  // Removing a point merges its incoming and outgoing segment (an end point just loses its segment). Objects on the
  // merged segments keep roughly their place; objects on a lost segment go with it.
  const count = segmentCount(t);
  const incoming = i > 0 ? i - 1 : t.closed ? count - 1 : -1;
  const outgoing = i < count ? i : -1;
  const removed = i === 0 ? 0 : Math.min(i, t.segments.length - 1);
  if (incoming >= 0 && outgoing >= 0) {
    const [before, after] = [segmentLength(t, incoming), segmentLength(t, outgoing)];
    const total = Math.max(1e-9, before + after);
    for (const o of t.objects) {
      if (o.segment === incoming) o.at = (o.at * before) / total;
      else if (o.segment === outgoing) {
        o.segment = incoming;
        o.at = (before + o.at * after) / total;
      }
    }
  } else t.objects = t.objects.filter((o) => o.segment !== removed);
  for (const o of t.objects) if (o.segment > removed) o.segment--;
  t.points.splice(i, 1);
  // Removing a point merges its two neighbouring segments; keep the earlier segment's settings.
  const seg = Math.min(i, t.segments.length - 1);
  if (i === 0) t.segments.splice(0, 1);
  else t.segments.splice(seg, 1);
  if (t.points.length < 3) t.closed = false;
  syncSegments(t);
  return true;
}

/** Approximate arc length of segment `s`. */
function segmentLength(t: Track, s: number): number {
  const curve = segmentBezier(t, s);
  let total = 0;
  let prev = curve[0];
  for (let i = 1; i <= 64; i++) {
    const p = bezierAt(curve, i / 64);
    total += length(sub(p, prev));
    prev = p;
  }
  return total;
}

/** Sets one handle and keeps the opposite one aligned (its own length is kept). */
export function setHandle(p: TrackPoint, which: "in" | "out", value: Vec3): void {
  const otherKey = which === "in" ? "handleOut" : "handleIn";
  const ownKey = which === "in" ? "handleIn" : "handleOut";
  p[ownKey] = value;
  const dir = normalize(value);
  if (length(dir) > 0) p[otherKey] = scale(dir, -length(p[otherKey]));
}

/**
 * Widths used to be stored per segment. Old files: each point gets the average of its incoming and outgoing
 * segment (same rule as TrackJson.LegacyPointWidth in C#), and the segment widths are dropped.
 */
function migrateWidths(t: Track): void {
  const legacy = t.segments.map((s) => (s as { width?: number }).width);
  t.points.forEach((p, i) => {
    if (typeof p.width === "number") return;
    const incoming = i > 0 ? i - 1 : t.closed ? t.segments.length - 1 : -1;
    const widths = [legacy[incoming], legacy[i]].filter((w): w is number => typeof w === "number");
    p.width = widths.length ? widths.reduce((a, b) => a + b) / widths.length : 16;
  });
  for (const s of t.segments) delete (s as { width?: number }).width;
}

/**
 * Reads a track file: a version 2 or 3 map, or a version 1 single track (becomes a map with that one track). Version 1
 * files got an automatic start and finish when they had none; those are now added as explicit waypoints. Before
 * version 3, the width of points with borders included the curbs' inset (see LEGACY_CURB_INSET).
 */
export function parseMap(json: string): TrackMap {
  const doc = JSON.parse(json) as Omit<Partial<TrackMap>, "version"> & Partial<Track> & { version?: number };
  if (doc.version !== 1 && doc.version !== 2 && doc.version !== 3) throw new Error(`Unsupported track version ${doc.version}`);
  // Read the map fields first: a version 1 file is the track itself, and normalizing it drops them.
  const { version, name = "Untitled", laps = 3 } = doc;
  const parts = version === 1 ? [doc as Track] : doc.tracks ?? [];
  if (parts.length === 0) throw new Error("The file has no track");
  const tracks = parts.map((p, i) => normalizeTrack(p, parts.length > 1 ? `Track ${i + 1}` : "The track"));
  if (version === 1) addLegacyWaypoints(tracks[0]);
  for (const t of tracks) waypointsToGates(t);
  if (version < 3)
    for (const t of tracks)
      for (const p of t.points) if (p.borders) p.width = Math.max(1, p.width - 2 * LEGACY_CURB_INSET);
  const blocks = (Array.isArray(doc.blocks) ? doc.blocks : [])
    .filter((b) => typeof b?.name === "string" && b.name.length > 0)
    .map((b): VanillaBlock => {
      const block: VanillaBlock = { name: b.name, x: Math.round(b.x ?? 0), y: Math.max(0, Math.round(b.y ?? 0)), z: Math.round(b.z ?? 0), dir: (((Math.round(b.dir ?? 0)) % 4) + 4) % 4 };
      if (b.pillars === false) block.pillars = false;
      if (b.pillar && !b.free) block.pillar = true;
      if (b.color && b.color !== "Default" && (ITEM_COLORS as readonly string[]).includes(b.color)) block.color = b.color;
      if (typeof b.scenery === "string" && b.scenery) block.scenery = b.scenery;
      if (typeof b.subVariant === "number" && Number.isInteger(b.subVariant) && b.subVariant > 0) block.subVariant = b.subVariant;
      if (typeof b.extraFlags === "number" && Number.isInteger(b.extraFlags) && b.extraFlags > 0) block.extraFlags = b.extraFlags;
      if (b.free && Array.isArray(b.position) && b.position.length === 3)
        Object.assign(block, { free: true, position: b.position.map(Number) as Vec3, yaw: b.yaw ?? 0, pitch: b.pitch ?? 0, roll: b.roll ?? 0 });
      return block;
    });
  const items = (Array.isArray(doc.items) ? doc.items : [])
    .filter((i) => typeof i?.name === "string" && i.name.length > 0 && Array.isArray(i.position) && i.position.length === 3)
    .map((i): VanillaItem => ({
      name: i.name, position: i.position.map(Number) as Vec3, yaw: Number(i.yaw) || 0,
      ...(typeof i.scenery === "string" && i.scenery ? { scenery: i.scenery } : {}),
    }));
  const t = doc.terrain;
  const terrain: TerrainGrid | undefined = t && Number.isInteger(t.columns) && Number.isInteger(t.rows) && t.columns > 1 && t.rows > 1
    && Array.isArray(t.heights) && t.heights.length === t.columns * t.rows && t.cell > 0
    ? {
      surface: t.surface === "Dirt" || t.surface === "Ice" ? t.surface : "Grass", cell: Number(t.cell), x0: Number(t.x0) || 0,
      z0: Number(t.z0) || 0, columns: t.columns, rows: t.rows, skirt: Math.max(0, Number(t.skirt) || 0),
      heights: t.heights.map((h: unknown) => (typeof h === "number" && Number.isFinite(h) ? h : null)),
      ...(typeof t.rockAngle === "number" && Number.isFinite(t.rockAngle) ? { rockAngle: Math.max(0, Math.min(90, t.rockAngle)) } : {}),
      ...((TERRAIN_QUALITIES as readonly string[]).includes(t.quality as string) && t.quality !== "High" ? { quality: t.quality as TerrainQuality } : {}),
      // The brushes' dabs (five numbers each).
      ...Object.fromEntries((["edits", "editsOver", "smooths", "smoothsOver"] as const).flatMap((k) =>
        Array.isArray(t[k]) && t[k].length > 0 && t[k].length % 5 === 0 && t[k].every((v: unknown) => typeof v === "number" && Number.isFinite(v)) ? [[k, t[k] as number[]]] : [])),
    } : undefined;
  const sceneryOptions = doc.sceneryOptions && typeof doc.sceneryOptions === "object" && !Array.isArray(doc.sceneryOptions) ? doc.sceneryOptions : undefined;
  return { version: 3, name, laps, tracks, blocks, ...(items.length ? { items } : {}), ...(doc.noStadium === true ? { noStadium: true as const } : {}), ...((MAP_ENVIRONMENTS as readonly string[]).includes(doc.environment as string) ? { environment: doc.environment as MapEnvironment } : {}), ...(terrain ? { terrain } : {}), ...(sceneryOptions ? { sceneryOptions } : {}) };
}

/** How far a start on an open track's first point and a finish on its last stood in from the road's end (WaypointBuilder.EndInset in the core). */
const LEGACY_END_INSET = 16;

/** A segment's length, roughly (its curve in 48 straight pieces): for placing things a distance along it. */
function roughSegmentLength(t: Track, segment: number): number {
  const curve = segmentBezier(t, segment);
  let length = 0, from = bezierAt(curve, 0);
  for (let k = 1; k <= 48; k++) {
    const to = bezierAt(curve, k / 48);
    length += Math.hypot(to[0] - from[0], to[1] - from[1], to[2] - from[2]);
    from = to;
  }
  return length;
}

/**
 * Start, checkpoints and finish stood on the spline's points until 0.7.0 (TrackPoint.waypoint): each becomes a Gate
 * object at its point, with its height, plane and linked group. A start on an open track's first point and a finish
 * on its last stood 16 m in from the road's end: they are put there, so the map stays as it was.
 */
function waypointsToGates(t: Track): void {
  const last = t.points.length - 1;
  // (At most a third of the segment in, as the core's inset was at most a third of the road.)
  const inset = (segment: number) => Math.min(1 / 3, LEGACY_END_INSET / Math.max(1, roughSegmentLength(t, segment)));
  t.points.forEach((p, i) => {
    const point = p as TrackPoint & { gateHeight?: number; gatePlane?: boolean; checkpointGroup?: number };
    if (p.waypoint && p.waypoint !== "None" && t.segments.length > 0) {
      const atEnd = !t.closed && i === last;
      const segment = Math.min(atEnd ? i - 1 : i, t.segments.length - 1);
      const at = !t.closed && i === 0 && p.waypoint === "Start" ? inset(segment) : atEnd && p.waypoint === "Finish" ? 1 - inset(segment) : atEnd ? 1 : 0;
      t.objects.push({
        kind: "Gate", segment, at, offset: 0, shape: "Round", length: 1, width: 1,
        height: point.gateHeight ?? DEFAULT_GATE_HEIGHT, gate: p.waypoint,
        ...(point.gatePlane === false ? { plane: false as const } : {}),
        ...(p.waypoint === "Checkpoint" && (point.checkpointGroup ?? 0) > 0 ? { checkpointGroup: point.checkpointGroup } : {}),
      });
    }
    p.waypoint = "None";
    delete point.gateHeight;
    delete point.gatePlane;
    delete point.checkpointGroup;
  });
}

/** What version 1 did automatically: start (start/finish when closed) at the first point, finish at the last. */
function addLegacyWaypoints(t: Track): void {
  const points = t.points;
  if (!points.some((p) => p.waypoint === "Start" || p.waypoint === "StartFinish")) points[0].waypoint = t.closed ? "StartFinish" : "Start";
  if (!t.closed && !points.some((p) => p.waypoint === "Finish" || p.waypoint === "StartFinish")) points[points.length - 1].waypoint = "Finish";
}

function normalizeTrack(t: Track, label: string): Track {
  if (!Array.isArray(t.points) || t.points.length < 2) throw new Error(`${label} needs at least two points`);
  t.closed ??= false;
  t.segments ??= [];
  migrateWidths(t);
  for (const key of ["version", "name", "laps"]) delete (t as unknown as Record<string, unknown>)[key];
  t.objects = (t.objects ?? []).map((o) => ({ ...o, height: o.height ?? 4 }));
  // (Gates from before they had three looks: `frame` was the game's flat gate, without it the ring.)
  for (const o of t.objects as (TrackObject & { frame?: boolean })[])
    if (o.kind === "Gate" && !o.look && "frame" in o) { if (o.frame) o.look = "flat"; delete o.frame; }
  // (Reactor boost gates were "plain" or "along the gate" until 0.7.4: both lifted the car, as "up" does now.)
  for (const o of t.objects)
    if (o.kind === "Gate" && (o.gate === "ReactorBoostOriented" || o.gate === "ReactorBoost2Oriented")) o.gate = o.gate.replace("Oriented", "");
  // "Keep edges" was a per-track switch for a day: every point of such a track keeps its edges fully.
  const legacy = t as Track & { shapeAnchor?: string };
  const legacyAnchor = legacy.shapeAnchor === "Edges" ? 1 : 0;
  delete legacy.shapeAnchor;
  for (const p of t.points) {
    p.edgeAnchor ??= legacyAnchor;
    // Banks used to be a running total for a while; as angles they mean the same orientation.
    p.bank = wrapBank(p.bank ?? 0);
    p.curve ??= 0;
    p.flat ??= 0;
    p.sausage ??= 0;
    delete (p as { flatOuter?: number }).flatOuter; // short-lived predecessor of "sausage"
    p.borderHeight ??= LEGACY_BORDER_HEIGHT;
    p.waypoint ??= "None";
    p.edgeAnchor = Math.max(0, Math.min(1, p.edgeAnchor));
    clampShape(p);
  }
  // Files from before gameplay effects existed.
  t.segments = t.segments.map((s) => ({ ...defaultSegment(), ...s }));
  migrateBorders(t);
  syncSegments(t);
  return t;
}

/**
 * Borders used to be set per segment. Old files: a point gets borders when every segment it joins had them, so each
 * segment keeps its borders (either end) and they fade out at the same points as before (where a segment without
 * borders begins).
 */
function migrateBorders(t: Track): void {
  if (t.points.some((p) => typeof p.borders === "boolean")) {
    for (const p of t.points) p.borders = !!p.borders;
    return;
  }
  const n = t.points.length;
  t.points.forEach((p, i) => {
    const joins = [i > 0 || t.closed ? t.segments[(i - 1 + n) % n] : undefined, i < t.segments.length ? t.segments[i] : undefined]
      .filter((s): s is TrackSegment => !!s);
    p.borders = joins.length > 0 && joins.every((s) => s.borders !== false);
  });
  // A lone segment with borders between two without: neither end qualifies, so both get them (it keeps its borders,
  // which now fade out over the neighbouring segments instead of inside it).
  t.segments.forEach((s, i) => {
    const [a, b] = [t.points[i], t.points[(i + 1) % n]];
    if (s.borders !== false && !a.borders && !b.borders) a.borders = b.borders = true;
  });
}
