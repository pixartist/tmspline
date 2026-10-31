import type { TrackMap } from "./track";

const AUTHOR_KEY = "tmspline.author";
const QUALITY_KEY = "tmspline.exportQuality";
/**
 * Per track name: measured map size divided by the estimate, and the quality of that export (see
 * MeshQualities.EstimateMapBytes).
 */
const CALIBRATION_KEY = "tmspline.sizeCalibration";
/** The game's limit for custom items embedded in a map. */
const MAP_LIMIT_BYTES = 5_000_000;

const QUALITIES = ["Low", "Normal", "Fine", "High", "Ultra"] as const;
type Quality = (typeof QUALITIES)[number];
const QUALITY_HINTS: Record<Quality, string> = {
  Low: "smallest; fast sweeping curves can feel bumpy",
  Normal: "smooth for most tracks",
  Fine: "smoother at high speed, still compact",
  High: "for very fast, high-g sections",
  Ultra: "smoothest; usually too big for long tracks",
};

interface Author {
  login: string;
  nickname: string;
}

interface Estimate {
  quality: Quality;
  items: number;
  triangles: number;
  bytes: number;
  minBytes: number;
  maxBytes: number;
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
      <h2>Author</h2>
      <label class="row"><span>Account login</span><input name="login" placeholder="optional, 22 characters" maxlength="22" /></label>
      <label class="row"><span>Nickname</span><input name="nickname" placeholder="optional" maxlength="64" /></label>
      <p class="hint">The account login (from trackmania.io) is set as map author, so you can upload the map.</p>
      <p class="result" hidden></p>
      <p class="error" hidden></p>
      <div class="actions">
        <button value="cancel" formnovalidate>Close</button>
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
  login.value = saved.login;
  nickname.value = saved.nickname;
  const quality = () => (form.querySelector<HTMLInputElement>('input[name="quality"]:checked')?.value ?? "Normal") as Quality;

  let estimates: Estimate[] = [];
  const showEstimates = () => {
    const measured = calibration(track.name);
    const factor = measured?.factor;
    for (const e of estimates) {
      const el = dialog.querySelector<HTMLElement>(`.size[data-quality="${e.quality}"]`)!;
      // After a real export of this track the estimates are scaled by how far off that one was. That is close for the
      // same quality; for others less so, since finer meshes compress a little better.
      const spread = measured?.quality === e.quality ? 0.05 : 0.2;
      const [low, high] = factor ? [e.bytes * factor * (1 - spread), e.bytes * factor * (1 + spread)] : [e.minBytes, e.maxBytes];
      const mid = factor ? e.bytes * factor : e.bytes;
      el.textContent = `≈ ${mb(mid)} (${mb(low)}–${mb(high)}) · ${(e.triangles / 1000).toFixed(0)}k triangles`;
      el.classList.toggle("over", low > MAP_LIMIT_BYTES);
      el.classList.toggle("near", low <= MAP_LIMIT_BYTES && high > MAP_LIMIT_BYTES);
    }
    note.textContent = factor
      ? "Sizes are scaled by your last export of this track."
      : "Sizes are estimates: how well the items compress depends on the track. The game allows 5 MB of custom items per map.";
  };
  void estimate(track).then((list) => {
    estimates = list;
    showEstimates();
  }, (err) => {
    note.textContent = `No size estimate: ${(err as Error).message}`;
    dialog.querySelectorAll<HTMLElement>(".size").forEach((el) => (el.textContent = ""));
  });

  form.addEventListener("submit", async (e) => {
    if ((e.submitter as HTMLButtonElement | null)?.value !== "export") return;
    e.preventDefault();
    const author: Author = { login: login.value.trim(), nickname: nickname.value.trim() };
    if (author.login && !/^[A-Za-z0-9_-]{22}$/.test(author.login)) {
      showError("The account login is the 22-character id, e.g. from your trackmania.io profile URL.");
      return;
    }
    localStorage.setItem(AUTHOR_KEY, JSON.stringify(author));
    const q = quality();
    localStorage.setItem(QUALITY_KEY, q);

    exportButton.disabled = true;
    exportButton.textContent = "Exporting…";
    error.hidden = true;
    result.hidden = true;
    try {
      const { name, bytes } = await exportMap(track, author, q);
      const estimated = estimates.find((x) => x.quality === q);
      if (estimated) setCalibration(track.name, { factor: bytes / estimated.bytes, quality: q });
      showEstimates();
      result.textContent = `Downloaded ${name}: ${mb(bytes)} at ${q} quality`
        + (bytes > MAP_LIMIT_BYTES ? " — over the game's 5 MB limit, try a lower quality." : ` (limit 5 MB).`);
      result.classList.toggle("over", bytes > MAP_LIMIT_BYTES);
      result.hidden = false;
    } catch (err) {
      showError((err as Error).message);
    } finally {
      exportButton.disabled = false;
      exportButton.textContent = "Export";
    }
  });
  dialog.addEventListener("close", () => dialog.remove());
  dialog.showModal();

  function showError(message: string) {
    error.textContent = message;
    error.hidden = false;
  }
}

const mb = (bytes: number) => `${(bytes / 1_000_000).toFixed(bytes < 10_000_000 ? 2 : 1)} MB`;

interface Calibration {
  factor: number;
  quality: Quality;
}

function calibration(name: string): Calibration | undefined {
  const all = JSON.parse(localStorage.getItem(CALIBRATION_KEY) ?? "{}") as Record<string, Calibration | number>;
  const c = all[name];
  // Saved by an earlier version of this dialog: a bare factor (of an unknown quality).
  return typeof c === "number" ? { factor: c, quality: "Normal" } : c;
}

function setCalibration(name: string, calibration: Calibration): void {
  const all = JSON.parse(localStorage.getItem(CALIBRATION_KEY) ?? "{}") as Record<string, Calibration | number>;
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

async function exportMap(track: TrackMap, author: Author, quality: Quality): Promise<{ name: string; bytes: number }> {
  const response = await fetch("/api/export", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ track, quality, authorLogin: author.login || null, authorNickname: author.nickname || null }),
  });
  if (!response.ok) throw new Error(await problemMessage(response, "Export failed"));

  const blob = await response.blob();
  const name = /filename="?([^";]+)"?/.exec(response.headers.get("Content-Disposition") ?? "")?.[1] ?? "track.Map.Gbx";
  const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: name });
  a.click();
  URL.revokeObjectURL(a.href);
  return { name, bytes: blob.size };
}
