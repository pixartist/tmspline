// TMSpline: the game side of the TMSpline track builder, talking to its local server.
// - Live update (Live.as): opens TMSpline's "Export to game" maps in the editor and merges them with what was built there.
// - Block library (Library.as): extracts and converts the game's blocks for the TMSpline editor's preview.

[Setting name="TMSpline server" description="Where the TMSpline server runs."]
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
    if (UI::BeginMenu("\\$f80TMSpline")) {
        RenderLiveMenu();
        UI::Separator();
        RenderLibraryMenu();
        UI::EndMenu();
    }
}
