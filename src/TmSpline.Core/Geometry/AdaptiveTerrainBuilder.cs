using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain's fine height map and its companions (TerrainMeshBuilder): all knowledge of the roads goes into a fine height map
/// and its companions, the mesh comes from those alone (AdaptiveTerrainMesher).
/// <list type="number">
/// <item>The land: the editor's grid (the roads' shape extended, smoothed, noise, its own keep-out), sampled at the fine
/// cell size.</item>
/// <item>The roads, from the same mesh the export builds: per ring whether it holds land (upright, not too steep);
/// pins along the edges (curb tops) of the road that holds land, except where another road lies below them (a bridge),
/// and across open tracks' ends; the cut (between two pinned edges); the keep-out (under road that holds no land, and
/// the room to drive on it).</item>
/// <item>The solve: the land pulled to the pins (fading out over PullDistance), kept out of the keep-out, sunk under the
/// stadium's ground where it is lower.</item>
/// <item>The fidelity: dense at the pins and under steep road, relaxing over FidelityFalloff.</item>
/// </list>
/// </summary>
public static class AdaptiveTerrainBuilder
{
    /// <summary>Fine cell size, metres.</summary>
    public const double Cell = 0.5;
    private const double PullDistance = 16, FidelityFalloff = 10, MinFidelity = 0.06;
    /// <summary>Beyond FarStart metres from every road the least fidelity falls to FarFidelity, over FarFade metres.</summary>
    private const double FarStart = 48, FarFade = 32, FarFidelity = 0.02;
    /// <summary>
    /// Edges more than this above the land are not pinned (the land stays: a road high over it), metres; the pull reaches
    /// out PullSlope metres per metre the land rises (at least PullDistance), a bank rather than a spire.
    /// </summary>
    private const double PinReach = 24, PullSlope = 1.5;
    /// <summary>
    /// Under road that holds land but is not cut out (a bridge), the land is raised to KeepClear under it when it is at
    /// most FillReach lower, fading out round it like the pins' pull: no open gap under a bridge.
    /// </summary>
    private const double FillReach = 8;
    /// <summary>Road of the same track within this many metres along it (at least two road widths) is the road's own cross-section.</summary>
    private const double OwnSection = 16;
    /// <summary>
    /// Off a pinned edge the land first runs on in the driving surface's direction (at most AlignSlope steep, so it stays
    /// grass), fading into its natural shape over AlignDistance metres.
    /// </summary>
    private const double AlignSlope = 0.7, AlignDistance = 20;
    /// <summary>Land more than this below a cut road (its edges' line) is under it, not at it: it is not cut, metres.</summary>
    private const double CutDepth = 4;
    /// <summary>Within this many metres of a pinned edge a cut stretch is always cut.</summary>
    private const double EdgeStrip = 4;
    /// <summary>A road whose middle lies more than this below its edges' line is hollow (a U), metres.</summary>
    private const double HollowSag = 1;
    /// <summary>The land beside a rolled-up road is read this far past its side, metres.</summary>
    private const double FlankOffset = 2;
    /// <summary>A rolled-up road needs its up at least this upright to be buried (not upside down).</summary>
    private const double RolledUp = 0.2;
    /// <summary>A rolled-up road's slot counts as closed where its edges are nearer than this, metres.</summary>
    private const double SlotClosed = 3;
    /// <summary>Land over a buried road, metres, and the least fidelity there (it must not come through the mesh's error).</summary>
    private const double Cover = 1, BuriedFidelity = 0.4;
    private const double DriveRoom = 4, KeepClear = 0.5, BridgeGap = 3, Sunk = -2;
    /// <summary>Half the stadium's floor (TrackMap's 48 cells of 32 m), metres: inside, land under the ground sinks.</summary>
    private const double StadiumHalf = 768;

    /// <summary>The road geometry of one track for the builder.</summary>
    private sealed record Road(Track Track, List<Ring> Rings, MeshChunk Mesh);

    /// <summary>A pin point's way out: the slope the land leaves it at, outwards (horizontal), its distance along the road, and how far along counts as the road's own cross-section.</summary>
    private readonly record struct EdgeTilt(double Slope, Vec3d Out, double Along, double OwnReach);

