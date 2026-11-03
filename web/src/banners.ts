/**
 * Banners over the 3D view (conflicts, hints, updates) stack below each other at the top instead of covering each
 * other: they all go into one host per container.
 */
export function bannerHost(container: HTMLElement): HTMLElement {
  let host = container.querySelector<HTMLElement>(":scope > .banner-stack");
  if (!host) {
    host = document.createElement("div");
    host.className = "banner-stack";
    container.appendChild(host);
  }
  return host;
}
