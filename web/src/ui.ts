import { MAX_BLOCK_LEVEL, RAISE_PER_STRENGTH, type Editor, type TerrainBrush } from "./editor";
import { showExportDialog } from "./export";
import { createCloudUi, type CloudUi } from "./cloudUi";
import { bannerHost } from "./banners";
import { DIRECTIONS, type BlockCatalogEntry } from "./vanilla";
import { FORESTS, forestsOf, SCENERY_THEMES } from "./scenery";
import { EDGE_DROP, OWN_TERRAIN_DEFAULTS } from "./ownTerrain";

/** Scenery panel settings (kept while the page is open). */
/** The terrain brush (Off: the mouse edits as usual). */
const paint = { brush: "off" as "off" | TerrainBrush, radius: 24, strength: 1, uncut: false };
const scenery = { mode: "terrain" as "fill" | "nestle" | "terrain", look: "alone" as "smooth" | "hill" | "alone", far: "fit" as "fit" | "lower" | "steps", showHeightMap: true, ownTerrain: { ...OWN_TERRAIN_DEFAULTS }, band: 4, theme: SCENERY_THEMES[0].id, gap: 1, hills: true, hillStep: 3, hillRise: 6, farRise: 0.5, cliffs: 10, forest: "spring", density: 0.5, clearance: 5, note: "" };

/** What a map keeps of the panel when it generates (not the note or the height map view). */
function sceneryToSave(): Record<string, unknown> {
  const { note: _note, showHeightMap: _view, ...options } = scenery;
  return structuredClone(options);
}

/** Puts a map's saved settings into the panel, within the fields' limits; whatever is missing or invalid stays as it is. */
function restoreScenery(saved: Record<string, unknown>): void {
  const number = (v: unknown, lo: number, hi: number, current: number) => (typeof v === "number" && Number.isFinite(v) ? Math.max(lo, Math.min(hi, v)) : current);
  const whole = (v: unknown, lo: number, hi: number, current: number) => Math.round(number(v, lo, hi, current));
  const choice = <T extends string>(v: unknown, options: readonly T[], current: T) => (options.includes(v as T) ? (v as T) : current);
  scenery.mode = choice(saved.mode, ["fill", "nestle", "terrain"], scenery.mode);
  scenery.look = choice(saved.look, ["smooth", "hill", "alone"], scenery.look);
  scenery.far = choice(saved.far, ["fit", "lower", "steps"], scenery.far);
  scenery.theme = choice(saved.theme, SCENERY_THEMES.map((th) => th.id), scenery.theme);
  scenery.forest = choice(saved.forest, ["none", ...FORESTS.map((f) => f.id)], scenery.forest);
  if (typeof saved.hills === "boolean") scenery.hills = saved.hills;
  scenery.band = whole(saved.band, 1, 24, scenery.band);
  scenery.gap = whole(saved.gap, 0, 8, scenery.gap);
  scenery.hillStep = whole(saved.hillStep, 1, 8, scenery.hillStep);
  scenery.hillRise = whole(saved.hillRise, 0, 16, scenery.hillRise);
  scenery.farRise = number(saved.farRise, 0, 4, scenery.farRise);
  scenery.cliffs = whole(saved.cliffs, 0, 24, scenery.cliffs);
  scenery.density = number(saved.density, 0, 1, scenery.density);
  scenery.clearance = number(saved.clearance, 0, 64, scenery.clearance);
  // In place: the panel's fields hold on to this object.
  const own = (saved.ownTerrain && typeof saved.ownTerrain === "object" ? saved.ownTerrain : {}) as Record<string, unknown>;
  const t = scenery.ownTerrain;
  t.width = whole(own.width, 32, 1024, t.width);
  t.smoothing = whole(own.smoothing, 0, 256, t.smoothing);
  t.noise = number(own.noise, 0, 256, t.noise);
  t.noiseSize = whole(own.noiseSize, 16, 512, t.noiseSize);
  t.rockAngle = whole(own.rockAngle, 0, 90, t.rockAngle);
  // Maps saved before this option had no rise.
  t.rise = number(own.rise, -100, 100, OWN_TERRAIN_DEFAULTS.rise);
  t.quality = choice<TerrainQuality>(own.quality, TERRAIN_QUALITIES, "High");
  // Maps saved before this option get the default bend when generated again.
  t.edgeDrop = number(own.edgeDrop, 0, 256, EDGE_DROP);
  t.seed = whole(own.seed, 0, 999999, 0);
}
import {
  appendPoint, MAP_ENVIRONMENTS, TERRAIN_QUALITIES, type MapEnvironment, type TerrainQuality, BORDER_MATERIALS, clampShape, DEFAULT_GATE_HEIGHT, BUMP_PATTERNS, ROAD_SIGNS, ROAD_SIGN_LABELS, SIGN_SIDES, cloneMap, DIRECTIONAL_EFFECTS, EFFECT_LABELS, smoothPoints, WAYPOINTS, wrapBank, deletePoint, EFFECT_SURFACES, EFFECTS, newMap, parseMap, SHAPE_PRESETS, SURFACES, syncSegments,
  GATES, ITEM_COLORS, OBJECT_KINDS, OBJECT_SHAPES, type ItemColor, type BorderMaterial, type BumpPattern, type RoadSignKind, type SignSide, type Effect, type ObjectShape, type Surface, type Track, type TrackMap, type TrackObjectKind, type TrackPoint, type Waypoint,
} from "./track";

// `?session=name` keeps a separate autosaved track (e.g. for testing without touching the main one).
const SESSION = new URLSearchParams(location.search).get("session");
const STORAGE_KEY = SESSION ? `tmspline.track.${SESSION}` : "tmspline.track";
const HISTORY_LIMIT = 200;

/** Undo/redo over whole-map snapshots. */
class History {
  private undoStack: TrackMap[] = [];
  private redoStack: TrackMap[] = [];

  push(snapshot: TrackMap): void {
    this.undoStack.push(snapshot);
    if (this.undoStack.length > HISTORY_LIMIT) this.undoStack.shift();
    this.redoStack = [];
  }
  undo(current: TrackMap): TrackMap | null {
    const t = this.undoStack.pop();
    if (t) this.redoStack.push(cloneMap(current));
    return t ?? null;
  }
  redo(current: TrackMap): TrackMap | null {
    const t = this.redoStack.pop();
    if (t) this.undoStack.push(cloneMap(current));
    return t ?? null;
  }
}

export function loadSavedMap(): TrackMap {
  try {
    const json = localStorage.getItem(STORAGE_KEY);
    if (json) return parseMap(json);
  } catch (err) {
    console.warn("Ignoring saved track", err);
  }
  return newMap();
}

