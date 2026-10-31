using System.Collections;
using System.Reflection;
using GBX.NET;

// Prints a Gbx node's object tree (GBX.NET properties), for finding where block definitions keep their meshes.
static class Tree
{
    public static void Run(string path, int maxDepth)
    {
        var node = Gbx.ParseNode(path);
        Console.WriteLine($"{Path.GetFileName(path)}: {node?.GetType().Name}");
        Print(node, 1, maxDepth, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    static void Print(object? node, int depth, int maxDepth, HashSet<object> seen)
    {
        if (node is null || depth > maxDepth || !seen.Add(node)) return;
        var pad = new string(' ', depth * 2);
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? v = null;
            try { v = p.GetValue(node); } catch { continue; }
            if (v is null) continue;
            if (IsLeaf(v)) { Console.WriteLine($"{pad}{p.Name} = {Short(v)}"); continue; }
            if (v is IEnumerable list)
            {
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0) continue;
                if (items.All(x => x is null || IsLeaf(x)))
                {
                    Console.WriteLine($"{pad}{p.Name} [{items.Count}] = {string.Join(", ", items.Take(8).Select(Short))}");
                    continue;
                }
                Console.WriteLine($"{pad}{p.Name} [{items.Count}]");
                for (var i = 0; i < Math.Min(items.Count, 6); i++)
                {
                    if (items[i] is null) continue;
                    Console.WriteLine($"{pad}  [{i}] {items[i]!.GetType().Name}");
                    if (items[i] is IEnumerable inner and not string)
                        foreach (var (x, j) in inner.Cast<object?>().Take(4).Select((x, j) => (x, j)))
                        { Console.WriteLine($"{pad}    [{j}] {x?.GetType().Name}"); Print(x, depth + 3, maxDepth, seen); }
                    else Print(items[i], depth + 2, maxDepth, seen);
                }
                continue;
            }
            if (!(v.GetType().Namespace?.StartsWith("GBX.NET") ?? false)) continue;
            Console.WriteLine($"{pad}{p.Name}: {v.GetType().Name}");
            Print(v, depth + 1, maxDepth, seen);
        }
    }

    static bool IsLeaf(object v) => v is string || v.GetType().IsPrimitive || v.GetType().IsEnum || v is decimal
        || v.GetType().Namespace == "GBX.NET" && v.GetType().IsValueType || v is GBX.NET.Components.GbxRefTableFile;
    static string Short(object? v) { var s = v?.ToString() ?? "null"; return s.Length > 120 ? s[..120] + "…" : s; }
}
