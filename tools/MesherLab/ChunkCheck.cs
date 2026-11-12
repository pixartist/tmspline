using System.Diagnostics;
using System.Numerics;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

/// <summary>
/// Whether chunks of the voxel terrain built on their own (TerrainMeshBuilder.BuildChunks, as after a dab of the
/// brush) come out as in the whole: the same triangles, so they meet the chunks beside them. And how long that takes.
/// Usage: MesherLab --chunks map.json
/// </summary>
static class ChunkCheck
{
    public static void Run(string mapFile)
    {
        var map = TrackJson.ParseMap(File.ReadAllText(mapFile));
        var grid = map.Terrain ?? throw new InvalidOperationException("no terrain");
        var sw = Stopwatch.StartNew();
        var roads = VoxelTerrain.Prepare(grid, map);
        Console.WriteLine($"prepare {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var whole = TerrainMeshBuilder.Build(grid, map);
        Console.WriteLine($"whole {sw.ElapsedMilliseconds} ms, {whole.Count} chunks, {whole.Sum(c => c.Triangles.Count)} triangles");
        var byKey = whole.ToDictionary(c => TerrainMeshBuilder.ChunkOf(grid, c.ItemOffset.X + 64, c.ItemOffset.Z + 64));
        // The fullest chunks, each alone, and two of them together.
        var keys = byKey.OrderByDescending(kv => kv.Value.Triangles.Count).Take(6).Select(kv => kv.Key).ToList();
        var sets = keys.Select(k => new HashSet<(int X, int Z)> { k }).Append([keys[0], keys[1]]).ToList();
        foreach (var set in sets)
        {
            sw.Restart();
            var part = TerrainMeshBuilder.BuildChunks(grid, map, roads, set);
            var ms = sw.ElapsedMilliseconds;
            foreach (var c in part)
            {
                var key = TerrainMeshBuilder.ChunkOf(grid, c.ItemOffset.X + 64, c.ItemOffset.Z + 64);
                var want = Triangles(byKey[key]);
                var got = Triangles(c);
                var missing = want.Count(t => !got.Contains(t));
                var extra = got.Count(t => !want.Contains(t));
                Console.WriteLine($"chunk {key}: {ms} ms for {set.Count}, {got.Count} triangles, {missing} of the whole's missing, {extra} not in the whole");
            }
        }
    }

    /// <summary>A chunk's triangles by their corners' places and normals (rounded), corners in a fixed order.</summary>
    private static HashSet<string> Triangles(MeshChunk c)
    {
        var set = new HashSet<string>();
        string Corner(int i) => $"{MathF.Round(c.Positions[i].X, 2)},{MathF.Round(c.Positions[i].Y, 2)},{MathF.Round(c.Positions[i].Z, 2)}/{MathF.Round(c.Normals[i].X, 1)},{MathF.Round(c.Normals[i].Y, 1)},{MathF.Round(c.Normals[i].Z, 1)}";
        foreach (var t in c.Triangles)
        {
            var corners = new[] { Corner(t.A), Corner(t.B), Corner(t.C) };
            Array.Sort(corners, StringComparer.Ordinal);
            set.Add(string.Join("|", corners) + "#" + (c.Materials[t.Material].GamePath is null ? 0 : 1));
        }
        return set;
    }
}