export function createUi(editor: Editor, toolbar: HTMLElement, inspector: HTMLElement, status: HTMLElement): void {
  const history = new History();
  editor.onBeforeEdit = (snapshot) => history.push(snapshot);
  busyOverlay(editor);
  // The desktop app's cloud link (sign-in, sync, sharing); null in a plain browser.
  let cloud: CloudUi | null = null;
  void createCloudUi(editor, toolbar.parentElement!, STORAGE_KEY).then((c) => {
    cloud = c;
    saveButton.hidden = !c;
  }, (err) => console.warn("Cloud link failed", err));
  const saveToCloud = () => void cloud?.save(saveButton);
  void hintBlockLibrary(editor.renderer.domElement.parentElement!);

  const button = (label: string, title: string, onClick: () => void) => {
    const b = document.createElement("button");
    b.textContent = label;
    b.title = title;
    b.addEventListener("click", onClick);
    toolbar.appendChild(b);
    return b;
  };
  const separator = () => toolbar.appendChild(document.createElement("span")).classList.add("sep");

  const undo = () => { const m = history.undo(editor.map); if (m) editor.setMap(m); };
  const redo = () => { const m = history.redo(editor.map); if (m) editor.setMap(m); };
  const addPoint = () => {
    let index = 0;
    editor.edit((t) => (index = appendPoint(t)), true);
    editor.select({ kind: "point", index });
  };
  const insertPoint = () => editor.setInsertMode(!editor.insertMode);
  /** Smooths the selected point/segment and its neighbours, or the whole track. One undo step per pass. */
  const smooth = (wholeTrack: boolean) => {
    const s = editor.selection;
    const n = editor.track.points.length;
    let indices: number[] | undefined;
    if (!wholeTrack && s) {
      // Only points and segments are on the track; with blocks or an object selected, M does nothing.
      if (s.kind !== "point" && s.kind !== "handle" && s.kind !== "segment") return;
      const around = s.kind === "segment" ? [s.index, (s.index + 1) % n] : [s.index];
      indices = around.flatMap((i) => [i - 1, i, i + 1]).map((i) => (editor.track.closed ? (i + n) % n : i));
    }
    editor.edit((t) => smoothPoints(t, indices, smoothStrength() / 100));
  };
  const removePoint = () => {
    const s = editor.selection;
    if (editor.deleteSelectedBlocks()) return;
    if (s?.kind === "object") {
      editor.edit((t) => void t.objects.splice(s.index, 1), true);
      editor.select(null);
      return;
    }
    if (s?.kind !== "point") return;
    editor.edit((t) => void deletePoint(t, s.index), true);
    editor.select(null);
  };

  button("New", "Start a new track", () => {
    if (!confirmDiscard()) return;
    editor.replaceMap(newMap());
    editor.select(null);
    editor.frameTrack();
    cloud?.replaced();
  });
  button("Open…", "Open one of your tracks, a .track.json file or a share link", () =>
    cloud ? cloud.open((opened) => openFile(editor, opened)) : openFile(editor));
  // Save: to the cloud (the desktop app); the map is also saved on this PC after every change, and uploaded shortly after.
  const saveButton = button("Save", "Save the map to your TMSpline account now (Ctrl+S); changes are also saved automatically", saveToCloud);
  saveButton.hidden = true;
  button("Download", "Download the map (all tracks) as a .track.json file", () => download(editor.map));
  button("Export map", "Generate a Trackmania .Map.Gbx", () => showExportDialog(editor.exportMap())).classList.add("primary");
  separator();
  button("Undo", "Undo (Ctrl+Z)", undo);
  button("Redo", "Redo (Ctrl+Y)", redo);
  separator();
  button("New track", "Add another track to the map, in the middle of the view (T)", () => editor.addTrack());
  button("Add point", "Append a point at the end of the active track (N) — or Ctrl+click the ground", addPoint);
  const insertButton = button("Insert point", "Click the road to insert a point there (I; Esc cancels)", insertPoint);
  button("Delete point", "Delete the selected point (Del)", removePoint);
  button("Smooth", "Smooth the whole track a little more with each click (M smooths only the selection)", () => smooth(true));
  smoothStrengthSlider(toolbar);
  separator();
  const placeTitles: Record<TrackObjectKind, string> = {
    Pillar: "Click the road to put a pillar under it (P)",
    Hole: "Click the road to cut a hole into it (H)",
    Gap: "Click the road to leave a gap there, e.g. for a jump",
    Bump: "Click the road to put a bump (hump or dip) into it",
    Gate: "Click the road to put one of the game's gates round it there (an effect, or another car)",
  };
  const placeButtons = OBJECT_KINDS.map((kind) =>
    [kind, button(kind, placeTitles[kind], () => editor.setPlaceMode(editor.placeMode === kind ? null : kind))] as const);
  separator();
  const palette = createBlockPalette(editor);
  const blocksButton = button("Blocks", "Place vanilla game blocks on the map grid or freely (B; Tab switches grid / free)", () => palette.toggle(blocksButton));
  const snapButton = button("Snap", "Snap track points, their handles and free blocks to the grid (X); points snap to the heights of block roads (2 m above each level). Ctrl while dragging ignores it", () => toggleSnap());
  const snapSelect = document.createElement("select");
  snapSelect.title = "Grid step (vertical: the same, at most 8 m = one block level)";
  for (const step of SNAP_STEPS) snapSelect.append(new Option(step === 32 ? "32 m (cells)" : `${step} m`, String(step)));
  snapSelect.value = String(savedSnapStep());
  snapSelect.addEventListener("change", () => { localStorage.setItem(SNAP_STEP_KEY, snapSelect.value); if (editor.snapStep > 0) editor.setSnap(Number(snapSelect.value)); snapSelect.blur(); });
  toolbar.appendChild(snapSelect);
  const toggleSnap = () => {
    const on = editor.snapStep === 0;
    localStorage.setItem(SNAP_ON_KEY, on ? "1" : "0");
    editor.setSnap(on ? Number(snapSelect.value) : 0);
  };
  if (localStorage.getItem(SNAP_ON_KEY) === "1") editor.setSnap(Number(snapSelect.value));
  separator();
  button("Frame", "Show the whole map (F)", () => editor.frameTrack());
  const toggleAxis = () => editor.setAxisGizmo(!editor.axisGizmoEnabled);
  const axisButton = button("Axis gizmo", "Show arrows for precise moves along one axis (G)", toggleAxis);

  window.addEventListener("keydown", (e) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement || document.querySelector("dialog[open]")) return;
    const key = e.key.toLowerCase();
    if ((e.ctrlKey || e.metaKey) && key === "z") { e.preventDefault(); e.shiftKey ? redo() : undo(); }
    else if ((e.ctrlKey || e.metaKey) && key === "y") { e.preventDefault(); redo(); }
    else if ((e.ctrlKey || e.metaKey) && key === "s") { e.preventDefault(); if (cloud) saveToCloud(); else download(editor.map); }
    else if (key === "n") addPoint();
    else if (key === "t") editor.addTrack();
    else if (key === "i") insertPoint();
    else if (key === "p") editor.setPlaceMode(editor.placeMode === "Pillar" ? null : "Pillar");
    else if (key === "h") editor.setPlaceMode(editor.placeMode === "Hole" ? null : "Hole");
    else if (key === "m") smooth(e.shiftKey);
    else if (key === "b") { e.preventDefault(); palette.toggle(blocksButton); }
    else if (key === "r") editor.rotateBlock(e.shiftKey ? -1 : 1);
    else if (key === "x") toggleSnap();
    else if (key === "tab") { if (editor.toggleFreeBlock()) e.preventDefault(); }
    else if (key === "pageup" || key === "pagedown") { if (editor.raiseBlock(key === "pageup" ? 1 : -1)) e.preventDefault(); }
    else if (key === "delete" || key === "backspace") removePoint();
    else if (key === "f") editor.frameTrack();
    else if (key === "g") toggleAxis();
    else if (key === "escape") {
      if (palette.isOpen) palette.close();
      else if (editor.insertMode) editor.setInsertMode(false);
      else if (editor.placeMode) editor.setPlaceMode(null);
      else if (editor.blockPlace) editor.setBlockPlaceMode(null);
      else editor.select(null);
    }
    else if (key === "[" || key === "]") {
      const s = editor.selection;
      if (s?.kind === "point" || s?.kind === "handle") {
        const step = (key === "[" ? -1 : 1) * (e.shiftKey ? 4 : 1);
        editor.edit((t) => (t.points[s.index].width = clamp(t.points[s.index].width + step, 4, 512)), false, `width:${s.index}`);
      }
    }
    else if (key === "q" || key === "e") {
      const s = editor.selection;
      if (s?.kind === "point" || s?.kind === "handle") {
        const step = (key === "q" ? -5 : 5) * (e.shiftKey ? 3 : 1);
        editor.edit((t) => (t.points[s.index].bank = wrapBank(t.points[s.index].bank + step)), false, `bank:${s.index}`);
      }
    }
  });

  // Autosave, but never silently overwrite a newer save from another tab: once another tab saves the same track,
  // this tab pauses autosaving until the user picks which version to keep.
  let saveTimer = 0;
  let lastSaved = localStorage.getItem(STORAGE_KEY);
  let conflict: HTMLElement | null = null;
  const save = (force = false) => {
    if (conflict && !force) return;
    const json = JSON.stringify(editor.map);
    if (json === lastSaved && !force) return;
    localStorage.setItem(STORAGE_KEY, json);
    lastSaved = json;
    cloud?.sync.changed(json);
  };
  window.addEventListener("storage", (e) => {
    if (e.key !== STORAGE_KEY || e.newValue === null || e.newValue === lastSaved || conflict) return;
    conflict = showConflict(editor.renderer.domElement.parentElement!, {
      loadTheirs: () => {
        const json = localStorage.getItem(STORAGE_KEY);
        if (json) {
          editor.replaceMap(parseMap(json));
          lastSaved = json;
        }
        conflict = null;
      },
      keepMine: () => {
        conflict = null;
        save(true);
      },
    });
  });

  // The panel follows the map's saved settings when they differ (another map loaded, undo of a Generate), by content:
  // an undo of anything else brings back an equal copy and must not reset what was just typed into the panel.
  let lastSceneryOptions: string | undefined;
  editor.onChange = () => {
    const sceneryOptions = JSON.stringify(editor.map.sceneryOptions);
    if (sceneryOptions !== lastSceneryOptions) {
      lastSceneryOptions = sceneryOptions;
      if (editor.map.sceneryOptions) restoreScenery(editor.map.sceneryOptions);
    }
    axisButton.classList.toggle("active", editor.axisGizmoEnabled);
    insertButton.classList.toggle("active", editor.insertMode);
    for (const [kind, b] of placeButtons) b.classList.toggle("active", editor.placeMode === kind);
    blocksButton.classList.toggle("active", !!editor.blockPlace || palette.isOpen);
    snapButton.classList.toggle("active", editor.snapStep > 0);
    snapButton.textContent = editor.snapStep > 0 ? `Snap ${editor.snapStep} m` : "Snap";
    renderInspector(editor, inspector, removePoint);
    const tracks = editor.map.tracks.length;
    status.textContent = (tracks > 1 ? `${tracks} tracks (editing track ${editor.active + 1}) · ` : "")
      + `${editor.map.tracks.reduce((n, t) => n + t.points.length, 0)} points · ${editor.stats.length.toFixed(0)} m · ${editor.stats.triangles.toLocaleString()} triangles`
      + (editor.map.blocks.length ? ` · ${editor.map.blocks.length} block${editor.map.blocks.length === 1 ? "" : "s"}` : "")
      + (SESSION ? ` · session "${SESSION}"` : "") + (conflict ? " · autosave paused" : "");
    clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => save(), 300);
  };
  editor.onChange();
}

const LIBRARY_HINT_KEY = "tmspline.libraryHintDismissed";

/**
 * Where the plugin can have the block library built (the desktop app) but there is none yet: blocks show as boxes and
 * the road without the game's textures, which is easy to take for a bug. Says once how to build it.
 */
async function hintBlockLibrary(container: HTMLElement): Promise<void> {
  if (localStorage.getItem(LIBRARY_HINT_KEY)) return;
  const health = await fetch("/api/health").then((r) => r.json()).catch(() => null);
  if (!health?.blockLibraryBuilder || health.blockLibrary) return;
  const banner = document.createElement("div");
  banner.className = "conflict-banner";
  banner.append(Object.assign(document.createElement("span"), {
    innerHTML: "No block library yet, so blocks show as boxes and the road without the game's textures. To build it: "
      + "in Trackmania, <b>F3 → TMSpline → Build block library</b> (with the Openplanet plugin), then reload here (F5).",
  }));
  const close = (dismiss: boolean) => {
    if (dismiss) localStorage.setItem(LIBRARY_HINT_KEY, "1");
    banner.remove();
  };
  const ok = Object.assign(document.createElement("button"), { textContent: "OK", className: "primary" });
  ok.addEventListener("click", () => close(false));
  const never = Object.assign(document.createElement("button"), { textContent: "Don't show again" });
  never.addEventListener("click", () => close(true));
  banner.append(ok, never);
  bannerHost(container).appendChild(banner);
}

