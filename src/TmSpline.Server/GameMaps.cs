namespace TmSpline.Server;

/// <summary>The latest map written to the game's Maps folder; the Openplanet plugin (tools/TMSplineLive) polls it.</summary>
/// <param name="Id">Changes with every export (the plugin reloads when it differs from the map it opened).</param>
/// <param name="MapPath">Relative to the game's Maps folder, with forward slashes (what the game's EditMap takes).</param>
public sealed record GameExport(string Id, string MapPath, string FullPath, long Bytes, DateTimeOffset ExportedAt);

/// <summary>
/// Writes exported maps straight into the game's Maps folder (a TMSpline subfolder), for a local dev setup: only when
/// <c>Game:MapsDir</c> is configured (appsettings.Development.json), "auto" meaning Documents\Trackmania\Maps.
/// </summary>
public sealed class GameMaps
{
    private const string Subfolder = "TMSpline";
    private readonly object _lock = new();
    private GameExport? _latest;

    public GameMaps(IConfiguration configuration, IWebHostEnvironment environment, ILogger<GameMaps> logger)
    {
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
        logger.LogInformation("Export to game writes to {Dir}", Path.Combine(dir, Subfolder));
    }

    /// <summary>The game's Maps folder, or null when export to game is off.</summary>
    public string? MapsDir { get; }

    public GameExport? Latest { get { lock (_lock) return _latest; } }

    /// <summary>Writes the map (replacing an older export of the same name) and makes it the latest.</summary>
    public GameExport Write(string fileName, byte[] data)
    {
        if (MapsDir is null) throw new InvalidOperationException("Export to game is not configured.");
        var name = Path.GetFileName(fileName);
        var folder = Path.Combine(MapsDir, Subfolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        // Written next to it first, so the game never sees a half-written map.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
        var now = DateTimeOffset.UtcNow;
        var export = new GameExport(now.ToUnixTimeMilliseconds().ToString(), $"{Subfolder}/{name}", path, data.Length, now);
        lock (_lock) _latest = export;
        return export;
    }
}
