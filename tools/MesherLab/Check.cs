using System.Diagnostics;
using System.Numerics;
using SkiaSharp;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

/// <summary>
/// The terrain checker: builds a map's terrain as the export does (old or new builder) and measures the finished triangles
/// against the real road mesh, independently of how the builder works.
/// <list type="bullet">
/// <item>Pokes: land inside the space a car needs over a driving surface (0.3 to 3.5 m along its normal).</item>
/// <item>Held edges: share of the road's edges where land meets the curb's outer top (within 15 cm, 30 cm out).</item>
/// <item>Open edges: land edges that are neither at a road, at the lining nor at the land's outline (holes).</item>
/// <item>Mesh: non-manifold edges, triangles, chunks, estimated bytes.</item>
/// <item>Pictures: per test cell a view and a cross-section (land green, rock grey, road orange, back faces magenta).</item>
/// </list>
/// Usage: MesherLab --check map.json outDir old|new
/// </summary>
static class Check
{
    sealed record Tri(Vector3 A, Vector3 B, Vector3 C, int Kind); // 0 land, 1 rock/lining, 2 road

    public static void Run(string mapFile, string outDir, string builder, string? only = null)
    {
        Directory.CreateDirectory(outDir);
        var map = TrackJson.ParseMap(File.ReadAllText(mapFile));
        if (only is not null)
        {
            var keep = only.Split(',').Select(int.Parse).Select(i => map.Tracks[i]).ToList();
            map.Tracks.Clear();
            map.Tracks.AddRange(keep);
        }
        var grid = map.Terrain ?? throw new InvalidOperationException("no terrain");
        TerrainMeshBuilder.Stitched = builder != "old";
        var sw = Stopwatch.StartNew();
        var chunks = TerrainMeshBuilder.Build(grid, map);
        var buildMs = sw.ElapsedMilliseconds;
        var land = new List<Tri>();
        foreach (var c in chunks)
        {
            var o = new Vector3((float)c.ItemOffset.X, (float)c.ItemOffset.Y, (float)c.ItemOffset.Z);
            foreach (var t in c.Triangles)
                land.Add(new Tri(c.Positions[t.A] + o, c.Positions[t.B] + o, c.Positions[t.C] + o, c.Materials[t.Material].GamePath is null ? 0 : 1));
        }
        var bytes = MeshQualities.EstimateTerrainBytes(chunks);
        Console.WriteLine($"[{builder}] {Path.GetFileName(mapFile)}: build {buildMs} ms, {chunks.Count} chunks, {land.Count} triangles ({land.Count(t => t.Kind == 1)} rock), estimated {bytes / 1e6:F2} MB");
        if (builder != "old" && Environment.GetEnvironmentVariable("CHECK_FIND") is { } findAt && TerrainMeshBuilder.LastStitched is { } dbf)
        {
            // The stations nearest a point, then every 4 m along that track round it: held scores and the rails' gap.
            var v = findAt.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var p = new Vector3(v[0], v[1], v[2]);
            var best = dbf.Stations.MinBy(x => Vector3.Distance(x.Section[13], p));
            Console.WriteLine($"  nearest station: t{best.Track} at {best.Along:F1}");
            var last = float.MinValue;
            foreach (var s in dbf.Stations.Where(x => x.Track == best.Track && Math.Abs(x.Along - best.Along) < 120).OrderBy(x => x.Along))
            {
                if (s.Along - last < 4) continue;
                last = s.Along;
                Console.WriteLine($"    {s.Along:F1}: held {s.HeldLeft:F2}/{s.HeldRight:F2} gap {Vector3.Distance(s.Section[0], s.Section[26]):F2} rails y {s.Section[0].Y:F1}/{s.Section[26].Y:F1} deck y {s.Section[13].Y:F1}");
            }
        }
        if (builder != "old" && Environment.GetEnvironmentVariable("CHECK_STATION") is { } at && TerrainMeshBuilder.LastStitched is { } dbg)
        {
            var parts = at.Split(':');
            var s = dbg.Stations.Where(x => x.Track == int.Parse(parts[0])).MinBy(x => Math.Abs(x.Along - float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
            Console.WriteLine($"  station t{s.Track} at {s.Along:F1}: held {s.HeldLeft:F2}/{s.HeldRight:F2}");
            for (var i = 0; i < s.Section.Length; i++) Console.WriteLine($"    {i}: ({s.Section[i].X:F2}, {s.Section[i].Y:F2}, {s.Section[i].Z:F2}) f {s.F[i]:F2}");
            var deckMid = s.Section[13];
            foreach (var up in new[] { 0.1f, 0.5f, 1f, 2f, 3f })
                Console.WriteLine($"  in room {up} m over the deck's middle: {dbg.InRoom(deckMid + Vector3.UnitY * up)}");
            foreach (var u in new[] { 0, 26 })
                Console.WriteLine($"  explain rail {u} at ({s.Section[u].X:F2}, {s.Section[u].Y:F2}, {s.Section[u].Z:F2}):\n{dbg.Explain(s.Section[u].X, s.Section[u].Z)}");
        }
        if (builder != "old" && Environment.GetEnvironmentVariable("CHECK_EXPLAIN") is { } ex && TerrainMeshBuilder.LastStitched is { } dbg3)
        {
            foreach (var one in ex.Split(';'))
            {
                var xz = one.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Console.WriteLine($"  explain ({xz[0]}, {xz[1]}):\n{dbg3.Explain(xz[0], xz[1])}");
            }
        }
        if (builder != "old" && Environment.GetEnvironmentVariable("CHECK_NEAR") is { } nearAt && TerrainMeshBuilder.LastStitched is { } dbn)
        {
            // The land and lining triangles round a point, each vertex marked P when it is a pin point.
            var v = nearAt.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var nearPt = new Vector3(v[0], v[1], v[2]);
            var pinPts = dbn.Pins.SelectMany(p => p.Points).Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToList();
            string Fmt(Vector3 p) => $"({p.X:F1},{p.Y:F1},{p.Z:F1}){(pinPts.Any(q => Vector3.DistanceSquared(q, p) < 1e-3f) ? "P" : "")}";
            foreach (var pin in dbn.Pins)
                if (pin.Points.Any(p => Vector3.Distance(new Vector3((float)p.X, (float)p.Y, (float)p.Z), nearPt) < 1.5f))
                    Console.WriteLine($"  pin of {pin.Points.Count}: {string.Join(" ", pin.Points.Take(40).Select(p => $"({p.X:F1},{p.Y:F1},{p.Z:F1})"))}");
            var L = dbn.Land;
            for (var i = 0; i < L.Indices.Count; i += 3)
            {
                Vector3 a = L.Positions[L.Indices[i]], b = L.Positions[L.Indices[i + 1]], c = L.Positions[L.Indices[i + 2]];
                if (Vector3.Distance((a + b + c) / 3, nearPt) > v[3]) continue;
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                Console.WriteLine($"  land {Fmt(a)} {Fmt(b)} {Fmt(c)} ny {MathF.Abs(n.Y):F2}");
            }
            for (var i = 0; i < dbn.LiningIndices.Count; i += 3)
            {
                Vector3 a = dbn.LiningVertices[dbn.LiningIndices[i]].Position, b = dbn.LiningVertices[dbn.LiningIndices[i + 1]].Position, c = dbn.LiningVertices[dbn.LiningIndices[i + 2]].Position;
                if (Vector3.Distance((a + b + c) / 3, nearPt) > v[3]) continue;
                Console.WriteLine($"  lining {Fmt(a)} {Fmt(b)} {Fmt(c)}");
            }
        }
        if (builder != "old") Console.WriteLine($"  budget:{TerrainMeshBuilder.TriangleBudget(grid, map)} land triangles");
        if (builder != "old" && TerrainMeshBuilder.LastStitched is { } st)
            Console.WriteLine($"  stages: {string.Join(", ", st.Milliseconds.Select(kv => $"{kv.Key} {kv.Value} ms"))}; pins {st.Pins.Count} ({st.Pins.Sum(p => p.Points.Count)} points), land {st.Land.Indices.Count / 3} triangles in {st.Land.Rounds} rounds, lining {st.LiningIndices.Count / 3}, refused {st.Land.RefusedSegments}");
        if (builder != "old" && TerrainMeshBuilder.LastStitched is { } stn) Console.WriteLine($"  notes: {stn.Notes.Count}; {string.Join(" | ", stn.Notes.Take(8))}");

        // The road as exported at Normal quality, world space.
        var road = new List<Tri>();
        var surface = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N, int Track, float Along, Vector3 Across)>();
        var ringsOf = new List<List<Ring>>();
        for (var ti = 0; ti < map.Tracks.Count; ti++)
        {
            var track = map.Tracks[ti];
            track.SyncSegments();
            var rings = TrackSampler.Sample(track, MeshQualities.Sampler(MeshQuality.Normal));
            ringsOf.Add(rings);
            if (rings.Count < 2) continue;
            var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, WorldSpace = true, MaxColumnAngleDegrees = MeshQualities.ColumnAngleDegrees(MeshQuality.Normal) };
            foreach (var ch in RoadMeshBuilder.Build(track, rings, Vec3d.Zero, "R", settings))
                foreach (var t in ch.Triangles)
                {
                    road.Add(new Tri(ch.Positions[t.A], ch.Positions[t.B], ch.Positions[t.C], 2));
                    if (ch.Surface.Count != ch.Positions.Count || float.IsNaN(ch.Surface[t.A].X) || float.IsNaN(ch.Surface[t.B].X) || float.IsNaN(ch.Surface[t.C].X)) continue;
                    var n = ch.Normals[t.A] + ch.Normals[t.B] + ch.Normals[t.C];
                    if (n.LengthSquared() < 1e-8f) continue;
                    surface.Add((ch.Positions[t.A], ch.Positions[t.B], ch.Positions[t.C], Vector3.Normalize(n), ti, (ch.Surface[t.A].X + ch.Surface[t.B].X + ch.Surface[t.C].X) / 3, new Vector3(ch.Surface[t.A].Y, ch.Surface[t.B].Y, ch.Surface[t.C].Y)));
                }
        }

        var pokes = Pokes(land, surface);
        if (builder != "old" && TerrainMeshBuilder.LastStitched is { } dbg2)
            foreach (var (p, track, along) in pokes.Skip(int.Parse(Environment.GetEnvironmentVariable("CHECK_POKES") ?? "0")).Take(3))
            {
                var pins = dbg2.Pins.Where(pin => pin.Points.Any(q => Math.Abs(q.X - p.X) < 12 && Math.Abs(q.Z - p.Z) < 12)).ToList();
                var sts = dbg2.Stations.Where(s => s.Track == track && Math.Abs(s.Along - along) < 10).ToList();
                Console.WriteLine($"  poke at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) t{track} {along:F0}: {pins.Count} pins near ({string.Join(", ", pins.Select(pin => pin.Points.Count))} points); stations held/fRail: " +
                    string.Join(" ", sts.Where((_, i) => i % 2 == 0).Select(s => $"{s.Along:F0}:{s.HeldLeft:F1}/{s.HeldRight:F1}({s.F[0]:F1},{s.F[26]:F1})")));
                foreach (var t in land.Where(t => Math.Abs(t.A.X - p.X) + Math.Abs(t.A.Z - p.Z) < 40))
                {
                    var det = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
                    if (Math.Abs(det) < 1e-9f) continue;
                    var l1 = ((t.B.Z - t.C.Z) * (p.X - t.C.X) + (t.C.X - t.B.X) * (p.Z - t.C.Z)) / det;
                    var l2 = ((t.C.Z - t.A.Z) * (p.X - t.C.X) + (t.A.X - t.C.X) * (p.Z - t.C.Z)) / det;
                    if (l1 < 0 || l2 < 0 || l1 + l2 > 1) continue;
                    var c = (t.A + t.B + t.C) / 3;
                    Console.WriteLine($"    tri {t.A:F1} {t.B:F1} {t.C:F1} centroid {c:F1} inRoom {dbg2.InRoom(c)}");
                }
                foreach (var pin in pins.Take(4))
                {
                    var near = pin.Points.Where(q => Math.Abs(q.X - p.X) < 12 && Math.Abs(q.Z - p.Z) < 12).ToList();
                    Console.WriteLine($"    pin: {string.Join(" ", near.Take(8).Select(q => $"({q.X:F1},{q.Y:F1},{q.Z:F1})"))}");
                }
            }
        Held(map, ringsOf, land);
        Exposed(land, road);
        Overhead(land, road, surface);
        var open = Topology(grid, land, road);
        Pictures(map, land, road, Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(mapFile)}_{builder}.png"), builder != "old" ? TerrainMeshBuilder.LastStitched : null, pokes, ringsOf, open);
    }

