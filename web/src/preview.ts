import * as THREE from "three";
import { BLOCK_CELL, BLOCK_LEVEL, type VanillaBlock } from "./track";
import { BlockLayer, pillarColumns, VanillaLibrary } from "./vanilla";

/**
 * Development tool: a contact sheet of vanilla blocks as the editor draws them (same library, clips, adapters and
 * columns), from fixed views, so block issues can be checked by name instead of described.
 *
 *   /preview.html?blocks=PlatformGrassSlope2Straight,PlatformGrassLoopStart&levels=0,3&size=360
 *
 * blocks: comma-separated names; levels: grid levels to place each block at (0 = ground; above it gets its columns);
 * dir: block direction 0..3; size: pixels per view. Rows are block × level, columns the views. window.previewDone turns
 * true when the sheet is complete.
 */

const params = new URLSearchParams(location.search);
const names = (params.get("blocks") ?? "PlatformGrassBase").split(",").map((s) => s.trim()).filter(Boolean);
const levels = (params.get("levels") ?? "0,3").split(",").map(Number).filter((n) => Number.isFinite(n) && n >= 0);
const dir = Math.round(Number(params.get("dir") ?? 0)) & 3;
const size = Math.max(160, Math.min(800, Number(params.get("size") ?? 320)));

/**
 * Views: azimuth (degrees; the camera sits at (sin, cos) of it on X/Z, so 0 is the North side, +Z) and elevation. The
 * game's East is -X, so +X+Z is North-West. The top view has North at the top and East on the right.
 */
const VIEWS: { label: string; azimuth: number; elevation: number }[] = [
  { label: "from N-W", azimuth: 45, elevation: 28 },
  { label: "from S-W", azimuth: 135, elevation: 28 },
  { label: "from S-E", azimuth: 225, elevation: 28 },
  { label: "from N-E", azimuth: 315, elevation: 28 },
  { label: "top (N up)", azimuth: 0, elevation: 89 },
];
const LABEL_W = 230;
const status = document.getElementById("status")!;
const sheet = document.getElementById("sheet") as HTMLCanvasElement;

declare global { interface Window { previewDone?: boolean } }

async function main(): Promise<void> {
  const lib = await VanillaLibrary.load();
  if (!lib) {
    status.textContent = "No block library (run tools/VanillaLib and the development server).";
    return;
  }
  const rows = names.flatMap((name) => levels.map((level) => ({ name, level })));
  sheet.width = LABEL_W + VIEWS.length * size;
  sheet.height = rows.length * size;
  const ctx = sheet.getContext("2d")!;
  ctx.fillStyle = "#1b1f24";
  ctx.fillRect(0, 0, sheet.width, sheet.height);

  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(1);
  renderer.setSize(size, size);
  const camera = new THREE.PerspectiveCamera(35, 1, 0.5, 8000);

  for (const [r, row] of rows.entries()) {
    status.textContent = `Rendering ${r + 1}/${rows.length}: ${row.name} (level ${row.level})…`;
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x9cc7ee);
    scene.add(new THREE.HemisphereLight(0xdfefff, 0x4a6b3a, 1.6));
    const sun = new THREE.DirectionalLight(0xffffff, 1.8);
    sun.position.set(300, 600, 200);
    scene.add(sun);
    const ground = new THREE.Mesh(new THREE.PlaneGeometry(4000, 4000).rotateX(-Math.PI / 2), new THREE.MeshStandardMaterial({ color: 0x6f9e4f, roughness: 1 }));
    scene.add(ground);
    const grid = new THREE.GridHelper(32 * BLOCK_CELL, 32, 0x3f5f30, 0x557a42);
    grid.position.y = 0.02;
    scene.add(grid);

    const known = lib.has(row.name);
    const block: VanillaBlock = { name: row.name, x: 0, y: row.level, z: 0, dir };
    const layer = new BlockLayer();
    layer.setLibrary(lib);
    // Only real geometry: the editor's ground shadow looks like a floor plate here.
    layer.shadowsVisible = false;
    const pillars = pillarColumns([block], lib);
    layer.setNeighbours([block, ...pillars]);
    layer.sync([block], null, null);
    layer.syncPillars(pillars);
    scene.add(layer.group);
    await lib.settled();
    layer.group.updateMatrixWorld(true);

    // Frame the block and its columns down to the ground.
    const box = new THREE.Box3().setFromObject(layer.group);
    box.expandByPoint(new THREE.Vector3(box.min.x, 0, box.min.z));
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    const distance = (sphere.radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2))) * 1.05;

    const y0 = r * size;
    ctx.fillStyle = "#d8dde3";
    ctx.font = "bold 14px system-ui, sans-serif";
    wrap(ctx, row.name, 10, y0 + 24, LABEL_W - 20, 18);
    ctx.font = "13px system-ui, sans-serif";
    ctx.fillStyle = "#9aa4ae";
    ctx.fillText(row.level === 0 ? "on the ground" : `level ${row.level}, on columns`, 10, y0 + size - 44);
    ctx.fillText(`dir ${dir}; N = +Z`, 10, y0 + size - 24);
    if (!known) {
      ctx.fillStyle = "#ff8a80";
      ctx.fillText("not in the library", 10, y0 + size - 64);
    }

    for (const [v, view] of VIEWS.entries()) {
      const az = THREE.MathUtils.degToRad(view.azimuth), el = THREE.MathUtils.degToRad(view.elevation);
      camera.position.set(
        sphere.center.x + distance * Math.cos(el) * Math.sin(az),
        sphere.center.y + distance * Math.sin(el),
        sphere.center.z + distance * Math.cos(el) * Math.cos(az));
      // Top view: North (+Z) at the top of the image.
      camera.up.set(0, 1, 0);
      if (view.elevation > 80) camera.up.set(0, 0, 1);
      camera.lookAt(sphere.center);
      renderer.render(scene, camera);
      const x0 = LABEL_W + v * size;
      ctx.drawImage(renderer.domElement, x0, y0);
      drawGizmo(ctx, camera, x0, y0, block.y * BLOCK_LEVEL);
      ctx.fillStyle = "rgba(0,0,0,0.55)";
      ctx.fillRect(x0, y0, 92, 20);
      ctx.fillStyle = "#fff";
      ctx.fillText(view.label, x0 + 6, y0 + 14);
      ctx.strokeStyle = "#1b1f24";
      ctx.strokeRect(x0 + 0.5, y0 + 0.5, size - 1, size - 1);
    }
    scene.remove(layer.group);
  }
  status.textContent = `${rows.length} rows: ${names.length} block(s) × levels ${levels.join(", ")}, dir ${dir}.`;
  window.previewDone = true;
}

