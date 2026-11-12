using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

namespace TmSpline.Server;

public sealed class ExportSettings
{
    /// <summary>Native (Windows), Wine (Linux, wine in PATH) or Docker (runs the tmspline-importer image per job).</summary>
    public string ImporterMode { get; set; } = "Wine";
    /// <summary>Folder with NadeoImporter.exe, libfbxsdk.dll and NadeoImporterMaterialLib.txt.</summary>
    public string ImporterDir { get; set; } = "";
    /// <summary>The importer may be downloaded from Nadeo into ImporterDir on the user's request (desktop app only).</summary>
    public bool ImporterDownload { get; set; }
    public string WineCommand { get; set; } = "wine";
    public string DockerImage { get; set; } = "tmspline-importer";
    public string WorkDir { get; set; } = "jobs";
    public string TemplatePath { get; set; } = "Templates/Empty48.Map.Gbx";
    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxQueuedJobs { get; set; } = 8;
    public int MaxPoints { get; set; } = 400;
    public double MaxLengthMeters { get; set; } = 20000;
    public int TimeoutSeconds { get; set; } = 300;
    public bool KeepJobFiles { get; set; }
    public int RequestsPerMinute { get; set; } = 10;
}

public sealed class ExportRequestDto
{
    /// <summary>The track file: a version 2 map with several tracks, or a single track (version 1).</summary>
    public TrackJson.TrackDto? Track { get; set; }
    /// <summary>Optional Trackmania account login (22 characters) to set as map author.</summary>
    public string? AuthorLogin { get; set; }
    public string? AuthorNickname { get; set; }
    /// <summary>Mesh quality; the default (Normal) if missing.</summary>
    public MeshQuality? Quality { get; set; }
}

/// <summary>Size estimate of one export quality (see MeshQualities.EstimateMapBytes).</summary>
/// <param name="TerrainBytes">The part of the sizes that is the own terrain: the same at every quality.</param>
public sealed record ExportEstimateDto(MeshQuality Quality, int Items, int Triangles, long Bytes, long MinBytes, long MaxBytes, long TerrainBytes);

public sealed class ExportRejectedException(string message) : Exception(message);
public sealed class ServerBusyException() : Exception("The export queue is full, please try again in a minute.");

public sealed record ExportedMap(byte[] Data, string FileName, TrackExportResult Result);

public sealed partial class ExportService
{
    private readonly ExportSettings _settings;
    private readonly ILogger<ExportService> _logger;
    private readonly IImporter _importer;
    private readonly SemaphoreSlim _slots;
    private readonly string _workDir;
    private readonly string _templatePath;
    private int _waiting;

