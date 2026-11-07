namespace TmSpline.Core.Geometry;

/// <summary>
/// An incremental constrained Delaunay triangulation in the plane (x, z), for the terrain mesher: points are inserted one
/// at a time (Lawson's flips keep it Delaunay), and segments can be forced in as edges (Sloan's flip algorithm), which no
/// later flip removes. A segment that would cross an earlier one is refused.
/// <para>
/// Half-edge layout as Delaunator's: triangle t has half-edges 3t, 3t+1, 3t+2, counter-clockwise; half-edge e runs from
/// <see cref="Vertex"/>(e) to Vertex(Next(e)); its twin is the same edge in the neighbouring triangle (-1 on the hull).
/// It starts as a rectangle (two triangles) round everything; its four corners are vertices 0 to 3.
/// </para>
/// </summary>
public sealed class ConstrainedDelaunay
{
    private readonly List<double> _x = [], _z = [];
    private readonly List<int> _tri = [];       // per half-edge: its start vertex
    private readonly List<int> _twin = [];      // per half-edge: twin, -1 on the hull
    private readonly List<bool> _fixed = [];    // per half-edge: part of a forced segment
    private readonly List<int> _out = [];       // per vertex: one half-edge leaving it
    private int _last;                          // triangle the last walk ended in
    private readonly Dictionary<(long, long), int> _at = [];
    private readonly double _snap;

    /// <summary>Number of vertices (the four corners included).</summary>
    public int Count => _x.Count;
    public int TriangleCount => _tri.Count / 3;
    public double X(int v) => _x[v];
    public double Z(int v) => _z[v];
    public int Vertex(int halfEdge) => _tri[halfEdge];
    public int Twin(int halfEdge) => _twin[halfEdge];
    public bool IsFixed(int halfEdge) => _fixed[halfEdge];
    public static int Next(int e) => e % 3 == 2 ? e - 2 : e + 1;
    public static int Prev(int e) => e % 3 == 0 ? e + 2 : e - 1;

    /// <param name="snap">Points closer than this are one vertex, metres.</param>
    public ConstrainedDelaunay(double minX, double minZ, double maxX, double maxZ, double snap = 0.005)
    {
        _snap = snap;
        var m = Math.Max(maxX - minX, maxZ - minZ) * 0.1 + 1;
        AddVertex(minX - m, minZ - m);
        AddVertex(maxX + m, minZ - m);
        AddVertex(maxX + m, maxZ + m);
        AddVertex(minX - m, maxZ + m);
        // Counter-clockwise in (x, z) with z as the second axis: (0,1,2) and (0,2,3).
        var a = AddTriangle(0, 1, 2);
        var b = AddTriangle(0, 2, 3);
        Link(a + 2, b);
    }

    /// <summary>Whether the vertex is one of the starting rectangle's corners (not a real point).</summary>
    public static bool IsCorner(int v) => v < 4;

    private int AddVertex(double x, double z)
    {
        _x.Add(x);
        _z.Add(z);
        _out.Add(-1);
        var i = _x.Count - 1;
        _at[Key(x, z)] = i;
        return i;
    }

    private (long, long) Key(double x, double z) => ((long)Math.Round(x / _snap), (long)Math.Round(z / _snap));

    private int AddTriangle(int a, int b, int c)
    {
        var t = _tri.Count;
        _tri.AddRange([a, b, c]);
        _twin.AddRange([-1, -1, -1]);
        _fixed.AddRange([false, false, false]);
        _out[a] = t; _out[b] = t + 1; _out[c] = t + 2;
        return t;
    }

    private void Set(int t, int a, int b, int c)
    {
        _tri[t] = a; _tri[t + 1] = b; _tri[t + 2] = c;
        _fixed[t] = _fixed[t + 1] = _fixed[t + 2] = false;
        _out[a] = t; _out[b] = t + 1; _out[c] = t + 2;
    }

