using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Bumpiness of the driving surface: how far it stands out of its plain shape (along its normal, meters) at a distance
/// along the track and a position across the road. Real geometry, so cars feel it. Sources: the segments' patterns
/// (waves along, ridges across, smooth noise) and placed bump objects (cos² humps or dips). Everything fades to zero
/// towards a pattern segment's ends (no step where the next segment is plain) and towards the road's edges (the curbs and
/// sides meet the surface where they always did), and the road stays flat around the gates (start, checkpoints, finish):
/// the car spawns and respawns on the plain surface there, not under a bump.
/// </summary>
public sealed class RoadBumps
{
    private readonly Track _track;
    private readonly double[] _starts;
    private readonly List<(double Distance, TrackObject Object)> _objects;
    private readonly List<double> _gates;

    /// <summary>Width of the fade towards the road's edges, meters.</summary>
    public const double EdgeFade = 1.5;

    /// <summary>The road is flat this far before and after a gate, then the bumps grow back over the same length.</summary>
    public const double GateFlat = 8;

    public RoadBumps(Track track, double[] segmentStarts)
    {
        _track = track;
        _starts = segmentStarts;
        _objects = track.Objects
            .Where(o => o.Kind == TrackObjectKind.Bump && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1)
            .Select(o => (segmentStarts[o.Segment] + o.At * (segmentStarts[o.Segment + 1] - segmentStarts[o.Segment]), o))
            .ToList();
        _gates = WaypointBuilder.GateDistances(track, segmentStarts);
    }

    /// <summary>1 away from the gates, 0 within <see cref="GateFlat"/> of one, smoothly in between (closed tracks wrap).</summary>
    private double GateFade(double distance)
    {
        var f = 1.0;
        var total = _starts[^1];
        foreach (var g in _gates)
        {
            var d = Math.Abs(distance - g);
            if (_track.Closed && total > 0) d = Math.Min(d, total - d);
            f = Math.Min(f, SmoothStep(Math.Clamp((d - GateFlat) / GateFlat, 0, 1)));
        }
        return f;
    }

    /// <summary>Whether anything bumps the road at all (most tracks: nothing, no extra cost).</summary>
    public bool Any => _objects.Count > 0 || _track.Segments.Any(s => s.Bumps != BumpPattern.None);

    /// <summary>
    /// Height out of the surface at `distance` (along the track) and `across` (meters from the center along the surface,
    /// positive = right), on a road `half` wide each side (to the surface's edge) in `segment`.
    /// </summary>
    public double At(int segment, double distance, double across, double half)
    {
        var edge = SmoothStep(Math.Clamp((half - Math.Abs(across)) / EdgeFade, 0, 1)) * GateFade(distance);
        if (edge <= 0) return 0;
        var h = 0.0;
        if (segment >= 0 && segment < _track.Segments.Count && _track.Segments[segment] is { Bumps: not BumpPattern.None } seg)
        {
            var (start, end) = (_starts[segment], _starts[segment + 1]);
            var size = Math.Max(0.5, seg.BumpSize);
            // Fading in and out over a wavelength (at most half the segment), so the next segment starts flat.
            var fade = Math.Max(1e-6, Math.Min(Math.Max(size, 4), (end - start) / 2));
            var ends = SmoothStep(Math.Clamp(Math.Min(distance - start, end - distance) / fade, 0, 1));
            var along = distance - start;
            var shape = seg.Bumps switch
            {
                BumpPattern.Waves => 0.5 - 0.5 * Math.Cos(2 * Math.PI * along / size),
                BumpPattern.Ridges => 0.5 - 0.5 * Math.Cos(2 * Math.PI * (across + half) / size),
                BumpPattern.Noise => Noise(along / size, across / size, segment),
                _ => 0,
            };
            h += seg.BumpHeight * shape * ends;
        }
        foreach (var (d, o) in _objects)
        {
            var u = (distance - d) / Math.Max(0.1, o.Length / 2);
            var v = (across - o.Offset) / Math.Max(0.1, o.Width / 2);
            if (Math.Abs(u) >= 1 || Math.Abs(v) >= 1) continue;
            double shape;
            if (o.Shape == ObjectShape.Round)
            {
                var r = Math.Sqrt(u * u + v * v);
                if (r >= 1) continue;
                shape = Sq(Math.Cos(Math.PI * r / 2));
            }
            else shape = Sq(Math.Cos(Math.PI * u / 2)) * Sq(Math.Cos(Math.PI * v / 2));
            h += o.Height * shape;
        }
        return h * edge;
    }

    /// <summary>
    /// Meters between cross-sections (and columns) the bumps need in a segment, or null where it has none: its pattern's
    /// detail, finer where a bump object is smaller than that (a few per bump).
    /// </summary>
    public double? Detail(int segment, bool alongOnly)
    {
        double? detail = null;
        if (_track.Segments[segment] is { Bumps: not BumpPattern.None } seg && !(alongOnly && seg.Bumps == BumpPattern.Ridges)
            && !(!alongOnly && seg.Bumps == BumpPattern.Waves))
            detail = Math.Max(0.25, seg.BumpDetail);
        foreach (var (_, o) in _objects)
            if (o.Segment == segment)
            {
                var size = alongOnly ? o.Length : o.Width;
                var d = Math.Max(0.25, Math.Min(_track.Segments[segment].BumpDetail, size / 6));
                detail = Math.Min(detail ?? double.MaxValue, d);
            }
        return detail;
    }

    /// <summary>Spans along the track that bump objects cover (they need cross-sections there), with their detail.</summary>
    public IEnumerable<(double Start, double End, double Detail)> ObjectSpans() =>
        _objects.Select(x => (x.Distance - x.Object.Length / 2, x.Distance + x.Object.Length / 2,
            Math.Max(0.25, Math.Min(_track.Segments[x.Object.Segment].BumpDetail, x.Object.Length / 6))));

    /// <summary>Smooth value noise in 0..1 (two octaves), one field per segment.</summary>
    private static double Noise(double x, double y, int seed) =>
        (Value(x, y, seed) * 2 + Value(x * 2.1 + 17.3, y * 2.1 - 5.7, seed + 911)) / 3;

    private static double Value(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double fx = SmoothStep(x - xi), fy = SmoothStep(y - yi);
        double Corner(int i, int j) => Hash(xi + i, yi + j, seed);
        var a = Corner(0, 0) + (Corner(1, 0) - Corner(0, 0)) * fx;
        var b = Corner(0, 1) + (Corner(1, 1) - Corner(0, 1)) * fx;
        return a + (b - a) * fy;
    }

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    private static double SmoothStep(double x) => x * x * (3 - 2 * x);
    private static double Sq(double x) => x * x;
}
