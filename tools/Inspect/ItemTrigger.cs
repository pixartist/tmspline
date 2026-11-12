using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

/// <summary>
/// An item's entity model, trigger surface and the item chunk 0x2E00201F as the library reads them (private fields
/// included): for making effect gates out of items (see ItemExporter.MakeEffectGate).
/// Usage: Inspect --itemtrigger some.Item.Gbx
/// </summary>
static class ItemTrigger
{
    public static void Run(string path)
    {
        var gbx = Gbx.Parse<CGameItemModel>(path);
        var item = gbx.Node;
        Console.WriteLine($"{Path.GetFileName(path)}: waypoint {item.WaypointType}, entity model {item.EntityModel?.GetType().Name}");
        foreach (var chunk in item.Chunks)
            if (chunk.GetType().Name == "Chunk2E00201F") Dump(chunk, "  ");
        CPlugSurface? trigger = null;
        if (item.EntityModel is CGameCommonItemEntityModel common)
        {
            trigger = common.TriggerShape as CPlugSurface;
            Console.WriteLine($"  static object {common.StaticObject?.GetType().Name}, trigger {common.TriggerShape?.GetType().Name}");
        }
        else if (item.EntityModel is CPlugPrefab prefab)
        {
            Console.WriteLine($"  prefab version {prefab.Version}, written {prefab.FileWriteTime}, url '{prefab.Url}', u01 {prefab.U01}, u02 {prefab.U02}");
            foreach (var ent in prefab.Ents ?? [])
            {
                Console.WriteLine($"    ent {ent.Model?.GetType().Name}");
                Dump(ent, "      ");
                if (ent.Model is NPlugTrigger_SSpecial special) { trigger = special.TriggerShape; Console.WriteLine($"      special version {special.Version}, mergeable {special.IsMergeable}"); }
            }
        }
        if (trigger is null) return;
        Console.WriteLine($"  trigger surface: version {trigger.SurfVersion}, materials {trigger.Materials?.Length.ToString() ?? "null"}");
        foreach (var m in trigger.Materials ?? []) Console.WriteLine($"    material: node {m.Material?.GetType().Name ?? "none"}, surface id {(m.SurfaceId is { } id ? ((int)id).ToString("X") : "null")}");
        foreach (var chunk in trigger.Chunks) Dump(chunk, "    ");
        if (trigger.Surf is CPlugSurface.Mesh mesh)
            Console.WriteLine($"    mesh: {mesh.Vertices?.Length} vertices, triangles (U02, U03, surface index): {string.Join(" ", (mesh.Triangles ?? []).Take(4).Select(t => $"({t.U02},{t.U03},{t.SurfaceIndex})"))} ...");
    }

    private static void Dump(object o, string indent)
    {
        Console.WriteLine($"{indent}{o.GetType().Name}:");
        foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var v = f.GetValue(o);
            var text = v is System.Collections.IEnumerable e and not string ? "[" + string.Join(", ", e.Cast<object>().Take(12)) + "]" : v?.ToString() ?? "null";
            Console.WriteLine($"{indent}  {f.FieldType.Name}{(f.FieldType.IsGenericType ? "<" + f.FieldType.GetGenericArguments()[0].Name + ">" : "")} {f.Name.Replace("<", "").Replace(">k__BackingField", "")} = {text}");
        }
    }
}
