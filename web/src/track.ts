// Track document — mirrors TrackJson.TrackDto in TmSpline.Core (camelCase JSON, version 2: a map with several tracks).
// Coordinates are meters relative to the map center at ground level, Y up.

export type Vec3 = [number, number, number];

export const SURFACES = [
  "RoadTech", "RoadDirt", "RoadBump", "RoadIce",
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
   * Cross-section bend in degrees: 0 = flat, negative = convex (sausage, down to -180),
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
  /** Height of the road borders in meters; blended smoothly towards the neighbouring points. */
  borderHeight: number;
  /** Race gate at this point. */
  waypoint: Waypoint;
}

export const DEFAULT_BORDER_HEIGHT = 0.8;
/**
 * Road width of new tracks: the driving surface of the game's road blocks between the inner faces of their curbs
 * (RoadTech/Dirt/Bump: x 2.7..29.3 in their 32 m cell). Our borders sit outside the width, as theirs do.
 */
export const DEFAULT_ROAD_WIDTH = 26.6;

/** Named cross-section shapes for the inspector. */
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
}

export const defaultSegment = (): TrackSegment =>
  ({ surface: "RoadTech", borders: true, borderMaterial: "Rubber", underside: true, color: "Default", effect: "None", effectYaw: 0, effectPitch: 0 });

export const OBJECT_KINDS = ["Pillar", "Hole", "Gap"] as const;
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
  /** Pillars: how far they stand up from the road, in meters. */
  height: number;
}

export function newObject(kind: TrackObjectKind, segment: number, at: number, offset: number): TrackObject {
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
export interface TrackMap {
  version: 2;
  name: string;
  /** Laps when the map has a start/finish. */
  laps: number;
  tracks: Track[];
  /** Vanilla game blocks on the map grid (exported by name; their meshes come from the game). */
  blocks: VanillaBlock[];
  /** Vanilla game items (trees, ...), exported by name; the editor draws stand-ins (their models are not converted). */
  items?: VanillaItem[];
}

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
  /** Exports only: extra block flag bits for the game (see GROUND_ADAPTER_FLAG). */
  extraFlags?: number;
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

/** Half the size of a 48×48 map; the editor keeps points inside these bounds. */
export const MAP_HALF_EXTENT = 768;

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
}

/** A straight 128 m track through `center` (default: the map center, 16 m up), heading along the horizontal `direction`. */
export function newTrack(center: Vec3 = [0, 16, 0], direction: Vec3 = [0, 0, 1]): Track {
  let dir = normalize([direction[0], 0, direction[2]]);
  if (length(dir) === 0) dir = [0, 0, 1];
  const point = (along: number): TrackPoint => ({
    position: add(center, scale(dir, along)), handleIn: scale(dir, -24), handleOut: scale(dir, 24),
    bank: 0, width: DEFAULT_ROAD_WIDTH, curve: 0, flat: 0, sausage: 0, borderHeight: DEFAULT_BORDER_HEIGHT, waypoint: "None",
  });
  return { closed: false, points: [point(-64), point(64)], segments: [defaultSegment()], objects: [] };
}

export function newMap(): TrackMap {
  return { version: 2, name: "My Track", laps: 3, tracks: [newTrack()], blocks: [] };
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
    borderHeight: last.borderHeight,
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
  const blend = (key: "width" | "curve" | "flat" | "sausage" | "borderHeight") => a[key] + (b[key] - a[key]) * smooth;

  a.handleOut = sub(q0, p0);
  b.handleIn = sub(q2, p3);
  const point: TrackPoint = {
    position: mid, handleIn: sub(r0, mid), handleOut: sub(r1, mid),
    bank: wrapBank(a.bank + wrapBank(b.bank - a.bank) * fraction),
    width: blend("width"), curve: blend("curve"), flat: blend("flat"), sausage: blend("sausage"),
    borderHeight: blend("borderHeight"),
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
 * Reads a track file: a version 2 map, or a version 1 single track (becomes a map with that one track). Version 1
 * files got an automatic start and finish when they had none; those are now added as explicit waypoints.
 */
export function parseMap(json: string): TrackMap {
  const doc = JSON.parse(json) as Omit<Partial<TrackMap>, "version"> & Partial<Track> & { version?: number };
  if (doc.version !== 1 && doc.version !== 2) throw new Error(`Unsupported track version ${doc.version}`);
  // Read the map fields first: a version 1 file is the track itself, and normalizing it drops them.
  const { version, name = "Untitled", laps = 3 } = doc;
  const parts = version === 2 ? doc.tracks ?? [] : [doc as Track];
  if (parts.length === 0) throw new Error("The file has no track");
  const tracks = parts.map((p, i) => normalizeTrack(p, parts.length > 1 ? `Track ${i + 1}` : "The track"));
  if (version === 1) addLegacyWaypoints(tracks[0]);
  const blocks = (Array.isArray(doc.blocks) ? doc.blocks : [])
    .filter((b) => typeof b?.name === "string" && b.name.length > 0)
    .map((b): VanillaBlock => {
      const block: VanillaBlock = { name: b.name, x: Math.round(b.x ?? 0), y: Math.max(0, Math.round(b.y ?? 0)), z: Math.round(b.z ?? 0), dir: (((Math.round(b.dir ?? 0)) % 4) + 4) % 4 };
      if (b.pillars === false) block.pillars = false;
      if (b.pillar && !b.free) block.pillar = true;
      if (b.color && b.color !== "Default" && (ITEM_COLORS as readonly string[]).includes(b.color)) block.color = b.color;
      if (typeof b.scenery === "string" && b.scenery) block.scenery = b.scenery;
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
  return { version: 2, name, laps, tracks, blocks, ...(items.length ? { items } : {}) };
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
  for (const p of t.points) {
    // Banks used to be a running total for a while; as angles they mean the same orientation.
    p.bank = wrapBank(p.bank ?? 0);
    p.curve ??= 0;
    p.flat ??= 0;
    p.sausage ??= 0;
    delete (p as { flatOuter?: number }).flatOuter; // short-lived predecessor of "sausage"
    p.borderHeight ??= DEFAULT_BORDER_HEIGHT;
    p.waypoint ??= "None";
  }
  // Files from before gameplay effects existed.
  t.segments = t.segments.map((s) => ({ ...defaultSegment(), ...s }));
  syncSegments(t);
  return t;
}
