using System;

namespace CultMath;

public partial struct int2 : IEquatable<int2>
{
    public static readonly int2 zero = new(0, 0);
    public static readonly int2 one = new(1, 1);

    public int x;
    public int y;

    public int2(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    public int this[int index]
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

    public static implicit operator int2(int value) => new(value, value);
    public static implicit operator float2(int2 value) => new(value.x, value.y);
    public static explicit operator System.Numerics.Vector2(int2 value) => new(value.x, value.y);
    public static explicit operator int2(System.Numerics.Vector2 value) => new((int)value.X, (int)value.Y);

    public static int2 operator +(int2 value) => value;
    public static int2 operator -(int2 value) => new(-value.x, -value.y);
    public static int2 operator +(int2 left, int2 right) => new(left.x + right.x, left.y + right.y);
    public static int2 operator -(int2 left, int2 right) => new(left.x - right.x, left.y - right.y);
    public static int2 operator *(int2 left, int2 right) => new(left.x * right.x, left.y * right.y);
    public static int2 operator /(int2 left, int2 right) => new(left.x / right.x, left.y / right.y);
    public static int2 operator %(int2 left, int2 right) => new(left.x % right.x, left.y % right.y);

    public static bool2 operator <(int2 left, int2 right) => new(left.x < right.x, left.y < right.y);
    public static bool2 operator >(int2 left, int2 right) => new(left.x > right.x, left.y > right.y);
    public static bool2 operator <=(int2 left, int2 right) => new(left.x <= right.x, left.y <= right.y);
    public static bool2 operator >=(int2 left, int2 right) => new(left.x >= right.x, left.y >= right.y);
    public static bool2 operator ==(int2 left, int2 right) => new(left.x == right.x, left.y == right.y);
    public static bool2 operator !=(int2 left, int2 right) => new(left.x != right.x, left.y != right.y);

    public readonly bool Equals(int2 other) => x == other.x && y == other.y;
    public override readonly bool Equals(object? obj) => obj is int2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    public override readonly string ToString() => FormattableString.Invariant($"int2({x}, {y})");
}
