using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

namespace TmSpline.Export;

public sealed class TrackExportRequest
{
    public required TrackMap Map { get; init; }
    public required string Name { get; init; }
    public string? AuthorLogin { get; init; }
    public string? AuthorNickname { get; init; }
    public double MaxChunkLength { get; init; } = 64;
    public MeshQuality Quality { get; init; } = MeshQualities.Default;
}

public sealed record TrackExportResult(int ItemCount, int TriangleCount, double Length, string ItemFolder);

/// <summary>The full pipeline: tracks → road meshes → items (NadeoImporter) → map with embedded items.</summary>
public static class TrackExporter
{
    /// <param name="userDir">Trackmania-style user folder where importer sources and items are written.</param>
    public static async Task<TrackExportResult> ExportAsync(TrackExportRequest request, string userDir, IImporter importer,
        Stream template, Stream output, Action<string>? log = null, CancellationToken ct = default)
    {
        var (length, meshes) = MeshQualities.BuildExport(request.Map, request.Quality, request.MaxChunkLength);
        if (meshes.Count == 0)
            throw new ArgumentException("The map has no road (every track needs at least two points)");
        // Own terrain: its chunks (the roads' edges built in), as items like the road.
        if (request.Map.Terrain is { } terrain)
        {
            var land = TerrainMeshBuilder.Build(terrain, request.Map);
            meshes.AddRange(land);
            log?.Invoke($"Terrain: {land.Count} chunk(s), {land.Sum(m => m.Triangles.Count)} triangles");
        }
        log?.Invoke($"Built {meshes.Count} item(s) at {request.Quality} quality, {meshes.Sum(m => m.Triangles.Count)} triangles, {meshes.Sum(m => m.Positions.Count)} vertices, {MeshQualities.SurfaceArea(meshes):0} m2, {request.Map.Tracks.Count} track(s), {length:0} m, "
            + $"gates: {string.Join(", ", meshes.Where(m => m.Waypoint != Waypoint.None).Select(g => g.Waypoint))}");

        // A unique folder per export, so re-exports never collide with items the game has cached from earlier versions.
        var itemFolder = $@"TMSpline\{SafeName(request.Name)}_{Guid.NewGuid().ToString("N")[..8]}";
        var items = await ItemExporter.ImportAllAsync(meshes, userDir, itemFolder, "TMSpline", importer, log, ct);

        MapExporter.Export(template, output, items, new MapExportOptions
        {
            MapName = request.Name,
            ItemFolder = itemFolder,
            AuthorLogin = request.AuthorLogin,
            AuthorNickname = request.AuthorNickname,
            Laps = request.Map.Laps,
            NoStadium = request.Map.NoStadium,
            Environment = request.Map.Environment,
            Blocks = request.Map.Blocks,
            // Vanilla items: the map's own (trees ...) and the placed pillars as the game's obstacle pillars.
            Items = [.. request.Map.Items, .. MeshQualities.GamePillars(request.Map, request.Quality)],
        }, log);

        return new TrackExportResult(items.Count, meshes.Sum(m => m.Triangles.Count), length, itemFolder);
    }

    public static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')).Trim('_');
        return safe.Length == 0 ? "Track" : safe.Length > 40 ? safe[..40] : safe;
    }
}
