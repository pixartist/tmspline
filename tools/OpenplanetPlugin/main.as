// TMSpline: the game side of the TMSpline track builder (https://github.com/pixartist/tmspline).
// It talks to the TMSpline desktop app running on the same PC.
//
// - Live update (Live.as): when TMSpline exports a map to the game, offers to reload it in the map editor. The open
//   map is saved first, and the app merges the new export into it, so what was built in the game's editor stays.
// - Block library (Library.as): copies the game's block definitions, meshes and textures to OpenplanetNext/Extract,
//   where the app converts them into its block preview. On request only (TMSpline menu), and nothing leaves the PC.
//
// What the plugin touches:
// - Network: only the address in the setting below (the app's local server, http://localhost:5080 by default).
// - Files read: maps in Maps/TMSpline (opened in the editor); game files by path (block library only).
// - Files written: the map open in the editor (saved before a live update); OpenplanetNext/Extract (block library).

[Setting name="TMSpline server" description="Address of the TMSpline desktop app's local server (http://localhost:5080 unless you changed its port)."]
string S_Server = "http://localhost:5080";

void Main()
{
    LiveLoop();
}

void Render()
{
    RenderLive();
    RenderLibrary();
}

void RenderMenu()
{
    if (UI::BeginMenu("TMSpline")) {
        RenderLiveMenu();
        UI::Separator();
        RenderLibraryMenu();
        UI::EndMenu();
    }
}
