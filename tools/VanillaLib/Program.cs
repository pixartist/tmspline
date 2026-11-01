using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.Plug;
using GBX.NET.LZO;

// Converts vanilla block meshes extracted with Openplanet (GameData\Stadium\GameCtnBlockInfo + Media\Prefab) into a mesh
// library for the editor's preview: index.json (blocks, their size in grid units and a mesh per ground/air variant) and
// one .bin per distinct mesh (Float32 positions, then Uint32 indices, per material group).
//
// LOCAL USE ONLY: the output is Nadeo's game data. It goes to artifacts/vanilla (ignored by Docker builds) and is served
// to the editor by the development server only. Never commit, upload or redistribute it.
//
// Usage: VanillaLib [extractDir] [outDir] [nameFilter]
Gbx.LZO = new Lzo();
// --dds <file.dds> <outDir>: converts one image (debugging), with and without alpha.
if (args.Length >= 3 && args[0] == "--dds") { Textures.ConvertForDebug(args[1], args[2]); return; }
var extract = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenplanetNext", "Extract");
var root = FindRepoRoot();
var outDir = args.Length > 1 ? args[1] : Path.Combine(root, "artifacts", "vanilla");
var filter = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "";
// --lod N: detail level of the meshes (0 = the game's full detail; the editor preview uses 1 by default).
var lodRequest = args.SkipWhile(a => a != "--lod").Skip(1).Select(int.Parse).FirstOrDefault(1);
var lodStats = new ConcurrentDictionary<int, int>();
var blockDir = Path.Combine(extract, "GameData", "Stadium", "GameCtnBlockInfo");
Directory.CreateDirectory(Path.Combine(outDir, "mesh"));
Directory.CreateDirectory(Path.Combine(outDir, "icon"));
Directory.CreateDirectory(Path.Combine(outDir, "texture"));

// Only blocks the mapper places: clips and pillars are added by the game itself.
// Pillar blocks are converted too (for the columns under air blocks), but the palette leaves them out.
var placeable = new[] { "GameCtnBlockInfoClassic", "GameCtnBlockInfoFlat", "GameCtnBlockInfoPillar" };
var files = placeable.Select(f => Path.Combine(blockDir, f)).Where(Directory.Exists)
    .SelectMany(d => Directory.EnumerateFiles(d, "*.Gbx", SearchOption.AllDirectories))
    .Where(f => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
Console.WriteLine($"{files.Count} block definitions in {blockDir}");

var materials = new ConcurrentDictionary<string, int>();
var materialLock = new object();
var meshes = new ConcurrentDictionary<string, MeshInfo>();
var blocks = new ConcurrentDictionary<string, BlockInfo>();
var failed = new ConcurrentBag<string>();
var skipped = new ConcurrentDictionary<string, int>();
var missingFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
var refCache = new ConcurrentDictionary<string, Lazy<string[]>>(StringComparer.OrdinalIgnoreCase);
var materialFiles = new ConcurrentDictionary<string, string>();
var blockModifiers = new ConcurrentDictionary<string, string>();
var meshVertices = new ConcurrentDictionary<string, Vector3[]>();
var meshSamples = new ConcurrentDictionary<string, Lazy<Vector3[]>>();
// The same without floor plates (Grass, plain TrackWallClips lids): a plate's trims, which decide how it turns.
var trimSamples = new ConcurrentDictionary<string, Lazy<Vector3[]>>();
bool IsFloorMaterial(int id) => materials.Any(m => m.Value == id && m.Key is "Grass" or "TrackWallClips");
var clipMeshes = new ConcurrentDictionary<string, Lazy<MeshRef?>>(StringComparer.OrdinalIgnoreCase);
string[] ClipFaces = ["ClipsNorth", "ClipsEast", "ClipsSouth", "ClipsWest", "ClipsTop", "ClipsBottom"];
var nodeCache = new ConcurrentDictionary<string, Lazy<(object?, int)>>(StringComparer.OrdinalIgnoreCase);
var done = 0;
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
{
    try
    {
        var (node, level) = LoadNode(file);
        if (node is CGameCtnBlockInfo info)
        {
            var name = info.Ident.Id is { Length: > 0 } id ? id : Path.GetFileName(file).Split('.')[0];
            // Surface variants (PlatformGrass, PlatformDirt, ...) reuse another block's mesh with a terrain modifier
            // that swaps its materials.
            if (References(file).FirstOrDefault(r => r.EndsWith(".TerrainModifier.Gbx", StringComparison.OrdinalIgnoreCase)) is { } modifier)
            {
                blockModifiers[name] = modifier;
                if (!File.Exists(modifier)) missingFiles.TryAdd(modifier, 0);
            }
            var ground = Variant(info.VariantBaseGround, file, level, true);
            var air = Variant(info.VariantBaseAir, file, level, false);
            // Ground-only blocks ("in ground" U bottoms have an empty air variant): off the ground they are the ground
            // variant as is, without sinking into the terrain.
            if (air is { Mesh: null, Clips: null, Units.Length: 0 } && info.VariantBaseGround is not null)
                air = Variant(info.VariantBaseGround, file, level, false);
            // The air variant the game uses with a column below (e.g. a slope with the column's wedge walls), when the
            // base one is the "no pillar below" variant.
            var withPillar = info.VariantBaseAir is { IsNoPillarBelowVariant: true }
                ? (info.AdditionalVariantsAir ?? []).FirstOrDefault(v => v is { IsNoPillarBelowVariant: false })
                : null;
            var airPillar = withPillar is null ? null : Variant(withPillar, file, level, false);
            // The same on the ground: the version with its "column below" adapter, which the game shows there too.
            var groundWith = info.VariantBaseGround is { IsNoPillarBelowVariant: true }
                ? (info.AdditionalVariantsGround ?? []).FirstOrDefault(v => v is { IsNoPillarBelowVariant: false })
                : null;
            var groundPillar = groundWith is null ? null : Variant(groundWith, file, level, true);
            // Blocks without a mesh of their own (shaped only by the clips the game adds, e.g. Deco walls) stay in the
            // palette with their icon and show as boxes.
            if (ground?.Mesh is not null || air?.Mesh is not null || ground?.Clips is not null || air?.Clips is not null)
            {
                // The game's own catalog icon (small WebP), shown in the editor's block palette.
                string? icon = null;
                if (info.IconWebP is { Length: > 0 } webp)
                {
                    icon = $"icon/{name}.webp";
                    File.WriteAllBytes(Path.Combine(outDir, icon), webp);
                }
                blocks[name] = new BlockInfo(Path.GetRelativePath(blockDir, Path.GetDirectoryName(file)!).Replace('\\', '/'), ground, air,
                    icon, info.PageName ?? "", info.CatalogPosition, info.IsPillar,
                    blockModifiers.TryGetValue(name, out var mod) ? Path.GetFileName(mod).Split('.')[0] : null, airPillar, groundPillar);
            }
        }
    }
    catch (Exception ex) { failed.Add($"{Path.GetFileName(file)}: {ex.GetType().Name} {ex.Message}"); }
    var n = Interlocked.Increment(ref done);
    if (n % 200 == 0) Console.WriteLine($"  {n}/{files.Count}");
});

