using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>Where a track object ended up: its center on the driving surface and the road frame there.</summary>
/// <param name="Across">Center across the surface after clamping (holes stay inside the road).</param>
/// <param name="Placed">False if the object cannot be built there (a pillar inside a gap, a gap of zero length ...).</param>
public readonly record struct ObjectPlacement(Vec3d Position, Vec3d Normal, Vec3d Tangent, double Distance, double Across, bool Placed);

/// <summary>A hole's outline in the road's (distance along, across) surface coordinates: a convex polygon, counter-clockwise.</summary>
public sealed class HoleOutline
{
    public required List<(double D, double S)> Points { get; init; }
    public double MinD => Points.Min(p => p.D);
    public double MaxD => Points.Max(p => p.D);
    public double MinS => Points.Min(p => p.S);
    public double MaxS => Points.Max(p => p.S);
}

/// <summary>Placement rules shared by the mesh builders and the editor preview.</summary>
public static class TrackObjects
{
    /// <summary>Holes keep at least this distance to the edges of the driving surface and to the ends of the track.</summary>
    public const double EdgeMargin = 0.1;
    /// <summary>Corners of a round hole's outline.</summary>
    private const int RoundSegments = 32;

    /// <summary>Distance from the track start to the object's center.</summary>
    public static double CenterDistance(TrackObject o, double[] segmentStarts) =>
        segmentStarts[o.Segment] + Math.Clamp(o.At, 0, 1) * (segmentStarts[o.Segment + 1] - segmentStarts[o.Segment]);

    private static IEnumerable<TrackObject> OfKind(Track track, TrackObjectKind kind, double[] segmentStarts) =>
        track.Objects.Where(o => o.Kind == kind && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1);

    /// <summary>Distance ranges where the road is left out.</summary>
    public static List<(double Start, double End)> GapRanges(Track track, double[] segmentStarts)
    {
        var total = segmentStarts[^1];
        return OfKind(track, TrackObjectKind.Gap, segmentStarts)
            .Select(o => GapRange(o, segmentStarts, total))
            .Where(g => g.End - g.Start > 0.05)
            .ToList();
    }

    private static (double Start, double End) GapRange(TrackObject o, double[] segmentStarts, double total)
    {
        var c = CenterDistance(o, segmentStarts);
        return (Math.Clamp(c - o.Length / 2, 0, total), Math.Clamp(c + o.Length / 2, 0, total));
    }

