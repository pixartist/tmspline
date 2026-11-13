using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The own terrain as a solid (prototype, TMSPLINE_TERRAIN=voxel): the editor's land (its height map: a landscape made
/// as if there were no road, then painted by hand) is everything under its surface, and each road cuts its room
/// (StitchedTerrain's: the space a car needs, the driving surface and a vault over it) out of it. The surface of what is
/// left is the mesh: open ground, cuttings, tunnels and their mouths all come from the same subtraction; a road over the
/// land stands free.
/// <list type="bullet">
/// <item>Field: f(p) = max(p.y - land(x, z), -room(p)), negative in the solid.</item>
/// <item>Octree: cells of Voxel metres within Near of a road's edge, twice the size for each doubling of the distance beyond
/// (up to Coarsest); only cells the surface may pass through are split.</item>
/// <item>Mesh: dual contouring (one point per leaf the surface passes through, at the mean of its edge crossings; one
/// quad, or a triangle where cells of different sizes meet, per smallest cell edge that changes sign): no cracks
/// between sizes.</item>
/// </list>
/// Not yet: a triangle budget, sharp edges where the land meets a road, a least cover over shallow tunnels.
/// </summary>
public static class VoxelTerrain
{
    /// <summary>The smallest cells' size, metres (TMSPLINE_VOXEL).</summary>
    public static float Voxel { get; set; } =
        float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_VOXEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1.5f;

    /// <summary>Within this of a road the cells are the smallest, and no cell is larger than Coarsest; metres (TMSPLINE_VOXEL_NEAR, TMSPLINE_VOXEL_COARSEST).</summary>
    public static float Near { get; set; } = Setting("TMSPLINE_VOXEL_NEAR", 3);
    public static float Coarsest { get; set; } = Setting("TMSPLINE_VOXEL_COARSEST", 32);

    /// <summary>A triangle at a driving surface is the cut's floor under it (left out) when it faces within about 35 degrees of it.</summary>
    private const float FloorAlike = 0.82f;
    /// <summary>How far the land may lie from a cell's one point before the cell is split, metres (TMSPLINE_VOXEL_TOLERANCE; 0: cells by the distance from the roads alone).</summary>
    public static float Tolerance { get; set; } = Setting("TMSPLINE_VOXEL_TOLERANCE", 1);

