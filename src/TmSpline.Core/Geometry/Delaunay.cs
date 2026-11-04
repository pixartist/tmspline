namespace TmSpline.Core.Geometry;

/// <summary>
/// Delaunay triangulation of points in the plane: a port of Mapbox's Delaunator (ISC licence, by Vladimir Agafonkin), the
/// sweep-hull algorithm. <see cref="Triangles"/> holds three point indices per triangle; points that coincide are skipped.
/// </summary>
public sealed class Delaunay
{
    public int[] Triangles { get; }

    private const double Epsilon = 1.1102230246251565e-16 * 2;
    private readonly double[] _coords;
    private readonly int[] _triangles;
    private readonly int[] _halfedges;
    private readonly int[] _hullPrev, _hullNext, _hullTri, _hullHash;
    private readonly int _hashSize;
    private int _hullStart;
    private int _trianglesLen;
    private double _cx, _cy;
    private readonly int[] _edgeStack = new int[512];

    /// <param name="coords">x0, y0, x1, y1, ...</param>
    public Delaunay(double[] coords)
    {
        _coords = coords;
        var n = coords.Length / 2;
        var maxTriangles = Math.Max(2 * n - 5, 0);
        _triangles = new int[maxTriangles * 3];
        _halfedges = new int[maxTriangles * 3];
        _hashSize = (int)Math.Ceiling(Math.Sqrt(n));
        _hullPrev = new int[n];
        _hullNext = new int[n];
        _hullTri = new int[n];
        _hullHash = new int[Math.Max(1, _hashSize)];
        Triangles = n < 3 ? [] : Run(n);
    }

