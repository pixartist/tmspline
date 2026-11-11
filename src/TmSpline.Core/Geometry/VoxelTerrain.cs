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
        float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_VOXEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1f;

    /// <summary>Within this of a road the cells are the smallest, and no cell is larger than Coarsest; metres (TMSPLINE_VOXEL_NEAR, TMSPLINE_VOXEL_COARSEST).</summary>
    public static float Near { get; set; } = Setting("TMSPLINE_VOXEL_NEAR", 3);
    public static float Coarsest { get; set; } = Setting("TMSPLINE_VOXEL_COARSEST", 32);

    /// <summary>A triangle at a driving surface is the cut's floor under it (left out) when it faces within about 35 degrees of it.</summary>
    private const float FloorAlike = 0.82f;
    /// <summary>Debug: TMSPLINE_VOXEL_FLOOR=keep leaves the cuts' floors under the roads in.</summary>
    private static readonly bool KeepFloor = Environment.GetEnvironmentVariable("TMSPLINE_VOXEL_FLOOR") == "keep";
    /// <summary>And when the land's own surface lies at least this far over it, metres.</summary>
    private const float FloorCover = 2f;

    /// <summary>How far the cut is softened where land and a room's wall meet, metres (TMSPLINE_VOXEL_SOFTEN; 0: hard).</summary>
    public static float Soften { get; set; } = Setting("TMSPLINE_VOXEL_SOFTEN", 4);

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

    public static AdaptiveMesh Build(TerrainGrid grid, TrackMap map)
    {
        var v = Voxel;
        var (room, roomFloor, nearest, road) = StitchedTerrain.Rooms(grid, map);
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
        const float Coarse = 8;
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
        float Field(Vector3 p)
        {
            var f = p.Y - LandAt(p.X, p.Z);
            if (f < 0 && Away(p.X, p.Z) < 2 * Coarse)
            {
                var cut = -room(p);
                // A soft cut over the road: where the land's surface and a room's wall come together (a tunnel's
                // mouth, a cutting's rim, a thin roof over a shallow tunnel) the edge between them is rounded off, and
                // land thinner than half of Soften goes altogether; as a hard cut it stood there in teeth and shards.
                // Beside the road, at the height of its edges and under it, the cut stays hard: the land meets them
                // exactly. Straight over the road it is soft from the edges' height on: a road coming out of the land
                // comes out where its cover gets thin, not through a wedge of land thinning to nothing along it.
                var (edge, over, across) = road(p);
                var k = Soften * (edge < 100 ? MathF.Max(Ramp(0.5f, 3, over), (1 - Ramp(0.8f, 1, across)) * Ramp(0, 0.75f, over)) : 1);
                if (k > 0.01f)
                {
                    var h = MathF.Max(k - MathF.Abs(f - cut), 0) / k;
                    f = MathF.Max(f, cut) + h * h * k / 4;
                }
                else f = MathF.Max(f, cut);
            }
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
            for (var d = Near; distance > d && size * 2 * v <= Coarsest; d *= 2) size *= 2;
            return size;
        }

        void Split(Node n)
        {
            if (n.Size == 1) return;
            var half = n.Size / 2;
            var c = World(n.X + half, n.Y + half, n.Z + half);
            var reach = n.Size * v * 0.866f;
            // How far the cell is from the roads' edges at least (that is where the land has to meet the road; a vault's
            // top and the floor under the road's middle are further): exactly within a square of them, else by the squares.
            var from = MathF.Max(0, Away(c.X, c.Z) - reach - Coarse);
            var above = 0f;
            if (from <= 0)
            {
                var (edge, up, _) = road(c);
                from = MathF.Max(0, MathF.Min(edge, 2 * Coarse) - reach);
                above = up;
            }
            // Split only where the surface may pass: the land's, outside the rooms, or a room's wall under the land.
            var over = c.Y - LandAt(c.X, c.Z);
            var span = n.Size * v * (0.5f + 0.71f * slope);
            var landNear = MathF.Abs(over) <= span;
            var wall = false;
            var inRoom = false;
            if (Away(c.X, c.Z) - reach < 2 * Coarse && over < span)
            {
                var d = room(c);
                wall = MathF.Abs(d) <= reach;
                inRoom = d < -reach;
            }
            // A rim (the land's surface and a room's wall in one cell, over the road: a tunnel's mouth, a cutting's top,
            // a thin roof) gets the smallest cells wherever it is: one point per cell cannot stand for two surfaces,
            // and larger cells stood there in teeth.
            var rim = landNear && wall && !inRoom && above + reach > 1;
            if (!rim && n.Size <= Wanted(from)) return;
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
            mesh.Positions.Add(new Vector3((float)(grid.X0 + (n.X + p.X * n.Size) * v), bottom + (n.Y + p.Y * n.Size) * v, (float)(grid.Z0 + (n.Z + p.Z * n.Size) * v)));
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
        return kept;
    }
}
