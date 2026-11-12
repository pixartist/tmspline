using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Gate objects (TrackObjectKind.Gate): a gate round the road that gives an effect or another car, where the object
/// sits. Three looks:
/// <list type="bullet">
/// <item>Custom: a gate of our own, fitted to the road like the checkpoint gates (WaypointBuilder.BuildEffectGate).
/// The exported item is made a gate after the import (ItemExporter.MakeEffectGate): its trigger gets the effect.</item>
/// <item>Flat, Ring: the game's own gate blocks, placed freely (they keep their size).</item>
/// </list>
/// </summary>
public static class GateObjects
{
    /// <summary>
    /// The gates by the key the files keep (the editor's GATES mirrors them), each with the game's name for its effect
    /// (a surface's gameplay id, as GBX.NET's CPlugSurface.GameplayId spells it; schadocalex's "Custom gates" tool
    /// uses the same for its eleven) and, where the game has one, its ring block.
    /// </summary>
    private static readonly Dictionary<string, (string Gameplay, string? Block)> All = new()
    {
        ["Turbo"] = ("Turbo", "GateSpecialTurbo"), ["Turbo2"] = ("Turbo2", "GateSpecialTurbo2"),
        ["TurboRoulette"] = ("TurboRoulette", "GateSpecialTurboRoulette"),
        ["ReactorBoost"] = ("ReactorBoost_Legacy", "GateSpecialBoost"), ["ReactorBoost2"] = ("ReactorBoost2_Legacy", "GateSpecialBoost2"),
        ["ReactorBoostOriented"] = ("ReactorBoost_Oriented", "GateSpecialBoostOriented"),
        ["ReactorBoost2Oriented"] = ("ReactorBoost2_Oriented", "GateSpecialBoost2Oriented"),
        ["SlowMotion"] = ("SlowMotion", "GateSpecialSlowMotion"), ["NoSteering"] = ("NoSteering", "GateSpecialNoSteering"),
        ["NoBrakes"] = ("NoBrakes", "GateSpecialNoBrake"), ["FreeWheeling"] = ("FreeWheeling", "GateSpecialNoEngine"),
        ["Cruise"] = ("Cruise", "GateSpecialCruise"), ["Fragile"] = ("Fragile", "GateSpecialFragile"), ["Reset"] = ("Reset", "GateSpecialReset"),
        ["CarStadium"] = ("VehicleTransform_Reset", "GateGameplayStadium"), ["CarSnow"] = ("VehicleTransform_CarSnow", "GateGameplaySnow"),
        ["CarRally"] = ("VehicleTransform_CarRally", "GateGameplayRally"), ["CarDesert"] = ("VehicleTransform_CarDesert", "GateGameplayDesert"),
        // The road effects the game has no gate block for: gates of our own only.
        ["NoGrip"] = ("NoGrip", null), ["ForceAcceleration"] = ("ForceAcceleration", null),
        ["Bumper"] = ("Bumper", null), ["Bumper2"] = ("Bumper2", null),
    };

    public static bool Known(string gate) => All.ContainsKey(gate);

    /// <summary>The game's name for a gate's effect (a surface's gameplay id).</summary>
    public static string Gameplay(string gate) => All[gate].Gameplay;

    /// <summary>The game's ring block for a gate, or null where it has none.</summary>
    public static string? Ring(string gate) => All[gate].Block;

    /// <summary>
    /// The game's flat gate for a gate (GateExpandable...: a frame 32 m wide and 8 m high standing in the middle of
    /// its block, with posts, a beam and a sill; freely placed it comes whole, as seen in game). It cannot be cut,
    /// widened or made higher: a second one beside or over it is another whole gate.
    /// </summary>
    public static string? Flat(string gate) => All[gate].Block is not { } block ? null
        : gate.StartsWith("Car", StringComparison.Ordinal) ? "GateExpandableGameplay" + gate[3..]
        : "GateExpandable" + block["Gate".Length..].Replace("Oriented", "");

