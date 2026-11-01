using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
Gbx.LZO = new Lzo();
if (args[0] == "--waypoints") { foreach (var f in args.Skip(1)) try { WaypointsDump.Run(f); } catch (Exception ex) { Console.WriteLine(f + ": " + ex.Message); } return; }
if (args[0] == "--colortype") { var t = typeof(GBX.NET.Engines.Game.CGameCtnAnchoredObject).GetProperty("Color")!.PropertyType; Console.WriteLine(t.FullName + ": " + string.Join(", ", Enum.GetNames(t).Zip(Enum.GetValues(t).Cast<object>().Select(Convert.ToInt32), (n, v) => $"{n}={v}"))); return; }
if (args[0] == "--colors") { var m = GBX.NET.Gbx.ParseNode<GBX.NET.Engines.Game.CGameCtnChallenge>(args[1]); foreach (var g in (m.AnchoredObjects ?? []).GroupBy(a => a.Color)) Console.WriteLine($"{g.Key}: {g.Count()} items, e.g. {g.First().ItemModel.Id}"); foreach (var b in m.Blocks ?? []) Console.WriteLine($"block {b.Name} {b.Coord} color={b.Color} pillar={b.IsPillar}"); return; }
if (args[0] == "--obj") { ObjExport.Run(args[1], args[2]); return; }
if (args[0] == "--free") { FreeBlocks.Run(args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a])); return; }
if (args[0] == "--pillars") { Pillars.Run(args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a])); return; }
if (args[0] == "--pillar-patterns") { PillarPatterns.Run(args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a])); return; }
if (args[0] == "--tree") { Tree.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 6); return; }
if (args[0] == "--refs") { Refs.Run(args[1]); return; }
if (args[0] == "--pivot") { foreach (var f in args.Skip(1)) Pivot.Run(f); return; }
if (args[0] == "--item") { ItemShape.Run(args[1]); return; }
if (args[0] == "--chunks") { ChunkInfo.Run(args[1]); return; }
if (args[0] == "--reflect") { Reflect.Run(args[1]); return; }
if (args[0] == "--items")
{
    // Item models used by maps (id, collection, author) with counts, e.g. to see how vanilla items are referenced.
    var seen = new Dictionary<string, int>();
    foreach (var f in args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a]))
        try
        {
            var m = Gbx.ParseNode<CGameCtnChallenge>(f);
            foreach (var o in m.AnchoredObjects ?? [])
            {
                var k = $"{o.ItemModel.Id} | {o.ItemModel.Collection} | {o.ItemModel.Author}";
                // One example placement per vanilla item: where the game puts its base, and its turn.
                if (!seen.ContainsKey(k) && o.ItemModel.Author == "Nadeo")
                {
                    Console.WriteLine($"  e.g. {o.ItemModel.Id}: pos {o.AbsolutePositionInMap} ypr {o.YawPitchRoll} pivot {o.PivotPosition} in {Path.GetFileName(f)}");
                    // How the ident is stored (collection as a number or a string), and the object's other fields.
                    var c = o.ItemModel.Collection;
                    Console.WriteLine($"    collection: {string.Join(", ", c.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).Select(p => $"{p.Name}={p.GetValue(c)}"))}");
                    Console.WriteLine($"    object: {string.Join(", ", o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && (p.PropertyType.IsValueType || p.PropertyType == typeof(string))).Select(p => { try { return $"{p.Name}={p.GetValue(o)}"; } catch { return p.Name + "=?"; } }))}");
                }
                seen[k] = seen.GetValueOrDefault(k) + 1;
            }
        }
        catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(f)}: {ex.Message}"); }
    foreach (var (k, n) in seen.OrderByDescending(x => x.Value).Take(80)) Console.WriteLine($"{n,6}  {k}");
    return;
}
if (args[0] == "--variants")
{
    // Block variants as the game saves them: name pattern, then maps or folders. Counts per (name, ground, variant).
    var pattern = new System.Text.RegularExpressions.Regex(args[1]);
    var counts = new Dictionary<string, int>();
    foreach (var f in args.Skip(2).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a]))
        try
        {
            foreach (var b in Gbx.ParseNode<CGameCtnChallenge>(f).Blocks ?? [])
                if (pattern.IsMatch(b.Name))
                {
                    var k = $"{b.Name} ground={b.IsGround} pillar={b.IsPillar} variant={b.Variant} sub={b.SubVariant} flags=0x{b.Flags:X8}";
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                }
        }
        catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(f)}: {ex.Message}"); }
    foreach (var (k, n) in counts.OrderBy(x => x.Key)) Console.WriteLine($"{n,6}  {k}");
    return;
}
if (args[0] == "--icon") { foreach (var f in args.Skip(1)) Reflect.Icon(f); return; }
if (args[0] == "--mobils") { foreach (var f in args.Skip(1)) Reflect.Mobils(f); return; }
if (args[0] == "--clip") { foreach (var f in args.Skip(1)) Reflect.Clip(f); return; }
if (args[0] == "--template") { Template.Run(args[1], args[2]); return; }
if (args[0] == "--ground") { Ground.Run(args.Skip(1)); return; }
// Prints a summary of a map: size, blocks, items, embedded files.
foreach (var path in args)
{
    var map = Gbx.ParseNode<CGameCtnChallenge>(path);
    Console.WriteLine($"{path}\n  name={map.MapName} uid={map.MapUid} author={map.AuthorLogin} size={map.Size} deco={map.Decoration}");
    Console.WriteLine($"  blocks={map.Blocks?.Count} items={map.AnchoredObjects?.Count}");
    foreach (var g in (map.Blocks ?? new()).GroupBy(b => b.Name).Take(15)) Console.WriteLine($"    block {g.Key} x{g.Count()} e.g. coord={g.First().Coord} free={g.First().IsFree} absPos={g.First().AbsolutePositionInMap}");
    foreach (var o in (map.AnchoredObjects ?? new()).Take(15)) Console.WriteLine($"    item {o.ItemModel} pos={o.AbsolutePositionInMap} pyr={o.PitchYawRoll}");
    if (map.EmbeddedZipData is { Length: > 0 }) using (var zip = map.OpenReadEmbeddedZipData()) foreach (var e in zip.Entries.Take(20)) Console.WriteLine($"    embedded {e.FullName} {e.Length}");
}
