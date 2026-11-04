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
    public double Width { get; set; } = DefaultWidth;

    /// <summary>
    /// Cross-section bend in degrees: 0 = flat, negative = convex (sausage; -360 = a closed tube driven on its outside), positive = concave
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

    /// <summary>
    /// "Keep edges" (0..1), blended between points: 0 = the bent cross-section keeps its middle on the spline (Width along
    /// the surface; pipes); 1 = it keeps its edges at the point's height and exactly Width apart, like the game's own
    /// pieces, with upright curbs. Above 0 the bend is limited to ±180° (<see cref="EffectiveCurve"/>).
    /// </summary>
    public double EdgeAnchor { get; set; }

    /// <summary>The bend that is built: limited to ±180° where the point keeps its edges (no pipes there).</summary>
    public double EffectiveCurve => EdgeAnchor > 0 ? Math.Clamp(Curve, -180, 180) : Curve;

    /// <summary>Height of the road borders (curbs) at this point in meters; blended smoothly towards the neighbouring points.</summary>
    public double BorderHeight { get; set; } = DefaultBorderHeight;

    /// <summary>
    /// Road borders (curbs) at this point: they blend from point to point (full here, gone at a point without, see
    /// TrackSampler.PointBorderScale), so a segment has them when either of its points does. Null on every point: the
    /// segments' own Borders settings (older tracks and code that sets them directly).
    /// </summary>
    public bool? Borders { get; set; }

    /// <summary>Race gate at this point (start, checkpoint, finish).</summary>
    public Waypoint Waypoint { get; set; }

    /// <summary>How high the gate at this point stands over the road, meters (its arch; the trigger reaches as high).</summary>
    public double GateHeight { get; set; } = DefaultGateHeight;
    /// <summary>
    /// Linked checkpoint group (Checkpoint waypoints only): checkpoints with the same number, on any track of the map,
    /// count as one, so passing any of them is enough (the game's LinkedCheckpoint with that order). 0: not linked.
    /// </summary>
    public int CheckpointGroup { get; set; }

    public const double DefaultGateHeight = 6;

    /// <summary>Border height of new points: as the game's road curbs (matched in game by the user).</summary>
    public const double DefaultBorderHeight = 1.0;

    /// <summary>Border height of points in files that stored none (the default before it matched the game's curbs).</summary>
    public const double LegacyBorderHeight = 0.8;

    /// <summary>Width of new roads: where the road texture matches the game's road blocks (user, checked in game).</summary>
    /// <summary>
    /// Road width of new points: the driving surface between the curbs of the game's road blocks (25.61 m; until file
    /// version 3 the width included 0.32 m under each curb, see <see cref="LegacyCurbInset"/>).
    /// </summary>
    public const double DefaultWidth = 25.61;

    /// <summary>
    /// Until file version 3 the curbs stood this far inside the width; such files' points with borders are read with
    /// their width less twice this, so the curbs and the road stay where they were.
    /// </summary>
    public const double LegacyCurbInset = 0.32;

    public TrackPoint(Vec3d position, Vec3d handleOut, double bankDegrees = 0, double width = DefaultWidth)
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

    /// <summary>Bumpiness of the driving surface (real geometry, so cars feel it); see RoadBumps.</summary>
    public BumpPattern Bumps { get; set; }
    /// <summary>Height of the bumps in meters (negative: dips).</summary>
    public double BumpHeight { get; set; } = 0.5;
    /// <summary>Wavelength (waves along, ridges across) or feature size (noise) in meters.</summary>
    public double BumpSize { get; set; } = 8;
    /// <summary>How finely the bumpy surface is cut, in meters between cross-sections and columns (smaller: more triangles).</summary>
    public double BumpDetail { get; set; } = 2;
    /// <summary>Variant of the noise pattern: 0 is the segment's own, any other number gives another one.</summary>
    public int BumpSeed { get; set; }

    /// <summary>The game's curve signs (chevrons) along this segment's borders; see SignBuilder.</summary>
    public RoadSignKind Signs { get; set; }
    public SignSide SignSide { get; set; } = SignSide.Both;
    /// <summary>Meters between signs along the segment.</summary>
    public double SignSpacing { get; set; } = 16;
    /// <summary>The sign items' colour.</summary>
    public ItemColor SignColor { get; set; }
}

/// <summary>The game's road sign items (curve chevrons), by item name.</summary>
public enum RoadSignKind { None, RoadSign, RoadSignB, RoadSignC }

public enum SignSide { Left, Right, Both }

