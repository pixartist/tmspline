using System.Security;
using System.Text;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

namespace TmSpline.Export;

/// <param name="Offset">Item pivot relative to the track origin.</param>
/// <param name="PitchYawRoll">Rotation to store in the map (see RotationConvention).</param>
/// <param name="Waypoint">Race waypoint the item is (start, checkpoint ...), None for road pieces.</param>
/// <param name="WaypointOrder">Linked checkpoint group of a checkpoint (0: not linked).</param>
public sealed record ImportedItem(string Name, string ItemGbxPath, string RelativeItemPath, Vec3d Offset = default, Vec3d PitchYawRoll = default,
    Waypoint Waypoint = Waypoint.None, ItemColor Color = ItemColor.Default, int WaypointOrder = 0);

public static class ItemExporter
{
    static ItemExporter()
    {
        GBX.NET.Gbx.LZO = new GBX.NET.LZO.Lzo();
    }

    /// <summary>
    /// Writes the importer sources (.fbx, .MeshParams.xml, .Item.xml) for all meshes below
    /// <c>{userDir}\Work\Items\{itemFolder}</c>, imports them in one importer run and returns the produced items.
    /// </summary>
    /// <param name="itemFolder">Folder below Items\, e.g. "TMSpline\MyTrack". Backslash separated.</param>
    public static async Task<List<ImportedItem>> ImportAllAsync(IReadOnlyList<MeshChunk> meshes, string userDir, string itemFolder,
        string authorName, IImporter importer, Action<string>? log = null, CancellationToken ct = default)
    {
        foreach (var mesh in meshes)
            WriteSources(mesh, userDir, itemFolder, authorName);

        // The importer is a Windows program: its arguments use backslashes, also under Wine.
        var output = await importer.RunAsync(userDir, $@"Items\{itemFolder}\*.Item.xml", ct);
        log?.Invoke(output);

        return meshes.Select(m =>
        {
            var item = FindImported(userDir, itemFolder, m.Name, output);
            UseGameMaterials(item.ItemGbxPath, m.AllMaterials, log);
            if (m.TriggerGameplay is { } gameplay)
                MakeEffectGate(item.ItemGbxPath, Enum.Parse<GBX.NET.Engines.Plug.CPlugSurface.GameplayId>(gameplay), m.TriggerDirection, log);
            return item with
            {
                Offset = m.ItemOffset,
                PitchYawRoll = m.Frame.StoredPitchYawRoll,
                Waypoint = m.Waypoint,
                WaypointOrder = m.WaypointOrder,
                Color = m.Color,
            };
        }).ToList();
    }

    /// <summary>
    /// Makes the item's materials with a <see cref="TmMaterial.GamePath"/> game materials (IsUsingGameMaterial, full
    /// link), as items built in the game's mesh modeller store them: only then does the collision use the PhysicsId
    /// the importer stored on them (magnet surfaces).
    /// </summary>
    /// <summary>
    /// Makes an imported item with a trigger (and no waypoint) a gate that gives an effect, the way schadocalex's
    /// "Custom gates" tool does it (gbx-py, custom_gates.py; checked in game 2026-11-12 with Turbo): the item's model
    /// becomes a prefab of two parts, its static object and a special trigger (NPlugTrigger_SSpecial) holding the
    /// item's trigger shape; that shape's surface is made "not collidable" with the effect as its gameplay id (in the
    /// list of the surface's material ids and on each of its triangles); and the item's flag that goes with a plain
    /// static object is cleared. (An effect written into a material, or a material put on a checkpoint's trigger,
    /// does nothing: the game reads that trigger only as a checkpoint.)
    /// </summary>
    private static void MakeEffectGate(string itemPath, GBX.NET.Engines.Plug.CPlugSurface.GameplayId effect, System.Numerics.Vector3? direction, Action<string>? log)
    {
        var gbx = GBX.NET.Gbx.Parse<GBX.NET.Engines.GameData.CGameItemModel>(itemPath);
        var item = gbx.Node;
        if (item.EntityModel is not GBX.NET.Engines.GameData.CGameCommonItemEntityModel { StaticObject: { } model, TriggerShape: GBX.NET.Engines.Plug.CPlugSurface trigger })
            throw new InvalidOperationException($"{itemPath}: not a static item with a trigger shape, cannot make it a gate");
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        byte physics = (byte)GBX.NET.Engines.Plug.CPlugSurface.MaterialId.NotCollidable, gameplay = (byte)effect;
        trigger.Materials = [];
        // (The surface's material ids: physics in the low byte, gameplay in the high one; the library has no name for them.)
        var surface = trigger.Chunks.First(c => c.GetType().Name == "Chunk0900C003");
        surface.GetType().GetField("U04", any)!.SetValue(surface, new[] { (ushort)(physics | gameplay << 8) });
        if (trigger.Surf is GBX.NET.Engines.Plug.CPlugSurface.Mesh mesh && mesh.Triangles is { } triangles)
        {
            mesh.Triangles = triangles.Select(t => new GBX.NET.Engines.Plug.CPlugSurface.Mesh.Triangle(t.Indices, physics, gameplay, 0)).ToArray();
            // A directed effect's direction (the importer leaves the item's +Z).
            if (direction is { } d) mesh.GameplayMainDir = new GBX.NET.Vec3(d.X, d.Y, d.Z);
        }

        var special = new GBX.NET.Engines.Plug.NPlugTrigger_SSpecial { Version = 1, TriggerShape = trigger };
        GBX.NET.Engines.Plug.CPlugPrefab.EntRef Part(GBX.NET.Engines.MwFoundations.CMwNod node) => new() { Model = node, Rotation = new GBX.NET.Quat(0, 0, 0, 1), Position = default, U01 = "" };
        item.EntityModel = new GBX.NET.Engines.Plug.CPlugPrefab { Version = 11, FileWriteTime = DateTime.Now, Url = "", Ents = [Part(model), Part(special)] };
        var flags = item.Chunks.First(c => c.GetType().Name == "Chunk2E00201F");
        flags.GetType().GetField("U08", any)!.SetValue(flags, (byte?)0);

        using (var file = File.Create(itemPath))
            gbx.Save(file);
        log?.Invoke($"{Path.GetFileName(itemPath)}: made a {effect} gate{(direction is { } to ? $", pointing {to}" : "")}");
    }

