using System.Text.Json;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Free blocks (tools/BlockProbe --free): RoadTechRampHigh in a row along +X at z = 192..224 m, all with their origin
// corner on the row's start line, written as a map and as a .track.json that opens the same layout in the editor.
//   1  grid block, cell (-6, 0, 6), East
//   2  the same as a free block (origin and yaw the editor computes): must look exactly like 1, one cell to the right
//   3  free, yaw 30°            4  free, pitch 20° (8 m up)    5  free, roll 30° (8 m up)
//   6  free, yaw 45° + pitch 20° + roll 30° (16 m up): shows the rotation order
static class FreeProbe
{
    public static async Task Run()
    {
        const string userDir = @"I:\Dokumente\Trackmania";
        const string folder = @"TMSpline\BlockProbeFree";
        var blocks = new List<VanillaBlock>
        {
            new() { Name = "RoadTechRampHigh", X = -6, Y = 0, Z = 6, Dir = 1 },
            Free(-96 + 32, 0, 192, -90, 0, 0),
            Free(0, 0, 192, 30, 0, 0),
            Free(64, 8, 192, 0, 20, 0),
            Free(128, 8, 192, 0, 0, 30),
            Free(192, 16, 192, 45, 20, 30),
        };

        var spawn = new Track();
        spawn.Points.Add(new TrackPoint(new Vec3d(16, 0.5, 100), new Vec3d(0, 0, 8)) { Waypoint = Waypoint.Start });
        spawn.Points.Add(new TrackPoint(new Vec3d(16, 0.5, 140), new Vec3d(0, 0, 8)) { Waypoint = Waypoint.Finish });
        spawn.Segments.Add(new TrackSegment());
        var meshes = MeshQualities.BuildExport(spawn, MeshQuality.Low, double.PositiveInfinity, "Start_").Meshes;

        var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);
        var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
        var output = Path.Combine(userDir, "Maps", "TMSpline", "BlockProbeFree.Map.Gbx");
        MapExporter.Export(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TmSpline.Server", "Templates", "Empty48.Map.Gbx"),
            output, items, new MapExportOptions { MapName = "TMSpline BlockProbeFree", ItemFolder = folder, Blocks = blocks }, Console.WriteLine);
        Console.WriteLine($"Wrote {output}");

        // The same layout for the editor (Open… this file).
        var point = (double z, string waypoint) => new
        {
            position = new[] { 16, 0.5, z }, handleIn = new[] { 0, 0, -8.0 }, handleOut = new[] { 0, 0, 8.0 },
            bank = 0, width = 16, curve = 0, flat = 0, sausage = 0, borderHeight = 1, waypoint,
        };
        var doc = new
        {
            version = 2, name = "BlockProbeFree", laps = 3,
            tracks = new[] { new { closed = false, points = new[] { point(100, "Start"), point(140, "Finish") }, segments = new[] { new { surface = "RoadTech" } }, objects = Array.Empty<object>() } },
            blocks = blocks.Select(b => b.Free
                ? (object)new { name = b.Name, x = 0, y = 0, z = 0, dir = 0, free = true, position = new[] { b.Position.X, b.Position.Y, b.Position.Z }, yaw = b.Yaw, pitch = b.Pitch, roll = b.Roll }
                : new { name = b.Name, x = b.X, y = b.Y, z = b.Z, dir = b.Dir }),
        };
        var json = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BlockProbeFree.track.json");
        File.WriteAllText(json, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote {Path.GetFullPath(json)}");
    }

    static VanillaBlock Free(double x, double y, double z, double yaw, double pitch, double roll) =>
        new() { Name = "RoadTechRampHigh", Free = true, Position = new Vec3d(x, y, z), Yaw = yaw, Pitch = pitch, Roll = roll };
}
