using System.Numerics;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Builds a map with one test road per candidate RotationConvention. Each road has a banked, climbing boost section
// aimed 40° right and 25° up, made of rotated items. Only the correct convention lines the boost pieces up with the
// rest of the road. Copy k (1-based, west to east) has k small cubes next to its start.
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\Calibration";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);

var meshes = new List<MeshChunk>();
var conventions = RotationConvention.All;
for (var k = 0; k < conventions.Count; k++)
{
    var start = new Vec3d(-360 + k * 48, 20, -60);
    var heading = new Vec3d(Math.Sin(20 * Math.PI / 180), Math.Sin(8 * Math.PI / 180), Math.Cos(20 * Math.PI / 180)).Normalized();
    var track = new Track();
    for (var i = 0; i < 4; i++)
        track.Points.Add(new TrackPoint(start + heading * (32.0 * i), heading * 10, bankDegrees: i is 1 or 2 ? 15 : 0));
    track.Segments.Add(new TrackSegment());
    track.Segments.Add(new TrackSegment { Effect = Effect.Turbo, EffectYaw = 40, EffectPitch = 25 });
    track.Segments.Add(new TrackSegment());

    var settings = new RoadMeshSettings { Rotation = conventions[k], MaxChunkLength = double.PositiveInfinity };
    meshes.AddRange(RoadMeshBuilder.Build(track, TrackSampler.Sample(track), Vec3d.Zero, $"C{k + 1:D2}", settings));
    Console.WriteLine($"copy {k + 1,2}: {conventions[k]}");
}

meshes.Add(Cube());
var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
var cube = items[^1];
var placed = items.Take(items.Count - 1).ToList();
for (var k = 0; k < conventions.Count; k++)
    for (var c = 0; c <= k; c++)
        placed.Add(cube with { Offset = new Vec3d(-360 + k * 48 - 14, 0.5, -70 + c * 3) });

var output = Path.Combine(userDir, "Maps", "TMSpline", "RotationCalibration.Map.Gbx");
MapExporter.Export(Path.Combine(userDir, "Maps", "Autosaves", "AutoSave.Map.Gbx"), output, placed,
    new MapExportOptions { MapName = "TMSpline Rotation Calibration", ItemFolder = folder });
Console.WriteLine($"Wrote {output} with {placed.Count} placed items");

// A 1 m marker cube.
static MeshChunk Cube()
{
    var m = new MeshChunk { Name = "Marker" };
    var mat = m.MaterialIndex(new TmMaterial("Marker", "CustomPlasticShiny", Color: "FF3020"));
    var faces = new (Vector3 N, Vector3 U, Vector3 V)[]
    {
        (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        (Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
        (Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX), (-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
    };
    for (var f = 0; f < faces.Length; f++)
    {
        var (n, u, v) = faces[f];
        var b = m.Positions.Count;
        foreach (var (a, c) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            m.Positions.Add((n + u * a + v * c) * 0.5f);
            m.Normals.Add(n);
            m.Uv0.Add(new Vector2((a + 1) / 2, (c + 1) / 2));
            m.Uv1.Add(new Vector2((f + 0.1f + 0.8f * (a + 1) / 2) / 6, 0.1f + 0.8f * (c + 1) / 2));
        }
        // u × v points inwards for every face above, so wind the quads the other way round.
        m.Triangles.Add(new Triangle(b, b + 2, b + 1, mat));
        m.Triangles.Add(new Triangle(b, b + 3, b + 2, mat));
    }
    return m;
}
