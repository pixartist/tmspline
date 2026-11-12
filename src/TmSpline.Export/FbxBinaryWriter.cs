using System.IO.Compression;
using System.Text;
using TmSpline.Core.Geometry;

namespace TmSpline.Export;

/// <summary>
/// Binary FBX 7.4 writer producing the same structure as the official NadeoImporter samples
/// (Blender "stable FBX IO" output): one mesh model, per-polygon-vertex normals, two UV layers
/// ("BaseMaterial", "Lightmap") and per-polygon materials. Mesh data is in meters, Y-up;
/// like the samples, the file unit is centimeters and the model is scaled by 100.
/// </summary>
public static class FbxBinaryWriter
{
    private const int Version = 7400;
    private const long GeometryId = 1000001;
    private const long ModelId = 1000002;
    private const long MaterialIdBase = 2000000;

    private static readonly byte[] HeadMagic = "Kaydara FBX Binary  \0\u001a\0"u8.ToArray();
    // Fixed values that the FBX SDK accepts together with the fixed CreationTime below (same as Blender).
    private static readonly byte[] FileId = { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };
    private static readonly byte[] FootId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
    private static readonly byte[] FootMagic = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
    private const string CreationTime = "1970-01-01 10:00:00:000";
    private const string Creator = "TmSpline";

    public static void Write(MeshChunk mesh, string path)
    {
        var root = new List<Node>
        {
            new("FBXHeaderExtension", Children:
            [
                new("FBXHeaderVersion", 1003),
                new("FBXVersion", Version),
                new("EncryptionType", 0),
                new("Creator", Creator),
            ]),
            new("FileId", FileId),
            new("CreationTime", CreationTime),
            new("Creator", Creator),
            new("GlobalSettings", Children:
            [
                new("Version", 1000),
                new("Properties70", Children:
                [
                    P("UpAxis", "int", "Integer", "", 1),
                    P("UpAxisSign", "int", "Integer", "", 1),
                    P("FrontAxis", "int", "Integer", "", 2),
                    P("FrontAxisSign", "int", "Integer", "", 1),
                    P("CoordAxis", "int", "Integer", "", 0),
                    P("CoordAxisSign", "int", "Integer", "", 1),
                    P("UnitScaleFactor", "double", "Number", "", 1.0),
                ]),
            ]),
            new("Documents", Children:
            [
                new("Count", 1),
                new("Document", 1000000L, "Scene", "Scene", Children:
                [
                    new("Properties70", Children: [P("SourceObject", "object", "", ""), P("ActiveAnimStackName", "KString", "", "", "")]),
                    new("RootNode", 0L),
                ]),
            ]),
            new("References"),
            new("Definitions", Children:
            [
                new("Version", 100),
                new("Count", 4 + (mesh.Sockets.Count > 0 ? 1 : 0)),
                new("ObjectType", "GlobalSettings", Children: [new("Count", 1)]),
                new("ObjectType", "Geometry", Children: [new("Count", 1 + mesh.Triggers.Count + mesh.NotCollidable.Count)]),
                new("ObjectType", "Model", Children: [new("Count", 1 + mesh.Triggers.Count + mesh.NotCollidable.Count + mesh.Sockets.Count)]),
                new("ObjectType", "Material", Children: [new("Count", mesh.Materials.Count + mesh.NotCollidable.Sum(c => c.Materials.Count))]),
                .. (mesh.Sockets.Count > 0 ? [new Node("ObjectType", "NodeAttribute", Children: [new("Count", mesh.Sockets.Count)])] : Array.Empty<Node>()),
            ]),
            new("Objects", Children: Objects(mesh).ToList()),
            new("Connections", Children: Connections(mesh).ToList()),
            new("Takes", Children: [new("Current", "")]),
        };

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(HeadMagic);
        w.Write(Version);
        foreach (var node in root)
            WriteNode(w, node);
        w.Write(new byte[13]); // end of top-level node list

        w.Write(FootId);
        w.Write(new byte[4]);
        var ofs = stream.Position;
        var pad = (int)(((ofs + 15) & ~15L) - ofs);
        w.Write(new byte[pad == 0 ? 16 : pad]);
        w.Write(Version);
        w.Write(new byte[120]);
        w.Write(FootMagic);
    }

