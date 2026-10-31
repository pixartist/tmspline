// Copies the published .NET WebAssembly runtime (TmSpline.Wasm) into public/_framework.
import { cpSync, rmSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const src = join(root, "..", "artifacts", "wasm", "wwwroot", "_framework");
const dst = join(root, "public", "_framework");
if (!existsSync(src)) throw new Error(`Missing ${src}; run dotnet publish first`);
rmSync(dst, { recursive: true, force: true });
cpSync(src, dst, { recursive: true });
console.log(`Copied ${src} -> ${dst}`);
