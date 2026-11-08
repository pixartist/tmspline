// MesherLab: the adaptive terrain mesher (AdaptiveTerrainMesher) on a synthetic scene, with checks and a picture.
// Usage: MesherLab <out.png>
// Scene (160 m square, 0.5 m cells): hills; a banked road on a three-quarter circle with its edges and both open ends
// pinned and its surface cut out; a straight road at grade crossing it twice (its pins cross the circle's: refused).
using SkiaSharp;
using TmSpline.Core.Geometry;

if (args.Length >= 3 && args[0] == "--map") { MapMode.Run(args[1], args[2]); return; }
var output = args.Length > 0 ? args[0] : "mesher.png";
const double Size = 160, Cell = 0.5;
int n = (int)(Size / Cell);
double Hills(double x, double z) => 6 + 5 * Math.Sin(x / 23) * Math.Cos(z / 31) + 2 * Math.Sin((x + z) / 9);

// The circle road: centre (80, 80), radius 45, 12 m wide, from 200 to 470 degrees; banked (outer edge 3 m up), rising.
const double Cx = 80, Cz = 80, Radius = 45, Half = 6, From = 200, To = 470;
double RoadY(double deg) => 8 + 6 * (deg - From) / (To - From);
Vec3d EdgePoint(double deg, int side)
{
    var a = deg * Math.PI / 180;
    var r = Radius + side * Half;
    return new Vec3d(Cx + r * Math.Cos(a), RoadY(deg) + (side > 0 ? 3 : 0), Cz + r * Math.Sin(a));
}
var pins = new List<TerrainPin>();
foreach (var side in new[] { -1, 1 })
    pins.Add(new TerrainPin(Enumerable.Range(0, 271).Select(k => EdgePoint(From + k, side)).ToList()));
pins.Add(new TerrainPin([EdgePoint(From, -1), EdgePoint(From, 1)]));
pins.Add(new TerrainPin([EdgePoint(To, -1), EdgePoint(To, 1)]));
// The straight road at grade along z = 80 (8 m wide, height 9): its edges, crossing the circle's.
foreach (var side in new[] { -1, 1 })
    pins.Add(new TerrainPin([new Vec3d(4, 9, 80 + side * 4), new Vec3d(156, 9, 80 + side * 4)]));

bool OnCircle(double x, double z, double margin)
{
    var d = Math.Sqrt((x - Cx) * (x - Cx) + (z - Cz) * (z - Cz));
    var deg = Math.Atan2(z - Cz, x - Cx) * 180 / Math.PI;
    while (deg < From) deg += 360;
    return Math.Abs(d - Radius) < Half - margin && deg > From + margin / Radius * 57.3 && deg < To - margin / Radius * 57.3;
}
bool OnStraight(double x, double z, double margin) => Math.Abs(z - 80) < 4 - margin && x > 4 + margin && x < 156 - margin;

// The land: hills, blended to the circle's edges over 16 m (as a solve would), a hole in one corner (no land).
var heights = new float[n * n];
var fidelity = new float[n * n];
var cut = new bool[n * n];
for (var r = 0; r < n; r++)
    for (var c = 0; c < n; c++)
    {
        double x = (c + 0.5) * Cell, z = (r + 0.5) * Cell;
        var i = r * n + c;
        // The nearest point of each road (its band, ends included): the land blends to its surface height there,
        // the fidelity falls with the distance to it.
        var d = Math.Sqrt((x - Cx) * (x - Cx) + (z - Cz) * (z - Cz));
        var deg = Math.Atan2(z - Cz, x - Cx) * 180 / Math.PI;
        while (deg < From) deg += 360;
        if (deg > To) deg = deg - To < From + 360 - deg ? To : From;
        var off = Math.Clamp(d - Radius, -Half, Half);
        var rad = deg * Math.PI / 180;
        double nx = Cx + (Radius + off) * Math.Cos(rad), nz = Cz + (Radius + off) * Math.Sin(rad);
        var dCircle = Math.Sqrt((x - nx) * (x - nx) + (z - nz) * (z - nz));
        var circleY = RoadY(deg) + 3 * (off + Half) / (2 * Half);
        double sx = Math.Clamp(x, 4, 156), sz = Math.Clamp(z, 76, 84);
        var dStraight = Math.Sqrt((x - sx) * (x - sx) + (z - sz) * (z - sz));
        var h = Hills(x, z);
        var wc = Math.Clamp(dCircle / 16, 0, 1); wc = wc * wc * (3 - 2 * wc);
        h = circleY * (1 - wc) + h * wc;
        var ws = Math.Clamp(dStraight / 10, 0, 1); ws = ws * ws * (3 - 2 * ws);
        h = 9 * (1 - ws) + h * ws;
        var near = Math.Min(dCircle, dStraight);
        heights[i] = x > 130 && z > 130 ? float.NaN : (float)h;
        cut[i] = OnCircle(x, z, 0) || OnStraight(x, z, 0);
        fidelity[i] = (float)Math.Max(0.06, Math.Exp(-Math.Max(0, near) / 10));
    }