var materialList = materials.OrderBy(m => m.Value).Select(m => m.Key).ToList();

// Surface modifiers (PlatformGrass, ...): a remap folder plus a game skin listing the materials it replaces. The
// replacement for material N is looked up in the remap folder; the exact layout is not documented, so all likely paths
// are tried (missing ones go to the plugin's list, which skips files the game does not have).
var gameData = Path.Combine(extract, "GameData");
var remaps = new SortedDictionary<string, SortedDictionary<string, string>>(); // modifier -> material -> remapped material key
foreach (var modifierPath in blockModifiers.Values.Distinct(StringComparer.OrdinalIgnoreCase))
{
    if (LoadNode(modifierPath).Node is not CPlugGameSkinAndFolder modifier || modifier.RemapFolder is not { Length: > 0 } folder) continue;
    var skinPath = References(modifierPath).FirstOrDefault(r => r.EndsWith(".GameSkin.gbx", StringComparison.OrdinalIgnoreCase));
    if (skinPath is null || LoadNode(skinPath).Node is not CPlugGameSkin skin) continue;
    var modifierName = Path.GetFileName(modifierPath).Split('.')[0];
    foreach (var fid in skin.HeaderFids ?? [])
    {
        if (fid?.Name is not { Length: > 0 } material || fid.Directory is not { Length: > 0 } original) continue;
        var originalPath = original.Replace('\\', '/');
        var file = Path.GetFileName(originalPath);
        // The replacement is the file of the same name in the remap folder (checked in game files 2026-11-01). Without
        // one the modifier keeps the original material.
        var found = Path.GetFullPath(Path.Combine(gameData, folder, file));
        if (!File.Exists(found)) { missingFiles.TryAdd(found, 0); continue; }
        // Empty files are left by extractions that failed: the game has no such file.
        if (new FileInfo(found).Length == 0) continue;
        var key = $"{modifierName}/{material}";
        materialFiles[key] = found;
        if (!remaps.TryGetValue(modifierName, out var m)) remaps[modifierName] = m = new();
        m[material] = key;
    }
}
Console.WriteLine($"Modifiers: {blockModifiers.Values.Distinct().Count()}, remapped materials found: {remaps.Values.Sum(m => m.Count)}");

// Materials and what they reference (texture files, then the images behind them): missing ones go to the plugin's list.
var materialDeps = new Dictionary<string, List<string>>();
foreach (var (name, path) in materialFiles.Concat(blockModifiers.Select(m => KeyValuePair.Create("modifier:" + m.Key, m.Value))))
{
    var found = new List<string>();
    var queue = new Queue<(string Path, int Depth)>([(path, 0)]);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (queue.Count > 0)
    {
        var (p, depth) = queue.Dequeue();
        if (!seen.Add(p)) continue;
        if (!File.Exists(p)) { missingFiles.TryAdd(p, 0); continue; }
        found.Add(p);
        // Shaders are the game's own code, not needed for a preview.
        if (depth < 3 && !p.Contains(".Shader.", StringComparison.OrdinalIgnoreCase))
            foreach (var r in References(p)) queue.Enqueue((r, depth + 1));
    }
    materialDeps[name] = found;
}
Console.WriteLine($"Materials: {materialFiles.Count} files, {materialDeps.Values.Sum(d => d.Count)} of their files found; "
    + $"kinds found: {string.Join(", ", materialDeps.Values.SelectMany(d => d).Select(f => string.Join('.', Path.GetFileName(f).Split('.').Skip(1))).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}"))}");
var textures = Textures.Export(materialList.Concat(remaps.Values.SelectMany(m => m.Values)), materialFiles, References, outDir);
Console.WriteLine($"Textures: {textures.Count(t => t.Value.Texture is not null)} of {textures.Count} materials textured (with modifier replacements) "
    + $"({string.Join(", ", textures.Values.GroupBy(t => t.Projection).Select(g => $"{g.Key} {g.Count()}"))})");
