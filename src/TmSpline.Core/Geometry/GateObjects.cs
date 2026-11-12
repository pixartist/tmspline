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
    /// The game's flat gate panel for a gate (GateExpandable...: 32 m wide, 8 m high, standing in the middle of its
    /// block, from 0.21 m under the block's floor up), for the gates with a frame of our own. Freely placed it is
    /// the panel alone: the posts the game sets between such blocks on its grid do not come with it.
    /// </summary>
    public static string Panel(string gate) => gate.StartsWith("Car", StringComparison.Ordinal)
        ? "GateExpandableGameplay" + gate[3..]
        : "GateExpandable" + Blocks[gate]["Gate".Length..].Replace("Oriented", "");

    private const double PanelSunk = 0.21;

    private static IEnumerable<TrackObject> Gates(Track track, double[] segmentStarts) =>
        track.Objects.Where(o => o.Kind == TrackObjectKind.Gate && o.Segment >= 0 && o.Segment < segmentStarts.Length - 1 && Blocks.ContainsKey(o.Gate));

    /// <summary>
    /// Where a gate with a frame stands: the middle of its panels' lower edge, with the ring's up and the road's
    /// direction (the frame and the panels lie in the ring's plane), and how many panels it takes across and up.
    /// </summary>
    public static (Vec3d Point, Vec3d Up, Vec3d Forward, WaypointBuilder.EffectFrame Layout) FrameAt(TrackObject o, Ring ring, RoadMeshSettings settings)
    {
        var layout = WaypointBuilder.EffectFrameAt(ring, o.Height, settings);
        return (ring.Position + ring.Right * layout.CenterX + ring.Up * layout.Bottom, ring.Up, ring.Tangent, layout);
    }

    /// <summary>The frames of the track's gates that have one, as items (meshes of our own).</summary>
    public static IEnumerable<MeshChunk> Frames(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, Vec3d origin, RoadMeshSettings settings, string namePrefix)
    {
        var index = 0;
        foreach (var o in Gates(track, segmentStarts))
        {
            index++;
            if (!o.Frame) continue;
            var ring = TrackObjects.RingAt(rings, TrackObjects.CenterDistance(o, segmentStarts));
            yield return WaypointBuilder.BuildEffectFrame(ring, o.Segment, o.Gate, o.Height, origin, settings, $"{namePrefix}_{index:D2}");
        }
    }

    /// <summary>
    /// The track's gates as free blocks: the game's ring upright to the surface in the road's middle, facing along
    /// the road, or (with a frame) its panels side by side and over one another in the frame. Turned by (yaw, pitch,
    /// roll) as free blocks are (Ry·Rx·Rz, the same as PillarBuilder.UprightAngles gives).
    /// </summary>
    public static IEnumerable<VanillaBlock> GameBlocks(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts)
    {
        var settings = new RoadMeshSettings();
        foreach (var o in Gates(track, segmentStarts))
        {
            var block = Blocks[o.Gate];
            var ring = TrackObjects.RingAt(rings, TrackObjects.CenterDistance(o, segmentStarts));
            if (o.Frame)
            {
                var (foot, frameUp, along, layout) = FrameAt(o, ring, settings);
                var ahead = (along - frameUp * Vec3d.Dot(along, frameUp)).Normalized();
                var side = Vec3d.Cross(frameUp, ahead);
                var angles = PillarBuilder.UprightAngles(ahead, frameUp);
                for (var i = 0; i < layout.Across; i++)
                    for (var j = 0; j < layout.Up; j++)
                    {
                        var middle = foot + side * ((i - (layout.Across - 1) / 2.0) * WaypointBuilder.EffectFrame.PanelWidth) + frameUp * (j * WaypointBuilder.EffectFrame.PanelHeight);
                        yield return new VanillaBlock
                        {
                            Name = Panel(o.Gate), Free = true, Position = middle - side * Middle + frameUp * PanelSunk - ahead * Middle,
                            Yaw = angles.Yaw, Pitch = angles.Pitch, Roll = angles.Roll,
                        };
                    }
                continue;
            }
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