    /// <summary>The road cross-section at a distance, interpolated between the two rings around it (like the mesh is).</summary>
    public static Ring RingAt(IReadOnlyList<Ring> rings, double distance)
    {
        if (distance <= rings[0].Distance) return rings[0].Clone();
        if (distance >= rings[^1].Distance) return rings[^1].Clone();
        int lo = 0, hi = rings.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (rings[mid].Distance <= distance) lo = mid; else hi = mid;
        }
        var (a, b) = (rings[lo], rings[hi]);
        var t = (distance - a.Distance) / Math.Max(1e-9, b.Distance - a.Distance);
        var r = a.Clone();
        r.Position = Vec3d.Lerp(a.Position, b.Position, t);
        r.Tangent = Vec3d.Lerp(a.Tangent, b.Tangent, t).Normalized();
        r.Right = Vec3d.Lerp(a.Right, b.Right, t).Normalized();
        r.Up = Vec3d.Lerp(a.Up, b.Up, t).Normalized();
        r.Distance = distance;
        r.Width = a.Width + (b.Width - a.Width) * t;
        r.Curve = a.Curve + (b.Curve - a.Curve) * t;
        r.Flat = a.Flat + (b.Flat - a.Flat) * t;
        r.Sausage = a.Sausage + (b.Sausage - a.Sausage) * t;
        r.EdgeAnchor = a.EdgeAnchor + (b.EdgeAnchor - a.EdgeAnchor) * t;
        r.BorderHeight = a.BorderHeight + (b.BorderHeight - a.BorderHeight) * t;
        r.BorderScale = a.BorderScale + (b.BorderScale - a.BorderScale) * t;
        r.IsVertex = false;
        return r;
    }

    /// <summary>A point on the driving surface (lifted along its normal by <paramref name="lift"/>) and the surface normal there.</summary>
    public static (Vec3d Point, Vec3d Normal) SurfacePoint(Ring ring, double across, double lift = 0)
    {
        var (x, y, nx, ny, _, _, _) = RoadMeshBuilder.SurfaceSample(ring, across);
        var normal = (ring.Right * nx + ring.Up * ny).Normalized();
        return (ring.Position + ring.Right * x + ring.Up * y + normal * lift, normal);
    }

    /// <summary>Hole center and half sizes in surface coordinates, clamped so the hole stays inside the road.</summary>
    private static (double D, double S, double HalfLength, double HalfWidth) HoleExtent(TrackObject o, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        var total = rings[^1].Distance;
        var halfLength = Math.Clamp(o.Length / 2, 0.05, Math.Max(0.05, total / 2 - EdgeMargin));
        var d = Math.Clamp(CenterDistance(o, segmentStarts), halfLength + EdgeMargin, Math.Max(halfLength + EdgeMargin, total - halfLength - EdgeMargin));
        var half = Math.Max(0.1, RingAt(rings, d).Width / 2 - EdgeMargin);
        var halfWidth = Math.Clamp(o.Width / 2, 0.05, half);
        return (d, Math.Clamp(o.Offset, -half + halfWidth, half - halfWidth), halfLength, halfWidth);
    }

    /// <summary>Meters on the surface per meter of track distance, at a point across the road (more on the outside of bends).</summary>
    public static double AlongStretch(IReadOnlyList<Ring> rings, double distance, double across)
    {
        var before = SurfacePoint(RingAt(rings, distance - 0.5), across).Point;
        var after = SurfacePoint(RingAt(rings, distance + 0.5), across).Point;
        return Math.Max(0.05, (after - before).Length);
    }

    /// <summary>
    /// Outlines of all holes. Along the road a hole is measured where it is (on the outside of a bend the surface is
    /// stretched), so a round hole stays round wherever it sits.
    /// </summary>
    public static List<HoleOutline> Holes(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        var result = new List<HoleOutline>();
        foreach (var o in OfKind(track, TrackObjectKind.Hole, segmentStarts))
        {
            var (d, s, halfLength, halfWidth) = HoleExtent(o, rings, segmentStarts);
            var hl = halfLength / AlongStretch(rings, d, s);
            var points = o.Shape == ObjectShape.Rect
                ? new List<(double, double)> { (d - hl, s - halfWidth), (d + hl, s - halfWidth), (d + hl, s + halfWidth), (d - hl, s + halfWidth) }
                : Enumerable.Range(0, RoundSegments).Select(i => 2 * Math.PI * i / RoundSegments)
                    .Select(a => (d + Math.Cos(a) * hl, s + Math.Sin(a) * halfWidth)).ToList();
            result.Add(new HoleOutline { Points = points });
        }
        return result;
    }

    /// <summary>Where each object of the track sits (same order as <see cref="Track.Objects"/>), for the editor.</summary>
    public static List<ObjectPlacement> Placements(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, RoadMeshSettings settings)
    {
        var total = rings[^1].Distance;
        var gaps = GapRanges(track, segmentStarts);
        var result = new List<ObjectPlacement>();
        foreach (var o in track.Objects)
        {
            var d = CenterDistance(o, segmentStarts);
            var across = 0.0;
            var placed = true;
            switch (o.Kind)
            {
                case TrackObjectKind.Hole:
                    (d, across, _, _) = HoleExtent(o, rings, segmentStarts);
                    break;
                case TrackObjectKind.Gap:
                    var (start, end) = GapRange(o, segmentStarts, total);
                    placed = end - start > 0.05;
                    break;
                case TrackObjectKind.Pillar:
                    (across, placed) = PillarBuilder.Footing(o, rings, d, gaps);
                    break;
                case TrackObjectKind.Bump:
                    across = o.Offset;
                    placed = !gaps.Any(g => d > g.Start && d < g.End);
                    break;
            }
            var ring = RingAt(rings, d);
            if (o.Kind == TrackObjectKind.Gate && o.Frame)
            {
                // A gate with a frame: where its panels stand (the middle of their lower edge, the ring's up), and in
                // place of the offset how many of them it takes across, for the editor to show them.
                var (foot, up, along, layout) = GateObjects.FrameAt(o, ring, settings);
                result.Add(new ObjectPlacement(foot, up, along, d, layout.Across, placed));
                continue;
            }
            var (point, normal) = SurfacePoint(ring, across);
            result.Add(new ObjectPlacement(point, normal, ring.Tangent, d, across, placed));
        }
        return result;
    }
}

