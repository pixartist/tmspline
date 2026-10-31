using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Where vanilla blocks end up relative to our coordinates, and how their direction turns them: RoadTechRampHigh in all
// four directions on the ground (cells x = -6, -3, 0, 3; z = 2) and North/East in the air (level 3, cells x = 6, 9).
// Around each block's cell, four short colored road stubs start 4 m outside the cell edges at the ramp's entry
// height, pointing away: red = +Z side, blue = +X side, green = -Z side, black = -X side.
// Columns left to right (towards +X): North, East, South, West (ground), North, East (air).
if (args.Contains("--free")) { await FreeProbe.Run(); return; }
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\BlockProbe";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);

var blocks = new List<VanillaBlock>();
var meshes = new List<MeshChunk>();
var cells = new (int X, int Y, int Dir)[] { (-6, 0, 0), (-3, 0, 1), (0, 0, 2), (3, 0, 3), (6, 3, 0), (9, 3, 1) };
const int z = 2;
var k = 0;
foreach (var (x, y, dir) in cells)
{
    blocks.Add(new VanillaBlock { Name = "RoadTechRampHigh", X = x, Y = y, Z = z, Dir = dir });
    var center = new Vec3d(x * 32 + 16, y * 8 + 2, z * 32 + 16);
    var sides = new (Vec3d Out, ItemColor Color)[]
    {
        (new Vec3d(0, 0, 1), ItemColor.Red), (new Vec3d(1, 0, 0), ItemColor.Blue),
        (new Vec3d(0, 0, -1), ItemColor.Green), (new Vec3d(-1, 0, 0), ItemColor.Black),
    };
    foreach (var (dirOut, color) in sides)
    {
        var start = center + dirOut * 20;
        var track = new Track();
        track.Points.Add(new TrackPoint(start, dirOut * 3, 0, 10));
        track.Points.Add(new TrackPoint(start + dirOut * 8, dirOut * 3, 0, 10));
        track.Segments.Add(new TrackSegment { Surface = Surface.PlatformTech, Borders = false });
        var (_, stub) = MeshQualities.BuildExport(track, MeshQuality.Low, double.PositiveInfinity, $"S{k++}_");
        foreach (var m in stub) m.Color = color;
        meshes.AddRange(stub);
    }
}

// A start so the map can be driven, south of the columns.
var spawn = new Track();
spawn.Points.Add(new TrackPoint(new Vec3d(16, 0.5, -10), new Vec3d(0, 0, 8)) { Waypoint = Waypoint.Start });
spawn.Points.Add(new TrackPoint(new Vec3d(16, 0.5, 20), new Vec3d(0, 0, 8)) { Waypoint = Waypoint.Finish });
spawn.Segments.Add(new TrackSegment());
meshes.AddRange(MeshQualities.BuildExport(spawn, MeshQuality.Low, double.PositiveInfinity, "Start_").Meshes);

var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
var output = Path.Combine(userDir, "Maps", "TMSpline", "BlockProbe.Map.Gbx");
MapExporter.Export(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TmSpline.Server", "Templates", "Empty48.Map.Gbx"),
    output, items, new MapExportOptions { MapName = "TMSpline BlockProbe", ItemFolder = folder, Blocks = blocks }, Console.WriteLine);
Console.WriteLine($"Wrote {output} ({items.Count} items, {blocks.Count} blocks)");