    static Dictionary<(int, int), List<int>> Buckets<T>(IReadOnlyList<T> items, Func<T, (Vector3, Vector3, Vector3)> tri, float size)
    {
        var b = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < items.Count; i++)
        {
            var (a, bb, c) = tri(items[i]);
            for (var x = (int)Math.Floor(Math.Min(a.X, Math.Min(bb.X, c.X)) / size); x <= (int)Math.Floor(Math.Max(a.X, Math.Max(bb.X, c.X)) / size); x++)
                for (var z = (int)Math.Floor(Math.Min(a.Z, Math.Min(bb.Z, c.Z)) / size); z <= (int)Math.Floor(Math.Max(a.Z, Math.Max(bb.Z, c.Z)) / size); z++)
                {
                    if (!b.TryGetValue((x, z), out var l)) b[(x, z)] = l = [];
                    l.Add(i);
                }
        }
        return b;
    }

    /// <summary>Terrain triangles whose back faces the sky with nothing over them (seen from above, their underside shows:
    /// a lining with open air behind it, or folded land).</summary>
    static void Exposed(List<Tri> land, List<Tri> road)
    {
        var all = land.Concat(road).ToList();
        var buckets = Buckets(all, t => (t.A, t.B, t.C), 8);
        bool Covered(Vector3 o)
        {
            if (!buckets.TryGetValue(((int)Math.Floor(o.X / 8), (int)Math.Floor(o.Z / 8)), out var list)) return false;
            foreach (var i in list)
            {
                var t = all[i];
                var det = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
                if (Math.Abs(det) < 1e-9f) continue;
                var l1 = ((t.B.Z - t.C.Z) * (o.X - t.C.X) + (t.C.X - t.B.X) * (o.Z - t.C.Z)) / det;
                var l2 = ((t.C.Z - t.A.Z) * (o.X - t.C.X) + (t.A.X - t.C.X) * (o.Z - t.C.Z)) / det;
                if (l1 < -1e-4f || l2 < -1e-4f || l1 + l2 > 1 + 1e-4f) continue;
                if (l1 * t.A.Y + l2 * t.B.Y + (1 - l1 - l2) * t.C.Y > o.Y + 0.05f) return true;
            }
            return false;
        }
        int[] count = [0, 0];
        float[] area = [0, 0];
        var where = new Dictionary<(int, int), float>();
        foreach (var t in land)
        {
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            var l = n.Length();
            if (l < 1e-8f || n.Y / l > -0.3f) continue;
            var c = (t.A + t.B + t.C) / 3;
            if (Covered(c)) continue;
            count[t.Kind]++;
            area[t.Kind] += l / 2;
            var k = ((int)Math.Round(c.X / 20) * 20, (int)Math.Round(c.Z / 20) * 20);
            where[k] = where.GetValueOrDefault(k) + l / 2;
        }
        Console.WriteLine($"  exposed undersides: land {count[0]} ({area[0]:F0} m²), rock {count[1]} ({area[1]:F0} m²); worst at {string.Join(" ", where.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"({kv.Key.Item1},{kv.Key.Item2}):{kv.Value:F0}"))}");
    }

    /// <summary>Land straight over an up-facing driving surface with no road surface between (a sheet over a bank).</summary>
    static void Overhead(List<Tri> land, List<Tri> road, List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N, int Track, float Along, Vector3 Across)> surface)
    {
        var all = land.Concat(road).ToList();
        var buckets = Buckets(all, t => (t.A, t.B, t.C), 8);
        var cells = new Dictionary<(int, int), (float H, int Track, float Along)>();
        foreach (var s in surface)
        {
            if (s.N.Y < 0.25f) continue;
            foreach (var p in Samples(new Tri(s.A, s.B, s.C, 2), 1f))
            {
                var key = ((int)Math.Floor(p.X / 0.5f), (int)Math.Floor(p.Z / 0.5f));
                if (cells.ContainsKey(key) || !buckets.TryGetValue(((int)Math.Floor(p.X / 8), (int)Math.Floor(p.Z / 8)), out var list)) continue;
                var best = float.PositiveInfinity;
                var kind = -1;
                foreach (var i in list)
                {
                    var t = all[i];
                    var det = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
                    if (Math.Abs(det) < 1e-9f) continue;
                    var l1 = ((t.B.Z - t.C.Z) * (p.X - t.C.X) + (t.C.X - t.B.X) * (p.Z - t.C.Z)) / det;
                    var l2 = ((t.C.Z - t.A.Z) * (p.X - t.C.X) + (t.A.X - t.C.X) * (p.Z - t.C.Z)) / det;
                    if (l1 < 0 || l2 < 0 || l1 + l2 > 1) continue;
                    var d = l1 * t.A.Y + l2 * t.B.Y + (1 - l1 - l2) * t.C.Y - p.Y;
                    if (d > 0.3f && d < best) { best = d; kind = t.Kind; }
                }
                if (kind is 0 or 1) cells[key] = (best, s.Track, s.Along);
            }
        }
        var groups = cells.Values.GroupBy(c => (c.Track, (int)(c.Along / 25))).OrderByDescending(g => g.Count()).Take(6);
        Console.WriteLine($"  overhead: {cells.Count} cells of 0.5 m with land straight over a driving surface (no road between)" +
            string.Concat(groups.Select(g => $"\n    track {g.Key.Track} at {g.Key.Item2 * 25}-{g.Key.Item2 * 25 + 25} m: {g.Count()} cells, {g.Min(c => c.H):F1}-{g.Max(c => c.H):F1} m over")));
    }

    static IEnumerable<Vector3> Samples(Tri t, float step)
    {
        var longest = Math.Max((t.A - t.B).Length(), Math.Max((t.B - t.C).Length(), (t.C - t.A).Length()));
        var s = Math.Min(96, Math.Max(1, (int)Math.Ceiling(longest / step)));
        for (var u = 0; u <= s; u++)
            for (var v = 0; u + v <= s; v++)
                yield return t.A * (1 - (float)(u + v) / s) + t.B * ((float)u / s) + t.C * ((float)v / s);
    }

    static List<(Vector3 P, int Track, float Along)> Pokes(List<Tri> land, List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N, int Track, float Along, Vector3 Across)> surface)
    {
        const float Size = 4;
        var buckets = Buckets(surface, s => (s.A, s.B, s.C), Size);
        // Half the road's width per track and metre along it: the outer metre of the surface (where the land meets a wall's foot) does not count.
        var halfWidth = new Dictionary<(int, int), float>();
        foreach (var s in surface) { var key = (s.Track, (int)s.Along); halfWidth[key] = Math.Max(halfWidth.GetValueOrDefault(key), Math.Max(Math.Abs(s.Across.X), Math.Max(Math.Abs(s.Across.Y), Math.Abs(s.Across.Z)))); }
        var cells = new Dictionary<(int, int), (float Depth, int Track, float Along, Vector3 P)>();
        foreach (var t in land)
            foreach (var p in Samples(t, 0.4f))
            {
                if (!buckets.TryGetValue(((int)Math.Floor(p.X / Size), (int)Math.Floor(p.Z / Size)), out var list)) continue;
                foreach (var i in list)
                {
                    var s = surface[i];
                    var d = Vector3.Dot(p - s.A, s.N);
                    if (d < 0.3f || d > 3.5f) continue;
                    var q = p - s.N * d;
                    // Inside the triangle (barycentric, in its plane).
                    Vector3 v0 = s.B - s.A, v1 = s.C - s.A, v2 = q - s.A;
                    float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                    var den = d00 * d11 - d01 * d01;
                    if (Math.Abs(den) < 1e-10f) continue;
                    var bv = (d11 * d20 - d01 * d21) / den;
                    var bw = (d00 * d21 - d01 * d20) / den;
                    if (bv < 0 || bw < 0 || bv + bw > 1) continue;
                    var across = Math.Abs(s.Across.X * (1 - bv - bw) + s.Across.Y * bv + s.Across.Z * bw);
                    if (across > halfWidth.GetValueOrDefault((s.Track, (int)s.Along)) - 1) continue;
                    var k = ((int)Math.Floor(p.X / 0.5f), (int)Math.Floor(p.Z / 0.5f));
                    if (!cells.TryGetValue(k, out var e) || e.Depth < d) cells[k] = (d, s.Track, s.Along, p);
                }
            }
        var worst = cells.Values.Count == 0 ? 0 : cells.Values.Max(c => c.Depth);
        Console.WriteLine($"  pokes: {cells.Count} cells of 0.5 m with land 0.3-3.5 m over a driving surface (worst {worst:F2} m); over 1 m: {cells.Values.Count(c => c.Depth > 1)}");
        foreach (var g in cells.Values.GroupBy(c => (c.Track, Bin: (int)(c.Along / 25))).OrderByDescending(g => g.Count()).Take(10))
            Console.WriteLine($"    track {g.Key.Track} at {g.Key.Bin * 25}-{g.Key.Bin * 25 + 25} m: {g.Count()} cells, worst {g.Max(c => c.Depth):F2} m");
        return cells.Values.GroupBy(c => (c.Track, Bin: (int)(c.Along / 25))).OrderByDescending(g => g.Count()).Take(9).Select(g => g.MaxBy(c => c.Depth)).Select(c => (c.P, c.Track, c.Along)).ToList();
    }

    static void Held(TrackMap map, List<List<Ring>> ringsOf, List<Tri> land)
    {
        const float Size = 4;
        var buckets = Buckets(land, t => (t.A, t.B, t.C), Size);
        float? HeightsNear(float x, float z, float y)
        {
            if (!buckets.TryGetValue(((int)Math.Floor(x / Size), (int)Math.Floor(z / Size)), out var list)) return null;
            float? best = null;
            foreach (var i in list)
            {
                var t = land[i];
                var det = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
                if (Math.Abs(det) < 1e-9f) continue;
                var l1 = ((t.B.Z - t.C.Z) * (x - t.C.X) + (t.C.X - t.B.X) * (z - t.C.Z)) / det;
                var l2 = ((t.C.Z - t.A.Z) * (x - t.C.X) + (t.A.X - t.C.X) * (z - t.C.Z)) / det;
                var l3 = 1 - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                var h = l1 * t.A.Y + l2 * t.B.Y + l3 * t.C.Y;
                if (best is null || Math.Abs(h - y) < Math.Abs(best.Value - y)) best = h;
            }
            return best;
        }
        var parts = new List<string>();
        for (var ti = 0; ti < map.Tracks.Count; ti++)
        {
            var rings = ringsOf[ti];
            var track = map.Tracks[ti];
            if (rings.Count < 2) continue;
            var held = new int[2];
            for (var i = 0; i < rings.Count; i++)
            {
                var r = rings[i];
                var seg = track.Segments[Math.Clamp(r.Segment, 0, track.Segments.Count - 1)];
                for (var sd = 0; sd < 2; sd++)
                {
                    var side = sd == 0 ? -1 : 1;
                    var c = RoadMeshBuilder.EdgeContact(r, seg, side);
                    var o = new Vector2((float)r.Right.X, (float)r.Right.Z);
                    if (o.LengthSquared() < 1e-6f) continue;
                    o = Vector2.Normalize(o) * side;
                    var h = HeightsNear((float)c.X + o.X * 0.3f, (float)c.Z + o.Y * 0.3f, (float)c.Y);
                    if (h is { } y && y - c.Y > -0.15 && y - c.Y < 0.1) held[sd]++;
                }
            }
            parts.Add($"t{ti} {100 * held[0] / rings.Count}/{100 * held[1] / rings.Count}%");
        }
        Console.WriteLine($"  held edges (left/right): {string.Join(", ", parts)}");
    }

    static List<Vector3> Topology(TerrainGrid grid, List<Tri> land, List<Tri> road)
    {
        var ids = new Dictionary<(long, long, long), int>();
        var pos = new List<Vector3>();
        int Id(Vector3 p)
        {
            var k = ((long)Math.Round(p.X * 500), (long)Math.Round(p.Y * 500), (long)Math.Round(p.Z * 500));
            if (ids.TryGetValue(k, out var i)) return i;
            ids[k] = pos.Count;
            pos.Add(p);
            return pos.Count - 1;
        }
        var use = new Dictionary<(int, int), int>();
        foreach (var t in land)
        {
            int a = Id(t.A), b = Id(t.B), c = Id(t.C);
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = u < v ? (u, v) : (v, u);
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        }
        // Road points in 0.5 m cubes (and their neighbours): a land edge there ends at the road.
        var near = new HashSet<(int, int, int)>();
        foreach (var t in road)
            foreach (var p in Samples(t, 0.4f))
                near.Add(((int)Math.Floor(p.X / 0.5f), (int)Math.Floor(p.Y / 0.5f), (int)Math.Floor(p.Z / 0.5f)));
        bool AtRoad(Vector3 p)
        {
            int x = (int)Math.Floor(p.X / 0.5f), y = (int)Math.Floor(p.Y / 0.5f), z = (int)Math.Floor(p.Z / 0.5f);
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++) if (near.Contains((x + dx, y + dy, z + dz))) return true;
            return false;
        }
        bool AtOutline(Vector3 p)
        {
            for (var dx = -3f; dx <= 3f; dx += 1.5f)
                for (var dz = -3f; dz <= 3f; dz += 1.5f)
                {
                    var fx = (p.X + dx - grid.X0) / grid.Cell;
                    var fz = (p.Z + dz - grid.Z0) / grid.Cell;
                    int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
                    if (double.IsNaN(grid.Height(c, r)) || double.IsNaN(grid.Height(c + 1, r)) || double.IsNaN(grid.Height(c, r + 1)) || double.IsNaN(grid.Height(c + 1, r + 1))) return true;
                }
            return false;
        }
        int nonManifold = use.Values.Count(v => v > 2), boundary = 0, open = 0;
        var openLength = 0f;
        var examples = new List<string>();
        var openAt = new List<Vector3>();
        foreach (var ((a, b), count) in use)
        {
            if (count != 1) continue;
            boundary++;
            var m = (pos[a] + pos[b]) / 2;
            if (AtRoad(m) || AtOutline(m)) continue;
            open++;
            openLength += (pos[a] - pos[b]).Length();
            openAt.Add(m);
            if (examples.Count < 6) examples.Add($"({m.X:F0}, {m.Y:F0}, {m.Z:F0})");
        }
        Console.WriteLine($"  mesh: {nonManifold} non-manifold edges; {boundary} boundary edges, {open} open (not at a road or the outline, {openLength:F0} m) e.g. {string.Join(" ", examples)}");
        if (Environment.GetEnvironmentVariable("CHECK_OPEN") is { } co)
            foreach (var g in openAt.GroupBy(p => ((int)Math.Floor(p.X / 20), (int)Math.Floor(p.Z / 20))).OrderByDescending(g => g.Count()).Take(int.Parse(co)))
                Console.WriteLine($"    open near ({g.Key.Item1 * 20 + 10}, {g.Average(p => p.Y):F0}, {g.Key.Item2 * 20 + 10}): {g.Count()} edges, y {g.Min(p => p.Y):F0}-{g.Max(p => p.Y):F0}");
        return openAt.GroupBy(p => ((int)Math.Floor(p.X / 30), (int)Math.Floor(p.Z / 30))).OrderByDescending(g => g.Count()).Take(6).Select(g => g.First()).ToList();
    }

    // ------------------------------------------------------------------ pictures

    static void Pictures(TrackMap map, List<Tri> land, List<Tri> road, string file, StitchedTerrainResult? st, List<(Vector3 P, int Track, float Along)> pokes, List<List<Ring>> ringsOf, List<Vector3> open)
    {
        var all = land.Concat(road).ToList();
        var buckets = Buckets(all, t => (t.A, t.B, t.C), 16);
        // Cells: rows of the test maps (tracks with 6 points per shape) per shape; any other track whole.
        var cells = new List<(string Label, Vector3 Centre, Vector3 Dir, float Half)>();
        for (var ti = 0; ti < map.Tracks.Count; ti++)
        {
            var pts = map.Tracks[ti].Points;
            if (pts.Count == 54 && ti == 0)
            {
                var p0 = pts[0].Position;
                var p1 = pts[1].Position;
                var d0 = new Vector3((float)(p1.X - p0.X), 0, (float)(p1.Z - p0.Z));
                cells.Add(($"t{ti} start", new Vector3((float)p0.X, (float)p0.Y, (float)p0.Z) + Vector3.Normalize(d0) * 4, Vector3.Normalize(d0), 18));
            }
            if (pts.Count == 54)
                for (var c = 0; c < 9; c++)
                {
                    var a = pts[6 * c + 2].Position;
                    var b = pts[6 * c + 3].Position;
                    var centre = new Vector3((float)(a.X + b.X) / 2, (float)(a.Y + b.Y) / 2, (float)(a.Z + b.Z) / 2);
                    var dir = new Vector3((float)(b.X - a.X), 0, (float)(b.Z - a.Z));
                    cells.Add(($"t{ti} c{c}", centre, dir.LengthSquared() > 0 ? Vector3.Normalize(dir) : Vector3.UnitX, 45));
                }
            else
            {
                float x0 = (float)pts.Min(p => p.Position.X), x1 = (float)pts.Max(p => p.Position.X), z0 = (float)pts.Min(p => p.Position.Z), z1 = (float)pts.Max(p => p.Position.Z);
                var centre = new Vector3((x0 + x1) / 2, (float)pts.Average(p => p.Position.Y), (z0 + z1) / 2);
                cells.Add(($"t{ti}", centre, x1 - x0 >= z1 - z0 ? Vector3.UnitX : Vector3.UnitZ, Math.Max(40, Math.Max(x1 - x0, z1 - z0) / 2 + 15)));
            }
        }
        foreach (var (p, track, along) in pokes)
        {
            var r = ringsOf[track].MinBy(r => Math.Abs(r.Distance - along))!;
            var dir = new Vector3((float)r.Tangent.X, 0, (float)r.Tangent.Z);
            cells.Add(($"poke t{track} {along:F0}", p, dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitX, 14));
        }
        foreach (var p in open) cells.Add(($"open ({p.X:F0}, {p.Y:F0}, {p.Z:F0})", p, Vector3.UnitX, 14));
        const int TW = 320, VH = 220, SH = 120, Per = 9;
        var rowsOut = (cells.Count + Per - 1) / Per;
        using var sheet = new SKBitmap(TW * Math.Min(Per, cells.Count), rowsOut * (VH + SH));
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(235, 235, 235));
        using var font = new SKFont(SKTypeface.Default, 13);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (var k = 0; k < cells.Count; k++)
        {
            var (label, centre, dir, half) = cells[k];
            var tris = new HashSet<int>();
            for (var x = (int)Math.Floor((centre.X - half) / 16); x <= (int)Math.Floor((centre.X + half) / 16); x++)
                for (var z = (int)Math.Floor((centre.Z - half) / 16); z <= (int)Math.Floor((centre.Z + half) / 16); z++)
                    if (buckets.TryGetValue((x, z), out var l)) tris.UnionWith(l);
            var list = tris.Select(i => all[i]).ToList();
            var view = Render(list, centre, half, TW, VH);
            var near = st?.Stations.MinBy(s => MathF.Abs(Vector3.Dot(s.Section[0] - centre, dir)) + 0.2f * new Vector2(s.Section[0].X - centre.X, s.Section[0].Z - centre.Z).Length());
            var section = Section(list, centre, dir, half, TW, SH, near);
            if (near is { } nn) label += $" a {nn.HeldLeft:F1}/{nn.HeldRight:F1}";
            int ox = k % Per * TW, oy = k / Per * (VH + SH);
            canvas.DrawBitmap(view, ox, oy);
            canvas.DrawBitmap(section, ox, oy + VH);
            canvas.DrawText(label, ox + 4, oy + 14, font, ink);
            view.Dispose();
            section.Dispose();
        }
        using var data = sheet.Encode(SKEncodedImageFormat.Png, 90);
        using var f = File.Create(file);
        data.SaveTo(f);
        Console.WriteLine($"  picture: {file}");
    }

    static SKBitmap Render(List<Tri> tris, Vector3 centre, float half, int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        var px = new byte[w * h * 4];
        var zb = new float[w * h];
        Array.Fill(zb, float.MaxValue);
        for (var i = 0; i < w * h; i++) { px[4 * i] = 245; px[4 * i + 1] = 245; px[4 * i + 2] = 245; px[4 * i + 3] = 255; }
        var fw = Vector3.Normalize(new Vector3(0.45f, -0.6f, 0.65f));
        var rt = Vector3.Normalize(Vector3.Cross(fw, Vector3.UnitY));
        var up = Vector3.Cross(rt, fw);
        var sc = w / (2.2f * half);
        var light = Vector3.Normalize(new Vector3(-0.3f, 0.8f, -0.5f));
        Vector3 Pr(Vector3 p) { var d = p - centre; return new(w / 2f + Vector3.Dot(d, rt) * sc, h / 2f - Vector3.Dot(d, up) * sc, Vector3.Dot(d, fw)); }
        foreach (var t in tris)
        {
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            var front = Vector3.Dot(n, fw) < 0;
            var shade = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(n, light));
            Vector3 col = t.Kind switch { 2 => new(215, 125, 70), 1 => new(135, 135, 145), _ => new(105, 170, 85) };
            if (!front && t.Kind != 2) col = new(220, 40, 200);
            col *= shade;
            var A = Pr(t.A); var B = Pr(t.B); var C = Pr(t.C);
            int x0 = (int)Math.Max(0, Math.Min(A.X, Math.Min(B.X, C.X))), x1 = (int)Math.Min(w - 1, Math.Max(A.X, Math.Max(B.X, C.X)) + 1);
            int y0 = (int)Math.Max(0, Math.Min(A.Y, Math.Min(B.Y, C.Y))), y1 = (int)Math.Min(h - 1, Math.Max(A.Y, Math.Max(B.Y, C.Y)) + 1);
            var dn = (B.Y - C.Y) * (A.X - C.X) + (C.X - B.X) * (A.Y - C.Y);
            if (Math.Abs(dn) < 1e-6f) continue;
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var u = ((B.Y - C.Y) * (x - C.X) + (C.X - B.X) * (y - C.Y)) / dn;
                    var v = ((C.Y - A.Y) * (x - C.X) + (A.X - C.X) * (y - C.Y)) / dn;
                    var ww = 1 - u - v;
                    if (u < -0.01f || v < -0.01f || ww < -0.01f) continue;
                    var d = u * A.Z + v * B.Z + ww * C.Z - (t.Kind == 2 ? 0.05f : 0);
                    var i = y * w + x;
                    if (d >= zb[i]) continue;
                    zb[i] = d;
                    px[4 * i] = (byte)Math.Clamp(col.Z, 0, 255);
                    px[4 * i + 1] = (byte)Math.Clamp(col.Y, 0, 255);
                    px[4 * i + 2] = (byte)Math.Clamp(col.X, 0, 255);
                }
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    static SKBitmap Section(List<Tri> tris, Vector3 centre, Vector3 dir, float half, int w, int h, (int Track, float Along, Vector3[] Section, float[] F, float HeldLeft, float HeldRight)? station)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(250, 250, 250));
        var across = new Vector3(-dir.Z, 0, dir.X);
        var segs = new List<(Vector2 P, Vector2 Q, int Kind)>();
        foreach (var t in tris)
        {
            float da = Vector3.Dot(t.A - centre, dir), db = Vector3.Dot(t.B - centre, dir), dc = Vector3.Dot(t.C - centre, dir);
            var pts = new List<Vector3>();
            void E(Vector3 u, float du, Vector3 v, float dv) { if ((du < 0) != (dv < 0)) pts.Add(Vector3.Lerp(u, v, du / (du - dv))); }
            E(t.A, da, t.B, db); E(t.B, db, t.C, dc); E(t.C, dc, t.A, da);
            if (pts.Count == 2) segs.Add((new(Vector3.Dot(pts[0] - centre, across), pts[0].Y), new(Vector3.Dot(pts[1] - centre, across), pts[1].Y), t.Kind));
        }
        var span = Math.Min(half, 40);
        var sc = w / (2 * span);
        float ymid = centre.Y;
        var colors = new[] { new SKColor(30, 120, 40), new SKColor(60, 60, 200), new SKColor(220, 90, 30) };
        foreach (var (p, q, kind) in segs.OrderBy(s => s.Kind == 2 ? 1 : 0))
        {
            using var paint = new SKPaint { Color = colors[kind], StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawLine(w / 2f + p.X * sc, h / 2f - (p.Y - ymid) * sc, w / 2f + q.X * sc, h / 2f - (q.Y - ymid) * sc, paint);
        }
        if (station is { } s)
        {
            using var room = new SKPaint { Color = new SKColor(120, 120, 120), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            for (var i = 0; i <= s.Section.Length; i++)
            {
                var p = s.Section[i % s.Section.Length];
                float x = w / 2f + Vector3.Dot(p - centre, across) * sc, y = h / 2f - (p.Y - ymid) * sc;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, room);
            for (var i = 0; i < s.Section.Length; i++)
            {
                var p = s.Section[i];
                using var dot = new SKPaint { Color = s.F[i] > 0 ? SKColors.Red : new SKColor(150, 150, 150), IsAntialias = true };
                canvas.DrawCircle(w / 2f + Vector3.Dot(p - centre, across) * sc, h / 2f - (p.Y - ymid) * sc, i == 0 || i == 26 ? 3 : 1.5f, dot);
            }
        }
        using var frame = new SKPaint { Color = new SKColor(180, 180, 180), Style = SKPaintStyle.Stroke };
        canvas.DrawRect(0, 0, w - 1, h - 1, frame);
        return bmp;
    }
}