/** Banner shown when another tab saved the track; returns the banner element. */
function showConflict(container: HTMLElement, actions: { loadTheirs: () => void; keepMine: () => void }): HTMLElement {
  const banner = document.createElement("div");
  banner.className = "conflict-banner";
  banner.append(Object.assign(document.createElement("span"), {
    textContent: "This track was changed in another tab. Autosave is paused here.",
  }));
  const choice = (label: string, action: () => void, primary = false) => {
    const b = Object.assign(document.createElement("button"), { textContent: label });
    if (primary) b.classList.add("primary");
    b.addEventListener("click", () => {
      banner.remove();
      action();
    });
    banner.append(b);
  };
  choice("Load the other version", actions.loadTheirs, true);
  choice("Keep this one", actions.keepMine);
  bannerHost(container).appendChild(banner);
  return banner;
}

// ---- vanilla block palette --------------------------------------------------------------------

const RECENT_BLOCKS_KEY = "tmspline.recentBlocks";
const SNAP_ON_KEY = "tmspline.snap";
const SNAP_STEP_KEY = "tmspline.snapStep";
const SNAP_STEPS = [32, 16, 8, 4, 2, 1];
const savedSnapStep = () => { const v = Number(localStorage.getItem(SNAP_STEP_KEY)); return SNAP_STEPS.includes(v) ? v : 8; };
const PALETTE_LIMIT = 400;

/**
 * The block palette under the Blocks button: the game's catalog icons, grouped by catalog page like the game's editor,
 * with categories (first part of the page), search over all blocks and recently used ones. Picking one starts block mode.
 */
