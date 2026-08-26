using System;

namespace CultMath;

public record struct int2(int x, int y)
{
    public static readonly int2 zero = new(0, 0);
    public static readonly int2 one = new(1, 1);

    public int this[int index] => index switch
    {
        0 => x,
        1 => y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

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
}
