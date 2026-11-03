using System.IO.Compression;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;
using GBX.NET.LZO;
using TmSpline.Core.Geometry;
using TmSpline.Core.Model;

namespace TmSpline.Export;

public sealed class MapExportOptions
{
    public required string MapName { get; init; }
    /// <summary>Folder below Items\ that holds this track's items, e.g. "TMSpline\MyTrack".</summary>
    public required string ItemFolder { get; init; }
    /// <summary>Where the track origin goes in the map. Default: horizontal map center at ground level.</summary>
    public Vec3d? Placement { get; init; }
    /// <summary>Remove all blocks and items of the template and give the map a fresh UID.</summary>
    public bool ClearTemplate { get; init; } = true;
    /// <summary>Laps for multilap maps (used when one of the items is a start/finish).</summary>
    public int Laps { get; init; } = 3;
    /// <summary>Use the decoration without the stadium (the template's has it).</summary>
    public bool NoStadium { get; init; }
    /// <summary>Account login (the 22-character account id) to put in as map author. Null keeps the template's.</summary>
    public string? AuthorLogin { get; init; }
    public string? AuthorNickname { get; init; }
    /// <summary>Vanilla blocks to place (by name; the game supplies their meshes).</summary>
    public IReadOnlyList<VanillaBlock> Blocks { get; init; } = [];

    /// <summary>Vanilla items to place (by name, from the game's Stadium collection).</summary>
    public IReadOnlyList<VanillaItem> Items { get; init; } = [];
}

public static class MapExporter
{
    /// <summary>World height of the stadium ground (block Y = 9).</summary>
    public const double GroundY = 8;
    /// <summary>Block Y of the ground level.</summary>
    public const int GroundBlockY = 9;
    /// <summary>The game's collection number of Stadium (2020), as its maps store it for vanilla items.</summary>
    public const int StadiumCollection = 26;

    /// <summary>The template's decoration (stadium, day) and the same without the stadium (seen in maps saved by the game).</summary>
    public const string StadiumDecoration = "48x48Screen155Day", NoStadiumDecoration = "NoStadium48x48Day";

    /// <summary>The mood names the game writes into the map's header for its decorations.</summary>
    private static readonly Dictionary<string, string> Moods = new()
    {
        [StadiumDecoration] = "Day",
        [NoStadiumDecoration] = "Day (no stadium)",
    };

    static MapExporter()
    {
        Gbx.LZO = new Lzo();
    }

