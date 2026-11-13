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
    /// <summary>Clearance of the gate on pipes driven on their outside (their arch is round the outside of the tube).</summary>
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
    /// <summary>The spawn point's height over the driving surface (the car is set down from there).</summary>
    private const double SpawnHeight = 0.15;

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

    /// <summary>
    /// Where along the track (distance from its start) the gates stand, as <see cref="RingAt"/> places them: at their
    /// points, the start and finish of an open track <see cref="EndInset"/> inwards.
    /// </summary>
    public static List<double> GateDistances(Track track, double[] segmentStarts)
    {
        var length = segmentStarts[^1];
        var last = track.Points.Count - 1;
        var inset = Math.Min(EndInset, length / 3);
        return Resolve(track).Select(w =>
            !track.Closed && w.Point == 0 && w.Type == Waypoint.Start ? inset
            : !track.Closed && w.Point == last && w.Type == Waypoint.Finish ? length - inset
            : !track.Closed && w.Point == last ? length
            : segmentStarts[Math.Min(w.Point, segmentStarts.Length - 1)])
            .Concat(RaceGates(track, segmentStarts).Select(g => g.Distance)).ToList();
    }

    /// <summary>The race gate a Gate object is (its Gate is Start, Checkpoint, Finish or StartFinish), or None (an effect, a car).</summary>
    public static Waypoint RaceGate(string gate) =>
        Enum.TryParse<Waypoint>(gate, out var type) && Enum.IsDefined(type) ? type : Waypoint.None;

    /// <summary>
    /// The track's race gates that are Gate objects, with where each stands: exactly where it was put, up to the
    /// road's very ends (gates on the first and last point of an open track were moved EndInset in; as objects they
    /// are the mapper's to place).
    /// </summary>
    public static IEnumerable<(TrackObject Object, Waypoint Type, double Distance)> RaceGates(Track track, double[] segmentStarts)
    {
        foreach (var o in track.Objects)
        {
            if (o.Kind != TrackObjectKind.Gate || o.Segment < 0 || o.Segment >= segmentStarts.Length - 1) continue;
            var type = RaceGate(o.Gate);
            if (type == Waypoint.None) continue;
            yield return (o, type, TrackObjects.CenterDistance(o, segmentStarts));
        }
    }

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
            var at = track.Points[point];
            chunks.Add(BuildGate(track, ring, Math.Min(point, Math.Max(0, track.SegmentCount - 1)), type, at.GateHeight, at.GatePlane, at.CheckpointGroup,
                origin, settings, $"{namePrefix}_{point:D2}"));
        }
        // And the race gates placed as objects, anywhere on the road.
        var index = 0;
        foreach (var (o, type, distance) in RaceGates(track, TrackSampler.SegmentStarts(track)))
            chunks.Add(BuildGate(track, TrackObjects.RingAt(rings, distance), o.Segment, type, o.Height, o.Plane, o.CheckpointGroup,
                origin, settings, $"{namePrefix}_G{++index:D2}"));
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

    /// <summary>
    /// A gate of our own for an effect or a car switch (GateObjects), at any place on the road: built like a
    /// checkpoint's (the same outline round the road's cross-section, in the effect's colour, with the effect's
    /// see-through plane and a trigger across the road), without a waypoint or a spawn point. Its trigger is what
    /// gives the effect, once the exporter has made the item a gate (MeshChunk.TriggerGameplay).
    /// </summary>
    public static MeshChunk BuildEffectGate(Track track, Ring ring, int segment, string gate, double gateHeight, Vec3d origin, RoadMeshSettings settings, string name)
    {
        var frame = settings.WorldSpace ? ItemFrame.Identity : ItemFrame.Facing(ring.Tangent, settings.Rotation);
        var offset = settings.WorldSpace ? Vec3d.Zero : ring.Position - origin;
        var itemOrigin = origin + offset;
        segment = Math.Clamp(segment, 0, Math.Max(0, track.SegmentCount - 1));
        // A directed effect's direction, from the ring's frame into the item's.
        Vector3? direction = null;
        if (GateObjects.Direction(gate) is { } d)
        {
            var local = frame.ToLocal(ring.Right * d.Right + ring.Up * d.Up + ring.Tangent * d.Along);
            direction = Vector3.Normalize(new Vector3((float)local.X, (float)local.Y, (float)local.Z));
        }
        var chunk = new MeshChunk { Name = name, ItemOffset = offset, Frame = frame, TriggerGameplay = GateObjects.Gameplay(gate), TriggerDirection = direction };

        var (minX, maxX, minY, maxY) = RoadMeshBuilder.SurfaceExtents(ring);
        var bottom = Math.Min(minY, 0) - settings.Thickness - 0.5;
        var height = Math.Clamp(gateHeight, 1, 100);
        var top = Math.Max(maxY, 0) + height;
        var left = minX - settings.BorderThickness - 1.0 - PostSize / 2;
        var right = maxX + settings.BorderThickness + 1.0 + PostSize / 2;

        var outline = GateOutline(ring, track.Segments[segment], settings, height);
        var beams = chunk.MaterialIndex(Surfaces.EffectGate(gate));
        var place = new BoxPlacer(ring, itemOrigin, frame, outline.Count + 1);
        for (var i = 0; i + 1 < outline.Count; i++)
            place.Beam(chunk, beams, outline[i], outline[i + 1], segment);

        // The effect's see-through plane (with white vertex colours where it is one of the game's effect planes).
        var look = Surfaces.EffectGatePlane(gate);
        // How strongly the game's effect planes show: the coloured ones at half, the cars' white one at three tenths (a tenth was invisible)
        // (in full they hid the road behind them; the owner's choice, 2026-11-12).
        var plane = new MeshChunk { Name = $"{chunk.Name}_curtain", VertexShade = !look.Link.Contains("TriggerFX") ? null : look.GamePath is null ? 0.5 : 0.3 };
        place.Curtain(plane, plane.MaterialIndex(look), CurtainOutline(ring, settings, outline), outline.Count, segment, CurtainTiling, true);
        chunk.NotCollidable.Add(plane);

        var trigger = new MeshChunk { Name = $"{chunk.Name}_trigger" };
        new BoxPlacer(ring, itemOrigin, frame, 1).Box(trigger, 0, new Vec3d((left + right) / 2, (bottom + top) / 2, 0),
            new Vec3d(right - left - PostSize, top - bottom, TriggerDepth), segment);
        chunk.Triggers.Add(trigger);
        return chunk;
    }

    private static MeshChunk BuildGate(Track track, Ring ring, int segment, Waypoint type, double gateHeight, bool gatePlane, int group,
        Vec3d origin, RoadMeshSettings settings, string name)
    {
        var frame = settings.WorldSpace ? ItemFrame.Identity : ItemFrame.Facing(ring.Tangent, settings.Rotation);
        var offset = settings.WorldSpace ? Vec3d.Zero : ring.Position - origin;
        var itemOrigin = origin + offset;
        segment = Math.Clamp(segment, 0, Math.Max(0, track.SegmentCount - 1));
        var chunk = new MeshChunk
        {
            Name = name, ItemOffset = offset, Frame = frame, Waypoint = type,
            WaypointOrder = type == Waypoint.Checkpoint ? Math.Max(0, group) : 0,
        };

        var (minX, maxX, minY, maxY) = RoadMeshBuilder.SurfaceExtents(ring);
        var bottom = Math.Min(minY, 0) - settings.Thickness - 0.5;
        var height = Math.Clamp(gateHeight, 1, 100);
        var top = Math.Max(maxY, 0) + height;
        var left = minX - settings.BorderThickness - 1.0 - PostSize / 2;
        var right = maxX + settings.BorderThickness + 1.0 + PostSize / 2;

        // The gate follows the road's cross-section: posts on the borders and an arch at a constant distance over the
        // surface, or a ring around the outside of pipes. It is a chain of beams along that outline.
        var outline = GateOutline(ring, track.Segments[segment], settings, height);
        var gate = chunk.MaterialIndex(Surfaces.Gate(type));
        // One lightmap row per beam, plus one for the see-through plane.
        var place = new BoxPlacer(ring, itemOrigin, frame, outline.Count + 1);
        for (var i = 0; i + 1 < outline.Count; i++)
            place.Beam(chunk, gate, outline[i], outline[i + 1], segment);

        // See-through plane where the trigger is, like the native gates: where the point asks for it.
        if (settings.GateCurtain is { } curtain && gatePlane && type is Waypoint.Checkpoint or Waypoint.Finish or Waypoint.StartFinish)
        {
            // Its own "_notcollidable_" object as the docs prescribe; pass-through really comes from the material itself.
            var plane = new MeshChunk { Name = $"{chunk.Name}_curtain" };
            // The game's own effect is the finish's (red) on finish gates.
            var effect = curtain == Surfaces.GateCurtain && type != Waypoint.Checkpoint ? Surfaces.GateCurtainFinish : curtain;
            place.Curtain(plane, plane.MaterialIndex(effect), CurtainOutline(ring, settings, outline), outline.Count, segment, CurtainTiling, effect.GamePath is not null);
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
            // On the driving surface in its middle (a road bent across lies above or below the ring's own point there),
            // standing as the surface does.
            var middle = RoadMeshBuilder.SurfaceSample(ring, 0);
            var normal = ring.Right * middle.NX + ring.Up * middle.NY;
            var position = frame.ToLocal(ring.Right * middle.X + ring.Up * middle.Y + normal * SpawnHeight);
            var up = frame.ToLocal(normal);
            var roll = Math.Atan2(-up.X, up.Y) * 180 / Math.PI;
            chunk.Sockets.Add((position, new Vec3d(0, 0, roll)));
        }
        return chunk;
    }

    /// <summary>
    /// The gate outline in the ring plane (right, up). Pipes get a closed ring around the outside of the tube; other
    /// roads get a frame upright in the point's frame: posts straight up from the borders (or road edges) and a beam
    /// across.
    /// </summary>
    /// <param name="height">How high the beam lies over the road's lowest place, meters.</param>
    private static List<(double X, double Y)> GateOutline(Ring ring, TrackSegment segment, RoadMeshSettings settings, double height)
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

        var borders = segment.Borders;
        // On the border, also where it is still growing (see Ring.BorderScale).
        var baseLift = borders ? ring.BorderHeight * ring.BorderScale : 0;
        // Posts in the middle of the curb, which reaches from the road's edge to its thickness outside.
        var outward = borders ? settings.BorderThickness * ring.BorderScale / 2 : -BeamWidth / 2;

        // Post feet on the curbs (upright curbs: see RoadMeshBuilder.CurbPoint); without borders at the road's edge.
        (double X, double Y) Foot(int side)
        {
            if (!borders) return Offset(side * half, baseLift, side * outward);
            var c = RoadMeshBuilder.CurbPoint(ring, side, outward, baseLift);
            return (c.X, c.Y);
        }
        // A plain frame, upright in the point's own frame whatever the road's sides do: a post straight up from each
        // foot and a beam across, `height` over the road's lowest place (and clear of the feet, on a road with high
        // sides). An arch following the surface folded up in a bowl's corners.
        // The posts stand outside the road: beside its widest place (the curbs' outer sides with borders), from under
        // its edge up.
        var samples = Enumerable.Range(0, 33).Select(i => RoadMeshBuilder.SurfaceSample(ring, -half + ring.Width * i / 32)).ToList();
        var lowest = samples.Min(p => p.Y);
        var beside = (borders ? settings.BorderThickness * ring.BorderScale : 0) + BeamWidth / 2 + 0.15;
        // A bar from the road's edge out to each post's foot holds the frame to the road: on a road bent downwards
        // (its edges tucked in under its widest place) the posts stand well clear of the edges.
        ((double X, double Y) Edge, (double X, double Y) Foot) Post(int side)
        {
            var edge = RoadMeshBuilder.SurfaceSample(ring, side * half);
            var outer = edge.X >= 0 ? samples.Max(p => p.X) + beside : samples.Min(p => p.X) - beside;
            // Under the road: the bar's top lies against the road's underside. (At half the road's thickness its upper
            // half stood out of the driving surface inside the border.)
            var y = edge.Y - settings.Thickness - BeamWidth / 2 - 0.02;
            return ((edge.X, y), (outer, y));
        }
        var (right, left) = (Post(1), Post(-1));
        var top = Math.Max(lowest + height, Math.Max(Foot(1).Y, Foot(-1).Y) + BeamWidth);
        points.Add(right.Edge);
        points.Add(right.Foot);
        points.Add((right.Foot.X, top));
        points.Add((left.Foot.X, top));
        points.Add(left.Foot);
        points.Add(left.Edge);
        return points;
    }

    /// <summary>
    /// Pairs of points (road surface, top) spanning the see-through plane. For pipes the top is the pipe's center, so
    /// the plane becomes a disc filling the tube; otherwise it reaches from the surface up to the arch.
    /// </summary>
    /// <param name="outline">The gate's outline (GateOutline): post foot, arch, post foot.</param>
    private static List<((double X, double Y) Bottom, (double X, double Y) Top)> CurtainOutline(Ring ring, RoadMeshSettings settings, List<(double X, double Y)> outline)
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

        // From the surface straight up to the beam, between the posts.
        if (outline.Count < 6) return pairs;
        double top = outline[2].Y - BeamWidth / 2, from = Math.Min(outline[2].X, outline[3].X), to = Math.Max(outline[2].X, outline[3].X);
        // As wide as the frame: from post to post, over the bars at their feet and over the road between them.
        pairs.Add((outline[1], (outline[1].X, top)));
        for (var i = 0; i <= ArchSegments; i++)
        {
            var bottom = Offset(half - ring.Width * i / ArchSegments, 0.05);
            pairs.Add((bottom, (Math.Clamp(bottom.X, from, to), Math.Max(top, bottom.Y))));
        }
        pairs.Add((outline[4], (outline[4].X, top)));
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
        /// <param name="effect">
        /// False: glass, its texture `tiling` times across and up. True: the game's checkpoint effect, laid out as on
        /// the game's own gates (GateExpandable's planes: u from 0.138 to 0.978 across a panel 8 m wide, v from 0.032 to
        /// 0.963 over 8 m of height; left of 0.138 the texture holds a bright beam), in panels of about 8 m.
        /// </param>
        public void Curtain(MeshChunk chunk, int material, List<((double X, double Y) Bottom, (double X, double Y) Top)> pairs, int row, int segment, float tiling, bool effect = false)
        {
            Vector3 Local((double X, double Y) p)
            {
                var world = ring.Position + ring.Right * p.X + ring.Up * p.Y;
                var l = frame.ToLocal(world - itemOrigin);
                return new Vector3((float)l.X, (float)l.Y, (float)l.Z);
            }
            static double Far((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            static (double X, double Y) Mix((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

            // Where each pair lies along the strip (by its bottom line), metres.
            var at = new double[pairs.Count];
            for (var i = 1; i < pairs.Count; i++) at[i] = at[i - 1] + Far(pairs[i - 1].Bottom, pairs[i].Bottom);
            var width = Math.Max(at[^1], 1e-6);
            ((double X, double Y) Bottom, (double X, double Y) Top) At(double d)
            {
                var i = 1;
                while (i + 1 < pairs.Count && at[i] < d) i++;
                var t = at[i] > at[i - 1] ? Math.Clamp((d - at[i - 1]) / (at[i] - at[i - 1]), 0, 1) : 0;
                return (Mix(pairs[i - 1].Bottom, pairs[i].Bottom, t), Mix(pairs[i - 1].Top, pairs[i].Top, t));
            }

            // The strip in panels, each with the texture once across: (pair, where along the strip, u).
            var panels = new List<List<(((double X, double Y) Bottom, (double X, double Y) Top) Pair, double At, float U)>>();
            var count = effect ? Math.Max(1, (int)Math.Round(width / 8)) : 1;
            for (var k = 0; k < count; k++)
            {
                double from = width * k / count, to = width * (k + 1) / count;
                float U(double d) => !effect ? (float)(d / width) * tiling : 0.138f + 0.84f * (float)((d - from) / (to - from));
                var panel = new List<(((double X, double Y), (double X, double Y)), double, float)> { (At(from), from, U(from)) };
                for (var i = 0; i < pairs.Count; i++)
                    if (at[i] > from + 1e-4 && at[i] < to - 1e-4) panel.Add((pairs[i], at[i], U(at[i])));
                panel.Add((At(to), to, U(to)));
                panels.Add(panel);
            }

            foreach (var side in new[] { 1, -1 })
            {
                var n = frame.ToLocal(ring.Tangent * side);
                var normal = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
                foreach (var panel in panels)
                {
                    var b = chunk.Positions.Count;
                    foreach (var (pair, d, u0) in panel)
                    {
                        var f = (float)(d / width);
                        var high = !effect ? tiling : 0.032f + 0.931f * (float)(Far(pair.Bottom, pair.Top) / 8);
                        var low = !effect ? 0 : 0.032f;
                        foreach (var (p, v, up) in new[] { (pair.Bottom, low, 0f), (pair.Top, high, 1f) })
                        {
                            chunk.Positions.Add(Local(p));
                            chunk.Normals.Add(normal);
                            chunk.Uv0.Add(new Vector2(u0, v));
                            // Each side gets half of the reserved lightmap row.
                            var u = (side > 0 ? 0.02f : 0.52f) + 0.46f * f;
                            chunk.Uv1.Add(new Vector2(u, (row + 0.1f + 0.8f * up) / _rows));
                        }
                    }
                    for (var i = 0; i + 1 < panel.Count; i++)
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