function createBlockPalette(editor: Editor) {
  const panel = Object.assign(document.createElement("div"), { className: "block-palette" });
  panel.hidden = true;
  const search = Object.assign(document.createElement("input"), { type: "search", placeholder: "Search all blocks, e.g. ramp, loop, dirt curve" });
  const categories = Object.assign(document.createElement("div"), { className: "block-categories" });
  const body = Object.assign(document.createElement("div"), { className: "block-body" });
  const note = Object.assign(document.createElement("p"), { className: "hint" });
  panel.append(search, categories, body, note);
  document.body.appendChild(panel);

  let category = localStorage.getItem(PALETTE_CATEGORY_KEY) ?? RECENT;
  let catalog: BlockCatalogEntry[] | null = null;
  const recent = (): string[] => { try { return JSON.parse(localStorage.getItem(RECENT_BLOCKS_KEY) ?? "[]"); } catch { return []; } };
  const pick = (name: string) => {
    localStorage.setItem(RECENT_BLOCKS_KEY, JSON.stringify([name, ...recent().filter((n) => n !== name)].slice(0, 24)));
    close();
    editor.setBlockPlaceMode(name);
  };
  const tile = (b: BlockCatalogEntry, prefix: string) => {
    const t = Object.assign(document.createElement("button"), { type: "button", className: "block-tile", title: b.name });
    if (b.icon) t.append(Object.assign(document.createElement("img"), { src: b.icon, alt: "", loading: "lazy", width: 64, height: 64 }));
    else t.append(Object.assign(document.createElement("span"), { className: "block-noicon", textContent: "?" }));
    // The category is the section heading already; the rest of the name tells the pieces apart.
    const short = prefix && b.name.startsWith(prefix) && b.name.length > prefix.length ? b.name.slice(prefix.length) : b.name;
    t.append(Object.assign(document.createElement("span"), { className: "block-name", textContent: short }));
    t.addEventListener("click", () => pick(b.name));
    return t;
  };
  const section = (title: string, blocks: BlockCatalogEntry[], prefix: string) => {
    if (blocks.length === 0) return;
    body.append(Object.assign(document.createElement("h3"), { textContent: `${title} (${blocks.length})` }));
    const grid = Object.assign(document.createElement("div"), { className: "block-grid" });
    grid.append(...blocks.slice(0, PALETTE_LIMIT).map((b) => tile(b, prefix)));
    body.append(grid);
  };
  const render = () => {
    const lib = editor.library;
    catalog ??= lib?.catalog ?? null;
    body.replaceChildren();
    categories.replaceChildren();
    if (!lib || !catalog) {
      // Without a library any name can still be placed (it shows as a box; the game knows the block).
      const typed = search.value.trim().replace(/[^\w]/g, "");
      if (typed) {
        const b = Object.assign(document.createElement("button"), { type: "button", textContent: `Place "${typed}"` });
        b.addEventListener("click", () => pick(typed));
        body.append(b);
      }
      note.textContent = "No local block library: type a block name to place it as a box. Convert the game's blocks with tools/VanillaLib (development only).";
      return;
    }
    const all = catalog;
    const topOf = (page: string) => page.split("/").find((part) => part.length > 0) ?? "Other";
    const rank = (top: string) => { const i = CATEGORY_ORDER.indexOf(top); return i < 0 ? CATEGORY_ORDER.length : i; };
    const tops = [...new Set(all.map((b) => topOf(b.page)))].sort((x, y) => rank(x) - rank(y) || x.localeCompare(y));
    const words = search.value.toLowerCase().split(/\s+/).filter(Boolean);
    for (const name of [RECENT, ...tops]) {
      const chip = Object.assign(document.createElement("button"), { type: "button", className: "chip", textContent: name.replace(/([a-z])([A-Z])/g, "$1 $2") });
      chip.classList.toggle("active", !words.length && name === category);
      chip.addEventListener("click", () => {
        category = name;
        localStorage.setItem(PALETTE_CATEGORY_KEY, name);
        search.value = "";
        render();
        search.focus();
      });
      categories.append(chip);
    }
    let shown: BlockCatalogEntry[];
    if (words.length) {
      shown = all.filter((b) => words.every((w) => b.name.toLowerCase().includes(w) || b.page.toLowerCase().includes(w)));
      groupByPage(shown).forEach(([page, blocks]) => section(page, blocks, topOf(page)));
    } else if (category === RECENT || !tops.includes(category)) {
      const byName = new Map(all.map((b) => [b.name, b]));
      shown = recent().map((n) => byName.get(n)).filter((b): b is BlockCatalogEntry => !!b);
      section("Recently used", shown, "");
      if (shown.length === 0) body.append(Object.assign(document.createElement("p"), { className: "hint", textContent: "Pick a category above, or search." }));
    } else {
      shown = all.filter((b) => topOf(b.page) === category);
      groupByPage(shown).forEach(([page, blocks]) => section(page.split("/").filter(Boolean).slice(1).join(" / ") || page, blocks, category));
    }
    note.textContent = `${shown.length} block${shown.length === 1 ? "" : "s"}${shown.length > PALETTE_LIMIT ? ` (sections show at most ${PALETTE_LIMIT})` : ""}. `
      + "Click one, then click the map to place it (Tab: free placement). Hover for the full name.";
  };
  search.addEventListener("input", render);
  search.addEventListener("keydown", (e) => {
    if (e.key === "Enter") body.querySelector<HTMLElement>(".block-tile")?.click();
    if (e.key === "Escape") close();
  });
  const close = () => {
    // Give the keys back to the editor (R, Tab, PgUp/PgDn), not to the hidden search box.
    search.blur();
    panel.hidden = true;
    editor.onChange();
  };
  const open = (anchor: HTMLElement) => {
    // Below the whole header (the toolbar may wrap), so the palette never covers the menu.
    const r = anchor.getBoundingClientRect();
    const header = anchor.closest("header")?.getBoundingClientRect() ?? r;
    const width = Math.min(720, window.innerWidth - 16);
    panel.style.width = `${width}px`;
    panel.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - width - 8))}px`;
    panel.style.top = `${Math.max(r.bottom, header.bottom) + 4}px`;
    panel.hidden = false;
    render();
    search.focus();
    search.select();
    editor.onChange();
  };
  document.addEventListener("pointerdown", (e) => {
    if (!panel.hidden && !panel.contains(e.target as Node) && !(e.target as HTMLElement).closest?.("#toolbar")) close();
  });
  return {
    get isOpen() { return !panel.hidden; },
    toggle: (anchor: HTMLElement) => (panel.hidden ? open(anchor) : close()),
    close,
  };
}

const RECENT = "Recent";
/** Categories in the order of the game's editor; others follow alphabetically. */
const CATEGORY_ORDER = ["RoadTech", "RoadDirt", "RoadBump", "RoadIce", "RoadWater", "PlatformTech", "PlatformDirt", "PlatformIce",
  "PlatformGrass", "PlatformPlastic", "Deco", "DecoWall", "Decorations", "Structure", "Other", "Pillars"];
const PALETTE_CATEGORY_KEY = "tmspline.blockCategory";

/** Blocks grouped by catalog page, pages in the order they first appear (the catalog is sorted by page). */
function groupByPage(blocks: BlockCatalogEntry[]): [string, BlockCatalogEntry[]][] {
  const pages = new Map<string, BlockCatalogEntry[]>();
  for (const b of blocks) {
    let list = pages.get(b.page);
    if (!list) pages.set(b.page, (list = []));
    list.push(b);
  }
  return [...pages];
}

// ---- smoothing strength -----------------------------------------------------------------------

const SMOOTH_STRENGTH_KEY = "tmspline.smoothStrength";
const smoothStrength = () => clamp(Number(localStorage.getItem(SMOOTH_STRENGTH_KEY) ?? 30) || 30, 1, 100);

/** Small slider next to the Smooth button; the value is remembered across sessions. */
function smoothStrengthSlider(toolbar: HTMLElement): void {
  const wrap = Object.assign(document.createElement("label"), { className: "toolbar-range", title: "Smoothing strength per click" });
  const range = Object.assign(document.createElement("input"), { type: "range", min: "1", max: "100", step: "1", value: String(smoothStrength()) });
  const value = Object.assign(document.createElement("span"), { textContent: `${smoothStrength()}%` });
  range.addEventListener("input", () => {
    localStorage.setItem(SMOOTH_STRENGTH_KEY, range.value);
    value.textContent = `${range.value}%`;
  });
  wrap.append(range, value);
  toolbar.appendChild(wrap);
}

// ---- inspector --------------------------------------------------------------------------------

let lastInspectorKey = "";

function renderInspector(editor: Editor, root: HTMLElement, removePoint: () => void): void {
  const s = editor.selection;
  const t = editor.track;
  // Loading a map switches the brush off in the editor.
  paint.brush = editor.terrainPaint?.brush ?? "off";
  // Rebuild the DOM only when the selection changes; otherwise just refresh values so inputs keep focus.
  const seg = s?.kind === "segment" ? t.segments[s.index] : undefined;
  const obj = s?.kind === "object" ? t.objects[s.index] : undefined;
  const key = `${editor.active}/${editor.map.tracks.length}:${s?.kind}:${s && "index" in s ? s.index : ""}:${t.points.length}:${t.closed}:${seg?.surface}:${seg?.effect}:${seg?.borders}:${seg?.borderMaterial}:${seg?.underside}:${seg?.bumps}:${seg?.signs}`
    + `:${(s?.kind === "point" || s?.kind === "handle") ? `${t.points[s.index]?.borders}:${(t.points[s.index]?.edgeAnchor ?? 0) > 0}:${t.points[s.index]?.waypoint}:${editor.map.tracks.flatMap((tr) => tr.points.map((q) => q.checkpointGroup ?? 0)).join(",")}:${profileChoice}:${Object.keys(loadProfiles()).join("/")}` : ""}`
    + `:${t.objects.length}:${obj?.kind}:${obj?.shape}:${editor.map.blocks.length}:${!!editor.library}`
    + `:${scenery.mode}:${scenery.hills}:${scenery.cliffs === 0}:${scenery.forest === "none"}:${!!editor.map.terrain}:${paint.brush}:${editor.map.environment ?? ""}`
    + `:${s?.kind === "block" ? !!editor.map.blocks[s.index]?.free : ""}:${s?.kind === "blocks" ? s.indices.join(",") : ""}`;
  if (key === lastInspectorKey) {
    root.querySelectorAll<HTMLInputElement>("[data-refresh]").forEach((el) => el.dispatchEvent(new Event("refresh")));
    return;
  }
  lastInspectorKey = key;
  root.replaceChildren();

  const section = (title: string) => {
    const h = document.createElement("h2");
    h.textContent = title;
    root.appendChild(h);
  };

  const map = editor.map;
  section("Map");
  textField(root, "Name", () => editor.map.name, (v) => editor.editMap((m) => (m.name = v)));
  if (map.tracks.some((tr) => tr.points.some((p) => p.waypoint === "StartFinish")))
    numberField(root, "Laps", () => editor.map.laps, (v) => editor.editMap((m) => (m.laps = Math.max(1, Math.min(99, Math.round(v))))), 1);
  selectField(root, "Environment", ["Stadium", ...MAP_ENVIRONMENTS], () => editor.map.environment ?? "Stadium", (v) => { editor.editMap((m) => {
    if (v === "Stadium") delete m.environment;
    else { m.environment = v as MapEnvironment; delete m.noStadium; }
  }); editor.onChange(); }, false, { Stadium: "Stadium", BlueBay: "Blue Bay", GreenCoast: "Green Coast", RedIsland: "Red Island", WhiteShore: "White Shore" });
  if (!editor.map.environment)
    checkbox(root, "Stadium", () => !editor.map.noStadium, (v) => editor.editMap((m) => {
      if (v) delete m.noStadium;
      else m.noStadium = true;
    }));

  section(map.tracks.length > 1 ? `Track ${editor.active + 1} of ${map.tracks.length}` : "Track");
  checkbox(root, "Closed loop", () => editor.track.closed, (v) => editor.edit((tr) => (tr.closed = v), true), t.points.length < 3);
  buttonRow(root, [
    ...(map.tracks.length > 1 ? [
      { label: "◀", onClick: () => editor.setActive((editor.active + map.tracks.length - 1) % map.tracks.length) },
      { label: "▶", onClick: () => editor.setActive((editor.active + 1) % map.tracks.length) },
    ] : []),
    { label: "New track", onClick: () => editor.addTrack() },
    ...(map.tracks.length > 1 ? [{
      label: "Delete track",
      onClick: () => { if (confirm(`Delete track ${editor.active + 1}? Undo brings it back.`)) editor.deleteActiveTrack(); },
    }] : []),
  ]);
  if (map.tracks.length > 1)
    hint(root, "Points of the other tracks are grey: click one (or their road) to edit that track.");

  if (!s && editor.library) {
    section("Scenery");
    const int = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, Math.round(v)));
    // Settings that show or hide other fields redraw the inspector.
    const relayout = () => editor.onChange();
    const nestle = scenery.mode === "nestle";
    // Nestle and the own terrain are a band round the tracks.
    const banded = scenery.mode !== "fill";
    selectField(root, "Mode", ["fill", "nestle", "terrain"], () => scenery.mode, (v) => { scenery.mode = v as typeof scenery.mode; relayout(); }, false,
      { fill: "Fill the grid (from the ground)", nestle: "Nestle (band at the track's height)", terrain: "Own terrain (mesh)" });
    if (nestle) {
      selectField(root, "2-level slopes", ["smooth", "hill", "alone"], () => scenery.look, (v) => (scenery.look = v as typeof scenery.look), false,
        { smooth: "Smooth (platform slopes)", hill: "Hill (terraced, where it fits)", alone: "Hill where a slope stands alone" });
      selectField(root, "Fitting the pieces", ["fit", "lower", "steps"], () => scenery.far, (v) => (scenery.far = v as typeof scenery.far), false,
        { fit: "Best fit per cell (small steps)", lower: "Lower to fit (no steps)", steps: "Steps away from the roads" });
      checkbox(root, "Show height map", () => scenery.showHeightMap, (v) => { scenery.showHeightMap = v; editor.showHeightMap(v); });
    }
    // The own terrain is not made of blocks: metres, no cells or levels.
    const own = scenery.mode === "terrain";
    if (own) {
      const t = scenery.ownTerrain;
      numberField(root, "Width (m)", () => t.width, (v) => (t.width = int(v, 32, 1024)), 16);
      numberField(root, "Smoothing (m)", () => t.smoothing, (v) => (t.smoothing = int(v, 0, 256)), 8);
      numberField(root, "Noise height (m)", () => t.noise, (v) => (t.noise = Math.max(0, Math.min(256, v))), 1);
      numberField(root, "Noise size (m)", () => t.noiseSize, (v) => (t.noiseSize = int(v, 16, 512)), 16);
      numberField(root, "Noise seed (other hills for each number)", () => t.seed ?? 0, (v) => (t.seed = int(v, 0, 999999)), 1);
      numberField(root, "Rock from (°, 90 = none)", () => t.rockAngle, (v) => (t.rockAngle = int(v, 0, 90)), 5);
      numberField(root, "Rise away from the track (%, 0 = off, negative = falls)", () => t.rise, (v) => { t.rise = Math.max(-100, Math.min(100, v)); }, 5);
      numberField(root, "Outer edge bends down by (m, 0 = off)", () => t.edgeDrop ?? EDGE_DROP, (v) => { t.edgeDrop = Math.max(0, Math.min(256, v)); }, 4);
      // The mesh's fineness: the map's own (kept with its land), and changed at once, without generating again.
      selectField(root, "Mesh quality", TERRAIN_QUALITIES, () => editor.map.terrain?.quality ?? (editor.map.terrain ? "High" : t.quality ?? "High"), (v) => {
        t.quality = v as TerrainQuality;
        if (editor.map.terrain && (editor.map.terrain.quality ?? "High") !== v)
          editor.editMap((m) => {
            const { quality: _, ...land } = m.terrain!;
            m.terrain = v === "High" ? land : { ...land, quality: v as TerrainQuality };
          });
      }, false, { Fine: "Fine (about 150%)", High: "High (100%)", Medium: "Medium (about 60%)", Low: "Low (about 45%)" });
      const size = Object.assign(document.createElement("p"), { className: "hint" });
      const showSize = () => {
        const s = editor.terrainSize();
        size.textContent = !editor.map.terrain ? "" : !s ? "Terrain size: measuring…"
          : `Terrain size: about ${(s.bytes / 1e6).toFixed(2)} MB (${Math.round(s.triangles / 1000)}k triangles). A map's roads and terrain may take 5 MB together (about 7 MB with Openplanet's \"Infinite embed size\").`;
      };
      editor.onTerrainSize = showSize;
      showSize();
      root.appendChild(size);
    }
    if (!banded) checkbox(root, "Hills", () => scenery.hills, (v) => { scenery.hills = v; relayout(); });
    if (!own) numberField(root, "Shoulder (cells)", () => scenery.gap, (v) => (scenery.gap = int(v, 0, 8)), 1);
    if (nestle) {
      numberField(root, "Band (cells)", () => scenery.band, (v) => (scenery.band = int(v, 1, 24)), 1);
    }
    if (!own && (scenery.hills || banded)) {
      numberField(root, "Hill step (cells)", () => scenery.hillStep, (v) => (scenery.hillStep = int(v, 1, 8)), 1);
      // With cliffs the hills climb to their top instead.
      if (scenery.cliffs === 0 || banded)
        numberField(root, "Hill height (levels)", () => scenery.hillRise, (v) => (scenery.hillRise = int(v, 0, 16)), 2);
      if (scenery.cliffs === 0 || banded)
        numberField(root, "Rise over distance (levels per cell, 0 = off)", () => scenery.farRise,
          (v) => (scenery.farRise = Math.max(0, Math.min(4, Math.round(v * 100) / 100))), 0.25);
    }
    if (!banded)
      numberField(root, "Cliffs at (cells, 0 = none)", () => scenery.cliffs, (v) => { scenery.cliffs = int(v, 0, 24); relayout(); }, 1);
    // The theme sets the land's surface and the cliffs, and picks its forest (which can still be changed).
    selectField(root, "Theme", SCENERY_THEMES.map((th) => th.id), () => scenery.theme, (v) => {
      scenery.theme = v;
      if (!editor.map.environment) scenery.forest = SCENERY_THEMES.find((th) => th.id === v)?.forest ?? scenery.forest;
    }, false, Object.fromEntries(SCENERY_THEMES.map((th) => [th.id, th.label])));
    // Each environment has its own vegetation: a forest of another one becomes this one's first.
    const forests = forestsOf(editor.map.environment);
    if (scenery.forest !== "none" && !forests.some((f) => f.id === scenery.forest)) scenery.forest = forests[0]?.id ?? "none";
    selectField(root, "Forest", ["none", ...forests.map((f) => f.id)], () => scenery.forest, (v) => { scenery.forest = v; relayout(); }, false,
      { none: "None", ...Object.fromEntries(forests.map((f) => [f.id, f.label])) });
    if (scenery.forest !== "none") {
      numberField(root, "Forest density (%)", () => Math.round(scenery.density * 100), (v) => (scenery.density = int(v, 0, 100) / 100), 10);
      numberField(root, "Tree clearance (m)", () => scenery.clearance, (v) => (scenery.clearance = Math.max(0, Math.min(64, v))), 1);
    }
    buttonRow(root, [
      {
        label: "Generate", onClick: async () => {
          const theme = SCENERY_THEMES.find((th) => th.id === scenery.theme) ?? SCENERY_THEMES[0];
          const n = await editor.generateScenery({ ...scenery, theme }, sceneryToSave());
          editor.showHeightMap(scenery.mode === "nestle" && scenery.showHeightMap);
          scenery.note = n === null ? "The block library lacks pieces for these settings."
            : `Placed ${n.blocks} blocks${n.items ? ` and ${n.items} trees` : ""}.`;
          editor.onChange();
        },
      },
      { label: "Remove", onClick: () => { scenery.note = `Removed ${editor.removeScenery()} blocks and trees.`; editor.onChange(); } },
    ]);
    if (editor.map.terrain) {
      section("Paint terrain");
      const apply = () => editor.setTerrainPaint(paint.brush === "off" ? null : { brush: paint.brush, radius: paint.radius, strength: paint.strength, uncut: paint.uncut });
      selectField(root, "Brush", ["off", "raise", "lower", "smooth", "flatten"], () => paint.brush, (v) => { paint.brush = v as typeof paint.brush; apply(); relayout(); }, false,
        { off: "Off", raise: "Raise", lower: "Lower", smooth: "Smooth", flatten: "Flatten (to where the stroke starts)" });
      numberField(root, "Radius (m)", () => paint.radius, (v) => { paint.radius = Math.max(4, Math.min(256, v)); apply(); }, 4);
      const solid = paint.brush === "raise" || paint.brush === "lower" || paint.brush === "smooth";
      numberField(root, paint.brush === "raise" || paint.brush === "lower" ? `Strength (x ${RAISE_PER_STRENGTH} m per dab)` : solid ? "Strength" : "Strength (0 to 1)", () => paint.strength,
        (v) => { paint.strength = Math.max(0.05, Math.min(solid ? 16 : 1, v)); apply(); }, 0.25);
      if (solid)
        checkbox(root, "Into the roads (not cut by them)", () => paint.uncut, (v) => { paint.uncut = v; apply(); });
      // What was painted into the roads cannot be painted away by the other brushes (the roads cut those): this does it.
      buttonRow(root, [{ label: "Clear roads", onClick: () => { scenery.note = editor.clearRoads() ? "The roads cut through everything painted again." : "Nothing is painted into the roads."; editor.onChange(); } }]);
      hint(root, "With a brush chosen, drag over the land to paint it (Alt+drag still orbits). Raise, lower and smooth work where the "
        + "cursor meets the land, also on a cutting's side or a tunnel's wall and roof, and the land follows while you paint; "
        + "the roads cut through the result unless \"Into the roads\" is ticked (Clear roads cuts those free again). Flatten "
        + "works on open ground. Each stroke is one undo step; generating again replaces the painted land.");
    }
    hint(root, (scenery.note ? scenery.note + " " : "") + (nestle
      ? "Lays a band of land round the tracks at their own height: up to just below the roads, a flat shoulder, then "
        + "hills rising by 2 levels every hill step, as free blocks (the land floats, e.g. along a ski slope high up), "
        + "also outside the grid. Where neighbouring cells differ in height, columns close the step down to the lower one. "
        + "Generating again replaces what was generated."
      : "Builds land round the tracks: up to just below the roads (they run on it instead of columns), a flat shoulder "
      + "beside them, then hills rising by 2 levels every hill step, and cliff walls further out as a backdrop (also "
      + "inside loops), with trees on the flat land (shown as simple stand-ins; the game has the real ones). Generating "
      + "again replaces what was generated; it is normal blocks and items otherwise (clear jumps by hand)."));
  }

  if (s?.kind === "point" || s?.kind === "handle") {
    const p = () => editor.track.points[s.index];
    section(`Point ${s.index + 1}${s.index === 0 ? " (first)" : ""}`);
    (["X", "Y", "Z"] as const).forEach((axis, i) =>
      numberField(root, axis, () => p().position[i], (v) => editor.edit((tr) => (tr.points[s.index].position[i] = v)), 0.5));
    rangeField(root, "Bank °", -180, 180, 1, () => p().bank,
      (v) => editor.edit((tr) => (tr.points[s.index].bank = wrapBank(v)), false, `bank:${s.index}`), 360);
    rangeField(root, "Width m", 4, 512, 0.5, () => p().width, (v) => editor.edit((tr) => (tr.points[s.index].width = v), false, `width:${s.index}`));
    // Points that keep their edges bend at most ±180° (no pipes).
    const shapeLimit = p().edgeAnchor > 0 ? 180 : 360;
    rangeField(root, "Shape °", -shapeLimit, shapeLimit, 1, () => p().curve, (v) => editor.edit((tr) => {
      tr.points[s.index].curve = v;
      clampShape(tr.points[s.index]);
    }, false, `curve:${s.index}`));
    rangeField(root, "Flat in %", 0, 90, 1, () => Math.round(p().flat * 100),
      (v) => editor.edit((tr) => (tr.points[s.index].flat = v / 100), false, `flat:${s.index}`));
    rangeField(root, "Sausage %", 0, 100, 1, () => Math.round(p().sausage * 100),
      (v) => editor.edit((tr) => (tr.points[s.index].sausage = v / 100), false, `sausage:${s.index}`));
    rangeField(root, "Keep edges %", 0, 100, 1, () => Math.round(p().edgeAnchor * 100), (v) => editor.edit((tr) => {
      tr.points[s.index].edgeAnchor = v / 100;
      clampShape(tr.points[s.index]);
    }, false, `edgeAnchor:${s.index}`));
    checkbox(root, "Borders", () => p().borders, (v) => editor.edit((tr) => {
      tr.points[s.index].borders = v;
      syncSegments(tr);
    }, true));
    if (p().borders)
      rangeField(root, "Border m", 0.2, 8, 0.1, () => p().borderHeight,
        (v) => editor.edit((tr) => (tr.points[s.index].borderHeight = v), false, `borderHeight:${s.index}`));
    else
      hint(root, "No borders here: they fade out from the previous point to this one and back in towards the next.");
    const waypoints = WAYPOINTS.filter((w) => editor.track.closed || w !== "StartFinish");
    selectField(root, "Waypoint", waypoints, () => p().waypoint, (v) => editor.edit((tr) => (tr.points[s.index].waypoint = v as Waypoint), true),
      false, { StartFinish: "Start / finish (laps)" });
    if (p().waypoint !== "None")
      rangeField(root, "Gate height m", 2, 30, 0.5, () => p().gateHeight ?? DEFAULT_GATE_HEIGHT, (v) => editor.edit((tr) => {
        const h = Math.max(1, Math.min(100, v));
        if (h === DEFAULT_GATE_HEIGHT) delete tr.points[s.index].gateHeight;
        else tr.points[s.index].gateHeight = h;
      }, false, `gateHeight:${s.index}`), 100);
    if (p().waypoint !== "None" && p().waypoint !== "Start")
      checkbox(root, "See-through plane in the gate", () => p().gatePlane !== false, (v) => editor.edit((tr) => {
        if (v) delete tr.points[s.index].gatePlane;
        else tr.points[s.index].gatePlane = false;
      }, true));
    if (p().waypoint === "Checkpoint") {
      // Groups in use anywhere on the map (linked checkpoints may sit on different tracks).
      const groups = [...new Set(editor.map.tracks.flatMap((tr) => tr.points.map((q) => q.checkpointGroup ?? 0)).filter((g) => g > 0))].sort((a, b) => a - b);
      const mine = p().checkpointGroup ?? 0;
      const options = ["0", ...groups.map(String), "new"];
      const labels: Record<string, string> = { "0": "Not linked", new: "New group" };
      for (const g of groups) {
        const count = editor.map.tracks.reduce((n, tr) => n + tr.points.filter((q) => q.checkpointGroup === g).length, 0);
        labels[String(g)] = `Group ${g} (${count - (g === mine ? 1 : 0)} other${count - (g === mine ? 1 : 0) === 1 ? "" : "s"})`;
      }
      selectField(root, "Linked", options, () => String(p().checkpointGroup ?? 0), (v) => editor.edit((tr) => {
        const point = tr.points[s.index];
        const group = v === "new" ? Math.max(0, ...groups) + 1 : Number(v);
        if (group > 0) point.checkpointGroup = group;
        else delete point.checkpointGroup;
      }, true), false, labels);
      hint(root, "Linked checkpoints count as one: passing any checkpoint of the group is enough (e.g. on alternative routes, also on other tracks). Green lines join them in the editor.");
    }
    buttonRow(root, SHAPE_PRESETS.map((preset) => ({
      label: preset.label,
      onClick: () => editor.edit((tr) => {
        const point = tr.points[s.index];
        // Pipes need room: keep the diameter (width / π) at least about 16 m.
        if (preset.curve >= 270) point.width = Math.max(point.width, 50);
        // U walls need height: keep the curved part of each side at least about 8 m.
        if (preset.flat > 0) point.width = Math.max(point.width, 32);
        point.curve = preset.curve;
        point.flat = preset.flat;
        point.sausage = preset.sausage;
        // A pipe cannot keep its edges.
        if (Math.abs(preset.curve) > 180) point.edgeAnchor = 0;
      }),
    })));
    profileFields(root, editor, s.index);
    hint(root, "Drag the point to move it (Shift: keep height, Alt: height only, Ctrl: snap to 1 m). Drag the blue handles to shape the curve. "
      + "Drag the orange ring to bank the road (Ctrl: 5° steps), or use Q / E. The road always rolls the shorter way "
      + "between two points, so a corkscrew is a few points stepping round (e.g. 0° → 120° → -120° → 0°). "
      + "[ / ] change the width (Shift: 4 m); "
      + "the road blends smoothly between the widths of neighbouring points. Shape bends the road: negative = sausage, "
      + "positive = half-pipe, 360° = closed pipe (diameter = width / π; turn borders off for closed pipes). "
      + "Flat in keeps the middle flat and bends only the sides (Shape 180° + Flat in 50% = U profile with vertical walls); "
      + "Sausage compresses the bend into a crown with straight slopes: 0% = round, 100% = triangle, in between a rounded "
      + "triangle like the game's sausage roads.");
  } else if (s?.kind === "segment") {
    const seg = () => editor.track.segments[s.index];
    section(`Segment ${s.index + 1}`);
    selectField(root, "Surface", SURFACES, () => seg().surface, (v) => editor.edit((tr) => (tr.segments[s.index].surface = v as Surface)));
    // Borders are switched on the points (either end of the segment); the segment picks their material.
    if (seg().borders) {
      selectField(root, "Border material", BORDER_MATERIALS, () => seg().borderMaterial,
        (v) => editor.edit((tr) => (tr.segments[s.index].borderMaterial = v as BorderMaterial)));
      if (seg().borderMaterial === "Plastic")
        hint(root, "Plastic borders can be grinded without losing speed, and the car bounces off them.");
    } else hint(root, "No borders: neither of this segment's points has them (turn them on in a point).");
    selectField(root, "Color", ITEM_COLORS, () => seg().color, (v) => editor.edit((tr) => (tr.segments[s.index].color = v as ItemColor)),
      false);
    selectField(root, "Bumps", BUMP_PATTERNS, () => seg().bumps, (v) => editor.edit((tr) => (tr.segments[s.index].bumps = v as BumpPattern)));
    if (seg().bumps !== "None") {
      const setSeg = (key: string, fn: (segment: Track["segments"][number]) => void) =>
        editor.edit((tr) => fn(tr.segments[s.index]), false, `${key}:${s.index}`);
      rangeField(root, "Bump height m", -20, 20, 0.05, () => seg().bumpHeight, (v) => setSeg("bumpHeight", (x) => (x.bumpHeight = Math.max(-20, Math.min(20, v)))), 20);
      rangeField(root, seg().bumps === "Noise" ? "Bump size m" : "Bump spacing m", 1, seg().bumps === "Noise" ? 512 : 64, 0.5, () => seg().bumpSize,
        (v) => setSeg("bumpSize", (x) => (x.bumpSize = Math.max(0.5, Math.min(512, v)))), 512);
      rangeField(root, "Bump detail m", 0.25, 8, 0.25, () => seg().bumpDetail, (v) => setSeg("bumpDetail", (x) => (x.bumpDetail = Math.max(0.25, v))), 16);
      if (seg().bumps === "Noise")
        rangeField(root, "Seed", 0, 100, 1, () => seg().bumpSeed ?? 0, (v) => setSeg("bumpSeed", (x) => {
          const seed = Math.round(Math.max(0, Math.min(99999, v)));
          if (seed === 0) delete x.bumpSeed;
          else x.bumpSeed = seed;
        }), 99999);
      hint(root, {
        Waves: "Rolling bumps across the whole width, one every spacing along the road (washboard). ",
        Noise: "Uneven ground: smooth random bumps of about this size. ",
        Ridges: "Ridges along the road, one every spacing across it (rumble lanes). ",
      }[seg().bumps as Exclude<BumpPattern, "None">]
        + "Real geometry the car feels; it fades in and out at the segment's ends and towards the edges. Detail is how "
        + "finely the surface is cut: about half the spacing looks smooth; smaller costs more triangles (see the export "
        + "size estimate).");
    }
    selectField(root, "Signs", ROAD_SIGNS, () => seg().signs, (v) => editor.edit((tr) => (tr.segments[s.index].signs = v as RoadSignKind)),
      false, ROAD_SIGN_LABELS);
    if (seg().signs !== "None") {
      selectField(root, "Sign side", SIGN_SIDES, () => seg().signSide, (v) => editor.edit((tr) => (tr.segments[s.index].signSide = v as SignSide)));
      rangeField(root, "Sign spacing m", 4, 64, 1, () => seg().signSpacing,
        (v) => editor.edit((tr) => (tr.segments[s.index].signSpacing = Math.max(2, v)), false, `signSpacing:${s.index}`), 256);
      hint(root, "The game's curve signs (chevrons) on top of the borders (at the road's edge without them), facing the "
        + "oncoming cars; exported as the game's items, shown here as simple panels.");
    }
    checkbox(root, "Underside", () => seg().underside, (v) => editor.edit((tr) => (tr.segments[s.index].underside = v)));
    if (!seg().underside)
      hint(root, "Without underside this segment is only the driving surface and its borders: about half the triangles, "
        + "which helps with the game's 5 MB limit for custom items. Seen from below the road is invisible.");

    section("Effect");
    const supported = EFFECT_SURFACES.has(seg().surface);
    selectField(root, "Effect", EFFECTS, () => seg().effect, (v) => editor.edit((tr) => {
      const segment = tr.segments[s.index];
      // Reactor boosts push away from the road by default (their direction is "up" for the item).
      const reactor = (e: string) => e.startsWith("ReactorBoost");
      if (reactor(v) && !reactor(segment.effect)) {
        segment.effectYaw = 0;
        segment.effectPitch = 90;
      } else if (!reactor(v) && reactor(segment.effect)) {
        segment.effectPitch = 0;
      }
      segment.effect = v as Effect;
    }, true), !supported, EFFECT_LABELS);
    if (!supported) hint(root, "This surface cannot carry gameplay effects. Use a Road or Platform surface (Tech, Dirt, Bump, Ice, Grass).");
    else if (DIRECTIONAL_EFFECTS.has(seg().effect)) {
      rangeField(root, "Direction °", -180, 180, 1, () => seg().effectYaw,
        (v) => editor.edit((tr) => (tr.segments[s.index].effectYaw = v), false, `effectYaw:${s.index}`));
      if (seg().effect.startsWith("ReactorBoost")) {
        rangeField(root, "Tilt °", -90, 90, 1, () => seg().effectPitch,
          (v) => editor.edit((tr) => (tr.segments[s.index].effectPitch = v), false, `effectPitch:${s.index}`));
        hint(root, "Direction turns the boost on the road (0° = forward, 90° = right, 180° = backwards); tilt aims it up "
          + "or down (90° = away from the road). The arrows show where it pushes.");
      } else {
        // The game only applies the part of the push along the car, so tilting the push out of the road plane by
        // acos(strength) scales the boost. The decals stay along the road either way.
        rangeField(root, "Strength %", 0, 100, 1, () => Math.round(Math.cos(seg().effectPitch * Math.PI / 180) * 100),
          (v) => editor.edit((tr) => {
            const segment = tr.segments[s.index];
            segment.effectPitch = (segment.effectPitch < 0 ? -1 : 1) * Math.acos(v / 100) * 180 / Math.PI;
          }, false, `effectPitch:${s.index}`));
        hint(root, "Direction turns the boost on the road (0° = forward, 90° = right, 180° = backwards); strength scales "
          + "its push. The arrows show where it pushes, shorter when weaker.");
      }
    } else if (seg().effect !== "None") {
      hint(root, "Takes effect when driving onto this segment; Reset cancels running effects.");
    }
    hint(root, "Press I, then click the road to insert a point at that spot.");
  } else if (s?.kind === "block" && editor.map.blocks[s.index]) {
    const b = () => editor.map.blocks[s.index];
    const set = (key: string, fn: (block: TrackMap["blocks"][number]) => void) =>
      editor.editBlocks((blocks) => fn(blocks[s.index]), `block${key}:${s.index}`);
    section(b().pillar ? "Pillar (column piece)" : "Vanilla block");
    textField(root, "Block", () => b().name, (v) => set("Name", (x) => (x.name = v.replace(/[^\w]/g, ""))));
    if (!b().pillar) checkbox(root, "Free", () => !!b().free, () => editor.toggleFreeBlock());
    if (b().free) {
      (["X", "Y", "Z"] as const).forEach((axis, i) =>
        numberField(root, `${axis} m`, () => b().position?.[i] ?? 0, (v) => set(axis, (x) => {
          const p = [...(x.position ?? [0, 0, 0])] as [number, number, number];
          p[i] = v;
          x.position = p;
        }), 0.5));
      rangeField(root, "Yaw °", -180, 180, 1, () => b().yaw ?? 0, (v) => set("Yaw", (x) => (x.yaw = wrapBank(v))), 360);
      rangeField(root, "Pitch °", -90, 90, 1, () => b().pitch ?? 0, (v) => set("Pitch", (x) => (x.pitch = Math.max(-90, Math.min(90, v)))), 90);
      rangeField(root, "Roll °", -180, 180, 1, () => b().roll ?? 0, (v) => set("Roll", (x) => (x.roll = wrapBank(v))), 360);
    } else {
      numberField(root, "Cell X", () => b().x, (v) => set("X", (x) => (x.x = Math.round(v))), 1);
      numberField(root, "Cell Z", () => b().z, (v) => set("Z", (x) => (x.z = Math.round(v))), 1);
      numberField(root, "Level", () => b().y, (v) => set("Y", (x) => (x.y = Math.max(0, Math.min(MAX_BLOCK_LEVEL, Math.round(v))))), 1);
      if (!b().pillar)
        checkbox(root, "Pillars", () => b().pillars !== false, (v) => set("Pillars", (x) => {
          if (v) delete x.pillars;
          else x.pillars = false;
        }), b().y === 0);
      else
        hint(root, "A column piece placed by hand: exported with the game's pillar flag (level 0 as the ground piece). "
          + "Automatic columns of blocks above stop on top of it.");
      selectField(root, "Direction", DIRECTIONS, () => DIRECTIONS[b().dir], (v) => set("Dir", (x) => (x.dir = DIRECTIONS.indexOf(v as typeof DIRECTIONS[number]))));
    }
    selectField(root, "Color", ITEM_COLORS, () => b().color ?? "Default", (v) => set("Color", (x) => {
      if (v === "Default") delete x.color;
      else x.color = v as ItemColor;
    }));
    buttonRow(root, [
      { label: "Rotate (R)", onClick: () => editor.rotateBlock(1) },
      { label: "Place another", onClick: () => editor.setBlockPlaceMode(b().name, { ...(b().free ? { free: true, yaw: b().yaw ?? 0 } : { free: false, dir: b().dir }), color: b().color ?? "Default" }) },
      { label: "Delete", onClick: removePoint },
    ]);
    if (editor.library && !editor.library.has(b().name))
      hint(root, "This block is not in the local block library, so it shows as a box. The game still places it by name.");
    hint(root, b().free
      ? "Free block: drag it to move it on its height (snap mode: on the grid; Ctrl: ignore snapping). R turns it 15° "
        + "(Shift+R back), PgUp / PgDn raise or lower it (by the vertical snap step, or 1 m). X / Y / Z are its origin corner. "
        + "Tab (or Free) turns it back into a grid block at the nearest cell."
      : "Drag the selected block to move it by cells. R turns it (Shift+R back), PgUp / PgDn change its level (8 m), "
        + "Del removes it. Cells are 32 m from the map center; level 0 is the ground (the block's ground variant). "
        + "Tab (or Free) turns it into a free block, which can be anywhere and at any angle.");
  } else if (s?.kind === "blocks") {
    const blocks = s.indices.map((i) => editor.map.blocks[i]).filter(Boolean);
    section(`${blocks.length} blocks`);
    const counts = new Map<string, number>();
    for (const b of blocks) counts.set(b.name, (counts.get(b.name) ?? 0) + 1);
    const names = [...counts].sort((a, b) => b[1] - a[1]);
    hint(root, names.slice(0, 6).map(([name, n]) => `${n}× ${name}`).join(", ") + (names.length > 6 ? `, … (${names.length} kinds)` : ""));
    buttonRow(root, [
      { label: "Deselect", onClick: () => editor.select(null) },
      { label: "Delete", onClick: removePoint },
    ]);
    hint(root, "Drag one of them to move them all (by whole cells as long as a grid block is among them). PgUp / PgDn "
      + "or Ctrl+wheel raise or lower them all, Del removes them. Shift+click a block to add or remove it, drag a rectangle "
      + "to select (Shift: add, Ctrl: remove).");
  } else if (s?.kind === "object" && obj) {
    const o = () => editor.track.objects[s.index];
    const set = (key: string, fn: (object: Track["objects"][number]) => void) =>
      editor.edit((tr) => fn(tr.objects[s.index]), false, `object${key}:${s.index}`);
    section(obj.kind);
    if (obj.kind === "Pillar") {
      // The game's obstacle pillars: round, 7 m tall, in these thicknesses.
      const sizes = ["2", "4", "8", "16"] as const;
      const nearest = () => sizes.reduce((a, b) => (Math.abs(Math.log(o().width / +b)) < Math.abs(Math.log(o().width / +a)) ? b : a));
      selectField(root, "Size", sizes, nearest, (v) => editor.edit((tr) => {
        const x = tr.objects[s.index];
        x.width = x.length = +v;
      }), false, { 2: "2 m", 4: "4 m", 8: "8 m", 16: "16 m" });
      rangeField(root, "Offset m", -32, 32, 0.1, () => o().offset, (v) => set("Offset", (x) => (x.offset = v)), 256);
      selectField(root, "Color", ITEM_COLORS, () => o().color ?? "Default", (v) => editor.edit((tr) => {
        const x = tr.objects[s.index];
        if (v === "Default") delete x.color;
        else x.color = v as ItemColor;
      }));
    } else if (obj.kind === "Gate") {
      selectField(root, "Gate", Object.keys(GATES), () => o().gate ?? "Turbo", (v) => editor.edit((tr) => (tr.objects[s.index].gate = v)),
        false, Object.fromEntries(Object.entries(GATES).map(([k, g]) => [k, g.label])));
      rangeField(root, "Road at (m up the ring)", 0, 32, 0.5, () => o().height, (v) => set("Height", (x) => (x.height = Math.max(0.1, Math.min(32, v)))), 32);
    } else if (obj.kind !== "Gap") {
      selectField(root, "Shape", OBJECT_SHAPES, () => o().shape, (v) => editor.edit((tr) => (tr.objects[s.index].shape = v as ObjectShape)),
        false, { Rect: "Rectangle" });
      rangeField(root, "Length m", 0.5, 32, 0.1, () => o().length, (v) => set("Length", (x) => (x.length = Math.max(0.1, v))), 256);
      rangeField(root, "Width m", 0.5, 32, 0.1, () => o().width, (v) => set("Width", (x) => (x.width = Math.max(0.1, v))), 256);
      rangeField(root, "Offset m", -32, 32, 0.1, () => o().offset, (v) => set("Offset", (x) => (x.offset = v)), 256);
      if (obj.kind === "Bump")
        rangeField(root, "Height m", -20, 20, 0.05, () => o().height, (v) => set("Height", (x) => (x.height = Math.max(-20, Math.min(20, v)))), 20);
    } else {
      rangeField(root, "Length m", 1, 128, 0.5, () => o().length, (v) => set("Length", (x) => (x.length = Math.max(0.1, v))), 1024);
    }
    buttonRow(root, [{ label: "Delete", onClick: removeObject(editor) }]);
    const placed = editor.placement(s.index)?.placed ?? true;
    if (!placed && obj.kind !== "Gate") hint(root, obj.kind === "Pillar" ? "This pillar is not built: it stands in a gap." : "This gap has no length on the track.");
    hint(root, {
      Pillar: "Drag the marker to move the pillar. It exports as the game's own obstacle pillar (round, 7 m tall, 2 to 16 m "
        + "thick), standing on the road upright to the surface (also on banked and bent road) and inside the driving surface.",
      Hole: "Drag the marker to move the hole along and across the road. It cuts through the road (and its boost marking) and "
        + "always stays inside the driving surface. Length runs along the road, width across it.",
      Gap: "Drag the marker to move the gap along the road. The road is left out over its whole width, with closed ends, "
        + "so you can build jumps.",
      Gate: "Drag the marker to move the gate along the road. It is the game's own gate block: a ring 32 m across with an "
        + "opening of 26 m, standing round the road upright to its surface, so its effect or car switch works as in the game. "
        + "The ring does not change size. \"Road at\" sets how high in the ring the road runs: 16 m is its middle, where a road "
        + "26 m wide just fits (the lower half of the ring hangs under the road); lower values lift the ring, for narrow roads.",
      Bump: "Drag the marker to move the bump. A smooth hump in the driving surface (negative height: a dip), real "
        + "geometry the car drives over; it flattens out towards the road's edges. The surface is cut finer around it "
        + "(see the segment's bump detail).",
    }[obj.kind]);
  } else {
    hint(root, "Drag a point to move it, click it to show its handles and banking ring. Click the road to edit a segment. "
      + "Ctrl+click the ground to extend the track. Pillar, Hole and Gap place things on the road. "
      + "Start, checkpoints and finish are only where you set them (select a point → Waypoint), or add them later in the game's editor.");
  }
}

