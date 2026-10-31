using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Prints the road frame at every spline point: pitch, how horizontal the tangent is, and the road normal's Y
// (negative = upside down).
static class PointUps
{
    public static void Run(string path)
    {
        var track = TrackJson.Parse(File.ReadAllText(path));
        var rings = TrackSampler.Sample(track);
        var seen = new HashSet<int>();
        foreach (var r in rings.Where(r => r.IsVertex))
        {
            if (!seen.Add(r.Segment)) continue;
            var horiz = Math.Sqrt(r.Tangent.X * r.Tangent.X + r.Tangent.Z * r.Tangent.Z);
            Console.WriteLine($"point {r.Segment,2}: pitch {Math.Asin(Math.Clamp(r.Tangent.Y, -1, 1)) * 180 / Math.PI,5:0}°  horizontal {horiz:0.00}  up.Y {r.Up.Y,6:0.00}{(r.Up.Y < 0 ? "  <- upside down" : "")}");
        }
    }
}