    private void Link(int a, int b)
    {
        if (a >= 0) _twin[a] = b;
        if (b >= 0) _twin[b] = a;
    }

    private double Orient(int a, int b, double px, double pz) =>
        (_x[b] - _x[a]) * (pz - _z[a]) - (_z[b] - _z[a]) * (px - _x[a]);

    private double Orient(int a, int b, int c) => Orient(a, b, _x[c], _z[c]);

    /// <summary>Whether d lies inside the circle through a, b, c (counter-clockwise).</summary>
    private bool InCircle(int a, int b, int c, int d)
    {
        double ax = _x[a] - _x[d], az = _z[a] - _z[d], bx = _x[b] - _x[d], bz = _z[b] - _z[d], cx = _x[c] - _x[d], cz = _z[c] - _z[d];
        double ap = ax * ax + az * az, bp = bx * bx + bz * bz, cp = cx * cx + cz * cz;
        return ax * (bz * cp - bp * cz) - az * (bx * cp - bp * cx) + ap * (bx * cz - bz * cx) > 1e-12 * (ap + bp + cp) * (ap + bp + cp);
    }

    /// <summary>
    /// Inserts a point; returns its vertex (an existing one when it is that close). A point on a forced segment splits it.
    /// </summary>
    public int Insert(double x, double z)
    {
        if (_at.TryGetValue(Key(x, z), out var existing)) return existing;
        var (t, edge) = Locate(x, z);
        if (t < 0) return -1;
        for (var k = 0; k < 3; k++)
        {
            var w = _tri[3 * t + k];
            if ((_x[w] - x) * (_x[w] - x) + (_z[w] - z) * (_z[w] - z) < _snap * _snap) return w;
        }
        var p = AddVertex(x, z);
        if (edge >= 0) SplitEdge(edge, p);
        else SplitTriangle(t, p);
        return p;
    }

    /// <summary>The triangle holding (x, z), and the half-edge it lies on (or -1). A walk from the last triangle.</summary>
    private (int Triangle, int Edge) Locate(double x, double z)
    {
        var t = _last < TriangleCount ? _last : 0;
        var rng = 0;
        for (var steps = 0; steps < 4 * TriangleCount + 100; steps++)
        {
            var b = 3 * t;
            var moved = false;
            var start = rng++ % 3; // vary the order so the walk does not cycle
            for (var k = 0; k < 3; k++)
            {
                var e = b + (start + k) % 3;
                if (Orient(_tri[e], _tri[Next(e)], x, z) < -1e-12 && _twin[e] >= 0)
                {
                    t = _twin[e] / 3;
                    moved = true;
                    break;
                }
            }
            if (moved) continue;
            _last = t;
            // Inside (or on an edge: nearly zero orientation within the edge's extent).
            for (var k = 0; k < 3; k++)
            {
                var e = b + k;
                int a = _tri[e], c = _tri[Next(e)];
                var len = Math.Sqrt((_x[c] - _x[a]) * (_x[c] - _x[a]) + (_z[c] - _z[a]) * (_z[c] - _z[a]));
                if (len > 0 && Math.Abs(Orient(a, c, x, z)) / len < _snap * 0.5) return (t, e);
            }
            return (t, -1);
        }
        return (-1, -1);
    }

    private void SplitTriangle(int t, int p)
    {
        var b = 3 * t;
        int a = _tri[b], c = _tri[b + 1], d = _tri[b + 2];
        int o0 = _twin[b], o1 = _twin[b + 1], o2 = _twin[b + 2];
        bool f0 = _fixed[b], f1 = _fixed[b + 1], f2 = _fixed[b + 2];
        Set(b, a, c, p);
        var t1 = AddTriangle(c, d, p);
        var t2 = AddTriangle(d, a, p);
        Link(b, o0); Link(t1, o1); Link(t2, o2);
        _fixed[b] = f0; _fixed[t1] = f1; _fixed[t2] = f2;
        Link(b + 1, t1 + 2); Link(t1 + 1, t2 + 2); Link(t2 + 1, b + 2);
        Legalize([b, t1, t2]);
    }

