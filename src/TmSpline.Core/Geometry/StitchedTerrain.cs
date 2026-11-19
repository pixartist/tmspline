using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The roads' rooms (what is left of the stitched terrain, the mesher before the voxel and boolean ones): per road a
/// closed section round the space a car needs (the driving surface and an arch over it, reaching straight up where the
/// sky is open over it), every StationStep metres along it. VoxelTerrain makes its cells small round them (Rooms), and
/// BooleanSeam builds the roads' free spaces from their sections (RoomSections).
/// </summary>
public static class StitchedTerrain
{
    private const float StationStep = 2f;
    /// <summary>Room's boundary this far inside the road's body (the land's edge is hidden in it), metres.</summary>
    private const float Tuck = 0.03f;
    /// <summary>The same for the driving surface (VoxelTerrain's rooms reach further under it: its land's surface is only as exact as its cells).</summary>
    [ThreadStatic] private static float deckTuck;
    private static float DeckTuck => deckTuck > 0 ? deckTuck : Tuck;
    /// <summary>The arch over the driving surface (along its normals, less on tight bends), and the rims' lift along the section's opening, metres.</summary>
    private const float ArchHeight = 4.5f, RimLift = 2f;
    /// <summary>Voxel rooms: the vault over a road at most this high over the line between its edges, metres (TMSPLINE_VAULT; unset: half the road's width).</summary>
    public static float VaultHeight { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_VAULT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vault) ? vault : float.MaxValue;
    private const int DeckN = 24, ArchN = 12;
    /// <summary>Chart index of the right rail; the left rail is 0; the arch runs from the right rail to U (the left again).</summary>
    private const int RailRight = DeckN + 2, SectionPoints = DeckN + ArchN + 2;
    private const float Bucket = 8f;

    private sealed class Station
    {
        public required Ring R;
        public required TrackSegment Seg;
        public Vector3 P, Right, Up, T;
        /// <summary>The section round the room, ring-local (right, up): rail L, deck L..R, rail R, arch R..L.</summary>
        public Vector2[] Poly = [];
        /// <summary>The road's own surface across the section, untucked: left edge's contact (a curb's, or the bare edge), the surface, the right edge's (as Poly's first RailRight + 1 points).</summary>
        public Vector2[] Contact = [];
        public Vector3[] W = [];
        /// <summary>The driving surface's normal at the deck points (W[1 + k]).</summary>
        public Vector3[] DeckNormal = [];
        public float Chord;
    }

    private sealed class Road
    {
        public int Index;
        public required Track Track;
        public List<Station> S = [];
        public float[] Dist = [];
        /// <summary>Stretches (distance along it, from and to) where the road is open: gaps and holes.</summary>
        public List<(float From, float To)> Open = [];
    }

    private sealed class Prism
    {
        public int Road, Station;
        public required Station A, B;
        /// <summary>Its sections' lowest and highest point (0, 0: not found yet); whether it is its road's last.</summary>
        public float Low, High;
        public bool Last;
        /// <summary>Whether the road is open here (a gap, or a hole in it): what lies under it shows.</summary>
        public bool Open;
    }

