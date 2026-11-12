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
    /// <summary>The scenery panel's noise height and size, metres (null: not given). VoxelTerrain's hills.</summary>
    public double? NoiseHeight { get; set; }
    public double? NoiseSize { get; set; }
    /// <summary>
    /// The 3D brush's dabs, five numbers each: a centre (x, y, z), a radius and an amount (metres): the land's surface
    /// moves out by the amount at the centre (in, when negative), fading to nothing at the radius. They add up, in any
    /// order, and work on walls and ceilings as on the ground (VoxelTerrain); the roads still cut through the result.
    /// </summary>
    public double[] Edits { get; set; } = [];
    /// <summary>Dabs like <see cref="Edits"/> that come after the roads' cut: land worked into the road's own space (a bump on it, a rock in a tunnel).</summary>
    public double[] EditsOver { get; set; } = [];
    /// <summary>The smoothing brush's dabs (x, y, z, radius, amount each): the solid is blurred where they lie, the more the more they add up to; the roads cut through the result.</summary>
    public double[] Smooths { get; set; } = [];
    /// <summary>Dabs like <see cref="Smooths"/> that come after the roads' cut (the cut's own edges are smoothed too).</summary>
    public double[] SmoothsOver { get; set; } = [];

    public double Height(int c, int r) => c < 0 || r < 0 || c >= Columns || r >= Rows ? double.NaN : Heights[r * Columns + c];

    /// <summary>Whether grid square (c, r) (corners c..c+1, r..r+1) is land.</summary>
    public bool IsLand(int c, int r) =>
        !double.IsNaN(Height(c, r)) && !double.IsNaN(Height(c + 1, r)) && !double.IsNaN(Height(c, r + 1)) && !double.IsNaN(Height(c + 1, r + 1));
}
