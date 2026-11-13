using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Seam experiment: closes the gap the cut left between the land and the roads with strips of triangles, from the
/// land's rim to the road's edge. Each rim loop is walked and its vertices matched to the nearest road edge (and how
/// far along it); a run of the loop along one edge is joined to that stretch of the edge like a zip, always taking the
/// shorter of the two next diagonals. The strip's triangles turn the same way as the land's, and the edge's points are
/// shared by the runs that use them. An open road's edge is one outline round it (up its left edge, across its end,
/// back down its right edge, across its start), so a run goes on round its ends; a closed track has two edges, each a
/// loop. Where the rim leaves an edge (crossing under the road) the gap stays open for now.
/// </summary>
public static class SeamZipper
{
    /// <summary>Debug: the last build's rim edges the strips failed at (x, y, z, x, y, z, 0), for the editor's mesh view.</summary>
    public static float[] LastOpen { get; private set; } = [];

    private sealed class Rail
    {
        public readonly List<Vector3> Points = [];
        /// <summary>Each point's distance along the outline from its first, metres.</summary>
        public readonly List<float> Along = [];
        /// <summary>The outline's length, back round to its first point.</summary>
        public float Length;

        public void Add(Vector3 p)
        {
            Along.Add(Points.Count == 0 ? 0 : Along[^1] + Vector3.Distance(Points[^1], p));
            Points.Add(p);
        }

        public void Close() => Length = MathF.Max(Along[^1] + Vector3.Distance(Points[^1], Points[0]), 1e-3f);

        /// <summary>From one distance along to another, the short way round (negative: backwards).</summary>
        public float Step(float from, float to)
        {
            var d = (to - from) % Length;
            if (d > Length / 2) d -= Length;
            else if (d < -Length / 2) d += Length;
            return d;
        }
    }

