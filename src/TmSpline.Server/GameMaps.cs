using System.Text.Json;
using TmSpline.Export;

namespace TmSpline.Server;

/// <summary>The latest export for the game; the Openplanet plugin (tools/TMSplineLive) polls it.</summary>
/// <param name="Id">Changes with every export (the plugin reloads when it differs from the map it opened).</param>
/// <param name="Bytes">Size of the export itself (without what the game edits add).</param>
/// <param name="MapPath">Relative to the game's Maps folder, with forward slashes (what the game's EditMap takes).</param>
/// <param name="Applied">
/// False while the export waits for the plugin: it saves the open map first, so the edits made in the game since are
/// in the file when the export is merged into it (POST /api/game/apply).
/// </param>
/// <param name="Merge">What the merge kept and replaced, once applied into a map edited in the game.</param>
public sealed record GameExport(string Id, string MapPath, string FullPath, long Bytes, DateTimeOffset ExportedAt, bool Applied,
    MapMergeResult? Merge = null);

/// <summary>
/// Writes exported maps straight into the game's Maps folder (a TMSpline subfolder), for a local dev setup: only when
/// <c>Game:MapsDir</c> is configured (appsettings.Development.json), "auto" meaning Documents\Trackmania\Maps.
/// An export goes into the map file as the game last saved it: what the previous export put there is replaced, what was
/// added in the game's editor stays (<see cref="MapMerge"/>). For that it keeps each map's last export as written
/// (<c>Game:StateDir</c>).
/// </summary>
public sealed class GameMaps
{
    private const string Subfolder = "TMSpline";
    /// <summary>How recently the plugin must have polled for an export to wait for it (it saves the open map first).</summary>
    private static readonly TimeSpan PluginTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly ILogger<GameMaps> _logger;
    private readonly string _stateDir = "";
    private GameExport? _latest;
    private Staged? _pending;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    /// <summary>An export not yet in the map file.</summary>
    private sealed record Staged(string Id, string Name, string ItemFolder, string ExportPath, DateTimeOffset ExportedAt);

    /// <summary>Which export the map file was last written from (what of it is TMSpline's).</summary>
    private sealed record MapState(string Id, string ItemFolder);

    public GameMaps(IConfiguration configuration, IWebHostEnvironment environment, ILogger<GameMaps> logger)
    {
        _logger = logger;
        var configured = configuration["Game:MapsDir"];
        if (string.IsNullOrWhiteSpace(configured)) return;
        var dir = configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Trackmania", "Maps")
            : Path.GetFullPath(configured, environment.ContentRootPath);
        if (!Directory.Exists(dir))
        {
            logger.LogWarning("Game maps folder {Dir} does not exist; export to game is off", dir);
            return;
        }
        MapsDir = dir;
        _stateDir = configuration["Game:StateDir"] is { Length: > 0 } state
            ? Path.GetFullPath(state, environment.ContentRootPath)
            : Path.Combine(Path.GetTempPath(), "TMSpline", "game");
        Directory.CreateDirectory(_stateDir);
        logger.LogInformation("Export to game writes to {Dir} (state in {State})", Path.Combine(dir, Subfolder), _stateDir);
    }

    /// <summary>The game's Maps folder, or null when export to game is off.</summary>
    public string? MapsDir { get; }

    /// <summary>The latest export; asking counts as the plugin being there.</summary>
    public GameExport? Poll()
    {
        lock (_lock)
        {
            _lastPoll = DateTimeOffset.UtcNow;
            return _latest;
        }
    }

    /// <summary>
    /// Takes a new export. With the plugin running it waits for it (the plugin saves the open map, then applies);
    /// otherwise it goes into the map file right away (merged with what the game last saved).
    /// </summary>
    public GameExport Add(string fileName, byte[] data, string itemFolder)
    {
        if (MapsDir is null) throw new InvalidOperationException("Export to game is not configured.");
        var name = Path.GetFileName(fileName);
        var now = DateTimeOffset.UtcNow;
        var id = now.ToUnixTimeMilliseconds().ToString();
        var exportPath = Path.Combine(_stateDir, $"{id}.export.gbx");
        File.WriteAllBytes(exportPath, data);
        var staged = new Staged(id, name, itemFolder, exportPath, now);

        lock (_lock)
        {
            if (_pending is { } older && older.Id != id) TryDelete(older.ExportPath);
            var waitForPlugin = now - _lastPoll < PluginTimeout && File.Exists(MapPath(name));
            if (!waitForPlugin)
                return ApplyLocked(staged);
            _pending = staged;
            _latest = new GameExport(id, $"{Subfolder}/{name}", MapPath(name), data.Length, now, Applied: false);
            _logger.LogInformation("Export {Id} of {Name} waits for the plugin to save the open map", id, name);
            return _latest;
        }
    }

    /// <summary>Puts the waiting export into its map file (after the plugin saved the editor's map).</summary>
    public GameExport? Apply(string id)
    {
        lock (_lock)
        {
            if (_latest is { } latest && latest.Id == id && latest.Applied) return latest;
            return _pending is { } pending && pending.Id == id ? ApplyLocked(pending) : null;
        }
    }

    private GameExport ApplyLocked(Staged export)
    {
        var path = MapPath(export.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var statePath = StatePath(export.Name);
        var state = ReadState(statePath);
        var previousPath = state is null ? null : Path.Combine(_stateDir, $"{state.Id}.export.gbx");
        MapMergeResult? merge = null;
        // Written next to it first, so the game never sees a half-written map.
        var temp = path + ".tmp";

        if (state is not null && File.Exists(path) && File.Exists(previousPath))
        {
            try
            {
                using (var edited = File.OpenRead(path))
                using (var previous = File.OpenRead(previousPath))
                using (var next = File.OpenRead(export.ExportPath))
                using (var output = File.Create(temp))
                    merge = MapMerge.Merge(edited, previous, state.ItemFolder, next, export.ItemFolder, output,
                        s => _logger.LogDebug("[{Id}] {Message}", export.Id, s));
                _logger.LogInformation("Export {Id} merged into {Path}: {Merge}", export.Id, path, merge);
            }
            catch (Exception e) when (e is not IOException)
            {
                // A map the merge cannot read: better the new export alone than nothing (the edits are in the backup).
                _logger.LogWarning(e, "Could not merge export {Id} into {Path}; replacing it", export.Id, path);
                File.Copy(path, path + ".backup", overwrite: true);
                File.Copy(export.ExportPath, temp, overwrite: true);
            }
        }
        else
        {
            File.Copy(export.ExportPath, temp, overwrite: true);
            _logger.LogInformation("Export {Id} written to {Path}{Note}", export.Id, path,
                File.Exists(path) ? " (replacing a map from before TMSpline kept track of its exports)" : "");
        }
        File.Move(temp, path, overwrite: true);

        File.WriteAllText(statePath, JsonSerializer.Serialize(new MapState(export.Id, export.ItemFolder)));
        if (previousPath is not null && previousPath != export.ExportPath) TryDelete(previousPath);
        if (_pending?.Id == export.Id) _pending = null;
        _latest = new GameExport(export.Id, $"{Subfolder}/{export.Name}", path, new FileInfo(export.ExportPath).Length, export.ExportedAt, Applied: true, merge);
        return _latest;
    }

    private string MapPath(string name) => Path.Combine(MapsDir!, Subfolder, name);

    private string StatePath(string name) => Path.Combine(_stateDir, name + ".json");

    private static MapState? ReadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<MapState>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
