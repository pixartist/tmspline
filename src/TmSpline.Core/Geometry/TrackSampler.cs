using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>A cross-section sample along the track.</summary>
public sealed class Ring
{
    public Vec3d Position;
    public Vec3d Tangent;
    /// <summary>Lateral axis after banking.</summary>
    public Vec3d Right;
    /// <summary>Road normal after banking.</summary>
    public Vec3d Up;
    /// <summary>Distance from the track start.</summary>
    public double Distance;
    /// <summary>Segment of the interval that starts at this ring.</summary>
    public int Segment;
    /// <summary>Road width at this ring (blended between points), measured along the surface.</summary>
    public double Width;
    /// <summary>Cross-section bend at this ring in radians (see TrackPoint.Curve).</summary>
    public double Curve;
    /// <summary>Flat middle share of the width at this ring (see TrackPoint.Flat).</summary>
    public double Flat;
    /// <summary>Sausageness at this ring (see TrackPoint.Sausage).</summary>
    public double Sausage;
    /// <summary>Border height at this ring in meters.</summary>
    public double BorderHeight;
    /// <summary>
    /// How far the borders have grown here (0..1): they rise out of the road's edge where they start and sink back where
    /// they end, so they never end in an open cross-section: blended from point to point with borders on the points,
    /// over <see cref="SamplerSettings.BorderRampLength"/> with borders on the segments.
    /// </summary>
    public double BorderScale = 1;
    /// <summary>True if this ring lies exactly on a spline vertex.</summary>
    public bool IsVertex;
    /// <summary>Arc-length fraction of this ring within its segment (0..1).</summary>
    public double SegmentU;

    public Ring Clone() => (Ring)MemberwiseClone();
}

public sealed class SamplerSettings
{
    /// <summary>
    /// Maximum distance between rings, in meters. Curves, banking and shape changes add rings on their own (see the
    /// other limits), so this only matters on straight, unchanging road, where long strips cost nothing in quality.
    /// </summary>
    public double MaxStep { get; set; } = 16.0;
    /// <summary>Maximum change of direction between rings, in degrees.</summary>
    public double MaxAngleDegrees { get; set; } = 2.0;
    /// <summary>Maximum change of road width between rings, in meters.</summary>
    public double MaxWidthStep { get; set; } = 2.0;
    /// <summary>Maximum change of the cross-section bend (Curve) between rings, in degrees.</summary>
    public double MaxCurveStepDegrees { get; set; } = 10.0;
    /// <summary>Maximum change of the flat share and sausageness between rings (0..1 scale).</summary>
    public double MaxShareStep { get; set; } = 0.1;
    /// <summary>Maximum change of the border height between rings, in meters.</summary>
    public double MaxBorderStep { get; set; } = 0.25;
    /// <summary>
    /// Length over which borders grow from nothing where they start (and shrink where they end), in meters; only for
    /// borders set on the segments (on the points they blend over whole segments).
    /// </summary>
    public double BorderRampLength { get; set; } = 12.0;
}

