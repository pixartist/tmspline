using System.Globalization;
using System.Numerics;
using System.Text;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The export's meshes (roads, gates, pillars, own terrain) as one Wavefront OBJ file, for Blender and other 3D
/// programs: an object per item, its triangles grouped by material (names only: the game's textures are not ours to
/// hand out). Editor coordinates, metres, Y up (the OBJ importers' default); triggers are left out.
/// </summary>
public static class ObjWriter
{
    public static byte[] Write(IEnumerable<MeshChunk> chunks, string title)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(title.ReplaceLineEndings(" ")).Append("\n# TMSpline: metres, Y up\n");
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Add(text, chunk, chunk.Name, ref offset);
            foreach (var part in chunk.NotCollidable) Add(text, part, $"{chunk.Name}_{part.Name}", ref offset, chunk);
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <param name="placed">The item a part belongs to: parts lie in their item's frame.</param>
    private static void Add(StringBuilder text, MeshChunk mesh, string name, ref int offset, MeshChunk? placed = null)
    {
        if (mesh.Triangles.Count == 0) return;
        var item = placed ?? mesh;
        var frame = item.Frame;
        Vector3 x = V(frame.X), y = V(frame.Y), z = V(frame.Z), origin = V(item.ItemOffset);
        var inv = CultureInfo.InvariantCulture;
        text.Append("o ").Append(Safe(name)).Append('\n');
        foreach (var p in mesh.Positions)
        {
            var w = origin + x * p.X + y * p.Y + z * p.Z;
            text.Append("v ").Append(w.X.ToString("0.###", inv)).Append(' ').Append(w.Y.ToString("0.###", inv)).Append(' ').Append(w.Z.ToString("0.###", inv)).Append('\n');
        }
        var uvs = mesh.Uv0.Count == mesh.Positions.Count;
        // (V upwards in OBJ, downwards in ours.)
        if (uvs) foreach (var t in mesh.Uv0) text.Append("vt ").Append(t.X.ToString("0.####", inv)).Append(' ').Append((1 - t.Y).ToString("0.####", inv)).Append('\n');
        var normals = mesh.Normals.Count == mesh.Positions.Count;
        if (normals)
            foreach (var n in mesh.Normals)
            {
                var w = x * n.X + y * n.Y + z * n.Z;
                text.Append("vn ").Append(w.X.ToString("0.###", inv)).Append(' ').Append(w.Y.ToString("0.###", inv)).Append(' ').Append(w.Z.ToString("0.###", inv)).Append('\n');
            }
        foreach (var group in mesh.Triangles.GroupBy(t => t.Material))
        {
            text.Append("usemtl ").Append(Safe(mesh.Materials[group.Key].Name)).Append('\n');
            foreach (var t in group)
            {
                text.Append('f');
                foreach (var i in (ReadOnlySpan<int>)[t.A, t.B, t.C])
                {
                    var k = (i + offset + 1).ToString(inv);
                    text.Append(' ').Append(k);
                    if (uvs || normals) text.Append('/');
                    if (uvs) text.Append(k);
                    if (normals) text.Append('/').Append(k);
                }
                text.Append('\n');
            }
        }
        offset += mesh.Positions.Count;
    }

    private static Vector3 V(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_'));
}