var index = new
{
    Note = "Trackmania game data extracted locally with Openplanet. Local use only: do not redistribute.",
    // 2: mesh files carry UV0 after the positions.
    Format = 2,
    Materials = materialList,
    Textures = textures,
    // Per surface modifier: game material -> the key of its replacement in Textures.
    Modifiers = remaps,
    Meshes = meshes.Values.OrderBy(m => m.File).ToDictionary(m => m.File, m => new { m.Groups, Bounds = new[] { m.Min, m.Max } }),
    Blocks = blocks.OrderBy(b => b.Key).ToDictionary(b => b.Key, b => b.Value),
};
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DictionaryKeyPolicy = null };
File.WriteAllText(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(index, json));
Console.WriteLine($"LOD {lodRequest}; models by number of detail levels: {string.Join(", ", lodStats.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value}"))}; "
    + $"{meshes.Values.Sum(m => m.Groups.Sum(g => (long)g.I)) / 3:N0} triangles in all meshes");
Console.WriteLine($"Wrote {outDir}: {blocks.Count} blocks, {meshes.Count} meshes, {materialList.Count} materials, {failed.Count} failed");
foreach (var f in failed.Take(30)) Console.WriteLine("  " + f);
File.WriteAllLines(Path.Combine(outDir, "missing-files.txt"), missingFiles.Keys.Order());
// The same list for the TMSplineExtract Openplanet plugin (tools/OpenplanetExtract), as game paths ("GameData/...").
var pluginStorage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(extract))!, "PluginStorage", "TMSplineExtract");
Directory.CreateDirectory(pluginStorage);
File.WriteAllLines(Path.Combine(pluginStorage, "missing.txt"),
    missingFiles.Keys.Select(f => Path.GetRelativePath(extract, f).Replace('\\', '/')).Order());
