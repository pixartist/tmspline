// Bridge to TmSpline.Core running in .NET WebAssembly (see src/TmSpline.Wasm), hosted in a Web Worker
// (previewWorker.ts) so rebuilds never block the editor. The preview mesh comes from the same code that
// generates the exported items.

import type { Track, TrackMap } from "./track";

export interface PreviewMesh {
  positions: Float32Array;
  normals: Float32Array;
  /** The export's base UVs. On the driving surface: U = distance along the track / 32 m, V = 0..1 across the road. */
  uvs: Float32Array;
  /** Per vertex: (distance along the track, across) on the driving surface, NaN elsewhere (for placing objects). */
  surface: Float32Array;
  indices: Uint32Array;
  /** Material index per triangle, into `materials`. */
  triangleMaterial: Int32Array;
  /** Track segment index per triangle (for picking). */
  triangleSegment: Int32Array;
  materials: string[];
  trackLength: number;
  /** Exact road frame at each spline point: tangent xyz, up xyz (6 floats per point). */
  pointFrames: Float32Array;
  /** One entry per boost piece: where it is, which way it pushes, and that direction before its tilt (in the road plane). */
  effects: { center: [number, number, number]; direction: [number, number, number]; roadDirection: [number, number, number]; effect: number }[];
  /** Distance where each segment starts; the last entry is the track length. */
  segmentStarts: Float32Array;
  /** Where each of the track's objects sits (same order as Track.objects). */
  objects: ObjectPlacement[];
}

export interface ObjectPlacement {
  /** Center on the driving surface, with the surface normal and the road direction there. */
  position: [number, number, number];
  normal: [number, number, number];
  tangent: [number, number, number];
  distance: number;
  /** Center across the surface, after the core clamped it (holes stay inside the road). */
  across: number;
  /** False if the object cannot be built there (e.g. a pillar under a road that faces sideways). */
  placed: boolean;
}

/** 0 = coarse (fast, while dragging), 1 = the export mesh at Low quality (finer qualities only add strips and columns). */
export type PreviewQuality = 0 | 1;

const worker = new Worker(new URL("./previewWorker.ts", import.meta.url), { type: "module" });
const pending = new Map<number, { resolve: (bytes: Uint8Array) => void; reject: (err: Error) => void }>();
let nextId = 1;
let ready: Promise<void> | null = null;
let resolveReady: () => void = () => {};
let rejectReady: (err: Error) => void = () => {};

worker.onmessage = (e: MessageEvent) => {
  const msg = e.data as { type: string; id?: number; bytes?: Uint8Array; message?: string };
  if (msg.type === "ready") {
    resolveReady();
    return;
  }
  const request = msg.id === undefined ? undefined : pending.get(msg.id);
  if (msg.type === "error" && !request) rejectReady(new Error(msg.message));
  if (!request) return;
  pending.delete(msg.id!);
  if (msg.type === "result") request.resolve(msg.bytes!);
  else request.reject(new Error(msg.message));
};

/** Starts the core in the worker; resolves once it can build meshes. */
export function loadCore(): Promise<void> {
  ready ??= new Promise<void>((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
    worker.postMessage({ type: "init" });
  });
  return ready;
}

export async function buildPreview(track: Track, quality: PreviewQuality = 1): Promise<PreviewMesh> {
  await loadCore();
  const id = nextId++;
  const bytes = await new Promise<Uint8Array>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    worker.postMessage({ type: "build", id, json: JSON.stringify(track), quality });
  });
  return decode(bytes);
}

/** The map's own terrain as the export builds it, in world space; the last `rockTriangles` triangles are rock. */
export interface TerrainMesh { positions: Float32Array; normals: Float32Array; uvs: Float32Array; indices: Uint32Array; rockTriangles: number }

/**
 * Builds the terrain preview: on the local server when it can (natively, many times faster than the WebAssembly core),
 * else in the worker.
 */
