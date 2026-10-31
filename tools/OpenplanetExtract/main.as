// Extracts game files by path, for TMSpline's local block library (tools/VanillaLib).
// Openplanet's pack explorer only extracts files whose names it knows; the game itself finds any file by its path.
// tools/VanillaLib writes the paths it is missing to PluginStorage/TMSplineExtract/missing.txt (one per line,
// "GameData/Stadium/..."); this plugin extracts them to OpenplanetNext/Extract. Local use only: never redistribute.

bool g_running = false;

void RenderMenu()
{
    if (UI::MenuItem("\\$f80TMSpline\\$z Extract missing block files", "", false, !g_running))
        startnew(ExtractAll);
}

void ExtractAll()
{
    g_running = true;
    string listPath = IO::FromStorageFolder("missing.txt");
    if (!IO::FileExists(listPath)) {
        UI::ShowNotification("TMSpline", "No list at " + listPath + " - run tools/VanillaLib first.");
        g_running = false;
        return;
    }
    IO::File file(listPath, IO::FileMode::Read);
    string[] lines = file.ReadToEnd().Split("\n");
    file.Close();

    int extracted = 0, notFound = 0, failed = 0;
    for (uint i = 0; i < lines.Length; i++) {
        string path = lines[i].Trim();
        if (path.Length == 0) continue;
        CSystemFidFile@ fid = Fids::GetGame(path);
        if (fid is null) {
            notFound++;
            trace("not found: " + path);
        } else if (Fids::Extract(fid)) {
            extracted++;
        } else {
            failed++;
            warn("could not extract: " + path);
        }
        if (i % 25 == 0) yield();
    }
    string summary = "Extracted " + extracted + " files, " + notFound + " not found, " + failed + " failed.";
    print(summary);
    UI::ShowNotification("TMSpline", summary + " Now run tools/VanillaLib again.");
    g_running = false;
}
