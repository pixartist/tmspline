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
| `src/TmSpline.Desktop` | The desktop app (`TMSpline.exe`): the editor in a window (Photino), the server inside it on localhost. |
| `src/TmSpline.Cloud` | The hosted service: Authentik login, website with the app download, API for cloud tracks, share links and updates. |
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

## Desktop app

Everything that touches Nadeo's software or the game's files runs on the user's own machine, in the desktop app:
the editor in a window (Photino.NET: WebView2 on Windows, WebKit on macOS/Linux) and the TMSpline server inside the
same process on `http://localhost:5080`, where the Openplanet plugin finds it.

```sh
dotnet run --project src/TmSpline.Desktop
```

Building the app also builds what it shows: the editor bundle (`npm run build`, web/dist → wwwroot) whenever the
editor's sources changed, and the preview core (`npm run wasm`) whenever TmSpline.Core or TmSpline.Wasm changed
(`-p:BuildEditor=false` skips both).

- Its files are in `%LOCALAPPDATA%\TMSpline` (`~/.local/share/TMSpline` elsewhere): the editor's storage (`webview`),
  export jobs, the block library (`vanilla`), live update state (`game`), the log (`logs/tmspline.log`) and an optional
  `settings.json` (`{ "port": 5080, "devTools": false }`; another port needs the same in the plugin's server URL).
- **NadeoImporter** is not shipped: the export dialog offers to download it from Nadeo
  (`nadeo-download.cdn.ubi.com`, 6.5 MB, checked against its known SHA-256) into `NadeoImporter`.
- The block library is converted by the app itself (`TMSpline.exe --vanilla-lib <extract> <out>`), no SDK needed.
- Trackmania and NadeoImporter are Windows programs: on Linux the importer runs through Wine; on macOS the app edits
  tracks but cannot export.
- **Cloud** (`settings.json` `"cloudUrl"`, default `https://tmspline.ddns.pixartist.de`): **Sign in** (top right) opens
  Authentik's login in the browser, which comes back to `http://127.0.0.1:5080/callback`; the refresh token is kept in
  `cloud-token.dat`, encrypted with the Windows account. Signed in, the open map is uploaded a moment after every
  change (☁ next to the account shows the state); if another PC saved the same track meanwhile, the editor asks which
  one to keep. Offline, it stays saved on the PC and goes up once the server is reachable again. **Open…** lists the
  cloud tracks, opens files and share links (as a copy of one's own); **Share** makes a link every signed-in user can
  open. A newer release than the app shows a notice. The local server forwards `/api/cloud/api/…` to the service with
  the token (the editor never sees it) and only accepts requests with `X-TMSpline: 1` for localhost hosts.

## Hosted service (TmSpline.Cloud)

Nothing of Nadeo's runs or is stored here. Sign-in is Authentik's (OpenID Connect): the website keeps a cookie, the
desktop app sends Authentik's access token. SQLite (`Cloud:DataDir/tmspline.db`) holds users, tracks and share links.

```sh
dotnet run --project src/TmSpline.Cloud   # http://localhost:5090, development: everyone is signed in as "dev"
```

- Website: `/` (app and plugin download, getting started), `/s/{share}` (a shared track). Both need a login.
- API (`/api`, login cookie or bearer token; changes with the cookie need the header `X-TMSpline: 1`):
  `GET /me`, `GET /tracks`, `GET|PUT|DELETE /tracks/{id}` (PUT with `baseVersion`: 409 with the stored version when
  another machine saved meanwhile), `POST|DELETE /tracks/{id}/share`, `GET /shared/{share}`, `GET /app/latest`.
- Releases: `Cloud:ReleasesDir` (default `data/releases`) holds the files and `latest.json`
  (`{ "version", "app", "plugin", "notes" }`); only the files it names are downloadable (`/download/{file}`).
- Production settings: `Auth:Authority` (the provider's OpenID issuer, `https://<authentik>/application/o/<slug>/`),
  `Auth:ClientId`, optionally `Auth:ClientSecret`, `Proxy:KnownProxies` (Caddy's address, for X-Forwarded-Proto).
- In development, the header `X-Dev-User: name` signs a request in as another user (to try sharing).

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
- Road width is set per point (inspector or `[`/`]`) and blends smoothly between points. It is the driving surface
  between the curbs, which stand outside it (1.695 m each); new tracks get 25.61 m, as the game's road blocks. Files from
  before version 3 counted 0.32 m under each curb as width: their points with borders load 0.64 m narrower, unchanged.
- Each point also has a cross-section **Shape** (degrees: negative = sausage, 0 = flat, 180 = half-pipe, 360 = closed
  pipe), a **Flat in** share that keeps the middle flat and bends only the sides (Shape 180° + Flat 50% = U profile), and
  **Sausage %**, which compresses the bend into a crown with straight slopes (0 % round, 100 % triangle; the Sausage
  preset is a rounded triangle like the game's sausage roads).
  Width is measured along the surface, so a pipe's diameter is width / π.
- **Keep edges %** (per point, blended between points like the shape) anchors bent cross-sections like the game's own
  pieces: at 100 % the road's edges stay at the point's height and exactly Width apart, so bent road lines up with
  blocks on the grid, the middle bows down (half-pipe) or up (sausage), and the curbs stand upright along the track's
  roll instead of following the bent surface. At 0 % (default) the middle stays on the spline and Width is measured
  along the surface, which is what pipes want; in between it blends. Points that keep their edges at all are limited
  to Shape −180…180° (no pipes; the Pipe preset turns Keep edges off).
- Border height is also set per point (0.2–8 m) and blends between points. Borders are switched on and off per point
  and blend from point to point too: a single point without borders fades them out towards it and back in after it.
- Click the road to select a segment; Ctrl+click the ground to extend the track.
- Segment borders: None, Rubber (native) or Plastic (grindable without losing speed, bouncy; shiny yellow).
- Segments can carry a gameplay effect on Road and Platform surfaces: Turbo (yellow/red), Reactor boost (normal/red),
  Slow motion, No steering, No brakes, No engine, Cruise, Fragile, No grip, Force acceleration, Reset, Bumper (normal/strong),
  with the matching native road markings where the importer has them. Turbos and reactor boosts are directional: their
  pieces are exported as separate 8 m items rotated so their local Z axis points along the effect (Direction / Tilt).
  NadeoImporter 2022_07_12 only accepts the reactor boosts as `ReactorBoost_Legacy` / `ReactorBoost2_Legacy`.
- Race gates: set a point's **Waypoint** to Start, Checkpoint, Finish or (closed tracks) Start/finish (**Gate height**: how high its
  arch and trigger reach over the road, 6 m by default), with **Laps** in the
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
  the scroll wheel zooms towards the cursor; `N` add point, `T` new track, `I` insert mode (click the road to insert a point there, with preview), `Del` delete point, `M` smooth selection (`Shift+M` or **Smooth** button: whole track; strength slider next to the button), `G` axis gizmo (points, handles and the selected blocks; grid blocks by cells and levels), `F` frame, `Ctrl+Z`/`Ctrl+Y` undo/redo.
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
