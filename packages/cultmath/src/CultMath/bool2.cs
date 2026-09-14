using System;

namespace CultMath;

public partial struct bool2 : IEquatable<bool2>
{
    public static readonly bool2 @false = new(false, false);
    public static readonly bool2 @true = new(true, true);

    public bool x;
    public bool y;

    public bool2(bool x, bool y)
    {
        this.x = x;
        this.y = y;
    }

    public bool this[int index]
    {
        readonly get => index switch
        {
            0 => x,
            1 => y,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        set
        {
            switch (index)
            {
                case 0: x = value; break;
                case 1: y = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public static implicit operator bool2(bool value) => new(value, value);

    public static bool2 operator !(bool2 value) => new(!value.x, !value.y);
    public static bool2 operator &(bool2 left, bool2 right) => new(left.x & right.x, left.y & right.y);
    public static bool2 operator |(bool2 left, bool2 right) => new(left.x | right.x, left.y | right.y);
    public static bool2 operator ^(bool2 left, bool2 right) => new(left.x ^ right.x, left.y ^ right.y);
    public static bool2 operator ==(bool2 left, bool2 right) => new(left.x == right.x, left.y == right.y);
    public static bool2 operator !=(bool2 left, bool2 right) => new(left.x != right.x, left.y != right.y);

    public readonly bool Equals(bool2 other) => x == other.x && y == other.y;
    public override readonly bool Equals(object? obj) => obj is bool2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    public override readonly string ToString() => $"bool2({x}, {y})";
}
