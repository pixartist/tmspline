import { defineConfig } from "vite";

export default defineConfig({
  // The .NET runtime in public/_framework is loaded at runtime, not bundled.
  build: { target: "es2022" },
  server: {
    port: 5173,
    // The export backend (src/TmSpline.Server) runs on :5080 during development.
    proxy: { "/api": "http://localhost:5080" },
  },
});
