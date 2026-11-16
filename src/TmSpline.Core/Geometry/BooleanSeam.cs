using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The boolean terrain (the owner's plan of 2026-11-16, "Terrain-Einbettung per Mesh-Boolean"): the roads as closed
/// solids, taken out of the land's solid with an exact boolean (the Manifold library); the land's faces that are left
/// end on the roads' faces, so nothing is matched and no hole can open.
/// </summary>
public static class BooleanSeam
{
    /// <summary>A road's mesh in world space, its vertices welded by place (the builder repeats them where UVs or normals change).</summary>
    internal sealed record RoadMesh(Track Track, List<Vector3> Positions, List<int> Indices);

    private static readonly float Weld = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_BOOL_WELD"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1e4f;

    internal static List<RoadMesh> RoadMeshes(TrackMap map, MeshQuality quality, bool keepOpen = false)
    {
        var result = new List<RoadMesh>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(quality));
            if (rings.Count < 2) continue;
            var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, WorldSpace = true, MaxColumnAngleDegrees = MeshQualities.ColumnAngleDegrees(quality) };
            var positions = new List<Vector3>();
            var indices = new List<int>();
            var at = new Dictionary<(long, long, long), int>();
            foreach (var chunk in RoadMeshBuilder.Build(track, rings, Vec3d.Zero, "T", settings))
            {
                var map2 = new int[chunk.Positions.Count];
                for (var i = 0; i < chunk.Positions.Count; i++)
                {
                    var p = chunk.Positions[i];
                    var key = ((long)MathF.Round(p.X * Weld), (long)MathF.Round(p.Y * Weld), (long)MathF.Round(p.Z * Weld));
                    if (!at.TryGetValue(key, out var v)) { v = positions.Count; positions.Add(p); at[key] = v; }
                    map2[i] = v;
                }
                foreach (var t in chunk.Triangles)
                {
                    // (Not the decals lying on the road: open sheets, no part of its body.)
                    var material = t.Material >= 0 && t.Material < chunk.Materials.Count ? chunk.Materials[t.Material] : null;
                    if (material is not null && (material.Name.StartsWith("Decal", StringComparison.Ordinal) || material.Link.Contains("Decal", StringComparison.Ordinal))) continue;
                    int a = map2[t.A], b = map2[t.B], c = map2[t.C];
                    if (a == b || b == c || a == c) continue;
                    indices.AddRange([a, b, c]);
                }
            }
            result.Add(new RoadMesh(track, positions, indices));
        }
        return result;
    }

    /// <summary>
    /// Of a mesh's connected parts only those that are closed (each edge used by two triangles, one each way): the road's
    /// body, not the decals lying on it (open strips: a wooden road's lines). How many triangles were left out.
    /// </summary>
    internal static List<int> ClosedParts(int vertices, List<int> indices, out int dropped)
    {
        var parent = new int[vertices];
        for (var i = 0; i < vertices; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            int a = Find(indices[t]), b = Find(indices[t + 1]), c = Find(indices[t + 2]);
            parent[a] = b; parent[Find(c)] = Find(b);
        }
        var uses = new Dictionary<long, int>(EdgeKeyComparer.Instance);
        for (var t = 0; t + 2 < indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                long key = ((long)indices[t + k] << 32) | (uint)indices[t + (k + 1) % 3];
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        var bad = new HashSet<int>();
        foreach (var (key, n) in uses)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            if (n != 1 || uses.GetValueOrDefault(((long)b << 32) | (uint)a) != 1) bad.Add(Find(a));
        }
        var kept = new List<int>(indices.Count);
        dropped = 0;
        for (var t = 0; t + 2 < indices.Count; t += 3)
            if (bad.Contains(Find(indices[t]))) dropped++;
            else kept.AddRange([indices[t], indices[t + 1], indices[t + 2]]);
        return kept;
    }

    /// <summary>The free space's floor lies this far under the driving surface (metres, TMSPLINE_BOOL_UNDER): inside the slab, so no face of it lies on the road's, and the land under the road is cut level there.</summary>
    public static float FreeUnder { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_BOOL_UNDER"), System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : 0.25f;

    /// <summary>Debug lines of the last build's free spaces (x, y, z, x, y, z, kind; kind 10 a valid one, 11 one the library refused).</summary>
    public static float[] LastVolumes { get; private set; } = [];

    /// <summary>
    /// The roads' free space as closed solids (step 2 of the plan): the voxel rooms' sections (StitchedTerrain, every
    /// 2 m: rail, driving surface FreeUnder under it, rail, a vault over it from rail to rail; a pipe's whole inside)
    /// swept along each road, its ends closed. Land in it is cut away exactly; its faces left become the land's there.
    /// </summary>
    internal static List<(List<Vector3> Positions, List<int> Indices)> FreeSpaces(TerrainGrid grid, TrackMap map)
    {
        var result = new List<(List<Vector3>, List<int>)>();
        foreach (var (track, all) in StitchedTerrain.RoomSections(grid, map, FreeUnder))
        {
            if (all.Count < 2) continue;
            var p = all[0].Length;
            foreach (var section in all) ContinueCurl(section);
            var sections = Thin(all, FreeTolerance);
            // A closed track: its last section is its first (all round, no ends).
            var round = track.Closed && Vector3.Distance(sections[0][0], sections[^1][0]) < 0.01f;
            var count = round ? sections.Count - 1 : sections.Count;
            var positions = new List<Vector3>();
            var indices = new List<int>();
            for (var k = 0; k < count; k++) positions.AddRange(sections[k]);
            for (var k = 0; k < (round ? count : count - 1); k++)
            {
                int r0 = k * p, r1 = (k + 1) % count * p;
                for (var j = 0; j < p; j++)
                {
                    int a = r0 + j, b = r0 + (j + 1) % p, c = r1 + (j + 1) % p, d = r1 + j;
                    indices.AddRange([a, b, c, a, c, d]);
                }
            }
            if (!round)
                foreach (var (k, flip) in new[] { (0, true), (count - 1, false) })
                {
                    // Each end a fan round its section's middle.
                    var middle = positions.Count;
                    var sum = Vector3.Zero;
                    for (var j = 0; j < p; j++) sum += positions[k * p + j];
                    positions.Add(sum / p);
                    for (var j = 0; j < p; j++)
                    {
                        int a = k * p + j, b = k * p + (j + 1) % p;
                        if (flip) indices.AddRange([middle, b, a]); else indices.AddRange([middle, a, b]);
                    }
                }
            // Facing out: turned over if its volume comes out negative.
            var volume = 0.0;
            for (var t = 0; t + 2 < indices.Count; t += 3) volume += Vector3.Dot(positions[indices[t]], Vector3.Cross(positions[indices[t + 1]], positions[indices[t + 2]]));
            if (volume < 0)
                for (var t = 0; t + 2 < indices.Count; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
            result.Add((positions, indices));
        }
        return result;
    }

    /// <summary>
    /// The free space's vault continues the road's curl: a smooth arch (a cubic curve) from the right rail round the
    /// open side to the left, leaving each rail the way the driving surface runs at that edge where the edge is curled
    /// past upright (a wall, a half-pipe, a tube's lip: the arch goes on round, no second, smaller tube on top), and
    /// straight away from the line between the rails where it is not (a flat road: about the old round vault, at least
    /// 4.5 m high). The section is changed in place.
    /// </summary>
    private static void ContinueCurl(Vector3[] section)
    {
        const int deckFirst = 1, deckLast = StitchedTerrain.RoomDeckPoints, rightRail = StitchedTerrain.RoomRailRight;
        var arch = section.Length - rightRail;
        Vector3 railR3 = section[rightRail], railL3 = section[0];
        var chord3 = railL3 - railR3;
        if (chord3.LengthSquared() < 0.09f) return;
        // The section's plane: along the chord and towards the vault's middle (the open side, as the rooms chose it).
        var e1 = Vector3.Normalize(chord3);
        var toTop = section[rightRail + arch / 2] - railR3;
        var e2 = toTop - e1 * Vector3.Dot(toTop, e1);
        if (e2.LengthSquared() < 1e-6f) return;
        e2 = Vector3.Normalize(e2);
        Vector2 To2(Vector3 v) => new(Vector3.Dot(v - railR3, e1), Vector3.Dot(v - railR3, e2));
        Vector3 To3(Vector2 v) => railR3 + e1 * v.X + e2 * v.Y;
        var c = chord3.Length();
        Vector2 railR = Vector2.Zero, railL = new(c, 0), open = Vector2.UnitY;
        // Each rail's way out: the surface's direction at that edge (outwards) where it is curled past upright, else
        // straight up from the chord.
        Vector2 Out(Vector2 edgeDir, Vector2 outward)
        {
            if (edgeDir.LengthSquared() < 1e-8f) return open;
            edgeDir = Vector2.Normalize(edgeDir);
            var angle = MathF.Atan2(Vector2.Dot(edgeDir, open), Vector2.Dot(edgeDir, outward));
            angle = Math.Clamp(angle, MathF.PI / 2, MathF.PI * 1.1f);
            return outward * MathF.Cos(angle) + open * MathF.Sin(angle);
        }
        var dirR = Out(To2(section[deckLast]) - To2(section[deckLast - 1]), new Vector2(-1, 0));
        var dirL = Out(To2(section[deckFirst]) - To2(section[deckFirst + 1]), new Vector2(1, 0));
        // Control lengths: a round vault over the chord (4/3 of half of it gives a half circle), at least 4.5 m high;
        // shorter where a rail's way turns back over the road (a tube's lips facing each other).
        var length = MathF.Max(c * 2 / 3, 6);
        var lengthR = length * (1 - 0.5f * MathF.Max(0, dirR.X));
        var lengthL = length * (1 - 0.5f * MathF.Max(0, -dirL.X));
        Vector2 p1 = railR + dirR * lengthR, p2 = railL + dirL * lengthL;
        var points = new Vector2[arch];
        for (var k = 1; k < arch; k++)
        {
            var t = (float)k / arch;
            var u = 1 - t;
            points[k] = railR * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + railL * (t * t * t);
        }
        // No higher over the driving surface's middle (along its normal there) than half the road's width, at least
        // 4.5 m, and no less than 2 m over the higher rail: where one side rises into a wall, the line between the rails
        // runs up it, and the arch over that line stood metres over the wall and the vaults round it (a hump).
        var middle = (deckFirst + deckLast) / 2;
        var mid = To2(section[middle]);
        var tangent = To2(section[middle + 1]) - To2(section[middle - 1]);
        var width = 0f;
        for (var k = deckFirst; k < deckLast; k++) width += Vector3.Distance(section[k], section[k + 1]);
        if (tangent.LengthSquared() > 1e-8f)
        {
            var up = Vector2.Normalize(new Vector2(-tangent.Y, tangent.X));
            if (Vector2.Dot(up, open) < 0) up = -up;
            float Height(Vector2 v) => Vector2.Dot(v - mid, up);
            var floor = MathF.Max(Height(railR), Height(railL));
            var cap = MathF.Max(MathF.Max(4.5f, width / 2), floor + 2);
            var top = float.MinValue;
            for (var k = 1; k < arch; k++) top = MathF.Max(top, Height(points[k]));
            if (top < cap)
            {
                // A road curving down (its middle over its rails): the arch over the line between the rails barely
                // clears it. Raised to the same height over the middle, smoothly (most in the middle, none at the rails).
                for (var k = 1; k < arch; k++) points[k] += up * (cap - top) * MathF.Sin(MathF.PI * k / arch);
            }
            else if (top > cap && top > floor + 1e-3f)
            {
                var keep = (cap - floor) / (top - floor);
                for (var k = 1; k < arch; k++)
                {
                    var h = Height(points[k]);
                    if (h > floor) points[k] -= up * (h - floor) * (1 - keep);
                }
            }
        }
        for (var k = 1; k < arch; k++) section[rightRail + k] = To3(points[k]);
    }

    /// <summary>How far a free space's section may lie off the straight line between the sections kept round it (metres): sections within it are left out.</summary>
    private const float FreeTolerance = 0.1f;

    /// <summary>
    /// Sections left out where the ones round them carry them: from each kept section, the next kept is the furthest one
    /// (at most 32 on) whose sections between lie, point by point, within <paramref name="tolerance"/> of the straight
    /// lines from the one to the other. The first and last are always kept.
    /// </summary>
    private static List<Vector3[]> Thin(List<Vector3[]> sections, float tolerance)
    {
        var kept = new List<Vector3[]> { sections[0] };
        var i = 0;
        while (i < sections.Count - 1)
        {
            var j = i + 1;
            for (var next = i + 2; next < sections.Count && next - i <= 32; next++)
            {
                var ok = true;
                for (var m = i + 1; m < next && ok; m++)
                {
                    var t = (float)(m - i) / (next - i);
                    for (var k = 0; k < sections[m].Length; k++)
                        if (Vector3.DistanceSquared(sections[m][k], Vector3.Lerp(sections[i][k], sections[next][k], t)) > tolerance * tolerance) { ok = false; break; }
                }
                if (!ok) break;
                j = next;
            }
            kept.Add(sections[j]);
            i = j;
        }
        return kept;
    }

    /// <summary>Debug lines for a free space: every other section's outline and lines along it at its rails, the driving surface's middle and the vault's top.</summary>
    private static void DrawVolume(List<float> lines, List<Vector3> positions, int sectionPoints, int sections, int kind)
    {
        void Line(Vector3 a, Vector3 b) => lines.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, kind]);
        for (var k = 0; k < sections; k++)
        {
            if (true)
                for (var j = 0; j < sectionPoints; j++) Line(positions[k * sectionPoints + j], positions[k * sectionPoints + (j + 1) % sectionPoints]);
            if (k + 1 < sections)
                foreach (var j in new[] { 0, (sectionPoints - 12) / 2, sectionPoints - 12, sectionPoints - 6 })
                    Line(positions[k * sectionPoints + j], positions[(k + 1) * sectionPoints + j]);
        }
    }

    /// <summary>Per edge (two vertices, either way) how many triangles use it, and how many of them run it a → b.</summary>
    internal static (int Open, int Over, int Flipped, int Edges) Topology(List<int> indices)
    {
        var uses = new Dictionary<long, (int Count, int Forward)>(EdgeKeyComparer.Instance);
        for (var t = 0; t + 2 < indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = indices[t + k], b = indices[t + (k + 1) % 3];
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                uses.TryGetValue(key, out var u);
                uses[key] = (u.Count + 1, u.Forward + (a < b ? 1 : 0));
            }
        int open = 0, over = 0, flipped = 0;
        foreach (var (_, u) in uses)
        {
            if (u.Count == 1) open++;
            else if (u.Count > 2) over++;
            else if (u.Forward != 1) flipped++;
        }
        return (open, over, flipped, uses.Count);
    }

    /// <summary>Two cubes, one taken out of the other: the output's runs, vertices and edges.</summary>
    public static string SelfTest()
    {
        static (List<Vector3>, List<int>) Cube(Vector3 o, float s)
        {
            var p = new List<Vector3>();
            for (var i = 0; i < 8; i++) p.Add(o + new Vector3((i & 1) * s, ((i >> 1) & 1) * s, ((i >> 2) & 1) * s));
            int[] f = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
            return (p, f.ToList());
        }
        var (pa, ia) = Cube(Vector3.Zero, 2);
        var (pb, ib) = Cube(new Vector3(1, 1, 1), 2);
        using var a = ManifoldLib.Solid.Of(ToMesh(pa, ia, Land));
        using var b = ManifoldLib.Solid.Of(ToMesh(pb, ib));
        using var a2 = a.AsOriginal();
        using var r = ManifoldLib.Solid.Batch([a2, b], ManifoldLib.Op.Subtract);
        var m = r.ToMesh();
        var (o, ov, fl, e) = Topology(m.Tris.Select(v => (int)v).ToList());
        return $"a {a.Status} vol {a.Volume}, b {b.Status} vol {b.Volume}; result {r.Status} vol {r.Volume}, {m.Tris.Length / 3} tris, {m.VertexCount} verts (numProp {m.NumProp}), merges {m.MergeFrom.Length}, runs {string.Join(",", m.RunOriginalId)} at {string.Join(",", m.RunIndex)}; edges {e}: {o} open, {ov} 3+, {fl} same way";
    }

    /// <summary>A report on the roads as solids: their meshes' topology and what the library makes of them.</summary>
    public static string CheckRoads(TrackMap map, MeshQuality quality)
    {
        var lines = new List<string>();
        foreach (var (road, n) in RoadMeshes(map, quality).Select((r, n) => (r, n)))
        {
            var (open, over, flipped, edges) = Topology(road.Indices);
            if (Environment.GetEnvironmentVariable("TMSPLINE_BOOL_OPEN") == "on")
            {
                var all = RoadMeshes(map, quality, keepOpen: true)[n];
                var uses = new Dictionary<long, int>(EdgeKeyComparer.Instance);
                for (var t = 0; t + 2 < all.Indices.Count; t += 3)
                    for (var k = 0; k < 3; k++) { long key = ((long)all.Indices[t + k] << 32) | (uint)all.Indices[t + (k + 1) % 3]; uses[key] = uses.GetValueOrDefault(key) + 1; }
                var shown = 0;
                foreach (var (key, c) in uses)
                {
                    int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                    if (uses.ContainsKey(((long)b << 32) | (uint)a) || shown++ > 12) continue;
                    lines.Add($"   open {all.Positions[a]} – {all.Positions[b]}");
                }
            }
            var line = $"track {n}: {road.Positions.Count} vertices, {road.Indices.Count / 3} triangles, {edges} edges: {open} open, {over} shared by 3+, {flipped} with both triangles the same way";
            if (ManifoldLib.Available)
            {
                using var solid = ManifoldLib.Solid.Of(ToMesh(road.Positions, road.Indices));
                line += $"; manifold: {solid.Status}, {solid.Triangles} triangles, genus {solid.Genus}, volume {solid.Volume:0}";
            }
            else line += "; manifold library not available";
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>A mesh for the library: per vertex x, y, z and a mark (what the vertex belongs to: Land, Closing or Road).</summary>
    internal static ManifoldLib.Mesh ToMesh(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, float mark = Road)
    {
        var props = new float[positions.Count * 4];
        for (var i = 0; i < positions.Count; i++) { props[4 * i] = positions[i].X; props[4 * i + 1] = positions[i].Y; props[4 * i + 2] = positions[i].Z; props[4 * i + 3] = mark; }
        return new ManifoldLib.Mesh { NumProp = 4, Props = props, Tris = indices.Select(i => (uint)i).ToArray() };
    }

    private const float Land = 0, Closing = 1, Road = 2;

    /// <summary>
    /// How deep under the driving surface the voxel land is cut for the boolean terrain (metres, TMSPLINE_BOOL_CUT):
    /// clear of the slab's underside, so the land's floor (at the voxels' precision) does not graze it in a thousand
    /// small seams; the land beside the road still meets it on its sides.
    /// </summary>
    public static float DeckCut { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_BOOL_CUT"), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.5f;

    /// <summary>Whether the voxel land is cut by the roads' rooms first (TMSPLINE_BOOL_VOXELCUT=on); off (the default): only the boolean takes land away.</summary>
    public static bool VoxelCut { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_BOOL_VOXELCUT") == "on";

    /// <summary>How far off the road's surface a seam may run where seam vertices are dropped (metres, TMSPLINE_BOOL_SEAM).</summary>
    public static float SeamTolerance { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_BOOL_SEAM"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0.005f;

    private static bool Stats => Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on";

    /// <summary>
    /// The land with the roads' solids taken out of it. The land (an open sheet) is first closed into a solid: walls
    /// straight down from its own edge to below everything, and a floor there. After the boolean only the faces of the
    /// land's own surface are kept (not the walls and floor, nor the roads' faces): they end exactly on the roads'
    /// faces. Without the library, or if a solid comes out invalid, the land is returned as it was.
    /// </summary>
    public static AdaptiveMesh Cut(AdaptiveMesh land, TerrainGrid grid, TrackMap map, MeshQuality quality)
    {
        if (!ManifoldLib.Available) { Console.WriteLine("  [boolean] the Manifold library is not available: land left as it is"); return land; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Time(string what) { if (Stats) Console.WriteLine($"  [boolean] {what}: {clock.ElapsedMilliseconds} ms"); clock.Restart(); }

        var split = LoopSeam.MakeManifold(land);
        // Only the vertices triangles use.
        var mesh = new AdaptiveMesh();
        {
            var index = new Dictionary<int, int>();
            foreach (var i in land.Indices)
            {
                if (!index.TryGetValue(i, out var j)) { j = mesh.Positions.Count; mesh.Positions.Add(land.Positions[i]); mesh.Pinned.Add(false); index[i] = j; }
                mesh.Indices.Add(j);
            }
        }
        // Facing up (the solid under it): else turned over.
        var up = 0.0;
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
            up += Vector3.Cross(b - a, c - a).Y;
        }
        if (up < 0)
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) (mesh.Indices[t + 1], mesh.Indices[t + 2]) = (mesh.Indices[t + 2], mesh.Indices[t + 1]);

        var roads = RoadMeshes(map, quality);
        var floor = MathF.Min(mesh.Positions.Count > 0 ? mesh.Positions.Min(p => p.Y) : 0, roads.Count > 0 ? roads.Min(r => r.Positions.Count > 0 ? r.Positions.Min(p => p.Y) : 0) : 0) - 10;
        var closed = Close(mesh, floor, out var why);
        if (closed is null) { Console.WriteLine($"  [boolean] the land could not be closed into a solid ({why}): left as it is"); return land; }
        Time($"closed ({split} vertices split where sheets touched)");

        using var made = ManifoldLib.Solid.Of(closed, merge: false);
        // (An original of its own: its id is what its faces are known by after the boolean.)
        using var ground = made.AsOriginal();
        if (ground.Status != ManifoldLib.Error.NoError)
        {
            // Where: the closed mesh's edges (copies merged), by the part they lie in.
            var to = new Dictionary<uint, uint>();
            for (var i = 0; i < closed.MergeFrom.Length; i++) to[closed.MergeFrom[i]] = closed.MergeTo[i];
            var merged = closed.Tris.Select(v => (int)to.GetValueOrDefault(v, v)).ToList();
            var (open, over, flipped, _) = Topology(merged);
            var wallStart = mesh.Indices.Count;
            Console.WriteLine($"  [boolean] the land's solid is invalid ({ground.Status}): {open} open edges, {over} shared by 3+, {flipped} run the same way; land {Topology(merged.Take(wallStart).ToList())}; left as it is");
            return land;
        }
        if (Stats)
        {
            var back = ground.ToMesh();
            var same0 = new Dictionary<uint, uint>();
            for (var i = 0; i < back.MergeFrom.Length; i++) same0[back.MergeFrom[i]] = back.MergeTo[i];
            Console.WriteLine($"  [boolean] land solid back: {back.Tris.Length / 3} tris, {back.VertexCount} verts, {back.MergeFrom.Length} merges; raw {Topology(back.Tris.Select(v => (int)v).ToList())}, merged {Topology(back.Tris.Select(v => (int)same0.GetValueOrDefault(v, v)).ToList())}");
        }
        var solids = new List<ManifoldLib.Solid> { ground };
        try
        {
            foreach (var road in roads)
            {
                var solid = ManifoldLib.Solid.Of(ToMesh(road.Positions, road.Indices));
                if (solid.Status != ManifoldLib.Error.NoError)
                {
                    // Not closed (the road builder leaves a few edges open, e.g. round some holes): its open loops
                    // filled (fans round their middles), and tried again.
                    solid.Dispose();
                    var positions = new List<Vector3>(road.Positions);
                    var tris = new List<int>(road.Indices);
                    var loops = Repair(positions, tris, out var whyNot);
                    if (loops is not null)
                        foreach (var loop in loops)
                        {
                            var middle = positions.Count;
                            positions.Add(loop.Aggregate(Vector3.Zero, (sum, v) => sum + positions[v]) / loop.Count);
                            for (var i = 0; i < loop.Count; i++) tris.AddRange([loop[(i + 1) % loop.Count], loop[i], middle]);
                        }
                    solid = ManifoldLib.Solid.Of(ToMesh(positions, tris));
                    Console.WriteLine($"  [boolean] a road's solid was not closed: its open edges filled ({(loops is null ? whyNot : $"{loops.Count} large loops")}); now {solid.Status}");
                    if (solid.Status != ManifoldLib.Error.NoError) { Console.WriteLine("  [boolean] that road is not cut out"); solid.Dispose(); continue; }
                }
                solids.Add(solid);
            }
            var roadCount = solids.Count - 1;
            var freeIds = new HashSet<uint>();
            var volumeLines = new List<float>();
            foreach (var (fp, fi) in FreeSpaces(grid, map))
            {
                var sectionPoints = StitchedTerrain.RoomSectionPoints;
                var sectionCount = fp.Count / sectionPoints;
                using var made2 = ManifoldLib.Solid.Of(ToMesh(fp, fi, Land));
                DrawVolume(volumeLines, fp, sectionPoints, sectionCount, made2.Status == ManifoldLib.Error.NoError ? 10 : 11);
                if (made2.Status != ManifoldLib.Error.NoError) { Console.WriteLine($"  [boolean] a road's free space is invalid ({made2.Status}): not cut out"); continue; }
                var free = made2.AsOriginal();
                freeIds.Add((uint)free.OriginalId);
                solids.Add(free);
            }
            LastVolumes = [.. volumeLines];
            Time($"solids: land {ground.Triangles} triangles (genus {ground.Genus}), {roadCount} roads, {freeIds.Count} free spaces");
            if (Environment.GetEnvironmentVariable("TMSPLINE_BOOL_EACH") == "on")
                for (var k = 1; k < solids.Count; k++)
                {
                    using var one = ground.Boolean(solids[k], ManifoldLib.Op.Subtract);
                    var m = one.ToMesh();
                    var same1 = new Dictionary<uint, uint>();
                    for (var i = 0; i < m.MergeFrom.Length; i++) same1[m.MergeFrom[i]] = m.MergeTo[i];
                    var landTris = new List<int>();
                    for (var r = 0; r + 1 < m.RunIndex.Length; r++)
                        if (m.RunOriginalId[r] == (uint)ground.OriginalId)
                            for (var t = (int)m.RunIndex[r]; t < (int)m.RunIndex[r + 1]; t++) landTris.Add((int)same1.GetValueOrDefault(m.Tris[t], m.Tris[t]));
                    Console.WriteLine($"  [boolean] road {k}: {solids[k].Triangles} triangles, volume {solids[k].Volume:0}, genus {solids[k].Genus}; land minus it: {one.Status}, {one.Triangles} triangles, land part {landTris.Count / 3}, {Topology(landTris)}");
                }
            using var result = ManifoldLib.Solid.Batch(solids, ManifoldLib.Op.Subtract);
            if (result.Status != ManifoldLib.Error.NoError) { Console.WriteLine($"  [boolean] the boolean failed ({result.Status}): land left as it is"); return land; }
            var output = result.ToMesh();
            Time($"boolean: {result.Triangles} triangles");

            // The land's own faces: from the land's solid (its run), every corner marked Land.
            var landId = (uint)ground.OriginalId;
            if (Stats) Console.WriteLine($"  [boolean] land id {ground.OriginalId}, runs: " + string.Join(", ", Enumerable.Range(0, output.RunOriginalId.Length).Select(r => $"{output.RunOriginalId[r]}: {(output.RunIndex[r + 1] - output.RunIndex[r]) / 3}")));
            var cut = new AdaptiveMesh();
            var at = new Dictionary<uint, int>();
            // Vertices the library gives twice (other properties at the same place) taken as one.
            var same = new Dictionary<uint, uint>();
            for (var i = 0; i < output.MergeFrom.Length; i++) same[output.MergeFrom[i]] = output.MergeTo[i];
            int fromRoads = 0, fromClosing = 0;
            for (var r = 0; r + 1 < output.RunIndex.Length; r++)
            {
                // (The free spaces' faces left are walls of land cut away: the land's.)
                var isLand = output.RunOriginalId[r] == landId || freeIds.Contains(output.RunOriginalId[r]);
                for (var t = (int)output.RunIndex[r]; t + 2 < (int)output.RunIndex[r + 1]; t += 3)
                {
                    if (!isLand) { fromRoads++; continue; }
                    uint a = output.Tris[t], b = output.Tris[t + 1], c = output.Tris[t + 2];
                    if (Mark(output, a) > 0.5f || Mark(output, b) > 0.5f || Mark(output, c) > 0.5f) { fromClosing++; continue; }
                    foreach (var corner in new[] { a, b, c })
                    {
                        var v = same.GetValueOrDefault(corner, corner);
                        if (!at.TryGetValue(v, out var i)) { i = cut.Positions.Count; cut.Positions.Add(output.Position((int)v)); cut.Pinned.Add(false); at[v] = i; }
                        cut.Indices.Add(i);
                    }
                }
            }
            if (Stats) Console.WriteLine($"  [boolean] kept {cut.Indices.Count / 3} land triangles (of {mesh.Indices.Count / 3}); left out {fromRoads} road and {fromClosing} wall and floor triangles");
            if (Stats)
            {
                var places = cut.Positions.Select(p => (p.X, p.Y, p.Z)).Distinct().Count();
                int tiny = 0, small = 0;
                for (var t = 0; t + 2 < cut.Indices.Count; t += 3)
                {
                    var area = Vector3.Cross(cut.Positions[cut.Indices[t + 1]] - cut.Positions[cut.Indices[t]], cut.Positions[cut.Indices[t + 2]] - cut.Positions[cut.Indices[t]]).Length() / 2;
                    if (area < 1e-4f) tiny++; else if (area < 0.05f) small++;
                }
                var (o, ov, fl, _) = Topology(cut.Indices);
                Console.WriteLine($"  [boolean] land out: {cut.Positions.Count} vertices at {places} places; {tiny} triangles under 1 cm2, {small} under 0.05 m2; {o} open edges, {ov} shared by 3+, {fl} run the same way");
            }
            if (Stats) SeamReport(cut);
            Time("split by origin");
            // The land along the seam simplified: its cut ran through every road face it crossed, a vertex wherever it
            // crossed one of their edges. Seam vertices on a straight line between their neighbours (within
            // SeamTolerance) go, then the land inside as the land was simplified before.
            var before = cut.Indices.Count / 3;
            cut = MeshSimplifier.Simplify(cut, 0, VoxelTerrain.SimplifyError, borderTolerance: SeamTolerance);
            MeshSimplifier.ImproveByFlips(cut, 35);
            if (Stats)
            {
                var (o2, ov2, fl2, _) = Topology(cut.Indices);
                Console.WriteLine($"  [boolean] simplified {before} land triangles to {cut.Indices.Count / 3}: {o2} open edges, {ov2} shared by 3+, {fl2} run the same way");
            }
            Time("simplified");
            return cut;
        }
        finally
        {
            foreach (var s in solids.Skip(1)) s.Dispose();
        }
    }

    /// <summary>Holes up to this size across (metres) and this many edges round are filled; larger ones are the land's own edge, walled.</summary>
    private const float SmallHole = 60;
    private const int SmallHoleEdges = 400;

    /// <summary>
    /// The land made a clean sheet: where the voxel mesher left it not manifold (an edge two triangles run the same way,
    /// or a vertex two of its open edges leave: sheets touching), the triangles there are taken out, round after round,
    /// and each small hole that leaves is filled with a fan round its middle. Returns the land's own edge as loops (each
    /// a → b as its triangles run it), or null and why.
    /// </summary>
    private static List<List<int>>? Repair(List<Vector3> positions, List<int> tris, out string why)
    {
        why = "";
        int removedTotal = 0, rounds = 0;
        for (; rounds < 30; rounds++)
        {
            var count = tris.Count / 3;
            var on = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            for (var t = 0; t < count; t++)
                for (var k = 0; k < 3; k++)
                {
                    long key = ((long)tris[3 * t + k] << 32) | (uint)tris[3 * t + (k + 1) % 3];
                    on[key] = on.GetValueOrDefault(key) + 1;
                }
            var remove = new bool[count];
            var outs = new Dictionary<int, int>();
            var ins = new Dictionary<int, int>();
            foreach (var (key, uses) in on)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                if (uses > 1 || on.ContainsKey(((long)b << 32) | (uint)a)) continue;
                outs[a] = outs.GetValueOrDefault(a) + 1;
                ins[b] = ins.GetValueOrDefault(b) + 1;
            }
            var pinch = new HashSet<int>(outs.Where(o => o.Value > 1).Select(o => o.Key).Concat(ins.Where(i => i.Value > 1).Select(i => i.Key)));
            var any = false;
            for (var t = 0; t < count; t++)
            {
                int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
                if (a == b || b == c || a == c || pinch.Contains(a) || pinch.Contains(b) || pinch.Contains(c)) { remove[t] = true; any = true; continue; }
                for (var k = 0; k < 3; k++)
                    if (on[((long)tris[3 * t + k] << 32) | (uint)tris[3 * t + (k + 1) % 3]] > 1) { remove[t] = true; any = true; break; }
            }
            if (!any) break;
            var kept = new List<int>(tris.Count);
            for (var t = 0; t < count; t++)
                if (!remove[t]) kept.AddRange([tris[3 * t], tris[3 * t + 1], tris[3 * t + 2]]);
                else removedTotal++;
            tris.Clear();
            tris.AddRange(kept);
        }

        // The open edges, walked into loops.
        var directed = new HashSet<long>(EdgeKeyComparer.Instance);
        for (var t = 0; t + 2 < tris.Count; t += 3)
            for (var k = 0; k < 3; k++) directed.Add(((long)tris[t + k] << 32) | (uint)tris[t + (k + 1) % 3]);
        var next = new Dictionary<int, int>();
        foreach (var key in directed)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            if (directed.Contains(((long)b << 32) | (uint)a)) continue;
            if (!next.TryAdd(a, b)) { why = $"a vertex two open edges still leave after {rounds} rounds (at {positions[a]})"; return null; }
        }
        var loops = new List<List<int>>();
        var done = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (done.Contains(start)) continue;
            var loop = new List<int>();
            var v = start;
            while (done.Add(v))
            {
                loop.Add(v);
                if (!next.TryGetValue(v, out v)) { why = "an open edge that does not lead on"; return null; }
            }
            if (v != start) { why = "open edges that run into a loop"; return null; }
            loops.Add(loop);
        }
        // Small holes filled, the rest kept as the land's own edge.
        var border = new List<List<int>>();
        var filled = 0;
        foreach (var loop in loops)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var sum = Vector3.Zero;
            foreach (var v in loop) { min = Vector3.Min(min, positions[v]); max = Vector3.Max(max, positions[v]); sum += positions[v]; }
            if (loop.Count > SmallHoleEdges || Vector3.Distance(min, max) > SmallHole) { border.Add(loop); continue; }
            var middle = positions.Count;
            positions.Add(sum / loop.Count);
            for (var i = 0; i < loop.Count; i++) tris.AddRange([loop[(i + 1) % loop.Count], loop[i], middle]);
            filled++;
        }
        if (border.Count == 0) { why = "no edge of its own"; return null; }
        if (Stats) Console.WriteLine($"  [boolean] repaired the land: {removedTotal} triangles taken out in {rounds} rounds, {filled} holes filled; {border.Count} loops of its own edge ({border.Sum(l => l.Count)} edges)");
        return border;
    }

    /// <summary>Debug: the open edges as loops, by size.</summary>
    private static void SeamReport(AdaptiveMesh mesh)
    {
        var directed = new HashSet<long>(EdgeKeyComparer.Instance);
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++) directed.Add(((long)mesh.Indices[t + k] << 32) | (uint)mesh.Indices[t + (k + 1) % 3]);
        var next = new Dictionary<int, int>();
        foreach (var key in directed)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            if (!directed.Contains(((long)b << 32) | (uint)a)) next.TryAdd(a, b);
        }
        var done = new HashSet<int>();
        var loops = new List<(int Edges, float Length, float Size)>();
        foreach (var start in next.Keys)
        {
            if (done.Contains(start)) continue;
            int count = 0; float length = 0;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            var v = start;
            while (done.Add(v) && next.TryGetValue(v, out var w)) { count++; length += Vector3.Distance(mesh.Positions[v], mesh.Positions[w]); min = Vector3.Min(min, mesh.Positions[v]); max = Vector3.Max(max, mesh.Positions[v]); v = w; }
            loops.Add((count, length, Vector3.Distance(min, max)));
        }
        string Band(Func<(int Edges, float Length, float Size), bool> f) { var l = loops.Where(f).ToList(); return $"{l.Count} loops, {l.Sum(x => x.Edges)} edges, {l.Sum(x => x.Length):0} m"; }
        Console.WriteLine($"  [boolean] seam loops: under 2 m across {Band(l => l.Size < 2)}; 2–10 m {Band(l => l.Size >= 2 && l.Size < 10)}; 10–50 m {Band(l => l.Size >= 10 && l.Size < 50)}; larger {Band(l => l.Size >= 50)}");
    }

    private static float Mark(ManifoldLib.Mesh mesh, uint v) => mesh.Props[(int)v * mesh.NumProp + 3];

    /// <summary>
    /// The land (an oriented sheet facing up, each edge used once or twice the other way) closed into a solid: from
    /// each of its own edges a wall straight down to <paramref name="floor"/>, and there a floor under all of it. The
    /// walls' and floor's vertices are marked Closing (the walls' top ones copies of the land's own, merged with them).
    /// Null (and why) if the land's edges do not form clean loops.
    /// </summary>
    private static ManifoldLib.Mesh? Close(AdaptiveMesh land, float floor, out string why)
    {
        why = "";
        var positions = new List<Vector3>(land.Positions);
        var tris = new List<int>(land.Indices);
        var loops = Repair(positions, tris, out why);
        if (loops is null) return null;
        var n = positions.Count;
        var marks = Enumerable.Repeat(Land, n).ToList();
        var mergeFrom = new List<uint>();
        var mergeTo = new List<uint>();
        var polygons = new List<double[]>();
        var bottomOf = new List<int>();
        foreach (var loop in loops)
        {
            int topFirst = positions.Count;
            foreach (var v in loop) { positions.Add(positions[v]); marks.Add(Closing); mergeFrom.Add((uint)positions.Count - 1); mergeTo.Add((uint)v); }
            int bottomFirst = positions.Count;
            foreach (var v in loop) { var p = positions[v]; positions.Add(new Vector3(p.X, floor, p.Z)); marks.Add(Closing); bottomOf.Add(positions.Count - 1); }
            var count = loop.Count;
            for (var i = 0; i < count; i++)
            {
                // The land runs a → b along its edge; the wall b → a, down to a' and b'.
                int a = topFirst + i, b = topFirst + (i + 1) % count, a2 = bottomFirst + i, b2 = bottomFirst + (i + 1) % count;
                tris.AddRange([b, a, a2]);
                tris.AddRange([b, a2, b2]);
            }
            var xy = new double[2 * count];
            for (var i = 0; i < count; i++) { var p = positions[loop[i]]; xy[2 * i] = p.X; xy[2 * i + 1] = p.Z; }
            polygons.Add(xy);
        }
        // The floor: all loops (seen from above) triangulated together, outer ones round one way, holes the other; the
        // library wants the outer ones counter-clockwise, so all are mirrored if the largest is not.
        double Area(double[] xy)
        {
            var s = 0.0;
            for (int i = 0, m = xy.Length / 2; i < m; i++) { int j = (i + 1) % m; s += xy[2 * i] * xy[2 * j + 1] - xy[2 * j] * xy[2 * i + 1]; }
            return s / 2;
        }
        var largest = polygons.OrderByDescending(p => Math.Abs(Area(p))).First();
        if (Area(largest) < 0)
            foreach (var xy in polygons)
                for (var i = 0; i < xy.Length; i += 2) xy[i] = -xy[i];
        var corners = ManifoldLib.Triangulate(polygons);
        if (corners.Length == 0) { why = "its floor could not be triangulated"; return null; }
        // Facing down: all turned together (the triangulation's own order is consistent; a sliver's own facing is not).
        var facing = 0.0;
        for (var t = 0; t + 2 < corners.Length; t += 3)
        {
            int a = bottomOf[corners[t]], b = bottomOf[corners[t + 1]], c = bottomOf[corners[t + 2]];
            facing += Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).Y;
        }
        for (var t = 0; t + 2 < corners.Length; t += 3)
        {
            int a = bottomOf[corners[t]], b = bottomOf[corners[t + 1]], c = bottomOf[corners[t + 2]];
            if (facing > 0) (b, c) = (c, b);
            tris.AddRange([a, b, c]);
        }

        var props = new float[positions.Count * 4];
        for (var i = 0; i < positions.Count; i++) { props[4 * i] = positions[i].X; props[4 * i + 1] = positions[i].Y; props[4 * i + 2] = positions[i].Z; props[4 * i + 3] = marks[i]; }
        return new ManifoldLib.Mesh { NumProp = 4, Props = props, Tris = tris.Select(i => (uint)i).ToArray(), MergeFrom = [.. mergeFrom], MergeTo = [.. mergeTo] };
    }
}
