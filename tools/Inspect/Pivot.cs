using GBX.NET;
using GBX.NET.Engines.GameData;
// Prints an item's placement parameters (pivots, offsets), which the game applies when placing it.
static class Pivot
{
    public static void Run(string path)
    {
        var item = Gbx.ParseNode<CGameItemModel>(path);
        Console.WriteLine(Path.GetFileName(path));
        foreach (var obj in new object?[] { item, item.DefaultPlacement })
            if (obj is not null)
                foreach (var p in obj.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
                {
                    var n = p.Name;
                    if (!(n.Contains("Pivot") || n.Contains("Offset") || n.Contains("Placement") || n.Contains("Ground") || n.Contains("Level") || n.Contains("Snap") || n.Contains("Box") || n.Contains("Height"))) continue;
                    object? v = null; try { v = p.GetValue(obj); } catch { }
                    var text = v is System.Collections.IEnumerable e and not string ? "[" + string.Join(", ", e.Cast<object>()) + "]" : v?.ToString();
                    Console.WriteLine($"  {obj.GetType().Name}.{n}: {text}");
                }
    }
}
