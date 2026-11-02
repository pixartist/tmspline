using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

public readonly record struct Triangle(int A, int B, int C, int Material, int Segment = 0);

/// <summary>
/// One exportable mesh (becomes one item). Positions are in the item's local frame: for normal chunks that is the
/// shared track origin without rotation (so seams between chunks are bit-identical); chunks with a directional
/// effect (boost) are rotated so their local +Z points along the effect direction.
/// </summary>
public sealed class MeshChunk
{
    public required string Name { get; init; }
    /// <summary>Item pivot relative to the track origin.</summary>
    public Vec3d ItemOffset { get; init; } = Vec3d.Zero;
    public ItemFrame Frame { get; init; } = ItemFrame.Identity;
    public Effect Effect { get; init; } = Effect.None;
    /// <summary>For directional effects: where (track space, chunk middle) and which way the effect pushes.</summary>
    public Vec3d EffectCenter { get; init; }
    public Vec3d EffectDirection { get; init; }
    /// <summary>The effect direction before its tilt, i.e. in the road plane (the preview draws boosts with it).</summary>
    public Vec3d EffectRoadDirection { get; init; }

    /// <summary>Race waypoint this item is (Item.xml Waypoint type), or None for road pieces.</summary>
    public Waypoint Waypoint { get; init; }
    /// <summary>Color of the placed item (the game's palette); what it tints depends on the materials.</summary>
    public ItemColor Color { get; set; }
    /// <summary>Invisible trigger meshes (exported as "_trigger_…" objects without materials).</summary>
    public List<MeshChunk> Triggers { get; } = new();
    /// <summary>Visible but non-collidable meshes (exported as "_notcollidable_…" objects, with their own materials).</summary>
    public List<MeshChunk> NotCollidable { get; } = new();

    /// <summary>All materials of this item: its own and those of its non-collidable parts (for the MeshParams).</summary>
    public IEnumerable<TmMaterial> AllMaterials => Materials.Concat(NotCollidable.SelectMany(c => c.Materials)).Distinct();
    /// <summary>Spawn points (exported as empty "_socket_start" objects): item-local position and rotation in degrees (FBX XYZ order).</summary>
    public List<(Vec3d Position, Vec3d RotationDegrees)> Sockets { get; } = new();
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> Uv0 { get; } = new();
    public List<Vector2> Uv1 { get; } = new();
    /// <summary>
    /// Per vertex: (distance along the track, across) on the driving surface, or <see cref="NoSurface"/> elsewhere.
    /// Only for the editor (placing objects); builders may leave it empty.
    /// </summary>
    public List<Vector2> Surface { get; } = new();
    public List<Triangle> Triangles { get; } = new();

    public static readonly Vector2 NoSurface = new(float.NaN, float.NaN);

    /// <summary>Adds the triangle wound so that its front (counter-clockwise) side faces along <paramref name="normal"/>.</summary>
    public static void AddFacing(MeshChunk chunk, Triangle t, Vector3 normal)
    {
        var p = chunk.Positions;
        var face = Vector3.Cross(p[t.B] - p[t.A], p[t.C] - p[t.A]);
        chunk.Triangles.Add(Vector3.Dot(face, normal) >= 0 ? t : t with { B = t.C, C = t.B });
    }
    public List<TmMaterial> Materials { get; } = new();

    public int MaterialIndex(TmMaterial material)
    {
        var i = Materials.IndexOf(material);
        if (i >= 0) return i;
        Materials.Add(material);
        return Materials.Count - 1;
    }
}

public sealed class RoadMeshSettings
{
    public double Thickness { get; set; } = 0.5;
    /// <summary>
    /// Curb width, from the road's edge outwards: as the game's RoadTech curbs, which reach from x = 1.5 in the cell to
    /// the edge of the driving surface at the matching road width of 25.61 m (both matched in game by the user).
    /// </summary>
    public double BorderThickness { get; set; } = 1.695;
    /// <summary>Approximate width of one column of the driving surface, in meters.</summary>
    public double ColumnWidth { get; set; } = 4.0;
    /// <summary>Maximum angle between two columns of a bent surface (pipes, sausages), in degrees.</summary>
    public double MaxColumnAngleDegrees { get; set; } = 7.5;
    /// <summary>Chunks are split at the first ring past this length. Use <see cref="double.PositiveInfinity"/> for a single item.</summary>
    public double MaxChunkLength { get; set; } = 128.0;
    /// <summary>Texture tiling for the base UV layer, in meters per UV unit.</summary>
    public double TextureScale { get; set; } = 8.0;
    /// <summary>Length of one road texture repeat along the track, in meters. Across the road the texture always spans the full width.</summary>
    public double RoadTextureLength { get; set; } = 32.0;
    /// <summary>
    /// Share of the road texture (0..0.5) left out on each side, for the Road* materials. Their textures run on under the
    /// curbs, with dark bands at their edges; the texture is stretched across the road, so a fixed share hides them at
    /// any width. 9 % of a 26.25 m span was the best of the candidates on all four road surfaces in game
    /// (tools/RoadInset); the texture then still ran 0.32 m under each curb. Over the 25.61 m driving surface alone the
    /// same layout (meters per texture and where it is cut off) is 10 %.
    /// </summary>
    public double RoadTextureInset { get; set; } = 0.1;
    /// <summary>Directional effects (boost) are split into chunks of at most this length, each aimed separately.</summary>
    public double EffectChunkLength { get; set; } = 8.0;
    public RotationConvention Rotation { get; set; } = RotationConvention.Default;
    /// <summary>Keep every chunk in track space (no item offset or rotation), e.g. for the editor preview.</summary>
    public bool WorldSpace { get; set; }
    /// <summary>Material of the see-through plane inside checkpoint/finish gates (null = no plane).</summary>
    public TmMaterial? GateCurtain { get; set; } = Surfaces.GateCurtain;
}

