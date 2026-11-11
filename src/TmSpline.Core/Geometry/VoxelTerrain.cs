using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The own terrain as a solid (prototype, TMSPLINE_TERRAIN=voxel): the editor's land is everything under its height
/// map, and each road cuts its room (StitchedTerrain's: the space a car needs, the driving surface and an arch over
/// it) out of it. The surface of what is left is the mesh: open ground, cuttings, tunnels and their mouths all come
/// from the same subtraction.
/// <list type="bullet">
/// <item>Field: f(p) = max(p.y - land(x, z), -room(p)), negative in the solid; sampled on a lattice of Voxel metres, per
/// column only from under the lowest room near it to just over the land.</item>
/// <item>Mesh: surface nets (one point per lattice cell the surface passes through, at the mean of its edge crossings;
/// one quad per lattice edge that changes sign).</item>
/// </list>
/// Not yet: finer cells along the roads, a triangle budget, sharp edges where the land meets a road.
/// </summary>
public static class VoxelTerrain
{
    /// <summary>The lattice's spacing, metres (TMSPLINE_VOXEL).</summary>
    public static float Voxel { get; set; } =
        float.TryParse(Environment.GetEnvironmentVariable("TMSPLINE_VOXEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1f;

    private sealed class Column
    {
        /// <summary>Lattice index of Values[0]; under it the solid, over the last value air.</summary>
        public int Y0;
        public float[] Values = [];
    }

    public static AdaptiveMesh Build(TerrainGrid grid, TrackMap map)
    {
        var v = Voxel;
        var (room, roomFloor) = StitchedTerrain.Rooms(grid, map);
        int nx = (int)Math.Floor((grid.Columns - 1) * grid.Cell / v) + 1, nz = (int)Math.Floor((grid.Rows - 1) * grid.Cell / v) + 1;
        var columns = new Column?[nx * nz];
        Parallel.For(0, nz, iz =>
        {
            for (var ix = 0; ix < nx; ix++)
            {
                float x = (float)(grid.X0 + ix * v), z = (float)(grid.Z0 + iz * v);
                var land = (float)AdaptiveTerrainBuilder.SampleGrid(grid, x, z);
                if (float.IsNaN(land)) continue;
                var floor = roomFloor(x, z);
                // Rooms wholly over the land cut nothing out of it.
                var rooms = floor < land + v;
                var y0 = (int)Math.Floor((rooms ? MathF.Min(land, floor) : land) / v) - 2;
                var y1 = (int)Math.Ceiling(land / v) + 2;
                var values = new float[y1 - y0 + 1];
                for (var k = 0; k < values.Length; k++)
                {
                    var y = (y0 + k) * v;
                    var f = y - land;
                    if (rooms && f < 0) f = MathF.Max(f, -room(new Vector3(x, y, z)));
                    values[k] = f;
                }
                columns[iz * nx + ix] = new Column { Y0 = y0, Values = values };
            }
        });

        float At(Column c, int iy)
        {
            var k = iy - c.Y0;
            return k < 0 ? -1 : k >= c.Values.Length ? 1 : c.Values[k];
        }

        // One point per cell the surface passes through.
        var mesh = new AdaptiveMesh();
        var pointOf = new Dictionary<(int, int, int), int>();
        var corner = new float[8];
        for (var iz = 0; iz + 1 < nz; iz++)
            for (var ix = 0; ix + 1 < nx; ix++)
            {
                Column? c00 = columns[iz * nx + ix], c10 = columns[iz * nx + ix + 1], c01 = columns[(iz + 1) * nx + ix], c11 = columns[(iz + 1) * nx + ix + 1];
                if (c00 is null || c10 is null || c01 is null || c11 is null) continue;
                var lo = Math.Min(Math.Min(c00.Y0, c10.Y0), Math.Min(c01.Y0, c11.Y0)) - 1;
                var hi = Math.Max(Math.Max(c00.Y0 + c00.Values.Length, c10.Y0 + c10.Values.Length), Math.Max(c01.Y0 + c01.Values.Length, c11.Y0 + c11.Values.Length));
                for (var iy = lo; iy < hi; iy++)
                {
                    // Corners: bit 0 x, bit 1 z, bit 2 y.
                    corner[0] = At(c00, iy); corner[1] = At(c10, iy); corner[2] = At(c01, iy); corner[3] = At(c11, iy);
                    corner[4] = At(c00, iy + 1); corner[5] = At(c10, iy + 1); corner[6] = At(c01, iy + 1); corner[7] = At(c11, iy + 1);
                    var solid = 0;
                    for (var k = 0; k < 8; k++) if (corner[k] < 0) solid++;
                    if (solid == 0 || solid == 8) continue;
                    var sum = Vector3.Zero;
                    var n = 0;
                    void Edge(int a, int b)
                    {
                        float fa = corner[a], fb = corner[b];
                        if ((fa < 0) == (fb < 0)) return;
                        var t = fa / (fa - fb);
                        Vector3 pa = new(a & 1, (a >> 2) & 1, (a >> 1) & 1), pb = new(b & 1, (b >> 2) & 1, (b >> 1) & 1);
                        sum += pa + (pb - pa) * t;
                        n++;
                    }
                    Edge(0, 1); Edge(2, 3); Edge(4, 5); Edge(6, 7);
                    Edge(0, 2); Edge(1, 3); Edge(4, 6); Edge(5, 7);
                    Edge(0, 4); Edge(1, 5); Edge(2, 6); Edge(3, 7);
                    var p = sum / n;
                    pointOf[(ix, iy, iz)] = mesh.Positions.Count;
                    mesh.Positions.Add(new Vector3((float)(grid.X0 + (ix + p.X) * v), (iy + p.Y) * v, (float)(grid.Z0 + (iz + p.Z) * v)));
                    mesh.Pinned.Add(false);
                }
            }

        // One quad per lattice edge that changes sign, between the four cells round it, facing the air.
        void Quad((int, int, int) a, (int, int, int) b, (int, int, int) c, (int, int, int) d, bool flip)
        {
            if (!pointOf.TryGetValue(a, out var ia) || !pointOf.TryGetValue(b, out var ib) || !pointOf.TryGetValue(c, out var ic) || !pointOf.TryGetValue(d, out var id)) return;
            if (flip) (ib, id) = (id, ib);
            mesh.Indices.Add(ia); mesh.Indices.Add(ib); mesh.Indices.Add(ic);
            mesh.Indices.Add(ia); mesh.Indices.Add(ic); mesh.Indices.Add(id);
        }
        for (var iz = 0; iz < nz; iz++)
            for (var ix = 0; ix < nx; ix++)
            {
                var c = columns[iz * nx + ix];
                if (c is null) continue;
                var cx = ix + 1 < nx ? columns[iz * nx + ix + 1] : null;
                var cz = iz + 1 < nz ? columns[(iz + 1) * nx + ix] : null;
                var lo = c.Y0 - 1;
                var hi = c.Y0 + c.Values.Length;
                if (cx is not null) { lo = Math.Min(lo, cx.Y0 - 1); hi = Math.Max(hi, cx.Y0 + cx.Values.Length); }
                if (cz is not null) { lo = Math.Min(lo, cz.Y0 - 1); hi = Math.Max(hi, cz.Y0 + cz.Values.Length); }
                for (var iy = lo; iy <= hi; iy++)
                {
                    var f = At(c, iy);
                    // Along x: round it the cells (y, z); along y: (z, x); along z: (x, y).
                    if (cx is not null && (f < 0) != (At(cx, iy) < 0))
                        Quad((ix, iy - 1, iz - 1), (ix, iy, iz - 1), (ix, iy, iz), (ix, iy - 1, iz), f >= 0);
                    if ((f < 0) != (At(c, iy + 1) < 0))
                        Quad((ix - 1, iy, iz - 1), (ix - 1, iy, iz), (ix, iy, iz), (ix, iy, iz - 1), f >= 0);
                    if (cz is not null && (f < 0) != (At(cz, iy) < 0))
                        Quad((ix - 1, iy - 1, iz), (ix, iy - 1, iz), (ix, iy, iz), (ix - 1, iy, iz), f >= 0);
                }
            }
        return mesh;
    }
}
