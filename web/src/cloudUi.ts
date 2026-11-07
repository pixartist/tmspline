import type { Editor } from "./editor";
import type { TrackMap } from "./track";
import { bannerHost } from "./banners";
import {
  availableUpdate, cloudStatus, CloudSync, deleteTrack, listTracks, loadShared, loadTrack, openWebsite, shareTrack, signIn, signOut,
  skipUpdate, unshareTrack, type CloudStatus, type CloudTrack,
} from "./cloud";

/** What ui.ts calls into: the cloud parts, or null without a cloud link (the editor in a plain browser). */
export interface CloudUi {
  sync: CloudSync;
  /** Open… with the cloud: the user's tracks, a file, or a share link. */
  open(openFile: (opened: () => void) => void): void;
  /** The map was replaced by a new one (New, a file): it becomes a new cloud track. */
  replaced(): void;
  /** Save: to the cloud now (signs in first when needed); shows the outcome on the button. */
  save(button: HTMLButtonElement): Promise<void>;
}

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);

export async function createCloudUi(editor: Editor, header: HTMLElement, storageKey: string): Promise<CloudUi | null> {
  let status = await cloudStatus();
  if (!status?.available) return null;

  const area = document.createElement("div");
  area.className = "cloud";
  const state = Object.assign(document.createElement("span"), { className: "cloud-state" });
  const shareButton = Object.assign(document.createElement("button"), { textContent: "Share", title: "Share this map with a link: everyone with a TMSpline account can open it" });
  const account = Object.assign(document.createElement("button"), { className: "account" });
  area.append(state, shareButton, account);
  header.appendChild(area);

  const container = editor.renderer.domElement.parentElement!;
  const mapJson = () => JSON.stringify(editor.map);
  const loadMap = (map: TrackMap) => {
    editor.replaceMap(map);
    editor.select(null);
    editor.frameTrack();
  };

  const sync = new CloudSync(`${storageKey}.cloud`, () => render(), (theirs) => showConflict(theirs));

  const render = () => {
    account.textContent = status?.signedIn ? `${status.user ?? "Signed in"} ▾` : status?.loggingIn ? "Signing in… (see browser)" : "Sign in";
    account.title = status?.signedIn ? "Your TMSpline account" : "Sign in to keep your tracks in the cloud and share them";
    shareButton.hidden = !status?.signedIn;
    state.textContent = sync.label;
    state.title = sync.message;
    state.dataset.state = sync.state;
  };

  let updateChecked = false;
  const applyStatus = (s: CloudStatus | null) => {
    status = s;
    sync.setSignedIn(!!s?.signedIn, mapJson(), s?.user ?? null);
    render();
    if (s?.error && !s.signedIn) banner(s.error, [["OK", () => {}]]);
    // Releases are for signed-in users: checked once signed in (at startup or later).
    if (s?.signedIn && !updateChecked) {
      updateChecked = true;
      void availableUpdate(s).then(async (release) => {
        if (!release) return;
        const text = `TMSpline ${release.version} is available${release.notes ? `: ${release.notes}` : "."}`;
        // The app updates itself where it can (a release build on Windows); otherwise the website has the download.
        const self = await fetch("/api/update", { headers: { "X-TMSpline": "1" } }).then((r) => (r.ok ? r.json() : null), () => null);
        banner(text, [
          self?.supported ? ["Install update", () => void installUpdate(release.version)] : ["Download", () => void openWebsite("/")],
          ["Later", () => {}],
          ["Skip this version", () => skipUpdate(release.version)],
        ]);
      }, () => {});
    }
  };

  account.addEventListener("click", async () => {
    if (status?.signedIn) {
      menu(account, [
        ["Open the website", () => void openWebsite("/")],
        ["Sign out", async () => applyStatus(await signOut())],
      ]);
      return;
    }
    if (status?.loggingIn) return;
    try {
      applyStatus(await signIn(() => {
        if (status) status = { ...status, loggingIn: true };
        render();
      }));
    } catch (err) {
      banner((err as Error).message, [["OK", () => {}]]);
      render();
    }
  });

  shareButton.addEventListener("click", () => void showShare());

  // ---- dialogs ------------------------------------------------------------------------------------------------------

  function banner(text: string, actions: [string, () => void][]): HTMLElement {
    const b = document.createElement("div");
    b.className = "conflict-banner";
    b.append(Object.assign(document.createElement("span"), { textContent: text }));
    actions.forEach(([label, action], i) => {
      const button = Object.assign(document.createElement("button"), { textContent: label });
      if (i === 0) button.classList.add("primary");
      button.addEventListener("click", () => {
        b.remove();
        action();
      });
      b.append(button);
    });
    bannerHost(container).appendChild(b);
    return b;
  }

  function showConflict(theirs: CloudTrack): void {
    banner(`"${theirs.name}" was saved on another PC (${new Date(theirs.updated).toLocaleString()}). Which one do you keep?`, [
      ["Load that version", async () => {
        const { track, map, json } = await loadTrack(theirs.id);
        loadMap(map);
        sync.attach(track, json);
      }],
      ["Keep this one", () => sync.overwrite(mapJson())],
      ["Keep both", () => {
        sync.detach();
        sync.overwrite(mapJson());
      }],
    ]);
  }

  async function showShare(): Promise<void> {
    const dialog = modal(`<h2>Share "${esc(editor.map.name || "Untitled")}"</h2><p class="hint">Creating the link…</p>`);
    try {
      // Upload first, so the link shows the map as it is now.
      sync.flush(mapJson());
      await waitUntil(() => sync.state !== "saving");
      if (sync.state !== "saved") throw new Error(sync.message || "The map could not be saved to the cloud.");
      const { url } = await shareTrack(sync.trackId);
      dialog.body.innerHTML = `<h2>Share "${esc(editor.map.name || "Untitled")}"</h2>
        <p class="hint">Everyone with a TMSpline account can open this link and take a copy. It always shows the map as you
          last saved it, until you stop sharing.</p>
        <div class="share-link"><input readonly value="${esc(url)}" /><button class="primary copy">Copy</button></div>
        <div class="actions"><button class="stop">Stop sharing</button><button class="close">Close</button></div>`;
      const input = dialog.body.querySelector("input")!;
      input.select();
      dialog.body.querySelector(".copy")!.addEventListener("click", async (e) => {
        await navigator.clipboard.writeText(url).catch(() => document.execCommand("copy"));
        (e.target as HTMLButtonElement).textContent = "Copied";
      });
      dialog.body.querySelector(".stop")!.addEventListener("click", async () => {
        await unshareTrack(sync.trackId);
        dialog.close();
      });
      dialog.body.querySelector(".close")!.addEventListener("click", () => dialog.close());
    } catch (err) {
      dialog.body.innerHTML = `<h2>Share</h2><p class="error">${esc((err as Error).message)}</p>
        <div class="actions"><button class="close">Close</button></div>`;
      dialog.body.querySelector(".close")!.addEventListener("click", () => dialog.close());
    }
  }

  function open(openFile: (opened: () => void) => void): void {
    const dialog = modal(`<h2>Open</h2>
      <div class="actions left"><button class="file">From a file…</button><button class="link">From a share link…</button></div>
      <div class="link-row" hidden><input placeholder="https://…/s/… or the link's code" /><button class="primary go">Open</button></div>
      <p class="error" hidden></p>
      <h3>Your tracks</h3><div class="track-list"><p class="hint">${status?.signedIn ? "Loading…" : "Sign in to keep your tracks in the cloud."}</p></div>
      <div class="actions"><button class="close">Close</button></div>`);
    const body = dialog.body;
    const error = body.querySelector<HTMLElement>(".error")!;
    const fail = (err: unknown) => {
      error.textContent = (err as Error).message;
      error.hidden = false;
    };
    body.querySelector(".close")!.addEventListener("click", () => dialog.close());
    body.querySelector(".file")!.addEventListener("click", () => {
      dialog.close();
      openFile(() => sync.detach());
    });
    const linkRow = body.querySelector<HTMLElement>(".link-row")!;
    body.querySelector(".link")!.addEventListener("click", () => {
      linkRow.hidden = false;
      linkRow.querySelector("input")!.focus();
    });
    const openLink = async () => {
      try {
        const { shared, map } = await loadShared(linkRow.querySelector("input")!.value);
        // A copy of one's own: saving it makes a new track of the user's, the shared one stays as it is.
        loadMap(map);
        sync.detach();
        sync.changed(mapJson());
        dialog.close();
        banner(`Opened a copy of "${shared.name}" by ${shared.owner}.`, [["OK", () => {}]]);
      } catch (err) {
        fail(err);
      }
    };
    linkRow.querySelector(".go")!.addEventListener("click", () => void openLink());
    linkRow.querySelector("input")!.addEventListener("keydown", (e) => { if (e.key === "Enter") void openLink(); });

    if (!status?.signedIn) return;
    const list = body.querySelector<HTMLElement>(".track-list")!;
    const show = (tracks: CloudTrack[]) => {
      if (tracks.length === 0) {
        list.innerHTML = `<p class="hint">No tracks yet: they appear here once you are signed in and edit one.</p>`;
        return;
      }
      list.innerHTML = tracks.map((t) => `
        <div class="track-row${t.id === sync.trackId ? " current" : ""}" data-id="${esc(t.id)}">
          <button class="name" title="Open">${esc(t.name)}</button>
          <span class="meta">${new Date(t.updated).toLocaleString()}${t.share ? " · shared" : ""}${t.id === sync.trackId ? " · open now" : ""}</span>
          <button class="delete" title="Delete this track from the cloud">Delete</button>
        </div>`).join("");
      list.querySelectorAll<HTMLElement>(".track-row").forEach((row) => {
        const id = row.dataset.id!;
        row.querySelector(".name")!.addEventListener("click", async () => {
          try {
            const { track, map, json } = await loadTrack(id);
            loadMap(map);
            sync.attach(track, json);
            dialog.close();
          } catch (err) {
            fail(err);
          }
        });
        row.querySelector(".delete")!.addEventListener("click", async (e) => {
          const button = e.target as HTMLButtonElement;
          if (button.dataset.confirm !== "1") {
            button.dataset.confirm = "1";
            button.textContent = "Delete for good?";
            return;
          }
          try {
            await deleteTrack(id);
            if (id === sync.trackId) sync.detach();
            row.remove();
          } catch (err) {
            fail(err);
          }
        });
      });
    };
    listTracks().then(show, fail);
  }

  // ---- startup ------------------------------------------------------------------------------------------------------

  applyStatus(status);

  /** Downloads the update in the app (progress in a banner), then offers to restart into it. */
  async function installUpdate(version: string): Promise<void> {
    const headers = { "X-TMSpline": "1" };
    const b = banner(`Downloading TMSpline ${version}…`, []);
    const text = b.querySelector("span")!;
    let s = await fetch("/api/update/install", { method: "POST", headers }).then((r) => r.json());
    while (s.state === "downloading") {
      text.textContent = `Downloading TMSpline ${version}… ${s.size ? Math.round((100 * s.received) / s.size) : 0} %`;
      await new Promise((r) => setTimeout(r, 400));
      s = await fetch("/api/update", { headers }).then((r) => r.json());
    }
    b.remove();
    if (s.state !== "ready") {
      banner(`The update could not be downloaded: ${s.error ?? "unknown error"}`, [["OK", () => {}], ["Open the website", () => void openWebsite("/")]]);
      return;
    }
    banner(`TMSpline ${version} is ready. Restart now? (Your map is saved.)`, [
      ["Restart now", async () => {
        // The map is saved on every change; this makes sure the last one is in before the app closes.
        await sync.flushAndWait(mapJson());
        const r = await fetch("/api/update/apply", { method: "POST", headers });
        if (!r.ok) {
          const p = await r.json().catch(() => null);
          banner(p?.detail ?? "The update could not be applied.", [["OK", () => {}]]);
        }
      }],
      ["Later", () => {}],
    ]);
  }

  async function save(button: HTMLButtonElement): Promise<void> {
    const label = "Save";
    const flash = (text: string) => {
      button.textContent = text;
      setTimeout(() => (button.textContent = label), 1800);
    };
    if (!status?.signedIn) {
      account.click();
      return;
    }
    if (sync.state === "conflict") {
      flash("Pick a version first");
      return;
    }
    button.disabled = true;
    button.textContent = "Saving…";
    sync.flush(mapJson());
    await waitUntil(() => sync.state !== "saving");
    button.disabled = false;
    if (sync.state === "saved") flash("Saved ✓");
    else {
      button.textContent = label;
      if (sync.state === "offline") banner("Not connected: the map is saved on this PC and goes up as soon as the server is reachable.", [["OK", () => {}]]);
      else if (sync.state === "error") banner(sync.message, [["OK", () => {}]]);
    }
  }

  return {
    sync,
    open,
    save,
    replaced: () => {
      sync.detach();
      sync.changed(mapJson());
    },
  };
}

