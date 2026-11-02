# TMSpline

Spline-based track builder for Trackmania (2020). Tracks are edited in the browser and exported as a map
whose road is made of custom items generated with Nadeo's NadeoImporter.

## Layout

| Path | What |
|---|---|
| `src/TmSpline.Core` | Track model, JSON format, spline sampling/frames/banking, road mesh generation. No dependencies. |
| `src/TmSpline.Export` | Binary FBX writer, NadeoImporter runner, map writer (GBX.NET). |
| `src/TmSpline.Wasm` | Core compiled to WebAssembly for the editor's live preview. |
| `src/TmSpline.Cli` | `track.json` → `.Map.Gbx` using a local NadeoImporter. |
| `src/TmSpline.Server` | ASP.NET Core: `POST /api/export` (track → map), serves the built editor in production. |
| `web/` | Editor: Vite + TypeScript + Three.js. |
| `Dockerfile` | Production image: server + editor + Wine. |
| `docker/importer` | Importer-only Wine image (used by the server's `Docker` importer mode). |
| `tools/` | Dev tools: FBX dump/round-trip/transplant, map inspector + template maker, magnet material comparison, frame smoothness check, item rotation calibration. |

## Run everything (local development, Windows)

```powershell
.\dev.ps1          # or dev.cmd: export server on :5080 + editor on http://localhost:5173, Ctrl+C stops both
.\dev.ps1 -Wasm    # force a rebuild of the preview core (otherwise rebuilt only when TmSpline.Core/Wasm changed)
.\dev.ps1 -NoServer
```

It installs the web dependencies on first run and writes the server's output to `artifacts/dev-server.log`. If
something already listens on :5080, it is used as the export server.

## Run the editor

```sh
cd web
npm install
npm run wasm   # publishes TmSpline.Wasm and copies the .NET runtime to public/_framework
npm run dev    # http://localhost:5173
```

Controls:
- Drag points and handles directly, parallel to the screen; `Shift` = keep height, `Alt` (pressed once dragging) = height
  only, `Ctrl` = snap to 1 m.
- The camera orbits with `Alt` + left drag (`Alt+Shift`: pan); right drag pans. A plain left drag on empty space (or on
  blocks that are not selected) draws a rectangle that selects the blocks whose centre is inside it (`Shift` adds,
  `Ctrl` removes); `Shift`+click adds or removes a single block. Dragging one of the selected blocks moves them all (by
  whole cells as long as a grid block is among them), `PgUp`/`PgDn` raise them, `Del` deletes them.
- Click a point to show its handles and the orange banking ring; drag the ring to bank (`Ctrl` = 5° steps) or use `Q`/`E`.
  Between two points the road rolls the shorter way round, so a corkscrew is a few points stepping round
  (e.g. 0° → 120° → -120° → 0°), and closed tracks with corkscrews close by themselves.
- Road width is set per point (inspector or `[`/`]`) and blends smoothly between points.
- Each point also has a cross-section **Shape** (degrees: negative = sausage, 0 = flat, 180 = half-pipe, 360 = closed
  pipe), a **Flat in** share that keeps the middle flat and bends only the sides (Shape 180° + Flat 50% = U profile), and
  **Sausage %**, which compresses the bend into a crown with straight slopes (0 % round, 100 % triangle; the Sausage
  preset is a rounded triangle like the game's sausage roads).
  Width is measured along the surface, so a pipe's diameter is width / π.
- Border height is also set per point (0.2–8 m) and blends between points. Borders are switched on and off per point
  and blend from point to point too: a single point without borders fades them out towards it and back in after it.
- Click the road to select a segment; Ctrl+click the ground to extend the track.
- Segment borders: None, Rubber (native) or Plastic (grindable without losing speed, bouncy; shiny yellow).
- Segments can carry a gameplay effect on Road and Platform surfaces: Turbo (yellow/red), Reactor boost (normal/red),
  Slow motion, No steering, No brakes, No engine, Cruise, Fragile, No grip, Force acceleration, Reset, Bumper (normal/strong),
  with the matching native road markings where the importer has them. Turbos and reactor boosts are directional: their
  pieces are exported as separate 8 m items rotated so their local Z axis points along the effect (Direction / Tilt).
  NadeoImporter 2022_07_12 only accepts the reactor boosts as `ReactorBoost_Legacy` / `ReactorBoost2_Legacy`.
- Race gates: set a point's **Waypoint** to Start, Checkpoint, Finish or (closed tracks) Start/finish, with **Laps** in the
  map settings. Nothing is placed automatically: without waypoints the map gets no gates (add them in the game's editor).
  Files from before maps had several tracks get their old automatic start and finish as explicit waypoints.
- A map can hold several tracks (**New track** or `T`), e.g. pieces of a route the car jumps between. The other tracks'
  points are grey; clicking one, or their road, makes that track the one being edited.
- **Blocks** (`B`) places vanilla game blocks on the 32 m map grid: pick one from the searchable list, click the map
  to place it (`R` rotates, `PgUp`/`PgDn` changes the 8 m level, `Esc` ends), drag the selected block by cells, `Del`
  removes it. They go into the map as the game's own blocks, by name, so they do not count towards the 5 MB item
  limit. With a local block library (below) the editor shows their real meshes, otherwise boxes of their size.
  `Tab` switches to free placement (the game's free block mode): the block stands on whatever is under the cursor, at any
  position, `R` turns it 15°, pitch and roll are in the inspector. `Tab` on a selected block converts it in place.
- **Snap** (`X`, step next to the button: 32 … 1 m) snaps track points (not their handles) and free blocks to the grid,
  vertically to the same step up to 8 m (one block level). `Ctrl` while dragging ignores it.
- `W`/`S` fly along the view, `A`/`D` sideways, `Space`/`Shift` up/down (speed follows the distance to the road);
  the scroll wheel zooms towards the cursor; `N` add point, `T` new track, `I` insert mode (click the road to insert a point there, with preview), `Del` delete point, `M` smooth selection (`Shift+M` or **Smooth** button: whole track; strength slider next to the button), `G` axis gizmo, `F` frame, `Ctrl+Z`/`Ctrl+Y` undo/redo.
- The same overview is in the collapsible **Controls** box in the editor's bottom-right corner.
The track autosaves in the browser (`?session=name` in the URL keeps a separate track; if another tab saves the same track, autosave pauses and asks which version to keep). **Save** downloads a `.track.json`.

## Vanilla block library (local development only)

The editor can show the game's own block meshes. They are Nadeo's assets: convert them locally, never commit,
upload or redistribute them (`artifacts/` is excluded from Docker builds, and only the Development settings serve it).

1. Install the TMSpline Openplanet plugin (see below) and start the dev stack.
2. In Trackmania, Openplanet menu → **TMSpline → Build block library**. The plugin extracts the block definitions and
   prefabs to `%USERPROFILE%\OpenplanetNext\Extract`, the server converts them with `tools/VanillaLib` into
   `artifacts/vanilla` (`Vanilla:Dir` and `Vanilla:Builder` in `appsettings.Development.json`), and the plugin extracts
   whatever the conversion still misses (materials, textures, their images), round by round until nothing more can be
   found. About half a minute per round; files already extracted are kept (**Rebuild** extracts everything again,
   e.g. after a game update).
3. Reload the editor. The development server serves the library at `/api/vanilla`.

Meshes use the game's detail level 1 (about 7× fewer triangles than full detail); run
`dotnet run -c Release --project tools/VanillaLib -- --lod 2` (coarser) or `--lod 0` (full detail) by hand for others.
Each material's diffuse texture is saved as WebP (at most 512 px) under `texture/`, with how the game maps it
(mesh UVs, or projected in world space for terrain-like materials such as grass, hills and track walls).

## TMSpline Openplanet plugin

`tools/OpenplanetPlugin` (one plugin, "TMSpline", talking to the local TMSpline server, `http://localhost:5080` by
default): copy the folder to `%USERPROFILE%\OpenplanetNext\Plugins\TMSpline`. It runs in Openplanet's developer
signature mode only: the regular mode loads only plugins signed by Openplanet, which happens when they are published
on openplanet.dev and pass its review.

- **Live update**: the export dialog's **Export to game** writes the map to `Documents\Trackmania\Maps\TMSpline`. In the
  game's editor the plugin offers to reload it: it saves the open map, the server merges the new export into it (what
  the previous export placed is replaced, what was added in the game's editor stays) and the editor reopens with the
  camera where it was.
- **Block library**: see above.

## Run the export server (development)

```sh
dotnet run --project src/TmSpline.Server   # http://localhost:5080, the Vite dev server proxies /api to it
```

`appsettings.Development.json` uses the `Native` importer mode with the NadeoImporter next to Trackmania.exe.
Modes: `Native` (Windows), `Wine` (Linux, `wine` in PATH), `Docker` (runs the `docker/importer` image per job).
Each export gets its own job folder and its own Nadeo.ini, so exports can run in parallel.

## Production image

NadeoImporter is not part of the image; mount the unzipped `NadeoImporter_2022_07_12` folder:

```sh
docker build -t tmspline .
docker run -p 8080:8080 -v /path/to/NadeoImporter_2022_07_12:/opt/nadeo-importer:ro tmspline
```

Limits (points, length, concurrent/queued jobs, requests per minute) are in the `Export` section of `appsettings.json`
and can be overridden with environment variables such as `Export__MaxConcurrentJobs=4`.

## Export a map from the command line

Requires NadeoImporter (from doc.trackmania.com) unzipped next to `Trackmania.exe`.

```sh
dotnet run --project src/TmSpline.Cli -- path/to/My_Track.track.json [--game <TM dir>] [--user <TM user dir>]
```

The map is written to `<TM user dir>\Maps\TMSpline\`. Track coordinates are meters relative to the map center
at ground level (map Y = 8).

## Licensing note

`GBX.NET.LZO` is GPL v3. Running it on a server is fine; distributing binaries that include it requires GPL v3.
