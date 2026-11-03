// Block library: extracts the game files TMSpline's editor needs to show vanilla blocks (meshes, materials, textures,
// icons) into OpenplanetNext/Extract and has the TMSpline app convert them into its block preview.
// One run does everything: the block definitions and prefabs the game knows by name first, then, round by round, the
// files the conversion still misses (the game finds any file by its path, even ones Openplanet has no name for).
// LOCAL USE ONLY: the files are Nadeo's game data. They stay on this PC; never redistribute them.

/// Folders extracted as a whole before the first conversion (everything in them the game knows by name).
const array<string> LIBRARY_FOLDERS = { "GameData/Stadium/GameCtnBlockInfo", "GameData/Stadium/Media/Prefab" };
const int LIBRARY_MAX_ROUNDS = 10;

bool g_libRunning = false;
string g_libStatus = "";
string g_libError = "";
int g_libDone = 0;      // files handled in the current step (for the progress line)
int g_libTotal = 0;
int g_libExtracted = 0; // files extracted in this run

void BuildLibrary(bool again)
{
    if (g_libRunning) return;
    g_libRunning = true;
    g_libError = "";
    g_libExtracted = 0;
    BuildLibraryRun(again);
    g_libRunning = false;
}

void BuildLibraryAgain() { BuildLibrary(true); }
void BuildLibraryMissing() { BuildLibrary(false); }

void BuildLibraryRun(bool again)
{
    // The app must be running and able to build the library.
    g_libStatus = "Asking the TMSpline app";
    auto check = Net::HttpGet(S_Server + "/api/vanilla/build");
    while (!check.Finished()) yield();
    if (check.ResponseCode() != 200) {
        g_libError = check.ResponseCode() == 404
            ? "This TMSpline app cannot build the block library (update it)."
            : "The TMSpline app is not reachable at " + S_Server + ". Start it, then try again.";
        return;
    }

    // 1. What the game knows by name.
    for (uint i = 0; i < LIBRARY_FOLDERS.Length; i++) {
        g_libStatus = "Extracting " + LIBRARY_FOLDERS[i];
        auto folder = Fids::GetGameFolder(LIBRARY_FOLDERS[i]);
        if (folder is null) { warn("Game folder not found: " + LIBRARY_FOLDERS[i]); continue; }
        array<string> paths;
        CollectFiles(folder, LIBRARY_FOLDERS[i], paths);
        ExtractPaths(paths, again);
    }

    // 2. Convert; extract what the conversion misses; again until nothing more can be found.
    for (int round = 1; round <= LIBRARY_MAX_ROUNDS; round++) {
        g_libStatus = "Converting (round " + round + ", takes a while)";
        Json::Value@ result = RunConversion();
        if (result is null) return;
        array<string> missing;
        if (result.HasKey("missing") && result["missing"].GetType() == Json::Type::Array)
            for (uint i = 0; i < result["missing"].Length; i++) missing.InsertLast(string(result["missing"][i]));
        int blocks = result.HasKey("blocks") ? int(result["blocks"]) : 0;
        int before = g_libExtracted;
        if (missing.Length > 0) {
            g_libStatus = "Extracting " + missing.Length + " missing files (round " + round + ")";
            ExtractPaths(missing, true);
        }
        if (g_libExtracted == before) {
            // Nothing new: the rest is not in the game (references to files it does not ship).
            g_libStatus = "Done: " + blocks + " blocks, " + g_libExtracted + " files extracted"
                + (missing.Length > 0 ? " (" + missing.Length + " referenced files are not in the game)" : "") + ". Reload the TMSpline editor.";
            print(g_libStatus);
            UI::ShowNotification("TMSpline", g_libStatus);
            return;
        }
    }
    g_libStatus = "Stopped after " + LIBRARY_MAX_ROUNDS + " rounds; run it again to continue.";
}