/**
 * Axis cross at the block's origin corner (cell 0,0 of its level): North (+Z), East (-X, the game's East) and Up, 16 m
 * long, projected with the view's camera.
 */
function drawGizmo(ctx: CanvasRenderingContext2D, camera: THREE.Camera, x0: number, y0: number, height: number): void {
  const origin = new THREE.Vector3(0, height + 0.5, 0);
  const toScreen = (p: THREE.Vector3) => {
    const q = p.clone().project(camera);
    return [x0 + (q.x + 1) / 2 * size, y0 + (1 - q.y) / 2 * size] as const;
  };
  const [ox, oy] = toScreen(origin);
  const axes: [string, THREE.Vector3, string][] = [
    ["N +Z", new THREE.Vector3(0, 0, 16), "#4da3ff"],
    ["E -X", new THREE.Vector3(-16, 0, 0), "#ff5a4f"],
    ["Up", new THREE.Vector3(0, 16, 0), "#58e06a"],
  ];
  ctx.save();
  ctx.lineWidth = 3;
  ctx.font = "bold 12px system-ui, sans-serif";
  for (const [label, d, colour] of axes) {
    const [ex, ey] = toScreen(origin.clone().add(d));
    ctx.strokeStyle = "#000";
    ctx.lineWidth = 5;
    ctx.beginPath(); ctx.moveTo(ox, oy); ctx.lineTo(ex, ey); ctx.stroke();
    ctx.strokeStyle = colour;
    ctx.lineWidth = 3;
    ctx.beginPath(); ctx.moveTo(ox, oy); ctx.lineTo(ex, ey); ctx.stroke();
    ctx.fillStyle = "#000";
    ctx.fillText(label, ex + 5, ey + 1);
    ctx.fillStyle = colour;
    ctx.fillText(label, ex + 4, ey);
  }
  ctx.restore();
}

/** Draws text wrapped at camel-case boundaries to fit the label column. */
function wrap(ctx: CanvasRenderingContext2D, text: string, x: number, y: number, width: number, lineHeight: number): void {
  const parts = text.split(/(?=[A-Z0-9])/);
  let line = "";
  for (const part of parts) {
    if (ctx.measureText(line + part).width > width && line) {
      ctx.fillText(line, x, y);
      y += lineHeight;
      line = part;
    } else line += part;
  }
  if (line) ctx.fillText(line, x, y);
}

void main();
