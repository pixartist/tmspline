using GBX.NET;
using GBX.NET.Engines.Game;
// Lists waypoint items/blocks of a map (to see how the game stores them).
static class WaypointsDump
{
    public static void Run(string path)
    {
        var map = Gbx.ParseNode<CGameCtnChallenge>(path);
        Console.WriteLine($"{Path.GetFileName(path)}: laps={map.NbLaps} isLapRace={map.IsLapRace}");
        foreach (var o in map.AnchoredObjects ?? new())
            if (o.WaypointSpecialProperty is { } w)
                Console.WriteLine($"  item {o.ItemModel.Id}: tag={w.Tag} order={w.Order} at {o.AbsolutePositionInMap}");
        foreach (var b in map.Blocks ?? new())
            if (b.WaypointSpecialProperty is { } w)
                Console.WriteLine($"  block {b.Name}: tag={w.Tag} order={w.Order}");
    }
}
