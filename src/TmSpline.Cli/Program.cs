using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Converts an editor track (.json) into a Trackmania map using the local NadeoImporter.
//
//   TmSpline.Cli <track.json> [--template <map.Map.Gbx>] [--out <map.Map.Gbx>] [--chunk <meters> | --single]
//                             [--game <TM dir>] [--user <TM user dir>] [--author <account login>] [--quality low|normal|fine|high|ultra]
//   TmSpline.Cli --sample <track.json>      writes the spike test track

if (args.Length == 0)
{
    Console.WriteLine("usage: TmSpline.Cli <track.json> [--template map] [--out map] [--chunk m | --single] [--game dir] [--user dir]");
    Console.WriteLine("       TmSpline.Cli --sample <track.json>");
    return 1;
}

string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (args[0] == "--sample")
{
    File.WriteAllText(args[1], TrackJson.Serialize(SampleTrack(), "TMSpline Sample"));
    Console.WriteLine($"Wrote {args[1]}");
    return 0;
}

var gameDir = Opt("--game") ?? @"E:\SteamLibrary\steamapps\common\Trackmania";
var userDir = Opt("--user") ?? @"I:\Dokumente\Trackmania";

var dto = TrackJson.ParseDto(File.ReadAllText(args[0]));
// Default: the neutral empty template that ships with the server.
var template = Opt("--template") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TmSpline.Server", "Templates", "Empty48.Map.Gbx");
var output = Opt("--out") ?? Path.Combine(userDir, "Maps", "TMSpline", TrackExporter.SafeName(dto.Name) + ".Map.Gbx");
var chunkLength = args.Contains("--single") ? double.PositiveInfinity : Opt("--chunk") is { } c ? double.Parse(c) : 64;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await using (var templateStream = File.OpenRead(template))
await using (var outputStream = File.Create(output))
{
    // The importer next to Trackmania.exe is already configured for the default user folder.
    var importer = new NativeImporter(gameDir, isolated: Opt("--user") is not null);
    var result = await TrackExporter.ExportAsync(new TrackExportRequest
    {
        Map = TrackJson.ToMap(dto),
        Name = dto.Name,
        AuthorLogin = Opt("--author"),
        MaxChunkLength = chunkLength,
        Quality = Opt("--quality") is { } q ? Enum.Parse<MeshQuality>(q, ignoreCase: true) : MeshQualities.Default,
    }, userDir, importer, templateStream, outputStream, Console.WriteLine);
    Console.WriteLine($"{result.ItemCount} items in {result.ItemFolder}");
}
Console.WriteLine($"Wrote {output}");
return 0;

static Track SampleTrack()
{
    var t = new Track();
    // Straight along +Z, a right turn (towards -X) climbing 8 m with banking, a wider dirt straight,
    // and a magnet section that banks up to 70 degrees and back. Starts 20 m above the ground.
    var lift = new Vec3d(0, 20, 0);
    t.Points.Add(new TrackPoint(lift + new Vec3d(0, 0, 0), new Vec3d(0, 0, 20)));
    t.Points.Add(new TrackPoint(lift + new Vec3d(0, 0, 64), new Vec3d(0, 0, 30)));
    t.Points.Add(new TrackPoint(lift + new Vec3d(-64, 8, 128), new Vec3d(-30, 0, 0), bankDegrees: 20));
    t.Points.Add(new TrackPoint(lift + new Vec3d(-160, 8, 128), new Vec3d(-30, 0, 0), width: 24));
    t.Points.Add(new TrackPoint(lift + new Vec3d(-240, 8, 128), new Vec3d(-30, 0, 0), bankDegrees: 70));
    t.Points.Add(new TrackPoint(lift + new Vec3d(-320, 8, 128), new Vec3d(-30, 0, 0)));

    t.Segments.Add(new TrackSegment { Surface = Surface.RoadTech });
    t.Segments.Add(new TrackSegment { Surface = Surface.RoadTech });
    t.Segments.Add(new TrackSegment { Surface = Surface.RoadDirt });
    t.Segments.Add(new TrackSegment { Surface = Surface.Magnet });
    t.Segments.Add(new TrackSegment { Surface = Surface.Magnet });
    return t;
}
