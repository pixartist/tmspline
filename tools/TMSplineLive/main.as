// TMSpline Live: reloads the map editor when TMSpline exports a new version of the map ("Export to game").
// The TMSpline server (local dev setup) writes the map into Maps/TMSpline and tells us about it at /api/game/latest;
// we leave the editor, open the new file in the editor and put the camera back where it was.
// Changes made in the game's editor since the last export are lost on reload (TMSpline's map replaces them).

[Setting name="TMSpline server" description="Where the TMSpline export server runs."]
string S_Server = "http://localhost:5080";

[Setting name="Reload automatically" description="Reload the editor as soon as a new export arrives, without asking. Changes made in the game's editor are lost."]
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
    // Whatever was exported before the plugin started is assumed to be what is open.
    if (g_firstPoll) g_seenId = g_latestId;
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
    if (S_KeepCamera && editor !is null && editor.OrbitalCameraControl !is null && EditingLatest()) {
        auto cam = editor.OrbitalCameraControl;
        target = cam.m_TargetedPosition;
        distance = cam.m_CameraToTargetDistance;
        hAngle = cam.m_CurrentHAngle;
        vAngle = cam.m_CurrentVAngle;
        haveCamera = true;
    }

    if (app.Editor !is null) {
        app.BackToMainMenu();
        while (app.Editor !is null) yield();
    }
    while (app.ManiaTitleControlScriptAPI is null || !app.ManiaTitleControlScriptAPI.IsReady) yield();
    app.ManiaTitleControlScriptAPI.EditMap(g_latestPath, "", "");

    // Wait for the editor to be up before moving its camera.
    while (Editor() is null || Editor().PluginMapType is null) yield();
    for (int i = 0; i < 10; i++) yield();
    @editor = Editor();
    if (haveCamera && editor !is null && editor.OrbitalCameraControl !is null) {
        auto cam = editor.OrbitalCameraControl;
        cam.m_TargetedPosition = target;
        cam.m_CameraToTargetDistance = distance;
        cam.m_CurrentHAngle = hAngle;
        cam.m_CurrentVAngle = vAngle;
    }
    g_busy = false;
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
        if (editing) {
            UI::TextDisabled("Reloading discards changes made in the game's editor.");
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