/// <summary>
/// Cuts holes out of the road surfaces as they are built: each triangle minus every hole outline it overlaps, in the
/// road's (distance, across) coordinates, so the cut follows banking and bent cross-sections exactly. Afterwards
/// <see cref="BuildWalls"/> closes the slab round each hole between the driving surface and the underside.
/// </summary>
internal sealed class HoleCutter
{
    public enum Role { CutOnly, Top, Bottom }

    private readonly record struct Corner(double D, double S, double W0, double W1, double W2);
    /// <summary>A triangle near a hole, kept to place the walls exactly on the cut surfaces.</summary>
    private readonly record struct Candidate(MeshChunk Chunk, (double D, double S) A, (double D, double S) B, (double D, double S) C, int Ia, int Ib, int Ic);

    private readonly List<HoleOutline> _holes;
    private readonly List<Candidate>[] _top, _bottom;

    public HoleCutter(List<HoleOutline> holes)
    {
        _holes = holes;
        _top = holes.Select(_ => new List<Candidate>()).ToArray();
        _bottom = holes.Select(_ => new List<Candidate>()).ToArray();
    }

    public bool Any => _holes.Count > 0;

    /// <summary>Adds the triangle to the chunk minus all holes. pa, pb, pc: (distance, across) of its corners.</summary>
    public void Add(MeshChunk chunk, Triangle tri, (double D, double S) pa, (double D, double S) pb, (double D, double S) pc, Role role)
    {
        var minD = Math.Min(pa.D, Math.Min(pb.D, pc.D));
        var maxD = Math.Max(pa.D, Math.Max(pb.D, pc.D));
        var minS = Math.Min(pa.S, Math.Min(pb.S, pc.S));
        var maxS = Math.Max(pa.S, Math.Max(pb.S, pc.S));
        var overlapping = Enumerable.Range(0, _holes.Count)
            .Where(h => _holes[h].MinD <= maxD && _holes[h].MaxD >= minD && _holes[h].MinS <= maxS && _holes[h].MaxS >= minS)
            .ToList();
        if (overlapping.Count == 0)
        {
            chunk.Triangles.Add(tri);
            return;
        }

        foreach (var h in overlapping)
        {
            var candidate = new Candidate(chunk, pa, pb, pc, tri.A, tri.B, tri.C);
            if (role == Role.Top) _top[h].Add(candidate);
            else if (role == Role.Bottom) _bottom[h].Add(candidate);
        }

        var original = new List<Corner> { new(pa.D, pa.S, 1, 0, 0), new(pb.D, pb.S, 0, 1, 0), new(pc.D, pc.S, 0, 0, 1) };
        var pieces = new List<List<Corner>> { original };
        foreach (var h in overlapping)
            pieces = pieces.SelectMany(p => Subtract(p, _holes[h].Points)).ToList();
        if (pieces.Count == 1 && ReferenceEquals(pieces[0], original))
        {
            chunk.Triangles.Add(tri);
            return;
        }

        foreach (var piece in pieces)
        {
            var indices = piece.Select(c => VertexAt(chunk, tri, c)).ToArray();
            for (var i = 1; i + 1 < indices.Length; i++)
                chunk.Triangles.Add(tri with { A = indices[0], B = indices[i], C = indices[i + 1] });
        }
    }