export async function buildTerrain(map: TrackMap): Promise<TerrainChunks> {
  try {
    const key = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;
    const response = await fetch(`/api/terrain?key=${key}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ track: map }),
    });
    if (response.ok) {
      const chunks = decodeChunks(new Uint8Array(await response.arrayBuffer()));
      if (chunks) return { chunks, key };
    }
  } catch { /* no server: the worker builds it */ }
  await loadCore();
  const id = nextId++;
  const bytes = await new Promise<Uint8Array>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    worker.postMessage({ type: "terrain", id, json: JSON.stringify(map) });
  });
  return { chunks: [{ x: 0, z: 0, mesh: decodeTerrain(bytes) }], key: null };
}

/** The terrain in chunks (squares of land, each its own mesh); `key`: the server's name for it, for dabTerrain, or null. */
export interface TerrainChunks { chunks: TerrainChunk[]; key: string | null }
/** A chunk at its place in the land's grid of chunks; no triangles: nothing is left there. */
export interface TerrainChunk { x: number; z: number; mesh: TerrainMesh }

/**
 * The brush's dabs (x, y, z, radius, amount each; `over`: not cut by the roads) built into the terrain last built under
 * the key: the chunks they changed. Null when the server no longer has that terrain (ask for the whole again).
 */
export async function dabTerrain(key: string, dabs: number[], over: boolean, smooth = false): Promise<TerrainChunk[] | null> {
  const response = await fetch("/api/terrain/dab", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ key, dabs, over, smooth }),
  });
  if (!response.ok) return null;
  return decodeChunks(new Uint8Array(await response.arrayBuffer()));
}

/** The server's chunks ("CHNK", their number, then each: place, counts, arrays), or null when the bytes are something else. */
function decodeChunks(bytes: Uint8Array): TerrainChunk[] | null {
  const buffer = bytes.slice().buffer;
  const view = new DataView(buffer);
  if (bytes.length < 8 || view.getInt32(0, true) !== 0x4b4e4843) return null;
  const count = view.getInt32(4, true);
  const chunks: TerrainChunk[] = [];
  let o = 8;
  for (let k = 0; k < count; k++) {
    const x = view.getInt32(o, true), z = view.getInt32(o + 4, true);
    const vertexCount = view.getInt32(o + 8, true), triangleCount = view.getInt32(o + 12, true), rockTriangles = view.getInt32(o + 16, true);
    o += 20;
    const positions = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
    const normals = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
    const uvs = new Float32Array(buffer, o, vertexCount * 2); o += vertexCount * 8;
    const indices = new Uint32Array(new Int32Array(buffer, o, triangleCount * 3)); o += triangleCount * 12;
    chunks.push({ x, z, mesh: { positions, normals, uvs, indices, rockTriangles } });
  }
  return chunks;
}

function decodeTerrain(bytes: Uint8Array): TerrainMesh {
  const buffer = bytes.slice().buffer;
  const view = new DataView(buffer);
  const vertexCount = view.getInt32(0, true), triangleCount = view.getInt32(4, true), rockTriangles = view.getInt32(8, true);
  let o = 12;
  const positions = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
  const normals = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
  const uvs = new Float32Array(buffer, o, vertexCount * 2); o += vertexCount * 8;
  const indices = new Uint32Array(new Int32Array(buffer, o, triangleCount * 3));
  return { positions, normals, uvs, indices, rockTriangles };
}

function decode(bytes: Uint8Array): PreviewMesh {
  // Copy into an aligned buffer so typed array views can be created directly.
  const buffer = bytes.slice().buffer;
  const view = new DataView(buffer);
  let o = 0;
  const vertexCount = view.getInt32(o, true); o += 4;
  const triangleCount = view.getInt32(o, true); o += 4;
  const materialCount = view.getInt32(o, true); o += 4;
  const trackLength = view.getFloat32(o, true); o += 4;

  const decoder = new TextDecoder();
  const materials: string[] = [];
  for (let i = 0; i < materialCount; i++) {
    const len = view.getInt32(o, true); o += 4;
    materials.push(decoder.decode(new Uint8Array(buffer, o, len)));
    o += len + ((4 - (len % 4)) % 4);
  }

  const positions = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
  const normals = new Float32Array(buffer, o, vertexCount * 3); o += vertexCount * 12;
  const uvs = new Float32Array(buffer, o, vertexCount * 2); o += vertexCount * 8;
  const surface = new Float32Array(buffer, o, vertexCount * 2); o += vertexCount * 8;
  const indices = new Uint32Array(new Int32Array(buffer, o, triangleCount * 3)); o += triangleCount * 12;
  const triangleMaterial = new Int32Array(buffer, o, triangleCount); o += triangleCount * 4;
  const triangleSegment = new Int32Array(buffer, o, triangleCount); o += triangleCount * 4;
  const pointCount = view.getInt32(o, true); o += 4;
  const pointFrames = new Float32Array(buffer, o, pointCount * 6); o += pointCount * 24;

  const effectCount = view.getInt32(o, true); o += 4;
  const effects: PreviewMesh["effects"] = [];
  for (let i = 0; i < effectCount; i++, o += 40) {
    const f = new Float32Array(buffer, o, 9);
    effects.push({ center: [f[0], f[1], f[2]], direction: [f[3], f[4], f[5]], roadDirection: [f[6], f[7], f[8]], effect: view.getInt32(o + 36, true) });
  }

  const segmentCount = view.getInt32(o, true); o += 4;
  const segmentStarts = new Float32Array(buffer, o, segmentCount + 1); o += (segmentCount + 1) * 4;
  const objectCount = view.getInt32(o, true); o += 4;
  const objects: ObjectPlacement[] = [];
  for (let i = 0; i < objectCount; i++, o += 48) {
    const f = new Float32Array(buffer, o, 11);
    objects.push({
      position: [f[0], f[1], f[2]], normal: [f[3], f[4], f[5]], tangent: [f[6], f[7], f[8]], distance: f[9], across: f[10],
      placed: view.getInt32(o + 44, true) !== 0,
    });
  }

  return { positions, normals, uvs, surface, indices, triangleMaterial, triangleSegment, materials, trackLength, pointFrames, effects, segmentStarts, objects };
}
