import * as THREE from "three";
import type { VanillaItem } from "./track";

/**
 * Stand-ins for the map's vanilla items (trees): their models are not converted, so each shows as a trunk and a crown,
 * sized and coloured by its name (spring / fall / fir / palm / frozen / cherry / cypress; very small .. tall). Instanced,
 * so thousands are cheap; not pickable.
 */
export class ItemLayer {
  readonly group = new THREE.Group();
  private key = "";

  sync(items: VanillaItem[] | undefined): void {
    const list = items ?? [];
    const key = `${list.length}:${list.map((i) => `${i.name}${i.position.join(",")}`).join(";").length}:${list[0]?.position.join(",")}`;
    if (key === this.key) return;
    this.key = key;
    for (const child of [...this.group.children]) {
      this.group.remove(child);
      (child as THREE.InstancedMesh).dispose?.();
    }
    if (!list.length) return;

    const trunk = new THREE.CylinderGeometry(0.25, 0.4, 1, 6).translate(0, 0.5, 0);
    const cone = new THREE.ConeGeometry(0.5, 1, 7).translate(0, 0.5, 0);
    const ball = new THREE.IcosahedronGeometry(0.5, 1).translate(0, 0.5, 0);
    const material = new THREE.MeshStandardMaterial({ roughness: 0.9 });
    const trunks = new THREE.InstancedMesh(trunk, material, list.length);
    const cones = list.filter((i) => crown(i.name) === "cone").length;
    const crowns = { cone: new THREE.InstancedMesh(cone, material, Math.max(1, cones)), ball: new THREE.InstancedMesh(ball, material, Math.max(1, list.length - cones)) };
    const counts = { cone: 0, ball: 0 };
    const m = new THREE.Matrix4(), q = new THREE.Quaternion(), up = new THREE.Vector3(0, 1, 0);
    list.forEach((item, i) => {
      const h = heightOf(item.name);
      const [x, y, z] = item.position;
      q.setFromAxisAngle(up, (item.yaw * Math.PI) / 180);
      m.compose(new THREE.Vector3(x, y, z), q, new THREE.Vector3(h * 0.06, h * 0.45, h * 0.06));
      trunks.setMatrixAt(i, m);
      trunks.setColorAt(i, new THREE.Color(0x6b4a2f));
      const kind = crown(item.name);
      const width = kind === "cone" ? h * 0.35 : h * 0.55;
      m.compose(new THREE.Vector3(x, y + h * 0.3, z), q, new THREE.Vector3(width, h * 0.75, width));
      crowns[kind].setMatrixAt(counts[kind], m);
      crowns[kind].setColorAt(counts[kind]++, new THREE.Color(colourOf(item.name)));
    });
    crowns.cone.count = counts.cone;
    crowns.ball.count = counts.ball;
    for (const mesh of [trunks, crowns.cone, crowns.ball]) {
      mesh.raycast = () => {};
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
      this.group.add(mesh);
    }
  }
}

const crown = (name: string): "cone" | "ball" => (/Fir|Cypress/.test(name) ? "cone" : "ball");

/** Rough heights in metres by size word (the game's trees are roughly this tall). */
function heightOf(name: string): number {
  if (/VerySmall/.test(name)) return 5;
  if (/Small/.test(name)) return 8;
  if (/Medium/.test(name)) return 12;
  if (/Big/.test(name)) return 15;
  if (/Tall/.test(name)) return 20;
  return 10;
}

function colourOf(name: string): number {
  if (/Fall/.test(name)) return 0xd0782a;
  if (/Frozen|Snow/.test(name)) return 0xdfe8ee;
  if (/Cherry/.test(name)) return 0xf0a8c8;
  if (/Palm/.test(name)) return 0x6aa83a;
  if (/Fir|Cypress/.test(name)) return 0x2f5e34;
  return 0x4f9a3c;
}
