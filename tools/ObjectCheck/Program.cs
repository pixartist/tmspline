using System.Numerics;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Builds a track with holes, gaps and pillars and reports open edges of the welded road mesh (holes and gaps must
// leave the slab closed), triangle counts and placements. Usage: ObjectCheck <track.json> [--coarse]
if (args[0] == "--sizes") { Sizes.Run(args[1], Enum.Parse<MeshQuality>(args[2], true)); return; }
var track = TrackJson.Parse(File.ReadAllText(args[0]));
var coarse = args.Contains("--coarse");
var seg = args.SkipWhile(a => a != "--seg").Skip(1).Select(int.Parse).FirstOrDefault();
if (track.Objects.Count == 0 && !args.Contains("--none"))
{
    track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Hole, Segment = seg, At = 0.3, Offset = 2, Shape = ObjectShape.Round, Length = 5, Width = 5 });
    track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Hole, Segment = seg, At = 0.6, Offset = -3, Shape = ObjectShape.Rect, Length = 3, Width = 6 });
    track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Gap, Segment = seg, At = 0.8, Length = 10 });
    track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Pillar, Segment = seg, At = 0.5, Shape = ObjectShape.Round, Length = 2, Width = 2 });
    track.Objects.Add(new TrackObject { Kind = TrackObjectKind.Pillar, Segment = seg, At = 0.45, Offset = 4, Shape = ObjectShape.Rect, Length = 2, Width = 3 });
}
if (args.Contains("--no-underside")) foreach (var sg in track.Segments) sg.Underside = false;
var rings = TrackSampler.Sample(track, coarse ? new SamplerSettings { MaxStep = 8, MaxAngleDegrees = 8 } : null);
var settings = new RoadMeshSettings { MaxChunkLength = 64, WorldSpace = true };
if (coarse) { settings.ColumnWidth = 8; settings.MaxColumnAngleDegrees = 22.5; }
var chunks = RoadMeshBuilder.Build(track, rings, Vec3d.Zero, "Road", settings);
var starts = TrackSampler.SegmentStarts(track);
Console.WriteLine($"{chunks.Count} chunks, {chunks.Sum(c => c.Triangles.Count)} triangles, length {rings[^1].Distance:0.0}");
foreach (var g in TrackObjects.GapRanges(track, starts))
    Console.WriteLine($"gap {g.Start:0.00}..{g.End:0.00}, rings at ends: {rings.Any(r => Math.Abs(r.Distance - g.Start) < 1e-6)} {rings.Any(r => Math.Abs(r.Distance - g.End) < 1e-6)}");
foreach (var p in TrackObjects.Placements(track, rings, starts, settings))
    Console.WriteLine($"placement {p.Position} d={p.Distance:0.00} s={p.Across:0.00} placed={p.Placed}");
var pillars = PillarBuilder.Build(track, rings, starts, Vec3d.Zero, settings);
Console.WriteLine($"pillars: {pillars?.Triangles.Count ?? 0} triangles");

// Weld all chunks by position and count edges used by one triangle only (open edges), and wrongly wound pairs.
var weld = new Dictionary<(long, long, long), int>();
int Id(Vector3 v) { var k = ((long)Math.Round(v.X * 1000), (long)Math.Round(v.Y * 1000), (long)Math.Round(v.Z * 1000)); if (!weld.TryGetValue(k, out var i)) weld[k] = i = weld.Count; return i; }
var edges = new Dictionary<(int, int), int>();
var points = new Dictionary<int, Vector3>();
foreach (var c in chunks)
    foreach (var t in c.Triangles)
    {
        var ids = new[] { t.A, t.B, t.C }.Select(i => { var id = Id(c.Positions[i]); points[id] = c.Positions[i]; return id; }).ToArray();
        for (var e = 0; e < 3; e++)
        {
            var key = (ids[e], ids[(e + 1) % 3]);
            edges[key] = edges.GetValueOrDefault(key) + 1;
        }
    }
var open = edges.Keys.Where(k => !edges.ContainsKey((k.Item2, k.Item1))).ToList();
// T-junctions (an edge matched by several shorter collinear edges the other way round) are not cracks.
bool Covered((int, int) e)
{
    var (a, b) = (points[e.Item1], points[e.Item2]);
    foreach (var f in new[] { 0.2f, 0.5f, 0.8f })
    {
        var x = a + (b - a) * f;
        var hit = edges.Keys.Where(o => (points[o.Item1] - x).Length() < 70).Any(o =>
        {
            var (c, d) = (points[o.Item1], points[o.Item2]);
            if (Vector3.Dot(d - c, b - a) >= 0) return false;
            var t = Math.Clamp(Vector3.Dot(x - c, d - c) / (d - c).LengthSquared(), 0, 1);
            return (c + (d - c) * t - x).Length() < 2e-3;
        });
        if (!hit) return false;
    }
    return true;
}
var tj = open.Count(Covered);
Console.WriteLine($"open (unmatched directed) edges: {open.Count}, of them T-junctions: {tj}");
open = open.Where(e => !Covered(e)).ToList();
foreach (var g in open.GroupBy(e => Math.Round(Distance(points[e.Item1]) / 4) * 4).OrderBy(g => g.Key))
    Console.WriteLine($"  near distance ~{g.Key}: {g.Count()}");

double Distance(Vector3 p)
{
    var best = rings.MinBy(r => (r.Position - new Vec3d(p.X, p.Y, p.Z)).Length)!;
    return best.Distance;
}
if (args.Contains("--detail"))
    foreach (var e in open.Where(e => Distance(points[e.Item1]) < 60))
        Console.WriteLine($"  open {points[e.Item1]} -> {points[e.Item2]} len {(points[e.Item2] - points[e.Item1]).Length():0.0000}");
if (args.Contains("--tri"))
{
    var target = new Vector3(-2f, 20f, 21.7f);
    Console.WriteLine("rings near: " + string.Join(", ", rings.Where(r => Math.Abs(r.Distance - 21.7) < 3).Select(r => r.Distance.ToString("0.000"))));
    foreach (var c in chunks)
        foreach (var t in c.Triangles)
        {
            var ps = new[] { t.A, t.B, t.C }.Select(i => c.Positions[i]).ToArray();
            if (ps.Any(p => (p - target).Length() < 0.05))
                Console.WriteLine($"  {c.Name} {c.Materials[t.Material].Name}: {string.Join(" ", ps.Select(p => $"({p.X:0.000},{p.Y:0.000},{p.Z:0.000})"))}");
        }
}
foreach (var h in TrackObjects.Holes(track, rings, starts))
    Console.WriteLine($"hole outline d {h.MinD:0.00}..{h.MaxD:0.00} s {h.MinS:0.00}..{h.MaxS:0.00}");
