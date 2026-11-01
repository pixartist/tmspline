using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Race gates (start, checkpoints, finish) at spline points. Each gate is one waypoint item: a colored arch outside
/// the road, an invisible trigger across it (checkpoints, finish) and a spawn point on the road (start, checkpoints),
/// all built in the road's own frame at that point so they follow banking, width and cross-section shape.
/// </summary>
public static class WaypointBuilder
{
    private const double PostSize = 1.2;
    private const double ClearanceAbove = 6;
    /// <summary>Gate beam cross-section in the road plane, and depth along the road.</summary>
    private const double BeamWidth = 0.9, BeamDepth = 1.0;
    /// <summary>Beams the arch over the road is made of.</summary>
    private const int ArchSegments = 24;
    /// <summary>
    /// Texture repeats across the see-through plane. This fine, the GPU samples the waterfall glass at a low mip level
    /// (its average), which gives an even frosted look and hides the material's scrolling animation.
    /// </summary>
    private const float CurtainTiling = 1000;
    /// <summary>From this bend on (degrees) the road counts as a pipe and the gate is a ring around it.</summary>
    private const double PipeCurveDegrees = 300;
    private const double TriggerDepth = 4;
    private const double SpawnHeight = 0.5;

    /// <summary>
    /// Start and finish gates on the very ends of an open track move this far inwards along the road (a few car
    /// lengths), so the car never spawns or stops right at the edge.
    /// </summary>
    public const double EndInset = 16;

    /// <summary>
    /// The waypoints to build: exactly the ones the mapper placed, nothing automatic (a map may have none and get its
    /// start and finish in the game's editor).
    /// </summary>
    public static List<(int Point, Waypoint Type)> Resolve(Track track) =>
        track.Points.Select((p, i) => (Point: i, Type: p.Waypoint)).Where(w => w.Type != Waypoint.None).ToList();

    /// <param name="namePrefix">Item name prefix, so several tracks can share one item folder (tools).</param>
    public static List<MeshChunk> Build(Track track, IReadOnlyList<Ring> rings, Vec3d origin, RoadMeshSettings settings, string namePrefix = "Gate")
    {
        var chunks = new List<MeshChunk>();
        if (rings.Count < 2)
            return chunks;
        foreach (var (point, type) in Resolve(track))
        {
            var ring = RingAt(track, rings, point, type);
            if (ring is null)
                continue;
            chunks.Add(BuildGate(track, ring, point, type, origin, settings, namePrefix));
        }
        return chunks;
    }

    /// <summary>
    /// The ring for a gate at a spline point. On open tracks, start/finish gates on the first/last point are moved
    /// <see cref="EndInset"/> inwards (at most a third of the track).
    /// </summary>
    private static Ring? RingAt(Track track, IReadOnlyList<Ring> rings, int point, Waypoint type)
    {
        var last = track.Points.Count - 1;
        var length = rings[^1].Distance;
        var inset = Math.Min(EndInset, length / 3);
        if (!track.Closed && point == 0 && type == Waypoint.Start)
            return rings.First(r => r.Distance >= inset);
        if (!track.Closed && point == last && type == Waypoint.Finish)
            return rings.Last(r => r.Distance <= length - inset);
        if (!track.Closed && point == last)
            return rings[^1];
        return rings.FirstOrDefault(r => r.IsVertex && r.SegmentU == 0 && r.Segment == point);
    }

