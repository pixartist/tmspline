using TmSpline.Core.Geometry;

namespace TmSpline.Core.Model;

public enum Surface
{
    RoadTech,
    RoadDirt,
    RoadBump,
    RoadIce,
    PlatformTech,
    PlatformDirt,
    PlatformGrass,
    PlatformIce,
    PlatformPlastic,
    Wood,
    Snow,
    Sand,
    Grass,
    Magnet,
    MagnetAccel,
    SuperMagnet,
}

/// <summary>What the road borders (curbs) are made of.</summary>
public enum BorderMaterial
{
    /// <summary>Native track borders: rubber physics, slows the car on contact.</summary>
    Rubber,
    /// <summary>Plastic physics: can be grinded without losing speed, and the car bounces off.</summary>
    Plastic,
}

/// <summary>Gameplay effect of a segment's driving surface (NadeoImporter GameplayId).</summary>
/// <remarks>Names are the NadeoImporter GameplayId values.</remarks>
public enum Effect
{
    None,
    /// <summary>Yellow boost, pushes along its direction.</summary>
    Turbo,
    /// <summary>Red boost, pushes along its direction.</summary>
    Turbo2,
    /// <summary>Reactor flames pushing along its direction (up from the road by default) for a while.</summary>
    ReactorBoost,
    /// <summary>Stronger reactor boost.</summary>
    ReactorBoost2,
    SlowMotion,
    NoSteering,
    NoBrakes,
    /// <summary>Engine off (shown as "No engine").</summary>
    FreeWheeling,
    Cruise,
    Fragile,
    NoGrip,
    ForceAcceleration,
    /// <summary>Cancels all running effects.</summary>
    Reset,
    Bumper,
    Bumper2,
}

/// <summary>The game's color palette for blocks and items (the placed item's color, as in the map editor).</summary>
public enum ItemColor
{
    Default,
    White,
    Green,
    Blue,
    Red,
    Black,
}

/// <summary>Race waypoint placed at a spline point.</summary>
public enum Waypoint
{
    None,
    Start,
    Checkpoint,
    Finish,
    /// <summary>Start and finish of a multilap (closed) track.</summary>
    StartFinish,
}

/// <summary>A spline vertex. Handles are offsets relative to <see cref="Position"/>.</summary>
public sealed class TrackPoint
{
    public Vec3d Position { get; set; }
    public Vec3d HandleIn { get; set; }
    public Vec3d HandleOut { get; set; }

    /// <summary>Banking angle in degrees. Positive = right side of the road goes down.</summary>
    public double BankDegrees { get; set; }

    /// <summary>Road width at this point in meters (measured along the surface); blended smoothly towards the neighbouring points.</summary>
    public double Width { get; set; } = 16;

    /// <summary>
    /// Cross-section bend in degrees: 0 = flat, negative = convex (sausage, down to -180), positive = concave
    /// (90 = gutter, 180 = half-pipe, 360 = closed pipe). Blended smoothly towards the neighbouring points.
    /// </summary>
    public double Curve { get; set; }

    /// <summary>
    /// Share of the width (0..0.9) that stays flat in the middle; only the sides bend (by Curve/2 each).
    /// With Curve = 180 this makes a U profile. Blended smoothly towards the neighbouring points.
    /// </summary>
    public double Flat { get; set; }

    /// <summary>
    /// "Sausageness" (0..1): compresses the bend of each side towards its inner end, with the rest straight at the
    /// final angle. 0 = even round bend, 1 = triangle (straight slopes meeting in a sharp crown), in between a rounded
    /// triangle like Trackmania's sausage roads.
    /// </summary>
    public double Sausage { get; set; }

    /// <summary>Height of the road borders (curbs) at this point in meters; blended smoothly towards the neighbouring points.</summary>
    public double BorderHeight { get; set; } = DefaultBorderHeight;

    /// <summary>Race gate at this point (start, checkpoint, finish).</summary>
    public Waypoint Waypoint { get; set; }

    public const double DefaultBorderHeight = 0.8;

    public TrackPoint(Vec3d position, Vec3d handleOut, double bankDegrees = 0, double width = 16)
    {
        Position = position;
        HandleOut = handleOut;
        HandleIn = -handleOut;
        BankDegrees = bankDegrees;
        Width = width;
    }
}

/// <summary>Settings for the segment that starts at the point with the same index.</summary>
public sealed class TrackSegment
{
    public Surface Surface { get; set; } = Surface.RoadTech;
    public bool Borders { get; set; } = true;
    public BorderMaterial BorderMaterial { get; set; } = BorderMaterial.Rubber;
    /// <summary>
    /// Whether the road slab is closed below (underside and side walls). Off saves about half the triangles where the
    /// road is never seen from below.
    /// </summary>
    public bool Underside { get; set; } = true;
    /// <summary>
    /// The game's item color for this segment's pieces (works on our items, checked in game with tools/ColorLook).
    /// Default keeps each material's own look, e.g. the yellow plastic.
    /// </summary>
    public ItemColor Color { get; set; }
    public Effect Effect { get; set; } = Effect.None;
    /// <summary>Boost direction relative to the road, around the road normal, in degrees (0 = forward, 90 = right).</summary>
    public double EffectYaw { get; set; }
    /// <summary>Boost direction tilt out of the road plane, in degrees (positive = up from the road surface).</summary>
    public double EffectPitch { get; set; }

