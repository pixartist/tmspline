using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// What the game's item color (the map editor's palette) does to our items: six identical tracks side by side, each
// placed with one color (column k has k pillars at its start: 1 Default, 2 White, 3 Green, 4 Blue, 5 Red, 6 Black).
// Each track runs through RoadTech with rubber borders, PlatformTech without borders, PlatformPlastic with plastic
// borders, RoadDirt without underside, and a checkpoint gate.
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\ColorLook";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);

var meshes = new List<MeshChunk>();
foreach (var color in Enum.GetValues<ItemColor>())
{
    var k = (int)color;
    var x = (k - 2.5) * 30;
    var track = new Track();
    for (var i = 0; i < 5; i++)
        track.Points.Add(new TrackPoint(new Vec3d(x, 10, -120 + i * 60), new Vec3d(0, 0, 20)) { Waypoint = i == 2 ? Waypoint.Checkpoint : Waypoint.None });
    track.Segments.Add(new TrackSegment { Surface = Surface.RoadTech });
    track.Segments.Add(new TrackSegment { Surface = Surface.PlatformTech, Borders = false });
    track.Segments.Add(new TrackSegment { Surface = Surface.PlatformPlastic, BorderMaterial = BorderMaterial.Plastic });
    track.Segments.Add(new TrackSegment { Surface = Surface.RoadDirt, Underside = false });
    for (var p = 0; p <= k; p++)
        track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Pillar, Segment = 0, At = 0.1, Offset = (p - k / 2.0) * 2.2, Length = 1, Width = 1, Height = 2 });

    var (_, trackMeshes) = MeshQualities.BuildExport(track, MeshQuality.Low, double.PositiveInfinity, $"C{k}_");
    foreach (var m in trackMeshes)
        m.Color = color;
    meshes.AddRange(trackMeshes);
    Console.WriteLine($"column {k + 1} ({k + 1} pillars): {color}");
}

// A start in front of the columns, so the map can be driven.
var start = new Track();
start.Points.Add(new TrackPoint(new Vec3d(0, 10, -200), new Vec3d(0, 0, 16)) { Waypoint = Waypoint.Start });
start.Points.Add(new TrackPoint(new Vec3d(0, 10, -150), new Vec3d(0, 0, 16)) { Waypoint = Waypoint.Finish });
start.Segments.Add(new TrackSegment());
meshes.AddRange(MeshQualities.BuildExport(start, MeshQuality.Low, double.PositiveInfinity, "Start_").Meshes);

var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
var output = Path.Combine(userDir, "Maps", "TMSpline", "ColorLook.Map.Gbx");
MapExporter.Export(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TmSpline.Server", "Templates", "Empty48.Map.Gbx"),
    output, items, new MapExportOptions { MapName = "TMSpline ColorLook", ItemFolder = folder });
Console.WriteLine($"Wrote {output} ({items.Count} items)");