    public static (TerrainField Field, List<TerrainPin> Pins) Fields(TerrainGrid grid, TrackMap map)
    {
        var roads = new List<Road>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Low));
            if (rings.Count < 2) continue;
            var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, WorldSpace = true, MaxColumnAngleDegrees = MeshQualities.ColumnAngleDegrees(MeshQuality.Low) };
            var chunks = RoadMeshBuilder.Build(track, rings, Vec3d.Zero, "T", settings);
            var merged = new MeshChunk { Name = "T" };
            foreach (var c in chunks)
            {
                var first = merged.Positions.Count;
                merged.Positions.AddRange(c.Positions);
                merged.Normals.AddRange(c.Normals);
                merged.Surface.AddRange(c.Surface.Count == c.Positions.Count ? c.Surface : Enumerable.Repeat(MeshChunk.NoSurface, c.Positions.Count));
                foreach (var t in c.Triangles) merged.Triangles.Add(t with { A = t.A + first, B = t.B + first, C = t.C + first });
            }
            roads.Add(new Road(track, rings, merged));
        }

        // The field: the editor grid's extent at the fine cell size, the land sampled from it.
        int columns = (int)Math.Ceiling((grid.Columns - 1) * grid.Cell / Cell), rows = (int)Math.Ceiling((grid.Rows - 1) * grid.Cell / Cell);
        double x0 = grid.X0, z0 = grid.Z0;
        var n = columns * rows;
        var land = new float[n];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                land[r * columns + c] = (float)SampleGrid(grid, x0 + (c + 0.5) * Cell, z0 + (r + 0.5) * Cell);
        int CellAt(double x, double z)
        {
            int c = (int)Math.Floor((x - x0) / Cell), r = (int)Math.Floor((z - z0) / Cell);
            return c < 0 || r < 0 || c >= columns || r >= rows ? -1 : r * columns + c;
        }

        // The roads: lowest point per cell (for the bridges), the keep-out of road that holds no land.
        var floor = new float[n];
        Array.Fill(floor, float.PositiveInfinity);
        // Whose lowest point that is: its road and its distance along it (a wall's own foot is no road under its edge).
        var floorRoad = new int[n];
        var floorAlong = new float[n];
        // Roads rolled up past upright walls (tubes) per ring; their points are kept apart, for the land over them. The lowest
        // point of every other road: a road crossing over a tube, which the land over the tube stays under.
        bool RolledUpAt(Road road, Ring ring)
        {
            if (Math.Abs(ring.Curve) <= Math.PI || ring.Up.Y < RolledUp) return false;
            var segment = road.Track.Segments[Math.Clamp(ring.Segment, 0, road.Track.Segments.Count - 1)];
            return (RoadMeshBuilder.EdgeContact(ring, segment, -1).Y + RoadMeshBuilder.EdgeContact(ring, segment, 1).Y) / 2 - ring.Position.Y > HollowSag;
        }
        var rolledUp = roads.Select(road => road.Rings.Select(ring => RolledUpAt(road, ring)).ToArray()).ToArray();
        var ringDistances = roads.Select(road => road.Rings.Select(ring => ring.Distance).ToArray()).ToArray();
        // The road leading into a tube is part of it: no road crossing over it.
        var ownOfTube = roads.Select((road, r) => ringDistances[r].Select(d => Enumerable.Range(0, road.Rings.Count)
            .Any(j => rolledUp[r][j] && Math.Abs(ringDistances[r][j] - d) <= Math.Max(OwnSection, road.Rings[j].Width * 2))).ToArray()).ToArray();
        var rolledPoints = new List<(int Cell, float Y, int Road, int Ring)>();
        var otherFloor = new float[n];
        Array.Fill(otherFloor, float.PositiveInfinity);
        void Lower(int k, double y, int roadIndex, double along)
        {
            var at = Array.BinarySearch(ringDistances[roadIndex], along);
            if (at < 0) at = Math.Clamp(~at, 0, ringDistances[roadIndex].Length - 1);
            if (rolledUp[roadIndex][at]) rolledPoints.Add((k, (float)y, roadIndex, at));
            else if (!ownOfTube[roadIndex][at]) otherFloor[k] = Math.Min(otherFloor[k], (float)y);
            if (y >= floor[k]) return;
            floor[k] = (float)y;
            floorRoad[k] = roadIndex;
            floorAlong[k] = (float)along;
        }
        var keepOut = new float[n];
        Array.Fill(keepOut, float.PositiveInfinity);
        // The same under road that holds land, kept apart: behind a pinned edge the road's own wall does not push the land down.
        var holdKeep = new float[n];
        Array.Fill(holdKeep, float.PositiveInfinity);
        var steep = new bool[n];
        // Per cell the lowest point of road that holds land (its underside, for the fill under bridges).
        var holdBottom = new float[n];
        Array.Fill(holdBottom, float.PositiveInfinity);
        for (var roadIndex = 0; roadIndex < roads.Count; roadIndex++)
        {
            var road = roads[roadIndex];
            var distances = road.Rings.Select(r => r.Distance).ToArray();
            bool HoldsAt(double d)
            {
                var i = Array.BinarySearch(distances, d);
                if (i < 0) i = Math.Clamp(~i, 1, distances.Length - 1) - 1;
                var j = Math.Min(distances.Length - 1, i + 1);
                return Holds(road.Rings[i]) && Holds(road.Rings[j]);
            }
            var m = road.Mesh;
            // The driving surface's points in 2 m cubes, holding or not, for the rest of the road (curbs, sides, underside).
            var near = new Dictionary<(int, int, int), (bool Holds, double Along)>();
            foreach (var t in m.Triangles)
            {
                var onSurface = !float.IsNaN(m.Surface[t.A].X) && !float.IsNaN(m.Surface[t.B].X) && !float.IsNaN(m.Surface[t.C].X);
                if (!onSurface) continue;
                var along = (double)(m.Surface[t.A].X + m.Surface[t.B].X + m.Surface[t.C].X) / 3;
                var holds = HoldsAt(along);
                var normal = System.Numerics.Vector3.Normalize(m.Normals[t.A] + m.Normals[t.B] + m.Normals[t.C]);
                foreach (var p in Points(m, t, Cell / 2))
                {
                    near[Cube(p)] = (holds, along);
                    var k = CellAt(p.X, p.Z);
                    if (k < 0) continue;
                    Lower(k, p.Y, roadIndex, along);
                    // Under any driving surface the land stays below it (where it is cut out this does not matter;
                    // where it is not, e.g. one edge unpinned, it would otherwise poke through). Not the curbs: the
                    // land meets their tops at the pins.
                    holdKeep[k] = Math.Min(holdKeep[k], (float)(p.Y - KeepClear));
                    if (holds) { holdBottom[k] = Math.Min(holdBottom[k], (float)p.Y); continue; }
                    steep[k] = true;
                    keepOut[k] = Math.Min(keepOut[k], (float)(p.Y - KeepClear));
                    // The room to drive: off the surface along its normal where that points down (upside down, walls).
                    if (normal.Y < 0.5)
                        for (var d = 0.5; d <= DriveRoom; d += 0.5)
                        {
                            var q = p + new Vec3d(normal.X, normal.Y, normal.Z) * d;
                            var j = CellAt(q.X, q.Z);
                            if (j >= 0) keepOut[j] = Math.Min(keepOut[j], (float)(q.Y - KeepClear));
                        }
                }
            }
            foreach (var t in m.Triangles)
            {
                var onSurface = !float.IsNaN(m.Surface[t.A].X) && !float.IsNaN(m.Surface[t.B].X) && !float.IsNaN(m.Surface[t.C].X);
                if (onSurface) continue;
                foreach (var p in Points(m, t, Cell / 2))
                {
                    if (!NearSurface(near, p, out var holds, out var along)) continue;
                    var k = CellAt(p.X, p.Z);
                    if (k < 0) continue;
                    Lower(k, p.Y, roadIndex, along);
                    if (!holds) keepOut[k] = Math.Min(keepOut[k], (float)(p.Y - KeepClear));
                    else holdBottom[k] = Math.Min(holdBottom[k], (float)p.Y);
                }
            }
        }

        // Pins: the edges of road that holds land, in runs; not where another road lies below (a bridge); open ends.
        var pins = new List<TerrainPin>();
        // Per pin (null: an open end) per point the slope the land leaves it at and the way out, horizontal.
        var pinTilts = new List<EdgeTilt[]?>();
        var pinRoads = new List<int>();
        var cut = new bool[n];
        // The cut's quads (between two pinned edges of consecutive rings), in 8 m buckets for the exact test.
        var quads = new List<(Vec3d A, Vec3d B, Vec3d C, Vec3d D)>();
        // Per cut cell the road's height (between its edges), and whether the land lies far below it (then it stays).
        var roadY = new float[n];
        Array.Fill(roadY, float.NegativeInfinity);
        var deep = new bool[n];
        // Which road's cross-section each cut cell belongs to.
        var cutRoad = new int[n];
        var cutAlong = new float[n];
        var cutReach = new float[n];
        var buckets = new Dictionary<(int, int), List<int>>();
        bool Bridge(Vec3d e, Vec3d outward, int roadIndex, Ring ring, double length)
        {
            // Too high over the land to hold it.
            var at = CellAt(e.X, e.Z);
            if (at >= 0 && !float.IsNaN(land[at]) && land[at] < e.Y - PinReach) return true;
            foreach (var d in new[] { 0.0, 1.0, 2.5 })
            {
                var k = CellAt(e.X + outward.X * d, e.Z + outward.Z * d);
                if (k < 0 || floor[k] >= e.Y - BridgeGap - 1) continue;
                // The road's own cross-section (a U's wall below its edge) is no road below it.
                var apart = Math.Abs(floorAlong[k] - ring.Distance);
                if (length > 0) apart = Math.Min(apart, length - apart);
                if (floorRoad[k] != roadIndex || apart > Math.Max(OwnSection, ring.Width * 2)) return true;
            }
            return false;
        }
        // A road rolled up past upright walls (a tube) is buried, unless all of it stands clear of the land (then it is a bridge).
        bool[] BuriedRings(int roadIndex)
        {
            var list = roads[roadIndex].Rings;
            var candidate = rolledUp[roadIndex];
            // Whether the land just past the road's side, straight out from its middle, comes up to about its floor.
            bool LandBeside(Ring ring, int side)
            {
                var across = new Vec3d(ring.Right.X, 0, ring.Right.Z);
                across = (across.Length > 1e-9 ? across.Normalized() : across) * side;
                for (var d = 0.0; d <= ring.Width; d += 1)
                {
                    var k = CellAt(ring.Position.X + across.X * d, ring.Position.Z + across.Z * d);
                    if (k < 0) return false;
                    if (!float.IsInfinity(floor[k])) continue;
                    var past = CellAt(ring.Position.X + across.X * (d + FlankOffset), ring.Position.Z + across.Z * (d + FlankOffset));
                    return past >= 0 && !float.IsNaN(land[past]) && land[past] >= ring.Position.Y - CutDepth;
                }
                return false;
            }
            // Decided per whole tube: no mound starting and stopping along it.
            var buried = new bool[list.Count];
            for (var start = 0; start < list.Count; start++)
            {
                if (!candidate[start]) continue;
                var end = start;
                while (end + 1 < list.Count && candidate[end + 1]) end++;
                if (Enumerable.Range(start, end - start + 1).Any(i => LandBeside(list[i], -1) || LandBeside(list[i], 1)))
                    Array.Fill(buried, true, start, end - start + 1);
                start = end;
            }
            return buried;
        }
        var rolledRings = new bool[roads.Count][];
        for (var roadIndex = 0; roadIndex < roads.Count; roadIndex++)
        {
            var road = roads[roadIndex];
            var rings = road.Rings;
            rolledRings[roadIndex] = BuriedRings(roadIndex);
            // Round a closed track the distances wrap.
            var loop = road.Track.Closed ? rings[^1].Distance : 0;
            var edges = new (Vec3d L, Vec3d R, bool PinL, bool PinR, EdgeTilt TiltL, EdgeTilt TiltR)[rings.Count];
            static EdgeTilt Tilt(Ring ring, int side, Vec3d right)
            {
                var along = RoadMeshBuilder.EdgeOutward(ring, side);
                var outward = right * side;
                // Past vertical (a wall rolling inwards) the surface has no way out: as steep as allowed.
                var run = along.X * outward.X + along.Z * outward.Z;
                return new EdgeTilt(Math.Clamp(along.Y / Math.Max(run, 1e-3), -AlignSlope, AlignSlope), outward, ring.Distance, Math.Max(OwnSection, ring.Width * 2));
            }
            for (var i = 0; i < rings.Count; i++)
            {
                var segment = road.Track.Segments[Math.Clamp(rings[i].Segment, 0, road.Track.Segments.Count - 1)];
                var l = RoadMeshBuilder.EdgeContact(rings[i], segment, -1);
                var r = RoadMeshBuilder.EdgeContact(rings[i], segment, 1);
                var right = new Vec3d(rings[i].Right.X, 0, rings[i].Right.Z);
                right = right.Length > 1e-9 ? right.Normalized() : right;
                if (rolledRings[roadIndex][i])
                {
                    // Buried: the land holds on to the edges along the open slot and lets go where the slot closes, running on
                    // over the top. It leaves the edges over the shell: no alignment with the surface.
                    var open = (l - r).Length >= SlotClosed;
                    var reach = Math.Max(OwnSection, rings[i].Width * 2);
                    edges[i] = (l, r, open, open, new EdgeTilt(double.NaN, right * -1, rings[i].Distance, reach), new EdgeTilt(double.NaN, right, rings[i].Distance, reach));
                    continue;
                }
                var holds = Holds(rings[i]);
                bool pinL = holds && !Bridge(l, right * -1, roadIndex, rings[i], loop), pinR = holds && !Bridge(r, right, roadIndex, rings[i], loop);
                // A hollow road with only one edge pinned has no cut: the land inside would hang from that edge down to under
                // the surface. Neither edge is pinned then, the land stays under the road.
                if (pinL != pinR && (l.Y + r.Y) / 2 - rings[i].Position.Y > HollowSag) pinL = pinR = false;
                edges[i] = (l, r, pinL, pinR, Tilt(rings[i], -1, right), Tilt(rings[i], 1, right));
            }
            foreach (var side in new[] { -1, 1 })
            {
                List<Vec3d>? run = null;
                List<EdgeTilt>? tilts = null;
                for (var i = 0; i < rings.Count; i++)
                {
                    var pinned = side < 0 ? edges[i].PinL : edges[i].PinR;
                    if (pinned)
                    {
                        (run ??= []).Add(side < 0 ? edges[i].L : edges[i].R);
                        (tilts ??= []).Add(side < 0 ? edges[i].TiltL : edges[i].TiltR);
                    }
                    if ((!pinned || i == rings.Count - 1) && run is { Count: >= 2 })
                    {
                        pins.Add(new TerrainPin(run));
                        pinTilts.Add([.. tilts!]);
                        pinRoads.Add(roadIndex);
                    }
                    if (!pinned) { run = null; tilts = null; }
                }
            }
            if (!road.Track.Closed)
                foreach (var i in new[] { 0, rings.Count - 1 })
                    if (edges[i].PinL && edges[i].PinR)
                    {
                        pins.Add(new TerrainPin([edges[i].L, rings[i].Position, edges[i].R]));
                        pinTilts.Add(null);
                        pinRoads.Add(-1);
                    }
            // The cut: between the two pinned edges of consecutive rings.
            for (var i = 0; i + 1 < rings.Count; i++)
                if (edges[i].PinL && edges[i].PinR && edges[i + 1].PinL && edges[i + 1].PinR)
                    FillQuad(edges[i].L, edges[i].R, edges[i + 1].R, edges[i + 1].L, roadIndex, rings[i]);
        }
        void FillQuad(Vec3d a, Vec3d b, Vec3d c, Vec3d d, int roadIndex, Ring ring)
        {
            var q = quads.Count;
            quads.Add((a, b, c, d));
            for (var bx = (int)Math.Floor(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) / 8); bx <= (int)Math.Floor(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) / 8); bx++)
                for (var bz = (int)Math.Floor(Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)) / 8); bz <= (int)Math.Floor(Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z)) / 8); bz++)
                {
                    if (!buckets.TryGetValue((bx, bz), out var list)) buckets[(bx, bz)] = list = [];
                    list.Add(q);
                }
            double minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            double minZ = Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)), maxZ = Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z));
            int c0 = Math.Max(0, (int)Math.Floor((minX - x0) / Cell)), c1 = Math.Min(columns - 1, (int)Math.Floor((maxX - x0) / Cell));
            int r0 = Math.Max(0, (int)Math.Floor((minZ - z0) / Cell)), r1 = Math.Min(rows - 1, (int)Math.Floor((maxZ - z0) / Cell));
            for (var r = r0; r <= r1; r++)
                for (var col = c0; col <= c1; col++)
                {
                    double px = x0 + (col + 0.5) * Cell, pz = z0 + (r + 0.5) * Cell;
                    var y = InTriangle(px, pz, a, b, c) ? PlaneY(px, pz, a, b, c) : InTriangle(px, pz, a, c, d) ? PlaneY(px, pz, a, c, d) : double.NaN;
                    if (double.IsNaN(y)) continue;
                    var k = r * columns + col;
                    cut[k] = true;
                    roadY[k] = Math.Max(roadY[k], (float)y);
                    cutRoad[k] = roadIndex;
                    cutAlong[k] = (float)ring.Distance;
                    cutReach[k] = (float)Math.Max(OwnSection, ring.Width * 2);
                }
        }

        // The columns of a buried road and its top: the land lies over it, nothing but another road crossing above holds it down.
        var rolled = new bool[n];
        var roof = new float[n];
        Array.Fill(roof, float.NegativeInfinity);
        foreach (var (cell, y, road, ring) in rolledPoints)
            if (rolledRings[road][ring])
            {
                rolled[cell] = true;
                roof[cell] = Math.Max(roof[cell], y);
            }
            else otherFloor[cell] = Math.Min(otherFloor[cell], y);
        // Where another road passes above a buried one, the height the land stays under. Below the buried road's top it is that
        // road's own continuation or a road under it: no limit.
        float UnderCrossing(int i) => otherFloor[i] > roof[i] ? otherFloor[i] - (float)KeepClear : float.PositiveInfinity;

        // The solve: the land pulled to the pins, fading out over PullDistance (from each cell's nearest pin point).
        var pinY = new float[n];
        Array.Fill(pinY, float.NaN);
        var pinSlope = new float[n];
        var pinOutX = new float[n];
        var pinOutZ = new float[n];
        var pinAlong = new float[n];
        var pinOwnReach = new float[n];
        var pinRoad = new int[n];
        Array.Fill(pinRoad, -1);
        for (var p = 0; p < pins.Count; p++)
        {
            var pin = pins[p];
            var tilts = pinTilts[p];
            for (var k = 0; k + 1 < pin.Points.Count; k++)
            {
                Vec3d a = pin.Points[k], b = pin.Points[k + 1];
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)) / (Cell / 2)));
                for (var s = 0; s <= steps; s++)
                {
                    var t = (double)s / steps;
                    var i = CellAt(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                    if (i < 0) continue;
                    pinY[i] = (float)(a.Y + (b.Y - a.Y) * t);
                    if (tilts is null) { pinSlope[i] = float.NaN; pinRoad[i] = -1; continue; }
                    pinRoad[i] = pinRoads[p];
                    pinAlong[i] = (float)(tilts[k].Along + (tilts[k + 1].Along - tilts[k].Along) * t);
                    pinOwnReach[i] = (float)tilts[k].OwnReach;
                    pinSlope[i] = (float)(tilts[k].Slope + (tilts[k + 1].Slope - tilts[k].Slope) * t);
                    (pinOutX[i], pinOutZ[i]) = ((float)tilts[k].Out.X, (float)tilts[k].Out.Z);
                }
            }
        }
        var (nearest, distance) = NearestTransform(pinY, columns, rows);
        var delta = new float[n];
        for (var i = 0; i < n; i++)
        {
            if (nearest[i] < 0) continue;
            var j = nearest[i];
            var at = float.IsNaN(land[j]) ? pinY[j] : land[j];
            var rise = pinY[j] - at;
            var reach = Math.Max(PullDistance, Math.Abs(rise) * PullSlope);
            if (distance[i] * Cell > reach) continue;
            var w = Math.Clamp(distance[i] * Cell / reach, 0, 1);
            delta[i] = (float)(rise * (1 - w * w * (3 - 2 * w)));
        }
        delta = Blur(delta, columns, rows, (int)Math.Round(2 / Cell));
        // Off the edges, on their outer side only: the land runs on in the surface's direction, fading into its own shape.
        var align = new float[n];
        for (var i = 0; i < n; i++)
        {
            var j = nearest[i];
            if (j < 0 || float.IsNaN(pinSlope[j]) || float.IsNaN(land[i])) continue;
            var d = distance[i] * Cell;
            if (d >= AlignDistance) continue;
            if ((i % columns - j % columns) * pinOutX[j] + (i / columns - j / columns) * pinOutZ[j] <= 0) continue;
            // Blends from the line off the edge to the land's own height (smootherstep: no change of curvature where it starts or ends).
            var w = d / AlignDistance;
            align[i] = (float)((pinY[j] + pinSlope[j] * d - land[i] - delta[i]) * (1 - w * w * w * (w * (6 * w - 15) + 10)));
        }
        align = Blur(align, columns, rows, (int)Math.Round(1 / Cell));
        // Under a cut stretch far over the land (a bridge pinned at its edges): the land stays, it is not at the road. Measured
        // from the road's own surface where that is lowest in the cell (a U's middle is far below its edges' line; another road
        // crossing below does not count), and never right at the edges: land kept there would join the pins through the road's side.
        float Apart(int roadIndex, float along, float other)
        {
            var apart = Math.Abs(along - other);
            return roads[roadIndex].Track.Closed ? Math.Min(apart, (float)roads[roadIndex].Rings[^1].Distance - apart) : apart;
        }
        for (var i = 0; i < n; i++)
        {
            if (!cut[i] || rolled[i] || float.IsNaN(land[i]) || distance[i] * Cell <= EdgeStrip) continue;
            var own = floorRoad[i] == cutRoad[i] && Apart(cutRoad[i], floorAlong[i], cutAlong[i]) <= cutReach[i];
            if (land[i] + delta[i] < (own ? Math.Min(roadY[i], floor[i]) : roadY[i]) - CutDepth) { deep[i] = true; cut[i] = false; }
        }
        // The fill under bridges: under them (see below) the land rises to KeepClear under their underside where it is at
        // most FillReach lower, fading out round them as the pins' pull does (further the more it rises), smoothed.
        var seedTop = new float[n];
        Array.Fill(seedTop, float.NaN);
        for (var i = 0; i < n; i++)
        {
            // Only under bridges: road whose edges are not pinned (no cut there), or a pinned stretch far over the land
            // (deep). Road the land meets keeps the pins' banks.
            // A buried road: the land rises over its top, a hill round it (the fade below).
            if (rolled[i])
            {
                var over = Math.Min(roof[i] + (float)Cover, UnderCrossing(i));
                if (!cut[i] && !float.IsNaN(land[i]) && over > land[i] + delta[i]) seedTop[i] = over;
                continue;
            }
            if (float.IsInfinity(holdBottom[i]) || float.IsNaN(land[i]) || !(deep[i] || float.IsNegativeInfinity(roadY[i]))) continue;
            // A road's own wall behind its pinned edge is no bridge.
            if (BehindOwnEdge(i)) continue;
            var top = holdBottom[i] - (float)KeepClear;
            var gap = top - (land[i] + delta[i]);
            if (gap > 0 && gap <= FillReach) seedTop[i] = top;
        }
        var (nearSeed, seedDistance) = NearestTransform(seedTop, columns, rows);
        var fill = new float[n];
        for (var i = 0; i < n; i++)
        {
            var j = nearSeed[i];
            if (j < 0) continue;
            var rise = seedTop[j] - (land[j] + delta[j]);
            var reach = Math.Max(PullDistance, rise * PullSlope);
            var d = seedDistance[i] * Cell;
            if (d > reach) continue;
            var w = Math.Clamp(d / reach, 0, 1);
            fill[i] = (float)(rise * (1 - w * w * (3 - 2 * w)));
        }
        fill = Blur(fill, columns, rows, (int)Math.Round(2 / Cell));
        // Behind a pinned edge, over that road's own wall (a wall leaning inwards lies under the land there): the land runs on
        // from the edge, covering the wall's back; pushed below the wall's foot it would hang from the edge through the road.
        bool BehindOwnEdge(int i)
        {
            var j = nearest[i];
            if (j < 0 || pinRoad[j] < 0 || floorRoad[i] != pinRoad[j]) return false;
            // Straight out from the edge: not the cells past the end of a pinned stretch, those are on the road. Right at the
            // edge the cells' grid is too coarse for a direction.
            int dc = i % columns - j % columns, dr = i / columns - j / columns;
            var length = Math.Sqrt(dc * dc + dr * dr);
            if (dc * pinOutX[j] + dr * pinOutZ[j] < (length > 4 ? 0.7 * length : -1)) return false;
            return Apart(pinRoad[j], floorAlong[i], pinAlong[j]) <= pinOwnReach[j];
        }
        var heights = new float[n];
        var fidelity = new float[n];
        var (_, toDense) = NearestTransform(pinY.Select((y, i) => steep[i] ? 0f : y).ToArray(), columns, rows);
        var (_, toRoad) = NearestTransform(floor.Select(y => float.IsInfinity(y) ? float.NaN : 0f).ToArray(), columns, rows);
        for (var i = 0; i < n; i++)
        {
            // Far from every road the land coarsens another step.
            var far = Math.Clamp((toRoad[i] * Cell - FarStart) / FarFade, 0, 1);
            var least = MinFidelity + (FarFidelity - MinFidelity) * far * far * (3 - 2 * far);
            fidelity[i] = (float)Math.Max(least, Math.Exp(-toDense[i] * Cell / FidelityFalloff));
            if (float.IsNaN(land[i])) { heights[i] = float.NaN; continue; }
            var free = land[i] + delta[i] + fill[i] + align[i];
            // Over a buried road the land stays above it, the cover growing in from the edges it holds on to.
            if (rolled[i]) fidelity[i] = Math.Max(fidelity[i], (float)BuriedFidelity);
            var h = rolled[i]
                ? cut[i] ? free : Math.Min(Math.Max(free, roof[i] + (float)(Cover * Math.Clamp(distance[i] * Cell / 2, 0, 1))), UnderCrossing(i))
                : Math.Min(free, BehindOwnEdge(i) ? keepOut[i] : Math.Min(keepOut[i], holdKeep[i]));
            int c = i % columns, r = i / columns;
            double x = x0 + (c + 0.5) * Cell, z = z0 + (r + 0.5) * Cell;
            if (Math.Abs(x) < StadiumHalf && Math.Abs(z) < StadiumHalf && h < 0.5) h = (float)Sunk;
            heights[i] = h;
        }
        bool CutAt(double x, double z)
        {
            var at = CellAt(x, z);
            if (at >= 0 && deep[at]) return false;
            if (!buckets.TryGetValue(((int)Math.Floor(x / 8), (int)Math.Floor(z / 8)), out var list)) return false;
            foreach (var q in list)
            {
                var (a, b, c, d) = quads[q];
                if (InTriangle(x, z, a, b, c) || InTriangle(x, z, a, c, d)) return true;
            }
            return false;
        }
        return (new TerrainField(x0, z0, Cell, columns, rows, heights, fidelity, cut) { CutAt = CutAt }, pins);
    }

    /// <summary>Holds land: banked less than about 84 degrees and not climbing or diving steeper than about 33 degrees (ownTerrain.ts decides the same).</summary>
    private static bool Holds(Ring ring) => ring.Up.Y >= 0.1 && Math.Abs(ring.Tangent.Normalized().Y) <= 0.55;

    private static (int, int, int) Cube(Vec3d p) => ((int)Math.Floor(p.X / 2), (int)Math.Floor(p.Y / 2), (int)Math.Floor(p.Z / 2));

    /// <summary>Whether p is by a driving surface; if so whether that holds land (by majority) and its distance along the road (the nearest cube's).</summary>
    private static bool NearSurface(Dictionary<(int, int, int), (bool Holds, double Along)> near, Vec3d p, out bool holds, out double along)
    {
        var (x, y, z) = Cube(p);
        int any = 0, holding = 0, nearest = int.MaxValue;
        along = 0;
        for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
            if (near.TryGetValue((x + dx, y + dy, z + dz), out var h))
            {
                any++;
                if (h.Holds) holding++;
                var off = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                if (off < nearest) { nearest = off; along = h.Along; }
            }
        holds = holding * 2 >= any;
        return any > 0;
    }

    private static IEnumerable<Vec3d> Points(MeshChunk m, Triangle t, double step)
    {
        Vec3d a = V(m.Positions[t.A]), b = V(m.Positions[t.B]), c = V(m.Positions[t.C]);
        var longest = Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length));
        var s = Math.Min(256, Math.Max(1, (int)Math.Ceiling(longest / step)));
        for (var u = 0; u <= s; u++)
            for (var v = 0; u + v <= s; v++)
                yield return a * (1 - (double)(u + v) / s) + b * ((double)u / s) + c * ((double)v / s);
    }

    private static Vec3d V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The height at (px, pz) on the plane through a, b, c.</summary>
    private static double PlaneY(double px, double pz, Vec3d a, Vec3d b, Vec3d c)
    {
        var det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (Math.Abs(det) < 1e-12) return (a.Y + b.Y + c.Y) / 3;
        var l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / det;
        var l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / det;
        return l1 * a.Y + l2 * b.Y + (1 - l1 - l2) * c.Y;
    }

    private static bool InTriangle(double px, double pz, Vec3d a, Vec3d b, Vec3d c)
    {
        double d1 = (b.X - a.X) * (pz - a.Z) - (b.Z - a.Z) * (px - a.X);
        double d2 = (c.X - b.X) * (pz - b.Z) - (c.Z - b.Z) * (px - b.X);
        double d3 = (a.X - c.X) * (pz - c.Z) - (a.Z - c.Z) * (px - c.X);
        return (d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0);
    }

    /// <summary>The editor grid's height at (x, z), bilinear between its vertices; NaN where a vertex round it has none.</summary>
    private static double SampleGrid(TerrainGrid grid, double x, double z)
    {
        var fx = (x - grid.X0) / grid.Cell;
        var fz = (z - grid.Z0) / grid.Cell;
        int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
        double u = fx - c, v = fz - r;
        return grid.Height(c, r) * (1 - u) * (1 - v) + grid.Height(c + 1, r) * u * (1 - v) + grid.Height(c, r + 1) * (1 - u) * v + grid.Height(c + 1, r + 1) * u * v;
    }

    /// <summary>Per cell the nearest cell with a value (not NaN) and its distance in cells (two-pass Euclidean transform).</summary>
    private static (int[] Nearest, float[] Distance) NearestTransform(float[] values, int columns, int rows)
    {
        var n = values.Length;
        var src = new int[n];
        for (var i = 0; i < n; i++) src[i] = float.IsNaN(values[i]) ? -1 : i;
        double D(int i, int c, int r) { if (src[i] < 0) return double.PositiveInfinity; int sc = src[i] % columns, sr = src[i] / columns; return (sc - c) * (sc - c) + (sr - r) * (sr - r); }
        void Relax(int c, int r, int dc, int dr)
        {
            int nc = c + dc, nr = r + dr;
            if (nc < 0 || nr < 0 || nc >= columns || nr >= rows) return;
            int i = r * columns + c, j = nr * columns + nc;
            if (src[j] >= 0 && D(j, c, r) < D(i, c, r)) src[i] = src[j];
        }
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++) { Relax(c, r, -1, 0); Relax(c, r, 0, -1); Relax(c, r, -1, -1); Relax(c, r, 1, -1); }
            for (var c = columns - 1; c >= 0; c--) Relax(c, r, 1, 0);
        }
        for (var r = rows - 1; r >= 0; r--)
        {
            for (var c = columns - 1; c >= 0; c--) { Relax(c, r, 1, 0); Relax(c, r, 0, 1); Relax(c, r, 1, 1); Relax(c, r, -1, 1); }
            for (var c = 0; c < columns; c++) Relax(c, r, -1, 0);
        }
        var dist = new float[n];
        for (var i = 0; i < n; i++) dist[i] = (float)Math.Sqrt(D(i, i % columns, i / columns));
        return (src, dist);
    }

    /// <summary>Two box blurs each way, radius in cells.</summary>
    private static float[] Blur(float[] values, int columns, int rows, int radius)
    {
        var a = values;
        for (var pass = 0; pass < 2; pass++)
            foreach (var alongX in new[] { true, false })
            {
                var o = new float[a.Length];
                int len = alongX ? columns : rows, lines = alongX ? rows : columns;
                for (var line = 0; line < lines; line++)
                {
                    double sum = 0;
                    int count = 0;
                    int Index(int i) => alongX ? line * columns + i : i * columns + line;
                    for (var i = -radius; i < len; i++)
                    {
                        int add = i + radius, drop = i - radius - 1;
                        if (add < len) { sum += a[Index(add)]; count++; }
                        if (drop >= 0) { sum -= a[Index(drop)]; count--; }
                        if (i >= 0) o[Index(i)] = (float)(sum / count);
                    }
                }
                a = o;
            }
        return a;
    }
}