public static class RoadMeshBuilder
{
    /// <summary>
    /// An edge of the cross-section in ring-local (right, up) coordinates: either a straight line from (X0, Y0) to
    /// (X1, Y1), or — when <see cref="Arc"/> is set — the bent road surface from surface coordinate S0 to S1,
    /// offset by Lift along its normal.
    /// </summary>
    /// <param name="DecalYaw">For the boost decal: texture rotation in radians (boost direction, positive = right).</param>
    /// <param name="DecalStart">For the boost decal: distance where the segment starts.</param>
    /// <param name="DecalPadLength">For the boost decal: length of one texture square, fitted so the segment holds whole pads.</param>
    private readonly record struct Edge(string Key, double X0, double Y0, double X1, double Y1, int Columns, TmMaterial Material,
        double DecalYaw = 0, double DecalStart = 0, double DecalPadLength = 1)
    {
        public SurfaceArc? Arc { get; init; }
    }

    /// <summary>
    /// The bent road surface, per side: flat for |s| ≤ A (middle), then a circular arc with curvature K up to |s| = B,
    /// then straight again at the angle the arc reached. s is the distance across the surface from the center
    /// (positive = right). A = 0 and B = half width give a single round arc; a short arc makes a (rounded) triangle.
    /// </summary>
    /// <param name="NormalSign">+1: normal on the driving side (top), -1: the other side (underside).</param>
    /// <param name="ColumnWidth">Meters per column on flat/straight parts.</param>
    /// <param name="MaxColumnAngle">Radians per column on the arc.</param>
    private readonly record struct SurfaceArc(double K, double A, double B, double Half, double S0, double S1, double Lift, double NormalSign,
        double ColumnWidth, double MaxColumnAngle)
    {
        /// <summary>The arc never gets shorter than this share of a side, so a 100 % sausage keeps a hint of a crown.</summary>
        private const double MinArcShare = 0.01;

        public static SurfaceArc For(Ring ring, double s0, double s1, double lift, double normalSign, RoadMeshSettings settings)
        {
            var half = ring.Width / 2;
            var a = half * Math.Clamp(ring.Flat, 0, 0.9);
            var arcLength = (half - a) * Math.Max(MinArcShare, 1 - Math.Clamp(ring.Sausage, 0, 1));
            // Each side bends by Curve / 2 over its arc.
            var k = ring.Curve / 2 / Math.Max(1e-6, arcLength);
            return new SurfaceArc(k, a, a + arcLength, half, s0, s1, lift, normalSign,
                settings.ColumnWidth, settings.MaxColumnAngleDegrees * Math.PI / 180);
        }

        /// <summary>Bend angle at s (constant past the arc), and which side (sign of s).</summary>
        private (double Angle, double Side) Bend(double s) => (K * Math.Clamp(Math.Abs(s) - A, 0, B - A), s < 0 ? -1 : 1);

        public (double X, double Y) Point(double s)
        {
            var side = s < 0 ? -1 : 1;
            var d = Math.Clamp(Math.Abs(s) - A, 0, B - A);
            var a = K * d;
            // Offset from the start of the bend; series expansion keeps it exact as the curvature goes to zero.
            var (bx, by) = Math.Abs(a) < 1e-6 ? (d, K * d * d / 2) : (Math.Sin(a) / K, (1 - Math.Cos(a)) / K);
            // Past the arc the surface continues straight along the final tangent.
            var straight = Math.Max(0, Math.Abs(s) - B);
            bx += straight * Math.Cos(a);
            by += straight * Math.Sin(a);
            var x = side * (Math.Min(Math.Abs(s), A) + bx);
            var (nx, ny) = Normal(s);
            return (x + nx * Lift, by + ny * Lift);
        }

        public (double X, double Y) Normal(double s)
        {
            var (a, side) = Bend(s);
            return (-side * Math.Sin(a), Math.Cos(a));
        }

        public (double X, double Y) Tangent(double s)
        {
            var (a, side) = Bend(s);
            return (Math.Cos(a), side * Math.Sin(a));
        }

        /// <summary>
        /// How many columns each part of one side needs: its length in columns, and for the arc at least one column
        /// per <see cref="MaxColumnAngle"/> of bend. A pure round bend or a flat road gives an even distribution.
        /// </summary>
        public (double Flat, double Arc, double Straight) Weights() =>
        (
            A / ColumnWidth,
            Math.Max((B - A) / ColumnWidth, Math.Abs(K * (B - A)) / MaxColumnAngle),
            (Half - B) / ColumnWidth
        );

        /// <summary>
        /// Surface coordinate of column fraction f (0..1 from S0 to S1). Columns are spread by need, so a narrow,
        /// sharp crown gets enough of them while flat and straight parts get few.
        /// </summary>
        public double At(double f)
        {
            if (Half <= 0)
                return S0 + (S1 - S0) * f;
            // Spread by need in "column space" between the ends' own column coordinates, so the ends land exactly on S0
            // and S1 also when the surface stops short of the full width.
            var (wf, wa, ws) = Weights();
            var (fa, fb, fh) = (A, B, Half);
            double ToT(double s)
            {
                var a = Math.Min(Math.Abs(s), fh);
                var t = a <= fa ? (fa > 0 ? a / fa * wf : 0)
                    : a <= fb ? wf + (fb > fa ? (a - fa) / (fb - fa) * wa : 0)
                    : wf + wa + (fh > fb ? (a - fb) / (fh - fb) * ws : 0);
                return s < 0 ? -t : t;
            }
            double FromT(double t)
            {
                var a = Math.Abs(t);
                var s = a <= wf ? (wf > 0 ? a / wf * fa : 0)
                    : a <= wf + wa ? fa + (wa > 0 ? (a - wf) / wa : 0) * (fb - fa)
                    : fb + (ws > 0 ? (a - wf - wa) / ws : 0) * (fh - fb);
                return (t < 0 ? -1 : 1) * Math.Min(s, fh);
            }
            if (f <= 0) return S0;
            if (f >= 1) return S1;
            var (t0, t1) = (ToT(S0), ToT(S1));
            return FromT(t0 + (t1 - t0) * f);
        }
    }

