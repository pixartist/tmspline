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
            var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, WorldSpace = true, LandSolids = true, MaxColumnAngleDegrees = MeshQualities.ColumnAngleDegrees(quality) };
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
            if (!keepOpen) SplitAtTJunctions(positions, indices);
            result.Add(new RoadMesh(track, positions, indices));
        }
        return result;
    }

    /// <summary>
    /// Open edges with another open edge's vertex lying on them (a T-junction: a hole's wall is split wherever the top's
    /// or the underside's triangles cross the hole's outline, the surface only where its own do) split there, the
    /// triangle fanned from its third corner, so both sides share the same vertices and the solid closes. How many
    /// triangles were split.
    /// </summary>
    internal static int SplitAtTJunctions(List<Vector3> positions, List<int> indices, float tolerance = 1e-3f)
    {
        var split = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var edgeTriangle = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            for (var t = 0; t + 2 < indices.Count; t += 3)
                for (var k = 0; k < 3; k++) edgeTriangle[Key(indices[t + k], indices[t + (k + 1) % 3])] = t;
            var open = edgeTriangle.Keys.Where(e => !edgeTriangle.ContainsKey(Key((int)(e & 0xffffffff), (int)(e >> 32)))).ToList();
            if (open.Count == 0) break;
            // The open edges' vertices, by x (an edge looks only at those within its x range).
            var boundary = new HashSet<int>();
            foreach (var e in open) { boundary.Add((int)(e >> 32)); boundary.Add((int)(e & 0xffffffff)); }
            var byX = boundary.OrderBy(v => positions[v].X).ToArray();
            var xs = byX.Select(v => positions[v].X).ToArray();
            var replaced = new Dictionary<int, List<int>>();
            foreach (var e in open)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                Vector3 pa = positions[a], pb = positions[b], d = pb - pa;
                var length2 = d.LengthSquared();
                if (length2 < 1e-12f) continue;
                var on = new List<(float T, int V)>();
                Vector3 lo = Vector3.Min(pa, pb) - new Vector3(tolerance), hi = Vector3.Max(pa, pb) + new Vector3(tolerance);
                var first = Array.BinarySearch(xs, lo.X);
                if (first < 0) first = ~first;
                while (first > 0 && xs[first - 1] >= lo.X) first--;
                for (var k = first; k < xs.Length && xs[k] <= hi.X; k++)
                {
                    var v = byX[k];
                    var q = positions[v];
                    if (v == a || v == b || q.Y < lo.Y || q.Y > hi.Y || q.Z < lo.Z || q.Z > hi.Z) continue;
                    var t = Vector3.Dot(q - pa, d) / length2;
                    if (t <= 1e-4f || t >= 1 - 1e-4f) continue;
                    if (Vector3.DistanceSquared(pa + d * t, q) > tolerance * tolerance) continue;
                    on.Add((t, v));
                }
                if (on.Count == 0) continue;
                var tri = edgeTriangle[e];
                if (replaced.ContainsKey(tri)) continue;
                // The corner opposite the edge.
                int third = -1;
                for (var k = 0; k < 3; k++) if (indices[tri + k] != a && indices[tri + k] != b) third = indices[tri + k];
                var chain = new List<int> { a };
                chain.AddRange(on.OrderBy(x => x.T).Select(x => x.V).Distinct());
                chain.Add(b);
                var fan = new List<int>();
                for (var i = 0; i + 1 < chain.Count; i++) fan.AddRange([chain[i], chain[i + 1], third]);
                replaced[tri] = fan;
            }
            if (replaced.Count == 0) break;
            var rebuilt = new List<int>(indices.Count + replaced.Sum(r => r.Value.Count));
            for (var t = 0; t + 2 < indices.Count; t += 3)
                if (replaced.TryGetValue(t, out var fan)) rebuilt.AddRange(fan);
                else rebuilt.AddRange([indices[t], indices[t + 1], indices[t + 2]]);
            indices.Clear();
            indices.AddRange(rebuilt);
            split += replaced.Count;
        }
        return split;
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

    /// <summary>How far a free space reaches past the road's edges (metres), from the rooms' rails.</summary>
    private const float FreeBeyond = 0.08f;

    /// <summary>Debug lines of the last build's free spaces (x, y, z, x, y, z, kind; kind 10 a valid one, 11 one the library refused).</summary>
    public static float[] LastVolumes { get; private set; } = [];
    /// <summary>The last build's solids (dev server: KeepSolids or TMSPLINE_VOXEL_STATS): the roads, free spaces, fills and abutments, as triangle meshes (x, y, z per vertex; three vertices per triangle).</summary>
    public static IReadOnlyList<(string Kind, float[] Positions, uint[] Tris)> LastSolids { get; private set; } = [];
    /// <summary>Keep the solids of each build in LastSolids (set by the dev server).</summary>
    public static bool KeepSolids { get; set; }

    /// <summary>A section of the abutment as the last build made it (dev server: DebugSection).</summary>
    internal sealed record DebugSectionData(Vector3[] Sec, Vector3[] Out, Vector3[] Dirs, float[] Reach, float[] Weight, float[] Drop, Vector3 Along, int Run, int K);
    private static List<DebugSectionData> LastSections = [];
    private static LandHeights? LastHeights;

    /// <summary>
    /// The last build's abutment section nearest a plan point, in that section's own frame (x across the road, y up,
    /// origin the deck's middle; the plane square to the road, horizontal across it): its points (rails, deck, vault),
    /// outer edge, ray directions, reaches, weights and drops, the land before the cut along it (every 0.5 m) and the
    /// real solids cut by its plane (segments). For the dev server's /api/debug/section. Null: none kept.
    /// </summary>
    public static object? DebugSection(float x, float z, int offset = 0)
    {
        var list = LastSections;
        if (list.Count == 0) return null;
        const int mid = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2;
        var best = list.MinBy(d => (d.Sec[mid].X - x) * (d.Sec[mid].X - x) + (d.Sec[mid].Z - z) * (d.Sec[mid].Z - z))!;
        var o = best.Sec[mid];
        var across = new Vector3(-best.Along.Z, 0, best.Along.X);
        float[] P(Vector3 v) => [Vector3.Dot(v - o, across), v.Y - o.Y];
        // (offset: a neighbouring section of the same run, in this one's frame, with its distance along.)
        if (offset != 0)
        {
            var other = list.FirstOrDefault(d => d.Run == best.Run && d.K == best.K + offset);
            if (other is null) return null;
            return new
            {
                k = other.K,
                along = Vector3.Dot(other.Sec[mid] - o, best.Along),
                sec = other.Sec.Select(P).ToArray(),
                @out = other.Out.Select(P).ToArray(),
                reach = other.Reach,
                weight = other.Weight,
                drop = other.Drop,
            };
        }
        float[] D(Vector3 v) => [Vector3.Dot(v, across), v.Y];
        var land = new List<float[]>();
        if (LastHeights is { } h)
            for (var s = -40f; s <= 40f; s += 0.5f)
            {
                var q = o + across * s;
                var top = h.Top(q.X, q.Z);
                if (float.IsFinite(top)) land.Add([s, top - o.Y]);
            }
        // The real solids cut by the plane (through o, square to Along): per kind, segments [x0, y0, x1, y1].
        var cuts = new Dictionary<string, List<float[]>>();
        foreach (var (kind, pos, tris) in LastSolids)
        {
            if (!cuts.TryGetValue(kind, out var segs)) cuts[kind] = segs = [];
            Vector3 V(uint i) => new(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
            for (var t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3[] tri = [V(tris[t]), V(tris[t + 1]), V(tris[t + 2])];
                var side = tri.Select(v => Vector3.Dot(v - o, best.Along)).ToArray();
                var hits = new List<float[]>();
                for (var e = 0; e < 3; e++)
                {
                    var f = (e + 1) % 3;
                    if (side[e] * side[f] >= 0) continue;
                    var q = Vector3.Lerp(tri[e], tri[f], side[e] / (side[e] - side[f]));
                    if (Vector3.Distance(q, o) < 60) hits.Add(P(q));
                }
                if (hits.Count == 2) segs.Add([hits[0][0], hits[0][1], hits[1][0], hits[1][1]]);
            }
        }
        return new
        {
            k = best.K,
            origin = new[] { o.X, o.Y, o.Z },
            along = new[] { best.Along.X, best.Along.Y, best.Along.Z },
            sec = best.Sec.Select(P).ToArray(),
            @out = best.Out.Select(P).ToArray(),
            dirs = best.Dirs.Select(D).ToArray(),
            reach = best.Reach,
            weight = best.Weight,
            drop = best.Drop,
            land,
            cuts,
        };
    }

    /// <summary>Timing (TMSPLINE_VOXEL_STATS=on): the time since the last stage of a terrain build, printed with this one's name.</summary>
    public static void Stage(string name)
    {
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") != "on") return;
        stageWatch ??= System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"  [time] {name}: {stageWatch.Elapsed.TotalSeconds:0.00} s");
        stageWatch.Restart();
    }
    [ThreadStatic] private static System.Diagnostics.Stopwatch? stageWatch;

    /// <summary>
    /// The roads' free space as closed solids (step 2 of the plan): the voxel rooms' sections (StitchedTerrain, every
    /// 2 m: rail, driving surface FreeUnder under it, rail, a vault over it from rail to rail; a pipe's whole inside)
    /// swept along each road, its ends closed. Land in it is cut away exactly; its faces left become the land's there.
    /// Floor: per point, whether it is on the deck under a segment without underside (the land's floor there: see Cut).
    /// </summary>
    internal static List<(List<Vector3> Positions, List<int> Indices, List<bool> Floor)> FreeSpaces(List<(Track Track, List<Vector3[]> Sections, List<double> Distances)> rooms, List<List<Vector3[]?>>? backs = null)
    {
        var result = new List<(List<Vector3>, List<int>, List<bool>)>();
        foreach (var (track, all, distances) in rooms)
        {
            if (all.Count < 2) continue;
            var p = all[0].Length;
            var starts = TrackSampler.SegmentStarts(track);
            var open = new HashSet<Vector3[]>(ReferenceEqualityComparer.Instance);

            for (var si = 0; si < all.Count; si++)
            {
                var section = all[si];
                // Under a segment without underside the land meets the road at its edges: the deck's two edge points
                // (the land's floor there) raised to OpenFlush under the driving surface and the rails on them (from rails
                // pushed past the edge and a deck under it there was a slot between the road and the land). Its middle is
                // lowered to OpenUnder after the vault (below), so that the land between the sections stays under the road.
                var openSegment = !SegmentAt(track, starts, distances[si]).Underside;
                if (openSegment)
                {
                    Flush(section, all[Math.Max(0, si - 1)], all[Math.Min(all.Count - 1, si + 1)]);
                    foreach (var rail in new[] { 0, StitchedTerrain.RoomRailRight })
                    {
                        var edge = rail == 0 ? 1 : StitchedTerrain.RoomDeckPoints;
                        var outward = section[edge] - section[rail == 0 ? 2 : StitchedTerrain.RoomDeckPoints - 1];
                        var gap = Vector3.Distance(section[1], section[StitchedTerrain.RoomDeckPoints]);
                        section[rail] = section[edge] + (outward.LengthSquared() > 1e-8f ? Vector3.Normalize(outward) * MathF.Min(OpenBeyond, 0.3f * gap) : Vector3.Zero);
                    }
                    all[si] = ContinueCurl(section, CutProfileAt(track, starts, distances[si]));
                    open.Add(all[si]);
                    continue;
                }
                // The rails (the rooms tuck them 3 cm in from the road's edge) moved out past it by FreeBeyond: land in
                // the strip along the edge stood over the driving surface and showed through it.
                var midPoint = section[1 + (StitchedTerrain.RoomDeckPoints - 1) / 2];
                // (The high edge of a steeply banked road: from its curb's inner top corner, so that land may cover the
                // edge's top there; see HighEdgeTop.)
                // (Blended in over a range of banking: a switch here made the free space jump where a road banked through it.)
                var tops = new[] { 0, StitchedTerrain.RoomRailRight }.Select(r => (Share: HighEdgeShare(section, r), Top: HighEdgeTop(section, r, anyBank: true))).ToArray();
                foreach (var rail in new[] { 0, StitchedTerrain.RoomRailRight })
                {
                    var plain = section[rail];
                    // (Out along the driving surface at the edge, as it runs there: pushed out level (away from the
                    // road's middle), at a curled edge the rail ended up behind the deck's edge and the free space's
                    // outline folded back on itself there, and the abutment with it.)
                    var edge = rail == 0 ? 1 : StitchedTerrain.RoomDeckPoints;
                    var outward = section[edge] - section[rail == 0 ? 2 : StitchedTerrain.RoomDeckPoints - 1];
                    // (At most 0.3 of the gap between the road's edges: on a nearly closed pipe the pushed rails passed
                    // each other.)
                    var beyond = MathF.Min(FreeBeyond, 0.3f * Vector3.Distance(section[1], section[StitchedTerrain.RoomDeckPoints]));
                    if (outward.LengthSquared() > 1e-6f) plain += Vector3.Normalize(outward) * beyond;
                    var (share, top) = tops[rail == 0 ? 0 : 1];
                    section[rail] = share > 0 && top is { } t ? Vector3.Lerp(plain, t.Inner, share) : plain;
                }
                all[si] = ContinueCurl(section, CutProfileAt(track, starts, distances[si]));
            }
            // (The middle of a deck without underside lowered to OpenUnder: the land between the sections (ruled, the road
            // curved) poked up through the road from a floor right under it. Only the edges stay at OpenFlush, a bevel to
            // the middle.)
            // (Each section's way along the road taken before any is lowered: from neighbours of which one was lowered
            // already it tilted, the deck went down at a slant that changed from section to section, and its sections no
            // longer thinned out: a build took minutes.)
            const int deckMiddle = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2;
            var alongs = Enumerable.Range(0, all.Count).Select(si => all[Math.Min(all.Count - 1, si + 1)][deckMiddle] - all[Math.Max(0, si - 1)][deckMiddle]).ToList();
            for (var si = 0; si < all.Count; si++)
                if (open.Contains(all[si])) Deepen(all[si], alongs[si]);
            p = all[0].Length;
            var sections = Thin(all, FreeTolerance);
            if (Stats) Console.WriteLine($"  [boolean] free space: {sections.Count} of {all.Count} sections kept ({open.Count} without underside)");
            backs?.Add(sections.Select(s => open.Contains(s) ? s : null).ToList());
            // A closed track: its last section is its first (all round): the pieces go on round past it. In overlapping pieces
            // (see Pieces): one sweep along the whole road overlapped itself where the road came back near itself (a road
            // over another), and the land in the overlap was not cut at all.
            var round = track.Closed && Vector3.Distance(sections[0][0], sections[^1][0]) < 0.01f;
            foreach (var piece in Pieces(sections, round))
            {
                var (sp, si2) = Sweep(piece);
                // (Sweep lays the points out section by section, then the two ends' middles.)
                var floor = new List<bool>(sp.Count);
                for (var v = 0; v < sp.Count; v++)
                {
                    var (k, j) = (v / p, v % p);
                    floor.Add(k < piece.Count && open.Contains(piece[k]) && j >= 1 && j <= StitchedTerrain.RoomDeckPoints);
                }
                result.Add((sp, si2, floor));
            }
        }
        return result;
    }

    /// <summary>
    /// The land under roads without underside, simply (the owner's bed): no bridge, a soft bed under the road in its own
    /// frame. Per section of a free space (where its segment has no underside):
    ///   the bed's top is the deck (the free space's floor), left to right;
    ///   its underside lies along each deck point's normal (away from the driving side) BedEdgeDepth deep at the edges,
    ///     easing to BedDepth over BedSlope in from them (on a road curving away from its driving side, at most
    ///     BedConvexCap of its bend radius: deeper, the underside folded through the bend's centre);
    ///   at each edge a lip, a half ellipse BedLipOut out past the edge and back under it (at most BedLipGap of the gap
    ///     between the edges: on a nearly closed pipe the two lips wrapped into each other);
    ///   all of it scaled by the section's share: the bridge test, rays straight down from the full bed's underside, the
    ///     share of them that start in the land or find it within BridgeReach, from BridgeFrom (a bridge: none) to
    ///     BridgeTo (all of it), smoothed along the road; none on a road curling past BedConvexFrom..BedConvexTo away from
    ///     its driving side (a tube driven on its outside carries itself).
    /// Each convex piece of a section's bed (a quad between two neighbouring deck points, or a lip) with the same piece of
    /// the next section is one convex hull, all of them added as one. Null: none.
    /// </summary>
    internal static ManifoldLib.Solid? BedFills(List<List<Vector3[]?>> runs, AdaptiveMesh landMesh, List<float>? lines = null)
    {
        var bedClock = System.Diagnostics.Stopwatch.StartNew();
        long tSections = 0, tOutlines = 0, tThin = 0, tSweep = 0;
        int kept = 0, total = 0;
        var heights = new LandHeights(landMesh);
        const int first = 1, last = StitchedTerrain.RoomDeckPoints, mid = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2;
        var pieces = new List<ManifoldLib.Solid>();
        var failed = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var debugSections = new List<DebugSectionData>();
        int skirtPieces = 0, bridgePieces = 0;

        // A skirt down one side of a section's bed (right: down its right lip, then along its half of the underside; left:
        // the other way), as the owner drew it: tangent rays down the side, from the lip on, the first that reaches the
        // land (within BedSkirtReach, starting over the land, not in it, and not through the free space on the way) gives
        // the skirt's foot; from the foot the other tangent to the bed; the skirt is the foot and the bed's outline between
        // the two tangents (from the ray's start on round to the second tangent), SkirtArc + 1 points. Null: none.
        Vector3[]? Skirt(Vector3[] section, List<Vector3> bottom, List<Vector3> top, (Vector3 Origin, Vector3 Across, Vector3 Up) f, bool right)
        {
            if (!BedSkirts) return null;
            if (bottom is null || top is null || bottom.Count < 26) return null;
            Vector2 P(Vector3 v) => new(Vector3.Dot(v - f.Origin, f.Across), Vector3.Dot(v - f.Origin, f.Up));
            // (The outline as a loop: the bottom from the right edge round to the left one, then the deck back.)
            var loop = new List<Vector3>(bottom);
            for (var j = top.Count - 1; j >= 0; j--) loop.Add(top[j]);
            var count = loop.Count;
            // (Each side only down its own lip and its half of the underside: further on, the way led up the other lip and
            // its rays crossed the road.)
            var reach = 13 + (bottom.Count - 26) / 2;
            int LoopIndex(int m) => right ? m : bottom.Count - 1 - m;
            for (var m = 0; m < reach; m++)
            {
                var p = loop[LoopIndex(m)];
                var d = Norm3(loop[LoopIndex(Math.Min(m + 1, bottom.Count - 1))] - loop[LoopIndex(Math.Max(m - 1, 0))]);
                if (d == Vector3.Zero) continue;
                // (Over the land, not in it: under the land, or where there is none below, a ray met it from beneath.)
                var ground = heights.Top(p.X, p.Z);
                if (!float.IsFinite(ground) || ground > p.Y) continue;
                // Along the ray to where it goes into the land.
                float hit = -1;
                for (var t = BedSkirtStep; t <= BedSkirtReach; t += BedSkirtStep)
                {
                    var q = p + d * t;
                    var g = heights.Top(q.X, q.Z);
                    if (!float.IsFinite(g) || g < q.Y) continue;
                    float lo = t - BedSkirtStep, hi = t;
                    for (var it = 0; it < 8; it++)
                    {
                        var mt = (lo + hi) / 2;
                        var mq = p + d * mt;
                        var mg = heights.Top(mq.X, mq.Z);
                        if (float.IsFinite(mg) && mg >= mq.Y) hi = mt; else lo = mt;
                    }
                    hit = hi;
                    break;
                }
                if (hit < 0) continue;
                var foot = p + d * hit;
                // (Not through the free space on the way: its section's outline crossed further on than the ray's start.)
                Vector2 a = P(p), b = P(foot);
                var crosses = false;
                for (var j = 0; j < section.Length && !crosses; j++)
                    if (SegmentsCross(a, b, P(section[j]), P(section[(j + 1) % section.Length]), out var ta) && ta * hit > 0.1f) crosses = true;
                if (crosses) continue;
                // The other tangent from the foot: the outline point at the other extreme of the angles seen from it.
                var centre = Vector2.Zero;
                foreach (var v in loop) centre += P(v);
                centre /= count;
                var fc = centre - b;
                float Rel(Vector3 v) { var w = P(v) - b; return MathF.Atan2(fc.X * w.Y - fc.Y * w.X, Vector2.Dot(fc, w)); }
                var iTop = LoopIndex(m);
                var topRel = Rel(loop[iTop]);
                var iLow = iTop;
                var best = topRel > 0 ? float.MaxValue : float.MinValue;
                for (var j = 0; j < count; j++)
                {
                    var r = Rel(loop[j]);
                    if (topRel > 0 ? r < best : r > best) { best = r; iLow = j; }
                }
                // (The way back from the foot to the bed's underside runs in the land, else no skirt: through the air,
                // the skirt met the land along one edge only, a blade of jagged land and odd normals.)
                var back = loop[iLow] - foot;
                var samples = Math.Max(2, (int)MathF.Ceiling(back.Length() / BedSkirtStep));
                for (var q = 1; q < samples; q++)
                {
                    var at = foot + back * ((float)q / samples);
                    var g = heights.Top(at.X, at.Z);
                    if (!float.IsFinite(g) || at.Y > g + BedSkirtAir) return null;
                }
                var arc = new List<Vector3>();
                for (var j = iTop; ; j = right ? (j + 1) % count : (j - 1 + count) % count)
                {
                    arc.Add(loop[j]);
                    if (j == iLow || arc.Count > count) break;
                }
                if (arc.Count < 2) return null;
                var result = new Vector3[SkirtArc + 1];
                result[0] = foot;
                var spaced = Respace3(arc, SkirtArc);
                for (var j = 0; j < SkirtArc; j++) result[j + 1] = spaced[j];
                return result;
            }
            return null;
        }

        // The bridge over a closing pipe's gap (from BedBridgeFrom down to BedBridgeTo between the edges): the hull of both
        // lips, in two halves, each cut to a disc round its lip's outer end (where it joins the underside), growing as the
        // gap closes until they meet over it: it creeps in from the sides (grown all at once it was a crossbar; from the
        // gap's middle it floated in a pipe that never quite closes). Null: none.
        List<Vector3>[]? Bridge(List<Vector3> bottom, (Vector3 Origin, Vector3 Across, Vector3 Up) f, float gap)
        {
            if (bottom is null || bottom.Count < 26) return null;
            var share = 1 - SmoothStep(BedBridgeTo, BedBridgeFrom, gap);
            if (share < 1e-3f) return null;
            Vector2 P(Vector3 v) => new(Vector3.Dot(v - f.Origin, f.Across), Vector3.Dot(v - f.Origin, f.Up));
            Vector3 L(Vector2 v) => f.Origin + f.Across * v.X + f.Up * v.Y;
            var hull = Hull2([.. bottom.Take(13).Concat(bottom.Skip(bottom.Count - 13)).Select(P)]);
            if (hull.Count < 3) return null;
            Vector2 endRight = P(bottom[12]), endLeft = P(bottom[^13]);
            var radius = Vector2.Distance(endLeft, endRight) * (0.15f + 0.4f * share);
            List<Vector3> Half(Vector2 c) => [.. ClipConvex(hull, [.. Enumerable.Range(0, 24).Select(i => c + new Vector2(MathF.Cos(i * MathF.PI / 12), MathF.Sin(i * MathF.PI / 12)) * radius)]).Select(L)];
            return [Half(endRight), Half(endLeft)];
        }
        foreach (var run in runs)
        {
            var count = run.Count;
            // Per section: the deck points' normals (away from the driving side), the full bed's depths, the lips' reach,
            // the share.
            var normals = new Vector3[count][];
            var depth = new float[count][];
            var lipOut = new float[count];
            var share = new float[count];
            for (var k = 0; k < count; k++)
            {
                if (run[k] is not { } sec) continue;
                var prev = run[Math.Max(0, k - 1)] ?? sec;
                var next = run[Math.Min(count - 1, k + 1)] ?? sec;
                var along = next[mid] - prev[mid];
                if (along.LengthSquared() < 1e-6f) continue;
                along = Vector3.Normalize(along);
                var n = new Vector3[last + 1];
                var sum = 0f;
                // (The driving side: towards the vault's middle, as the rooms chose it.)
                var vaultMid = sec[StitchedTerrain.RoomRailRight + (sec.Length - StitchedTerrain.RoomRailRight) / 2];
                for (var j = first; j <= last; j++)
                {
                    var t = sec[Math.Min(last, j + 1)] - sec[Math.Max(first, j - 1)];
                    var c = Vector3.Cross(along, t);
                    n[j] = c.LengthSquared() > 1e-10f ? Vector3.Normalize(c) : Vector3.Zero;
                    sum += Vector3.Dot(n[j], vaultMid - sec[j]);
                }
                // n: towards the driving side; the bed goes the other way.
                if (sum < 0) for (var j = first; j <= last; j++) n[j] = -n[j];
                // How far the deck turns away from its driving side (degrees; a road driven on the outside of its bend),
                // and its length.
                var turn = 0f;
                var length = 0f;
                for (var j = first; j < last; j++)
                {
                    length += Vector3.Distance(sec[j], sec[j + 1]);
                    if (j == first) continue;
                    Vector3 t0 = sec[j] - sec[j - 1], t1 = sec[j + 1] - sec[j];
                    if (t0.LengthSquared() < 1e-10f || t1.LengthSquared() < 1e-10f) continue;
                    t0 = Vector3.Normalize(t0); t1 = Vector3.Normalize(t1);
                    var a = MathF.Acos(Math.Clamp(Vector3.Dot(t0, t1), -1, 1));
                    // (Turning towards the driving side (a pipe) counts negative.)
                    turn += Vector3.Dot(t1 - t0, n[j]) < 0 ? a : -a;
                }
                var turnDeg = turn * 180 / MathF.PI;
                var cap = turn > 1e-3f ? BedConvexCap * length / turn : float.MaxValue;
                var keep = 1 - SmoothStep(BedConvexFrom, BedConvexTo, turnDeg);
                normals[k] = n;
                depth[k] = new float[last + 1];
                var arc = new float[last + 1];
                for (var j = first + 1; j <= last; j++) arc[j] = arc[j - 1] + Vector3.Distance(sec[j - 1], sec[j]);
                var hits = 0;
                // (On a closing pipe all of it BedDepth deep: a ring of one thickness round it, not dented at the seam.)
                var closing = 1 - SmoothStep(0, BedCloseGap, Vector3.Distance(sec[first], sec[last]));
                for (var j = first; j <= last; j++)
                {
                    var fromEdge = MathF.Min(arc[j], arc[last] - arc[j]);
                    var d = MathF.Min(cap, float.Lerp(BedEdgeDepth + (BedDepth - BedEdgeDepth) * SmoothStep(0, BedSlope, fromEdge), BedDepth, closing));
                    depth[k][j] = d;
                    var probe = sec[j] - n[j] * d;
                    // The bridge test: in the land (land over it), or land within reach straight below.
                    var top = heights.Top(probe.X, probe.Z);
                    var below = heights.Below(probe.X, probe.Z, probe.Y);
                    if ((float.IsFinite(top) && top > probe.Y) || (float.IsFinite(below) && probe.Y - below < BridgeReach)) hits++;
                }
                share[k] = SmoothStep(BridgeFrom, MathF.Max(BridgeTo, BridgeFrom + 0.01f), (float)hits / (last - first + 1)) * keep;
                lipOut[k] = MathF.Min(BedLipOut, BedLipGap * Vector3.Distance(sec[first], sec[last]));
            }
            tSections += bedClock.ElapsedMilliseconds; bedClock.Restart();
            // The share smoothed along the road (no section's bed jumps against its neighbours').
            for (var pass = 0; pass < 3; pass++)
            {
                var s2 = (float[])share.Clone();
                for (var k = 0; k < count; k++)
                {
                    if (normals[k] is null) continue;
                    float a = k > 0 && normals[k - 1] is not null ? share[k - 1] : share[k], b = k + 1 < count && normals[k + 1] is not null ? share[k + 1] : share[k];
                    s2[k] = 0.25f * a + 0.5f * share[k] + 0.25f * b;
                }
                share = s2;
            }
            // Each section's bed: its outline (the deck right to left on top; the right lip's curve down to the underside, the
            // underside, the left lip's curve back up below), cut into BedStrips quads between the deck and the bottom (both
            // respaced evenly from the right edge to the left), the same count per section.
            var bed = new (Vector3[] Top, Vector3[] Bottom)?[count];
            // (Per section also its outline as built, before respacing, and its frame: for the skirts and the bridge.)
            var rawBottom = new List<Vector3>[count];
            var rawTop = new List<Vector3>[count];
            var frames = new (Vector3 Origin, Vector3 Across, Vector3 Up)[count];
            var gaps = new float[count];
            for (var k = 0; k < count; k++)
            {
                if (run[k] is not { } sec || normals[k] is null || share[k] < 1e-3f) continue;
                var n = normals[k];
                var s = share[k];
                // (Nothing reaches past the road point it hangs from towards the road's driving side, along the road's own
                // up at its middle (not world up: an inverted road keeps its bed behind it). On a curled edge the bed wrapped
                // round the edge and stood up over it as a crest.)
                var up = n[mid];
                // How far each edge curls past upright (degrees, from outward towards the driving side, its turning summed
                // from the deck's middle): from BedRadialFrom to BedRadialTo its lip turns to straight out of the pipe and
                // the cap fades out (over a nearly closed pipe the free space only cuts the gap: a bed over it is its cover).
                var across = sec[mid - 1] - sec[mid + 1];
                across = Norm3(across - up * Vector3.Dot(across, up));
                float EdgeAngle(int step)
                {
                    var outward = step > 0 ? -across : across;
                    var end = step > 0 ? last : first;
                    Vector2? lastDir = null;
                    var angle = 0f;
                    for (var j = mid; j != end; j += step)
                    {
                        var d3 = sec[j + step] - sec[j];
                        var d = new Vector2(Vector3.Dot(d3, outward), Vector3.Dot(d3, up));
                        if (d.LengthSquared() < 1e-10f) continue;
                        if (lastDir is not { } l) angle = MathF.Atan2(d.Y, d.X);
                        else angle += MathF.Atan2(l.X * d.Y - l.Y * d.X, Vector2.Dot(l, d));
                        lastDir = d;
                    }
                    return angle * 180 / MathF.PI;
                }
                float radialR = SmoothStep(BedRadialFrom, BedRadialTo, EdgeAngle(1)), radialL = SmoothStep(BedRadialFrom, BedRadialTo, EdgeAngle(-1));
                var capShare = 1 - MathF.Max(radialR, radialL);
                Vector3 Cap(Vector3 p, Vector3 from) => p - up * (MathF.Max(0, Vector3.Dot(p - from, up)) * capShare);
                var u = new Vector3[last + 1];
                for (var j = first; j <= last; j++) u[j] = Cap(sec[j] - n[j] * (depth[k][j] * s), sec[j]);
                var spacing = 0f;
                for (var j = first; j < last; j++) spacing += Vector3.Distance(sec[j], sec[j + 1]);
                spacing /= last - first;
                // (The lips join the underside one deck point past the steep part of it, where it is smooth.)
                var join = Math.Clamp((int)MathF.Ceiling(BedLipJoin * BedSlope / MathF.Max(spacing, 1e-3f)) + 1, 1, mid - first - 1);
                // How much the bed is a clean ring round a closing pipe (BedCloseGap down to no gap between the edges): its
                // lips blend onto the underside itself (the two lips left a notch between them at the seam).
                var gap = Vector3.Distance(sec[first], sec[last]);
                var close = 1 - SmoothStep(0, BedCloseGap, gap);
                Vector3 UnderAt(float at)
                {
                    var i0 = Math.Clamp((int)MathF.Floor(at), first, last);
                    var i1 = Math.Min(last, i0 + 1);
                    return Vector3.Lerp(u[i0], u[i1], Math.Clamp(at - i0, 0, 1));
                }
                // A lip: a cubic from the road's edge (leaving along the road's own edge direction without its part towards
                // the driving side, sideways away from the road where little is left: no kink at the edge) to the underside
                // (arriving along it: no corner there).
                List<Vector3> Lip(int edge, int inner, int endAt, int towardsEdge, int awayFromEdge, float radial)
                {
                    var e = sec[edge];
                    var t = Norm3(sec[edge] - sec[inner]);
                    var side = e - sec[mid];
                    side = Norm3(side - up * Vector3.Dot(side, up));
                    // (Without its part towards the driving side, nor back in over the road: a curled edge's direction turned
                    // back over it and the lip crossed the road.)
                    var flat = t - up * MathF.Max(0, Vector3.Dot(t, up));
                    flat -= side * MathF.Min(0, Vector3.Dot(flat, side));
                    var dir = Norm3(Vector3.Lerp(Norm3(flat + side * MathF.Max(0, 0.3f - flat.Length())), -n[edge], radial));
                    var end = u[endAt];
                    // (Its full BedLipOut once it points out of the pipe: the gap's limit is for a lip pointing across it.)
                    var c1 = e + dir * (BedLipReach * float.Lerp(lipOut[k], BedLipOut, radial) * s);
                    var c2 = end + Norm3(u[towardsEdge] - u[awayFromEdge]) * (0.6f * Vector3.Distance(e, end));
                    var pts = new List<Vector3>();
                    for (var q = 0; q <= 12; q++)
                    {
                        var tt = q / 12f;
                        var w = 1 - tt;
                        var curve = e * (w * w * w) + c1 * (3 * w * w * tt) + c2 * (3 * w * tt * tt) + end * (tt * tt * tt);
                        pts.Add(Cap(Vector3.Lerp(curve, UnderAt(edge + (endAt - edge) * tt), close), e));
                    }
                    return pts;
                }
                var bottom = Lip(last, last - 1, last - join, last - join + 1, last - join - 1, radialR);
                for (var j = last - join - 1; j >= first + join + 1; j--) bottom.Add(u[j]);
                var leftLip = Lip(first, first + 1, first + join, first + join - 1, first + join + 1, radialL);
                leftLip.Reverse();
                bottom.AddRange(leftLip);
                // (Its top a little into the free space, BedOverlap along the deck's normals: on the deck itself, the free
                // space's floor, the two solids shared a surface and the boolean shredded it into millions of slivers.)
                var top = new List<Vector3>();
                for (var j = last; j >= first; j--) top.Add(sec[j] + n[j] * BedOverlap);
                bed[k] = (Respace3(top, BedStrips + 1), Respace3(bottom, BedStrips + 1));
                rawBottom[k] = bottom;
                rawTop[k] = top;
                frames[k] = (sec[mid], across, up);
                gaps[k] = gap;
                if (KeepSolids)
                {
                    // (For /api/debug/section: out = the bed's underside under each deck point, dirs = the normals, reach =
                    // the full depths, weight = the share.)
                    var nextSec = run[Math.Min(count - 1, k + 1)] ?? sec;
                    var prevSec = run[Math.Max(0, k - 1)] ?? sec;
                    var al = nextSec[mid] - prevSec[mid];
                    al.Y = 0;
                    if (al.LengthSquared() > 1e-8f)
                    {
                        var outs = sec.Select((v, i) => i >= first && i <= last ? u[i] : v).ToArray();
                        var dirs = sec.Select((v, i) => i >= first && i <= last ? n[i] : Vector3.Zero).ToArray();
                        var reach = sec.Select((v, i) => i >= first && i <= last ? depth[k][i] : 0f).ToArray();
                        var weight = sec.Select(_ => s).ToArray();
                        debugSections.Add(new(sec, outs, dirs, reach, weight, new float[sec.Length], Vector3.Normalize(al), runs.IndexOf(run), k));
                    }
                }
                if (lines is not null)
                    for (var q = 0; q + 1 < bottom.Count; q++) lines.AddRange([bottom[q].X, bottom[q].Y, bottom[q].Z, bottom[q + 1].X, bottom[q + 1].Y, bottom[q + 1].Z, 12]);
            }
            // The sections kept (as the free spaces': from each kept one the furthest, at most 32 on, whose sections between
            // lie, point by point, within BedTolerance of the straight lines to it; a dense run of sections made a
            // million hulls), and between each two kept ones BedStrips convex pieces.
            bool Carries(int i, int j)
            {
                for (var m = i + 1; m < j; m++)
                {
                    if (bed[m] is not { } mm) return false;
                    var t = (float)(m - i) / (j - i);
                    var (ai, bi) = bed[i]!.Value;
                    var (aj, bj) = bed[j]!.Value;
                    for (var q = 0; q <= BedStrips; q++)
                        if (Vector3.DistanceSquared(mm.Top[q], Vector3.Lerp(ai[q], aj[q], t)) > BedTolerance * BedTolerance
                            || Vector3.DistanceSquared(mm.Bottom[q], Vector3.Lerp(bi[q], bj[q], t)) > BedTolerance * BedTolerance) return false;
                }
                return true;
            }
            tOutlines += bedClock.ElapsedMilliseconds; bedClock.Restart();
            // Chains of kept sections (unbroken by sections without a bed), each swept into solids like the free spaces (in
            // overlapping pieces: a solid overlapping itself, a road passing near itself, did not add its overlap): one
            // closed outline per section, the deck right to left and the bottom back. (As convex hulls between the sections,
            // a dense road made tens of thousands of pieces whose union took minutes and left a mesh of slivers.)
            var chain = new List<int>();
            var keptHere = new List<int>();
            void SweepChain(List<int> ks)
            {
                if (ks.Count < 2) return;
                var outlines = ks.Select(k =>
                {
                    var (tp, bt) = bed[k]!.Value;
                    var loop = new Vector3[2 * BedStrips];
                    for (var q = 0; q <= BedStrips; q++) loop[q] = tp[q];
                    for (var q = BedStrips - 1; q >= 1; q--) loop[2 * BedStrips - q] = bt[q];
                    return loop;
                }).ToList();
                tThin += bedClock.ElapsedMilliseconds; bedClock.Restart();
                foreach (var piece in Pieces(outlines, false))
                {
                    var (sp, si) = BooleanSeam.Sweep(piece);
                    var made = ManifoldLib.Solid.Of(ToMesh(sp, si, Land));
                    if (made.Status == ManifoldLib.Error.NoError && made.Triangles > 0) pieces.Add(made);
                    else { made.Dispose(); failed++; }
                }
                tSweep += bedClock.ElapsedMilliseconds; bedClock.Restart();
            }
            for (var k = 0; k < count;)
            {
                if (bed[k] is null) { SweepChain(chain); chain = []; k++; continue; }
                chain.Add(k);
                keptHere.Add(k);
                var next = k + 1;
                if (next >= count || bed[next] is null) { k = next; continue; }
                // (At least BedStep on, whatever the tolerance says: the road's sections come every half metre or so, and
                // a bed changing along a road curling through its shapes kept nearly all of them.)
                for (var j = k + 2; j < count && j - k <= 64 && bed[j] is not null; j++)
                    if (Vector3.Distance(run[k]![mid], run[j]![mid]) < BedStep || (j - k <= 32 && Carries(k, j))) next = j;
                    else break;
                k = next;
                kept++;
            }
            SweepChain(chain);
            total += bed.Count(b => b is not null);
            // The skirts and the bridges over closing pipes, on the kept sections.
            var skirtsR = keptHere.Select(k => Skirt(run[k]!, rawBottom[k], rawTop[k], frames[k], true)).ToList();
            var skirtsL = keptHere.Select(k => Skirt(run[k]!, rawBottom[k], rawTop[k], frames[k], false)).ToList();
            bool Linked(int q) => keptHere[q] - keptHere[q - 1] <= 64 && Enumerable.Range(keptHere[q - 1], keptHere[q] - keptHere[q - 1] + 1).All(i => bed[i] is not null);
            // (Each skirt between two kept sections in a row one convex hull: swept along the road, a skirt whose ray's
            // start or second tangent moved along the bed from section to section twisted through itself, and the
            // boolean left overlaps, holes and edges of three or more triangles.)
            foreach (var skirts in new[] { skirtsR, skirtsL })
                for (var q = 1; q < skirts.Count; q++)
                {
                    if (skirts[q - 1] is not { } sa || skirts[q] is not { } sb || !Linked(q)) continue;
                    var hull = ManifoldLib.Solid.Hull([.. sa, .. sb]);
                    if (hull.Status == ManifoldLib.Error.NoError && hull.Triangles > 0) { pieces.Add(hull); skirtPieces++; }
                    else { hull.Dispose(); failed++; }
                }
            for (var q = 1; q < keptHere.Count; q++)
            {
                if (!Linked(q)) continue;
                int ka = keptHere[q - 1], kb = keptHere[q];
                var ha = Bridge(rawBottom[ka], frames[ka], gaps[ka]);
                var hb = Bridge(rawBottom[kb], frames[kb], gaps[kb]);
                if (ha is null || hb is null) continue;
                for (var side = 0; side < 2; side++)
                {
                    if (ha[side].Count < 3 || hb[side].Count < 3) continue;
                    var hull = ManifoldLib.Solid.Hull([.. ha[side], .. hb[side]]);
                    if (hull.Status == ManifoldLib.Error.NoError && hull.Triangles > 0) { pieces.Add(hull); bridgePieces++; }
                    else { hull.Dispose(); failed++; }
                }
            }
        }
        if (Stats) Console.WriteLine($"  [boolean] bed: {pieces.Count} pieces ({skirtPieces} skirts, {bridgePieces} bridges), {failed} invalid");
        if (KeepSolids) (LastSections, LastHeights) = (debugSections, heights);
        if (pieces.Count == 0) return null;
        bedClock.Restart();
        using var all = ManifoldLib.Solid.Batch(pieces, ManifoldLib.Op.Add);
        foreach (var p in pieces) p.Dispose();
        var tUnion = bedClock.ElapsedMilliseconds;
        if (Stats) Console.WriteLine($"  [boolean] bed: {kept} of {total} sections kept; times: sections {tSections} ms, outlines {tOutlines} ms, thinning {tThin} ms, sweeps {tSweep} ms, union {tUnion} ms ({all.Triangles} triangles)");
        if (all.Status != ManifoldLib.Error.NoError) return null;
        // (With the land's mark on its points, as the other fills.)
        var m = all.ToMesh();
        var positions = Enumerable.Range(0, m.VertexCount).Select(m.Position).ToList();
        using var marked = ManifoldLib.Solid.Of(ToMesh(positions, m.Tris.Select(v => (int)v).ToList(), Land));
        return marked.Status == ManifoldLib.Error.NoError ? marked.AsOriginal() : null;
    }

    /// <summary>Whether segments a-b and c-d cross (t: where along a-b, 0..1).</summary>
    private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t)
    {
        t = 0;
        var r = b - a;
        var s = d - c;
        var den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-9f) return false;
        var w = c - a;
        t = (w.X * s.Y - w.Y * s.X) / den;
        var v = (w.X * r.Y - w.Y * r.X) / den;
        return t >= 0 && t <= 1 && v >= 0 && v <= 1;
    }

    /// <summary>The convex hull of 2D points (counter-clockwise).</summary>
    private static List<Vector2> Hull2(List<Vector2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Vector2>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            var start = h.Count;
            foreach (var p in pass)
            {
                while (h.Count >= start + 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }

    /// <summary>A convex polygon cut to another (both counter-clockwise).</summary>
    private static List<Vector2> ClipConvex(List<Vector2> subject, List<Vector2> clip)
    {
        var output = subject;
        for (var i = 0; i < clip.Count && output.Count > 0; i++)
        {
            Vector2 a = clip[i], b = clip[(i + 1) % clip.Count];
            float Side(Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var input = output;
            output = [];
            for (var j = 0; j < input.Count; j++)
            {
                Vector2 p = input[j], q = input[(j + 1) % input.Count];
                float sp = Side(p), sq = Side(q);
                if (sp >= 0) output.Add(p);
                if ((sp >= 0) != (sq >= 0)) output.Add(p + (q - p) * (sp / (sp - sq)));
            }
        }
        return output;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The bed under a road without underside: how deep under its edges and under the rest, over how far in from the edges it goes from the one to the other, how far its lip wraps out past the edges (metres).</summary>
    private const float BedEdgeDepth = 2.2f, BedDepth = 8.2f, BedSlope = 2.6f, BedLipOut = 0.85f;
    /// <summary>The lip's curve: how far out it leaves the edge (its first control, in BedLipOut's: its bulge); where it joins the underside (one deck point past this share of BedSlope in from the edge).</summary>
    private const float BedLipReach = 2, BedLipJoin = 1;
    /// <summary>The edge angles (degrees past outward towards the driving side) over which a lip turns from the road's edge direction to straight out of the pipe and the bed's cap to the road fades out.</summary>
    private const float BedRadialFrom = 120, BedRadialTo = 165;
    /// <summary>How many quads a section's bed is cut into (between the deck and its bottom, edge to edge).</summary>
    private const int BedStrips = 24;
    /// <summary>How far (metres) a bed section may lie off the straight lines between the sections kept round it: sections within it are left out.</summary>
    private const float BedTolerance = 0.25f;
    /// <summary>The least distance between the bed's kept sections (metres, along the road's middle).</summary>
    private const float BedStep = 2;
    /// <summary>How far the bed's top reaches into the free space over the deck (metres; the free space cuts it back).</summary>
    private const float BedOverlap = 0.05f;
    /// <summary>The lip at most this share of the gap between the road's edges; the bed at most this share of the bend radius under a road curving away from its driving side.</summary>
    private const float BedLipGap = 0.35f, BedConvexCap = 0.8f;
    /// <summary>The bed fades out on a road turning this far away from its driving side (degrees, about its curl).</summary>
    private const float BedConvexFrom = 256, BedConvexTo = 328;
    /// <summary>The bridge test: a ray from the bed's underside finding land within this counts (metres); below this share of the rays it is a bridge, from this one the bed is whole.</summary>
    private const float BridgeReach = 5.5f, BridgeFrom = 0.2f, BridgeTo = 0.5f;
    /// <summary>Gap between a pipe's edges (metres) under which its bed blends into a clean ring round it.</summary>
    private const float BedCloseGap = 2;
    /// <summary>Gaps between a pipe's edges (metres) over which the bed's bridge over the gap creeps in from the sides (none from BedBridgeFrom, whole at BedBridgeTo).</summary>
    private const float BedBridgeFrom = 6, BedBridgeTo = 1;
    /// <summary>Skirts down the bed's sides to the land (tangent rays): off, they made caves and creases where the land lay close.</summary>
    private const bool BedSkirts = false;
    /// <summary>A skirt's tangent ray reaches the land within this (metres), tested every BedSkirtStep.</summary>
    private const float BedSkirtReach = 6, BedSkirtStep = 0.25f;
    /// <summary>How far over the land (metres) a skirt's way back from its foot to the bed may run.</summary>
    private const float BedSkirtAir = 0.05f;
    /// <summary>Points on a skirt's side along the bed (between its two tangents).</summary>
    private const int SkirtArc = 12;

    private static Vector3 Norm3(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;

    /// <summary>A 3D polyline's points respaced evenly along it (count of them, first and last kept).</summary>
    private static Vector3[] Respace3(List<Vector3> line, int count)
    {
        var along = new float[line.Count];
        for (var k = 1; k < line.Count; k++) along[k] = along[k - 1] + Vector3.Distance(line[k - 1], line[k]);
        var total = along[^1];
        var result = new Vector3[count];
        var seg = 1;
        for (var k = 0; k < count; k++)
        {
            var target = total * k / (count - 1);
            while (seg < line.Count - 1 && along[seg] < target) seg++;
            var piece = along[seg] - along[seg - 1];
            var f = piece > 1e-6f ? Math.Clamp((target - along[seg - 1]) / piece, 0, 1) : 0;
            result[k] = Vector3.Lerp(line[seg - 1], line[seg], f);
        }
        return result;
    }

    /// <summary>
    /// Where a segment has no underside: how far under the driving surface the land's floor (the free space's deck) lies,
    /// and how far past the road's edges the rails (metres): the land meets the road at its edge.
    /// </summary>
    public static float OpenFlush { get; set; } = 0.02f;
    private const float OpenBeyond = 0.01f;

    /// <summary>How far under the driving surface the land's floor lies under the middle of a road without underside (metres).</summary>
    public static float OpenUnder { get; set; } = 0.6f;

    /// <summary>
    /// Lowers a section's deck (all but its two edge points) by OpenUnder - FreeUnder, away from the road: within the
    /// section's plane, square to the deck, on the side away from its vault.
    /// </summary>
    private static void Deepen(Vector3[] section, Vector3 along)
    {
        var extra = OpenUnder - FreeUnder;
        if (extra <= 0) return;
        const int n = StitchedTerrain.RoomDeckPoints;
        if (along.LengthSquared() < 1e-6f) return;
        along = Vector3.Normalize(along);
        var vault = section[StitchedTerrain.RoomRailRight + (section.Length - StitchedTerrain.RoomRailRight) / 2];
        // The deck's side facing away from the vault: by all of its points' normals together (from its middle alone the
        // side flipped from section to section on a curled or steeply banked road, the deck went up and down by
        // twice the depth and its sections no longer thinned out: a build took minutes).
        var sign = DeckSide(section, along, vault) > 0 ? -1f : 1f;
        var moved = new Vector3[n + 2];
        for (var j = 2; j < n; j++)
        {
            var down = Vector3.Cross(section[j + 1] - section[j - 1], along) * sign;
            if (down.LengthSquared() < 1e-8f) continue;
            moved[j] = section[j] + Vector3.Normalize(down) * extra;
        }
        for (var j = 2; j < n; j++) if (moved[j] != default) section[j] = moved[j];
    }

    /// <summary>How much the deck's normals (Cross(its direction, along)) face its vault, summed over its points (positive: they do).</summary>
    private static float DeckSide(Vector3[] section, Vector3 along, Vector3 vault)
    {
        const int n = StitchedTerrain.RoomDeckPoints;
        var sum = 0f;
        for (var j = 1; j <= n; j++)
        {
            var c = Vector3.Cross(section[Math.Min(n, j + 1)] - section[Math.Max(1, j - 1)], along);
            if (c.LengthSquared() > 1e-10f) sum += Vector3.Dot(Vector3.Normalize(c), vault - section[j]);
        }
        return sum;
    }

    /// <summary>
    /// Raises a section's two deck edge points from FreeUnder to OpenFlush under the driving surface: within the section's
    /// plane, square to the deck, towards its vault.
    /// </summary>
    private static void Flush(Vector3[] section, Vector3[] before, Vector3[] after)
    {
        var lift = FreeUnder - OpenFlush;
        if (MathF.Abs(lift) < 1e-4f) return;
        const int n = StitchedTerrain.RoomDeckPoints, mid = 1 + (n - 1) / 2;
        var along = after[mid] - before[mid];
        if (along.LengthSquared() < 1e-6f) return;
        along = Vector3.Normalize(along);
        var vault = section[StitchedTerrain.RoomRailRight + (section.Length - StitchedTerrain.RoomRailRight) / 2];
        // The deck's side facing its vault (by all of its points' normals together, see Deepen).
        var sign = DeckSide(section, along, vault) > 0 ? 1f : -1f;
        foreach (var j in new[] { 1, n })
        {
            var up = Vector3.Cross(section[Math.Min(n, j + 1)] - section[Math.Max(1, j - 1)], along) * sign;
            if (up.LengthSquared() > 1e-8f) section[j] += Vector3.Normalize(up) * lift;
        }
    }

    /// <summary>The segment a distance along the track lies in.</summary>
    private static TrackSegment SegmentAt(Track track, IReadOnlyList<double> starts, double distance)
    {
        var s = 0;
        while (s + 1 < starts.Count && starts[s + 1] <= distance) s++;
        return track.Segments[Math.Clamp(s, 0, track.Segments.Count - 1)];
    }

    /// <summary>
    /// The high edge of a road banked steeper than EnvelopBank (the edges' height apart over their distance): the top of the
    /// road's edge there, its inner corner (CurbClear off it, on the open side) and its outer one: a curb's top, or without
    /// a curb the slab's own edge (the driving surface's edge, and SlabThickness behind it). The free space starts at the
    /// inner corner and the land covers the edge from it, up over the outer corner (the owner's profile): along the slab's
    /// own faces, land tucked under the edge left thin slivers that flickered with the road. Null elsewhere.
    /// </summary>
    internal static (Vector3 Inner, Vector3 Outer)? HighEdgeTop(Vector3[] sec, int side, bool anyBank = false)
    {
        const int rightRail = StitchedTerrain.RoomRailRight;
        Vector3 rail = sec[side], other = sec[side == 0 ? rightRail : 0];
        var across = rail - other;
        if (across.LengthSquared() < 1 || (!anyBank && across.Y <= EnvelopBank * across.Length())) return null;
        var mid = sec[1 + (StitchedTerrain.RoomDeckPoints - 1) / 2];
        var open = sec[rightRail + (sec.Length - rightRail) / 2] - mid;
        var inward = -Vector3.Normalize(across);
        open -= inward * Vector3.Dot(open, inward);
        if (open.LengthSquared() < 1e-6f) return null;
        open = Vector3.Normalize(open);
        var deckEdge = sec[side == 0 ? 1 : StitchedTerrain.RoomDeckPoints];
        var h = Vector3.Dot(rail - deckEdge, open);
        // No curb: the rail is the driving surface's own edge (the free spaces' decks lie FreeUnder under the surface,
        // their rails on it: from the deck the corner stood that far under the road and the cove started there).
        // (Blended between the two over curb heights from 0.3 to 0.7 m: curbs grow out of the edge where borders start.)
        var c = Math.Clamp((h - 0.3f) / 0.4f, 0, 1);
        c = c * c * (3 - 2 * c);
        return (Vector3.Lerp(rail + open * CurbClear, deckEdge + open * (h + CurbClear), c), Vector3.Lerp(rail - open * SlabThickness, rail, c));
    }

    /// <summary>
    /// How much of a section's edge is a steep high edge (HighEdgeTop): 0 below EnvelopBank - HighEdgeBlend (the edges'
    /// height apart over their distance), 1 above EnvelopBank + HighEdgeBlend, smooth between.
    /// </summary>
    internal static float HighEdgeShare(Vector3[] sec, int side)
    {
        Vector3 rail = sec[side], other = sec[side == 0 ? StitchedTerrain.RoomRailRight : 0];
        var across = rail - other;
        if (across.LengthSquared() < 1) return 0;
        var x = Math.Clamp((across.Y / across.Length() - (EnvelopBank - HighEdgeBlend)) / (2 * HighEdgeBlend), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private const float HighEdgeBlend = 0.1f;

    /// <summary>Over how many metres the cut profile blends at a joint between two segments (centred on it).</summary>
    private const float CutBlend = 16;

    /// <summary>
    /// How much of the Wall cut profile (TrackSegment.CutProfile) a track has at a distance along it: 0 Slope, 1 Wall,
    /// blended smoothly over CutBlend round the joints between segments with different profiles.
    /// </summary>
    internal static float CutProfileAt(Track track, double[] starts, double distance) =>
        SegmentBlend(track, starts, distance, s => s.CutProfile == CutProfile.Wall ? 1 : 0);

    /// <summary>
    /// How deep a fill may reach down to the ground at a distance along a track, by its segments' fill profiles
    /// (TrackSegment.FillProfile: Embankment MaxFill, Dam MaxDam, None 0), blended over CutBlend round the joints.
    /// </summary>
    internal static float FillDepthAt(Track track, double[] starts, double distance) =>
        SegmentBlend(track, starts, distance, s => s.FillProfile switch { FillProfile.Dam => MaxDam, FillProfile.None => 0, _ => MaxFill });

    /// <summary>A value of a track's segments at a distance along it, blended smoothly over CutBlend round the joints between them.</summary>
    private static float SegmentBlend(Track track, double[] starts, double distance, Func<TrackSegment, float> value)
    {
        var n = starts.Length - 1;
        if (n < 1 || track.Segments.Count == 0) return 0;
        float Of(int s)
        {
            s = track.Closed ? ((s % n) + n) % n : Math.Clamp(s, 0, n - 1);
            return value(track.Segments[Math.Clamp(s, 0, track.Segments.Count - 1)]);
        }
        var k = 0;
        while (k + 1 < n && distance >= starts[k + 1]) k++;
        float own = Of(k), half = CutBlend / 2;
        static float Smooth(double t) { var x = (float)Math.Clamp(t, 0, 1); return x * x * (3 - 2 * x); }
        var fromStart = distance - starts[k];
        var toEnd = starts[k + 1] - distance;
        if (fromStart < half && (k > 0 || track.Closed)) return float.Lerp(Of(k - 1), own, Smooth((fromStart + half) / CutBlend));
        if (toEnd < half && (k < n - 1 || track.Closed)) return float.Lerp(own, Of(k + 1), Smooth((half - toEnd) / CutBlend));
        return own;
    }

    /// <summary>
    /// The Slope cut profile's cove at a road edge (metres): out from the edge and up to where it stands upright, a quarter
    /// ellipse leaving the edge along the road (the owner's profile). The vault's arch starts at its top.
    /// </summary>
    private const float CoveOut = 10, CoveUp = 8;
    /// <summary>
    /// Over how much (of a way's direction, as a unit vector's component) one turning in over the road loses the cove (the
    /// vault changes smoothly along a road banking or curling through it).
    /// </summary>
    private const float FitBlend = 0.2f;
    /// <summary>
    /// The edge angles (degrees past outward towards the open side) over which a curling edge goes from a vault (its cove,
    /// a round arch at least 4.5 m high) to a simple cap (no cove, the arch a circular arc across the gap): from CapFrom
    /// all vault, from CapTo all cap.
    /// </summary>
    private const float CapFrom = 60, CapTo = 95;
    /// <summary>
    /// The edge angles (degrees, negative: curling down, away from the open side; a road driven on the outside of its
    /// bend) over which the vault becomes the band (the driving surface offset BandHeight along its normal): from
    /// ConvexFrom on, all band from ConvexTo.
    /// </summary>
    private const float ConvexFrom = -87, ConvexTo = -110, BandHeight = 4.5f;
    /// <summary>The points of a free space's vault, from the right rail round to the left (evenly spaced along it).</summary>
    private const int VaultPoints = 28;

    /// <summary>
    /// The free space's vault: from the right rail round the open side to the left, as the owner's topology model (each
    /// rule continuous in the road's banking and curl, so the vault never jumps along a road):
    ///   the frame: x along the deck's middle (towards the left), y towards the open side (as the rooms chose it);
    ///   'up' and 'outward' for the coves: world up and level away from the road on a road facing up, blending into the
    ///     road's own frame (its open side; along x out of each rail) as it turns on its side or over (upright);
    ///   each edge's cove (Slope cut profile; Wall: none, blended by wall): CoveOut out, CoveUp up, leaving the rail along
    ///     the road's own edge direction (outward where that runs down, never in over the road), arriving upright; its
    ///     outward part (the lip) fading as the edge comes up to upright (a shrinking cove on a near-upright edge left an
    ///     S-crease); on a convex road far enough out to clear the road itself above the rail; under 10 cm none;
    ///     all of it fading out between CapFrom and CapTo (Uncurled);
    ///   the arch between the coves' tops, a cubic: control lengths of a round vault (2/3 of the span, at least 6 m)
    ///     blending to a circular arc's across the gap as the coves fade (a nearly closed pipe's lips facing each other:
    ///     6 m controls overshot and folded into a spike); the height rule (at least half the road's width over its
    ///     middle, 2 m over the higher end) fading with them;
    ///   on a road curling down past ConvexFrom..ConvexTo, blended point by point into the band (no cove and arch reach
    ///     round a tube driven on its outside).
    /// Returns the section with its new vault (VaultPoints points after the right rail).
    /// </summary>
    private static Vector3[] ContinueCurl(Vector3[] section, float wall)
    {
        const int deckFirst = 1, deckLast = StitchedTerrain.RoomDeckPoints, rightRail = StitchedTerrain.RoomRailRight;
        const int middle = (deckFirst + deckLast) / 2;
        var arch = section.Length - rightRail;
        var result = new Vector3[rightRail + 1 + VaultPoints];
        Array.Copy(section, result, rightRail + 1);
        Vector3 railR3 = section[rightRail], railL3 = section[0];
        // (Degenerate: the rooms' own vault, spread over the points.)
        Vector3[] AsItWas()
        {
            for (var k = 1; k <= VaultPoints; k++)
            {
                var x = (float)k / (VaultPoints + 1) * arch;
                int i0 = (int)MathF.Floor(x), i1 = Math.Min(i0 + 1, arch);
                Vector3 a = i0 == 0 ? railR3 : i0 >= arch ? railL3 : section[rightRail + i0], b = i1 >= arch ? railL3 : section[rightRail + i1];
                result[rightRail + k] = Vector3.Lerp(a, b, x - i0);
            }
            return result;
        }
        // The frame: along the deck's middle (from the rails it flipped where they met on a closed pipe).
        var e1 = section[middle - 1] - section[middle + 1];
        if (e1.LengthSquared() < 1e-8f) return AsItWas();
        e1 = Vector3.Normalize(e1);
        var toTop = section[rightRail + arch / 2] - section[middle];
        var e2 = toTop - e1 * Vector3.Dot(toTop, e1);
        if (e2.LengthSquared() < 1e-6f) return AsItWas();
        e2 = Vector3.Normalize(e2);
        Vector2 To2(Vector3 v) => new(Vector3.Dot(v - railR3, e1), Vector3.Dot(v - railR3, e2));
        Vector2 Plane(Vector3 v) => new(Vector3.Dot(v, e1), Vector3.Dot(v, e2));
        Vector3 To3(Vector2 v) => railR3 + e1 * v.X + e2 * v.Y;
        static Vector2 Norm(Vector2 v) => v.LengthSquared() > 1e-12f ? Vector2.Normalize(v) : Vector2.Zero;
        static float Smooth(float e0, float e1v, float x)
        {
            var t = Math.Clamp((x - e0) / (e1v - e0), 0, 1);
            return t * t * (3 - 2 * t);
        }
        Vector2 railR = Vector2.Zero, railL = To2(railL3), open = Vector2.UnitY;
        var deck = new Vector2[deckLast + 1];
        for (var j = deckFirst; j <= deckLast; j++) deck[j] = To2(section[j]);
        // The deck's normals towards the driving side (the open side at its middle).
        var normal = new Vector2[deckLast + 1];
        for (var j = deckFirst; j <= deckLast; j++)
        {
            var t = deck[Math.Min(deckLast, j + 1)] - deck[Math.Max(deckFirst, j - 1)];
            normal[j] = Norm(new Vector2(-t.Y, t.X));
        }
        if (Vector2.Dot(normal[middle], open) < 0) for (var j = deckFirst; j <= deckLast; j++) normal[j] = -normal[j];
        // The angle of the driving surface's direction at an edge (outwards), from outward towards the open side: its
        // direction at the deck's middle, then its turning summed point by point out to the edge (no wrap past half a turn).
        float EdgeAngle(int step, Vector2 outward)
        {
            Vector2 Basis(Vector2 d) => new(Vector2.Dot(d, outward), Vector2.Dot(d, open));
            var end = step > 0 ? deckLast : deckFirst;
            Vector2? last = null;
            var angle = 0f;
            for (var k = middle; k != end; k += step)
            {
                var d = Basis(deck[k + step] - deck[k]);
                if (d.LengthSquared() < 1e-10f) continue;
                if (last is not { } l) angle = MathF.Atan2(d.Y, d.X);
                else angle += MathF.Atan2(l.X * d.Y - l.Y * d.X, Vector2.Dot(l, d));
                last = d;
            }
            return angle * 180 / MathF.PI;
        }
        // (Leaving a curled edge the way the surface runs there, clamped between upright and a little past level.)
        Vector2 Out(float angle, Vector2 outward)
        {
            var a = Math.Clamp(angle, 90, 198) * MathF.PI / 180;
            return outward * MathF.Cos(a) + open * MathF.Sin(a);
        }
        // How far a way keeps clear of turning in over the road (inwardSign: +1 at the right rail, -1 at the left) or back
        // under it: 1 well clear, 0 not, between over FitBlend.
        float Fits(Vector2 d, float inwardSign)
        {
            if (d.LengthSquared() < 1e-6f) return 0;
            d = Vector2.Normalize(d);
            return Math.Clamp((0.5f + FitBlend - d.X * inwardSign) / FitBlend, 0, 1) * Math.Clamp((d.Y + 0.9f + FitBlend) / FitBlend, 0, 1);
        }
        float Uncurled(float angle) => 1 - Smooth(CapFrom, CapTo, angle);
        // 'Up' for the coves: world up on a road facing up, its open side on one on its side or over.
        var upright = Smooth(0.3f, 0.8f, e2.Y);
        var worldUp = Norm(Plane(Vector3.UnitY));
        if (worldUp == Vector2.Zero) worldUp = open;
        var coveUpDir = Norm(Vector2.Lerp(open, worldUp, upright));
        float co = (1 - wall) * CoveOut, cu = (1 - wall) * CoveUp;
        (List<Vector2> Cove, Vector2 Top, Vector2 Way, float Share) Edge(Vector2 rail, Vector3 rail3, Vector3 other3, float angle, Vector2 outDir, float s, int edge, int inner)
        {
            var share = Uncurled(angle);
            var level = rail3 - other3;
            level.Y = 0;
            var awayWorld = level.LengthSquared() > 1e-8f ? Norm(Plane(Vector3.Normalize(level))) : outDir;
            if (awayWorld == Vector2.Zero) awayWorld = outDir;
            var away = Norm(Vector2.Lerp(outDir, awayWorld, upright));
            var size = share * Fits(away * co + coveUpDir * cu, s);
            var way = Norm(Vector2.Lerp(Out(angle, outDir), Norm(Vector2.Lerp(open, coveUpDir, Fits(coveUpDir, s))), share));
            var t = Norm(deck[edge] - deck[inner]);
            var start = Vector2.Dot(t, coveUpDir) < 0 ? away : Vector2.Dot(t, away) >= 0 ? t : Norm(t - away * Vector2.Dot(t, away));
            var lip = Smooth(0, 0.5f, Vector2.Dot(t, away) + 2 * MathF.Max(0, -Vector2.Dot(t, coveUpDir)));
            var up = cu * size;
            // (Far enough out to clear the road itself above the rail where its driving side faces out past it: by BandHeight
            // (a driving surface's clearance, as the band gives; 0.3 m left a thin strip along a drooping convex edge) for
            // a point facing right out at the cove's height or above, down to 0.3 m near the rail, where the cove's quarter
            // ellipse cannot give more.)
            var clear = 0f;
            for (var j = deckFirst; j <= deckLast; j++)
            {
                var facing = Smooth(0, 0.3f, Vector2.Dot(normal[j], away));
                var h = Vector2.Dot(deck[j] - rail, coveUpDir);
                var margin = float.Lerp(0.3f, float.Lerp(0.3f, BandHeight, facing), Math.Clamp(h / MathF.Max(up, 0.01f), 0, 1));
                var b = (Vector2.Dot(deck[j] - rail, away) + margin) * facing;
                if (b <= 0 || h <= 0) continue;
                var f = 1 - MathF.Pow(1 - MathF.Min(1, h / MathF.Max(up, 0.01f)), 2);
                clear = MathF.Max(clear, b / MathF.Sqrt(MathF.Max(0.01f, f)));
            }
            var outR = MathF.Max(co * size * lip, clear * share);
            var tiny = Smooth(0.05f, 0.15f, MathF.Max(outR, up));
            (outR, up) = (outR * tiny, up * tiny);
            var top = rail + away * outR + coveUpDir * up;
            var cove = new List<Vector2>();
            if (MathF.Max(outR, up) > 1e-3f)
            {
                var c1 = rail + start * (0.55f * float.Lerp(up, outR, Math.Clamp(Vector2.Dot(start, away), 0, 1)));
                var c2 = top - coveUpDir * (up * 0.55f);
                const int steps = 12;
                for (var q = 1; q <= steps; q++)
                {
                    var u = (float)q / steps;
                    var w = 1 - u;
                    cove.Add(rail * (w * w * w) + c1 * (3 * w * w * u) + c2 * (3 * w * u * u) + top * (u * u * u));
                }
            }
            return (cove, top, way, share);
        }
        float angleR = EdgeAngle(1, new Vector2(-1, 0)), angleL = EdgeAngle(-1, new Vector2(1, 0));
        var (coveR, startR, dirR, shareR) = Edge(railR, railR3, railL3, angleR, new Vector2(-1, 0), 1, deckLast, deckLast - 1);
        var (coveL, startL, dirL, shareL) = Edge(railL, railL3, railR3, angleL, new Vector2(1, 0), -1, deckFirst, deckFirst + 1);
        // The arch between the coves' tops: control lengths of a round vault, blending to a circular arc's as the coves fade.
        var span = Vector2.Distance(startR, startL);
        var length = MathF.Max(span * 2 / 3, 6);
        var archShare = MathF.Max(shareR, shareL);
        var turn = MathF.Acos(Math.Clamp(Vector2.Dot(dirR, -dirL), -1, 1));
        var arcLength = span * (2f / 3) / (1 + MathF.Cos(turn / 2));
        var lengthR = float.Lerp(arcLength, length * (1 - 0.5f * MathF.Max(0, dirR.X)), archShare);
        var lengthL = float.Lerp(arcLength, length * (1 - 0.5f * MathF.Max(0, -dirL.X)), archShare);
        Vector2 p1 = startR + dirR * lengthR, p2 = startL + dirL * lengthL;
        const int archSteps = 48;
        var points = new Vector2[archSteps + 1];
        for (var k = 0; k <= archSteps; k++)
        {
            var t = (float)k / archSteps;
            var u = 1 - t;
            points[k] = startR * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + startL * (t * t * t);
        }
        // The height rule (no higher over the deck's middle than half the road's width, at least 4.5 m, and 2 m over the
        // higher end), fading with the coves.
        var mid = deck[middle];
        var tangent = deck[middle + 1] - deck[middle - 1];
        var width = 0f;
        for (var k = deckFirst; k < deckLast; k++) width += Vector2.Distance(deck[k], deck[k + 1]);
        if (tangent.LengthSquared() > 1e-8f && archShare > 0)
        {
            var up = Vector2.Normalize(new Vector2(-tangent.Y, tangent.X));
            if (Vector2.Dot(up, open) < 0) up = -up;
            float Height(Vector2 v) => Vector2.Dot(v - mid, up);
            var floor = MathF.Max(Height(startR), Height(startL));
            var cap = MathF.Max(MathF.Max(4.5f, width / 2), floor + 2);
            var top = float.MinValue;
            for (var k = 1; k < archSteps; k++) top = MathF.Max(top, Height(points[k]));
            if (top < cap)
                for (var k = 1; k < archSteps; k++) points[k] += up * (cap - top) * archShare * MathF.Sin(MathF.PI * k / archSteps);
            else if (top > cap && top > floor + 1e-3f)
            {
                var keep = (cap - floor) / (top - floor);
                for (var k = 1; k < archSteps; k++)
                {
                    var h = Height(points[k]);
                    if (h > floor) points[k] -= up * (h - floor) * (1 - keep) * archShare;
                }
            }
        }
        // The whole vault (right rail, its cove, the arch, the left cove back down, left rail).
        var line = new List<Vector2> { railR };
        line.AddRange(coveR);
        line.AddRange(points.Skip(1).Take(archSteps - 1));
        for (var k = coveL.Count - 1; k >= 0; k--) line.Add(coveL[k]);
        line.Add(railL);
        // The band: the deck offset along its normals past the driving surface by BandHeight, right rail to left.
        var band = new List<Vector2> { railR };
        for (var j = deckLast; j >= deckFirst; j--) band.Add(deck[j] + normal[j] * (BandHeight + FreeUnder));
        band.Add(railL);
        var convex = 1 - Smooth(ConvexTo, ConvexFrom, MathF.Min(angleR, angleL));
        // Both evenly respaced rail to rail, blended point by point.
        var vaultPts = Respace(line, VaultPoints + 2);
        var bandPts = convex > 0 ? Respace(band, VaultPoints + 2) : vaultPts;
        for (var k = 1; k <= VaultPoints; k++) result[rightRail + k] = To3(Vector2.Lerp(vaultPts[k], bandPts[k], convex));
        return result;
    }

    /// <summary>A polyline's points respaced evenly along it (count of them, first and last kept).</summary>
    private static Vector2[] Respace(List<Vector2> line, int count)
    {
        var along = new float[line.Count];
        for (var k = 1; k < line.Count; k++) along[k] = along[k - 1] + Vector2.Distance(line[k - 1], line[k]);
        var total = along[^1];
        var result = new Vector2[count];
        var seg = 1;
        for (var k = 0; k < count; k++)
        {
            var target = total * k / (count - 1);
            while (seg < line.Count - 1 && along[seg] < target) seg++;
            var piece = along[seg] - along[seg - 1];
            var f = piece > 1e-6f ? Math.Clamp((target - along[seg - 1]) / piece, 0, 1) : 0;
            result[k] = Vector2.Lerp(line[seg - 1], line[seg], f);
        }
        return result;
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

    /// <summary>
    /// The roads' own rings as the embankments' stations (laid out like a room's section: index 0 the left contact, 1 to
    /// RoomDeckPoints the driving surface left to right, RoomRailRight the right contact, a point over the middle after it):
    /// the same rings, at the same quality, as the roads cut out of the land, so an embankment's edge runs straight
    /// between the same points as the road's, exactly along it.
    /// </summary>
    internal static List<(Track Track, List<Vector3[]> Sections, List<float> Wall, List<float> FillDepth)> RoadStations(TrackMap map, MeshQuality quality)
    {
        static Vector3 W(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        var result = new List<(Track, List<Vector3[]>, List<float>, List<float>)>();
        const int deck = StitchedTerrain.RoomDeckPoints, rightRail = StitchedTerrain.RoomRailRight, points = StitchedTerrain.RoomSectionPoints;
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(quality));
            if (rings.Count < 2 || track.Segments.Count == 0) continue;
            var starts = TrackSampler.SegmentStarts(track);
            foreach (var run in SplitAtGaps(track, rings, r => r.Distance))
            {
            var list = new List<Vector3[]>();
            var wall = new List<float>();
            var fillDepth = new List<float>();
            foreach (var r in run)
            {
                wall.Add(CutProfileAt(track, starts, r.Distance));
                var segment = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)];
                // (Without underside: none of these fills, the abutment round the road instead (BackFills): -1.)
                fillDepth.Add(segment.Underside ? FillDepthAt(track, starts, r.Distance) : -1);
                var section = new Vector3[points];
                for (var j = 0; j < deck; j++)
                {
                    var q = RoadMeshBuilder.SurfaceSample(r, (j / (double)(deck - 1) - 0.5) * r.Width);
                    section[1 + j] = W(r.Position + r.Right * q.X + r.Up * q.Y);
                }
                var mid = section[1 + (deck - 1) / 2];
                var m = RoadMeshBuilder.SurfaceSample(r, 0);
                var normal = W(r.Right * m.NX + r.Up * m.NY);
                for (var j = rightRail + 1; j < points; j++) section[j] = mid + normal * 5;
                section[0] = W(RoadMeshBuilder.EdgeContact(r, segment, -1));
                section[rightRail] = W(RoadMeshBuilder.EdgeContact(r, segment, 1));
                list.Add(section);
            }
            result.Add((track, list, wall, fillDepth));
            }
        }
        return result;
    }

    /// <summary>
    /// A road's stations in runs between its gaps (where the road is left out): no free space, embankment or cutting
    /// there. The rings at a gap's ends (the sampler puts one exactly at each) end and start the runs beside it.
    /// </summary>
    internal static List<List<T>> SplitAtGaps<T>(Track track, IReadOnlyList<T> stations, Func<T, double> distance)
    {
        var gaps = TrackObjects.GapRanges(track, TrackSampler.SegmentStarts(track));
        var runs = new List<List<T>>();
        if (gaps.Count == 0) { runs.Add([.. stations]); return runs; }
        var current = new List<T>();
        const double eps = 1e-3;
        for (var i = 0; i < stations.Count; i++)
        {
            var d = distance(stations[i]);
            if (gaps.Any(g => d > g.Start + eps && d < g.End - eps))
            {
                if (current.Count >= 2) runs.Add(current);
                current = [];
                continue;
            }
            // (A gap lying between two stations outside it.)
            if (current.Count > 0)
            {
                var before = distance(current[^1]);
                if (gaps.Any(g => g.Start >= before - eps && g.End <= d + eps))
                {
                    if (current.Count >= 2) runs.Add(current);
                    current = [];
                }
            }
            current.Add(stations[i]);
        }
        if (current.Count >= 2) runs.Add(current);
        return runs;
    }

    /// <summary>Embankments: their slope (metres out per metre down), the most they fill (metres; deeper: a bridge, left alone), the edge's anchor under the top of the land's contact on the road's side (metres).</summary>
    private const float EmbankSlope = 1.5f, MaxFill = 10, EmbankAnchor = 0.3f, EdgeAnchor = 0f, Berm = 0.25f, UnderSlab = 0.6f;
    /// <summary>A fill's shoulder: how far it carries the road's surface on before rounding into the slope (metres; the owner's profile).</summary>
    private const float FillShoulder = 1;
    /// <summary>How far before and after the corner between a fill's shoulder and its face the rounding runs (metres).</summary>
    private const float FillRounding = 0.6f;
    /// <summary>
    /// The rounded nose at the top of a steep fill face (the owner's sketch): points from the corner between the shoulder
    /// and the face, as (along the shoulder, out from the face, down the face) in metres, the last one where the face
    /// runs on back to its foot.
    /// </summary>
    private static readonly (float Along, float Out, float Down)[] Nose = [(-0.1f, 0, 0), (0.25f, 0, 0.17f), (0, 0.59f, 0.52f), (0, 0.71f, 1.17f), (0, 0.51f, 2.46f)];
    /// <summary>How steep a fill face (its vertical over horizontal) starts getting the nose, and has all of it.</summary>
    private const float NoseFrom = 1.5f, NoseFull = 4;
    /// <summary>Over how many stations a fill's side changes from the rounded profile to the crest and back.</summary>
    private const int CrestBlend = 5;
    /// <summary>How far before and after the crest at a steep high edge its rounding runs (metres).</summary>
    private const float CrestRound = 1.2f;
    /// <summary>
    /// A fill's face: how much steeper than the ground just outside the edge (radians), and the steepest it gets (metres
    /// down per metre out; about upright).
    /// </summary>
    private const float FaceMargin = 10 * MathF.PI / 180, FaceSteepest = 20;
    /// <summary>Where a fill's face starts, past its shoulder and rounding: how far out from the edge and how far under it (metres).</summary>
    private const float FaceStart = 2 * FillShoulder, FaceTop = 0.3f;
    /// <summary>How far a fill's bottom reaches down to the lowest ground under the road only where it banks less than this (the edges' height apart over their distance; about 30 degrees).</summary>
    private const float LevelBank = 0.5f;
    /// <summary>How deep the Dam fill profile reaches down to the ground at most (metres).</summary>
    private const float MaxDam = 300;
    /// <summary>
    /// Steep banking's high edge (see HighEdgeTop): from about 45 degrees; the free space's start over the curb's top; the
    /// fill's crest over the higher of the curb's top corners and out past the outer one (metres).
    /// </summary>
    private const float EnvelopBank = 0.7f, CurbClear = 0.03f, CrestOver = 0.5f, CrestOut = 0.2f;
    /// <summary>The road slab's thickness under the driving surface (RoadMeshSettings.Thickness).</summary>
    private const float SlabThickness = 0.5f;
    /// <summary>How much an embankment's weight may change from one station to the next.</summary>
    private const float WeightStep = 0.12f;

    /// <summary>The land's heights where a vertical line meets it: its triangles in plan squares.</summary>
    internal sealed class LandHeights
    {
        private const float Square = 8;
        private readonly AdaptiveMesh mesh;
        private readonly Dictionary<(int, int), List<int>> at = [];
        public LandHeights(AdaptiveMesh mesh)
        {
            this.mesh = mesh;
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            {
                Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Square), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Square);
                int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Square), z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Square);
                for (var x = x0; x <= x1; x++)
                    for (var z = z0; z <= z1; z++)
                    {
                        if (!at.TryGetValue((x, z), out var list)) at[(x, z)] = list = [];
                        list.Add(t);
                    }
            }
        }
        /// <summary>The heights of the land's surface over and under a plan point.</summary>
        public IEnumerable<float> At(float x, float z)
        {
            if (!at.TryGetValue(((int)MathF.Floor(x / Square), (int)MathF.Floor(z / Square)), out var list)) yield break;
            foreach (var t in list)
            {
                Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
                var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(d) < 1e-9f) continue;
                var u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
                var v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                yield return a.Y * u + b.Y * v + c.Y * (1 - u - v);
            }
        }
        /// <summary>The highest of them (NaN: no land there).</summary>
        public float Top(float x, float z) { var top = float.NaN; foreach (var y in At(x, z)) if (float.IsNaN(top) || y > top) top = y; return top; }
        /// <summary>The highest at or under a height (NaN: none).</summary>
        public float Below(float x, float z, float y0) { var best = float.NaN; foreach (var y in At(x, z)) if (y <= y0 && (float.IsNaN(best) || y > best)) best = y; return best; }
    }

    /// <summary>
    /// Embankments under each road (the owner's idea, step 3 of the plan): one continuous fill wherever the road runs over
    /// land, from one edge's face under the road to the other's, its bottom 1 m under the lowest ground beneath it (a dam
    /// under the road: where the land fell away under the road, a fill on one side alone stood out as a wedge). At each
    /// edge (EmbankAnchor under the land's contact on the road's side) a shoulder carries the road's surface on
    /// (FillShoulder), rounded into a face down into the ground, as steep as the ground there falls away (FillSide). Its
    /// depth follows the ground station by station (smoothed); where the land is higher than an edge the fill shrinks
    /// under the road there (the free spaces cut that land back). Where the ground lies deeper than the segment's fill
    /// profile allows (FillDepthAt) the road is a bridge: no fill (the fills round it end there). Steep banking (except at
    /// the high edge), curled edges and fast twist make a side smaller; roads not facing up get none. Returns the fills (to
    /// add).
    /// </summary>
    internal static List<(List<Vector3> Positions, List<int> Indices, int SectionPoints)> Embankments(List<(Track Track, List<Vector3[]> Sections, List<float> Wall, List<float> FillDepth)> rooms, LandHeights land)
    {
        var fills = new List<(List<Vector3>, List<int>, int)>();
        const int deckMid = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2, rightRail = StitchedTerrain.RoomRailRight;
        foreach (var (_, sections, _, maxDepth) in rooms)
        {
            var n = sections.Count;
            var left = FillSide.Of(sections, 0, land, maxDepth);
            var right = FillSide.Of(sections, rightRail, land, maxDepth);
            // (Stations on a segment without underside: no fill, BackFills wraps the road there.)
            for (var i = 0; i < n; i++)
                if (maxDepth[i] < 0) left.Valid[i] = right.Valid[i] = false;
            // A road along a hillside, one edge touching the land: its other edge is filled down to the ground however deep
            // (straight down next to the road where the hillside falls away fast). A bridge is a road high over the ground
            // on both sides; one over a valley still is.
            for (var i = 0; i < n; i++)
                foreach (var (side, other) in new[] { (left, right), (right, left) })
                    if (other.Valid[i] && other.Depth[i] <= 1.001f && side.Valid[i] && !float.IsFinite(side.Depth[i]) && float.IsFinite(side.Hillside[i]))
                    {
                        side.Depth[i] = side.Hillside[i];
                        if (side.HillsideUpright[i]) side.Face[i] = FaceSteepest;
                    }
            // A fill where both edges have one, and how low the ground lies under the road (its edges and middle; a little
            // more than the profile allows at most).
            var active = new bool[n];
            var under = new float[n];
            for (var i = 0; i < n; i++)
            {
                under[i] = float.NaN;
                active[i] = left.Valid[i] && right.Valid[i] && float.IsFinite(left.Depth[i]) && float.IsFinite(right.Depth[i]);
                if (!active[i]) continue;
                var sec = sections[i];
                foreach (var q in new[] { sec[1], sec[deckMid], sec[StitchedTerrain.RoomDeckPoints] })
                {
                    var g = land.Below(q.X, q.Z, q.Y);
                    if (float.IsFinite(g)) under[i] = float.IsNaN(under[i]) ? g : MathF.Min(under[i], g);
                }
                if (float.IsFinite(under[i])) under[i] = MathF.Max(under[i], sec[deckMid].Y - MathF.Min(maxDepth[i], MaxDam) - 1);
            }
            bool Valid(int k) => left.Valid[k] && right.Valid[k];
            // Bridges and fills shorter than three stations go over to what is round them (a dip under a short bridge is
            // filled at the depth round it; a short fill between bridges goes).
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < n;)
                {
                    if (!Valid(i)) { i++; continue; }
                    var j = i;
                    while (j + 1 < n && Valid(j + 1) && active[j + 1] == active[i]) j++;
                    var bounded = i > 0 && Valid(i - 1) && j + 1 < n && Valid(j + 1);
                    if (j - i + 1 < 3 && bounded && (pass == 1) == active[i])
                        for (var k = i; k <= j; k++)
                        {
                            active[k] = !active[i];
                            if (!active[k]) continue;
                            foreach (var s in new[] { left, right })
                                if (!float.IsFinite(s.Depth[k])) s.Depth[k] = MathF.Max(s.Depth[i - 1], s.Depth[j + 1]);
                            if (!float.IsFinite(under[k])) under[k] = MathF.Min(under[i - 1], under[j + 1]);
                        }
                    i = j + 1;
                }
            // Smoothed along the road: each side as deep as the deepest of the five stations round it, the ground under the
            // road the lowest of them (still reaching the ground everywhere, no steps).
            var depthL = new float[n];
            var depthR = new float[n];
            var low = new float[n];
            // (A station as deep as a neighbour going straight down goes straight down too: its own sloping face, made that
            // deep, ran far out over the falling hillside and stood out of it as a wedge.)
            var uprightL = new bool[n];
            var uprightR = new bool[n];
            for (var i = 0; i < n; i++)
            {
                if (!active[i]) continue;
                float dl = left.Depth[i], dr = right.Depth[i], g = under[i];
                bool ul = left.Face[i] >= FaceSteepest, ur = right.Face[i] >= FaceSteepest;
                for (var k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++)
                {
                    if (!active[k]) continue;
                    if (left.Depth[k] > dl) (dl, ul) = (left.Depth[k], ul || left.Face[k] >= FaceSteepest);
                    if (right.Depth[k] > dr) (dr, ur) = (right.Depth[k], ur || right.Face[k] >= FaceSteepest);
                    if (float.IsFinite(under[k])) g = float.IsNaN(g) ? under[k] : MathF.Min(g, under[k]);
                }
                (depthL[i], depthR[i], low[i], uprightL[i], uprightR[i]) = (dl, dr, g, ul, ur);
            }
            for (var i = 0; i < n; i++)
            {
                if (uprightL[i]) left.Face[i] = FaceSteepest;
                if (uprightR[i]) right.Face[i] = FaceSteepest;
            }
            // Each stretch of fill, swept; its ends bow in to the road's edges (see Bow): where the road stops allowing a
            // fill (upside down, curled) and at a bridge. (Abutments carrying the end on along the road stood out of the
            // land as wedges and blades wherever it fell away to the side.) The road's own ends stop with their section.
            for (var i = 0; i < n;)
            {
                if (!active[i]) { i++; continue; }
                var j = i;
                while (j + 1 < n && active[j + 1]) j++;
                var (from, to) = (i, j);
                i = j + 1;
                if (to - from < 1) continue;
                bool Ramped(int k) => k >= 0 && k < n;
                bool rampStart = Ramped(from - 1), rampEnd = Ramped(to + 1);
                var polys = new List<Vector3[]>();
                for (var k = from; k <= to; k++)
                {
                    var ramp = Bow(sections, k, from, to, rampStart, rampEnd, MathF.Max(depthL[k], depthR[k]), deckMid);
                    var (underL, sideL) = left.Part(k, depthL[k], ramp);
                    var (underR, sideR) = right.Part(k, depthR[k], ramp);
                    // The bottom: under both feet and 1 m under the lowest ground beneath the road.
                    var bottom = MathF.Min(sideL[^1].Y, sideR[^1].Y) - 0.5f;
                    // (Only under a fairly level road: under one banked into a wall, "under the road" is the whole height
                    // behind it, and the fill stood there as a block down to the ground that the free spaces cut into spikes.)
                    var bank = sections[k][rightRail] - sections[k][0];
                    if (float.IsFinite(low[k]) && MathF.Abs(bank.Y) < LevelBank * bank.Length()) bottom = MathF.Min(bottom, low[k] - 1);
                    // (Bowing in at the ends, the bottom too rises to just under the road: kept at the lowest ground, the
                    // body under the road ended as a square block.)
                    var top = MathF.Min(underL.Y, underR.Y) - 0.5f;
                    if (bottom < top) bottom = top + (bottom - top) * ramp;
                    polys.Add([underL, .. sideL, sideL[^1] with { Y = bottom }, sideR[^1] with { Y = bottom }, .. sideR.Reverse(), underR]);
                }
                // (In overlapping pieces, as the free spaces: a long stretch could overlap itself.)
                foreach (var piece in Pieces(polys, false)) { var (sp, si) = Sweep(piece); fills.Add((sp, si, polys[0].Length)); }
            }
            // Where only one edge can have a fill at all (the other one's way out turns under the road: a road banked into a
            // wall, a curled side), that edge gets one of its own, reaching in under the road from it: the fill under the
            // whole road needs both, and a wall's high edge stood bare.
            // (So does the high edge of a road banked more than LevelBank where the road has no fill under it, its low edge
            // high over the ground: the land hugs the wall's top. On a level road a fill on one side alone stood out as a
            // wedge where the land fell away under the road.)
            foreach (var (side, other, rail, otherRail) in new[] { (left, right, 0, rightRail), (right, left, rightRail, 0) })
                for (var i = 0; i < n;)
                {
                    bool HighEdge(int k) { var across = sections[k][rail] - sections[k][otherRail]; return across.Y > LevelBank * across.Length(); }
                    bool Alone(int k) => side.Valid[k] && float.IsFinite(side.Depth[k]) && (!other.Valid[k] || (!active[k] && HighEdge(k)));
                    if (!Alone(i)) { i++; continue; }
                    var j = i;
                    while (j + 1 < n && Alone(j + 1)) j++;
                    var (from, to) = (i, j);
                    i = j + 1;
                    // (One station on into a fill under the whole road either side, so that the two overlap.)
                    bool Joins(int k) => k >= 0 && k < n && active[k] && side.Valid[k] && float.IsFinite(side.Depth[k]);
                    if (Joins(from - 1)) from--;
                    if (Joins(to + 1)) to++;
                    if (to - from < 1) continue;
                    var (first, last) = (from, to);
                    // (Bowing in where it ends at a bridge or where the edge stops allowing a fill; where it runs on into a
                    // fill under the whole road, or at the road's own end, not.)
                    bool Bridge(int k) => k >= 0 && k < n && !Joins(k);
                    bool bowStart = Bridge(first - 1), bowEnd = Bridge(last + 1);
                    Vector3[] Section(int k, bool bowed = true)
                    {
                        var deepest = side.Depth[k];
                        for (var m = Math.Max(first, k - 2); m <= Math.Min(last, k + 2); m++) deepest = MathF.Max(deepest, side.Depth[m]);
                        var (behindSlab, part) = side.Part(k, deepest, bowed ? Bow(sections, k, first, last, bowStart, bowEnd, deepest, deckMid) : 1);
                        // (In under the road by half its width seen from above, and 1 m: on a wall that is little, and the fill
                        // does not reach down behind it.)
                        var plan = sections[k][rightRail] - sections[k][0];
                        plan.Y = 0;
                        var reach = side.Anchor[k] + side.Inward[k] * (plan.Length() / 2 + 1) + side.Behind[k] * UnderSlab;
                        var bottom = part[^1].Y - 0.5f;
                        return [reach, behindSlab, .. part, part[^1] with { Y = bottom }, reach with { Y = bottom }];
                    }
                    // Where it ends at a bridge in a cliff (an end carried on down along the road would not find the ground on a
                    // slope: the land under the wall a thin fin), the fill stops earlier, where it would (at most CliffTrim
                    // stations), or there is none: a block of fill on a fin looked wrong however it ended.
                    bool Cliff(int k, int inner) =>
                        Abutment(Section(k, false), sections[k][deckMid] - sections[inner][deckMid], [SidePoints + 2, SidePoints + 3], land) is { Cliff: true };
                    var dropped = false;
                    if (bowStart)
                    {
                        var trim = 0;
                        while (to - from >= 1 && Cliff(from, from + 1) && trim++ < CliffTrim) from++;
                        dropped |= to - from < 1 || Cliff(from, from + 1);
                    }
                    if (bowEnd && !dropped)
                    {
                        var trim = 0;
                        while (to - from >= 1 && Cliff(to, to - 1) && trim++ < CliffTrim) to--;
                        dropped |= to - from < 1 || Cliff(to, to - 1);
                    }
                    if (dropped) continue;
                    (first, last) = (from, to);
                    var polys = new List<Vector3[]>();
                    for (var k = from; k <= to; k++) polys.Add(Section(k));
                    foreach (var piece in Pieces(polys, false)) { var (sp, si) = Sweep(piece); fills.Add((sp, si, polys[0].Length)); }
                }
        }
        return fills;
    }

    /// <summary>How far an abutment runs on along the road at most before it drops straight down instead (metres).</summary>
    private const float AbutmentReach = 20;
    /// <summary>How many stations a one-sided fill may be shortened by at most so that its abutment finds the ground on a slope.</summary>
    private const int CliffTrim = 12;

    /// <summary>How long a fill's end bows in at least (metres).</summary>
    private const float BowLength = 8;

    /// <summary>
    /// How much of a fill's side a station of a stretch (from..to) has where the stretch's ends bow in (start, end: which
    /// do): rising from nothing at the end along a quarter circle, whole once as far from it as the fill is deep at
    /// EmbankSlope (and 2 m), at least BowLength, so that its end is rounded in to the road's edge seen from above (the owner's idea; an
    /// abutment carried on along the road stood out of falling land).
    /// </summary>
    private static float Bow(List<Vector3[]> sections, int k, int from, int to, bool start, bool end, float depth, int mid)
    {
        var length = MathF.Max(BowLength, (float.IsFinite(depth) ? depth : 1) * EmbankSlope + 2);
        var r = 1f;
        if (start) r = MathF.Min(r, Vector3.Distance(sections[k][mid], sections[from][mid]) / length);
        if (end) r = MathF.Min(r, Vector3.Distance(sections[k][mid], sections[to][mid]) / length);
        r = Math.Clamp(r, 0, 1);
        return MathF.Sqrt(1 - (1 - r) * (1 - r));
    }

    /// <summary>
    /// An abutment: a fill's end section carried on along the road (along: out of the fill, levelled) and down at
    /// EmbankSlope, each of its points until it meets the ground under it (its end follows the terrain: carried on as a
    /// whole until its top met the lowest ground under any of it, an abutment ran far out over a hillside falling away to
    /// one side and stood out of it as a wedge). A point that does not meet the ground within AbutmentReach (a cliff) drops
    /// straight down to the ground under it instead (Cliff: the highest point did). Its bottom points (bottom: their
    /// indices) go with their neighbours on the section, 1 m under them. Null
    /// where there is no ground under it at all.
    /// </summary>
    private static ((List<Vector3>, List<int>, int) Solid, bool Cliff)? Abutment(Vector3[] end, Vector3 along, int[] bottom, LandHeights land)
    {
        along.Y = 0;
        if (along.LengthSquared() < 1e-6f) return null;
        along = Vector3.Normalize(along);
        var carried = new Vector3[end.Length];
        var lowest = float.NaN;
        var highest = 0;
        for (var j = 1; j < end.Length; j++) if (end[j].Y > end[highest].Y) highest = j;
        var cliff = false;
        for (var j = 0; j < end.Length; j++)
        {
            if (bottom.Contains(j)) continue;
            var q = end[j];
            var met = float.NaN;
            for (var t = 0.5f; t <= AbutmentReach; t += 0.5f)
            {
                var at = q + along * t - Vector3.UnitY * (t / EmbankSlope);
                var g = land.Below(at.X, at.Z, q.Y + 0.05f);
                if (float.IsFinite(g)) lowest = float.IsNaN(lowest) ? g : MathF.Min(lowest, g);
                if (float.IsFinite(g) && at.Y <= g) { met = t; break; }
            }
            if (float.IsFinite(met))
            {
                carried[j] = q + along * met - Vector3.UnitY * (met / EmbankSlope);
                continue;
            }
            if (j == highest) cliff = true;
            carried[j] = new Vector3(float.NaN);
        }
        // Points that met no ground: where the highest one did not (a cliff), straight down to the ground under each;
        // otherwise they end next to where they start (the rest of the abutment on the ground; dropped straight down, a
        // point at the edge of a thin fin stood out of its falling side as a wedge).
        for (var j = 0; j < end.Length; j++)
        {
            if (bottom.Contains(j) || !float.IsNaN(carried[j].X)) continue;
            var q = end[j];
            if (!cliff) { carried[j] = q + along * 0.25f - Vector3.UnitY * 0.25f; continue; }
            var under = land.Below(q.X, q.Z, q.Y + 0.05f);
            if (float.IsNaN(under)) return null;
            lowest = float.IsNaN(lowest) ? under : MathF.Min(lowest, under);
            carried[j] = q with { Y = MathF.Min(q.Y - 0.5f, under - 1) };
        }
        if (float.IsNaN(lowest)) return null;
        // Each bottom point goes with its neighbour on the section (the foot over it, or the point in under the road), as
        // far along and 1 m under where that one ended, which is at or under the ground there. (All of them as far as the
        // farthest point and under the lowest ground sampled anywhere, an abutment on a thin fin stood out of its side as
        // a wedge down to the valley floor.)
        foreach (var k in bottom)
        {
            var pair = !bottom.Contains((k + end.Length - 1) % end.Length) ? (k + end.Length - 1) % end.Length : (k + 1) % end.Length;
            var shift = carried[pair] - end[pair];
            shift.Y = 0;
            carried[k] = (end[k] + shift) with { Y = MathF.Min(end[k].Y, carried[pair].Y) - 1 };
        }
        var (positions, indices) = Sweep([end, carried]);
        return ((positions, indices, end.Length), cliff);
    }

    /// <summary>One edge of a road's stations, for its fill: where the fill starts, which way it goes out, how steep its face is and how deep it reaches.</summary>
    private sealed class FillSide
    {
        public bool[] Valid = [];
        public Vector3[] Anchor = [], Outward = [], Shoulder = [], Inward = [], Behind = [];
        public float[] Weight = [], Face = [], Depth = [];
        /// <summary>How deep the fill would reach however deep the ground (for a road along a hillside; see Embankments), and whether its face goes straight down.</summary>
        public float[] Hillside = [];
        public bool[] HillsideUpright = [];
        /// <summary>The high edge of a steeply banked road: its curb's inner and outer top corners (the fill covers the curb's top from the inner one).</summary>
        public (Vector3 Inner, Vector3 Outer)?[] CurbTop = [];
        /// <summary>How much of the crest profile a station with a curb top takes: rising over CrestBlend stations from where the crest starts or ends, so that the side changes over smoothly (at once, the change stood as an edge).</summary>
        public float[] CrestWeight = [];

        public static FillSide Of(List<Vector3[]> sections, int side, LandHeights land, List<float> maxDepth)
        {
            const int deckMid = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2, rightRail = StitchedTerrain.RoomRailRight;
            var n = sections.Count;
            var s = new FillSide
            {
                Valid = new bool[n], Anchor = new Vector3[n], Outward = new Vector3[n], Shoulder = new Vector3[n],
                Inward = new Vector3[n], Behind = new Vector3[n], Weight = new float[n], Face = new float[n], Depth = new float[n],
                Hillside = new float[n], HillsideUpright = new bool[n],
                CurbTop = new (Vector3, Vector3)?[n],
                CrestWeight = new float[n],
            };
            Array.Fill(s.Face, 1 / EmbankSlope);
            Array.Fill(s.Depth, float.NaN);
            Array.Fill(s.Hillside, float.NaN);
            static float Fade(float from, float to, float x) { var t = Math.Clamp((x - from) / (to - from), 0, 1); return 1 - t * t * (3 - 2 * t); }
            for (var i = 0; i < n; i++)
            {
                var sec = sections[i];
                Vector3 rail = sec[side], other = sec[side == 0 ? rightRail : 0], mid = sec[deckMid];
                var across = rail - other;
                if (across.LengthSquared() < 1) continue;
                // Facing up: the vault's top over the deck.
                if (sec[rightRail + 6].Y < mid.Y) continue;
                // A way out across the plan (a curled side's top rail still has one; a side curled right over not). The high
                // edge of a banked road (up to a wall): out is also away from the side driven on (behind the road), which
                // stays level however steep it banks; the land hugs the wall up to its top edge. (The low edge's way out turns
                // down under the road: as before.)
                var flat = new Vector3(across.X, 0, across.Z);
                var high = across.Y > 0.3f * across.Length();
                var openSide = sec[rightRail + (sec.Length - rightRail) / 2] - mid;
                if (high && openSide.LengthSquared() > 1e-6f)
                {
                    var back = -Vector3.Normalize(openSide);
                    var way = flat / across.Length() + new Vector3(back.X, 0, back.Z);
                    if (way.Length() < 0.3f) continue;
                    s.Outward[i] = Vector3.Normalize(way);
                }
                else
                {
                    if (flat.Length() < 0.3f * across.Length()) continue;
                    s.Outward[i] = Vector3.Normalize(flat);
                }
                // Banking (fading from about 58 to 76 degrees, not at the high edge): the edges' height apart over their
                // distance; curl: how far the surface at this edge turns up (its last piece's direction, outwards), against
                // the road's own cross-section (on a banked road the whole surface is tilted, and its high edge rising in the
                // world is no curl).
                var edgeDir = side == 0 ? sec[1] - sec[2] : sec[StitchedTerrain.RoomDeckPoints] - sec[StitchedTerrain.RoomDeckPoints - 1];
                var curl = edgeDir.LengthSquared() > 1e-8f ? MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(edgeDir), Vector3.Normalize(across)), -1, 1)) : 0;
                s.Weight[i] = MathF.Min(high ? 1 : Fade(0.85f, 0.97f, MathF.Abs(across.Y) / across.Length()), Fade(1.2f, 1.9f, curl));
                // Twist: how fast the driving surface's edge rises or falls against the road's middle from the station before,
                // per metre along (a road rolling over). The surface's own edge, not the rail: where a curb grows out of the
                // edge the rail climbs by the curb's height on a level road, and was taken for a twist.
                if (i > 0)
                {
                    var prev = sections[i - 1];
                    var edge = side == 0 ? 1 : StitchedTerrain.RoomDeckPoints;
                    var along = Vector3.Distance(sec[deckMid], prev[deckMid]);
                    if (along > 1e-3f)
                        s.Weight[i] = MathF.Min(s.Weight[i], Fade(0.25f, 0.5f, MathF.Abs((sec[edge].Y - sec[deckMid].Y) - (prev[edge].Y - prev[deckMid].Y)) / along));
                }
                // Under a curb's top (its outer face then holds the land), or, where the road has no curb (the rail is the
                // driving surface's own edge, near the deck's), just under that edge: 0.3 m down the slab's side the land met
                // it half way and left its edge standing out.
                var hasCurb = Vector3.Distance(rail, sec[side == 0 ? 1 : StitchedTerrain.RoomDeckPoints]) > 0.5f;
                s.Anchor[i] = rail - Vector3.UnitY * (hasCurb ? EmbankAnchor : EdgeAnchor);
                s.Inward[i] = -Vector3.Normalize(across);
                s.CurbTop[i] = HighEdgeTop(sec, side);
                s.Behind[i] = openSide.LengthSquared() > 1e-6f ? -Vector3.Normalize(openSide - s.Inward[i] * Vector3.Dot(openSide, s.Inward[i])) : -Vector3.UnitY;
                if (!float.IsFinite(s.Behind[i].X)) s.Behind[i] = -Vector3.UnitY;
                // The shoulder carries the road's surface on outwards (level where that runs down: a low edge).
                var on = -s.Inward[i];
                s.Shoulder[i] = on.Y >= 0 && new Vector3(on.X, 0, on.Z).Length() > 0.3f ? on : s.Outward[i];
                s.Valid[i] = true;
                // The face: as steep as the ground falls away just outside the edge (from 2 to 10 m out) and FaceMargin more,
                // at least EmbankSlope, at most upright (FaceSteepest): on a steep hillside a 1:1.5 slope met the ground only
                // far out and stood out of it as a shelf; this way the fill hugs the road.
                var e = s.Anchor[i];
                Vector3 near = e + s.Outward[i] * 2, far = e + s.Outward[i] * 10;
                float g2 = land.Below(near.X, near.Z, e.Y + 0.05f), g10 = land.Below(far.X, far.Z, e.Y + 0.05f);
                if (float.IsFinite(g2) && float.IsFinite(g10))
                {
                    var angle = MathF.Atan((g2 - g10) / 8) + FaceMargin;
                    s.Face[i] = angle >= MathF.Atan(FaceSteepest) ? FaceSteepest : MathF.Max(1 / EmbankSlope, MathF.Tan(angle));
                }
            }
            // The weight changes slowly along the road: by at most WeightStep a station either way (a dip spreads to the
            // stations round it); the face's steepness is the mean of the five stations round each.
            for (var i = 1; i < n; i++) s.Weight[i] = MathF.Min(s.Weight[i], s.Weight[i - 1] + WeightStep);
            for (var i = n - 2; i >= 0; i--) s.Weight[i] = MathF.Min(s.Weight[i], s.Weight[i + 1] + WeightStep);
            var face = new float[n];
            for (var i = 0; i < n; i++)
            {
                float sum = 0, count = 0;
                for (var k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++)
                    if (s.Valid[k]) { sum += s.Face[k]; count++; }
                face[i] = count > 0 ? sum / count : 1 / EmbankSlope;
            }
            s.Face = face;
            // The crest's blend: by stations to the nearest one without a curb top, eased.
            for (var i = 0; i < n; i++)
            {
                if (s.CurbTop[i] is null) continue;
                var gap = CrestBlend;
                for (var d = 1; d < CrestBlend; d++)
                    if (i - d < 0 || s.CurbTop[i - d] is null || i + d >= n || s.CurbTop[i + d] is null) { gap = d; break; }
                var x = (float)gap / CrestBlend;
                s.CrestWeight[i] = x * x * (3 - 2 * x);
            }
            // How deep: down the face (from its top, past the shoulder and the rounding) until it meets the ground, and 1 m
            // into it, as far as the segment's fill profile allows (deeper: NaN, a bridge); land higher than the edge just
            // outside it: the least fill. (From the edge itself, a steep face found the land still high under the road at
            // once, and its fill hung in the air where the hillside fell away under it.)
            for (var i = 0; i < n; i++)
            {
                // (The None fill profile: never a fill.)
                if (!s.Valid[i] || maxDepth[i] < 0.5f) continue;
                var e = s.Anchor[i];
                var probe = e + s.Outward[i] * 0.5f;
                var top = land.Top(probe.X, probe.Z);
                if (float.IsFinite(top) && top > e.Y + 0.05f) { s.Depth[i] = 1; continue; }
                // A bridge where the ground right next to the edge (where the face starts) lies deeper than the segment's
                // fill profile allows; otherwise the fill reaches as deep as its face needs to meet the ground (Reach). The
                // same down to any depth (Hillside): for a road along a hillside, its other edge touching the land.
                (s.Depth[i], var upright) = Reach(i, MathF.Min(maxDepth[i], MaxDam));
                // (Hillside: straight down next to the road to the ground there, however deep; followed down the face, it met
                // a falling hillside tens of metres deep.)
                var beside = e + s.Outward[i] * FaceStart;
                var under = land.Below(beside.X, beside.Z, e.Y + 0.05f);
                if (float.IsFinite(under) && e.Y - under <= MaxDam) (s.Hillside[i], s.HillsideUpright[i]) = (e.Y - under + 1, true);
                if (upright) s.Face[i] = FaceSteepest;
            }
            return s;

            (float Depth, bool Upright) Reach(int i, float limit)
            {
                var e = s.Anchor[i];
                var start = e + s.Outward[i] * FaceStart;
                var next = land.Below(start.X, start.Z, e.Y + 0.05f);
                if (float.IsFinite(next) && e.Y - next > limit) return (float.NaN, false);
                for (var t = FaceStart; (t - FaceStart) * s.Face[i] <= limit; t += 0.5f)
                {
                    var q = e + s.Outward[i] * t;
                    var ground = land.Below(q.X, q.Z, e.Y + 0.05f);
                    // (No land under the edge's height there: either it rises over it (the least fill, as next to the edge)
                    // or there is none at all.)
                    if (float.IsNaN(ground)) return land.Top(q.X, q.Z) > e.Y ? (1, false) : (float.NaN, false);
                    if (ground >= e.Y - FaceTop - (t - FaceStart) * s.Face[i]) return (MathF.Max(0, e.Y - ground) + 1, false);
                }
                // Not met within that (a hillside falling away faster than the face): straight down next to the road to the
                // ground there, filling only the gap between the road and the hillside beside it (followed down, the face
                // met it tens of metres deep).
                return float.IsFinite(next) ? (e.Y - next + 1, true) : (float.NaN, false);
            }
        }

        /// <summary>
        /// This edge's part of a fill's section (amount: its depth; ramp: how much of it, at a stretch's ramped ends): the
        /// point behind the slab under the edge (where the section runs on under the road), and from the edge out: the
        /// shoulder carrying the road's surface on (FillShoulder, less as the side fades), rounded into the face, the face's
        /// foot (a under the anchor). At the high edge of a steep bank: over the curb's top from its inner corner to a crest
        /// CrestOver over the higher of its top corners and CrestOut past the outer one, and down at EmbankSlope (the owner's
        /// profile).
        /// </summary>
        public (Vector3 Under, Vector3[] Side) Part(int k, float amount, float ramp)
        {
            var f = MathF.Max(0.05f, ramp * Weight[k]);
            var a = (MathF.Max(1, amount) - 1) * f + 1;
            Vector3 o = Outward[k], u = Vector3.UnitY;
            var e = Anchor[k] - u * ((MathF.Max(1, amount) - 1) * (1 - f));
            var behindSlab = e + Inward[k] * 0.02f + Behind[k] * UnderSlab;
            Vector3[]? crestSide = null;
            if (CurbTop[k] is { } top)
            {
                var crest = top.Outer + o * CrestOut + u * (MathF.Max(top.Inner.Y - top.Outer.Y, 0) + CrestOver);
                var slope = Vector3.Normalize(o * EmbankSlope - u);
                var drop = crest.Y - e.Y + a;
                var crestFoot = crest + o * (drop * EmbankSlope) - u * drop;
                // (As many points as the rounded side below, so that both can follow each other along one fill; where the
                // fill's end bows in, shrunk towards the curb's inner corner with it.)
                Vector3 In(Vector3 q) => top.Inner + (q - top.Inner) * f;
                // The crest itself rounded (the owner's wish): a curve from CrestRound before it, over it, to as far down
                // the slope, its control a little over the crest so that the curve still clears the curb's top.
                var rise = crest - top.Inner;
                var before = crest - (rise.LengthSquared() > 1e-6f ? Vector3.Normalize(rise) : o) * MathF.Min(CrestRound, rise.Length() * 0.8f);
                var after = crest + slope * CrestRound;
                var control = crest + u * (CrestRound * 0.25f);
                Vector3 Over(float q) => (1 - q) * (1 - q) * before + 2 * q * (1 - q) * control + q * q * after;
                crestSide = [top.Inner, In(before), In(Over(1 / 3f)), In(Over(2 / 3f)), In(after),
                    In(Vector3.Lerp(after, crestFoot, 0.5f)), In(crestFoot)];
                if (CrestWeight[k] >= 1) return (behindSlab, crestSide);
            }
            var down = Vector3.Normalize(o - u * Face[k]);
            var sh = Vector3.Normalize(Shoulder[k]);
            // The corner where the shoulder meets the face, rounded (the owner's profiles; one cut corner stood out from an
            // upright wall as a ledge). On a gentle face a curve from FillRounding before the corner to as far down the
            // face; on a steep one a rounded nose standing out over the face below it (Nose), the face running back from
            // it to its foot; in between, blended.
            var corner = e + Shoulder[k] * (FillShoulder * f);
            var t = FillRounding * f;
            Vector3 from = corner - sh * t, to = corner + down * t;
            Vector3 Round(float q) => (1 - q) * (1 - q) * from + 2 * q * (1 - q) * corner + q * q * to;
            Vector3[] round = [from, Round(0.25f), Round(0.5f), Round(0.75f), to];
            var outward = sh - down * Vector3.Dot(sh, down);
            outward = outward.LengthSquared() > 1e-6f ? Vector3.Normalize(outward) : o;
            var steep = Math.Clamp((Face[k] - NoseFrom) / (NoseFull - NoseFrom), 0, 1);
            for (var j = 0; j < round.Length; j++)
            {
                var (along, outOf, downOf) = Nose[j];
                var nose = corner + (sh * along + outward * outOf + down * downOf) * f;
                round[j] = Vector3.Lerp(round[j], nose, steep);
            }
            // Down the face to the fill's depth (a under the anchor), at least a little: on the face's line from the corner.
            var depthY = e.Y - a;
            var foot = corner + down * (MathF.Max(0, corner.Y - depthY) / -down.Y);
            if (foot.Y > round[^1].Y - 0.1f) foot = round[^1] + down * 0.1f;
            Vector3[] side = [e, .. round, foot];
            // (Where the crest starts or ends along the road, blended from the rounded side into it point by point.)
            if (crestSide is not null)
                for (var j = 0; j < side.Length; j++) side[j] = Vector3.Lerp(side[j], crestSide[j], CrestWeight[k]);
            return (behindSlab, side);
        }
    }

    /// <summary>How many points one side of a fill's section has (see FillSide.Part).</summary>
    private const int SidePoints = 7;

    /// <summary>How many sections a swept piece of free space or fill spans, and how many it shares with the next.</summary>
    private const int PieceSections = 16, PieceOverlap = 2;

    /// <summary>
    /// Sections along a road in pieces of PieceSections + 1, each sharing PieceOverlap + 1 with the next (round: the last
    /// ones run on past the first section). A piece is too short to overlap itself, so each makes a valid solid; the
    /// boolean takes overlapping solids (subtracted or added one by one) as they are, while one solid overlapping itself
    /// (a road passing near itself) left the land in the overlap uncut.
    /// </summary>
    internal static IEnumerable<List<Vector3[]>> Pieces(List<Vector3[]> sections, bool round)
    {
        var count = round ? sections.Count - 1 : sections.Count;
        if (count < 2) yield break;
        const int step = PieceSections - PieceOverlap;
        if (!round)
        {
            for (var start = 0; ; start += step)
            {
                var end = Math.Min(start + PieceSections, count - 1);
                yield return sections.GetRange(start, end - start + 1);
                if (end == count - 1) yield break;
            }
        }
        for (var start = 0; start < count; start += step)
            yield return [.. Enumerable.Range(start, Math.Min(PieceSections, count) + 1).Select(k => sections[k % count])];
    }

    /// <summary>Sections (each the same number of points, a closed outline) swept into a closed solid, its ends fans round their middles, facing out.</summary>
    private static (List<Vector3>, List<int>) Sweep(List<Vector3[]> sections)
    {
        var p = sections[0].Length;
        var count = sections.Count;
        var positions = new List<Vector3>();
        var indices = new List<int>();
        foreach (var sec in sections) positions.AddRange(sec);
        for (var k = 0; k + 1 < count; k++)
            for (var j = 0; j < p; j++)
            {
                int a = k * p + j, b = k * p + (j + 1) % p, c = (k + 1) * p + (j + 1) % p, d = (k + 1) * p + j;
                indices.AddRange([a, b, c, a, c, d]);
            }
        foreach (var (k, flip) in new[] { (0, true), (count - 1, false) })
        {
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
        var volume = 0.0;
        for (var t = 0; t + 2 < indices.Count; t += 3) volume += Vector3.Dot(positions[indices[t]], Vector3.Cross(positions[indices[t + 1]], positions[indices[t + 2]]));
        if (volume < 0)
            for (var t = 0; t + 2 < indices.Count; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        return (positions, indices);
    }

    /// <summary>How deep a pit under a hole in the road reaches below the hole's lowest point on the driving surface, metres.</summary>
    private const float PitDepth = 2;

    /// <summary>
    /// The pits under the roads' holes: each hole's outline on the driving surface straight down to a level floor
    /// PitDepth under its lowest point, and up a metre over its highest (the free space takes the land above). Taken out of
    /// the land like a free space: through a hole the land showed the free space's floor just under the road, bent with
    /// it and simplified into fans.
    /// </summary>
    internal static List<(List<Vector3> Positions, List<int> Indices)> Pits(TrackMap map, MeshQuality quality)
    {
        var result = new List<(List<Vector3>, List<int>)>();
        foreach (var track in map.Tracks)
        {
            if (!track.Objects.Any(o => o.Kind == TrackObjectKind.Hole)) continue;
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(quality));
            if (rings.Count < 2) continue;
            foreach (var hole in TrackObjects.Holes(track, rings, TrackSampler.SegmentStarts(track)))
            {
                var outline = hole.Points.Select(q =>
                {
                    var (point, _) = TrackObjects.SurfacePoint(TrackObjects.RingAt(rings, q.D), q.S);
                    return new Vector3((float)point.X, (float)point.Y, (float)point.Z);
                }).ToList();
                if (outline.Count < 3) continue;
                float bottom = outline.Min(q => q.Y) - PitDepth, top = outline.Max(q => q.Y) + 1;
                result.Add(Sweep([[.. outline.Select(q => q with { Y = bottom })], [.. outline.Select(q => q with { Y = top })]]));
            }
        }
        return result;
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
                foreach (var j in sectionPoints <= 2 * SidePoints + 4 ? Enumerable.Range(0, sectionPoints).ToArray() : new[] { 0, (sectionPoints - 12) / 2, sectionPoints - 12, sectionPoints - 6 })
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

    /// <summary>
    /// Marks on the solids' points (their fourth property; the library carries it to the points of the result): the
    /// land, its closing walls and floor, the roads, and the free space's deck under a segment without underside
    /// (Floor: below Land, so that between the two it never reads as Closing).
    /// </summary>
    private const float Land = 0, Closing = 1, Road = 2, Floor = -1;

    /// <summary>
    /// How deep under the driving surface the roads' rooms reach for the voxel land (metres, TMSPLINE_BOOL_CUT): where
    /// its cells are made small round them (VoxelTerrain), clear of the slab's underside.
    /// </summary>
    public static float DeckCut { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_BOOL_CUT"), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.5f;

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

        var split = MakeManifold(land);
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

        if (Stats) Console.WriteLine($"  [boolean] folded triangles in the land before the cut: {Folded(mesh)}");
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
        // (The solids the land is cut by and grown with, for the dev server's volume view: LastSolids.)
        var kept = new List<(string Kind, float[] Positions, uint[] Tris)>();
        void Keep(string kind, ManifoldLib.Solid solid)
        {
            if (!Stats && !KeepSolids) return;
            var m = solid.ToMesh();
            var positions = new float[m.VertexCount * 3];
            for (var v = 0; v < m.VertexCount; v++) (positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]) = (m.Props[v * m.NumProp], m.Props[v * m.NumProp + 1], m.Props[v * m.NumProp + 2]);
            kept.Add((kind, positions, m.Tris));
        }
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
                Keep("road", solid);
            }
            var roadCount = solids.Count - 1;
            var freeIds = new HashSet<uint>();
            var volumeLines = new List<float>();
            var rooms = StitchedTerrain.RoomSections(grid, map, FreeUnder);
            var backs = new List<List<Vector3[]?>>();
            foreach (var (fp, fi, ff) in FreeSpaces(rooms, backs))
            {
                var sectionPoints = StitchedTerrain.RoomRailRight + 1 + VaultPoints;
                var sectionCount = fp.Count / sectionPoints;
                var freeMesh = ToMesh(fp, fi, Land);
                for (var v = 0; v < ff.Count; v++) if (ff[v]) freeMesh.Props[4 * v + 3] = Floor;
                using var made2 = ManifoldLib.Solid.Of(freeMesh);
                DrawVolume(volumeLines, fp, sectionPoints, sectionCount, made2.Status == ManifoldLib.Error.NoError ? 10 : 11);
                if (made2.Status != ManifoldLib.Error.NoError) { Console.WriteLine($"  [boolean] a road's free space is invalid ({made2.Status}): not cut out"); continue; }
                var free = made2.AsOriginal();
                freeIds.Add((uint)free.OriginalId);
                solids.Add(free);
                Keep("free", free);
            }
            // The pits under the roads' holes, taken out as free spaces (their faces are the land's).
            foreach (var (pp, pi) in Pits(map, quality))
            {
                using var madeP = ManifoldLib.Solid.Of(ToMesh(pp, pi, Land));
                DrawVolume(volumeLines, pp, pp.Count / 2 - 1, 2, madeP.Status == ManifoldLib.Error.NoError ? 10 : 11);
                if (madeP.Status != ManifoldLib.Error.NoError) { Console.WriteLine($"  [boolean] a hole's pit is invalid ({madeP.Status}): not cut out"); continue; }
                var pit = madeP.AsOriginal();
                freeIds.Add((uint)pit.OriginalId);
                solids.Add(pit);
                Keep("free", pit);
            }
            // Embankments beside the roads (land added up to a road's edge; land over it the free spaces cut back).
            var fillIds = new HashSet<uint>();
            if (Environment.GetEnvironmentVariable("TMSPLINE_BOOL_EMBANK") != "off")
            {
                var heights = new LandHeights(mesh);
                var fills = Embankments(RoadStations(map, quality), heights);
                var fillSolids = new List<ManifoldLib.Solid>();
                // (The land wrapped round roads without underside.)
                if (backs.Any(run => run.Any(s => s is not null)) && BedFills(backs, mesh, volumeLines) is { } backFill)
                {
                    fillIds.Add((uint)backFill.OriginalId);
                    fillSolids.Add(backFill);
                    Keep("abutment", backFill);
                    Time("back fills");
                }
                foreach (var (ep, ei, sectionPoints) in fills)
                {
                    using var madeE = ManifoldLib.Solid.Of(ToMesh(ep, ei, Land));
                    DrawVolume(volumeLines, ep, sectionPoints, (ep.Count - 2) / sectionPoints, madeE.Status == ManifoldLib.Error.NoError ? 12 : 11);
                    if (madeE.Status != ManifoldLib.Error.NoError) continue;
                    var e = madeE.AsOriginal();
                    fillIds.Add((uint)e.OriginalId);
                    fillSolids.Add(e);
                    Keep("fill", e);
                }
                if (fillSolids.Count > 0)
                {
                    // The land with its embankments, as one (each keeps its id).
                    var grown = ManifoldLib.Solid.Batch(fillSolids.Prepend(ground), ManifoldLib.Op.Add);
                    foreach (var f in fillSolids) f.Dispose();
                    if (grown.Status == ManifoldLib.Error.NoError) solids[0] = grown;
                    else { Console.WriteLine($"  [boolean] the embankments could not be added ({grown.Status})"); grown.Dispose(); }
                }
                Time($"embankments: {fillIds.Count} fills");
            }
            LastVolumes = [.. volumeLines];
            LastSolids = [.. kept];
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
            // (For the relaxing: the vertices the roads' faces have, where the land meets a road, and the land's
            // vertices on the free spaces' walls, which stay where the cut put them.)
            var roadCorners = new HashSet<uint>();
            var wallVertices = new HashSet<int>();
            for (var r = 0; r + 1 < output.RunIndex.Length; r++)
            {
                // (The free spaces' faces left are walls of land cut away: the land's.)
                var id = output.RunOriginalId[r];
                var isLand = id == landId || freeIds.Contains(id) || fillIds.Contains(id);
                var isWall = freeIds.Contains(id);
                for (var t = (int)output.RunIndex[r]; t + 2 < (int)output.RunIndex[r + 1]; t += 3)
                {
                    if (!isLand)
                    {
                        fromRoads++;
                        for (var q = 0; q < 3; q++) roadCorners.Add(same.GetValueOrDefault(output.Tris[t + q], output.Tris[t + q]));
                        continue;
                    }
                    uint a = output.Tris[t], b = output.Tris[t + 1], c = output.Tris[t + 2];
                    if (Mark(output, a) > 0.5f || Mark(output, b) > 0.5f || Mark(output, c) > 0.5f) { fromClosing++; continue; }
                    foreach (var corner in new[] { a, b, c })
                    {
                        var v = same.GetValueOrDefault(corner, corner);
                        // (The land's floor under a road without underside stays where the cut put it, just under the
                        // road's surface: simplified like the rest, it rose through the road.)
                        if (!at.TryGetValue(v, out var i)) { i = cut.Positions.Count; cut.Positions.Add(output.Position((int)v)); cut.Pinned.Add(Mark(output, v) < -0.5f); at[v] = i; }
                        cut.Indices.Add(i);
                        if (isWall) wallVertices.Add(i);
                    }
                }
            }
            // (Carried through the simplifying: which vertices the land meets the roads with, and which lie on the free
            // spaces' walls.)
            var carry = new List<bool[]> { new bool[cut.Positions.Count], new bool[cut.Positions.Count], cut.Pinned.ToArray() };
            foreach (var v in roadCorners) if (at.TryGetValue(v, out var i)) carry[0][i] = true;
            foreach (var i in wallVertices) carry[1][i] = true;
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
            // Small loose pieces (land left between a road and its free space, cut off from the rest) left out: the
            // roads are whole, so no hole opens where they were.
            var dropped = DropLoose(cut, SmallPiece);
            if (Stats) Console.WriteLine($"  [boolean] left out {dropped} loose pieces of land under {SmallPiece} m2");
            // The land along the seam simplified: its cut ran through every road face it crossed, a vertex wherever it
            // crossed one of their edges. Seam vertices on a straight line between their neighbours (within
            // SeamTolerance) go, then the land inside as the land was simplified before.
            var before = cut.Indices.Count / 3;
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after the cut: {Folded(cut)}");
            cut = MeshSimplifier.Simplify(cut, 0, VoxelTerrain.SimplifyError, borderTolerance: SeamTolerance, carry: carry);
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after simplifying: {Folded(cut)}");
            MeshSimplifier.ImproveByFlips(cut, 35);
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after flips: {Folded(cut)}");
            if (Stats)
            {
                var (o2, ov2, fl2, _) = Topology(cut.Indices);
                Console.WriteLine($"  [boolean] simplified {before} land triangles to {cut.Indices.Count / 3}: {o2} open edges, {ov2} shared by 3+, {fl2} run the same way");
            }
            Time("simplified");
            if (carry[0].Length == cut.Positions.Count)
            {
                // (Held: the floor under roads without underside, and all land between a road's edges, seen from
                // above, near its height: relaxed, it came up through the road.)
                bool[] Hold()
                {
                    var h = Between(rooms, cut.Positions);
                    for (var i = 0; i < h.Length; i++) h[i] |= carry[2][i];
                    return h;
                }
                // The land near the roads remeshed first (its triangles made even: the cut's slivers and crowds of
                // vertices at a crease's tip gone, which the relaxing could not move), all but the roads' edges, the
                // land held and the land further than RelaxFar from the roads.
                {
                    var trianglesWere = cut.Indices.Count / 3;
                    var hold0 = Hold();
                    var along = AlongLand(cut, carry[0], RelaxFar);
                    var keep = new bool[cut.Positions.Count];
                    for (var i = 0; i < keep.Length; i++) keep[i] = carry[0][i] || hold0[i] || !(along[i] < RelaxFar);
                    // (And the creases, edges where the land bends by more than RemeshCrease: collapsed across one, and put
                    // back onto the land on its other side, the triangles there turned over (a notch one saw the road through).)
                    var creases = 0;
                    {
                        var faceOf = new Dictionary<long, int>(EdgeKeyComparer.Instance);
                        var cosLimit = MathF.Cos(RemeshCrease * MathF.PI / 180);
                        Vector3 FaceN(int t) => Norm3(Vector3.Cross(cut.Positions[cut.Indices[t + 1]] - cut.Positions[cut.Indices[t]], cut.Positions[cut.Indices[t + 2]] - cut.Positions[cut.Indices[t]]));
                        for (var t = 0; t + 2 < cut.Indices.Count; t += 3)
                            for (var k = 0; k < 3; k++)
                            {
                                int a = cut.Indices[t + k], b = cut.Indices[t + (k + 1) % 3];
                                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                                if (!faceOf.Remove(key, out var other)) { faceOf[key] = t; continue; }
                                var (na, nb) = (FaceN(t), FaceN(other));
                                if (na == Vector3.Zero || nb == Vector3.Zero || Vector3.Dot(na, nb) >= cosLimit) continue;
                                if (!keep[a] || !keep[b]) creases++;
                                keep[a] = keep[b] = true;
                            }
                    }
                    var roadAt = new HashSet<Vector3>(Enumerable.Range(0, cut.Positions.Count).Where(i => carry[0][i]).Select(i => cut.Positions[i]));
                    var flags = new List<bool[]> { carry[1], carry[2] };
                    // (Back onto the land as it was after every move: unprojected, a vertex slid along its averaged normal's
                    // plane off a crease (the cut's line of slivers) and folded the triangles beside it; relaxed, a groove.)
                    cut = MeshRemesher.Remesh(cut, Nearest(cut, keep), rounds: 3, keep: keep, carry: flags);
                    carry = new List<bool[]> { cut.Positions.Select(roadAt.Contains).ToArray(), flags[0], flags[1] };
                    if (Stats) Console.WriteLine($"  [boolean] remeshed near the roads: {trianglesWere} land triangles to {cut.Indices.Count / 3} ({creases} crease edges kept); {Topology(cut.Indices)}");
                    Time("remeshed near the roads");
                }
                var foldedBefore = Stats ? Folded(cut) : 0;
                var moved = Relax(cut, carry[0], carry[1], Hold());
                // (Stats: holes, open edges neither along a road nor at the land's own border, grouped by place.)
                if (Stats)
                {
                    var use = new Dictionary<long, int>(EdgeKeyComparer.Instance);
                    for (var t = 0; t + 2 < cut.Indices.Count; t += 3)
                        for (var k = 0; k < 3; k++)
                        {
                            int a = cut.Indices[t + k], b = cut.Indices[t + (k + 1) % 3];
                            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                            use[key] = use.GetValueOrDefault(key) + 1;
                        }
                    // (Only near the roads, within 30 m of one: further out the land's own edge.)
                    var nearRoad = new HashSet<(int, int)>();
                    for (var i = 0; i < cut.Positions.Count; i++)
                        if (carry[0][i])
                            for (var dx = -1; dx <= 1; dx++)
                                for (var dz = -1; dz <= 1; dz++)
                                    nearRoad.Add(((int)MathF.Floor(cut.Positions[i].X / 30) + dx, (int)MathF.Floor(cut.Positions[i].Z / 30) + dz));
                    var holes = new Dictionary<(int, int), (int Count, Vector3 At)>();
                    foreach (var (key, u) in use)
                    {
                        if (u == 2) continue;
                        int a = (int)(key >> 32), b = (int)(uint)key;
                        if (carry[0][a] && carry[0][b]) continue;
                        var mid = (cut.Positions[a] + cut.Positions[b]) / 2;
                        if (!nearRoad.Contains(((int)MathF.Floor(mid.X / 30), (int)MathF.Floor(mid.Z / 30)))) continue;
                        var cellKey = ((int)MathF.Floor(mid.X / 8), (int)MathF.Floor(mid.Z / 8));
                        var had = holes.GetValueOrDefault(cellKey);
                        holes[cellKey] = (had.Count + 1, had.Count == 0 ? mid : had.At);
                    }
                    Console.WriteLine($"  [boolean] holes: {holes.Values.Sum(h => h.Count)} open edges off the roads in {holes.Count} places" + string.Concat(holes.Values.OrderByDescending(h => h.Count).Take(12).Select(h => $"; {h.Count} at ({h.At.X:0.0}, {h.At.Y:0.0}, {h.At.Z:0.0})")));
                }
                if (Stats) Console.WriteLine($"  [boolean] relaxed {moved} land vertices near the roads ({carry[0].Count(x => x)} on the roads, {carry[1].Count(x => x)} on free space walls only into the land); folded before {foldedBefore}, after {Folded(cut)}");
                Time("relaxed");
            }
            return cut;
        }
        finally
        {
            foreach (var s in solids) if (!ReferenceEquals(s, ground)) s.Dispose();
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

    /// <summary>
    /// The land made manifold where the voxel mesher left sheets touching (land thinner than a cell: one vertex for both
    /// its sides): at an edge three or more triangles share, they are sorted round it and each joined to its neighbour
    /// that runs the other way (the sheets paired up); a pair of triangles running the same way (one flipped) is not
    /// joined. Then each vertex is split into one per fan of triangles joined round it, the copies at the same place:
    /// nothing moves, and every edge has at most two triangles. Returns how many vertices were added.
    /// </summary>
    internal static int MakeManifold(AdaptiveMesh mesh)
    {
        var idx = mesh.Indices;
        var pos = mesh.Positions;
        var triangles = idx.Count / 3;
        // Per undirected edge its triangles.
        var edges = new Dictionary<long, List<int>>(EdgeKeyComparer.Instance);
        for (var t = 0; t < triangles; t++)
            for (var k = 0; k < 3; k++)
            {
                int a = idx[3 * t + k], b = idx[3 * t + (k + 1) % 3];
                var key = a < b ? Key(a, b) : Key(b, a);
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = [];
                list.Add(t);
            }
        bool Runs(int t, int a, int b)
        {
            for (var k = 0; k < 3; k++)
                if (idx[3 * t + k] == a && idx[3 * t + (k + 1) % 3] == b) return true;
            return false;
        }
        int Third(int t, int a, int b)
        {
            for (var k = 0; k < 3; k++)
                if (idx[3 * t + k] != a && idx[3 * t + k] != b) return idx[3 * t + k];
            return idx[3 * t];
        }
        // The joins: pairs of triangles across an edge, by corner (triangle, its corner vertex).
        var parent = new int[3 * triangles];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
        int Corner(int t, int v)
        {
            for (var k = 0; k < 3; k++)
                if (idx[3 * t + k] == v) return 3 * t + k;
            return 3 * t;
        }
        void Join(int t1, int t2, int a, int b)
        {
            Union(Corner(t1, a), Corner(t2, a));
            Union(Corner(t1, b), Corner(t2, b));
        }
        foreach (var (key, list) in edges)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            if (list.Count < 2) continue;
            if (list.Count == 2)
            {
                if (Runs(list[0], a, b) != Runs(list[1], a, b)) Join(list[0], list[1], a, b);
                continue;
            }
            // Round the edge: each triangle's angle about it.
            var axis = pos[b] - pos[a];
            if (axis.LengthSquared() < 1e-12f) continue;
            axis = Vector3.Normalize(axis);
            var refDir = Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            refDir = Vector3.Normalize(refDir);
            var other = Vector3.Cross(axis, refDir);
            var round = list.Select(t =>
            {
                var w = pos[Third(t, a, b)] - pos[a];
                w -= axis * Vector3.Dot(w, axis);
                return (T: t, Angle: MathF.Atan2(Vector3.Dot(w, other), Vector3.Dot(w, refDir)), Forward: Runs(t, a, b));
            }).OrderBy(x => x.Angle).ToList();
            // Paired neighbour by neighbour, starting where that pairs every one with one running the other way.
            for (var offset = 0; offset < 2; offset++)
            {
                if (round.Count % 2 != 0) break;
                var ok = true;
                for (var i = offset; i < round.Count + offset; i += 2)
                    if (round[i % round.Count].Forward == round[(i + 1) % round.Count].Forward) { ok = false; break; }
                if (!ok) continue;
                for (var i = offset; i < round.Count + offset; i += 2) Join(round[i % round.Count].T, round[(i + 1) % round.Count].T, a, b);
                break;
            }
        }
        // Each vertex: one copy per fan of joined corners.
        var copyOf = new Dictionary<(int Vertex, int Fan), int>();
        var firstFan = new Dictionary<int, int>();
        var added = 0;
        for (var c = 0; c < 3 * triangles; c++)
        {
            var v = idx[c];
            var fan = Find(c);
            if (!firstFan.TryGetValue(v, out var f0)) { firstFan[v] = fan; continue; }
            if (f0 == fan) continue;
            if (!copyOf.TryGetValue((v, fan), out var copy))
            {
                copy = pos.Count;
                pos.Add(pos[v]);
                mesh.Pinned.Add(v < mesh.Pinned.Count && mesh.Pinned[v]);
                copyOf[(v, fan)] = copy;
                added++;
            }
            idx[c] = copy;
        }
        return added;
    }

    /// <summary>An edge's key (its two vertices).</summary>
    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    /// <summary>Debug: triangles facing against a neighbour across an edge (folded over), counted.</summary>
    private static int Folded(AdaptiveMesh mesh)
    {
        var idx = mesh.Indices;
        var normals = new Vector3[idx.Count / 3];
        for (var t = 0; t < normals.Length; t++)
        {
            var n = Vector3.Cross(mesh.Positions[idx[3 * t + 1]] - mesh.Positions[idx[3 * t]], mesh.Positions[idx[3 * t + 2]] - mesh.Positions[idx[3 * t]]);
            normals[t] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero;
        }
        var byEdge = new Dictionary<long, int>(EdgeKeyComparer.Instance);
        var folded = new HashSet<int>();
        for (var t = 0; t < normals.Length; t++)
            for (var k = 0; k < 3; k++)
            {
                int a = idx[3 * t + k], b = idx[3 * t + (k + 1) % 3];
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (byEdge.TryGetValue(key, out var other)) { if (Vector3.Dot(normals[t], normals[other]) < -0.3f) { folded.Add(t); folded.Add(other); } }
                else byEdge[key] = t;
            }
        return folded.Count;
    }

    /// <summary>The relaxing near the roads: fully up to RelaxNear from a road's edge (metres along the land; the edge itself
    /// held), fading out to RelaxFar; RelaxSteps rounds.</summary>
    private const float RemeshCrease = 35;
    private const float RelaxNear = 15, RelaxFar = 25, RelaxMaxShift = 1, RelaxHoldFade = 2, RelaxStep = 0.1f;
    private const int RelaxSteps = 15;

    /// <summary>
    /// The land relaxed near the roads after the cut: its creases (where a skirt or the bed meets the land) rounded.
    /// Each round moves every vertex towards the middle of its neighbours, only along its normal (so the triangles keep
    /// their spread and do not fold), then back a little (Taubin's two steps: the land does not shrink). Fully near the
    /// roads, fading out further away; none at their edge (the vertices the land meets them with stay), between their
    /// edges (the land they cover), on the floor under them, on the land's open edges or at a place two vertices
    /// share; on the free spaces' walls only into the land (tunnels kept round, the road's room kept clear).
    /// Returns how many vertices could move.
    /// </summary>
    private static int Relax(AdaptiveMesh mesh, bool[] road, bool[] held, bool[] hold)
    {
        if (RelaxSteps <= 0) return 0;
        var relaxClock = System.Diagnostics.Stopwatch.StartNew();
        var pos = mesh.Positions;
        var idx = mesh.Indices;
        var n = pos.Count;
        // Edges, and how many triangles share each (one: an open edge).
        var edgeUse = new Dictionary<long, int>(EdgeKeyComparer.Instance);
        for (var t = 0; t + 2 < idx.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = idx[t + k], b = idx[t + (k + 1) % 3];
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        var degree = new int[n + 1];
        foreach (var key in edgeUse.Keys) { degree[(int)(key >> 32) + 1]++; degree[(int)(uint)key + 1]++; }
        for (var i = 0; i < n; i++) degree[i + 1] += degree[i];
        var near = new int[degree[n]];
        var fill = (int[])degree.Clone();
        var fixedHere = new bool[n];
        foreach (var (key, use) in edgeUse)
        {
            int a = (int)(key >> 32), b = (int)(uint)key;
            near[fill[a]++] = b;
            near[fill[b]++] = a;
            if (use != 2) fixedHere[a] = fixedHere[b] = true;
        }
        for (var i = 0; i < n; i++) if (road[i]) fixedHere[i] = true;
        for (var i = 0; i < n; i++) if (hold[i]) fixedHere[i] = true;
        foreach (var g in Enumerable.Range(0, n).GroupBy(i => pos[i]))
            if (g.Skip(1).Any()) foreach (var i in g) fixedHere[i] = true;

        // Distance along the land to the roads (from the vertices the land meets them with), up to RelaxFar.
        var dist = new float[n];
        Array.Fill(dist, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        for (var i = 0; i < n; i++) if (road[i]) { dist[i] = 0; queue.Enqueue(i, 0); }
        while (queue.TryDequeue(out var i, out var d))
        {
            if (d > dist[i] || d > RelaxFar) continue;
            for (var e = degree[i]; e < degree[i + 1]; e++)
            {
                var j = near[e];
                var dj = d + Vector3.Distance(pos[i], pos[j]);
                if (dj < dist[j]) { dist[j] = dj; queue.Enqueue(j, dj); }
            }
        }
        // (And fading in over RelaxHoldFade from the land held, between the roads' edges and under them: held right next to
        // land moved metres, the two folded into a slot one saw through.)
        var holdSeeds = new bool[n];
        for (var i = 0; i < n; i++) holdSeeds[i] = hold[i] && !road[i];
        var fromHold = AlongLand(mesh, holdSeeds, RelaxHoldFade);
        var weight = new float[n];
        var moving = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (fixedHere[i] || !float.IsFinite(dist[i])) continue;
            weight[i] = (1 - SmoothStep(RelaxNear, RelaxFar, dist[i])) * SmoothStep(0, RelaxHoldFade, fromHold[i]);
            if (weight[i] > 0) moving.Add(i);
        }
        if (moving.Count == 0) return 0;

        // Per vertex its triangles, and each triangle's facing before: a move that would turn one of a vertex's
        // triangles over (more than about 85 degrees from how it faced) is halved, up to three times, else not made.
        var triStart = new int[n + 1];
        for (var t = 0; t < idx.Count; t++) triStart[idx[t] + 1]++;
        for (var i = 0; i < n; i++) triStart[i + 1] += triStart[i];
        var tris = new int[idx.Count];
        var triFill = (int[])triStart.Clone();
        for (var t = 0; t < idx.Count; t++) tris[triFill[idx[t]]++] = t / 3;
        Vector3 Face(int t) => Vector3.Cross(pos[idx[3 * t + 1]] - pos[idx[3 * t]], pos[idx[3 * t + 2]] - pos[idx[3 * t]]);
        var facing = new Vector3[idx.Count / 3];
        // (Triangles under 1 cm2 not checked: slivers of the cut, turned over at the slightest move, unseen; checked, the
        // vertices round them, a crease's tip, hardly moved.)
        for (var t = 0; t < facing.Length; t++) { var f = Face(t); facing[t] = f.Length() < 2e-4f ? Vector3.Zero : Norm3(f); }
        // (And no move folds two neighbouring triangles against each other: how many folds are round a vertex.)
        var across = new int[idx.Count];
        {
            var byEdge = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            Array.Fill(across, -1);
            for (var t = 0; t < idx.Count; t++)
            {
                int a = idx[t], b = idx[t - t % 3 + (t % 3 + 1) % 3];
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (byEdge.Remove(key, out var other)) { across[t] = other; across[other] = t; }
                else byEdge[key] = t;
            }
        }
        int FoldsAt(int i)
        {
            var folds = 0;
            for (var e = triStart[i]; e < triStart[i + 1]; e++)
            {
                var t = tris[e];
                if (facing[t] == Vector3.Zero) continue;
                var nt = Norm3(Face(t));
                for (var k = 0; k < 3; k++)
                    if (across[3 * t + k] is var o and >= 0 && facing[o / 3] != Vector3.Zero && Vector3.Dot(nt, Norm3(Face(o / 3))) < -0.3f) folds++;
            }
            return folds;
        }
        bool Keeps(int i)
        {
            for (var e = triStart[i]; e < triStart[i + 1]; e++)
            {
                var t = tris[e];
                if (facing[t] == Vector3.Zero) continue;
                var f = Face(t);
                if (Vector3.Dot(f, facing[t]) < 0.08f * f.Length()) return false;
            }
            return true;
        }
        // Vertices joined by edges under 1 cm (the cut's crowds at a crease's tip) moved as one: moved one by one, the
        // slivers between them turned over and they hardly moved. A group moves where all of its vertices may.
        var group = new int[n];
        for (var i = 0; i < n; i++) group[i] = i;
        int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
        for (var i = 0; i < n; i++)
            for (var e = degree[i]; e < degree[i + 1]; e++)
                if (Vector3.DistanceSquared(pos[i], pos[near[e]]) < 1e-4f) group[Root(i)] = Root(near[e]);
        var members = new Dictionary<int, List<int>>();
        foreach (var i in moving) { var r = Root(i); if (!members.TryGetValue(r, out var l)) members[r] = l = []; l.Add(i); }
        var groups = members.Values.Where(l => l.All(i => Root(i) == Root(l[0]))).ToList();
        // (A group with a vertex that may not move stays.)
        var groupSize = new Dictionary<int, int>();
        for (var i = 0; i < n; i++) { var r = Root(i); groupSize[r] = groupSize.GetValueOrDefault(r) + 1; }
        groups = groups.Where(l => groupSize[Root(l[0])] == l.Count).ToList();
        if (Stats) Console.WriteLine($"  [boolean] relax setup: {relaxClock.ElapsedMilliseconds} ms ({groups.Count} groups)");
        relaxClock.Restart();
        var normals = new Vector3[n];
        var startPos = pos.ToArray();
        var startNormal = new Vector3[n];
        for (var t = 0; t + 2 < idx.Count; t += 3)
        {
            var c = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
            startNormal[idx[t]] += c; startNormal[idx[t + 1]] += c; startNormal[idx[t + 2]] += c;
        }
        for (var i = 0; i < n; i++) startNormal[i] = Norm3(startNormal[i]);
        for (var step = 0; step < 2 * RelaxSteps; step++)
        {
            var factor = step % 2 == 0 ? 0.5f : -0.53f;
            Array.Clear(normals);
            for (var t = 0; t + 2 < idx.Count; t += 3)
            {
                var c = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
                normals[idx[t]] += c; normals[idx[t + 1]] += c; normals[idx[t + 2]] += c;
            }
            foreach (var g in groups)
            {
                var root = Root(g[0]);
                Vector3 nsum = Vector3.Zero, centre = Vector3.Zero, avg = Vector3.Zero;
                var count = 0;
                var w = 1f;
                var wall = false;
                foreach (var i in g)
                {
                    nsum += normals[i];
                    centre += pos[i];
                    w = MathF.Min(w, weight[i]);
                    wall |= held[i];
                    for (var e = degree[i]; e < degree[i + 1]; e++)
                        if (Root(near[e]) != root) { avg += pos[near[e]]; count++; }
                }
                if (count == 0) continue;
                var nm = Norm3(nsum);
                centre /= g.Count;
                avg /= count;
                // (At most RelaxStep a step: the move grows with the edges round a vertex, and next to the remeshed land's
                // larger triangles a crease's vertices went down metres where their neighbours along the road did not.)
                var along = Math.Clamp(w * factor * Vector3.Dot(avg - centre, nm), -RelaxStep, RelaxStep);
                if (along == 0) continue;
                var was = g.Select(i => pos[i]).ToArray();
                var foldsWere = g.Sum(FoldsAt);
                for (var tries = 0; tries < 4; tries++, along /= 2)
                {
                    for (var q = 0; q < g.Count; q++) pos[g[q]] = was[q] + nm * along;
                    if (g.All(Keeps) && g.Sum(FoldsAt) <= foldsWere) break;
                    for (var q = 0; q < g.Count; q++) pos[g[q]] = was[q];
                }
            }
            // After each pair of steps: on a free space's wall only into the land from where it was (against its normal
            // then: never into the road's room), and nowhere further than RelaxMaxShift. (Held back in each step, the
            // walls only ever went in, every round, into spikes deep under the land.)
            if (step % 2 == 1)
                foreach (var g in groups)
                    foreach (var i in g)
                    {
                        var shift = pos[i] - startPos[i];
                        if (held[i] && Vector3.Dot(shift, startNormal[i]) > 0) shift -= startNormal[i] * Vector3.Dot(shift, startNormal[i]);
                        if (shift.Length() > RelaxMaxShift) shift *= RelaxMaxShift / shift.Length();
                        var at = startPos[i] + shift;
                        if (at == pos[i]) continue;
                        pos[i] = at;
                        if (!Keeps(i)) pos[i] = startPos[i];
                    }
        }
        return moving.Count;
    }

    /// <summary>The nearest point on a mesh (its triangles with a vertex not kept: where a remesh can move it), or the point
    /// itself where none lies within 3 m.</summary>
    private static Func<Vector3, Vector3> Nearest(AdaptiveMesh mesh, bool[] keep)
    {
        const float cell = 3;
        var pos = mesh.Positions.ToArray();
        var idx = mesh.Indices.ToArray();
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vector3 v) => ((int)MathF.Floor(v.X / cell), (int)MathF.Floor(v.Y / cell), (int)MathF.Floor(v.Z / cell));
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            if (keep[idx[t]] && keep[idx[t + 1]] && keep[idx[t + 2]]) continue;
            Vector3 a = pos[idx[t]], b = pos[idx[t + 1]], c = pos[idx[t + 2]];
            var (lo, hi) = (Key(Vector3.Min(a, Vector3.Min(b, c))), Key(Vector3.Max(a, Vector3.Max(b, c))));
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) grid[(x, y, z)] = l = [];
                        l.Add(t);
                    }
        }
        return p =>
        {
            var (kx, ky, kz) = Key(p);
            var best = p;
            var bestD = float.MaxValue;
            for (var x = kx - 1; x <= kx + 1; x++)
                for (var y = ky - 1; y <= ky + 1; y++)
                    for (var z = kz - 1; z <= kz + 1; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) continue;
                        foreach (var t in l)
                        {
                            var q = ClosestOnTriangle(p, pos[idx[t]], pos[idx[t + 1]], pos[idx[t + 2]]);
                            var d = Vector3.DistanceSquared(p, q);
                            if (d < bestD) { bestD = d; best = q; }
                        }
                    }
            return bestD <= cell * cell ? best : p;
        };
    }

    /// <summary>The point of triangle abc nearest to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>Per vertex its distance along the land's edges from the nearest seed, up to <paramref name="limit"/> (beyond: infinity).</summary>
    private static float[] AlongLand(AdaptiveMesh mesh, bool[] seeds, float limit)
    {
        var pos = mesh.Positions;
        var idx = mesh.Indices;
        var n = pos.Count;
        var near = new List<int>[n];
        for (var i = 0; i < n; i++) near[i] = [];
        for (var t = 0; t + 2 < idx.Count; t += 3)
            for (var k = 0; k < 3; k++) { int a = idx[t + k], b = idx[t + (k + 1) % 3]; near[a].Add(b); near[b].Add(a); }
        var dist = new float[n];
        Array.Fill(dist, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        for (var i = 0; i < n; i++) if (seeds[i]) { dist[i] = 0; queue.Enqueue(i, 0); }
        while (queue.TryDequeue(out var i, out var d))
        {
            if (d > dist[i] || d > limit) continue;
            foreach (var j in near[i])
            {
                var dj = d + Vector3.Distance(pos[i], pos[j]);
                if (dj < dist[j]) { dist[j] = dj; queue.Enqueue(j, dj); }
            }
        }
        return dist;
    }

    /// <summary>Per position whether it lies between a road's edges seen from above (between its left and right contacts,
    /// section to section), from BetweenBelow under the road to BetweenAbove over it.</summary>
    private static bool[] Between(List<(Track Track, List<Vector3[]> Sections, List<double> Distances)> rooms, List<Vector3> positions)
    {
        const float cell = 4;
        var quads = new List<(Vector2 A, Vector2 B, Vector2 C, Vector2 D, float Low, float High)>();
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var (_, sections, _) in rooms)
            for (var k = 0; k + 1 < sections.Count; k++)
            {
                Vector3 l0 = sections[k][0], r0 = sections[k][StitchedTerrain.RoomRailRight], l1 = sections[k + 1][0], r1 = sections[k + 1][StitchedTerrain.RoomRailRight];
                var low = MathF.Min(MathF.Min(l0.Y, r0.Y), MathF.Min(l1.Y, r1.Y)) - BetweenBelow;
                var high = MathF.Max(MathF.Max(l0.Y, r0.Y), MathF.Max(l1.Y, r1.Y)) + BetweenAbove;
                var q = quads.Count;
                quads.Add((new(l0.X, l0.Z), new(r0.X, r0.Z), new(r1.X, r1.Z), new(l1.X, l1.Z), low, high));
                float x0 = MathF.Min(MathF.Min(l0.X, r0.X), MathF.Min(l1.X, r1.X)), x1 = MathF.Max(MathF.Max(l0.X, r0.X), MathF.Max(l1.X, r1.X));
                float z0 = MathF.Min(MathF.Min(l0.Z, r0.Z), MathF.Min(l1.Z, r1.Z)), z1 = MathF.Max(MathF.Max(l0.Z, r0.Z), MathF.Max(l1.Z, r1.Z));
                for (var gx = (int)MathF.Floor(x0 / cell); gx <= (int)MathF.Floor(x1 / cell); gx++)
                    for (var gz = (int)MathF.Floor(z0 / cell); gz <= (int)MathF.Floor(z1 / cell); gz++)
                    {
                        if (!grid.TryGetValue((gx, gz), out var list)) grid[(gx, gz)] = list = [];
                        list.Add(q);
                    }
            }
        static float Side(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        static bool InTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            float d1 = Side(a, b, p), d2 = Side(b, c, p), d3 = Side(c, a, p);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
        var result = new bool[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var v = positions[i];
            if (!grid.TryGetValue(((int)MathF.Floor(v.X / cell), (int)MathF.Floor(v.Z / cell)), out var list)) continue;
            var p = new Vector2(v.X, v.Z);
            foreach (var q in list)
            {
                var (a, b, c, d, low, high) = quads[q];
                if (v.Y < low || v.Y > high) continue;
                if (InTriangle(a, b, c, p) || InTriangle(a, c, d, p)) { result[i] = true; break; }
            }
        }
        return result;
    }
    private const float BetweenBelow = 12, BetweenAbove = 3;

    /// <summary>Loose pieces of land smaller than this are left out (square metres).</summary>
    private const float SmallPiece = 30;

    /// <summary>Removes the pieces of the mesh (triangles joined by shared vertices) smaller than <paramref name="area"/>; how many.</summary>
    private static int DropLoose(AdaptiveMesh mesh, float area)
    {
        var idx = mesh.Indices;
        var parent = new int[mesh.Positions.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (var t = 0; t + 2 < idx.Count; t += 3)
        {
            int a = Find(idx[t]), b = Find(idx[t + 1]);
            parent[a] = b;
            parent[Find(idx[t + 2])] = Find(b);
        }
        var areas = new Dictionary<int, float>();
        for (var t = 0; t + 2 < idx.Count; t += 3)
        {
            var root = Find(idx[t]);
            var tri = Vector3.Cross(mesh.Positions[idx[t + 1]] - mesh.Positions[idx[t]], mesh.Positions[idx[t + 2]] - mesh.Positions[idx[t]]).Length() / 2;
            areas[root] = areas.GetValueOrDefault(root) + tri;
        }
        var small = areas.Where(x => x.Value < area).Select(x => x.Key).ToHashSet();
        if (small.Count == 0) return 0;
        var kept = new List<int>(idx.Count);
        for (var t = 0; t + 2 < idx.Count; t += 3)
            if (!small.Contains(Find(idx[t]))) kept.AddRange([idx[t], idx[t + 1], idx[t + 2]]);
        idx.Clear();
        idx.AddRange(kept);
        return small.Count;
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
