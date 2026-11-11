using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>A vertex of the lining (the rock under land that passes over a road): position, normal (into the road's room), its track, its place on the track's chart (along, round the arch; metres).</summary>
public readonly record struct LiningVertex(Vector3 Position, Vector3 Normal, int Road, Vector2 Chart);

/// <summary>What <see cref="StitchedTerrain"/> builds: the land's height map and pins for the mesher, the mesh, the lining.</summary>
public sealed class StitchedTerrainResult
{
    public required TerrainField Field { get; init; }
    public required List<TerrainPin> Pins { get; init; }
    public required AdaptiveMesh Land { get; init; }
    public List<LiningVertex> LiningVertices { get; } = [];
    /// <summary>Three indices into LiningVertices per triangle, wound to face into the road's room.</summary>
    public List<int> LiningIndices { get; } = [];
    /// <summary>Whether a point is inside a road's room (the space a car needs on it, plus the road's top).</summary>
    public required Func<Vector3, bool> InRoom { get; init; }
    /// <summary>For the checker: how the ground at (x, z) came about.</summary>
    public required Func<float, float, string> Explain { get; init; }
    public Dictionary<string, long> Milliseconds { get; } = [];
    /// <summary>For the checker: things worth knowing about this build.</summary>
    public List<string> Notes { get; } = [];
    /// <summary>Per track and side the share of its stations whose edge the land holds (0..1).</summary>
    public List<(int Track, float Left, float Right)> Attached { get; } = [];
    /// <summary>For the checker: per station its track, distance, room section (world), ground minus section per point, held (left, right).</summary>
    public List<(int Track, float Along, Vector3[] Section, float[] F, float HeldLeft, float HeldRight)> Stations { get; } = [];
}

/// <summary>
/// The own terrain, one mechanism for every road shape ("stitched ground"):
/// <list type="number">
/// <item>Rails: each road edge (its curb's outer top, RoadMeshBuilder.EdgeContact), every 2 m or less. One test per rail
/// decides whether land holds it (faces the sky, not too steep along the road, an open slot, not overhung, land not far
/// above it); the answer is smoothed along the road to on or off, with a smooth ramp between.</item>
/// <item>Ground: one height per column, the editor's land blended into the held rails' heights, each rail only on its own
/// outer side (and across its road), the upper road winning where roads lie over each other; beside a road that faces
/// sideways the land stays below the space it faces.</item>
/// <item>Rooms: per road a closed section round the space a car needs (the driving surface and an arch over it), swept
/// along the road. Land inside a room is cut out. Where the ground meets a room's boundary (an exact line, found on the
/// room's own chart) is a fixed edge of the land's mesh: along held rails that is the rail itself.</item>
/// <item>Lining: where the ground lies over a room's arch (a road under land), the arch is the tunnel's ceiling: rock,
/// sharing its edge with the land.</item>
/// </list>
/// The land stays a height map (one sheet per column) outside tunnels; the mesh comes from AdaptiveTerrainMesher.
/// </summary>
public static class StitchedTerrain
{
    public const double Cell = 0.5;
    private const float StationStep = 2f;
    /// <summary>Room's boundary this far inside the road's body (the land's edge is hidden in it), metres.</summary>
    private const float Tuck = 0.03f;
    /// <summary>The arch over the driving surface (along its normals, less on tight bends), and the rims' lift along the section's opening, metres.</summary>
    private const float ArchHeight = 4.5f, RimLift = 2f;
    /// <summary>Between a road's held edges the ground lies this much over their line (inside the room: cut out), metres.</summary>
    private const float InteriorLift = 1.2f;
    private const int DeckN = 24, ArchN = 12;
    /// <summary>Chart index of the right rail; the left rail is 0; the arch runs from the right rail to U (the left again).</summary>
    private const int RailRight = DeckN + 2, SectionPoints = DeckN + ArchN + 2;
    private const float Bucket = 8f;
    private const float MinReach = 16, MaxReach = 64;
    /// <summary>Softmax temperature of the level preference (the upper road wins), metres.</summary>
    private const float Tau = 2f;
    /// <summary>A held rail is exact (the land's line is the rail) where the ground at it is this near it, metres.</summary>
    private const float Won = 0.6f;
    private const double MinFidelity = 0.06, FarStart = 48, FarFade = 32, FarFidelity = 0.02, FidelityFalloff = 10;
    private const double StadiumHalf = 768, Sunk = -2;

    private sealed class Station
    {
        public required Ring R;
        public required TrackSegment Seg;
        public Vector3 P, Right, Up, T;
        /// <summary>The section round the room, ring-local (right, up): rail L, deck L..R, rail R, arch R..L.</summary>
        public Vector2[] Poly = [];
        public Vector3[] W = [];
        /// <summary>The driving surface's normal at the deck points (W[1 + k]).</summary>
        public Vector3[] DeckNormal = [];
        public Vector3 Centre;
        public Vector3 MeanNormal;
        public float Chord;
        /// <summary>The floor's corner before a sideways opening: the arch's first point from the right rail (1) or the left (-1), 0 none.</summary>
        public int FootEnd;
        /// <summary>How far (0..1) the land's edge on that side is the floor's corner instead of the rail.</summary>
        public float FootW;

        /// <summary>
        /// The point the land holds on a side (0 left, 1 right): the rail, or before a sideways opening the floor's
        /// corner (the arch's first point from the rail): the land's edge is that line by design, the floor between it
        /// and the rail is built with the lining. Seen from above a wall's two rails lie on one line at two heights,
        /// which one height per place cannot meet; the floor's corner lies out in front of both.
        /// </summary>
        public Vector3 Held(int sd)
        {
            var rail = sd == 0 ? 0 : RailRight;
            if (!FootSeam || FootW <= 0 || FootEnd == 0 || (FootEnd > 0) != (sd == 1)) return W[rail];
            return Vector3.Lerp(W[rail], W[sd == 1 ? RailRight + 1 : SectionPoints - 1], FootW);
        }

        /// <summary>FootW on the side that has the floor's corner, 0 on the other.</summary>
        public float SeamW(int sd) => FootSeam && FootEnd != 0 && (FootEnd > 0) == (sd == 1) ? FootW : 0;
    }

    /// <summary>Debug: TMSPLINE_FOOTSEAM=off keeps the land's edge at the rail before sideways openings.</summary>
    private static readonly bool FootSeam = Environment.GetEnvironmentVariable("TMSPLINE_FOOTSEAM") != "off";

    private sealed class Road
    {
        public int Index;
        public required Track Track;
        public List<Station> S = [];
        public float[] Dist = [];
        /// <summary>Per side (0 left, 1 right) per station: how far land holds the rail (0..1), and whether it is exact.</summary>
        public float[][] A = [[], []];
        public bool[][] Exact = [[], []];
        public float[,] F = new float[0, 0];
    }

    private struct Rail
    {
        /// <summary>The rail, outward (plan), along (plan), the surface's normal at the rim, the other rail of its station.</summary>
        public Vector3 C, O, T, N, Opp;
        /// <summary>0 at an open track's end, 1 from 4 m along it: across the road the ground meets the end's line there.</summary>
        public float A, Reach, Inward, Along, EndFade;
        /// <summary>How far (plan, metres) the held point lies out from its rail (a floor's corner), 0 at the rail.</summary>
        public float Floor;
        /// <summary>The station's mean surface normal's Y (how far the driving side faces up).</summary>
        public float FacesUp;
        public int Road, Station, Side;
    }

    private struct Face
    {
        public Vector3 Low, F;
        public float E;
    }

    private sealed class Prism
    {
        public int Road, Station;
        public required Station A, B;
    }

    private static float SS(float a, float b, float x)
    {
        x = Math.Clamp((x - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static float Quintic(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (6 * t - 15) + 10);
    }

    private static Vector3 V(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <param name="maxTriangles">The land's triangle budget (0: none): the mesh gets coarser until it fits.</param>
    public static StitchedTerrainResult Build(TerrainGrid grid, TrackMap map, int maxTriangles = 0) => new Builder(grid, map, maxTriangles).Run();

    private sealed class Builder(TerrainGrid grid, TrackMap map, int maxTriangles)
    {
        private readonly List<Road> roads = [];
        private readonly List<Rail> rails = [];
        private readonly List<Face> faces = [];
        private readonly List<Prism> prisms = [];
        private readonly Dictionary<(int, int), List<int>> prismBuckets = [];
        private double x0, z0;
        private int cols, rows;
        private float[] nat = [], g = [], fid = [];

        /// <summary>Each cell's distance to the nearest road, metres.</summary>
        private float[] roadDistance = [];

        /// <summary>The budget coarsens the land up to NearScale within BudgetNear of a road, and fully from BudgetFar on.</summary>
        private const float BudgetNear = 16, BudgetFar = 48;

        /// <summary>Within BudgetNear of a road the budget coarsens at most this much (none within 2 m).</summary>
        private const double NearScale = 4;
        private bool[] cut = [];
        private List<int>[] railBuckets = [];
        private int bCols, bRows;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Dictionary<string, long> times = [];

        private void Lap(string name)
        {
            times[name] = clock.ElapsedMilliseconds;
            clock.Restart();
        }

        public StitchedTerrainResult Run()
        {
            cols = (int)Math.Ceiling((grid.Columns - 1) * grid.Cell / Cell);
            rows = (int)Math.Ceiling((grid.Rows - 1) * grid.Cell / Cell);
            x0 = grid.X0;
            z0 = grid.Z0;
            nat = new float[cols * rows];
            Parallel.For(0, rows, r =>
            {
                for (var c = 0; c < cols; c++) nat[r * cols + c] = (float)AdaptiveTerrainBuilder.SampleGrid(grid, x0 + (c + 0.5) * Cell, z0 + (r + 0.5) * Cell);
            });
            Sections();
            Lap("sections");
            Attachment();
            Lap("attachment");
            Ground();
            Lap("ground");
            var pins = Contours(out var liningChains);
            Lap("contours");
            var field = new TerrainField(x0, z0, Cell, cols, rows, g, fid, cut)
            {
                // Cut where the triangle's middle is in a room, or the ground there is (a triangle spanning a twisting
                // road between its rails can pass under the surface at its middle).
                CutAtHeight = (x, y, z) =>
                {
                    var p = new Vector3((float)x, (float)y, (float)z);
                    if (InRoom(p)) return true;
                    var gy = GAt(p.X, p.Z);
                    return !float.IsNaN(gy) && InRoom(new Vector3(p.X, gy - 0.02f, p.Z));
                },
            };
            // The budget: the land away from the roads gets coarser (its fidelity divided by the scale) until it fits, at
            // most five more tries. The land along the roads keeps its detail: that is where it has to meet them, and
            // coarser there it cuts through the roads and leaves the lining in the open.
            var scale = 1.0;
            var baseFid = (float[])fid.Clone();
            AdaptiveMesh land;
            var tries = 0;
            var last = int.MaxValue;
            while (true)
            {
                if (scale > 1)
                    for (var k = 0; k < fid.Length; k++)
                    {
                        static double Ramp(double v, double a, double b) { var t = Math.Clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
                        var near = Math.Min(scale, NearScale);
                        fid[k] = (float)(baseFid[k] / Math.Pow(near, Ramp(roadDistance[k], 2, BudgetNear)) / Math.Pow(scale / near, Ramp(roadDistance[k], BudgetNear, BudgetFar)));
                    }
                land = AdaptiveTerrainMesher.Build(field, pins, new MesherSettings(Tolerance: 0.08, MinEdge: 1.5, MaxEdge: 48, PinSpacing: PinPiece + 0.05, MaxRounds: 100, OutlineSpacing: 8));
                var count = land.Indices.Count / 3;
                // Stop when it fits, or when coarsening no longer helps (the roads' own edges need the rest).
                if (maxTriangles <= 0 || count <= maxTriangles || ++tries > 5 || count > 0.95 * last) break;
                last = count;
                scale *= Math.Clamp(1.15 * count / maxTriangles, 1.5, 4);
            }
            times["budget scale x100"] = (long)Math.Round(scale * 100);
            Lap("mesh");
            var result = new StitchedTerrainResult { Field = field, Pins = pins, Land = land, InRoom = p => InRoom(p), Explain = Explain };
            Lining(liningChains, result);
            Lap("lining");
            foreach (var (k, v) in times) result.Milliseconds[k] = v;
            foreach (var road in roads)
                for (var i = 0; i < road.S.Count; i++)
                    result.Stations.Add((road.Index, road.Dist[i], road.S[i].W, Enumerable.Range(0, SectionPoints).Select(u => road.F[i, u]).ToArray(), road.A[0][i], road.A[1][i]));
            foreach (var road in roads)
                result.Attached.Add((road.Index, road.A[0].Count(a => a >= 0.98f) / (float)Math.Max(1, road.S.Count), road.A[1].Count(a => a >= 0.98f) / (float)Math.Max(1, road.S.Count)));
            return result;
        }

        // ---------------------------------------------------------------- 1. Stations and their sections

        private static Ring Lerp(Ring a, Ring b, double t)
        {
            var r = a.Clone();
            r.Position = a.Position + (b.Position - a.Position) * t;
            r.Up = (a.Up + (b.Up - a.Up) * t).Normalized();
            r.Right = (a.Right + (b.Right - a.Right) * t).Normalized();
            r.Tangent = (a.Tangent + (b.Tangent - a.Tangent) * t).Normalized();
            r.Width = a.Width + (b.Width - a.Width) * t;
            r.Curve = a.Curve + (b.Curve - a.Curve) * t;
            r.Flat = a.Flat + (b.Flat - a.Flat) * t;
            r.Sausage = a.Sausage + (b.Sausage - a.Sausage) * t;
            r.BorderHeight = a.BorderHeight + (b.BorderHeight - a.BorderHeight) * t;
            r.BorderScale = a.BorderScale + (b.BorderScale - a.BorderScale) * t;
            r.EdgeAnchor = a.EdgeAnchor + (b.EdgeAnchor - a.EdgeAnchor) * t;
            r.Distance = a.Distance + (b.Distance - a.Distance) * t;
            return r;
        }

        private void Sections()
        {
            for (var ti = 0; ti < map.Tracks.Count; ti++)
            {
                var track = map.Tracks[ti];
                track.SyncSegments();
                var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.High));
                if (rings.Count < 2 || track.Segments.Count == 0) continue;
                var road = new Road { Index = ti, Track = track };
                var list = new List<Ring>();
                for (var i = 0; i + 1 < rings.Count; i++)
                {
                    var n = Math.Max(1, (int)Math.Ceiling((rings[i + 1].Distance - rings[i].Distance) / StationStep));
                    for (var k = 0; k < n; k++) list.Add(k == 0 ? rings[i] : Lerp(rings[i], rings[i + 1], (double)k / n));
                }
                list.Add(rings[^1]);
                foreach (var r in list)
                {
                    var seg = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)];
                    var st = new Station { R = r, Seg = seg, P = V(r.Position), Right = V(r.Right), Up = V(r.Up), T = V(r.Tangent) };
                    Section(st);
                    road.S.Add(st);
                }
                road.Dist = road.S.Select(s => (float)s.R.Distance).ToArray();
                roads.Add(road);
            }
            Decks();
            if (Environment.GetEnvironmentVariable("TMSPLINE_SKY") != "off") Sky();
            // The rooms between consecutive stations, in plan buckets.
            foreach (var road in roads)
                for (var i = 0; i + 1 < road.S.Count; i++)
                {
                    var p = new Prism { Road = road.Index, Station = i, A = road.S[i], B = road.S[i + 1] };
                    var id = prisms.Count;
                    prisms.Add(p);
                    float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                    foreach (var w in p.A.W.Concat(p.B.W))
                    {
                        minX = Math.Min(minX, w.X); maxX = Math.Max(maxX, w.X); minZ = Math.Min(minZ, w.Z); maxZ = Math.Max(maxZ, w.Z);
                    }
                    for (var bx = (int)Math.Floor(minX / Bucket); bx <= (int)Math.Floor(maxX / Bucket); bx++)
                        for (var bz = (int)Math.Floor(minZ / Bucket); bz <= (int)Math.Floor(maxZ / Bucket); bz++)
                        {
                            if (!prismBuckets.TryGetValue((bx, bz), out var l)) prismBuckets[(bx, bz)] = l = [];
                            l.Add(id);
                        }
                }
        }

        /// <summary>
        /// The open sky over the driving surface: where a deck point faces up and no road surface is over it for
        /// SkyClear metres, the room reaches straight up (to SkyHeight, or SkyEarth under the next road over it), so no
        /// land hangs over a steep bank or into an open half-pipe. Under a road closer than that (a pipe's or a curl's
        /// own top, the lap above, a low bridge) the room keeps its arch: the land wraps round as a tunnel.
        /// </summary>
        /// <summary>The decks' triangles (road, station) in plan buckets.</summary>
        private readonly List<(Vector3 A, Vector3 B, Vector3 C, int Road, int Station)> deckTris = [];
        private readonly Dictionary<(int, int), List<int>> deckBuckets = [];

        private void Decks()
        {
            foreach (var road in roads)
                for (var i = 0; i + 1 < road.S.Count; i++)
                {
                    var a = road.S[i].W;
                    var b = road.S[i + 1].W;
                    // Rail to rail (0 .. RailRight).
                    for (var u = 0; u < RailRight; u++)
                    {
                        Add(a[u], a[u + 1], b[u + 1], road.Index, i);
                        Add(a[u], b[u + 1], b[u], road.Index, i);
                    }
                }
            void Add(Vector3 a, Vector3 b, Vector3 c, int road, int station)
            {
                var id = deckTris.Count;
                deckTris.Add((a, b, c, road, station));
                for (var bx = (int)Math.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Bucket); bx <= (int)Math.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Bucket); bx++)
                    for (var bz = (int)Math.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Bucket); bz <= (int)Math.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Bucket); bz++)
                    {
                        if (!deckBuckets.TryGetValue((bx, bz), out var l)) deckBuckets[(bx, bz)] = l = [];
                        l.Add(id);
                    }
            }
        }

        /// <summary>The decks' heights straight over or under (x, z), each with its road and station.</summary>
        private IEnumerable<(float Y, int Road, int Station)> DecksAt(float x, float z)
        {
            if (!deckBuckets.TryGetValue(((int)Math.Floor(x / Bucket), (int)Math.Floor(z / Bucket)), out var list)) yield break;
            foreach (var id in list)
            {
                var (a, b, c, road, station) = deckTris[id];
                var det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(det) < 1e-9f) continue;
                var l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
                var l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
                if (l1 < 0 || l2 < 0 || l1 + l2 > 1) continue;
                yield return (l1 * a.Y + l2 * b.Y + (1 - l1 - l2) * c.Y, road, station);
            }
        }

        private void Sky()
        {
            // FacesUp is low: straight up from a steep deck runs nearly along it, so the room the column adds over the
            // deck (its height times the normal's Y) fades out by itself towards a wall; fading the height as well left
            // land 15-25 m over 60-75 degree banks.
            const float SkyHeight = 40, SkyEarth = 2.5f, SkyClear = ArchHeight + SkyEarth, FacesUp = 0.02f, FadeIn = 0.13f;
            // The height of the nearest deck over p (straight up), infinity if none.
            float Over(Vector3 p)
            {
                var best = float.PositiveInfinity;
                foreach (var (y, _, _) in DecksAt(p.X, p.Z))
                {
                    var d = y - p.Y;
                    if (d > 0.3f && d < best) best = d;
                }
                return best;
            }
            foreach (var road in roads)
            {
                var n = road.S.Count;
                var raw = new float[n, DeckN + 1];
                for (var i = 0; i < n; i++)
                {
                    var st = road.S[i];
                    for (var k = 0; k <= DeckN; k++)
                    {
                        // Faded in with how much the surface faces up (no step where it turns past FacesUp).
                        var ny = st.DeckNormal[k].Y;
                        var face = Math.Clamp((ny - FacesUp) / FadeIn, 0, 1);
                        if (face <= 0) continue;
                        var over = Over(st.W[1 + k]);
                        if (over < SkyClear) continue;
                        raw[i, k] = MathF.Min(SkyHeight, over - SkyEarth) * face * face * (3 - 2 * face);
                    }
                }
                // Smoothed along the road: the least over a few stations, then their mean (the walls' tops do not step
                // from station to station; never higher than the road over it allows).
                const int W = 3;
                for (var i = 0; i < n; i++)
                {
                    var sky = new float[DeckN + 1];
                    var any = false;
                    for (var k = 0; k <= DeckN; k++)
                    {
                        float sum = 0;
                        var cnt = 0;
                        for (var j = Math.Max(0, i - W); j <= Math.Min(n - 1, i + W); j++)
                        {
                            var least = float.MaxValue;
                            for (var m = Math.Max(0, j - W); m <= Math.Min(n - 1, j + W); m++) least = MathF.Min(least, raw[m, k]);
                            sum += least;
                            cnt++;
                        }
                        sky[k] = MathF.Min(raw[i, k], sum / cnt);
                        if (sky[k] > ArchHeight) any = true; else sky[k] = 0;
                    }
                    if (any) Section(road.S[i], sky);
                }
            }
        }

        private static List<Vector2> Hull(List<Vector2> p)
        {
            p = p.OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
            static float Cr(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<Vector2>();
            foreach (var q in p) { while (h.Count >= 2 && Cr(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            var t = h.Count + 1;
            for (var i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= t && Cr(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        /// <summary>The station's section: rails, deck (tucked), arch; world points; mean normal; slot chord.</summary>
        private static void Section(Station st, float[]? sky = null)
        {
            var r = st.R;
            Vector2 Loc(Vector3 w) { var d = w - st.P; return new(Vector3.Dot(d, st.Right), Vector3.Dot(d, st.Up)); }
            var deck = new Vector2[DeckN + 1];
            var offset = new List<Vector2>();
            var nb = Vector2.Zero;
            st.DeckNormal = new Vector3[DeckN + 1];
            for (var k = 0; k <= DeckN; k++)
            {
                var q = RoadMeshBuilder.SurfaceSample(r, (k / (double)DeckN - 0.5) * r.Width);
                var n = new Vector2((float)q.NX, (float)q.NY);
                st.DeckNormal[k] = st.Right * n.X + st.Up * n.Y;
                var pt = new Vector2((float)q.X, (float)q.Y);
                deck[k] = pt - n * Tuck;
                var rm = ArchHeight;
                if (q.Curvature > 1e-4) rm = Math.Min(rm, (float)(0.9 / q.Curvature));
                offset.Add(pt + n * rm);
                nb += n;
            }
            nb = nb.LengthSquared() > 1e-4f ? Vector2.Normalize(nb) : Vector2.UnitY;
            var mid = deck.Aggregate((a, b) => a + b) / deck.Length;
            Vector2 TuckIn(Vector2 rail) { var d = mid - rail; return d.LengthSquared() > 1e-6f ? rail + Vector2.Normalize(d) * Tuck : rail; }
            var railL = TuckIn(Loc(V(RoadMeshBuilder.EdgeContact(r, st.Seg, -1))));
            var railR = TuckIn(Loc(V(RoadMeshBuilder.EdgeContact(r, st.Seg, 1))));
            st.Chord = (railR - railL).Length();

            // The arch: the hull of the deck's offset, the rails and the rails lifted, from the right rail round the opening to the left.
            List<Vector2> chain;
            // The floor's corner (see below): which end of the arch it is at (1 the right rail's, -1 the left's), how far
            // (0..1) the opening faces sideways.
            var foot = Vector2.Zero;
            var footEnd = 0;
            var footW = 0f;
            if (st.Chord < 3f) chain = [railR, railL];
            else
            {
                var pts = new List<Vector2>(offset) { railL, railR, railL + nb * RimLift, railR + nb * RimLift };
                pts.AddRange(deck);
                // Out of the opening: the rails pushed ArchHeight from their chord, away from the deck (on a curled
                // section the opening faces sideways or down, and the land in front of it would close it).
                var chord = railR - railL;
                var o = Vector2.Normalize(new Vector2(-chord.Y, chord.X));
                var toDeck = Vector2.Dot(o, mid - (railL + railR) / 2);
                if (MathF.Abs(toDeck) < 0.5f ? Vector2.Dot(o, nb) < 0 : toDeck > 0) o = -o;
                Vector2 oL = o * ArchHeight, oR = oL;
                // Where the opening faces sideways (a wall, an overhang, a pipe on its side) the lower rail's goes out
                // level instead: the floor in front of the wall, to under all of a deck leaning over it. The room's
                // underside is then the floor's level from the rail on, and the land in front meets it there (sloping
                // up along the arch it met the land metres over the rail: a bank of rock before the driving surface).
                var upw = new Vector2(st.Right.Y, st.Up.Y);
                if (upw.LengthSquared() > 1e-4f)
                {
                    upw = Vector2.Normalize(upw);
                    footW = 1 - SS(0.35f, 0.7f, MathF.Abs(Vector2.Dot(o, upw)));
                    if (footW > 0)
                    {
                        var level = new Vector2(upw.Y, -upw.X);
                        if (Vector2.Dot(level, o) < 0) level = -level;
                        var low = Vector2.Dot(railR, upw) < Vector2.Dot(railL, upw) ? railR : railL;
                        var reach = ArchHeight;
                        foreach (var d in deck) reach = MathF.Max(reach, Vector2.Dot(d - low, level) + 2);
                        // A little under the rail's level at its far end: the land in front (at the rail's level or just
                        // under it) then meets the arch rising from the corner, at its own height, one clean line; level
                        // with it the two met wherever the ground's cells stepped (a sawtooth of small holes).
                        var push = Vector2.Lerp(o * ArchHeight, level * reach - upw * 0.3f, footW);
                        if (low == railR) { oR = push; footEnd = 1; } else { oL = push; footEnd = -1; }
                        foot = low + push;
                    }
                }
                pts.Add(railL + oL);
                pts.Add(railR + oR);
                // The open sky over the deck (see Sky): straight up in the section's plane.
                // The middle the hull's corners are ordered round: that of the hull without the sky (inside it either way;
                // the sky's tall column would draw it away from the deck, and corners under a curled lip out of order).
                var near = Hull(pts);
                var c = near.Aggregate((a, b) => a + b) / near.Count;
                var up = new Vector2(st.Right.Y, st.Up.Y);
                if (sky is not null && up.LengthSquared() > 1e-4f)
                {
                    up = Vector2.Normalize(up);
                    for (var k = 0; k <= DeckN; k++)
                        if (sky[k] > 0) pts.Add(deck[k] + up * sky[k]);
                }
                var hull = Hull(pts);
                static float Ang(Vector2 v) => MathF.Atan2(v.Y, v.X);
                static float Mod(float a) { a %= 2 * MathF.PI; return a < 0 ? a + 2 * MathF.PI : a; }
                float aR = Ang(railR - c), aL = Ang(railL - c), aN = Ang(nb);
                var ccw = Mod(aL - aR);
                var dir = Mod(aN - aR) < ccw ? 1 : -1;
                var span = dir > 0 ? ccw : 2 * MathF.PI - ccw;
                chain = [railR];
                foreach (var (v, t) in hull.Select(v => (v, dir > 0 ? Mod(Ang(v - c) - aR) : Mod(aR - Ang(v - c)))).Where(x => x.Item2 > 1e-3f && x.Item2 < span - 1e-3f).OrderBy(x => x.Item2))
                    if ((v - railR).Length() > 0.05f && (v - railL).Length() > 0.05f) chain.Add(v);
                chain.Add(railL);
                // The floor's corner only where it is the hull's corner next to its rail.
                // The floor's corner only where it is one of the hull's first corners from its rail (the rim's lift may
                // stand between them).
                if (footEnd != 0)
                {
                    var at = -1;
                    for (var i = 1; i <= 2 && i + 1 < chain.Count; i++)
                        if ((chain[footEnd > 0 ? i : chain.Count - 1 - i] - foot).Length() < 0.05f) { at = i; break; }
                    if (at < 0) footEnd = 0;
                }
                // Rounded corners (Chaikin, ends kept).
                for (var pass = 0; pass < 2 && chain.Count > 2; pass++)
                {
                    var next = new List<Vector2> { chain[0] };
                    for (var i = 0; i + 1 < chain.Count; i++)
                    {
                        var a = chain[i];
                        var b = chain[i + 1];
                        if (i > 0) next.Add(Vector2.Lerp(a, b, 0.25f));
                        if (i + 2 < chain.Count) next.Add(Vector2.Lerp(a, b, 0.75f));
                    }
                    next.Add(chain[^1]);
                    chain = next;
                }
            }
            // Resampled to ArchN pieces by length.
            var arch = new Vector2[ArchN + 1];
            {
                var len = new float[chain.Count];
                for (var i = 1; i < chain.Count; i++) len[i] = len[i - 1] + (chain[i] - chain[i - 1]).Length();
                var total = Math.Max(1e-4f, len[^1]);
                var j = 0;
                // The floor's corner is the arch's first point from its rail (not rounded off, however long the arch):
                // the others share what is left.
                var footLen = footEnd == 0 ? 0 : MathF.Min((foot - (footEnd > 0 ? railR : railL)).Length(), total * 0.5f);
                for (var k = 0; k <= ArchN; k++)
                {
                    var s = total * k / ArchN;
                    if (footEnd > 0 && k >= 1) s += (footLen + (total - footLen) * (k - 1) / (ArchN - 1) - s) * footW;
                    if (footEnd < 0 && k <= ArchN - 1) s += ((total - footLen) * k / (ArchN - 1) - s) * footW;
                    while (j + 2 < chain.Count && len[j + 1] < s) j++;
                    var seg = Math.Max(1e-6f, len[j + 1] - len[j]);
                    arch[k] = Vector2.Lerp(chain[j], chain[j + 1], Math.Clamp((s - len[j]) / seg, 0, 1));
                }
                arch[0] = railR;
                arch[ArchN] = railL;
                if (footEnd > 0) arch[1] = Vector2.Lerp(arch[1], foot, footW);
                if (footEnd < 0) arch[ArchN - 1] = Vector2.Lerp(arch[ArchN - 1], foot, footW);
                st.FootEnd = footEnd;
                st.FootW = footEnd != 0 ? footW : 0;
            }
            var poly = new Vector2[SectionPoints];
            poly[0] = railL;
            for (var k = 0; k <= DeckN; k++) poly[1 + k] = deck[k];
            poly[RailRight] = railR;
            for (var k = 1; k < ArchN; k++) poly[RailRight + k] = arch[k];
            st.Poly = poly;
            st.W = poly.Select(v => st.P + st.Right * v.X + st.Up * v.Y).ToArray();
            var centre = poly.Aggregate((a, b) => a + b) / poly.Length;
            st.Centre = st.P + st.Right * centre.X + st.Up * centre.Y;
            st.MeanNormal = Vector3.Normalize(st.Right * nb.X + st.Up * nb.Y);
        }

        /// <summary>Inside any road's room (not counting the stations of `exceptRoad` from `from` to `to` metres along it).</summary>
        public bool InRoom(Vector3 p, int exceptRoad = -1, float from = 0, float to = 0, bool only = false)
        {
            if (!prismBuckets.TryGetValue(((int)Math.Floor(p.X / Bucket), (int)Math.Floor(p.Z / Bucket)), out var list)) return false;
            foreach (var id in list)
            {
                var iv = prisms[id];
                var inRange = iv.Road == exceptRoad && iv.A.R.Distance >= from && iv.A.R.Distance <= to;
                // Not those stations, or (only) just those.
                if (inRange != only) continue;
                var t0 = Vector3.Dot(p - iv.A.P, iv.A.T);
                var t1 = Vector3.Dot(p - iv.B.P, iv.B.T);
                var f = t0 - t1;
                if (MathF.Abs(f) < 1e-5f) continue;
                var s = t0 / f;
                if (s < -0.01f || s > 1.01f) continue;
                s = Math.Clamp(s, 0, 1);
                var P = Vector3.Lerp(iv.A.P, iv.B.P, s);
                var R = Vector3.Lerp(iv.A.Right, iv.B.Right, s);
                var U = Vector3.Lerp(iv.A.Up, iv.B.Up, s);
                var d = p - P;
                var q = new Vector2(Vector3.Dot(d, R) / R.LengthSquared(), Vector3.Dot(d, U) / U.LengthSquared());
                var inside = false;
                var pa = iv.A.Poly;
                var pb = iv.B.Poly;
                for (int i = 0, j = pa.Length - 1; i < pa.Length; j = i++)
                {
                    var a = Vector2.Lerp(pa[j], pb[j], s);
                    var b = Vector2.Lerp(pa[i], pb[i], s);
                    if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y)) inside = !inside;
                }
                if (inside) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 2. Attachment

        private float Nat(float x, float z)
        {
            var fx = (x - x0) / Cell - 0.5;
            var fz = (z - z0) / Cell - 0.5;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
            if (c < 0 || r < 0 || c + 1 >= cols || r + 1 >= rows) return float.NaN;
            float u = (float)(fx - c), v = (float)(fz - r);
            float At(int cc, int rr) => nat[rr * cols + cc];
            return At(c, r) * (1 - u) * (1 - v) + At(c + 1, r) * u * (1 - v) + At(c, r + 1) * (1 - u) * v + At(c + 1, r + 1) * u * v;
        }

        public float GAt(float x, float z)
        {
            var fx = (x - x0) / Cell - 0.5;
            var fz = (z - z0) / Cell - 0.5;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
            if (c < 0 || r < 0 || c + 1 >= cols || r + 1 >= rows) return float.NaN;
            float u = (float)(fx - c), v = (float)(fz - r);
            float At(int cc, int rr) => g[rr * cols + cc];
            return At(c, r) * (1 - u) * (1 - v) + At(c + 1, r) * u * (1 - v) + At(c, r + 1) * (1 - u) * v + At(c + 1, r + 1) * u * v;
        }

        private void Attachment()
        {
            foreach (var road in roads)
            {
                var st = road.S;
                var n = st.Count;
                // The outward side's sign: the first upright station's right against the plan perpendicular of its tangent.
                var sgn = 1f;
                foreach (var s in st)
                    if (s.Up.Y > 0.5f)
                    {
                        sgn = Vector3.Dot(s.Right, Vector3.Cross(s.T, Vector3.UnitY)) >= 0 ? 1 : -1;
                        break;
                    }
                var score = new float[2, n];
                var reach = new float[2, n];
                var ramp = new float[2, n];
                var inward = new float[2, n];
                var outward = new Vector3[2, n];
                var tangent = new Vector3[n];
                var rimNormal = new Vector3[2, n];
                var lastU = Vector3.UnitX;
                var lastT = Vector3.UnitX;
                for (var i = 0; i < n; i++)
                {
                    var s = st[i];
                    var tp = new Vector3(s.T.X, 0, s.T.Z);
                    var ul = tp.Length();
                    var u = ul > 0.15f ? Vector3.Normalize(Vector3.Cross(tp, Vector3.UnitY)) : lastU;
                    lastU = u;
                    tangent[i] = ul > 0.15f ? tp / ul : lastT;
                    lastT = tangent[i];
                    var slope = MathF.Abs(s.T.Y) / MathF.Max(0.01f, ul);
                    var cl = s.Held(0);
                    var cr = s.Held(1);
                    // The section's open side faces up enough (its mean normal), not upside down.
                    var nb = s.MeanNormal;
                    var nh = new Vector3(nb.X, 0, nb.Z);
                    var e = SS(0.35f, 0.75f, nh.Length());
                    // From the room's lowest point (below the low rail where the surface faces down).
                    if (e > 0) faces.Add(new Face { Low = s.W.MinBy(w => w.Y), F = Vector3.Normalize(nh), E = e });
                    for (var sd = 0; sd < 2; sd++)
                    {
                        var side = sd == 0 ? -1 : 1;
                        var c = sd == 0 ? cl : cr;
                        var opp = s.W[sd == 0 ? RailRight : 0]; // (the other rail itself: its side of the road ends there, not at a floor's corner)
                        var o = u * side * sgn;
                        outward[sd, i] = o;
                        var q = RoadMeshBuilder.SurfaceSample(s.R, side * s.R.Width / 2);
                        var rn = Vector3.Normalize(s.Right * (float)q.NX + s.Up * (float)q.NY);
                        rimNormal[sd, i] = Vector3.Normalize(Vector3.Lerp(rn, Vector3.UnitY, SS(0.5f, -0.1f, rn.Y) * 0.999f));
                        var no = Nat(c.X + o.X * 6, c.Z + o.Z * 6);
                        if (float.IsNaN(no) || float.IsNaN(Nat(c.X, c.Z))) { score[sd, i] = -1; ramp[sd, i] = 8; reach[sd, i] = MinReach; continue; }
                        var fall = no - c.Y;
                        var mFace = (nb.Y + 0.3f) / 0.3f;
                        var mFall = (12 - fall) / 4;
                        var mSlope = (1.0f - slope) / 0.3f;
                        var mSlot = Math.Abs(s.R.Curve) > Math.PI ? (s.Chord - 4.5f) / 1.5f : 1;
                        var mOver = (Vector3.Dot(c - opp, o) + 2.5f) / 2f;
                        score[sd, i] = Math.Clamp(MathF.Min(mFace, MathF.Min(mFall, MathF.Min(mSlope, MathF.Min(mSlot, mOver)))), -1, 1);
                        var rise = MathF.Abs(c.Y - no);
                        ramp[sd, i] = MathF.Max(8, 1.5f * rise);
                        reach[sd, i] = Math.Clamp(1.5f * rise, MinReach, MaxReach);
                        inward[sd, i] = MathF.Max(0, Vector3.Dot(opp - c, -o));
                    }
                }
                // A rail inside another road's room holds no land (another road runs through it).
                for (var i = 0; i < n; i++)
                    for (var sd = 0; sd < 2; sd++)
                        if (score[sd, i] > 0)
                        {
                            var c = st[i].Held(sd);
                            if (InRoom(c + Vector3.UnitY * 0.05f, road.Index, road.Dist[i] - 12, road.Dist[i] + 12)) score[sd, i] = -1;
                        }
                road.A = [new float[n], new float[n]];
                road.Exact = [new bool[n], new bool[n]];
                for (var sd = 0; sd < 2; sd++)
                {
                    // Smoothed over +-8 m, to on/off with hysteresis, short runs removed, then ramps.
                    var sm = new float[n];
                    for (var i = 0; i < n; i++)
                    {
                        float sum = 0;
                        var cnt = 0;
                        for (var j = Math.Max(0, i - 4); j <= Math.Min(n - 1, i + 4); j++) { sum += score[sd, j]; cnt++; }
                        sm[i] = sum / cnt;
                    }
                    var on = new bool[n];
                    var cur = sm[0] > 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (cur && sm[i] < -0.15f) cur = false;
                        else if (!cur && sm[i] > 0.15f) cur = true;
                        on[i] = cur;
                    }
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var want = pass == 1;
                        var i0 = 0;
                        while (i0 < n)
                        {
                            if (on[i0] != want) { i0++; continue; }
                            var j = i0;
                            while (j < n && on[j] == want) j++;
                            double len = road.Dist[j - 1] - road.Dist[i0];
                            var need = want ? 2 * Enumerable.Range(i0, j - i0).Average(k => ramp[sd, k]) : 16;
                            var edge = i0 == 0 || j == n;
                            if (want && len < need) for (var k = i0; k < j; k++) on[k] = false;
                            else if (!want && len < need && !edge) for (var k = i0; k < j; k++) on[k] = true;
                            i0 = j;
                        }
                    }
                    // Ramps: from the nearest off station either way, over its ramp length.
                    var a = road.A[sd];
                    var lastOff = -1;
                    for (var i = 0; i < n; i++)
                    {
                        if (!on[i]) { lastOff = i; a[i] = 0; continue; }
                        a[i] = lastOff < 0 ? 1 : Quintic((road.Dist[i] - road.Dist[lastOff]) / ramp[sd, lastOff]);
                    }
                    lastOff = -1;
                    for (var i = n - 1; i >= 0; i--)
                    {
                        if (!on[i]) { lastOff = i; continue; }
                        if (lastOff >= 0) a[i] = MathF.Min(a[i], Quintic((road.Dist[lastOff] - road.Dist[i]) / ramp[sd, lastOff]));
                    }
                    for (var i = 0; i < n; i++)
                        rails.Add(new Rail
                        {
                            C = st[i].Held(sd), Opp = st[i].W[sd == 0 ? RailRight : 0], Floor = MathF.Max(0, Vector3.Dot(st[i].W[sd == 0 ? 0 : RailRight] - st[i].Held(sd), -outward[sd, i])), O = outward[sd, i], T = tangent[i], N = rimNormal[sd, i], A = a[i], FacesUp = st[i].MeanNormal.Y,
                            Reach = reach[sd, i], Inward = inward[sd, i], Along = road.Dist[i], EndFade = road.Track.Closed ? 1 : SS(0, 4, MathF.Min(road.Dist[i] - road.Dist[0], road.Dist[^1] - road.Dist[i])), Road = road.Index, Station = i, Side = sd,
                        });
                }
            }
        }

        // ---------------------------------------------------------------- 3. Ground

        private void Ground()
        {
            var n = cols * rows;
            bCols = (int)Math.Ceiling(cols * Cell / Bucket) + 1;
            bRows = (int)Math.Ceiling(rows * Cell / Bucket) + 1;
            railBuckets = new List<int>[bCols * bRows];
            for (var i = 0; i < rails.Count; i++)
            {
                var s = rails[i];
                if (s.A <= 0) continue;
                int bx0 = (int)Math.Floor((s.C.X - s.Reach - x0) / Bucket), bx1 = (int)Math.Floor((s.C.X + s.Reach - x0) / Bucket);
                int bz0 = (int)Math.Floor((s.C.Z - s.Reach - z0) / Bucket), bz1 = (int)Math.Floor((s.C.Z + s.Reach - z0) / Bucket);
                for (var bz = Math.Max(0, bz0); bz <= Math.Min(bRows - 1, bz1); bz++)
                    for (var bx = Math.Max(0, bx0); bx <= Math.Min(bCols - 1, bx1); bx++)
                        (railBuckets[bz * bCols + bx] ??= []).Add(i);
            }
            g = new float[n];
            Parallel.For(0, rows, () => new List<(int I, float D2, float Side, float K, float Kn, float Y)>(), (r, _, hits) =>
            {
                for (var c = 0; c < cols; c++)
                {
                    var k = r * cols + c;
                    g[k] = Column(k, (float)(x0 + (c + 0.5) * Cell), (float)(z0 + (r + 0.5) * Cell), hits);
                }
                return hits;
            }, _ => { });

            // Distances to the rails (held, and any station's), in metres: for the blur and the fidelity.
            var seedHeld = new bool[n];
            var seedAny = new bool[n];
            foreach (var s in rails)
            {
                var k = CellOf(s.C.X, s.C.Z);
                if (k < 0) continue;
                seedAny[k] = true;
                if (s.A > 0.5f) seedHeld[k] = true;
            }
            var nearHeld = Chamfer(seedHeld);
            var nearAny = roadDistance = Chamfer(seedAny);

            // Beside road that faces sideways, the land on the side it faces stays below the space it faces.
            var bound = new float[n];
            Array.Fill(bound, float.PositiveInfinity);
            var mask = new bool[n];
            foreach (var f in faces)
            {
                var lat = new Vector3(-f.F.Z, 0, f.F.X);
                for (var h = 0f; h <= 30; h += 0.5f)
                {
                    var v = f.Low.Y + 0.7f * MathF.Max(0, h - 6) + (1 - f.E) * 80 + 80 * SS(20, 30, h);
                    for (var l = -1.5f; l <= 1.5f; l += 0.5f)
                    {
                        var k = CellOf(f.Low.X + f.F.X * h + lat.X * l, f.Low.Z + f.F.Z * h + lat.Z * l);
                        if (k < 0) continue;
                        mask[k] = true;
                        if (v < bound[k]) bound[k] = v;
                    }
                }
            }
            // Slope-limited inside the faced area (no step where one station's strip ends and the next begins).
            {
                float ax = 0.7f * (float)Cell, dg = ax * MathF.Sqrt(2);
                void Relax(int i, int j, float cost) { if (mask[j] && bound[j] + cost < bound[i]) bound[i] = bound[j] + cost; }
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                    {
                        var i = r * cols + c;
                        if (!mask[i]) continue;
                        if (c > 0) Relax(i, i - 1, ax);
                        if (r > 0) { Relax(i, i - cols, ax); if (c > 0) Relax(i, i - cols - 1, dg); if (c + 1 < cols) Relax(i, i - cols + 1, dg); }
                    }
                for (var r = rows - 1; r >= 0; r--)
                    for (var c = cols - 1; c >= 0; c--)
                    {
                        var i = r * cols + c;
                        if (!mask[i]) continue;
                        if (c + 1 < cols) Relax(i, i + 1, ax);
                        if (r + 1 < rows) { Relax(i, i + cols, ax); if (c + 1 < cols) Relax(i, i + cols + 1, dg); if (c > 0) Relax(i, i + cols - 1, dg); }
                    }
            }
            // Not at held rails: there the land is the rail's (fading in over 1 to 4 m from it).
            for (var i = 0; i < n; i++) if (mask[i] && bound[i] < g[i]) g[i] -= (g[i] - bound[i]) * SS(1, 4, nearHeld[i]);

            // Smoothed away from the held rails (they stay exact).
            for (var pass = 0; pass < 3; pass++)
            {
                var b = (float[])g.Clone();
                Parallel.For(3, rows - 3, r =>
                {
                    for (var c = 3; c < cols - 3; c++)
                    {
                        var k = r * cols + c;
                        if (float.IsNaN(b[k])) continue;
                        var w0 = SS(1.5f, 5f, nearHeld[k]);
                        if (w0 <= 0) continue;
                        float s = 0;
                        var m = 0;
                        for (var dr = -3; dr <= 3; dr++)
                            for (var dc = -3; dc <= 3; dc++)
                            {
                                var v = b[(r + dr) * cols + c + dc];
                                if (float.IsNaN(v)) continue;
                                s += v;
                                m++;
                            }
                        if (m > 0) g[k] = b[k] + (s / m - b[k]) * w0;
                    }
                });
            }

            // Over the driving surface of held road the ground stays above it (inside the room, cut out): the line between
            // the rails alone does not follow a twisting or bent surface, and land under it would come through it.
            foreach (var road in roads)
                for (var i = 0; i + 1 < road.S.Count; i++)
                {
                    var hold = MathF.Min(MathF.Min(road.A[0][i], road.A[1][i]), MathF.Min(road.A[0][i + 1], road.A[1][i + 1]));
                    if (hold <= 0) continue;
                    var a = road.S[i].W;
                    var b = road.S[i + 1].W;
                    var na = road.S[i].DeckNormal;
                    var nb = road.S[i + 1].DeckNormal;
                    // A section curled over itself (an overhang): the land also goes over the curl's back, round it as
                    // round a tunnel (the line between the rails runs through the curl).
                    var curled = CurlLift && (Math.Abs(road.S[i].R.Curve) > Math.PI || Math.Abs(road.S[i + 1].R.Curve) > Math.PI);
                    for (var u = 1; u < DeckN + 1; u++)
                    {
                        // The driving side: the surface's own normal at the quad's corners.
                        var side = na[u - 1] + na[u] + nb[u - 1] + nb[u];
                        Lift(a[u], a[u + 1], b[u + 1], hold, side, road.Index, i, false);
                        Lift(a[u], b[u + 1], b[u], hold, side, road.Index, i, false);
                        if (!curled) continue;
                        // The back's own surface, BackClear behind the driving surface (clear of the road's slab, on
                        // the flanks sideways too).
                        Vector3 a0 = a[u] - na[u - 1] * BackClear, a1 = a[u + 1] - na[u] * BackClear;
                        Vector3 b0 = b[u] - nb[u - 1] * BackClear, b1 = b[u + 1] - nb[u] * BackClear;
                        Lift(a0, a1, b1, hold, side, road.Index, i, true);
                        Lift(a0, b1, b0, hold, side, road.Index, i, true);
                    }
                }

            // On the stadium's floor land lower than its ground sinks under it (as the editor's grid), away from held rails.
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    var k = r * cols + c;
                    double x = x0 + (c + 0.5) * Cell, z = z0 + (r + 0.5) * Cell;
                    if (Math.Abs(x) < StadiumHalf && Math.Abs(z) < StadiumHalf && g[k] < 0.5f && nearHeld[k] > 3) g[k] = (float)Sunk;
                }

            fid = new float[n];
            cut = new bool[n];
            Parallel.For(0, rows, r =>
            {
                for (var c = 0; c < cols; c++)
                {
                    var k = r * cols + c;
                    var near = nearAny[k];
                    var far = Math.Clamp((near - FarStart) / FarFade, 0, 1);
                    var least = MinFidelity + (FarFidelity - MinFidelity) * far * far * (3 - 2 * far);
                    fid[k] = (float)Math.Max(least, Math.Exp(-near / FidelityFalloff));
                    if (near < 60 && !float.IsNaN(g[k]))
                        cut[k] = InRoom(new Vector3((float)(x0 + (c + 0.5) * Cell), g[k] - 0.02f, (float)(z0 + (r + 0.5) * Cell)));
                }
            });
        }

        /// <summary>Raises the ground at the cells whose middle lies under the triangle (plan) to DeckClear over it, by `hold`.</summary>
        private void Lift(Vector3 a, Vector3 b, Vector3 c, float hold, Vector3 side, int road, int station, bool back)
        {
            // Not where it would bury the same stretch of road under it (a deck curled under itself, as a hump's lip).
            const int Stretch = (int)(12 / StationStep);
            const float DeckClear = 0.5f;
            // Surface whose driving side faces up; on a curled section also its top's back (driving side down).
            var nrm = Vector3.Cross(b - a, c - a);
            if (nrm.LengthSquared() < 1e-10f) return;
            if (Vector3.Dot(nrm, side) < 0) nrm = -nrm;
            var ny = nrm.Y / nrm.Length();
            if (back ? ny > -0.05f : ny < 0.2f) return;
            var det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(det) < 1e-6f) return;
            if (!back)
                foreach (var q in new[] { a, b, c, (a + b + c) / 3 })
                    foreach (var (y, rd, s) in DecksAt(q.X, q.Z))
                        if (rd == road && Math.Abs(s - station) <= Stretch && y < q.Y - 1) return;
            int c0 = Math.Max(0, (int)Math.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - x0) / Cell - 0.5));
            int c1 = Math.Min(cols - 1, (int)Math.Ceiling((MathF.Max(a.X, MathF.Max(b.X, c.X)) - x0) / Cell - 0.5));
            int r0 = Math.Max(0, (int)Math.Floor((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - z0) / Cell - 0.5));
            int r1 = Math.Min(rows - 1, (int)Math.Ceiling((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - z0) / Cell - 0.5));
            for (var r = r0; r <= r1; r++)
                for (var col = c0; col <= c1; col++)
                {
                    float px = (float)(x0 + (col + 0.5) * Cell), pz = (float)(z0 + (r + 0.5) * Cell);
                    var l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / det;
                    var l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / det;
                    var l3 = 1 - l1 - l2;
                    if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                    var k = r * cols + col;
                    if (float.IsNaN(g[k])) continue;
                    var want = l1 * a.Y + l2 * b.Y + l3 * c.Y + (back ? 0 : DeckClear);
                    if (want <= g[k]) continue;
                    var buried = false;
                    if (!back)
                        foreach (var (y, rd, s) in DecksAt(px, pz))
                        if (rd == road && Math.Abs(s - station) <= Stretch && y < want - DeckClear - 1) { buried = true; break; }
                    if (!buried) g[k] += (want - g[k]) * hold;
                }
        }

        private int CellOf(float x, float z)
        {
            int c = (int)Math.Floor((x - x0) / Cell), r = (int)Math.Floor((z - z0) / Cell);
            return c < 0 || r < 0 || c >= cols || r >= rows ? -1 : r * cols + c;
        }

        /// <summary>Approximate Euclidean distance (metres) to the nearest seed cell (two chamfer passes).</summary>
        private float[] Chamfer(bool[] seed)
        {
            var d = new float[seed.Length];
            for (var i = 0; i < d.Length; i++) d[i] = seed[i] ? 0 : 1e6f;
            float ax = (float)Cell, dg = ax * MathF.Sqrt(2);
            void Relax(int i, int j, float cost) { if (d[j] + cost < d[i]) d[i] = d[j] + cost; }
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    var i = r * cols + c;
                    if (c > 0) Relax(i, i - 1, ax);
                    if (r > 0) { Relax(i, i - cols, ax); if (c > 0) Relax(i, i - cols - 1, dg); if (c + 1 < cols) Relax(i, i - cols + 1, dg); }
                }
            for (var r = rows - 1; r >= 0; r--)
                for (var c = cols - 1; c >= 0; c--)
                {
                    var i = r * cols + c;
                    if (c + 1 < cols) Relax(i, i + 1, ax);
                    if (r + 1 < rows) { Relax(i, i + cols, ax); if (c + 1 < cols) Relax(i, i + cols + 1, dg); if (c > 0) Relax(i, i + cols - 1, dg); }
                }
            return d;
        }

        /// <summary>The ground in one column: the land blended into the held rails round it.</summary>
        private float Column(int k, float px, float pz, List<(int I, float D2, float Side, float K, float Kn, float Y)> hits)
        {
            var land = nat[k];
            if (float.IsNaN(land)) return float.NaN;
            int bx = (int)Math.Floor((px - x0) / Bucket), bz = (int)Math.Floor((pz - z0) / Bucket);
            if (bx < 0 || bz < 0 || bx >= bCols || bz >= bRows) return land;
            var list = railBuckets[bz * bCols + bx];
            if (list is null) return land;
            hits.Clear();
            var top0 = float.MinValue;
            foreach (var i in list)
            {
                var s = rails[i];
                float dx = px - s.C.X, dz = pz - s.C.Z, d2 = dx * dx + dz * dz;
                if (d2 >= s.Reach * s.Reach) continue;
                var h = dx * s.O.X + dz * s.O.Z;
                var side = SS(-s.Inward - 0.25f, -s.Inward + 0.25f, h);
                if (side <= 0) continue;
                // Across its own road only near its own station (not beyond the road's end).
                if (h < 0) side *= 1 - SS(1.5f, 3f, MathF.Abs(dx * s.T.X + dz * s.T.Z));
                if (side <= 0) continue;
                var d = MathF.Sqrt(d2);
                var K = d < 1 || h < 0 ? 1 : 1 - Quintic((d - 1) / (s.Reach - 1));
                if (K <= 0) continue;
                var kn = (1 - SS(2, 14, d)) * s.A * side;
                // The height it asks for: outwards the surface's slope continued (at most 1, the offset saturating);
                // across its road the line to the other rail, lifted between them (inside the room).
                float y;
                if (h > 0)
                {
                    var ny = MathF.Max(s.N.Y, 0.2f);
                    float gx = -s.N.X / ny, gz = -s.N.Z / ny;
                    var gl = MathF.Sqrt(gx * gx + gz * gz);
                    if (gl > 1) { gx /= gl; gz /= gl; }
                    var F = d < 12 ? 1 - d / 24 : 12 / (2 * d);
                    y = s.C.Y + (gx * dx + gz * dz) * F;
                }
                else
                {
                    // Over the floor between a floor's corner and its rail: just over the floor (inside the room, cut; the
                    // floor itself is lining), level, so the ground has no step at the corner's line. The road's own
                    // rules start at the rail.
                    var inward = s.Inward - s.Floor;
                    var past = -h - s.Floor;
                    if (past <= 0)
                    {
                        hits.Add((i, d2, side, K, 0, s.C.Y + 0.5f * SS(0, 0.5f, -h)));
                        continue;
                    }
                    var t = inward > 0.01f ? Math.Clamp(past / inward, 0, 1) : 0;
                    var chord = s.C.Y + (s.Opp.Y - s.C.Y) * t;
                    // A steep section (a wall) is narrow from above: its line would cut through the driving surface.
                    // There the ground in its footprint stands at its top (inside the room), rising from the rail within 1.5 m.
                    // Not where the driving side faces up (a wide steep bank): the top would stand far over its lower part,
                    // over the open sky; there the ground stays on the line, under the deck.
                    var high = MathF.Max(s.C.Y, s.Opp.Y);
                    var steep = SS(1.5f, 4f, MathF.Abs(s.Opp.Y - s.C.Y) / MathF.Max(0.5f, inward)) * SS(0.2f, 1.5f, past) * (1 - SS(0.1f, 0.3f, s.FacesUp));
                    y = chord + ((high - chord) * steep + InteriorLift * MathF.Min(SS(0.3f, 2.5f, past), SS(0.3f, 2.5f, inward - past))) * s.EndFade;
                }
                // Across its own road the height only has to stay inside the room: it takes no part in the level preference.
                if (h < 0) kn = 0;
                hits.Add((i, d2, side, K, kn, y));
                if (kn > 1e-4f && y > top0) top0 = y;
            }
            if (hits.Count == 0) return land;
            // Layers: the samples of one road close along it (within LayerGap) are one sheet of road, however it twists;
            // another road, or the same road a lap later, is another. The level preference decides between layers only:
            // a soft maximum of their heights; a layer well below it counts little (the upper road wins).
            hits.Sort((a, b) =>
            {
                Rail ra = rails[a.I], rb = rails[b.I];
                return ra.Road != rb.Road ? ra.Road.CompareTo(rb.Road) : ra.Along.CompareTo(rb.Along);
            });
            Span<(int From, int To, double W, double H, double Wk, double Hk, double A)> layers = stackalloc (int, int, double, double, double, double, double)[Math.Min(hits.Count, 64)];
            var count = 0;
            for (var j = 0; j < hits.Count; j++)
            {
                var h = hits[j];
                var s = rails[h.I];
                if (count == 0 || (j > 0 && (rails[hits[j - 1].I].Road != s.Road || s.Along - rails[hits[j - 1].I].Along > LayerGap)))
                {
                    if (count == layers.Length) break;
                    layers[count++] = (j, j, 0, 0, 0, 0, 0);
                }
                ref var l = ref layers[count - 1];
                var w = s.A * h.Side * h.K / (h.D2 + 0.02f);
                l.To = j;
                l.W += w;
                l.H += w * h.Y;
                l.A = Math.Max(l.A, s.A * h.Side * h.K);
                if (h.Kn > 0) { var wk = h.Kn / (h.D2 + 1); l.Wk += wk; l.Hk += wk * h.Y; }
            }
            double top = double.MinValue, sum = 0;
            for (var j = 0; j < count; j++) if (layers[j].Wk > 0) top = Math.Max(top, layers[j].Hk / layers[j].Wk);
            double yt = double.MinValue;
            if (top > double.MinValue)
            {
                double sy = 0;
                for (var j = 0; j < count; j++)
                    if (layers[j].Wk > 0) { sum += layers[j].Wk; sy += layers[j].Wk * Math.Exp((layers[j].Hk / layers[j].Wk - top) / Tau); }
                yt = top + Tau * Math.Log(Math.Max(1e-30, sy / sum));
            }
            double W = 0, H = 0, A = 0;
            for (var j = 0; j < count; j++)
            {
                var l = layers[j];
                if (l.W <= 0) continue;
                var level = l.Wk > 0 ? l.Hk / l.Wk : l.H / l.W;
                var pen = yt > double.MinValue ? Math.Max(0, (yt - level) / Tau) : 0;
                var f = Math.Exp(-pen * pen);
                W += f * l.W;
                H += f * l.H;
                A = Math.Max(A, f * l.A);
                if (Explaining is { } ex)
                {
                    ex.AppendLine($"  layer {j}: road {rails[hits[l.From].I].Road} along {rails[hits[l.From].I].Along:F0}-{rails[hits[l.To].I].Along:F0}, level {level:F2}, weight {l.W:F2} -> {f * l.W:F2}, A {l.A:F2}, mean {l.H / l.W:F2}");
                    foreach (var h in hits.Skip(l.From).Take(l.To - l.From + 1).OrderByDescending(h => rails[h.I].A * h.Side * h.K / (h.D2 + 0.02f)).Take(6))
                    {
                        var s = rails[h.I];
                        ex.AppendLine($"    rail side {s.Side} at {s.Along:F1} ({s.C.X:F1}, {s.C.Y:F2}, {s.C.Z:F1}) d {MathF.Sqrt(h.D2):F2} h {(px - s.C.X) * s.O.X + (pz - s.C.Z) * s.O.Z:F2} A {s.A:F2} side {h.Side:F2} K {h.K:F2} -> y {h.Y:F2} w {s.A * h.Side * h.K / (h.D2 + 0.02f):F2}");
                    }
                }
            }
            Explaining?.AppendLine($"  land {land:F2}, A {A:F2}, blend {(W > 0 ? H / W : double.NaN):F2}");
            return W > 0 ? (float)((1 - A) * land + A * (H / W)) : land;
        }

        [ThreadStatic] private static System.Text.StringBuilder? Explaining;

        /// <summary>For the checker: how the ground at (x, z) came about.</summary>
        public string Explain(float x, float z)
        {
            Explaining = new System.Text.StringBuilder();
            var k = CellOf(x, z);
            if (k < 0) return "outside";
            int c = k % cols, r = k / cols;
            var y = Column(k, (float)(x0 + (c + 0.5) * Cell), (float)(z0 + (r + 0.5) * Cell), []);
            Explaining.AppendLine($"  column {y:F2}, final ground {g[k]:F2}, cut {cut[k]}");
            var s = Explaining.ToString();
            Explaining = null;
            return s;
        }

        /// <summary>Samples of one road further apart along it than this belong to different layers, metres.</summary>
        private const float LayerGap = 40;

        // ---------------------------------------------------------------- 4. Contours: where the ground meets the rooms

        private sealed class CVertex
        {
            public Vector3 P;
            public float Along, U;
            public bool Rail;
        }

        private float F(Vector3 w)
        {
            var y = GAt(w.X, w.Z);
            return float.IsNaN(y) ? -1000 : y - w.Y;
        }

        private List<TerrainPin> Contours(out List<(Road Road, List<CVertex> Chain)> arches)
        {
            var archList = new List<(Road Road, List<CVertex> Chain)>();
            arches = archList;
            var pins = new List<TerrainPin>();
            foreach (var road in roads)
            {
                var st = road.S;
                var n = st.Count;
                const int U = SectionPoints;
                // Held rails whose ground is at them are exact: the land's line is the rail.
                for (var i = 0; i < n; i++)
                    for (var sd = 0; sd < 2; sd++)
                    {
                        var c = st[i].Held(sd);
                        road.Exact[sd][i] = road.A[sd][i] >= 0.98f && MathF.Abs(GAt(c.X, c.Z) - c.Y) < Won;
                    }
                var f = new float[n, U];
                for (var i = 0; i < n; i++)
                {
                    for (var u = 0; u < U; u++)
                        f[i, u] = (u == 0 && road.Exact[0][i]) || (u == RailRight && road.Exact[1][i]) ? 0 : F(st[i].W[u]);
                    // The floor's corner as the land's line: the rail under the ground (the floor is lining), the corner
                    // on it; between the two the line is where the held point is.
                    for (var sd = 0; sd < 2; sd++)
                    {
                        var w = st[i].SeamW(sd);
                        if (w <= 0 || !road.Exact[sd][i]) continue;
                        int rail = sd == 0 ? 0 : RailRight, next = sd == 0 ? U - 1 : RailRight + 1;
                        f[i, rail] = w;
                        if (w >= 1) f[i, next] = 0; else if (f[i, next] < 0) f[i, next] = -(1 - w);
                    }
                }
                road.F = f;

                // Marching squares on the chart (along x round), round cyclic.
                var vertices = new List<CVertex>();
                var byEdge = new Dictionary<long, int>();
                long Key(int type, int i, int u) => ((long)type << 40) | ((long)i << 16) | (uint)u;
                int EdgePoint(int type, int i, int u)
                {
                    var key = Key(type, i, u);
                    if (byEdge.TryGetValue(key, out var id)) return id;
                    int ia = i, ua = u, ib = type == 0 ? i + 1 : i, ub = type == 0 ? u : (u + 1) % U;
                    Vector3 a = st[ia].W[ua], b = st[ib].W[ub];
                    float fa = f[ia, ua], fb = f[ib, ub];
                    var t = fa / (fa - fb);
                    var rail = false;
                    // The designed line between a rail and the floor's corner: where the held point is, not searched for.
                    if (type == 1 && ((ua == RailRight && road.Exact[1][ia] && st[ia].SeamW(1) > 0) || (ua == U - 1 && road.Exact[0][ia] && st[ia].SeamW(0) > 0))) rail = true;
                    else if (fa == 0 && ((ua == 0 && road.Exact[0][ia]) || (ua == RailRight && road.Exact[1][ia]))) { t = 0; rail = true; }
                    else if (fb == 0 && ((ub == 0 && road.Exact[0][ib]) || (ub == RailRight && road.Exact[1][ib]))) { t = 1; rail = true; }
                    else
                    {
                        // Secant steps on the true ground, every other one a bisection: where the ground steps (the foot of a
                        // wall's fill, 20 m over the land in front within a cell) the secant's far end stays put and it
                        // crawls; three such steps left the line metres up the arch, over the land it belongs to.
                        float lo = 0, hi = 1, flo = fa, fhi = fb;
                        for (var it = 0; it < 16 && hi - lo > 1e-3f; it++)
                        {
                            var ft = F(Vector3.Lerp(a, b, t));
                            if (MathF.Abs(ft) < 0.005f) break;
                            if ((ft > 0) == (flo > 0)) { lo = t; flo = ft; } else { hi = t; fhi = ft; }
                            t = it % 2 == 1 ? (lo + hi) / 2 : Math.Clamp(lo + (hi - lo) * flo / (flo - fhi), lo, hi);
                        }
                    }
                    var p = Vector3.Lerp(a, b, t);
                    float along = road.Dist[ia] + (road.Dist[ib] - road.Dist[ia]) * t;
                    float uu = type == 0 ? u : u + t;
                    vertices.Add(new CVertex { P = p, Along = along, U = uu, Rail = rail });
                    byEdge[key] = vertices.Count - 1;
                    return vertices.Count - 1;
                }
                var links = new Dictionary<int, List<int>>();
                void Link(int a, int b)
                {
                    if (a == b) return;
                    if (!links.TryGetValue(a, out var la)) links[a] = la = [];
                    if (!links.TryGetValue(b, out var lb)) links[b] = lb = [];
                    la.Add(b);
                    lb.Add(a);
                }
                for (var i = 0; i + 1 < n; i++)
                    for (var u = 0; u < U; u++)
                    {
                        var u1 = (u + 1) % U;
                        bool p00 = f[i, u] > 0, p10 = f[i + 1, u] > 0, p11 = f[i + 1, u1] > 0, p01 = f[i, u1] > 0;
                        var mask = (p00 ? 1 : 0) | (p10 ? 2 : 0) | (p11 ? 4 : 0) | (p01 ? 8 : 0);
                        if (mask == 0 || mask == 15) continue;
                        // Edges: 0 bottom (i,u)-(i+1,u), 1 right (i+1,u)-(i+1,u1), 2 top (i,u1)-(i+1,u1), 3 left (i,u)-(i,u1).
                        int E(int e) => e switch { 0 => EdgePoint(0, i, u), 1 => EdgePoint(1, i + 1, u), 2 => EdgePoint(0, i, u1), _ => EdgePoint(1, i, u) };
                        var centre = (f[i, u] + f[i + 1, u] + f[i + 1, u1] + f[i, u1]) > 0;
                        switch (mask)
                        {
                            case 1: case 14: Link(E(3), E(0)); break;
                            case 2: case 13: Link(E(0), E(1)); break;
                            case 4: case 11: Link(E(1), E(2)); break;
                            case 8: case 7: Link(E(2), E(3)); break;
                            case 3: case 12: Link(E(3), E(1)); break;
                            case 6: case 9: Link(E(0), E(2)); break;
                            case 5:
                                if (centre) { Link(E(3), E(2)); Link(E(0), E(1)); } else { Link(E(3), E(0)); Link(E(1), E(2)); }
                                break;
                            case 10:
                                if (centre) { Link(E(3), E(0)); Link(E(1), E(2)); } else { Link(E(0), E(1)); Link(E(2), E(3)); }
                                break;
                        }
                    }
                // Usable vertices: land there, not inside another road's room (nor this road's further along).
                var bad = new bool[vertices.Count];
                for (var v = 0; v < vertices.Count; v++)
                {
                    var p = vertices[v].P;
                    bad[v] = float.IsNaN(GAt(p.X, p.Z)) || InRoom(p + Vector3.UnitY * 0.01f, road.Index, vertices[v].Along - 12, vertices[v].Along + 12)
                        || InRoom(p - Vector3.UnitY * 0.01f, road.Index, vertices[v].Along - 12, vertices[v].Along + 12);
                }
                // Chains.
                var used = new HashSet<(int, int)>();
                var chains = new List<List<int>>();
                foreach (var start in links.Keys.OrderBy(k => links[k].Count == 1 ? 0 : 1).ThenBy(k => k))
                    foreach (var first in links[start])
                    {
                        if (used.Contains((start, first))) continue;
                        var chain = new List<int> { start };
                        int prev = start, cur = first;
                        used.Add((prev, cur)); used.Add((cur, prev));
                        while (true)
                        {
                            chain.Add(cur);
                            if (cur == start) break;
                            var next = links[cur].FirstOrDefault(x => !used.Contains((cur, x)), -1);
                            if (next < 0) break;
                            used.Add((cur, next)); used.Add((next, cur));
                            prev = cur;
                            cur = next;
                        }
                        chains.Add(chain);
                    }
                foreach (var chain in chains)
                {
                    // Split at unusable vertices.
                    var piece = new List<int>();
                    void Flush()
                    {
                        if (piece.Count >= 2) Emit(road, piece.Select(v => vertices[v]).ToList(), pins, archList);
                        piece = [];
                    }
                    foreach (var v in chain)
                    {
                        if (bad[v]) { Flush(); continue; }
                        piece.Add(v);
                    }
                    Flush();
                }
                // An open track's end where land holds both rails: the land meets the end's cross-section (rail, the
                // driving surface's edge, rail), flush with the surface for a car leaving it.
                if (!road.Track.Closed)
                    foreach (var i in new[] { 0, n - 1 })
                    {
                        if (!road.Exact[0][i] || !road.Exact[1][i]) continue;
                        var line = new List<Vec3d>();
                        for (var u = 0; u <= RailRight; u++)
                        {
                            var p = st[i].W[u];
                            if (u > 0 && u < RailRight && u % 2 == 0 && u != DeckN + 1) continue;
                            line.Add(new Vec3d(p.X, p.Y, p.Z));
                        }
                        pins.Add(new TerrainPin(line));
                    }
            }
            return pins;
        }

        /// <summary>Lining pieces smaller than this (square metres) are left out.</summary>
        private const float MinLiningArea = 6;

        /// <summary>Debug: TMSPLINE_LINING_RAY=off keeps lining the ground field covers even with open air behind it.</summary>
        private static readonly bool RayTest = Environment.GetEnvironmentVariable("TMSPLINE_LINING_RAY") != "off";

        /// <summary>Debug: TMSPLINE_CURL=off leaves the land under a curled section's back (no lift over it).</summary>
        /// <summary>How far behind a curled section's driving surface the land over its back lies (the slab is 0.5 m).</summary>
        private const float BackClear = 1.8f;
        private static readonly bool CurlLift = Environment.GetEnvironmentVariable("TMSPLINE_CURL") != "off";

        /// <summary>Longest piece of a fixed edge, metres.</summary>
        private const float PinPiece = 3;

        private static bool OnArch(float u) => u >= RailRight - 1e-4f || u <= 1e-4f;

        /// <summary>A chain of the line where the ground meets a room: simplified, as a pin; its arch pieces for the lining.</summary>
        private static void Emit(Road road, List<CVertex> chain, List<TerrainPin> pins, List<(Road, List<CVertex>)> arches)
        {
            var closed = chain.Count > 2 && ReferenceEquals(chain[0], chain[^1]);
            var keep = new bool[chain.Count];
            keep[0] = keep[^1] = true;
            // Where the chain passes between the arch and the deck (at a rail), and where it starts or stops following a rail.
            for (var i = 1; i + 1 < chain.Count; i++)
                if (chain[i].Rail != chain[i - 1].Rail || chain[i].Rail != chain[i + 1].Rail
                    || OnArch(chain[i - 1].U) != OnArch(chain[i + 1].U))
                    keep[i] = true;
            if (closed && chain.Count > 3)
            {
                var far = 1;
                for (var i = 1; i + 1 < chain.Count; i++) if ((chain[i].P - chain[0].P).LengthSquared() > (chain[far].P - chain[0].P).LengthSquared()) far = i;
                keep[far] = true;
            }
            void Rec(int a, int b)
            {
                if (b <= a + 1) return;
                var md = 0f;
                var mi = -1;
                var ab = chain[b].P - chain[a].P;
                var l2 = MathF.Max(1e-8f, ab.LengthSquared());
                var eps = 0.04f;
                for (var i = a + 1; i < b; i++)
                {
                    if (keep[i]) { mi = i; md = float.MaxValue; break; }
                    var t = Math.Clamp(Vector3.Dot(chain[i].P - chain[a].P, ab) / l2, 0, 1);
                    var d = (chain[i].P - (chain[a].P + ab * t)).Length();
                    var e = chain[i].Rail ? 0.015f : eps;
                    if (d / e > md) { md = d / e; mi = i; }
                }
                if (md > 1 || MathF.Sqrt(l2) > 24)
                {
                    var k = mi < 0 ? (a + b) / 2 : mi;
                    keep[k] = true;
                    Rec(a, k);
                    Rec(k, b);
                }
            }
            Rec(0, chain.Count - 1);
            // Split into pieces of at most PinPiece (long fixed edges make the triangulation slow); the land and the lining
            // share these points, so their seam has no T-junctions.
            var simple = chain.Where((_, i) => keep[i]).ToList();
            var kept = new List<CVertex> { simple[0] };
            for (var i = 1; i < simple.Count; i++)
            {
                CVertex a = simple[i - 1], b = simple[i];
                var pieces = (int)Math.Ceiling((b.P - a.P).Length() / PinPiece);
                // Round the section: the left rail (0) is also the arch's end (SectionPoints).
                float ua = a.U, ub = b.U;
                if (ua <= 1e-4f && ub > RailRight) ua = SectionPoints;
                if (ub <= 1e-4f && ua > RailRight) ub = SectionPoints;
                for (var k = 1; k < pieces; k++)
                {
                    var t = (float)k / pieces;
                    kept.Add(new CVertex { P = Vector3.Lerp(a.P, b.P, t), Along = a.Along + (b.Along - a.Along) * t, U = ua + (ub - ua) * t, Rail = a.Rail && b.Rail });
                }
                kept.Add(b);
            }
            pins.Add(new TerrainPin(kept.Select(v => new Vec3d(v.P.X, v.P.Y, v.P.Z)).ToList()));
            // Arch pieces: consecutive vertices both on the arch, and not a step along a rail line on the deck side.
            var arch = new List<CVertex>();
            for (var i = 0; i < kept.Count; i++)
            {
                var on = OnArch(kept[i].U);
                var segOn = i > 0 && on && OnArch(kept[i - 1].U) && SegmentOnArch(kept[i - 1], kept[i]);
                if (segOn) { if (arch.Count == 0) arch.Add(kept[i - 1]); arch.Add(kept[i]); }
                else { if (arch.Count >= 2) arches.Add((road, arch)); arch = []; }
            }
            if (arch.Count >= 2) arches.Add((road, arch));
        }

        private static bool SegmentOnArch(CVertex a, CVertex b)
        {
            // Both on rail lines: on the arch only if they run along the same rail on its arch side (never: the rails' own
            // line belongs to the deck's edge). A piece from a rail into the arch is on the arch.
            float ua = a.U, ub = b.U;
            var railA = ua is 0 or RailRight;
            var railB = ub is 0 or RailRight;
            if (railA && railB) return false;
            return true;
        }

        // ---------------------------------------------------------------- 5. Lining

        /// <summary>The finished land mesh's triangles in 8 m plan buckets (the lining stays under it).</summary>
        private Dictionary<(int, int), List<int>> landBuckets = [];
        private AdaptiveMesh? landMesh;

        /// <summary>The land mesh's height at (x, z) (the highest triangle there), NaN where it has none.</summary>
        private float LandAt(float x, float z)
        {
            if (landMesh is null || !landBuckets.TryGetValue(((int)Math.Floor(x / Bucket), (int)Math.Floor(z / Bucket)), out var list)) return float.NaN;
            var best = float.NaN;
            var p = landMesh.Positions;
            var ix = landMesh.Indices;
            foreach (var t in list)
            {
                Vector3 a = p[ix[t]], b = p[ix[t + 1]], c = p[ix[t + 2]];
                var det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(det) < 1e-9f) continue;
                var l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
                var l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
                var l3 = 1 - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                var y = l1 * a.Y + l2 * b.Y + l3 * c.Y;
                if (float.IsNaN(best) || y > best) best = y;
            }
            return best;
        }

        /// <summary>Whether a ray from o along d (unit) meets the land mesh within max metres.</summary>
        private bool LandHit(Vector3 o, Vector3 d, float max)
        {
            if (landMesh is null) return false;
            var p = landMesh.Positions;
            var ix = landMesh.Indices;
            var seen = new HashSet<(int, int)>();
            var flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            var steps = flat < 1e-4f ? 0 : (int)Math.Ceiling(max * flat / 2);
            for (var s = 0; s <= steps; s++)
            {
                var q = o + d * (steps == 0 ? 0 : max * s / steps);
                for (var ox = -1; ox <= 1; ox++)
                    for (var oz = -1; oz <= 1; oz++)
                    {
                        var key = ((int)Math.Floor(q.X / Bucket) + ox, (int)Math.Floor(q.Z / Bucket) + oz);
                        if (!seen.Add(key) || !landBuckets.TryGetValue(key, out var list)) continue;
                        foreach (var t in list)
                        {
                            Vector3 a = p[ix[t]], b = p[ix[t + 1]], c = p[ix[t + 2]];
                            Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
                            var det = Vector3.Dot(e1, h);
                            if (MathF.Abs(det) < 1e-9f) continue;
                            var f = 1 / det;
                            var sv = o - a;
                            var u = f * Vector3.Dot(sv, h);
                            if (u < -1e-4f || u > 1 + 1e-4f) continue;
                            var qv = Vector3.Cross(sv, e1);
                            var v = f * Vector3.Dot(d, qv);
                            if (v < -1e-4f || u + v > 1 + 1e-4f) continue;
                            var dist = f * Vector3.Dot(e2, qv);
                            if (dist > -0.05f && dist < max) return true;
                        }
                    }
            }
            return false;
        }

        private void Lining(List<(Road Road, List<CVertex> Chain)> chains, StitchedTerrainResult result)
        {
            landMesh = result.Land;
            for (var t = 0; t + 2 < landMesh.Indices.Count; t += 3)
            {
                Vector3 a = landMesh.Positions[landMesh.Indices[t]], b = landMesh.Positions[landMesh.Indices[t + 1]], c = landMesh.Positions[landMesh.Indices[t + 2]];
                for (var bx = (int)Math.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Bucket); bx <= (int)Math.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Bucket); bx++)
                    for (var bz = (int)Math.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Bucket); bz <= (int)Math.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Bucket); bz++)
                    {
                        if (!landBuckets.TryGetValue((bx, bz), out var l)) landBuckets[(bx, bz)] = l = [];
                        l.Add(t);
                    }
            }
            foreach (var road in roads)
            {
                var mine = chains.Where(c => c.Road == road).Select(c => c.Chain).ToList();
                var st = road.S;
                var n = st.Count;
                const int U = SectionPoints;
                // Station intervals with ground over some of their arch.
                var covered = new bool[n];
                for (var i = 0; i + 1 < n; i++)
                    for (var u = RailRight; u <= U && !covered[i]; u++)
                        if (road.F[i, u % U] > 0 || road.F[i + 1, u % U] > 0) covered[i] = true;
                var step = 1f;
                var i0 = 0;
                while (i0 < n - 1)
                {
                    if (!covered[i0]) { i0++; continue; }
                    var i1 = i0;
                    while (i1 < n - 1 && covered[i1]) i1++;
                    LiningStretch(road, mine, Math.Max(0, i0 - 1), Math.Min(n - 1, i1 + 1), step, result);
                    i0 = i1 + 1;
                }
            }
        }

        private Vector3 ChartWorld(Road road, float along, float y, float step, out int station)
        {
            var d = road.Dist;
            var i = Array.BinarySearch(d, along);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, d.Length - 2);
            var t = Math.Clamp((along - d[i]) / Math.Max(1e-6f, d[i + 1] - d[i]), 0, 1);
            var u = RailRight + y / step;
            var uf = (int)Math.Floor(u);
            uf = Math.Clamp(uf, RailRight, SectionPoints - 1);
            var tu = Math.Clamp(u - uf, 0, 1);
            Vector3 W(int ii, int uu) => road.S[ii].W[uu % SectionPoints];
            var a = Vector3.Lerp(W(i, uf), W(i, uf + 1), tu);
            var b = Vector3.Lerp(W(i + 1, uf), W(i + 1, uf + 1), tu);
            station = t < 0.5f ? i : i + 1;
            return Vector3.Lerp(a, b, t);
        }

        private void LiningStretch(Road road, List<List<CVertex>> chains, int i0, int i1, float step, StitchedTerrainResult result)
        {
            var st = road.S;
            float ylen = (SectionPoints - RailRight) * step;
            float xa = road.Dist[i0], xb = road.Dist[i1];
            var cdt = new ConstrainedDelaunay(xa - 2, -1, xb + 2, ylen + 1);
            var exact = new Dictionary<int, Vector3>();
            int Put(float x, float y, Vector3? world)
            {
                var v = cdt.Insert(x, y);
                if (v >= 0 && world is { } w && !exact.ContainsKey(v)) exact[v] = w;
                return v;
            }
            float ChartY(CVertex v) => (v.U <= 1e-4f ? SectionPoints : v.U) - RailRight;
            // The contour's arch pieces in this stretch: fixed edges, their points exact (shared with the land).
            var boundary = new List<(float X, int V)>[] { [], [] };
            foreach (var chain in chains)
            {
                if (chain.All(v => v.Along < xa - 0.01f || v.Along > xb + 0.01f)) continue;
                var prev = -1;
                foreach (var cv in chain)
                {
                    var y = ChartY(cv) * step;
                    var v = Put(cv.Along, y, cv.P);
                    if (v >= 0 && prev >= 0) cdt.Constrain(prev, v);
                    if (v >= 0 && y < 1e-4f) boundary[0].Add((cv.Along, v));
                    if (v >= 0 && y > ylen - 1e-4f) boundary[1].Add((cv.Along, v));
                    prev = v;
                }
            }
            // The lattice: stations (every 8 m, more on bends), arch points every other one.
            var lastKept = -1;
            for (var i = i0; i <= i1; i++)
            {
                var keep = i == i0 || i == i1 || road.Dist[i] - road.Dist[lastKept] >= 8
                    || Vector3.Dot(st[i].T, st[lastKept].T) < MathF.Cos(4 * MathF.PI / 180);
                if (!keep) continue;
                lastKept = i;
                for (var u = RailRight; u <= SectionPoints; u += 2)
                {
                    var y = (u - RailRight) * step;
                    var v = Put(road.Dist[i], y, st[i].W[u % SectionPoints]);
                    if (v < 0) continue;
                    if (u == RailRight) boundary[0].Add((road.Dist[i], v));
                    if (u == SectionPoints) boundary[1].Add((road.Dist[i], v));
                }
            }
            foreach (var line in boundary)
            {
                line.Sort((p, q) => p.X.CompareTo(q.X));
                for (var k = 0; k + 1 < line.Count; k++) if (line[k].V != line[k + 1].V) cdt.Constrain(line[k].V, line[k + 1].V);
            }
            // Triangles over the arch whose middle lies under the ground.
            var index = new Dictionary<int, int>();
            var normals = new Dictionary<int, Vector3>();
            var tris = new List<(int A, int B, int C)>();
            int Vert(int v)
            {
                if (index.TryGetValue(v, out var id)) return id;
                var x = (float)cdt.X(v);
                var y = (float)cdt.Z(v);
                Vector3 w;
                if (exact.TryGetValue(v, out var e)) w = e;
                else
                {
                    // Inside the lining: under the land mesh as built (its triangles are coarser than the ground).
                    w = ChartWorld(road, x, y, step, out _);
                    var top = LandAt(w.X, w.Z);
                    if (!float.IsNaN(top) && w.Y > top - 0.15f)
                    {
                        if (w.Y - (top - 0.15f) > 1) result.Notes.Add($"lining clamp {w.Y - (top - 0.15f):F1} m at track {road.Index} {x:F0} m, round {y:F1}: ({w.X:F1}, {w.Y:F1}, {w.Z:F1})");
                        w.Y = top - 0.15f;
                    }
                }
                result.LiningVertices.Add(new LiningVertex(w, Vector3.Zero, road.Index, new Vector2(x, y)));
                index[v] = result.LiningVertices.Count - 1;
                return index[v];
            }
            foreach (var (a, b, c, _) in cdt.Triangles())
            {
                if (ConstrainedDelaunay.IsCorner(a) || ConstrainedDelaunay.IsCorner(b) || ConstrainedDelaunay.IsCorner(c)) continue;
                var mx = (float)(cdt.X(a) + cdt.X(b) + cdt.X(c)) / 3;
                var my = (float)(cdt.Z(a) + cdt.Z(b) + cdt.Z(c)) / 3;
                if (my <= 0 || my >= ylen || mx < xa || mx > xb) continue;
                var w = ChartWorld(road, mx, my, step, out var station);
                // Under land, unless that land is cut out by this road's own room here (a section curled over itself);
                // land cut out by another road over it still covers (earth between the two).
                // The floor between a rail and the floor's corner the land holds: always (it is the land's strip there).
                var seamSide = st[station].FootEnd > 0 ? 1 : 0;
                var seamW = st[station].SeamW(seamSide);
                var onFloor = seamW > 0 && road.Exact[seamSide][station] && (seamSide == 1 ? my < seamW * step : my > ylen - seamW * step);
                if (onFloor) { }
                else if (F(w) <= 0) continue;
                else if (InRoom(new Vector3(w.X, GAt(w.X, w.Z) - 0.02f, w.Z), road.Index, mx - 12, mx + 12, only: true))
                {
                    // The floor before a sideways opening stays all the same (under a deck leaning over it the ground is
                    // the land behind the deck's top, cut out; the floor from the low rail to the land in front has earth
                    // under it): the arch's first piece from the low rail, facing up into the room.
                    var end = st[station].FootEnd;
                    if (end == 0 || (end > 0 ? my > 2 * step : my < ylen - 2 * step)) continue;
                    Vector3 At(int v) => exact.TryGetValue(v, out var e) ? e : ChartWorld(road, (float)cdt.X(v), (float)cdt.Z(v), step, out _);
                    Vector3 fa = At(a), fb = At(b), fc = At(c);
                    var fn = Vector3.Cross(fb - fa, fc - fa);
                    if (fn.LengthSquared() < 1e-10f) continue;
                    fn = Vector3.Normalize(fn);
                    if (fn.Y < 0) fn = -fn;
                    if (fn.Y < 0.7f || Vector3.Dot(fn, st[station].Centre - (fa + fb + fc) / 3) <= 0) continue;
                }
                int ia = Vert(a), ib = Vert(b), ic = Vert(c);
                var pa = result.LiningVertices[ia].Position;
                var pb = result.LiningVertices[ib].Position;
                var pc = result.LiningVertices[ic].Position;
                var nrm = Vector3.Cross(pb - pa, pc - pa);
                if (nrm.LengthSquared() < 1e-10f) continue;
                // Facing into the room: towards the section's middle.
                var into = st[station].Centre - (pa + pb + pc) / 3;
                if (Vector3.Dot(nrm, into) < 0) { (ib, ic) = (ic, ib); nrm = -nrm; }
                // Earth behind it: the land mesh as built lies outward of it or above it (a ceiling or wall with open
                // air behind is the ground field's guess where the coarser land mesh runs lower). Pieces facing up into
                // the room (the floor side) are never seen from outside.
                var outward = -Vector3.Normalize(nrm);
                var mid = (pa + pb + pc) / 3;
                // (Up from a little way into the earth: on a vertical wall a ray up its face could pass the land's edge.)
                if (RayTest && outward.Y > -0.5f && !LandHit(mid, outward, 60) && !LandHit(mid + outward * 0.3f, Vector3.UnitY, 200)) continue;
                foreach (var v in new[] { ia, ib, ic }) normals[v] = normals.GetValueOrDefault(v) + nrm;
                tris.Add((ia, ib, ic));
            }
            foreach (var (v, nrm) in normals)
            {
                var lv = result.LiningVertices[v];
                result.LiningVertices[v] = lv with { Normal = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : -Vector3.UnitY };
            }
            // Small pieces on their own (slivers where the ground just touches the arch, at tube mouths) are left out.
            var parent = new Dictionary<int, int>();
            int Find(int v) { while (parent.TryGetValue(v, out var p) && p != v) { var g2 = parent.GetValueOrDefault(p, p); parent[v] = g2; v = p; } return v; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
            foreach (var (a, b, c) in tris) { parent.TryAdd(a, a); parent.TryAdd(b, b); parent.TryAdd(c, c); Union(a, b); Union(b, c); }
            var area = new Dictionary<int, float>();
            foreach (var (a, b, c) in tris)
            {
                var pa = result.LiningVertices[a].Position;
                var r = Find(a);
                area[r] = area.GetValueOrDefault(r) + 0.5f * Vector3.Cross(result.LiningVertices[b].Position - pa, result.LiningVertices[c].Position - pa).Length();
            }
            foreach (var (a, b, c) in tris)
                if (area[Find(a)] >= MinLiningArea) result.LiningIndices.AddRange([a, b, c]);
        }
    }
}
