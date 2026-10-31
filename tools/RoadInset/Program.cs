using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Road texture inset candidates side by side: candidate k leaves out k-1 steps of the texture on each side and has k
// pillars standing at its start, so it can be told apart in game. Two rows: a 16 m road in front, a 40 m road behind
// (the same inset should look the same at both widths). Usage: RoadInset [surface ...] (default RoadTech).
const string userDir = @"I:\Dokumente\Trackmania";
const string folder = @"TMSpline\RoadInset";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);
var insets = new[] { 0.0, 0.03, 0.06, 0.09, 0.12, 0.15 };
var surfaces = args.Length > 0 ? args.Select(Enum.Parse<Surface>).ToArray() : new[] { Surface.RoadTech };

var meshes = new List<MeshChunk>();
var name = 0;
foreach (var (surface, si) in surfaces.Select((s, i) => (s, i)))
    foreach (var (width, row) in new[] { (16.0, 0), (40.0, 1) })
        for (var k = 0; k < insets.Length; k++)
        {
            // Rows of strips along +Z; each surface gets its own pair of rows further on.
            var x = (k - (insets.Length - 1) / 2.0) * 52;
            var z = -300 + (si * 2 + row) * 130;
            var track = new Track();
            track.Points.Add(new TrackPoint(new Vec3d(x, 10, z), new Vec3d(0, 0, 24), width: width));
            track.Points.Add(new TrackPoint(new Vec3d(x, 10, z + 96), new Vec3d(0, 0, 24), width: width));
            track.Segments.Add(new TrackSegment { Surface = surface });
            // Candidate number = pillars across the start of the strip.
            for (var p = 0; p <= k; p++)
                track.Objects.Add(new TrackObject
                {
                    Kind = TrackObjectKind.Pillar, Segment = 0, At = 0.06, Offset = (p - k / 2.0) * 2.2, Length = 1, Width = 1, Height = 2,
                });
            var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, RoadTextureInset = insets[k] };
            var rings = TrackSampler.Sample(track);
            meshes.AddRange(RoadMeshBuilder.Build(track, rings, Vec3d.Zero, $"Road{name}", settings));
            if (PillarBuilder.Build(track, rings, TrackSampler.SegmentStarts(track), Vec3d.Zero, settings, $"Pillars{name}") is { } pillars)
                meshes.Add(pillars);
            name++;
            if (row == 0 && si == 0)
                Console.WriteLine($"candidate {k + 1} ({k + 1} pillars): inset {insets[k] * 100:0}% of the texture per side");
        }

// A start so the map can be driven: a short gate-only track in front of the rows.
var start = new Track();
start.Points.Add(new TrackPoint(new Vec3d(0, 10, -400), new Vec3d(0, 0, 16)));
start.Points.Add(new TrackPoint(new Vec3d(0, 10, -340), new Vec3d(0, 0, 16)));
start.Segments.Add(new TrackSegment());
var startRings = TrackSampler.Sample(start);
meshes.AddRange(RoadMeshBuilder.Build(start, startRings, Vec3d.Zero, "Approach", new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity }));
meshes.AddRange(WaypointBuilder.Build(start, startRings, Vec3d.Zero, new RoadMeshSettings(), "Gate"));

var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer);
var output = Path.Combine(userDir, "Maps", "TMSpline", "RoadInset.Map.Gbx");
MapExporter.Export(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TmSpline.Server", "Templates", "Empty48.Map.Gbx"),
    output, items, new MapExportOptions { MapName = "TMSpline RoadInset", ItemFolder = folder });
Console.WriteLine($"Wrote {output} ({items.Count} items)");
