using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Ring-by-ring trace of position, heading, pitch and road up for the given segments.
static class Trace
{
    public static void Run(string path, int fromSegment, int toSegment)
    {
        var track = TrackJson.Parse(File.ReadAllText(path));
        var rings = TrackSampler.Sample(track);
        for (var i = 0; i < rings.Count; i++)
        {
            var r = rings[i];
            if (r.Segment < fromSegment || r.Segment > toSegment || i % 2 != 0) continue;
            var heading = Math.Atan2(r.Tangent.X, r.Tangent.Z) * 180 / Math.PI;
            var pitch = Math.Asin(Math.Clamp(r.Tangent.Y, -1, 1)) * 180 / Math.PI;
            Console.WriteLine($"seg {r.Segment,2}{(r.IsVertex ? "*" : " ")} pos {r.Position}  heading {heading,5:0}°  pitch {pitch,4:0}°  up {r.Up}");
        }
    }
}
