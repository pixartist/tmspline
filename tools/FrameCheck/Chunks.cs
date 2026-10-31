using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

// Prints each chunk's item frame and the bounding box of its local vertex positions.
static class Chunks
{
    public static void Run(string path, int boostSegment)
    {
        var track = TrackJson.Parse(File.ReadAllText(path));
        track.Segments[boostSegment].Effect = Effect.Turbo;
        var chunks = RoadMeshBuilder.Build(track, TrackSampler.Sample(track), Vec3d.Zero, "C", new RoadMeshSettings());
        foreach (var c in chunks.Where(c => c.Effect != Effect.None).Take(3))
        {
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var p in c.Positions) { min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p); }
            Console.WriteLine($"{c.Name}: X={c.Frame.X} Y={c.Frame.Y} Z={c.Frame.Z} dir={c.EffectDirection}\n   local bbox min={min} max={max}");
        }
    }
}