/** Deletes the selected object (Delete button in the object inspector). */
const removeObject = (editor: Editor) => () => {
  const s = editor.selection;
  if (s?.kind !== "object") return;
  editor.edit((t) => void t.objects.splice(s.index, 1), true);
  editor.select(null);
};

function row(root: HTMLElement, label: string): HTMLLabelElement {
  const el = document.createElement("label");
  el.className = "row";
  el.append(Object.assign(document.createElement("span"), { textContent: label }));
  root.appendChild(el);
  return el;
}

function bindRefresh(input: HTMLInputElement | HTMLSelectElement, refresh: () => void) {
  input.dataset.refresh = "";
  input.addEventListener("refresh", () => { if (document.activeElement !== input) refresh(); });
  refresh();
}

function numberField(root: HTMLElement, label: string, get: () => number, set: (v: number) => void, step: number) {
  const input = Object.assign(document.createElement("input"), { type: "number", step: String(step) });
  bindRefresh(input, () => (input.value = String(round(get()))));
  input.addEventListener("change", () => { const v = parseFloat(input.value); if (Number.isFinite(v)) set(v); });
  row(root, label).appendChild(input);
}

/** Slider plus number field. The number field may accept a wider range (`inputLimit`) than the slider shows. */
function rangeField(root: HTMLElement, label: string, min: number, max: number, step: number, get: () => number, set: (v: number) => void,
  inputLimit?: number) {
  const r = row(root, label);
  const range = Object.assign(document.createElement("input"), { type: "range", min: String(min), max: String(max), step: String(step) });
  const [nmin, nmax] = inputLimit === undefined ? [min, max] : [-inputLimit, inputLimit];
  const num = Object.assign(document.createElement("input"), { type: "number", min: String(nmin), max: String(nmax), step: String(step), className: "small" });
  bindRefresh(range, () => (range.value = String(get())));
  bindRefresh(num, () => (num.value = String(round(get()))));
  // Callers pass a merge key to editor.edit, so one slider drag becomes one undo step.
  range.addEventListener("input", () => { set(parseFloat(range.value)); num.value = range.value; });
  num.addEventListener("change", () => { const v = parseFloat(num.value); if (Number.isFinite(v)) set(clamp(v, nmin, nmax)); });
  r.append(range, num);
}

