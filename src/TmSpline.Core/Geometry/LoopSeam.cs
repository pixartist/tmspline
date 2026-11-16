using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The loops terrain (TerrainMesher.Loops; the owner's plan of 2026-11-16): the land joined to the roads with strips,
/// never leaving a hole (where it cannot be joined cleanly it stays as it was).
/// <list type="number">
/// <item>The roads' shells: per ring their edges (the driving surface's own) and undersides, as one graph; the driving
/// surface is never part of a way, and no link from land to road may pass through any of the road.</item>
/// <item>The land near a road: within D of its sides or underside (to their faces), reached without passing through it.
/// The triangles touching it are a group's region; the region's border, the land loops.</item>
/// <item>Each land loop's vertices take their nearest road vertex (within LinkReach, reached unblocked); between matched
/// vertices the road loop takes the shortest way over the shell, within the corridor. A matched vertex whose way goes up
/// and straight back is dropped.</item>
/// <item>Strips zip each piece of land loop to its piece of road loop. A group is cut and zipped only if all its loops
/// close; round anything that fails the land is spared (left whole, a little wider each round) and the loops found again.</item>
/// </list>
/// The voxel land under it is built for this: rooms cut deeper under the driving surface (DeckCut), the floors under the
/// roads kept (no holes of its own). Debug (Gaps view): see <see cref="Run"/>; TMSPLINE_LOOP_DRAW=groups draws the
/// near land's and road's groups instead, the land left whole.
/// </summary>
public static class LoopSeam
{
    /// <summary>How far under the driving surface the roads' rooms cut the land, metres (TMSPLINE_LOOP_CUT; the voxel terrain's: 0.45, less than the slab's 0.5).</summary>
    public static float DeckCut { get; set; } = Setting("TMSPLINE_LOOP_CUT", 1.5f);

    /// <summary>D, metres (TMSPLINE_LOOP_D).</summary>
    public static float Reach { get; set; } = Setting("TMSPLINE_LOOP_D", 2);

    /// <summary>
    /// The longest way over the shell between two loop vertices' nodes, metres (TMSPLINE_LOOP_PATH): only to keep the
    /// search short; the 2·D corridor is the rule. (A limit of twice the land's own length between them was too short at
    /// a pipe's mouth: from one side of its narrow opening to the other the way round its outside is ~50 m.)
    /// </summary>
    public static float MaxPath { get; set; } = Setting("TMSPLINE_LOOP_PATH", 150);

    /// <summary>Underside nodes across a ring, edge to edge (the shell's nodes between its two edges).</summary>
    private const int Underside = 9;
    /// <summary>A ring's shell nodes: its left edge, the underside (Underside of them), its right edge.</summary>
    private const int Columns = Underside + 2;

    private static float Setting(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>The roads' shells as one graph: nodes (places) and, per node, its neighbours with the step's length.</summary>
    private sealed class Shell
    {
        public readonly List<Vector3> Nodes = [];
        public readonly List<List<(int Node, float Length)>> Next = [];
        /// <summary>The shell's quads round the slab (sides and underside, not the driving surface), by their corner nodes in turn.</summary>
        public readonly List<(int A, int B, int C, int D)> Quads = [];
        /// <summary>Per node: (track, ring, column), for the debug output.</summary>
        public readonly List<(int Track, int Ring, int Column)> Where = [];

        private readonly Dictionary<(int, int, int), List<int>> buckets = [];
        private const float Bucket = 4;

        /// <summary>Per node: its ring's distance along its track, metres.</summary>
        public readonly List<float> Along = [];
        /// <summary>The ring distance new nodes get (set per ring while building).</summary>
        public float RingAlong;

        /// <summary>Whether two nodes lie on separate parts of the roads: other tracks, or far apart along one.</summary>
        public bool Apart(int a, int b)
        {
            if (Where[a].Track != Where[b].Track) return true;
            var gap = MathF.Abs(Along[a] - Along[b]);
            // (Round a closed track the shorter way: its start is no part apart from its end.)
            if (TrackLength.TryGetValue(Where[a].Track, out var length)) gap = MathF.Min(gap, length - gap);
            return gap > PartLength;
        }
        /// <summary>Closed tracks' lengths, metres.</summary>
        public readonly Dictionary<int, float> TrackLength = [];

        public int Add(Vector3 p, (int, int, int) where)
        {
            Nodes.Add(p);
            Next.Add([]);
            Where.Add(where);
            Along.Add(RingAlong);
            var key = Key(p);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(Nodes.Count - 1);
            return Nodes.Count - 1;
        }

        public void Link(int a, int b)
        {
            if (a == b || Next[a].Any(n => n.Node == b)) return;
            var length = Vector3.Distance(Nodes[a], Nodes[b]);
            Next[a].Add((b, length));
            Next[b].Add((a, length));
        }

        private static (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Y / Bucket), (int)MathF.Floor(p.Z / Bucket));

        /// <summary>
        /// The node nearest p within radius whose line to p passes through no road (its driving surface, sides and
        /// underside: the shell's faces), or -1. Only the nearest few are tried.
        /// </summary>
        public int Nearest(Vector3 p, float radius, out float distance)
        {
            distance = radius;
            var candidates = new List<(float D, int Node)>();
            foreach (var n in Within(p, radius))
            {
                var d = Vector3.Distance(p, Nodes[n]);
                if (d < radius) candidates.Add((d, n));
            }
            candidates.Sort((a, b) => a.D.CompareTo(b.D));
            foreach (var (d, n) in candidates.Take(32))
                if (Clear(p, Nodes[n])) { distance = d; return n; }
            return -1;
        }

        /// <summary>Whether a node is one of a ring's edges (the driving surface's own edge, the slab's upper corner).</summary>
        public bool IsEdge(int n) => Where[n].Column == 0 || Where[n].Column == Columns - 1;

        /// <summary>
        /// The nearest edge node within radius that p reaches without passing through a road; only with none, the nearest
        /// node of any kind (the slab's lower corner, its underside). The land meets the road's upper edge where it can.
        /// </summary>
        public int NearestPreferEdge(Vector3 p, float radius)
        {
            var candidates = new List<(float D, int Node)>();
            foreach (var n in Within(p, radius))
            {
                var d = Vector3.Distance(p, Nodes[n]);
                if (d < radius) candidates.Add((d, n));
            }
            candidates.Sort((a, b) => a.D.CompareTo(b.D));
            var tried = 0;
            foreach (var (_, n) in candidates)
            {
                if (!IsEdge(n)) continue;
                if (Clear(p, Nodes[n])) return n;
                if (++tried >= 16) break;
            }
            foreach (var (_, n) in candidates.Take(32))
                if (Clear(p, Nodes[n])) return n;
            return -1;
        }

        /// <summary>The road's faces (driving surface, sides, underside), by the buckets they overlap.</summary>
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> faces = [];
        private readonly Dictionary<(int, int, int), List<int>> faceBuckets = [];

        /// <summary>Per face: one of the slab's sides or its underside (not the driving surface).</summary>
        private readonly List<bool> faceIsShell = [];

        public void Face(Vector3 a, Vector3 b, Vector3 c, bool shell)
        {
            faces.Add((a, b, c));
            faceIsShell.Add(shell);
            var lo = Key(Vector3.Min(a, Vector3.Min(b, c)));
            var hi = Key(Vector3.Max(a, Vector3.Max(b, c)));
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!faceBuckets.TryGetValue((x, y, z), out var list)) faceBuckets[(x, y, z)] = list = [];
                        list.Add(faces.Count - 1);
                    }
        }

        /// <summary>
        /// Whether p lies within radius of the road's sides or underside (the nearest points on those faces, not only
        /// their nodes), the line to such a point passing through no road.
        /// </summary>
        public bool NearSurface(Vector3 p, float radius)
        {
            var (bx, by, bz) = Key(p);
            var cells = (int)MathF.Ceiling(radius / Bucket);
            var seen = new HashSet<int>();
            var candidates = new List<(float D, Vector3 Q)>();
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var y = by - cells; y <= by + cells; y++)
                    for (var z = bz - cells; z <= bz + cells; z++)
                    {
                        if (!faceBuckets.TryGetValue((x, y, z), out var list)) continue;
                        foreach (var f in list)
                        {
                            if (!faceIsShell[f] || !seen.Add(f)) continue;
                            var (a, b, c) = faces[f];
                            var q = ClosestOnTriangle(p, a, b, c);
                            var dist = Vector3.Distance(p, q);
                            if (dist <= radius) candidates.Add((dist, q));
                        }
                    }
            candidates.Sort((u, w) => u.D.CompareTo(w.D));
            foreach (var (_, q) in candidates.Take(8))
                if (Clear(p, q)) return true;
            return false;
        }

        [ThreadStatic] public static (float At, float Length) LastBlock;

        /// <summary>Whether the line from p to a node q passes through none of the road's faces (touching them at q aside).</summary>
        public bool Clear(Vector3 p, Vector3 q, float spare = 0.1f)
        {
            var d = q - p;
            var length = d.Length();
            if (length < 1e-4f) return true;
            var lo = Key(Vector3.Min(p, q));
            var hi = Key(Vector3.Max(p, q));
            var seen = new HashSet<int>();
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!faceBuckets.TryGetValue((x, y, z), out var list)) continue;
                        foreach (var f in list)
                        {
                            if (!seen.Add(f)) continue;
                            var (a, b, c) = faces[f];
                            if (Hit(p, d, a, b, c) is { } t && t * length < length - spare) { LastBlock = (t * length, length); return false; }
                        }
                    }
            return true;
        }

        /// <summary>Where along p + t·d (0..1) the segment crosses triangle abc (Möller–Trumbore), or null.</summary>
        private static float? Hit(Vector3 p, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e1 = b - a, e2 = c - a;
            var h = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-9f) return null;
            var inv = 1 / det;
            var s = p - a;
            var u = Vector3.Dot(s, h) * inv;
            if (u < 0 || u > 1) return null;
            var qv = Vector3.Cross(s, e1);
            var v = Vector3.Dot(d, qv) * inv;
            if (v < 0 || u + v > 1) return null;
            var t = Vector3.Dot(e2, qv) * inv;
            return t is >= 0 and <= 1 ? t : null;
        }

        /// <summary>The nodes in the buckets within radius of p (some a little further).</summary>
        public IEnumerable<int> Within(Vector3 p, float radius)
        {
            var (bx, by, bz) = Key(p);
            var cells = (int)MathF.Ceiling(radius / Bucket);
            for (var x = bx - cells; x <= bx + cells; x++)
                for (var y = by - cells; y <= by + cells; y++)
                    for (var z = bz - cells; z <= bz + cells; z++)
                        if (buckets.TryGetValue((x, y, z), out var list))
                            foreach (var n in list) yield return n;
        }
    }

    /// <summary>
    /// Each track's shell, ring by ring at the quality the road is built at: per ring its left edge (the driving
    /// surface's own), the underside (the surface moved in by the slab's thickness), its right edge. Nodes link to their
    /// neighbours across the ring (round from one edge under the road to the other, never over the driving surface) and
    /// along the track, diagonals included. A ring closed round (a pipe) has one edge node; a closed track's last ring
    /// links to its first.
    /// </summary>
    private static Shell BuildShell(TrackMap map, MeshQuality roadQuality)
    {
        static Vector3 W(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        var shell = new Shell();
        var thickness = (float)new RoadMeshSettings().Thickness;
        var ends = new List<(int Track, int[] First, int[] Last)>();
        var joins = 0;
        for (var t = 0; t < map.Tracks.Count; t++)
        {
            var track = map.Tracks[t];
            track.SyncSegments();
            if (track.Segments.Count == 0) continue;
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(roadQuality));
            if (rings.Count < 2) continue;
            var ids = new int[rings.Count][];
            // Per ring its outline round the slab: left edge, the driving surface across, right edge (top), and the
            // underside nodes below (bottom).
            var tops = new Vector3[rings.Count][];
            for (var k = 0; k < rings.Count; k++)
            {
                var r = rings[k];
                Vector3 Top(double s, out Vector3 normal)
                {
                    var q = RoadMeshBuilder.SurfaceSample(r, s);
                    normal = Vector3.Normalize(W(r.Right * q.NX + r.Up * q.NY));
                    return W(r.Position + r.Right * q.X + r.Up * q.Y);
                }
                var row = ids[k] = new int[Columns];
                var left = Top(-0.5 * r.Width, out _);
                var right = Top(0.5 * r.Width, out _);
                // A ring closed round (a pipe): its two edges one node, and so the slab's sides next to them (one place).
                var closed = Vector3.Distance(left, right) < 0.05f;
                // (A road built without an underside: the shell's underside its driving surface's back.)
                var slab = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)].Underside ? thickness : 0;
                shell.RingAlong = (float)r.Distance;
                row[0] = shell.Add(left, (t, k, 0));
                tops[k] = new Vector3[Underside];
                for (var j = 0; j < Underside; j++)
                {
                    var top = tops[k][j] = Top((j / (double)(Underside - 1) - 0.5) * r.Width, out var n);
                    row[j + 1] = closed && j == Underside - 1 ? row[1] : shell.Add(top - n * slab, (t, k, j + 1));
                }
                row[Columns - 1] = closed ? row[0] : shell.Add(right, (t, k, Columns - 1));
                for (var j = 0; j + 1 < Columns; j++) shell.Link(row[j], row[j + 1]);
            }
            void Join(int ka, int kb)
            {
                int[] a = ids[ka], b = ids[kb];
                for (var j = 0; j < Columns; j++)
                {
                    shell.Link(a[j], b[j]);
                    if (j + 1 < Columns) { shell.Link(a[j], b[j + 1]); shell.Link(a[j + 1], b[j]); }
                }
                // The road between the two rings as faces: its driving surface, and round its slab from one edge under it
                // to the other (sides and underside).
                void Quad(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, bool round) { shell.Face(p0, p1, q1, round); shell.Face(p0, q1, q0, round); }
                for (var j = 0; j + 1 < Underside; j++) Quad(tops[ka][j], tops[ka][j + 1], tops[kb][j], tops[kb][j + 1], false);
                for (var j = 0; j + 1 < Columns; j++)
                {
                    Quad(shell.Nodes[a[j]], shell.Nodes[a[j + 1]], shell.Nodes[b[j]], shell.Nodes[b[j + 1]], true);
                    shell.Quads.Add((a[j], a[j + 1], b[j + 1], b[j]));
                }
            }
            for (var k = 0; k + 1 < rings.Count; k++) Join(k, k + 1);
            if (track.Closed) { Join(rings.Count - 1, 0); shell.TrackLength[t] = (float)(rings[^1].Distance + Vector3.Distance(W(rings[^1].Position), W(rings[0].Position))); }
            if (!track.Closed) ends.Add((t, ids[0], ids[^1]));
        }
        // Where an open road starts or ends at another road (a splice, a junction): its end rings' nodes linked to the
        // other roads' nodes within EndJoin, so a road loop can go on from one road to the other.
        foreach (var (t, first, last) in ends)
            foreach (var row in new[] { first, last })
                foreach (var n in row.Distinct())
                    foreach (var m in shell.Within(shell.Nodes[n], EndJoin).ToList())
                        if (shell.Where[m].Track != t && Vector3.Distance(shell.Nodes[n], shell.Nodes[m]) <= EndJoin) { shell.Link(n, m); joins++; }
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on")
            foreach (var (t, first, last) in ends)
                foreach (var (row, which) in new[] { (first, "first"), (last, "last") })
                {
                    var best = float.MaxValue; var bestWhere = (0, 0, 0);
                    foreach (var n in row)
                        foreach (var m in shell.Within(shell.Nodes[n], 12))
                            if (shell.Where[m].Track != t && Vector3.Distance(shell.Nodes[n], shell.Nodes[m]) is var dd && dd < best) { best = dd; bestWhere = shell.Where[m]; }
                    if (best < 12) Console.WriteLine($"    [loops] track {t} {which} ring: another road's node {best:0.00} m away ({bestWhere})");
                }
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on" && joins > 0) Console.WriteLine($"    [loops] {joins} links where roads' ends meet other roads");
        return shell;
    }

    /// <summary>Land on a road's space this close over its driving surface is near, metres.</summary>
    private const float SpaceReach = 4;

    /// <summary>How close to its road vertex a strip's diagonal may touch the road, metres (the slab is 0.5 m thick).</summary>
    private const float StripSpare = 0.75f;

    /// <summary>Two places on one track this far apart along it are separate parts of the roads (land between them a bridge), metres.</summary>
    private const float PartLength = 30;

    /// <summary>How close an open road's end ring comes to another road for the two to be linked there, metres.</summary>
    private const float EndJoin = 2;

    /// <summary>
    /// The groups view (the owner's, 2026-11-16): the land's vertices with a road vertex (edge or underside) within D
    /// that they reach without passing through a road, grouped where mesh edges join them; and the other way round the
    /// road's vertices within D of the land's surface (reached from it without passing through a road), grouped where
    /// the shell joins them. Each group's border drawn
    /// in a colour of its own (kinds from 16: the editor gives each kind a colour), round the faces touching it (the
    /// land step 4 takes out).
    /// </summary>
    private static void DrawGroups(AdaptiveMesh mesh, Shell shell, bool[] near, float d, Action<Vector3, Vector3, int> line, Action<Vector3, int, float> cross, bool stats)
    {
        var positions = mesh.Positions;
        static int Find(int[] parent, int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        static void Union(int[] parent, int a, int b) { a = Find(parent, a); b = Find(parent, b); if (a != b) parent[a] = b; }
        // Borders: per group, the edges of the faces touching it that only one of those faces has (each group's own
        // closed border, whatever other groups lie next to it).
        static void Borders(IEnumerable<int[]> faces, int[] parent, Func<int, bool> member, Dictionary<int, int> kindOf, Func<int, Vector3> at, Action<Vector3, Vector3, int> line, HashSet<int> bordered, HashSet<(int, int)>? ownEdges = null)
        {
            var uses = new Dictionary<(int Group, int A, int B), int>();
            var groups = new HashSet<int>();
            foreach (var f in faces)
            {
                groups.Clear();
                foreach (var v in f) if (member(v)) groups.Add(Find(parent, v));
                foreach (var g in groups)
                    for (var k = 0; k < f.Length; k++)
                    {
                        int a = f[k], b = f[(k + 1) % f.Length];
                        var key = a < b ? (g, a, b) : (g, b, a);
                        uses[key] = uses.GetValueOrDefault(key) + 1;
                    }
            }
            foreach (var ((g, a, b), n) in uses)
            {
                if (n != 1) continue;
                bordered.Add(g);
                // (An edge of the land itself, no triangle beyond it: grey, not the group's colour.)
                line(at(a), at(b), ownEdges is not null && ownEdges.Contains((a, b)) ? 9 : kindOf[g]);
            }
        }

        // The land's groups.
        var landParent = Enumerable.Range(0, positions.Count).ToArray();
        var faces = new List<int[]>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int[] f = [mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]];
            faces.Add(f);
            for (var k = 0; k < 3; k++)
                if (near[f[k]] && near[f[(k + 1) % 3]]) Union(landParent, f[k], f[(k + 1) % 3]);
        }
        var kind = new Dictionary<int, int>();
        var nextKind = 16;
        var landVertices = Enumerable.Range(0, positions.Count).Where(v => near[v]).ToList();
        foreach (var v in landVertices)
            if (!kind.ContainsKey(Find(landParent, v))) kind[Find(landParent, v)] = nextKind++;
        var landGroups = kind.Count;
        var bordered = new HashSet<int>();
        var edgeUses = new Dictionary<(int, int), int>();
        foreach (var f in faces)
            for (var k = 0; k < 3; k++)
            {
                int a = f[k], b = f[(k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
            }
        var ownEdges = edgeUses.Where(e => e.Value == 1).Select(e => e.Key).ToHashSet();
        if (stats)
        {
            // The land mesh's health: edges three or more triangles share, and edges two share running the same way (one
            // of them flipped).
            var directed = new Dictionary<(int, int), int>();
            foreach (var f in faces)
                for (var k = 0; k < 3; k++) directed[(f[k], f[(k + 1) % 3])] = directed.GetValueOrDefault((f[k], f[(k + 1) % 3])) + 1;
            var crowded = edgeUses.Where(e => e.Value > 2).ToList();
            var flipped = directed.Where(e => e.Value > 1).ToList();
            Console.WriteLine($"    land mesh: {faces.Count} triangles, {ownEdges.Count} open edges, {crowded.Count} edges shared by 3+, {flipped.Count} edges two triangles run the same way");
        }
        // The broken edges drawn: shared by three or more triangles red (8), two triangles running the same way orange (3).
        {
            var directed = new Dictionary<(int, int), int>();
            foreach (var f in faces)
                for (var k = 0; k < 3; k++) directed[(f[k], f[(k + 1) % 3])] = directed.GetValueOrDefault((f[k], f[(k + 1) % 3])) + 1;
            foreach (var e in edgeUses.Where(e => e.Value > 2)) { line(positions[e.Key.Item1], positions[e.Key.Item2], 8); cross(positions[e.Key.Item1], 8, 0.8f); }
            foreach (var e in directed.Where(e => e.Value > 1)) line(positions[e.Key.Item1], positions[e.Key.Item2], 3);
        }
        Borders(faces, landParent, v => near[v], kind, v => positions[v], line, bordered, ownEdges);
        foreach (var v in landVertices)
            if (!bordered.Contains(Find(landParent, v))) cross(positions[v], kind[Find(landParent, v)], 0.5f);

        if (Environment.GetEnvironmentVariable("TMSPLINE_LOOP_PROBE") is { Length: > 0 } probes)
            foreach (var probe in probes.Split('|'))
            {
                var pv = probe.Split(';').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var at = new Vector3(pv[0], pv[1], pv[2]);
                foreach (var v in Enumerable.Range(0, positions.Count).Where(v => Vector3.Distance(positions[v], at) < pv[3]))
                {
                    var p = positions[v];
                    var cands = shell.Within(p, d + 4).Select(n => (D: Vector3.Distance(p, shell.Nodes[n]), N: n)).OrderBy(x => x.D).Take(5);
                    Console.WriteLine($"    probe land {v} at {p.X:0.0};{p.Y:0.0};{p.Z:0.0}: near {(near[v] ? "yes" : "no")}; " +
                        string.Join("; ", cands.Select(x => $"{x.D:0.0} m {shell.Where[x.N]} {(shell.Clear(p, shell.Nodes[x.N]) ? "clear" : "blocked")}")));
                }
            }
        // The road's groups: the nodes within D of the land's surface (the nearest point on any of its triangles, not
        // only its vertices: those lie ~4 m apart, and a node a few centimetres over the land was often further than D
        // from every one of them), the line from that point to the node passing through no road.
        const float Cell = 4;
        (int, int, int) Key(Vector3 q) => ((int)MathF.Floor(q.X / Cell), (int)MathF.Floor(q.Y / Cell), (int)MathF.Floor(q.Z / Cell));
        var triangleBuckets = new Dictionary<(int, int, int), List<int>>();
        for (var t = 0; t < faces.Count; t++)
        {
            Vector3 a = positions[faces[t][0]], b = positions[faces[t][1]], c = positions[faces[t][2]];
            var lo = Key(Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(d));
            var hi = Key(Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(d));
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!triangleBuckets.TryGetValue((x, y, z), out var list)) triangleBuckets[(x, y, z)] = list = [];
                        list.Add(t);
                    }
        }
        var reached = new bool[shell.Nodes.Count];
        Parallel.For(0, shell.Nodes.Count, n =>
        {
            var q = shell.Nodes[n];
            if (!triangleBuckets.TryGetValue(Key(q), out var list)) return;
            var candidates = new List<(float D, Vector3 P)>();
            foreach (var t in list)
            {
                var p = ClosestOnTriangle(q, positions[faces[t][0]], positions[faces[t][1]], positions[faces[t][2]]);
                var dist = Vector3.Distance(p, q);
                if (dist <= d) candidates.Add((dist, p));
            }
            candidates.Sort((x, y) => x.D.CompareTo(y.D));
            foreach (var (_, p) in candidates.Take(8))
                if (shell.Clear(p, q)) { reached[n] = true; break; }
        });
        var roadParent = Enumerable.Range(0, shell.Nodes.Count).ToArray();
        for (var n = 0; n < shell.Nodes.Count; n++)
            if (reached[n])
                foreach (var (m, _) in shell.Next[n])
                    if (reached[m]) Union(roadParent, n, m);
        var roadKind = new Dictionary<int, int>();
        for (var n = 0; n < shell.Nodes.Count; n++)
            if (reached[n] && !roadKind.ContainsKey(Find(roadParent, n))) roadKind[Find(roadParent, n)] = nextKind++;
        var roadBordered = new HashSet<int>();
        Borders(shell.Quads.Select(q => new[] { q.A, q.B, q.C, q.D }), roadParent, n => reached[n], roadKind, n => shell.Nodes[n], line, roadBordered);
        for (var n = 0; n < shell.Nodes.Count; n++)
            if (reached[n] && !roadBordered.Contains(Find(roadParent, n))) cross(shell.Nodes[n], roadKind[Find(roadParent, n)], 0.3f);

        if (stats)
            Console.WriteLine($"  [loops] groups view, D {d} m: {landVertices.Count} land vertices near the road in {landGroups} groups; {reached.Count(r => r)} road vertices near the land in {roadKind.Count} groups");
    }

    /// <summary>The point of triangle abc nearest p (Ericson).</summary>
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
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        var denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>How far a land loop's vertex looks for its road vertex, metres (TMSPLINE_LOOP_LINK).</summary>
    public static float LinkReach { get; set; } = Setting("TMSPLINE_LOOP_LINK", 8);
    /// <summary>The road loop keeps to road vertices this close to its group's land (reached without passing through a road), metres (TMSPLINE_LOOP_CORRIDOR).</summary>
    public static float Corridor { get; set; } = Setting("TMSPLINE_LOOP_CORRIDOR", 8);

    /// <summary>
    /// The land joined to the roads (changed in place); returns the debug lines. The land near a road (within D of its
    /// sides or underside, reached without passing through a road) makes groups: each group's region the triangles
    /// touching it, bordered by land loops. Each land loop gets a road loop (from each loop vertex's road vertex to the
    /// next one's, the shortest way over the road's sides and underside within the corridor), and the two are zipped
    /// together with a strip, piece by piece between matched vertices, its triangles turning the land's way. A group is
    /// cut out and zipped only if every one of its loops closes: where anything fails (a loop that does not close, a
    /// vertex two loops pass, an edge three triangles share on its border, a loop vertex with no road vertex, no way over
    /// the road) its land stays as it was, unconnected but whole: no holes (the owner's rule, 2026-11-16).
    /// Debug lines: 6 land loops cut and zipped, 7 their road loops, 8 the loops of groups left whole, 9 links.
    /// </summary>
    public static float[] Run(AdaptiveMesh mesh, TrackMap map, MeshQuality roadQuality, Func<Vector3, float>? room = null, Func<Vector3, (float Distance, Vector3 Deck)>? deckOf = null, Func<Vector3, (float Distance, Vector3 Point, Vector3 Left, Vector3 Right)>? surface = null)
    {
        var stats = Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on";
        var d = Reach;
        var shell = BuildShell(map, roadQuality);
        var debug = new List<float>();
        void Line(Vector3 a, Vector3 b, int kind) => debug.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, kind]);
        void Cross(Vector3 p, int kind, float size = 0.6f)
        {
            Line(p - Vector3.UnitX * size, p + Vector3.UnitX * size, kind);
            Line(p - Vector3.UnitY * size, p + Vector3.UnitY * size, kind);
            Line(p - Vector3.UnitZ * size, p + Vector3.UnitZ * size, kind);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Time(string what) { if (stats) Console.WriteLine($"    [loops time] {what}: {watch.Elapsed.TotalSeconds:0.00} s"); watch.Restart(); }
        Time("shell");
        // Land lying on a road's space (its cut, within 0.5 m) less than SpaceReach over its driving surface is near too:
        // over a banked road's low edge the land rests on the space's arch, a few metres over the deck, and would hang
        // over the road.
        bool OnRoadSpace(Vector3 p) => room is not null && surface is not null && room(p) < 0.5f && surface(p).Distance < SpaceReach;
        var split = MakeManifold(mesh);
        if (stats) Console.WriteLine($"    [loops] {split} vertices split where sheets of land touched (edges three or more triangles shared, corners)");
        Time("manifold");
        var positions = mesh.Positions;
        var near = new bool[positions.Count];
        Parallel.For(0, positions.Count, v => near[v] = shell.NearSurface(positions[v], d) || OnRoadSpace(positions[v]));
        Time("near");
        // The groups view (TMSPLINE_LOOP_DRAW=groups): the groups drawn, the land left whole.
        if (Environment.GetEnvironmentVariable("TMSPLINE_LOOP_DRAW") == "groups")
        {
            DrawGroups(mesh, shell, near, d, Line, Cross, stats);
            return debug.ToArray();
        }

        var indices = mesh.Indices;
        var triangles = indices.Count / 3;
        var centre = new Vector3[triangles];
        for (var t = 0; t < triangles; t++) centre[t] = (positions[indices[3 * t]] + positions[indices[3 * t + 1]] + positions[indices[3 * t + 2]]) / 3;
        // Land left whole round where something failed (a round later the loops are found again without it): what fails
        // stays local, not a whole road's land.
        var spared = new bool[triangles];
        // Bridges: land near two separate parts of the roads (other roads, or one road far apart along it, passing near
        // itself) stays land, each road's land a group of its own on its side: a road loop cannot go from one to the
        // other. Each near vertex's part is its nearest road vertex; a vertex whose triangles hold near vertices of
        // separate parts is a bridge's, and so are the triangles round it.
        var part = new int[positions.Count];
        Parallel.For(0, positions.Count, v => part[v] = near[v] ? shell.Nearest(positions[v], d + 2, out _) : -1);
        var seenParts = new Dictionary<int, int>();
        var bridge = new bool[positions.Count];
        for (var t = 0; t < triangles; t++)
        {
            int a = indices[3 * t], b = indices[3 * t + 1], c = indices[3 * t + 2];
            foreach (var x in new[] { a, b, c })
            {
                if (part[x] < 0) continue;
                foreach (var v in new[] { a, b, c })
                {
                    if (!seenParts.TryGetValue(v, out var other)) seenParts[v] = part[x];
                    else if (shell.Apart(other, part[x])) bridge[v] = true;
                }
            }
        }
        var bridgeTriangles = 0;
        // A triangle is near too where its surface comes within D though its corners do not (its edges' middles or its
        // own middle: the land's vertices lie ~4 m apart, and under a road's edge the land between two of them can lie a
        // metre under the road with neither corner near).
        var nearTriangle = new bool[triangles];
        Parallel.For(0, triangles, t =>
        {
            Vector3 a = positions[indices[3 * t]], b = positions[indices[3 * t + 1]], c = positions[indices[3 * t + 2]];
            nearTriangle[t] = shell.NearSurface((a + b + c) / 3, d) || OnRoadSpace((a + b + c) / 3) || shell.NearSurface((a + b) / 2, d) || shell.NearSurface((b + c) / 2, d) || shell.NearSurface((c + a) / 2, d);
        });
        var touches = new bool[triangles];
        var parent = Enumerable.Range(0, positions.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        var failed = new Dictionary<int, string>();
        var failedAt = new List<Vector3>();
        var pieces = new List<(int Group, List<int> Land, List<int> Road, List<int> Triangles)>();
        // A piece zipped: each step the shorter of the two next diagonals whose line passes through no road (else the
        // other; with neither, null: the strip would pass through the road). Corners: land vertices as they are, road
        // vertices n as -1 - n. A triangle on the land's edge (a, a+1) holds it the other way round (a+1 to a), so the
        // strip turns the land's way.
        var zipFails = 0;
        List<int>? Zip(List<int> land, List<int> road)
        {
            // The strip as a way through the grid of (land i, road j): each step one triangle, advancing on the land
            // (to i+1, its new diagonal land i+1 to road j) or on the road (to j+1, its diagonal land i to road j+1).
            // Of the ways whose diagonals all pass through no road (within StripSpare of the road vertex), the one with
            // the shortest diagonals in all (dynamic programming); none: null.
            int n = land.Count, m = road.Count;
            var cost = new float[n, m];
            var from = new byte[n, m];
            for (var a = 0; a < n; a++)
                for (var b = 0; b < m; b++) cost[a, b] = float.PositiveInfinity;
            cost[0, 0] = 0;
            var clear = new Dictionary<(int, int), bool>();
            bool Diagonal(int a, int b)
            {
                if (!clear.TryGetValue((a, b), out var ok)) clear[(a, b)] = ok = shell.Clear(positions[land[a]], shell.Nodes[road[b]], StripSpare);
                return ok;
            }
            for (var a = 0; a < n; a++)
                for (var b = 0; b < m; b++)
                {
                    if (a == 0 && b == 0) continue;
                    var best = float.PositiveInfinity;
                    byte how = 0;
                    if (a > 0 && float.IsFinite(cost[a - 1, b]) && Diagonal(a, b))
                    {
                        var c = cost[a - 1, b] + Vector3.Distance(positions[land[a]], shell.Nodes[road[b]]);
                        if (c < best) { best = c; how = 1; }
                    }
                    if (b > 0 && float.IsFinite(cost[a, b - 1]) && Diagonal(a, b))
                    {
                        var c = cost[a, b - 1] + Vector3.Distance(positions[land[a]], shell.Nodes[road[b]]);
                        if (c < best) { best = c; how = 2; }
                    }
                    cost[a, b] = best;
                    from[a, b] = how;
                }
            if (!float.IsFinite(cost[n - 1, m - 1])) return null;
            var steps = new List<byte>();
            for (int a = n - 1, b = m - 1; a > 0 || b > 0;)
            {
                var how = from[a, b];
                steps.Add(how);
                if (how == 1) a--; else b--;
            }
            steps.Reverse();
            var corners = new List<int>();
            int i = 0, j = 0;
            foreach (var how in steps)
            {
                if (how == 1) { corners.AddRange([land[i], -1 - road[j], land[i + 1]]); i++; }
                else { corners.AddRange([land[i], -1 - road[j], -1 - road[j + 1]]); j++; }
            }
            return corners;
        }
        var loopsOf = new Dictionary<int, List<List<int>>>();
        var loops = new List<List<int>>();
        var openLoop = new List<bool>();
        var groupLand = new Dictionary<int, List<Vector3>>();
        var links = new List<(Vector3, Vector3)>();
        var rounds = 0;
        var spareRadius = 6f;
        var spurs = 0;
        HashSet<int>? corridorSet = null;
        for (var round = 0; ; round++)
        {
        rounds = round + 1;
        // The regions: the triangles touching near land (less those spared); groups: their vertices joined through them.
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        failed.Clear();
        failedAt.Clear();
        pieces.Clear();
        loopsOf.Clear();
        links.Clear();
        for (var t = 0; t < triangles; t++)
        {
            int a = indices[3 * t], b = indices[3 * t + 1], c = indices[3 * t + 2];
            touches[t] = false;
            if (spared[t] || !near[a] && !near[b] && !near[c] && !nearTriangle[t]) continue;
            if (bridge[a] || bridge[b] || bridge[c]) { if (round == 0) bridgeTriangles++; continue; }
            touches[t] = true;
            Union(a, b);
            Union(b, c);
        }

        // The land loops: the kept triangles' edges beside a region's triangle, in the kept triangles' turn. A group whose
        // border is not clean is left whole (failed: why).
        var keptDirected = new Dictionary<long, int>(EdgeKeyComparer.Instance);
        var allUses = new Dictionary<long, int>(EdgeKeyComparer.Instance);
        var regionEdges = new HashSet<long>(EdgeKeyComparer.Instance);
        for (var t = 0; t < triangles; t++)
            for (var k = 0; k < 3; k++)
            {
                int a = indices[3 * t + k], b = indices[3 * t + (k + 1) % 3];
                var undirected = a < b ? Key(a, b) : Key(b, a);
                allUses[undirected] = allUses.GetValueOrDefault(undirected) + 1;
                if (touches[t]) regionEdges.Add(undirected);
                else keptDirected[Key(a, b)] = keptDirected.GetValueOrDefault(Key(a, b)) + 1;
            }
        void AddPiece(int g, List<int> land, List<int> road)
        {
            var corners = Zip(land, road);
            if (corners is null) { Fail(g, "a strip that would pass through the road", positions[land[0]]); return; }
            pieces.Add((g, land, road, corners));
        }
        void Fail(int group, string why, Vector3 at) { failed.TryAdd(group, why); failedAt.Add(at); if (stats) Console.WriteLine($"      fail (round {round + 1}): {why} at {at}"); }
        // The land's own edge (the vertices on edges only one triangle has): where a region reaches it, its border is
        // open there, from that edge along the road and back to it.
        var onOwnEdge = new HashSet<int>();
        foreach (var (e, n) in allUses)
            if (n == 1) { onOwnEdge.Add((int)(e >> 32)); onOwnEdge.Add((int)(e & 0xffffffff)); }
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (e, count) in keptDirected)
        {
            int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
            if (keptDirected.ContainsKey(Key(b, a)) || !regionEdges.Contains(a < b ? Key(a, b) : Key(b, a))) continue;
            if (count > 1) { Fail(Find(a), "two triangles running the same way on its border", positions[a]); continue; }
            if (!outgoing.TryGetValue(a, out var list)) outgoing[a] = list = [];
            list.Add(b);
        }
        foreach (var (v, list) in outgoing)
            if (list.Count > 1) Fail(Find(v), "a vertex its border passes twice", positions[v]);
        loops = [];
        openLoop.Clear();
        // Open borders first (from a vertex nothing leads to: the land's own edge), then the closed ones.
        var targets = new HashSet<int>(outgoing.Values.SelectMany(l => l));
        var starts = outgoing.Keys.Where(v => !targets.Contains(v)).ToList();
        foreach (var start in starts.Concat(outgoing.Keys.ToList()))
            while (outgoing.TryGetValue(start, out var first) && first.Count > 0)
            {
                var loop = new List<int> { start };
                var at = start;
                var closed = false;
                while (true)
                {
                    if (!outgoing.TryGetValue(at, out var next) || next.Count == 0) break;
                    var to = next[^1];
                    next.RemoveAt(next.Count - 1);
                    if (to == start) { closed = true; break; }
                    loop.Add(to);
                    at = to;
                    if (loop.Count > positions.Count) break;
                }
                // (Open: only from the land's own edge to it again.)
                var open = !closed && onOwnEdge.Contains(loop[0]) && onOwnEdge.Contains(loop[^1]);
                if (!closed && !open || loop.Count < 3 && !open || loop.Count < 2)
                {
                    if (stats)
                    {
                        var incident = allUses.Where(e => (int)(e.Key >> 32) == at || (int)(e.Key & 0xffffffff) == at).ToList();
                        Console.WriteLine($"      dead end at {positions[at]}: start on own edge {onOwnEdge.Contains(loop[0])}, end on own edge {onOwnEdge.Contains(at)}, {loop.Count} long; its edges: " +
                            string.Join(", ", incident.Select(e => { int o = (int)(e.Key >> 32) == at ? (int)(e.Key & 0xffffffff) : (int)(e.Key >> 32); return $"uses {e.Value} region {regionEdges.Contains(e.Key)} kept {keptDirected.GetValueOrDefault(Key(at, o))}/{keptDirected.GetValueOrDefault(Key(o, at))}"; })));
                    }
                    Fail(Find(start), "a border that does not close", positions[at]);
                    continue;
                }
                loops.Add(loop);
                openLoop.Add(open);
            }

        // Each group's land (its region's vertices), for the corridor.
        groupLand = [];
        var seenVertex = new HashSet<int>();
        for (var t = 0; t < triangles; t++)
        {
            if (!touches[t]) continue;
            for (var k = 0; k < 3; k++)
            {
                var v = indices[3 * t + k];
                if (!seenVertex.Add(v)) continue;
                var g = Find(v);
                if (!groupLand.TryGetValue(g, out var list)) groupLand[g] = list = [];
                list.Add(positions[v]);
            }
        }
        HashSet<int> Allowed(int g)
        {
            if (corridorSet is not null) return corridorSet;
            // The road vertices within the corridor of the group's land that some of it reaches without passing through a road.
            // (Once, from all the groups' land of the first round: later rounds only spare some of it.)
            var land = groupLand.Values.SelectMany(l => l).ToList();
            var landBuckets = new Dictionary<(int, int, int), List<Vector3>>();
            (int, int, int) K(Vector3 p) => ((int)MathF.Floor(p.X / 4), (int)MathF.Floor(p.Y / 4), (int)MathF.Floor(p.Z / 4));
            foreach (var p in land)
            {
                if (!landBuckets.TryGetValue(K(p), out var list)) landBuckets[K(p)] = list = [];
                list.Add(p);
            }
            var r = Corridor;
            var candidates = new HashSet<int>();
            foreach (var p in land)
                foreach (var n in shell.Within(p, r))
                    if (Vector3.DistanceSquared(p, shell.Nodes[n]) <= r * r) candidates.Add(n);
            var cells = (int)MathF.Ceiling(r / 4);
            var keep = new System.Collections.Concurrent.ConcurrentBag<int>();
            Parallel.ForEach(candidates, n =>
            {
                var q = shell.Nodes[n];
                var (bx, by, bz) = K(q);
                var close = new List<(float D, Vector3 P)>();
                for (var x = bx - cells; x <= bx + cells; x++)
                    for (var y = by - cells; y <= by + cells; y++)
                        for (var z = bz - cells; z <= bz + cells; z++)
                            if (landBuckets.TryGetValue((x, y, z), out var list))
                                foreach (var p in list)
                                    if (Vector3.Distance(p, q) is var dist && dist <= r) close.Add((dist, p));
                close.Sort((a, b) => a.D.CompareTo(b.D));
                foreach (var (_, p) in close.Take(24))
                    if (shell.Clear(p, q)) { keep.Add(n); break; }
            });
            return corridorSet = [.. keep];
        }

        // The shortest way over the shell from one node to another through allowed nodes (Dijkstra, up to a length).
        List<int>? Path(int from, int to, HashSet<int> allowed)
        {
            if (from == to) return [from];
            var dist = new Dictionary<int, float> { [from] = 0 };
            var back = new Dictionary<int, int>();
            // (A*: the straight distance left to the target as the estimate.)
            var goal = shell.Nodes[to];
            var queue = new PriorityQueue<int, float>();
            queue.Enqueue(from, Vector3.Distance(shell.Nodes[from], goal));
            var done = new HashSet<int>();
            while (queue.TryDequeue(out var n, out _))
            {
                if (!done.Add(n)) continue;
                var dn = dist[n];
                if (n == to)
                {
                    var path = new List<int> { to };
                    while (path[^1] != from) path.Add(back[path[^1]]);
                    path.Reverse();
                    return path;
                }
                foreach (var (m, length) in shell.Next[n])
                {
                    if (!allowed.Contains(m) && m != to) continue;
                    // (Along the sides and underside a step costs twice its length: the road loop keeps to the upper
                    // edges where it can.)
                    var dm = dn + (shell.IsEdge(n) && shell.IsEdge(m) ? length : 2 * length);
                    if (dm > MaxPath || dist.TryGetValue(m, out var old) && old <= dm) continue;
                    dist[m] = dm;
                    back[m] = n;
                    queue.Enqueue(m, dm + Vector3.Distance(shell.Nodes[m], goal));
                }
            }
            return null;
        }

        // Each loop's pieces: from a matched vertex (one with a road vertex it reaches) along the land loop to the next,
        // and the way over the road between their road vertices.
        var allowedOf = new Dictionary<int, HashSet<int>>();
        Time($"round {round + 1} regions and loops");
        for (var l = 0; l < loops.Count; l++)
        {
            var loop = loops[l];
            var open = openLoop[l];
            var g = Find(loop[0]);
            if (!loopsOf.TryGetValue(g, out var list)) loopsOf[g] = list = [];
            list.Add(loop);
            if (failed.ContainsKey(g)) continue;
            var node = loop.Select(v => shell.NearestPreferEdge(positions[v], LinkReach)).ToArray();
            var matched = Enumerable.Range(0, loop.Count).Where(i => node[i] >= 0).ToList();
            if (matched.Count == 0) { Fail(g, "a loop with no road vertex in reach", positions[loop[0]]); continue; }
            if (!allowedOf.TryGetValue(g, out var allowed)) allowedOf[g] = allowed = Allowed(g);
            // The ways between matched vertices in turn; a matched vertex where the road loop goes to its road vertex and
            // straight back (the way to it ends where the way on begins: its road vertex lies off the line, up a pipe's
            // side, and the strip would stand there as a tent) is matched no more, the way going on past it, until none.
            // (An open border's ways run from its first matched vertex to its last, not round.)
            var wayCount = open ? matched.Count - 1 : matched.Count;
            var ways = new List<int>?[matched.Count];
            for (var m = 0; m < wayCount; m++) ways[m] = Path(node[matched[m]], node[matched[(m + 1) % matched.Count]], allowed);
            for (var pass = 0; pass < 100000 && matched.Count > 2; pass++)
            {
                var drop = -1;
                for (var m = open ? 1 : 0; m < (open ? matched.Count - 1 : matched.Count) && drop < 0; m++)
                {
                    var before = ways[(m + matched.Count - 1) % matched.Count];
                    var after = ways[m];
                    if (before is { Count: > 1 } && after is { Count: > 1 } && before[^2] == after[1]) drop = m;
                }
                if (drop < 0) break;
                var prev = (drop + matched.Count - 1) % matched.Count;
                var merged = Path(node[matched[prev]], node[matched[(drop + 1) % matched.Count]], allowed);
                if (merged is null) break;
                matched.RemoveAt(drop);
                var list2 = ways.ToList();
                list2.RemoveAt(drop);
                ways = [.. list2];
                ways[drop == 0 ? matched.Count - 1 : drop - 1] = merged;
                wayCount = open ? matched.Count - 1 : matched.Count;
                spurs++;
            }
            if (open)
            {
                // The open border's ends: from the land's edge to its first matched vertex, and from its last to the land's
                // edge again, fanned to those vertices' road vertices.
                if (matched[0] > 0) AddPiece(g, loop.Take(matched[0] + 1).ToList(), [node[matched[0]]]);
                if (matched[^1] < loop.Count - 1) AddPiece(g, loop.Skip(matched[^1]).ToList(), [node[matched[^1]]]);
            }
            for (var m = 0; m < wayCount; m++)
            {
                int i = matched[m], i2 = matched[(m + 1) % matched.Count];
                var path = ways[m];
                if (path is null)
                {
                    if (stats) Console.WriteLine($"      no way: loop vertex at {positions[loop[i]]} to {positions[loop[i2]]}, road vertices {shell.Where[node[i]]} to {shell.Where[node[i2]]}, {Vector3.Distance(shell.Nodes[node[i]], shell.Nodes[node[i2]]):0.0} m apart; in the corridor: {allowed.Contains(node[i])}, {allowed.Contains(node[i2])}");
                    Fail(g, "no way over the road between two loop vertices", positions[loop[i]]);
                    break;
                }
                var land = new List<int> { loop[i] };
                for (var k = (i + 1) % loop.Count; ; k = (k + 1) % loop.Count)
                {
                    land.Add(loop[k]);
                    if (k == i2) break;
                }
                AddPiece(g, land, path);
            }
            foreach (var i in matched) links.Add((positions[loop[i]], shell.Nodes[node[i]]));
        }
        Time($"round {round + 1} links and ways");
        if (stats) Console.WriteLine($"    [loops] round {round + 1}: {groupLand.Count} groups, {failed.Count} failing");
        if (failed.Count == 0 || round >= 39) break;
        // Spared: the region's triangles round each failure, wider each round.
        foreach (var at in failedAt)
            for (var t = 0; t < triangles; t++)
                if (touches[t] && Vector3.DistanceSquared(centre[t], at) < spareRadius * spareRadius) spared[t] = true;
        spareRadius *= 1.2f;
        }
        foreach (var (a, b) in links) Line(a, b, 9);

        // The groups that close: their regions out, their strips in.
        if (Environment.GetEnvironmentVariable("TMSPLINE_LOOP_PROBE") is { Length: > 0 } probes)
            foreach (var probe in probes.Split('|'))
            {
                var pv = probe.Split(';').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var at = new Vector3(pv[0], pv[1], pv[2]);
                for (var t = 0; t < triangles; t++)
                {
                    if (Vector3.Distance(centre[t], at) > pv[3]) continue;
                    int a = indices[3 * t], b = indices[3 * t + 1], c = indices[3 * t + 2];
                    var g = Find(a);
                    if (room is not null && surface is not null)
                        foreach (var v in new[] { a, b, c })
                            Console.WriteLine($"      vertex {v} at {positions[v]}: room {room(positions[v]):0.00}, over the deck {surface(positions[v]).Distance:0.00} (deck point {surface(positions[v]).Point})");
                    Console.WriteLine($"    probe triangle {t} at {centre[t]}: near {near[a]},{near[b]},{near[c]} surface {nearTriangle[t]}, bridge {bridge[a] || bridge[b] || bridge[c]}, spared {spared[t]}, touches {touches[t]}, group {(touches[t] ? (failed.TryGetValue(g, out var why) ? why : "cut") : "-")}");
                }
            }
        var cut = new bool[triangles];
        for (var t = 0; t < triangles; t++) cut[t] = touches[t] && !failed.ContainsKey(Find(indices[3 * t]));
        var result = new List<int>(indices.Count);
        for (var t = 0; t < triangles; t++)
            if (!cut[t]) result.AddRange([indices[3 * t], indices[3 * t + 1], indices[3 * t + 2]]);
        var roadVertex = new Dictionary<int, int>();
        int RoadVertex(int n)
        {
            if (roadVertex.TryGetValue(n, out var id)) return id;
            id = positions.Count;
            positions.Add(shell.Nodes[n]);
            mesh.Pinned.Add(false);
            return roadVertex[n] = id;
        }
        var strips = 0;
        foreach (var (g, _, road, corners) in pieces)
        {
            if (failed.ContainsKey(g)) continue;
            for (var j = 0; j + 1 < road.Count; j++) Line(shell.Nodes[road[j]], shell.Nodes[road[j + 1]], 7);
            foreach (var c in corners) result.Add(c >= 0 ? c : RoadVertex(-1 - c));
            strips += corners.Count / 3;
        }
        foreach (var (g, list) in loopsOf)
            foreach (var loop in list)
                for (var i = 0; i < loop.Count; i++)
                    Line(positions[loop[i]], positions[loop[(i + 1) % loop.Count]], failed.ContainsKey(g) ? 8 : 6);

        if (stats)
        {
            var groups = groupLand.Count;
            Console.WriteLine($"  [loops] D {d} m: {near.Count(x => x)} land vertices near the roads; after {rounds} rounds {groups} groups, {loops.Count} land loops; {groups - failed.Count} groups cut and zipped ({strips} strip triangles, {cut.Count(c => c)} land triangles out), {failed.Count} left whole, {spared.Count(x => x)} triangles spared round failures, {spurs} matched vertices dropped for going up and back, {bridgeTriangles} land triangles bridges between separate roads:");
            foreach (var why in failed.GroupBy(f => f.Value))
                Console.WriteLine($"    {why.Count()} {why.Key}");
        }
        // Land left whole that reaches into a road's space (the voxels follow a curved road's space only roughly, and
        // spared land is not cut back): its vertices there moved to just under the driving surface's back, the slab's
        // thickness and a little more, where a road has a deck over it. Vertices of strips stay where they are.
        var pushed = 0;
        if (room is not null && deckOf is not null && surface is not null)
        {
            var stripVertex = new HashSet<int>();
            var firstNew = positions.Count - roadVertex.Count;
            for (var t = 0; t + 2 < result.Count; t += 3)
                if (result[t] >= firstNew || result[t + 1] >= firstNew || result[t + 2] >= firstNew)
                    for (var k = 0; k < 3; k++) stripVertex.Add(result[t + k]);
            var thickness = (float)new RoadMeshSettings().Thickness;
            foreach (var v in result.Distinct().ToList())
            {
                if (v >= firstNew || stripVertex.Contains(v)) continue;
                var p = positions[v];
                // (Only just over a driving surface, within 2 m: higher in a road's space is a tunnel's roof, not to move.)
                if (room(p) >= 0 || surface(p).Distance > 2) continue;
                var (_, deck) = deckOf(p);
                if (deck == default) continue;
                positions[v] = surface(p).Point - deck * (thickness + 0.25f);
                pushed++;
            }
            if (stats) Console.WriteLine($"  [loops] {pushed} land vertices left whole inside a road's space moved under it");
        }

        // The hole check: the open edges left. On the road (both ends road vertices: the strips' road side, which the road
        // covers) and the land's own border (edges the land had open before) are fine; any other is a hole, drawn red (8)
        // with a cross.
        {
            var uses = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            for (var t = 0; t + 2 < result.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = result[t + k], b = result[t + (k + 1) % 3];
                    var key = a < b ? Key(a, b) : Key(b, a);
                    uses[key] = uses.GetValueOrDefault(key) + 1;
                }
            var origUses = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            for (var t = 0; t + 2 < indices.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = indices[t + k], b = indices[t + (k + 1) % 3];
                    var key = a < b ? Key(a, b) : Key(b, a);
                    origUses[key] = origUses.GetValueOrDefault(key) + 1;
                }
            var firstRoadVertex = positions.Count - roadVertex.Count;
            var ownEdgeVertex = new HashSet<int>();
            foreach (var (e, n) in origUses)
                if (n == 1) { ownEdgeVertex.Add((int)(e >> 32)); ownEdgeVertex.Add((int)(e & 0xffffffff)); }
            int onRoad = 0, ownBorder = 0, holes = 0;
            foreach (var (e, n) in uses)
            {
                if (n != 1) continue;
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                if (a >= firstRoadVertex && b >= firstRoadVertex) { onRoad++; continue; }
                if (a < firstRoadVertex && b < firstRoadVertex && origUses.GetValueOrDefault(e) == 1) { ownBorder++; continue; }
                // (A strip's end where an open border meets the land's own edge: from that edge to the road.)
                if ((a >= firstRoadVertex) != (b >= firstRoadVertex) && ownEdgeVertex.Contains(a < firstRoadVertex ? a : b)) { ownBorder++; continue; }
                holes++;
                Line(positions[a], positions[b], 8);
                Cross(positions[a], 8, 1f);
            }
            LastHoles = holes;
            if (stats) Console.WriteLine($"  [loops] open edges: {onRoad} on the road, {ownBorder} the land's own border, {holes} holes");
        }
        mesh.Indices.Clear();
        mesh.Indices.AddRange(result);
        return debug.ToArray();
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

    /// <summary>The last build's hole count (open edges neither on a road nor the land's own border).</summary>
    public static int LastHoles { get; private set; }
}
