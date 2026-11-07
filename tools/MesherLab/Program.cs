// MesherLab: the adaptive terrain mesher (AdaptiveTerrainMesher) on a synthetic scene, with checks and a picture.
// Usage: MesherLab <out.png>
// Scene (160 m square, 0.5 m cells): hills; a banked road on a three-quarter circle with its edges and both open ends
// pinned and its surface cut out; a straight road at grade crossing it twice (its pins cross the circle's: refused).
using SkiaSharp;
using TmSpline.Core.Geometry;

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
        var d = Math.Sqrt((x - Cx) * (x - Cx) + (z - Cz) * (z - Cz));
        var deg = Math.Atan2(z - Cz, x - Cx) * 180 / Math.PI;
        while (deg < From) deg += 360;
        var dEdge = Math.Abs(d - Radius) - Half;
        var h = Hills(x, z);
        if (deg <= To && dEdge < 16)
        {
            var edgeY = RoadY(deg) + (d > Radius ? 3 : 0);
            var w = Math.Clamp(dEdge / 16, 0, 1);
            w = w * w * (3 - 2 * w);
            h = edgeY * (1 - w) + h * w;
        }
        var dStraight = Math.Abs(z - 80) - 4;
        if (x > 4 && x < 156 && dStraight < 10) { var w = Math.Clamp(dStraight / 10, 0, 1); h = 9 * (1 - w) + h * w; }
        heights[i] = x > 130 && z > 130 ? float.NaN : (float)h;
        cut[i] = OnCircle(x, z, 0) || OnStraight(x, z, 0);
        var near = Math.Min(Math.Abs(dEdge), Math.Abs(dStraight));
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
