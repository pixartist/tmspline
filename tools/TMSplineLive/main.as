// TMSpline Live: reloads the map editor when TMSpline exports a new version of the map ("Export to game").
// The TMSpline server (local dev setup) writes the map into Maps/TMSpline and tells us about it at /api/game/latest;
// we leave the editor, open the new file in the editor and put the camera back where it was.
// What was added in the game's editor stays: before reloading we save the open map, and the server merges the new export
// into it (it replaces only what its previous export put there).

[Setting name="TMSpline server" description="Where the TMSpline export server runs."]
string S_Server = "http://localhost:5080";

[Setting name="Reload automatically" description="Reload the editor as soon as a new export arrives, without asking (the open map is saved first)."]
bool S_Auto = false;

[Setting name="Keep camera" description="Put the editor camera back where it was after reloading."]
bool S_KeepCamera = true;

// The export the open editor shows (or that we already offered and the user dismissed).
string g_seenId = "";
bool g_firstPoll = true;
bool g_busy = false;
bool g_serverDown = false;

// The latest export the server knows about.
string g_latestId = "";
string g_latestPath = "";      // relative to the Maps folder, e.g. "TMSpline/My Track.Map.Gbx"
string g_latestTime = "";
bool g_latestApplied = true;   // false: the server waits for us to save the open map, then merges the export into it
string g_error = "";

void Main()
{
    while (true) {
        Poll();
        sleep(1000);
    }
}

void Poll()
{
    auto req = Net::HttpGet(S_Server + "/api/game/latest");
    while (!req.Finished()) yield();
    int code = req.ResponseCode();
    g_serverDown = code != 200 && code != 204;
    if (code == 204) g_firstPoll = false;   // no export yet: the first one to come is new
    if (code != 200) return;

    auto json = Json::Parse(req.String());
    if (json.GetType() != Json::Type::Object || !json.HasKey("id") || !json.HasKey("mapPath")) return;
    g_latestId = string(json["id"]);
    g_latestPath = string(json["mapPath"]);
    g_latestTime = json.HasKey("exportedAt") ? string(json["exportedAt"]) : "";
    g_latestApplied = !json.HasKey("applied") || bool(json["applied"]);
    // Whatever was exported (and written) before the plugin started is assumed to be what is open.
    if (g_firstPoll && g_latestApplied) g_seenId = g_latestId;
    g_firstPoll = false;

    if (S_Auto && Pending() && InEditor() && !g_busy) startnew(Reload);
}

bool Pending() { return g_latestId != "" && g_latestId != g_seenId; }

CGameCtnEditorFree@ Editor() { return cast<CGameCtnEditorFree>(GetApp().Editor); }
bool InEditor() { return Editor() !is null; }

/// The file name part of a path (either slash).
string FileName(const string &in path)
{
    string p = path.Replace("\\", "/");
    int slash = p.LastIndexOf("/");
    return slash < 0 ? p : p.SubStr(slash + 1);
}

/// True when the editor has the latest export's map open (by file name), so its camera is worth keeping.
bool EditingLatest()
{
    auto app = cast<CTrackMania>(GetApp());
    if (app.RootMap is null || app.RootMap.MapInfo is null) return false;
    return FileName(app.RootMap.MapInfo.FileName).ToLower() == FileName(g_latestPath).ToLower();
}

void Reload()
{
    if (g_busy || g_latestPath == "") return;
    g_busy = true;
    g_seenId = g_latestId;
    auto app = cast<CTrackMania>(GetApp());

    // Camera of the open editor (orbital camera: target, distance, angles).
    bool haveCamera = false;
    vec3 target;
    float distance = 0, hAngle = 0, vAngle = 0;
    auto editor = Editor();
    if (S_KeepCamera && editor !is null && editor.OrbitalCameraControl !is null) {
        string open = app.RootMap !is null && app.RootMap.MapInfo !is null ? app.RootMap.MapInfo.FileName : "(none)";
        if (EditingLatest()) {
            auto cam = editor.OrbitalCameraControl;
            target = cam.m_TargetedPosition;
            distance = cam.m_CameraToTargetDistance;
            hAngle = cam.m_CurrentHAngle;
            vAngle = cam.m_CurrentVAngle;
            haveCamera = true;
            trace("Camera saved: target " + target.ToString() + ", distance " + distance + ", angles " + hAngle + " / " + vAngle);
        } else trace("Camera not kept: open map '" + open + "' is not the export '" + g_latestPath + "'");
    }

    if (!g_latestApplied) {
        // Save the open map (when it is this one), so the edits made in the game are in the file the export merges into.
        if (EditingLatest() && !SaveOpenMap()) { g_busy = false; return; }
        if (!ApplyExport()) { g_busy = false; return; }
    }
    g_error = "";

    if (app.Editor !is null) {
        app.BackToMainMenu();
        while (app.Editor !is null) yield();
    }
    while (app.ManiaTitleControlScriptAPI is null || !app.ManiaTitleControlScriptAPI.IsReady) yield();
    app.ManiaTitleControlScriptAPI.EditMap(g_latestPath, "", "");

    // Wait until the editor is up and takes requests; it places its own start camera while loading.
    while (Editor() is null || Editor().PluginMapType is null || !Editor().PluginMapType.IsEditorReadyForRequest) yield();
    if (haveCamera) {
        // Set it for a while: the editor may still move the camera during its first frames.
        for (int i = 0; i < 30; i++) {
            if (!SetCamera(target, distance, hAngle, vAngle)) break;
            yield();
        }
        trace("Camera restored");
    }
    g_busy = false;
}

