namespace TmSpline.Core.Model;

/// <summary>The terrain's look and surface: the game's hill materials (as on the DecoHill blocks).</summary>
public enum TerrainSurface { Grass, Dirt, Ice }

/// <summary>
/// Own terrain round the tracks: heights on a square grid of vertices (editor coordinates, metres), exported as custom
/// items. Vertex (c, r) stands at x = X0 + c Cell, z = Z0 + r Cell; Heights holds rows one after another, NaN where there
/// is no terrain. A grid square becomes land where its four corners have heights; land at its outer edge gets a skirt
/// down by Skirt metres, so the floating band does not show as a sheet.
/// </summary>
public sealed class TerrainGrid
{
    public TerrainSurface Surface { get; set; }
    public double Cell { get; set; } = 16;
    public double X0 { get; set; }
    public double Z0 { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public double[] Heights { get; set; } = [];
    public double Skirt { get; set; } = 16;
    /// <summary>Land steeper than this (degrees) is rock (Surfaces.TerrainRock), as are the skirts; 90: never.</summary>
    public double RockAngle { get; set; } = 90;
    /// <summary>Experimental: the mesh from a fine height map with pins and fidelity (AdaptiveTerrainBuilder).</summary>
    public bool Adaptive { get; set; }

    public double Height(int c, int r) => c < 0 || r < 0 || c >= Columns || r >= Rows ? double.NaN : Heights[r * Columns + c];

    /// <summary>Whether grid square (c, r) (corners c..c+1, r..r+1) is land.</summary>
    public bool IsLand(int c, int r) =>
        !double.IsNaN(Height(c, r)) && !double.IsNaN(Height(c + 1, r)) && !double.IsNaN(Height(c, r + 1)) && !double.IsNaN(Height(c + 1, r + 1));
}