    private static MeshChunk BuildGate(Track track, Ring ring, int point, Waypoint type, Vec3d origin, RoadMeshSettings settings, string namePrefix)
    {
        var frame = settings.WorldSpace ? ItemFrame.Identity : ItemFrame.Facing(ring.Tangent, settings.Rotation);
        var offset = settings.WorldSpace ? Vec3d.Zero : ring.Position - origin;
        var itemOrigin = origin + offset;
        var segment = Math.Min(point, Math.Max(0, track.SegmentCount - 1));
        var chunk = new MeshChunk { Name = $"{namePrefix}_{point:D2}", ItemOffset = offset, Frame = frame, Waypoint = type };

        var (minX, maxX, minY, maxY) = RoadMeshBuilder.SurfaceExtents(ring);
        var bottom = Math.Min(minY, 0) - settings.Thickness - 0.5;
        var top = Math.Max(maxY, 0) + ClearanceAbove;
        var left = minX - settings.BorderThickness - 1.0 - PostSize / 2;
        var right = maxX + settings.BorderThickness + 1.0 + PostSize / 2;

        // The gate follows the road's cross-section: posts on the borders and an arch at a constant distance over the
        // surface, or a ring around the outside of pipes. It is a chain of beams along that outline.
        var outline = GateOutline(ring, track.Segments[segment], settings);
        var gate = chunk.MaterialIndex(Surfaces.Gate(type));
        // One lightmap row per beam, plus one for the see-through plane.
        var place = new BoxPlacer(ring, itemOrigin, frame, outline.Count + 1);
        for (var i = 0; i + 1 < outline.Count; i++)
            place.Beam(chunk, gate, outline[i], outline[i + 1], segment);

        // See-through plane where the trigger is, like the native gates.
        if (settings.GateCurtain is { } curtain && type is Waypoint.Checkpoint or Waypoint.Finish or Waypoint.StartFinish)
        {
            // Its own "_notcollidable_" object as the docs prescribe; pass-through really comes from the material itself.
            var plane = new MeshChunk { Name = $"{chunk.Name}_curtain" };
            place.Curtain(plane, plane.MaterialIndex(curtain), CurtainOutline(ring, settings), outline.Count, segment);
            chunk.NotCollidable.Add(plane);
        }

        // Trigger across the whole road (checkpoints and finish); no material, the importer hides it.
        if (type is Waypoint.Checkpoint or Waypoint.Finish or Waypoint.StartFinish)
        {
            var trigger = new MeshChunk { Name = $"{chunk.Name}_trigger" };
            new BoxPlacer(ring, itemOrigin, frame, 1).Box(trigger, 0, new Vec3d((left + right) / 2, (bottom + top) / 2, 0),
                new Vec3d(right - left - PostSize, top - bottom, TriggerDepth), segment);
            chunk.Triggers.Add(trigger);
        }

        // Spawn point on the road, facing along it and rolled with it (start, and respawn at checkpoints).
        if (type is Waypoint.Start or Waypoint.Checkpoint or Waypoint.StartFinish)
        {
            var position = frame.ToLocal(ring.Up * SpawnHeight);
            var up = frame.ToLocal(ring.Up);
            var roll = Math.Atan2(-up.X, up.Y) * 180 / Math.PI;
            chunk.Sockets.Add((position, new Vec3d(0, 0, roll)));
        }
        return chunk;
    }

    /// <summary>
    /// The gate outline in the ring plane (right, up). Pipes get a closed ring around the outside of the tube; other
    /// roads get posts from the borders (or road edges) up to an arch that runs at a constant distance over the
    /// surface. On tight concave bends the distance shrinks so the arch cannot fold over itself.
    /// </summary>
    private static List<(double X, double Y)> GateOutline(Ring ring, TrackSegment segment, RoadMeshSettings settings)
    {
        var half = ring.Width / 2;
        var points = new List<(double X, double Y)>();
        (double X, double Y) Offset(double s, double distance, double along = 0)
        {
            var p = RoadMeshBuilder.SurfaceSample(ring, s);
            return (p.X + p.NX * distance + p.TX * along, p.Y + p.NY * distance + p.TY * along);
        }

        if (Math.Abs(ring.Curve) >= PipeCurveDegrees * Math.PI / 180)
        {
            // Around the outside, closed all round: for a pipe below the underside, for a convex tube (driven on its
            // outside) above the road with the usual clearance.
            var outside = ring.Curve > 0 ? -(settings.Thickness + BeamWidth / 2 + 0.3) : ClearanceAbove;
            for (var i = 0; i <= ArchSegments * 2; i++)
                points.Add(Offset(-half + ring.Width * i / (ArchSegments * 2), outside));
            points.Add(points[0]);
            return points;
        }

        var curvature = RoadMeshBuilder.SurfaceSample(ring, half).Curvature;
        var clearance = curvature > 1e-6 ? Math.Min(ClearanceAbove, 0.6 / curvature) : ClearanceAbove;
        var borders = segment.Borders;
        // On the border, also where it is still growing (see Ring.BorderScale).
        var baseLift = borders ? ring.BorderHeight * ring.BorderScale : 0;
        // Posts in the middle of the curb, which reaches from its inset inside the width to its thickness outside.
        var outward = borders ? (settings.BorderThickness - settings.BorderInset) * ring.BorderScale / 2 : -BeamWidth / 2;

        points.Add(Offset(half, baseLift, outward));             // right post foot, on the border
        for (var i = 0; i <= ArchSegments; i++)                   // arch over the road, right to left
        {
            var s = half - ring.Width * i / ArchSegments;
            var along = i == 0 ? outward : i == ArchSegments ? -outward : 0;
            points.Add(Offset(s, clearance, along));
        }
        points.Add(Offset(-half, baseLift, -outward));           // left post foot
        return points;
    }

