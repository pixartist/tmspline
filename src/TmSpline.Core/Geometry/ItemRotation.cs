namespace TmSpline.Core.Geometry;

/// <summary>
/// How Trackmania turns an anchored item's stored <c>PitchYawRoll</c> into a rotation. Not documented anywhere we
/// could find, so it is parameterized and determined with a calibration map (tools/RotationCalibration):
/// <list type="bullet">
/// <item><see cref="YawOuter"/>: rotation = Ry(yaw)·Rx(pitch) if true, Rx(pitch)·Ry(yaw) if false (roll is always 0 here);</item>
/// <item><see cref="YawSign"/>/<see cref="PitchSign"/>: +1 if a positive stored angle is a right-handed rotation;</item>
/// <item><see cref="SwapSlots"/>: whether the stored vector is (yaw, pitch, roll) instead of (pitch, yaw, roll).</item>
/// </list>
/// </summary>
public readonly record struct RotationConvention(bool YawOuter, int YawSign, int PitchSign, bool SwapSlots)
{
    /// <summary>
    /// The convention used for export, verified in game: the rotation probe map (tools/RotationProbe) showed the stored
    /// vector is (yaw about Y, pitch about X, roll about Z), each right-handed, and copy 5 of the calibration map
    /// (Ry·Rx, yaw applied outermost) was the seamless one.
    /// </summary>
    public static RotationConvention Default => All[4];

    /// <summary>All 16 candidates, in calibration-map order (copy 1 = index 0).</summary>
    public static IReadOnlyList<RotationConvention> All { get; } =
        (from yawOuter in new[] { true, false }
         from swap in new[] { false, true }
         from yawSign in new[] { 1, -1 }
         from pitchSign in new[] { 1, -1 }
         select new RotationConvention(yawOuter, yawSign, pitchSign, swap)).ToList();

    public override string ToString() =>
        $"{(YawOuter ? "Ry·Rx" : "Rx·Ry")}, yaw {(YawSign > 0 ? "+" : "-")}, pitch {(PitchSign > 0 ? "+" : "-")}, {(SwapSlots ? "(yaw,pitch,roll)" : "(pitch,yaw,roll)")}";
}

/// <summary>An item's orientation: its local axes in track space, and the angles to store in the map.</summary>
public readonly record struct ItemFrame(Vec3d X, Vec3d Y, Vec3d Z, Vec3d StoredPitchYawRoll)
{
    public static readonly ItemFrame Identity = new(Vec3d.UnitX, Vec3d.UnitY, Vec3d.UnitZ, Vec3d.Zero);

    public bool IsIdentity => StoredPitchYawRoll == Vec3d.Zero;

    /// <summary>Track-space vector to item-local coordinates.</summary>
    public Vec3d ToLocal(Vec3d v) => new(Vec3d.Dot(v, X), Vec3d.Dot(v, Y), Vec3d.Dot(v, Z));

    /// <summary>
    /// The frame whose local +Z points along <paramref name="forward"/> (the direction oriented gameplay effects
    /// such as Turbo push), with no roll, expressed for the given convention.
    /// </summary>
    public static ItemFrame Facing(Vec3d forward, RotationConvention c)
    {
        var f = forward.Normalized();
        double yaw, pitch;
        if (c.YawOuter)
        {
            // Ry(yaw)·Rx(pitch)·(0,0,1) = (cos p sin y, -sin p, cos p cos y)
            yaw = Math.Atan2(f.X, f.Z);
            pitch = -Math.Asin(Math.Clamp(f.Y, -1, 1));
        }
        else
        {
            // Rx(pitch)·Ry(yaw)·(0,0,1) = (sin y, -sin p cos y, cos p cos y)
            yaw = Math.Asin(Math.Clamp(f.X, -1, 1));
            pitch = Math.Atan2(-f.Y, f.Z);
        }

        var m = c.YawOuter ? Mul(Ry(yaw), Rx(pitch)) : Mul(Rx(pitch), Ry(yaw));
        var x = new Vec3d(m[0, 0], m[1, 0], m[2, 0]);
        var y = new Vec3d(m[0, 1], m[1, 1], m[2, 1]);
        var z = new Vec3d(m[0, 2], m[1, 2], m[2, 2]);

        var storedYaw = c.YawSign * yaw;
        var storedPitch = c.PitchSign * pitch;
        var stored = c.SwapSlots ? new Vec3d(storedYaw, storedPitch, 0) : new Vec3d(storedPitch, storedYaw, 0);
        return new ItemFrame(x, y, z, stored);
    }

    private static double[,] Rx(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
    }

    private static double[,] Ry(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        for (var k = 0; k < 3; k++)
            r[i, j] += a[i, k] * b[k, j];
        return r;
    }
}