    private void SplitEdge(int e, int p)
    {
        var f = _twin[e];
        var fixedEdge = _fixed[e];
        // Triangle of e: a -> b -> c.
        int e1 = Next(e), e2 = Prev(e);
        int a = _tri[e], b = _tri[e1], c = _tri[e2];
        int ob = _twin[e1], oc = _twin[e2];
        bool fb = _fixed[e1], fc = _fixed[e2];
        var te = e - e % 3;
        Set(te, a, p, c);                 // a -> p, p -> c, c -> a
        var t1 = AddTriangle(p, b, c);    // p -> b, b -> c, c -> p
        Link(te + 2, oc); _fixed[te + 2] = fc;
        Link(t1 + 1, ob); _fixed[t1 + 1] = fb;
        Link(te + 1, t1 + 2);
        _fixed[te] = fixedEdge; _fixed[t1] = fixedEdge;
        var outer = new List<int> { te + 2, t1 + 1 };
        if (f >= 0)
        {
            // Twin's triangle: b -> a -> d.
            int f1 = Next(f), f2 = Prev(f);
            var d = _tri[f2];
            int oa = _twin[f1], od = _twin[f2];
            bool fa = _fixed[f1], fd = _fixed[f2];
            var tf = f - f % 3;
            Set(tf, b, p, d);             // b -> p, p -> d, d -> b
            var t2 = AddTriangle(p, a, d);// p -> a, a -> d, d -> p
            Link(tf + 2, od); _fixed[tf + 2] = fd;
            Link(t2 + 1, oa); _fixed[t2 + 1] = fa;
            Link(tf + 1, t2 + 2);
            _fixed[tf] = fixedEdge; _fixed[t2] = fixedEdge;
            Link(te, t2);                 // a -> p with p -> a
            Link(t1, tf);                 // p -> b with b -> p
            outer.Add(tf + 2);
            outer.Add(t2 + 1);
        }
        else
        {
            Link(te, -1);
            Link(t1, -1);
        }
        Legalize(outer);
    }

