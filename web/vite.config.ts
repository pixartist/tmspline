import { defineConfig } from "vite";

export default defineConfig({
  // The .NET runtime in public/_framework is loaded at runtime, not bundled.
  build: { target: "es2022" },
  server: {
    port: 5173,
    // The export backend (src/TmSpline.Server) runs on :5080 during development. TMSPLINE_API points the editor at
    // another one, e.g. a test server, while the desktop app (which also listens on :5080) is running.
    proxy: { "/api": process.env.TMSPLINE_API ?? "http://localhost:5080" },
  },
});