    /// <summary>Effects that push along the item's local Z axis, so their pieces are aimed (see RoadMeshBuilder).</summary>
    public bool IsDirectional => Effect is Effect.Turbo or Effect.Turbo2 or Effect.ReactorBoost or Effect.ReactorBoost2;
}

/// <summary>Kinds of things the mapper places on the track.</summary>
public enum TrackObjectKind
{
    /// <summary>An obstacle standing on the driving surface.</summary>
    Pillar,
    /// <summary>A cut through the road slab (driving surface and underside), with walls round its edge.</summary>
    Hole,
    /// <summary>The whole road left out over a stretch, with closed ends (a jump).</summary>
    Gap,
}

public enum ObjectShape
{
    Round,
    Rect,
}

/// <summary>
/// Something placed on the track. Anchored to a segment (arc-length fraction <see cref="At"/> of its center), so edits
/// elsewhere on the track do not move it.
/// </summary>
public sealed class TrackObject
{
    public TrackObjectKind Kind { get; set; }
    public int Segment { get; set; }
    /// <summary>Arc-length fraction (0..1) of the object's center within its segment.</summary>
    public double At { get; set; }
    /// <summary>Center across the driving surface in meters, measured along the surface (positive = right). Not used by gaps.</summary>
    public double Offset { get; set; }
    /// <summary>Outline of holes and pillars (gaps always span the whole road).</summary>
    public ObjectShape Shape { get; set; }
    /// <summary>Size along the road in meters.</summary>
    public double Length { get; set; } = 4;
    /// <summary>Size across the road in meters (holes, pillars).</summary>
    public double Width { get; set; } = 4;
    /// <summary>Pillars: how far they stand up from the road, in meters.</summary>
    public double Height { get; set; } = 4;
}

/// <summary>
/// A map: one or more tracks (separate splines, e.g. pieces of one route that the car jumps between). Start, checkpoints
/// and finish are only built where the mapper placed them (they can also be added in the game's editor later).
/// </summary>
public sealed class TrackMap
{
    public string Name { get; set; } = "Untitled";
    public List<Track> Tracks { get; } = new();
    /// <summary>Number of laps when the map has a start/finish (multilap).</summary>
    public int Laps { get; set; } = 3;

    /// <summary>Vanilla game blocks placed on the map grid (written into the map by name, not as custom items).</summary>
    public List<VanillaBlock> Blocks { get; } = new();

    public static TrackMap Single(Track track, string name = "Untitled") => new() { Name = name, Tracks = { track } };
}

/// <summary>
/// A vanilla block on the map grid. X/Z count 32 m cells from the map center (cell 0 spans 0..32 m), Y counts 8 m
/// levels above the ground (0 = on the ground, which uses the block's ground variant). Dir is the game's block
/// direction (0 North, 1 East, 2 South, 3 West).
/// </summary>
public sealed class VanillaBlock
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Dir { get; set; }

    /// <summary>
    /// Free block (the game's free block mode): placed at <see cref="Position"/> (meters, track coordinates, the block's
    /// own origin corner) turned by yaw/pitch/roll in degrees, instead of the grid cell and direction.
    /// </summary>
    public bool Free { get; set; }
    public Vec3d Position { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public double Roll { get; set; }

    /// <summary>A pillar block (column piece under an air block), placed with the game's pillar flag.</summary>
    public bool Pillar { get; set; }

    /// <summary>The block's colour (Default: the game's own look, e.g. wooden road columns).</summary>
    public ItemColor Color { get; set; }
}

public sealed class Track
{
    public List<TrackPoint> Points { get; } = new();

    /// <summary>Segment i connects point i to point i+1 (or the last point to point 0 when closed).</summary>
    public List<TrackSegment> Segments { get; } = new();

    public List<TrackObject> Objects { get; } = new();

    public bool Closed { get; set; }

    public int SegmentCount => Closed ? Points.Count : Math.Max(0, Points.Count - 1);

    public TrackPoint SegmentStart(int segment) => Points[segment];
    public TrackPoint SegmentEnd(int segment) => Points[(segment + 1) % Points.Count];

    /// <summary>Makes sure there is exactly one <see cref="TrackSegment"/> per spline segment.</summary>
    public void SyncSegments()
    {
        while (Segments.Count < SegmentCount)
            Segments.Add(Segments.Count > 0 ? Clone(Segments[^1]) : new TrackSegment());
        while (Segments.Count > SegmentCount)
            Segments.RemoveAt(Segments.Count - 1);
        Objects.RemoveAll(o => o.Segment < 0 || o.Segment >= SegmentCount);
    }

    private static TrackSegment Clone(TrackSegment s) => new()
    {
        Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
        Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
    };
}
