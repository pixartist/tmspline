using System.Numerics;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// The game's curve signs (RoadSign, RoadSignB, RoadSignC items) along a segment's borders: every SignSpacing meters on
/// the left, right or both sides, standing on the curb (or at the road's edge without borders), upright to the road and
/// facing the oncoming cars. Signs on the right are turned round, so the chevrons on both sides point into the road (the
/// panels are seen from behind there; to be checked in game).
/// </summary>
public static class SignBuilder
{
    /// <summary>One sign: where it stands (its base), which way its front faces, up, and the item name.</summary>
    public readonly record struct Sign(Vec3d Position, Vec3d Facing, Vec3d Up, string Name, ItemColor Color);

    public static List<Sign> Signs(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, RoadMeshSettings settings)
    {
        var signs = new List<Sign>();
        if (rings.Count < 2) return signs;
        var gaps = TrackObjects.GapRanges(track, segmentStarts);
        for (var s = 0; s < track.SegmentCount; s++)
        {
            var seg = track.Segments[s];
            if (seg.Signs == RoadSignKind.None) continue;
            var (start, end) = (segmentStarts[s], segmentStarts[s + 1]);
            var spacing = Math.Max(2, seg.SignSpacing);
            var sides = seg.SignSide switch { SignSide.Left => new[] { -1 }, SignSide.Right => new[] { 1 }, _ => new[] { -1, 1 } };
            for (var d = start + spacing / 2; d < end; d += spacing)
            {
                if (gaps.Any(g => d > g.Start && d < g.End)) continue;
                var ring = TrackObjects.RingAt(rings, d);
                foreach (var side in sides)
                {
                    // On top of the curb, in its middle (it reaches from its inset inside the width to its thickness
                    // outside); without borders at the road's edge.
                    var scale = Math.Clamp(ring.BorderScale, 0, 1);
                    var across = side * (ring.Width / 2 + (seg.Borders ? (settings.BorderThickness - settings.BorderInset) * scale / 2 : 0));
                    var lift = seg.Borders ? ring.BorderHeight * scale : 0;
                    var (point, up) = TrackObjects.SurfacePoint(ring, across, lift);
                    var forward = (ring.Tangent - up * Vec3d.Dot(ring.Tangent, up)).Normalized();
                    // Front towards the cars coming along the road; turned round on the right side.
                    var facing = side < 0 ? -forward : forward;
                    signs.Add(new Sign(point, facing, up, seg.Signs.ToString(), seg.SignColor));
                }
            }
        }
        return signs;
    }

    /// <summary>
    /// The signs as the game's items. The sign model's front is not its +Z: with +Z on the facing it stood turned 90°
    /// clockwise seen from above (checked in game by the user), so +Z goes 90° counter-clockwise of it
    /// (up × facing; seen from above +Z is up and +X left).
    /// </summary>
    public static IEnumerable<VanillaItem> GameItems(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, RoadMeshSettings settings) =>
        Signs(track, rings, segmentStarts, settings).Select(sign =>
        {
            var modelZ = Vec3d.Cross(sign.Up, sign.Facing).Normalized();
            var (yaw, pitch, roll) = PillarBuilder.UprightAngles(modelZ, sign.Up);
            return new VanillaItem { Name = sign.Name, Position = sign.Position, Yaw = yaw, Pitch = pitch, Roll = roll, Color = sign.Color };
        });

    /// <summary>Stand-ins for the editor: a post with a panel (2 m wide, 1.5 m tall) facing like the sign.</summary>
    public static MeshChunk? Preview(Track track, IReadOnlyList<Ring> rings, double[] segmentStarts, RoadMeshSettings settings)
    {
        var signs = Signs(track, rings, segmentStarts, settings);
        if (signs.Count == 0) return null;
        var chunk = new MeshChunk { Name = "Signs" };
        var material = chunk.MaterialIndex(Surfaces.Pillar);
        foreach (var sign in signs)
        {
            var right = Vec3d.Cross(sign.Up, sign.Facing).Normalized();
            // Panel 0.8..2.3 m up, post below it.
            Box(chunk, material, sign.Position + sign.Up * 1.55, right * 1.0, sign.Up * 0.75, sign.Facing * 0.08);
            Box(chunk, material, sign.Position + sign.Up * 0.4, right * 0.08, sign.Up * 0.4, sign.Facing * 0.08);
        }
        return chunk;
    }

    /// <summary>A box around `center` with half extents along three (orthogonal) axes.</summary>
    private static void Box(MeshChunk chunk, int material, Vec3d center, Vec3d ax, Vec3d ay, Vec3d az)
    {
        Vector3 F(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        foreach (var (n, u, v) in new[] { (az, ax, ay), (-az, -ax, ay), (ax, -az, ay), (-ax, az, ay), (ay, ax, -az), (-ay, ax, az) })
        {
            var normal = F(n.Normalized());
            var first = chunk.Positions.Count;
            foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                chunk.Positions.Add(F(center + n + u * a + v * b));
                chunk.Normals.Add(normal);
                chunk.Uv0.Add(new Vector2((a + 1) / 2f, (b + 1) / 2f));
                chunk.Uv1.Add(new Vector2(0.5f, 0.5f));
                chunk.Surface.Add(MeshChunk.NoSurface);
            }
            MeshChunk.AddFacing(chunk, new Triangle(first, first + 1, first + 2, material), normal);
            MeshChunk.AddFacing(chunk, new Triangle(first, first + 2, first + 3, material), normal);
        }
    }
}
