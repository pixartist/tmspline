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
    /// <summary>
    /// Edges more than this above the land are not pinned (the land stays: a road high over it), metres; the pull reaches
    /// out PullSlope metres per metre the land rises (at least PullDistance), a bank rather than a spire.
    /// </summary>
    private const double PinReach = 24, PullSlope = 1.5;
    private const double DriveRoom = 4, KeepClear = 0.5, BridgeGap = 3, Sunk = -2;
    /// <summary>Half the stadium's floor (TrackMap's 48 cells of 32 m), metres: inside, land under the ground sinks.</summary>
    private const double StadiumHalf = 768;

    /// <summary>The road geometry of one track for the builder.</summary>
    private sealed record Road(Track Track, List<Ring> Rings, MeshChunk Mesh);

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
        var keepOut = new float[n];
        Array.Fill(keepOut, float.PositiveInfinity);
        var steep = new bool[n];
        foreach (var road in roads)
        {
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
            var near = new Dictionary<(int, int, int), bool>();
            foreach (var t in m.Triangles)
            {
                var onSurface = !float.IsNaN(m.Surface[t.A].X) && !float.IsNaN(m.Surface[t.B].X) && !float.IsNaN(m.Surface[t.C].X);
                if (!onSurface) continue;
                var holds = HoldsAt((m.Surface[t.A].X + m.Surface[t.B].X + m.Surface[t.C].X) / 3);
                var normal = System.Numerics.Vector3.Normalize(m.Normals[t.A] + m.Normals[t.B] + m.Normals[t.C]);
                foreach (var p in Points(m, t, Cell / 2))
                {
                    near[Cube(p)] = holds;
                    var k = CellAt(p.X, p.Z);
                    if (k < 0) continue;
                    floor[k] = Math.Min(floor[k], (float)p.Y);
                    if (holds) continue;
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
                    if (!NearSurface(near, p, out var holds)) continue;
                    var k = CellAt(p.X, p.Z);
                    if (k < 0) continue;
                    floor[k] = Math.Min(floor[k], (float)p.Y);
                    if (!holds) keepOut[k] = Math.Min(keepOut[k], (float)(p.Y - KeepClear));
                }
            }
        }

        // Pins: the edges of road that holds land, in runs; not where another road lies below (a bridge); open ends.
        var pins = new List<TerrainPin>();
        var cut = new bool[n];
        bool Bridge(Vec3d e, Vec3d outward)
        {
            // Too high over the land to hold it.
            var at = CellAt(e.X, e.Z);
            if (at >= 0 && !float.IsNaN(land[at]) && land[at] < e.Y - PinReach) return true;
            foreach (var d in new[] { 0.0, 1.0, 2.5 })
            {
                var k = CellAt(e.X + outward.X * d, e.Z + outward.Z * d);
                if (k >= 0 && floor[k] < e.Y - BridgeGap - 1) return true;
            }
            return false;
        }
        foreach (var road in roads)
        {
            var rings = road.Rings;
            var edges = new (Vec3d L, Vec3d R, bool PinL, bool PinR)[rings.Count];
            for (var i = 0; i < rings.Count; i++)
            {
                var segment = road.Track.Segments[Math.Clamp(rings[i].Segment, 0, road.Track.Segments.Count - 1)];
                var l = RoadMeshBuilder.EdgeContact(rings[i], segment, -1);
                var r = RoadMeshBuilder.EdgeContact(rings[i], segment, 1);
                var right = new Vec3d(rings[i].Right.X, 0, rings[i].Right.Z);
                right = right.Length > 1e-9 ? right.Normalized() : right;
                var holds = Holds(rings[i]);
                edges[i] = (l, r, holds && !Bridge(l, right * -1), holds && !Bridge(r, right));
            }
            foreach (var side in new[] { -1, 1 })
            {
                List<Vec3d>? run = null;
                for (var i = 0; i < rings.Count; i++)
                {
                    var pinned = side < 0 ? edges[i].PinL : edges[i].PinR;
                    if (pinned) (run ??= []).Add(side < 0 ? edges[i].L : edges[i].R);
                    if ((!pinned || i == rings.Count - 1) && run is { Count: >= 2 }) pins.Add(new TerrainPin(run));
                    if (!pinned) run = null;
                }
            }
            if (!road.Track.Closed)
                foreach (var i in new[] { 0, rings.Count - 1 })
                    if (edges[i].PinL && edges[i].PinR)
                        pins.Add(new TerrainPin([edges[i].L, rings[i].Position, edges[i].R]));
            // The cut: between the two pinned edges of consecutive rings.
            for (var i = 0; i + 1 < rings.Count; i++)
                if (edges[i].PinL && edges[i].PinR && edges[i + 1].PinL && edges[i + 1].PinR)
                {
                    FillQuad(edges[i].L, edges[i].R, edges[i + 1].R, edges[i + 1].L);
                }
        }
        void FillQuad(Vec3d a, Vec3d b, Vec3d c, Vec3d d)
        {
            double minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            double minZ = Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)), maxZ = Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z));
            int c0 = Math.Max(0, (int)Math.Floor((minX - x0) / Cell)), c1 = Math.Min(columns - 1, (int)Math.Floor((maxX - x0) / Cell));
            int r0 = Math.Max(0, (int)Math.Floor((minZ - z0) / Cell)), r1 = Math.Min(rows - 1, (int)Math.Floor((maxZ - z0) / Cell));
            for (var r = r0; r <= r1; r++)
                for (var col = c0; col <= c1; col++)
                {
                    double px = x0 + (col + 0.5) * Cell, pz = z0 + (r + 0.5) * Cell;
                    if (InTriangle(px, pz, a, b, c) || InTriangle(px, pz, a, c, d)) cut[r * columns + col] = true;
                }
        }

        // The solve: the land pulled to the pins, fading out over PullDistance (from each cell's nearest pin point).
        var pinY = new float[n];
        Array.Fill(pinY, float.NaN);
        foreach (var pin in pins)
            for (var k = 0; k + 1 < pin.Points.Count; k++)
            {
                Vec3d a = pin.Points[k], b = pin.Points[k + 1];
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)) / (Cell / 2)));
                for (var s = 0; s <= steps; s++)
                {
                    var t = (double)s / steps;
                    var i = CellAt(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                    if (i >= 0) pinY[i] = (float)(a.Y + (b.Y - a.Y) * t);
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
        var heights = new float[n];
        var fidelity = new float[n];
        var (_, toDense) = NearestTransform(pinY.Select((y, i) => steep[i] ? 0f : y).ToArray(), columns, rows);
        for (var i = 0; i < n; i++)
        {
            fidelity[i] = (float)Math.Max(MinFidelity, Math.Exp(-toDense[i] * Cell / FidelityFalloff));
            if (float.IsNaN(land[i])) { heights[i] = float.NaN; continue; }
            var h = Math.Min(land[i] + delta[i], keepOut[i]);
            int c = i % columns, r = i / columns;
            double x = x0 + (c + 0.5) * Cell, z = z0 + (r + 0.5) * Cell;
            if (Math.Abs(x) < StadiumHalf && Math.Abs(z) < StadiumHalf && h < 0.5) h = (float)Sunk;
            heights[i] = h;
        }
        return (new TerrainField(x0, z0, Cell, columns, rows, heights, fidelity, cut), pins);
    }

    /// <summary>Holds land: upright and not climbing or diving steeper than about 33 degrees (as TerrainMeshBuilder).</summary>
    private static bool Holds(Ring ring) => ring.Up.Y >= 0.5 && Math.Abs(ring.Tangent.Normalized().Y) <= 0.55;

    private static (int, int, int) Cube(Vec3d p) => ((int)Math.Floor(p.X / 2), (int)Math.Floor(p.Y / 2), (int)Math.Floor(p.Z / 2));

    private static bool NearSurface(Dictionary<(int, int, int), bool> near, Vec3d p, out bool holds)
    {
        var (x, y, z) = Cube(p);
        int any = 0, holding = 0;
        for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
            if (near.TryGetValue((x + dx, y + dy, z + dz), out var h)) { any++; if (h) holding++; }
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
