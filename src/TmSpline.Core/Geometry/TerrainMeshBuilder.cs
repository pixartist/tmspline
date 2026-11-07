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
    /// <summary>Width of the curbs outside the road's surface (RoadMeshSettings.BorderThickness), metres.</summary>
    private static readonly double CurbWidth = new RoadMeshSettings().BorderThickness;
    /// <summary>Where a triangle is tested for lying on a road (barycentric weights).</summary>
    private static readonly (double, double, double)[] Weights =
        [(1.0 / 3, 1.0 / 3, 1.0 / 3), (0.5, 0.5, 0), (0, 0.5, 0.5), (0.5, 0, 0.5), (0.8, 0.1, 0.1), (0.1, 0.8, 0.1), (0.1, 0.1, 0.8),
         (0.25, 0.75, 0), (0.75, 0.25, 0), (0, 0.25, 0.75), (0, 0.75, 0.25), (0.25, 0, 0.75), (0.75, 0, 0.25)];
    /// <summary>Land this far or further below a road is under it (a bridge), not on it, metres.</summary>
    private const double BelowRoad = 3;
    /// <summary>
    /// The land meets a road's edge when the height map there is at most this much above it, or at most PullReach below
    /// it (the rows then pull it up to the edge: at a loop's or wall's start the editor's map is held lower), metres.
    /// </summary>
    private const double EdgeReach = 3, PullReach = 24;
    /// <summary>A point this far inside a road's edge is on it (less: the edge's own triangles, a curve's chords), metres.</summary>
    private const double EdgeMargin = 0.25;
    /// <summary>No triangle longer than this on a side (the triangulation's hull fills what is no land).</summary>
    private const double MaxEdge = 48;

    /// <summary>
    /// A point of a road's edge every couple of metres: its centre, both edges (null: none, or no land meets it there),
    /// and whether land may meet the road here at all (upright, not too steep; the edges are still there for leaving the
    /// land off the road).
    /// </summary>
    private sealed record EdgeSample(Vec3d Centre, Vec3d Right, Vec3d? Left, Vec3d? RightEdge, double RoomLeft, double RoomRight, bool Holds = true);

    public static List<MeshChunk> Build(TerrainGrid grid, TrackMap? map = null, string namePrefix = "Terrain")
    {
        var chunks = new List<MeshChunk>();
        if (grid.Columns < 2 || grid.Rows < 2) return chunks;
        if (grid.Adaptive && map is not null) return BuildAdaptive(grid, map, namePrefix);
        var (allSamples, ends) = map is null ? ([], []) : RoadSamples(map);
        // Land meets an edge where the height map is near it or below (not where it is far below: a bridge over the
        // land, which the editor's map keeps below the road it passes over): elsewhere the edge has no rows.
        var onRoad = RoadTest(allSamples, ends);
        // Pulled up only where no other road lies under the edge or just beyond it (a bridge over a road: the land stays
        // with the road below).
        bool Bridge(EdgeSample s, Vec3d e, int sign)
        {
            foreach (var d in new[] { 0.5, 2.0, 4.0 })
            {
                double x = e.X + s.Right.X * sign * d, z = e.Z + s.Right.Z * sign * d;
                if (Sample(grid, x, z) is var h && !double.IsNaN(h) && onRoad(x, z, h)) return true;
            }
            return false;
        }
        Vec3d? Met(EdgeSample s, Vec3d? edge, int sign) => s.Holds && edge is { } e && Sample(grid, e.X, e.Z) is var h
            && (double.IsNaN(h) || (h <= e.Y + EdgeReach && h >= e.Y - (Bridge(s, e, sign) ? EdgeReach : PullReach))) ? e : null;
        var samples = allSamples.Select(s => s with { Left = Met(s, s.Left, -1), RightEdge = Met(s, s.RightEdge, 1) }).ToList();
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
            foreach (var (edge, sign, room, road) in new[] { (sample.Left, -1, sample.RoomLeft, allSamples[s].Left), (sample.RightEdge, 1, sample.RoomRight, allSamples[s].RightEdge) })
            {
                // An edge the land does not meet still gets a point at the land's height under it, so the triangulation
                // has no triangle reaching across the road.
                // (Not where that is on another road: a bridge's edge over a road.)
                if (edge is null && road is { } f && Sample(grid, f.X, f.Z) is var below && !double.IsNaN(below) && below < f.Y + 0.5
                    && !onRoad(f.X, f.Z, Math.Min(below, f.Y)))
                    Add(f.X, Math.Min(below, f.Y), f.Z, true);
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
                    // road: on a steep one its height beside the edge is off by up to a metre or two; where it is held
                    // lower, the rows pull it up to the edge).
                    var w = offset / lastRow;
                    var rowY = e.Y + (y - e.Y) * w * w * (3 - 2 * w);
                    // Not onto another road (the one under a loop, say): the rows end before it.
                    if (onRoad(x, z, rowY)) break;
                    Add(x, rowY, z, false);
                }
            }
        }
        // ...in front of the ends of open tracks: the same rows across the whole width (beyond the side rows too), from
        // the road's surface height in the middle and its edges' heights at the sides into the height map...
        foreach (var (index, forward) in ends)
        {
            var sample = samples[index];
            var right = new Vec3d(sample.Right.X, 0, sample.Right.Z).Normalized();
            var c = sample.Centre;
            double Lateral(Vec3d? e) => e is { } v ? (v.X - c.X) * right.X + (v.Z - c.Z) * right.Z : 0;
            var halfL = -Lateral(sample.Left);
            var halfR = Lateral(sample.RightEdge);
            if (halfL <= 0 && halfR <= 0) continue;
            // The road's end across: its surface's height, rising to the edges' (curb tops) across the curbs.
            double Profile(double l)
            {
                var (half, edgeY) = l < 0 ? (halfL, sample.Left?.Y ?? c.Y) : (halfR, sample.RightEdge?.Y ?? c.Y);
                var t = Math.Clamp((Math.Abs(l) - (half - CurbWidth)) / CurbWidth, 0, 1);
                return c.Y + (edgeY - c.Y) * t * t * (3 - 2 * t);
            }
            for (var row = 0; row < RowOffsets.Length; row++)
            {
                var offset = RowOffsets[row];
                // Across the end itself only between the edges; further out also in front of the side rows.
                var from = row == 0 ? -halfL : -halfL - offset;
                var to = row == 0 ? halfR : halfR + offset;
                var count = Math.Max(1, (int)Math.Ceiling((to - from) / AlongSpacing));
                for (var j = 0; j <= count; j++)
                {
                    var l = from + (to - from) * j / count;
                    double x = c.X + forward.X * offset + right.X * l, z = c.Z + forward.Z * offset + right.Z * l;
                    var baseY = Profile(Math.Clamp(l, -halfL, halfR));
                    if (row == 0) { Add(x, baseY, z, true); continue; }
                    var y = Sample(grid, x, z);
                    if (double.IsNaN(y)) continue;
                    var w = offset / lastRow;
                    Add(x, baseY + (y - baseY) * w * w * (3 - 2 * w), z, false);
                }
            }
        }
        // ...and the grid beyond them, sparser with the distance from the roads (and at full density along the land's
        // outer edge, so its shape stays).
        var fromRoad = DistanceFromRoads(grid, allSamples, lastRow + MediumGrid);
        // The rows replace the grid only next to the edges that have them (beside an edge the land does not meet, the
        // grid stays: else nothing would be there but long triangles).
        var fromRows = DistanceFromPoints(grid, samples.SelectMany(x => new[] { x.Left, x.RightEdge }).OfType<Vec3d>(), lastRow + 1);
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
            {
                var k = r * grid.Columns + c;
                var h = grid.Heights[k];
                if (double.IsNaN(h)) continue;
                var d = fromRoad[k];
                if (fromRows[k] < lastRow + 1) continue;
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
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t + 2 < all.Length; t += 3)
        {
            int a = all[t], b = all[t + 1], c = all[t + 2];
            double cx = (xs[a] + xs[b] + xs[c]) / 3, cz = (zs[a] + zs[b] + zs[c]) / 3;
            if (Long(a, b) || Long(b, c) || Long(c, a) || OnRoad(a, b, c) || double.IsNaN(Sample(grid, cx, cz))) continue;
            triangles.Add((a, b, c));
        }
        // On a road anywhere: tested at the centre, the sides' middles and near the corners (a long triangle beside a
        // road can cross it with its centre off it).
        bool OnRoad(int a, int b, int c)
        {
            foreach (var (wa, wb, wc) in Weights)
                if (onRoad(xs[a] * wa + xs[b] * wb + xs[c] * wc, zs[a] * wa + zs[b] * wb + zs[c] * wc, ys[a] * wa + ys[b] * wb + ys[c] * wc))
                    return true;
            return false;
        }
        bool Long(int a, int b) => (xs[a] - xs[b]) * (xs[a] - xs[b]) + (zs[a] - zs[b]) * (zs[a] - zs[b]) > MaxEdge * MaxEdge;
        if (triangles.Count == 0) return chunks;
        return Assemble(grid, xs, ys, zs, onEdge, triangles, namePrefix);
    }

    /// <summary>The experiment: fine height map, pins and fidelity (AdaptiveTerrainBuilder), meshed by AdaptiveTerrainMesher.</summary>
    private static List<MeshChunk> BuildAdaptive(TerrainGrid grid, TrackMap map, string namePrefix)
    {
        var (field, pins) = AdaptiveTerrainBuilder.Fields(grid, map);
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

    /// <summary>
    /// The roads' edges every <see cref="AlongSpacing"/> metres or so (between the sampled rings, edges and directions
    /// interpolated): no edge where a road is steeply banked or upside down, and how far land may reach out on each side
    /// before it folds over itself on the inside of a bend.
    /// </summary>
    private static (List<EdgeSample> Samples, List<(int Index, Vec3d Forward)> Ends) RoadSamples(TrackMap map)
    {
        var samples = new List<EdgeSample>();
        var ends = new List<(int, Vec3d)>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Low));
            if (rings.Count < 2) continue;
            var first = samples.Count;
            // No land meets walls, upside-down road, or road climbing or diving steeper than about 33 degrees (a loop's
            // sides); the editor's height map (ownTerrain.ts) uses the same limits.
            bool Holds(int i) => rings[i].Up.Y >= 0.5 && Math.Abs(rings[i].Tangent.Normalized().Y) <= 0.55;
            Vec3d? Edge(int i, int side)
            {
                // Edges where the road is not upside down (on walls too: the land is left off them).
                if (rings[i].Up.Y < -0.2) return null;
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
                // The last ring's own sample comes with the next interval (or after the loop for the track's end). In
                // between, the edges on the straight line between the rings' (as the road's mesh has them, built from the
                // same rings).
                for (var j = 0; j < steps; j++)
                {
                    var t = (double)j / steps;
                    samples.Add(new EdgeSample(Lerp(a.Position, b.Position, t), Lerp(a.Right, b.Right, t).Normalized(),
                        la is { } l0 && lb is { } l1 ? Lerp(l0, l1, t) : null, ra is { } r0 && rb is { } r1 ? Lerp(r0, r1, t) : null, roomL, roomR,
                        Holds(i) && Holds(i + 1)));
                }
            }
            var last = rings.Count - 1;
            samples.Add(new EdgeSample(rings[last].Position, rings[last].Right, Edge(last, -1), Edge(last, 1),
                InnerRoom(rings, last, -1), InnerRoom(rings, last, 1), Holds(last)));
            // An open track's ends, each with the way out of the road (along the track, level).
            if (!track.Closed)
            {
                Vec3d Level(Vec3d v) { var h = new Vec3d(v.X, 0, v.Z); return h.Length < 1e-9 ? h : h.Normalized(); }
                ends.Add((first, Level(rings[0].Tangent) * -1));
                ends.Add((samples.Count - 1, Level(rings[last].Tangent)));
            }
        }
        return (samples, ends);
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
    /// <summary>Per grid vertex its (horizontal) distance from the nearest of the points, up to `reach` (further: infinity).</summary>
    private static double[] DistanceFromPoints(TerrainGrid grid, IEnumerable<Vec3d> points, double reach)
    {
        var d = new double[grid.Columns * grid.Rows];
        Array.Fill(d, double.PositiveInfinity);
        foreach (var p in points)
        {
            var c0 = Math.Max(0, (int)Math.Floor((p.X - reach - grid.X0) / grid.Cell));
            var c1 = Math.Min(grid.Columns - 1, (int)Math.Ceiling((p.X + reach - grid.X0) / grid.Cell));
            var r0 = Math.Max(0, (int)Math.Floor((p.Z - reach - grid.Z0) / grid.Cell));
            var r1 = Math.Min(grid.Rows - 1, (int)Math.Ceiling((p.Z + reach - grid.Z0) / grid.Cell));
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    double dx = grid.X0 + c * grid.Cell - p.X, dz = grid.Z0 + r * grid.Cell - p.Z;
                    var v = Math.Sqrt(dx * dx + dz * dz);
                    var k = r * grid.Columns + c;
                    if (v < d[k]) d[k] = v;
                }
        }
        return d;
    }

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
    /// Whether land at (x, y, z) lies on a road: across a road's edge sample there, between its two edges (a little inside
    /// them, so the triangles along an edge stay), and not well below the road (land under a bridge stays). Every road
    /// nearby counts (stacked roads), the edges as the road has them, whether the land meets them or not.
    /// </summary>
    private static Func<double, double, double, bool> RoadTest(List<EdgeSample> samples, List<(int Index, Vec3d Forward)> ends)
    {
        const double cell = 8;
        var endOf = ends.ToDictionary(e => e.Index, e => e.Forward);
        var hash = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < samples.Count; i++)
        {
            var k = ((int)Math.Floor(samples[i].Centre.X / cell), (int)Math.Floor(samples[i].Centre.Z / cell));
            if (!hash.TryGetValue(k, out var list)) hash[k] = list = [];
            list.Add(i);
        }
        return (x, z, y) =>
        {
            int hx = (int)Math.Floor(x / cell), hz = (int)Math.Floor(z / cell);
            for (var dx = -3; dx <= 3; dx++)
                for (var dz = -3; dz <= 3; dz++)
                {
                    if (!hash.TryGetValue((hx + dx, hz + dz), out var list)) continue;
                    foreach (var i in list)
                    {
                        var s = samples[i];
                        // Only the sample right here along its road (beyond a track's end: right next to its last one).
                        var along = (x - s.Centre.X) * -s.Right.Z + (z - s.Centre.Z) * s.Right.X;
                        if (Math.Abs(along) > AlongSpacing * 0.75) continue;
                        // In front of an end: land.
                        if (endOf.TryGetValue(i, out var forward) && (x - s.Centre.X) * forward.X + (z - s.Centre.Z) * forward.Z > 0.02) continue;
                        var lateral = (x - s.Centre.X) * s.Right.X + (z - s.Centre.Z) * s.Right.Z;
                        var right = s.RightEdge is { } re ? (re.X - s.Centre.X) * s.Right.X + (re.Z - s.Centre.Z) * s.Right.Z : Half(s.Centre, s.Left);
                        var left = s.Left is { } le ? -((le.X - s.Centre.X) * s.Right.X + (le.Z - s.Centre.Z) * s.Right.Z) : Half(s.Centre, s.RightEdge);
                        if (lateral >= right - EdgeMargin || -lateral >= left - EdgeMargin) continue;
                        // Not well below the road there (its height across it: on a banked road the low side is lower).
                        var edgeY = lateral >= 0 ? s.RightEdge?.Y : s.Left?.Y;
                        var width = lateral >= 0 ? right : left;
                        var roadY = edgeY is { } ey && width > 1e-6 ? s.Centre.Y + (ey - s.Centre.Y) * Math.Abs(lateral) / width : s.Centre.Y;
                        if (y >= roadY - BelowRoad) return true;
                    }
                }
            return false;
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