    /// <summary>Convex pieces of a convex polygon outside a convex counter-clockwise outline (the polygon itself if they do not meet).</summary>
    private static List<List<Corner>> Subtract(List<Corner> polygon, List<(double D, double S)> outline)
    {
        var result = new List<List<Corner>>();
        var remaining = polygon;
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var outside = Clip(remaining, p, q, keepLeft: false);
            if (ReferenceEquals(outside, remaining))
            {
                result.Add(remaining);
                return result;
            }
            if (outside.Count >= 3 && Math.Abs(Area(outside)) > 1e-10)
                result.Add(outside);
            remaining = Clip(remaining, p, q, keepLeft: true);
            if (remaining.Count < 3)
                return result;
        }
        // What is left lies inside the hole.
        return result;
    }

    /// <summary>Sutherland–Hodgman clip against the line p→q. Returns the polygon itself if it lies entirely on the kept side.</summary>
    private static List<Corner> Clip(List<Corner> polygon, (double D, double S) p, (double D, double S) q, bool keepLeft)
    {
        var sign = keepLeft ? 1 : -1;
        // Corners (almost) on the line count as on both sides, so neighbouring triangles always split at the same points.
        var sides = polygon.Select(v => sign * ((q.D - p.D) * (v.S - p.S) - (q.S - p.S) * (v.D - p.D)))
            .Select(x => Math.Abs(x) < 1e-8 ? 0 : x).ToArray();
        if (sides.All(x => x >= 0))
            return polygon;
        var result = new List<Corner>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = (i + 1) % polygon.Count;
            if (sides[i] >= 0) result.Add(polygon[i]);
            if ((sides[i] >= 0) != (sides[j] >= 0))
            {
                var t = sides[i] / (sides[i] - sides[j]);
                var (a, b) = (polygon[i], polygon[j]);
                result.Add(new Corner(a.D + (b.D - a.D) * t, a.S + (b.S - a.S) * t,
                    a.W0 + (b.W0 - a.W0) * t, a.W1 + (b.W1 - a.W1) * t, a.W2 + (b.W2 - a.W2) * t));
            }
        }
        return result;
    }

    private static double Area(List<Corner> polygon)
    {
        var area = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            area += a.D * b.S - b.D * a.S;
        }
        return area / 2;
    }

    /// <summary>A vertex at barycentric weights inside the triangle: one of its corners, or a new one with interpolated attributes.</summary>
    private static int VertexAt(MeshChunk chunk, Triangle tri, Corner c)
    {
        if (c.W0 > 1 - 1e-12) return tri.A;
        if (c.W1 > 1 - 1e-12) return tri.B;
        if (c.W2 > 1 - 1e-12) return tri.C;
        Vector3 V3(List<Vector3> list) => list[tri.A] * (float)c.W0 + list[tri.B] * (float)c.W1 + list[tri.C] * (float)c.W2;
        Vector2 V2(List<Vector2> list) => list[tri.A] * (float)c.W0 + list[tri.B] * (float)c.W1 + list[tri.C] * (float)c.W2;
        var index = chunk.Positions.Count;
        chunk.Positions.Add(V3(chunk.Positions));
        chunk.Normals.Add(Vector3.Normalize(V3(chunk.Normals)));
        chunk.Uv0.Add(V2(chunk.Uv0));
        chunk.Uv1.Add(V2(chunk.Uv1));
        chunk.Surface.Add(V2(chunk.Surface));
        return index;
    }

    private static (double W0, double W1, double W2) Barycentric(Candidate t, (double D, double S) p)
    {
        var (ax, ay, bx, by, cx, cy) = (t.A.D, t.A.S, t.B.D, t.B.S, t.C.D, t.C.S);
        var det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(det) < 1e-18) return (double.NaN, double.NaN, double.NaN);
        var w0 = ((by - cy) * (p.D - cx) + (cx - bx) * (p.S - cy)) / det;
        var w1 = ((cy - ay) * (p.D - cx) + (ax - cx) * (p.S - cy)) / det;
        return (w0, w1, 1 - w0 - w1);
    }

    private static Vector3 Map(Candidate t, (double D, double S) p)
    {
        var (w0, w1, w2) = Barycentric(t, p);
        var pos = t.Chunk.Positions;
        return pos[t.Ia] * (float)w0 + pos[t.Ib] * (float)w1 + pos[t.Ic] * (float)w2;
    }

    private static Candidate? Containing(List<Candidate> candidates, (double D, double S) p)
    {
        foreach (var t in candidates)
        {
            var (w0, w1, w2) = Barycentric(t, p);
            if (w0 >= -1e-7 && w1 >= -1e-7 && w2 >= -1e-7)
                return t;
        }
        return null;
    }

    /// <summary>Segment parameters (0..1 along p→q) where it crosses the triangle's edges.</summary>
    private static IEnumerable<double> Crossings((double D, double S) p, (double D, double S) q, Candidate t)
    {
        foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
        {
            var (rx, ry) = (q.D - p.D, q.S - p.S);
            var (sx, sy) = (b.D - a.D, b.S - a.S);
            var denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < 1e-18) continue;
            var u = ((a.D - p.D) * sy - (a.S - p.S) * sx) / denom;
            var v = ((a.D - p.D) * ry - (a.S - p.S) * rx) / denom;
            if (u > 0 && u < 1 && v >= -1e-9 && v <= 1 + 1e-9)
                yield return u;
        }
    }

    /// <summary>
    /// Walls round every hole, from the cut edge of the driving surface down to the cut edge of the underside, facing
    /// into the hole. The outline is split wherever it crosses a triangle of either surface, so the walls meet both
    /// surfaces exactly; every piece goes into the chunk whose triangles it lies on.
    /// </summary>
    /// <param name="band">Lightmap band for the walls (U range); holes in the same chunk share it side by side.</param>
    public void BuildWalls(TmMaterial material, (double U0, double U1) band, double textureScale)
    {
        var quads = new List<(MeshChunk Chunk, int Hole, Vector3[] Corners, Vector3 Normal, double U0, double U1, double Along0, double Along1)>();
        for (var h = 0; h < _holes.Count; h++)
        {
            var outline = _holes[h].Points;
            var along = 0.0;
            for (var i = 0; i < outline.Count; i++)
            {
                var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
                var ts = _top[h].Concat(_bottom[h]).SelectMany(t => Crossings(p, q, t)).Append(0).Append(1).Order().ToList();
                (double D, double S) At(double t) => (p.D + (q.D - p.D) * t, p.S + (q.S - p.S) * t);
                // Into the hole: left of p→q (the outline runs counter-clockwise).
                var inward = (D: -(q.S - p.S), S: q.D - p.D);
                var inwardLength = Math.Max(1e-12, Math.Sqrt(inward.D * inward.D + inward.S * inward.S));
                for (var k = 0; k + 1 < ts.Count; k++)
                {
                    if (ts[k + 1] - ts[k] < 1e-9) continue;
                    var mid = At((ts[k] + ts[k + 1]) / 2);
                    if (Containing(_top[h], mid) is not { } top || Containing(_bottom[h], mid) is not { } bottom || top.Chunk != bottom.Chunk)
                        continue;
                    var (a, b) = (At(ts[k]), At(ts[k + 1]));
                    var corners = new[] { Map(top, a), Map(top, b), Map(bottom, b), Map(bottom, a) };
                    var inside = Map(top, (mid.D + inward.D / inwardLength * 0.01, mid.S + inward.S / inwardLength * 0.01));
                    var normal = Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]);
                    if (Vector3.Dot(normal, inside - corners[0]) < 0) normal = -normal;
                    var length = (corners[1] - corners[0]).Length();
                    quads.Add((top.Chunk, h, corners, Vector3.Normalize(normal), (i + ts[k]) / outline.Count, (i + ts[k + 1]) / outline.Count,
                        along, along + length));
                    along += length;
                }
            }
        }

        foreach (var group in quads.GroupBy(q => q.Chunk))
        {
            var chunk = group.Key;
            var holes = group.Select(q => q.Hole).Distinct().ToList();
            var m = chunk.MaterialIndex(material);
            foreach (var q in group)
            {
                // Each hole of the chunk gets its own slice of the band: U round the outline, V from the underside up.
                var slice = (band.U1 - band.U0) / holes.Count;
                var u0 = band.U0 + slice * holes.IndexOf(q.Hole);
                double U(double f) => u0 + slice * (0.05 + 0.9 * f);
                var height = (q.Corners[0] - q.Corners[3]).Length();
                var uvs = new[]
                {
                    (new Vector2((float)(q.Along0 / textureScale), (float)(height / textureScale)), new Vector2((float)U(q.U0), 0.99f)),
                    (new Vector2((float)(q.Along1 / textureScale), (float)(height / textureScale)), new Vector2((float)U(q.U1), 0.99f)),
                    (new Vector2((float)(q.Along1 / textureScale), 0), new Vector2((float)U(q.U1), 0.01f)),
                    (new Vector2((float)(q.Along0 / textureScale), 0), new Vector2((float)U(q.U0), 0.01f)),
                };
                var first = chunk.Positions.Count;
                for (var i = 0; i < 4; i++)
                {
                    chunk.Positions.Add(q.Corners[i]);
                    chunk.Normals.Add(q.Normal);
                    chunk.Uv0.Add(uvs[i].Item1);
                    chunk.Uv1.Add(uvs[i].Item2);
                    chunk.Surface.Add(MeshChunk.NoSurface);
                }
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 2, m), q.Normal);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 2, first + 3, m), q.Normal);
            }
        }
    }
}

