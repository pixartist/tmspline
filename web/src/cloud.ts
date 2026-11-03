import { parseMap, type TrackMap } from "./track";

/**
 * TMSpline's hosted service, through the desktop app's local server (/api/cloud): it signs in with Authentik in the
 * user's browser and forwards these requests with the user's token. In the browser-only editor (no cloud link) all of
 * this stays hidden.
 */
export interface CloudStatus {
  available: boolean;
  signedIn: boolean;
  user: string | null;
  url: string | null;
  appVersion: string | null;
  loggingIn: boolean;
  error: string | null;
}

/** A track in the user's cloud list. */
export interface CloudTrack {
  id: string;
  name: string;
  version: number;
  updated: string;
  share: string | null;
}

interface SavedTrack extends CloudTrack {
  data: unknown;
}

interface SharedTrack {
  share: string;
  name: string;
  owner: string;
  updated: string;
  data: unknown;
}

interface Release {
  version: string;
  notes: string | null;
}

const HEADERS = { "X-TMSpline": "1" };
const JSON_HEADERS = { ...HEADERS, "Content-Type": "application/json" };
const SKIPPED_UPDATE_KEY = "tmspline.skippedUpdate";

export class CloudError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function problem(response: Response, fallback: string): Promise<CloudError> {
  let message = `${fallback} (${response.status})`;
  try {
    const p = await response.json();
    message = p.detail ?? p.title ?? message;
  } catch {
    // not a problem+json answer
  }
  if (response.status === 401) message = "You are signed out. Please sign in again.";
  return new CloudError(message, response.status);
}

export async function cloudStatus(): Promise<CloudStatus | null> {
  try {
    const response = await fetch("/api/cloud", { headers: HEADERS });
    return response.ok ? ((await response.json()) as CloudStatus) : null;
  } catch {
    return null;
  }
}

/** A request to the service's API (path below /api/). */
async function api(method: string, path: string, body?: unknown): Promise<Response> {
  return fetch(`/api/cloud/api/${path}`, {
    method,
    headers: body === undefined ? HEADERS : JSON_HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export async function listTracks(): Promise<CloudTrack[]> {
  const response = await api("GET", "tracks");
  if (!response.ok) throw await problem(response, "Could not load your tracks");
  return (await response.json()) as CloudTrack[];
}

export async function loadTrack(id: string): Promise<{ track: CloudTrack; map: TrackMap; json: string }> {
  const response = await api("GET", `tracks/${encodeURIComponent(id)}`);
  if (!response.ok) throw await problem(response, "Could not load the track");
  const saved = (await response.json()) as SavedTrack;
  const json = JSON.stringify(saved.data);
  return { track: saved, map: parseMap(json), json };
}

export async function deleteTrack(id: string): Promise<void> {
  const response = await api("DELETE", `tracks/${encodeURIComponent(id)}`);
  if (!response.ok && response.status !== 404) throw await problem(response, "Could not delete the track");
}

export async function shareTrack(id: string): Promise<{ share: string; url: string }> {
  const response = await api("POST", `tracks/${encodeURIComponent(id)}/share`);
  if (!response.ok) throw await problem(response, "Could not share the track");
  return (await response.json()) as { share: string; url: string };
}

export async function unshareTrack(id: string): Promise<void> {
  const response = await api("DELETE", `tracks/${encodeURIComponent(id)}/share`);
  if (!response.ok && response.status !== 404) throw await problem(response, "Could not stop sharing");
}

/** A shared track by its link (…/s/{id}) or id. */
export async function loadShared(link: string): Promise<{ shared: SharedTrack; map: TrackMap }> {
  const id = /([a-z0-9]{6,32})\/?$/.exec(link.trim())?.[1];
  if (!id) throw new CloudError("That is not a TMSpline share link.", 400);
  const response = await api("GET", `shared/${id}`);
  if (response.status === 404) throw new CloudError("This link does not exist (any more): its owner may have stopped sharing it.", 404);
  if (!response.ok) throw await problem(response, "Could not load the shared track");
  const shared = (await response.json()) as SharedTrack;
  return { shared, map: parseMap(JSON.stringify(shared.data)) };
}

export async function openWebsite(path = "/"): Promise<void> {
  await fetch(`/api/cloud/open?path=${encodeURIComponent(path)}`, { method: "POST", headers: HEADERS });
}

/** Signs in through the user's browser; resolves once signed in (or with the reason it did not work). */
export async function signIn(onWaiting: () => void): Promise<CloudStatus> {
  const response = await fetch("/api/cloud/login", { method: "POST", headers: HEADERS });
  if (!response.ok) throw await problem(response, "Could not start signing in");
  let status = (await response.json()) as CloudStatus;
  if (status.signedIn) return status;
  onWaiting();
  const deadline = Date.now() + 10 * 60_000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 1500));
    status = (await cloudStatus()) ?? status;
    if (status.signedIn || !status.loggingIn) return status;
  }
  return status;
}

export async function signOut(): Promise<CloudStatus | null> {
  await fetch("/api/cloud/logout", { method: "POST", headers: HEADERS });
  return cloudStatus();
}

/** A newer release than this app, unless the user dismissed that version. */
export async function availableUpdate(status: CloudStatus): Promise<Release | null> {
  if (!status.signedIn || !status.appVersion) return null;
  const response = await api("GET", "app/latest");
  if (response.status !== 200) return null;
  const release = (await response.json()) as Release;
  if (compareVersions(release.version, status.appVersion) <= 0) return null;
  return localStorage.getItem(SKIPPED_UPDATE_KEY) === release.version ? null : release;
}

export const skipUpdate = (version: string) => localStorage.setItem(SKIPPED_UPDATE_KEY, version);

