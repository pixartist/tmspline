using GBX.NET;
using GBX.NET.Engines.Game;

// Lists free blocks (TM2020 free block mode) in maps: coord, absolute position, rotation, flags. For learning how the
// game stores them before writing our own.
static class FreeBlocks
{
    public static void Run(IEnumerable<string> paths)
    {
        var shown = 0;
        foreach (var path in paths)
        {
            CGameCtnChallenge map;
            try { map = Gbx.ParseNode<CGameCtnChallenge>(path); }
            catch { continue; }
            var free = (map.GetBlocks() ?? []).Where(b => b.IsFree).ToList();
            if (path.Contains("Empty48")) Console.WriteLine($"template chunk 5F: {map.Chunks.OfType<CGameCtnChallenge.Chunk0304305F>().FirstOrDefault()?.Version.ToString() ?? "missing"}");
            if (free.Count == 0) continue;
            Console.WriteLine($"{Path.GetFileName(path)}: size {map.Size}, {free.Count} free of {map.GetBlocks().Count()} blocks, chunk 5F: v{map.Chunks.OfType<CGameCtnChallenge.Chunk0304305F>().FirstOrDefault()?.Version}");
            foreach (var b in free.Take(12))
                Console.WriteLine($"  {b.Name,-36} coord {b.Coord} dir {b.Direction} pos {b.AbsolutePositionInMap} ypr {b.YawPitchRoll} ground {b.IsGround} var {b.Variant}/{b.SubVariant} flags 0x{b.Flags:X8}");
            if (++shown >= 6) break;
        }
        if (shown == 0) Console.WriteLine("No free blocks found");
    }
}
