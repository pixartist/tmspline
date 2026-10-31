using GBX.NET;
using GBX.NET.Engines.Game;

// Variant patterns of pillar columns (bottom to top) per pillar block, over many maps.
static class PillarPatterns
{
    public static void Run(IEnumerable<string> paths)
    {
        var patterns = new Dictionary<string, Dictionary<string, int>>();
        var under = new Dictionary<string, HashSet<string>>();
        foreach (var path in paths)
        {
            CGameCtnChallenge map;
            try { map = Gbx.ParseNode<CGameCtnChallenge>(path); } catch { continue; }
            var blocks = (map.GetBlocks() ?? []).ToList();
            foreach (var column in blocks.Where(b => b.IsPillar).GroupBy(b => (b.Coord.X, b.Coord.Z, b.Name)))
            {
                var list = column.OrderBy(b => b.Coord.Y).ToList();
                // split into runs of consecutive levels
                var runs = new List<List<CGameCtnBlock>> { new() { list[0] } };
                foreach (var b in list.Skip(1))
                    if (b.Coord.Y == runs[^1][^1].Coord.Y + 1) runs[^1].Add(b); else runs.Add(new() { b });
                foreach (var run in runs)
                {
                    var key = $"bottomY={(run[0].Coord.Y == 9 ? "ground" : "air")} " + string.Join(" ", run.Select(b => $"{b.Variant}{(b.IsGround ? "g" : "")}"));
                    if (!patterns.TryGetValue(column.Key.Name, out var d)) patterns[column.Key.Name] = d = new();
                    d[key] = d.GetValueOrDefault(key) + 1;
                    var top = run[^1].Coord.Y + 1;
                    var aboveBlock = blocks.FirstOrDefault(b => !b.IsPillar && b.Coord.X == column.Key.X && b.Coord.Z == column.Key.Z && b.Coord.Y == top);
                    if (!under.TryGetValue(column.Key.Name, out var u)) under[column.Key.Name] = u = new();
                    u.Add(aboveBlock?.Name ?? "(none)");
                }
            }
        }
        foreach (var (name, d) in patterns.OrderByDescending(p => p.Value.Values.Sum()).Take(12))
        {
            Console.WriteLine($"{name}: under {string.Join(", ", under[name].Take(6))}");
            foreach (var (k, n) in d.OrderByDescending(x => x.Value).Take(6)) Console.WriteLine($"   {n,4} x {k}");
        }
    }
}