    // Object ids: the main mesh, then one geometry/model pair per trigger, then one attribute/model pair per socket.
    private static long TriggerGeometryId(int i) => GeometryId + 10 + i * 2;
    private static long TriggerModelId(int i) => GeometryId + 11 + i * 2;
    private static long SocketAttributeId(int i) => 3000000 + i * 2;
    private static long NotCollidableGeometryId(int i) => 4000000 + i * 1000;
    private static long NotCollidableModelId(int i) => 4000001 + i * 1000;
    private static long NotCollidableMaterialId(int i, int m) => 4000100 + i * 1000 + m;
    private static long SocketModelId(int i) => 3000001 + i * 2;

    private static IEnumerable<Node> Objects(MeshChunk mesh)
    {
        foreach (var node in MeshNodes(mesh, mesh.Name, GeometryId, ModelId, withMaterials: true))
            yield return node;

        // Triggers: invisible meshes without materials; the importer recognizes them by the "_trigger_" prefix.
        for (var i = 0; i < mesh.Triggers.Count; i++)
            foreach (var node in MeshNodes(mesh.Triggers[i], $"_trigger_{(char)('A' + i)}", TriggerGeometryId(i), TriggerModelId(i), withMaterials: false))
                yield return node;

        // Visible but not collidable parts; the importer recognizes them by the "_notcollidable_" prefix.
        for (var i = 0; i < mesh.NotCollidable.Count; i++)
        {
            var part = mesh.NotCollidable[i];
            foreach (var node in MeshNodes(part, $"_notcollidable_{i}", NotCollidableGeometryId(i), NotCollidableModelId(i), withMaterials: true))
                yield return node;
            for (var m = 0; m < part.Materials.Count; m++)
                yield return MaterialNode(NotCollidableMaterialId(i, m), part.Materials[m].Name);
        }

        // Sockets: empty nodes; "_socket_start" marks the spawn point and its orientation.
        for (var i = 0; i < mesh.Sockets.Count; i++)
        {
            var (position, rotation) = mesh.Sockets[i];
            yield return new Node("NodeAttribute", SocketAttributeId(i), Name("_socket_start", "NodeAttribute"), "Null", Children:
            [
                new("TypeFlags", "Null"),
            ]);
            // Like the mesh models, the file unit is centimeters and nodes are scaled by 100; translations are in file units.
            yield return new Node("Model", SocketModelId(i), Name("_socket_start", "Model"), "Null", Children:
            [
                new("Version", 232),
                new("Properties70", Children:
                [
                    P("Lcl Translation", "Lcl Translation", "", "A", position.X * 100, position.Y * 100, position.Z * 100),
                    P("Lcl Rotation", "Lcl Rotation", "", "A", rotation.X, rotation.Y, rotation.Z),
                    P("Lcl Scaling", "Lcl Scaling", "", "A", 100.0, 100.0, 100.0),
                    P("DefaultAttributeIndex", "int", "Integer", "", 0),
                    P("InheritType", "enum", "", "", 1),
                ]),
                new("MultiLayer", 0),
                new("MultiTake", 0),
                new("Shading", true),
                new("Culling", "CullingOff"),
            ]);
        }

        for (var i = 0; i < mesh.Materials.Count; i++)
            yield return MaterialNode(MaterialIdBase + i, mesh.Materials[i].Name);
    }

    private static Node MaterialNode(long id, string name) =>
        new("Material", id, Name(name, "Material"), "", Children:
        [
            new("Version", 102),
            new("ShadingModel", "Phong"),
            new("MultiLayer", 0),
            new("Properties70", Children:
            [
                P("DiffuseColor", "Color", "", "A", 0.8, 0.8, 0.8),
            ]),
        ]);