    /// <summary>
    /// Pairs of points (road surface, top) spanning the see-through plane. For pipes the top is the pipe's center, so
    /// the plane becomes a disc filling the tube; otherwise it reaches from the surface up to the arch.
    /// </summary>
    private static List<((double X, double Y) Bottom, (double X, double Y) Top)> CurtainOutline(Ring ring, RoadMeshSettings settings)
    {
        var half = ring.Width / 2;
        var pairs = new List<((double, double), (double, double))>();
        (double X, double Y) Offset(double s, double distance)
        {
            var p = RoadMeshBuilder.SurfaceSample(ring, s);
            return (p.X + p.NX * distance, p.Y + p.NY * distance);
        }

        if (ring.Curve <= -PipeCurveDegrees * Math.PI / 180)
        {
            // Convex tube: a band from the road out to the gate's ring all round.
            for (var i = 0; i <= ArchSegments * 2; i++)
            {
                var s = half - ring.Width * i / (ArchSegments * 2);
                pairs.Add((Offset(s, 0.05), Offset(s, ClearanceAbove - BeamWidth / 2)));
            }
            return pairs;
        }
        if (ring.Curve >= PipeCurveDegrees * Math.PI / 180)
        {
            // Right to left like the arch, so both sides of the strip wind like their normals.
            var samples = Enumerable.Range(0, ArchSegments * 2 + 1).Select(i => Offset(half - ring.Width * i / (ArchSegments * 2), 0.05)).ToList();
            var center = (samples.Average(p => p.X), samples.Average(p => p.Y));
            foreach (var p in samples)
                pairs.Add((p, center));
            return pairs;
        }

        var curvature = RoadMeshBuilder.SurfaceSample(ring, half).Curvature;
        var clearance = curvature > 1e-6 ? Math.Min(ClearanceAbove, 0.6 / curvature) : ClearanceAbove;
        for (var i = 0; i <= ArchSegments; i++)
        {
            var s = half - ring.Width * i / ArchSegments;
            pairs.Add((Offset(s, 0.05), Offset(s, clearance - BeamWidth / 2)));
        }
        return pairs;
    }

    /// <summary>Adds beams and boxes in the ring's frame to a chunk in item-local space.</summary>
    /// <param name="boxes">Number of boxes that will be added, to give each its own lightmap cell.</param>
    private sealed class BoxPlacer(Ring ring, Vec3d itemOrigin, ItemFrame frame, int boxes)
    {
        private readonly int _rows = Math.Max(1, boxes);

        /// <summary>A beam between two points of the ring plane, overlapping a little so joints stay closed.</summary>
        public void Beam(MeshChunk chunk, int material, (double X, double Y) a, (double X, double Y) b, int segment)
        {
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6)
                return;
            (dx, dy) = (dx / length, dy / length);
            // Axes in 3D: along the beam, across it in the ring plane, and (right-handed) against the road direction.
            var along = ring.Right * dx + ring.Up * dy;
            var across = ring.Right * -dy + ring.Up * dx;
            var center = ring.Right * ((a.X + b.X) / 2) + ring.Up * ((a.Y + b.Y) / 2);
            Oriented(chunk, material, center, along, across, -ring.Tangent, new Vec3d(length + BeamWidth, BeamWidth, BeamDepth), segment);
        }

