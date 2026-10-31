using GBX.NET;
using GBX.NET.Engines.Game;
// Prints the lowest item Y and the lowest ground block coord per map, to calibrate world height of block Y.
static class Ground
{
    public static void Run(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var map = Gbx.ParseNode<CGameCtnChallenge>(path);
                var items = map.AnchoredObjects ?? new();
                var minItem = items.Count > 0 ? items.Min(o => o.AbsolutePositionInMap.Y) : float.NaN;
                var minBlock = map.Blocks?.Where(b => !b.IsFree).Select(b => b.Coord.Y).DefaultIfEmpty(-1).Min();
                var free = map.Blocks?.Where(b => b.IsFree).Select(b => b.AbsolutePositionInMap?.Y ?? float.NaN).DefaultIfEmpty(float.NaN).Min();
                Console.WriteLine($"{Path.GetFileName(path)}: minItemY={minItem} minBlockCoordY={minBlock} minFreeBlockY={free}");
            }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); }
        }
    }
}
