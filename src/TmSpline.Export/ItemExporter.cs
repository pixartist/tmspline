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