public static class TrackSampler
{
    public static List<Ring> Sample(Track track, SamplerSettings? settings = null)
    {
        settings ??= new SamplerSettings();
        track.SyncSegments();
        var segCount = track.SegmentCount;
        if (segCount == 0)
            return new List<Ring>();

        var tables = new ArcTable[segCount];
        for (var s = 0; s < segCount; s++)
            tables[s] = new ArcTable(Bezier(track, s));

        var starts = Starts(tables);
        // Gaps need rings exactly at their ends, so the road stops and starts where the mapper put them.
        var cuts = TrackObjects.GapRanges(track, starts).SelectMany(g => new[] { g.Start, g.End }).ToList();
        // Border ramps need rings along them, or they would be one long wedge (borders on the points blend over whole
        // segments instead, see RingCount).
        var ramps = track.PointBorders ? new List<BorderRun>() : BorderRuns(track, starts, settings);
        cuts.AddRange(ramps.SelectMany(r => r.RingDistances(starts[^1], track.Closed)));
        // Bump objects: cross-sections over their length, a few per bump (segment patterns raise the ring count instead).
        foreach (var (from, to, detail) in new RoadBumps(track, starts).ObjectSpans())
            for (var d = from; d <= to + 1e-6; d += detail)
                if (d > 0 && d < starts[^1]) cuts.Add(d);

        var rings = new List<Ring>();
        var distance = 0.0;
        for (var s = 0; s < segCount; s++)
        {
            var table = tables[s];
            var isLastSegment = s == segCount - 1;
            // Each segment emits its start ring and interior rings; the end ring is the next segment's start.
            // The very last ring of the track is emitted explicitly below.
            foreach (var u in RingFractions(track, s, table, settings, cuts.Where(d => d > distance && d < distance + table.Length)
                         .Select(d => (d - distance) / table.Length)))
                rings.Add(MakeRing(track, tables, s, u, distance + u * table.Length, settings));
            distance += table.Length;
            if (isLastSegment)
            {
                var end = MakeRing(track, tables, s, 1.0, distance, settings);
                end.Segment = s;
                rings.Add(end);
            }
        }

        ComputeFrames(track, rings);
        foreach (var ring in rings)
            ring.BorderScale = track.PointBorders
                ? PointBorderScale(track, ring.Segment, ring.SegmentU)
                : ramps.Select(r => r.ScaleAt(ring.Distance, starts[^1], track.Closed)).DefaultIfEmpty(1).Min();
        return rings;
    }

    /// <summary>
    /// Borders set on the points blend from point to point like the width: full at points with borders, gone at points
    /// without, smoothly in between. A single point without borders fades them out towards it and back in after it.
    /// </summary>
    private static double PointBorderScale(Track track, int segment, double u)
    {
        static double On(TrackPoint p) => p.Borders == true ? 1 : 0;
        return Lerp(On(track.SegmentStart(segment)), On(track.SegmentEnd(segment)), SmoothStep(u));
    }

    /// <summary>
    /// A stretch of consecutive segments with borders. Where it starts after (or ends before) a segment without borders, the
    /// borders grow (shrink) over <see cref="Ramp"/> meters; at an open track's own start or end they stay full height.
    /// </summary>
    private sealed record BorderRun(double From, double Length, bool RampStart, bool RampEnd, double Ramp)
    {
        private const int Steps = 6;

        /// <summary>Position in the run (0..Length), or null outside it; on closed tracks runs wrap round.</summary>
        private double? Along(double d, double total, bool closed)
        {
            var rel = d - From;
            if (closed) rel = ((rel % total) + total) % total;
            return rel >= -1e-6 && rel <= Length + 1e-6 ? rel : null;
        }

        public double ScaleAt(double d, double total, bool closed)
        {
            if (Along(d, total, closed) is not { } rel) return 1;
            var f = 1.0;
            if (RampStart) f = Math.Min(f, SmoothStep(Math.Clamp(rel / Ramp, 0, 1)));
            if (RampEnd) f = Math.Min(f, SmoothStep(Math.Clamp((Length - rel) / Ramp, 0, 1)));
            return f;
        }

        public IEnumerable<double> RingDistances(double total, bool closed)
        {
            double Wrap(double d) => closed ? ((d % total) + total) % total : d;
            for (var i = 1; i <= Steps; i++)
            {
                if (RampStart) yield return Wrap(From + Ramp * i / Steps);
                if (RampEnd) yield return Wrap(From + Length - Ramp * i / Steps);
            }
        }
    }