var field = new TerrainField(0, 0, Cell, n, n, heights, fidelity, cut);

var sw = System.Diagnostics.Stopwatch.StartNew();
var mesh = AdaptiveTerrainMesher.Build(field, pins);
sw.Stop();
var triangles = mesh.Indices.Count / 3;
Console.WriteLine($"{mesh.Positions.Count} vertices, {triangles} triangles, {mesh.Rounds} rounds, worst error {mesh.WorstError:F2} tolerances, {mesh.RefusedSegments} pin segments refused, {sw.ElapsedMilliseconds} ms");

// Checks. 1: every pin point of the circle is a vertex at its exact height.
var vertexAt = new Dictionary<(long, long), System.Numerics.Vector3>();
foreach (var p in mesh.Positions) vertexAt[((long)Math.Round(p.X * 100), (long)Math.Round(p.Z * 100))] = p;
int missing = 0, wrongHeight = 0;
foreach (var pin in pins.Take(4))
    foreach (var p in pin.Points)
    {
        if (!vertexAt.TryGetValue(((long)Math.Round(p.X * 100), (long)Math.Round(p.Z * 100)), out var v)) { missing++; continue; }
        if (Math.Abs(v.Y - p.Y) > 1e-3) wrongHeight++;
    }
// 2: the circle's edges are mesh edges (between consecutive pin points away from the crossings).
var edges = new HashSet<(int, int)>();
for (var t = 0; t < mesh.Indices.Count; t += 3)
    for (var k = 0; k < 3; k++)
    {
        int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
        edges.Add((Math.Min(a, b), Math.Max(a, b)));
    }
var index = new Dictionary<(long, long), int>();
for (var i = 0; i < mesh.Positions.Count; i++) index[((long)Math.Round(mesh.Positions[i].X * 100), (long)Math.Round(mesh.Positions[i].Z * 100))] = i;
int pinEdges = 0, pinEdgesMissing = 0;
foreach (var pin in pins.Take(2))
    for (var k = 0; k + 1 < pin.Points.Count; k++)
    {
        Vec3d a = pin.Points[k], b = pin.Points[k + 1];
        if (Math.Abs((a.Z + b.Z) / 2 - 80) < 8) continue; // near the crossings
        if (!index.TryGetValue(((long)Math.Round(a.X * 100), (long)Math.Round(a.Z * 100)), out var ia) ||
            !index.TryGetValue(((long)Math.Round(b.X * 100), (long)Math.Round(b.Z * 100)), out var ib)) { pinEdgesMissing++; if (pinEdgesMissing <= 8) Console.WriteLine($"  no vertex: ({a.X:F1}, {a.Z:F1}) - ({b.X:F1}, {b.Z:F1})"); continue; }
        pinEdges++;
        if (!edges.Contains((Math.Min(ia, ib), Math.Max(ia, ib)))) { pinEdgesMissing++; if (pinEdgesMissing <= 8) Console.WriteLine($"  not an edge: ({a.X:F1}, {a.Z:F1}) - ({b.X:F1}, {b.Z:F1})"); }
    }
