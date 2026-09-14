using System;

namespace CultMath;

public partial struct float2 : IEquatable<float2>
{
    public static readonly float2 zero = new(0.0f, 0.0f);
    public static readonly float2 one = new(1.0f, 1.0f);

    public float x;
    public float y;

    public float2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public float this[int index]
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

    public static implicit operator float2(float value) => new(value, value);
    public static explicit operator System.Numerics.Vector2(float2 value) => new(value.x, value.y);
    public static explicit operator float2(System.Numerics.Vector2 value) => new(value.X, value.Y);

    public static float2 operator +(float2 value) => value;
    public static float2 operator -(float2 value) => new(-value.x, -value.y);

    public static float2 operator +(float2 left, float2 right) => new(left.x + right.x, left.y + right.y);
    public static float2 operator +(float2 left, float right) => new(left.x + right, left.y + right);
    public static float2 operator +(float left, float2 right) => new(left + right.x, left + right.y);
    public static float2 operator -(float2 left, float2 right) => new(left.x - right.x, left.y - right.y);
    public static float2 operator -(float2 left, float right) => new(left.x - right, left.y - right);
    public static float2 operator -(float left, float2 right) => new(left - right.x, left - right.y);
    public static float2 operator *(float2 left, float2 right) => new(left.x * right.x, left.y * right.y);
    public static float2 operator *(float2 left, float right) => new(left.x * right, left.y * right);
    public static float2 operator *(float left, float2 right) => new(left * right.x, left * right.y);
    public static float2 operator /(float2 left, float2 right) => new(left.x / right.x, left.y / right.y);
    public static float2 operator /(float2 left, float right) => new(left.x / right, left.y / right);
    public static float2 operator /(float left, float2 right) => new(left / right.x, left / right.y);
    public static float2 operator %(float2 left, float2 right) => new(left.x % right.x, left.y % right.y);
    public static float2 operator %(float2 left, float right) => new(left.x % right, left.y % right);
    public static float2 operator %(float left, float2 right) => new(left % right.x, left % right.y);

    public static bool2 operator <(float2 left, float2 right) => new(left.x < right.x, left.y < right.y);
    public static bool2 operator <(float2 left, float right) => new(left.x < right, left.y < right);
    public static bool2 operator <(float left, float2 right) => new(left < right.x, left < right.y);
    public static bool2 operator >(float2 left, float2 right) => new(left.x > right.x, left.y > right.y);
    public static bool2 operator >(float2 left, float right) => new(left.x > right, left.y > right);
    public static bool2 operator >(float left, float2 right) => new(left > right.x, left > right.y);
    public static bool2 operator <=(float2 left, float2 right) => new(left.x <= right.x, left.y <= right.y);
    public static bool2 operator <=(float2 left, float right) => new(left.x <= right, left.y <= right);
    public static bool2 operator <=(float left, float2 right) => new(left <= right.x, left <= right.y);
    public static bool2 operator >=(float2 left, float2 right) => new(left.x >= right.x, left.y >= right.y);
    public static bool2 operator >=(float2 left, float right) => new(left.x >= right, left.y >= right);
    public static bool2 operator >=(float left, float2 right) => new(left >= right.x, left >= right.y);
    public static bool2 operator ==(float2 left, float2 right) => new(left.x == right.x, left.y == right.y);
    public static bool2 operator ==(float2 left, float right) => new(left.x == right, left.y == right);
    public static bool2 operator ==(float left, float2 right) => new(left == right.x, left == right.y);
    public static bool2 operator !=(float2 left, float2 right) => new(left.x != right.x, left.y != right.y);
    public static bool2 operator !=(float2 left, float right) => new(left.x != right, left.y != right);
    public static bool2 operator !=(float left, float2 right) => new(left != right.x, left != right.y);

    public readonly bool Equals(float2 other) => x.Equals(other.x) && y.Equals(other.y);
    public override readonly bool Equals(object? obj) => obj is float2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    public override readonly string ToString() => FormattableString.Invariant($"float2({x:R}, {y:R})");
}
