using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;

namespace TmSpline.Export;

/// <summary>What <see cref="MapMerge.Merge"/> did.</summary>
public sealed record MapMergeResult(int RemovedBlocks, int RemovedItems, int KeptBlocks, int KeptItems, int AddedBlocks, int AddedItems)
{
    public override string ToString() =>
        $"removed {RemovedBlocks} blocks and {RemovedItems} items of the previous export, kept {KeptBlocks} blocks and {KeptItems} items, added {AddedBlocks} blocks and {AddedItems} items";
}

/// <summary>
/// Puts a new TMSpline export into a map that was edited in the game's editor, keeping what was added there: the
/// edited map was the previous export plus the game edits, so whatever of the previous export is still in it unchanged
/// (same block or item at the same place) is TMSpline's and gets replaced by the new export's content; everything else
/// (added, or moved since) stays.
/// </summary>
public static class MapMerge
{
    static MapMerge()
    {
        Gbx.LZO = new Lzo();
    }

    /// <param name="edited">The map as the game saved it (the previous export with the game edits).</param>
    /// <param name="previous">The previous export as TMSpline wrote it (what of <paramref name="edited"/> is TMSpline's).</param>
    /// <param name="previousItemFolder">Its item folder below Items\ (its road items, embedded in the map).</param>
    /// <param name="next">The new export.</param>
    /// <param name="nextItemFolder">Its item folder.</param>
    public static MapMergeResult Merge(Stream edited, Stream previous, string previousItemFolder, Stream next, string nextItemFolder,
        Stream output, Action<string>? log = null)
    {
        var gbx = Gbx.Parse<CGameCtnChallenge>(edited);
        var map = gbx.Node;
        var old = Gbx.ParseNode<CGameCtnChallenge>(previous);
        var add = Gbx.ParseNode<CGameCtnChallenge>(next);
        // Road items of any export of this track (every export gets its own folder "TMSpline\<name>_<id>"): also those of
        // exports this merge has no record of (written by another server, or before the record was kept).
        var oldPrefix = Family(previousItemFolder);
        var newPrefix = Family(nextItemFolder);

        // Blocks: the previous export's ones that are still there as exported.
        var blocks = map.Blocks ??= [];
        var ownedBlocks = new Matcher<CGameCtnBlock>(old.Blocks ?? [], b => b.Name, BlockPosition);
        var keptBlocks = blocks.Where(b => !ownedBlocks.Take(b.Name, BlockPosition(b), BlockAngles(b), BlockAngles)).ToList();
        var removedBlocks = blocks.Count - keptBlocks.Count;

        // Items: the previous export's road items by their folder (all of them, wherever they are now), and its vanilla
        // items (trees, pillars) that are still where it put them.
        var items = map.AnchoredObjects ??= [];
        var ownedItems = new Matcher<CGameCtnAnchoredObject>((old.AnchoredObjects ?? []).Where(o => !IsIn(o, oldPrefix)),
            o => o.ItemModel.Id, o => o.AbsolutePositionInMap);
        var keptItems = items.Where(o => !IsIn(o, oldPrefix) && !IsIn(o, newPrefix)
            && !ownedItems.Take(o.ItemModel.Id, o.AbsolutePositionInMap, o.YawPitchRoll, i => i.YawPitchRoll)).ToList();
        var removedItems = items.Count - keptItems.Count;
        log?.Invoke($"Edited map: {blocks.Count} blocks, {items.Count} items; {removedBlocks} blocks and {removedItems} items are the previous export's");

        var addedBlocks = add.Blocks ?? [];
        var addedItems = add.AnchoredObjects ?? [];
        map.Blocks = [.. keptBlocks, .. addedBlocks];
        map.AnchoredObjects = [.. keptItems, .. addedItems];

        // The road items travel inside the map: swap the previous export's for the new one's.
        var newEntries = new List<(string Name, byte[] Data)>();
        if (add.EmbeddedZipData is { Length: > 0 })
        {
            using var zip = add.OpenReadEmbeddedZipData();
            foreach (var entry in zip.Entries.Where(e => InFolder(e.FullName, newPrefix)))
            {
                using var s = entry.Open();
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                newEntries.Add((entry.FullName, copy.ToArray()));
            }
        }
        map.UpdateEmbeddedZipData(zip =>
        {
            foreach (var entry in zip.Entries.Where(e => InFolder(e.FullName, oldPrefix) || InFolder(e.FullName, newPrefix)).ToList())
                entry.Delete();
            foreach (var (name, data) in newEntries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        });

        // TMSpline decides the name and the race settings; the rest (medals, map info) stays as saved in the game. The map
        // is as tall as the taller of the two (the new export grows to fit its content), and the decoration (stadium or
        // not) follows TMSpline when it changed there since the previous export; otherwise the game's stays.
        map.MapName = add.MapName;
        map.IsLapRace = add.IsLapRace;
        map.NbLaps = add.NbLaps;
        if (add.Size.Y > map.Size.Y)
            map.Size = new Int3(map.Size.X, add.Size.Y, map.Size.Z);
        if (add.Decoration.Id != old.Decoration.Id)
            map.Decoration = add.Decoration;
        MapExporter.SyncHeaderXml(map);
        gbx.Save(output);

        var result = new MapMergeResult(removedBlocks, removedItems, keptBlocks.Count, keptItems.Count, addedBlocks.Count, addedItems.Count);
        log?.Invoke($"Merged: {result}");
        return result;
    }

    /// <summary>"TMSpline\Name_1a2b3c4d" → "TMSpline\Name_" (the folders of all exports of that track); other folders as they are.</summary>
    private static string Family(string itemFolder)
    {
        var folder = itemFolder.TrimEnd('\\');
        var underscore = folder.LastIndexOf('_');
        return underscore > folder.LastIndexOf('\\') && folder.Length - underscore == 9 && folder[(underscore + 1)..].All(char.IsAsciiHexDigit)
            ? folder[..(underscore + 1)]
            : folder + "\\";
    }

    private static bool IsIn(CGameCtnAnchoredObject o, string prefix) => InFamily(o.ItemModel.Id, prefix);

    /// <summary>Whether an item path lies in a folder of the family (prefix "…_" then exactly 8 hex digits and a backslash).</summary>
    private static bool InFamily(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (prefix.EndsWith('\\')) return true;
        var rest = path.AsSpan(prefix.Length);
        return rest.Length > 9 && rest[8] == '\\' && rest[..8].ToString().All(char.IsAsciiHexDigit);
    }

    private static bool InFolder(string entry, string prefix) =>
        InFamily(entry.Replace('/', '\\'), "Items\\" + prefix);

    /// <summary>Where a block is: its cell (and direction) for grid blocks, its world position for free ones.</summary>
    private static Vec3 BlockPosition(CGameCtnBlock b) =>
        b.IsFree ? b.AbsolutePositionInMap ?? default : new Vec3(b.Coord.X, b.Coord.Y, b.Coord.Z);

    private static Vec3 BlockAngles(CGameCtnBlock b) =>
        b.IsFree ? b.YawPitchRoll ?? default : new Vec3((int)b.Direction, 0, 0);

    /// <summary>
    /// Owned objects by name and position (1 m grid), each matched once: the game stores positions as floats like we
    /// do, so a few millimetres (and angles a full turn apart) count as the same place.
    /// </summary>
    private sealed class Matcher<T>
    {
        private const float PositionTolerance = 0.02f;
        private const float AngleTolerance = 0.002f;
        private readonly Dictionary<(string, int, int, int), List<T>> _cells = [];

        public Matcher(IEnumerable<T> owned, Func<T, string> name, Func<T, Vec3> position)
        {
            _position = position;
            foreach (var o in owned)
            {
                var key = Key(name(o), position(o));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(o);
            }
        }

        private readonly Func<T, Vec3> _position;

        /// <summary>True (and the owned object is used up) when an owned object with this name is at this place.</summary>
        public bool Take(string name, Vec3 position, Vec3 angles, Func<T, Vec3> ownedAngles)
        {
            var (_, cx, cy, cz) = Key(name, position);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!_cells.TryGetValue((name, cx + dx, cy + dy, cz + dz), out var list)) continue;
                var i = list.FindIndex(o => Close(_position(o), position) && SameAngles(ownedAngles(o), angles));
                if (i < 0) continue;
                list.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static (string, int, int, int) Key(string name, Vec3 p) =>
            (name, (int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));

        private static bool Close(Vec3 a, Vec3 b) =>
            MathF.Abs(a.X - b.X) <= PositionTolerance && MathF.Abs(a.Y - b.Y) <= PositionTolerance && MathF.Abs(a.Z - b.Z) <= PositionTolerance;

        private static bool SameAngles(Vec3 a, Vec3 b) => SameAngle(a.X, b.X) && SameAngle(a.Y, b.Y) && SameAngle(a.Z, b.Z);

        private static bool SameAngle(float a, float b)
        {
            var d = MathF.IEEERemainder(a - b, MathF.Tau);
            return MathF.Abs(d) <= AngleTolerance;
        }
    }
}