    /// <summary>The stretches of the track with borders (see <see cref="BorderRun"/>).</summary>
    private static List<BorderRun> BorderRuns(Track track, double[] starts, SamplerSettings settings)
    {
        var n = track.SegmentCount;
        var runs = new List<BorderRun>();
        bool Borders(int s) => track.Segments[((s % n) + n) % n].Borders;
        // All with or all without borders: nothing starts or ends.
        var without = Enumerable.Range(0, n).Where(s => !Borders(s)).ToList();
        if (without.Count == 0 || without.Count == n) return runs;
        // Closed tracks: scan from just after a segment without borders, so no run is split at the seam.
        var first = track.Closed ? without[0] + 1 : 0;
        for (var i = 0; i < n; )
        {
            if (!Borders(first + i)) { i++; continue; }
            var j = i;
            while (j < n && Borders(first + j)) j++;
            var length = 0.0;
            for (var k = i; k < j; k++)
            {
                var seg = (first + k) % n;
                length += starts[seg + 1] - starts[seg];
            }
            var (a, b) = ((first + i) % n, (first + j - 1) % n);
            // Borders on the points: they fade out towards an end point without them (a bordered point at an open
            // track's end keeps them full). On the segments: they fade where a segment without borders follows.
            var rampStart = track.PointBorders ? track.SegmentStart(a).Borders != true : track.Closed || a > 0;
            var rampEnd = track.PointBorders ? track.SegmentEnd(b).Borders != true : track.Closed || b < n - 1;
            var ramp = Math.Min(settings.BorderRampLength, rampStart && rampEnd ? length / 2 : length);
            if (ramp > 1e-6) runs.Add(new BorderRun(starts[a], length, rampStart, rampEnd, ramp));
            i = j;
        }
        return runs;
    }

    /// <summary>Distance from the track start to the start of each segment; the last entry is the total length.</summary>
    public static double[] SegmentStarts(Track track)
    {
        track.SyncSegments();
        return Starts(Enumerable.Range(0, track.SegmentCount).Select(s => new ArcTable(Bezier(track, s))).ToArray());
    }

    private static double[] Starts(ArcTable[] tables)
    {
        var starts = new double[tables.Length + 1];
        for (var s = 0; s < tables.Length; s++)
            starts[s + 1] = starts[s] + tables[s].Length;
        return starts;
    }

    /// <summary>
    /// Arc-length fractions of a segment's rings (without its end): evenly spaced, plus the forced ones (gap ends).
    /// Even rings very close to a forced one are dropped, so there are no sliver strips.
    /// </summary>
    private static IEnumerable<double> RingFractions(Track track, int segment, ArcTable table, SamplerSettings settings, IEnumerable<double> forced)
    {
        var n = RingCount(track, segment, table, settings);
        var extra = forced.ToList();
        var minGap = Math.Min(0.25, settings.MaxStep / 4) / Math.Max(1e-9, table.Length);
        return Enumerable.Range(0, n).Select(i => (double)i / n)
            .Where(u => u == 0 || extra.All(f => Math.Abs(f - u) > minGap))
            .Concat(extra.Where(f => f > minGap && f < 1 - minGap))
            .Distinct().Order();
    }

    private static int RingCount(Track track, int segment, ArcTable table, SamplerSettings settings)
    {
        var (a, b) = (track.SegmentStart(segment), track.SegmentEnd(segment));
        // Width and shape blend with a smoothstep, whose steepest slope is 1.5 times the average.
        double Blend(Func<TrackPoint, double> value, double maxStep) => 1.5 * Math.Abs(value(b) - value(a)) / maxStep;
        // Banks are interpolated through the neighbouring points too, so the neighbouring segments' bank changes count.
        var count = track.SegmentCount;
        double BankStep(int s) => track.Closed || (s >= 0 && s < count)
            ? Math.Abs(ShortestAngle(track.SegmentEnd((s + count) % count).BankDegrees - track.SegmentStart((s + count) % count).BankDegrees))
            : 0;
        var bank = Math.Max(BankStep(segment), Math.Max(BankStep(segment - 1), BankStep(segment + 1)));
        var needed = new[]
        {
            table.Length / settings.MaxStep,
            table.TotalTurnDegrees / settings.MaxAngleDegrees,
            1.5 * bank / settings.MaxAngleDegrees,
            Blend(p => p.Width, settings.MaxWidthStep),
            Blend(p => p.Curve, settings.MaxCurveStepDegrees),
            Blend(p => p.Flat, settings.MaxShareStep),
            Blend(p => p.Sausage, settings.MaxShareStep),
            Blend(p => p.BorderHeight, settings.MaxBorderStep),
            // Borders fading between points (see PointBorderScale).
            Blend(p => p.Borders == true ? 1 : 0, settings.MaxShareStep),
            // Bumps along the road (waves, noise): a cross-section every `detail` meters.
            track.Segments[segment] is { Bumps: BumpPattern.Waves or BumpPattern.Noise } bumpy ? table.Length / Math.Max(0.25, bumpy.BumpDetail) : 0,
        };
        return Math.Max(1, (int)Math.Ceiling(needed.Max() - 1e-9));
    }