Console.WriteLine($"  {missingFiles.Count} referenced files are missing (missing-files.txt)");
foreach (var (type, n) in skipped.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  skipped: {n} x {type}");

VariantInfo? Variant(CGameCtnBlockInfoVariant? variant, string file, int level, bool ground)
{
    if (variant is null) return null;
    var unitInfos = (variant.BlockUnitModels ?? []).Where(u => u is not null).Select(u => u!).ToList();
    // "In ground" blocks (the bottom of a U slope) sink this many levels into the terrain when placed on the ground:
    // their units, mesh and clips are stored that much lower, so level 0 is the one at the terrain surface.
    var sink = ground && variant is CGameCtnBlockInfoVariantGround { AutoTerrainHeightOffset: > 0 } g ? (int)g.AutoTerrainHeightOffset : 0;
    var units = unitInfos.Select(u => u.RelativeOffset - new Int3(0, sink, 0)).ToList();
    var size = units.Count == 0 ? new[] { 1, 1, 1 } : new[] { units.Max(u => u.X) + 1, units.Max(u => u.Y) + 1, units.Max(u => u.Z) + 1 };
    var mesh = MeshOf(variant, file, level, 0, -8 * sink);
    Int3 Cell(Int3 u) => u - new Int3(0, sink, 0);
    // The pieces the game adds on free faces of each unit (sides, caps, walls of columns): one "free clip" per face.
    // A free clip is hidden when the neighbouring face offers a clip group it connects to (its symmetrical group).
    var clips = new List<ClipInfo>();
    foreach (var u in unitInfos)
        for (var face = 0; face < ClipFaces.Length; face++)
        {
            // The ground version of a clip only on the bottom level; the levels above use the air one (otherwise
            // walls get a base trim on every level).
            var (clipMesh, connects, offers, stack, stackGroup, options) = FaceClips(u, ClipFaces[face], file, level, ground && u.RelativeOffset.Y == sink);
            var cell = Cell(u.RelativeOffset);
            if (clipMesh is not null || offers.Length > 0)
                clips.Add(new ClipInfo([cell.X, cell.Y, cell.Z], face, clipMesh,
                    connects.Length > 0 ? connects : null, offers.Length > 0 ? offers : null,
                    face >= 4 && clipMesh is not null && mesh is not null ? CapTurn(clipMesh, mesh, cell, face == 4, units) : 0,
                    stack, stackGroup, options.Length > 1 ? options : null));
        }
    // The columns the game's editor puts under this variant: pillar block, unit (offset in cells) and direction.
    var pillars = (variant.PlacedPillarParams ?? []).Select(p => new PillarInfo(
            p.BlockInfo?.Ident.Id is { Length: > 0 } id ? id
                : RefPath(p, "BlockInfo", file, level) is { } path ? Path.GetFileName(path).Split('.')[0] : "",
            [p.Offset.X, p.Offset.Y, p.Offset.Z], (int)p.Dir))
        .Where(p => p.Block.Length > 0).ToArray();
    return new VariantInfo(size, units.Select(u => new[] { u.X, u.Y, u.Z }).ToArray(), mesh, pillars.Length > 0 ? pillars : null,
        clips.Count > 0 ? clips.ToArray() : null);
}

/// <summary>
/// Mesh of a unit face's free clip (the piece shown when nothing is next to that face), from the clip's ground or air
/// variant. Clips are embedded in the block's file or are external .EDClip files.
/// </summary>
(MeshRef? Mesh, string[] Connects, string[] Offers, MeshRef?[]? Stack, string? StackGroup, ClipOption[] Options) FaceClips(CGameCtnBlockUnitInfo unit, string face, string file, int level, bool ground)
{
    MeshRef? found = null;
    MeshRef?[]? stack = null;
    string? stackGroup = null;
    var connects = new List<string>();
    var offers = new HashSet<string>();
    var options = new List<ClipOption>();
    var refs = unit.GetType().GetProperty("References")!.GetValue(unit);
    var refList = refs?.GetType().GetProperty(face)?.GetValue(refs) as System.Collections.IList;
    System.Collections.IList? nodes = null;
    try { nodes = (unit.GetType().GetProperty(face)!.GetValue(unit) as System.Collections.IEnumerable)?.Cast<object?>().ToList(); }
    catch { /* external clips that GBX.NET cannot resolve: loaded below */ }
    var count = Math.Max(refList?.Count ?? 0, nodes?.Count ?? 0);
    for (var i = 0; i < count; i++)
    {
        var nodeRef = refList is not null && i < refList.Count ? refList[i] : null;
        var external = nodeRef?.GetType().GetProperty("File")?.GetValue(nodeRef) as GBX.NET.Components.GbxRefTableFile;
        CGameCtnBlockInfo? clip;
        string clipFile = file;
        int clipLevel = level;
        if (external?.FilePath is { Length: > 0 } rel)
        {
            clipFile = Resolve(file, level, rel);
            (var node, clipLevel) = LoadNode(clipFile);
            if (node is null) { missingFiles.TryAdd(clipFile, 0); continue; }
            clip = node as CGameCtnBlockInfo;
        }
        else clip = nodes is not null && i < nodes.Count ? nodes[i] as CGameCtnBlockInfo : null;
        if (clip is not CGameCtnBlockInfoClip c) continue;
        foreach (var g in new[] { c.ClipGroupId, c.ClipGroupId2 }) if (!string.IsNullOrEmpty(g)) offers.Add(g);
        var type = c.ClipType.ToString();
        if (!c.IsFullFreeClip && !type.StartsWith("FreeClip", StringComparison.Ordinal)) continue;
        var key = $"{clipFile}|{(external is null ? $"{face}{i}|{unit.RelativeOffset}" : "")}|{ground}";
        // The variant for the block's height; when it has no mesh (e.g. a column's cap on the ground), the other one.
        MeshRef? ShapeOf(int set) => clipMeshes.GetOrAdd($"{key}|{set}", _ => new Lazy<MeshRef?>(() =>
            (ground ? MeshOf(c.VariantBaseGround, clipFile, clipLevel, set) : MeshOf(c.VariantBaseAir, clipFile, clipLevel, set))
            ?? (ground ? MeshOf(c.VariantBaseAir, clipFile, clipLevel, set) : MeshOf(c.VariantBaseGround, clipFile, clipLevel, set)))).Value;
        // Vertical clips (walls of columns and the adapters above them) come in shapes for their place in a wall that
        // runs over several levels: middle, top, bottom, alone (mobil sets 0..3). Some have no middle shape (an adapter
        // wedge only exists as the top of a wall), so any shape makes them count.
        MeshRef?[]? shapes = c is CGameCtnBlockInfoClipVertical ? [ShapeOf(0), ShapeOf(1), ShapeOf(2), ShapeOf(3)] : null;
        var result = shapes?.FirstOrDefault(m => m is not null) ?? ShapeOf(0);
        // What it connects to: its symmetrical groups, or its own groups when it has none (vertical clips of columns).
        var symmetric = new[] { c.SymmetricalClipGroupId, c.SymmetricalClipGroupId2 }.Where(g => !string.IsNullOrEmpty(g)).Select(g => g!).ToList();
        var joins = symmetric.Count > 0 ? symmetric : new[] { c.ClipGroupId, c.ClipGroupId2 }.Where(g => !string.IsNullOrEmpty(g)).Select(g => g!).ToList();
        var clipName = external?.FilePath is { Length: > 0 } ? Path.GetFileName(clipFile).Split('.')[0] : c.Ident.Id is { Length: > 0 } id ? id : $"embedded{i}";
        options.Add(new ClipOption(clipName, result, joins.ToArray(), shapes, (c as CGameCtnBlockInfoClipVertical)?.VerticalClipGroupId));
        if (found is not null || result is null) continue;
        found = result;
        if (c is CGameCtnBlockInfoClipVertical vertical)
        {
            stack = shapes;
            stackGroup = vertical.VerticalClipGroupId;
        }
        connects.AddRange(joins);
    }
    return (found, connects.Distinct().ToArray(), offers.Order().ToArray(), stack, stackGroup, options.ToArray());
}

MeshRef? MeshOf(CGameCtnBlockInfoVariant? variant, string file, int level, int set = 0, float dy = 0)
{
    if (variant is null) return null;
    var builder = new MeshBuilder();
    foreach (var mobil in variant.Mobils?.ElementAtOrDefault(set) ?? [])
    {
        if (mobil is null) continue;
        // GeomRotation is in degrees, applied X, then Z, then Y: a wall checkpoint is the floor one stood up (-90 about X),
        // then turned in the wall's plane (Z: 90 Right, -90 Left, 180 Down).
        const float deg = MathF.PI / 180;
        var m = mobil.HasGeomTransformation
            ? Matrix4x4.CreateRotationX(mobil.GeomRotation.X * deg) * Matrix4x4.CreateRotationZ(mobil.GeomRotation.Z * deg) * Matrix4x4.CreateRotationY(mobil.GeomRotation.Y * deg)
              * Matrix4x4.CreateTranslation(mobil.GeomTranslation.X, mobil.GeomTranslation.Y, mobil.GeomTranslation.Z)
            : Matrix4x4.Identity;
        if (dy != 0) m *= Matrix4x4.CreateTranslation(0, dy, 0);
        AddModel(builder, mobil.PrefabFid, RefPath(mobil, "PrefabFid", file, level), file, level, m, 0);
    }
    return builder.IsEmpty ? null : Save(builder);
}

/// <summary>
/// Adds a model: from its own file when it is an external reference (loaded here, relative to the file that references
/// it; GBX.NET does not resolve references of nested files reliably), otherwise the embedded node.
/// </summary>
void AddModel(MeshBuilder b, object? embedded, string? refPath, string ownerPath, int ownerLevel, Matrix4x4 m, int depth)
{
    if (depth > 10) return;
    object? model = embedded;
    var path = ownerPath;
    var level = ownerLevel;
    if (refPath is not null)
    {
        (model, level) = LoadNode(refPath);
        path = refPath;
        if (model is null) { skipped.AddOrUpdate($"missing file {Folder(refPath)}", 1, (_, n) => n + 1); missingFiles.TryAdd(refPath, 0); return; }
    }
    switch (model)
    {
        case CPlugPrefab prefab:
            foreach (var ent in prefab.Ents ?? [])
            {
                var q = new Quaternion(ent.Rotation.X, ent.Rotation.Y, ent.Rotation.Z, ent.Rotation.W);
                var em = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(ent.Position.X, ent.Position.Y, ent.Position.Z) * m;
                var entRef = RefPath(ent, "Model", path, level);
                if (ent.Model is null && entRef is null) continue; // empty entries (spots, markers)
                AddModel(b, ent.Model, entRef, path, level, em, depth + 1);
            }
            break;
        case CPlugStaticObjectModel s:
            AddModel(b, s.Mesh, RefPath(s, "Mesh", path, level), path, level, m, depth + 1);
            break;
        case CPlugDynaObjectModel d:
            AddModel(b, d.Mesh, RefPath(d, "Mesh", path, level), path, level, m, depth + 1);
            break;
        case CPlugSolid2Model solid:
            AddSolid(b, solid, m, path, level);
            break;
        case null:
            break;
        default:
            skipped.AddOrUpdate(model.GetType().Name, 1, (_, n) => n + 1);
            break;
    }
}

/// <summary>Parsed files (node and reference-table ancestor level), shared by all blocks.</summary>
(object? Node, int Level) LoadNode(string path) => nodeCache.GetOrAdd(path, p => new Lazy<(object?, int)>(() =>
{
    if (!File.Exists(p)) return (null, 0);
    try
    {
        var gbx = Gbx.Parse(p);
        return (gbx.Node, gbx.RefTable?.AncestorLevel ?? 0);
    }
    catch { return (null, 0); }
})).Value;

/// <summary>
/// Full path of the external file behind a node property (null when embedded). Reference-table paths are relative to the
/// folder `level` levels above the referencing file's folder.
/// </summary>
static string? RefPath(object owner, string property, string ownerPath, int level)
{
    var refs = owner.GetType().GetProperty("References")?.GetValue(owner);
    var nodeRef = refs?.GetType().GetProperty(property)?.GetValue(refs);
    var file = nodeRef?.GetType().GetProperty("File")?.GetValue(nodeRef) as GBX.NET.Components.GbxRefTableFile;
    return file?.FilePath is { Length: > 0 } relative ? Resolve(ownerPath, level, relative) : null;
}

static string Resolve(string ownerPath, int level, string relative)
{
    var dir = Path.GetDirectoryName(ownerPath)!;
    for (var i = 0; i < level; i++) dir = Path.GetDirectoryName(dir) ?? dir;
    return Path.GetFullPath(Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar)));
}