/// <summary>
/// Pillars: obstacles standing on the driving surface, along its normal (so they stay upright on banked and bent road,
/// in pipes and loops). Their foot follows the surface; all pillars of a track go into one item.
/// </summary>
public static class PillarBuilder
{
    /// <summary>How far the foot reaches into the road, so it never shows a gap on a bent surface.</summary>
    private const double Sink = 0.2;
    private const int RoundSegments = 24;

    /// <summary>
    /// Diameters of the game's obstacle pillars (ObstaclePillar2m ... 16m; the number is the thickness, per the user). They
    /// are round, stand 7 m above their base (the item's pivot) and reach 1 m below it (measured on the 2/4/8 m models).
    /// </summary>
    public static readonly double[] GameSizes = [2, 4, 8, 16];
    public const double GameHeight = 7;

    /// <summary>The game pillar nearest to a pillar's width.</summary>
    public static double GameSize(double width) => GameSizes.MinBy(s => Math.Abs(Math.Log(Math.Max(0.1, width) / s)));

    public static string GameItemName(double size) => $"ObstaclePillar{size:0}m";

    /// <summary>
    /// The track's pillars as the game's items: at their foot on the surface, upright to it and facing along the road,
    /// turned as (yaw, pitch, roll) degrees for the anchored-object convention (Ry·Rx, roll innermost about Z).
    /// </summary>
    public static IEnumerable<VanillaItem> GameItems(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        var gaps = TrackObjects.GapRanges(track, segmentStarts);
        foreach (var o in track.Objects.Where(o => o.Kind == TrackObjectKind.Pillar && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1))
        {
            var distance = TrackObjects.CenterDistance(o, segmentStarts);
            var (across, placed) = Footing(o, rings, distance, gaps);
            if (!placed) continue;
            var ring = TrackObjects.RingAt(rings, distance);
            var (point, up) = TrackObjects.SurfacePoint(ring, across);
            var forward = (ring.Tangent - up * Vec3d.Dot(ring.Tangent, up)).Normalized();
            var (yaw, pitch, roll) = UprightAngles(forward, up);
            yield return new VanillaItem
            {
                Name = GameItemName(GameSize(o.Width)), Position = point, Yaw = yaw, Pitch = pitch, Roll = roll, Color = o.Color,
            };
        }
    }