// 3: no triangle reaches onto a road (points inside each triangle, 5 cm margin).
var onRoad = 0;
for (var t = 0; t < mesh.Indices.Count; t += 3)
{
    var A = mesh.Positions[mesh.Indices[t]]; var B = mesh.Positions[mesh.Indices[t + 1]]; var C = mesh.Positions[mesh.Indices[t + 2]];
    var hit = false;
    for (var u = 1; u < 6 && !hit; u++)
        for (var v = 1; u + v < 6 && !hit; v++)
        {
            double w = 1 - u / 6.0 - v / 6.0;
            double x = A.X * w + B.X * u / 6.0 + C.X * v / 6.0, z = A.Z * w + B.Z * u / 6.0 + C.Z * v / 6.0;
            if (OnCircle(x, z, 0.05) || OnStraight(x, z, 0.05)) hit = true;
        }
    if (hit) onRoad++;
}
// 4: no triangle's side crosses a pin segment of the circle (the pins are edges of the mesh, maybe split).
static double Orient(double ax, double az, double bx, double bz, double px, double pz) => (bx - ax) * (pz - az) - (bz - az) * (px - ax);
var crossings = 0;
foreach (var pin in pins.Take(2))
    for (var k = 0; k + 1 < pin.Points.Count; k++)
    {
        Vec3d p = pin.Points[k], q = pin.Points[k + 1];
        foreach (var (a, b) in edges)
        {
            var A = mesh.Positions[a]; var B = mesh.Positions[b];
            double o1 = Orient(p.X, p.Z, q.X, q.Z, A.X, A.Z), o2 = Orient(p.X, p.Z, q.X, q.Z, B.X, B.Z);
            double o3 = Orient(A.X, A.Z, B.X, B.Z, p.X, p.Z), o4 = Orient(A.X, A.Z, B.X, B.Z, q.X, q.Z);
            if (Math.Min(Math.Min(Math.Abs(o1), Math.Abs(o2)), Math.Min(Math.Abs(o3), Math.Abs(o4))) > 1e-3 && o1 * o2 < 0 && o3 * o4 < 0 && Math.Abs((p.Z + q.Z) / 2 - 80) >= 8) { crossings++; if (crossings <= 6) Console.WriteLine($"  crossing: pin ({p.X:F2}, {p.Z:F2})-({q.X:F2}, {q.Z:F2}) edge ({A.X:F2}, {A.Z:F2})-({B.X:F2}, {B.Z:F2})"); }
        }
    }
Console.WriteLine($"circle pin points missing {missing}, wrong height {wrongHeight}; circle edge segments {pinEdges}, not whole mesh edges {pinEdgesMissing}; mesh edges crossing a circle pin {crossings}; triangles on a road {onRoad}");

// The picture: land shaded by height, triangle edges, pins red, cut cells blue.
const int Px = 1600;
var scale = Px / Size;
using var bitmap = new SKBitmap(Px, Px);
using var canvas = new SKCanvas(bitmap);
canvas.Clear(new SKColor(20, 20, 26));
for (var r = 0; r < n; r++)
    for (var c = 0; c < n; c++)
    {
        var i = r * n + c;
        SKColor col;
        if (cut[i]) col = new SKColor(40, 60, 130);
        else if (float.IsNaN(heights[i])) continue;
        else { var g = (byte)Math.Clamp(40 + heights[i] * 9, 0, 255); col = new SKColor((byte)(g / 2), g, (byte)(g / 3)); }
        using var paint = new SKPaint { Color = col };
        canvas.DrawRect((float)(c * Cell * scale), (float)(r * Cell * scale), (float)(Cell * scale) + 1, (float)(Cell * scale) + 1, paint);
    }
using (var line = new SKPaint { Color = new SKColor(255, 255, 255, 140), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke })
    for (var t = 0; t < mesh.Indices.Count; t += 3)
    {
        var path = new SKPath();
        var a = mesh.Positions[mesh.Indices[t]];
        path.MoveTo(a.X * (float)scale, a.Z * (float)scale);
        path.LineTo(mesh.Positions[mesh.Indices[t + 1]].X * (float)scale, mesh.Positions[mesh.Indices[t + 1]].Z * (float)scale);
        path.LineTo(mesh.Positions[mesh.Indices[t + 2]].X * (float)scale, mesh.Positions[mesh.Indices[t + 2]].Z * (float)scale);
        path.Close();
        canvas.DrawPath(path, line);
    }
using (var red = new SKPaint { Color = SKColors.Red, StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke })
    foreach (var pin in pins)
        for (var k = 0; k + 1 < pin.Points.Count; k++)
            canvas.DrawLine((float)(pin.Points[k].X * scale), (float)(pin.Points[k].Z * scale), (float)(pin.Points[k + 1].X * scale), (float)(pin.Points[k + 1].Z * scale), red);
