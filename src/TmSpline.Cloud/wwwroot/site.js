// The website's data: who is signed in, the current release, a shared track.
(() => {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  const mb = (bytes) => `${(bytes / 1_000_000).toFixed(1)} MB`;
  const get = async (url) => {
    const r = await fetch(url, { credentials: "same-origin" });
    if (r.status === 401) location.href = `/login?returnUrl=${encodeURIComponent(location.pathname)}`;
    if (r.status === 204 || r.status === 404) return null;
    if (!r.ok) throw new Error(`${r.status}`);
    return r.json();
  };

  void get("/api/me").then((me) => me && ($("user").textContent = me.name), () => {});

  const app = $("app-download"), plugin = $("plugin-download");
  if (app) {
    get("/api/app/latest").then((r) => {
      const link = (f, label) => f
        ? `<a class="button" href="${esc(f.url)}" download>${label}</a><span class="muted">${esc(f.name)} · ${mb(f.size)}</span>`
          + `<span class="muted hash" title="SHA-256 of the file, to check the download">SHA-256 ${esc(f.sha256)}</span>`
        : `<span class="muted">Not available yet.</span>`;
      app.innerHTML = r ? link(r.app, `Download TMSpline ${esc(r.version)}`) + (r.notes ? `<p class="small">${esc(r.notes)}</p>` : "")
        : `<span class="muted">No release yet: coming soon.</span>`;
      plugin.innerHTML = r ? link(r.plugin, "Download the plugin") : `<span class="muted">Coming with the first release.</span>`;
    }, () => (app.innerHTML = `<span class="muted">Could not load the release.</span>`));
  }

  const shared = $("shared");
  if (shared) {
    const id = location.pathname.split("/").pop();
    get(`/api/shared/${encodeURIComponent(id)}`).then((t) => {
      if (!t) {
        shared.innerHTML = `<h2>Shared track</h2><p>This link does not exist (any more): its owner may have stopped sharing it.</p>`;
        return;
      }
      document.title = `${t.name} · TMSpline`;
      const tracks = Array.isArray(t.data.tracks) ? t.data.tracks.length : 1;
      const points = (Array.isArray(t.data.tracks) ? t.data.tracks : [t.data]).reduce((n, tr) => n + (tr.points?.length ?? 0), 0);
      shared.innerHTML = `<h2>${esc(t.name)}</h2>
        <p class="muted">by ${esc(t.owner)} · updated ${new Date(t.updated).toLocaleString()} · ${tracks} track${tracks === 1 ? "" : "s"}, ${points} points</p>
        <p>Open it in the TMSpline app (<b>Open… → Shared link</b>, paste this page's address) to take a copy, or download the track file.</p>
        <div class="download"><a class="button" id="file">Download .track.json</a></div>`;
      $("file").addEventListener("click", () => {
        const blob = new Blob([JSON.stringify(t.data, null, 1)], { type: "application/json" });
        const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: `${t.name.replace(/[^\w.-]+/g, "_")}.track.json` });
        a.click();
        URL.revokeObjectURL(a.href);
      });
    }, () => (shared.innerHTML = `<h2>Shared track</h2><p>Could not load it.</p>`));
  }
})();
