using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Seam experiment: closes the gap the cut left between the land and the roads with strips of triangles, from the
/// land's rim to the road's edge. Each rim loop is walked and its vertices matched to the nearest road edge (and how
/// far along it); a run of the loop along one edge is joined to that stretch of the edge like a zip, always taking the
/// shorter of the two next diagonals. The strip's triangles turn the same way as the land's, and the edge's points are
/// shared by the runs that use them. Where the loop leaves an edge (crossing under the road, round its end) the gap
/// stays open for now.
/// </summary>
public static class SeamZipper
{
    /// <summary>The edge's points are no closer together along it than this, metres.</summary>
    private const float EdgeStep = 2;

    private sealed class Rail
    {
        public readonly List<Vector3> Points = [];
        public readonly List<float> Along = [];
    }

    public static int Zip(AdaptiveMesh mesh, TrackMap map, Func<Vector3, float> surface, float gap)
    {
        // The road edges: per track and side, points along the driving surface's edge, about EdgeStep apart.
        var rails = new List<Rail>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.High));
            if (rings.Count < 2) continue;
            foreach (var side in new[] { -1, 1 })
            {
                var rail = new Rail();
                for (var i = 0; i < rings.Count; i++)
                {
                    var r = rings[i];
                    if (rail.Along.Count > 0 && r.Distance - rail.Along[^1] < EdgeStep && i < rings.Count - 1) continue;
                    var e = RoadMeshBuilder.SurfaceSample(r, side * r.Width / 2);
                    var w = r.Position + r.Right * e.X + r.Up * e.Y;
                    rail.Points.Add(new Vector3((float)w.X, (float)w.Y, (float)w.Z));
                    rail.Along.Add((float)r.Distance);
                }
                if (rail.Points.Count >= 2) rails.Add(rail);
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
        // The nearest point on any edge within reach: which edge, how far along it (metres), how far away.
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
                            // The two segments from this point.
                            for (var k = Math.Max(i - 1, 0); k < Math.Min(i + 1, rail.Points.Count - 1); k++)
                            {
                                Vector3 a = rail.Points[k], b = rail.Points[k + 1];
                                var e = b - a;
                                var h = Math.Clamp(Vector3.Dot(p - a, e) / MathF.Max(e.LengthSquared(), 1e-9f), 0, 1);
                                var d = Vector3.Distance(p, a + e * h);
                                if (d < best.Distance) best = (r, rail.Along[k] + (rail.Along[k + 1] - rail.Along[k]) * h, d);
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
            if (i >= rail.Along.Count) return rail.Along.Count - 1;
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
            bool Continues(int i, int j, int direction) =>
                match[i].Rail >= 0 && match[i].Rail == match[j].Rail && MathF.Abs(match[j].Along - match[i].Along) < 3 * gap
                && (direction == 0 || (match[j].Along - match[i].Along) * direction > -0.5f);
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
                    if (direction == 0 && MathF.Abs(match[i1].Along - match[last].Along) > 0.5f) direction = MathF.Sign(match[i1].Along - match[last].Along);
                    run.Add(i1);
                }
                at += run.Count;
                if (run.Count < 2 || direction == 0) continue;
                // The stretch of the edge, the same way as the rim runs.
                var r = match[run[0]].Rail;
                var rail = rails[r];
                int j0 = IndexAt(rail, match[run[0]].Along), j1 = IndexAt(rail, match[run[^1]].Along);
                var edge = new List<int>();
                for (var j = j0; ; j += direction)
                {
                    edge.Add(j);
                    if (j == j1 || j < 0 || j >= rail.Points.Count) break;
                }
                // Zip: the rim's run (as the land's edges run) and the edge's stretch, the shorter diagonal first.
                var rim = run.Select(k => loop[k]).ToList();
                var ids = edge.Select(j => EdgeVertex(r, j)).ToList();
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
                    made++;
                }
            }
        }
        return made;
    }
}