/// <summary>The files a Gbx file references (its reference table), as full paths. Empty for missing or unreadable files.</summary>
string[] References(string path) => refCache.GetOrAdd(path, p => new Lazy<string[]>(() =>
{
    if (!File.Exists(p) || !p.EndsWith(".gbx", StringComparison.OrdinalIgnoreCase)) return [];
    try
    {
        var gbx = Gbx.Parse(p);
        var level = gbx.RefTable?.AncestorLevel ?? 0;
        return (gbx.RefTable?.Files ?? []).Select(f => Resolve(p, level, f.FilePath)).ToArray();
    }
    catch { return []; }
})).Value;

static string Folder(string path) => Path.GetDirectoryName(path)?.Replace(Path.Combine("OpenplanetNext", "Extract"), "…") ?? path;

void AddSolid(MeshBuilder b, CPlugSolid2Model solid, Matrix4x4 m, string ownerPath, int ownerLevel)
{
    var visuals = solid.Visuals?.ToArray() ?? [];
    var refs = solid.Materials?.EnumerateReferences().ToArray() ?? [];
    var names = refs.Select(r => MaterialName(r?.File?.FilePath)).ToArray();
    // Remember where each material's file is (for its textures).
    for (var i = 0; i < refs.Length; i++)
        if (refs[i]?.File?.FilePath is { Length: > 0 } rel)
            materialFiles.TryAdd(names[i], Resolve(ownerPath, ownerLevel, rel));
    var geoms = solid.ShadedGeoms ?? [];
    // Highest detail only: LOD bit 0 (or the lowest LOD index on files without masks).
    // Detail level: the requested LOD (--lod, 0 = full detail), or the coarsest one the model has if it has fewer.
    // Masked models: a geom is part of LOD k when bit k is set; older ones give one LOD index per geom.
    var masked = geoms.Any(g => g.LodMask != 0);
    var levels = masked
        ? Enumerable.Range(0, 8).Where(k => geoms.Any(g => ((g.LodMask >> k) & 1) != 0)).ToList()
        : geoms.Select(g => g.Lod).Distinct().Order().ToList();
    var level = levels.Count == 0 ? 0 : levels[Math.Min(lodRequest, levels.Count - 1)];
    lodStats.AddOrUpdate(levels.Count, 1, (_, n) => n + 1);
    foreach (var g in geoms.Where(g => masked ? ((g.LodMask >> level) & 1) != 0 : g.Lod == level))
    {
        if (g.VisualIndex < 0 || g.VisualIndex >= visuals.Length || visuals[g.VisualIndex] is not CPlugVisualIndexedTriangles v) continue;
        var stream = v.VertexStreams?.FirstOrDefault(s => s.Positions is { Length: > 0 });
        var positions = stream?.Positions;
        var indices = v.IndexBuffer?.Indices;
        if (positions is null || indices is null || indices.Length < 3) continue;
        // First texture coordinate set (UV0), from the same stream or another one with as many vertices.
        var uvs = stream!.UVs?.Values.FirstOrDefault(u => u?.Length == positions.Length)
            ?? v.VertexStreams!.SelectMany(s => s.UVs?.Values ?? Enumerable.Empty<Vec2[]>()).FirstOrDefault(u => u?.Length == positions.Length);
        var material = g.MaterialIndex >= 0 && g.MaterialIndex < names.Length ? names[g.MaterialIndex] : "Unknown";
        int id;
        lock (materialLock) id = materials.GetOrAdd(material, _ => materials.Count);
        b.Add(id, positions.Select(p => Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m)).ToArray(),
            uvs?.Select(u => new Vector2(u.X, u.Y)).ToArray() ?? new Vector2[positions.Length], indices.Select(i => (int)i).ToArray());
    }
}