    /// <summary>
    /// Where the driving surface's right edge is in ring-local (right, up) coordinates, for the ring's width, flat share
    /// and sausageness bent by <paramref name="curve"/> (radians); see TrackSampler's edge anchoring.
    /// </summary>
    internal static (double X, double Y) SurfaceEdge(Ring ring, double curve)
    {
        var bent = ring.Clone();
        bent.Curve = curve;
        return SurfaceArc.For(bent, 0, 0, 0, 1, new RoadMeshSettings()).Point(ring.Width / 2);
    }

    /// <summary>
    /// Frame the curb on the side of surface coordinate s (the road's edge) is built in, in ring-local (right, up)
    /// coordinates: the road's edge, the direction of growing s (outward = times the side) and the curb's up. On the bent
    /// surface's edge, turned towards upright in the ring's frame by <see cref="Ring.EdgeAnchor"/> (fully: upright, and
    /// as the edge itself stays put then, so does the curb).
    /// </summary>
    private static ((double X, double Y) E, (double X, double Y) T, (double X, double Y) N) CurbFrame(Ring ring, SurfaceArc arc, double s)
    {
        var (e, t, n) = (arc.Point(s), arc.Tangent(s), arc.Normal(s));
        var k = Math.Clamp(ring.EdgeAnchor, 0, 1);
        if (k <= 0) return (e, t, n);
        var side = s < 0 ? -1 : 1;
        // The surface's bend angle at its end (see SurfaceArc.Tangent), turned back towards upright.
        var angle = Math.Atan2(t.Y * side, t.X) * (1 - k);
        return (e, (Math.Cos(angle), side * Math.Sin(angle)), (-side * Math.Sin(angle), Math.Cos(angle)));
    }

    /// <summary>
    /// A point on a curb in ring-local coordinates, with its up and outward directions: on side ±1, <paramref name="outward"/>
    /// meters out from the road's edge (its full width) and <paramref name="lift"/> up (signs, gate posts).
    /// </summary>
    internal static (double X, double Y, double NX, double NY, double TX, double TY) CurbPoint(Ring ring, int side, double outward, double lift)
    {
        var arc = SurfaceArc.For(ring, 0, 0, 0, 1, new RoadMeshSettings());
        var (e, t, n) = CurbFrame(ring, arc, side * ring.Width / 2);
        return (e.X + t.X * side * outward + n.X * lift, e.Y + t.Y * side * outward + n.Y * lift, n.X, n.Y, t.X, t.Y);
    }

    /// <summary>
    /// The curb's bottom corners on one side: the inner one on the slab's underside (so the slab stays closed), the outer
    /// one below the curb's outer face, in the curb's frame (turned curbs: never inside the inner one).
    /// </summary>
    private static ((double X, double Y) Inner, (double X, double Y) Outer) CurbBottom(Ring ring, SurfaceArc arc, double s, int side,
        double thickness, double h)
    {
        var (e, n0) = (arc.Point(s), arc.Normal(s));
        var inner = (X: e.X - n0.X * h, Y: e.Y - n0.Y * h);
        var (c, t, n) = CurbFrame(ring, arc, s);
        // The inner corner in the curb's frame (outward, from the edge); the outer corner at least as far out, always the
        // slab's thickness below the edge, so the curb's outer face does not move with the shape (the bottom face slopes).
        var u = ((inner.X - c.X) * t.X + (inner.Y - c.Y) * t.Y) * side;
        var ou = Math.Max(thickness, u);
        return (inner, (c.X + t.X * side * ou - n.X * h, c.Y + t.Y * side * ou - n.Y * h));
    }

    /// <summary>Height of the boost decal above the driving surface.</summary>
    private const double DecalLift = 0.02;

    // Lightmap atlas: every edge key gets its own non-overlapping band in U.
    private static readonly (string Key, double Weight)[] LightmapBands =
    {
        ("top", 6), ("bottom_b", 1.5), ("bottom_n", 1.5),
        ("rb_out", 0.5), ("rb_top", 0.5), ("rb_in", 0.5),
        ("lb_in", 0.5), ("lb_top", 0.5), ("lb_out", 0.5),
        ("rs", 0.5), ("ls", 0.5), ("rb_bot", 0.25), ("lb_bot", 0.25), ("decal", 0.25),
        ("cap", 0.5), ("hole", 0.5),
    };