    private static Ring MakeRing(Track track, ArcTable[] tables, int s, double u, double distance, SamplerSettings settings)
    {
        var table = tables[s];
        var t = table.ParamAtFraction(u);
        var curve = table.Curve;
        var tangent = curve.Derivative(t);
        if (tangent.LengthSquared < 1e-12)
            tangent = curve.Evaluate(Math.Min(1, t + 1e-4)) - curve.Evaluate(Math.Max(0, t - 1e-4));

        return new Ring
        {
            Position = curve.Evaluate(t),
            Tangent = tangent.Normalized(),
            Distance = distance,
            Segment = s,
            Width = WidthAt(track, s, u),
            Curve = Lerp(track.SegmentStart(s).Curve, track.SegmentEnd(s).Curve, SmoothStep(u)) * Math.PI / 180,
            Flat = Lerp(track.SegmentStart(s).Flat, track.SegmentEnd(s).Flat, SmoothStep(u)),
            Sausage = Lerp(track.SegmentStart(s).Sausage, track.SegmentEnd(s).Sausage, SmoothStep(u)),
            BorderHeight = Lerp(track.SegmentStart(s).BorderHeight, track.SegmentEnd(s).BorderHeight, SmoothStep(u)),
            IsVertex = u == 0 || u == 1,
            SegmentU = u,
        };
    }

    private static CubicBezier Bezier(Track track, int s)
    {
        var a = track.SegmentStart(s);
        var b = track.SegmentEnd(s);
        return new CubicBezier(a.Position, a.Position + a.HandleOut, b.Position + b.HandleIn, b.Position);
    }

    /// <summary>
    /// Catmull-Rom interpolation (C1 across vertices, in roll per metre) of per-point banks in radians that have already
    /// been unwrapped along the track (see <see cref="UnwrapBanks"/>). On closed tracks every lap continues the total of
    /// one lap. The slope at a point is the bank change across its neighbours over the distance between them, and each
    /// segment scales it by its own length: segments of different lengths then meet at the same roll rate (with slopes
    /// per segment, a short segment next to a long one rolled several times faster on its side and overshot).
    /// </summary>
    /// <param name="lengths">Arc length of each segment.</param>
    private static double BankAt(Track track, double[] unwrapped, double[] lengths, int s, double u)
    {
        var count = track.Points.Count;
        var segments = lengths.Length;
        var lapRoll = unwrapped[count] - unwrapped[0];
        double B(int i)
        {
            if (!track.Closed)
                return unwrapped[Math.Clamp(i, 0, count - 1)];
            var laps = (int)Math.Floor((double)i / count);
            return unwrapped[((i % count) + count) % count] + laps * lapRoll;
        }
        // Open ends mirror the first and last segment (as the clamped banks do), so the ends keep their old slope.
        double L(int seg) => Math.Max(1e-6, track.Closed ? lengths[((seg % segments) + segments) % segments] : lengths[Math.Clamp(seg, 0, segments - 1)]);
        double Slope(int i) => (B(i + 1) - B(i - 1)) / (L(i - 1) + L(i));

        double p1 = B(s), p2 = B(s + 1);
        var m1 = Slope(s) * L(s);
        var m2 = Slope(s + 1) * L(s);
        double u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * p1 + (u3 - 2 * u2 + u) * m1 + (-2 * u3 + 3 * u2) * p2 + (u3 - u2) * m2;
    }

