using System;

namespace CultMath;

public record struct float2(float x, float y)
{
    public static readonly float2 zero = new(0.0f, 0.0f);
    public static readonly float2 one = new(1.0f, 1.0f);

    public float this[int index] => index switch
    {
        0 => x,
        1 => y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator float2(float value) => new(value, value);
    public static explicit operator System.Numerics.Vector2(float2 value) => new(value.x, value.y);
    public static explicit operator float2(System.Numerics.Vector2 value) => new(value.X, value.Y);

    public static float2 operator +(float2 value) => value;
    public static float2 operator -(float2 value) => new(-value.x, -value.y);
    public static float2 operator +(float2 left, float2 right) => new(left.x + right.x, left.y + right.y);
    public static float2 operator -(float2 left, float2 right) => new(left.x - right.x, left.y - right.y);
    public static float2 operator *(float2 left, float2 right) => new(left.x * right.x, left.y * right.y);
    public static float2 operator /(float2 left, float2 right) => new(left.x / right.x, left.y / right.y);
    public static float2 operator %(float2 left, float2 right) => new(left.x % right.x, left.y % right.y);
}