    public static List<MeshChunk> Build(Track track, IReadOnlyList<Ring> rings, Vec3d origin, string namePrefix, RoadMeshSettings? settings = null)
    {
        settings ??= new RoadMeshSettings();
        var chunks = new List<MeshChunk>();
        if (rings.Count < 2)
            return chunks;

        var segmentRanges = SegmentRanges(rings);
        var segmentStarts = TrackSampler.SegmentStarts(track);
        var roadBumps = new RoadBumps(track, segmentStarts);
        var bumps = roadBumps.Any ? roadBumps : null;
        var ringColumns = RingColumns(track, rings, settings, bumps);
        var bands = BuildBands();
        // Small integer ids for edge keys make the per-chunk vertex cache cheap to hash.
        var keyIds = LightmapBands.Select((b, i) => (b.Key, i)).ToDictionary(x => x.Key, x => x.i);

        var cutter = new HoleCutter(TrackObjects.Holes(track, rings, segmentStarts));
        var gaps = TrackObjects.GapRanges(track, segmentStarts);
        var intervals = rings.Count - 1;
        var inGap = Enumerable.Range(0, intervals).Select(k =>
        {
            var mid = (rings[k].Distance + rings[k + 1].Distance) / 2;
            return gaps.Any(g => mid > g.Start && mid < g.End);
        }).ToArray();
        // The road ends (and gets a cap) before a gap and at an open track's own start and end.
        bool GapBefore(int k) => k > 0 ? inGap[k - 1] : !track.Closed || inGap[intervals - 1];
        bool GapAfter(int k) => k + 1 < intervals ? inGap[k + 1] : !track.Closed || inGap[0];

        foreach (var (start, end) in SplitRanges(track, rings, settings))
        {
            var chunk = CreateChunk(track, rings, start, end, origin, $"{namePrefix}_{chunks.Count:D3}", settings);
            var itemOrigin = origin + chunk.ItemOffset;
            var vertexCache = new Dictionary<long, int>();
            var chunkStart = rings[start].Distance;
            var chunkLength = Math.Max(1e-6, rings[end].Distance - chunkStart);
            // Ends of the road at gaps and open track ends: (ring, segment, facing forward, only the curbs' end faces: where borders start or
            // end, grown down to a sliver).
            var caps = new List<(int Ring, TrackSegment Segment, bool Forward, bool CurbsOnly)>();

            for (var k = start; k < end; k++)
            {
                if (inGap[k])
                    continue;
                var segment = track.Segments[rings[k].Segment];
                if (GapBefore(k)) caps.Add((k, segment, false, false));
                else if (segment.Borders && rings[k].BorderScale <= 0) caps.Add((k, segment, false, true));
                if (GapAfter(k)) caps.Add((k + 1, segment, true, false));
                else if (segment.Borders && rings[k + 1].BorderScale <= 0) caps.Add((k + 1, segment, true, true));
                var range = segmentRanges[rings[k].Segment];
                // Both rings of the interval use the interval's segment, so per-segment shape changes (borders) never
                // tear a strip mid-interval; with the same segment both profiles list the same edges in the same order.
                var edges = Profile(rings[k], segment, range, ringColumns[k], settings).ToArray();
                var edgesNext = Profile(rings[k + 1], segment, range, ringColumns[k + 1], settings).ToArray();
                for (var e = 0; e < edges.Length; e++)
                {
                    var edge = edges[e];
                    var edgeNext = edgesNext[e];
                    var material = chunk.MaterialIndex(edge.Material);
                    var band = bands[edge.Key];
                    var keyId = keyIds[edge.Key];
                    var role = edge.Key == "top" ? HoleCutter.Role.Top
                        : edge.Key.StartsWith("bottom") ? HoleCutter.Role.Bottom : HoleCutter.Role.CutOnly;
                    var cut = edge.Arc is not null && edgeNext.Arc is not null && cutter.Any;
                    var ringIndex = k;
                    // Corners are (ring offset 0 or 1, column).
                    var bumpSegment = rings[ringIndex].Segment;
                    int V((int R, int C) v) => v.R == 0
                        ? Vertex(chunk, vertexCache, rings[ringIndex], edge, keyId, ringIndex, v.C, itemOrigin, band, chunkStart, chunkLength, settings, bumps, bumpSegment)
                        : Vertex(chunk, vertexCache, rings[ringIndex + 1], edgeNext, keyId, ringIndex + 1, v.C, itemOrigin, band, chunkStart, chunkLength, settings, bumps, bumpSegment);
                    // Holes are cut in (distance, across) coordinates of the surface.
                    (double, double) P((int R, int C) v) => v.R == 0
                        ? (rings[ringIndex].Distance, edge.Arc!.Value.At((double)v.C / edge.Columns))
                        : (rings[ringIndex + 1].Distance, edgeNext.Arc!.Value.At((double)v.C / edgeNext.Columns));
                    void Tri((int, int) x, (int, int) y, (int, int) z)
                    {
                        var t = new Triangle(V(x), V(y), V(z), material, rings[ringIndex].Segment);
                        if (cut) cutter.Add(chunk, t, P(x), P(y), P(z), role);
                        else chunk.Triangles.Add(t);
                    }

                    var (m, n) = (edge.Columns, edgeNext.Columns);
                    if (m == n)
                        for (var c = 0; c < m; c++)
                        {
                            Tri((0, c), (1, c), (1, c + 1));
                            Tri((0, c), (1, c + 1), (0, c + 1));
                        }
                    else
                    {
                        // Where the column count changes (first interval of a segment): zip the two rows together by
                        // their position across the road, so both rings keep their exact vertices and nothing cracks.
                        int i = 0, j = 0;
                        while (i < m || j < n)
                        {
                            if (i < m && (j == n || (double)(i + 1) / m < (double)(j + 1) / n))
                                Tri((0, i), (1, j), (0, ++i));
                            else
                                Tri((0, i), (1, j), (1, ++j));
                        }
                    }
                }
            }

            for (var i = 0; i < caps.Count; i++)
            {
                var band = bands["cap"];
                var slice = (band.U1 - band.U0) / caps.Count;
                AddCap(chunk, rings[caps[i].Ring], caps[i].Segment, caps[i].Forward, ringColumns[caps[i].Ring], itemOrigin,
                    (band.U0 + slice * i, band.U0 + slice * (i + 1)), settings, caps[i].CurbsOnly);
            }
            chunks.Add(chunk);
        }

        cutter.BuildWalls(Surfaces.Underside, bands["hole"], settings.TextureScale);
        // Chunks that lie completely inside a gap have nothing to export.
        chunks.RemoveAll(c => c.Triangles.Count == 0);
        return chunks;
    }

