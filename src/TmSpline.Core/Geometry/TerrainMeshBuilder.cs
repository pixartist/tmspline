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

    public static List<MeshChunk> Build(TerrainGrid grid, string namePrefix = "Terrain")
    {
        var chunks = new List<MeshChunk>();
        if (grid.Columns < 2 || grid.Rows < 2) return chunks;
        var triangles = Simplify(grid);
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
        var cell = grid.Cell;
        (double X, double Z) At(int i) => (grid.X0 + i % grid.Columns * cell, grid.Z0 + i / grid.Columns * cell);
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
            var (x, z) = At(i);
            var y = grid.Heights[i];
            var o = chunk.ItemOffset;
            chunk.Positions.Add(new Vector3((float)(x - o.X), (float)(y - o.Y), (float)(z - o.Z)));
            chunk.Normals.Add(Normal(grid, i % grid.Columns, i / grid.Columns));
            chunk.Uv0.Add(new Vector2((float)(x / 32), (float)(z / 32)));
            // Lightmap: the land over most of the chunk's square, the skirts in a strip beside it.
            chunk.Uv1.Add(new Vector2((float)(0.02 + 0.76 * Math.Clamp((x - o.X) / ChunkSize, 0, 1)), (float)(0.02 + 0.96 * Math.Clamp((z - o.Z) / ChunkSize, 0, 1))));
            vertexOf[(chunk, i)] = chunk.Positions.Count - 1;
            return chunk.Positions.Count - 1;
        }
        var skirts = new Dictionary<MeshChunk, int>();
        foreach (var (a, b, c) in triangles)
        {
            var (ax, az) = At(a);
            var (bx, bz) = At(b);
            var (cx, cz) = At(c);
            var chunk = ChunkAt((ax + bx + cx) / 3, (az + bz + cz) / 3);
            MeshChunk.AddFacing(chunk, new Triangle(Vertex(chunk, a), Vertex(chunk, b), Vertex(chunk, c), 0), Vector3.UnitY);
            if (grid.Skirt <= 0) continue;
            foreach (var (u, v, w) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                if (edgeUse[u < v ? (u, v) : (v, u)] != 1) continue;
                var (ux, uz) = At(u);
                var (vx, vz) = At(v);
                var (wx, wz) = At(w);
                // Outward: across the edge, away from the triangle's third corner.
                var outward = new Vector3((float)(vz - uz), 0, (float)(ux - vx));
                if (outward.X * (wx - ux) + outward.Z * (wz - uz) > 0) outward = -outward;
                outward = Vector3.Normalize(outward);
                var k = skirts.GetValueOrDefault(chunk);
                skirts[chunk] = k + 1;
                var first = chunk.Positions.Count;
                var o = chunk.ItemOffset;
                foreach (var (i, down) in new[] { (u, 0.0), (v, 0.0), (u, grid.Skirt), (v, grid.Skirt) })
                {
                    var (x, z) = At(i);
                    var y = grid.Heights[i] - down;
                    chunk.Positions.Add(new Vector3((float)(x - o.X), (float)(y - o.Y), (float)(z - o.Z)));
                    chunk.Normals.Add(outward);
                    chunk.Uv0.Add(new Vector2((float)((x + z) / 32), (float)(y / 32)));
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
    /// The land's triangles (grid vertex indices), simplified: the grid padded to a square of 2^k + 1 vertices is split
    /// into right triangles, each halved while the height map strays more than <see cref="Tolerance"/> from it, or while
    /// it holds both land and no land (down to the grid's own triangles, kept where all three corners are land).
    /// </summary>
    private static List<(int A, int B, int C)> Simplify(TerrainGrid grid)
    {
        var size = 2;
        while (size + 1 < Math.Max(grid.Columns, grid.Rows)) size *= 2;
        var n = size + 1;
        var h = new double[n * n];
        Array.Fill(h, double.NaN);
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
                h[r * n + c] = grid.Heights[r * grid.Columns + c];

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

    /// <summary>
    /// The map's terrain (grid chunks and edge strips) as the export builds it, in world space, packed for the editor:
    /// <code>
    /// int32 vertexCount, int32 triangleCount, float32[vertexCount*3] positions, float32[vertexCount*3] normals,
    /// float32[vertexCount*2] uv0, int32[triangleCount*3] indices
    /// </code>
    /// Empty (both counts 0) without terrain.
    /// </summary>
    public static byte[] Preview(string mapJson)
    {
        var map = TrackJson.ParseMap(mapJson);
        var chunks = map.Terrain is { } grid ? Build(grid).Concat(BuildFillets(map, grid)).ToList() : [];
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

    /// <summary>
    /// How far a road edge's strip reaches out, in grid cells: past the vertices held below the road (those within a cell
    /// of it) and the triangles round them, onto land at its own height (less on the inside of tight bends).
    /// </summary>
    private const double FilletCells = 3;
    /// <summary>
    /// The strip's cross-section: from the road's edge it climbs steeply, then levels into the land (shares of its width and
    /// of the height to the land), as land rising from a curb's top.
    /// </summary>
    private static readonly (double Along, double Up)[] FilletProfile = [(0, 0), (0.3, 0.55), (0.65, 0.88), (1, 1)];
    /// <summary>Rings per strip item.</summary>
    private const int FilletRingsPerChunk = 48;
    /// <summary>No strip where the terrain is this far below the road's edge (a bridge over a valley).</summary>
    private const double FilletMaxDrop = 24;

    /// <summary>
    /// Strips along both edges of every road, from the road's edge contact (RoadMeshBuilder.EdgeContact: the curb's top
    /// or the surface's edge) out to the terrain's surface, so the land meets the road exactly. The grid beside the roads
    /// stays below their edges (the editor caps it), so the strips lie on top of it. None where the road is steeply banked
    /// or upside down, where there is no terrain, or where the terrain is far below.
    /// </summary>
    public static List<MeshChunk> BuildFillets(TrackMap map, TerrainGrid grid, string namePrefix = "Fillet")
    {
        var chunks = new List<MeshChunk>();
        var material = Surfaces.Terrain(grid.Surface);
        for (var t = 0; t < map.Tracks.Count; t++)
        {
            var track = map.Tracks[t];
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Low));
            if (rings.Count < 2) continue;
            foreach (var side in new[] { -1, 1 })
            {
                // Each ring's edge contact and the strip's outer point on the terrain (null: no strip there).
                var strip = new (Vec3d Edge, Vec3d Out)?[rings.Count];
                for (var i = 0; i < rings.Count; i++)
                {
                    var ring = rings[i];
                    if (ring.Up.Y < 0.5) continue;
                    var segment = track.Segments[Math.Clamp(ring.Segment, 0, track.Segments.Count - 1)];
                    var edge = RoadMeshBuilder.EdgeContact(ring, segment, side);
                    var outward = new Vec3d(ring.Right.X * side, 0, ring.Right.Z * side);
                    if (outward.Length < 1e-6) continue;
                    outward = outward.Normalized();
                    var width = Math.Min(FilletCells * grid.Cell, InnerRoom(rings, i, side));
                    var x = edge.X + outward.X * width;
                    var z = edge.Z + outward.Z * width;
                    // A little below the grid's surface: the simplified land strays from it by up to the tolerance.
                    var y = SurfaceHeight(grid, x, z) - Tolerance;
                    if (double.IsNaN(y) || edge.Y - y > FilletMaxDrop || y - edge.Y > FilletMaxDrop) continue;
                    strip[i] = (edge, new Vec3d(x, y, z));
                }
                for (var start = 0; start < rings.Count - 1; start += FilletRingsPerChunk)
                {
                    var end = Math.Min(start + FilletRingsPerChunk, rings.Count - 1);
                    var chunk = FilletChunk(strip, start, end, material, $"{namePrefix}_T{t + 1}{(side < 0 ? "L" : "R")}_{start / FilletRingsPerChunk:D3}");
                    if (chunk is not null) chunks.Add(chunk);
                }
            }
        }
        return chunks;
    }

    /// <summary>
    /// How far a strip may reach out at ring i before it folds over itself on the inside of a bend: 0.7 times the bend's
    /// radius there (unlimited on the outside and on straights).
    /// </summary>
    private static double InnerRoom(IReadOnlyList<Ring> rings, int i, int side)
    {
        var a = rings[Math.Max(0, i - 1)];
        var b = rings[Math.Min(rings.Count - 1, i + 1)];
        var ta = new Vec3d(a.Tangent.X, 0, a.Tangent.Z);
        var tb = new Vec3d(b.Tangent.X, 0, b.Tangent.Z);
        if (ta.Length < 1e-6 || tb.Length < 1e-6) return double.PositiveInfinity;
        ta = ta.Normalized();
        tb = tb.Normalized();
        var turn = Math.Acos(Math.Clamp(ta.X * tb.X + ta.Z * tb.Z, -1, 1));
        var length = b.Distance - a.Distance;
        if (turn < 1e-4 || length <= 0) return double.PositiveInfinity;
        // Which way it turns, seen against the side's outward direction.
        var right = rings[i].Right;
        var towards = (tb.X - ta.X) * right.X * side + (tb.Z - ta.Z) * right.Z * side;
        return towards > 0 ? 0.7 * length / turn : double.PositiveInfinity;
    }

    private static MeshChunk? FilletChunk((Vec3d Edge, Vec3d Out)?[] strip, int start, int end, TmMaterial material, string name)
    {
        var quads = new List<int>();
        for (var i = start; i < end; i++)
            if (strip[i] is not null && strip[i + 1] is not null) quads.Add(i);
        if (quads.Count == 0) return null;
        var first = strip[quads[0]]!.Value.Edge;
        var offset = new Vec3d(Math.Round(first.X), Math.Round(first.Y), Math.Round(first.Z));
        var chunk = new MeshChunk { Name = name, ItemOffset = offset };
        var m = chunk.MaterialIndex(material);
        // A point of the strip: row j of the profile at ring i.
        Vec3d At(int i, int j)
        {
            var (edge, o) = strip[i]!.Value;
            var (along, up) = FilletProfile[j];
            return new Vec3d(edge.X + (o.X - edge.X) * along, edge.Y + (o.Y - edge.Y) * up, edge.Z + (o.Z - edge.Z) * along);
        }
        var index = new Dictionary<(int, int), int>();
        int Vertex(int i, int j)
        {
            if (index.TryGetValue((i, j), out var v)) return v;
            var p = At(i, j);
            // Normal: across the strip (between the neighbouring rows) crossed with along the road.
            var prev = strip[Math.Max(start, i - 1)] is null ? i : Math.Max(start, i - 1);
            var next = strip[Math.Min(end, i + 1)] is null ? i : Math.Min(end, i + 1);
            var along = At(next, j) - At(prev, j);
            var across = At(i, Math.Min(FilletProfile.Length - 1, j + 1)) - At(i, Math.Max(0, j - 1));
            var n = Vec3d.Cross(along, across);
            if (n.Y < 0) n = n * -1;
            n = n.Length < 1e-9 ? Vec3d.UnitY : n.Normalized();
            chunk.Positions.Add(new Vector3((float)(p.X - offset.X), (float)(p.Y - offset.Y), (float)(p.Z - offset.Z)));
            chunk.Normals.Add(new Vector3((float)n.X, (float)n.Y, (float)n.Z));
            chunk.Uv0.Add(new Vector2((float)(p.X / 32), (float)(p.Z / 32)));
            chunk.Uv1.Add(new Vector2(0.02f + 0.96f * (i - start) / Math.Max(1, end - start), 0.02f + 0.96f * j / (FilletProfile.Length - 1)));
            index[(i, j)] = chunk.Positions.Count - 1;
            return chunk.Positions.Count - 1;
        }
        foreach (var i in quads)
            for (var j = 0; j + 1 < FilletProfile.Length; j++)
            {
                int a = Vertex(i, j), b = Vertex(i + 1, j), c = Vertex(i + 1, j + 1), d = Vertex(i, j + 1);
                MeshChunk.AddFacing(chunk, new Triangle(a, b, c, m), Vector3.UnitY);
                MeshChunk.AddFacing(chunk, new Triangle(a, c, d, m), Vector3.UnitY);
            }
        return chunk;
    }

    /// <summary>
    /// The terrain's height at (x, z) on its triangles as built (the same diagonal per square), NaN where there is no land.
    /// </summary>
    public static double SurfaceHeight(TerrainGrid grid, double x, double z)
    {
        var fx = (x - grid.X0) / grid.Cell;
        var fz = (z - grid.Z0) / grid.Cell;
        var c = (int)Math.Floor(fx);
        var r = (int)Math.Floor(fz);
        if (!grid.IsLand(c, r)) return double.NaN;
        double u = fx - c, v = fz - r;
        double h00 = grid.Height(c, r), h10 = grid.Height(c + 1, r), h01 = grid.Height(c, r + 1), h11 = grid.Height(c + 1, r + 1);
        if (Math.Abs(h00 - h11) <= Math.Abs(h10 - h01))
            // Diagonal (0,0)-(1,1): triangles (00, 10, 11) below it (u >= v) and (00, 11, 01) above.
            return u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v : h00 + (h11 - h01) * u + (h01 - h00) * v;
        // Diagonal (1,0)-(0,1): triangles (00, 10, 01) where u + v <= 1 and (10, 11, 01).
        return u + v <= 1 ? h00 + (h10 - h00) * u + (h01 - h00) * v : h11 + (h01 - h11) * (1 - u) + (h10 - h11) * (1 - v);
    }

    /// <summary>The surface normal at a grid vertex from its neighbours' heights (one-sided at the land's edge).</summary>
    private static Vector3 Normal(TerrainGrid grid, int c, int r)
    {
        var h = grid.Height(c, r);
        double At(int dc, int dr) { var v = grid.Height(c + dc, r + dr); return double.IsNaN(v) ? h : v; }
        var dx = (At(1, 0) - At(-1, 0)) / (2 * grid.Cell);
        var dz = (At(0, 1) - At(0, -1)) / (2 * grid.Cell);
        return Vector3.Normalize(new Vector3((float)-dx, 1, (float)-dz));
    }
}
