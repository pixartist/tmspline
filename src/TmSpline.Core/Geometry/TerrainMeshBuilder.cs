using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The terrain (TerrainGrid, the editor's height map) as items, built from the roads outwards:
/// <list type="bullet">
/// <item>Rows of points along every road edge, densest at the road: the first on the edge itself (the curb's top, or the
/// surface's edge: RoadMeshBuilder.EdgeContact), the others at the land's height a little further out.</item>
/// <item>Beyond them the height map's grid, thinned out with the distance from the roads (the land relaxes).</item>
/// <item>One Delaunay triangulation of all of them, without the triangles on the roads, so the land ends exactly at their
/// edges; a skirt hangs from its outer edge. Cut into square chunks (one item each).</item>
/// </list>
/// </summary>
public static class TerrainMeshBuilder
{
    /// <summary>Size of a chunk (one item), metres.</summary>
    private const double ChunkSize = 128;
    /// <summary>The rows along a road's edge: metres out from it (0: on the edge).</summary>
    private static readonly double[] RowOffsets = [0, 0.75, 2, 4, 7, 11, 16];
    /// <summary>Metres between the rows' points along the road (the outer rows every other one).</summary>
    private const double AlongSpacing = 2;
    /// <summary>Beyond the rows: the grid's full density for this many metres, then every second vertex, then every fourth.</summary>
    private const double DenseGrid = 24, MediumGrid = 72;
    /// <summary>No triangle longer than this on a side (the triangulation's hull fills what is no land).</summary>
    private const double MaxEdge = 48;

    /// <summary>A point of a road's edge every couple of metres: its centre, both edges (null: no land meets it there).</summary>
    private sealed record EdgeSample(Vec3d Centre, Vec3d Right, Vec3d? Left, Vec3d? RightEdge, double RoomLeft, double RoomRight);

    public static List<MeshChunk> Build(TerrainGrid grid, TrackMap? map = null, string namePrefix = "Terrain")
    {
        var chunks = new List<MeshChunk>();
        if (grid.Columns < 2 || grid.Rows < 2) return chunks;
        var samples = map is null ? [] : RoadSamples(map);
        var lastRow = RowOffsets[^1];

        // The points: the rows along the edges...
        var xs = new List<double>();
        var ys = new List<double>();
        var zs = new List<double>();
        var onEdge = new List<bool>();
        void Add(double x, double y, double z, bool edge) { xs.Add(x); ys.Add(y); zs.Add(z); onEdge.Add(edge); }
        for (var s = 0; s < samples.Count; s++)
        {
            var sample = samples[s];
            foreach (var (edge, sign, room) in new[] { (sample.Left, -1, sample.RoomLeft), (sample.RightEdge, 1, sample.RoomRight) })
            {
                if (edge is not { } e) continue;
                var outward = new Vec3d(sample.Right.X * sign, 0, sample.Right.Z * sign);
                Add(e.X, e.Y, e.Z, true);
                for (var row = 1; row < RowOffsets.Length; row++)
                {
                    var offset = RowOffsets[row];
                    if (offset > room || (offset >= 7 && s % 2 == 1)) continue;
                    double x = e.X + outward.X * offset, z = e.Z + outward.Z * offset;
                    var y = Sample(grid, x, z);
                    if (double.IsNaN(y)) continue;
                    // From the edge's own height into the height map's over the rows (the map is coarser than the
                    // road: on a steep one its height beside the edge is off by up to a metre or two).
                    var w = offset / lastRow;
                    Add(x, e.Y + (y - e.Y) * w * w * (3 - 2 * w), z, false);
                }
            }
        }
        // ...and the grid beyond them, sparser with the distance from the roads (and at full density along the land's
        // outer edge, so its shape stays).
        var fromRoad = DistanceFromRoads(grid, samples, lastRow + MediumGrid);
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
            {
                var k = r * grid.Columns + c;
                var h = grid.Heights[k];
                if (double.IsNaN(h)) continue;
                var d = fromRoad[k];
                if (d < lastRow + 1) continue;
                var step = d < lastRow + DenseGrid ? 1 : d < lastRow + MediumGrid ? 2 : 4;
                var border = double.IsNaN(grid.Height(c - 1, r)) || double.IsNaN(grid.Height(c + 1, r)) || double.IsNaN(grid.Height(c, r - 1)) || double.IsNaN(grid.Height(c, r + 1));
                if (!border && (c % step != 0 || r % step != 0)) continue;
                Add(grid.X0 + c * grid.Cell, h, grid.Z0 + r * grid.Cell, false);
            }
        if (xs.Count < 3) return chunks;

        var coords = new double[xs.Count * 2];
        for (var i = 0; i < xs.Count; i++) { coords[2 * i] = xs[i]; coords[2 * i + 1] = zs[i]; }
        var all = new Delaunay(coords).Triangles;

        // Land only: not on a road, not longer than the land's points are apart, not where the height map has none.
        var onRoad = RoadTest(samples);
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t + 2 < all.Length; t += 3)
        {
            int a = all[t], b = all[t + 1], c = all[t + 2];
            double cx = (xs[a] + xs[b] + xs[c]) / 3, cz = (zs[a] + zs[b] + zs[c]) / 3;
            if (Long(a, b) || Long(b, c) || Long(c, a) || onRoad(cx, cz) || double.IsNaN(Sample(grid, cx, cz))) continue;
            triangles.Add((a, b, c));
        }
        bool Long(int a, int b) => (xs[a] - xs[b]) * (xs[a] - xs[b]) + (zs[a] - zs[b]) * (zs[a] - zs[b]) > MaxEdge * MaxEdge;
        if (triangles.Count == 0) return chunks;

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

        // Outer edges (one triangle only) get a skirt, except along the roads (there the land ends at their edge).
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
            chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(ys[i] - o.Y), (float)(zs[i] - o.Z)));
            chunk.Normals.Add(normals[i]);
            chunk.Uv0.Add(MaskUv(normals[i].Y));
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
                if (edgeUse[u < v ? (u, v) : (v, u)] != 1 || (onEdge[u] && onEdge[v])) continue;
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
                    chunk.Positions.Add(new Vector3((float)(xs[i] - o.X), (float)(ys[i] - down - o.Y), (float)(zs[i] - o.Z)));
                    chunk.Normals.Add(outward);
                    // The hill's side (dirt), as on the hill pieces' flanks.
                    chunk.Uv0.Add(MaskUv(0));
                    chunk.Uv1.Add(new Vector2(down == 0 ? 0.8f : 0.98f, 0.02f + 0.96f * ((k % 256) + (i == u ? 0.05f : 0.95f)) / 256));
                }
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 3, 0), outward);
                MeshChunk.AddFacing(chunk, new Triangle(first, first + 3, first + 2, 0), outward);
            }
        }
        return byChunk.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// The roads' edges every <see cref="AlongSpacing"/> metres or so (between the sampled rings, edges and directions
    /// interpolated): no edge where a road is steeply banked or upside down, and how far land may reach out on each side
    /// before it folds over itself on the inside of a bend.
    /// </summary>
    private static List<EdgeSample> RoadSamples(TrackMap map)
    {
        var samples = new List<EdgeSample>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Low));
            if (rings.Count < 2) continue;
            Vec3d? Edge(int i, int side)
            {
                if (rings[i].Up.Y < 0.5) return null;
                var segment = track.Segments[Math.Clamp(rings[i].Segment, 0, track.Segments.Count - 1)];
                return RoadMeshBuilder.EdgeContact(rings[i], segment, side);
            }
            for (var i = 0; i + 1 < rings.Count; i++)
            {
                Ring a = rings[i], b = rings[i + 1];
                var steps = Math.Max(1, (int)Math.Ceiling((b.Distance - a.Distance) / AlongSpacing));
                Vec3d? la = Edge(i, -1), lb = Edge(i + 1, -1), ra = Edge(i, 1), rb = Edge(i + 1, 1);
                double roomL = Math.Min(InnerRoom(rings, i, -1), InnerRoom(rings, i + 1, -1));
                double roomR = Math.Min(InnerRoom(rings, i, 1), InnerRoom(rings, i + 1, 1));
                // The last ring's own sample comes with the next interval (or after the loop for the track's end).
                for (var j = 0; j < steps; j++)
                {
                    var t = (double)j / steps;
                    samples.Add(new EdgeSample(Lerp(a.Position, b.Position, t), Lerp(a.Right, b.Right, t).Normalized(),
                        la is { } l0 && lb is { } l1 ? Lerp(l0, l1, t) : null, ra is { } r0 && rb is { } r1 ? Lerp(r0, r1, t) : null, roomL, roomR));
                }
            }
            var last = rings.Count - 1;
            samples.Add(new EdgeSample(rings[last].Position, rings[last].Right, Edge(last, -1), Edge(last, 1),
                InnerRoom(rings, last, -1), InnerRoom(rings, last, 1)));
        }
        return samples;
    }

    private static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    /// <summary>
    /// How far land may reach out at ring i before it folds over itself on the inside of a bend: 0.7 times the bend's radius
    /// there (unlimited on the outside and on straights).
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
        var right = rings[i].Right;
        var towards = (tb.X - ta.X) * right.X * side + (tb.Z - ta.Z) * right.Z * side;
        return towards > 0 ? 0.7 * length / turn : double.PositiveInfinity;
    }

    /// <summary>
    /// Per grid vertex its distance from the nearest road's edge (from the centre, less the road's half width there),
    /// metres, up to <paramref name="reach"/> beyond the edges (further: infinity).
    /// </summary>
    private static double[] DistanceFromRoads(TerrainGrid grid, List<EdgeSample> samples, double reach)
    {
        var d = new double[grid.Columns * grid.Rows];
        Array.Fill(d, double.PositiveInfinity);
        foreach (var s in samples)
        {
            var half = Math.Max(Half(s.Centre, s.Left), Half(s.Centre, s.RightEdge));
            var radius = half + reach;
            var c0 = Math.Max(0, (int)Math.Floor((s.Centre.X - radius - grid.X0) / grid.Cell));
            var c1 = Math.Min(grid.Columns - 1, (int)Math.Ceiling((s.Centre.X + radius - grid.X0) / grid.Cell));
            var r0 = Math.Max(0, (int)Math.Floor((s.Centre.Z - radius - grid.Z0) / grid.Cell));
            var r1 = Math.Min(grid.Rows - 1, (int)Math.Ceiling((s.Centre.Z + radius - grid.Z0) / grid.Cell));
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    double dx = grid.X0 + c * grid.Cell - s.Centre.X, dz = grid.Z0 + r * grid.Cell - s.Centre.Z;
                    var v = Math.Sqrt(dx * dx + dz * dz) - half;
                    var k = r * grid.Columns + c;
                    if (v < d[k]) d[k] = v;
                }
        }
        return d;
    }

    private static double Half(Vec3d centre, Vec3d? edge) =>
        edge is { } e ? Math.Sqrt((e.X - centre.X) * (e.X - centre.X) + (e.Z - centre.Z) * (e.Z - centre.Z)) : 0;

    /// <summary>
    /// Whether a point (x, z) lies on a road: across its nearest edge sample, between the two edges (a little inside them,
    /// so the triangles along an edge stay).
    /// </summary>
    private static Func<double, double, bool> RoadTest(List<EdgeSample> samples)
    {
        const double cell = 8;
        var hash = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < samples.Count; i++)
        {
            var k = ((int)Math.Floor(samples[i].Centre.X / cell), (int)Math.Floor(samples[i].Centre.Z / cell));
            if (!hash.TryGetValue(k, out var list)) hash[k] = list = [];
            list.Add(i);
        }
        return (x, z) =>
        {
            int hx = (int)Math.Floor(x / cell), hz = (int)Math.Floor(z / cell);
            var best = -1;
            var bestD = double.PositiveInfinity;
            for (var dx = -3; dx <= 3; dx++)
                for (var dz = -3; dz <= 3; dz++)
                {
                    if (!hash.TryGetValue((hx + dx, hz + dz), out var list)) continue;
                    foreach (var i in list)
                    {
                        var c = samples[i].Centre;
                        var d = (c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                }
            if (best < 0) return false;
            var s = samples[best];
            var lateral = (x - s.Centre.X) * s.Right.X + (z - s.Centre.Z) * s.Right.Z;
            var right = s.RightEdge is { } re ? (re.X - s.Centre.X) * s.Right.X + (re.Z - s.Centre.Z) * s.Right.Z : Half(s.Centre, s.Left);
            var left = s.Left is { } le ? -((le.X - s.Centre.X) * s.Right.X + (le.Z - s.Centre.Z) * s.Right.Z) : Half(s.Centre, s.RightEdge);
            return lateral < right - 0.05 && -lateral < left - 0.05;
        };
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
}
