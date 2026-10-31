using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Prints the decal V range per boost segment (should be 0 .. whole number of pads).
static class Decals
{
    public static void Run(string path)
    {
        var track = TrackJson.Parse(File.ReadAllText(path));
        track.Segments[1].Effect = Effect.Turbo;
        track.Segments[2].Effect = Effect.Turbo2;
        var chunks = RoadMeshBuilder.Build(track, TrackSampler.Sample(track), Vec3d.Zero, "C", new RoadMeshSettings());
        var bySegment = new SortedDictionary<int, (float Min, float Max)>();
        foreach (var c in chunks)
            foreach (var t in c.Triangles.Where(t => c.Materials[t.Material].Name.StartsWith("Decal")))
                foreach (var v in new[] { t.A, t.B, t.C })
                {
                    var uv = c.Uv0[v].Y;
                    bySegment[t.Segment] = bySegment.TryGetValue(t.Segment, out var r) ? (Math.Min(r.Min, uv), Math.Max(r.Max, uv)) : (uv, uv);
                }
        foreach (var (s, r) in bySegment)
            Console.WriteLine($"segment {s} ({track.SegmentStart(s).Width}..{track.SegmentEnd(s).Width} m wide): decal V {r.Min:0.###} .. {r.Max:0.###}");
    }
}