    private int[] Run(int n)
    {
        var coords = _coords;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var ids = new int[n];
        for (var i = 0; i < n; i++)
        {
            double x = coords[2 * i], y = coords[2 * i + 1];
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            ids[i] = i;
        }
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        // The seed triangle: the point nearest the centre, its nearest neighbour, and the point making the smallest circle.
        var minDist = double.PositiveInfinity;
        int i0 = 0, i1 = 0, i2 = 0;
        for (var i = 0; i < n; i++)
        {
            var d = Dist(cx, cy, coords[2 * i], coords[2 * i + 1]);
            if (d < minDist) { i0 = i; minDist = d; }
        }
        double i0x = coords[2 * i0], i0y = coords[2 * i0 + 1];
        minDist = double.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            if (i == i0) continue;
            var d = Dist(i0x, i0y, coords[2 * i], coords[2 * i + 1]);
            if (d < minDist && d > 0) { i1 = i; minDist = d; }
        }
        double i1x = coords[2 * i1], i1y = coords[2 * i1 + 1];
        var minRadius = double.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            if (i == i0 || i == i1) continue;
            var r = Circumradius(i0x, i0y, i1x, i1y, coords[2 * i], coords[2 * i + 1]);
            if (r < minRadius) { i2 = i; minRadius = r; }
        }
        if (double.IsPositiveInfinity(minRadius)) return []; // all on a line
        double i2x = coords[2 * i2], i2y = coords[2 * i2 + 1];
        if (Orient(i0x, i0y, i1x, i1y, i2x, i2y))
        {
            (i1, i2) = (i2, i1);
            (i1x, i2x) = (i2x, i1x);
            (i1y, i2y) = (i2y, i1y);
        }
        (_cx, _cy) = Circumcenter(i0x, i0y, i1x, i1y, i2x, i2y);

        var dists = new double[n];
        for (var i = 0; i < n; i++) dists[i] = Dist(coords[2 * i], coords[2 * i + 1], _cx, _cy);
        Array.Sort(dists, ids);

        _hullStart = i0;
        _hullNext[i0] = _hullPrev[i2] = i1;
        _hullNext[i1] = _hullPrev[i0] = i2;
        _hullNext[i2] = _hullPrev[i1] = i0;
        _hullTri[i0] = 0;
        _hullTri[i1] = 1;
        _hullTri[i2] = 2;
        Array.Fill(_hullHash, -1);
        _hullHash[HashKey(i0x, i0y)] = i0;
        _hullHash[HashKey(i1x, i1y)] = i1;
        _hullHash[HashKey(i2x, i2y)] = i2;
        _trianglesLen = 0;
        AddTriangle(i0, i1, i2, -1, -1, -1);

        double xp = 0, yp = 0;
        for (var k = 0; k < ids.Length; k++)
        {
            var i = ids[k];
            double x = coords[2 * i], y = coords[2 * i + 1];
            if (k > 0 && Math.Abs(x - xp) <= Epsilon && Math.Abs(y - yp) <= Epsilon) continue;
            xp = x;
            yp = y;
            if (i == i0 || i == i1 || i == i2) continue;

            // A visible edge of the hull, found through the angle hash.
            var start = 0;
            for (int j = 0, key = HashKey(x, y); j < _hashSize; j++)
            {
                start = _hullHash[(key + j) % _hashSize];
                if (start != -1 && start != _hullNext[start]) break;
            }
            start = _hullPrev[start];
            int e = start, q;
            while (!Orient(x, y, coords[2 * e], coords[2 * e + 1], coords[2 * (q = _hullNext[e])], coords[2 * q + 1]))
            {
                e = q;
                if (e == start) { e = -1; break; }
            }
            if (e == -1) continue; // a point on the hull already (or a duplicate)

            var t = AddTriangle(e, i, _hullNext[e], -1, -1, _hullTri[e]);
            _hullTri[i] = Legalize(t + 2);
            _hullTri[e] = t;

            // Walk forward along the hull, adding triangles and flipping.
            var nx = _hullNext[e];
            while (Orient(x, y, coords[2 * nx], coords[2 * nx + 1], coords[2 * (q = _hullNext[nx])], coords[2 * q + 1]))
            {
                t = AddTriangle(nx, i, q, _hullTri[i], -1, _hullTri[nx]);
                _hullTri[i] = Legalize(t + 2);
                _hullNext[nx] = nx; // removed from the hull
                nx = q;
            }
            // And backward from the start.
            if (e == start)
                while (Orient(x, y, coords[2 * (q = _hullPrev[e])], coords[2 * q + 1], coords[2 * e], coords[2 * e + 1]))
                {
                    t = AddTriangle(q, i, e, -1, _hullTri[e], _hullTri[q]);
                    Legalize(t + 2);
                    _hullTri[q] = t;
                    _hullNext[e] = e;
                    e = q;
                }
            _hullStart = _hullPrev[i] = e;
            _hullNext[e] = _hullPrev[nx] = i;
            _hullNext[i] = nx;
            _hullHash[HashKey(x, y)] = i;
            _hullHash[HashKey(coords[2 * e], coords[2 * e + 1])] = e;
        }
        return _triangles[.._trianglesLen];
    }

    private int HashKey(double x, double y) =>
        (int)(Math.Floor(PseudoAngle(x - _cx, y - _cy) * _hashSize) % _hashSize);

    private static double PseudoAngle(double dx, double dy)
    {
        var p = dx / (Math.Abs(dx) + Math.Abs(dy));
        return (dy > 0 ? 3 - p : 1 + p) / 4; // [0..1]
    }

    private int Legalize(int a)
    {
        var i = 0;
        int ar;
        while (true)
        {
            var b = _halfedges[a];
            var a0 = a - a % 3;
            ar = a0 + (a + 2) % 3;
            if (b == -1)
            {
                if (i == 0) break;
                a = _edgeStack[--i];
                continue;
            }
            var b0 = b - b % 3;
            var al = a0 + (a + 1) % 3;
            var bl = b0 + (b + 2) % 3;
            int p0 = _triangles[ar], pr = _triangles[a], pl = _triangles[al], p1 = _triangles[bl];
            var c = _coords;
            if (InCircle(c[2 * p0], c[2 * p0 + 1], c[2 * pr], c[2 * pr + 1], c[2 * pl], c[2 * pl + 1], c[2 * p1], c[2 * p1 + 1]))
            {
                _triangles[a] = p1;
                _triangles[b] = p0;
                var hbl = _halfedges[bl];
                // An edge on the hull swapped: fix the hull's reference to it.
                if (hbl == -1)
                {
                    var e = _hullStart;
                    do
                    {
                        if (_hullTri[e] == bl) { _hullTri[e] = a; break; }
                        e = _hullPrev[e];
                    } while (e != _hullStart);
                }
                Link(a, hbl);
                Link(b, _halfedges[ar]);
                Link(ar, bl);
                var br = b0 + (b + 1) % 3;
                if (i < _edgeStack.Length) _edgeStack[i++] = br;
            }
            else
            {
                if (i == 0) break;
                a = _edgeStack[--i];
            }
        }
        return ar;
    }

    private void Link(int a, int b)
    {
        _halfedges[a] = b;
        if (b != -1) _halfedges[b] = a;
    }

    private int AddTriangle(int i0, int i1, int i2, int a, int b, int c)
    {
        var t = _trianglesLen;
        _triangles[t] = i0;
        _triangles[t + 1] = i1;
        _triangles[t + 2] = i2;
        Link(t, a);
        Link(t + 1, b);
        Link(t + 2, c);
        _trianglesLen += 3;
        return t;
    }

    private static double Dist(double ax, double ay, double bx, double by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);

    private static bool Orient(double px, double py, double qx, double qy, double rx, double ry) =>
        (qy - py) * (rx - qx) - (qx - px) * (ry - qy) < 0;

    private static bool InCircle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        double dx = ax - px, dy = ay - py, ex = bx - px, ey = by - py, fx = cx - px, fy = cy - py;
        double ap = dx * dx + dy * dy, bp = ex * ex + ey * ey, cp = fx * fx + fy * fy;
        return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
    }

    private static double Circumradius(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy, cl = ex * ex + ey * ey;
        var d = 0.5 / (dx * ey - dy * ex);
        double x = (ey * bl - dy * cl) * d, y = (dx * cl - ex * bl) * d;
        return x * x + y * y;
    }

    private static (double X, double Y) Circumcenter(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy, cl = ex * ex + ey * ey;
        var d = 0.5 / (dx * ey - dy * ex);
        return (ax + (ey * bl - dy * cl) * d, ay + (dx * cl - ex * bl) * d);
    }
}