/// All files below a game folder, as game paths ("GameData/...").
void CollectFiles(CSystemFidsFolder@ folder, const string &in path, array<string> &inout paths)
{
    for (uint i = 0; i < folder.Leaves.Length; i++)
        paths.InsertLast(path + "/" + folder.Leaves[i].FileName);
    for (uint i = 0; i < folder.Trees.Length; i++) {
        auto sub = folder.Trees[i];
        CollectFiles(sub, path + "/" + sub.DirName, paths);
        if (i % 20 == 0) yield();
    }
}

/// Extracts game files by path into OpenplanetNext/Extract (files already there are kept unless `again`).
void ExtractPaths(const array<string> &in paths, bool again)
{
    g_libDone = 0;
    g_libTotal = paths.Length;
    int notFound = 0, failed = 0;
    for (uint i = 0; i < paths.Length; i++) {
        g_libDone = i + 1;
        if (i % 25 == 0) yield();
        if (!again && IO::FileExists(IO::FromDataFolder("Extract/" + paths[i]))) continue;
        CSystemFidFile@ fid = Fids::GetGame(paths[i]);
        if (fid is null) { notFound++; continue; }
        if (Fids::Extract(fid)) g_libExtracted++;
        else { failed++; warn("Could not extract " + paths[i]); }
    }
    trace("Extracted from " + paths.Length + " paths: " + notFound + " not in the game, " + failed + " failed (" + g_libExtracted + " in this run)");
    g_libTotal = 0;
}

/// Has the app convert the extracted files; its result (blocks, missing files), or null (g_libError says why).
Json::Value@ RunConversion()
{
    auto start = Net::HttpPost(S_Server + "/api/vanilla/build", "", "application/json");
    while (!start.Finished()) yield();
    if (start.ResponseCode() != 200 && start.ResponseCode() != 202) {
        g_libError = "The app could not start the conversion (" + start.ResponseCode() + "): " + start.String();
        return null;
    }
    while (true) {
        sleep(1000);
        auto req = Net::HttpGet(S_Server + "/api/vanilla/build");
        while (!req.Finished()) yield();
        if (req.ResponseCode() != 200) { g_libError = "Lost the app during the conversion (" + req.ResponseCode() + ")."; return null; }
        auto json = Json::Parse(req.String());
        if (json.GetType() != Json::Type::Object) continue;
        if (json.HasKey("running") && bool(json["running"])) {
            if (json.HasKey("progress") && json["progress"].GetType() == Json::Type::String)
                g_libStatus = "Converting: " + string(json["progress"]);
            continue;
        }
        if (json.HasKey("error") && json["error"].GetType() == Json::Type::String) {
            g_libError = "Conversion failed: " + string(json["error"]);
            return null;
        }
        return json;
    }
    return null;   // not reached; keeps the compiler sure every path returns
}

void RenderLibrary()
{
    if (!g_libRunning && g_libError == "") return;
    UI::SetNextWindowPos(20, 260, UI::Cond::FirstUseEver);
    if (UI::Begin("TMSpline block library", UI::WindowFlags::AlwaysAutoResize | UI::WindowFlags::NoCollapse)) {
        if (g_libError != "") {
            UI::Text("\\$f66" + g_libError);
            if (!g_libRunning && UI::Button("Close")) g_libError = "";
        } else {
            UI::Text(g_libStatus);
            if (g_libTotal > 0) UI::Text("\\$888" + g_libDone + " / " + g_libTotal + " files, " + g_libExtracted + " extracted");
        }
    }
    UI::End();
}

void RenderLibraryMenu()
{
    if (UI::MenuItem("Build block library", "", false, !g_libRunning)) startnew(BuildLibraryMissing);
    if (UI::MenuItem("Rebuild block library (extract everything again)", "", false, !g_libRunning)) startnew(BuildLibraryAgain);
    if (g_libStatus != "") UI::TextDisabled(g_libStatus);
}