static string MaterialName(string? path)
{
    if (string.IsNullOrEmpty(path)) return "Unknown";
    var name = Path.GetFileName(path.Replace('\\', '/'));
    var dot = name.IndexOf('.');
    return dot > 0 ? name[..dot] : name;
}

/// <summary>
/// Quarter turns (about the cell's vertical axis, as Ry(k·90°)) for a top or bottom clip: the game shares such clips
/// between blocks that point different ways (a slope's underside under Slope2 Up, Down, Left, Right) and turns them to
/// the block. Picked as the turn whose height profile matches the block's own mesh over the unit's cell best; flat clips
/// keep 0.
/// </summary>
int CapTurn(MeshRef clip, MeshRef block, Int3 unit, bool topFace, IReadOnlyList<Int3> units)
{
    if (!meshVertices.TryGetValue(clip.File, out var cv) || !meshVertices.TryGetValue(block.File, out var bv)) return 0;
    if (cv.Length == 0) return 0;
    // Caps over several cells (a curve's column top, a loop end's curved trim) span the block's footprint from the unit
    // they hang on: turned so they lie within it (a trim hung on the far corner unit needs a half turn).
    var multiCell = cv.Min(v => Math.Min(v.X, v.Z)) < -1 || cv.Max(v => Math.Max(v.X, v.Z)) > 33;
    if (multiCell) return InFootprintTurn(clip, unit, units);
    // Flat caps (edge trims, plates): turned to lie where the block's own mesh meets that face.
    if (cv.Max(v => v.Y) - cv.Min(v => v.Y) < 2) return FootprintTurn(clip, block, unit, topFace);
    // Uphill directions: of the clip's top profile (highest point per bin, so walls hanging below do not count) and of
    // the block's mesh over the unit's cell.
    const int n = 8;
    static int Bin(float c) => Math.Clamp((int)(c / 32f * n), 0, n - 1);
    var top = new float?[n, n];
    foreach (var v in cv)
    {
        ref var h = ref top[Bin(v.X), Bin(v.Z)];
        h = Math.Max(h ?? float.MinValue, v.Y);
    }
    var clipPoints = new List<(double X, double Z, double Y)>();
    for (var a = 0; a < n; a++)
        for (var b = 0; b < n; b++)
            if (top[a, b] is { } h) clipPoints.Add(((a + 0.5) * 32 / n, (b + 0.5) * 32 / n, h));
    var blockPoints = bv.Select(v => (X: (double)v.X - unit.X * 32, Z: (double)v.Z - unit.Z * 32, Y: (double)v.Y))
        .Where(v => v.X >= 0 && v.X <= 32 && v.Z >= 0 && v.Z <= 32).ToList();
    var (cx, cz) = Gradient(clipPoints);
    var (bx, bz) = Gradient(blockPoints);
    if (Math.Sqrt(bx * bx + bz * bz) < 0.05 || Math.Sqrt(cx * cx + cz * cz) < 0.05) return 0;
    var best = 0;
    var bestDot = double.MinValue;
    for (var k = 0; k < 4; k++)
    {
        double cos = Math.Round(Math.Cos(k * Math.PI / 2)), sin = Math.Round(Math.Sin(k * Math.PI / 2));
        var dot = (cx * cos + cz * sin) * bx + (-cx * sin + cz * cos) * bz;
        if (dot > bestDot + 1e-9) { bestDot = dot; best = k; }
    }
    return best;
}

