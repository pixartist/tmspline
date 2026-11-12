import type { TrackMap } from "./track";

const AUTHOR_KEY = "tmspline.author";
const QUALITY_KEY = "tmspline.exportQuality";
/**
 * Per track name: the road's measured share of the map size (the size less the estimated terrain) divided by its estimate,
 * and the quality of that export (see MeshQualities.EstimateMapBytes).
 */
const CALIBRATION_KEY = "tmspline.sizeCalibration2";
/** The game's limit for custom items embedded in a map. */
const MAP_LIMIT_BYTES = 5_000_000;

const QUALITIES = ["Low", "Normal", "Fine", "High", "Extra", "Ultra"] as const;
type Quality = (typeof QUALITIES)[number];
const QUALITY_HINTS: Record<Quality, string> = {
  Low: "smallest; fast sweeping curves can feel bumpy",
  Normal: "smooth for most tracks",
  Fine: "smoother at high speed, still compact",
  High: "for very fast, high-g sections",
  Extra: "between High and Ultra in smoothness and size",
  Ultra: "smoothest; usually too big for long tracks",
};

interface Author {
  login: string;
  nickname: string;
}

/** GET /api/importer: NadeoImporter on the machine that exports (in the desktop app: the user's own). */
interface ImporterStatus {
  installed: boolean;
  canInstall: boolean;
  installing: boolean;
  received: number;
  size: number;
  error: string | null;
  source: string;
}

interface Estimate {
  quality: Quality;
  items: number;
  triangles: number;
  bytes: number;
  minBytes: number;
  maxBytes: number;
  /** The own terrain's part of the sizes: the same at every quality. */
  terrainBytes: number;
}