    /// <summary>
    /// An item's (yaw, pitch, roll) in degrees for the anchored-object convention, R = Ry(yaw)·Rx(pitch)·Rz(roll), so that
    /// its local +Z points along `forward` and +Y along `up` (orthogonal unit vectors).
    /// </summary>
    public static (double Yaw, double Pitch, double Roll) UprightAngles(Vec3d forward, Vec3d up)
    {
        // R·Z = forward gives yaw and pitch, then roll from where Y has to go.
        var yaw = Math.Atan2(forward.X, forward.Z);
        var pitch = -Math.Asin(Math.Clamp(forward.Y, -1, 1));
        // w = Rx(-pitch)·Ry(-yaw)·up = Rz(roll)·Y = (-sin roll, cos roll, 0)
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        var t = new Vec3d(cy * up.X - sy * up.Z, up.Y, sy * up.X + cy * up.Z);
        var w = new Vec3d(t.X, cp * t.Y + sp * t.Z, -sp * t.Y + cp * t.Z);
        var roll = Math.Atan2(-w.X, w.Y);
        const double deg = 180 / Math.PI;
        return (yaw * deg, pitch * deg, roll * deg);
    }

    /// <summary>Where a pillar stands: its center across the surface (kept inside the road) and whether it can be built.</summary>
    internal static (double Across, bool Placed) Footing(TrackObject o, IReadOnlyList<Ring> rings, double distance, List<(double Start, double End)> gaps)
    {
        var half = TrackObjects.RingAt(rings, distance).Width / 2;
        var halfWidth = Math.Min(GameSize(o.Width) / 2, half);
        var across = Math.Clamp(o.Offset, -half + halfWidth, half - halfWidth);
        return (across, !gaps.Any(g => distance >= g.Start && distance <= g.End));
    }

