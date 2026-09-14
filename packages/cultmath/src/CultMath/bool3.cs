using System;

namespace CultMath;

public partial struct bool3 : IEquatable<bool3>
{
    public static readonly bool3 @false = new(false, false, false);
    public static readonly bool3 @true = new(true, true, true);

    public bool x;
    public bool y;
    public bool z;

    public bool3(bool x, bool y, bool z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public bool this[int index]
    {
        readonly get => index switch
        {
            0 => x,
            1 => y,
            2 => z,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        set
        {
            switch (index)
            {
                case 0: x = value; break;
                case 1: y = value; break;
                case 2: z = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public static implicit operator bool3(bool value) => new(value, value, value);

    public static bool3 operator !(bool3 value) => new(!value.x, !value.y, !value.z);
    public static bool3 operator &(bool3 left, bool3 right) => new(left.x & right.x, left.y & right.y, left.z & right.z);
    public static bool3 operator |(bool3 left, bool3 right) => new(left.x | right.x, left.y | right.y, left.z | right.z);
    public static bool3 operator ^(bool3 left, bool3 right) => new(left.x ^ right.x, left.y ^ right.y, left.z ^ right.z);
    public static bool3 operator ==(bool3 left, bool3 right) => new(left.x == right.x, left.y == right.y, left.z == right.z);
    public static bool3 operator !=(bool3 left, bool3 right) => new(left.x != right.x, left.y != right.y, left.z != right.z);

    public readonly bool Equals(bool3 other) => x == other.x && y == other.y && z == other.z;
    public override readonly bool Equals(object? obj) => obj is bool3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => $"bool3({x}, {y}, {z})";
}
