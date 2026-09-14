using System;

namespace CultMath;

public struct float4 : IEquatable<float4>
{
    public static readonly float4 zero = new(0.0f, 0.0f, 0.0f, 0.0f);
    public static readonly float4 one = new(1.0f, 1.0f, 1.0f, 1.0f);

    public float x;
    public float y;
    public float z;
    public float w;

    public float4(float x, float y, float z, float w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public float4(float2 xy, float z, float w)
        : this(xy.x, xy.y, z, w)
    {
    }

    public float4(float3 xyz, float w)
        : this(xyz.x, xyz.y, xyz.z, w)
    {
    }

    public float4(float x, float3 yzw)
        : this(x, yzw.x, yzw.y, yzw.z)
    {
    }

    public float2 xy
    {
        readonly get => new(x, y);
        set
        {
            x = value.x;
            y = value.y;
        }
    }

    public float3 xyz
    {
        readonly get => new(x, y, z);
        set
        {
            x = value.x;
            y = value.y;
            z = value.z;
        }
    }

    public float3 xyw
    {
        readonly get => new(x, y, w);
        set
        {
            x = value.x;
            y = value.y;
            w = value.z;
        }
    }

    public float3 yzx
    {
        readonly get => new(y, z, x);
        set
        {
            y = value.x;
            z = value.y;
            x = value.z;
        }
    }

    public float this[int index]
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

    public static implicit operator float4(float value) => new(value, value, value, value);
    public static explicit operator System.Numerics.Vector4(float4 value) => new(value.x, value.y, value.z, value.w);
    public static explicit operator float4(System.Numerics.Vector4 value) => new(value.X, value.Y, value.Z, value.W);

    public static float4 operator +(float4 value) => value;
    public static float4 operator -(float4 value) => new(-value.x, -value.y, -value.z, -value.w);

    public static float4 operator +(float4 left, float4 right) => new(left.x + right.x, left.y + right.y, left.z + right.z, left.w + right.w);
    public static float4 operator +(float4 left, float right) => new(left.x + right, left.y + right, left.z + right, left.w + right);
    public static float4 operator +(float left, float4 right) => new(left + right.x, left + right.y, left + right.z, left + right.w);
    public static float4 operator -(float4 left, float4 right) => new(left.x - right.x, left.y - right.y, left.z - right.z, left.w - right.w);
    public static float4 operator -(float4 left, float right) => new(left.x - right, left.y - right, left.z - right, left.w - right);
    public static float4 operator -(float left, float4 right) => new(left - right.x, left - right.y, left - right.z, left - right.w);
    public static float4 operator *(float4 left, float4 right) => new(left.x * right.x, left.y * right.y, left.z * right.z, left.w * right.w);
    public static float4 operator *(float4 left, float right) => new(left.x * right, left.y * right, left.z * right, left.w * right);
    public static float4 operator *(float left, float4 right) => new(left * right.x, left * right.y, left * right.z, left * right.w);
    public static float4 operator /(float4 left, float4 right) => new(left.x / right.x, left.y / right.y, left.z / right.z, left.w / right.w);
    public static float4 operator /(float4 left, float right) => new(left.x / right, left.y / right, left.z / right, left.w / right);
    public static float4 operator /(float left, float4 right) => new(left / right.x, left / right.y, left / right.z, left / right.w);
    public static float4 operator %(float4 left, float4 right) => new(left.x % right.x, left.y % right.y, left.z % right.z, left.w % right.w);
    public static float4 operator %(float4 left, float right) => new(left.x % right, left.y % right, left.z % right, left.w % right);
    public static float4 operator %(float left, float4 right) => new(left % right.x, left % right.y, left % right.z, left % right.w);

    public static bool4 operator <(float4 left, float4 right) => new(left.x < right.x, left.y < right.y, left.z < right.z, left.w < right.w);
    public static bool4 operator <(float4 left, float right) => new(left.x < right, left.y < right, left.z < right, left.w < right);
    public static bool4 operator <(float left, float4 right) => new(left < right.x, left < right.y, left < right.z, left < right.w);
    public static bool4 operator >(float4 left, float4 right) => new(left.x > right.x, left.y > right.y, left.z > right.z, left.w > right.w);
    public static bool4 operator >(float4 left, float right) => new(left.x > right, left.y > right, left.z > right, left.w > right);
    public static bool4 operator >(float left, float4 right) => new(left > right.x, left > right.y, left > right.z, left > right.w);
    public static bool4 operator <=(float4 left, float4 right) => new(left.x <= right.x, left.y <= right.y, left.z <= right.z, left.w <= right.w);
    public static bool4 operator <=(float4 left, float right) => new(left.x <= right, left.y <= right, left.z <= right, left.w <= right);
    public static bool4 operator <=(float left, float4 right) => new(left <= right.x, left <= right.y, left <= right.z, left <= right.w);
    public static bool4 operator >=(float4 left, float4 right) => new(left.x >= right.x, left.y >= right.y, left.z >= right.z, left.w >= right.w);
    public static bool4 operator >=(float4 left, float right) => new(left.x >= right, left.y >= right, left.z >= right, left.w >= right);
    public static bool4 operator >=(float left, float4 right) => new(left >= right.x, left >= right.y, left >= right.z, left >= right.w);
    public static bool4 operator ==(float4 left, float4 right) => new(left.x == right.x, left.y == right.y, left.z == right.z, left.w == right.w);
    public static bool4 operator ==(float4 left, float right) => new(left.x == right, left.y == right, left.z == right, left.w == right);
    public static bool4 operator ==(float left, float4 right) => new(left == right.x, left == right.y, left == right.z, left == right.w);
    public static bool4 operator !=(float4 left, float4 right) => new(left.x != right.x, left.y != right.y, left.z != right.z, left.w != right.w);
    public static bool4 operator !=(float4 left, float right) => new(left.x != right, left.y != right, left.z != right, left.w != right);
    public static bool4 operator !=(float left, float4 right) => new(left != right.x, left != right.y, left != right.z, left != right.w);

    public readonly bool Equals(float4 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
    public override readonly bool Equals(object? obj) => obj is float4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override readonly string ToString() => FormattableString.Invariant($"float4({x:R}, {y:R}, {z:R}, {w:R})");
}
