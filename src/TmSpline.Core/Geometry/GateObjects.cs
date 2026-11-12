using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Gate objects (TrackObjectKind.Gate): the game's own gate blocks, placed freely round the road where the object
/// sits. The effect or the other car comes from the block itself (a property of the game's block with its trigger);
/// an item of ours has no such property, so the gates cannot be meshes of our own.
/// </summary>
public static class GateObjects
{
    /// <summary>The game's block for each gate (the keys are what the files keep; the editor's GATES mirrors them).</summary>
    public static readonly IReadOnlyDictionary<string, string> Blocks = new Dictionary<string, string>
    {
        ["Turbo"] = "GateSpecialTurbo", ["Turbo2"] = "GateSpecialTurbo2", ["TurboRoulette"] = "GateSpecialTurboRoulette",
        ["ReactorBoost"] = "GateSpecialBoost", ["ReactorBoost2"] = "GateSpecialBoost2",
        ["ReactorBoostOriented"] = "GateSpecialBoostOriented", ["ReactorBoost2Oriented"] = "GateSpecialBoost2Oriented",
        ["SlowMotion"] = "GateSpecialSlowMotion", ["NoSteering"] = "GateSpecialNoSteering", ["NoBrakes"] = "GateSpecialNoBrake",
        ["FreeWheeling"] = "GateSpecialNoEngine", ["Cruise"] = "GateSpecialCruise", ["Fragile"] = "GateSpecialFragile",
        ["Reset"] = "GateSpecialReset",
        ["CarStadium"] = "GateGameplayStadium", ["CarSnow"] = "GateGameplaySnow", ["CarRally"] = "GateGameplayRally",
        ["CarDesert"] = "GateGameplayDesert",
    };

    /// <summary>
    /// A gate block is a ring 32 m across standing in the middle of its block (16 m from its sides), its opening
    /// 26.2 m, from 2.9 m to 29.1 m up (measured on the game's meshes). The road runs through it at the object's
    /// height in the ring: on its lower rim, where only a narrow road fits, up to its middle, where 26 m fit.
    /// </summary>
    private const double Middle = 16;

    /// <summary>
    /// The track's gates as free blocks: upright to the surface in the road's middle, facing along the road; the block
    /// turned by (yaw, pitch, roll) as free blocks are (Ry·Rx·Rz, the same as PillarBuilder.UprightAngles gives).
    /// </summary>
    public static IEnumerable<VanillaBlock> GameBlocks(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        foreach (var o in track.Objects.Where(o => o.Kind == TrackObjectKind.Gate && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1))
        {
            if (!Blocks.TryGetValue(o.Gate, out var block)) continue;
            var ring = TrackObjects.RingAt(rings, TrackObjects.CenterDistance(o, segmentStarts));
            var (point, up) = TrackObjects.SurfacePoint(ring, 0);
            var forward = (ring.Tangent - up * Vec3d.Dot(ring.Tangent, up)).Normalized();
            var right = Vec3d.Cross(up, forward);
            var (yaw, pitch, roll) = PillarBuilder.UprightAngles(forward, up);
            yield return new VanillaBlock
            {
                Name = block, Free = true, Position = point - right * Middle - up * Math.Clamp(o.Height, 0, 32) - forward * Middle,
                Yaw = yaw, Pitch = pitch, Roll = roll,
            };
        }
    }
}