    /// <summary>
    /// Unwraps the point banks (radians, count + 1 entries; the last one is the closing point) so that between two
    /// neighbouring points the *total* roll — levelling correction plus bank — takes the shorter way round. Unwrapping
    /// the bank on its own is not enough: where the levelling turns the road over (a dive that curls back under) and
    /// the bank turns it back, each part alone may pick a different direction and the road spins a full extra turn.
    /// </summary>
    /// <param name="corrections">Levelling correction at each point (radians, count + 1 entries).</param>
    private static double[] UnwrapBanks(Track track, double[] corrections)
    {
        var count = track.Points.Count;
        double Bank(int i) => track.Points[i % count].BankDegrees * Math.PI / 180;
        var unwrapped = new double[count + 1];
        unwrapped[0] = Bank(0);
        for (var i = 1; i <= count; i++)
        {
            var bankStep = Bank(i) - Bank(i - 1);
            var correctionStep = corrections[i] - corrections[i - 1];
            var total = ShortestAngle((bankStep + correctionStep) * 180 / Math.PI) * Math.PI / 180;
            unwrapped[i] = unwrapped[i - 1] + total - correctionStep;
        }
        return unwrapped;
    }

    /// <summary>
    /// Width between two points: smoothstep over the segment's arc length. Equals the point widths exactly at the
    /// points, has zero slope there (so consecutive segments join smoothly) and never overshoots.
    /// </summary>
    private static double WidthAt(Track track, int s, double u) =>
        Lerp(track.SegmentStart(s).Width, track.SegmentEnd(s).Width, SmoothStep(u));

    /// <summary>Rotation-minimizing lateral axes via the double reflection method (Wang et al. 2008).</summary>
    private static Vec3d[] RotationMinimizingRights(List<Ring> rings)
    {
        var count = rings.Count;
        var rights = new Vec3d[count];
        var t0 = rings[0].Tangent;
        var h0 = Vec3d.Cross(t0, Vec3d.UnitY);
        rights[0] = h0.Length > 1e-6 ? h0.Normalized() : Vec3d.Cross(t0, Vec3d.UnitZ).Normalized();

        for (var i = 0; i + 1 < count; i++)
        {
            var r = rights[i];
            var t = rings[i].Tangent;
            var v1 = rings[i + 1].Position - rings[i].Position;
            var c1 = v1.LengthSquared;
            if (c1 > 1e-12)
            {
                r -= v1 * (2 / c1 * Vec3d.Dot(v1, r));
                t -= v1 * (2 / c1 * Vec3d.Dot(v1, t));
            }
            var v2 = rings[i + 1].Tangent - t;
            var c2 = v2.LengthSquared;
            if (c2 > 1e-12)
                r -= v2 * (2 / c2 * Vec3d.Dot(v2, r));
            // Re-orthogonalize against the tangent to stop numerical drift over long tracks.
            var tn = rings[i + 1].Tangent;
            rights[i + 1] = (r - tn * Vec3d.Dot(r, tn)).Normalized();
        }
        return rights;
    }

    /// <summary>Angle that rotates <paramref name="from"/> onto <paramref name="to"/> around <paramref name="axis"/>.</summary>
    private static double SignedAngle(Vec3d from, Vec3d to, Vec3d axis) =>
        Math.Atan2(Vec3d.Dot(Vec3d.Cross(from, to), axis), Vec3d.Dot(from, to));

    /// <summary>
    /// Cubic Hermite interpolation of roll keys over distance, with Catmull-Rom style tangents (C1).
    /// Before the first and after the last key the roll is held constant.
    /// </summary>
    private static double InterpolateRoll(List<(double Distance, double Angle)> keys, double d)
    {
        if (d <= keys[0].Distance) return keys[0].Angle;
        if (d >= keys[^1].Distance) return keys[^1].Angle;

        var k = 0;
        while (keys[k + 1].Distance < d) k++;
        var (d0, a0) = keys[k];
        var (d1, a1) = keys[k + 1];
        var span = d1 - d0;
        if (span < 1e-9) return a1;

        double Slope(int i)
        {
            // Zero slope at the ends; otherwise the average of the neighbouring secant slopes.
            if (i <= 0 || i >= keys.Count - 1) return 0;
            var (dp, ap) = keys[i - 1];
            var (dn, an) = keys[i + 1];
            return (an - ap) / Math.Max(1e-9, dn - dp);
        }

        var m0 = Slope(k) * span;
        var m1 = Slope(k + 1) * span;
        var u = (d - d0) / span;
        double u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * a0 + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * a1 + (u3 - u2) * m1;
    }

