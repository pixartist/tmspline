using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>A land mesh being built (VoxelTerrain, BooleanSeam): positions (x, y, z) and three indices per triangle.</summary>
public sealed class AdaptiveMesh
{
    public List<Vector3> Positions { get; } = [];
    public List<int> Indices { get; } = [];
    /// <summary>Per position: whether it lies on a road's edge (TerrainMeshBuilder puts no skirt between two such).</summary>
    public List<bool> Pinned { get; } = [];
}

/// <summary>Helpers on the editor's height map (TerrainGrid) and on fields over its cells.</summary>
public static class TerrainGrids
{
    /// <summary>The editor grid's height at (x, z), bilinear between its vertices; NaN where a vertex round it has none.</summary>
    internal static double SampleGrid(TerrainGrid grid, double x, double z)
    {
        var fx = (x - grid.X0) / grid.Cell;
        var fz = (z - grid.Z0) / grid.Cell;
        int c = (int)Math.Floor(fx), r = (int)Math.Floor(fz);
        double u = fx - c, v = fz - r;
        return grid.Height(c, r) * (1 - u) * (1 - v) + grid.Height(c + 1, r) * u * (1 - v) + grid.Height(c, r + 1) * (1 - u) * v + grid.Height(c + 1, r + 1) * u * v;
    }

    /// <summary>Per cell the nearest cell with a value (not NaN) and its distance in cells (two-pass Euclidean transform).</summary>
    internal static (int[] Nearest, float[] Distance) NearestTransform(float[] values, int columns, int rows)
    {
        var n = values.Length;
        var src = new int[n];
        for (var i = 0; i < n; i++) src[i] = float.IsNaN(values[i]) ? -1 : i;
        double D(int i, int c, int r) { if (src[i] < 0) return double.PositiveInfinity; int sc = src[i] % columns, sr = src[i] / columns; return (sc - c) * (sc - c) + (sr - r) * (sr - r); }
        void Relax(int c, int r, int dc, int dr)
        {
            int nc = c + dc, nr = r + dr;
            if (nc < 0 || nr < 0 || nc >= columns || nr >= rows) return;
            int i = r * columns + c, j = nr * columns + nc;
            if (src[j] >= 0 && D(j, c, r) < D(i, c, r)) src[i] = src[j];
        }
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++) { Relax(c, r, -1, 0); Relax(c, r, 0, -1); Relax(c, r, -1, -1); Relax(c, r, 1, -1); }
            for (var c = columns - 1; c >= 0; c--) Relax(c, r, 1, 0);
        }
        for (var r = rows - 1; r >= 0; r--)
        {
            for (var c = columns - 1; c >= 0; c--) { Relax(c, r, 1, 0); Relax(c, r, 0, 1); Relax(c, r, 1, 1); Relax(c, r, -1, 1); }
            for (var c = 0; c < columns; c++) Relax(c, r, -1, 0);
        }
        var dist = new float[n];
        for (var i = 0; i < n; i++) dist[i] = (float)Math.Sqrt(D(i, i % columns, i / columns));
        return (src, dist);
    }

    /// <summary>Two box blurs each way, radius in cells.</summary>
    internal static float[] Blur(float[] values, int columns, int rows, int radius)
    {
        var a = values;
        for (var pass = 0; pass < 2; pass++)
            foreach (var alongX in new[] { true, false })
            {
                var o = new float[a.Length];
                int len = alongX ? columns : rows, lines = alongX ? rows : columns;
                for (var line = 0; line < lines; line++)
                {
                    double sum = 0;
                    int count = 0;
                    int Index(int i) => alongX ? line * columns + i : i * columns + line;
                    for (var i = -radius; i < len; i++)
                    {
                        int add = i + radius, drop = i - radius - 1;
                        if (add < len) { sum += a[Index(add)]; count++; }
                        if (drop >= 0) { sum -= a[Index(drop)]; count--; }
                        if (i >= 0) o[Index(i)] = (float)(sum / count);
                    }
                }
                a = o;
            }
        return a;
    }
}
