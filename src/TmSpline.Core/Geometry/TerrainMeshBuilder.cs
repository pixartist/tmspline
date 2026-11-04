using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain grid (TerrainGrid) as items: square chunks of the grid, each grid square two triangles (split along the
/// diagonal whose ends are closer in height, so ridges and valleys keep their line), smooth normals from the whole grid,
/// and a skirt hanging from the land's outer edge.
/// </summary>
public static class TerrainMeshBuilder
{
    /// <summary>Grid squares per chunk side (16 m squares: 128 m chunks).</summary>
    public const int ChunkSquares = 8;

    public static List<MeshChunk> Build(TerrainGrid grid, string namePrefix = "Terrain")
    {
        var chunks = new List<MeshChunk>();
        if (grid.Columns < 2 || grid.Rows < 2) return chunks;
        var material = Surfaces.Terrain(grid.Surface);
        var squaresX = grid.Columns - 1;
        var squaresZ = grid.Rows - 1;
        for (var cj = 0; cj * ChunkSquares < squaresZ; cj++)
            for (var ci = 0; ci * ChunkSquares < squaresX; ci++)
            {
                var chunk = BuildChunk(grid, ci * ChunkSquares, cj * ChunkSquares, material, $"{namePrefix}_{cj:D2}_{ci:D2}");
                if (chunk is not null) chunks.Add(chunk);
            }
        return chunks;
    }

    private static MeshChunk? BuildChunk(TerrainGrid grid, int c0, int r0, TmMaterial material, string name)
    {
        var c1 = Math.Min(c0 + ChunkSquares, grid.Columns - 1);
        var r1 = Math.Min(r0 + ChunkSquares, grid.Rows - 1);
        var land = new List<(int C, int R)>();
        for (var r = r0; r < r1; r++)
            for (var c = c0; c < c1; c++)
                if (grid.IsLand(c, r)) land.Add((c, r));
        if (land.Count == 0) return null;

        var cell = grid.Cell;
        var offset = new Vec3d(grid.X0 + c0 * cell, land.Min(s => Math.Min(grid.Height(s.C, s.R), grid.Height(s.C + 1, s.R + 1))), grid.Z0 + r0 * cell);
        var chunk = new MeshChunk { Name = name, ItemOffset = offset };
        var m = chunk.MaterialIndex(material);
        Vector3 Local(double x, double y, double z) => new((float)(x - offset.X), (float)(y - offset.Y), (float)(z - offset.Z));
        var span = Math.Max(c1 - c0, r1 - r0);

        // The grid's vertices of this chunk (shared by its squares).
        var index = new Dictionary<(int, int), int>();
        int Vertex(int c, int r)
        {
            if (index.TryGetValue((c, r), out var i)) return i;
            var x = grid.X0 + c * cell;
            var z = grid.Z0 + r * cell;
            chunk.Positions.Add(Local(x, grid.Height(c, r), z));
            chunk.Normals.Add(Normal(grid, c, r));
            chunk.Uv0.Add(new Vector2((float)(x / 32), (float)(z / 32)));
            // Lightmap: the chunk's squares over most of the map, the skirts in a strip beside them.
            chunk.Uv1.Add(new Vector2(0.02f + 0.76f * (c - c0) / span, 0.02f + 0.96f * (r - r0) / span));
            index[(c, r)] = chunk.Positions.Count - 1;
            return chunk.Positions.Count - 1;
        }
        foreach (var (c, r) in land)
        {
            int a = Vertex(c, r), b = Vertex(c + 1, r), d = Vertex(c, r + 1), e = Vertex(c + 1, r + 1);
            var up = Vector3.UnitY;
            // Split along the diagonal whose ends are closer in height.
            if (Math.Abs(grid.Height(c, r) - grid.Height(c + 1, r + 1)) <= Math.Abs(grid.Height(c + 1, r) - grid.Height(c, r + 1)))
            {
                MeshChunk.AddFacing(chunk, new Triangle(a, b, e, m), up);
                MeshChunk.AddFacing(chunk, new Triangle(a, e, d, m), up);
            }
            else
            {
                MeshChunk.AddFacing(chunk, new Triangle(a, b, d, m), up);
                MeshChunk.AddFacing(chunk, new Triangle(b, e, d, m), up);
            }
        }

        // Skirts: every square side with no land beyond it, down by Skirt metres.
        if (grid.Skirt > 0)
        {
            var sides = new List<(int C0, int R0, int C1, int R1, Vector3 Out)>();
            foreach (var (c, r) in land)
            {
                if (!grid.IsLand(c, r - 1)) sides.Add((c, r, c + 1, r, -Vector3.UnitZ));
                if (!grid.IsLand(c, r + 1)) sides.Add((c, r + 1, c + 1, r + 1, Vector3.UnitZ));
                if (!grid.IsLand(c - 1, r)) sides.Add((c, r, c, r + 1, -Vector3.UnitX));
                if (!grid.IsLand(c + 1, r)) sides.Add((c + 1, r, c + 1, r + 1, Vector3.UnitX));
            }
            for (var s = 0; s < sides.Count; s++)
            {
                var (sc0, sr0, sc1, sr1, outward) = sides[s];
                var first = chunk.Positions.Count;
                var v0 = 0.02f + 0.96f * s / sides.Count;
                var v1 = 0.02f + 0.96f * (s + 0.9f) / sides.Count;
                foreach (var (c, r, down, v) in new[] { (sc0, sr0, 0.0, v0), (sc1, sr1, 0.0, v1), (sc0, sr0, grid.Skirt, v0), (sc1, sr1, grid.Skirt, v1) })
                {
                    var x = grid.X0 + c * cell;
                    var z = grid.Z0 + r * cell;
                    var y = grid.Height(c, r) - down;
                    chunk.Positions.Add(Local(x, y, z));
                    chunk.Normals.Add(outward);
                    chunk.Uv0.Add(new Vector2((float)((x + z) / 32), (float)(y / 32)));
                    chunk.Uv1.Add(new Vector2(down == 0 ? 0.8f : 0.98f, v));
                }
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 3, m), outward);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 3, first + 2, m), outward);
            }
        }
        return chunk;
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
    private const double FilletCells = 2.25;
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
                    var y = SurfaceHeight(grid, x, z);
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