    /// <summary>
    /// Wraps a bank difference into [-180°, 180°]. An exact half turn is ambiguous; it follows the sign of the raw
    /// difference, so 0° → 180° → 0° rolls there and back instead of making a full turn.
    /// </summary>
    private static double ShortestAngle(double degrees)
    {
        var a = degrees % 360;
        if (a > 180) a -= 360;
        else if (a < -180) a += 360;
        if (Math.Abs(Math.Abs(a) - 180) < 1e-9)
            a = degrees >= 0 ? 180 : -180;
        return a;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double SmoothStep(double x) => x * x * (3 - 2 * x);

    /// <summary>
    /// Spline points whose tangent is steeper than this (|horizontal component| below the value) have no
    /// meaningful "level" orientation, e.g. the sides of a loop; their roll is interpolated from neighbours.
    /// </summary>
    private const double LevelableHorizontal = 0.35;

    /// <summary>Below this horizontal tangent component (pitch above ~76°) the track counts as having gone vertical.</summary>
    private const double VerticalHorizontal = 0.25;

    /// <summary>
    /// Builds the road frame (before banking) like a roller-coaster track:
    /// <list type="number">
    /// <item>a rotation-minimizing frame (double reflection) is carried along the whole track, so the road never
    /// twists on its own, including through loops and vertical sections;</item>
    /// <item>at every spline point that is not near-vertical we measure the roll between that frame and a level road;</item>
    /// <item>that roll correction is interpolated smoothly (C1) along the distance between those points.</item>
    /// </list>
    /// The road is therefore exactly level at the points, roll changes smoothly in between, and closed tracks close
    /// without a twist seam.
    /// </summary>
    private static void ComputeFrames(Track track, List<Ring> rings)
    {
        var count = rings.Count;
        var rmf = RotationMinimizingRights(rings);

        // Roll correction samples at levelable spline points: (distance, angle), each levelled upright or upside down.
        // A road only turns over by going (nearly) vertical, so a point keeps the previous point's state unless the
        // track went vertical in between. Then the direction decides: going vertical upwards (a loop's climb) leads to
        // upside down (the top of the loop); going vertical downwards (a loop's descent, or a dive that curls back
        // under) leads to upright, and the roll needed in between is spread smoothly over that section.
        // (Deciding by the raw RMF instead is unreliable: on long tracks and steep turns it drifts ~90° from level.)
        var keys = new List<(double Distance, double Angle)>();
        var previous = 0.0;
        var inverted = false;
        var wentVertical = false;
        var lastVerticalUp = false;
        for (var i = 0; i < count; i++)
        {
            var t = rings[i].Tangent;
            var horizontal = Vec3d.Cross(t, Vec3d.UnitY);
            if (horizontal.Length < VerticalHorizontal)
            {
                wentVertical = true;
                lastVerticalUp = t.Y > 0;
            }
            if (!rings[i].IsVertex && i != 0 && i != count - 1)
                continue;
            if (horizontal.Length < LevelableHorizontal)
                continue;

            var level = horizontal.Normalized(); // the lateral axis of an upright, level road
            if (keys.Count > 0 && wentVertical)
                inverted = lastVerticalUp;
            if (inverted)
                level = -level;
            // Unwrap so consecutive corrections never jump by a full turn.
            var angle = SignedAngle(rmf[i], level, t);
            while (angle - previous > Math.PI) angle -= 2 * Math.PI;
            while (angle - previous < -Math.PI) angle += 2 * Math.PI;
            keys.Add((rings[i].Distance, angle));
            previous = angle;
            wentVertical = false;
        }

        double Roll(double distance) => keys.Count == 0 ? 0 : InterpolateRoll(keys, distance);
        var rights = new Vec3d[count];
        for (var i = 0; i < count; i++)
        {
            var roll = Roll(rings[i].Distance);
            rights[i] = Vec3d.Rotate(rmf[i], rings[i].Tangent, roll).Normalized();
        }

        if (track.Closed && count > 1)
        {
            // Normally both ends are levelled at point 0 and already match. If point 0 is near-vertical,
            // distribute whatever twist remains so the loop still closes smoothly.
            var t = rings[count - 1].Tangent;
            var twist = SignedAngle(rights[count - 1], rights[0], t);
            var total = rings[count - 1].Distance;
            if (total > 0 && Math.Abs(twist) > 1e-9)
                for (var i = 1; i < count; i++)
                    rights[i] = Vec3d.Rotate(rights[i], rings[i].Tangent, twist * rings[i].Distance / total).Normalized();
        }

        // Levelling correction at every point (the ring that starts each segment, plus the final ring).
        var pointCount = track.Points.Count;
        var corrections = new double[pointCount + 1];
        foreach (var ring in rings)
            if (ring.IsVertex && ring.SegmentU == 0)
                corrections[ring.Segment] = Roll(ring.Distance);
        corrections[pointCount] = Roll(rings[^1].Distance);
        if (!track.Closed)
            corrections[pointCount - 1] = corrections[pointCount];
        var banks = UnwrapBanks(track, corrections);
        var starts = SegmentStarts(track);
        var lengths = Enumerable.Range(0, track.SegmentCount).Select(s => starts[s + 1] - starts[s]).ToArray();

        for (var i = 0; i < count; i++)
        {
            var ring = rings[i];
            var bank = BankAt(track, banks, lengths, ring.Segment, ring.SegmentU);
            var right = Vec3d.Rotate(rights[i], ring.Tangent, bank).Normalized();
            ring.Right = right;
            ring.Up = Vec3d.Cross(right, ring.Tangent).Normalized();
        }

        if (track.Closed && count > 1)
        {
            // Force exact closure so the loop seam is bit-identical.
            var closing = rings[0].Clone();
            closing.Distance = rings[count - 1].Distance;
            closing.Segment = rings[count - 1].Segment;
            rings[count - 1] = closing;
        }
    }
}

public readonly record struct CubicBezier(Vec3d P0, Vec3d P1, Vec3d P2, Vec3d P3)
{
    public Vec3d Evaluate(double t)
    {
        var u = 1 - t;
        return P0 * (u * u * u) + P1 * (3 * u * u * t) + P2 * (3 * u * t * t) + P3 * (t * t * t);
    }

    public Vec3d Derivative(double t)
    {
        var u = 1 - t;
        return (P1 - P0) * (3 * u * u) + (P2 - P1) * (6 * u * t) + (P3 - P2) * (3 * t * t);
    }
}

/// <summary>Arc-length lookup table for a single Bézier segment.</summary>
internal sealed class ArcTable
{
    private readonly double[] _lengths;

