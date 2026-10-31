using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;
// How an existing waypoint property is built (chunks, versions), plus the chunk APIs available on nodes.
static class ChunkInfo
{
    public static void Run(string path)
    {
        var map = Gbx.ParseNode<CGameCtnChallenge>(path);
        var w = map.AnchoredObjects?.Select(o => o.WaypointSpecialProperty).FirstOrDefault(x => x is not null)
                ?? map.Blocks?.Select(b => b.WaypointSpecialProperty).FirstOrDefault(x => x is not null);
        if (w is null) { Console.WriteLine("no waypoint"); return; }
        foreach (var c in w.Chunks)
        {
            var version = c.GetType().GetProperty("Version")?.GetValue(c);
            Console.WriteLine($"chunk {c.GetType().Name} id={c.Id:X8} version={version}");
        }
        foreach (var m in typeof(CGameWaypointSpecialProperty).GetMethods().Where(m => m.Name.Contains("Chunk")).Select(m => m.ToString()).Distinct())
            Console.WriteLine("  api: " + m);
    }
}
