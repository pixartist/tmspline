using System.Numerics;
using System.Runtime.InteropServices;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The Manifold library (github.com/elalish/manifold, Apache 2.0) through its C bindings (manifoldc.dll, built by
/// deploy/build-manifold.cmd): exact, robust mesh booleans on closed, oriented solids. Each object lives in memory
/// the library allocates; the wrappers free it on Dispose.
/// </summary>
internal static class ManifoldLib
{
    private const string Lib = "manifoldc";

    public enum Op { Add = 0, Subtract = 1, Intersect = 2 }

    public enum Error
    {
        NoError, NonFiniteVertex, NotManifold, VertexIndexOutOfBounds, PropertiesWrongLength, MissingPositionProperties,
        MergeVectorsDifferentLengths, MergeIndexOutOfBounds, TransformWrongLength, RunIndexWrongLength, FaceIdWrongLength,
        InvalidConstruction, ResultTooLarge, InvalidTangents, Cancelled,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshGLOptions
    {
        public IntPtr RunIndices;
        public nuint RunIndicesLength;
        public IntPtr RunOriginalIds;
        public nuint RunOriginalIdsLength;
        public IntPtr MergeFromVert;
        public IntPtr MergeToVert;
        public nuint MergeVertsLength;
        public IntPtr HalfedgeTangents;
    }

    [DllImport(Lib)] private static extern IntPtr manifold_alloc_meshgl();
    [DllImport(Lib)] private static extern IntPtr manifold_alloc_manifold();
    [DllImport(Lib)] private static extern IntPtr manifold_alloc_manifold_vec();
    [DllImport(Lib)] private static extern void manifold_delete_meshgl(IntPtr m);
    [DllImport(Lib)] private static extern void manifold_delete_manifold(IntPtr m);
    [DllImport(Lib)] private static extern void manifold_delete_manifold_vec(IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_w_options(IntPtr mem, float[] vertProps, nuint nVerts, nuint nProps, uint[] triVerts, nuint nTris, ref MeshGLOptions options);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_merge(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_of_meshgl(IntPtr mem, IntPtr mesh);
    [DllImport(Lib)] private static extern IntPtr manifold_get_meshgl(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_as_original(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_manifold_empty_vec(IntPtr mem);
    [DllImport(Lib)] private static extern void manifold_manifold_vec_push_back(IntPtr ms, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_batch_boolean(IntPtr mem, IntPtr ms, Op op);
    [DllImport(Lib)] private static extern IntPtr manifold_boolean(IntPtr mem, IntPtr a, IntPtr b, Op op);
    [DllImport(Lib)] private static extern Error manifold_status(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_num_tri(IntPtr m);
    [DllImport(Lib)] private static extern int manifold_genus(IntPtr m);
    [DllImport(Lib)] private static extern int manifold_original_id(IntPtr m);
    [DllImport(Lib)] private static extern double manifold_volume(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_num_prop(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_num_vert(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_num_tri(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_vert_properties_length(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_tri_length(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_merge_length(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_run_index_length(IntPtr m);
    [DllImport(Lib)] private static extern nuint manifold_meshgl_run_original_id_length(IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_vert_properties(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_tri_verts(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_merge_from_vert(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_merge_to_vert(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_run_index(IntPtr mem, IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_meshgl_run_original_id(IntPtr mem, IntPtr m);
    [DllImport(Lib)] public static extern uint manifold_reserve_ids(uint n);
    [DllImport(Lib)] private static extern IntPtr manifold_hull_pts(IntPtr mem, double[] ps, nuint length);

    [DllImport(Lib)] private static extern IntPtr manifold_alloc_simple_polygon();
    [DllImport(Lib)] private static extern IntPtr manifold_alloc_polygons();
    [DllImport(Lib)] private static extern IntPtr manifold_alloc_triangulation();
    [DllImport(Lib)] private static extern void manifold_delete_simple_polygon(IntPtr p);
    [DllImport(Lib)] private static extern void manifold_delete_polygons(IntPtr p);
    [DllImport(Lib)] private static extern void manifold_delete_triangulation(IntPtr p);
    [DllImport(Lib)] private static extern IntPtr manifold_simple_polygon(IntPtr mem, double[] xy, nuint length);
    [DllImport(Lib)] private static extern IntPtr manifold_polygons(IntPtr mem, IntPtr[] polygons, nuint length);
    [DllImport(Lib)] private static extern IntPtr manifold_triangulate(IntPtr mem, IntPtr polygons, double epsilon);
    [DllImport(Lib)] private static extern nuint manifold_triangulation_num_tri(IntPtr m);
    [DllImport(Lib)] private static extern IntPtr manifold_triangulation_tri_verts(IntPtr mem, IntPtr m);

    /// <summary>
    /// Polygons (each a list of x, y pairs; outer ones counter-clockwise, holes clockwise) triangulated as one: corners
    /// as indices into all the polygons' points in order, counter-clockwise.
    /// </summary>
    public static int[] Triangulate(IReadOnlyList<double[]> polygons, double epsilon = -1)
    {
        var simple = polygons.Select(p => manifold_simple_polygon(manifold_alloc_simple_polygon(), p, (nuint)(p.Length / 2))).ToArray();
        var all = manifold_polygons(manifold_alloc_polygons(), simple, (nuint)simple.Length);
        var triangulation = manifold_triangulate(manifold_alloc_triangulation(), all, epsilon);
        try
        {
            var count = (int)manifold_triangulation_num_tri(triangulation) * 3;
            var result = new int[count];
            if (count == 0) return result;
            var mem = Marshal.AllocHGlobal(count * 4);
            try { manifold_triangulation_tri_verts(mem, triangulation); Marshal.Copy(mem, result, 0, count); }
            finally { Marshal.FreeHGlobal(mem); }
            return result;
        }
        finally
        {
            manifold_delete_triangulation(triangulation);
            manifold_delete_polygons(all);
            foreach (var p in simple) manifold_delete_simple_polygon(p);
        }
    }

    /// <summary>Whether the native library can be loaded here (not in the browser's WebAssembly build).</summary>
    public static bool Available
    {
        get
        {
            if (available is { } a) return a;
            try { manifold_reserve_ids(0); available = true; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException) { available = false; }
            return available.Value;
        }
    }
    private static bool? available;

    /// <summary>A mesh as the library takes and gives it: per vertex its properties (x, y, z first), triangles by vertex.</summary>
    public sealed class Mesh
    {
        public int NumProp = 3;
        public float[] Props = [];
        public uint[] Tris = [];
        /// <summary>Vertices to be taken as the same one (same place, other properties): from[i] is the same as to[i].</summary>
        public uint[] MergeFrom = [], MergeTo = [];
        /// <summary>Output only: the triangles (as offsets into Tris, in thirds: run r holds triangles RunIndex[r]/3 to RunIndex[r+1]/3) by the input they came from.</summary>
        public uint[] RunIndex = [], RunOriginalId = [];
        public int VertexCount => Props.Length / NumProp;
        public Vector3 Position(int v) => new(Props[v * NumProp], Props[v * NumProp + 1], Props[v * NumProp + 2]);
    }

    public sealed class Solid : IDisposable
    {
        internal IntPtr Handle;
        internal Solid(IntPtr handle) => Handle = handle;
        public Error Status => manifold_status(Handle);
        public int Triangles => (int)manifold_num_tri(Handle);
        public int Genus => manifold_genus(Handle);
        public int OriginalId => manifold_original_id(Handle);
        public double Volume => manifold_volume(Handle);
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) manifold_delete_manifold(Handle);
            Handle = IntPtr.Zero;
        }

        /// <summary>A solid from a closed, oriented mesh (vertices at the same place with nothing to merge them stay apart; <paramref name="merge"/>: those within the library's precision are merged first).</summary>
        public static Solid Of(Mesh mesh, bool merge = true)
        {
            var options = new MeshGLOptions();
            var from = GCHandle.Alloc(mesh.MergeFrom, GCHandleType.Pinned);
            var to = GCHandle.Alloc(mesh.MergeTo, GCHandleType.Pinned);
            try
            {
                if (mesh.MergeFrom.Length > 0)
                {
                    options.MergeFromVert = from.AddrOfPinnedObject();
                    options.MergeToVert = to.AddrOfPinnedObject();
                    options.MergeVertsLength = (nuint)mesh.MergeFrom.Length;
                }
                var gl = manifold_meshgl_w_options(manifold_alloc_meshgl(), mesh.Props, (nuint)mesh.VertexCount, (nuint)mesh.NumProp, mesh.Tris, (nuint)(mesh.Tris.Length / 3), ref options);
                if (merge)
                {
                    var merged = manifold_meshgl_merge(manifold_alloc_meshgl(), gl);
                    manifold_delete_meshgl(gl);
                    gl = merged;
                }
                var solid = new Solid(manifold_of_meshgl(manifold_alloc_manifold(), gl));
                manifold_delete_meshgl(gl);
                return solid;
            }
            finally { from.Free(); to.Free(); }
        }

        /// <summary>This solid as an original of its own (a new id its triangles are known by after booleans).</summary>
        public Solid AsOriginal() => new(manifold_as_original(manifold_alloc_manifold(), Handle));

        /// <summary>The convex hull of points (empty where they span no volume).</summary>
        public static Solid Hull(IReadOnlyList<Vector3> points)
        {
            var xyz = new double[points.Count * 3];
            for (var i = 0; i < points.Count; i++) (xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]) = (points[i].X, points[i].Y, points[i].Z);
            return new Solid(manifold_hull_pts(manifold_alloc_manifold(), xyz, (nuint)points.Count));
        }

        public Solid Boolean(Solid other, Op op) => new(manifold_boolean(manifold_alloc_manifold(), Handle, other.Handle, op));

        public static Solid Batch(IEnumerable<Solid> solids, Op op)
        {
            var vec = manifold_manifold_empty_vec(manifold_alloc_manifold_vec());
            try
            {
                foreach (var s in solids) manifold_manifold_vec_push_back(vec, s.Handle);
                return new Solid(manifold_batch_boolean(manifold_alloc_manifold(), vec, op));
            }
            finally { manifold_delete_manifold_vec(vec); }
        }

        public Mesh ToMesh()
        {
            var gl = manifold_get_meshgl(manifold_alloc_meshgl(), Handle);
            try
            {
                var mesh = new Mesh { NumProp = (int)manifold_meshgl_num_prop(gl) };
                mesh.Props = Floats(manifold_meshgl_vert_properties_length(gl), mem => manifold_meshgl_vert_properties(mem, gl));
                mesh.Tris = Uints(manifold_meshgl_tri_length(gl), mem => manifold_meshgl_tri_verts(mem, gl));
                var merges = manifold_meshgl_merge_length(gl);
                mesh.MergeFrom = Uints(merges, mem => manifold_meshgl_merge_from_vert(mem, gl));
                mesh.MergeTo = Uints(merges, mem => manifold_meshgl_merge_to_vert(mem, gl));
                mesh.RunIndex = Uints(manifold_meshgl_run_index_length(gl), mem => manifold_meshgl_run_index(mem, gl));
                mesh.RunOriginalId = Uints(manifold_meshgl_run_original_id_length(gl), mem => manifold_meshgl_run_original_id(mem, gl));
                return mesh;
            }
            finally { manifold_delete_meshgl(gl); }
        }
    }

    private static float[] Floats(nuint length, Func<IntPtr, IntPtr> fill)
    {
        var result = new float[(int)length];
        if (result.Length == 0) return result;
        var mem = Marshal.AllocHGlobal(result.Length * 4);
        try { fill(mem); Marshal.Copy(mem, result, 0, result.Length); }
        finally { Marshal.FreeHGlobal(mem); }
        return result;
    }

    private static uint[] Uints(nuint length, Func<IntPtr, IntPtr> fill)
    {
        var ints = new int[(int)length];
        if (ints.Length == 0) return [];
        var mem = Marshal.AllocHGlobal(ints.Length * 4);
        try { fill(mem); Marshal.Copy(mem, ints, 0, ints.Length); }
        finally { Marshal.FreeHGlobal(mem); }
        return Array.ConvertAll(ints, i => (uint)i);
    }
}
