using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain (TerrainGrid, the editor's height map) as items: the roads and the land go into a fine height map with
/// pins along the edges the land meets, a cut and a fidelity map (AdaptiveTerrainBuilder); the mesh comes from those
/// alone (AdaptiveTerrainMesher). Then normals, grass and rock, lightmap UVs, skirts under the outer edge, square chunks.
/// </summary>
public static class TerrainMeshBuilder
{
    /// <summary>Size of a chunk (one item), metres.</summary>
    private const double ChunkSize = 128;

    public static List<MeshChunk> Build(TerrainGrid grid, TrackMap? map = null, string namePrefix = "Terrain")
    {
        if (grid.Columns < 2 || grid.Rows < 2) return [];
        var (field, pins) = AdaptiveTerrainBuilder.Fields(grid, map ?? new TrackMap());
        var mesh = AdaptiveTerrainMesher.Build(field, pins);
        var xs = mesh.Positions.Select(p => (double)p.X).ToList();
        var ys = mesh.Positions.Select(p => (double)p.Y).ToList();
        var zs = mesh.Positions.Select(p => (double)p.Z).ToList();
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) triangles.Add((mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]));
        return triangles.Count == 0 ? [] : Assemble(grid, xs, ys, zs, mesh.Pinned, triangles, namePrefix);
    }

    /// <summary>
    /// The land's triangles (points xs/ys/zs, `onEdge`: on a road's edge, no skirt between two such) as items: normals,
    /// grass and rock (cut along the rock angle), lightmap UVs, skirts under the outer edge, square chunks.
    /// </summary>
    internal static List<MeshChunk> Assemble(TerrainGrid grid, List<double> xs, List<double> ys, List<double> zs, List<bool> onEdge,
        List<(int A, int B, int C)> triangles, string namePrefix)
    {
        // Normals: the triangles' round each point, by area.
        var normals = new Vector3[xs.Count];
        foreach (var (a, b, c) in triangles)
        {
            var n = Vector3.Cross(new Vector3((float)(xs[b] - xs[a]), (float)(ys[b] - ys[a]), (float)(zs[b] - zs[a])),
                new Vector3((float)(xs[c] - xs[a]), (float)(ys[c] - ys[a]), (float)(zs[c] - zs[a])));
            if (n.Y < 0) n = -n;
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        for (var i = 0; i < normals.Length; i++) normals[i] = normals[i].LengthSquared() > 0 ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        var normalOf = normals.ToList();

        // Outer edges (one triangle only) get a skirt, except along the roads (there the land ends at their edge).
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in triangles)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = u < v ? (u, v) : (v, u);
                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
            }

        var material = Surfaces.Terrain(grid.Surface);
        // Rock: faces steeper than the rock angle, and the skirts (cliff faces under the land's edge).
        var rocky = grid.RockAngle < 90;
        var rock = Surfaces.TerrainRock(grid.Surface);
        var rockBelow = (float)Math.Cos(grid.RockAngle * Math.PI / 180);
        var byChunk = new Dictionary<(int, int), MeshChunk>();
        var vertexOf = new Dictionary<(MeshChunk, int, bool), int>();
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
        // Grass and rock corners are separate vertices: the two materials read their first UV layer differently.
        int Vertex(MeshChunk chunk, int i, bool stone)
        {
            if (vertexOf.TryGetValue((chunk, i, stone), out var v)) return v;
            var o = chunk.ItemOffset;
            chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(ys[i] - o.Y), (float)(zs[i] - o.Z)));
            chunk.Normals.Add(normalOf[i]);
            // Lightmap: the land over most of the chunk's square, the skirts in a strip beside it.
            var light = new Vector2((float)(0.02 + 0.76 * Math.Clamp((xs[i] - o.X) / ChunkSize, 0, 1)), (float)(0.02 + 0.96 * Math.Clamp((zs[i] - o.Z) / ChunkSize, 0, 1)));
            chunk.Uv0.Add(stone ? light : GroundUv(xs[i], zs[i]));
            chunk.Uv1.Add(light);
            vertexOf[(chunk, i, stone)] = chunk.Positions.Count - 1;
            return chunk.Positions.Count - 1;
        }
        // Rock where the land is steeper than the rock angle, measured on the (smoothed) vertex normals and interpolated
        // across each triangle: a triangle the threshold runs through is cut along it, so the border between grass and
        // rock is a smooth line, not the triangles' zigzag. Neighbours share the cut points on their common edge.
        var cuts = new Dictionary<(int, int), int>();
        int Cut(int u, int v)
        {
            var key = u < v ? (u, v) : (v, u);
            if (cuts.TryGetValue(key, out var i)) return i;
            var t = Math.Clamp((rockBelow - normalOf[u].Y) / (normalOf[v].Y - normalOf[u].Y), 0, 1);
            xs.Add(xs[u] + (xs[v] - xs[u]) * t);
            ys.Add(ys[u] + (ys[v] - ys[u]) * t);
            zs.Add(zs[u] + (zs[v] - zs[u]) * t);
            onEdge.Add(false);
            normalOf.Add(Vector3.Normalize(Vector3.Lerp(normalOf[u], normalOf[v], (float)t)));
            cuts[key] = xs.Count - 1;
            return xs.Count - 1;
        }
        var skirts = new Dictionary<MeshChunk, int>();
        foreach (var (a, b, c) in triangles)
        {
            var chunk = ChunkAt((xs[a] + xs[b] + xs[c]) / 3, (zs[a] + zs[b] + zs[c]) / 3);
            void Emit(int p, int q, int r, bool stone) =>
                MeshChunk.AddFacing(chunk, new Triangle(Vertex(chunk, p, stone), Vertex(chunk, q, stone), Vertex(chunk, r, stone), stone ? chunk.MaterialIndex(rock) : 0), Vector3.UnitY);
            bool Steep(int i) => normalOf[i].Y < rockBelow;
            var steep = new[] { a, b, c }.Count(Steep);
            if (!rocky || steep == 0 || steep == 3) Emit(a, b, c, rocky && steep == 3);
            else
            {
                // The corner on its own side of the line, and the other two in order round the triangle.
                var (lone, p, q) = Steep(a) == (steep == 1) ? (a, b, c) : Steep(b) == (steep == 1) ? (b, c, a) : (c, a, b);
                var loneRock = steep == 1;
                int cp = Cut(lone, p), cq = Cut(lone, q);
                Emit(lone, cp, cq, loneRock);
                Emit(cp, p, q, !loneRock);
                Emit(cp, q, cq, !loneRock);
            }
            if (grid.Skirt <= 0) continue;
            foreach (var (u, v, w) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                if (edgeUse[u < v ? (u, v) : (v, u)] != 1 || (onEdge[u] && onEdge[v])) continue;
                // Outward: across the edge, away from the triangle's third corner.
                var outward = new Vector3((float)(zs[v] - zs[u]), 0, (float)(xs[u] - xs[v]));
                if (outward.X * (xs[w] - xs[u]) + outward.Z * (zs[w] - zs[u]) > 0) outward = -outward;
                if (outward.LengthSquared() < 1e-12) continue;
                outward = Vector3.Normalize(outward);
                // Only the land's outer edge (no height map beyond it): where the land was left off a road it has
                // land beyond, under the road, and a skirt there would be a wall beside the road.
                if (!double.IsNaN(Sample(grid, (xs[u] + xs[v]) / 2 + outward.X * 2, (zs[u] + zs[v]) / 2 + outward.Z * 2))) continue;
                var k = skirts.GetValueOrDefault(chunk);
                skirts[chunk] = k + 1;
                var first = chunk.Positions.Count;
                var o = chunk.ItemOffset;
                foreach (var (i, down) in new[] { (u, 0.0), (v, 0.0), (u, grid.Skirt), (v, grid.Skirt) })
                {
                    chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(ys[i] - down - o.Y), (float)(zs[i] - o.Z)));
                    chunk.Normals.Add(outward);
                    var light = new Vector2(down == 0 ? 0.8f : 0.98f, 0.02f + 0.96f * ((k % 256) + (i == u ? 0.05f : 0.95f)) / 256);
                    // Rock: its lightmap; else the hill's side (dirt), as on the hill pieces' flanks, u along the edge.
                    chunk.Uv0.Add(rocky ? light : new Vector2((float)((i == u ? 0 : Math.Sqrt((xs[v] - xs[u]) * (xs[v] - xs[u]) + (zs[v] - zs[u]) * (zs[v] - zs[u]))) / 32 + GroundUv(xs[u], zs[u]).X), SideV));
                    chunk.Uv1.Add(light);
                }
                var sm = rocky ? chunk.MaterialIndex(rock) : 0;
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 3, sm), outward);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 3, first + 2, sm), outward);
            }
        }
        return byChunk.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1).Select(kv => kv.Value).ToList();
    }

    /// <summary>The height map's height at (x, z), bilinear between its vertices; NaN where a vertex round it has none.</summary>
    private static double Sample(TerrainGrid grid, double x, double z)
    {
        var fx = (x - grid.X0) / grid.Cell;
        var fz = (z - grid.Z0) / grid.Cell;
        var c = (int)Math.Floor(fx);
        var r = (int)Math.Floor(fz);
        double u = fx - c, v = fz - r;
        double h00 = grid.Height(c, r), h10 = grid.Height(c + 1, r), h01 = grid.Height(c, r + 1), h11 = grid.Height(c + 1, r + 1);
        return h00 * (1 - u) * (1 - v) + h10 * u * (1 - v) + h01 * (1 - u) * v + h11 * u * v;
    }

    /// <summary>
    /// The hill material's first UV layer maps its own texture (DecoHill_D, besides the projected ones): grass, and at one
    /// end of v the wooden strip of the hill pieces' vertical sides (theirs at v 0.2 to 0.26). On the hill pieces
    /// (DecoHillSlope2Straight) u runs along the ground, a 32 m block from 0 to 1. The land maps the grass by position at
    /// that scale in both directions (a direction without change stretches the texture's pixels into lines): u along a
    /// slant, v across it, back and forth between 0.3 and 0.7 (mirrored, so it never reaches the wood).
    /// </summary>
    /// <summary>The hill pieces' side strip (v 0.2 to 0.26: the wood under the grass's edge), for the skirts.</summary>
    private const float SideV = 0.23f;

    private static Vector2 GroundUv(double x, double z)
    {
        var u = (x * 0.8 + z * 0.6) / 32;
        var across = (-x * 0.6 + z * 0.8) / 32 / 0.8; // in periods of the mirror (0.4 there and 0.4 back)
        var f = across - Math.Floor(across);
        var v = 0.3 + 0.8 * (f < 0.5 ? f : 1 - f);
        return new Vector2((float)u, (float)v);
    }

    /// <summary>
    /// The map's terrain (its chunks) as the export builds it, in world space, packed for the editor:
    /// <code>
    /// int32 vertexCount, int32 triangleCount, int32 rockTriangles, float32[vertexCount*3] positions,
    /// float32[vertexCount*3] normals, float32[vertexCount*2] uv0, int32[triangleCount*3] indices (the rock's last)
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
        var rockIndices = new List<int>();
        foreach (var c in chunks)
        {
            var o = new Vector3((float)c.ItemOffset.X, (float)c.ItemOffset.Y, (float)c.ItemOffset.Z);
            var first = positions.Count;
            positions.AddRange(c.Positions.Select(p => p + o));
            normals.AddRange(c.Normals);
            uvs.AddRange(c.Uv0);
            foreach (var t in c.Triangles)
                (c.Materials[t.Material].GamePath is null ? indices : rockIndices).AddRange([first + t.A, first + t.B, first + t.C]);
        }
        var rockTriangles = rockIndices.Count / 3;
        indices.AddRange(rockIndices);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(positions.Count);
        w.Write(indices.Count / 3);
        w.Write(rockTriangles);
        foreach (var p in positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var n in normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
        foreach (var u in uvs) { w.Write(u.X); w.Write(u.Y); }
        foreach (var i in indices) w.Write(i);
        w.Flush();
        return stream.ToArray();
    }
}