    public CubicBezier Curve { get; }
    public double Length { get; }
    public double TotalTurnDegrees { get; }

    public ArcTable(CubicBezier curve, int resolution = 512)
    {
        Curve = curve;
        _lengths = new double[resolution + 1];
        var prev = curve.Evaluate(0);
        Vec3d? prevDir = null;
        var turn = 0.0;
        for (var i = 1; i <= resolution; i++)
        {
            var p = curve.Evaluate((double)i / resolution);
            var d = p - prev;
            _lengths[i] = _lengths[i - 1] + d.Length;
            if (d.LengthSquared > 1e-18)
            {
                var dir = d.Normalized();
                if (prevDir is { } pd)
                    turn += Math.Acos(Math.Clamp(Vec3d.Dot(pd, dir), -1, 1));
                prevDir = dir;
            }
            prev = p;
        }
        Length = _lengths[resolution];
        TotalTurnDegrees = turn * 180 / Math.PI;
    }

    /// <summary>Maps an arc-length fraction in [0,1] to the curve parameter t.</summary>
    public double ParamAtFraction(double fraction)
    {
        if (fraction <= 0) return 0;
        if (fraction >= 1) return 1;
        var target = fraction * Length;
        int lo = 0, hi = _lengths.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_lengths[mid] < target) lo = mid; else hi = mid;
        }
        var span = _lengths[hi] - _lengths[lo];
        var f = span > 0 ? (target - _lengths[lo]) / span : 0;
        return (lo + f) / (_lengths.Length - 1);
    }
}
