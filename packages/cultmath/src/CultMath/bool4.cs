using System;

namespace CultMath;

public struct bool4 : IEquatable<bool4>
{
    public static readonly bool4 @false = new(false, false, false, false);
    public static readonly bool4 @true = new(true, true, true, true);

    public bool x;
    public bool y;
    public bool z;
    public bool w;

    public bool4(bool x, bool y, bool z, bool w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public bool this[int index]
    {
        readonly get => index switch
        {
            0 => x,
            1 => y,
            2 => z,
            3 => w,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        set
        {
            switch (index)
            {
                case 0: x = value; break;
                case 1: y = value; break;
                case 2: z = value; break;
                case 3: w = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public static implicit operator bool4(bool value) => new(value, value, value, value);

    public static bool4 operator !(bool4 value) => new(!value.x, !value.y, !value.z, !value.w);
    public static bool4 operator &(bool4 left, bool4 right) => new(left.x & right.x, left.y & right.y, left.z & right.z, left.w & right.w);
    public static bool4 operator |(bool4 left, bool4 right) => new(left.x | right.x, left.y | right.y, left.z | right.z, left.w | right.w);
    public static bool4 operator ^(bool4 left, bool4 right) => new(left.x ^ right.x, left.y ^ right.y, left.z ^ right.z, left.w ^ right.w);
    public static bool4 operator ==(bool4 left, bool4 right) => new(left.x == right.x, left.y == right.y, left.z == right.z, left.w == right.w);
    public static bool4 operator !=(bool4 left, bool4 right) => new(left.x != right.x, left.y != right.y, left.z != right.z, left.w != right.w);

    public readonly bool Equals(bool4 other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public override readonly bool Equals(object? obj) => obj is bool4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override readonly string ToString() => $"bool4({x}, {y}, {z}, {w})";
}