    /// <summary>
    /// The projection zipper (the default; TMSPLINE_ZIP=rail: the edge-following one below): each rim vertex joined to
    /// its nearest point on the nearest road's surface line (its edges, the back of its surface, past an open end its
    /// end), the strip running between the rim and those points. Beside a road that is its edge; under it, or round a
    /// tube, its underside: there the seam leaves the edge and runs along the road's body (the owner's sketch). A rim
    /// vertex whose line to that point passes through a road's room (over a tunnel's mouth) is not joined: that rim is
    /// an opening. Except at an open road's end: there the land round the end leaves a window the shape of the road's
    /// vault, which nothing drives through; it is closed with a fan.
    /// </summary>
    /// <param name="roadQuality">The quality the roads are built at: the strips meet their edges' own vertices.</param>
    public static int Zip(AdaptiveMesh mesh, TrackMap map, Func<Vector3, (float Distance, Vector3 Point, Vector3 Left, Vector3 Right)> surface, Func<Vector3, float> room, float gap, MeshQuality roadQuality = MeshQuality.Low, Func<Vector3, bool>? underLand = null)
    {
        if (Environment.GetEnvironmentVariable("TMSPLINE_ZIP") == "rail") return ZipEdges(mesh, map, p => surface(p).Distance, room, gap);
        // Diagnostics: what is left unjoined within a radius of a plan point (TMSPLINE_ZIP_NEAR="x;z;radius").
        var near = Environment.GetEnvironmentVariable("TMSPLINE_ZIP_NEAR")?.Split(';').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        bool Near(Vector3 p) => near is { Length: 3 } && MathF.Sqrt((p.X - near[0]) * (p.X - near[0]) + (p.Z - near[1]) * (p.Z - near[1])) < near[2];
        // The open roads' ends: the first and last cross-sections, facing out.
        var ends = new List<(Vector3 P, Vector3 Out, Vector3 Right, Vector3 Up, float Width)>();
        foreach (var track in map.Tracks)
        {
            if (track.Closed) continue;
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Normal));
            if (rings.Count < 2) continue;
            static Vector3 V(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
            foreach (var (r, sign) in new[] { (rings[0], -1f), (rings[^1], 1f) })
                ends.Add((V(r.Position), V(r.Tangent) * sign, V(r.Right), V(r.Up), (float)r.Width));
        }
        // Whether a place lies in a road's end: within the gap of its cross-section, across no wider than the road and
        // the gap, and no higher over it than the road is wide (its vault).
        bool AtEnd(Vector3 p) => EndOf(p) is not null;
        (Vector3 P, Vector3 Out, Vector3 Right, Vector3 Up, float Width)? EndOf(Vector3 p)
        {
            foreach (var e in ends)
            {
                var d = p - e.P;
                if (MathF.Abs(Vector3.Dot(d, e.Out)) < gap && MathF.Abs(Vector3.Dot(d, e.Right)) < e.Width / 2 + gap && Vector3.Dot(d, e.Up) > 0.5f && Vector3.Dot(d, e.Up) < e.Width) return e;
            }
            return null;
        }
        // The road edges exactly as the road's mesh has them: each ring's edge contact (a curb's, or the bare edge),
        // straight between rings.
        var edges = new List<List<Vector3>>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            if (track.Segments.Count == 0) continue;
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(roadQuality));
            if (rings.Count < 2) continue;
            foreach (var side in new[] { -1, 1 })
                edges.Add(rings.Select(r =>
                {
                    var w = RoadMeshBuilder.EdgeContact(r, track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)], side);
                    return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
                }).ToList());
        }
        const float EdgeBucket = 8;
        var edgeBuckets = new Dictionary<(int, int), List<(int Edge, int Index)>>();
        for (var e = 0; e < edges.Count; e++)
            for (var i = 0; i + 1 < edges[e].Count; i++)
            {
                // Each piece in the squares its ends and middle fall in.
                foreach (var p in new[] { edges[e][i], (edges[e][i] + edges[e][i + 1]) / 2, edges[e][i + 1] })
                {
                    var key = ((int)MathF.Floor(p.X / EdgeBucket), (int)MathF.Floor(p.Z / EdgeBucket));
                    if (!edgeBuckets.TryGetValue(key, out var list)) edgeBuckets[key] = list = [];
                    if (list.Count == 0 || list[^1] != (e, i)) list.Add((e, i));
                }
            }
        // A road point on an edge (within half a metre): onto the edge's own line; which edge, and where along it (its
        // piece's index plus how far along that piece).
        (int Edge, float At, Vector3 Point)? OnEdge(Vector3 p)
        {
            (int Edge, float At, Vector3 Point)? best = null;
            var bestDistance = 0.5f;
            int bx = (int)MathF.Floor(p.X / EdgeBucket), bz = (int)MathF.Floor(p.Z / EdgeBucket);
            for (var x = bx - 1; x <= bx + 1; x++)
                for (var z = bz - 1; z <= bz + 1; z++)
                    if (edgeBuckets.TryGetValue((x, z), out var list))
                        foreach (var (e, i) in list)
                        {
                            Vector3 a = edges[e][i], b = edges[e][i + 1];
                            var d = b - a;
                            var h = Math.Clamp(Vector3.Dot(p - a, d) / MathF.Max(d.LengthSquared(), 1e-9f), 0, 1);
                            var q = a + d * h;
                            var distance = Vector3.Distance(p, q);
                            if (distance < bestDistance) { bestDistance = distance; best = (e, i + h, q); }
                        }
            return best;
        }
        // The edge's own vertices, as the mesh's, made when a strip first uses them.
        var roadVertex = new Dictionary<(int, int), int>();
        int RoadVertex(int e, int i)
        {
            if (!roadVertex.TryGetValue((e, i), out var id))
            {
                id = mesh.Positions.Count;
                mesh.Positions.Add(edges[e][i]);
                mesh.Pinned.Add(false);
                roadVertex[(e, i)] = id;
            }
            return id;
        }
        // The grid zipper (the default; TMSPLINE_ZIP=project: each rim vertex its own road point): each road's attach
        // surface as a grid, ring by ring at the quality the road is built at, across from its left edge's contact (a
        // curb's, or the bare edge) over the driving surface to its right edge's; its border is the two edges and the
        // two ends' cross lines. Rim vertices join its nodes, and between two neighbours the road side runs along the
        // grid from one node to the next: continuous along a run, whatever each vertex chose.
        var grid = Environment.GetEnvironmentVariable("TMSPLINE_ZIP") != "project";
        // The advancing front (the default with the grid; TMSPLINE_STITCH=off: the earlier bridges, close-road strips,
        // end windows and hole fills): what the strips along runs leave open, closed triangle by triangle.
        var stitch = grid && Environment.GetEnvironmentVariable("TMSPLINE_STITCH") != "off";
        const int Across = 16;
        var ribbons = new List<(List<Vector3[]> Cross, List<float> Along)>();
        // Each node's side of the driving surface: the way it faces (into the room).
        var ribbonNormals = new List<List<Vector3[]>>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            if (track.Segments.Count == 0) continue;
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(roadQuality));
            if (rings.Count < 2) continue;
            static Vector3 W(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
            var cross = new List<Vector3[]>();
            var along = new List<float>();
            var normals = new List<Vector3[]>();
            foreach (var r in rings)
            {
                var segment = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)];
                var points = new Vector3[Across + 1];
                var facing = new Vector3[Across + 1];
                for (var j = 0; j <= Across; j++)
                {
                    var q = RoadMeshBuilder.SurfaceSample(r, (j / (double)Across - 0.5) * r.Width);
                    facing[j] = Vector3.Normalize(W(r.Right * q.NX + r.Up * q.NY));
                }
                normals.Add(facing);
                points[0] = W(RoadMeshBuilder.EdgeContact(r, segment, -1));
                points[Across] = W(RoadMeshBuilder.EdgeContact(r, segment, 1));
                for (var j = 1; j < Across; j++)
                {
                    var e = RoadMeshBuilder.SurfaceSample(r, (j / (double)Across - 0.5) * r.Width);
                    points[j] = W(r.Position + r.Right * e.X + r.Up * e.Y);
                }
                cross.Add(points);
                along.Add((float)r.Distance);
            }
            ribbons.Add((cross, along));
            ribbonNormals.Add(normals);
        }
        var ribbonBuckets = new Dictionary<(int, int), List<(int Ribbon, int Ring)>>();
        for (var rb = 0; rb < ribbons.Count; rb++)
            for (var k = 0; k < ribbons[rb].Cross.Count; k++)
            {
                var c = ribbons[rb].Cross[k][Across / 2];
                var key = ((int)MathF.Floor(c.X / EdgeBucket), (int)MathF.Floor(c.Z / EdgeBucket));
                if (!ribbonBuckets.TryGetValue(key, out var list)) ribbonBuckets[key] = list = [];
                list.Add((rb, k));
            }
        // The nearest place on any road's grid: which road, where along (ring index and fraction) and across (0 at the
        // left edge, 1 at the right), how far.
        (int Ribbon, float S, float U, float Distance)? Project(Vector3 p)
        {
            (int, float, float, float)? best = null;
            var bestDistance = 2.5f * gap;
            var cells = (int)MathF.Ceiling((2.5f * gap + 20) / EdgeBucket);
            int bx = (int)MathF.Floor(p.X / EdgeBucket), bz = (int)MathF.Floor(p.Z / EdgeBucket);
            var seen = new HashSet<(int, int)>();
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var z = bz - cells; z <= bz + cells; z++)
                {
                    if (!ribbonBuckets.TryGetValue((x, z), out var list)) continue;
                    foreach (var (rb, k0) in list)
                        for (var k = k0 - 1; k <= k0; k++)
                        {
                            var cross = ribbons[rb].Cross;
                            if (k < 0 || k + 1 >= cross.Count || !seen.Add((rb, k))) continue;
                            for (var j = 0; j < Across; j++)
                            {
                                Vector3 a = cross[k][j], b = cross[k + 1][j], c = cross[k + 1][j + 1], d = cross[k][j + 1];
                                foreach (var (p0, p1, p2, s0, u0, s1, u1, s2, u2) in new[]
                                {
                                    (a, b, c, k, j, k + 1, j, k + 1, j + 1),
                                    (a, c, d, k, j, k + 1, j + 1, k, j + 1),
                                })
                                {
                                    var q = ClosestOnTriangle(p, p0, p1, p2, out var v, out var w);
                                    var distance = Vector3.Distance(p, q);
                                    if (distance >= bestDistance) continue;
                                    bestDistance = distance;
                                    var u = 1 - v - w;
                                    best = (rb, u * s0 + v * s1 + w * s2, (u * u0 + v * u1 + w * u2) / Across, distance);
                                }
                            }
                        }
                }
            return best;
        }
        // The grid's nodes as the mesh's vertices (on an edge, the road mesh's own edge vertex), made when first used.
        var nodeVertex = new Dictionary<(int, int, int), int>();
        var roadNodeOf = new Dictionary<int, (int Ribbon, int K, int J)>();
        int Node(int rb, int k, int j)
        {
            if (!nodeVertex.TryGetValue((rb, k, j), out var id))
            {
                id = mesh.Positions.Count;
                // (A node across the road a little under its surface: the strips meeting the underside stay under it, not
                // on the driving surface between nodes.)
                mesh.Positions.Add(j == 0 || j == Across ? ribbons[rb].Cross[k][j] : ribbons[rb].Cross[k][j] - ribbonNormals[rb][k][j] * 0.15f);
                mesh.Pinned.Add(false);
                nodeVertex[(rb, k, j)] = id;
                roadNodeOf[id] = (rb, k, j);
            }
            return id;
        }
        // (The front's helpers.) A grid node's mesh vertex the other way round; an edge node's outward direction; a
        // ring's direction along the road; whether a triangle stays out of the rooms and off the decks.
        Vector3 OutOf(int rb, int k, int j)
        {
            var cross = ribbons[rb].Cross[k];
            var o = j == 0 ? cross[0] - cross[Across] : cross[Across] - cross[0];
            return o.LengthSquared() > 1e-6f ? Vector3.Normalize(o) : Vector3.Zero;
        }
        Vector3 Tangent(int rb, int k)
        {
            var cross = ribbons[rb].Cross;
            int k0 = Math.Max(0, k - 1), k1 = Math.Min(cross.Count - 1, k + 1);
            var d = cross[k1][Across / 2] - cross[k0][Across / 2];
            return d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.Zero;
        }
        bool ClearOfRoads(Vector3 x, Vector3 y, Vector3 z)
        {
            for (var u = 1; u < 6; u++)
                for (var v = 1; u + v < 6; v++)
                {
                    var q = x + (y - x) * (u / 6f) + (z - x) * (v / 6f);
                    if (MathF.Min(Vector3.Distance(q, x), MathF.Min(Vector3.Distance(q, y), Vector3.Distance(q, z))) < 0.75f) continue;
                    if (room(q) < -0.5f) return false;
                    var (d, _, l, r) = surface(q);
                    var lr = r - l;
                    if (d < 0.25f && lr.LengthSquared() > 1e-4f)
                    {
                        var across = Vector3.Dot(q - l, lr) / lr.LengthSquared();
                        if (across > 0.05f && across < 0.95f) return false;
                    }
                }
            return true;
        }
        var breaks = 0;
        // Debug: the rim edges the strips fail at (a break, or an end unjoined), for the editor's mesh view.
        var failedEdges = new List<float>();
        void Failed(int a, int b) { Vector3 p = mesh.Positions[a], q = mesh.Positions[b]; failedEdges.AddRange([p.X, p.Y, p.Z, q.X, q.Y, q.Z, 0]); }
        // Switches from one road to another along a rim (bridged): from one node to the other.
        var bridges = new List<((int Ribbon, int K, int J) From, (int Ribbon, int K, int J) To)>();
        int snaps = 0, targets = 0;
        var farthest = 0f;
        var made = 0;
        var pinches = Unpinch(mesh);
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  {pinches} vertices where rims touched split");
        SplitLongRims(mesh, gap);
        // Each edge's triangle's third corner (as the triangle winds the edge): a rim edge's land lies towards it.
        var third = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
                third[(mesh.Indices[t + k], mesh.Indices[t + (k + 1) % 3])] = mesh.Indices[t + (k + 2) % 3];
        // Away from the land across a rim edge, along the land's surface.
        Vector3 Outward(int a, int b)
        {
            if (!third.TryGetValue((a, b), out var c)) return Vector3.Zero;
            Vector3 pa = mesh.Positions[a], pb = mesh.Positions[b], e = pb - pa;
            var o = (pa + pb) / 2 - mesh.Positions[c];
            o -= e * (Vector3.Dot(o, e) / MathF.Max(e.LengthSquared(), 1e-9f));
            return o.LengthSquared() > 1e-9f ? Vector3.Normalize(o) : Vector3.Zero;
        }
        foreach (var loop in RimLoops(mesh))
        {
            var n = loop.Count;
            var target = new int[n];
            var onEdge = new (int Edge, float At)?[n];
            var node = new (int Ribbon, int K, int J)[n];
            for (var i = 0; grid && i < n; i++)
            {
                target[i] = -1;
                var p = mesh.Positions[loop[i]];
                var outward = Outward(loop[(i - 1 + n) % n], loop[i]) + Outward(loop[i], loop[(i + 1) % n]);
                outward = outward.LengthSquared() > 1e-9f ? Vector3.Normalize(outward) : Vector3.Zero;
                // The nodes it may join: nearest the place where it is nearest a road's grid, and that ring's edges; the
                // same for the place a step out from the land (another road may be nearer that way).
                var candidates = new List<(int Ribbon, int K, int J)>();
                foreach (var hit in new[] { Project(p), Project(p + outward * gap) })
                {
                    if (hit is not { } h) continue;
                    var count = ribbons[h.Ribbon].Cross.Count;
                    var k = Math.Clamp((int)MathF.Round(h.S), 0, count - 1);
                    var j = Math.Clamp((int)MathF.Round(h.U * Across), 0, Across);
                    candidates.Add((h.Ribbon, k, j));
                    candidates.Add((h.Ribbon, k, 0));
                    candidates.Add((h.Ribbon, k, Across));
                }
                Vector3 At((int Ribbon, int K, int J) c) => ribbons[c.Ribbon].Cross[c.K][c.J];
                // From the driving side (over the road) land meets an edge only, never the driving surface itself;
                // a node across the road only from behind it (the underside).
                bool Side((int Ribbon, int K, int J) c)
                {
                    if (c.J == 0 || c.J == Across) return true;
                    if (Vector3.Dot(p - At(c), ribbonNormals[c.Ribbon][c.K][c.J]) >= 0) return false;
                    // (And under the road, between its edges as seen across it: land beside it, a little below the
                    // deck, joins an edge; to the middle of the underside its strip would cut through the road's edge.)
                    Vector3 l = ribbons[c.Ribbon].Cross[c.K][0], r = ribbons[c.Ribbon].Cross[c.K][Across], lr = r - l;
                    var across = Vector3.Dot(p - l, lr) / MathF.Max(lr.LengthSquared(), 1e-6f);
                    return across > 0.02f && across < 0.98f;
                }
                var reachable = candidates.Distinct().Where(c => Side(c) && Vector3.Distance(p, At(c)) < 2.5f * gap && Clear(room, p, At(c)))
                    .OrderBy(c => Vector3.DistanceSquared(p, At(c))).ToList();
                // The nearest out from the land (not back through it); failing that, the nearest at all.
                // An edge first (the owner's rule: the seam runs along the underside only where the edge is too far or
                // a line to it would cross the road): the nearest edge node out from the land, if no farther than half
                // again the nearest node at all; then the nearest out from the land; then the nearest at all.
                var nearestAll = reachable.Count > 0 ? Vector3.Distance(p, At(reachable[0])) : 0;
                var chosen = reachable.Cast<(int, int, int)?>().FirstOrDefault(c => (c!.Value.Item3 == 0 || c.Value.Item3 == Across) && Vector3.Dot(At(c.Value) - p, outward) > 0 && Vector3.Distance(p, At(c.Value)) <= 1.5f * nearestAll)
                    ?? reachable.Cast<(int, int, int)?>().FirstOrDefault(c => Vector3.Dot(At(c!.Value) - p, outward) > 0) ?? reachable.Cast<(int, int, int)?>().FirstOrDefault();
                if (chosen is not { } pick)
                {
                    if (Near(p)) Console.WriteLine($"  rim vertex {p} not joined: no grid node it reaches");
                    continue;
                }
                node[i] = pick;
                target[i] = Node(pick.Item1, pick.Item2, pick.Item3);
                targets++;
                if (pick.Item3 == 0 || pick.Item3 == Across) snaps++;
                if (Near(p)) Console.WriteLine($"  rim vertex {p} (loop of {n}, #{i}) joined to road {pick.Item1} ring {pick.Item2} across {pick.Item3} at {At(pick)}");
            }
            // A vertex on another road than both its neighbours, which agree (one rim vertex at the tip of the land
            // between a helix's turns choosing the upper turn, its neighbours the lower one's start): onto their road,
            // the nearest node between their rings it reaches. (From the neighbours' choices only, not the order the
            // rim is walked in.)
            bool Together((int Ribbon, int K, int J) a, (int Ribbon, int K, int J) b) =>
                a.Ribbon == b.Ribbon && MathF.Abs(ribbons[a.Ribbon].Along[a.K] - ribbons[a.Ribbon].Along[b.K]) <= 3 * gap;
            for (var i = 0; grid && i < n; i++)
            {
                int prev = (i - 1 + n) % n, next = (i + 1) % n;
                if (target[i] < 0 || target[prev] < 0 || target[next] < 0) continue;
                if (Together(node[i], node[prev]) || Together(node[i], node[next]) || !Together(node[prev], node[next])) continue;
                var p = mesh.Positions[loop[i]];
                var rb = node[prev].Ribbon;
                int k0 = Math.Min(node[prev].K, node[next].K), k1 = Math.Max(node[prev].K, node[next].K);
                (int, int, int)? better = null;
                var bestDistance = 2.5f * gap;
                for (var k = k0; k <= k1; k++)
                    for (var jj = 0; jj <= Across; jj++)
                    {
                        var q = ribbons[rb].Cross[k][jj];
                        if (jj != 0 && jj != Across)
                        {
                            if (Vector3.Dot(p - q, ribbonNormals[rb][k][jj]) >= 0) continue;
                            Vector3 l = ribbons[rb].Cross[k][0], lr = ribbons[rb].Cross[k][Across] - l;
                            var across = Vector3.Dot(p - l, lr) / MathF.Max(lr.LengthSquared(), 1e-6f);
                            if (across <= 0.02f || across >= 0.98f) continue;
                        }
                        var distance = Vector3.Distance(p, q);
                        if (distance < bestDistance && Clear(room, p, q)) { bestDistance = distance; better = (rb, k, jj); }
                    }
                if (better is not { } b)
                {
                    // None it reaches: not joined (the fan below closes it with its neighbours').
                    if (Near(p)) Console.WriteLine($"  rim vertex {p} on road {node[i].Ribbon} ring {node[i].K}, its neighbours on another, unjoined");
                    target[i] = -1;
                    continue;
                }
                if (Near(p)) Console.WriteLine($"  rim vertex {p} moved from road {node[i].Ribbon} ring {node[i].K} to its neighbours' road {b.Item1} ring {b.Item2} across {b.Item3}");
                node[i] = b;
                target[i] = Node(b.Item1, b.Item2, b.Item3);
            }
            for (var i = 0; !grid && i < n; i++)
            {
                target[i] = -1;
                var p = mesh.Positions[loop[i]];
                var (distance, nearest, left, right) = surface(p);
                // Where the land would go on past its rim: away from it along its surface. The road points it may
                // join: the nearest road's (its nearest point, that section's edges) and those of the road nearest a
                // step out that way (the land under a helix's upper turn, just over the lower one: the lower one).
                var outward = Outward(loop[(i - 1 + n) % n], loop[i]) + Outward(loop[i], loop[(i + 1) % n]);
                outward = outward.LengthSquared() > 1e-9f ? Vector3.Normalize(outward) : Vector3.Zero;
                var (_, outNearest, outLeft, outRight) = surface(p + outward * gap);
                var list = new List<Vector3> { nearest, left, right, outNearest, outLeft, outRight };
                // And staying on the road the previous rim vertex joined: that point, and the road's points halfway
                // to it (round the tip of the land between a helix's turns, back to the lower one's start, not up
                // to the upper turn and back down, which left the lower one's corner open).
                Vector3? previous = i > 0 && target[i - 1] >= 0 ? mesh.Positions[target[i - 1]] : null;
                if (previous is { } prev)
                {
                    var (_, halfNearest, halfLeft, halfRight) = surface((p + prev) / 2);
                    list.AddRange([prev, halfNearest, halfLeft, halfRight]);
                }
                // In a road end's window (over its deck's end): only points at that deck's level (not another road
                // over it); none, and the window is closed with a fan below.
                // (Only where that end's deck is about as near as any road: not land along another road over it.)
                var end = EndOf(p);
                if (end is { } atEnd && Vector3.Dot(p - atEnd.P, atEnd.Up) > distance + 1) end = null;
                var candidates = list.Where(c => Vector3.Distance(p, c) < 2.5f * gap)
                    .Where(c => end is not { } e || MathF.Abs(Vector3.Dot(c - e.P, e.Up)) < 2)
                    .OrderBy(c => previous is { } prev2 && Vector3.Distance(c, prev2) < 2 * gap ? 0 : 1)
                    .ThenBy(c => Vector3.DistanceSquared(p, c)).ToList();
                // The first one out from the land (not back through it) that it reaches without crossing a room;
                // failing that, the first it reaches at all.
                Vector3? reached = null;
                if (distance < 2.5f * gap)
                {
                    foreach (var candidate in candidates)
                        if (Vector3.Dot(candidate - p, outward) > 0 && Clear(room, p, candidate)) { reached = candidate; break; }
                    if (reached is null)
                        foreach (var candidate in candidates)
                            if (Clear(room, p, candidate)) { reached = candidate; break; }
                }
                if (reached is not { } point)
                {
                    if (Near(p)) Console.WriteLine($"  rim vertex {p} not joined: road surface {distance:0.0} m away at {nearest}, {(distance >= 1.5f * gap ? "too far" : "no line out of the rooms")}");
                    continue;
                }
                if (Near(p)) Console.WriteLine($"  rim vertex {p} (loop of {n}, #{i}) joined to {point}{(OnEdge(point) is { } oe ? $" on edge {oe.Edge} at {oe.At:0.0}" : " (not on an edge)")}");
                targets++;
                // On an edge: the road mesh's own edge vertex nearest it (the land's side of the seam has no vertices
                // of its own along an edge, only the road's).
                if (OnEdge(point) is { } edge)
                {
                    var vertex = Math.Clamp((int)MathF.Round(edge.At), 0, edges[edge.Edge].Count - 1);
                    snaps++;
                    farthest = MathF.Max(farthest, Vector3.Distance(point, edges[edge.Edge][vertex]));
                    onEdge[i] = (edge.Edge, vertex);
                    target[i] = RoadVertex(edge.Edge, vertex);
                    continue;
                }
                target[i] = mesh.Positions.Count;
                mesh.Positions.Add(point);
                mesh.Pinned.Add(false);
            }
            for (var i = 0; grid && i < n; i++)
            {
                var j = (i + 1) % n;
                if (target[i] < 0 || target[j] < 0)
                {
                    // (Debug: a rim edge the strip fails at, near a road: an end of it unjoined.)
                    if (surface(mesh.Positions[loop[i]]).Distance < 2.5f * gap) Failed(loop[i], loop[j]);
                    continue;
                }
                var (ri, ki, ji) = node[i];
                var (rj, kj, jj) = node[j];
                // A break: from one road to another, or far along it (a run ends there). Bridged, where the piece of
                // land it makes stays out of the rooms and is not too long (the land between a helix's turns going from
                // the upper one to the lower one's start); else reported and left open.
                if (ri != rj || MathF.Abs(ribbons[ri].Along[ki] - ribbons[ri].Along[kj]) > 3 * gap)
                {
                    Vector3 ni = mesh.Positions[target[i]], nj = mesh.Positions[target[j]], li = mesh.Positions[loop[i]], lj = mesh.Positions[loop[j]];
                    var stays = Vector3.Distance(ni, nj) <= 6 * gap;
                    foreach (var (u, v) in new[] { (0.33f, 0.33f), (0.5f, 0.25f), (0.25f, 0.5f), (0.5f, 0.5f) })
                        if (stays && (room(li + (lj - li) * u + (ni - li) * v) < -3 || room(lj + (ni - lj) * u + (nj - lj) * v) < -3)) stays = false;
                    // Nor through a road's deck: no point inside its two triangles (away from their corners) on a road's
                    // surface (at a loop's foot a bridge from the entry's edge to the exit's reached through the road).
                    for (var a = 1; stays && a < 6; a++)
                        for (var b = 1; stays && a + b < 6; b++)
                        {
                            float u = a / 6f, v = b / 6f;
                            foreach (var q in new[] { li + (lj - li) * u + (ni - li) * v, lj + (ni - lj) * u + (nj - lj) * v })
                            {
                                var nearCorner = MathF.Min(MathF.Min(Vector3.Distance(q, ni), Vector3.Distance(q, nj)), MathF.Min(Vector3.Distance(q, li), Vector3.Distance(q, lj))) < 1.5f;
                                if (!nearCorner && surface(q).Distance < 0.3f) { stays = false; break; }
                            }
                        }
                    if (stays && !stitch)
                    {
                        mesh.Indices.AddRange([loop[j], loop[i], target[i]]);
                        mesh.Indices.AddRange([loop[j], target[i], target[j]]);
                        made += 2;
                        bridges.Add((node[i], node[j]));
                        if (Near(li)) Console.WriteLine($"  bridge from rim {li} (road {ri} ring {ki}) to {lj} (road {rj} ring {kj})");
                        continue;
                    }
                    breaks++;
                    Failed(loop[i], loop[j]);
                    if (Near(mesh.Positions[loop[i]])) Console.WriteLine($"  break between rim {mesh.Positions[loop[i]]} (road {ri} ring {ki}) and {mesh.Positions[loop[j]]} (road {rj} ring {kj})");
                    continue;
                }
                // The road side: along the grid from i's node to j's, ring by ring (across as far as between them), or
                // across one ring (round an end).
                var chain = new List<int> { target[i] };
                if (ki == kj)
                    for (var c = ji; c != jj;) { c += Math.Sign(jj - ji); chain.Add(Node(ri, ki, c)); }
                else
                {
                    // Along the grid's lines only (across a ring, then on to the next): a diagonal between nodes on a
                    // curved deck (a loop's climb) cut under its surface and came out through it.
                    var dir = Math.Sign(kj - ki);
                    var at = ji;
                    for (var k = ki; ; k += dir)
                    {
                        var want = (int)MathF.Round(ji + (jj - ji) * (k + dir - ki) / (float)(kj - ki));
                        if (k == kj) want = jj;
                        while (at != want) { at += Math.Sign(want - at); chain.Add(Node(ri, k, at)); }
                        if (k == kj) break;
                        chain.Add(Node(ri, k + dir, at));
                        if (k + dir == kj && at == jj) break;
                    }
                }
                mesh.Indices.AddRange([loop[j], loop[i], chain[0]]);
                made++;
                for (var c = 0; c + 1 < chain.Count; c++)
                {
                    if (chain[c] == chain[c + 1]) continue;
                    mesh.Indices.AddRange([loop[j], chain[c], chain[c + 1]]);
                    made++;
                }
            }
            for (var i = 0; !grid && i < n; i++)
            {
                var j = (i + 1) % n;
                if (target[i] < 0 || target[j] < 0) continue;
                Vector3 pi = mesh.Positions[target[i]], pj = mesh.Positions[target[j]];
                // Far apart (from one road to another: where the land between a helix's turns goes from the lower one's
                // edge to the upper one's underside): only if the piece of land it makes stays out of the rooms (not
                // across a road), and not farther than six gaps.
                bool Bridge()
                {
                    if (Vector3.Distance(pi, pj) > 6 * gap) return false;
                    Vector3 ri = mesh.Positions[loop[i]], rj = mesh.Positions[loop[j]];
                    foreach (var (u, v) in new[] { (0.33f, 0.33f), (0.5f, 0.25f), (0.25f, 0.5f), (0.5f, 0.5f) })
                    {
                        if (room(ri + (rj - ri) * u + (pi - ri) * v) < -3) return false;
                        if (room(rj + (pi - rj) * u + (pj - rj) * v) < -3) return false;
                    }
                    return true;
                }
                if (Vector3.Distance(pi, pj) > 3 * gap && !Bridge())
                {
                    if (Near(mesh.Positions[loop[i]])) Console.WriteLine($"  rim {mesh.Positions[loop[i]]} to {mesh.Positions[loop[j]]} not joined: their road points {Vector3.Distance(pi, pj):0.0} m apart ({pi}, {pj})");
                    continue;
                }
                mesh.Indices.AddRange([loop[j], loop[i], target[i]]);
                made++;
                // The road side: from one road point to the next, through the edge's own vertices between them where
                // both lie on the same edge (the strip then follows the road's edge exactly, not a chord across it).
                var chain = new List<int> { target[i] };
                if (onEdge[i] is { } ei && onEdge[j] is { } ej && ei.Edge == ej.Edge && MathF.Abs(ej.At - ei.At) < 16)
                {
                    if (ej.At > ei.At) for (var v = (int)MathF.Floor(ei.At) + 1; v <= (int)MathF.Ceiling(ej.At) - 1; v++) chain.Add(RoadVertex(ei.Edge, v));
                    else for (var v = (int)MathF.Ceiling(ei.At) - 1; v >= (int)MathF.Floor(ej.At) + 1; v--) chain.Add(RoadVertex(ei.Edge, v));
                }
                chain.Add(target[j]);
                for (var c = 0; c + 1 < chain.Count; c++)
                {
                    if (Vector3.DistanceSquared(mesh.Positions[chain[c]], mesh.Positions[chain[c + 1]]) < 1e-6f) continue;
                    mesh.Indices.AddRange([loop[j], chain[c], chain[c + 1]]);
                    made++;
                }
            }
            // An open road's end: a run of unjoined rim vertices, all at the end, between two joined ones: the window
            // over the end's deck, closed by a fan from its middle (in the rim's order back, then along the road).
            for (var i = 0; !stitch && i < n; i++)
            {
                var j = (i + 1) % n;
                if (target[i] < 0 || target[j] >= 0) continue;
                var run = new List<int>();
                var k = j;
                while (target[k] < 0 && run.Count < n) { run.Add(k); k = (k + 1) % n; }
                if (run.Count == n || run.Count > 24) continue;
                // (At an open road's end; or one or two vertices only, anywhere: a single unjoined one between joined ones.)
                if (run.Count > 2 && !run.All(r => AtEnd(mesh.Positions[loop[r]]))) continue;
                // The window: from the joined vertex after the run, back along the rim to the one before it, then
                // its road point and the other's.
                var window = new List<int> { loop[k] };
                for (var r = run.Count - 1; r >= 0; r--) window.Add(loop[run[r]]);
                window.Add(loop[i]);
                window.Add(target[i]);
                if (Vector3.DistanceSquared(mesh.Positions[target[i]], mesh.Positions[target[k]]) > 1e-4f) window.Add(target[k]);
                var middle = window.Aggregate(Vector3.Zero, (sum, v) => sum + mesh.Positions[v]) / window.Count;
                if (window.Any(v => Vector3.Distance(middle, mesh.Positions[v]) > 6 * gap)) continue;
                var centre = mesh.Positions.Count;
                mesh.Positions.Add(middle);
                mesh.Pinned.Add(false);
                for (var w = 0; w < window.Count; w++)
                {
                    mesh.Indices.AddRange([window[w], window[(w + 1) % window.Count], centre]);
                    made++;
                }
                if (Near(middle)) Console.WriteLine($"  end window of {run.Count} rim vertices closed at {middle}");
            }
        }
        // Close roads (no land left between them, the gap cut it all away: a loop's entry and exit side by side): edges
        // no strip uses, each node joined to the nearest free edge node of another road (or of the same road far along
        // it) that it faces, within two gaps and a bit, where the line between them stays out of the rooms and off the
        // decks and the land would be there (its middle not over the land); runs of such pairs zipped edge to edge.
        var closeStrips = 0;
        if (grid && !stitch)
        {
            var reachClose = 2 * gap + 2;
            // Nodes a close-roads strip has taken (each only once).
            var taken = new HashSet<(int, int, int)>();
            // Of those, the ones inside a strip (not its ends): no other strip may reach them.
            var inside = new HashSet<(int, int, int)>();
            bool Free(int rb, int k, int j) => !nodeVertex.ContainsKey((rb, k, j));
            // An edge node's outward direction: from the other edge across its ring.
            (int Ribbon, int K, int J)? Partner(int rb, int k, int j)
            {
                var p = ribbons[rb].Cross[k][j];
                var outward = OutOf(rb, k, j);
                (int, int, int)? best = null;
                var bestDistance = reachClose;
                var why = Environment.GetEnvironmentVariable("TMSPLINE_CLOSE_DIAG") == "on" && Near(p) ? new Dictionary<string, int>() : null;
                void No(string reason) { if (why is not null) why[reason] = why.GetValueOrDefault(reason) + 1; }
                var cells = (int)MathF.Ceiling((reachClose + 20) / EdgeBucket);
                int bx = (int)MathF.Floor(p.X / EdgeBucket), bz = (int)MathF.Floor(p.Z / EdgeBucket);
                for (var x = bx - cells; x <= bx + cells; x++)
                    for (var z = bz - cells; z <= bz + cells; z++)
                    {
                        if (!ribbonBuckets.TryGetValue((x, z), out var list)) continue;
                        foreach (var (rb2, k2) in list)
                        {
                            // (Not this road's own stretch here: far along it, or another road.)
                            if (rb2 == rb && MathF.Abs(ribbons[rb].Along[k2] - ribbons[rb].Along[k]) < 4 * gap) continue;
                            foreach (var j2 in new[] { 0, Across })
                            {
                                // (The partner may be a node a land strip uses, or a close-roads strip's end; not
                                // one inside a close-roads strip.)
                                if (inside.Contains((rb2, k2, j2))) continue;
                                var q = ribbons[rb2].Cross[k2][j2];
                                var d = Vector3.Distance(p, q);
                                if (d >= bestDistance || d < 0.05f) { No("far"); continue; }
                                // Facing each other: each outward towards the other.
                                // (Each outward towards the other: the edges face; where they cross over each other, the
                                // holes either side are the loops' to fill, not a fan up a loop.)
                                var towards = Vector3.Normalize(q - p);
                                if (Vector3.Dot(towards, outward) <= 0 || Vector3.Dot(-towards, OutOf(rb2, k2, j2)) <= 0) { No("facing"); continue; }
                                // (The land would be there: at least its lower end at the land, under a road coming down to it.)
                                if (underLand is not null && !underLand(p.Y < q.Y ? p : q)) { No("over the land"); continue; }
                                if (!Clear(room, p, q)) { No("room"); continue; }
                                var offDeck = true;
                                for (var t = 1; t < 8 && offDeck; t++)
                                {
                                    var m = Vector3.Lerp(p, q, t / 8f);
                                    if (Vector3.Distance(m, p) > 1 && Vector3.Distance(m, q) > 1 && surface(m).Distance < 0.3f) offDeck = false;
                                }
                                if (!offDeck) { No("deck"); continue; }
                                bestDistance = d;
                                best = (rb2, k2, j2);
                            }
                        }
                    }
                if (why is not null && best is null) Console.WriteLine($"  no partner for road {rb} ring {k} edge {j}: {string.Join(", ", why.Select(kv => $"{kv.Key} {kv.Value}"))}");
                return best;
            }
            for (var rb = 0; rb < ribbons.Count; rb++)
                foreach (var j in new[] { 0, Across })
                {
                    var count = ribbons[rb].Cross.Count;
                    var partner = new (int Ribbon, int K, int J)?[count];
                    for (var k = 0; k < count; k++)
                    {
                        if (Free(rb, k, j)) partner[k] = Partner(rb, k, j);
                        if (Environment.GetEnvironmentVariable("TMSPLINE_CLOSE_DIAG") == "on" && Near(ribbons[rb].Cross[k][j])) Console.WriteLine($"  edge node road {rb} ring {k} edge {j} at {ribbons[rb].Cross[k][j]}: {(Free(rb, k, j) ? (partner[k] is { } pp ? $"paired with road {pp.Ribbon} ring {pp.K} edge {pp.J}" : "free, no partner") : "used by a strip")}");
                    }
                    // Runs: consecutive nodes paired to the same edge, its rings going one way.
                    for (var k = 0; k < count; k++)
                        if (taken.Contains((rb, k, j)) || partner[k] is { } pk && inside.Contains(pk)) partner[k] = null;
                    var k0 = 0;
                    while (k0 < count)
                    {
                        if (partner[k0] is not { } first) { k0++; continue; }
                        var k1 = k0;
                        while (k1 + 1 < count && partner[k1 + 1] is { } nx && nx.Ribbon == first.Ribbon && nx.J == first.J
                               && MathF.Abs(nx.K - partner[k1]!.Value.K) <= 3) k1++;
                        if (k1 > k0 || partner[k0]!.Value.K != first.K)
                        {
                            // This edge's stretch and the other's, the other's in the order matching this one's.
                            var a = Enumerable.Range(k0, k1 - k0 + 1).Select(k => Node(rb, k, j)).ToList();
                            int b0 = partner[k0]!.Value.K, b1 = partner[k1]!.Value.K, dir = Math.Sign(b1 - b0);
                            var b = new List<int>();
                            for (var k = b0; ; k += dir == 0 ? 1 : dir)
                            {
                                b.Add(Node(first.Ribbon, k, first.J));
                                taken.Add((first.Ribbon, k, first.J));
                                if (k != b0 && k != b1) inside.Add((first.Ribbon, k, first.J));
                                if (k == b1 || dir == 0) break;
                            }
                            for (var k = k0; k <= k1; k++)
                            {
                                taken.Add((rb, k, j));
                                if (k != k0 && k != k1) inside.Add((rb, k, j));
                            }
                            // (Later runs of this edge: not into what this one took.)
                            for (var k = k1 + 1; k < count; k++)
                                if (partner[k] is { } pk && inside.Contains(pk)) partner[k] = null;
                            // Zip, the shorter diagonal first; each triangle facing the way the roads' surfaces face.
                            var up = ribbonNormals[rb][k0][j] + ribbonNormals[first.Ribbon][b0][first.J];
                            void Add(int x, int y, int z)
                            {
                                Vector3 px = mesh.Positions[x], py = mesh.Positions[y], pz = mesh.Positions[z];
                                if (Vector3.Dot(Vector3.Cross(py - px, pz - px), up) < 0) (y, z) = (z, y);
                                mesh.Indices.AddRange([x, y, z]);
                                closeStrips++;
                                made++;
                            }
                            int ia = 0, ib = 0;
                            while (ia < a.Count - 1 || ib < b.Count - 1)
                            {
                                bool stepA = ib == b.Count - 1 || (ia < a.Count - 1 && Vector3.DistanceSquared(mesh.Positions[a[ia + 1]], mesh.Positions[b[ib]]) <= Vector3.DistanceSquared(mesh.Positions[a[ia]], mesh.Positions[b[ib + 1]]));
                                if (stepA) { Add(a[ia], a[ia + 1], b[ib]); ia++; }
                                else { Add(a[ia], b[ib], b[ib + 1]); ib++; }
                            }
                            if (Near(mesh.Positions[a[0]])) Console.WriteLine($"  close roads: road {rb} rings {k0}..{k1} (edge {j}) to road {first.Ribbon} rings {b0}..{b1} (edge {first.J})");
                        }
                        k0 = k1 + 1;
                    }
                }
            if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  close roads: {closeStrips} strip triangles between road edges");
        }
        // Two switches meeting at a node (one piece of land's ends at it, another's starts there), both from or to
        // the same ring of a road: the space between them (the window behind a road's start, up to another road over
        // it, between two pieces of land) closed, along that ring between their nodes. A fan over the switches' far
        // nodes, the shared one and that ring's nodes between.
        var windows = 0;
        foreach (var into in bridges)
            foreach (var outOf in bridges)
            {
                if (into.To != outOf.From) continue;
                var (y, x, z) = (outOf.To, into.To, into.From);
                // (into: z to x; outOf: x to y. Their strips' road sides run z->x and x->y; the window turns the other way round them.)
                if (y.Ribbon != z.Ribbon || y.K != z.K || y == z) continue;
                var polygon = new List<int> { Node(y.Ribbon, y.K, y.J), Node(x.Ribbon, x.K, x.J), Node(z.Ribbon, z.K, z.J) };
                for (var c = z.J; c != y.J;) { c += Math.Sign(y.J - z.J); if (c != y.J) polygon.Add(Node(z.Ribbon, z.K, c)); }
                var middle = polygon.Aggregate(Vector3.Zero, (sum, v) => sum + mesh.Positions[v]) / polygon.Count;
                var centre = mesh.Positions.Count;
                mesh.Positions.Add(middle);
                mesh.Pinned.Add(false);
                for (var w = 0; w < polygon.Count; w++) mesh.Indices.AddRange([polygon[w], polygon[(w + 1) % polygon.Count], centre]);
                made += polygon.Count;
                windows++;
                if (Near(middle)) Console.WriteLine($"  window between two switches closed at {middle} ({polygon.Count} corners)");
            }
        if (grid && Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  grid: {bridges.Count} switches bridged, {windows} windows between them closed");
        // Holes as loops (the owner's idea): every open edge of the land that is not land meeting a road along the grid,
        // closed by the shortest way back from its far end to its start, along other open land edges and the roads'
        // grids (their edges, undersides and ends; not their driving surface), no longer round than MaxHole; that loop
        // filled as one polygon, unless the fill would pass through a road's room (a tunnel's mouth stays open).
        var loopFills = 0;
        if (grid && !stitch && Environment.GetEnvironmentVariable("TMSPLINE_HOLES") != "off")
        {
            const float MaxHole = 40;
            var roadNode = nodeVertex.ToDictionary(kv => kv.Value, kv => (Ribbon: kv.Key.Item1, K: kv.Key.Item2, J: kv.Key.Item3));
            var tried = new HashSet<(int, int)>();
            for (var pass = 0; pass < 200; pass++)
            {
            var progress = false;
            // The open edges, each as its triangle runs it (a to b).
            var uses = new Dictionary<(int, int), int>();
            var runs = new Dictionary<(int, int), bool>();
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    uses[key] = uses.GetValueOrDefault(key) + 1;
                    runs[(a, b)] = true;
                }
            bool Seam(int a, int b) => roadNode.TryGetValue(a, out var na) && roadNode.TryGetValue(b, out var nb) && na.Ribbon == nb.Ribbon
                && Math.Abs(na.K - nb.K) + Math.Abs(na.J - nb.J) == 1;
            var open = new Dictionary<int, List<int>>();
            foreach (var ((a, b), n) in uses)
            {
                if (n != 1 || Seam(a, b)) continue;
                if (!open.TryGetValue(a, out var la)) open[a] = la = [];
                if (!open.TryGetValue(b, out var lb)) open[b] = lb = [];
                la.Add(b);
                lb.Add(a);
            }
            // The graph's places: the mesh's vertices (>= 0), and grid nodes not yet in it (< 0, made when a fill uses them).
            var virtualNodes = new List<(int Ribbon, int K, int J)>();
            var virtualIndex = new Dictionary<(int, int, int), int>();
            int Place((int Ribbon, int K, int J) n)
            {
                if (nodeVertex.TryGetValue(n, out var id)) return id;
                if (!virtualIndex.TryGetValue(n, out var v)) { v = -1 - virtualNodes.Count; virtualNodes.Add(n); virtualIndex[n] = v; }
                return v;
            }
            (int Ribbon, int K, int J)? GridOf(int place) => place < 0 ? virtualNodes[-1 - place] : roadNode.TryGetValue(place, out var n) ? n : null;
            Vector3 PosOf(int place)
            {
                if (place >= 0) return mesh.Positions[place];
                var (rb, k, j) = virtualNodes[-1 - place];
                return j == 0 || j == Across ? ribbons[rb].Cross[k][j] : ribbons[rb].Cross[k][j] - ribbonNormals[rb][k][j] * 0.15f;
            }
            IEnumerable<int> Neighbours(int place)
            {
                if (place >= 0 && open.TryGetValue(place, out var list)) foreach (var o in list) yield return o;
                if (GridOf(place) is not { } g) yield break;
                var (rb, k, j) = g;
                var count = ribbons[rb].Cross.Count;
                foreach (var (dk, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int k2 = k + dk, j2 = j + dj;
                    if (k2 < 0 || k2 >= count || j2 < 0 || j2 > Across) continue;
                    // (Along the edges, or across a road's end, or from under the road out to its nearer edge; not across its
                    // underside from edge to edge: a plug's loop wrapped round the
                    // road then and had no filling off it.)
                    if (dk != 0 && j != 0 && j != Across) continue;
                    if (dj != 0 && k != 0 && k != count - 1 && Math.Min(j2, Across - j2) >= Math.Min(j, Across - j)) continue;
                    // (Along a ring between rings only at the edges, the ends or underneath: grid lines all lie on the
                    // road; a grid line the land already meets is no hole's side.)
                    var other = Place((rb, k2, j2));
                    if (place >= 0 && other >= 0 && uses.TryGetValue(place < other ? (place, other) : (other, place), out var n) && n >= 1 && Seam(place, other)) continue;
                    yield return other;
                }
            }
            foreach (var (a, list) in open.ToList())
            {
                foreach (var b in list.ToList())
                {
                    if (a > b || tried.Contains((a, b))) continue;
                    tried.Add((a, b));
                    // The way back from b to a, not over the edge itself: shortest, at most MaxHole round with it.
                    var first = Vector3.Distance(PosOf(a), PosOf(b));
                    var dist = new Dictionary<int, float> { [b] = 0 };
                    var from = new Dictionary<int, int>();
                    var queue = new PriorityQueue<int, float>();
                    queue.Enqueue(b, 0);
                    var found = false;
                    while (queue.TryDequeue(out var at, out var d))
                    {
                        if (d > dist[at]) continue;
                        if (at == a) { found = true; break; }
                        if (d + first > MaxHole) break;
                        foreach (var nb in Neighbours(at))
                        {
                            if (at == b && nb == a) continue;
                            var nd = d + Vector3.Distance(PosOf(at), PosOf(nb));
                            if (nd + first > MaxHole || (dist.TryGetValue(nb, out var old) && old <= nd)) continue;
                            dist[nb] = nd;
                            from[nb] = at;
                            queue.Enqueue(nb, nd);
                        }
                    }
                    if (!found) continue;
                    // The loop: a, b, then the way back to a.
                    var cycle = new List<int> { a };
                    var path = new List<int>();
                    for (var at = a; at != b; at = from[at]) path.Add(from[at]);
                    // (path runs from a's predecessor back towards b: reverse it to go b ... before a.)
                    path.Reverse();
                    cycle.AddRange(path);
                    if (cycle.Count < 3 || cycle.Distinct().Count() != cycle.Count) continue;
                    // A plug only (the owner's: where two roads overlap): its loop runs along two road parts, two roads
                    // or one road far along it (a loop's entry and exit); not along one road's edge and the land.
                    var parts = cycle.Select(GridOf).Where(g => g is not null).Select(g => g!.Value).ToList();
                    var twoParts = parts.Any(x => parts.Any(y => x.Ribbon != y.Ribbon || MathF.Abs(ribbons[x.Ribbon].Along[x.K] - ribbons[y.Ribbon].Along[y.K]) > 4 * gap));
                    if (!twoParts) continue;
                    var polygon = cycle.Select(PosOf).ToList();
                    // Ear clipping in the loop's best-fit plane (Newell's normal).
                    var normal = Vector3.Zero;
                    for (var i = 0; i < polygon.Count; i++)
                    {
                        Vector3 u = polygon[i], v = polygon[(i + 1) % polygon.Count];
                        normal += new Vector3((u.Y - v.Y) * (u.Z + v.Z), (u.Z - v.Z) * (u.X + v.X), (u.X - v.X) * (u.Y + v.Y));
                    }
                    if (normal.LengthSquared() < 1e-8f) continue;
                    normal = Vector3.Normalize(normal);
                    var axisU = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
                    var axisV = Vector3.Cross(normal, axisU);
                    var flat = polygon.Select(q => new Vector2(Vector3.Dot(q, axisU), Vector3.Dot(q, axisV))).ToList();
                    var remaining = Enumerable.Range(0, cycle.Count).ToList();
                    var triangles = new List<(int, int, int)>();
                    static float Cross2(Vector2 o, Vector2 p, Vector2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
                    // An ear off the roads in space too (flat in the loop's plane it may still cut across a road's deck
                    // where the loop bends round it): no point inside it, away from its corners, on a road or in its room.
                    int inRoom = 0, onDeck = 0;
                    bool OffRoad(Vector3 x, Vector3 y, Vector3 z) => Touching(x, y, z) == 0;
                    int Touching(Vector3 x, Vector3 y, Vector3 z)
                    {
                        var bad = 0;
                        for (var u = 1; u < 5; u++)
                            for (var v = 1; u + v < 5; v++)
                            {
                                var q = x + (y - x) * (u / 5f) + (z - x) * (v / 5f);
                                if (MathF.Min(Vector3.Distance(q, x), MathF.Min(Vector3.Distance(q, y), Vector3.Distance(q, z))) < 0.75f) continue;
                                if (room(q) < -0.5f) { inRoom++; bad++; continue; }
                                // (On the deck, between its edges: not just beside an edge it runs along.)
                                var (d, _, l, r) = surface(q);
                                var lr = r - l;
                                if (d < 0.25f && lr.LengthSquared() > 1e-4f)
                                {
                                    var across = Vector3.Dot(q - l, lr) / lr.LengthSquared();
                                    if (across > 0.05f && across < 0.95f) { onDeck++; bad++; }
                                }
                            }
                        return bad;
                    }
                    var guard = 0;
                    // (Each step the ear touching the roads least: none if it can, as little as the loop's shape allows
                    // where two roads' edges cross over each other.)
                    var touching = 0;
                    while (remaining.Count > 3 && guard++ < 1000)
                    {
                        var best = -1;
                        var bestBad = int.MaxValue;
                        for (var i = 0; i < remaining.Count; i++)
                        {
                            int i0 = remaining[(i - 1 + remaining.Count) % remaining.Count], i1 = remaining[i], i2 = remaining[(i + 1) % remaining.Count];
                            if (Cross2(flat[i0], flat[i1], flat[i2]) <= 1e-6f) continue;
                            var inside = false;
                            foreach (var o in remaining)
                            {
                                if (o == i0 || o == i1 || o == i2) continue;
                                if (Cross2(flat[i0], flat[i1], flat[o]) >= 0 && Cross2(flat[i1], flat[i2], flat[o]) >= 0 && Cross2(flat[i2], flat[i0], flat[o]) >= 0) { inside = true; break; }
                            }
                            if (inside) continue;
                            var bad = Touching(polygon[i0], polygon[i1], polygon[i2]);
                            if (bad < bestBad) { bestBad = bad; best = i; if (bad == 0) break; }
                        }
                        if (best < 0) break;
                        int b0 = remaining[(best - 1 + remaining.Count) % remaining.Count], b1 = remaining[best], b2 = remaining[(best + 1) % remaining.Count];
                        triangles.Add((b0, b1, b2));
                        touching += bestBad;
                        remaining.RemoveAt(best);
                    }
                    // A polygon round a road's edge in space has no flat triangulation off the road: then a fan, from its
                    // middle or from one of its corners, the first whose triangles all stay off the roads.
                    var apex = -1;
                    if (remaining.Count == 3)
                    {
                        touching += Touching(polygon[remaining[0]], polygon[remaining[1]], polygon[remaining[2]]);
                        triangles.Add((remaining[0], remaining[1], remaining[2]));
                        if (touching > 0 && Near(PosOf(a))) Console.WriteLine($"  hole of {cycle.Count} corners at {PosOf(a)}: filled touching a road at {touching} sample points");
                    }
                    else
                    {
                        triangles.Clear();
                        var middle = polygon.Aggregate(Vector3.Zero, (sum, q) => sum + q) / polygon.Count;
                        var n = polygon.Count;
                        if (Enumerable.Range(0, n).All(i => OffRoad(polygon[i], polygon[(i + 1) % n], middle)))
                            apex = n;
                        else
                            for (var c = 0; c < n && apex < 0; c++)
                                if (Enumerable.Range(0, n).Where(i => i != c && (i + 1) % n != c).All(i => OffRoad(polygon[c], polygon[i], polygon[(i + 1) % n])))
                                    apex = c;
                        if (apex < 0)
                        {
                            if (Near(PosOf(a)) && cycle.Count > 6) Console.WriteLine($"  hole of {cycle.Count} corners at {PosOf(a)} not filled: no triangulation off the roads (ears in a room {inRoom}, on a deck {onDeck})");
                            continue;
                        }
                        if (apex == n) { cycle.Add(int.MinValue); polygon.Add(middle); }
                        for (var i = 0; i < n; i++)
                            if (i != apex && (i + 1) % n != apex) triangles.Add((i, (i + 1) % n, apex));
                    }
                    // Not through a road's room.
                    if (triangles.Any(tr => room((polygon[tr.Item1] + polygon[tr.Item2] + polygon[tr.Item3]) / 3) < -1)) continue;
                    var centreId = -1;
                    int CentreVertex(Vector3 q)
                    {
                        if (centreId < 0) { centreId = mesh.Positions.Count; mesh.Positions.Add(q); mesh.Pinned.Add(false); }
                        return centreId;
                    }
                    // Wound against the edge a-b's own triangle: if that runs a to b, the fill runs b to a.
                    var reverse = runs.ContainsKey((a, b));
                    foreach (var (x, y, z) in triangles)
                    {
                        int vx = cycle[x], vy = cycle[y], vz = cycle[z];
                        var ids = new[] { vx, vy, vz }.Select(pl => pl >= 0 ? pl : pl == int.MinValue ? CentreVertex(polygon[^1]) : Node(virtualNodes[-1 - pl].Ribbon, virtualNodes[-1 - pl].K, virtualNodes[-1 - pl].J)).ToArray();
                        // (The polygon goes a, then the way back: a's next is the way back's first, so the loop runs a->path->b->a:
                        // its edge b->a; the fill's order as clipped runs the loop's way.)
                        if (reverse) mesh.Indices.AddRange([ids[0], ids[2], ids[1]]);
                        else mesh.Indices.AddRange([ids[0], ids[1], ids[2]]);
                        made++;
                    }
                    loopFills++;
                    if (Near(PosOf(a))) Console.WriteLine($"  hole of {cycle.Count} corners ({first + dist[a]:0} m round) filled at {PosOf(a)}");
                    // (The open edges changed: start again from them.)
                    progress = true;
                    break;
                }
                if (progress) break;
            }
            if (!progress) break;
            }
        }
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  {loopFills} holes filled as loops");
        if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
            foreach (var rim in RimLoops(mesh).Where(l => l.Any(v => Near(mesh.Positions[v]))))
            {
                // The open boundary round the place, as R (a road point) and L (land), with where it runs.
                var start = rim.FindIndex(v => Near(mesh.Positions[v]));
                var text = new List<string>();
                for (var k = -6; k < 30; k++)
                {
                    var v = rim[((start + k) % rim.Count + rim.Count) % rim.Count];
                    var q = mesh.Positions[v];
                    text.Add($"{(surface(q).Distance < 0.1f ? "R" : "L")}({q.X:0},{q.Y:0},{q.Z:0})");
                }
                Console.WriteLine($"  open boundary of {rim.Count} near: {string.Join(" ", text)}");
            }
        // The advancing front: every open edge near a road that still needs a triangle (the land's open rims; the road
        // edges' and ends' pieces land is near and no strip uses), closed one triangle at a time: on a front edge, the
        // third corner from the front's vertices near it, the best first (closing a corner of the front, then small and
        // well shaped). A triangle only where it enters no road's room and crosses no deck, intersects none there is,
        // does not fold back over the triangle it grows from, has no edge longer than the reach nor a sliver's angle;
        // between road vertices only where the land would be. Until none is left that may be made.
        if (stitch)
        {
            var reachStitch = 2.5f * gap;
            var used = new HashSet<(int, int)>();
            var edgeUses = new Dictionary<(int, int), int>();
            var thirdOf = new Dictionary<(int, int), int>();
            void Record(int a, int b, int c)
            {
                foreach (var (x, y, z) in new[] { (a, b, c), (b, c, a), (c, a, b) })
                {
                    used.Add((x, y));
                    thirdOf[(x, y)] = z;
                    var key = x < y ? (x, y) : (y, x);
                    edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
                }
            }
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) Record(mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]);
            // Triangles in 8 m cells, for the intersection test.
            const float Cell = 8;
            var cellsOf = new Dictionary<(int, int, int), List<int>>();
            IEnumerable<(int, int, int)> Cells(Vector3 lo, Vector3 hi)
            {
                for (var x = (int)MathF.Floor(lo.X / Cell); x <= (int)MathF.Floor(hi.X / Cell); x++)
                    for (var y = (int)MathF.Floor(lo.Y / Cell); y <= (int)MathF.Floor(hi.Y / Cell); y++)
                        for (var z = (int)MathF.Floor(lo.Z / Cell); z <= (int)MathF.Floor(hi.Z / Cell); z++)
                            yield return (x, y, z);
            }
            void AddCells(int t)
            {
                Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
                foreach (var key in Cells(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c))))
                {
                    if (!cellsOf.TryGetValue(key, out var list)) cellsOf[key] = list = [];
                    list.Add(t);
                }
            }
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) AddCells(t);
            // Road pieces: an edge's or an end's segment between two grid nodes (on the road; no triangle on its road side).
            var roadPiece = new HashSet<(int, int)>();
            bool Piece(int a, int b) => roadPiece.Contains(a < b ? (a, b) : (b, a));
            // Terrain vertices in cells, to find road pieces land is near.
            var landCells = new Dictionary<(int, int, int), List<int>>();
            for (var t = 0; t < mesh.Indices.Count; t++)
            {
                var v = mesh.Indices[t];
                if (nodeVertex.ContainsValue(v)) continue;
                var q = mesh.Positions[v];
                var key = ((int)MathF.Floor(q.X / Cell), (int)MathF.Floor(q.Y / Cell), (int)MathF.Floor(q.Z / Cell));
                if (!landCells.TryGetValue(key, out var list)) landCells[key] = list = [];
                list.Add(v);
            }
            bool LandNear(Vector3 q)
            {
                foreach (var key in Cells(q - new Vector3(reachStitch), q + new Vector3(reachStitch)))
                    if (landCells.TryGetValue(key, out var list))
                        foreach (var v in list)
                            if (Vector3.DistanceSquared(mesh.Positions[v], q) < reachStitch * reachStitch) return true;
                return false;
            }
            // The front.
            var front = new HashSet<(int, int)>();
            foreach (var ((a, b), n) in edgeUses)
            {
                if (n != 1) continue;
                var (x, y) = used.Contains((a, b)) ? (a, b) : (b, a);
                if (Piece(x, y)) continue;
                if (MathF.Min(surface(mesh.Positions[x]).Distance, surface(mesh.Positions[y]).Distance) > reachStitch) continue;
                front.Add((y, x));
            }
            for (var rb = 0; rb < ribbons.Count; rb++)
            {
                var count = ribbons[rb].Cross.Count;
                void Segment(int k0, int j0, int k1, int j1)
                {
                    var mid = (ribbons[rb].Cross[k0][j0] + ribbons[rb].Cross[k1][j1]) / 2;
                    var known = nodeVertex.ContainsKey((rb, k0, j0)) && nodeVertex.ContainsKey((rb, k1, j1));
                    if (!known && !LandNear(mid)) return;
                    int a = Node(rb, k0, j0), b = Node(rb, k1, j1);
                    roadPiece.Add(a < b ? (a, b) : (b, a));
                    // (The land meets it from one side at most here: the side facing the way the road does.)
                    if (edgeUses.GetValueOrDefault(a < b ? (a, b) : (b, a)) > 0) return;
                    front.Add((a, b));
                    front.Add((b, a));
                }
                foreach (var j in new[] { 0, Across })
                    for (var k = 0; k + 1 < count; k++) Segment(k, j, k + 1, j);
                foreach (var k in new[] { 0, count - 1 })
                    for (var j = 0; j < Across; j++) Segment(k, j, k, j + 1);
            }
            Vector3 P(int v) => mesh.Positions[v];
            bool IsRoad(int v) => roadNodeOf.ContainsKey(v);
            // Front vertices in cells.
            var frontCells = new Dictionary<(int, int, int), HashSet<int>>();
            void AddFrontVertex(int v)
            {
                var q = P(v);
                var key = ((int)MathF.Floor(q.X / Cell), (int)MathF.Floor(q.Y / Cell), (int)MathF.Floor(q.Z / Cell));
                if (!frontCells.TryGetValue(key, out var set)) frontCells[key] = set = [];
                set.Add(v);
            }
            foreach (var (a, b) in front) { AddFrontVertex(a); AddFrontVertex(b); }
            // Segment-triangle intersection (Moller-Trumbore), strictly inside both.
            static bool SegmentHits(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c)
            {
                var d = p1 - p0;
                Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
                var det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-9f) return false;
                var f = 1 / det;
                var sv = p0 - a;
                var u = f * Vector3.Dot(sv, h);
                if (u <= 1e-4f || u >= 1 - 1e-4f) return false;
                var q = Vector3.Cross(sv, e1);
                var v = f * Vector3.Dot(d, q);
                if (v <= 1e-4f || u + v >= 1 - 1e-4f) return false;
                var t = f * Vector3.Dot(e2, q);
                return t > 1e-4f && t < 1 - 1e-4f;
            }
            bool Crosses(int u, int v, int c)
            {
                Vector3 a = P(u), b = P(v), d = P(c);
                foreach (var key in Cells(Vector3.Min(a, Vector3.Min(b, d)), Vector3.Max(a, Vector3.Max(b, d))))
                {
                    if (!cellsOf.TryGetValue(key, out var list)) continue;
                    foreach (var t in list)
                    {
                        int x = mesh.Indices[t], y = mesh.Indices[t + 1], z = mesh.Indices[t + 2];
                        Vector3 px = P(x), py = P(y), pz = P(z);
                        // Edges of the one through the other, where they share no corner.
                        if (x != u && x != v && x != c && y != u && y != v && y != c && z != u && z != v && z != c)
                        {
                            if (SegmentHits(a, b, px, py, pz) || SegmentHits(b, d, px, py, pz) || SegmentHits(d, a, px, py, pz)) return true;
                            if (SegmentHits(px, py, a, b, d) || SegmentHits(py, pz, a, b, d) || SegmentHits(pz, px, a, b, d)) return true;
                        }
                        else
                        {
                            // (Sharing a corner: only the edges away from it.)
                            if (SegmentHits(px, py, a, b, d) && x != u && x != v && x != c && y != u && y != v && y != c) return true;
                            if (SegmentHits(py, pz, a, b, d) && y != u && y != v && y != c && z != u && z != v && z != c) return true;
                            if (SegmentHits(pz, px, a, b, d) && z != u && z != v && z != c && x != u && x != v && x != c) return true;
                        }
                    }
                }
                return false;
            }
            static float MinAngle(Vector3 a, Vector3 b, Vector3 c)
            {
                static float At(Vector3 p, Vector3 q, Vector3 r) { Vector3 x = q - p, y = r - p; var d = x.Length() * y.Length(); return d < 1e-9f ? 0 : MathF.Acos(Math.Clamp(Vector3.Dot(x, y) / d, -1, 1)); }
                return MathF.Min(At(a, b, c), MathF.Min(At(b, c, a), At(c, a, b))) * 180 / MathF.PI;
            }
            // Whether a triangle on the front edge u->v with its third corner c may be made.
            bool Valid(int u, int v, int c)
            {
                if (c == u || c == v || used.Contains((v, c)) || used.Contains((c, u))) return false;
                if (edgeUses.GetValueOrDefault(v < c ? (v, c) : (c, v)) >= 2 || edgeUses.GetValueOrDefault(c < u ? (c, u) : (u, c)) >= 2) return false;
                Vector3 a = P(u), b = P(v), d = P(c);
                if (Vector3.Distance(b, d) > reachStitch || Vector3.Distance(d, a) > reachStitch) return false;
                if (MinAngle(a, b, d) < 8) return false;
                // Not folding back over the triangle the front edge belongs to (it lies on v->u's side).
                var mid = (a + b) / 2;
                var e = b - a;
                if (thirdOf.TryGetValue((v, u), out var t))
                {
                    var o = mid - P(t);
                    o -= e * (Vector3.Dot(o, e) / MathF.Max(e.LengthSquared(), 1e-9f));
                    if (Vector3.Dot(d - mid, o) <= 0) return false;
                }
                else if (Piece(u, v) && roadNodeOf.TryGetValue(u, out var nu))
                {
                    // On a road piece: out from the road (not across it), facing the way the road does.
                    var o = OutOf(nu.Ribbon, nu.K, nu.J);
                    if (nu.J != 0 && nu.J != Across) o = nu.K == 0 ? -Tangent(nu.Ribbon, 0) : Tangent(nu.Ribbon, ribbons[nu.Ribbon].Cross.Count - 1);
                    if (Vector3.Dot(d - mid, o) <= 0) return false;
                    if (Vector3.Dot(Vector3.Cross(b - a, d - a), ribbonNormals[nu.Ribbon][nu.K][nu.J]) < 0) return false;
                }
                // Between road vertices only: only where the land would be.
                if (IsRoad(u) && IsRoad(v) && IsRoad(c) && underLand is not null)
                {
                    var low = a.Y < b.Y ? (a.Y < d.Y ? a : d) : (b.Y < d.Y ? b : d);
                    if (!underLand(low)) return false;
                }
                if (!ClearOfRoads(a, b, d)) return false;
                return !Crosses(u, v, c);
            }
            float Score(int u, int v, int c)
            {
                Vector3 a = P(u), b = P(v), d = P(c);
                var perimeter = Vector3.Distance(a, b) + Vector3.Distance(b, d) + Vector3.Distance(d, a);
                // Closing a corner of the front (c on a front edge from v, or into u): first.
                var closes = front.Contains((c, v)) || front.Contains((u, c));
                return perimeter * (closes ? 0.3f : 1);
            }
            var queue = new PriorityQueue<(int U, int V, int C), float>();
            void Propose((int, int) edge)
            {
                var (u, v) = edge;
                Vector3 a = P(u), b = P(v);
                var mid = (a + b) / 2;
                var best = -1;
                var bestScore = float.MaxValue;
                foreach (var key in Cells(mid - new Vector3(reachStitch), mid + new Vector3(reachStitch)))
                {
                    if (!frontCells.TryGetValue(key, out var set)) continue;
                    foreach (var c in set)
                    {
                        if (c == u || c == v || Vector3.Distance(P(c), mid) > reachStitch) continue;
                        var sc = Score(u, v, c);
                        if (sc >= bestScore) continue;
                        if (!Valid(u, v, c)) continue;
                        bestScore = sc;
                        best = c;
                    }
                }
                if (best >= 0) queue.Enqueue((u, v, best), bestScore);
            }
            foreach (var edge in front.ToList()) Propose(edge);
            var stitched = 0;
            // (TMSPLINE_STITCH=none: the front only, nothing made: what is open, drawn in the editor.)
            var onlyFront = Environment.GetEnvironmentVariable("TMSPLINE_STITCH") == "none";
            while (!onlyFront && queue.TryDequeue(out var cand, out _) && stitched < 20000)
            {
                var (u, v, c) = cand;
                if (!front.Contains((u, v))) continue;
                if (!Valid(u, v, c)) { Propose((u, v)); continue; }
                mesh.Indices.AddRange([u, v, c]);
                Record(u, v, c);
                AddCells(mesh.Indices.Count - 3);
                stitched++;
                made++;
                front.Remove((u, v));
                foreach (var (x, y) in new[] { (v, c), (c, u) })
                {
                    if (front.Remove((x, y))) continue;
                    // A new front edge, unless it is a road piece (its other side is the road) or closed already.
                    if (Piece(x, y) || edgeUses.GetValueOrDefault(x < y ? (x, y) : (y, x)) >= 2) { front.Remove((y, x)); continue; }
                    front.Add((y, x));
                    AddFrontVertex(x);
                    AddFrontVertex(y);
                    Propose((y, x));
                }
            }
            if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  front: {stitched} triangles, {front.Count} front edges left open");

            if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                foreach (var (a, b) in front.Where(e => Near(P(e.Item1))).Take(30))
                    Console.WriteLine($"    open: {P(a)} to {P(b)}{(Piece(a, b) ? " (road)" : "")}");
        }
        // What is left open and small (a rim shorter than four gaps: no road's opening is) is filled, with a fan from
        // its middle: the patterns the strips do not cover (where the land between a helix's turns meets a road's start).
        var filled = 0;
        foreach (var hole in stitch ? [] : RimLoops(mesh))
        {
            var perimeter = 0f;
            for (var k = 0; k < hole.Count; k++) perimeter += Vector3.Distance(mesh.Positions[hole[k]], mesh.Positions[hole[(k + 1) % hole.Count]]);
            if (hole.Any(v => Near(mesh.Positions[v])) && perimeter < 400) Console.WriteLine($"  open rim of {hole.Count} vertices, {perimeter:0} m round, through: {string.Join(" ", hole.Where((_, k) => k % Math.Max(1, hole.Count / 16) == 0).Select(v => $"({mesh.Positions[v].X:0},{mesh.Positions[v].Y:0},{mesh.Positions[v].Z:0})"))}");
            if (perimeter >= 4 * gap) continue;
            var middle = hole.Aggregate(Vector3.Zero, (sum, v) => sum + mesh.Positions[v]) / hole.Count;
            var centre = mesh.Positions.Count;
            mesh.Positions.Add(middle);
            mesh.Pinned.Add(false);
            for (var k = 0; k < hole.Count; k++) mesh.Indices.AddRange([hole[(k + 1) % hole.Count], hole[k], centre]);
            made += hole.Count;
            filled++;
            if (Near(middle)) Console.WriteLine($"  hole of {hole.Count} rim vertices ({perimeter:0.0} m round) filled at {middle}");
        }
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  {filled} small holes filled");
        if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
        {
            // Orientation: an edge two triangles run the same way (one of them turned over), and edges of three or more.
            var runs = new Dictionary<(int, int), int>();
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    var key = (mesh.Indices[t + k], mesh.Indices[t + (k + 1) % 3]);
                    runs[key] = runs.GetValueOrDefault(key) + 1;
                }
            var same = runs.Where(kv => kv.Value > 1 || (kv.Key.Item1 != kv.Key.Item2 && false)).ToList();
            var undirected = new Dictionary<(int, int), int>();
            foreach (var (key, c) in runs) { var u = key.Item1 < key.Item2 ? key : (key.Item2, key.Item1); undirected[u] = undirected.GetValueOrDefault(u) + c; }
            Console.WriteLine($"  edges run the same way by two triangles: {same.Count}; edges of three or more triangles: {undirected.Count(kv => kv.Value > 2)}");
            foreach (var kv in same.Where(kv => Near(mesh.Positions[kv.Key.Item1])).Take(12)) Console.WriteLine($"    same way: {mesh.Positions[kv.Key.Item1]} to {mesh.Positions[kv.Key.Item2]}");
        }
        if (grid && Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  grid: {ribbons.Count} roads, {Across} across; {breaks} breaks between runs");
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  {targets} road points, {snaps} of them to a vertex on an edge of the road's mesh (their road vertex up to {farthest:0.00} m away), {edges.Count} edges at {roadQuality}");
        LastOpen = failedEdges.ToArray();
        return made;
    }

    /// <summary>The point of a triangle nearest p (Ericson), with its weights on b (v) and c (w).</summary>
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float v, out float w)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { v = 0; w = 0; return a; }
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { v = 1; w = 0; return b; }
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { v = d1 / (d1 - d3); w = 0; return a + ab * v; }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { v = 0; w = 1; return c; }
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { v = 0; w = d2 / (d2 - d6); return a + ac * w; }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); v = 1 - w; return b + (c - b) * w; }
        var denom = 1 / (va + vb + vc);
        v = vb * denom;
        w = vc * denom;
        return a + ab * v + ac * w;
    }

    /// <summary>
    /// Whether the straight line from a place to a point on a road stays out of the roads' rooms (the owner's rule: no
    /// seam through the road; at a tunnel's mouth the land over it would otherwise reach down through it). The last
    /// stretch before the road is left out: the room reaches a little past the deck's edge and under it.
    /// </summary>
    private static bool Clear(Func<Vector3, float> room, Vector3 from, Vector3 to)
    {
        var length = Vector3.Distance(from, to);
        var steps = Math.Max(4, (int)MathF.Ceiling(length / 0.5f));
        for (var k = 1; k < steps; k++)
        {
            if (length * (1 - k / (float)steps) < 1.5f) break;
            // (Deeper than a curved vault's sag over a line along its wall: through the room, not along it.)
            if (room(Vector3.Lerp(from, to, k / (float)steps)) < -2f) return false;
        }
        return true;
    }

    /// <summary>
    /// Vertices where two pieces of land touch at a point only (two rims meeting there: the land round a road and the
    /// land between a helix's turns): one copy per fan of triangles round them, so each rim is a loop of its own and
    /// the strips on one side do not take the other's for a neighbour. Returns how many were split.
    /// </summary>
    private static int Unpinch(AdaptiveMesh mesh)
    {
        var around = new Dictionary<int, List<int>>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                if (!around.TryGetValue(mesh.Indices[t + k], out var list)) around[mesh.Indices[t + k]] = list = [];
                list.Add(t);
            }
        var split = 0;
        foreach (var (v, triangles) in around)
        {
            if (triangles.Count < 2) continue;
            // Fans: triangles round v joined where they share an edge from v (the same other corner).
            var parent = Enumerable.Range(0, triangles.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var byCorner = new Dictionary<int, int>();
            for (var i = 0; i < triangles.Count; i++)
                for (var k = 0; k < 3; k++)
                {
                    var c = mesh.Indices[triangles[i] + k];
                    if (c == v) continue;
                    if (byCorner.TryGetValue(c, out var j)) parent[Find(i)] = Find(j);
                    else byCorner[c] = i;
                }
            var copies = new Dictionary<int, int>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var root = Find(i);
                if (root == Find(0)) continue;
                if (!copies.TryGetValue(root, out var copy))
                {
                    if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on") Console.WriteLine($"  pinch at {mesh.Positions[v]}: {triangles.Count} triangles round it");
                    copy = mesh.Positions.Count;
                    mesh.Positions.Add(mesh.Positions[v]);
                    mesh.Pinned.Add(false);
                    copies[root] = copy;
                    split++;
                }
                for (var k = 0; k < 3; k++)
                    if (mesh.Indices[triangles[i] + k] == v) mesh.Indices[triangles[i] + k] = copy;
            }
        }
        return split;
    }

    /// <summary>
    /// Rim edges longer than the gap split (their triangle with them, the land's shape unchanged), again until none is:
    /// each piece of a long rim gets its own road point (a long one between a helix's turns otherwise reached from one
    /// turn to the other, and was left open).
    /// </summary>
    private static void SplitLongRims(AdaptiveMesh mesh, float gap)
    {
        for (var round = 0; round < 6; round++)
        {
            var count = new Dictionary<(int, int), int>();
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    count[key] = count.GetValueOrDefault(key) + 1;
                }
            var split = 0;
            var triangles = mesh.Indices.Count;
            for (var t = 0; t + 2 < triangles; t += 3)
            {
                // The triangle's longest rim edge, if longer than the gap.
                var best = -1;
                var length = gap;
                for (var k = 0; k < 3; k++)
                {
                    int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                    if (count[a < b ? (a, b) : (b, a)] != 1) continue;
                    var l = Vector3.Distance(mesh.Positions[a], mesh.Positions[b]);
                    if (l > length) { length = l; best = k; }
                }
                if (best < 0) continue;
                int p = mesh.Indices[t + best], q = mesh.Indices[t + (best + 1) % 3], r = mesh.Indices[t + (best + 2) % 3];
                var m = mesh.Positions.Count;
                mesh.Positions.Add((mesh.Positions[p] + mesh.Positions[q]) / 2);
                mesh.Pinned.Add(false);
                // (p, q, r) becomes (p, m, r) and (m, q, r).
                mesh.Indices[t] = p;
                mesh.Indices[t + 1] = m;
                mesh.Indices[t + 2] = r;
                mesh.Indices.AddRange([m, q, r]);
                split++;
            }
            if (split == 0) break;
        }
    }

    /// <summary>The rim: boundary edges (used by one triangle), each from a to b as the triangle winds, chained into loops.</summary>
    private static List<List<int>> RimLoops(AdaptiveMesh mesh)
    {
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        // Walked edge by edge, each boundary edge once: where rims touch (a vertex with two ways out) every walk still
        // closes, since each vertex has as many rim edges in as out (a loop may pass such a vertex twice).
        var outgoing = new Dictionary<int, List<int>>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                if (count[a < b ? (a, b) : (b, a)] != 1) continue;
                if (!outgoing.TryGetValue(a, out var list)) outgoing[a] = list = [];
                list.Add(b);
            }
        var loops = new List<List<int>>();
        foreach (var start in outgoing.Keys.ToList())
            while (outgoing[start].Count > 0)
            {
                var loop = new List<int>();
                var v = start;
                while (outgoing.TryGetValue(v, out var ways) && ways.Count > 0)
                {
                    loop.Add(v);
                    var w = ways[^1];
                    ways.RemoveAt(ways.Count - 1);
                    v = w;
                    if (v == start) break;
                }
                if (loop.Count >= 3 && v == start) loops.Add(loop);
            }
        return loops;
    }

    /// <summary>The edge-following zipper (TMSPLINE_ZIP=rail): rim runs matched to the road edges' outlines.</summary>
    private static int ZipEdges(AdaptiveMesh mesh, TrackMap map, Func<Vector3, float> surface, Func<Vector3, float> room, float gap)
    {
        // The road edges as outlines, points about half the gap apart (about as far apart as the rim's vertices).
        var step = gap / 2;
        var rails = new List<Rail>();
        foreach (var track in map.Tracks)
        {
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.High));
            if (rings.Count < 2) continue;
            static Vector3 At(Ring r, double across)
            {
                var e = RoadMeshBuilder.SurfaceSample(r, across);
                var w = r.Position + r.Right * e.X + r.Up * e.Y;
                return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
            }
            // One side's points along the road, about `step` apart (both ends always).
            List<Vector3> Side(int side)
            {
                var points = new List<Vector3>();
                var last = double.MinValue;
                for (var i = 0; i < rings.Count; i++)
                {
                    if (rings[i].Distance - last < step && i < rings.Count - 1) continue;
                    points.Add(At(rings[i], side * rings[i].Width / 2));
                    last = rings[i].Distance;
                }
                return points;
            }
            // Across a ring from one edge (from: -1 left, 1 right) towards the other, the points between them.
            IEnumerable<Vector3> Across(Ring r, int from)
            {
                var n = Math.Max(1, (int)Math.Round(r.Width / step));
                for (var k = 1; k < n; k++) yield return At(r, from * (0.5 - k / (double)n) * r.Width);
            }
            List<Vector3> left = Side(-1), right = Side(1);
            if (track.Closed)
            {
                foreach (var points in new[] { left, right })
                {
                    var rail = new Rail();
                    // (The last ring is the first again.)
                    var closing = points.Count > 2 && Vector3.Distance(points[0], points[^1]) < 0.5f;
                    foreach (var p in closing ? points.SkipLast(1) : points) rail.Add(p);
                    rail.Close();
                    rails.Add(rail);
                }
            }
            else
            {
                var rail = new Rail();
                foreach (var p in left) rail.Add(p);
                foreach (var p in Across(rings[^1], -1)) rail.Add(p);
                for (var i = right.Count - 1; i >= 0; i--) rail.Add(right[i]);
                foreach (var p in Across(rings[0], 1)) rail.Add(p);
                rail.Close();
                rails.Add(rail);
            }
        }
        if (rails.Count == 0) return 0;

        // A bucket grid of the edges' points, 8 m squares.
        const float Bucket = 8;
        var buckets = new Dictionary<(int, int), List<(int Rail, int Index)>>();
        for (var r = 0; r < rails.Count; r++)
            for (var i = 0; i < rails[r].Points.Count; i++)
            {
                var p = rails[r].Points[i];
                var key = ((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Z / Bucket));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add((r, i));
            }
        var reach = 2.5f * gap;
        var Diag = Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on";
        var blocked = 0;
        // Whether the straight line from a place to an edge point stays out of the roads' rooms (the owner's rule: no
        // seam through the road; at a tunnel's mouth the land over it would otherwise reach down through it).
        bool Clear(Vector3 from, Vector3 to)
        {
            var steps = Math.Max(4, (int)MathF.Ceiling(Vector3.Distance(from, to) / 0.5f));
            // (The last stretch before the edge left out: the room reaches a little past the deck's edge and under it.)
            var length = Vector3.Distance(from, to);
            for (var k = 1; k < steps; k++)
            {
                if (length * (1 - k / (float)steps) < 1.5f) break;
                var value = room(Vector3.Lerp(from, to, k / (float)steps));
                if (value < -0.5f)
                {
                    if (Diag && blocked++ < 30) Console.WriteLine($"  blocked from {from} to {to} ({Vector3.Distance(from, to):0.0} m): room {value:0.00} at {k / (float)steps:0.00} of the way, {Vector3.Distance(Vector3.Lerp(from, to, k / (float)steps), to):0.0} m before the edge");
                    return false;
                }
            }
            return true;
        }
        // The nearest point on any edge within reach that can be seen from the place without passing through a road:
        // which edge, how far along it (metres), how far away.
        (int Rail, float Along, float Distance) Nearest(Vector3 p)
        {
            var best = (Rail: -1, Along: 0f, Distance: reach);
            var cells = (int)MathF.Ceiling(reach / Bucket);
            int bx = (int)MathF.Floor(p.X / Bucket), bz = (int)MathF.Floor(p.Z / Bucket);
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var z = bz - cells; z <= bz + cells; z++)
                    if (buckets.TryGetValue((x, z), out var list))
                        foreach (var (r, i) in list)
                        {
                            var rail = rails[r];
                            // The two segments from this point (the outline goes round).
                            var count = rail.Points.Count;
                            for (var k = i - 1; k <= i; k++)
                            {
                                int k0 = (k + count) % count, k1 = (k0 + 1) % count;
                                Vector3 a = rail.Points[k0], b = rail.Points[k1];
                                var e = b - a;
                                var h = Math.Clamp(Vector3.Dot(p - a, e) / MathF.Max(e.LengthSquared(), 1e-9f), 0, 1);
                                var d = Vector3.Distance(p, a + e * h);
                                var length = k1 == 0 ? rail.Length - rail.Along[k0] : rail.Along[k1] - rail.Along[k0];
                                if (d < best.Distance && Clear(p, a + e * h)) best = (r, (rail.Along[k0] + length * h) % rail.Length, d);
                            }
                        }
            return best;
        }

        // The rim: boundary edges (used by one triangle), each from a to b as the triangle winds, chained into loops.
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var next = new Dictionary<int, int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                if (count[a < b ? (a, b) : (b, a)] == 1) next.TryAdd(a, b);
            }
        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int>();
            var v = start;
            while (seen.Add(v))
            {
                loop.Add(v);
                if (!next.TryGetValue(v, out v)) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }

        // The edges' points as the mesh's vertices, made when a strip first uses them.
        var edgeVertex = new Dictionary<(int, int), int>();
        int EdgeVertex(int rail, int index)
        {
            if (!edgeVertex.TryGetValue((rail, index), out var id))
            {
                id = mesh.Positions.Count;
                mesh.Positions.Add(rails[rail].Points[index]);
                mesh.Pinned.Add(false);
                edgeVertex[(rail, index)] = id;
            }
            return id;
        }
        // The edge point nearest a distance along it.
        static int IndexAt(Rail rail, float along)
        {
            var i = rail.Along.BinarySearch(along);
            if (i >= 0) return i;
            i = ~i;
            if (i == 0) return 0;
            if (i >= rail.Along.Count) return along - rail.Along[^1] < rail.Length - along ? rail.Along.Count - 1 : 0;
            return along - rail.Along[i - 1] < rail.Along[i] - along ? i - 1 : i;
        }

        var made = 0;
        foreach (var loop in loops)
        {
            var match = loop.Select(v =>
            {
                var p = mesh.Positions[v];
                return surface(p) < 1.5f * gap ? Nearest(p) : (Rail: -1, Along: 0f, Distance: float.MaxValue);
            }).ToArray();
            // Runs: consecutive vertices on the same edge, going one way along it without big jumps.
            float Delta(int i, int j) => rails[match[i].Rail].Step(match[i].Along, match[j].Along);
            bool Continues(int i, int j, int direction) =>
                match[i].Rail >= 0 && match[i].Rail == match[j].Rail && MathF.Abs(Delta(i, j)) < 3 * gap
                && (direction == 0 || Delta(i, j) * direction > -0.5f);
            // Start the walk where a run begins (if the whole loop is one run, anywhere).
            var n = loop.Count;
            var first = 0;
            for (var i = 0; i < n; i++)
                if (!Continues((i - 1 + n) % n, i, 0)) { first = i; break; }
            var at = 0;
            while (at < n)
            {
                var i0 = (first + at) % n;
                if (match[i0].Rail < 0) { at++; continue; }
                var run = new List<int> { i0 };
                var direction = 0;
                while (at + run.Count < n)
                {
                    var i1 = (first + at + run.Count) % n;
                    var last = run[^1];
                    if (!Continues(last, i1, direction)) break;
                    if (direction == 0 && MathF.Abs(Delta(last, i1)) > 0.5f) direction = MathF.Sign(Delta(last, i1));
                    run.Add(i1);
                }
                at += run.Count;
                if (run.Count < 2 || direction == 0) continue;
                // The whole loop along one outline (land round a road all the way): the run closes on itself.
                var whole = run.Count == n && Continues(run[^1], run[0], direction);
                // The stretch of the edge, the same way as the rim runs: as far along as the rim goes.
                var r = match[run[0]].Rail;
                var rail = rails[r];
                var travel = 0f;
                for (var k = 1; k < run.Count; k++) travel += Delta(run[k - 1], run[k]) * direction;
                if (whole) travel += Delta(run[^1], run[0]) * direction;
                int j0 = IndexAt(rail, match[run[0]].Along), points = rail.Points.Count;
                // (From the edge point nearest the run's start: how far that already is along the way.)
                var gone = rail.Step(match[run[0]].Along, rail.Along[j0]) * direction;
                var edge = new List<int> { j0 };
                for (var j = j0; edge.Count <= points;)
                {
                    var j2 = (j + direction + points) % points;
                    var length = MathF.Abs(rail.Step(rail.Along[j], rail.Along[j2]));
                    if (!whole && gone + length / 2 > travel) break;
                    gone += length;
                    j = j2;
                    edge.Add(j);
                    if (whole && j == j0) break;
                }
                // Zip: the rim's run (as the land's edges run) and the edge's stretch, the shorter diagonal first.
                var rim = run.Select(k => loop[k]).ToList();
                if (whole) rim.Add(rim[0]);
                var ids = edge.Select(j => EdgeVertex(r, j)).ToList();
                if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                {
                    var far = 0f;
                    for (var k = 1; k < ids.Count; k++) far = MathF.Max(far, Vector3.Distance(mesh.Positions[ids[k - 1]], mesh.Positions[ids[k]]));
                    if (far > 3 * gap)
                        Console.WriteLine($"  run of {rim.Count} rim vertices at {mesh.Positions[rim[0]]}: edge {r}, {edge.Count} points from {rail.Along[j0]:0} m, direction {direction}, travel {travel:0} m, whole {whole}; edge points up to {far:0} m apart; along {string.Join(" ", run.Take(12).Select(k => match[k].Along.ToString("0")))}");
                }
                int a = 0, b = 0;
                while (a < rim.Count - 1 || b < ids.Count - 1)
                {
                    bool rimStep;
                    if (a == rim.Count - 1) rimStep = false;
                    else if (b == ids.Count - 1) rimStep = true;
                    else rimStep = Vector3.DistanceSquared(mesh.Positions[rim[a + 1]], mesh.Positions[ids[b]])
                        <= Vector3.DistanceSquared(mesh.Positions[rim[a]], mesh.Positions[ids[b + 1]]);
                    if (rimStep)
                    {
                        mesh.Indices.AddRange([rim[a + 1], rim[a], ids[b]]);
                        a++;
                    }
                    else
                    {
                        mesh.Indices.AddRange([rim[a], ids[b], ids[b + 1]]);
                        b++;
                    }
                    if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                    {
                        int t0 = mesh.Indices[^3], t1 = mesh.Indices[^2], t2 = mesh.Indices[^1];
                        Vector3 q0 = mesh.Positions[t0], q1 = mesh.Positions[t1], q2 = mesh.Positions[t2];
                        var longest = MathF.Max(Vector3.Distance(q0, q1), MathF.Max(Vector3.Distance(q1, q2), Vector3.Distance(q2, q0)));
                        if (longest > 4 * gap) Console.WriteLine($"  long strip triangle {longest:0} m at {q0}, {q1}, {q2}: edge {r}, run of {rim.Count}, stretch of {ids.Count} from {rail.Along[j0]:0} m, direction {direction}, travel {travel:0}");
                    }
                    made++;
                }
            }
        }
        return made;
    }
}
