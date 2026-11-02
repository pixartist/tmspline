const COLLAPSED_KEY = "tmspline.helpCollapsed";

const CONTROLS: [string, string][] = [
  ["Drag point / handle", "Move parallel to the screen"],
  ["+ Shift", "Keep height (move on the ground plane)"],
  ["+ Alt (once dragging)", "Change height only"],
  ["+ Ctrl", "Snap to 1 m"],
  ["Drag orange ring", "Bank the road (Ctrl: 5° steps)"],
  ["Q / E", "Bank −5° / +5° (Shift: 15°)"],
  ["[ / ]", "Width −1 / +1 m at the point (Shift: 4 m)"],
  ["Shape (inspector)", "Sausage · flat · half-pipe · pipe, blended between points"],
  ["Click road", "Select segment (surface, borders, effects)"],
  ["Waypoint (point)", "Start · checkpoint · finish (only where you set them)"],
  ["T", "New track in the map (click a grey point or road to edit another track)"],
  ["Ctrl+click ground", "Extend the track"],
  ["N / Del", "Add point at the end · delete point"],
  ["I, then click road", "Insert a point there (preview while hovering; Esc cancels)"],
  ["P / H, then click road", "Place a pillar (obstacle) / cut a hole there (Gap button: leave out the road); drag the diamond marker to move it"],
  ["B, then click map", "Place a vanilla game block (R rotate, PgUp/PgDn or Ctrl+wheel level, Tab grid / free, Esc or right click: done); drag the selected block"],
  ["Click / Shift+click block", "Select a block / add or remove it (several: drag one to move all, Del deletes, PgUp/PgDn raise)"],
  ["Left drag empty space", "Select blocks in the rectangle (Shift: add, Ctrl: remove)"],
  ["X", "Snap mode: points and free blocks snap to the grid (step next to the Snap button; Ctrl while dragging ignores it)"],
  ["M / Shift+M", "Smooth selection / whole track (strength: slider next to Smooth)"],
  ["Alt + left drag", "Orbit (Alt+Shift: pan)"],
  ["Right drag / wheel", "Pan / zoom"],
  ["W S / A D", "Fly along the view / sideways (speed follows the distance to the road)"],
  ["Space / Shift", "Camera up / down"],
  ["F / G", "Frame track · axis gizmo"],
  ["Ctrl+Z / Ctrl+Y", "Undo / redo"],
];

/** Collapsible controls overview in the bottom-right corner of the viewport. */
export function createControlsHelp(viewport: HTMLElement): void {
  const box = document.createElement("section");
  box.className = "controls-help";

  const header = document.createElement("button");
  header.className = "controls-help-header";
  header.title = "Show or hide the controls";

  const table = document.createElement("dl");
  for (const [keys, action] of CONTROLS) {
    table.append(Object.assign(document.createElement("dt"), { textContent: keys }));
    table.append(Object.assign(document.createElement("dd"), { textContent: action }));
  }

  const apply = (collapsed: boolean) => {
    box.classList.toggle("collapsed", collapsed);
    header.textContent = collapsed ? "Controls ▴" : "Controls ▾";
    localStorage.setItem(COLLAPSED_KEY, collapsed ? "1" : "0");
  };
  header.addEventListener("click", () => apply(!box.classList.contains("collapsed")));
  apply(localStorage.getItem(COLLAPSED_KEY) === "1");

  box.append(header, table);
  viewport.appendChild(box);
}