    /// <summary>
    /// A gate block is a ring 32 m across standing in the middle of its block (16 m from its sides), its opening
    /// 26.2 m, from 2.9 m to 29.1 m up (measured on the game's meshes). The road runs through it at the object's
    /// height in the ring: on its lower rim, where only a narrow road fits, up to its middle, where 26 m fit.
    /// </summary>
    private const double Middle = 16;

    /// <summary>The flat gate's sill reaches 1 m over its block's floor (measured on the game's mesh): the road runs just over it.</summary>
    private const double FlatSill = 1.05;

    private static IEnumerable<TrackObject> Gates(Track track, double[] segmentStarts) =>
        track.Objects.Where(o => o.Kind == TrackObjectKind.Gate && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1 && Known(o.Gate));

    /// <summary>The look a gate gets: the game's blocks only where the game has one for its effect.</summary>
    public static GateLook LookOf(TrackObject o) => Ring(o.Gate) is null ? GateLook.Custom : o.Look;

    /// <summary>
    /// Where a flat gate stands: over the road's lowest place in the middle of its width, with the ring's up and the
    /// road's direction (it lies in the ring's plane).
    /// </summary>
    public static (Vec3d Point, Vec3d Up, Vec3d Forward) FlatAt(Ring ring)
    {
        var (minX, maxX, minY, _) = RoadMeshBuilder.SurfaceExtents(ring);
        return (ring.Position + ring.Right * ((minX + maxX) / 2) + ring.Up * minY, ring.Up, ring.Tangent);
    }

    /// <summary>The track's gates of our own as items (see WaypointBuilder.BuildEffectGate).</summary>
    public static IEnumerable<MeshChunk> Custom(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, Vec3d origin, RoadMeshSettings settings, string namePrefix)
    {
        var index = 0;
        foreach (var o in Gates(track, segmentStarts))
        {
            index++;
            if (LookOf(o) != GateLook.Custom) continue;
            var ring = TrackObjects.RingAt(rings, TrackObjects.CenterDistance(o, segmentStarts));
            yield return WaypointBuilder.BuildEffectGate(track, ring, o.Segment, o.Gate, o.Height, origin, settings, $"{namePrefix}_{index:D2}");
        }
    }

    /// <summary>
    /// The track's gates that are the game's blocks, freely placed: the flat gate over the road, or the ring upright
    /// to the surface in the road's middle, facing along the road. Turned by (yaw, pitch, roll) as free blocks are
    /// (Ry·Rx·Rz, the same as PillarBuilder.UprightAngles gives).
    /// </summary>
    public static IEnumerable<VanillaBlock> GameBlocks(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        foreach (var o in Gates(track, segmentStarts))
        {
            var look = LookOf(o);
            if (look == GateLook.Custom) continue;
            var ring = TrackObjects.RingAt(rings, TrackObjects.CenterDistance(o, segmentStarts));
            if (look == GateLook.Flat)
            {
                var (foot, frameUp, along) = FlatAt(ring);
                var ahead = (along - frameUp * Vec3d.Dot(along, frameUp)).Normalized();
                var side = Vec3d.Cross(frameUp, ahead);
                var angles = PillarBuilder.UprightAngles(ahead, frameUp);
                yield return new VanillaBlock
                {
                    Name = Flat(o.Gate)!, Free = true, Position = foot - side * Middle - frameUp * FlatSill - ahead * Middle,
                    Yaw = angles.Yaw, Pitch = angles.Pitch, Roll = angles.Roll,
                };
                continue;
            }
            var (point, up) = TrackObjects.SurfacePoint(ring, 0);
            var forward = (ring.Tangent - up * Vec3d.Dot(ring.Tangent, up)).Normalized();
            var right = Vec3d.Cross(up, forward);
            var (yaw, pitch, roll) = PillarBuilder.UprightAngles(forward, up);
            yield return new VanillaBlock
            {
                Name = Ring(o.Gate)!, Free = true, Position = point - right * Middle - up * Math.Clamp(o.Height, 0, 32) - forward * Middle,
                Yaw = yaw, Pitch = pitch, Roll = roll,
            };
        }
    }
}
