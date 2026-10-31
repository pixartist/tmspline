using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

// Converts the visual mesh of a Gbx file (an item, a Solid2 model, or anything that contains one) to Wavefront OBJ:
// one group per shaded geometry (material), highest detail level only. For checking whether game meshes can be read.
static class ObjExport
{
    public static void Run(string path, string output)
    {
        var node = Gbx.ParseNode(path);
        Console.WriteLine($"{Path.GetFileName(path)}: {node?.GetType().Name}");
        var solid = FindSolid2(node, 0, new HashSet<object>());
        if (solid is null)
        {
            Console.WriteLine("  no CPlugSolid2Model found; nodes inside:");
            Dump(node, 1, new HashSet<object>());
            return;
        }

        var obj = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var vertexBase = 1;
        var visuals = (solid.Visuals ?? []).ToArray();
        var geoms = (solid.ShadedGeoms ?? []).ToList();
        var lod0 = geoms.Count > 0 ? geoms.Min(g => g.Lod) : 0;
        var used = geoms.Count > 0 ? geoms.Where(g => g.Lod == lod0).Select(g => (g.VisualIndex, g.MaterialIndex)).ToList()
            : visuals.Select((_, i) => (i, -1)).ToList();
        var triangles = 0;
        foreach (var (visualIndex, material) in used)
        {
            if (visualIndex < 0 || visualIndex >= visuals.Length || visuals[visualIndex] is not CPlugVisualIndexedTriangles v)
                continue;
            var positions = (v.VertexStreams.Select(s => s.Positions).FirstOrDefault(p => p is { Length: > 0 }) ?? []).Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
            var indices = Indices(v);
            if (positions.Length == 0 || indices.Length == 0)
            {
                Console.WriteLine($"  visual {visualIndex}: {positions.Length} positions, {indices.Length} indices (skipped)");
                continue;
            }
            obj.AppendLine($"g visual{visualIndex}_material{material}");
            foreach (var p in positions)
                obj.AppendLine(string.Create(inv, $"v {p.X} {p.Y} {p.Z}"));
            for (var i = 0; i + 2 < indices.Length; i += 3)
                obj.AppendLine($"f {indices[i] + vertexBase} {indices[i + 1] + vertexBase} {indices[i + 2] + vertexBase}");
            vertexBase += positions.Length;
            triangles += indices.Length / 3;
            var min = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            Console.WriteLine($"  visual {visualIndex} (material {material}): {positions.Length} vertices, {indices.Length / 3} triangles, bounds {min} .. {max}");
        }
        File.WriteAllText(output, obj.ToString());
        Console.WriteLine($"Wrote {output}: {vertexBase - 1} vertices, {triangles} triangles"
            + $"; materials: {string.Join(", ", (solid.Materials ?? []).Select(m => m?.ToString() ?? "?"))}");
    }

    /// <summary>Triangle indices of a visual (its index buffer), via reflection so it works across GBX.NET versions.</summary>
    static int[] Indices(CPlugVisualIndexed v)
    {
        var buffer = v.GetType().GetProperty("IndexBuffer")?.GetValue(v);
        if (buffer is null) return [];
        var raw = buffer.GetType().GetProperty("Indices")?.GetValue(buffer);
        return raw switch
        {
            int[] a => a,
            ushort[] a => a.Select(x => (int)x).ToArray(),
            uint[] a => a.Select(x => (int)x).ToArray(),
            System.Collections.IEnumerable e => e.Cast<object>().Select(Convert.ToInt32).ToArray(),
            _ => [],
        };
    }

    /// <summary>The first Solid2 model reachable from a node through its properties (items, block infos, trees ...).</summary>
    static CPlugSolid2Model? FindSolid2(object? node, int depth, HashSet<object> seen)
    {
        if (node is CPlugSolid2Model s) return s;
        if (node is null || depth > 8 || node is string || node.GetType().IsPrimitive || !seen.Add(node)) return null;
        if (node is System.Collections.IEnumerable list)
        {
            foreach (var x in list)
                if (FindSolid2(x, depth + 1, seen) is { } found) return found;
            return null;
        }
        if (!(node.GetType().Namespace?.StartsWith("GBX.NET") ?? false)) return null;
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? v = null;
            try { v = p.GetValue(node); } catch { }
            if (FindSolid2(v, depth + 1, seen) is { } found) return found;
        }
        return null;
    }

    static void Dump(object? node, int depth, HashSet<object> seen)
    {
        if (node is null || depth > 4 || node is string || node.GetType().IsPrimitive || !seen.Add(node)) return;
        if (!(node.GetType().Namespace?.StartsWith("GBX.NET") ?? false)) return;
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? v = null;
            try { v = p.GetValue(node); } catch { }
            if (v is not null && (v.GetType().Namespace?.StartsWith("GBX.NET.Engines") ?? false) || v is System.Collections.IEnumerable and not string)
            {
                Console.WriteLine($"{new string(' ', depth * 2)}{p.Name}: {v?.GetType().Name}");
                Dump(v, depth + 1, seen);
            }
        }
    }
}