using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 90))
using (var file = File.Create(output))
    data.SaveTo(file);
Console.WriteLine($"picture: {Path.GetFullPath(output)}");

static class MapMode
{
    public static void Run(string mapFile, string output)
    {
        var map = TmSpline.Core.Model.TrackJson.ParseMap(File.ReadAllText(mapFile));
        var grid = map.Terrain ?? throw new InvalidOperationException("The map has no terrain");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (field, pins) = AdaptiveTerrainBuilder.Fields(grid, map);
        var tFields = sw.ElapsedMilliseconds;
        var mesh = AdaptiveTerrainMesher.Build(field, pins);
        var tMesh = sw.ElapsedMilliseconds - tFields;
        sw.Restart();
        var chunks = TerrainMeshBuilder.Build(grid, map);
        var tAll = sw.ElapsedMilliseconds;
        Console.WriteLine($"field {field.Columns}x{field.Rows} cells of {field.Cell} m, {pins.Count} pins ({pins.Sum(p => p.Points.Count)} points), {field.Cut.Count(c => c)} cut cells: {tFields} ms");
        Console.WriteLine($"mesh {mesh.Positions.Count} vertices, {mesh.Indices.Count / 3} triangles, {mesh.Rounds} rounds, worst error {mesh.WorstError:F2}, {mesh.RefusedSegments} pin segments refused: {tMesh} ms");
        Console.WriteLine($"export build: {chunks.Count} chunks, {chunks.Sum(c => c.Triangles.Count)} triangles: {tAll} ms");
        // Lightmap UVs per chunk and material: NadeoImporter refuses a surface without area ("LightMap TexCoord Invalid Surface").
        static void Surfaces(string label, List<MeshChunk> built)
        {
            var rows = built.SelectMany(chunk => chunk.Triangles.GroupBy(t => t.Material).Select(g =>
            {
                double Area(Triangle t)
                {
                    System.Numerics.Vector2 p = chunk.Uv1[t.A], q = chunk.Uv1[t.B], r = chunk.Uv1[t.C];
                    return Math.Abs((double)(q.X - p.X) * (r.Y - p.Y) - (double)(q.Y - p.Y) * (r.X - p.X));
                }
                return (chunk.Name, Material: g.Key, Count: g.Count(), Total: g.Sum(Area));
            })).OrderBy(x => x.Total).ToList();
            var smallest = rows.Take(3).Select(x => $"{x.Name}/{x.Material}: {x.Count} tris, area {x.Total:E2}");
            Console.WriteLine($"  {label}: {built.Count} chunks, surfaces without area {rows.Count(x => x.Total == 0)}; smallest {string.Join("; ", smallest)}");
        }
        Surfaces($"rock {grid.RockAngle}", chunks);
        // Pin segments (between consecutive mesh vertices on a pin) with no triangle on them: a gap at the road's edge.
        var edgeUse = new Dictionary<(int, int), int>();
        for (var t = 0; t < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int u = mesh.Indices[t + k], v = mesh.Indices[t + (k + 1) % 3];
                var key = (Math.Min(u, v), Math.Max(u, v));
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        var pinnedVertices = Enumerable.Range(0, mesh.Positions.Count).Where(i => mesh.Pinned[i]).ToList();
        var byPos = pinnedVertices.ToDictionary(i => ((long)Math.Round(mesh.Positions[i].X * 200), (long)Math.Round(mesh.Positions[i].Z * 200)), i => i);
        int segments = 0, bare = 0;
        var examples = new List<string>();
        foreach (var pin in pins)
            for (var k = 0; k + 1 < pin.Points.Count; k++)
            {
                // Pins are split into PinSpacing pieces in the mesher: walk along this segment's pieces.
                Vec3d p0 = pin.Points[k], p1 = pin.Points[k + 1];
                var len = Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Z - p0.Z) * (p1.Z - p0.Z));
                var nPieces = Math.Max(1, (int)Math.Ceiling(len / 1.5));
                for (var j = 0; j < nPieces; j++)
                {
                    double ta = (double)j / nPieces, tb = (double)(j + 1) / nPieces;
                    var ka = ((long)Math.Round((p0.X + (p1.X - p0.X) * ta) * 200), (long)Math.Round((p0.Z + (p1.Z - p0.Z) * ta) * 200));
                    var kb = ((long)Math.Round((p0.X + (p1.X - p0.X) * tb) * 200), (long)Math.Round((p0.Z + (p1.Z - p0.Z) * tb) * 200));
                    if (!byPos.TryGetValue(ka, out var ia) || !byPos.TryGetValue(kb, out var ib)) continue;
                    segments++;
                    if (edgeUse.GetValueOrDefault((Math.Min(ia, ib), Math.Max(ia, ib))) == 0)
                    {
                        bare++;
                        if (examples.Count < 8) { var A = mesh.Positions[ia]; var B = mesh.Positions[ib]; double mx = (A.X + B.X) / 2, mz = (A.Z + B.Z) / 2, nx = -(B.Z - A.Z), nz = B.X - A.X; var nl = Math.Sqrt(nx * nx + nz * nz); nx /= nl; nz /= nl; string Side(double s) { double x = mx + nx * s, z = mz + nz * s; var k = field.CellAt(x, z); var covered = false; for (var t = 0; t < mesh.Indices.Count && !covered; t += 3) { var P = mesh.Positions[mesh.Indices[t]]; var Q = mesh.Positions[mesh.Indices[t + 1]]; var R = mesh.Positions[mesh.Indices[t + 2]]; double d1 = (Q.X - P.X) * (z - P.Z) - (Q.Z - P.Z) * (x - P.X), d2 = (R.X - Q.X) * (z - Q.Z) - (R.Z - Q.Z) * (x - Q.X), d3 = (P.X - R.X) * (z - R.Z) - (P.Z - R.Z) * (x - R.X); covered = (d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0); } return (field.IsCut(x, z) ? "cut" : "land") + (covered ? "+mesh" : "-NOMESH"); } examples.Add($"({A.X:F1}, {A.Z:F1}) y {A.Y:F1}: {Side(0.3)} | {Side(-0.3)}"); }
                    }
                }
            }
        Console.WriteLine($"pin pieces checked {segments}, with no land triangle on them {bare}: {string.Join(" ", examples)}");

