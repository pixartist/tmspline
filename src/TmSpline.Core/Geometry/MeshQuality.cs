using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// How finely the exported road follows the spline. Finer levels keep the kinks between strips smaller, which is what
/// the car feels at speed (a 2° kink at 300 km/h jolts it by almost 3 m/s), at the cost of map size.
/// </summary>
public enum MeshQuality
{
    Low,
    Normal,
    Fine,
    High,
    /// <summary>Between High and Ultra: about the middle in map size.</summary>
    Extra,
    Ultra,
}

public static class MeshQualities
{
    public const MeshQuality Default = MeshQuality.Normal;

    /// <summary>Ring spacing: maximum step on straight road and maximum bend (along the road) between two rings.</summary>
    public static SamplerSettings Sampler(MeshQuality quality) => quality switch
    {
        MeshQuality.Low => new SamplerSettings { MaxStep = 16, MaxAngleDegrees = 2 },
        MeshQuality.Normal => new SamplerSettings { MaxStep = 8, MaxAngleDegrees = 1 },
        MeshQuality.Fine => new SamplerSettings { MaxStep = 6, MaxAngleDegrees = 0.7 },
        MeshQuality.High => new SamplerSettings { MaxStep = 4, MaxAngleDegrees = 0.5 },
        MeshQuality.Extra => new SamplerSettings { MaxStep = 3, MaxAngleDegrees = 0.35 },
        _ => new SamplerSettings { MaxStep = 2, MaxAngleDegrees = 0.25 },
    };

    /// <summary>Maximum bend across the road between two columns (pipes, half-pipes, sausages), in degrees.</summary>
    public static double ColumnAngleDegrees(MeshQuality quality) => quality switch
    {
        MeshQuality.Low => 7.5,
        MeshQuality.Normal => 5,
        MeshQuality.Fine => 4.25,
        MeshQuality.High => 3.75,
        MeshQuality.Extra => 3.25,
        _ => 2.5,
    };

    /// <summary>Total area of all triangles in square meters.</summary>
    public static double SurfaceArea(IEnumerable<MeshChunk> meshes) => meshes.Sum(m => m.Triangles.Sum(t =>
        0.5 * System.Numerics.Vector3.Cross(m.Positions[t.B] - m.Positions[t.A], m.Positions[t.C] - m.Positions[t.A]).Length()));

    /// <summary>
    /// Rough map size in bytes for exported meshes, fitted to real exports (template, a little per item, and per triangle by
    /// material, after NadeoImporter and the map's zip). How well the items compress depends on the track (regular,
    /// straight meshes compress about twice as well), so the real size lies within about
    /// <see cref="EstimateLow"/>..<see cref="EstimateHigh"/> times the estimate.
    /// </summary>
    public static long EstimateMapBytes(IEnumerable<MeshChunk> meshes)
    {
        const double template = 12_500, perItem = 1_480, road = 17.6, custom = 4.75, other = 12.4;
        var bytes = template;
        foreach (var m in meshes)
        {
            bytes += perItem;
            foreach (var t in m.Triangles)
            {
                var link = m.Materials[t.Material].Link;
                bytes += link.StartsWith("Road") || link.StartsWith("Platform") ? road : link.StartsWith("Custom") ? custom : other;
            }
            bytes += m.NotCollidable.Sum(c => c.Triangles.Count) * other;
        }
        return (long)bytes;
    }

    public const double EstimateLow = 0.78, EstimateHigh = 1.32;

    /// <summary>
    /// The exported meshes (road chunks, gates, pillars) of all tracks of a map at a quality, as <c>TrackExporter</c>
    /// builds them, and the total road length. Item names get a per-track prefix when there are several tracks.
    /// </summary>
    public static (double Length, List<MeshChunk> Meshes) BuildExport(TrackMap map, MeshQuality quality, double maxChunkLength)
    {
        var meshes = new List<MeshChunk>();
        var length = 0.0;
        for (var t = 0; t < map.Tracks.Count; t++)
        {
            var (rings, trackMeshes) = BuildExport(map.Tracks[t], quality, maxChunkLength, map.Tracks.Count > 1 ? $"T{t + 1}_" : "");
            meshes.AddRange(trackMeshes);
            length += rings.Count > 0 ? rings[^1].Distance : 0;
        }
        return (length, meshes);
    }

    /// <summary>The exported meshes of one track, item names starting with <paramref name="prefix"/>.</summary>
    public static (List<Ring> Rings, List<MeshChunk> Meshes) BuildExport(Track track, MeshQuality quality, double maxChunkLength, string prefix = "")
    {
        var rings = TrackSampler.Sample(track, Sampler(quality));
        var meshes = new List<MeshChunk>();
        if (rings.Count < 2)
            return (rings, meshes);
        var settings = new RoadMeshSettings { MaxChunkLength = maxChunkLength, MaxColumnAngleDegrees = ColumnAngleDegrees(quality) };
        meshes.AddRange(RoadMeshBuilder.Build(track, rings, Vec3d.Zero, prefix + "Road", settings));
        meshes.AddRange(WaypointBuilder.Build(track, rings, Vec3d.Zero, settings, prefix + "Gate"));
        // Pillars are the game's own items (see GamePillars), not part of the custom meshes.
        return (rings, meshes);
    }

    /// <summary>The game's items the tracks place: obstacle pillars (placed pillars) and curve signs (segment signs).</summary>
    public static List<VanillaItem> GamePillars(TrackMap map, MeshQuality quality)
    {
        var items = new List<VanillaItem>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var pillars = track.Objects.Any(o => o.Kind == TrackObjectKind.Pillar);
            var signs = track.Segments.Any(s => s.Signs != RoadSignKind.None);
            if (!pillars && !signs) continue;
            var rings = TrackSampler.Sample(track, Sampler(quality));
            if (rings.Count < 2) continue;
            var starts = TrackSampler.SegmentStarts(track);
            if (pillars) items.AddRange(PillarBuilder.GameItems(track, rings, starts));
            if (signs) items.AddRange(SignBuilder.GameItems(track, rings, starts, new RoadMeshSettings()));
        }
        return items;
    }
}