/// Saves the open map under its own file name and waits until the file on disk changed.
bool SaveOpenMap()
{
    auto editor = Editor();
    if (editor is null || editor.PluginMapType is null || editor.Challenge is null) return true;
    string fileName = editor.Challenge.MapInfo.FileName;
    string full = IO::FromUserGameFolder("Maps/" + g_latestPath);
    int64 before = IO::FileExists(full) ? IO::FileModifiedTime(full) : 0;
    trace("Saving the open map '" + fileName + "' before merging export " + g_latestId);
    editor.PluginMapType.SaveMap(fileName);
    uint64 start = Time::Now;
    while (Time::Now - start < 10000) {
        if (IO::FileExists(full) && IO::FileModifiedTime(full) != before) break;
        yield();
    }
    if (Time::Now - start >= 10000) warn("Saving took over 10 s or did not change " + full + "; merging anyway");
    sleep(500);   // let the game finish writing
    return true;
}

/// Asks the server to merge the waiting export into the (saved) map file.
bool ApplyExport()
{
    auto req = Net::HttpPost(S_Server + "/api/game/apply?id=" + g_latestId, "", "application/json");
    while (!req.Finished()) yield();
    int code = req.ResponseCode();
    if (code == 200) {
        auto json = Json::Parse(req.String());
        if (json.GetType() == Json::Type::Object && json.HasKey("merge") && json["merge"].GetType() == Json::Type::Object) {
            auto m = json["merge"];
            trace("Merged: kept " + int(m["keptBlocks"]) + " blocks and " + int(m["keptItems"]) + " items from the game, replaced "
                + int(m["removedBlocks"]) + " blocks and " + int(m["removedItems"]) + " items of the previous export");
        }
        g_latestApplied = true;
        return true;
    }
    g_error = code == 404 ? "That export is gone (a newer one replaced it)." : "Merging failed (" + code + "): " + req.String();
    warn(g_error);
    return false;
}

/// Puts the editor camera at the given orbit, through the editor's script API (which the editor respects)
/// and the orbital camera itself (so it updates right away). False when the editor is gone.
bool SetCamera(const vec3 &in target, float distance, float hAngle, float vAngle)
{
    auto editor = Editor();
    if (editor is null || editor.PluginMapType is null || editor.OrbitalCameraControl is null) return false;
    auto pmt = editor.PluginMapType;
    pmt.CameraTargetPosition = target;
    pmt.CameraToTargetDistance = distance;
    pmt.CameraHAngle = hAngle;
    pmt.CameraVAngle = vAngle;
    auto cam = editor.OrbitalCameraControl;
    cam.m_TargetedPosition = target;
    cam.m_CameraToTargetDistance = distance;
    cam.m_CurrentHAngle = hAngle;
    cam.m_CurrentVAngle = vAngle;
    return true;
}

/// Small window while a new export waits (in the editor and in the menus).
void Render()
{
    if (!Pending() || S_Auto || g_busy) return;
    auto app = cast<CTrackMania>(GetApp());
    bool editing = InEditor();
    // Outside the editor only while in the menus (not while driving a map).
    if (!editing && app.CurrentPlayground !is null) return;

    UI::SetNextWindowPos(20, 120, UI::Cond::FirstUseEver);
    if (UI::Begin("TMSpline Live", UI::WindowFlags::AlwaysAutoResize | UI::WindowFlags::NoCollapse)) {
        UI::Text("New export: \\$fd8" + g_latestPath);
        if (g_error != "") UI::Text("\\$f66" + g_error);
        if (editing) {
            UI::TextDisabled(g_latestApplied ? "Reloading discards changes made in the game's editor."
                : "Saves this map, then reloads it with the new export (what you added here stays).");
            if (UI::Button("Reload editor")) startnew(Reload);
        } else if (UI::Button("Open in editor")) startnew(Reload);
        UI::SameLine();
        if (UI::Button("Dismiss")) g_seenId = g_latestId;
    }
    UI::End();
}

void RenderMenu()
{
    string state = g_serverDown ? " \\$f66(server not reachable)" : g_latestPath == "" ? " \\$888(no export yet)" : "";
    if (UI::MenuItem("\\$f80TMSpline\\$z Open latest export in editor" + state, "", false, g_latestPath != "" && !g_busy))
        startnew(Reload);
}
