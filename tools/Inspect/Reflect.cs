using GBX.NET;
using GBX.NET.Engines.Game;
static class Reflect
{
    public static void Run(string file)
    {
        var gbx = Gbx.Parse(file);
        var info = (CGameCtnBlockInfo)gbx.Node!;
        var all = new List<(string, CGameCtnBlockInfoVariant)>();
        if (info.VariantBaseGround is { } g) all.Add(("baseGround", g));
        var k = 0; foreach (var v in info.AdditionalVariantsGround ?? []) if (v is not null) all.Add(($"addGround{k++}", v));
        if (info.VariantBaseAir is { } a) all.Add(("baseAir", a));
        k = 0; foreach (var v in info.AdditionalVariantsAir ?? []) if (v is not null) all.Add(($"addAir{k++}", v));
        foreach (var (name, v) in all)
        {
            var clips = new List<string>();
            foreach (var u in v.BlockUnitModels ?? [])
            {
                if (u is null) continue;
                var refs = u.GetType().GetProperty("References")!.GetValue(u)!;
                foreach (var face in new[] { "ClipsNorth", "ClipsEast", "ClipsSouth", "ClipsWest", "ClipsTop", "ClipsBottom" })
                    if (refs.GetType().GetProperty(face)!.GetValue(refs) is System.Collections.IList rl)
                        foreach (var r in rl)
                            clips.Add($"{u.RelativeOffset.Y}{u.RelativeOffset.Z}{face[5]}:{Path.GetFileName((r?.GetType().GetProperty("File")?.GetValue(r) as GBX.NET.Components.GbxRefTableFile)?.FilePath ?? "emb").Split('.')[0]}");
            }
            Console.WriteLine($"{name} npb={v.IsNoPillarBelowVariant} name='{v.Name}' {string.Join(" ", clips)}");
        }
    }

    /// <summary>The mobils of each variant: their geometry transforms and prefab references.</summary>
    public static void Mobils(string file)
    {
        var info = (CGameCtnBlockInfo)Gbx.Parse(file).Node!;
        void Dump(string name, CGameCtnBlockInfoVariant? v)
        {
            if (v is null) return;
            var set = 0;
            foreach (var mobils in v.Mobils ?? [])
            {
                foreach (var m in mobils ?? [])
                    if (m is not null)
                        Console.WriteLine($"{name}[{set}] geom={m.HasGeomTransformation} t={m.GeomTranslation} r={m.GeomRotation} prefab={(m.GetType().GetProperty("PrefabFid")?.GetValue(m) as GBX.NET.Components.GbxRefTableFile)?.FilePath ?? m.PrefabFid?.GetType().Name}");
                set++;
            }
        }
        Dump("ground", info.VariantBaseGround);
        Dump("air", info.VariantBaseAir);
        // Scalar properties of the block and its ground variant (to compare blocks that differ in placement).
        foreach (var o in new object?[] { info, info.VariantBaseGround })
            foreach (var p in o?.GetType().GetProperties() ?? [])
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v; try { v = p.GetValue(o); } catch { continue; }
                if (v is null || v is System.Collections.IEnumerable and not string || v.GetType().IsClass && v is not string) continue;
                Console.WriteLine($"  {o!.GetType().Name}.{p.Name} = {v}");
            }
    }

    /// <summary>The icon of a block or item: its chunk's fields and the collector's icon properties.</summary>
    public static void Icon(string file)
    {
        var node = Gbx.Parse(file).Node!;
        Console.Write($"{Path.GetFileName(file).Split('.')[0]}:");
        foreach (var p in node.GetType().GetProperties().Where(p => p.Name.Contains("Icon") && p.GetIndexParameters().Length == 0))
        {
            var v = p.GetValue(node);
            Console.Write($" {p.Name}={(v is byte[] b ? $"{b.Length} bytes" : v)}");
        }
        foreach (var c in node.Chunks.Where(c => c.Id == 0x2E001004))
            foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Cast<System.Reflection.MemberInfo>().Concat(c.GetType().GetProperties()))
            {
                var v = f is System.Reflection.FieldInfo fi ? fi.GetValue(c) : ((System.Reflection.PropertyInfo)f).GetValue(c);
                if (v is not byte[]) Console.Write($" chunk.{f.Name}={v}");
            }
        Console.WriteLine();
    }

    /// <summary>A clip's type, groups and its variants' mobil counts.</summary>
    public static void Clip(string file)
    {
        var node = Gbx.Parse(file).Node;
        if (node is not CGameCtnBlockInfoClip c) { Console.WriteLine($"{Path.GetFileName(file)}: {node?.GetType().Name}"); return; }
        string Mobils(CGameCtnBlockInfoVariant? v) => v is null ? "-" : string.Join("/", (v.Mobils ?? []).Select(s => s?.Count ?? 0));
        Console.WriteLine($"{Path.GetFileName(file).Split('.')[0]}: {c.GetType().Name} type={c.ClipType} full={c.IsFullFreeClip} group={c.ClipGroupId}|{c.ClipGroupId2} sym={c.SymmetricalClipGroupId}|{c.SymmetricalClipGroupId2} ground={Mobils(c.VariantBaseGround)} air={Mobils(c.VariantBaseAir)}"
            + (c is CGameCtnBlockInfoClipVertical v ? $" vgroup={v.VerticalClipGroupId}" : ""));
    }
}