    /// <summary>
    /// Columns across the driving surface at every ring. Each segment gets as many as its own cross-sections need (flat
    /// roads few, bent shapes more: at least one per <see cref="RoadMeshSettings.MaxColumnAngleDegrees"/> of bend), so a
    /// single pipe does not make the whole track heavy. A ring has one count, that of the segment arriving at it; the
    /// first interval of a segment with a different count is zipped (see Build), so the mesh stays closed.
    /// </summary>
    private static int[] RingColumns(Track track, IReadOnlyList<Ring> rings, RoadMeshSettings settings, RoadBumps? bumps = null)
    {
        int Needed(Ring r, int segment)
        {
            // With a flat middle the same bend happens over a shorter part of the width, so it needs more columns.
            var (flat, arc, straight) = SurfaceArc.For(r, 0, 0, 0, 1, settings).Weights();
            // Even, so there is always a column on the road's center line (the crown of a sausage).
            var columns = Math.Max(2, Math.Max((int)Math.Ceiling(r.Width / settings.ColumnWidth - 1e-9), 2 * (int)Math.Ceiling(flat + arc + straight - 1e-9)));
            // Bumps across (noise, ridges, bump objects): a column every `detail` meters.
            if (bumps?.Detail(segment, alongOnly: false) is { } detail)
                columns = Math.Max(columns, (int)Math.Ceiling(r.Width / detail - 1e-9));
            return columns + columns % 2;
        }

        var perSegment = new int[track.SegmentCount];
        for (var k = 0; k + 1 < rings.Count; k++)
        {
            var s = rings[k].Segment;
            perSegment[s] = Math.Max(perSegment[s], Math.Max(Needed(rings[k], s), Needed(rings[k + 1], s)));
        }
        var result = new int[rings.Count];
        for (var k = 1; k < rings.Count; k++)
            result[k] = perSegment[rings[k - 1].Segment];
        // A closed track's first ring is its last one.
        result[0] = track.Closed ? result[^1] : perSegment[rings[0].Segment];
        return result;
    }