/// <summary>
/// Quarter turn for a flat cap: the game shares edge trims and plates between blocks (one trim modelled along the cell's
/// South edge serves walls and loops on any edge) and turns them to the block. Picked as the turn that puts most of the
/// clip over the block's own surface at that face's height (within the unit's cell); 0 when none is clearly better.
/// </summary>
int FootprintTurn(MeshRef clip, MeshRef block, Int3 unit, bool top)
{
    if (!meshSamples.TryGetValue(clip.File, out var cs) || !meshSamples.TryGetValue(block.File, out var bs)) return 0;
    var height = unit.Y * 8f + (top ? 8 : 0);
    const float reach = 1.25f;
    var near = bs.Value
        .Select(v => (X: v.X - unit.X * 32f, Z: v.Z - unit.Z * 32f, v.Y))
        .Where(v => v.X >= -0.5f && v.X <= 32.5f && v.Z >= -0.5f && v.Z <= 32.5f && Math.Abs(v.Y - height) <= 2f)
        .ToList();
    if (near.Count == 0) return 0;
    // Grid of the block's surface near the face (2.5 m cells) for quick look-ups.
    var grid = new HashSet<(int, int)>(near.Select(v => ((int)Math.Floor(v.X / reach), (int)Math.Floor(v.Z / reach))));
    bool Covered(double x, double z)
    {
        int gx = (int)Math.Floor(x / reach), gz = (int)Math.Floor(z / reach);
        for (var a = -1; a <= 1; a++)
            for (var b = -1; b <= 1; b++)
                if (grid.Contains((gx + a, gz + b))) return true;
        return false;
    }
    // A plate with trims turns by its trims: its floor overlaps the block alike in several turns.
    var points = trimSamples.TryGetValue(clip.File, out var ts) && ts.Value.Length > 0 ? ts.Value : cs.Value;
    var scores = new double[4];
    for (var k = 0; k < 4; k++)
    {
        double cos = Math.Round(Math.Cos(k * Math.PI / 2)), sin = Math.Round(Math.Sin(k * Math.PI / 2));
        var hit = 0;
        foreach (var p in points)
        {
            double x = p.X - 16, z = p.Z - 16;
            if (Covered(16 + x * cos + z * sin, 16 - x * sin + z * cos)) hit++;
        }
        scores[k] = hit / (double)Math.Max(1, points.Length);
    }
    var best = 0;
    for (var k = 1; k < 4; k++) if (scores[k] > scores[best]) best = k;
    // Plates count mostly by their large flat part, which overlaps alike in every turn; the edge decides.
    // A clear margin wins on its own: a quarter-disc underside under a quarter-disc platform overlaps the wrong way round
    // by ~80 % (the coverage test is a few metres coarse), the right way by ~100 %.
    var margin = scores[best] - scores[0];
    if (Environment.GetEnvironmentVariable("TURN_DEBUG") is { } dbg && block.File.Contains(dbg)) Console.WriteLine($"turn {clip.File} on {block.File}: {string.Join(" ", scores.Select(s => s.ToString("0.00")))}");
    return margin > 0.1 || margin > 0.02 && scores[best] > scores[0] * 1.25 ? best : 0;
}

/// <summary>
/// Quarter turn (about the unit cell's centre, as placed) that puts most of a multi-cell cap over the block's own units
/// (on X/Z); 0 unless another turn is clearly better.
/// </summary>
int InFootprintTurn(MeshRef clip, Int3 unit, IReadOnlyList<Int3> units)
{
    if (!meshSamples.TryGetValue(clip.File, out var cs) || cs.Value.Length == 0) return 0;
    var cells = units.Select(u => (u.X, u.Z)).ToHashSet();
    var scores = new double[4];
    for (var k = 0; k < 4; k++)
    {
        double cos = Math.Round(Math.Cos(k * Math.PI / 2)), sin = Math.Round(Math.Sin(k * Math.PI / 2));
        var hit = 0;
        foreach (var p in cs.Value)
        {
            double x = p.X - 16, z = p.Z - 16;
            double bx = unit.X * 32 + 16 + x * cos + z * sin, bz = unit.Z * 32 + 16 - x * sin + z * cos;
            if (cells.Contains(((int)Math.Floor(bx / 32), (int)Math.Floor(bz / 32)))) hit++;
        }
        scores[k] = hit / (double)cs.Value.Length;
    }
    var best = 0;
    for (var k = 1; k < 4; k++) if (scores[k] > scores[best] + 1e-9) best = k;
    return scores[best] - scores[0] > 0.1 ? best : 0;
}

/// <summary>Least-squares plane y = a·x + b·z + c through points: its uphill gradient (a, b).</summary>
static (double A, double B) Gradient(List<(double X, double Z, double Y)> p)
{
    if (p.Count < 3) return (0, 0);
    double mx = p.Average(q => q.X), mz = p.Average(q => q.Z), my = p.Average(q => q.Y);
    double sxx = 0, szz = 0, sxz = 0, sxy = 0, szy = 0;
    foreach (var (x, z, y) in p)
    {
        double dx = x - mx, dz = z - mz, dy = y - my;
        sxx += dx * dx; szz += dz * dz; sxz += dx * dz; sxy += dx * dy; szy += dz * dy;
    }
    var det = sxx * szz - sxz * sxz;
    return Math.Abs(det) < 1e-9 ? (0, 0) : ((sxy * szz - szy * sxz) / det, (szy * sxx - sxy * sxz) / det);
}