    public ExportService(IOptions<ExportSettings> options, IHostEnvironment env, ILogger<ExportService> logger)
    {
        _settings = options.Value;
        _logger = logger;
        _workDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, _settings.WorkDir));
        _templatePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _settings.TemplatePath));
        Directory.CreateDirectory(_workDir);
        _slots = new SemaphoreSlim(Math.Max(1, _settings.MaxConcurrentJobs));

        var timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        _importer = _settings.ImporterMode.ToLowerInvariant() switch
        {
            "native" => new NativeImporter(_settings.ImporterDir, isolated: true, timeout),
            "wine" => new WineImporter(_settings.ImporterDir, _settings.WineCommand, timeout),
            "docker" => new DockerImporter(_settings.ImporterDir, _workDir, _settings.DockerImage, timeout),
            _ => throw new InvalidOperationException($"Unknown Export:ImporterMode '{_settings.ImporterMode}'"),
        };
        _logger.LogInformation("Export: importer {Mode} from {Dir}, jobs in {WorkDir}", _settings.ImporterMode, _settings.ImporterDir, _workDir);
    }

    public string ImporterMode => _settings.ImporterMode;

    /// <summary>Builds the export meshes at every quality (no importer) and estimates the map size of each.</summary>
    public List<ExportEstimateDto> Estimate(ExportRequestDto request)
    {
        var (map, _) = Validate(request);
        // The terrain does not depend on the quality: built once, and only the road's share of the size is uncertain by quality.
        var land = map.Terrain is { } grid ? TerrainMeshBuilder.Build(grid, map) : new List<MeshChunk>();
        var terrainBytes = MeshQualities.EstimateTerrainBytes(land);
        return Enum.GetValues<MeshQuality>().AsParallel().AsOrdered().Select(q =>
        {
            var (_, meshes) = MeshQualities.BuildExport(map, q, 64);
            var road = MeshQualities.EstimateMapBytes(meshes);
            return new ExportEstimateDto(q, meshes.Count + land.Count, meshes.Sum(m => m.Triangles.Count) + land.Sum(m => m.Triangles.Count), road + terrainBytes,
                (long)(road * MeshQualities.EstimateLow) + terrainBytes, (long)(road * MeshQualities.EstimateHigh) + terrainBytes, terrainBytes);
        }).ToList();
    }

    /// <summary>
    /// The map as a 3D model (Wavefront OBJ) for Blender and the like: the meshes the export makes items of, at the
    /// request's quality, with the own terrain. No importer is needed. The game's blocks and trees are not in it.
    /// </summary>
    public (byte[] Data, string FileName) Model(ExportRequestDto request)
    {
        var (map, name) = Validate(request);
        var (_, meshes) = MeshQualities.BuildExport(map, request.Quality ?? MeshQuality.Normal, 64);
        if (map.Terrain is { } grid) meshes.AddRange(TerrainMeshBuilder.Build(grid, map));
        return (ObjWriter.Write(meshes, name), TrackExporter.SafeName(name) + ".obj");
    }

    /// <summary>The map's own terrain as the export builds it, packed for the editor (TerrainMeshBuilder.Preview).</summary>
    public byte[] TerrainPreview(ExportRequestDto request, string? key = null)
    {
        var (map, _) = Validate(request);
        if (map.Terrain is not { } grid || !TerrainMeshBuilder.Voxel) return TerrainMeshBuilder.Preview(map);
        // The voxel terrain, chunk by chunk; kept under the editor's key, so that the brush's dabs can be built into it
        // without the map being sent again (TerrainDab).
        var roads = VoxelTerrain.Prepare(grid, map);
        var bytes = TerrainMeshBuilder.PackChunks(grid, TerrainMeshBuilder.Build(grid, map, roads: roads));
        if (!string.IsNullOrEmpty(key) && key.Length <= 64)
            lock (_painting)
            {
                _painting[key] = new Painting(map, roads, DateTime.UtcNow);
                // A few maps at most (each is a height map and its roads).
                foreach (var old in _painting.OrderByDescending(p => p.Value.Used).Skip(4).Select(p => p.Key).ToList()) _painting.Remove(old);
            }
        return bytes;
    }

    private sealed record Painting(TrackMap Map, VoxelTerrain.Roads Roads, DateTime Used);
    private readonly Dictionary<string, Painting> _painting = [];

    /// <summary>
    /// The brush's dabs (x, y, z, radius, amount each; `over`: not cut by the roads) added to the terrain last built
    /// under the key: the chunks they reach, built again and packed as TerrainPreview's. Null when there is no such
    /// terrain (the server was restarted: the editor asks for the whole again).
    /// </summary>
    public byte[]? TerrainDab(string key, double[] dabs, bool over, bool smooth = false)
    {
        Painting? painting;
        lock (_painting) _painting.TryGetValue(key, out painting);
        if (painting?.Map.Terrain is not { } grid) return null;
        if (dabs.Length == 0 || dabs.Length % 5 != 0 || dabs.Length > 5 * 256 || !dabs.All(double.IsFinite))
            throw new ExportRejectedException("Dabs are five numbers each.");
        lock (painting)
        {
            if (smooth && over) grid.SmoothsOver = [.. grid.SmoothsOver, .. dabs];
            else if (smooth) grid.Smooths = [.. grid.Smooths, .. dabs];
            else if (over) grid.EditsOver = [.. grid.EditsOver, .. dabs];
            else grid.Edits = [.. grid.Edits, .. dabs];
            // The chunks a dab changes: those within its radius, and a little more (the cells round it get smaller).
            var keys = new HashSet<(int X, int Z)>();
            for (var i = 0; i < dabs.Length; i += 5)
            {
                var reach = Math.Abs(dabs[i + 3]) + 16;
                var (x0, z0) = TerrainMeshBuilder.ChunkOf(grid, dabs[i] - reach, dabs[i + 2] - reach);
                var (x1, z1) = TerrainMeshBuilder.ChunkOf(grid, dabs[i] + reach, dabs[i + 2] + reach);
                for (var x = x0; x <= x1; x++) for (var z = z0; z <= z1; z++) keys.Add((x, z));
            }
            if (keys.Count > 64) throw new ExportRejectedException("The dabs reach too far.");
            return TerrainMeshBuilder.PackChunks(grid, TerrainMeshBuilder.BuildChunks(grid, painting.Map, painting.Roads, keys), keys);
        }
    }

    /// <summary>The body of a dab request.</summary>
    public sealed class TerrainDabDto
    {
        public string? Key { get; set; }
        public double[]? Dabs { get; set; }
        public bool Over { get; set; }
        /// <summary>The smoothing brush's dabs, not the raising and lowering ones'.</summary>
        public bool Smooth { get; set; }
    }

    public async Task<ExportedMap> ExportAsync(ExportRequestDto request, CancellationToken ct)
    {
        var (map, name) = Validate(request);

        if (Interlocked.Increment(ref _waiting) > _settings.MaxConcurrentJobs + _settings.MaxQueuedJobs)
        {
            Interlocked.Decrement(ref _waiting);
            throw new ServerBusyException();
        }

        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                return await RunJobAsync(map, name, request, ct);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private async Task<ExportedMap> RunJobAsync(TrackMap map, string name, ExportRequestDto request, CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var jobDir = Path.Combine(_workDir, jobId);
        var userDir = Path.Combine(jobDir, "user");
        Directory.CreateDirectory(userDir);
        var started = DateTime.UtcNow;

        try
        {
            // Another environment: its own empty map beside the Stadium one (BlueBay.Map.Gbx ...).
            var templatePath = map.Environment == MapEnvironment.Stadium ? _templatePath
                : Path.Combine(Path.GetDirectoryName(_templatePath)!, $"{map.Environment}.Map.Gbx");
            if (!File.Exists(templatePath)) throw new ExportRejectedException($"No template map for {map.Environment} on this server.");
            await using var template = File.OpenRead(templatePath);
            using var output = new MemoryStream();
            var result = await TrackExporter.ExportAsync(new TrackExportRequest
            {
                Map = map,
                Name = name,
                AuthorLogin = request.AuthorLogin,
                AuthorNickname = request.AuthorNickname,
                Quality = request.Quality ?? MeshQualities.Default,
            }, userDir, _importer, template, output, s => _logger.LogDebug("[{Job}] {Message}", jobId, s), ct);

            _logger.LogInformation("[{Job}] Exported '{Name}': {Items} items, {Length:0} m in {Seconds:0.0} s",
                jobId, name, result.ItemCount, result.Length, (DateTime.UtcNow - started).TotalSeconds);
            return new ExportedMap(output.ToArray(), TrackExporter.SafeName(name) + ".Map.Gbx", result);
        }
        catch (ImporterException e)
        {
            _logger.LogError("[{Job}] Importer failed: {Output}", jobId, e.Output);
            throw;
        }
        finally
        {
            if (!_settings.KeepJobFiles)
                TryDelete(jobDir);
        }
    }

    /// <summary>Most tracks one map may have.</summary>
    private const int MaxTracks = 64;
    /// <summary>Highest point of anything in meters above the ground, and highest block level: the map grows up to its
    /// largest size (MapExporter.MaxMapRows: 255 rows of 8 m, the ground at row 9) and keeps some room above.</summary>
    /// <summary>" at x, y, z m (limits ...)" for refusal messages, so the user can find the piece.</summary>
    private static string Where(double[]? p) => p is { Length: 3 }
        ? $" (at {p[0]:0}, {p[1]:0}, {p[2]:0} m; the limits are {MaxOffset:0} m from the centre and -16 to {MaxHeight:0} m high)"
        : "";

    private const double MaxHeight = 1960;
    /// <summary>The land may lie this deep: in the environments with water it goes on under the surface (the editor makes it down to -64 m; the check leaves room).</summary>
    private const double MinTerrainHeight = -1024;
    /// <summary>Most vertices of an own terrain grid (4 m cells: 32 km² or so).</summary>
    private const int MaxTerrainVertices = 2_000_000;
    /// <summary>
    /// How far from the map's centre points, free blocks and items may be: 2 km beyond the 48 grid's edges (768 m).
    /// The game keeps and drives them there; the grid itself cannot grow (wider maps crash the game).
    /// </summary>
    private const double MaxOffset = 768 + 2048;
    private const int MaxBlockLevel = 240;
    // Generated scenery (land, hills and their columns, trees) runs into thousands.
    private const int MaxBlocks = 20000;
    private const int MaxItems = 20000;

    private (TrackMap Map, string Name) Validate(ExportRequestDto request)
    {
        var dto = request.Track ?? throw new ExportRejectedException("Missing track");
        if (dto.Version > TrackJson.CurrentVersion)
            throw new ExportRejectedException($"Track file version {dto.Version} is newer than this server supports");
        var parts = TrackJson.Parts(dto);
        if (parts.Count > MaxTracks)
            throw new ExportRejectedException($"Too many tracks ({parts.Count}, max {MaxTracks})");
        var blocks = dto.Blocks ?? new();
        if (blocks.Count > MaxBlocks)
            throw new ExportRejectedException($"Too many blocks ({blocks.Count}, max {MaxBlocks})");
        foreach (var block in blocks)
        {
            if (block.Name is not { Length: > 0 and <= 100 } || !block.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ExportRejectedException("Block names may only contain letters, digits and _");
            if (block.Free && (block.Position is not { Length: 3 } bp || !bp.All(double.IsFinite) || Math.Abs(bp[0]) > MaxOffset || Math.Abs(bp[2]) > MaxOffset
                || bp[1] is < -16 or > MaxHeight || !double.IsFinite(block.Yaw + block.Pitch + block.Roll)))
                throw new ExportRejectedException($"Free block {block.Name} is outside the map{Where(block.Position)}");
            if (!block.Free && (Math.Abs(block.X) > 24 || Math.Abs(block.Z) > 24 || block.Y is < 0 or > MaxBlockLevel))
                throw new ExportRejectedException($"Block {block.Name} is outside the map");
        }
        var vanillaItems = dto.Items ?? new();
        if (vanillaItems.Count > MaxItems)
            throw new ExportRejectedException($"Too many items ({vanillaItems.Count}, max {MaxItems})");
        foreach (var item in vanillaItems)
        {
            if (item.Name is not { Length: > 0 and <= 100 } || !item.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ExportRejectedException("Item names may only contain letters, digits and _");
            if (item.Position is not { Length: 3 } ip || !ip.All(double.IsFinite) || Math.Abs(ip[0]) > MaxOffset || Math.Abs(ip[2]) > MaxOffset
                || ip[1] is < -16 or > MaxHeight || !double.IsFinite(item.Yaw))
                throw new ExportRejectedException($"Item {item.Name} is outside the map{Where(item.Position)}");
        }
        var pointCount = parts.Sum(t => t.Points.Count);
        if (pointCount > _settings.MaxPoints)
            throw new ExportRejectedException($"Too many points ({pointCount}, max {_settings.MaxPoints})");
        for (var t = 0; t < parts.Count; t++)
            if (parts[t].Points.Count < 2)
                throw new ExportRejectedException(parts.Count > 1 ? $"Track {t + 1} needs at least two points" : "A track needs at least two points");
        if (request.AuthorLogin is { Length: > 0 } login && !AccountLogin().IsMatch(login))
            throw new ExportRejectedException("Author login must be the 22-character account id (see trackmania.io)");

        foreach (var p in parts.SelectMany(t => t.Points))
        {
            if (p.Position.Length != 3 || p.HandleIn.Length != 3 || p.HandleOut.Length != 3)
                throw new ExportRejectedException("Point coordinates must be [x, y, z]");
            if (p.Position.Concat(p.HandleIn).Concat(p.HandleOut).Append(p.Bank).Any(v => !double.IsFinite(v)))
                throw new ExportRejectedException("Point values must be finite numbers");
            if (Math.Abs(p.Bank) > 3600)
                throw new ExportRejectedException("Bank must be within ±3600° (10 turns)");
            if (Math.Abs(p.Position[0]) > MaxOffset || Math.Abs(p.Position[2]) > MaxOffset || p.Position[1] < -8 || p.Position[1] > MaxHeight)
                throw new ExportRejectedException($"Points must stay within {MaxOffset} m of the map's centre");
            if (p.Width is { } w && (!double.IsFinite(w) || w < 2 || w > 512))
                throw new ExportRejectedException("Road width must be between 2 and 512 m");
            if (!double.IsFinite(p.Curve) || p.Curve < -360 || p.Curve > 360)
                throw new ExportRejectedException("Road shape must be between -360° (tube) and 360° (pipe)");
            if (p.BorderHeight is { } bh && (!double.IsFinite(bh) || bh < 0.1 || bh > 10))
                throw new ExportRejectedException("Border height must be between 0.1 and 10 m");
            if (!double.IsFinite(p.Flat) || p.Flat < 0 || p.Flat > 0.9)
                throw new ExportRejectedException("Flat must be between 0 and 90 %");
            if (!double.IsFinite(p.Sausage) || p.Sausage < 0 || p.Sausage > 1)
                throw new ExportRejectedException("Sausage must be between 0 and 100 %");
        }
        foreach (var s in parts.SelectMany(t => t.Segments))
        {
            if (!double.IsFinite(s.EffectYaw) || !double.IsFinite(s.EffectPitch) || Math.Abs(s.EffectYaw) > 360 || Math.Abs(s.EffectPitch) > 90)
                throw new ExportRejectedException("Effect direction must be within ±360° (direction) and ±90° (tilt)");
        }

        if (dto.Terrain is { } terrain)
        {
            if (terrain.Columns < 2 || terrain.Rows < 2 || (long)terrain.Columns * terrain.Rows > MaxTerrainVertices || terrain.Heights.Length != terrain.Columns * terrain.Rows)
                throw new ExportRejectedException($"The terrain grid must have 2 to {MaxTerrainVertices} vertices, one height each");
            if (!double.IsFinite(terrain.Cell) || terrain.Cell < 4 || terrain.Cell > 64 || !double.IsFinite(terrain.Skirt) || terrain.Skirt < 0 || terrain.Skirt > MaxHeight + 16)
                throw new ExportRejectedException($"The terrain's cells must be 4 to 64 m, its skirt 0 to {MaxHeight + 16:0} m");
            if (!double.IsFinite(terrain.X0) || !double.IsFinite(terrain.Z0) || Math.Abs(terrain.X0) > MaxOffset + 512 || Math.Abs(terrain.Z0) > MaxOffset + 512
                || Math.Abs(terrain.X0 + terrain.Cell * (terrain.Columns - 1)) > MaxOffset + 512 || Math.Abs(terrain.Z0 + terrain.Cell * (terrain.Rows - 1)) > MaxOffset + 512)
                throw new ExportRejectedException($"The terrain must stay within {MaxOffset} m of the map's centre");
            if (terrain.RockAngle is { } rockAngle && (!double.IsFinite(rockAngle) || rockAngle < 0 || rockAngle > 90))
                throw new ExportRejectedException("The terrain's rock angle must be 0 to 90°");
            if (terrain.Heights.Any(h => h is { } v && (!double.IsFinite(v) || v < MinTerrainHeight || v > MaxHeight)))
                throw new ExportRejectedException($"Terrain heights must be between {MinTerrainHeight:0} and {MaxHeight:0} m");
        }

        var map = TrackJson.ToMap(dto);
        var length = map.Tracks.Sum(t => TrackSampler.Sample(t).LastOrDefault()?.Distance ?? 0);
        if (length > _settings.MaxLengthMeters)
            throw new ExportRejectedException($"The tracks are too long ({length:0} m, max {_settings.MaxLengthMeters:0} m in all)");

        var name = string.IsNullOrWhiteSpace(dto.Name) ? "Untitled" : dto.Name.Trim();
        return (map, name.Length > 64 ? name[..64] : name);
    }

    private void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not delete job folder {Dir}", dir);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
    private static partial Regex AccountLogin();
}
