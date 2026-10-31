using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Measures how smoothly the road rolls: the twist rate (deg/m) relative to a rotation-minimizing transport of
// the previous ring's frame. Kinks show up as jumps in the twist rate between neighbouring rings.
if (args.Contains("--trace")) { Trace.Run(args[0], int.Parse(args[^2]), int.Parse(args[^1])); return; }
if (args.Contains("--ups")) { PointUps.Run(args[0]); return; }
if (args.Contains("--widths")) { var wt = TrackJson.Parse(File.ReadAllText(args[0])); Console.WriteLine("point widths: " + string.Join(", ", wt.Points.Select(p => p.Width))); var wr = TrackSampler.Sample(wt); Console.WriteLine("ring widths: " + string.Join(" ", wr.Where((_, i) => i % 6 == 0).Select(r => r.Width.ToString("0.0")))); return; }
if (args.Contains("--decals")) { Decals.Run(args[0]); return; }
if (args.Contains("--chunks")) { Chunks.Run(args[0], int.Parse(args[^1])); return; }
var track = TrackJson.Parse(File.ReadAllText(args[0]));
var rings = TrackSampler.Sample(track);
double maxRate = 0, maxJump = 0, jumpAt = 0;
double? prevRate = null;
for (var i = 1; i < rings.Count; i++)
{
    var a = rings[i - 1];
    var b = rings[i];
    var ds = b.Distance - a.Distance;
    if (ds < 1e-6) continue;
    // Transport a.Right to b by projecting it onto b's normal plane (good enough at ~2 m steps).
    var carried = (a.Right - b.Tangent * Vec3d.Dot(a.Right, b.Tangent)).Normalized();
    var angle = Math.Atan2(Vec3d.Dot(Vec3d.Cross(carried, b.Right), b.Tangent), Vec3d.Dot(carried, b.Right)) * 180 / Math.PI;
    var rate = angle / ds;
    maxRate = Math.Max(maxRate, Math.Abs(rate));
    if (prevRate is { } p && Math.Abs(rate - p) > maxJump) { maxJump = Math.Abs(rate - p); jumpAt = b.Distance; }
    prevRate = rate;
    if (args.Contains("--profile") && i % 3 == 0)
        Console.WriteLine($"{b.Distance,7:0.0} m  seg {b.Segment}  pitch {Math.Asin(Math.Clamp(b.Tangent.Y, -1, 1)) * 180 / Math.PI,6:0}°  twist {rate,7:0.00}°/m");
}
Console.WriteLine($"rings {rings.Count}, max twist rate {maxRate:0.00}°/m, max twist-rate jump {maxJump:0.00}°/m at {jumpAt:0} m");
