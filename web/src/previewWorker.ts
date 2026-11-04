// Runs TmSpline.Core (.NET WebAssembly) off the main thread, so mesh rebuilds never freeze the editor.
// Messages in:  { type: "init" } | { type: "build", id, json, quality } | { type: "terrain", id, json (the map) }
// Messages out: { type: "ready" } | { type: "result", id, bytes } | { type: "error", id?, message }

interface WasmExports {
  TmSpline: { Wasm: { Interop: { BuildPreview(json: string, quality: number): Uint8Array; BuildTerrain(json: string): Uint8Array } } };
}

// Typed by hand: the project's DOM lib types `self` as a Window, not a worker scope.
const ctx = self as unknown as {
  postMessage(message: unknown, transfer?: Transferable[]): void;
  onmessage: ((e: MessageEvent) => void) | null;
};

let exportsPromise: Promise<WasmExports> | null = null;

function loadCore(): Promise<WasmExports> {
  exportsPromise ??= (async () => {
    // Loaded from public/_framework at runtime; an absolute URL keeps Vite from treating it as a module to bundle.
    const url = new URL(`${import.meta.env.BASE_URL}_framework/dotnet.js`, self.location.origin).href;
    const { dotnet } = await import(/* @vite-ignore */ url);
    const runtime = await dotnet.create();
    return (await runtime.getAssemblyExports("TmSpline.Wasm.dll")) as WasmExports;
  })();
  return exportsPromise;
}

ctx.onmessage = async (e: MessageEvent) => {
  const msg = e.data as { type: string; id?: number; json?: string; quality?: number };
  try {
    const core = await loadCore();
    if (msg.type === "init") {
      ctx.postMessage({ type: "ready" });
      return;
    }
    // Copy into a buffer we own before transferring it to the main thread.
    const interop = core.TmSpline.Wasm.Interop;
    const bytes = (msg.type === "terrain" ? interop.BuildTerrain(msg.json!) : interop.BuildPreview(msg.json!, msg.quality ?? 1)).slice();
    ctx.postMessage({ type: "result", id: msg.id, bytes }, [bytes.buffer]);
  } catch (err) {
    ctx.postMessage({ type: "error", id: msg.id, message: String(err) });
  }
};