    private static void UseGameMaterials(string itemPath, IEnumerable<TmMaterial> materials, Action<string>? log)
    {
        var paths = materials.Where(m => m.GamePath is not null).ToDictionary(m => m.Name, m => m.GamePath!);
        if (paths.Count == 0) return;
        var gbx = GBX.NET.Gbx.Parse<GBX.NET.Engines.GameData.CGameItemModel>(itemPath);
        var mesh = (gbx.Node.EntityModel as GBX.NET.Engines.GameData.CGameCommonItemEntityModel)?.StaticObject?.Mesh
            ?? throw new InvalidOperationException($"{itemPath}: not a static item with a mesh");
        var changed = 0;
        foreach (var material in mesh.CustomMaterials ?? [])
        {
            if (material.MaterialUserInst is not { } inst || inst.MaterialName is null || !paths.TryGetValue(inst.MaterialName, out var path)) continue;
            inst.IsUsingGameMaterial = true;
            inst.Link = path;
            changed++;
        }
        if (changed == 0) return;
        using (var file = File.Create(itemPath))
            gbx.Save(file);
        log?.Invoke($"{Path.GetFileName(itemPath)}: {changed} material(s) made game materials");
    }

    public static void WriteSources(MeshChunk mesh, string userDir, string itemFolder, string authorName)
    {
        var workDir = Path.Combine(userDir, "Work", "Items", ToFileSystemPath(itemFolder));
        var meshDir = Path.Combine(workDir, "Mesh");
        Directory.CreateDirectory(meshDir);

        FbxBinaryWriter.Write(mesh, Path.Combine(meshDir, mesh.Name + ".fbx"));
        File.WriteAllText(Path.Combine(meshDir, mesh.Name + ".MeshParams.xml"), MeshParamsXml(mesh), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workDir, mesh.Name + ".Item.xml"), ItemXml(mesh, authorName), new UTF8Encoding(false));
    }

    private static ImportedItem FindImported(string userDir, string itemFolder, string name, string importerOutput)
    {
        var relativeItem = $@"{itemFolder}\{name}.Item.Gbx";
        var dir = Path.Combine(userDir, "Items", ToFileSystemPath(itemFolder));
        // The importer writes ".Item.gbx" (lowercase); match case-insensitively so this also works on Linux.
        var gbx = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + ".Item.Gbx", StringComparison.OrdinalIgnoreCase))
            : null;
        if (gbx is null)
            throw new ImporterException($"NadeoImporter did not produce {relativeItem}", importerOutput);
        return new ImportedItem(name, gbx, relativeItem);
    }

    /// <summary>Item folders are backslash-separated Trackmania paths; convert them for the local file system.</summary>
    private static string ToFileSystemPath(string itemFolder) => itemFolder.Replace('\\', Path.DirectorySeparatorChar);

    private static string MeshParamsXml(MeshChunk mesh)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<MeshParams MeshType=\"Static\" Collection=\"Stadium\" Scale=\"1\" FbxFile=\"{Esc(mesh.Name)}.fbx\">");
        sb.AppendLine("  <Materials>");
        foreach (var m in mesh.AllMaterials)
        {
            var physics = m.PhysicsId is null ? "" : $" PhysicsId=\"{Esc(m.PhysicsId)}\"";
            var color = m.Color is null ? "" : $" Color=\"{Esc(m.Color)}\"";
            var gameplay = m.GameplayId is null ? "" : $" GameplayId=\"{Esc(m.GameplayId)}\"";
            sb.AppendLine($"    <Material Name=\"{Esc(m.Name)}\" Link=\"{Esc(m.Link)}\"{color}{physics}{gameplay} />");
        }
        sb.AppendLine("  </Materials>");
        sb.AppendLine("</MeshParams>");
        return sb.ToString();
    }

    private static string ItemXml(MeshChunk mesh, string authorName) =>
        $"""
        <Item Type="StaticObject" Collection="Stadium" AuthorName="{Esc(authorName)}">
          <MeshParamsLink File="Mesh/{Esc(mesh.Name)}.MeshParams.xml"/>{(mesh.Waypoint == Waypoint.None ? "" : $"\n  <Waypoint Type=\"{mesh.Waypoint}\"/>")}
          <GridSnap HStep="0" VStep="0"/>
          <Levitation VStep="0" GhostMode="true"/>
          <Options OneAxisRotation="false"/>
        </Item>
        """;

    private static string Esc(string s) => SecurityElement.Escape(s);
}
