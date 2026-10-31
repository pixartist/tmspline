using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
Gbx.LZO = new Lzo();
if (args[0] == "--waypoints") { foreach (var f in args.Skip(1)) try { WaypointsDump.Run(f); } catch (Exception ex) { Console.WriteLine(f + ": " + ex.Message); } return; }
if (args[0] == "--colortype") { var t = typeof(GBX.NET.Engines.Game.CGameCtnAnchoredObject).GetProperty("Color")!.PropertyType; Console.WriteLine(t.FullName + ": " + string.Join(", ", Enum.GetNames(t).Zip(Enum.GetValues(t).Cast<object>().Select(Convert.ToInt32), (n, v) => $"{n}={v}"))); return; }
if (args[0] == "--colors") { var m = GBX.NET.Gbx.ParseNode<GBX.NET.Engines.Game.CGameCtnChallenge>(args[1]); foreach (var g in m.AnchoredObjects!.GroupBy(a => a.Color)) Console.WriteLine($"{g.Key}: {g.Count()} items, e.g. {g.First().ItemModel.Id}"); return; }
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