    private static IEnumerable<Node> MeshNodes(MeshChunk mesh, string name, long geometryId, long modelId, bool withMaterials)
    {
        var corners = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var layer0 = new List<Node>
        {
            new("Version", 100),
            LayerElement("LayerElementNormal", 0),
            LayerElement("LayerElementUV", 0),
        };
        if (withMaterials)
            layer0.Add(LayerElement("LayerElementMaterial", 0));
        if (mesh.VertexShade is not null)
            layer0.Add(LayerElement("LayerElementColor", 0));

        var geometry = new List<Node>
        {
            new("Properties70"),
            new("GeometryVersion", 124),
            new("Vertices", mesh.Positions.SelectMany(p => new double[] { p.X, p.Y, p.Z }).ToArray()),
            new("PolygonVertexIndex", mesh.Triangles.SelectMany(t => new[] { t.A, t.B, -(t.C + 1) }).ToArray()),
            new("LayerElementNormal", 0, Children:
            [
                new("Version", 101),
                new("Name", ""),
                new("MappingInformationType", "ByPolygonVertex"),
                new("ReferenceInformationType", "Direct"),
                new("Normals", corners.SelectMany(i => { var n = mesh.Normals[i]; return new double[] { n.X, n.Y, n.Z }; }).ToArray()),
            ]),
            UvLayer(0, "BaseMaterial", mesh.Uv0, corners),
            UvLayer(1, "Lightmap", mesh.Uv1, corners),
        };
        if (withMaterials)
        {
            geometry.Add(new("LayerElementMaterial", 0, Children:
            [
                new("Version", 101),
                new("Name", ""),
                new("MappingInformationType", "ByPolygon"),
                new("ReferenceInformationType", "IndexToDirect"),
                new("Materials", mesh.Triangles.Select(t => t.Material).ToArray()),
            ]));
        }
        if (mesh.VertexShade is { } shade)
        {
            // One colour (a grey) for every corner.
            geometry.Add(new("LayerElementColor", 0, Children:
            [
                new("Version", 101),
                new("Name", "Col"),
                new("MappingInformationType", "ByPolygonVertex"),
                new("ReferenceInformationType", "IndexToDirect"),
                new("Colors", new double[] { shade, shade, shade, 1 }),
                new("ColorIndex", new int[corners.Length]),
            ]));
        }
        geometry.Add(new("Layer", 0, Children: layer0));
        geometry.Add(new("Layer", 1, Children: [new("Version", 100), LayerElement("LayerElementUV", 1)]));

        yield return new Node("Geometry", geometryId, Name(name, "Geometry"), "Mesh", Children: geometry);
        yield return new Node("Model", modelId, Name(name, "Model"), "Mesh", Children:
        [
            new("Version", 232),
            new("Properties70", Children:
            [
                P("Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0),
                P("Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0),
                P("Lcl Scaling", "Lcl Scaling", "", "A", 100.0, 100.0, 100.0),
                // Without this the FBX SDK does not bind the geometry to the node ("nothing in the geometry").
                P("DefaultAttributeIndex", "int", "Integer", "", 0),
                P("InheritType", "enum", "", "", 1),
            ]),
            new("MultiLayer", 0),
            new("MultiTake", 0),
            new("Shading", true),
            new("Culling", "CullingOff"),
        ]);
    }

    private static IEnumerable<Node> Connections(MeshChunk mesh)
    {
        yield return new Node("C", "OO", ModelId, 0L);
        yield return new Node("C", "OO", GeometryId, ModelId);
        // Material order of connection defines the material indices used by LayerElementMaterial.
        for (var i = 0; i < mesh.Materials.Count; i++)
            yield return new Node("C", "OO", MaterialIdBase + i, ModelId);
        for (var i = 0; i < mesh.Triggers.Count; i++)
        {
            yield return new Node("C", "OO", TriggerModelId(i), 0L);
            yield return new Node("C", "OO", TriggerGeometryId(i), TriggerModelId(i));
        }
        for (var i = 0; i < mesh.NotCollidable.Count; i++)
        {
            yield return new Node("C", "OO", NotCollidableModelId(i), 0L);
            yield return new Node("C", "OO", NotCollidableGeometryId(i), NotCollidableModelId(i));
            // Material order of connection defines this part's own material indices.
            for (var m = 0; m < mesh.NotCollidable[i].Materials.Count; m++)
                yield return new Node("C", "OO", NotCollidableMaterialId(i, m), NotCollidableModelId(i));
        }
        for (var i = 0; i < mesh.Sockets.Count; i++)
        {
            yield return new Node("C", "OO", SocketModelId(i), 0L);
            yield return new Node("C", "OO", SocketAttributeId(i), SocketModelId(i));
        }
    }

    private static Node UvLayer(int index, string name, List<System.Numerics.Vector2> uvs, int[] corners) =>
        new("LayerElementUV", index, Children:
        [
            new("Version", 101),
            new("Name", name),
            new("MappingInformationType", "ByPolygonVertex"),
            new("ReferenceInformationType", "IndexToDirect"),
            new("UV", uvs.SelectMany(uv => new double[] { uv.X, uv.Y }).ToArray()),
            new("UVIndex", corners),
        ]);

    private static Node LayerElement(string type, int typedIndex) =>
        new("LayerElement", Children: [new("Type", type), new("TypedIndex", typedIndex)]);

    private static Node P(params object[] values) => new("P", values);

    /// <summary>Binary FBX object names are "Name\0\x01Class".</summary>
    private static string Name(string name, string cls) => name + "\0\u0001" + cls;

    private sealed record Node(string Name, params object[] Props)
    {
        public List<Node>? Children { get; init; }

        public Node(string name, object p0, List<Node> Children) : this(name, new[] { p0 }) => this.Children = Children;
        public Node(string name, object p0, object p1, object p2, List<Node> Children) : this(name, new[] { p0, p1, p2 }) => this.Children = Children;
        public Node(string name, List<Node> Children) : this(name, Array.Empty<object>()) => this.Children = Children;
    }

    private static void WriteNode(BinaryWriter w, Node node)
    {
        var stream = w.BaseStream;
        var start = stream.Position;
        w.Write(0u); // end offset, patched below
        w.Write((uint)node.Props.Length);
        w.Write(0u); // property list length, patched below
        var nameBytes = Encoding.ASCII.GetBytes(node.Name);
        w.Write((byte)nameBytes.Length);
        w.Write(nameBytes);

        var propsStart = stream.Position;
        foreach (var p in node.Props)
            WriteProperty(w, p);
        var propsLength = stream.Position - propsStart;

        var children = node.Children ?? new List<Node>();
        foreach (var child in children)
            WriteNode(w, child);
        if (children.Count > 0 || node.Props.Length == 0)
            w.Write(new byte[13]); // null record terminating the child list

        var end = stream.Position;
        stream.Position = start;
        w.Write((uint)end);
        stream.Position = start + 8;
        w.Write((uint)propsLength);
        stream.Position = end;
    }

    private static void WriteProperty(BinaryWriter w, object value)
    {
        switch (value)
        {
            case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case short s: w.Write((byte)'Y'); w.Write(s); break;
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case long l: w.Write((byte)'L'); w.Write(l); break;
            case float f: w.Write((byte)'F'); w.Write(f); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case string str:
            {
                var bytes = Encoding.UTF8.GetBytes(str);
                w.Write((byte)'S'); w.Write(bytes.Length); w.Write(bytes);
                break;
            }
            case byte[] raw: w.Write((byte)'R'); w.Write(raw.Length); w.Write(raw); break;
            case double[] da: WriteArray(w, 'd', da.Length, bw => { foreach (var x in da) bw.Write(x); }); break;
            case int[] ia: WriteArray(w, 'i', ia.Length, bw => { foreach (var x in ia) bw.Write(x); }); break;
            default: throw new NotSupportedException($"FBX property type {value.GetType()}");
        }
    }

    private static void WriteArray(BinaryWriter w, char type, int count, Action<BinaryWriter> writeItems)
    {
        var raw = new MemoryStream();
        using (var bw = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            writeItems(bw);

        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);

        w.Write((byte)type);
        w.Write(count);
        w.Write(1); // zlib encoding
        w.Write((int)compressed.Length);
        w.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
    }
}