function waitUntil(done: () => boolean): Promise<void> {
  return new Promise((resolve) => {
    const tick = () => (done() ? resolve() : setTimeout(tick, 100));
    tick();
  });
}

function modal(html: string): { body: HTMLElement; close: () => void } {
  const dialog = document.createElement("dialog");
  dialog.className = "export cloud-dialog";
  dialog.innerHTML = `<div class="body">${html}</div>`;
  document.body.appendChild(dialog);
  dialog.addEventListener("close", () => dialog.remove());
  dialog.showModal();
  return { body: dialog.querySelector<HTMLElement>(".body")!, close: () => dialog.close() };
}

function menu(anchor: HTMLElement, items: [string, () => void][]): void {
  const m = document.createElement("div");
  m.className = "cloud-menu";
  for (const [label, action] of items) {
    const b = Object.assign(document.createElement("button"), { textContent: label });
    b.addEventListener("click", () => {
      m.remove();
      action();
    });
    m.append(b);
  }
  const r = anchor.getBoundingClientRect();
  Object.assign(m.style, { top: `${r.bottom + 4}px`, right: `${window.innerWidth - r.right}px` });
  document.body.appendChild(m);
  setTimeout(() => document.addEventListener("pointerdown", (e) => { if (!m.contains(e.target as Node)) m.remove(); }, { once: true }));
}