function checkbox(root: HTMLElement, label: string, get: () => boolean, set: (v: boolean) => void, disabled = false) {
  const input = Object.assign(document.createElement("input"), { type: "checkbox", disabled });
  bindRefresh(input, () => (input.checked = get()));
  input.addEventListener("change", () => set(input.checked));
  row(root, label).appendChild(input);
}

function textField(root: HTMLElement, label: string, get: () => string, set: (v: string) => void) {
  const input = Object.assign(document.createElement("input"), { type: "text" });
  bindRefresh(input, () => (input.value = get()));
  input.addEventListener("change", () => set(input.value.trim() || "Untitled"));
  row(root, label).appendChild(input);
}

function selectField(root: HTMLElement, label: string, options: readonly string[], get: () => string, set: (v: string) => void, disabled = false,
  labels: Partial<Record<string, string>> = {}) {
  const select = document.createElement("select");
  select.disabled = disabled;
  for (const o of options) select.append(new Option(labels[o] ?? o.replace(/([a-z])([A-Z])/g, "$1 $2"), o));
  bindRefresh(select, () => (select.value = get()));
  select.addEventListener("change", () => set(select.value));
  row(root, label).appendChild(select);
}

function buttonRow(root: HTMLElement, buttons: { label: string; onClick: () => void }[]) {
  const row = document.createElement("div");
  row.className = "button-row";
  for (const b of buttons) {
    const button = Object.assign(document.createElement("button"), { textContent: b.label, type: "button" });
    button.addEventListener("click", b.onClick);
    row.appendChild(button);
  }
  root.appendChild(row);
}