    /// <summary>Restores the Delaunay condition across the given edges (and those it changes), keeping forced ones.</summary>
    private void Legalize(List<int> edges)
    {
        var stack = new Stack<int>(edges);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 1_000_000)
        {
            var e = stack.Pop();
            var f = _twin[e];
            if (f < 0 || _fixed[e]) continue;
            int a = _tri[e], b = _tri[Next(e)], c = _tri[Prev(e)], d = _tri[Prev(f)];
            if (!InCircle(a, b, c, d)) continue;
            Flip(e);
            // After the flip: triangle (c, a, d) and (d, b, c); the edges away from c to check again.
            var t1 = e - e % 3;
            var t2 = f - f % 3;
            stack.Push(t1 + 1);
            stack.Push(t2);
        }
    }

    /// <summary>
    /// Flips the edge e (a -> b, triangle a b c; twin b -> a, triangle b a d) to c -> d. Afterwards e's triangle is
    /// (c, a, d) and its twin's (d, b, c); returns the new diagonal's half-edge in e's triangle (d -> c).
    /// </summary>
    private int Flip(int e)
    {
        var f = _twin[e];
        int e1 = Next(e), e2 = Prev(e), f1 = Next(f), f2 = Prev(f);
        int a = _tri[e], b = _tri[e1], c = _tri[e2], d = _tri[f2];
        int x1 = _twin[e1], x2 = _twin[e2], y1 = _twin[f1], y2 = _twin[f2];
        bool g1 = _fixed[e1], g2 = _fixed[e2], h1 = _fixed[f1], h2 = _fixed[f2];
        int t1 = e - e % 3, t2 = f - f % 3;
        Set(t1, c, a, d);   // c->a (was e2), a->d (was f1), d->c (diagonal)
        Set(t2, d, b, c);   // d->b (was f2), b->c (was e1), c->d (diagonal)
        Link(t1, x2); _fixed[t1] = g2;
        Link(t1 + 1, y1); _fixed[t1 + 1] = h1;
        Link(t2, y2); _fixed[t2] = h2;
        Link(t2 + 1, x1); _fixed[t2 + 1] = g1;
        Link(t1 + 2, t2 + 2); _fixed[t1 + 2] = false; _fixed[t2 + 2] = false;
        return t1 + 2;
    }

    /// <summary>The half-edge from u to v, or -1.</summary>
    public int Find(int u, int v)
    {
        var start = _out[u];
        if (start < 0) return -1;
        // Around u: counter-clockwise (twin of the previous edge), then clockwise from the start for hull vertices.
        var e = start;
        for (var guard = 0; guard < 4096; guard++)
        {
            if (_tri[Next(e)] == v) return e;
            var t = _twin[Prev(e)];
            if (t < 0) break;
            e = t;
            if (e == start) return -1;
        }
        e = start;
        for (var guard = 0; guard < 4096; guard++)
        {
            var t = _twin[e];
            if (t < 0) return -1;
            e = Next(t);
            if (_tri[Next(e)] == v) return e;
            if (e == start) return -1;
        }
        return -1;
    }

    /// <summary>
    /// Forces the segment u-v in as an edge (split at vertices lying on it). Returns false when it would cross an earlier
    /// forced segment (then nothing of it is forced past that point).
    /// </summary>
    public bool Constrain(int u, int v)
    {
        if (u == v || u < 0 || v < 0) return true;
        var guard = 0;
        while (u != v && guard++ < 10000)
        {
            var h = Find(u, v);
            if (h >= 0) { Fix(h); return true; }
            var (crossing, stop) = Crossings(u, v);
            if (crossing is null) return false;
            if (!Flatten(u, stop, crossing)) return false;
            h = Find(u, stop);
            if (h < 0) return false;
            Fix(h);
            u = stop;
        }
        return u == v;
    }

    private void Fix(int h)
    {
        _fixed[h] = true;
        if (_twin[h] >= 0) _fixed[_twin[h]] = true;
    }

    /// <summary>
    /// The edges the segment from u towards v crosses, up to v or the first vertex lying on the segment (returned as the
    /// stop); null when a crossed edge is forced.
    /// </summary>
    private (List<(int, int)>? Edges, int Stop) Crossings(int u, int v)
    {
        double vx = _x[v], vz = _z[v];
        var len = Math.Sqrt((vx - _x[u]) * (vx - _x[u]) + (vz - _z[u]) * (vz - _z[u]));
        double Side(int w) => Orient(u, v, w) / len;
        // The triangle round u that the segment leaves through.
        var start = _out[u];
        var e = start;
        var found = -1;
        for (var pass = 0; pass < 2 && found < 0; pass++)
        {
            e = start;
            for (var g = 0; g < 4096; g++)
            {
                int b = _tri[Next(e)], c = _tri[Prev(e)];
                double sb = Side(b), sc = Side(c);
                // A neighbour on the segment, ahead: stop there.
                if (Math.Abs(sb) < _snap && Ahead(u, v, b)) return ([], b);
                if (Math.Abs(sc) < _snap && Ahead(u, v, c)) return ([], c);
                if (sb < 0 && sc > 0) { found = e; break; }
                var t = pass == 0 ? _twin[Prev(e)] : _twin[e];
                if (t < 0) break;
                e = pass == 0 ? t : Next(t);
                if (e == start) break;
            }
        }
        if (found < 0) return (null, -1);
        var edges = new List<(int, int)>();
        var cross = Next(found); // b -> c, crossed
        for (var g = 0; g < 100000; g++)
        {
            if (_fixed[cross]) return (null, -1);
            edges.Add((_tri[cross], _tri[Next(cross)]));
            var t = _twin[cross];
            if (t < 0) return (null, -1);
            var w = _tri[Prev(t)];
            if (w == v) return (edges, v);
            var sw = Side(w);
            if (Math.Abs(sw) < _snap) return (edges, w);
            // Leave through the edge on w's side that the segment crosses: (t's start -> w) or (w -> t's end).
            cross = sw < 0 ? Prev(t) : Next(t);
            // Next(t) runs from twin's end (= cross's start) ... pick by side: the crossed edge has ends on both sides.
            int p0 = _tri[cross], p1 = _tri[Next(cross)];
            if (Side(p0) * Side(p1) > 0) cross = cross == Prev(t) ? Next(t) : Prev(t);
        }
        return (null, -1);
    }

    private bool Ahead(int u, int v, int w)
    {
        double dx = _x[v] - _x[u], dz = _z[v] - _z[u];
        var t = ((_x[w] - _x[u]) * dx + (_z[w] - _z[u]) * dz) / (dx * dx + dz * dz);
        return t > 1e-9 && t <= 1 + 1e-9;
    }

    /// <summary>Sloan's flips: removes the crossing edges until u-v is an edge, then restores Delaunay round it.</summary>
    private bool Flatten(int u, int v, List<(int, int)> crossing)
    {
        var queue = new Queue<(int, int)>(crossing);
        var made = new List<(int, int)>();
        var guard = 0;
        while (queue.Count > 0)
        {
            if (guard++ > 100000 + 50 * crossing.Count) return false;
            var (p, q) = queue.Dequeue();
            var h = Find(p, q);
            if (h < 0) continue;
            var f = _twin[h];
            if (f < 0) return false;
            int c = _tri[Prev(h)], d = _tri[Prev(f)];
            // Convex quad (p, d, q, c): the diagonal c-d crosses p-q.
            if (Orient(c, d, p) * Orient(c, d, q) >= 0 || Orient(p, q, c) * Orient(p, q, d) >= 0)
            {
                queue.Enqueue((p, q));
                continue;
            }
            Flip(h);
            if (c != u && c != v && d != u && d != v && SegmentsCross(u, v, c, d)) queue.Enqueue((c, d));
            else made.Add((c, d));
        }
        // Delaunay again round the new edges (not the segment itself).
        var changed = true;
        for (var pass = 0; changed && pass < 100; pass++)
        {
            changed = false;
            for (var i = 0; i < made.Count; i++)
            {
                var (p, q) = made[i];
                if ((p == u && q == v) || (p == v && q == u)) continue;
                var h = Find(p, q);
                if (h < 0 || _fixed[h]) continue;
                var f = _twin[h];
                if (f < 0) continue;
                int a = _tri[h], b = _tri[Next(h)], c = _tri[Prev(h)], d = _tri[Prev(f)];
                if (!InCircle(a, b, c, d)) continue;
                Flip(h);
                made[i] = (c, d);
                changed = true;
            }
        }
        return Find(u, v) >= 0;
    }

    private bool SegmentsCross(int a, int b, int c, int d) =>
        Orient(a, b, c) * Orient(a, b, d) < 0 && Orient(c, d, a) * Orient(c, d, b) < 0;

    /// <summary>The triangles' vertices (three per triangle, counter-clockwise), without those touching the corners.</summary>
    public IEnumerable<(int A, int B, int C, int Triangle)> Triangles()
    {
        for (var t = 0; t < TriangleCount; t++)
        {
            int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
            if (IsCorner(a) || IsCorner(b) || IsCorner(c)) continue;
            yield return (a, b, c, t);
        }
    }

    /// <summary>All triangles, corners included (for the mesher's checks).</summary>
    public (int A, int B, int C) TriangleAt(int t) => (_tri[3 * t], _tri[3 * t + 1], _tri[3 * t + 2]);
}
