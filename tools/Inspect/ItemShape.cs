using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;
// Prints the visible mesh and collision surface of an item (triangle counts, surface materials).
static class ItemShape
{
    public static void Run(string path)
    {
        var item = Gbx.ParseNode<CGameItemModel>(path);
        Console.WriteLine($"{Path.GetFileName(path)}: entity {item.EntityModel?.GetType().Name}");
        if (item.EntityModel is CGameCommonItemEntityModel ent)
            foreach (var p in ent.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
            {
                object? v = null;
                try { v = p.GetValue(ent); } catch { }
                if (v is not null) Console.WriteLine($"  entity.{p.Name}: {v}");
                if (v is CPlugSurface surf) { Console.Write("    "); Describe(surf); }
            }
        Console.WriteLine($"  waypoint: {item.WaypointType}");
        if (item.EntityModel is CGameCommonItemEntityModel common && common.StaticObject is { } so)
        {
            Console.WriteLine($"  mesh: {so.Mesh?.GetType().Name}, IsMeshCollidable={so.IsMeshCollidable}");
            if (so.Mesh is { } mesh)
                foreach (var p in mesh.GetType().GetProperties().Where(p => p.Name.Contains("Material") || p.Name.Contains("Shaded") || p.Name.Contains("Visual")))
                {
                    var v = p.GetValue(mesh);
                    var count = v is System.Collections.ICollection c ? c.Count.ToString() : v?.ToString();
                    Console.WriteLine($"    {p.Name}: {count}");
                    if (v is System.Collections.IEnumerable e and not string)
                        foreach (var x in e.Cast<object>().Take(8))
                            Console.WriteLine($"      {Describe(x)}");
                    if (p.Name == "CustomMaterials" && v is System.Collections.IEnumerable cm)
                        foreach (var m in cm.Cast<object>())
                            if (m.GetType().GetProperty("MaterialUserInst")?.GetValue(m) is { } inst)
                                Console.WriteLine($"        user inst: {Describe(inst)}");
                }
            if (so.Mesh is CPlugSolid2Model all)
                foreach (var p in all.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType == typeof(CPlugSolid2Model)))
                {
                    object? v = null;
                    try { v = p.GetValue(all); } catch { }
                    Console.WriteLine($"    s2.{p.Name}: {(v is System.Collections.ICollection c ? $"[{c.Count}]" : v)}");
                    if (p.Name.Contains("Layer") && v is System.Collections.IEnumerable e and not string)
                        foreach (var x in e.Cast<object>()) Console.WriteLine($"      {Describe(x)}");
                }
            if (so.Mesh is CPlugSolid2Model s2)
                foreach (var vis in s2.Visuals ?? [])
                    if (vis is CPlugVisual3D v3)
                        Console.WriteLine($"    vertex streams: {string.Join(" | ", v3.VertexStreams.Select(st => string.Join(",", st.DataDecls?.Select(d => d.WeightCount + ":" + d.ToString()) ?? [])))}"
                            + $" colors={v3.Vertices?.FirstOrDefault().Color}"
                            + string.Concat(v3.VertexStreams.Select(st => st.Positions is { Length: > 0 } ps ? $" stream y {ps.Min(v => v.Y):0.00}..{ps.Max(v => v.Y):0.00} x {ps.Min(v => v.X):0.00}..{ps.Max(v => v.X):0.00} z {ps.Min(v => v.Z):0.00}..{ps.Max(v => v.Z):0.00}" : ""))
                            + (v3.Vertices is { Length: > 0 } vs ? $" bounds y {vs.Min(v => v.Position.Y):0.00}..{vs.Max(v => v.Position.Y):0.00} x {vs.Min(v => v.Position.X):0.00}..{vs.Max(v => v.Position.X):0.00} z {vs.Min(v => v.Position.Z):0.00}..{vs.Max(v => v.Position.Z):0.00}" : ""));
            Describe(so.Shape);
        }
    }

    static string Describe(object x)
    {
        var props = x.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => { try { var v = p.GetValue(x); return v is null or System.Collections.ICollection ? null : $"{p.Name}={v}"; } catch { return null; } })
            .Where(v => v is not null).Take(8);
        return $"{x.GetType().Name}: {string.Join(", ", props)}";
    }

    static void Describe(CPlugSurface? shape)
    {
        if (shape is null) { Console.WriteLine("  no shape"); return; }
        Console.WriteLine($"  shape surface: {shape.Surf?.GetType().Name}");
        if (shape.Surf is CPlugSurface.Mesh m)
            Console.WriteLine($"  collision mesh: {m.Vertices?.Length} vertices, {m.CookedTriangles?.Length} triangles"
                + (m.Vertices is { Length: > 0 } vs ? $", y {vs.Min(v => v.Y):0.00}..{vs.Max(v => v.Y):0.00}" : ""));
        foreach (var mat in shape.Materials ?? Array.Empty<CPlugSurface.SurfMaterial>())
            Console.WriteLine($"  surface material: id={mat.SurfaceId}");
    }
}
