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

    /// <summary>
    /// The roads' free space as closed solids (step 2 of the plan): the voxel rooms' sections (StitchedTerrain, every
    /// 2 m: rail, driving surface FreeUnder under it, rail, a vault over it from rail to rail; a pipe's whole inside)
    /// swept along each road, its ends closed. Land in it is cut away exactly; its faces left become the land's there.
    /// </summary>
    internal static List<(List<Vector3> Positions, List<int> Indices)> FreeSpaces(List<(Track Track, List<Vector3[]> Sections)> rooms)
    {
        var result = new List<(List<Vector3>, List<int>)>();
        foreach (var (track, all) in rooms)
        {
            if (all.Count < 2) continue;
            var p = all[0].Length;

            foreach (var section in all)
            {
                // The rails (the rooms tuck them 3 cm in from the road's edge) moved out past it by FreeBeyond: land in
                // the strip along the edge stood over the driving surface and showed through it.
                var midPoint = section[1 + (StitchedTerrain.RoomDeckPoints - 1) / 2];
                foreach (var rail in new[] { 0, StitchedTerrain.RoomRailRight })
                {
                    var outward = section[rail] - midPoint;
                    outward.Y = 0;
                    if (outward.LengthSquared() > 1e-4f) section[rail] += Vector3.Normalize(outward) * FreeBeyond;
                }
                ContinueCurl(section);
            }
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

    /// <summary>
    /// The roads' own rings as the embankments' stations (laid out like a room's section: index 0 the left contact, 1 to
    /// RoomDeckPoints the driving surface left to right, RoomRailRight the right contact, a point over the middle after it):
    /// the same rings, at the same quality, as the roads cut out of the land, so an embankment's edge runs straight
    /// between the same points as the road's, exactly along it.
    /// </summary>
    internal static List<(Track Track, List<Vector3[]> Sections)> RoadStations(TrackMap map, MeshQuality quality)
    {
        static Vector3 W(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        var result = new List<(Track, List<Vector3[]>)>();
        const int deck = StitchedTerrain.RoomDeckPoints, rightRail = StitchedTerrain.RoomRailRight, points = StitchedTerrain.RoomSectionPoints;
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(quality));
            if (rings.Count < 2 || track.Segments.Count == 0) continue;
            foreach (var run in SplitAtGaps(track, rings, r => r.Distance))
            {
            var list = new List<Vector3[]>();
            foreach (var r in run)
            {
                var segment = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)];
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
            result.Add((track, list));
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

    /// <summary>Embankments and cuttings: their slope (metres out per metre down or up), the most they fill or cut (metres; more: a bridge or a tunnel, left alone), the edge's anchor under the top of the land's contact on the road's side (metres).</summary>
    private const float EmbankSlope = 1.5f, MaxFill = 10, MaxCut = 10, EmbankAnchor = 0.3f, EdgeAnchor = 0f, Berm = 0.25f, UnderSlab = 0.6f;
    private const int EmbankPoints = 6;
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
    /// Beside each road (the owner's idea, step 3 of the plan): where the land just outside an edge lies lower than it
    /// (at most MaxFill), an embankment from the edge (EmbankAnchor under the land's contact on the road's side) down at
    /// EmbankSlope until it meets the ground, reaching in under the road (two sides make a dam); where it lies higher
    /// (at most MaxCut), a cutting from the edge up at that slope until it comes out of the land. Section by section
    /// (the rooms' stations), swept where consecutive stations agree; banked roads (edges far apart in height) and roads
    /// not facing up get neither. Returns the embankments (to add) and the cuttings (to take away).
    /// </summary>
    internal static (List<(List<Vector3>, List<int>)> Fills, List<(List<Vector3>, List<int>)> Cuts) Embankments(List<(Track Track, List<Vector3[]> Sections)> rooms, LandHeights land, List<float> debug)
    {
        var fills = new List<(List<Vector3>, List<int>)>();
        var cuts = new List<(List<Vector3>, List<int>)>();
        const int deckMid = 1 + (StitchedTerrain.RoomDeckPoints - 1) / 2, rightRail = StitchedTerrain.RoomRailRight;
        foreach (var (_, sections) in rooms)
            foreach (var side in new[] { 0, rightRail })
            {
                // Per station: the edge's anchor, the way out, how far under the road a fill reaches, and how deep a fill
                // or how high a cutting would go there (NaN: none possible); the kind the land asks for (0 none, 1 fill,
                // 2 cut).
                var n = sections.Count;
                var anchor = new Vector3[n];
                var outward = new Vector3[n];
                var half = new float[n];
                var fillDepth = new float[n];
                var cutHeight = new float[n];
                var kinds = new int[n];
                var valid = new bool[n];
                // How much of an embankment or cutting the road's shape there allows (1 a level road, fading to 0 as it
                // banks or its side curls up into a wall: there the rail climbs and twists, and slopes from it came out
                // in saw-teeth and chunks round tubes).
                var weight = new float[n];
                static float Fade(float from, float to, float x) { var t = Math.Clamp((x - from) / (to - from), 0, 1); return 1 - t * t * (3 - 2 * t); }
                for (var i = 0; i < n; i++)
                {
                    fillDepth[i] = cutHeight[i] = float.NaN;
                    var sec = sections[i];
                    Vector3 rail = sec[side], other = sec[side == 0 ? rightRail : 0], mid = sec[deckMid];
                    var across = rail - other;
                    if (across.LengthSquared() < 1) continue;
                    // Facing up: the vault's top over the deck.
                    if (sec[rightRail + 6].Y < mid.Y) continue;
                    // A way out across the plan (a curled side's top rail still has one; a side curled right over not).
                    var flat = new Vector3(across.X, 0, across.Z);
                    if (flat.Length() < 0.3f * across.Length()) continue;
                    outward[i] = Vector3.Normalize(flat);
                    // Banking (a banked turn keeps its embankments; they fade only from about 58 to 76 degrees): the edges' height apart over their distance; curl: how far the surface at this edge turns
                    // up (its last piece's direction, outwards).
                    var edgeDir = side == 0 ? sec[1] - sec[2] : sec[StitchedTerrain.RoomDeckPoints] - sec[StitchedTerrain.RoomDeckPoints - 1];
                    // (Against the road's own cross-section, the line between its rails: on a banked road the whole
                    // surface is tilted, and its high edge rising in the world is no curl.)
                    var curl = edgeDir.LengthSquared() > 1e-8f ? MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(edgeDir), Vector3.Normalize(across)), -1, 1)) : 0;
                    weight[i] = MathF.Min(Fade(0.85f, 0.97f, MathF.Abs(across.Y) / across.Length()), Fade(1.2f, 1.9f, curl));
                    // Twist: how fast the driving surface's edge rises or falls against the road's middle from the station
                    // before, per metre along (a road rolling over). The surface's own edge, not the rail: where a curb
                    // grows out of the edge the rail climbs by the curb's height on a level road, and was taken for a twist.
                    if (i > 0)
                    {
                        var prev = sections[i - 1];
                        var edge = side == 0 ? 1 : StitchedTerrain.RoomDeckPoints;
                        var along = Vector3.Distance(sec[deckMid], prev[deckMid]);
                        if (along > 1e-3f)
                            weight[i] = MathF.Min(weight[i], Fade(0.25f, 0.5f, MathF.Abs((sec[edge].Y - sec[deckMid].Y) - (prev[edge].Y - prev[deckMid].Y)) / along));
                    }
                    // Under a curb's top (its outer face then holds the land), or, where the road has no curb (the rail is
                    // the driving surface's own edge, near the deck's), just under that edge: 0.3 m down the slab's side
                    // the land met it half way and left its edge standing out.
                    var hasCurb = Vector3.Distance(rail, sec[side == 0 ? 1 : StitchedTerrain.RoomDeckPoints]) > 0.5f;
                    anchor[i] = rail - Vector3.UnitY * (hasCurb ? EmbankAnchor : EdgeAnchor);
                    half[i] = flat.Length() / 2 + 1;
                    valid[i] = true;
                }
                // (Each anchor at its own station's edge, not smoothed along the road: the land is to meet the edge
                // exactly, to be drivable.)
                for (var i = 0; i < n; i++)
                {
                    if (!valid[i]) continue;
                    var e = anchor[i];
                    // Up from the edge until the slope comes out of the land.
                    for (var t = 0.5f; t <= MaxCut * EmbankSlope; t += 0.5f)
                    {
                        var q = e + outward[i] * t;
                        var landTop = land.Top(q.X, q.Z);
                        if (float.IsNaN(landTop) || landTop <= e.Y + t / EmbankSlope) { cutHeight[i] = t / EmbankSlope + 1; break; }
                    }
                    // Down from the edge until the slope meets the ground.
                    for (var t = 0f; t <= MaxFill * EmbankSlope; t += 0.5f)
                    {
                        var q = e + outward[i] * t;
                        var ground = land.Below(q.X, q.Z, e.Y + 0.05f);
                        if (float.IsNaN(ground)) break;
                        if (ground >= e.Y - t / EmbankSlope) { fillDepth[i] = t / EmbankSlope + 1; break; }
                    }
                    var probe = e + outward[i] * 0.5f;
                    var top = land.Top(probe.X, probe.Z);
                    if (float.IsNaN(top)) continue;
                    kinds[i] = top > e.Y + 0.05f ? (float.IsNaN(cutHeight[i]) ? 0 : 2) : (float.IsNaN(fillDepth[i]) ? 0 : 1);
                }
                // The weight changes slowly along the road: by at most WeightStep a station either way (a dip spreads to
                // the stations round it). Low weight only flattens a piece; it never splits one: the two ends' ramps
                // left a hump of land between them.
                for (var i = 1; i < n; i++) weight[i] = MathF.Min(weight[i], weight[i - 1] + WeightStep);
                for (var i = n - 2; i >= 0; i--) weight[i] = MathF.Min(weight[i], weight[i + 1] + WeightStep);
                // Smoothed along the road: each station the kind most of the five round it ask for (its own on a tie),
                // where that kind is possible there; then pieces shorter than three stations dropped.
                var smooth = new int[n];
                for (var i = 0; i < n; i++)
                {
                    Span<int> votes = stackalloc int[3];
                    for (var k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) votes[kinds[k]]++;
                    var best = kinds[i];
                    for (var kind = 0; kind < 3; kind++) if (votes[kind] > votes[best]) best = kind;
                    if (best == 1 && float.IsNaN(fillDepth[i]) || best == 2 && float.IsNaN(cutHeight[i])) best = 0;
                    smooth[i] = best;
                }
                for (var i = 0; i < n;)
                {
                    var j = i;
                    while (j + 1 < n && smooth[j + 1] == smooth[i]) j++;
                    if (smooth[i] != 0 && j - i + 1 < 3)
                        for (var k = i; k <= j; k++) smooth[k] = 0;
                    i = j + 1;
                }
                // Short gaps (up to three stations) between two pieces of the same kind bridged, their depth or height
                // the straight line between the stations either side: the ramps of the two pieces' ends left a crease
                // there.
                for (var i = 1; i < n;)
                {
                    if (smooth[i] != 0 || smooth[i - 1] == 0) { i++; continue; }
                    var j = i;
                    while (j + 1 < n && smooth[j + 1] == 0) j++;
                    var kind = smooth[i - 1];
                    if (j + 1 < n && smooth[j + 1] == kind && j - i + 1 <= 3)
                    {
                        var amounts = kind == 1 ? fillDepth : cutHeight;
                        float from = amounts[i - 1], to = amounts[j + 1];
                        var bridged = true;
                        for (var k = i; k <= j; k++) if (outward[k] == default) bridged = false;
                        if (bridged && float.IsFinite(from) && float.IsFinite(to))
                            for (var k = i; k <= j; k++)
                            {
                                smooth[k] = kind;
                                amounts[k] = from + (to - from) * (k - i + 1) / (j - i + 2);
                            }
                    }
                    i = j + 1;
                }
                // Each piece: its depth or height its own station's (every station exact, not smoothed), ramped down to the
                // edge over its first and last three stations, swept.
                for (var i = 0; i < n;)
                {
                    if (smooth[i] == 0) { i++; continue; }
                    var j = i;
                    while (j + 1 < n && smooth[j + 1] == smooth[i]) j++;
                    var kind = smooth[i];
                    var amounts = kind == 1 ? fillDepth : cutHeight;
                    var polys = new List<Vector3[]>();
                    for (var k = i; k <= j; k++)
                    {
                        var amount = MathF.Max(0, amounts[k]);
                        // Ramped over the piece's ends: as long along the road as its slope reaches out, and 2 m more.
                        // (By distance along the road: the road's rings are not evenly spaced.)
                        var fromEnd = MathF.Min(Vector3.Distance(sections[k][deckMid], sections[i][deckMid]), Vector3.Distance(sections[k][deckMid], sections[j][deckMid]));
                        var ramp = MathF.Min(1, (fromEnd + 1) / (amount * EmbankSlope + 2));
                        // Less of it (the ramps at a piece's ends, a banked or curling road): the slope starts nearer the
                        // ground (lower for a fill, higher for a cutting) and still reaches it, at the same slope; none
                        // at all at the ground itself.
                        var f = ramp * weight[k];
                        // (A cutting shrinks to nothing as f does: a box of its least size at its ends notched the land.)
                        var a = kind == 1 ? (amount - 1) * f + 1 : MathF.Max(0.05f, amount * f);
                        var shift = (amount - 1) * (1 - f);
                        Vector3 o = outward[k], u = Vector3.UnitY;
                        var e = anchor[k] + (kind == 1 ? -u : u) * shift;
                        // (A level strip of Berm before the slope: the land flush with the edge where it is seen.)
                        var berm = o * (Berm * f);
                        polys.Add(kind == 1
                            // (Inside the edge it drops under the slab at once before reaching in under the road: level with the
                            // edge it stood over the driving surface of a banked or curved road and poked through.)
                            ? [e - o * half[k] - u * UnderSlab, e - o * 0.02f - u * UnderSlab, e, e + berm, e + berm + o * (a * EmbankSlope) - u * a, e - o * half[k] - u * MathF.Max(a + 1, UnderSlab + 0.5f)]
                            : [e - o * f, e, e + berm, e + berm + o * (a * EmbankSlope) + u * a, e + berm + o * (a * EmbankSlope) + u * (a + 2 * f + 0.05f), e - o * f + u * (a + 2 * f + 0.05f)]);
                    }
                    (kind == 1 ? fills : cuts).Add(Sweep(polys));
                    i = j + 1;
                }
            }
        return (fills, cuts);
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
                foreach (var j in sectionPoints < 12 ? Enumerable.Range(0, sectionPoints).ToArray() : new[] { 0, (sectionPoints - 12) / 2, sectionPoints - 12, sectionPoints - 6 })
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
            var rooms = StitchedTerrain.RoomSections(grid, map, FreeUnder);
            foreach (var (fp, fi) in FreeSpaces(rooms))
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
            // The pits under the roads' holes, taken out as free spaces (their faces are the land's).
            foreach (var (pp, pi) in Pits(map, quality))
            {
                using var madeP = ManifoldLib.Solid.Of(ToMesh(pp, pi, Land));
                DrawVolume(volumeLines, pp, pp.Count / 2 - 1, 2, madeP.Status == ManifoldLib.Error.NoError ? 10 : 11);
                if (madeP.Status != ManifoldLib.Error.NoError) { Console.WriteLine($"  [boolean] a hole's pit is invalid ({madeP.Status}): not cut out"); continue; }
                var pit = madeP.AsOriginal();
                freeIds.Add((uint)pit.OriginalId);
                solids.Add(pit);
            }
            // Embankments and cuttings beside the roads (land added up to a road's edge, or cut down to it).
            var fillIds = new HashSet<uint>();
            var cutIds = new HashSet<uint>();
            if (Environment.GetEnvironmentVariable("TMSPLINE_BOOL_EMBANK") != "off")
            {
                var heights = new LandHeights(mesh);
                var (fills, cuts) = Embankments(RoadStations(map, quality), heights, volumeLines);
                var fillSolids = new List<ManifoldLib.Solid>();
                foreach (var (kind, list, ids, lineKind) in new[] { (0, fills, fillIds, 12), (1, cuts, cutIds, 13) })
                    foreach (var (ep, ei) in list)
                    {
                        using var madeE = ManifoldLib.Solid.Of(ToMesh(ep, ei, Land));
                        DrawVolume(volumeLines, ep, EmbankPoints, (ep.Count - 2) / EmbankPoints, madeE.Status == ManifoldLib.Error.NoError ? lineKind : 11);
                        if (madeE.Status != ManifoldLib.Error.NoError) continue;
                        var e = madeE.AsOriginal();
                        ids.Add((uint)e.OriginalId);
                        if (kind == 0) fillSolids.Add(e); else solids.Add(e);
                    }
                if (fillSolids.Count > 0)
                {
                    // The land with its embankments, as one (each keeps its id).
                    var grown = ManifoldLib.Solid.Batch(fillSolids.Prepend(ground), ManifoldLib.Op.Add);
                    foreach (var f in fillSolids) f.Dispose();
                    if (grown.Status == ManifoldLib.Error.NoError) solids[0] = grown;
                    else { Console.WriteLine($"  [boolean] the embankments could not be added ({grown.Status})"); grown.Dispose(); }
                }
                Time($"embankments: {fillIds.Count} fills, {cutIds.Count} cuttings");
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
                var id = output.RunOriginalId[r];
                var isLand = id == landId || freeIds.Contains(id) || fillIds.Contains(id) || cutIds.Contains(id);
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
            // Small loose pieces (land left between a road and its free space, cut off from the rest) left out: the
            // roads are whole, so no hole opens where they were.
            var dropped = DropLoose(cut, SmallPiece);
            if (Stats) Console.WriteLine($"  [boolean] left out {dropped} loose pieces of land under {SmallPiece} m2");
            // The land along the seam simplified: its cut ran through every road face it crossed, a vertex wherever it
            // crossed one of their edges. Seam vertices on a straight line between their neighbours (within
            // SeamTolerance) go, then the land inside as the land was simplified before.
            var before = cut.Indices.Count / 3;
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after the cut: {Folded(cut)}");
            cut = MeshSimplifier.Simplify(cut, 0, VoxelTerrain.SimplifyError, borderTolerance: SeamTolerance);
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after simplifying: {Folded(cut)}");
            MeshSimplifier.ImproveByFlips(cut, 35);
            if (Stats) Console.WriteLine($"  [boolean] folded triangles after flips: {Folded(cut)}");
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