    private static float SS(float a, float b, float x)
    {
        x = Math.Clamp((x - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static Vector3 V(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>The roads' rooms alone, for VoxelTerrain: signed distance to them (negative inside), and their lowest point in a plan point's square of 8 m (+infinity: none there), and the distance with the way the driving surface faces where that is the nearest of a room's outline (else zero); the distance to the nearest road edge with the height over that road's edge line.</summary>
    public static (Func<Vector3, float> Distance, Func<float, float, float> Floor, Func<Vector3, (float Distance, Vector3 Deck)> Nearest, Func<Vector3, (float Edge, float Over, float Across, float Beyond)> Road, Func<Vector3, (float Distance, Vector3 Point, Vector3 Left, Vector3 Right)> Surface) Rooms(TerrainGrid grid, TrackMap map)
    {
        var b = new Builder(map);
        deckTuck = BooleanSeam.DeckCut;
        try { b.BuildRooms(); } finally { deckTuck = 0; }
        return (p => b.RoomDistance(p, out _), (x, z) => b.RoomFloor(x, z, 0), p => (b.RoomDistance(p, out var deck), deck), p => (b.RoadDistance(p, out var over, out var across, out var beyond), over, across, beyond), p => (b.SurfaceDistance(p, out var point, out var left, out var right), point, left, right));
    }

    /// <summary>
    /// The rooms' sections (world points: left rail, driving surface tucked <paramref name="tuck"/> under it, right rail,
    /// vault back to the left rail), every StationStep metres along each road, with the road's track.
    /// </summary>
    /// <summary>How many points a room's section has.</summary>
    internal const int RoomSectionPoints = SectionPoints;
    /// <summary>In a room's section: the driving surface's points (1 to RoomDeckPoints), the right rail's index (the left's is 0; the vault follows it).</summary>
    internal const int RoomDeckPoints = DeckN + 1, RoomRailRight = RailRight;

    internal static List<(Track Track, List<Vector3[]> Sections, List<double> Distances)> RoomSections(TerrainGrid grid, TrackMap map, float tuck)
    {
        var b = new Builder(map);
        deckTuck = tuck;
        try { b.BuildRooms(); } finally { deckTuck = 0; }
        return b.RoomPolygons();
    }

    private sealed class Builder(TrackMap map)
    {
        private readonly List<Road> roads = [];
        private readonly List<Prism> prisms = [];
        private readonly Dictionary<(int, int), List<int>> prismBuckets = [];
        // ---------------------------------------------------------------- Stations and their sections

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

        private void Sections(bool sky = true)
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
                // Where the road is open (gaps and holes, with a few metres round them), by distance along it.
                var starts = TrackSampler.SegmentStarts(track);
                foreach (var o in track.Objects)
                    if (o.Kind is TrackObjectKind.Gap or TrackObjectKind.Hole && o.Segment >= 0 && o.Segment < starts.Length - 1)
                    {
                        var at = TrackObjects.CenterDistance(o, starts);
                        road.Open.Add(((float)(at - o.Length / 2 - 4), (float)(at + o.Length / 2 + 4)));
                    }
                roads.Add(road);
            }
            Decks();
            if (sky && Environment.GetEnvironmentVariable("TMSPLINE_SKY") != "off") Sky();
            // The rooms between consecutive stations, in plan buckets.
            foreach (var road in roads)
                for (var i = 0; i + 1 < road.S.Count; i++)
                {
                    var p = new Prism { Road = road.Index, Station = i, A = road.S[i], B = road.S[i + 1], Last = i + 2 == road.S.Count };
                    p.Open = road.Open.Any(o => road.Dist[i + 1] >= o.From && road.Dist[i] <= o.To);
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
                deck[k] = pt - n * DeckTuck;
                var rm = ArchHeight;
                if (q.Curvature > 1e-4) rm = Math.Min(rm, (float)(0.9 / q.Curvature));
                offset.Add(pt + n * rm);
                nb += n;
            }
            nb = nb.LengthSquared() > 1e-4f ? Vector2.Normalize(nb) : Vector2.UnitY;
            var mid = deck.Aggregate((a, b) => a + b) / deck.Length;
            Vector2 TuckIn(Vector2 rail) { var d = mid - rail; return d.LengthSquared() > 1e-6f ? rail + Vector2.Normalize(d) * Tuck : rail; }
            Vector2 contactL = Loc(V(RoadMeshBuilder.EdgeContact(r, st.Seg, -1))), contactR = Loc(V(RoadMeshBuilder.EdgeContact(r, st.Seg, 1)));
            var railL = TuckIn(contactL);
            var railR = TuckIn(contactR);
            st.Contact = new Vector2[RailRight + 1];
            st.Contact[0] = contactL;
            for (var k = 0; k <= DeckN; k++) st.Contact[1 + k] = deck[k] + (st.DeckNormal[k] == default ? Vector2.Zero : new Vector2(Vector3.Dot(st.DeckNormal[k], st.Right), Vector3.Dot(st.DeckNormal[k], st.Up))) * DeckTuck;
            st.Contact[RailRight] = contactR;
            st.Chord = (railR - railL).Length();
            // A pipe's opening faces from its middle to its slot: the mean of its normals is no direction once the slot is
            // narrow (they cancel out, and it pointed down as often as up).
            if (Math.Abs(r.Curve) > Math.PI)
            {
                var open = (railL + railR) / 2 - mid;
                if (open.LengthSquared() > 0.25f) nb = Vector2.Normalize(open);
            }

            // The arch: the hull of the deck's offset, the rails and the rails lifted, from the right rail round the opening to the left.
            List<Vector2> chain;
            // The floor's corner (see below): which end of the arch it is at (1 the right rail's, -1 the left's), how far
            // (0..1) the opening faces sideways.
            var foot = Vector2.Zero;
            var footEnd = 0;
            var footW = 0f;
            if (st.Chord < SlotMin) chain = [railR, railL];
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
            }
            // VoxelTerrain's rooms: a round vault over the road, from rail to rail (half a circle, on a narrow road at
            // least ArchHeight high), not the hull's flat-topped shape.
            if (deckTuck > 0 && st.Chord >= SlotMin)
            {
                var half = (railR - railL) / 2;
                var vaultMid = (railL + railR) / 2;
                var o = Vector2.Normalize(new Vector2(-half.Y, half.X));
                var toDeck = Vector2.Dot(o, mid - vaultMid);
                // On the driving side: the way the deck faces (a half-pipe's deck lies under the line between its rails,
                // a road arching up lies over it: either way its vault goes the way it faces). Only where that is too
                // sideways to tell (a tube nearly closed), away from the deck.
                var facing = nb.LengthSquared() > 1e-8f ? Vector2.Dot(o, Vector2.Normalize(nb)) : 0;
                if (MathF.Abs(facing) > 0.3f ? facing < 0 : MathF.Abs(toDeck) < 0.5f ? Vector2.Dot(o, nb) < 0 : toDeck > 0) o = -o;
                // (No higher than VaultHeight, TMSPLINE_VAULT: a wide road's vault a flatter half-ellipse.)
                var height = MathF.Max(ArchHeight, MathF.Min(half.Length(), VaultHeight));
                for (var k = 1; k < ArchN; k++)
                {
                    var a = MathF.PI * k / ArchN;
                    arch[k] = vaultMid + half * MathF.Cos(a) + o * (height * MathF.Sin(a));
                }
            }
            var poly = new Vector2[SectionPoints];
            poly[0] = railL;
            for (var k = 0; k <= DeckN; k++) poly[1 + k] = deck[k];
            poly[RailRight] = railR;
            for (var k = 1; k < ArchN; k++) poly[RailRight + k] = arch[k];
            st.Poly = poly;
            st.W = poly.Select(v => st.P + st.Right * v.X + st.Up * v.Y).ToArray();
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

        /// <summary>For VoxelTerrain: the rooms alone (stations, sections, sky).</summary>
        public void BuildRooms() => Sections(sky: false);

        // (Split at the road's gaps: no room where the road is left out. Each section with its distance along the track.)
        public List<(Track Track, List<Vector3[]> Sections, List<double> Distances)> RoomPolygons() => roads
            .SelectMany(r => BooleanSeam.SplitAtGaps(r.Track, r.S, st => st.R.Distance)
                .Select(run => (r.Track, run.Select(st => st.W).ToList(), run.Select(st => st.R.Distance).ToList())))
            .ToList();

        /// <summary>The lowest point of the rooms near a plan point (those in its bucket and the ones round it), or +infinity.</summary>
        public float RoomFloor(float x, float z, int around = 1)
        {
            var low = float.PositiveInfinity;
            int bx = (int)Math.Floor(x / Bucket), bz = (int)Math.Floor(z / Bucket);
            for (var dx = -around; dx <= around; dx++)
                for (var dz = -around; dz <= around; dz++)
                    if (prismBuckets.TryGetValue((bx + dx, bz + dz), out var list))
                        foreach (var id in list)
                        {
                            var iv = prisms[id];
                            if (iv.Low == 0 && iv.High == 0)
                            {
                                float lo = float.MaxValue, hi = float.MinValue;
                                foreach (var w in iv.A.W.Concat(iv.B.W)) { lo = MathF.Min(lo, w.Y); hi = MathF.Max(hi, w.Y); }
                                iv.Low = lo;
                                iv.High = hi;
                            }
                            low = MathF.Min(low, iv.Low);
                        }
            return low;
        }

        /// <summary>Distance to the nearest road edge (a rail), metres (`far` where none is within about a bucket), and how far over that road's edge line the point lies (towards its vault's top).</summary>
        /// <param name="beyond">Past an open road's end (along it, outwards): the distance to its end, the line across between its edges; else MaxValue.</param>
        public float RoadDistance(Vector3 p, out float over, out float across, out float beyond, float far = 1e3f)
        {
            var best = far;
            over = 0;
            across = 0;
            beyond = float.MaxValue;
            int bx = (int)Math.Floor(p.X / Bucket), bz = (int)Math.Floor(p.Z / Bucket);
            for (var ox = -1; ox <= 1; ox++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (!prismBuckets.TryGetValue((bx + ox, bz + oz), out var list)) continue;
                    foreach (var id in list)
                    {
                        var iv = prisms[id];
                        var t0 = Vector3.Dot(p - iv.A.P, iv.A.T);
                        var t1 = Vector3.Dot(p - iv.B.P, iv.B.T);
                        var f = t0 - t1;
                        if (MathF.Abs(f) < 1e-5f) continue;
                        var s = t0 / f;
                        var along = 0f;
                        if (s < 0) { if (iv.Station > 0 && s < -0.05f) continue; along = -t0; }
                        else if (s > 1) { if (!iv.Last && s > 1.05f) continue; along = t1; }
                        if (along > best) continue;
                        s = Math.Clamp(s, 0, 1);
                        var P = Vector3.Lerp(iv.A.P, iv.B.P, s);
                        var R = Vector3.Lerp(iv.A.Right, iv.B.Right, s);
                        var U = Vector3.Lerp(iv.A.Up, iv.B.Up, s);
                        var d = p - P;
                        var q = new Vector2(Vector3.Dot(d, R) / R.LengthSquared(), Vector3.Dot(d, U) / U.LengthSquared());
                        Vector2 railL = Vector2.Lerp(iv.A.Poly[0], iv.B.Poly[0], s), railR = Vector2.Lerp(iv.A.Poly[RailRight], iv.B.Poly[RailRight], s);
                        var near = MathF.Min((q - railL).LengthSquared(), (q - railR).LengthSquared());
                        var dist = MathF.Sqrt(near + along * along);
                        if (dist >= best) continue;
                        best = dist;
                        // How far the point lies over the line between the two edges, towards the vault's top.
                        var mid = (railL + railR) / 2;
                        var up = Vector2.Lerp(iv.A.Poly[RailRight + ArchN / 2], iv.B.Poly[RailRight + ArchN / 2], s) - mid;
                        over = up.LengthSquared() > 1e-8f ? Vector2.Dot(q - mid, Vector2.Normalize(up)) : 0;
                        // And where across the road: 0 at its middle, 1 at an edge.
                        var half = (railR - railL) / 2;
                        across = half.LengthSquared() > 1e-8f ? MathF.Abs(Vector2.Dot(q - mid, half)) / half.LengthSquared() : 0;
                        if (along > 0)
                        {
                            // To the line between the edges at the end, in the section's plane, then along.
                            var line = railR - railL;
                            var h = line.LengthSquared() > 1e-8f ? Math.Clamp(Vector2.Dot(q - railL, line) / line.LengthSquared(), 0, 1) : 0;
                            beyond = MathF.Sqrt((q - (railL + line * h)).LengthSquared() + along * along);
                        }
                        else beyond = float.MaxValue;
                    }
                }
            return best;
        }

        /// <summary>
        /// Distance to the nearest road's driving surface, edges included (the section's line from one edge across the
        /// surface to the other, not the vault over it), and past an open road's end, to that line at the end: one
        /// distance with no jumps in it, for the seam (`far` where no road is within about a bucket).
        /// </summary>
        /// <param name="point">The nearest point on that line (p itself where no road is near).</param>
        /// <param name="left">That section's left edge (p itself where no road is near).</param>
        /// <param name="right">And its right edge.</param>
        public float SurfaceDistance(Vector3 p, out Vector3 point, out Vector3 left, out Vector3 right, float far = 1e3f)
        {
            var best = far;
            point = left = right = p;
            int bx = (int)Math.Floor(p.X / Bucket), bz = (int)Math.Floor(p.Z / Bucket);
            for (var ox = -1; ox <= 1; ox++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (!prismBuckets.TryGetValue((bx + ox, bz + oz), out var list)) continue;
                    foreach (var id in list)
                    {
                        var iv = prisms[id];
                        var t0 = Vector3.Dot(p - iv.A.P, iv.A.T);
                        var t1 = Vector3.Dot(p - iv.B.P, iv.B.T);
                        var f = t0 - t1;
                        if (MathF.Abs(f) < 1e-5f) continue;
                        var s = t0 / f;
                        var along = 0f;
                        if (s < 0) { if (iv.Station > 0 && s < -0.05f) continue; along = -t0; }
                        else if (s > 1) { if (!iv.Last && s > 1.05f) continue; along = t1; }
                        if (along >= best) continue;
                        s = Math.Clamp(s, 0, 1);
                        var P = Vector3.Lerp(iv.A.P, iv.B.P, s);
                        var R = Vector3.Lerp(iv.A.Right, iv.B.Right, s);
                        var U = Vector3.Lerp(iv.A.Up, iv.B.Up, s);
                        var d = p - P;
                        var q = new Vector2(Vector3.Dot(d, R) / R.LengthSquared(), Vector3.Dot(d, U) / U.LengthSquared());
                        var near = float.MaxValue;
                        var at = Vector2.Zero;
                        var a = Vector2.Lerp(iv.A.Poly[0], iv.B.Poly[0], s);
                        var contact = iv.A.Contact.Length > RailRight && iv.B.Contact.Length > RailRight;
                        for (var i = 1; i <= RailRight; i++)
                        {
                            var b = Vector2.Lerp(iv.A.Poly[i], iv.B.Poly[i], s);
                            var e = b - a;
                            var w = q - a;
                            var l2 = e.LengthSquared();
                            var h = l2 > 1e-12f ? Math.Clamp(Vector2.Dot(w, e) / l2, 0, 1) : 0;
                            var e2 = (w - e * h).LengthSquared();
                            if (e2 < near)
                            {
                                near = e2;
                                // (The point on the road's own surface, untucked, at the same place along the section.)
                                at = contact ? Vector2.Lerp(Vector2.Lerp(iv.A.Contact[i - 1], iv.B.Contact[i - 1], s), Vector2.Lerp(iv.A.Contact[i], iv.B.Contact[i], s), h) : a + e * h;
                            }
                            a = b;
                        }
                        var dist = MathF.Sqrt(near + along * along);
                        if (dist < best)
                        {
                            best = dist;
                            point = P + R * at.X + U * at.Y;
                            Vector2 l = contact ? Vector2.Lerp(iv.A.Contact[0], iv.B.Contact[0], s) : Vector2.Lerp(iv.A.Poly[0], iv.B.Poly[0], s);
                            Vector2 r = contact ? Vector2.Lerp(iv.A.Contact[RailRight], iv.B.Contact[RailRight], s) : Vector2.Lerp(iv.A.Poly[RailRight], iv.B.Poly[RailRight], s);
                            left = P + R * l.X + U * l.Y;
                            right = P + R * r.X + U * r.Y;
                        }
                    }
                }
            return best;
        }

        /// <summary>
        /// Signed distance to the rooms (negative inside), metres; `far` where none is within about a bucket. Across a
        /// room's section the distance to its outline, beyond a road's end the distance to its end section.
        /// </summary>
        public float RoomDistance(Vector3 p, out Vector3 deck, float far = 1e3f)
        {
            var best = far;
            deck = default;
            int bx = (int)Math.Floor(p.X / Bucket), bz = (int)Math.Floor(p.Z / Bucket);
            for (var ox = -1; ox <= 1; ox++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (!prismBuckets.TryGetValue((bx + ox, bz + oz), out var list)) continue;
                    foreach (var id in list)
                    {
                        var iv = prisms[id];
                        var t0 = Vector3.Dot(p - iv.A.P, iv.A.T);
                        var t1 = Vector3.Dot(p - iv.B.P, iv.B.T);
                        var f = t0 - t1;
                        if (MathF.Abs(f) < 1e-5f) continue;
                        var s = t0 / f;
                        // Before the first or past the last section: only at a road's ends (elsewhere the next prism has it).
                        var along = 0f;
                        if (s < 0) { if (iv.Station > 0 && s < -0.05f) continue; along = -t0; }
                        else if (s > 1) { if (!iv.Last && s > 1.05f) continue; along = t1; }
                        if (along > best) continue;
                        s = Math.Clamp(s, 0, 1);
                        var P = Vector3.Lerp(iv.A.P, iv.B.P, s);
                        var R = Vector3.Lerp(iv.A.Right, iv.B.Right, s);
                        var U = Vector3.Lerp(iv.A.Up, iv.B.Up, s);
                        var d = p - P;
                        var q = new Vector2(Vector3.Dot(d, R) / R.LengthSquared(), Vector3.Dot(d, U) / U.LengthSquared());
                        var inside = false;
                        var near = float.MaxValue;
                        var nearDeck = Vector2.Zero;
                        var middle = Vector2.Zero;
                        var pa = iv.A.Poly;
                        var pb = iv.B.Poly;
                        var a = Vector2.Lerp(pa[^1], pb[^1], s);
                        for (var i = 0; i < pa.Length; i++)
                        {
                            var b = Vector2.Lerp(pa[i], pb[i], s);
                            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y)) inside = !inside;
                            var e = b - a;
                            var w = q - a;
                            var l2 = e.LengthSquared();
                            var h = l2 > 1e-12f ? Math.Clamp(Vector2.Dot(w, e) / l2, 0, 1) : 0;
                            var e2 = (w - e * h).LengthSquared();
                            // (The section runs rail, driving surface, rail, then the vault back to the first rail.)
                            // Of the driving surface: which way it faces (across the piece, turned into the room below).
                            if (e2 < near) { near = e2; nearDeck = i >= 1 && i <= RailRight ? new Vector2(-e.Y, e.X) : Vector2.Zero; }
                            middle += b;
                            a = b;
                        }
                        var d2 = MathF.Sqrt(near);
                        float dist;
                        if (along > 0) dist = inside ? along : MathF.Sqrt(d2 * d2 + along * along);
                        else dist = inside ? -d2 : d2;
                        if (dist < best)
                        {
                            best = dist;
                            deck = default;
                            // (Not under a gap or a hole: there the floor shows, and counts as any other of the cut's walls.)
                            if (along <= 0 && !iv.Open && nearDeck.LengthSquared() > 1e-12f)
                            {
                                if (Vector2.Dot(nearDeck, middle / pa.Length - q) < 0) nearDeck = -nearDeck;
                                deck = Vector3.Normalize(R * nearDeck.X + U * nearDeck.Y);
                            }
                        }
                    }
                }
            return best;
        }

        /// <summary>A pipe's slot narrower than this is closed: no room over it, and its land lets go.</summary>
        private const float SlotMin = 0.3f;
    }
}
