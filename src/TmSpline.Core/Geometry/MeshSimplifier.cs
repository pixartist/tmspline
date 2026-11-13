using System.Numerics;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Mesh simplification by edge collapse with quadric error metrics (Garland and Heckbert, 1997): the two ends of the
/// edge whose merge changes the shape least (the summed squared distances to the planes of the triangles round them)
/// are merged, again and again, until the mesh has the triangles asked for or the next merge would change it more than
/// allowed. Flat land ends up in few large triangles; ridges, valleys and banks keep their vertices.
/// <list type="bullet">
/// <item>The mesh's open borders (the land's outline) stay where they are: their vertices are not moved.</item>
/// <item>A merge is refused where it would flip or squash a triangle, make the surface pinch (two sheets meeting in an
/// edge), put a vertex where it may not be (Allowed) or make an edge longer than MaxEdge there.</item>
/// <item>The merged vertex goes to whichever of the two ends or their middle changes the shape least.</item>
/// </list>
/// </summary>
public static class MeshSimplifier
{
    /// <summary>A merge is refused where a triangle round it would be thinner than this (twice its area over its longest side squared: 0.87 equilateral) (TMSPLINE_SQUASH).</summary>
    public static float Squash { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_SQUASH"), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.1f;
    /// <param name="targetTriangles">Stop at this many triangles.</param>
    /// <param name="maxError">Stop when the next merge would move the surface further than this, metres (root of the quadric error).</param>
    /// <param name="maxEdge">The longest an edge may become at a place, metres (null: no limit).</param>
    /// <param name="allowed">Whether a vertex may be put at a place (null: anywhere).</param>
    public static AdaptiveMesh Simplify(AdaptiveMesh mesh, int targetTriangles, float maxError,
        Func<Vector3, float>? maxEdge = null, Func<Vector3, bool>? allowed = null)
    {
        var n = mesh.Positions.Count;
        var pos = mesh.Positions.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
        var faces = new List<int[]>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            faces.Add([mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]]);
        var alive = Enumerable.Repeat(true, faces.Count).ToArray();
        var around = new List<int>[n];
        for (var i = 0; i < n; i++) around[i] = [];
        for (var f = 0; f < faces.Count; f++)
            foreach (var v in faces[f]) around[v].Add(f);

        // The open borders: edges of one triangle; their vertices stay.
        var edgeCount = new Dictionary<long, int>();
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        foreach (var f in faces)
            for (var k = 0; k < 3; k++) { var key = Key(f[k], f[(k + 1) % 3]); edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1; }
        var locked = new bool[n];
        foreach (var (key, count) in edgeCount)
            if (count != 2) { locked[(int)(key >> 32)] = true; locked[(int)(key & 0xffffffff)] = true; }

        // Per vertex the quadric of the planes round it (weighted by the triangles' areas).
        var quadric = new Quadric[n];
        foreach (var f in faces)
        {
            var normal = Vector3.Cross(pos[f[1]] - pos[f[0]], pos[f[2]] - pos[f[0]]);
            var area = normal.Length();
            if (area < 1e-9f) continue;
            normal /= area;
            var q = Quadric.Plane(normal, -Vector3.Dot(normal, pos[f[0]]), area / 2);
            foreach (var v in f) quadric[v] = quadric[v] + q;
        }

        var version = new int[n];
        var dead = new bool[n];
        var queue = new PriorityQueue<(int A, int B, int VA, int VB), double>();
        var limit = (double)maxError * maxError;

        (double Cost, Vector3 At) Best(int a, int b)
        {
            var q = quadric[a] + quadric[b];
            if (locked[a] && locked[b]) return (double.MaxValue, default);
            Span<Vector3> options = locked[a] ? [pos[a]] : locked[b] ? [pos[b]] : [pos[a], pos[b], (pos[a] + pos[b]) / 2];
            var best = (Cost: double.MaxValue, At: pos[a]);
            foreach (var p in options)
            {
                var cost = q.Error(p);
                if (cost < best.Cost) best = (cost, p);
            }
            return best;
        }
        void Push(int a, int b)
        {
            var (cost, _) = Best(a, b);
            if (cost <= limit) queue.Enqueue((a, b, version[a], version[b]), cost);
        }
        foreach (var key in edgeCount.Keys) Push((int)(key >> 32), (int)(key & 0xffffffff));

        var triangles = faces.Count;
        HashSet<int> Ring(int v)
        {
            var set = new HashSet<int>();
            foreach (var f in around[v])
                if (alive[f]) foreach (var u in faces[f]) if (u != v) set.Add(u);
            return set;
        }

        while (triangles > targetTriangles && queue.TryDequeue(out var e, out _))
        {
            var (a, b, va, vb) = e;
            if (dead[a] || dead[b] || version[a] != va || version[b] != vb) continue;
            var (cost, at) = Best(a, b);
            if (cost > limit) continue;
            // The link condition: the two share exactly the two vertices of the triangles on their edge (else the
            // surface would pinch).
            var ringA = Ring(a);
            var ringB = Ring(b);
            if (!ringA.Contains(b)) continue;
            var common = 0;
            foreach (var u in ringA) if (ringB.Contains(u)) common++;
            if (common != 2) continue;
            if (allowed is not null && !allowed(at)) continue;
            // No edge too long, no triangle flipped or squashed.
            var ok = true;
            if (maxEdge is not null)
            {
                var longest = maxEdge(at);
                foreach (var u in ringA) if (u != b && Vector3.Distance(at, pos[u]) > longest) { ok = false; break; }
                if (ok) foreach (var u in ringB) if (u != a && Vector3.Distance(at, pos[u]) > longest) { ok = false; break; }
            }
            if (ok)
                foreach (var v in new[] { a, b })
                    foreach (var f in around[v])
                    {
                        if (!alive[f]) continue;
                        var face = faces[f];
                        if (face.Contains(a) && face.Contains(b)) continue;
                        Vector3 p0 = pos[face[0]], p1 = pos[face[1]], p2 = pos[face[2]];
                        var before = Vector3.Cross(p1 - p0, p2 - p0);
                        Vector3 Moved(int i) => face[i] == a || face[i] == b ? at : pos[face[i]];
                        Vector3 q0 = Moved(0), q1 = Moved(1), q2 = Moved(2);
                        var after = Vector3.Cross(q1 - q0, q2 - q0);
                        var la = after.Length();
                        // Squashed: thinner than a tenth of its longest side's height for an equilateral triangle.
                        var longestSide = MathF.Max((q1 - q0).LengthSquared(), MathF.Max((q2 - q1).LengthSquared(), (q0 - q2).LengthSquared()));
                        if (la < Squash * longestSide || Vector3.Dot(before, after) < 0.25f * before.Length() * la) { ok = false; break; }
                    }
            if (!ok) continue;

            // Merge b into a.
            foreach (var f in around[b])
            {
                if (!alive[f]) continue;
                var face = faces[f];
                if (face.Contains(a)) { alive[f] = false; triangles--; continue; }
                for (var k = 0; k < 3; k++) if (face[k] == b) face[k] = a;
                around[a].Add(f);
            }
            around[a].RemoveAll(f => !alive[f]);
            dead[b] = true;
            pos[a] = at;
            quadric[a] = quadric[a] + quadric[b];
            locked[a] |= locked[b];
            version[a]++;
            version[b]++;
            // (Edges not at a keep their costs: their ends' quadrics are unchanged.)
            foreach (var u in Ring(a)) Push(a, u);
        }

        // The kept triangles and vertices, renumbered.
        var result = new AdaptiveMesh();
        var index = new int[n];
        Array.Fill(index, -1);
        for (var f = 0; f < faces.Count; f++)
        {
            if (!alive[f]) continue;
            foreach (var v in faces[f])
            {
                if (index[v] < 0)
                {
                    index[v] = result.Positions.Count;
                    result.Positions.Add(pos[v]);
                    result.Pinned.Add(false);
                }
                result.Indices.Add(index[v]);
            }
        }
        return result;
    }

