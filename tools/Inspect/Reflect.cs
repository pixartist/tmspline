using GBX.NET;
using GBX.NET.Engines.Game;
static class Reflect
{
    public static void Run(string file)
    {
        Console.WriteLine(string.Join("; ", typeof(CGameCtnBlockUnitInfo).GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}")));
        var refsT = typeof(CGameCtnBlockUnitInfo).GetProperties().Where(p => p.Name == "References").Select(p => p.PropertyType).First();
        Console.WriteLine("refs: " + string.Join("; ", refsT.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}")));
        Console.WriteLine("clip: " + string.Join("; ", typeof(CGameCtnBlockInfoClip).GetProperties().Where(p => p.DeclaringType == typeof(CGameCtnBlockInfoClip)).Select(p => $"{p.PropertyType.Name} {p.Name}")));
        var g = Gbx.Parse(file);
        var info = (CGameCtnBlockInfo)g.Node!;
        var u = info.VariantBaseAir!.BlockUnitModels![0]!;
        foreach (var name in new[] { "ClipsNorth", "ClipsEast", "ClipsTop", "ClipsBottom" })
        {
            var v = u.GetType().GetProperty(name)!.GetValue(u) as System.Collections.IEnumerable;
            Console.WriteLine($"{name}: " + string.Join(", ", v?.Cast<object?>().Select(x => x?.GetType().Name ?? "null") ?? []));
        }
    }
}