        // Picture: 2 px per metre: land shaded, cut blue, triangle edges, pins red.
        const double Scale = 2;
        int w = (int)(field.Columns * field.Cell * Scale), h = (int)(field.Rows * field.Cell * Scale);
        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(20, 20, 26));
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in field.Heights) if (!float.IsNaN(v) && v > -1.5) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        for (var r = 0; r < field.Rows; r++)
            for (var c = 0; c < field.Columns; c++)
            {
                var i = r * field.Columns + c;
                var v = field.Heights[i];
                SKColor col;
                if (field.Cut[i]) col = new SKColor(40, 60, 130);
                else if (float.IsNaN(v)) continue;
                else if (v < -1) col = new SKColor(60, 60, 60);
                else { var g = (byte)Math.Clamp(30 + (v - lo) / (hi - lo) * 200, 0, 255); col = new SKColor((byte)(g / 2), g, (byte)(g / 3)); }
                bitmap.SetPixel((int)(c * field.Cell * Scale), (int)(r * field.Cell * Scale), col);
            }
        // Fill the gaps between the sampled pixels (one per cell at 1 px per cell).
        using (var line = new SKPaint { Color = new SKColor(255, 255, 255, 110), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke })
            for (var t = 0; t < mesh.Indices.Count; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    var a = mesh.Positions[mesh.Indices[t + k]]; var b = mesh.Positions[mesh.Indices[t + (k + 1) % 3]];
                    canvas.DrawLine((float)((a.X - field.X0) * Scale), (float)((a.Z - field.Z0) * Scale), (float)((b.X - field.X0) * Scale), (float)((b.Z - field.Z0) * Scale), line);
                }
        using (var red = new SKPaint { Color = SKColors.Red, StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke })
            foreach (var pin in pins)
                for (var k = 0; k + 1 < pin.Points.Count; k++)
                    canvas.DrawLine((float)((pin.Points[k].X - field.X0) * Scale), (float)((pin.Points[k].Z - field.Z0) * Scale),
                        (float)((pin.Points[k + 1].X - field.X0) * Scale), (float)((pin.Points[k + 1].Z - field.Z0) * Scale), red);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(output);
        data.SaveTo(file);
        Console.WriteLine($"picture: {Path.GetFullPath(output)}");
    }
}