function compareVersions(a: string, b: string): number {
  const pa = a.split(/[.-]/).map((x) => Number.parseInt(x, 10) || 0), pb = b.split(/[.-]/).map((x) => Number.parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d) return Math.sign(d);
  }
  return 0;
}

// ---- sync -----------------------------------------------------------------------------------------------------------

/** Which cloud track the autosaved map is, and what was last uploaded (a hash, so big maps are not stored twice). */
interface Link {
  id: string;
  version: number | null;
  synced: string | null;
}

export type SyncState = "off" | "saved" | "saving" | "offline" | "conflict" | "error";

/** 53-bit string hash (cyrb53): tells whether the map changed since the last upload. */
function hash(text: string): string {
  let h1 = 0xdeadbeef, h2 = 0x41c6ce57;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    h1 = Math.imul(h1 ^ c, 2654435761);
    h2 = Math.imul(h2 ^ c, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
}

const newId = () => crypto.randomUUID();

/**
 * Keeps the autosaved map in the cloud while signed in: every local save is uploaded a moment later (only when it
 * changed). Each upload names the version it replaces; if another PC saved the track meanwhile, the service refuses
 * and the user picks which one to keep. Without a connection the map stays saved locally and is uploaded later.
 */
export class CloudSync {
  state: SyncState = "off";
  message = "";
  signedIn = false;
  private link: Link;
  private timer = 0;
  private busy = false;
  private pending: string | null = null;

  constructor(private readonly key: string, private readonly onState: () => void,
    private readonly onConflict: (theirs: CloudTrack) => void) {
    this.link = this.read() ?? { id: newId(), version: null, synced: null };
  }

  get trackId(): string {
    return this.link.id;
  }

  private read(): Link | null {
    try {
      const l = JSON.parse(localStorage.getItem(this.key) ?? "null") as Link | null;
      return l && typeof l.id === "string" ? l : null;
    } catch {
      return null;
    }
  }

  private write(): void {
    localStorage.setItem(this.key, JSON.stringify(this.link));
  }

  private set(state: SyncState, message = ""): void {
    this.state = state;
    this.message = message;
    this.onState();
  }

  setSignedIn(signedIn: boolean, json: string): void {
    this.signedIn = signedIn;
    if (!signedIn) this.set("off");
    else this.changed(json);
  }

  /** The map is a new track (New, opened from a file or a share link): the next upload creates it in the cloud. */
  detach(): void {
    this.link = { id: newId(), version: null, synced: null };
    this.write();
  }

  /** The map was opened from the cloud. */
  attach(track: CloudTrack, json: string): void {
    this.link = { id: track.id, version: track.version, synced: hash(json) };
    this.write();
    if (this.signedIn) this.set("saved");
  }

  /** The map was saved locally; uploads it after a moment if it changed. */
  changed(json: string): void {
    if (!this.signedIn) return;
    if (this.state === "conflict") return;
    if (hash(json) === this.link.synced) {
      if (this.state !== "saving") this.set("saved");
      return;
    }
    this.pending = json;
    clearTimeout(this.timer);
    this.timer = window.setTimeout(() => void this.upload(), 1500);
  }

  /** Uploads now if the map changed since the last upload (e.g. before sharing it). */
  flush(json: string): void {
    if (!this.signedIn || this.state === "conflict") return;
    if (hash(json) === this.link.synced) {
      this.set("saved");
      return;
    }
    clearTimeout(this.timer);
    this.pending = json;
    this.set("saving");
    void this.upload();
  }

  /** Uploads now if needed and waits for it (at most a few seconds), e.g. before the app closes for an update. */
  async flushAndWait(json: string): Promise<void> {
    this.flush(json);
    const deadline = Date.now() + 8000;
    while (this.state === "saving" && Date.now() < deadline) await new Promise((r) => setTimeout(r, 100));
  }

  /** Keep this version after a conflict: overwrite the one in the cloud. */
  overwrite(json: string): void {
    this.link.version = null;
    this.link.synced = null;
    this.set("saving");
    this.pending = json;
    void this.upload();
  }

  private async upload(): Promise<void> {
    if (this.busy || this.pending === null) return;
    const json = this.pending;
    this.pending = null;
    this.busy = true;
    this.set("saving");
    try {
      const map = JSON.parse(json) as TrackMap;
      const response = await api("PUT", `tracks/${encodeURIComponent(this.link.id)}`,
        { name: map.name || "Untitled", data: map, baseVersion: this.link.version });
      if (response.ok) {
        const saved = (await response.json()) as CloudTrack;
        this.link = { id: saved.id, version: saved.version, synced: hash(json) };
        this.write();
        this.set("saved");
      } else if (response.status === 409) {
        this.set("conflict", "Changed on another PC");
        this.onConflict((await response.json()) as CloudTrack);
      } else if (response.status === 401) {
        this.signedIn = false;
        this.set("off");
      } else if (response.status >= 500) {
        this.pending ??= json;
        this.set("offline", "Not connected: saved on this PC, uploaded later");
        this.retryLater();
      } else {
        this.set("error", (await problem(response, "Upload failed")).message);
      }
    } catch {
      this.pending ??= json;
      this.set("offline", "Not connected: saved on this PC, uploaded later");
      this.retryLater();
    } finally {
      this.busy = false;
    }
    if (this.pending !== null && this.state === "saved") void this.upload();
  }

  private retryLater(): void {
    clearTimeout(this.timer);
    this.timer = window.setTimeout(() => void this.upload(), 30_000);
  }

  /** Short text for the status bar. */
  get label(): string {
    switch (this.state) {
      case "saved": return "☁ saved";
      case "saving": return "☁ saving…";
      case "offline": return "☁ offline";
      case "conflict": return "☁ changed elsewhere";
      case "error": return "☁ not saved";
      default: return "";
    }
  }
}
