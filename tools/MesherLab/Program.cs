// MesherLab: the terrain (VoxelTerrain, cut by BooleanSeam) on a map file, for checks from the command line.
// Usage: MesherLab --size map.json | --chunkprobe map.json "x;z|x;z" | --chunks map.json
// (Its earlier modes, the adaptive and stitched meshers' synthetic scene, map pictures and seam loops, went with those meshers.)
using TmSpline.Core.Geometry;

if (args.Length >= 2 && args[0] == "--chunks") { ChunkCheck.Run(args[1]); return; }
if (args.Length >= 2 && args[0] == "--size")
{
    // The land's mesh as the settings in the environment build it: triangles, vertices, and the estimated size.
    var sizeMap = TmSpline.Core.Model.TrackJson.ParseMap(File.ReadAllText(args[1]));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var land = TmSpline.Core.Geometry.VoxelTerrain.Build(sizeMap.Terrain!, sizeMap);
    var used = land.Indices.Distinct().Count();
    Console.WriteLine($"{land.Indices.Count / 3} triangles, {used} vertices, about {land.Indices.Count / 3 * 24.3 / 1024:0} KB, built in {watch.Elapsed.TotalSeconds:0.0} s");
    return;
}
if (args.Length >= 3 && args[0] == "--chunkprobe")
{
    // The land as the editor gets it (TerrainMeshBuilder.Build: chunks), its heights at plan points "x;z|x;z".
    var probeMap = TmSpline.Core.Model.TrackJson.ParseMap(File.ReadAllText(args[1]));
    var chunks = TmSpline.Core.Geometry.TerrainMeshBuilder.Build(probeMap.Terrain!, probeMap);
    foreach (var probe in args[2].Split('|'))
    {
        var c = probe.Split(';').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var hits = new List<string>();
        foreach (var ch in chunks)
        {
            var o = ch.ItemOffset;
            foreach (var t in ch.Triangles)
            {
                System.Numerics.Vector3 a = ch.Positions[t.A] + new System.Numerics.Vector3((float)o.X, (float)o.Y, (float)o.Z), b = ch.Positions[t.B] + new System.Numerics.Vector3((float)o.X, (float)o.Y, (float)o.Z), cc = ch.Positions[t.C] + new System.Numerics.Vector3((float)o.X, (float)o.Y, (float)o.Z);
                var d = (b.Z - cc.Z) * (a.X - cc.X) + (cc.X - b.X) * (a.Z - cc.Z);
                if (MathF.Abs(d) < 1e-9f) continue;
                var u = ((b.Z - cc.Z) * (c[0] - cc.X) + (cc.X - b.X) * (c[1] - cc.Z)) / d;
                var v = ((cc.Z - a.Z) * (c[0] - cc.X) + (a.X - cc.X) * (c[1] - cc.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                hits.Add($"{a.Y * u + b.Y * v + cc.Y * (1 - u - v):0.00} ({ch.Name})");
            }
        }
        Console.WriteLine($"{c[0]},{c[1]}: " + string.Join(", ", hits));
    }
    return;
}
Console.WriteLine("Usage: MesherLab --size map.json | --chunkprobe map.json \"x;z|x;z\" | --chunks map.json");
