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

    /// <summary>
    /// The builder: StitchedTerrain (one land surface, rooms cut out, lining under covered road), or with the environment
    /// variable TMSPLINE_TERRAIN=old the earlier AdaptiveTerrainBuilder.
    /// </summary>
    /// <summary>VoxelTerrain (the land as a solid the roads cut their rooms out of): this branch's builder; TMSPLINE_TERRAIN=stitched or old for the earlier ones.</summary>
    public static bool Voxel { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_TERRAIN") is null or "voxel";

    public static bool Stitched { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_TERRAIN") != "old";

    /// <summary>
    /// What the map may weigh in all (the game's limit is 5 MB of custom items), and the fewest triangles the land gets even
    /// when the road leaves less room.
    /// </summary>
    private const double MapBudgetBytes = 4_750_000;
    private const int MinTriangles = 30_000;

    /// <summary>
    /// The land's triangles that fit beside the road at Normal quality (by the size estimates); 0 (no limit) when budgets
    /// are switched off with TMSPLINE_TERRAIN_BUDGET=off.
    /// </summary>
    public static int TriangleBudget(TerrainGrid grid, TrackMap? map)
    {
        if (Environment.GetEnvironmentVariable("TMSPLINE_TERRAIN_BUDGET") == "off") return 0;
        var road = map is null ? 0 : MeshQualities.EstimateMapBytes(MeshQualities.BuildExport(map, MeshQualities.Default, 64).Meshes);
        // Items: the chunks the land will cover.
        var chunks = new HashSet<(int, int)>();
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
                if (!double.IsNaN(grid.Height(c, r)))
                    chunks.Add(((int)Math.Floor(c * grid.Cell / ChunkSize), (int)Math.Floor(r * grid.Cell / ChunkSize)));
        var left = MapBudgetBytes - road - chunks.Count * 1_480.0;
        return Math.Max(MinTriangles, (int)(left / 24.3));
    }

    /// <summary>The last stitched build's details (for the checker).</summary>
    public static StitchedTerrainResult? LastStitched { get; private set; }

    public static List<MeshChunk> Build(TerrainGrid grid, TrackMap? map = null, string namePrefix = "Terrain", VoxelTerrain.Roads? roads = null)
    {
        if (grid.Columns < 2 || grid.Rows < 2) return [];
        AdaptiveMesh mesh;
        StitchedTerrainResult? stitched = null;
        if (Voxel)
        {
            LastStitched = null;
            mesh = VoxelTerrain.Build(grid, map ?? new TrackMap(), roads);
        }
        else if (Stitched)
        {
            stitched = StitchedTerrain.Build(grid, map ?? new TrackMap(), TriangleBudget(grid, map));
            LastStitched = stitched;
            mesh = stitched.Land;
        }
        else
        {
            var (field, pins) = AdaptiveTerrainBuilder.Fields(grid, map ?? new TrackMap());
            mesh = AdaptiveTerrainMesher.Build(field, pins);
        }
        var xs = mesh.Positions.Select(p => (double)p.X).ToList();
        var ys = mesh.Positions.Select(p => (double)p.Y).ToList();
        var zs = mesh.Positions.Select(p => (double)p.Z).ToList();
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) triangles.Add((mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]));
        return triangles.Count == 0 ? [] : Assemble(grid, xs, ys, zs, mesh.Pinned, triangles, namePrefix, stitched, Voxel);
    }

    /// <summary>
    /// The land's triangles (points xs/ys/zs, `onEdge`: on a road's edge, no skirt between two such) as items: normals,
    /// grass and rock (cut along the rock angle), lightmap UVs, skirts under the outer edge, square chunks.
    /// </summary>
    internal static List<MeshChunk> Assemble(TerrainGrid grid, List<double> xs, List<double> ys, List<double> zs, List<bool> onEdge,
        List<(int A, int B, int C)> triangles, string namePrefix, StitchedTerrainResult? stitched = null, bool solid = false, HashSet<(int, int)>? only = null)
    {
        // The lining's floors (the room over them, no steeper than the rock angle) are land like that beside them: their
        // points on the land's edge are the land's own (one point, one normal: no crease along the join), the others new.
        var floorOf = new Dictionary<int, int>();
        var floors = new List<(int A, int B, int C)>();
        var floorAt = new HashSet<int>();
        if (stitched is { } sf && sf.LiningIndices.Count > 0)
        {
            static (long, long, long) Key(double x, double y, double z) => ((long)Math.Round(x * 500), (long)Math.Round(y * 500), (long)Math.Round(z * 500));
            var landAt = new Dictionary<(long, long, long), int>();
            for (var i = 0; i < xs.Count; i++) landAt[Key(xs[i], ys[i], zs[i])] = i;
            var floorBelow = grid.RockAngle < 90 ? (float)Math.Cos(grid.RockAngle * Math.PI / 180) : 0.5f;
            for (var t = 0; t + 2 < sf.LiningIndices.Count; t += 3)
            {
                Vector3 pa = sf.LiningVertices[sf.LiningIndices[t]].Position, pb = sf.LiningVertices[sf.LiningIndices[t + 1]].Position, pc = sf.LiningVertices[sf.LiningIndices[t + 2]].Position;
                var facing = Vector3.Cross(pb - pa, pc - pa);
                if (facing.LengthSquared() < 1e-12f || Vector3.Normalize(facing).Y < floorBelow) continue;
                int Fv(int i)
                {
                    if (floorOf.TryGetValue(i, out var id)) return id;
                    var p = sf.LiningVertices[i].Position;
                    if (!landAt.TryGetValue(Key(p.X, p.Y, p.Z), out id))
                    {
                        xs.Add(p.X); ys.Add(p.Y); zs.Add(p.Z);
                        onEdge.Add(false);
                        id = xs.Count - 1;
                    }
                    return floorOf[i] = id;
                }
                floors.Add((Fv(sf.LiningIndices[t]), Fv(sf.LiningIndices[t + 1]), Fv(sf.LiningIndices[t + 2])));
                floorAt.Add(t);
            }
        }
        // Normals: the triangles' round each point, by area.
        var normals = new Vector3[xs.Count];
        foreach (var (a, b, c) in triangles.Concat(floors))
        {
            var n = Vector3.Cross(new Vector3((float)(xs[b] - xs[a]), (float)(ys[b] - ys[a]), (float)(zs[b] - zs[a])),
                new Vector3((float)(xs[c] - xs[a]), (float)(ys[c] - ys[a]), (float)(zs[c] - zs[a])));
            // (A height map's triangles all face up; a solid's keep their own side: ceilings and overhangs face down.)
            if (!solid && n.Y < 0) n = -n;
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        for (var i = 0; i < normals.Length; i++) normals[i] = normals[i].LengthSquared() > 0 ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        var normalOf = normals.ToList();
        // How steep the land is at each point, for rock or grass: a height map's smoothed normal; a solid's flattest
        // face there (at a rim, where land meets a cut's wall, the smoothed normal is neither's, and rock ran out
        // from it over the land in teeth).
        var flat = normalOf.Select(n => n.Y).ToList();
        if (solid)
        {
            for (var i = 0; i < flat.Count; i++) flat[i] = -1;
            foreach (var (a, b, c) in triangles)
            {
                var n = Vector3.Cross(new Vector3((float)(xs[b] - xs[a]), (float)(ys[b] - ys[a]), (float)(zs[b] - zs[a])),
                    new Vector3((float)(xs[c] - xs[a]), (float)(ys[c] - ys[a]), (float)(zs[c] - zs[a])));
                if (n.LengthSquared() < 1e-12f) continue;
                var y = Vector3.Normalize(n).Y;
                flat[a] = MathF.Max(flat[a], y);
                flat[b] = MathF.Max(flat[b], y);
                flat[c] = MathF.Max(flat[c], y);
            }
        }

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
            // The lightmap (also the rock's first layer) comes once the chunk's extent is known, below.
            chunk.Uv0.Add(stone ? default : GroundUv(xs[i], zs[i]));
            chunk.Uv1.Add(default);
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
            var t = Math.Clamp((rockBelow - flat[u]) / (flat[v] - flat[u]), 0, 1);
            xs.Add(xs[u] + (xs[v] - xs[u]) * t);
            ys.Add(ys[u] + (ys[v] - ys[u]) * t);
            zs.Add(zs[u] + (zs[v] - zs[u]) * t);
            onEdge.Add(false);
            normalOf.Add(Vector3.Normalize(Vector3.Lerp(normalOf[u], normalOf[v], (float)t)));
            flat.Add(rockBelow);
            cuts[key] = xs.Count - 1;
            return xs.Count - 1;
        }
        var skirts = new Dictionary<MeshChunk, int>();
        foreach (var (a, b, c) in triangles)
        {
            // (Building a part: the triangles round it are there for the normals only.)
            if (only is not null && !only.Contains(((int)Math.Floor(((xs[a] + xs[b] + xs[c]) / 3 - grid.X0) / ChunkSize), (int)Math.Floor(((zs[a] + zs[b] + zs[c]) / 3 - grid.Z0) / ChunkSize)))) continue;
            var chunk = ChunkAt((xs[a] + xs[b] + xs[c]) / 3, (zs[a] + zs[b] + zs[c]) / 3);
            var facing = Vector3.UnitY;
            if (solid)
            {
                facing = Vector3.Cross(new Vector3((float)(xs[b] - xs[a]), (float)(ys[b] - ys[a]), (float)(zs[b] - zs[a])), new Vector3((float)(xs[c] - xs[a]), (float)(ys[c] - ys[a]), (float)(zs[c] - zs[a])));
                if (facing.LengthSquared() < 1e-12f) continue;
            }
            void Emit(int p, int q, int r, bool stone) =>
                MeshChunk.AddFacing(chunk, new Triangle(Vertex(chunk, p, stone), Vertex(chunk, q, stone), Vertex(chunk, r, stone), stone ? chunk.MaterialIndex(rock) : 0), facing);
            bool Steep(int i) => flat[i] < rockBelow;
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
        // The lining (tunnel ceilings under land): rock, facing into the road's room; per chunk its own vertices.
        var liningOf = new Dictionary<(MeshChunk, int), int>();
        if (stitched is { } st && st.LiningIndices.Count > 0)
            for (var t = 0; t + 2 < st.LiningIndices.Count; t += 3)
            {
                int a = st.LiningIndices[t], b = st.LiningIndices[t + 1], c = st.LiningIndices[t + 2];
                LiningVertex A = st.LiningVertices[a], B = st.LiningVertices[b], C = st.LiningVertices[c];
                var mid = (A.Position + B.Position + C.Position) / 3;
                var chunk = ChunkAt(mid.X, mid.Z);
                int Lv(int i)
                {
                    if (liningOf.TryGetValue((chunk, i), out var v)) return v;
                    var lv = st.LiningVertices[i];
                    var o = chunk.ItemOffset;
                    chunk.Positions.Add(new Vector3((float)(lv.Position.X - o.X), (float)(lv.Position.Y - o.Y), (float)(lv.Position.Z - o.Z)));
                    chunk.Normals.Add(lv.Normal);
                    chunk.Uv0.Add(default);
                    chunk.Uv1.Add(default);
                    liningOf[(chunk, i)] = chunk.Positions.Count - 1;
                    return chunk.Positions.Count - 1;
                }
                var facing = Vector3.Cross(B.Position - A.Position, C.Position - A.Position);
                // A floor is land like that beside it: grass, on the land's maps and points (see above).
                if (floorAt.Contains(t))
                {
                    MeshChunk.AddFacing(chunk, new Triangle(Vertex(chunk, floorOf[a], false), Vertex(chunk, floorOf[b], false), Vertex(chunk, floorOf[c], false), 0), facing);
                    continue;
                }
                MeshChunk.AddFacing(chunk, new Triangle(Lv(a), Lv(b), Lv(c), chunk.MaterialIndex(rock)), facing);
            }
        // Its lightmap: the strip beside the land's, one band per track in the chunk, along by its chart.
        foreach (var group in liningOf.GroupBy(kv => kv.Key.Item1))
        {
            var chunk = group.Key;
            var byRoad = group.GroupBy(kv => stitched!.LiningVertices[kv.Key.Item2].Road).ToList();
            for (var k = 0; k < byRoad.Count; k++)
            {
                var charts = byRoad[k].Select(kv => (kv.Value, stitched!.LiningVertices[kv.Key.Item2].Chart)).ToList();
                float xa = charts.Min(c => c.Chart.X), xb = charts.Max(c => c.Chart.X), ya = charts.Min(c => c.Chart.Y), yb = charts.Max(c => c.Chart.Y);
                float v0 = 0.02f + 0.96f * k / byRoad.Count, v1 = 0.02f + 0.96f * (k + 1) / byRoad.Count;
                foreach (var (v, chart) in charts)
                {
                    var light = new Vector2(0.8f + 0.18f * (chart.Y - ya) / Math.Max(1e-3f, yb - ya), v0 + (v1 - v0) * (chart.X - xa) / Math.Max(1e-3f, xb - xa));
                    chunk.Uv1[v] = light;
                    chunk.Uv0[v] = light;
                }
            }
        }
        // Lightmap: the land over most of the map of its chunk, the skirts in the strip beside it. By the chunk's real extent,
        // which is more than its square (a triangle belongs to the chunk of its middle): clamped to the square, pieces beyond
        // it fell flat, and a chunk whose rock was only such pieces was refused by the importer.
        foreach (var group in vertexOf.GroupBy(kv => kv.Key.Item1))
        {
            var chunk = group.Key;
            float x0 = group.Min(kv => chunk.Positions[kv.Value].X), z0 = group.Min(kv => chunk.Positions[kv.Value].Z);
            var width = Math.Max(group.Max(kv => chunk.Positions[kv.Value].X) - x0, 1e-3f);
            var depth = Math.Max(group.Max(kv => chunk.Positions[kv.Value].Z) - z0, 1e-3f);
            foreach (var ((_, _, stone), v) in group)
            {
                var light = new Vector2(0.02f + 0.76f * (chunk.Positions[v].X - x0) / width, 0.02f + 0.96f * (chunk.Positions[v].Z - z0) / depth);
                chunk.Uv1[v] = light;
                if (stone) chunk.Uv0[v] = light;
            }
            foreach (var surface in chunk.Triangles.GroupBy(t => t.Material))
                EnsureLightmapArea(chunk, surface.Key, surface.ToList());
        }
        var withLand = vertexOf.Keys.Select(k => k.Item1).ToHashSet();
        foreach (var chunk in liningOf.Keys.Select(k => k.Item1).Distinct().Where(c => !withLand.Contains(c)))
            foreach (var surface in chunk.Triangles.GroupBy(t => t.Material))
                EnsureLightmapArea(chunk, surface.Key, surface.ToList());
        return byChunk.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1).Select(kv => kv.Value).ToList();
    }

    /// <summary>A surface with less lightmap than this (doubled, 1 = the map) counts as none: a few square centimetres of land.</summary>
    private const double MinLightmapArea = 1e-7;

    /// <summary>
    /// NadeoImporter refuses a chunk with a material that has no area in the lightmap ("LightMap TexCoord Invalid Surface").
    /// A surface that small (a sliver of rock) is invisible: its corners take turns on a small triangle of the map.
    /// </summary>
    private static void EnsureLightmapArea(MeshChunk chunk, int material, List<Triangle> triangles)
    {
        var area = 0.0;
        foreach (var t in triangles)
        {
            Vector2 p = chunk.Uv1[t.A], q = chunk.Uv1[t.B], r = chunk.Uv1[t.C];
            area += Math.Abs((double)(q.X - p.X) * (r.Y - p.Y) - (double)(q.Y - p.Y) * (r.X - p.X));
        }
        if (area >= MinLightmapArea) return;
        Vector2[] corners = [new(0.1f, 0.1f), new(0.2f, 0.1f), new(0.1f, 0.2f)];
        var seen = new HashSet<int>();
        foreach (var v in triangles.SelectMany(t => new[] { t.A, t.B, t.C }))
        {
            if (!seen.Add(v)) continue;
            chunk.Uv1[v] = corners[(seen.Count - 1) % 3];
            if (material != 0) chunk.Uv0[v] = chunk.Uv1[v];
        }
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
    public static byte[] Preview(string mapJson) => Preview(TrackJson.ParseMap(mapJson));

    /// <summary>The chunk (its place in the land's grid of chunks) a plan point is in.</summary>
    public static (int X, int Z) ChunkOf(TerrainGrid grid, double x, double z) => ((int)Math.Floor((x - grid.X0) / ChunkSize), (int)Math.Floor((z - grid.Z0) / ChunkSize));

    /// <summary>
    /// Some chunks of the voxel terrain alone (after a dab of the brush): built with the land round them, so they come
    /// out as in the whole, edge to edge with the chunks beside them.
    /// </summary>
    public static List<MeshChunk> BuildChunks(TerrainGrid grid, TrackMap map, VoxelTerrain.Roads roads, HashSet<(int X, int Z)> keys, string namePrefix = "Terrain")
    {
        if (keys.Count == 0) return [];
        var region = ((float)(grid.X0 + keys.Min(k => k.X) * ChunkSize), (float)(grid.Z0 + keys.Min(k => k.Z) * ChunkSize),
            (float)(grid.X0 + (keys.Max(k => k.X) + 1) * ChunkSize), (float)(grid.Z0 + (keys.Max(k => k.Z) + 1) * ChunkSize));
        var mesh = VoxelTerrain.Build(grid, map, roads, region);
        var xs = mesh.Positions.Select(p => (double)p.X).ToList();
        var ys = mesh.Positions.Select(p => (double)p.Y).ToList();
        var zs = mesh.Positions.Select(p => (double)p.Z).ToList();
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) triangles.Add((mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]));
        return triangles.Count == 0 ? [] : Assemble(grid, xs, ys, zs, mesh.Pinned, triangles, namePrefix, null, true, keys.Select(k => (k.X, k.Z)).ToHashSet());
    }

    /// <summary>
    /// Chunks packed for the editor, each on its own: "CHNK", their number, then per chunk its place (x, z), vertex and
    /// triangle counts, rock triangles (the last ones), positions (world), normals, uvs, indices. `keys`: chunks to
    /// name even when they came out empty (the editor drops what it has there).
    /// </summary>
    public static byte[] PackChunks(TerrainGrid grid, List<MeshChunk> chunks, IEnumerable<(int X, int Z)>? keys = null)
    {
        var byKey = chunks.ToDictionary(c => ChunkOf(grid, c.ItemOffset.X + ChunkSize / 2, c.ItemOffset.Z + ChunkSize / 2));
        var all = (keys ?? []).Concat(byKey.Keys).Distinct().OrderBy(k => k.Z).ThenBy(k => k.X).ToList();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(0x4B4E4843);
        w.Write(all.Count);
        foreach (var key in all)
        {
            w.Write(key.X);
            w.Write(key.Z);
            if (!byKey.TryGetValue(key, out var c)) { w.Write(0); w.Write(0); w.Write(0); continue; }
            var o = new Vector3((float)c.ItemOffset.X, (float)c.ItemOffset.Y, (float)c.ItemOffset.Z);
            var land = c.Triangles.Where(t => c.Materials[t.Material].GamePath is null).ToList();
            var rock = c.Triangles.Where(t => c.Materials[t.Material].GamePath is not null).ToList();
            w.Write(c.Positions.Count);
            w.Write(land.Count + rock.Count);
            w.Write(rock.Count);
            foreach (var p in c.Positions) { w.Write(p.X + o.X); w.Write(p.Y + o.Y); w.Write(p.Z + o.Z); }
            foreach (var n in c.Normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
            foreach (var u in c.Uv0) { w.Write(u.X); w.Write(u.Y); }
            foreach (var t in land.Concat(rock)) { w.Write(t.A); w.Write(t.B); w.Write(t.C); }
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>See <see cref="Preview(string)"/>.</summary>
    public static byte[] Preview(TrackMap map)
    {
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
