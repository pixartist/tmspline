namespace TmSpline.Core.Geometry;

/// <summary>
/// Hashing for edge keys packed as <c>(a &lt;&lt; 32) | b</c>. A long's own hash is <c>low ^ high</c>, here
/// <c>a ^ b</c>: a mesh's neighbouring vertices have similar indices, so those collide by the thousand and a dictionary of
/// edges turns into long lists. This one mixes all the bits (a 64-bit finaliser, after MurmurHash3).
/// </summary>
public sealed class EdgeKeyComparer : IEqualityComparer<long>
{
    public static readonly EdgeKeyComparer Instance = new();

    public bool Equals(long x, long y) => x == y;

    public int GetHashCode(long key)
    {
        var h = (ulong)key;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return (int)h;
    }
}