MeshRef Save(MeshBuilder b)
{
    var (bytes, groups, min, max) = b.Serialize();
    var hash = Convert.ToHexString(SHA1.HashData(bytes))[..16].ToLowerInvariant();
    var file = $"mesh/{hash}.bin";
    // Blocks share meshes; the first one to get here writes the file (GetOrAdd alone may run the factory twice).
    if (meshes.TryAdd(file, new MeshInfo(file, groups, min, max)))
        File.WriteAllBytes(Path.Combine(outDir, file), bytes);
    meshVertices.TryAdd(file, b.Vertices.ToArray());
    meshSamples.TryAdd(file, new Lazy<Vector3[]>(() => b.Samples().ToArray()));
    trimSamples.TryAdd(file, new Lazy<Vector3[]>(() => b.Samples(m => !IsFloorMaterial(m)).ToArray()));
    return new MeshRef(file);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TmSpline.sln"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

record MeshRef(string File);
record PillarInfo(string Block, int[] Offset, int Dir);
/// <summary>A unit face's free clip: unit offset, face (0..5 = North, East, South, West, Top, Bottom) and mesh.</summary>
/// <param name="Mesh">The free clip shown when the face is not connected (null: the face only offers groups).</param>
/// <param name="Connects">Clip groups of a neighbouring face that hide the free clip.</param>
/// <param name="Offers">Clip groups this face offers to its neighbours.</param>
/// <param name="Turn">Top/bottom clips: quarter turns Ry(k·90°) about the cell's centre (see CapTurn).</param>
/// <param name="Stack">Vertical clips: shapes as the middle, top, bottom of a wall over several levels, or alone.</param>
/// <param name="StackGroup">Vertical clips: their VerticalClipGroupId (walls of the same group continue each other).</param>
/// <param name="Options">Every free clip of the face (name, mesh, groups it connects to), in the file's order.</param>
record ClipInfo(int[] Unit, int Face, MeshRef? Mesh, string[]? Connects, string[]? Offers, int Turn, MeshRef?[]? Stack, string? StackGroup, ClipOption[]? Options);
/// <summary>One free clip of a face: its name, mesh (null: none for this height) and the groups that hide it.</summary>
record ClipOption(string Name, MeshRef? Mesh, string[] Connects, MeshRef?[]? Stack, string? StackGroup);
record VariantInfo(int[] Size, int[][] Units, MeshRef? Mesh, PillarInfo[]? Pillars, ClipInfo[]? Clips);
/// <param name="AirPillar">The air variant used when a column stands below the block (null: same as Air).</param>
record BlockInfo(string Folder, VariantInfo? Ground, VariantInfo? Air, string? Icon, string Page, int Catalog, bool Pillar, string? Modifier,
    VariantInfo? AirPillar, VariantInfo? GroundPillar);
record Group(int M, int V, int I);
record MeshInfo(string File, Group[] Groups, float[] Min, float[] Max);

/// <summary>Triangles per material, in block-local metres (0..32 per grid unit on X/Z, 8 m per unit on Y).</summary>
sealed class MeshBuilder
{
    readonly SortedDictionary<int, (List<Vector3> V, List<Vector2> T, List<int> I)> groups = new();
    public bool IsEmpty => groups.Count == 0;
    public IEnumerable<Vector3> Vertices => groups.Values.SelectMany(g => g.V);

    /// <summary>Points spread over the triangles (about every metre), so large faces count where their vertices are few.</summary>
    public IEnumerable<Vector3> Samples(Func<int, bool>? keep = null)
    {
        foreach (var (material, g) in groups)
            if (keep is null || keep(material))
            for (var i = 0; i + 2 < g.I.Count; i += 3)
            {
                Vector3 a = g.V[g.I[i]], b = g.V[g.I[i + 1]], c = g.V[g.I[i + 2]];
                var longest = Math.Max(Vector3.Distance(a, b), Math.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
                var n = Math.Clamp((int)Math.Ceiling(longest / 1f), 1, 48);
                for (var p = 0; p <= n; p++)
                    for (var q = 0; p + q <= n; q++)
                        yield return a + (b - a) * (p / (float)n) + (c - a) * (q / (float)n);
            }
    }

    public void Add(int material, Vector3[] positions, Vector2[] uvs, int[] indices)
    {
        if (!groups.TryGetValue(material, out var g)) groups[material] = g = (new(), new(), new());
        var baseIndex = g.V.Count;
        g.V.AddRange(positions);
        g.T.AddRange(uvs);
        for (var i = 0; i + 2 < indices.Length; i += 3)
            if (indices[i] < positions.Length && indices[i + 1] < positions.Length && indices[i + 2] < positions.Length)
            { g.I.Add(indices[i] + baseIndex); g.I.Add(indices[i + 1] + baseIndex); g.I.Add(indices[i + 2] + baseIndex); }
    }

    public (byte[] Bytes, Group[] Groups, float[] Min, float[] Max) Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var list = new List<Group>();
        // Layout (format 2): all groups' positions (Float32 x,y,z), then all groups' UV0 (Float32 u,v), then all groups'
        // indices (Uint32, relative to the group).
        foreach (var (_, g) in groups)
            foreach (var v in g.V) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        foreach (var (_, g) in groups)
            foreach (var t in g.T) { w.Write(t.X); w.Write(t.Y); }
        foreach (var (m, g) in groups)
        {
            foreach (var i in g.I) w.Write(i);
            list.Add(new Group(m, g.V.Count, g.I.Count));
        }
        return (ms.ToArray(), list.ToArray(), [min.X, min.Y, min.Z], [max.X, max.Y, max.Z]);
    }
}
