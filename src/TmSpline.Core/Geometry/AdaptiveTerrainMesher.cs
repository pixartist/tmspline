using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain's height map at working resolution: heights at cell centres (cell (c, r) centred at x = X0 + (c + 0.5)
/// Cell, z = Z0 + (r + 0.5) Cell; NaN: no land), the mesh density wanted there (0..1), and where the land is cut out
/// (road the land meets: its triangles are left out, the pins along its edges bound it).
/// </summary>
public sealed record TerrainField(double X0, double Z0, double Cell, int Columns, int Rows, float[] Heights, float[] Fidelity, bool[] Cut)
{
    /// <summary>
    /// The exact cut, when known (the roads' polygons between their pinned edges): a point on it is cut. Without it the
    /// cells' Cut decides (at cell size: a thin triangle beside a pinned edge can fall in a cut cell).
    /// </summary>
    public Func<double, double, bool>? CutAt { get; init; }

    public bool IsCut(double x, double z)
    {
        if (CutAt is { } exact) return exact(x, z);
        var k = CellAt(x, z);
        return k >= 0 && Cut[k];
    }

    public double Height(int c, int r) => c < 0 || r < 0 || c >= Columns || r >= Rows ? double.NaN : Heights[r * Columns + c];

    /// <summary>Bilinear between the cell centres; NaN where one round the point has no land.</summary>
    public double Sample(double x, double z)
    {
        var fx = (x - X0) / Cell - 0.5;
        var fz = (z - Z0) / Cell - 0.5;
        int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
        double u = fx - c, v = fz - r;
        c = Math.Clamp(c, 0, Columns - 2);
        r = Math.Clamp(r, 0, Rows - 2);
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double a = Height(c, r), b = Height(c + 1, r), d = Height(c, r + 1), e = Height(c + 1, r + 1);
        return a * (1 - u) * (1 - v) + b * u * (1 - v) + d * (1 - u) * v + e * u * v;
    }

    public int CellAt(double x, double z)
    {
        int c = (int)Math.Floor((x - X0) / Cell), r = (int)Math.Floor((z - Z0) / Cell);
        return c < 0 || r < 0 || c >= Columns || r >= Rows ? -1 : r * Columns + c;
    }
}

/// <summary>A line the land must meet exactly (a road's edge, an open track's end): its points with their heights.</summary>
public sealed record TerrainPin(IReadOnlyList<Vec3d> Points);

/// <param name="Tolerance">Height error allowed where fidelity is 1, metres (divided by the fidelity elsewhere).</param>
/// <param name="MinEdge">Longest triangle side where fidelity is 1, metres (divided by the fidelity elsewhere, up to MaxEdge).</param>
/// <param name="PinSpacing">Pins' points at most this far apart (longer segments are split), metres.</param>
/// <param name="OutlineSpacing">Points along the land's outline at most this far apart, metres.</param>
public sealed record MesherSettings(double Tolerance = 0.08, double MinEdge = 1.5, double MaxEdge = 48, double PinSpacing = 1.5, int MaxRounds = 100,
    double OutlineSpacing = 2);

/// <summary>The mesher's result: positions (x, y, z) and three indices per triangle, with what it could not do.</summary>
public sealed class AdaptiveMesh
{
    public List<Vector3> Positions { get; } = [];
    public List<int> Indices { get; } = [];
    /// <summary>Per position: whether it is a pin's point (its height exact).</summary>
    public List<bool> Pinned { get; } = [];
    /// <summary>Pin segments that crossed an earlier one and were left out.</summary>
    public int RefusedSegments { get; set; }
    public int Rounds { get; set; }
    /// <summary>Largest height error left (in tolerances: 1 = at the limit), over the land.</summary>
    public double WorstError { get; set; }
}

