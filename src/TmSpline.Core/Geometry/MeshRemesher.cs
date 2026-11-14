using System.Numerics;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Adaptive isotropic remeshing (after Botsch and Kobbelt, 2004): the triangles made even and well shaped, nearly
/// equilateral, at the sizes the mesh already has at each place (so a simplified mesh keeps its few large triangles on
/// flat land and its small ones where the shape needs them). Each round:
/// <list type="number">
/// <item>Edges longer than 4/3 of their target length are split in their middle.</item>
/// <item>Edges shorter than 4/5 of it are collapsed (where that makes no edge too long, flips no triangle and does not
/// make the surface pinch).</item>
/// <item>Edges are flipped towards six neighbours per vertex, where the two triangles lie nearly flat.</item>
/// <item>Each vertex slides towards the middle of its neighbours along the surface (not off it), then is put back onto
/// the surface itself (Project).</item>
/// </list>
/// The open borders stay where they are.
/// </summary>
public static class MeshRemesher
{
    /// <param name="project">Puts a place back onto the surface (null: it stays where it was moved).</param>
    public static AdaptiveMesh Remesh(AdaptiveMesh mesh, Func<Vector3, Vector3>? project = null, int rounds = 5, float flatDegrees = 35)
    {
        // Timing (TMSPLINE_VOXEL_STATS=on): each step's time, and how often and how long the projection ran.
        var stats = Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on";
        var stepTime = new double[4];
        long projections = 0, projectTicks = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Step(int i) { stepTime[i] += watch.Elapsed.TotalSeconds; watch.Restart(); }
        if (stats && project is not null)
        {
            var inner = project;
            project = p =>
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var r = inner(p);
                Interlocked.Increment(ref projections);
                Interlocked.Add(ref projectTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                return r;
            };
        }
        var pos = mesh.Positions.Select(p => p).ToList();
        var faces = new List<int[]>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) faces.Add([mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]]);
        var flat = MathF.Cos(flatDegrees * MathF.PI / 180);
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        // The target length per vertex: the mean of its edges as the mesh comes, evened out with its neighbours.
        var target = new List<float>(new float[pos.Count]);
        {
            var sum = new float[pos.Count];
            var count = new int[pos.Count];
            foreach (var f in faces)
                for (var k = 0; k < 3; k++)
                {
                    int a = f[k], b = f[(k + 1) % 3];
                    var l = Vector3.Distance(pos[a], pos[b]);
                    sum[a] += l; count[a]++; sum[b] += l; count[b]++;
                }
            for (var i = 0; i < pos.Count; i++) target[i] = count[i] > 0 ? sum[i] / count[i] : 1;
            for (var pass = 0; pass < 2; pass++)
            {
                var next = target.ToArray();
                var n = new int[pos.Count];
                var acc = new float[pos.Count];
                foreach (var f in faces)
                    for (var k = 0; k < 3; k++) { acc[f[k]] += target[f[(k + 1) % 3]]; n[f[k]]++; }
                for (var i = 0; i < pos.Count; i++) if (n[i] > 0) next[i] = 0.5f * target[i] + 0.5f * acc[i] / n[i];
                target = next.ToList();
            }
        }

        bool[] Locked()
        {
            var count = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            foreach (var f in faces) for (var k = 0; k < 3; k++) { var key = Key(f[k], f[(k + 1) % 3]); count[key] = count.GetValueOrDefault(key) + 1; }
            var locked = new bool[pos.Count];
            foreach (var (key, c) in count) if (c != 2) { locked[(int)(key >> 32)] = true; locked[(int)(key & 0xffffffff)] = true; }
            return locked;
        }
        Vector3 Normal(int[] f) => Vector3.Cross(pos[f[1]] - pos[f[0]], pos[f[2]] - pos[f[0]]);

        watch.Restart();
        for (var round = 0; round < rounds; round++)
        {
            var locked = Locked();

            // 1. Split long edges (both triangles on an edge split together; a border edge splits its one triangle).
            {
                var edgeFaces = new Dictionary<long, List<int>>(EdgeKeyComparer.Instance);
                for (var f = 0; f < faces.Count; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        var key = Key(faces[f][k], faces[f][(k + 1) % 3]);
                        if (!edgeFaces.TryGetValue(key, out var l)) edgeFaces[key] = l = [];
                        l.Add(f);
                    }
                var touched = new HashSet<int>();
                foreach (var (key, list) in edgeFaces)
                {
                    int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                    if (locked[a] && locked[b]) continue;
                    if (Vector3.Distance(pos[a], pos[b]) <= 4f / 3 * (target[a] + target[b]) / 2) continue;
                    if (list.Any(touched.Contains)) continue;
                    var mid = (pos[a] + pos[b]) / 2;
                    if (project is not null) mid = project(mid);
                    var m = pos.Count;
                    pos.Add(mid);
                    target.Add((target[a] + target[b]) / 2);
                    foreach (var f in list)
                    {
                        var face = faces[f];
                        var ia = Array.IndexOf(face, a);
                        var ib = Array.IndexOf(face, b);
                        var ic = 3 - ia - ib;
                        var c = face[ic];
                        // Keep the winding: replace b by m in one half, a by m in the other.
                        var first = (int[])face.Clone(); first[ib] = m;
                        var second = (int[])face.Clone(); second[ia] = m;
                        faces[f] = first;
                        faces.Add(second);
                        touched.Add(f);
                        touched.Add(faces.Count - 1);
                    }
                }
            }

            Step(0);
            // 2. Collapse short edges.
            {
                var around = new List<int>[pos.Count];
                for (var i = 0; i < pos.Count; i++) around[i] = [];
                for (var f = 0; f < faces.Count; f++) foreach (var v in faces[f]) around[v].Add(f);
                var alive = Enumerable.Repeat(true, faces.Count).ToArray();
                var dead = new bool[pos.Count];
                locked = Locked();
                HashSet<int> Ring(int v)
                {
                    var set = new HashSet<int>();
                    foreach (var f in around[v]) if (alive[f]) foreach (var u in faces[f]) if (u != v) set.Add(u);
                    return set;
                }
                var edges = new List<(int A, int B)>();
                for (var f = 0; f < faces.Count; f++)
                    for (var k = 0; k < 3; k++) { int a = faces[f][k], b = faces[f][(k + 1) % 3]; if (a < b) edges.Add((a, b)); }
                foreach (var (a0, b0) in edges.OrderBy(e => Vector3.DistanceSquared(pos[e.A], pos[e.B])))
                {
                    int a = a0, b = b0;
                    if (dead[a] || dead[b]) continue;
                    var limit = (target[a] + target[b]) / 2;
                    if (Vector3.Distance(pos[a], pos[b]) >= 0.8f * limit) continue;
                    if (locked[a] && locked[b]) continue;
                    if (locked[b]) (a, b) = (b, a);
                    var at = locked[a] ? pos[a] : (pos[a] + pos[b]) / 2;
                    if (project is not null && !locked[a]) at = project(at);
                    var ringA = Ring(a);
                    var ringB = Ring(b);
                    if (!ringA.Contains(b)) continue;
                    var common = 0;
                    foreach (var u in ringA) if (ringB.Contains(u)) common++;
                    if (common != 2) continue;
                    var ok = true;
                    foreach (var u in ringA.Concat(ringB)) if (u != a && u != b && Vector3.Distance(at, pos[u]) > 4f / 3 * limit) { ok = false; break; }
                    if (ok)
                        foreach (var v in new[] { a, b })
                            foreach (var f in around[v])
                            {
                                if (!alive[f]) continue;
                                var face = faces[f];
                                if (face.Contains(a) && face.Contains(b)) continue;
                                var before = Normal(face);
                                var moved = face.Select(i => i == a || i == b ? at : pos[i]).ToArray();
                                var after = Vector3.Cross(moved[1] - moved[0], moved[2] - moved[0]);
                                if (Vector3.Dot(before, after) < 0.25f * before.Length() * after.Length() || after.LengthSquared() < 1e-8f) { ok = false; break; }
                            }
                    if (!ok) continue;
                    foreach (var f in around[b])
                    {
                        if (!alive[f]) continue;
                        var face = faces[f];
                        if (face.Contains(a)) { alive[f] = false; continue; }
                        for (var k = 0; k < 3; k++) if (face[k] == b) face[k] = a;
                        around[a].Add(f);
                    }
                    dead[b] = true;
                    pos[a] = at;
                    target[a] = (target[a] + target[b]) / 2;
                }
                faces = faces.Where((_, f) => alive[f]).ToList();
            }

            Step(1);
            // 3. Flip towards six neighbours a vertex (four on a border), where the two triangles lie nearly flat.
            {
                locked = Locked();
                var valence = new int[pos.Count];
                var edgeFaces = new Dictionary<long, List<int>>(EdgeKeyComparer.Instance);
                for (var f = 0; f < faces.Count; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        var key = Key(faces[f][k], faces[f][(k + 1) % 3]);
                        if (!edgeFaces.TryGetValue(key, out var l)) { edgeFaces[key] = l = []; valence[faces[f][k]]++; valence[faces[f][(k + 1) % 3]]++; }
                        l.Add(f);
                    }
                int Wanted(int v) => locked[v] ? 4 : 6;
                var touched = new HashSet<int>();
                foreach (var (key, list) in edgeFaces)
                {
                    if (list.Count != 2 || touched.Contains(list[0]) || touched.Contains(list[1])) continue;
                    int[] f1 = faces[list[0]], f2 = faces[list[1]];
                    int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                    var i1 = Array.IndexOf(f1, a);
                    if (f1[(i1 + 1) % 3] != b) { (a, b) = (b, a); i1 = Array.IndexOf(f1, a); }
                    if (f1[(i1 + 1) % 3] != b) continue;
                    var c = f1[(i1 + 2) % 3];
                    var i2 = Array.IndexOf(f2, b);
                    if (i2 < 0 || f2[(i2 + 1) % 3] != a) continue;
                    var d = f2[(i2 + 2) % 3];
                    if (c == d || edgeFaces.ContainsKey(Key(c, d))) continue;
                    Vector3 n1 = Normal(f1), n2 = Normal(f2);
                    if (Vector3.Dot(n1, n2) < flat * n1.Length() * n2.Length()) continue;
                    int[] g1 = [a, d, c], g2 = [d, b, c];
                    Vector3 m1 = Normal(g1), m2 = Normal(g2), mean = n1 + n2;
                    if (Vector3.Dot(m1, mean) <= 0 || Vector3.Dot(m2, mean) <= 0) continue;
                    int Dev(int v, int val) => Math.Abs(val - Wanted(v));
                    var before = Dev(a, valence[a]) + Dev(b, valence[b]) + Dev(c, valence[c]) + Dev(d, valence[d]);
                    var after = Dev(a, valence[a] - 1) + Dev(b, valence[b] - 1) + Dev(c, valence[c] + 1) + Dev(d, valence[d] + 1);
                    if (after >= before) continue;
                    faces[list[0]] = g1;
                    faces[list[1]] = g2;
                    valence[a]--; valence[b]--; valence[c]++; valence[d]++;
                    touched.Add(list[0]);
                    touched.Add(list[1]);
                }
            }

            Step(2);
            // 4. Slide along the surface towards the neighbours' middle, then back onto the surface.
            {
                locked = Locked();
                var normal = new Vector3[pos.Count];
                var middle = new Vector3[pos.Count];
                var weight = new float[pos.Count];
                foreach (var f in faces)
                {
                    var n = Normal(f);
                    var area = n.Length();
                    var centre = (pos[f[0]] + pos[f[1]] + pos[f[2]]) / 3;
                    foreach (var v in f) { normal[v] += n; middle[v] += centre * area; weight[v] += area; }
                }
                var moved = pos.ToArray();
                Parallel.For(0, pos.Count, v =>
                {
                    if (locked[v] || weight[v] < 1e-9f || normal[v].LengthSquared() < 1e-12f) return;
                    var n = Vector3.Normalize(normal[v]);
                    var shift = middle[v] / weight[v] - pos[v];
                    shift -= n * Vector3.Dot(shift, n);
                    var at = pos[v] + shift * 0.5f;
                    moved[v] = project is not null ? project(at) : at;
                });
                // (A move that flips a triangle is undone.)
                var undo = new bool[pos.Count];
                foreach (var f in faces)
                {
                    var before = Normal(f);
                    var after = Vector3.Cross(moved[f[1]] - moved[f[0]], moved[f[2]] - moved[f[0]]);
                    if (Vector3.Dot(before, after) <= 0.25f * before.Length() * after.Length()) foreach (var v in f) undo[v] = true;
                }
                for (var v = 0; v < pos.Count; v++) if (!undo[v]) pos[v] = moved[v];
            }
            Step(3);
        }
        if (stats)
            Console.WriteLine($"  [remesh] split {stepTime[0]:0.0} s, collapse {stepTime[1]:0.0} s, flip {stepTime[2]:0.0} s, relax {stepTime[3]:0.0} s; " +
                $"{projections} projections, {projectTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.0} s in them (summed over threads)");

        var result = new AdaptiveMesh();
        var index = new int[pos.Count];
        Array.Fill(index, -1);
        foreach (var f in faces)
            foreach (var v in f)
            {
                if (index[v] < 0) { index[v] = result.Positions.Count; result.Positions.Add(pos[v]); result.Pinned.Add(false); }
                result.Indices.Add(index[v]);
            }
        return result;
    }
}
