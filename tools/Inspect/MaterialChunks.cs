using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

/// <summary>
/// Which chunk of a CPlugMaterial holds its surface kind and its gameplay effect: a material with each chunk type alone
/// is put on an item's trigger shape and the item written twice, with and without the value; the chunk whose bytes
/// differ holds it. (For the trial of effect gates of our own: a material written into the item's trigger.)
/// Usage: Inspect --matchunks gate.Item.Gbx
/// </summary>
static class MaterialChunks
{
    public static void Run(string itemPath)
    {
        var chunkTypes = typeof(CPlugMaterial).GetNestedTypes().Where(t => t.Name.StartsWith("Chunk")).OrderBy(t => t.Name).ToList();
        byte[] Write(Type? chunk, CPlugSurface.MaterialId surface, CPlugSurface.GameplayId gameplay)
        {
            var gbx = Gbx.Parse<CGameItemModel>(itemPath);
            var trigger = (CPlugSurface)((CGameCommonItemEntityModel)gbx.Node.EntityModel!).TriggerShape!;
            var material = new CPlugMaterial { SurfaceId = surface, GameplayId = gameplay };
            if (chunk is not null)
            {
                var create = material.GetType().GetMethods().First(m => m.Name == "CreateChunk" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                create.MakeGenericMethod(chunk).Invoke(material, null);
            }
            trigger.Materials = [new CPlugSurface.SurfMaterial { Material = material }];
            using var stream = new MemoryStream();
            gbx.Save(stream);
            return stream.ToArray();
        }
        foreach (var chunk in chunkTypes.Prepend(null!))
        {
            try
            {
                var plain = Write(chunk, CPlugSurface.MaterialId.Concrete, CPlugSurface.GameplayId.None);
                var surface = Write(chunk, CPlugSurface.MaterialId.Asphalt, CPlugSurface.GameplayId.None);
                var gameplay = Write(chunk, CPlugSurface.MaterialId.Concrete, CPlugSurface.GameplayId.Turbo);
                Console.WriteLine($"{chunk?.Name ?? "(no chunk)"}: {plain.Length} bytes; surface kind {(plain.AsSpan().SequenceEqual(surface) ? "not written" : "WRITTEN")}, gameplay {(plain.AsSpan().SequenceEqual(gameplay) ? "not written" : "WRITTEN")}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{chunk?.Name ?? "(no chunk)"}: {(e is TargetInvocationException t ? t.InnerException?.Message : e.Message)}");
            }
        }
    }
}