/// <summary>Bump patterns of a segment's driving surface.</summary>
public enum BumpPattern
{
    None,
    /// <summary>Rolling bumps across the full width, one every BumpSize along the road (washboard).</summary>
    Waves,
    /// <summary>Uneven ground: smooth random bumps about BumpSize across.</summary>
    Noise,
    /// <summary>Ridges running along the road, one every BumpSize across it (rumble lanes).</summary>
    Ridges,
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
    /// <summary>A smooth hump (or dip, with a negative height) in the driving surface.</summary>
    Bump,
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
    /// <summary>Pillars: the game item's colour.</summary>
    public ItemColor Color { get; set; }
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

    /// <summary>Without the stadium around the map (the game's "Day (no stadium)" decoration).</summary>
    public bool NoStadium { get; set; }

    /// <summary>Vanilla game blocks placed on the map grid (written into the map by name, not as custom items).</summary>
    public List<VanillaBlock> Blocks { get; } = new();

    /// <summary>Vanilla game items (trees, ...), written into the map by name (the game has their models).</summary>
    public List<VanillaItem> Items { get; } = new();

    /// <summary>Own terrain round the tracks (exported as custom items), or null.</summary>
    public TerrainGrid? Terrain { get; set; }

    public static TrackMap Single(Track track, string name = "Untitled") => new() { Name = name, Tracks = { track } };
}

/// <summary>
/// A vanilla block on the map grid. X/Z count 32 m cells from the map center (cell 0 spans 0..32 m), Y counts 8 m
/// levels above the ground (0 = on the ground, which uses the block's ground variant). Dir is the game's block
/// direction (0 North, 1 East, 2 South, 3 West).
/// </summary>
/// <summary>
/// A vanilla item of the game's Stadium collection (e.g. "SpringTreeTall"), standing at <see cref="Position"/> (meters,
/// track coordinates; the item's base) turned by <see cref="Yaw"/> degrees about the vertical.
/// </summary>
public sealed class VanillaItem
{
    public string Name { get; set; } = "";
    public Vec3d Position { get; set; }
    public double Yaw { get; set; }
    /// <summary>Tilt (degrees) for items standing upright on sloped or banked road: pitch about X, roll about Z.</summary>
    public double Pitch { get; set; }
    public double Roll { get; set; }
    /// <summary>The item's colour (Default: its own look).</summary>
    public ItemColor Color { get; set; }
}

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

    /// <summary>The block's variant as the game numbers them (0: base, 1..: the additional ones).</summary>
    public int Variant { get; set; }

    /// <summary>The variant's sub-variant (e.g. 1 on the hill's outer corner, the one that meets its straights).</summary>
    public int SubVariant { get; set; }

    /// <summary>
    /// Extra block flag bits the game sets on some blocks (only <see cref="AllowedExtraFlags"/>; e.g. 0x400000 on ground hill
    /// slopes in maps it saved, meaning not yet known).
    /// </summary>
    public int ExtraFlags { get; set; }

    public const int AllowedExtraFlags = 0x200000 | 0x400000;

    /// <summary>The block's colour (Default: the game's own look, e.g. wooden road columns).</summary>
    public ItemColor Color { get; set; }
}

/// <summary>
/// Old per-track setting for what a bent cross-section keeps in place; read from files only (Edges = every point at
/// TrackPoint.EdgeAnchor 1).
/// </summary>
public enum ShapeAnchor
{
    /// <summary>The road's middle stays on the spline and the sides bend up or down; Width is measured along the surface.</summary>
    Center,
    /// <summary>
    /// The road's edges stay on the spline's height, Width apart (as the game's own pieces, so bent road lines up with
    /// blocks on the grid); the middle bows down (half-pipe) or up (sausage).
    /// </summary>
    Edges,
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
        // Borders set on the points: a segment has them when either end does.
        if (PointBorders)
            for (var s = 0; s < SegmentCount; s++)
                Segments[s].Borders = SegmentStart(s).Borders == true || SegmentEnd(s).Borders == true;
    }

    /// <summary>Whether the borders are set on the points (see TrackPoint.Borders) rather than on the segments.</summary>
    public bool PointBorders => Points.Any(p => p.Borders is not null);

    private static TrackSegment Clone(TrackSegment s) => new()
    {
        Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
        Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
        Bumps = s.Bumps, BumpHeight = s.BumpHeight, BumpSize = s.BumpSize, BumpDetail = s.BumpDetail, BumpSeed = s.BumpSeed,
        Signs = s.Signs, SignSide = s.SignSide, SignSpacing = s.SignSpacing, SignColor = s.SignColor,
    };
}
