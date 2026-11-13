using System.Numerics;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

/// <summary>
/// Seam experiment: builds a map's voxel terrain with the land the roads enclose left out (VoxelTerrain.OpenSeam),
/// finds the rims of the holes (boundary loops of the mesh) and tells for each loop which road edges it runs along:
/// the seam's three cases (one loop round both edges, one loop crossing under the road where it leaves the land, two
/// loops, one per edge) should show as the runs of edge labels along each loop.
/// Usage: MesherLab --seam map.json [reach m, default 12]
/// </summary>
static class SeamLoops
{
    private sealed record Rail(int Track, int Side, List<(Vector3 P, double Distance)> Points);

    public static void Run(string mapFile, double reach)
    {
        var map = TrackJson.ParseMap(File.ReadAllText(mapFile));
        var grid = map.Terrain ?? throw new InvalidOperationException("no terrain");
        VoxelTerrain.OpenSeam = true;
        var mesh = VoxelTerrain.Build(grid, map);
        Console.WriteLine($"{Path.GetFileName(mapFile)}: cells {VoxelTerrain.Voxel} m, {mesh.Indices.Count / 3} triangles with the enclosed land left out");
        // How thin the triangles are: how many have a smallest angle under 10 and under 20 degrees.
        int thin10 = 0, thin20 = 0;
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            Vector3 p0 = mesh.Positions[mesh.Indices[t]], p1 = mesh.Positions[mesh.Indices[t + 1]], p2 = mesh.Positions[mesh.Indices[t + 2]];
            static float Angle(Vector3 p, Vector3 q, Vector3 r) { Vector3 u = q - p, v = r - p; var d = u.Length() * v.Length(); return d < 1e-12f ? 0 : MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / d, -1, 1)) * 180 / MathF.PI; }
            var min = MathF.Min(Angle(p0, p1, p2), MathF.Min(Angle(p1, p2, p0), Angle(p2, p0, p1)));
            if (min < 10) thin10++;
            if (min < 20) thin20++;
        }
        Console.WriteLine($"thin triangles: {thin10} with an angle under 10 degrees, {thin20} under 20");

        // The road edges: per track and side, points every ring (world space) with their distance along the track.
        var rails = new List<Rail>();
        for (var t = 0; t < map.Tracks.Count; t++)
        {
            var track = map.Tracks[t];
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Normal));
            foreach (var side in new[] { -1, 1 })
            {
                var points = rings.Select(r =>
                {
                    var e = RoadMeshBuilder.SurfaceSample(r, side * r.Width / 2);
                    var w = r.Position + r.Right * e.X + r.Up * e.Y;
                    return (new Vector3((float)w.X, (float)w.Y, (float)w.Z), r.Distance);
                }).ToList();
                rails.Add(new Rail(t, side, points));
            }
        }
        // A bucket grid of the edge points, 8 m squares.
        var buckets = new Dictionary<(int, int), List<(int Rail, int Index)>>();
        for (var r = 0; r < rails.Count; r++)
            for (var i = 0; i < rails[r].Points.Count; i++)
            {
                var p = rails[r].Points[i].P;
                var key = ((int)MathF.Floor(p.X / 8), (int)MathF.Floor(p.Z / 8));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add((r, i));
            }
        (int Rail, double Along, float Distance) Nearest(Vector3 p)
        {
            var best = (Rail: -1, Along: 0.0, Distance: float.MaxValue);
            var cells = (int)Math.Ceiling(reach / 8);
            int bx = (int)MathF.Floor(p.X / 8), bz = (int)MathF.Floor(p.Z / 8);
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var z = bz - cells; z <= bz + cells; z++)
                    if (buckets.TryGetValue((x, z), out var list))
                        foreach (var (r, i) in list)
                        {
                            var d = Vector3.Distance(p, rails[r].Points[i].P);
                            if (d < best.Distance) best = (r, rails[r].Points[i].Distance, d);
                        }
            return best.Distance <= reach ? best : (-1, 0, best.Distance);
        }

        // Boundary edges (used by one triangle), each from a to b as the triangle winds, chained into loops.
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var next = new Dictionary<int, int>();
        var branching = 0;
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                if (count[a < b ? (a, b) : (b, a)] != 1) continue;
                if (!next.TryAdd(a, b)) branching++;
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
            loops.Add(loop);
        }
        Console.WriteLine($"{loops.Count} boundary loops ({next.Count} rim vertices, {branching} vertices where rims touch)");

        // Each loop as runs of labels: the rail its vertices are nearest (within reach), or "-" for none.
        var touching = 0;
        foreach (var loop in loops.OrderByDescending(l => l.Count))
        {
            var labels = loop.Select(v => Nearest(mesh.Positions[v])).ToList();
            if (labels.All(l => l.Rail < 0)) continue;
            touching++;
            var runs = new List<(int Rail, int Count, double From, double To)>();
            foreach (var l in labels)
            {
                if (runs.Count > 0 && runs[^1].Rail == l.Rail)
                    runs[^1] = (l.Rail, runs[^1].Count + 1, runs[^1].From, l.Along);
                else runs.Add((l.Rail, 1, l.Along, l.Along));
            }
            // (A loop is a cycle: its last run continues into its first.)
            if (runs.Count > 1 && runs[0].Rail == runs[^1].Rail)
            {
                runs[0] = (runs[0].Rail, runs[0].Count + runs[^1].Count, runs[^1].From, runs[0].To);
                runs.RemoveAt(runs.Count - 1);
            }
            string Name(int r) => r < 0 ? "-" : $"T{rails[r].Track}{(rails[r].Side < 0 ? "L" : "R")}";
            var near = labels.Count(l => l.Rail >= 0);
            var text = string.Join(" ", runs.Where(r => r.Count >= 2 || r.Rail >= 0).Take(24)
                .Select(r => r.Rail < 0 ? $"-({r.Count})" : $"{Name(r.Rail)}[{r.From:0}..{r.To:0} m]({r.Count})"));
            Console.WriteLine($"  loop of {loop.Count} vertices, {near} near an edge, {runs.Count} runs: {text}{(runs.Count > 24 ? " ..." : "")}");
        }
        Console.WriteLine($"{touching} loops touch a road edge");
    }
}
