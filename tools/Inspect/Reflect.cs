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
