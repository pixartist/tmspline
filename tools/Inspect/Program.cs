using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
Gbx.LZO = new Lzo();
if (args[0] == "--libcolours") { var patched = TmSpline.Export.ImporterFiles.WithEffectColours(File.ReadAllText(args[1])); var all = patched.Split((char)10); for (var i = 0; i < all.Length; i++) if (all[i].Contains("DMaterial(") && all[i].Contains("FX")) Console.WriteLine(string.Join(" / ", all.Skip(i).Take(6).Select(l => l.Trim()))); Console.WriteLine($"{all.Count(l => l.Contains("DColor0"))} materials with colours, was {File.ReadAllText(args[1]).Split("DColor0").Length - 1}"); return; }
if (args[0] == "--itemtrigger") { foreach (var f in args.Skip(1)) ItemTrigger.Run(f); return; }
if (args[0] == "--matchunks") { MaterialChunks.Run(args[1]); return; }
if (args[0] == "--fields") { foreach (var n in args.Skip(1)) { var t = typeof(GBX.NET.Engines.Plug.CPlugSurface).Assembly.GetTypes().FirstOrDefault(x => x.FullName == n || x.Name == n); if (t is null) { Console.WriteLine(n + ": not found"); continue; } Console.WriteLine(t.FullName); foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)) Console.WriteLine($"  field {f.FieldType.Name} {f.Name}"); foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)) Console.WriteLine($"  method {(m.IsPublic ? "public " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})"); foreach (var c in t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)) Console.WriteLine($"  ctor {(c.IsPublic ? "public " : "")}({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})"); } return; }
if (args[0] == "--type") { foreach (var n in args.Skip(1)) { var t = typeof(GBX.NET.Engines.Plug.CPlugSurface).Assembly.GetTypes().FirstOrDefault(x => x.Name == n || x.FullName == n); if (t is null) { Console.WriteLine(n + ": not found"); continue; } Console.WriteLine(t.FullName + (t.IsEnum ? ": " + string.Join(", ", Enum.GetNames(t)) : "")); foreach (var p in t.GetProperties()) Console.WriteLine($"  {p.PropertyType.Name}{(p.PropertyType.IsGenericType ? "<" + string.Join(",", p.PropertyType.GetGenericArguments().Select(a => a.Name)) + ">" : "")} {p.Name}{(p.CanWrite ? "" : " (read only)")}"); foreach (var nt in t.GetNestedTypes()) Console.WriteLine("  nested " + nt.Name); } return; }
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
if (args[0] == "--mapitems")
{
    // A map's items and blocks by name: how many, their collection, heights and cells (for the environments' vegetation).
    foreach (var f in args.Skip(1))
    {
        var m = Gbx.ParseNode<CGameCtnChallenge>(f);
        Console.WriteLine($"{Path.GetFileName(f)}: deco {m.Decoration} collection {m.Decoration.Collection.Number}");
        foreach (var g in (m.AnchoredObjects ?? []).GroupBy(o => (o.ItemModel.Id, o.ItemModel.Collection.Number, o.ItemModel.Author)).OrderBy(g => g.Key.Id))
            Console.WriteLine($"  item {g.Key.Id} | collection {g.Key.Number} | {g.Key.Author} | x{g.Count()} | y {g.Min(o => o.AbsolutePositionInMap.Y):0.##}..{g.Max(o => o.AbsolutePositionInMap.Y):0.##} | unit y {string.Join(",", g.Select(o => o.BlockUnitCoord.Y).Distinct().Take(6))} | e.g. {g.First().AbsolutePositionInMap}");
        foreach (var g in (m.AnchoredObjects ?? []).Where(o => o.ItemModel.Author == "Nadeo").GroupBy(o => o.ItemModel.Id).OrderBy(g => g.Key))
            Console.WriteLine($"  flags of {g.Key}: {string.Join(" ", g.GroupBy(o => o.Flags).OrderBy(f => f.Key).Select(f => $"0x{f.Key:X}x{f.Count()}"))}");
        foreach (var g in (m.Blocks ?? []).GroupBy(b => b.Name).OrderBy(g => g.Key))
            Console.WriteLine($"  block {g.Key} x{g.Count()} | rows {string.Join(",", g.Select(b => b.Coord.Y).Distinct().OrderBy(y => y).Take(8))} | free {g.Count(b => b.IsFree)} | e.g. {g.First().Coord} {g.First().AbsolutePositionInMap}");
    }
    return;
}
if (args[0] == "--itemprops")
{
    // The plain properties of a map's items whose name contains the text: --itemprops <map> <text> [count].
    var m = Gbx.ParseNode<CGameCtnChallenge>(args[1]);
    foreach (var o in (m.AnchoredObjects ?? []).Where(o => o.ItemModel.Id.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(args.Length > 3 ? int.Parse(args[3]) : 3))
    {
        Console.WriteLine(o.ItemModel);
        foreach (var p in o.GetType().GetProperties().OrderBy(p => p.Name))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v is null) continue;
            Console.WriteLine($"  {p.Name} = {v}");
        }
    }
    return;
}
if (args[0] == "--mapprops")
{
    // A map's plain properties (numbers, texts, idents, vectors), for comparing maps of different environments.
    foreach (var f in args.Skip(1))
    {
        var m = Gbx.ParseNode<CGameCtnChallenge>(f);
        Console.WriteLine(f);
        foreach (var p in m.GetType().GetProperties().OrderBy(p => p.Name))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(m); } catch { continue; }
            if (v is null || (v is not string && !v.GetType().IsValueType && v is not Ident)) continue;
            var s = v.ToString() ?? "";
            if (s.Length > 0 && s.Length < 160) Console.WriteLine($"  {p.Name} = {s}");
        }
    }
    return;
}
if (args[0] == "--decorations")
{
    // Map decorations (mood / stadium) in use, with the size and an example map each.
    var found = new Dictionary<string, (int Count, string Example)>();
    foreach (var f in args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a, "*.Map.Gbx", SearchOption.AllDirectories) : [a]))
        try
        {
            var m = Gbx.ParseNode<CGameCtnChallenge>(f);
            var k = $"{m.Decoration?.Id} | {m.Decoration?.Collection} | {m.Decoration?.Author} | size {m.Size} | blocks e.g. {string.Join(",", (m.Blocks ?? []).Select(b => b.Name).Distinct().Take(4))}";
            found[k] = found.TryGetValue(k, out var v) ? (v.Count + 1, v.Example) : (1, f);
        }
        catch (Exception ex) { Console.WriteLine($"{f}: {ex.Message}"); }
    foreach (var (k, v) in found.OrderBy(x => x.Key)) Console.WriteLine($"{k} — {v.Count} maps, e.g. {v.Example}");
    return;
}
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
if (args[0] == "--template") { Template.Run(args[1], args[2], args.Contains("--keep-blocks")); return; }
if (args[0] == "--ground") { Ground.Run(args.Skip(1)); return; }
// Prints a summary of a map: size, blocks, items, embedded files.
foreach (var path in args)
{
    var map = Gbx.ParseNode<CGameCtnChallenge>(path);
    Console.WriteLine($"{path}\n  name={map.MapName} uid={map.MapUid} author={map.AuthorLogin} size={map.Size} deco={map.Decoration}");
    Console.WriteLine($"  blocks={map.Blocks?.Count} items={map.AnchoredObjects?.Count} deco collection={map.Decoration.Collection.Number}/{map.Decoration.Collection.String} mapinfo={map.MapInfo.Collection.Number}/{map.MapInfo.Collection.String}");
    foreach (var g in (map.Blocks ?? new()).GroupBy(b => b.Name).Take(15)) Console.WriteLine($"    block {g.Key} x{g.Count()} e.g. coord={g.First().Coord} free={g.First().IsFree} absPos={g.First().AbsolutePositionInMap}");
    foreach (var o in (map.AnchoredObjects ?? new()).Take(15)) Console.WriteLine($"    item {o.ItemModel} pos={o.AbsolutePositionInMap} pyr={o.PitchYawRoll} unit={o.BlockUnitCoord} collection={o.ItemModel.Collection.Number}/{o.ItemModel.Collection.String}");
    if (map.EmbeddedZipData is { Length: > 0 }) using (var zip = map.OpenReadEmbeddedZipData()) foreach (var e in zip.Entries.Take(20)) Console.WriteLine($"    embedded {e.FullName} {e.Length}");
}