/** Shows the export dialog, sends the track to the server and downloads the resulting map. */
export function showExportDialog(track: TrackMap): void {
  const saved: Author = JSON.parse(localStorage.getItem(AUTHOR_KEY) ?? '{"login":"","nickname":""}');
  const savedQuality = (localStorage.getItem(QUALITY_KEY) ?? "Normal") as Quality;

  const dialog = document.createElement("dialog");
  dialog.className = "export";
  dialog.innerHTML = `
    <form method="dialog">
      <h2>Export map</h2>
      <p class="hint">The road is converted to custom items on the server and embedded into a .Map.Gbx.
        Put the file into <code>Documents\\Trackmania\\Maps</code> and open it in the editor.</p>
      <h2>Quality</h2>
      <div class="qualities">${QUALITIES.map((q) => `
        <label class="quality"><input type="radio" name="quality" value="${q}" ${q === savedQuality ? "checked" : ""} />
          <span class="name">${q}</span><span class="size" data-quality="${q}">…</span>
          <span class="hint">${QUALITY_HINTS[q]}</span></label>`).join("")}
      </div>
      <p class="hint estimate-note">Estimating map sizes…</p>
      <div class="importer" hidden>
        <p>Exporting needs Nadeo's <b>NadeoImporter</b>, which turns the road into the game's items. TMSpline cannot
          include it, but can download it from Nadeo (<code class="source"></code>, <span class="size"></span>)
          into its own folder.</p>
        <p class="progress" hidden></p>
        <button type="button" class="install">Download NadeoImporter</button>
      </div>
      <h2>Author</h2>
      <label class="row"><span>Account login</span><input name="login" placeholder="optional, 22 characters" maxlength="22" /></label>
      <label class="row"><span>Nickname</span><input name="nickname" placeholder="optional" maxlength="64" /></label>
      <p class="hint">The account login (from trackmania.io) is set as map author, so you can upload the map.</p>
      <p class="result" hidden></p>
      <p class="error" hidden></p>
      <div class="actions">
        <button value="cancel" formnovalidate>Close</button>
        <button value="model" class="model" title="The roads and the own terrain as a Wavefront OBJ file (metres, Y up), for Blender and other 3D programs. Without the game's blocks, trees and textures.">3D model (.obj)</button>
        <button value="game" class="game" hidden title="Write the map into Documents\Trackmania\Maps\TMSpline; the TMSpline Live plugin reloads it in the game's editor">Export to game</button>
        <button value="export" class="primary">Export</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);

  const form = dialog.querySelector("form")!;
  const login = form.elements.namedItem("login") as HTMLInputElement;
  const nickname = form.elements.namedItem("nickname") as HTMLInputElement;
  const error = dialog.querySelector<HTMLElement>(".error")!;
  const result = dialog.querySelector<HTMLElement>(".result")!;
  const note = dialog.querySelector<HTMLElement>(".estimate-note")!;
  const exportButton = dialog.querySelector<HTMLButtonElement>("button.primary")!;
  const gameButton = dialog.querySelector<HTMLButtonElement>("button.game")!;
  const modelButton = dialog.querySelector<HTMLButtonElement>("button.model")!;
  // Only local setups write into the game's folder (the server says so).
  void fetch("/api/health").then((r) => r.json()).then((h) => (gameButton.hidden = !h.exportToGame), () => {});
  void setupImporter(dialog, (ready) => (exportButton.disabled = gameButton.disabled = !ready));
  login.value = saved.login;
  nickname.value = saved.nickname;
  const quality = () => (form.querySelector<HTMLInputElement>('input[name="quality"]:checked')?.value ?? "Normal") as Quality;

  let estimates: Estimate[] = [];
  const showEstimates = () => {
    const measured = calibration(track.name);
    const factor = measured?.factor;
    for (const e of estimates) {
      const el = dialog.querySelector<HTMLElement>(`.size[data-quality="${e.quality}"]`)!;
      // After a real export of this track the road's part is scaled by how far off that one was; the terrain stays, it is the
      // same at every quality. That is close for the same quality; for others less so, since finer meshes compress a little better.
      const spread = measured?.quality === e.quality ? 0.05 : 0.2;
      const road = (e.bytes - e.terrainBytes) * (factor ?? 1);
      const [low, high] = factor ? [road * (1 - spread) + e.terrainBytes, road * (1 + spread) + e.terrainBytes] : [e.minBytes, e.maxBytes];
      const mid = road + e.terrainBytes;
      el.textContent = `≈ ${mb(mid)} (${mb(low)}–${mb(high)}) · ${(e.triangles / 1000).toFixed(0)}k triangles`;
      el.classList.toggle("over", low > MAP_LIMIT_BYTES);
      el.classList.toggle("near", low <= MAP_LIMIT_BYTES && high > MAP_LIMIT_BYTES);
    }
    const terrain = estimates[0]?.terrainBytes ?? 0;
    note.textContent = (factor ? "Road sizes are scaled by your last export of this track. " : "Sizes are estimates: how well the items compress depends on the track. ")
      + (terrain > 0 ? `Each includes about ${mb(terrain)} of terrain, which is the same at every quality. ` : "")
      + "The game's editor allows 5 MB of custom items per map; with Openplanet's \"Infinite embed size\" (it comes with Openplanet) a map can be about 7 MB in all and still be played online.";
  };
  void estimate(track).then((list) => {
    estimates = list;
    showEstimates();
  }, (err) => {
    note.textContent = `No size estimate: ${(err as Error).message}`;
    dialog.querySelectorAll<HTMLElement>(".size").forEach((el) => (el.textContent = ""));
  });

  form.addEventListener("submit", async (e) => {
    const action = (e.submitter as HTMLButtonElement | null)?.value;
    if (action === "model") {
      // The meshes alone: no importer, no author.
      e.preventDefault();
      const q = quality();
      modelButton.disabled = true;
      modelButton.textContent = "Building…";
      error.hidden = true;
      result.hidden = true;
      try {
        const { name, bytes } = await downloadModel(track, q);
        result.textContent = `Downloading ${name}: ${mb(bytes)} at ${q} quality. In Blender: File > Import > Wavefront (.obj).`;
        result.classList.remove("over");
        result.hidden = false;
      } catch (err) {
        showError((err as Error).message);
      } finally {
        modelButton.disabled = false;
        modelButton.textContent = "3D model (.obj)";
      }
      return;
    }
    if (action !== "export" && action !== "game") return;
    const toGame = action === "game";
    e.preventDefault();
    const author: Author = { login: login.value.trim(), nickname: nickname.value.trim() };
    if (author.login && !/^[A-Za-z0-9_-]{22}$/.test(author.login)) {
      showError("The account login is the 22-character id, e.g. from your trackmania.io profile URL.");
      return;
    }
    localStorage.setItem(AUTHOR_KEY, JSON.stringify(author));
    const q = quality();
    localStorage.setItem(QUALITY_KEY, q);

    const button = toGame ? gameButton : exportButton;
    const label = button.textContent;
    exportButton.disabled = gameButton.disabled = true;
    button.textContent = "Exporting…";
    error.hidden = true;
    result.hidden = true;
    try {
      const { name, bytes } = await (toGame ? exportToGame : exportMap)(track, author, q);
      const estimated = estimates.find((x) => x.quality === q);
      // Kept within reason: with much terrain and little road the road's part is a small difference of large numbers.
      if (estimated) setCalibration(track.name, { factor: Math.min(2, Math.max(0.5, (bytes - estimated.terrainBytes) / (estimated.bytes - estimated.terrainBytes))), quality: q });
      showEstimates();
      result.textContent = `${toGame ? "Wrote" : "Downloaded"} ${name}: ${mb(bytes)} at ${q} quality`
        + (bytes > MAP_LIMIT_BYTES ? " — over the game's 5 MB limit: try a lower quality, or use Openplanet's \"Infinite embed size\" (maps up to about 7 MB play online)." : ` (limit 5 MB).`);
      result.classList.toggle("over", bytes > MAP_LIMIT_BYTES);
      result.hidden = false;
    } catch (err) {
      showError((err as Error).message);
    } finally {
      exportButton.disabled = gameButton.disabled = false;
      button.textContent = label;
    }
  });
  dialog.addEventListener("close", () => dialog.remove());
  dialog.showModal();

  function showError(message: string) {
    error.textContent = message;
    error.hidden = false;
  }
}

/**
 * Where the importer can be downloaded (the desktop app) and is missing, the dialog offers the download and keeps the
 * export buttons off until it is there.
 */
async function setupImporter(dialog: HTMLDialogElement, setReady: (ready: boolean) => void): Promise<void> {
  const box = dialog.querySelector<HTMLElement>(".importer")!;
  const progress = box.querySelector<HTMLElement>(".progress")!;
  const install = box.querySelector<HTMLButtonElement>("button.install")!;
  const get = async () => {
    const response = await fetch("/api/importer");
    return response.ok ? ((await response.json()) as ImporterStatus) : null;
  };
  let status = await get().catch(() => null);
  // Older servers have no /api/importer; servers that cannot download it report a missing importer on export.
  if (!status || status.installed || !status.canInstall) return;
  box.querySelector<HTMLElement>(".source")!.textContent = status.source;
  box.querySelector<HTMLElement>(".size")!.textContent = mb(status.size);
  box.hidden = false;
  setReady(false);

  const follow = async () => {
    install.disabled = true;
    progress.hidden = false;
    while (status && status.installing && dialog.open) {
      progress.textContent = `Downloading… ${Math.round((100 * status.received) / Math.max(1, status.size))} %`;
      await new Promise((r) => setTimeout(r, 300));
      status = await get().catch(() => null);
    }
    if (status?.installed) {
      box.hidden = true;
      setReady(true);
      return;
    }
    progress.textContent = `The download failed: ${status?.error ?? "no answer from TMSpline"}`;
    install.disabled = false;
  };
  if (status.installing) void follow();
  install.addEventListener("click", async () => {
    const response = await fetch("/api/importer/install", { method: "POST" }).catch(() => null);
    status = response?.ok ? await get().catch(() => null) : null;
    if (status && !status.installing && !status.installed) status = { ...status, installing: true };
    await follow();
  });
}

const mb = (bytes: number) => `${(bytes / 1_000_000).toFixed(bytes < 10_000_000 ? 2 : 1)} MB`;

interface Calibration {
  factor: number;
  quality: Quality;
}

function calibration(name: string): Calibration | undefined {
  return (JSON.parse(localStorage.getItem(CALIBRATION_KEY) ?? "{}") as Record<string, Calibration>)[name];
}

function setCalibration(name: string, calibration: Calibration): void {
  const all = JSON.parse(localStorage.getItem(CALIBRATION_KEY) ?? "{}") as Record<string, Calibration>;
  all[name] = calibration;
  localStorage.setItem(CALIBRATION_KEY, JSON.stringify(all));
}

async function problemMessage(response: Response, fallback: string): Promise<string> {
  let message = `${fallback} (${response.status})`;
  try {
    const problem = await response.json();
    message = problem.detail ?? problem.title ?? message;
  } catch {
    // not a problem+json response
  }
  return response.status === 429 ? "Too many requests, please wait a minute." : message;
}

/** Map size estimates for every quality, from the server (it builds the meshes, without running the importer). */
async function estimate(track: TrackMap): Promise<Estimate[]> {
  const response = await fetch("/api/estimate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ track }),
  });
  if (!response.ok) throw new Error(await problemMessage(response, "Estimate failed"));
  return (await response.json()) as Estimate[];
}

function exportMap(track: TrackMap, author: Author, quality: Quality): Promise<{ name: string; bytes: number }> {
  return download("/api/export", { track, quality, authorLogin: author.login || null, authorNickname: author.nickname || null }, "track.Map.Gbx");
}

/**
 * The 3D model: the server builds and keeps it, and the window fetches it as a plain download by its ticket. (Read into
 * the page first, as the map is, a 16 MB model failed in a tester's window while receiving it.)
 */
async function downloadModel(track: TrackMap, quality: Quality): Promise<{ name: string; bytes: number }> {
  const started = performance.now();
  let stage = "sending the request";
  let answered = false;
  try {
    const response = await fetch("/api/export/model", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ track, quality }) });
    if (!response.ok) {
      answered = true;
      throw new Error(await problemMessage(response, "Export failed"));
    }
    stage = "reading the answer";
    const built = (await response.json()) as { ticket: string; fileName: string; bytes: number };
    stage = "starting the download";
    const a = Object.assign(document.createElement("a"), { href: `/api/export/model/${built.ticket}`, download: built.fileName });
    a.click();
    return { name: built.fileName, bytes: built.bytes };
  } catch (err) {
    if (answered) throw err;
    const message = (err as Error).message;
    void fetch("/api/export/failed", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ what: "/api/export/model", stage, message, seconds: (performance.now() - started) / 1000, requestBytes: 0 }),
    }).catch(() => {});
    throw new Error(`${message} (while ${stage}, after ${((performance.now() - started) / 1000).toFixed(1)} s)`);
  }
}

/**
 * Posts the request and saves the file that comes back (named by the server, else `fallback`). When that fails without
 * an answer from the server (the browser's "Failed to fetch"), the error says at which step, and the server is told for
 * its log (the app's tmspline.log).
 */
async function download(url: string, body: unknown, fallback: string): Promise<{ name: string; bytes: number }> {
  const text = JSON.stringify(body);
  const started = performance.now();
  let stage = "sending the request";
  let answered = false;
  try {
    const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: text });
    if (!response.ok) {
      answered = true;
      throw new Error(await problemMessage(response, "Export failed"));
    }
    stage = "receiving the file";
    const blob = await response.blob();
    stage = "saving the file";
    const name = /filename="?([^";]+)"?/.exec(response.headers.get("Content-Disposition") ?? "")?.[1] ?? fallback;
    const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: name });
    a.click();
    URL.revokeObjectURL(a.href);
    return { name, bytes: blob.size };
  } catch (err) {
    if (answered) throw err;
    const message = (err as Error).message;
    void fetch("/api/export/failed", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ what: url, stage, message, seconds: (performance.now() - started) / 1000, requestBytes: text.length }),
    }).catch(() => {});
    throw new Error(`${message} (while ${stage}, after ${((performance.now() - started) / 1000).toFixed(1)} s)`);
  }
}

/** Writes the map into the game's Maps folder (Maps/TMSpline), where the TMSpline Live plugin reloads it. */
async function exportToGame(track: TrackMap, author: Author, quality: Quality): Promise<{ name: string; bytes: number }> {
  const response = await fetch("/api/export/game", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ track, quality, authorLogin: author.login || null, authorNickname: author.nickname || null }),
  });
  if (!response.ok) throw new Error(await problemMessage(response, "Export failed"));
  const written = (await response.json()) as { mapPath: string; bytes: number; applied: boolean };
  // With the TMSpline Live plugin running, the export waits for it to save the open map and merge it in.
  return { name: `Maps/${written.mapPath}${written.applied ? "" : " (TMSpline Live merges it in the game)"}`, bytes: written.bytes };
}
