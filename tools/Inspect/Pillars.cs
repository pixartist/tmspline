using GBX.NET;
using GBX.NET.Engines.Game;

// How maps saved by the game store the columns under air blocks: pillar blocks (names, flags, coords) next to the
// blocks above them. For replicating the editor's automatic pillars.
static class Pillars
{
    public static void Run(IEnumerable<string> paths)
    {
        var shown = 0;
        foreach (var path in paths)
        {
            CGameCtnChallenge map;
            try { map = Gbx.ParseNode<CGameCtnChallenge>(path); }
            catch { continue; }
            var blocks = (map.GetBlocks() ?? []).ToList();
            var pillars = blocks.Where(b => b.IsPillar || b.Name.Contains("Pillar")).ToList();
            if (pillars.Count == 0) continue;
            Console.WriteLine($"{Path.GetFileName(path)}: {blocks.Count} blocks, {pillars.Count} pillars");
            foreach (var g in pillars.GroupBy(p => p.Name).OrderByDescending(g => g.Count()).Take(8))
                Console.WriteLine($"  {g.Count(),4} x {g.Key}  e.g. coord {g.First().Coord} dir {g.First().Direction} flags 0x{g.First().Flags:X8} pillarFlag {g.First().IsPillar} var {g.First().Variant}/{g.First().SubVariant}");
            // One column: the pillars under the first air block that has any.
            var sample = pillars.First();
            var above = blocks.Where(b => !b.IsPillar && !b.Name.Contains("Pillar") && b.Coord.X == sample.Coord.X && b.Coord.Z == sample.Coord.Z)
                .OrderBy(b => b.Coord.Y).ToList();
            var column = pillars.Where(p => p.Coord.X == sample.Coord.X && p.Coord.Z == sample.Coord.Z).OrderBy(p => p.Coord.Y).ToList();
            Console.WriteLine($"  column at {sample.Coord.X},{sample.Coord.Z}: pillars at Y {string.Join(",", column.Select(p => $"{p.Coord.Y}:{p.Name}"))}; blocks above: {string.Join(", ", above.Select(b => $"{b.Coord.Y}:{b.Name}"))}");
            foreach (var p in column) Console.WriteLine($"    Y {p.Coord.Y}: {p.Name} dir {p.Direction} flags 0x{p.Flags:X8} var {p.Variant}/{p.SubVariant} ground {p.IsGround}");
            foreach (var b in above) Console.WriteLine($"    above Y {b.Coord.Y}: {b.Name} dir {b.Direction} flags 0x{b.Flags:X8} var {b.Variant}/{b.SubVariant} ground {b.IsGround}");
            if (++shown >= 5) break;
        }
        if (shown == 0) Console.WriteLine("No pillars found");
    }
}
