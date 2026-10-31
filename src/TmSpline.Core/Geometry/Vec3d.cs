namespace TmSpline.Core.Geometry;

/// <summary>Double-precision vector. All track math runs in doubles; we convert to float exactly once
/// per vertex so that vertices shared between chunks end up bit-identical.</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);
    public static readonly Vec3d UnitX = new(1, 0, 0);
    public static readonly Vec3d UnitY = new(0, 1, 0);
    public static readonly Vec3d UnitZ = new(0, 0, 1);

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => a * s;
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3d Normalized()
    {
        var len = Length;
        return len < 1e-12 ? Zero : this / len;
    }

    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3d Cross(Vec3d a, Vec3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    /// <summary>Rotates <paramref name="v"/> around unit axis <paramref name="axis"/> by <paramref name="angle"/> radians (Rodrigues).</summary>
    public static Vec3d Rotate(Vec3d v, Vec3d axis, double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return v * c + Cross(axis, v) * s + axis * (Dot(axis, v) * (1 - c));
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