        /// <summary>A double-sided strip between bottom/top point pairs in the ring plane (the see-through plane).</summary>
        /// <param name="row">Lightmap row reserved for it.</param>
        public void Curtain(MeshChunk chunk, int material, List<((double X, double Y) Bottom, (double X, double Y) Top)> pairs, int row, int segment)
        {
            Vector3 Local((double X, double Y) p)
            {
                var world = ring.Position + ring.Right * p.X + ring.Up * p.Y;
                var l = frame.ToLocal(world - itemOrigin);
                return new Vector3((float)l.X, (float)l.Y, (float)l.Z);
            }

            foreach (var side in new[] { 1, -1 })
            {
                var n = frame.ToLocal(ring.Tangent * side);
                var normal = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
                var b = chunk.Positions.Count;
                for (var i = 0; i < pairs.Count; i++)
                {
                    var f = (float)i / Math.Max(1, pairs.Count - 1);
                    foreach (var (p, v) in new[] { (pairs[i].Bottom, 0f), (pairs[i].Top, 1f) })
                    {
                        chunk.Positions.Add(Local(p));
                        chunk.Normals.Add(normal);
                        chunk.Uv0.Add(new Vector2(f, v) * CurtainTiling);
                        // Each side gets half of the reserved lightmap row.
                        var u = (side > 0 ? 0.02f : 0.52f) + 0.46f * f;
                        chunk.Uv1.Add(new Vector2(u, (row + 0.1f + 0.8f * v) / _rows));
                    }
                }
                for (var i = 0; i + 1 < pairs.Count; i++)
                {
                    int a0 = b + i * 2, a1 = a0 + 1, c0 = a0 + 2, c1 = a0 + 3;
                    // Wind each side so it faces its normal (forward / backward along the road).
                    if (side > 0)
                    {
                        chunk.Triangles.Add(new Triangle(a0, c0, c1, material, segment));
                        chunk.Triangles.Add(new Triangle(a0, c1, a1, material, segment));
                    }
                    else
                    {
                        chunk.Triangles.Add(new Triangle(a0, c1, c0, material, segment));
                        chunk.Triangles.Add(new Triangle(a0, a1, c1, material, segment));
                    }
                }
            }
        }

        /// <summary>A box centered at (right, up, forward) ring coordinates, aligned with the ring frame.</summary>
        public void Box(MeshChunk chunk, int material, Vec3d center, Vec3d size, int segment) =>
            Oriented(chunk, material, ring.Right * center.X + ring.Up * center.Y + ring.Tangent * center.Z,
                ring.Right, ring.Up, -ring.Tangent, size, segment);

        /// <param name="center">Offset from the ring position.</param>
        /// <param name="ax">Box axes (right-handed: ax × ay = az) — keeps the faces' winding outward.</param>
        private void Oriented(MeshChunk chunk, int material, Vec3d center, Vec3d ax, Vec3d ay, Vec3d az, Vec3d size, int segment)
        {
            var h = size * 0.5;
            var faces = new (Vector3 N, Vector3 U, Vector3 V)[]
            {
                (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
                (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
                (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
            };
            var boxIndex = chunk.Triangles.Count / 12;
            for (var f = 0; f < faces.Length; f++)
            {
                var (n, u, v) = faces[f]; // u × v = n: (0,1,2), (0,2,3) is counter-clockwise from outside
                var b = chunk.Positions.Count;
                foreach (var (a, c) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    var p = n + u * a + v * c;
                    var world = ring.Position + center + ax * (p.X * h.X) + ay * (p.Y * h.Y) + az * (p.Z * h.Z);
                    var local = frame.ToLocal(world - itemOrigin);
                    var normal = frame.ToLocal((ax * n.X + ay * n.Y + az * n.Z).Normalized());
                    chunk.Positions.Add(new Vector3((float)local.X, (float)local.Y, (float)local.Z));
                    chunk.Normals.Add(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z));
                    chunk.Uv0.Add(new Vector2((a + 1) / 2, (c + 1) / 2));
                    // Lightmap: one cell per face per box in a 6 x boxes grid.
                    chunk.Uv1.Add(new Vector2((f + 0.1f + 0.8f * (a + 1) / 2) / 6, (boxIndex + 0.1f + 0.8f * (c + 1) / 2) / _rows));
                }
                chunk.Triangles.Add(new Triangle(b, b + 1, b + 2, material, segment));
                chunk.Triangles.Add(new Triangle(b, b + 2, b + 3, material, segment));
            }
        }

    }
}