// ---- saved point profiles ---------------------------------------------------------------------

/** A point's cross-section settings, saved under a name (not its position, handles, bank or waypoint). */
type PointProfile = Pick<TrackPoint, "width" | "curve" | "flat" | "sausage" | "edgeAnchor" | "borders" | "borderHeight">;
const PROFILE_FIELDS = ["width", "curve", "flat", "sausage", "edgeAnchor", "borders", "borderHeight"] as const;
/** Shared by all tracks (per `?session`, like the autosave). */
const PROFILES_KEY = SESSION ? `tmspline.profiles.${SESSION}` : "tmspline.profiles";
let profileChoice = "";
let profileName = "";

function loadProfiles(): Record<string, PointProfile> {
  try {
    const parsed = JSON.parse(localStorage.getItem(PROFILES_KEY) ?? "{}");
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

const saveProfiles = (profiles: Record<string, PointProfile>) => localStorage.setItem(PROFILES_KEY, JSON.stringify(profiles));

/** Pick a saved profile and apply it to the point, or save the point's settings as one. */
function profileFields(root: HTMLElement, editor: Editor, index: number) {
  const profiles = loadProfiles();
  const names = Object.keys(profiles).sort((a, b) => a.localeCompare(b));
  if (!names.includes(profileChoice)) profileChoice = names[0] ?? "";

  const pick = row(root, "Profile");
  const select = document.createElement("select");
  if (!names.length) select.append(new Option("(none saved)", ""));
  for (const n of names) select.append(new Option(n, n));
  select.value = profileChoice;
  select.disabled = !names.length;
  select.addEventListener("change", () => (profileChoice = select.value));
  pick.appendChild(select);
  if (names.length)
    buttonRow(root, [
      {
        label: "Apply profile",
        onClick: () => {
          const profile = loadProfiles()[profileChoice];
          if (!profile) return;
          editor.edit((tr) => {
            const point = tr.points[index] as unknown as Record<string, unknown>;
            for (const f of PROFILE_FIELDS) if (profile[f] !== undefined) point[f] = profile[f];
            clampShape(tr.points[index]);
            syncSegments(tr);
          }, true);
        },
      },
      {
        label: "Delete profile",
        onClick: () => {
          const all = loadProfiles();
          delete all[profileChoice];
          saveProfiles(all);
          profileChoice = "";
          editor.onChange();
        },
      },
    ]);

  const save = row(root, "Save as");
  const name = Object.assign(document.createElement("input"), { type: "text", placeholder: "Profile name", value: profileName });
  name.addEventListener("input", () => (profileName = name.value));
  const button = Object.assign(document.createElement("button"), { textContent: "Save", type: "button" });
  const store = () => {
    const n = name.value.trim();
    if (!n) return name.focus();
    const point = editor.track.points[index];
    const all = loadProfiles();
    all[n] = Object.fromEntries(PROFILE_FIELDS.map((f) => [f, point[f]])) as PointProfile;
    saveProfiles(all);
    profileChoice = n;
    profileName = "";
    editor.onChange();
  };
  button.addEventListener("click", store);
  name.addEventListener("keydown", (e) => { if (e.key === "Enter") store(); e.stopPropagation(); });
  save.append(name, button);
  hint(root, "Profiles keep a point's width, shape, flat in, sausage and borders (not its position, bank or waypoint), "
    + "for all tracks. Saving under an existing name replaces that profile.");
}

function hint(root: HTMLElement, text: string) {
  root.appendChild(Object.assign(document.createElement("p"), { className: "hint", textContent: text }));
}

// ---- files ------------------------------------------------------------------------------------

function download(track: TrackMap): void {
  const blob = new Blob([JSON.stringify(track, null, 2)], { type: "application/json" });
  const a = Object.assign(document.createElement("a"), {
    href: URL.createObjectURL(blob),
    download: `${track.name.replace(/[^\w-]+/g, "_") || "track"}.track.json`,
  });
  a.click();
  URL.revokeObjectURL(a.href);
}

function openFile(editor: Editor, opened?: () => void): void {
  const input = Object.assign(document.createElement("input"), { type: "file", accept: ".json,application/json" });
  input.addEventListener("change", async () => {
    const file = input.files?.[0];
    if (!file) return;
    try {
      const map = parseMap(await file.text());
      editor.replaceMap(map);
      editor.select(null);
      editor.frameTrack();
      opened?.();
    } catch (err) {
      alert(`Could not open ${file.name}: ${(err as Error).message}`);
    }
  });
  input.click();
}

const confirmDiscard = () => confirm("Start a new track? The current one can still be restored with Undo.");
const clamp = (v: number, min: number, max: number) => Math.max(min, Math.min(max, v));
const round = (v: number) => Math.round(v * 100) / 100;

/** A box over the 3D view while the editor works on something that takes a while, with what and for how long. */
function busyOverlay(editor: Editor): void {
  const host = editor.renderer.domElement.parentElement!;
  if (!document.getElementById("busy-style")) {
    const style = document.createElement("style");
    style.id = "busy-style";
    style.textContent = `
      .busy-overlay { position: absolute; left: 50%; top: 18px; transform: translateX(-50%); z-index: 20; display: flex; gap: 10px;
        align-items: center; padding: 8px 14px; border-radius: 8px; background: rgba(16, 18, 28, 0.88); color: #eef0f6;
        font: 13px system-ui, sans-serif; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35); pointer-events: none; }
      .busy-overlay[hidden] { display: none; }
      .busy-overlay .spin { width: 14px; height: 14px; border-radius: 50%; border: 2px solid rgba(255, 255, 255, 0.25);
        border-top-color: #ffe066; animation: busy-spin 0.8s linear infinite; }
      .busy-overlay .time { opacity: 0.6; font-variant-numeric: tabular-nums; }
      @keyframes busy-spin { to { transform: rotate(360deg); } }`;
    document.head.appendChild(style);
  }
  const box = document.createElement("div");
  box.className = "busy-overlay";
  box.hidden = true;
  const spin = document.createElement("div");
  spin.className = "spin";
  const text = document.createElement("span");
  const time = document.createElement("span");
  time.className = "time";
  box.append(spin, text, time);
  if (getComputedStyle(host).position === "static") host.style.position = "relative";
  host.appendChild(box);
  let since = 0, timer = 0;
  editor.onBusy = (message) => {
    if (!message) {
      box.hidden = true;
      clearInterval(timer);
      timer = 0;
      return;
    }
    text.textContent = message;
    if (box.hidden) {
      since = performance.now();
      box.hidden = false;
      time.textContent = "";
      timer = window.setInterval(() => (time.textContent = `${Math.round((performance.now() - since) / 1000)} s`), 500);
    }
  };
}
