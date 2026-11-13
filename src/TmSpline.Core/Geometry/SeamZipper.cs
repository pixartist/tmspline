using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Seam experiment: closes the gap the cut left between the land and the roads with strips of triangles, from the
/// land's rim to the road's edge. Each rim loop is walked and its vertices matched to the nearest road edge (and how
/// far along it); a run of the loop along one edge is joined to that stretch of the edge like a zip, always taking the
/// shorter of the two next diagonals. The strip's triangles turn the same way as the land's, and the edge's points are
/// shared by the runs that use them. An open road's edge is one outline round it (up its left edge, across its end,
/// back down its right edge, across its start), so a run goes on round its ends; a closed track has two edges, each a
/// loop. Where the rim leaves an edge (crossing under the road) the gap stays open for now.
/// </summary>
public static class SeamZipper
{
    private sealed class Rail
    {
        public readonly List<Vector3> Points = [];
        /// <summary>Each point's distance along the outline from its first, metres.</summary>
        public readonly List<float> Along = [];
        /// <summary>The outline's length, back round to its first point.</summary>
        public float Length;

        public void Add(Vector3 p)
        {
            Along.Add(Points.Count == 0 ? 0 : Along[^1] + Vector3.Distance(Points[^1], p));
            Points.Add(p);
        }

        public void Close() => Length = MathF.Max(Along[^1] + Vector3.Distance(Points[^1], Points[0]), 1e-3f);

        /// <summary>From one distance along to another, the short way round (negative: backwards).</summary>
        public float Step(float from, float to)
        {
            var d = (to - from) % Length;
            if (d > Length / 2) d -= Length;
            else if (d < -Length / 2) d += Length;
            return d;
        }
    }