/// <summary>
/// Builds a terrain mesh from a height map alone (nothing about roads): the pins go in first as exact vertices and forced
/// edges of a constrained Delaunay triangulation, then points are added where the triangles are furthest from the map
/// (greedy insertion, Garland and Heckbert's terrain approximation) or longer than the fidelity allows, until both hold.
/// Triangles on cut cells or without land are left out.
/// </summary>
public static class AdaptiveTerrainMesher
{
    public static AdaptiveMesh Build(TerrainField field, IReadOnlyList<TerrainPin> pins, MesherSettings? settings = null)
    {
        var s = settings ?? new MesherSettings();
        var result = new AdaptiveMesh();
        double x1 = field.X0 + field.Columns * field.Cell, z1 = field.Z0 + field.Rows * field.Cell;
        var cdt = new ConstrainedDelaunay(field.X0, field.Z0, x1, z1);
        var heights = new List<double> { double.NaN, double.NaN, double.NaN, double.NaN };
        var pinned = new List<bool> { false, false, false, false };
        int Put(double x, double z, double y, bool pin)
        {
            var v = cdt.Insert(x, z);
            if (v < 0) return -1;
            while (heights.Count <= v) { heights.Add(double.NaN); pinned.Add(false); }
            if (pin) { heights[v] = y; pinned[v] = true; }
            else if (!pinned[v]) heights[v] = y;
            return v;
        }

        // 1. The pins: their points (split to PinSpacing) and segments.
        foreach (var pin in pins)
        {
            var prev = -1;
            Vec3d? last = null;
            foreach (var p in pin.Points)
            {
                if (last is { } l)
                {
                    var len = Math.Sqrt((p.X - l.X) * (p.X - l.X) + (p.Z - l.Z) * (p.Z - l.Z));
                    var n = Math.Max(1, (int)Math.Ceiling(len / s.PinSpacing));
                    for (var k = 1; k <= n; k++)
                    {
                        var t = (double)k / n;
                        var v = Put(l.X + (p.X - l.X) * t, l.Z + (p.Z - l.Z) * t, l.Y + (p.Y - l.Y) * t, true);
                        if (v >= 0 && prev >= 0 && !cdt.Constrain(prev, v)) result.RefusedSegments++;
                        if (v >= 0) prev = v;
                    }
                }
                else prev = Put(p.X, p.Z, p.Y, true);
                last = p;
            }
        }

        // 2. A coarse start over the land, and the field's outline.
        var seed = Math.Max(1, (int)Math.Round(s.MaxEdge / field.Cell));
        for (var r = 0; r < field.Rows; r += seed)
            for (var c = 0; c < field.Columns; c += seed)
                Seed(c, r);
        for (var c = 0; c < field.Columns; c += seed) { Seed(c, field.Rows - 1); }
        for (var r = 0; r < field.Rows; r += seed) { Seed(field.Columns - 1, r); }
        Seed(field.Columns - 1, field.Rows - 1);
        // The land's outline (cells with land next to cells without), every OutlineSpacing: the mesh reaches out to it.
        var step = Math.Max(1, (int)Math.Round(s.OutlineSpacing / field.Cell));
        for (var r = 0; r < field.Rows; r++)
            for (var c = 0; c < field.Columns; c++)
            {
                if ((r + c) % step != 0 || double.IsNaN(field.Height(c, r))) continue;
                if (double.IsNaN(field.Height(c - 1, r)) || double.IsNaN(field.Height(c + 1, r)) || double.IsNaN(field.Height(c, r - 1)) || double.IsNaN(field.Height(c, r + 1))
                    || c == 0 || r == 0 || c == field.Columns - 1 || r == field.Rows - 1)
                    Seed(c, r);
            }
        void Seed(int c, int r)
        {
            var h = field.Height(c, r);
            if (!double.IsNaN(h)) Put(field.X0 + (c + 0.5) * field.Cell, field.Z0 + (r + 0.5) * field.Cell, h, false);
        }

        // 3. Refinement: per triangle its worst cell (height error over the tolerance there) and its size over the
        // longest side allowed; every triangle past 1 gets a point, worst first, a few cells apart.
        for (var round = 0; round < s.MaxRounds; round++)
        {
            result.Rounds = round + 1;
            var candidates = new List<(double Score, double X, double Z, double Y)>();
            var worst = 0.0;
            for (var t = 0; t < cdt.TriangleCount; t++)
            {
                var (a, b, c) = cdt.TriangleAt(t);
                if (ConstrainedDelaunay.IsCorner(a) || ConstrainedDelaunay.IsCorner(b) || ConstrainedDelaunay.IsCorner(c)) continue;
                double ax = cdt.X(a), az = cdt.Z(a), bx = cdt.X(b), bz = cdt.Z(b), cx = cdt.X(c), cz = cdt.Z(c);
                double ay = heights[a], by = heights[b], cy = heights[c];
                double mx = (ax + bx + cx) / 3, mz = (az + bz + cz) / 3;
                var mid = field.CellAt(mx, mz);
                if (field.IsCut(mx, mz)) continue;
                if (double.IsNaN(ay) || double.IsNaN(by) || double.IsNaN(cy) || mid < 0 || float.IsNaN(field.Heights[mid])) continue;
                var fid = mid >= 0 ? Math.Max(0.02, field.Fidelity[mid]) : 0.02;
                // Size.
                var longest = Math.Sqrt(Math.Max((ax - bx) * (ax - bx) + (az - bz) * (az - bz),
                    Math.Max((bx - cx) * (bx - cx) + (bz - cz) * (bz - cz), (cx - ax) * (cx - ax) + (cz - az) * (cz - az))));
                var allowed = Math.Min(s.MaxEdge, s.MinEdge / fid);
                (double Score, double X, double Z, double Y) best = (0, 0, 0, 0);
                if (longest > allowed && longest > field.Cell * 1.5)
                    best = (longest / allowed, mx, mz, field.Sample(mx, mz));
                // Height error over the cells inside.
                var det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Math.Abs(det) > 1e-12)
                {
                    int c0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - field.X0) / field.Cell));
                    int c1 = Math.Min(field.Columns - 1, (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - field.X0) / field.Cell));
                    int r0 = Math.Max(0, (int)Math.Floor((Math.Min(az, Math.Min(bz, cz)) - field.Z0) / field.Cell));
                    int r1 = Math.Min(field.Rows - 1, (int)Math.Floor((Math.Max(az, Math.Max(bz, cz)) - field.Z0) / field.Cell));
                    for (var r = r0; r <= r1; r++)
                        for (var col = c0; col <= c1; col++)
                        {
                            var k = r * field.Columns + col;
                            if (field.Cut[k]) continue;
                            var h = field.Heights[k];
                            if (float.IsNaN(h)) continue;
                            double px = field.X0 + (col + 0.5) * field.Cell, pz = field.Z0 + (r + 0.5) * field.Cell;
                            var l1 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / det;
                            var l2 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / det;
                            var l3 = 1 - l1 - l2;
                            if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                            var err = Math.Abs(l1 * ay + l2 * by + l3 * cy - h) / (s.Tolerance / Math.Max(0.02, field.Fidelity[k]));
                            if (err > worst) worst = err;
                            if (err > best.Score) best = (err, px, pz, h);
                        }
                }
                if (best.Score > 1 && !double.IsNaN(best.Y)) candidates.Add(best);
            }
            result.WorstError = worst;
            if (candidates.Count == 0) break;
            // Worst first, not two in one round within two cells of each other; in rows (short walks for the inserts).
            candidates.Sort((p, q) => q.Score.CompareTo(p.Score));
            var taken = new HashSet<(int, int)>();
            var chosen = new List<(double X, double Z, double Y)>();
            foreach (var (_, x, z, y) in candidates)
            {
                var key = ((int)Math.Floor((x - field.X0) / (2 * field.Cell)), (int)Math.Floor((z - field.Z0) / (2 * field.Cell)));
                if (!taken.Add(key)) continue;
                chosen.Add((x, z, y));
            }
            chosen.Sort((p, q) => p.Z != q.Z ? p.Z.CompareTo(q.Z) : p.X.CompareTo(q.X));
            var before = cdt.Count;
            foreach (var (x, z, y) in chosen) Put(x, z, y, false);
            if (cdt.Count == before) break;
        }

        // 4. The mesh: land triangles only.
        var index = new Dictionary<int, int>();
        foreach (var (a, b, c, _) in cdt.Triangles())
        {
            if (double.IsNaN(heights[a]) || double.IsNaN(heights[b]) || double.IsNaN(heights[c])) continue;
            double ox = (cdt.X(a) + cdt.X(b) + cdt.X(c)) / 3, oz = (cdt.Z(a) + cdt.Z(b) + cdt.Z(c)) / 3;
            var mid = field.CellAt(ox, oz);
            if (mid < 0 || field.IsCut(ox, oz) || float.IsNaN(field.Heights[mid])) continue;
            foreach (var v in new[] { a, b, c })
            {
                if (!index.TryGetValue(v, out var i))
                {
                    i = result.Positions.Count;
                    result.Positions.Add(new Vector3((float)cdt.X(v), (float)heights[v], (float)cdt.Z(v)));
                    result.Pinned.Add(pinned[v]);
                    index[v] = i;
                }
                result.Indices.Add(i);
            }
        }
        return result;
    }
}
