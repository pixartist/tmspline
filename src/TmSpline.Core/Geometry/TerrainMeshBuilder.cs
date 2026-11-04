using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain grid (TerrainGrid) as items: the grid simplified to as few triangles as keep within
/// <see cref="Tolerance"/> of it (a right-triangulated irregular network, as Mapbox's Martini builds it: halving right
/// triangles where they stray, so neighbours always share their edges and there are no cracks), with smooth normals from
/// the whole grid and a skirt hanging from the land's outer edge, cut into square chunks (one item each).
/// </summary>
public static class TerrainMeshBuilder
{
    /// <summary>How far the simplified land may stray from the height map, metres.</summary>
    public const double Tolerance = 0.2;
    /// <summary>Size of a chunk (one item), metres.</summary>
    private const double ChunkSize = 128;

    public static List<MeshChunk> Build(TerrainGrid grid, TrackMap? map = null, string namePrefix = "Terrain")
    {
        var chunks = new List<MeshChunk>();
        if (grid.Columns < 2 || grid.Rows < 2) return chunks;
        var cell = grid.Cell;
        var count = grid.Columns * grid.Rows;
        // The grid's vertices: where they stand and how high, the ones next to the roads moved onto their edges.
        var xs = new double[count];
        var zs = new double[count];
        var heights = (double[])grid.Heights.Clone();
        for (var i = 0; i < count; i++)
        {
            xs[i] = grid.X0 + i % grid.Columns * cell;
            zs[i] = grid.Z0 + i / grid.Columns * cell;
        }
        var snapped = new bool[count];
        if (map is not null) SnapToRoadEdges(grid, map, xs, zs, heights, snapped);
        var triangles = Simplify(grid, heights, snapped);
        if (triangles.Count == 0) return chunks;

        // Outer edges (used by one triangle only) get a skirt, on the side away from their triangle.
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in triangles)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = u < v ? (u, v) : (v, u);
                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
            }

        var material = Surfaces.Terrain(grid.Surface);
        var byChunk = new Dictionary<(int, int), MeshChunk>();
        var vertexOf = new Dictionary<(MeshChunk, int), int>();
        MeshChunk ChunkAt(double x, double z)
        {
            var k = ((int)Math.Floor((x - grid.X0) / ChunkSize), (int)Math.Floor((z - grid.Z0) / ChunkSize));
            if (byChunk.TryGetValue(k, out var chunk)) return chunk;
            var offset = new Vec3d(grid.X0 + k.Item1 * ChunkSize, 0, grid.Z0 + k.Item2 * ChunkSize);
            chunk = new MeshChunk { Name = $"{namePrefix}_{k.Item2 + 100:D3}_{k.Item1 + 100:D3}", ItemOffset = offset };
            chunk.MaterialIndex(material);
            byChunk[k] = chunk;
            return chunk;
        }
        int Vertex(MeshChunk chunk, int i)
        {
            if (vertexOf.TryGetValue((chunk, i), out var v)) return v;
            var o = chunk.ItemOffset;
            var normal = Normal(grid, heights, i % grid.Columns, i / grid.Columns);
            chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(heights[i] - o.Y), (float)(zs[i] - o.Z)));
            chunk.Normals.Add(normal);
            chunk.Uv0.Add(MaskUv(normal.Y));
            // Lightmap: the land over most of the chunk's square, the skirts in a strip beside it.
            chunk.Uv1.Add(new Vector2((float)(0.02 + 0.76 * Math.Clamp((xs[i] - o.X) / ChunkSize, 0, 1)), (float)(0.02 + 0.96 * Math.Clamp((zs[i] - o.Z) / ChunkSize, 0, 1))));
            vertexOf[(chunk, i)] = chunk.Positions.Count - 1;
            return chunk.Positions.Count - 1;
        }
        var skirts = new Dictionary<MeshChunk, int>();
        foreach (var (a, b, c) in triangles)
        {
            var chunk = ChunkAt((xs[a] + xs[b] + xs[c]) / 3, (zs[a] + zs[b] + zs[c]) / 3);
            MeshChunk.AddFacing(chunk, new Triangle(Vertex(chunk, a), Vertex(chunk, b), Vertex(chunk, c), 0), Vector3.UnitY);
            if (grid.Skirt <= 0) continue;
            foreach (var (u, v, w) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                if (edgeUse[u < v ? (u, v) : (v, u)] != 1) continue;
                // Outward: across the edge, away from the triangle's third corner.
                var outward = new Vector3((float)(zs[v] - zs[u]), 0, (float)(xs[u] - xs[v]));
                if (outward.X * (xs[w] - xs[u]) + outward.Z * (zs[w] - zs[u]) > 0) outward = -outward;
                if (outward.LengthSquared() < 1e-12) continue;
                outward = Vector3.Normalize(outward);
                var k = skirts.GetValueOrDefault(chunk);
                skirts[chunk] = k + 1;
                var first = chunk.Positions.Count;
                var o = chunk.ItemOffset;
                foreach (var (i, down) in new[] { (u, 0.0), (v, 0.0), (u, grid.Skirt), (v, grid.Skirt) })
                {
                    chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(heights[i] - down - o.Y), (float)(zs[i] - o.Z)));
                    chunk.Normals.Add(outward);
                    // The hill's side (dirt), as on the hill pieces' flanks.
                    chunk.Uv0.Add(MaskUv(0));
                    // Each skirt piece its own little stretch of the lightmap strip.
                    chunk.Uv1.Add(new Vector2(down == 0 ? 0.8f : 0.98f, 0.02f + 0.96f * ((k % 256) + (i == u ? 0.05f : 0.95f)) / 256));
                }
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 3, 0), outward);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 3, first + 2, 0), outward);
            }
        }
        return byChunk.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// The hill material's first UV layer is no texture position (it projects its textures by position) but its mask
    /// between grass and the flank's dirt, as on the hill pieces: their tops at v 0.4 to 0.9, their sides below 0.3
    /// (measured on DecoHillSlope2Straight). Grass where the land is flat, turning to dirt where it gets steep.
    /// </summary>
    private static Vector2 MaskUv(float normalY)
    {
        var steep = Math.Clamp((0.9f - normalY) / 0.3f, 0, 1);
        return new Vector2(0.5f, 0.55f - 0.4f * steep);
    }

    /// <summary>
    /// Moves the grid's vertices next to the roads onto their edges, at the edges' height (RoadMeshBuilder.EdgeContact:
    /// the curb's top, or the surface's edge), so the land's mesh itself runs along every road's edge: per vertex the
    /// nearest edge within half a cell across it. Not where the road is steeply banked or upside down.
    /// </summary>
    private static void SnapToRoadEdges(TerrainGrid grid, TrackMap map, double[] xs, double[] zs, double[] heights, bool[] snapped)
    {
        var cell = grid.Cell;
        var best = new double[xs.Length];
        Array.Fill(best, double.PositiveInfinity);
        var target = new (double X, double Y, double Z)[xs.Length];
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Low));
            if (rings.Count < 2) continue;
            foreach (var side in new[] { -1, 1 })
            {
                var edge = new Vec3d?[rings.Count];
                for (var i = 0; i < rings.Count; i++)
                {
                    if (rings[i].Up.Y < 0.5) continue;
                    var segment = track.Segments[Math.Clamp(rings[i].Segment, 0, track.Segments.Count - 1)];
                    edge[i] = RoadMeshBuilder.EdgeContact(rings[i], segment, side);
                }
                for (var i = 0; i + 1 < rings.Count; i++)
                {
                    if (edge[i] is not { } p || edge[i + 1] is not { } q) continue;
                    double dx = q.X - p.X, dz = q.Z - p.Z;
                    var length2 = dx * dx + dz * dz;
                    if (length2 < 1e-9) continue;
                    var c0 = (int)Math.Floor((Math.Min(p.X, q.X) - cell - grid.X0) / cell);
                    var c1 = (int)Math.Ceiling((Math.Max(p.X, q.X) + cell - grid.X0) / cell);
                    var r0 = (int)Math.Floor((Math.Min(p.Z, q.Z) - cell - grid.Z0) / cell);
                    var r1 = (int)Math.Ceiling((Math.Max(p.Z, q.Z) + cell - grid.Z0) / cell);
                    for (var r = Math.Max(0, r0); r <= Math.Min(grid.Rows - 1, r1); r++)
                        for (var c = Math.Max(0, c0); c <= Math.Min(grid.Columns - 1, c1); c++)
                        {
                            var k = r * grid.Columns + c;
                            if (double.IsNaN(heights[k]) && !snapped[k]) continue;
                            double vx = grid.X0 + c * cell, vz = grid.Z0 + r * cell;
                            var t = Math.Clamp(((vx - p.X) * dx + (vz - p.Z) * dz) / length2, 0, 1);
                            double ex = p.X + dx * t, ez = p.Z + dz * t;
                            var d = Math.Sqrt((vx - ex) * (vx - ex) + (vz - ez) * (vz - ez));
                            if (d > cell * 0.5 || d >= best[k]) continue;
                            // Only from outside the road (a vertex under it, raised to the edge, would poke through
                            // the surface beside the curb).
                            var right = rings[i].Right + rings[i + 1].Right;
                            if (((vx - ex) * right.X + (vz - ez) * right.Z) * side < -0.25) continue;
                            best[k] = d;
                            target[k] = (ex, p.Y + (q.Y - p.Y) * t, ez);
                        }
                }
            }
        }
        for (var k = 0; k < xs.Length; k++)
        {
            if (double.IsPositiveInfinity(best[k])) continue;
            (xs[k], heights[k], zs[k]) = target[k];
            snapped[k] = true;
        }
    }

    /// <summary>
    /// The land's triangles (grid vertex indices), simplified: the grid padded to a square of 2^k + 1 vertices is split
    /// into right triangles, each halved while the height map strays more than <see cref="Tolerance"/> from it, or while
    /// it holds both land and no land (down to the grid's own triangles, kept where all three corners are land).
    /// </summary>
    private static List<(int A, int B, int C)> Simplify(TerrainGrid grid, double[] heights, bool[] keep)
    {
        var size = 2;
        while (size + 1 < Math.Max(grid.Columns, grid.Rows)) size *= 2;
        var n = size + 1;
        var h = new double[n * n];
        Array.Fill(h, double.NaN);
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
                h[r * n + c] = heights[r * grid.Columns + c];

        // Errors at every triangle's hypotenuse midpoint, smallest triangles first, each also its children's (Martini).
        var errors = new double[n * n];
        var smallest = size * size * 2;
        var count = smallest * 2 - 2;
        var parents = count - smallest;
        for (var t = count - 1; t >= 0; t--)
        {
            var id = t + 2;
            int ax = 0, ay = 0, bx = 0, by = 0, cx = 0, cy = 0;
            if ((id & 1) != 0) { bx = by = cx = size; }
            else { ax = ay = cy = size; }
            while ((id >>= 1) > 1)
            {
                var hx = (ax + bx) >> 1;
                var hy = (ay + by) >> 1;
                if ((id & 1) != 0) { bx = ax; by = ay; ax = cx; ay = cy; }
                else { ax = bx; ay = by; bx = cx; by = cy; }
                cx = hx;
                cy = hy;
            }
            var mx = (ax + bx) >> 1;
            var my = (ay + by) >> 1;
            var m = my * n + mx;
            double ha = h[ay * n + ax], hb = h[by * n + bx], hm = h[m], hc = h[cy * n + cx];
            var nan = (double.IsNaN(ha) ? 1 : 0) + (double.IsNaN(hb) ? 1 : 0) + (double.IsNaN(hm) ? 1 : 0) + (double.IsNaN(hc) ? 1 : 0);
            // Land and no land in one triangle: always split (an all-empty one never matters).
            var error = nan == 0 ? Math.Abs((ha + hb) / 2 - hm) : nan == 4 ? 0 : double.PositiveInfinity;
            // Full detail round the vertices on the roads' edges (the edge lines must stay as they are).
            if (Kept(keep, grid, ax, ay) || Kept(keep, grid, bx, by) || Kept(keep, grid, mx, my) || Kept(keep, grid, cx, cy)) error = double.PositiveInfinity;
            errors[m] = Math.Max(errors[m], error);
            if (t < parents)
            {
                var left = ((ay + cy) >> 1) * n + ((ax + cx) >> 1);
                var right = ((by + cy) >> 1) * n + ((bx + cx) >> 1);
                errors[m] = Math.Max(errors[m], Math.Max(errors[left], errors[right]));
            }
        }

        var triangles = new List<(int, int, int)>();
        var stack = new Stack<(int Ax, int Ay, int Bx, int By, int Cx, int Cy)>();
        stack.Push((0, 0, size, size, size, 0));
        stack.Push((size, size, 0, 0, 0, size));
        while (stack.Count > 0)
        {
            var (ax, ay, bx, by, cx, cy) = stack.Pop();
            var mx = (ax + bx) >> 1;
            var my = (ay + by) >> 1;
            if (Math.Abs(ax - cx) + Math.Abs(ay - cy) > 1 && errors[my * n + mx] > Tolerance)
            {
                stack.Push((cx, cy, ax, ay, mx, my));
                stack.Push((bx, by, cx, cy, mx, my));
                continue;
            }
            int a = ay * n + ax, b = by * n + bx, c = cy * n + cx;
            if (double.IsNaN(h[a]) || double.IsNaN(h[b]) || double.IsNaN(h[c])) continue;
            triangles.Add((ay * grid.Columns + ax, by * grid.Columns + bx, cy * grid.Columns + cx));
        }
        return triangles;
    }

    private static bool Kept(bool[] keep, TerrainGrid grid, int c, int r) =>
        c < grid.Columns && r < grid.Rows && keep[r * grid.Columns + c];

    /// <summary>
    /// The map's terrain (its chunks) as the export builds it, in world space, packed for the editor:
    /// <code>
    /// int32 vertexCount, int32 triangleCount, float32[vertexCount*3] positions, float32[vertexCount*3] normals,
    /// float32[vertexCount*2] uv0, int32[triangleCount*3] indices
    /// </code>
    /// Empty (both counts 0) without terrain.
    /// </summary>
    public static byte[] Preview(string mapJson)
    {
        var map = TrackJson.ParseMap(mapJson);
        var chunks = map.Terrain is { } grid ? Build(grid, map) : [];
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        foreach (var c in chunks)
        {
            var o = new Vector3((float)c.ItemOffset.X, (float)c.ItemOffset.Y, (float)c.ItemOffset.Z);
            var first = positions.Count;
            positions.AddRange(c.Positions.Select(p => p + o));
            normals.AddRange(c.Normals);
            uvs.AddRange(c.Uv0);
            foreach (var t in c.Triangles) indices.AddRange([first + t.A, first + t.B, first + t.C]);
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(positions.Count);
        w.Write(indices.Count / 3);
        foreach (var p in positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var n in normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
        foreach (var u in uvs) { w.Write(u.X); w.Write(u.Y); }
        foreach (var i in indices) w.Write(i);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>The surface normal at a grid vertex from its neighbours' heights (one-sided at the land's edge).</summary>
    private static Vector3 Normal(TerrainGrid grid, double[] heights, int c, int r)
    {
        double H(int cc, int rr) => cc < 0 || rr < 0 || cc >= grid.Columns || rr >= grid.Rows ? double.NaN : heights[rr * grid.Columns + cc];
        var h = H(c, r);
        double At(int dc, int dr) { var v = H(c + dc, r + dr); return double.IsNaN(v) ? h : v; }
        var dx = (At(1, 0) - At(-1, 0)) / (2 * grid.Cell);
        var dz = (At(0, 1) - At(0, -1)) / (2 * grid.Cell);
        return Vector3.Normalize(new Vector3((float)-dx, 1, (float)-dz));
    }
}