    /// <summary>
    /// The projection zipper (the default; TMSPLINE_ZIP=rail: the edge-following one below): each rim vertex joined to
    /// its nearest point on the nearest road's surface line (its edges, the back of its surface, past an open end its
    /// end), the strip running between the rim and those points. Beside a road that is its edge; under it, or round a
    /// tube, its underside: there the seam leaves the edge and runs along the road's body (the owner's sketch). A rim
    /// vertex whose line to that point passes through a road's room (over a tunnel's mouth) is not joined: that rim is
    /// an opening.
    /// </summary>
    public static int Zip(AdaptiveMesh mesh, TrackMap map, Func<Vector3, (float Distance, Vector3 Point)> surface, Func<Vector3, float> room, float gap)
    {
        if (Environment.GetEnvironmentVariable("TMSPLINE_ZIP") == "rail") return ZipEdges(mesh, map, p => surface(p).Distance, room, gap);
        var made = 0;
        foreach (var loop in RimLoops(mesh))
        {
            var n = loop.Count;
            var target = new int[n];
            for (var i = 0; i < n; i++)
            {
                target[i] = -1;
                var p = mesh.Positions[loop[i]];
                var (distance, point) = surface(p);
                if (distance >= 1.5f * gap || !Clear(room, p, point)) continue;
                target[i] = mesh.Positions.Count;
                mesh.Positions.Add(point);
                mesh.Pinned.Add(false);
            }
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                if (target[i] < 0 || target[j] < 0) continue;
                Vector3 pi = mesh.Positions[target[i]], pj = mesh.Positions[target[j]];
                // (Not across from one road or side to another: the points far apart.)
                if (Vector3.Distance(pi, pj) > 3 * gap) continue;
                mesh.Indices.AddRange([loop[j], loop[i], target[i]]);
                made++;
                if (Vector3.DistanceSquared(pi, pj) > 1e-4f)
                {
                    mesh.Indices.AddRange([loop[j], target[i], target[j]]);
                    made++;
                }
            }
        }
        return made;
    }

    /// <summary>
    /// Whether the straight line from a place to a point on a road stays out of the roads' rooms (the owner's rule: no
    /// seam through the road; at a tunnel's mouth the land over it would otherwise reach down through it). The last
    /// stretch before the road is left out: the room reaches a little past the deck's edge and under it.
    /// </summary>
    private static bool Clear(Func<Vector3, float> room, Vector3 from, Vector3 to)
    {
        var length = Vector3.Distance(from, to);
        var steps = Math.Max(4, (int)MathF.Ceiling(length / 0.5f));
        for (var k = 1; k < steps; k++)
        {
            if (length * (1 - k / (float)steps) < 1.5f) break;
            if (room(Vector3.Lerp(from, to, k / (float)steps)) < -0.5f) return false;
        }
        return true;
    }

    /// <summary>The rim: boundary edges (used by one triangle), each from a to b as the triangle winds, chained into loops.</summary>
    private static List<List<int>> RimLoops(AdaptiveMesh mesh)
    {
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var next = new Dictionary<int, int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                if (count[a < b ? (a, b) : (b, a)] == 1) next.TryAdd(a, b);
            }
        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int>();
            var v = start;
            while (seen.Add(v))
            {
                loop.Add(v);
                if (!next.TryGetValue(v, out v)) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>The edge-following zipper (TMSPLINE_ZIP=rail): rim runs matched to the road edges' outlines.</summary>
    private static int ZipEdges(AdaptiveMesh mesh, TrackMap map, Func<Vector3, float> surface, Func<Vector3, float> room, float gap)
    {
        // The road edges as outlines, points about half the gap apart (about as far apart as the rim's vertices).
        var step = gap / 2;
        var rails = new List<Rail>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.High));
            if (rings.Count < 2) continue;
            static Vector3 At(Ring r, double across)
            {
                var e = RoadMeshBuilder.SurfaceSample(r, across);
                var w = r.Position + r.Right * e.X + r.Up * e.Y;
                return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
            }
            // One side's points along the road, about `step` apart (both ends always).
            List<Vector3> Side(int side)
            {
                var points = new List<Vector3>();
                var last = double.MinValue;
                for (var i = 0; i < rings.Count; i++)
                {
                    if (rings[i].Distance - last < step && i < rings.Count - 1) continue;
                    points.Add(At(rings[i], side * rings[i].Width / 2));
                    last = rings[i].Distance;
                }
                return points;
            }
            // Across a ring from one edge (from: -1 left, 1 right) towards the other, the points between them.
            IEnumerable<Vector3> Across(Ring r, int from)
            {
                var n = Math.Max(1, (int)Math.Round(r.Width / step));
                for (var k = 1; k < n; k++) yield return At(r, from * (0.5 - k / (double)n) * r.Width);
            }
            List<Vector3> left = Side(-1), right = Side(1);
            if (track.Closed)
            {
                foreach (var points in new[] { left, right })
                {
                    var rail = new Rail();
                    // (The last ring is the first again.)
                    var closing = points.Count > 2 && Vector3.Distance(points[0], points[^1]) < 0.5f;
                    foreach (var p in closing ? points.SkipLast(1) : points) rail.Add(p);
                    rail.Close();
                    rails.Add(rail);
                }
            }
            else
            {
                var rail = new Rail();
                foreach (var p in left) rail.Add(p);
                foreach (var p in Across(rings[^1], -1)) rail.Add(p);
                for (var i = right.Count - 1; i >= 0; i--) rail.Add(right[i]);
                foreach (var p in Across(rings[0], 1)) rail.Add(p);
                rail.Close();
                rails.Add(rail);
            }
        }
        if (rails.Count == 0) return 0;

        // A bucket grid of the edges' points, 8 m squares.
        const float Bucket = 8;
        var buckets = new Dictionary<(int, int), List<(int Rail, int Index)>>();
        for (var r = 0; r < rails.Count; r++)
            for (var i = 0; i < rails[r].Points.Count; i++)
            {
                var p = rails[r].Points[i];
                var key = ((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Z / Bucket));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add((r, i));
            }
        var reach = 2.5f * gap;
        var Diag = Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on";
        var blocked = 0;
        // Whether the straight line from a place to an edge point stays out of the roads' rooms (the owner's rule: no
        // seam through the road; at a tunnel's mouth the land over it would otherwise reach down through it).
        bool Clear(Vector3 from, Vector3 to)
        {
            var steps = Math.Max(4, (int)MathF.Ceiling(Vector3.Distance(from, to) / 0.5f));
            // (The last stretch before the edge left out: the room reaches a little past the deck's edge and under it.)
            var length = Vector3.Distance(from, to);
            for (var k = 1; k < steps; k++)
            {
                if (length * (1 - k / (float)steps) < 1.5f) break;
                var value = room(Vector3.Lerp(from, to, k / (float)steps));
                if (value < -0.5f)
                {
                    if (Diag && blocked++ < 30) Console.WriteLine($"  blocked from {from} to {to} ({Vector3.Distance(from, to):0.0} m): room {value:0.00} at {k / (float)steps:0.00} of the way, {Vector3.Distance(Vector3.Lerp(from, to, k / (float)steps), to):0.0} m before the edge");
                    return false;
                }
            }
            return true;
        }
        // The nearest point on any edge within reach that can be seen from the place without passing through a road:
        // which edge, how far along it (metres), how far away.
        (int Rail, float Along, float Distance) Nearest(Vector3 p)
        {
            var best = (Rail: -1, Along: 0f, Distance: reach);
            var cells = (int)MathF.Ceiling(reach / Bucket);
            int bx = (int)MathF.Floor(p.X / Bucket), bz = (int)MathF.Floor(p.Z / Bucket);
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var z = bz - cells; z <= bz + cells; z++)
                    if (buckets.TryGetValue((x, z), out var list))
                        foreach (var (r, i) in list)
                        {
                            var rail = rails[r];
                            // The two segments from this point (the outline goes round).
                            var count = rail.Points.Count;
                            for (var k = i - 1; k <= i; k++)
                            {
                                int k0 = (k + count) % count, k1 = (k0 + 1) % count;
                                Vector3 a = rail.Points[k0], b = rail.Points[k1];
                                var e = b - a;
                                var h = Math.Clamp(Vector3.Dot(p - a, e) / MathF.Max(e.LengthSquared(), 1e-9f), 0, 1);
                                var d = Vector3.Distance(p, a + e * h);
                                var length = k1 == 0 ? rail.Length - rail.Along[k0] : rail.Along[k1] - rail.Along[k0];
                                if (d < best.Distance && Clear(p, a + e * h)) best = (r, (rail.Along[k0] + length * h) % rail.Length, d);
                            }
                        }
            return best;
        }

        // The rim: boundary edges (used by one triangle), each from a to b as the triangle winds, chained into loops.
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var next = new Dictionary<int, int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                if (count[a < b ? (a, b) : (b, a)] == 1) next.TryAdd(a, b);
            }
        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int>();
            var v = start;
            while (seen.Add(v))
            {
                loop.Add(v);
                if (!next.TryGetValue(v, out v)) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }

        // The edges' points as the mesh's vertices, made when a strip first uses them.
        var edgeVertex = new Dictionary<(int, int), int>();
        int EdgeVertex(int rail, int index)
        {
            if (!edgeVertex.TryGetValue((rail, index), out var id))
            {
                id = mesh.Positions.Count;
                mesh.Positions.Add(rails[rail].Points[index]);
                mesh.Pinned.Add(false);
                edgeVertex[(rail, index)] = id;
            }
            return id;
        }
        // The edge point nearest a distance along it.
        static int IndexAt(Rail rail, float along)
        {
            var i = rail.Along.BinarySearch(along);
            if (i >= 0) return i;
            i = ~i;
            if (i == 0) return 0;
            if (i >= rail.Along.Count) return along - rail.Along[^1] < rail.Length - along ? rail.Along.Count - 1 : 0;
            return along - rail.Along[i - 1] < rail.Along[i] - along ? i - 1 : i;
        }

        var made = 0;
        foreach (var loop in loops)
        {
            var match = loop.Select(v =>
            {
                var p = mesh.Positions[v];
                return surface(p) < 1.5f * gap ? Nearest(p) : (Rail: -1, Along: 0f, Distance: float.MaxValue);
            }).ToArray();
            // Runs: consecutive vertices on the same edge, going one way along it without big jumps.
            float Delta(int i, int j) => rails[match[i].Rail].Step(match[i].Along, match[j].Along);
            bool Continues(int i, int j, int direction) =>
                match[i].Rail >= 0 && match[i].Rail == match[j].Rail && MathF.Abs(Delta(i, j)) < 3 * gap
                && (direction == 0 || Delta(i, j) * direction > -0.5f);
            // Start the walk where a run begins (if the whole loop is one run, anywhere).
            var n = loop.Count;
            var first = 0;
            for (var i = 0; i < n; i++)
                if (!Continues((i - 1 + n) % n, i, 0)) { first = i; break; }
            var at = 0;
            while (at < n)
            {
                var i0 = (first + at) % n;
                if (match[i0].Rail < 0) { at++; continue; }
                var run = new List<int> { i0 };
                var direction = 0;
                while (at + run.Count < n)
                {
                    var i1 = (first + at + run.Count) % n;
                    var last = run[^1];
                    if (!Continues(last, i1, direction)) break;
                    if (direction == 0 && MathF.Abs(Delta(last, i1)) > 0.5f) direction = MathF.Sign(Delta(last, i1));
                    run.Add(i1);
                }
                at += run.Count;
                if (run.Count < 2 || direction == 0) continue;
                // The whole loop along one outline (land round a road all the way): the run closes on itself.
                var whole = run.Count == n && Continues(run[^1], run[0], direction);
                // The stretch of the edge, the same way as the rim runs: as far along as the rim goes.
                var r = match[run[0]].Rail;
                var rail = rails[r];
                var travel = 0f;
                for (var k = 1; k < run.Count; k++) travel += Delta(run[k - 1], run[k]) * direction;
                if (whole) travel += Delta(run[^1], run[0]) * direction;
                int j0 = IndexAt(rail, match[run[0]].Along), points = rail.Points.Count;
                // (From the edge point nearest the run's start: how far that already is along the way.)
                var gone = rail.Step(match[run[0]].Along, rail.Along[j0]) * direction;
                var edge = new List<int> { j0 };
                for (var j = j0; edge.Count <= points;)
                {
                    var j2 = (j + direction + points) % points;
                    var length = MathF.Abs(rail.Step(rail.Along[j], rail.Along[j2]));
                    if (!whole && gone + length / 2 > travel) break;
                    gone += length;
                    j = j2;
                    edge.Add(j);
                    if (whole && j == j0) break;
                }
                // Zip: the rim's run (as the land's edges run) and the edge's stretch, the shorter diagonal first.
                var rim = run.Select(k => loop[k]).ToList();
                if (whole) rim.Add(rim[0]);
                var ids = edge.Select(j => EdgeVertex(r, j)).ToList();
                if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                {
                    var far = 0f;
                    for (var k = 1; k < ids.Count; k++) far = MathF.Max(far, Vector3.Distance(mesh.Positions[ids[k - 1]], mesh.Positions[ids[k]]));
                    if (far > 3 * gap)
                        Console.WriteLine($"  run of {rim.Count} rim vertices at {mesh.Positions[rim[0]]}: edge {r}, {edge.Count} points from {rail.Along[j0]:0} m, direction {direction}, travel {travel:0} m, whole {whole}; edge points up to {far:0} m apart; along {string.Join(" ", run.Take(12).Select(k => match[k].Along.ToString("0")))}");
                }
                int a = 0, b = 0;
                while (a < rim.Count - 1 || b < ids.Count - 1)
                {
                    bool rimStep;
                    if (a == rim.Count - 1) rimStep = false;
                    else if (b == ids.Count - 1) rimStep = true;
                    else rimStep = Vector3.DistanceSquared(mesh.Positions[rim[a + 1]], mesh.Positions[ids[b]])
                        <= Vector3.DistanceSquared(mesh.Positions[rim[a]], mesh.Positions[ids[b + 1]]);
                    if (rimStep)
                    {
                        mesh.Indices.AddRange([rim[a + 1], rim[a], ids[b]]);
                        a++;
                    }
                    else
                    {
                        mesh.Indices.AddRange([rim[a], ids[b], ids[b + 1]]);
                        b++;
                    }
                    if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                    {
                        int t0 = mesh.Indices[^3], t1 = mesh.Indices[^2], t2 = mesh.Indices[^1];
                        Vector3 q0 = mesh.Positions[t0], q1 = mesh.Positions[t1], q2 = mesh.Positions[t2];
                        var longest = MathF.Max(Vector3.Distance(q0, q1), MathF.Max(Vector3.Distance(q1, q2), Vector3.Distance(q2, q0)));
                        if (longest > 4 * gap) Console.WriteLine($"  long strip triangle {longest:0} m at {q0}, {q1}, {q2}: edge {r}, run of {rim.Count}, stretch of {ids.Count} from {rail.Along[j0]:0} m, direction {direction}, travel {travel:0}");
                    }
                    made++;
                }
            }
        }
        return made;
    }
}
