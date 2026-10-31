using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Five short tracks side by side (west to east = candidates 1..5), each with a checkpoint gate in the middle whose
// see-through plane uses a different material. Drive through them to check that they are not collidable.
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\CurtainLook8";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);
// Only natively non-collidable materials: the game ignores a PhysicsId="NotCollidable" override for collision.
var candidates = Enumerable.Range(1, 5).Select(i => (TmMaterial?)new TmMaterial($"Curtain{i}", "GlassWaterWall", null, "80C8FF")).ToArray();
// Texture tiling per candidate (U, V). Very fine tiling makes the GPU sample a low mip level, i.e. the texture's
// average: an even frosted look on which the scrolling animation is invisible.
var uvScale = new System.Numerics.Vector2[] { new(20, 20), new(60, 60), new(200, 200), new(1000, 1000), new(0, 200) };

var meshes = new List<MeshChunk>();
for (var k = 0; k < candidates.Length; k++)
{
    // West to east: +X is west in this right-handed, Y-up frame when driving along +Z.
    var x = (candidates.Length - 1 - k) * 40.0 - 80;
    var track = new Track();
    for (var i = 0; i < 3; i++)
        track.Points.Add(new TrackPoint(new Vec3d(x, 10, -48 + i * 48), new Vec3d(0, 0, 16)) { Waypoint = i == 1 ? Waypoint.Checkpoint : Waypoint.None });
    track.Segments.Add(new TrackSegment());
    track.Segments.Add(new TrackSegment());
    var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, GateCurtain = candidates[k] };
    var rings = TrackSampler.Sample(track);
    meshes.AddRange(RoadMeshBuilder.Build(track, rings, Vec3d.Zero, $"Road{k + 1}", settings));
    // Only the checkpoint gate (the automatic start/finish would just clutter the comparison).
    var gates = WaypointBuilder.Build(track, rings, Vec3d.Zero, settings, $"Gate{k + 1}").Where(g => g.Waypoint == Waypoint.Checkpoint).ToList();
    foreach (var plane in gates.SelectMany(g => g.NotCollidable))
        for (var i = 0; i < plane.Uv0.Count; i++)
            plane.Uv0[i] = plane.Uv0[i] * uvScale[k] + (uvScale[k].X == 0 ? new System.Numerics.Vector2(0.5f, 0) : default);
    meshes.AddRange(gates);
    Console.WriteLine($"candidate {k + 1}: {candidates[k]?.Link ?? "no plane"} color {candidates[k]?.Color ?? "-"} uv x{uvScale[k]}{(candidates[k]?.PhysicsId is null ? "" : " + PhysicsId " + candidates[k]!.PhysicsId)}");
}

var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
var output = Path.Combine(userDir, "Maps", "TMSpline", "CurtainLook8.Map.Gbx");
MapExporter.Export(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TmSpline.Server", "Templates", "Empty48.Map.Gbx"),
    output, items, new MapExportOptions { MapName = $"TMSpline {Path.GetFileName(folder)}", ItemFolder = folder });
Console.WriteLine($"Wrote {output}");