    /// <summary>
    /// Closes the road slab where it ends at a gap: the strip between driving surface and underside, plus the curbs'
    /// cross-sections, all in the plane of the ring and facing out of the road. With <paramref name="curbsOnly"/> just the
    /// curbs' cross-sections (where borders start or end on running road).
    /// </summary>
    private static void AddCap(MeshChunk chunk, Ring ring, TrackSegment segment, bool forward, int topColumns, Vec3d itemOrigin,
        (double U0, double U1) band, RoadMeshSettings s, bool curbsOnly = false)
    {
        // As in Profile: the curbs stand on the road's edges, outside the width.
        var (bh, bt) = segment.Borders ? BorderSize(ring, s) : (0, 0);
        var half = ring.Width * 0.5;
        var h = s.Thickness;
        var top = SurfaceArc.For(ring, half, -half, 0, 1, s);
        var bottom = SurfaceArc.For(ring, -half, half, -h, -1, s);
        var points = new List<(double X, double Y)>();
        var triangles = new List<(int, int, int)>();
        for (var c = 0; c <= topColumns && !curbsOnly; c++)
        {
            var across = top.At((double)c / topColumns);
            points.Add(top.Point(across));
            points.Add(bottom.Point(across));
            if (c > 0)
            {
                var i = points.Count - 4;
                triangles.Add((i, i + 1, i + 3));
                triangles.Add((i, i + 3, i + 2));
            }
        }
        if (segment.Borders)
        {
            // Cross-sections of the curbs (as built in Profile): convex pentagons round the surface's end.
            var arc = SurfaceArc.For(ring, 0, 0, 0, 1, s);
            foreach (var sign in new[] { 1, -1 })
            {
                var (e, t, n) = CurbFrame(ring, arc, sign * half);
                (double X, double Y) At(double along, double up) => (e.X + t.X * along * sign + n.X * up, e.Y + t.Y * along * sign + n.Y * up);
                var (innerBottom, outerBottom) = CurbBottom(ring, arc, sign * half, sign, bt, h);
                var first = points.Count;
                points.AddRange(new[] { e, At(0, bh), At(bt, bh), outerBottom, innerBottom });
                for (var i = 1; i < 4; i++)
                    triangles.Add((first, first + i, first + i + 1));
            }
        }

        var normal = chunk.Frame.ToLocal(forward ? ring.Tangent : -ring.Tangent);
        var normalF = new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z);
        var (minX, maxX) = (points.Min(p => p.X), points.Max(p => p.X));
        var (minY, maxY) = (points.Min(p => p.Y), points.Max(p => p.Y));
        var m = chunk.MaterialIndex(Surfaces.Underside);
        var baseIndex = chunk.Positions.Count;
        foreach (var (x, y) in points)
        {
            var local = chunk.Frame.ToLocal(ring.Position + ring.Right * x + ring.Up * y - itemOrigin);
            chunk.Positions.Add(new Vector3((float)local.X, (float)local.Y, (float)local.Z));
            chunk.Normals.Add(normalF);
            chunk.Uv0.Add(new Vector2((float)(x / s.TextureScale), (float)(y / s.TextureScale)));
            var fu = (x - minX) / Math.Max(1e-6, maxX - minX);
            var fv = (y - minY) / Math.Max(1e-6, maxY - minY);
            chunk.Uv1.Add(new Vector2((float)(band.U0 + (band.U1 - band.U0) * (0.05 + 0.9 * fu)), (float)(0.01 + 0.98 * fv)));
            chunk.Surface.Add(MeshChunk.NoSurface);
        }
        foreach (var (a, b, c) in triangles)
            MeshChunk.AddFacing(chunk, new Triangle(baseIndex + a, baseIndex + b, baseIndex + c, m, ring.Segment), normalF);
    }

    /// <summary>
    /// Splits ring indices into chunk ranges. Adjacent ranges share their boundary ring. Segments with a directional
    /// effect always start a new chunk and are cut into short pieces so every piece can be aimed along the road.
    /// </summary>
    private static IEnumerable<(int Start, int End)> SplitRanges(Track track, IReadOnlyList<Ring> rings, RoadMeshSettings settings)
    {
        bool Directional(int ring) => track.Segments[rings[ring].Segment].IsDirectional;

        var start = 0;
        for (var i = 1; i < rings.Count; i++)
        {
            var isLast = i == rings.Count - 1;
            var maxLength = Directional(start) ? settings.EffectChunkLength : settings.MaxChunkLength;
            // Directional pieces are aimed per segment; a color change needs a new item (the color is per placed item).
            var segmentChange = rings[i].Segment != rings[start].Segment && (Directional(start) || Directional(i)
                || track.Segments[rings[i].Segment].Color != track.Segments[rings[start].Segment].Color);
            if (isLast || segmentChange || rings[i].Distance - rings[start].Distance >= maxLength)
            {
                yield return (start, i);
                start = i;
            }
        }
    }

    private static MeshChunk CreateChunk(Track track, IReadOnlyList<Ring> rings, int start, int end, Vec3d origin, string name, RoadMeshSettings settings)
    {
        var segment = track.Segments[rings[start].Segment];
        if (!segment.IsDirectional || !Surfaces.SupportsEffects(segment.Surface))
            return new MeshChunk { Name = name, Color = segment.Color };

        // Aim the piece with the road frame in its middle, turned by the effect yaw/pitch of the segment.
        var mid = rings[(start + end) / 2];
        var yaw = -segment.EffectYaw * Math.PI / 180;       // positive yaw = towards the right of the road
        var pitch = segment.EffectPitch * Math.PI / 180;    // positive pitch = up, away from the road surface
        var flat = Vec3d.Rotate(mid.Tangent, mid.Up, yaw).Normalized();
        var right = Vec3d.Rotate(mid.Right, mid.Up, yaw);
        var dir = Vec3d.Rotate(flat, right, pitch).Normalized();

        return new MeshChunk
        {
            Name = name,
            ItemOffset = settings.WorldSpace ? Vec3d.Zero : rings[start].Position - origin,
            Frame = settings.WorldSpace ? ItemFrame.Identity : ItemFrame.Facing(dir, settings.Rotation),
            Effect = segment.Effect,
            Color = segment.Color,
            EffectCenter = mid.Position - origin,
            EffectDirection = dir,
            EffectRoadDirection = flat,
        };
    }

    /// <summary>Start and end distance and the average road width of every segment.</summary>
    private static Dictionary<int, (double Start, double End, double Width)> SegmentRanges(IReadOnlyList<Ring> rings)
    {
        var ranges = new Dictionary<int, (double Start, double End, double WidthArea)>();
        for (var k = 0; k + 1 < rings.Count; k++)
        {
            var s = rings[k].Segment;
            var area = (rings[k].Width + rings[k + 1].Width) * 0.5 * (rings[k + 1].Distance - rings[k].Distance);
            ranges[s] = ranges.TryGetValue(s, out var r)
                ? (Math.Min(r.Start, rings[k].Distance), Math.Max(r.End, rings[k + 1].Distance), r.WidthArea + area)
                : (rings[k].Distance, rings[k + 1].Distance, area);
        }
        return ranges.ToDictionary(kv => kv.Key, kv =>
            (kv.Value.Start, kv.Value.End, kv.Value.WidthArea / Math.Max(1e-6, kv.Value.End - kv.Value.Start)));
    }

    /// <summary>Bounding box of the driving surface at a ring, in ring-local (right, up) coordinates.</summary>
    public static (double MinX, double MaxX, double MinY, double MaxY) SurfaceExtents(Ring ring)
    {
        var arc = SurfaceArc.For(ring, 0, 0, 0, 1, new RoadMeshSettings());
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (var i = 0; i <= 64; i++)
        {
            var (x, y) = arc.Point(-ring.Width / 2 + ring.Width * i / 64);
            (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
        }
        return (minX, maxX, minY, maxY);
    }

    /// <summary>
    /// A point on the driving surface at surface coordinate s (meters across from the center, positive = right), in
    /// ring-local (right, up) coordinates, with the surface normal and tangent (direction of increasing s) there,
    /// and the curvature (1/m, positive = concave) of the bend at that point.
    /// </summary>
    public static (double X, double Y, double NX, double NY, double TX, double TY, double Curvature) SurfaceSample(Ring ring, double s)
    {
        var arc = SurfaceArc.For(ring, 0, 0, 0, 1, new RoadMeshSettings());
        var (x, y) = arc.Point(s);
        var (nx, ny) = arc.Normal(s);
        var (tx, ty) = arc.Tangent(s);
        return (x, y, nx, ny, tx, ty, arc.K);
    }

    /// <summary>
    /// Height and thickness (outside the road width) of the
    /// curbs at a ring: where borders start or end they grow out of the road's edge (see Ring.BorderScale), from a sliver
    /// (not zero: no degenerate triangles) to full size.
    /// </summary>
    private static (double Height, double Thickness) BorderSize(Ring ring, RoadMeshSettings s)
    {
        // Where the cross-section closes (a road folding into a pipe), the two curbs face each other: they shrink with the
        // gap between the road's edges, so they never run into each other or the far wall (and a pipe's edges meet).
        var room = BorderRoom(ring, s);
        var k = Math.Max(MinBorderScale, Math.Min(ring.BorderScale, room));
        return (ring.BorderHeight * k, s.BorderThickness * k);
    }

    /// <summary>
    /// How much of the curbs fits (0..1): 1 while the road's edges are at least two curb thicknesses (plus a metre) apart
    /// or open away from each other (bends up to 180°), less as a closing cross-section brings them together.
    /// </summary>
    private static double BorderRoom(Ring ring, RoadMeshSettings s)
    {
        if (Math.Abs(ring.Curve) <= Math.PI) return 1;
        var arc = SurfaceArc.For(ring, 0, 0, 0, 1, s);
        var half = ring.Width * 0.5;
        var (a, b) = (arc.Point(half), arc.Point(-half));
        var gap = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        return Math.Clamp(gap / (2 * s.BorderThickness + 1), 0, 1);
    }

    private const double MinBorderScale = 0.02;

    /// <summary>Step of the finite differences that tilt the normals of a bumpy surface, meters.</summary>
    private const double BumpStep = 0.2;

    private static IEnumerable<Edge> Profile(Ring ring, TrackSegment segment, (double Start, double End, double Width) range, int topColumns, RoadMeshSettings s)
    {
        var width = ring.Width * 0.5;
        var h = s.Thickness;
        var road = Surfaces.Road(segment.Surface, segment.Effect);
        // The curbs stand on the road's edges, outside the width.
        var (bh, bt) = segment.Borders ? BorderSize(ring, s) : (0, 0);
        var half = width;
        // The driving surface goes right to left; its normal is explicit (see SurfaceArc).
        Edge Surface(string key, double lift, double sign, TmMaterial material, double s0, double s1) =>
            new(key, 0, 0, 0, 0, topColumns, material) { Arc = SurfaceArc.For(ring, s0, s1, lift, sign, s) };

        yield return Surface("top", 0, 1, road, half, -half);
        if (road.GameplayId is not null && Surfaces.Decal(segment.Effect) is { } decal)
        {
            // Whole pads per segment: about one road width each, stretched slightly to fit exactly.
            var length = Math.Max(1e-6, range.End - range.Start);
            var pads = Math.Max(1, Math.Round(length / range.Width));
            yield return Surface("decal", DecalLift, 1, decal, half, -half) with
            {
                DecalYaw = segment.EffectYaw * Math.PI / 180, DecalStart = range.Start, DecalPadLength = length / pads,
            };
        }
        if (segment.Underside)
            yield return Surface(segment.Borders ? "bottom_b" : "bottom_n", -h, -1, Surfaces.Underside, -half, half);

        // Curbs and sides are built in the local frame of each surface end: outward along the tangent,
        // up along the normal. Straight edges are ordered so that the outward normal is (dy, -dx).
        var arc = SurfaceArc.For(ring, 0, 0, 0, 1, s);
        var (er, tr, nr) = CurbFrame(ring, arc, half);
        var (el, tl, nl) = CurbFrame(ring, arc, -half);
        (double X, double Y) At((double X, double Y) e, (double X, double Y) t, double along, (double X, double Y) n, double up) =>
            (e.X + t.X * along + n.X * up, e.Y + t.Y * along + n.Y * up);
        Edge Line(string key, (double X, double Y) a, (double X, double Y) b, TmMaterial m) => new(key, a.X, a.Y, b.X, b.Y, 1, m);

        if (segment.Borders)
        {
            var border = Surfaces.BorderOf(segment.BorderMaterial);
            // Right end: outward = +tangent. Left end: outward = -tangent. The bottom corners: see CurbBottom.
            var (rIn, rOut) = CurbBottom(ring, arc, half, 1, bt, h);
            var (lIn, lOut) = CurbBottom(ring, arc, -half, -1, bt, h);
            yield return Line("rb_out", rOut, At(er, tr, bt, nr, bh), border);
            yield return Line("rb_top", At(er, tr, bt, nr, bh), At(er, tr, 0, nr, bh), border);
            yield return Line("rb_in", At(er, tr, 0, nr, bh), er, border);
            if (segment.Underside)
                yield return Line("rb_bot", rIn, rOut, Surfaces.Underside);
            yield return Line("lb_in", el, At(el, tl, 0, nl, bh), border);
            yield return Line("lb_top", At(el, tl, 0, nl, bh), At(el, tl, -bt, nl, bh), border);
            yield return Line("lb_out", At(el, tl, -bt, nl, bh), lOut, border);
            if (segment.Underside)
                yield return Line("lb_bot", lOut, lIn, Surfaces.Underside);
        }
        else if (segment.Underside)
        {
            // Plain sides close the slab along the surface's own normal, also with upright borders.
            yield return Line("rs", CurbBottom(ring, arc, half, 1, 0, h).Inner, er, Surfaces.Underside);
            yield return Line("ls", el, CurbBottom(ring, arc, -half, -1, 0, h).Inner, Surfaces.Underside);
        }
    }

    private static int Vertex(MeshChunk chunk, Dictionary<long, int> cache, Ring ring, Edge edge, int keyId, int ringIndex, int col,
        Vec3d itemOrigin, (double U0, double U1) band, double chunkStart, double chunkLength, RoadMeshSettings s,
        RoadBumps? bumps = null, int segment = -1)
    {
        var cacheKey = ((long)keyId << 48) | ((long)ringIndex << 16) | (uint)col;
        if (cache.TryGetValue(cacheKey, out var existing))
            return existing;

        var f = (double)col / edge.Columns;
        double x, y, nx, ny, across, len;
        Vec3d? bumpNormal = null;
        if (edge.Arc is { } arc)
        {
            var sAt = arc.At(f);
            (x, y) = arc.Point(sAt);
            (nx, ny) = arc.Normal(sAt);
            (nx, ny) = (nx * arc.NormalSign, ny * arc.NormalSign);
            across = sAt;
            len = Math.Abs(arc.S1 - arc.S0);
            // Bumps lift the driving surface (and the boost decal on it) along its normal; the normal tilts with their
            // slope (finite differences along and across).
            if (bumps is not null && edge.Key is "top" or "decal")
            {
                var half = Math.Max(Math.Abs(arc.S0), Math.Abs(arc.S1));
                double D(double d, double a) => bumps.At(segment, d, a, half);
                var lift = D(ring.Distance, sAt);
                if (lift != 0 || D(ring.Distance + BumpStep, sAt) != 0 || D(ring.Distance, sAt + BumpStep) != 0)
                {
                    var slopeAcross = (D(ring.Distance, sAt + BumpStep) - D(ring.Distance, sAt - BumpStep)) / (2 * BumpStep);
                    var slopeAlong = (D(ring.Distance + BumpStep, sAt) - D(ring.Distance - BumpStep, sAt)) / (2 * BumpStep);
                    var (tx, ty) = arc.Tangent(sAt);
                    var n = ring.Right * nx + ring.Up * ny;
                    var acrossDir = ring.Right * tx + ring.Up * ty;
                    bumpNormal = (n - acrossDir * slopeAcross - ring.Tangent * slopeAlong).Normalized();
                    (x, y) = (x + nx * lift, y + ny * lift);
                }
            }
        }
        else
        {
            x = edge.X0 + (edge.X1 - edge.X0) * f;
            y = edge.Y0 + (edge.Y1 - edge.Y0) * f;
            var dx = edge.X1 - edge.X0;
            var dy = edge.Y1 - edge.Y0;
            len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
            (nx, ny) = (dy / len, -dx / len);
            across = x;
        }

        var world = ring.Position + ring.Right * x + ring.Up * y;
        var local = chunk.Frame.ToLocal(world - itemOrigin);
        var normal = chunk.Frame.ToLocal(bumpNormal ?? (ring.Right * nx + ring.Up * ny).Normalized());

        // Trackmania road textures run along U, so U follows the track and V goes across it.
        // Road textures are authored for exactly one road width (V 0..1).
        var isRoad = edge.Key == "top";
        var u0 = ring.Distance / (isRoad ? s.RoadTextureLength : s.TextureScale);
        // Road texture across: the position on the surface, not the column fraction (columns are spread by need).
        var (t0, t1) = edge.Arc is { } surface ? (surface.S0, surface.S1) : (0, 0);
        var acrossFraction = edge.Arc is not null && t1 != t0 ? (across - t0) / (t1 - t0) : f;
        var inset = edge.Material.Link.StartsWith("Road") ? s.RoadTextureInset : 0;
        var v0 = isRoad ? inset + acrossFraction * (1 - 2 * inset) : len * f / s.TextureScale;
        if (edge.Key == "decal")
        {
            // Boost texture: one square per road width, chevrons along +V, U growing to the left of +V.
            // Rotated by the boost yaw so the chevrons point where the boost pushes.
            // Across: the actual road width at this ring, so the pad edges follow width changes.
            // Along: whole pads over the segment, so the chevrons never get squeezed or cut off.
            var acrossNorm = -across / ring.Width;
            var along = (ring.Distance - edge.DecalStart) / edge.DecalPadLength;
            double c = Math.Cos(edge.DecalYaw), sn = Math.Sin(edge.DecalYaw);
            u0 = acrossNorm * c + along * sn + 0.5;
            v0 = -acrossNorm * sn + along * c;
        }

        // Small padding inside each band keeps lightmap texels from bleeding between bands.
        var pad = (band.U1 - band.U0) * 0.05;
        var u1 = band.U0 + pad + (band.U1 - band.U0 - 2 * pad) * f;
        var v1 = 0.01 + 0.98 * (ring.Distance - chunkStart) / chunkLength;

        var index = chunk.Positions.Count;
        chunk.Positions.Add(new Vector3((float)local.X, (float)local.Y, (float)local.Z));
        chunk.Normals.Add(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z));
        chunk.Uv0.Add(new Vector2((float)u0, (float)v0));
        chunk.Uv1.Add(new Vector2((float)u1, (float)v1));
        chunk.Surface.Add(edge.Key is "top" or "decal" ? new Vector2((float)ring.Distance, (float)across) : MeshChunk.NoSurface);
        cache[cacheKey] = index;
        return index;
    }

    private static Dictionary<string, (double U0, double U1)> BuildBands()
    {
        var total = LightmapBands.Sum(b => b.Weight);
        var result = new Dictionary<string, (double, double)>();
        var u = 0.0;
        foreach (var (key, weight) in LightmapBands)
        {
            var w = weight / total;
            result[key] = (u, u + w);
            u += w;
        }
        return result;
    }
}