    /// <summary>
    /// Thin triangles made fatter by flipping edges (as a Delaunay triangulation would have them): the edge two
    /// triangles share is swapped for the other diagonal of the quad they make, where that raises the smaller of the
    /// two triangles' smallest angles, the two lie nearly in one plane (so the land's shape does not change) and the quad
    /// is convex there. Vertices do not move and the open borders stay as they are.
    /// </summary>
    /// <param name="flatDegrees">The most the two triangles may be bent against each other, degrees.</param>
    public static void ImproveByFlips(AdaptiveMesh mesh, float flatDegrees = 12, int passes = 6)
    {
        var pos = mesh.Positions;
        var faces = new List<int[]>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) faces.Add([mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]]);
        var flat = MathF.Cos(flatDegrees * MathF.PI / 180);
        Vector3 Normal(int[] f) => Vector3.Cross(pos[f[1]] - pos[f[0]], pos[f[2]] - pos[f[0]]);
        float MinAngle(int a, int b, int c)
        {
            float Angle(Vector3 p, Vector3 q, Vector3 r)
            {
                Vector3 u = q - p, v = r - p;
                var d = u.Length() * v.Length();
                return d < 1e-12f ? 0 : MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / d, -1, 1));
            }
            return MathF.Min(Angle(pos[a], pos[b], pos[c]), MathF.Min(Angle(pos[b], pos[c], pos[a]), Angle(pos[c], pos[a], pos[b])));
        }
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        for (var pass = 0; pass < passes; pass++)
        {
            // Each edge's triangles (the edges of exactly two can flip).
            var edges = new Dictionary<long, List<int>>();
            for (var f = 0; f < faces.Count; f++)
                for (var k = 0; k < 3; k++)
                {
                    var key = Key(faces[f][k], faces[f][(k + 1) % 3]);
                    if (!edges.TryGetValue(key, out var list)) edges[key] = list = [];
                    list.Add(f);
                }
            var touched = new HashSet<int>();
            var flips = 0;
            foreach (var (key, list) in edges)
            {
                if (list.Count != 2 || touched.Contains(list[0]) || touched.Contains(list[1])) continue;
                int[] f1 = faces[list[0]], f2 = faces[list[1]];
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                // Wound so that f1 runs a to b, f2 b to a; c and d their third corners.
                var i1 = Array.IndexOf(f1, a);
                if (f1[(i1 + 1) % 3] != b) (a, b) = (b, a);
                i1 = Array.IndexOf(f1, a);
                if (f1[(i1 + 1) % 3] != b) continue;
                var c = f1[(i1 + 2) % 3];
                var i2 = Array.IndexOf(f2, b);
                if (i2 < 0 || f2[(i2 + 1) % 3] != a) continue;
                var d = f2[(i2 + 2) % 3];
                if (c == d || edges.ContainsKey(Key(c, d))) continue;
                Vector3 n1 = Normal(f1), n2 = Normal(f2);
                if (Vector3.Dot(n1, n2) < flat * n1.Length() * n2.Length()) continue;
                int[] g1 = [a, d, c], g2 = [d, b, c];
                Vector3 m1 = Normal(g1), m2 = Normal(g2), mean = n1 + n2;
                if (Vector3.Dot(m1, mean) <= 0 || Vector3.Dot(m2, mean) <= 0) continue;
                var before = MathF.Min(MinAngle(a, b, c), MinAngle(b, a, d));
                var after = MathF.Min(MinAngle(a, d, c), MinAngle(d, b, c));
                if (after < before + 0.02f) continue;
                faces[list[0]] = g1;
                faces[list[1]] = g2;
                touched.Add(list[0]);
                touched.Add(list[1]);
                flips++;
            }
            if (flips == 0) break;
        }
        mesh.Indices.Clear();
        foreach (var f in faces) mesh.Indices.AddRange(f);
    }

    /// <summary>A symmetric 4x4 quadric (the ten distinct entries), summing squared distances to planes.</summary>
    private readonly record struct Quadric(double A, double B, double C, double D, double E, double F, double G, double H, double I, double J)
    {
        public static Quadric Plane(Vector3 n, float d, float weight) => new(
            weight * n.X * n.X, weight * n.X * n.Y, weight * n.X * n.Z, weight * n.X * d,
            weight * n.Y * n.Y, weight * n.Y * n.Z, weight * n.Y * d,
            weight * n.Z * n.Z, weight * n.Z * d, weight * d * d);

        public static Quadric operator +(Quadric p, Quadric q) => new(p.A + q.A, p.B + q.B, p.C + q.C, p.D + q.D, p.E + q.E,
            p.F + q.F, p.G + q.G, p.H + q.H, p.I + q.I, p.J + q.J);

        /// <summary>The summed weighted squared distances of a point to the planes; divided by the weights: a squared distance.</summary>
        public double Error(Vector3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            var e = A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x + E * y * y + 2 * F * y * z + 2 * G * y + H * z * z + 2 * I * z + J;
            var w = A + E + H;
            return w > 1e-12 ? Math.Max(0, e / w) : 0;
        }
    }
}