    /// <summary>
    /// Loads the template map, removes items previously generated into the same item folder, embeds the given items
    /// and places all of them at the track origin without rotation (the chunk meshes already carry their offset).
    /// </summary>
    public static void Export(Stream template, Stream output, IReadOnlyList<ImportedItem> items, MapExportOptions options, Action<string>? log = null)
    {
        var gbx = Gbx.Parse<CGameCtnChallenge>(template);
        var map = gbx.Node;
        map.MapName = options.MapName;

        if (options.ClearTemplate)
        {
            // Start from an empty map with its own identity so it never collides with the template,
            // and without items the template embedded for itself.
            map.RemoveAll();
            map.EmbeddedZipData = null;
            map.MapUid = NewMapUid();
            log?.Invoke($"Cleared template, new map UID {map.MapUid}");
        }

        if (options.AuthorLogin is { Length: > 0 } login)
        {
            map.AuthorLogin = login;
            map.MapInfo = new Ident(map.MapUid, map.MapInfo.Collection, login);
        }
        if (options.AuthorNickname is { Length: > 0 } nickname)
            map.AuthorNickname = nickname;

        var prefix = options.ItemFolder.TrimEnd('\\') + "\\";
        var removed = map.RemoveAnchoredObjects(o => o.ItemModel.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            log?.Invoke($"Removed {removed} previously generated items");

        map.UpdateEmbeddedZipData(zip =>
        {
            foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('/', '\\').StartsWith("Items\\" + prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                entry.Delete();
            foreach (var item in items)
                zip.CreateEntryFromFile(item.ItemGbxPath, ("Items/" + item.RelativeItemPath).Replace('\\', '/'));
        });

        if (options.NoStadium)
            map.Decoration = new Ident(NoStadiumDecoration, map.Decoration.Collection, map.Decoration.Author);

        var origin = options.Placement ?? new Vec3d(map.Size.X * 32 / 2.0, GroundY, map.Size.Z * 32 / 2.0);
        log?.Invoke($"Map size {map.Size}, placing track origin at {origin}");
        foreach (var item in items)
        {
            var ident = ReadIdent(item);
            var p = origin + item.Offset;
            var r = item.PitchYawRoll;
            var placed = map.PlaceAnchoredObject(ident, new Vec3((float)p.X, (float)p.Y, (float)p.Z), new Vec3((float)r.X, (float)r.Y, (float)r.Z), Vec3.Zero);
            if (item.Color != ItemColor.Default)
                placed.Color = (DifficultyColor)(int)item.Color;
            if (WaypointTag(item.Waypoint) is { } tag)
                placed.WaypointSpecialProperty = WaypointProperty(tag);
            log?.Invoke($"Placed {ident.Id} at {p}{(item.Waypoint == Waypoint.None ? "" : $" ({item.Waypoint})")}");
        }

        // Vanilla blocks: cell 0 starts at the map center (where the track origin is), level 0 is the ground.
        foreach (var block in options.Blocks)
        {
            if (block.Free)
            {
                // As the game stores free blocks: coord (-1, 0, -1), North, the free flag, the world position of the
                // block's origin and yaw/pitch/roll in radians (checked against free blocks in maps saved by the game).
                var at = origin + block.Position;
                if (at.X < 0 || at.Z < 0 || at.X > map.Size.X * 32 || at.Z > map.Size.Z * 32)
                {
                    log?.Invoke($"Skipped free block {block.Name} outside the map at {at}");
                    continue;
                }
                var free = map.PlaceBlock(block.Name, new Int3(-1, 0, -1), Direction.North, isGround: false, variant: 0, subVariant: 0);
                free.IsFree = true;
                free.AbsolutePositionInMap = new Vec3((float)at.X, (float)at.Y, (float)at.Z);
                free.YawPitchRoll = new Vec3((float)(block.Yaw * Math.PI / 180), (float)(block.Pitch * Math.PI / 180), (float)(block.Roll * Math.PI / 180));
                if (block.Color != ItemColor.Default)
                    free.Color = (DifficultyColor)(int)block.Color;
                log?.Invoke($"Placed free block {block.Name} at {at} (yaw {block.Yaw}°, pitch {block.Pitch}°, roll {block.Roll}°), flags 0x{free.Flags:X8}");
                continue;
            }
            var coord = new Int3(block.X + map.Size.X / 2, block.Y + GroundBlockY, block.Z + map.Size.Z / 2);
            if (coord.X < 0 || coord.Z < 0 || coord.X >= map.Size.X || coord.Z >= map.Size.Z)
            {
                log?.Invoke($"Skipped block {block.Name} outside the map at {coord}");
                continue;
            }
            var placed = map.PlaceBlock(block.Name, coord, (Direction)(block.Dir & 3), isGround: block.Y == 0, variant: (byte)block.Variant, subVariant: 0);
            // Columns under air blocks are pillar blocks with the pillar flag, as the game's editor saves them.
            if (block.Pillar)
                placed.IsPillar = true;
            if (block.ExtraFlags != 0)
                placed.Flags |= block.ExtraFlags & VanillaBlock.AllowedExtraFlags;
            if (block.Color != ItemColor.Default)
                placed.Color = (DifficultyColor)(int)block.Color;
            log?.Invoke($"Placed {(block.Pillar ? "pillar" : "block")} {block.Name} at {coord} ({(Direction)(block.Dir & 3)}), flags 0x{placed.Flags:X8}");
        }

        // Vanilla items (trees): by name from the game's own collection, as the game's editor saves them (seen in maps:
        // "SpringTreeTall", author Nadeo, collection stored as the NUMBER 26 (Stadium2020; the string form is not found:
        // "Missing items"), the cell it stands in as BlockUnitCoord (x/32, y/8 + 8, z/32); the position is the item's base,
        // yaw in radians).
        foreach (var item in options.Items)
        {
            var at = origin + item.Position;
            if (at.X < 0 || at.Z < 0 || at.X > map.Size.X * 32 || at.Z > map.Size.Z * 32)
                continue;
            var placed = map.PlaceAnchoredObject(new Ident(item.Name, new Id(StadiumCollection), "Nadeo"),
                new Vec3((float)at.X, (float)at.Y, (float)at.Z),
                new Vec3((float)(item.Yaw * Math.PI / 180), (float)(item.Pitch * Math.PI / 180), (float)(item.Roll * Math.PI / 180)), Vec3.Zero);
            if (item.Color != ItemColor.Default)
                placed.Color = (DifficultyColor)(int)item.Color;
            placed.BlockUnitCoord = new Byte3((byte)Math.Clamp((int)Math.Floor(at.X / 32), 0, 255),
                (byte)Math.Clamp((int)Math.Floor(at.Y / 8) + 8, 0, 255), (byte)Math.Clamp((int)Math.Floor(at.Z / 32), 0, 255));
        }
        if (options.Items.Count > 0)
            log?.Invoke($"Placed {options.Items.Count} vanilla items");

        FitHeight(map, log);

        // Multilap: a start/finish gate makes it a lap race.
        var laps = items.Any(i => i.Waypoint == Waypoint.StartFinish);
        map.IsLapRace = laps;
        map.NbLaps = laps ? Math.Clamp(options.Laps, 1, 99) : 3;

        SyncHeaderXml(map);
        gbx.Save(output);
    }

    /// <summary>Highest map size in block rows (8 m each; the ground is row <see cref="GroundBlockY"/>): the size is a byte.</summary>
    public const int MaxMapRows = 255;

    /// <summary>
    /// Makes the map tall enough for what is in it: the template's height (40 rows, 248 m above the ground) or more, up
    /// to <see cref="MaxMapRows"/>. Items only tell where their pivot is, not how far their mesh reaches up, so they get a
    /// generous margin (a 128 m track chunk can climb a lot); blocks get a few rows for their own height.
    /// </summary>
    private static void FitHeight(CGameCtnChallenge map, Action<string>? log)
    {
        var rows = map.Size.Y;
        foreach (var o in map.GetAnchoredObjects())
            rows = Math.Max(rows, (int)Math.Floor(o.AbsolutePositionInMap.Y / 8) + 8 + 16);
        foreach (var b in map.GetBlocks())
            rows = Math.Max(rows, (b.IsFree && b.AbsolutePositionInMap is { } at ? (int)Math.Floor(at.Y / 8) + 8 : b.Coord.Y) + 8);
        rows = Math.Min(rows, MaxMapRows);
        if (rows == map.Size.Y) return;
        map.Size = new Int3(map.Size.X, rows, map.Size.Z);
        log?.Invoke($"Map made {rows} rows tall for its content");
    }

    public static void Export(string templateMapPath, string outputMapPath, IReadOnlyList<ImportedItem> items, MapExportOptions options, Action<string>? log = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMapPath))!);
        using var template = File.OpenRead(templateMapPath);
        using var output = File.Create(outputMapPath);
        Export(template, output, items, options, log);
    }

    /// <summary>
    /// The header XML (shown in the game's map browser) is stored as-is by GBX.NET, so keep its ident in sync with
    /// the map: uid, name, author and zone.
    /// </summary>
    public static void SyncHeaderXml(CGameCtnChallenge map)
    {
        if (string.IsNullOrEmpty(map.Xml))
            return;
        var doc = System.Xml.Linq.XDocument.Parse(map.Xml);
        if (doc.Root?.Element("ident") is { } ident)
        {
            ident.SetAttributeValue("uid", map.MapUid);
            ident.SetAttributeValue("name", map.MapName);
            ident.SetAttributeValue("author", map.AuthorLogin);
            ident.SetAttributeValue("authorzone", map.AuthorZone ?? "World");
        }
        if (doc.Root?.Element("desc") is { } desc && Moods.TryGetValue(map.Decoration.Id, out var mood))
            desc.SetAttributeValue("mood", mood);
        map.Xml = doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    /// <summary>A waypoint property built like the game's own: chunk 2E009000 (version 2) and 2E009001.</summary>
    private static CGameWaypointSpecialProperty WaypointProperty(string tag)
    {
        var property = new CGameWaypointSpecialProperty();
        property.CreateChunk<CGameWaypointSpecialProperty.Chunk2E009000>().Version = 2;
        property.CreateChunk<CGameWaypointSpecialProperty.Chunk2E009001>();
        property.Tag = tag;
        property.Order = 0;
        return property;
    }

    /// <summary>Waypoint tags as the game stores them on placed blocks and items.</summary>
    private static string? WaypointTag(Waypoint waypoint) => waypoint switch
    {
        Waypoint.Start => "Spawn",
        Waypoint.Checkpoint => "Checkpoint",
        Waypoint.Finish => "Goal",
        Waypoint.StartFinish => "StartFinish",
        _ => null,
    };

    /// <summary>Map UIDs are 27 characters of URL-safe base64, like the ones the game generates.</summary>
    private static string NewMapUid()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(21);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_')[..27];
    }

    private static Ident ReadIdent(ImportedItem item)
    {
        var header = Gbx.ParseHeaderNode<CGameItemModel>(item.ItemGbxPath);
        var author = header.Ident?.Author ?? "";
        // Placed custom items are referenced by their path relative to the Items folder.
        return new Ident(item.RelativeItemPath, new Id(26), author);
    }
}