    /// <summary>Debug: TMSPLINE_VOXEL_FLOOR=keep leaves the cuts' floors under the roads in.</summary>
    private static readonly bool KeepFloor = Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_FLOOR") == "keep";
    /// <summary>And when the land's own surface lies at least this far over it, metres.</summary>
    private const float FloorCover = 2f;
    /// <summary>Seam experiment: land between a road's edges up to this far over the line between them (under a road bent downwards) belongs to the road, metres.</summary>
    private const float CrownMax = 4;
    /// <summary>Seam experiment: the cut's field as one distance to the road's surface, without jumps (TMSPLINE_SEAM_FIELD=rules: the earlier rules for edges, end, crown and deck).</summary>
    public static bool SeamSmooth { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_SEAM_FIELD") != "rules";
    /// <summary>Seam experiment: the edges cut only outwards, a half disc across the road (TMSPLINE_SEAM_HALF=off: all round).</summary>
    public static bool SeamHalfDisc { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_SEAM_HALF") != "off";
    /// <summary>Simplification experiment: the land simplified to this many triangles (TMSPLINE_SIMPLIFY; 0: not), and no further than this changes it, metres (TMSPLINE_SIMPLIFY_ERROR).</summary>
    /// <summary>Seam experiment: vertices close to the cut line are moved onto it (TMSPLINE_SEAM_SNAP=off: not).</summary>
    public static bool SeamSnap { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_SEAM_SNAP") != "off";
    public static int SimplifyTo { get; set; } = (int)Setting("TMSPLINE_SIMPLIFY", 0);
    public static float SimplifyError { get; set; } = Setting("TMSPLINE_SIMPLIFY_ERROR", 0.5f);
    /// <summary>Seam experiment: leave out the land the roads enclose (see the end of Build).</summary>
    public static bool OpenSeam { get; set; } = Environment.GetEnvironmentVariable("TMSPLINE_SEAM") == "open";
    /// <summary>
    /// Seam experiment: pieces of land the cut left on their own (fewer than this many triangles, not joined to the
    /// rest by a shared corner) are left out: bits of land the distance to the road's body did not quite reach, which
    /// stood in the gap as islands.
    /// </summary>
    private const int SeamIsland = 64;

    /// <summary>
    /// Also left out: a piece of land whose area lies mostly under a road, between its edges (under: a function telling
    /// that of a place): land under a road's deck that the cuts along both its edges left on its own, hanging under it.
    /// The land a track loop encloses is cut off all round too, but lies beside the road, not under it.
    /// </summary>
    private static void DropIslands(AdaptiveMesh mesh, Func<Vector3, bool>? under = null)
    {
        var parent = Enumerable.Range(0, mesh.Positions.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int a = Find(mesh.Indices[t]), b = Find(mesh.Indices[t + 1]), c = Find(mesh.Indices[t + 2]);
            parent[b] = a;
            parent[Find(c)] = a;
        }
        var size = new Dictionary<int, int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            var root = Find(mesh.Indices[t]);
            size[root] = size.GetValueOrDefault(root) + 1;
        }
        // Per piece, its area and how much of it lies under a road (the largest piece always stays).
        var area = new Dictionary<int, (double All, double Under)>();
        var where = new Dictionary<int, Vector3>();
        if (under is not null)
        {
            var largest = size.MaxBy(kv => kv.Value).Key;
            for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            {
                var root = Find(mesh.Indices[t]);
                if (root == largest) continue;
                Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
                var s = Vector3.Cross(b - a, c - a).Length() / 2;
                var (all, below) = area.GetValueOrDefault(root);
                area[root] = (all + s, below + (under((a + b + c) / 3) ? s : 0));
                where.TryAdd(root, (a + b + c) / 3);
            }
        }
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on")
            foreach (var (root, x) in area.OrderByDescending(kv => kv.Value.All).Take(8))
                Console.WriteLine($"  piece of {size[root]} triangles, {x.All:0} m2, {x.Under / Math.Max(x.All, 1e-9):P0} under a road, at {where[root]}");
        bool Keep(int root) => size[root] >= SeamIsland && !(area.TryGetValue(root, out var x) && x.Under > x.All / 2);
        var kept = new List<int>(mesh.Indices.Count);
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            if (Keep(Find(mesh.Indices[t])))
                kept.AddRange([mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]]);
        mesh.Indices.Clear();
        mesh.Indices.AddRange(kept);
    }

    /// <summary>The land is cut back this far from a road's body (its edges, the back of its surface): the band the seam's strip will fill; metres (TMSPLINE_SEAM_GAP).</summary>
    public static float SeamGap { get; set; } = Setting("TMSPLINE_SEAM_GAP", 6);

    /// <summary>How far the cut is softened where land and a room's wall meet, metres (TMSPLINE_VOXEL_SOFTEN; 0: hard).</summary>
    public static float Soften { get; set; } = Setting("TMSPLINE_VOXEL_SOFTEN", 4);

    /// <summary>
    /// The cells at a quality: the smallest ones' size, how far the land may bend away from a cell before it is split,
    /// and how far from a road's edge the smallest cells reach; metres. High is the settings above. Below it the
    /// smallest cells are 2 m: with larger ones (2.5, 3, 4 m were tried, and 4 m cells right beside the edges) the
    /// land stood a metre and more into the roads' space in many more places; so Low saves on the land away from the
    /// roads instead. Triangles against High's, measured on three maps: Fine 1.5 to 1.6 times, Medium 0.6, Low 0.42
    /// to 0.47.
    /// </summary>
    public static (float Voxel, float Tolerance, float Near) Cells(TerrainQuality quality) => quality switch
    {
        TerrainQuality.Fine => (1, 1, 3),
        TerrainQuality.Medium => (2, 2, 3),
        TerrainQuality.Low => (2, 6, 1),
        _ => (Voxel, Tolerance, Near),
    };

    private static float Ramp(float a, float b, float x)
    {
        x = Math.Clamp((x - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static float Setting(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private sealed class Node
    {
        /// <summary>Its lowest corner and size, in smallest cells.</summary>
        public int X, Y, Z, Size;
        public Node[]? Kids;
        public int Vertex = -1;
    }

    /// <summary>What a build needs of the roads (their rooms, and how far each place is from them): the same until a road or the land's extent changes.</summary>
    public sealed class Roads
    {
        internal (Func<Vector3, float> Distance, Func<float, float, float> Floor, Func<Vector3, (float Distance, Vector3 Deck)> Nearest, Func<Vector3, (float Edge, float Over, float Across, float Beyond)> Road, Func<Vector3, (float Distance, Vector3 Point, Vector3 Left, Vector3 Right)> Surface) Rooms { get; init; }
        internal float[] Away { get; init; } = [];
        internal int Columns { get; init; }
        internal int Rows { get; init; }
    }

    /// <summary>Plan squares of the distance to the roads, metres.</summary>
    private const float Coarse = 8;

    /// <summary>A part of the land is built with this much round it (cells there are as in the whole; beyond it they are left large), and its triangles' normals take in those up to half of it out; metres.</summary>
    private const float Margin = 48;

    /// <summary>The smoothing brush: the blur's width for one dab of amount 1, and the widest it gets; metres.</summary>
    private const float BlurPerDab = 2, MaxBlur = 12;

    public static Roads Prepare(TerrainGrid grid, TrackMap map)
    {
        var rooms = StitchedTerrain.Rooms(grid, map);
        var roomFloor = rooms.Floor;
        double width = (grid.Columns - 1) * grid.Cell, depth = (grid.Rows - 1) * grid.Cell;
        int dc = (int)Math.Ceiling(width / Coarse) + 1, dr = (int)Math.Ceiling(depth / Coarse) + 1;
        var away = new float[dc * dr];
        for (var r = 0; r < dr; r++)
            for (var c = 0; c < dc; c++)
                away[r * dc + c] = float.IsPositiveInfinity(roomFloor((float)(grid.X0 + (c + 0.5) * Coarse), (float)(grid.Z0 + (r + 0.5) * Coarse))) ? 1e6f : 0;
        {
            float ax = Coarse, dg = Coarse * MathF.Sqrt(2);
            void Relax(int i, int j, float cost) { if (away[j] + cost < away[i]) away[i] = away[j] + cost; }
            for (var r = 0; r < dr; r++)
                for (var c = 0; c < dc; c++)
                {
                    var i = r * dc + c;
                    if (c > 0) Relax(i, i - 1, ax);
                    if (r > 0) { Relax(i, i - dc, ax); if (c > 0) Relax(i, i - dc - 1, dg); if (c + 1 < dc) Relax(i, i - dc + 1, dg); }
                }
            for (var r = dr - 1; r >= 0; r--)
                for (var c = dc - 1; c >= 0; c--)
                {
                    var i = r * dc + c;
                    if (c + 1 < dc) Relax(i, i + 1, ax);
                    if (r + 1 < dr) { Relax(i, i + dc, ax); if (c + 1 < dc) Relax(i, i + dc + 1, dg); if (c > 0) Relax(i, i + dc - 1, dg); }
                }
        }
        return new Roads { Rooms = rooms, Away = away, Columns = dc, Rows = dr };
    }

    /// <param name="prepared">From <see cref="Prepare"/> for this land and these roads, or null.</param>
    /// <param name="region">Only this part of the land (plan: x0, z0, x1, z1), with Margin/2 round it for the normals; null: all.</param>
    public static AdaptiveMesh Build(TerrainGrid grid, TrackMap map, Roads? roads = null, (float X0, float Z0, float X1, float Z1)? region = null)
    {
        var (v, tolerance, near) = Cells(grid.Quality);
        // Coarser than High: every cell's point is moved onto the surface (as those of grazing cells are). The mean of
        // a large cell's edge crossings can lie well off it, and then land stood in the roads' space.
        var settle = grid.Quality > TerrainQuality.High || Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_SETTLE") == "on";
        var prepared = roads ?? Prepare(grid, map);
        var (room, _, nearest, road, surface) = prepared.Rooms;
        // The land's heights, also beyond its outline (each place without land takes its nearest land's: the octree
        // needs a height everywhere; what lies outside the outline is left out of the mesh at the end).
        var heights = (double[])grid.Heights.Clone();
        {
            var queue = new Queue<int>();
            for (var i = 0; i < heights.Length; i++) if (!double.IsNaN(heights[i])) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                int c = i % grid.Columns, r = i / grid.Columns;
                foreach (var (dc0, dr0) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int cc = c + dc0, rr = r + dr0;
                    if (cc < 0 || rr < 0 || cc >= grid.Columns || rr >= grid.Rows) continue;
                    var j = rr * grid.Columns + cc;
                    if (!double.IsNaN(heights[j])) continue;
                    heights[j] = heights[i];
                    queue.Enqueue(j);
                }
            }
        }
        float LandAt(float x, float z)
        {
            double fx = Math.Clamp((x - grid.X0) / grid.Cell, 0, grid.Columns - 1.001), fz = Math.Clamp((z - grid.Z0) / grid.Cell, 0, grid.Rows - 1.001);
            int c = (int)fx, r = (int)fz;
            double u = fx - c, w = fz - r;
            var i = r * grid.Columns + c;
            return (float)(heights[i] * (1 - u) * (1 - w) + heights[i + 1] * u * (1 - w) + heights[i + grid.Columns] * (1 - u) * w + heights[i + grid.Columns + 1] * u * w);
        }
        // The steepest the land gets (metres per metre; painted steps count up to 4), for whether a cell can hold its
        // surface; its lowest and highest.
        var slope = 0.3f;
        float lowest = float.MaxValue, highest = float.MinValue;
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
            {
                var h = heights[r * grid.Columns + c];
                if (double.IsNaN(h)) continue;
                lowest = MathF.Min(lowest, (float)h);
                highest = MathF.Max(highest, (float)h);
                if (c + 1 < grid.Columns && r + 1 < grid.Rows)
                    slope = MathF.Max(slope, (float)(Math.Sqrt(Math.Pow(heights[r * grid.Columns + c + 1] - h, 2) + Math.Pow(heights[(r + 1) * grid.Columns + c] - h, 2)) / grid.Cell));
            }
        if (lowest > highest) return new AdaptiveMesh();
        slope = MathF.Min(slope * 1.2f, 4);
        var points = map.Tracks.SelectMany(t => t.Points).ToList();
        double width = (grid.Columns - 1) * grid.Cell, depth = (grid.Rows - 1) * grid.Cell;

        // How far each place is from the roads (plan, metres; on squares of Coarse metres, the rooms' own: 0).
        var away = prepared.Away;
        int dc = prepared.Columns, dr = prepared.Rows;
        float Away(float x, float z)
        {
            int c = Math.Clamp((int)Math.Floor((x - grid.X0) / Coarse), 0, dc - 1), r = Math.Clamp((int)Math.Floor((z - grid.Z0) / Coarse), 0, dr - 1);
            return away[r * dc + c];
        }

        // The lattice: its origin, and the root cell round the land and the roads.
        var bottom = MathF.Min(lowest, points.Count > 0 ? (float)points.Min(p => p.Position.Y) : lowest) - 24;
        var top = highest + 8;
        var rootSize = 1;
        while (rootSize * v < Math.Max(Math.Max(width, depth), top - bottom)) rootSize *= 2;
        Vector3 World(int x, int y, int z) => new((float)(grid.X0 + x * v), bottom + y * v, (float)(grid.Z0 + z * v));

        var values = new Dictionary<long, float>();
        // The brush's dabs, by plan square (Coarse metres): each moves the land's surface out by its amount at its
        // centre (in, when negative), fading to nothing at its radius.
        // (First those the roads cut through, then from firstOver on those that come after the cut.)
        var dabs = grid.Edits.Concat(grid.EditsOver).ToArray();
        var firstOver = grid.Edits.Length;
        static Dictionary<(int, int), List<int>> BySquare(double[] dabs)
        {
            var at = new Dictionary<(int, int), List<int>>();
            for (var i = 0; i + 4 < dabs.Length; i += 5)
            {
                double x = dabs[i], z = dabs[i + 2], r = dabs[i + 3];
                for (var bx = (int)Math.Floor((x - r) / Coarse); bx <= (int)Math.Floor((x + r) / Coarse); bx++)
                    for (var bz = (int)Math.Floor((z - r) / Coarse); bz <= (int)Math.Floor((z + r) / Coarse); bz++)
                    {
                        if (!at.TryGetValue((bx, bz), out var list)) at[(bx, bz)] = list = [];
                        list.Add(i);
                    }
            }
            return at;
        }
        var dabsAt = BySquare(dabs);
        // The smoothing brush's dabs, kept the same way: where they lie the solid is blurred, the wider the more of
        // them add up there (BlurPerDab metres at an amount of 1, growing with the root of the sum, as blurring
        // again and again does; MaxBlur at most). Those the roads cut through blur the land before the cut, the
        // others the whole after it.
        var blurs = grid.Smooths.Concat(grid.SmoothsOver).ToArray();
        var firstBlurOver = grid.Smooths.Length;
        var blursAt = BySquare(blurs);
        float Blur(Vector3 p, bool over)
        {
            if (!blursAt.TryGetValue(((int)MathF.Floor(p.X / Coarse), (int)MathF.Floor(p.Z / Coarse)), out var list)) return 0;
            var sum = 0f;
            foreach (var i in list)
            {
                if (i >= firstBlurOver != over) continue;
                float dx = p.X - (float)blurs[i], dy = p.Y - (float)blurs[i + 1], dz = p.Z - (float)blurs[i + 2], r = (float)blurs[i + 3];
                var t = (dx * dx + dy * dy + dz * dz) / (r * r);
                if (t < 1) sum += MathF.Abs((float)blurs[i + 4]) * (1 - t) * (1 - t);
            }
            return MathF.Min(MaxBlur, BlurPerDab * MathF.Sqrt(sum));
        }
        // A field blurred at a place: its mean over the place, six points half the width off along the axes and the
        // eight corners the full width off (a flat or evenly sloping surface stays where it is).
        float Blurred(Func<Vector3, float> field, Vector3 p, bool over)
        {
            var h = blursAt.Count == 0 ? 0 : Blur(p, over);
            if (h < 0.05f) return field(p);
            var sum = 2 * field(p);
            float a = h / 2, c = h * 0.577f;
            sum += 2 * (field(p + new Vector3(a, 0, 0)) + field(p - new Vector3(a, 0, 0)) + field(p + new Vector3(0, a, 0)) + field(p - new Vector3(0, a, 0)) + field(p + new Vector3(0, 0, a)) + field(p - new Vector3(0, 0, a)));
            for (var k = 0; k < 8; k++) sum += field(p + new Vector3((k & 1) == 0 ? -c : c, (k & 2) == 0 ? -c : c, (k & 4) == 0 ? -c : c));
            return sum / 22;
        }
        float Painted(Vector3 p, bool over)
        {
            if (!dabsAt.TryGetValue(((int)MathF.Floor(p.X / Coarse), (int)MathF.Floor(p.Z / Coarse)), out var list)) return 0;
            var sum = 0f;
            foreach (var i in list)
            {
                if (i >= firstOver != over) continue;
                float dx = p.X - (float)dabs[i], dy = p.Y - (float)dabs[i + 1], dz = p.Z - (float)dabs[i + 2], r = (float)dabs[i + 3];
                var t = (dx * dx + dy * dy + dz * dz) / (r * r);
                if (t < 1) sum += (float)dabs[i + 4] * (1 - t) * (1 - t);
            }
            return sum;
        }
        // In a box: how far the dabs there can move the surface at most, and the smallest of their radii (0: none there).
        (float Reach, float Radius, float Over) PaintedIn(Vector3 c, float half)
        {
            if (dabsAt.Count == 0 && blursAt.Count == 0) return (0, 0, 0);
            // A large cell: taken as painted (its children are looked at properly).
            if (half > 4 * Coarse) return (1e6f, 0, firstOver < dabs.Length || firstBlurOver < blurs.Length ? 1e6f : 0);
            float reach = 0, radius = float.MaxValue, reachOver = 0;
            // Smoothing moves the surface by its blur's width at most.
            float blur = 0, blurOver = 0;
            var blurred = new HashSet<int>();
            for (var bx = (int)MathF.Floor((c.X - half) / Coarse); bx <= (int)MathF.Floor((c.X + half) / Coarse); bx++)
                for (var bz = (int)MathF.Floor((c.Z - half) / Coarse); bz <= (int)MathF.Floor((c.Z + half) / Coarse); bz++)
                {
                    if (!blursAt.TryGetValue((bx, bz), out var list)) continue;
                    foreach (var i in list)
                    {
                        var r = (float)blurs[i + 3];
                        if (MathF.Abs(c.X - (float)blurs[i]) > half + r || MathF.Abs(c.Y - (float)blurs[i + 1]) > half + r || MathF.Abs(c.Z - (float)blurs[i + 2]) > half + r || !blurred.Add(i)) continue;
                        if (i >= firstBlurOver) blurOver += MathF.Abs((float)blurs[i + 4]); else blur += MathF.Abs((float)blurs[i + 4]);
                        radius = MathF.Min(radius, r);
                    }
                }
            blur = MathF.Min(MaxBlur, BlurPerDab * MathF.Sqrt(blur));
            blurOver = MathF.Min(MaxBlur, BlurPerDab * MathF.Sqrt(blurOver));
            reach = blur + blurOver;
            reachOver = blurOver;
            var seen = new HashSet<int>();
            for (var bx = (int)MathF.Floor((c.X - half) / Coarse); bx <= (int)MathF.Floor((c.X + half) / Coarse); bx++)
                for (var bz = (int)MathF.Floor((c.Z - half) / Coarse); bz <= (int)MathF.Floor((c.Z + half) / Coarse); bz++)
                {
                    if (!dabsAt.TryGetValue((bx, bz), out var list)) continue;
                    foreach (var i in list)
                    {
                        var r = (float)dabs[i + 3];
                        if (MathF.Abs(c.X - (float)dabs[i]) > half + r || MathF.Abs(c.Y - (float)dabs[i + 1]) > half + r || MathF.Abs(c.Z - (float)dabs[i + 2]) > half + r || !seen.Add(i)) continue;
                        reach += MathF.Abs((float)dabs[i + 4]);
                        if (i >= firstOver) reachOver += MathF.Abs((float)dabs[i + 4]);
                        radius = MathF.Min(radius, r);
                    }
                }
            return reach > 0 ? (reach, radius, reachOver) : (0, 0, 0);
        }

        float Land(Vector3 p) => p.Y - LandAt(p.X, p.Z) - Painted(p, false);
        Func<Vector3, float> landField = Land, cutField = Cut;
        float Field(Vector3 p) => Blurred(cutField, p, true);
        float Cut(Vector3 p)
        {
            var f = Blurred(landField, p, false);
            if (f < 0 && Away(p.X, p.Z) < 2 * Coarse)
            {
                var cut = -room(p);
                // A soft cut over the road: where the land's surface and a room's wall come together (a tunnel's
                // mouth, a cutting's rim, a thin roof over a shallow tunnel) the edge between them is rounded off, and
                // land thinner than half of Soften goes altogether; as a hard cut it stood there in teeth and shards.
                // Beside the road, at the height of its edges and under it, the cut stays hard: the land meets them
                // exactly. Straight over the road it is soft from the edges' height on: a road coming out of the land
                // comes out where its cover gets thin, not through a wedge of land thinning to nothing along it.
                var (edge, over, across, _) = road(p);
                var k = Soften * (edge < 100 ? MathF.Max(Ramp(0.5f, 3, over), (1 - Ramp(0.8f, 1, across)) * Ramp(0, 0.75f, over)) : 1);
                if (k > 0.01f)
                {
                    var h = MathF.Max(k - MathF.Abs(f - cut), 0) / k;
                    f = MathF.Max(f, cut) + h * h * k / 4;
                }
                else f = MathF.Max(f, cut);
            }
            // Land worked into the roads' space comes after their cut.
            if (firstOver < dabs.Length) f -= Painted(p, true);
            return f;
        }
        float At(int x, int y, int z)
        {
            var key = ((long)x << 42) | ((long)y << 21) | (long)z;
            if (values.TryGetValue(key, out var f)) return f;
            return values[key] = Field(World(x, y, z));
        }

        // The cell size wanted at a distance from the roads, in smallest cells.
        int Wanted(float distance)
        {
            var size = 1;
            for (var d = near; distance > d && size * 2 * v <= Coarsest; d *= 2) size *= 2;
            return size;
        }

        void Split(Node n)
        {
            if (n.Size == 1) return;
            var half = n.Size / 2;
            var c = World(n.X + half, n.Y + half, n.Z + half);
            var reach = n.Size * v * 0.866f;
            // Outside the part asked for (and its margin): left as it is.
            if (region is { } part)
            {
                var side = n.Size * v / 2;
                if (c.X + side < part.X0 - Margin || c.X - side > part.X1 + Margin || c.Z + side < part.Z0 - Margin || c.Z - side > part.Z1 + Margin) return;
            }
            // How far the cell is from the roads' edges at least (that is where the land has to meet the road; a vault's
            // top and the floor under the road's middle are further): exactly within a square of them, else by the squares.
            var from = MathF.Max(0, Away(c.X, c.Z) - reach - Coarse);
            var above = 0f;
            if (from <= 0)
            {
                var (edge, up, _, _) = road(c);
                from = MathF.Max(0, MathF.Min(edge, 2 * Coarse) - reach);
                above = up;
            }
            // Split only where the surface may pass: the land's, outside the rooms, or a room's wall under the land.
            var over = c.Y - LandAt(c.X, c.Z);
            var span = n.Size * v * (0.5f + 0.71f * slope);
            // Where the brush painted, the surface may lie further from the height map's, and the cells are small enough
            // for the dabs (a quarter of the smallest radius).
            var (painted, dabRadius, paintedOver) = PaintedIn(c, n.Size * v / 2);
            var landNear = MathF.Abs(over) <= span + painted;
            var wall = false;
            var inRoom = false;
            if (Away(c.X, c.Z) - reach < 2 * Coarse && over < span + painted)
            {
                var d = room(c);
                // (Dabs that come after the cut can stand in the room, up to their amounts from its wall.)
                wall = MathF.Abs(d) <= reach + paintedOver;
                inRoom = d < -reach - paintedOver;
            }
            // A rim (the land's surface and a room's wall in one cell, over the road: a tunnel's mouth, a cutting's top,
            // a thin roof) gets the smallest cells wherever it is: one point per cell cannot stand for two surfaces,
            // and larger cells stood there in teeth.
            var rim = landNear && wall && !inRoom && above + reach > 1;
            var wanted = Wanted(from);
            if (painted > 0 && landNear && dabRadius > 0) while (wanted > 1 && wanted * v > dabRadius / 4) wanted /= 2;
            // The land's own shape asks for smaller cells too, wherever it is: a cell gives the land one point, at the
            // mean of its corners' heights, and where the land in it bends away from that by more than Tolerance (a
            // crest, a hollow, a steep bank's foot) the cell is split. Far from the roads the cells were large whatever
            // the land did, and hills came out in facets metres off the height map (rock and grass with them).
            var rough = false;
            if (!rim && n.Size <= wanted && n.Size > 1 && tolerance > 0 && landNear && !inRoom)
            {
                float side = n.Size * v / 2, q = side / 2;
                var corners = (LandAt(c.X - side, c.Z - side) + LandAt(c.X + side, c.Z - side) + LandAt(c.X - side, c.Z + side) + LandAt(c.X + side, c.Z + side)) / 4;
                var inner = (LandAt(c.X - q, c.Z - q) + LandAt(c.X + q, c.Z - q) + LandAt(c.X - q, c.Z + q) + LandAt(c.X + q, c.Z + q)) / 4;
                rough = MathF.Abs(LandAt(c.X, c.Z) - corners) > tolerance || MathF.Abs(inner - corners) > tolerance;
            }
            if (!rim && !rough && n.Size <= wanted) return;
            if (!(landNear && !inRoom) && !wall) return;
            n.Kids = new Node[8];
            for (var k = 0; k < 8; k++)
            {
                n.Kids[k] = new Node { X = n.X + (k & 1) * half, Y = n.Y + ((k >> 1) & 1) * half, Z = n.Z + ((k >> 2) & 1) * half, Size = half };
                Split(n.Kids[k]);
            }
        }
        var root = new Node { Size = rootSize };
        Split(root);

        var mesh = new AdaptiveMesh();
        int Vertex(Node n)
        {
            if (n.Vertex >= 0) return n.Vertex;
            var sum = Vector3.Zero;
            var count = 0;
            Span<float> corner = stackalloc float[8];
            for (var k = 0; k < 8; k++) corner[k] = At(n.X + (k & 1) * n.Size, n.Y + ((k >> 1) & 1) * n.Size, n.Z + ((k >> 2) & 1) * n.Size);
            for (var a = 0; a < 8; a++)
                for (var axis = 0; axis < 3; axis++)
                {
                    var b = a | (1 << axis);
                    if (b == a || (corner[a] < 0) == (corner[b] < 0)) continue;
                    var t = corner[a] / (corner[a] - corner[b]);
                    Vector3 pa = new(a & 1, (a >> 1) & 1, (a >> 2) & 1), pb = new(b & 1, (b >> 1) & 1, (b >> 2) & 1);
                    sum += pa + (pb - pa) * t;
                    count++;
                }
            var p = count > 0 ? sum / count : new Vector3(0.5f);
            n.Vertex = mesh.Positions.Count;
            var place = new Vector3((float)(grid.X0 + (n.X + p.X * n.Size) * v), bottom + (n.Y + p.Y * n.Size) * v, (float)(grid.Z0 + (n.Z + p.Z * n.Size) * v));
            if (count == 0 || settle)
            {
                // A cell the surface only grazes (it passes through smaller cells beside it, and none of this one's own
                // edges cross it) still gives a point to their triangles: the place in it nearest the surface, found
                // from its middle down the field's slope. Left in the middle it stood out of the land as a spike.
                Vector3 low = World(n.X, n.Y, n.Z), high = World(n.X + n.Size, n.Y + n.Size, n.Z + n.Size);
                var e = 0.25f * v;
                for (var step = 0; step < 12; step++)
                {
                    var f = Field(place);
                    if (MathF.Abs(f) < 0.05f) break;
                    var g = new Vector3(Field(place + new Vector3(e, 0, 0)) - Field(place - new Vector3(e, 0, 0)), Field(place + new Vector3(0, e, 0)) - Field(place - new Vector3(0, e, 0)), Field(place + new Vector3(0, 0, e)) - Field(place - new Vector3(0, 0, e))) / (2 * e);
                    // On one of the cell's faces it goes on along the face (not against it, which gets nowhere).
                    if ((place.X <= low.X && f * g.X > 0) || (place.X >= high.X && f * g.X < 0)) g.X = 0;
                    if ((place.Y <= low.Y && f * g.Y > 0) || (place.Y >= high.Y && f * g.Y < 0)) g.Y = 0;
                    if ((place.Z <= low.Z && f * g.Z > 0) || (place.Z >= high.Z && f * g.Z < 0)) g.Z = 0;
                    if (g.LengthSquared() < 1e-6f) break;
                    place = Vector3.Clamp(place - g * (f / g.LengthSquared()), low, high);
                }
            }
            mesh.Positions.Add(place);
            mesh.Pinned.Add(false);
            return n.Vertex;
        }

        // A child by its place: its bit on each of the three axes (0 x, 1 y, 2 z).
        static Node Kid(Node n, int axisA, int bitA, int axisB, int bitB, int axisC, int bitC) => n.Kids![(bitA << axisA) | (bitB << axisB) | (bitC << axisC)];

        // The four cells round an edge along `e`, by their side across p = e+1 and q = e+2 (n[p + 2q]).
        void Edge(Node n0, Node n1, Node n2, Node n3, int e)
        {
            int p = (e + 1) % 3, q = (e + 2) % 3;
            if (n0.Kids is null && n1.Kids is null && n2.Kids is null && n3.Kids is null)
            {
                // The edge is the smallest cell's: at its corner towards the other three, along e.
                Span<int> at = stackalloc int[3];
                var small = n0;
                var si = 0;
                if (n1.Size < small.Size) { small = n1; si = 1; }
                if (n2.Size < small.Size) { small = n2; si = 2; }
                if (n3.Size < small.Size) { small = n3; si = 3; }
                at[0] = small.X; at[1] = small.Y; at[2] = small.Z;
                if ((si & 1) == 0) at[p] += small.Size;
                if ((si & 2) == 0) at[q] += small.Size;
                var f0 = At(at[0], at[1], at[2]);
                at[e] += small.Size;
                var f1 = At(at[0], at[1], at[2]);
                if ((f0 < 0) == (f1 < 0)) return;
                int a = Vertex(n0), b = Vertex(n1), c = Vertex(n3), d = Vertex(n2);
                if (f0 >= 0) (b, d) = (d, b);
                if (a != b && b != c && a != c) { mesh.Indices.Add(a); mesh.Indices.Add(b); mesh.Indices.Add(c); }
                if (a != c && c != d && a != d) { mesh.Indices.Add(a); mesh.Indices.Add(c); mesh.Indices.Add(d); }
                return;
            }
            for (var h = 0; h < 2; h++)
                Edge(n0.Kids is null ? n0 : Kid(n0, e, h, p, 1, q, 1), n1.Kids is null ? n1 : Kid(n1, e, h, p, 0, q, 1),
                    n2.Kids is null ? n2 : Kid(n2, e, h, p, 1, q, 0), n3.Kids is null ? n3 : Kid(n3, e, h, p, 0, q, 0), e);
        }

        // Two cells sharing a face across `a` (n0 on its low side).
        void Face(Node n0, Node n1, int a)
        {
            if (n0.Kids is null && n1.Kids is null) return;
            int b = (a + 1) % 3, c = (a + 2) % 3;
            for (var i = 0; i < 2; i++)
                for (var j = 0; j < 2; j++)
                    Face(n0.Kids is null ? n0 : Kid(n0, a, 1, b, i, c, j), n1.Kids is null ? n1 : Kid(n1, a, 0, b, i, c, j), a);
            // The edges in the shared face: along e, through its middle across o.
            foreach (var (e, o) in new[] { (b, c), (c, b) })
                for (var h = 0; h < 2; h++)
                {
                    Node Pick(int sideA, int sideO)
                    {
                        var n = sideA == 0 ? n0 : n1;
                        return n.Kids is null ? n : Kid(n, a, 1 - sideA, o, sideO, e, h);
                    }
                    // Across p = e+1 and q = e+2: one of them is a, the other o.
                    if ((e + 1) % 3 == a) Edge(Pick(0, 0), Pick(1, 0), Pick(0, 1), Pick(1, 1), e);
                    else Edge(Pick(0, 0), Pick(0, 1), Pick(1, 0), Pick(1, 1), e);
                }
        }

        void Cell(Node n)
        {
            if (n.Kids is null) return;
            foreach (var k in n.Kids) Cell(k);
            for (var a = 0; a < 3; a++)
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                for (var i = 0; i < 2; i++)
                    for (var j = 0; j < 2; j++)
                        Face(Kid(n, a, 0, b, i, c, j), Kid(n, a, 1, b, i, c, j), a);
                // The edges along a through the cell's middle, in each half.
                for (var h = 0; h < 2; h++)
                    Edge(Kid(n, a, h, b, 0, c, 0), Kid(n, a, h, b, 1, c, 0), Kid(n, a, h, b, 0, c, 1), Kid(n, a, h, b, 1, c, 1), a);
            }
        }
        Cell(root);

        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on")
        {
            var bySize = new SortedDictionary<int, (int Cells, int Inside, int Away0)>();
            void Count(Node n)
            {
                if (n.Kids is not null) { foreach (var k in n.Kids) Count(k); return; }
                if (n.Vertex < 0) return;
                var c = World(n.X + n.Size / 2, n.Y + n.Size / 2, n.Z + n.Size / 2);
                var e = bySize.GetValueOrDefault(n.Size);
                bySize[n.Size] = (e.Cells + 1, e.Inside + (c.Y < LandAt(c.X, c.Z) - 2 * v ? 1 : 0), e.Away0 + (Away(c.X, c.Z) <= 0 ? 1 : 0));
            }
            Count(root);
            foreach (var (size, e) in bySize) Console.WriteLine($"  voxel cells of {size * v} m: {e.Cells} with a point, {e.Inside} of them well under the land, {e.Away0} in a road's square");
        }
        // Debug: TMSPLINE_VOXEL_PROBE=x,y,z prints the cells whose points lie within 2 m of that place.
        if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_PROBE") is { } probe)
        {
            var at = probe.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            void Probe(Node n)
            {
                if (n.Kids is not null) { foreach (var k in n.Kids) Probe(k); return; }
                if (n.Vertex < 0 || Vector3.Distance(mesh.Positions[n.Vertex], new Vector3(at[0], at[1], at[2])) > (at.Length > 3 ? at[3] : 2)) return;
                Console.WriteLine($"  probe: cell of {n.Size * v} m from {World(n.X, n.Y, n.Z)}, point {mesh.Positions[n.Vertex]}");
                for (var k = 0; k < 8; k++)
                {
                    var w = World(n.X + (k & 1) * n.Size, n.Y + ((k >> 1) & 1) * n.Size, n.Z + ((k >> 2) & 1) * n.Size);
                    Console.WriteLine($"    corner {w}: field {At(n.X + (k & 1) * n.Size, n.Y + ((k >> 1) & 1) * n.Size, n.Z + ((k >> 2) & 1) * n.Size):0.00}, land {LandAt(w.X, w.Z):0.0}, room {room(w):0.0}, away {Away(w.X, w.Z):0}");
                }
            }
            Probe(root);
        }
        // Only where the editor has land (its outline; the heights are not used): the rest is left out.
        // Nor the cuts' floors under the driving surfaces (the road lies on them): triangles at a driving surface that
        // face as it does. Land coming to the road from the side faces another way and stays, up to the road's edge.
        var under = new Vector3[mesh.Positions.Count];
        Parallel.For(0, under.Length, i =>
        {
            var p = mesh.Positions[i];
            if (Away(p.X, p.Z) > 0) return;
            var (d, deck) = nearest(p);
            // A point of a large cell can lie over the floor it stands for, in the room (over the driving surface):
            // back onto the floor.
            // (Not where land was worked into the road's space: that stays where it was put, and its floor shows.)
            if (Painted(p, true) != 0 || Blur(p, true) > 0) return;
            if (deck != default && d < 0 && d > -2 * v)
            {
                p += deck * d;
                mesh.Positions[i] = p;
            }
            // Not near where the land's own surface comes down to the road (a road half sunk into it): there the floor
            // shows through the slot between the land's edge and the driving surface.
            if (MathF.Abs(d) < v && LandAt(p.X, p.Z) - p.Y > FloorCover + v) under[i] = deck;
        });
        var kept = new AdaptiveMesh();
        var index = new Dictionary<int, int>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
            var mid = (a + b + c) / 3;
            if (double.IsNaN(AdaptiveTerrainBuilder.SampleGrid(grid, mid.X, mid.Z))) continue;
            if (region is { } part && (mid.X < part.X0 - Margin / 2 || mid.X > part.X1 + Margin / 2 || mid.Z < part.Z0 - Margin / 2 || mid.Z > part.Z1 + Margin / 2)) continue;
            Vector3 ua = under[mesh.Indices[t]], ub = under[mesh.Indices[t + 1]], uc = under[mesh.Indices[t + 2]];
            if (!KeepFloor && ua != default && ub != default && uc != default)
            {
                var facing = Vector3.Cross(b - a, c - a);
                if (facing.LengthSquared() > 1e-12f && Vector3.Dot(Vector3.Normalize(facing), Vector3.Normalize(ua + ub + uc)) > FloorAlike) continue;
            }
            for (var k = 0; k < 3; k++)
            {
                var i = mesh.Indices[t + k];
                if (!index.TryGetValue(i, out var id))
                {
                    id = kept.Positions.Count;
                    kept.Positions.Add(mesh.Positions[i]);
                    kept.Pinned.Add(false);
                    index[i] = id;
                }
                kept.Indices.Add(id);
            }
        }

        // Seam experiment: how far a place is from a road's body, less SeamGap (negative: the land there goes). The body
        // is the road's edges seen from outside them (the half of the plane across the road beyond each edge: never more
        // than 90 degrees from the road's own outward direction, the owner's rule), an open road's end seen from past it,
        // and the back of its driving surface (under an open road, round a closed tube).
        float SeamField(Vector3 p)
        {
            // The smooth field: how far from the road's surface (its edges included, past an open end its end), less
            // the gap, and inside a road's room how deep in it (negative); no jumps, so the cut's line is clean.
            if (SeamSmooth) return MathF.Min(surface(p).Distance - SeamGap, room(p));
            return SeamRules(p);
        }
        float SeamRules(Vector3 p)
        {
            var (toEdge, over, across, beyond) = road(p);
            var (toRoom, deck) = nearest(p);
            var body = across >= 1 || !SeamHalfDisc ? toEdge : float.MaxValue;
            body = MathF.Min(body, beyond);
            // Between the edges, outside the road's room: under the line between the edges, how far under it; over it
            // (under a road bent downwards, whose middle stands over its edges) nothing, the road's own space. Land
            // higher than that over the line (a tunnel's roof) does not count.
            if (toEdge < 100 && across < 1 && toRoom > 0 && over < CrownMax) body = MathF.Min(body, MathF.Max(0, -over));
            if (deck != default && toRoom > -1) body = MathF.Min(body, MathF.Max(toRoom, 0));
            // Land inside a road's room (reaching into it where the voxels missed it) is always the road's: without
            // this the field jumped back up to the edges' distance a metre inside, and the cut followed that jump.
            if (toRoom <= 0) body = 0;
            return body - SeamGap;
        }

        // Simplification experiment (TMSPLINE_SIMPLIFY=triangles): the whole land simplified first (quadric edge
        // collapse), its edges near the roads kept no longer than the gap, and no vertex put inside a road's room.
        if (SimplifyTo > 0 && region is null)
        {
            var before = kept.Indices.Count / 3;
            kept = MeshSimplifier.Simplify(kept, SimplifyTo, SimplifyError,
                p => road(p).Edge < 3 * SeamGap ? SeamGap : float.MaxValue,
                p => room(p) > 0.2f);
            if (Environment.GetEnvironmentVariable("TMSPLINE_FLIPS") != "off") MeshSimplifier.ImproveByFlips(kept, float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_FLIP_DEG"), System.Globalization.CultureInfo.InvariantCulture, out var flipDeg) ? flipDeg : 35);
            if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on")
                Console.WriteLine($"  simplified {before} triangles to {kept.Indices.Count / 3}");
            // Remeshing experiment (TMSPLINE_REMESH=on): the simplified land's triangles made even (MeshRemesher), its
            // vertices put back onto the land's surface (the field's zero, down its slope).
            if (Environment.GetEnvironmentVariable("TMSPLINE_REMESH") == "on")
            {
                Vector3 Project(Vector3 p)
                {
                    const float h = 0.2f;
                    for (var step = 0; step < 3; step++)
                    {
                        var f = Field(p);
                        if (MathF.Abs(f) < 0.02f) break;
                        var g = new Vector3(Field(p + Vector3.UnitX * h) - Field(p - Vector3.UnitX * h), Field(p + Vector3.UnitY * h) - Field(p - Vector3.UnitY * h),
                            Field(p + Vector3.UnitZ * h) - Field(p - Vector3.UnitZ * h)) / (2 * h);
                        if (g.LengthSquared() < 0.01f) break;
                        var move = g * (f / g.LengthSquared());
                        if (move.Length() > 2) break;
                        p -= move;
                    }
                    return p;
                }
                kept = MeshRemesher.Remesh(kept, Project);
                if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on")
                    Console.WriteLine($"  remeshed to {kept.Indices.Count / 3} triangles");
            }
        }
        if (OpenSeam)
        {
            kept = CutSeam(kept, SeamField);
            if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
                Console.WriteLine($"  right after the cut: {kept.Indices.Distinct().Count(i => SeamField(kept.Positions[i]) < -0.1f)} used vertices inside it");
            if (Environment.GetEnvironmentVariable("TMSPLINE_FLIPS") != "off") MeshSimplifier.ImproveByFlips(kept, float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_FLIP_DEG"), System.Globalization.CultureInfo.InvariantCulture, out var flipDeg) ? flipDeg : 35);
            if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
            {
                // Kept land under a road between its edges, close to its room: why the cut left it.
                var rows = new List<string>();
                foreach (var p in kept.Indices.Distinct().Select(i => kept.Positions[i]))
                {
                    var (toEdge, over, across, beyond) = road(p);
                    var (toRoom, deck) = nearest(p);
                    if (toEdge < 100 && across < 1 && over < 0 && toRoom < 8)
                        rows.Add($"    at {p.X:0},{p.Y:0},{p.Z:0}: to room {toRoom:0.0}, deck {(deck != default ? "yes" : "no")}, edge {toEdge:0.0}, across {across:0.00}, over {over:0.0}, field {SeamField(p):0.0}");
                }
                Console.WriteLine($"  {rows.Count} kept vertices under a road within 8 m of its room");
                foreach (var r in rows.Take(25)) Console.WriteLine(r);
            }
            DropIslands(kept, p => road(p) is var r && r.Edge < 100 && r.Across < 1 && r.Over < 0);
            if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
            {
                // Land facing down under a road (its back to the sky): where, and the field there.
                var rows = new List<(Vector3 Mid, float Area)>();
                for (var t = 0; t + 2 < kept.Indices.Count; t += 3)
                {
                    Vector3 a = kept.Positions[kept.Indices[t]], b = kept.Positions[kept.Indices[t + 1]], c = kept.Positions[kept.Indices[t + 2]];
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-8f || Vector3.Normalize(n).Y > -0.3f) continue;
                    var mid = (a + b + c) / 3;
                    if (surface(mid).Distance > 30) continue;
                    rows.Add((mid, n.Length() / 2));
                }
                Console.WriteLine($"  {rows.Count} land triangles facing down within 30 m of a road, {rows.Sum(r => r.Area):0} m2");
                foreach (var g in rows.GroupBy(r => ((int)MathF.Floor(r.Mid.X / 40), (int)MathF.Floor(r.Mid.Z / 40))).OrderByDescending(g => g.Sum(r => r.Area)).Take(8))
                {
                    var m = g.OrderByDescending(r => r.Area).First().Mid;
                    Console.WriteLine($"    {g.Count()} near {m.X:0},{m.Y:0},{m.Z:0} ({g.Sum(r => r.Area):0} m2): road surface {surface(m).Distance:0.0} m away, room {room(m):0.0}, field {SeamField(m):0.0}, land field {Field(m):0.0}");
                }
            }
            if (Environment.GetEnvironmentVariable("TMSPLINE_PROBE") is { Length: > 0 } probeAt)
            {
                var pv = probeAt.Split(';').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var q = new Vector3(pv[0], pv[1], pv[2]);
                var rd = road(q);
                Console.WriteLine($"  probe {q}: edge {rd.Edge:0.0}, over {rd.Over:0.0}, across {rd.Across:0.00}, beyond {rd.Beyond:0.0}; surface {surface(q).Distance:0.0} at {surface(q).Point}; room {room(q):0.0}; field {SeamField(q):0.0}");
            }
            // The zipper: the gap closed with strips from the land's rim to the road edges (TMSPLINE_ZIP=off: left open).
            if (Environment.GetEnvironmentVariable("TMSPLINE_ZIP") != "off")
            {
                var strips = SeamZipper.Zip(kept, map, surface, room, SeamGap);
                if (Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_STATS") == "on") Console.WriteLine($"  zipped {strips} strip triangles");
            }
        }
        return kept;
    }

    /// <summary>
    /// Seam experiment: edges whose two ends lie outside the cut but whose middle lies in it (a long edge reaching over
    /// the gap, whose triangles bridged it and touched the road) are split in their middle, again where needed, so the
    /// cut sees that they pass through it.
    /// </summary>
    private static AdaptiveMesh SplitAcross(AdaptiveMesh mesh, Func<Vector3, float> field)
    {
        var pos = mesh.Positions.ToList();
        var g = new List<float>(pos.Select(field));
        var faces = new List<int[]>();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3) faces.Add([mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]]);
        for (var round = 0; round < 4; round++)
        {
            var split = new Dictionary<long, int>();
            foreach (var f in faces)
                for (var k = 0; k < 3; k++)
                {
                    int a = f[k], b = f[(k + 1) % 3];
                    if (g[a] < 0 || g[b] < 0) continue;
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (split.ContainsKey(key)) continue;
                    var mid = (pos[a] + pos[b]) / 2;
                    var gm = field(mid);
                    if (gm >= 0) continue;
                    split[key] = pos.Count;
                    pos.Add(mid);
                    g.Add(gm);
                }
            if (split.Count == 0) break;
            // Each triangle with split edges: into two, three or four.
            var next = new List<int[]>(faces.Count + split.Count * 2);
            int? Mid(int a, int b) => split.TryGetValue(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a, out var m) ? m : null;
            foreach (var f in faces)
            {
                int a = f[0], b = f[1], c = f[2];
                int? ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                var count = (ab is null ? 0 : 1) + (bc is null ? 0 : 1) + (ca is null ? 0 : 1);
                if (count == 0) { next.Add(f); continue; }
                if (count == 3) { next.Add([a, ab!.Value, ca!.Value]); next.Add([ab.Value, b, bc!.Value]); next.Add([ca.Value, bc.Value, c]); next.Add([ab.Value, bc.Value, ca.Value]); continue; }
                // Rotate so that the split edge (or the first of two) starts at a.
                while (Mid(a, b) is null || (count == 2 && Mid(b, c) is null && Mid(c, a) is not null && Mid(a, b) is null))
                {
                    (a, b, c) = (b, c, a);
                    if (Mid(a, b) is not null) break;
                }
                var m1 = Mid(a, b)!.Value;
                if (count == 1) { next.Add([a, m1, c]); next.Add([m1, b, c]); continue; }
                if (Mid(b, c) is { } m2) { next.Add([a, m1, c]); next.Add([m1, b, m2]); next.Add([m1, m2, c]); continue; }
                var m3 = Mid(c, a)!.Value;
                next.Add([a, m1, m3]); next.Add([m1, b, c]); next.Add([m1, c, m3]);
            }
            faces = next;
        }
        var result = new AdaptiveMesh();
        result.Positions.AddRange(pos);
        result.Pinned.AddRange(pos.Select(_ => false));
        foreach (var f in faces) result.Indices.AddRange(f);
        return result;
    }

    /// <summary>
    /// Seam experiment: the land cut back from the roads along one continuous line, where the field (SeamField) is
    /// zero, the part where it is negative left out. A vertex already close to the line (within a third of its
    /// shortest edge) is moved onto it first, and the line then passes through it: the cut leaves no slivers. The new
    /// corners on cut edges are shared by the triangles on both sides.
    /// </summary>
    private static AdaptiveMesh CutSeam(AdaptiveMesh mesh, Func<Vector3, float> field)
    {
        mesh = SplitAcross(mesh, field);
        var n = mesh.Positions.Count;
        var pos = mesh.Positions.ToArray();
        var g = new float[n];
        Parallel.For(0, n, i => g[i] = field(pos[i]));
        if (Environment.GetEnvironmentVariable("TMSPLINE_SEAM_DIAG") == "on")
        {
            var differ = 0;
            for (var i = 0; i < n; i++) if (MathF.Abs(field(pos[i]) - g[i]) > 1e-3f) differ++;
            Console.WriteLine($"  field values differing between parallel and sequential: {differ} of {n}");
        }
        // Each vertex's shortest edge.
        var shortest = Enumerable.Repeat(float.MaxValue, n).ToArray();
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                var l = Vector3.Distance(pos[a], pos[b]);
                shortest[a] = MathF.Min(shortest[a], l);
                shortest[b] = MathF.Min(shortest[b], l);
            }
        Parallel.For(0, n, i =>
        {
            if (!SeamSnap || MathF.Abs(g[i]) >= shortest[i] / 3) return;
            // Onto the line: down (or up) the field's slope, by its value.
            const float h = 0.25f;
            var p = pos[i];
            var grad = new Vector3(field(p + Vector3.UnitX * h) - field(p - Vector3.UnitX * h),
                field(p + Vector3.UnitY * h) - field(p - Vector3.UnitY * h),
                field(p + Vector3.UnitZ * h) - field(p - Vector3.UnitZ * h)) / (2 * h);
            if (grad.LengthSquared() < 0.25f) return;
            // Up to three steps; the move kept only if it really ends on the line (the field is the smallest of
            // several distances, and one step across a fold between them can land far off it).
            var q = p;
            var value = g[i];
            for (var step = 0; step < 3 && MathF.Abs(value) > 0.05f; step++)
            {
                q -= grad * (value / grad.LengthSquared());
                value = field(q);
                grad = new Vector3(field(q + Vector3.UnitX * h) - field(q - Vector3.UnitX * h),
                    field(q + Vector3.UnitY * h) - field(q - Vector3.UnitY * h),
                    field(q + Vector3.UnitZ * h) - field(q - Vector3.UnitZ * h)) / (2 * h);
                if (grad.LengthSquared() < 0.25f) break;
            }
            if (MathF.Abs(value) > 0.05f || Vector3.Distance(p, q) > shortest[i] / 2) return;
            pos[i] = q;
            g[i] = 0;
        });

        var result = new AdaptiveMesh();
        var index = new int[n];
        Array.Fill(index, -1);
        var crossings = new Dictionary<long, int>();
        int Kept(int i)
        {
            if (index[i] < 0)
            {
                index[i] = result.Positions.Count;
                result.Positions.Add(pos[i]);
                result.Pinned.Add(false);
            }
            return index[i];
        }
        int Crossing(int i, int j)
        {
            if (g[i] == 0) return Kept(i);
            if (g[j] == 0) return Kept(j);
            var key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            if (crossings.TryGetValue(key, out var id)) return id;
            // Where the field really is zero along the edge (it bends between the ends): bisection, from the ends'
            // opposite signs.
            float lo = 0, hi = 1;
            var loIn = g[i] >= 0;
            for (var step = 0; step < 14; step++)
            {
                var mid = (lo + hi) / 2;
                if (field(pos[i] + (pos[j] - pos[i]) * mid) >= 0 == loIn) lo = mid; else hi = mid;
            }
            var at = pos[i] + (pos[j] - pos[i]) * ((lo + hi) / 2);
            id = result.Positions.Count;
            result.Positions.Add(at);
            result.Pinned.Add(false);
            crossings[key] = id;
            return id;
        }
        Span<int> polygon = stackalloc int[6];
        for (var t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int c0 = mesh.Indices[t], c1 = mesh.Indices[t + 1], c2 = mesh.Indices[t + 2];
            var count = 0;
            for (var k = 0; k < 3; k++)
            {
                int i = k == 0 ? c0 : k == 1 ? c1 : c2, j = k == 0 ? c1 : k == 1 ? c2 : c0;
                bool inI = g[i] >= 0, inJ = g[j] >= 0;
                if (inI) polygon[count++] = Kept(i);
                if (inI != inJ)
                {
                    var c = Crossing(i, j);
                    if (count == 0 || polygon[count - 1] != c) polygon[count++] = c;
                }
            }
            if (count > 1 && polygon[count - 1] == polygon[0]) count--;
            for (var k = 1; k + 1 < count; k++)
            {
                if (polygon[0] == polygon[k] || polygon[k] == polygon[k + 1] || polygon[0] == polygon[k + 1]) continue;
                result.Indices.Add(polygon[0]);
                result.Indices.Add(polygon[k]);
                result.Indices.Add(polygon[k + 1]);
            }
        }
        return result;
    }

}