    public static MeshChunk? Build(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, Vec3d origin, RoadMeshSettings settings,
        string name = "Pillars")
    {
        var gaps = TrackObjects.GapRanges(track, segmentStarts);
        var pillars = track.Objects
            .Where(o => o.Kind == TrackObjectKind.Pillar && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1)
            .Select(o => (Object: o, Distance: TrackObjects.CenterDistance(o, segmentStarts)))
            .Select(p => (p.Object, p.Distance, Footing: Footing(p.Object, rings, p.Distance, gaps)))
            .Where(p => p.Footing.Placed)
            .ToList();
        if (pillars.Count == 0)
            return null;

        var chunk = new MeshChunk { Name = name };
        var material = chunk.MaterialIndex(Surfaces.Pillar);
        // Lightmap: one square cell per pillar, sides on the left, top on the right.
        var cells = (int)Math.Ceiling(Math.Sqrt(pillars.Count));
        var cell = 1.0 / cells;
        Vector3 F(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        for (var i = 0; i < pillars.Count; i++)
        {
            var (o, distance, (across, _)) = pillars[i];
            var ring = TrackObjects.RingAt(rings, distance);
            var (center, up) = TrackObjects.SurfacePoint(ring, across);
            var forward = (ring.Tangent - up * Vec3d.Dot(ring.Tangent, up)).Normalized();
            var right = Vec3d.Cross(forward, up).Normalized();
            var stretch = TrackObjects.AlongStretch(rings, distance, across);
            // The editor's stand-in for the game pillar the export places: round, its diameter, 7 m tall.
            var size = GameSize(o.Width);
            var (hl, hw) = (size / 2, size / 2);
            var height = GameHeight;
            var shape = ObjectShape.Round;

            // Outline in the pillar's (along, across) frame with outward normals; rect corners are doubled so every
            // face has its own normals.
            var outline = new List<(double A, double C, double NA, double NC)>();
            if (shape == ObjectShape.Rect)
            {
                var corners = new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) };
                var normals = new[] { (0.0, -1.0), (1.0, 0.0), (0.0, 1.0), (-1.0, 0.0) };
                for (var f = 0; f < 4; f++)
                {
                    var (a, b) = (corners[f], corners[(f + 1) % 4]);
                    outline.Add((a.Item1 * hl, a.Item2 * hw, normals[f].Item1, normals[f].Item2));
                    outline.Add((b.Item1 * hl, b.Item2 * hw, normals[f].Item1, normals[f].Item2));
                }
            }
            else
                for (var k = 0; k <= RoundSegments; k++)
                {
                    var a = 2 * Math.PI * k / RoundSegments;
                    var (na, nc) = (Math.Cos(a) / hl, Math.Sin(a) / hw);
                    var len = Math.Sqrt(na * na + nc * nc);
                    outline.Add((Math.Cos(a) * hl, Math.Sin(a) * hw, na / len, nc / len));
                }

            // The foot of each outline point sits on the surface where it is; the top is flat.
            Vec3d Foot((double A, double C, double, double) p) =>
                TrackObjects.SurfacePoint(TrackObjects.RingAt(rings, distance + p.A / stretch), across + p.C, -Sink).Point - origin;
            Vec3d Top((double A, double C, double, double) p) => center + up * height + forward * p.A + right * p.C - origin;

            var (cx, cy) = (i % cells * cell, i / cells * cell);
            var lengths = new double[outline.Count];
            for (var k = 1; k < outline.Count; k++)
                lengths[k] = lengths[k - 1] + Math.Sqrt(Math.Pow(outline[k].A - outline[k - 1].A, 2) + Math.Pow(outline[k].C - outline[k - 1].C, 2));
            var perimeter = Math.Max(1e-9, lengths[^1]);
            var pairs = shape == ObjectShape.Rect ? Enumerable.Range(0, 4).Select(f => (2 * f, 2 * f + 1)) : Enumerable.Range(0, RoundSegments).Select(k => (k, k + 1));
            foreach (var (ka, kb) in pairs)
            {
                var first = chunk.Positions.Count;
                foreach (var k in new[] { ka, kb })
                {
                    var p = outline[k];
                    var n = F((forward * p.NA + right * p.NC).Normalized());
                    var lu = cx + cell * (0.03 + 0.7 * lengths[k] / perimeter);
                    foreach (var (pos, v) in new[] { (Foot(p), 0.0), (Top(p), 1.0) })
                    {
                        chunk.Positions.Add(F(pos));
                        chunk.Normals.Add(n);
                        chunk.Uv0.Add(new Vector2((float)(lengths[k] / settings.TextureScale), (float)(v * (height + Sink) / settings.TextureScale)));
                        chunk.Uv1.Add(new Vector2((float)lu, (float)(cy + cell * (0.03 + 0.94 * v))));
                        chunk.Surface.Add(MeshChunk.NoSurface);
                    }
                }
                // first, first+1: foot/top at ka; first+2, first+3: foot/top at kb.
                var outward = F((forward * (outline[ka].NA + outline[kb].NA) + right * (outline[ka].NC + outline[kb].NC)).Normalized());
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 2, first + 3, material), outward);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 3, first + 1, material), outward);
            }

            // Flat top.
            var capCenter = chunk.Positions.Count;
            var upF = F(up);
            void CapVertex(Vec3d pos, double fa, double fc)
            {
                chunk.Positions.Add(F(pos));
                chunk.Normals.Add(upF);
                chunk.Uv0.Add(new Vector2((float)(fa * hl / settings.TextureScale), (float)(fc * hw / settings.TextureScale)));
                chunk.Uv1.Add(new Vector2((float)(cx + cell * (0.85 + 0.06 * (fa + 1))), (float)(cy + cell * (0.03 + 0.06 * (fc + 1)))));
                chunk.Surface.Add(MeshChunk.NoSurface);
            }
            CapVertex(center + up * height - origin, 0, 0);
            var rim = outline.Where((_, k) => shape == ObjectShape.Rect ? k % 2 == 0 : k < RoundSegments).ToList();
            foreach (var p in rim)
                CapVertex(Top(p), p.A / hl, p.C / hw);
            for (var k = 0; k < rim.Count; k++)
                MeshChunk.AddFacing(chunk, new Triangle(capCenter, capCenter + 1 + k, capCenter + 1 + (k + 1) % rim.Count, material), upF);
        }
        return chunk;
    }
}
