using System.Numerics;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Probe map for Trackmania's item rotation. West to east, 30 m apart, 10 m above the ground:
//   1. axis marker, no rotation          (red bar = local +X, green = +Y, blue = +Z, white cube = origin)
//   2. axis marker, stored vector (0.5, 0, 0)
//   3. axis marker, stored vector (0, 0.5, 0)
//   4. axis marker, stored vector (0, 0, 0.5)
// Plus, further north, a straight road along +Z whose middle segment is a boost made of unrotated items,
// and two 16 m decal test pads south of the markers (west: Turbo_Decal, east: Turbo2_Decal) with U along +X, V along +Z.
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\Probe";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);

var probe = AxisMarker();
var road = new Track();
for (var i = 0; i < 4; i++)
    road.Points.Add(new TrackPoint(new Vec3d(0, 10, 80 + i * 32), new Vec3d(0, 0, 10)));
road.Segments.Add(new TrackSegment());
road.Segments.Add(new TrackSegment { Effect = Effect.Turbo });
road.Segments.Add(new TrackSegment());
// WorldSpace keeps the boost pieces unrotated (identity frame) while still splitting them out with the boost material.
var roadMeshes = RoadMeshBuilder.Build(road, TrackSampler.Sample(road), Vec3d.Zero, "ProbeRoad",
    new RoadMeshSettings { WorldSpace = true, MaxChunkLength = double.PositiveInfinity });

var pads = new[] { DecalPad("PadTurbo", "Turbo_Decal"), DecalPad("PadTurbo2", "Turbo2_Decal") };
var items = await ItemExporter.ImportAllAsync([probe, .. pads, .. roadMeshes], userDir, folder, "TMSpline", importer, Console.WriteLine);
var marker = items[0];
var placed = new List<ImportedItem>(items.Skip(3))
{
    items[1] with { Offset = new Vec3d(-30, 0.2, -40) },
    items[2] with { Offset = new Vec3d(10, 0.2, -40) },
};
var rotations = new[] { Vec3d.Zero, new Vec3d(0.5, 0, 0), new Vec3d(0, 0.5, 0), new Vec3d(0, 0, 0.5) };
for (var i = 0; i < rotations.Length; i++)
    placed.Add(marker with { Offset = new Vec3d(-45 + i * 30, 10, 0), PitchYawRoll = rotations[i] });

var output = Path.Combine(userDir, "Maps", "TMSpline", "RotationProbe.Map.Gbx");
MapExporter.Export(Path.Combine(userDir, "Maps", "Autosaves", "AutoSave.Map.Gbx"), output, placed,
    new MapExportOptions { MapName = "TMSpline Rotation Probe", ItemFolder = folder });
Console.WriteLine($"Wrote {output}");

// A 16 x 16 m platform with the decal 2 cm above it; decal UV (0,0) at -X/-Z, U grows along +X, V along +Z.
static MeshChunk DecalPad(string name, string decal)
{
    var m = new MeshChunk { Name = name };
    Box(m, m.MaterialIndex(new TmMaterial("Base", "PlatformTech")), new Vector3(0, -0.25f, 0), new Vector3(16, 0.5f, 16));
    var mat = m.MaterialIndex(new TmMaterial("Decal", decal));
    var b = m.Positions.Count;
    foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
    {
        m.Positions.Add(new Vector3(-8 + 16 * u, 0.02f, -8 + 16 * v));
        m.Normals.Add(Vector3.UnitY);
        m.Uv0.Add(new Vector2(u, v));
        m.Uv1.Add(new Vector2(0.9f + 0.09f * u, 0.9f + 0.09f * v));
    }
    // Counter-clockwise seen from above (+Y).
    m.Triangles.Add(new Triangle(b, b + 2, b + 1, mat));
    m.Triangles.Add(new Triangle(b, b + 3, b + 2, mat));
    return m;
}

static MeshChunk AxisMarker()
{
    var m = new MeshChunk { Name = "Axes" };
    int Mat(string name, string color) => m.MaterialIndex(new TmMaterial(name, "CustomPlasticShiny", Color: color));
    Box(m, Mat("White", "FFFFFF"), new Vector3(0, 0, 0), new Vector3(1.6f, 1.6f, 1.6f));
    Box(m, Mat("Red", "FF2020"), new Vector3(4, 0, 0), new Vector3(8, 0.6f, 0.6f));    // +X
    Box(m, Mat("Green", "20FF20"), new Vector3(0, 3, 0), new Vector3(0.6f, 6, 0.6f));  // +Y
    Box(m, Mat("Blue", "2060FF"), new Vector3(0, 0, 5), new Vector3(0.6f, 0.6f, 10)); // +Z
    return m;
}

static void Box(MeshChunk m, int material, Vector3 center, Vector3 size)
{
    var h = size / 2;
    var faces = new (Vector3 N, Vector3 U, Vector3 V)[]
    {
        (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
    };
    var boxIndex = m.Triangles.Count / 12;
    for (var f = 0; f < faces.Length; f++)
    {
        var (n, u, v) = faces[f]; // u × v = n, so (0,1,2),(0,2,3) is counter-clockwise from outside
        var b = m.Positions.Count;
        foreach (var (a, c) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            m.Positions.Add(center + (n + u * a + v * c) * h);
            m.Normals.Add(n);
            m.Uv0.Add(new Vector2((a + 1) / 2, (c + 1) / 2));
            // Lightmap: one cell per face per box in a 6 x 4 grid.
            m.Uv1.Add(new Vector2((f + 0.1f + 0.8f * (a + 1) / 2) / 6, (boxIndex + 0.1f + 0.8f * (c + 1) / 2) / 4));
        }
        m.Triangles.Add(new Triangle(b, b + 1, b + 2, material));
        m.Triangles.Add(new Triangle(b, b + 2, b + 3, material));
    }
}
