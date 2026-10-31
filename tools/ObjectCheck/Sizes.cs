using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Per exported mesh: name, triangles, and vertices/triangles per material link (for calibrating the size estimate).
static class Sizes
{
    public static void Run(string trackPath, MeshQuality quality)
    {
        var track = TrackJson.Parse(File.ReadAllText(trackPath));
        var (_, meshes) = MeshQualities.BuildExport(track, quality, 64);
        if (Environment.GetEnvironmentVariable("ESTIMATE_ONLY") is not null) { Console.WriteLine(MeshQualities.EstimateMapBytes(meshes)); return; }
        foreach (var m in meshes)
        {
            var groups = m.Triangles.GroupBy(t => m.Materials[t.Material].Link)
                .Select(g => $"{g.Key}:{g.Count()}:{g.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Count()}");
            Console.WriteLine($"{m.Name}\t{string.Join(" ", groups)}\tnc:{m.NotCollidable.Sum(c => c.Triangles.Count)}");
        }
    }
}
