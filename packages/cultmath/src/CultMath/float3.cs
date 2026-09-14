using System;

namespace CultMath;

public struct float3 : IEquatable<float3>
{
    public static readonly float3 zero = new(0.0f, 0.0f, 0.0f);
    public static readonly float3 one = new(1.0f, 1.0f, 1.0f);

    public float x;
    public float y;
    public float z;

    public float3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public float3(float2 xy, float z)
        : this(xy.x, xy.y, z)
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

    public float2 xz
    {
        readonly get => new(x, z);
        set
        {
            x = value.x;
            z = value.y;
        }
    }

    public float2 yx
    {
        readonly get => new(y, x);
        set
        {
            y = value.x;
            x = value.y;
        }
    }

    public float2 yz
    {
        readonly get => new(y, z);
        set
        {
            y = value.x;
            z = value.y;
        }
    }

    public float2 zy
    {
        readonly get => new(z, y);
        set
        {
            z = value.x;
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

    public float3 xzy
    {
        readonly get => new(x, z, y);
        set
        {
            x = value.x;
            z = value.y;
            y = value.z;
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

    public float3 zyx
    {
        readonly get => new(z, y, x);
        set
        {
            z = value.x;
            y = value.y;
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

    public static implicit operator float3(float value) => new(value, value, value);
    public static explicit operator System.Numerics.Vector3(float3 value) => new(value.x, value.y, value.z);
    public static explicit operator float3(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

    public static float3 operator +(float3 value) => value;
    public static float3 operator -(float3 value) => new(-value.x, -value.y, -value.z);

    public static float3 operator +(float3 left, float3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static float3 operator +(float3 left, float right) => new(left.x + right, left.y + right, left.z + right);
    public static float3 operator +(float left, float3 right) => new(left + right.x, left + right.y, left + right.z);
    public static float3 operator -(float3 left, float3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static float3 operator -(float3 left, float right) => new(left.x - right, left.y - right, left.z - right);
    public static float3 operator -(float left, float3 right) => new(left - right.x, left - right.y, left - right.z);
    public static float3 operator *(float3 left, float3 right) => new(left.x * right.x, left.y * right.y, left.z * right.z);
    public static float3 operator *(float3 left, float right) => new(left.x * right, left.y * right, left.z * right);
    public static float3 operator *(float left, float3 right) => new(left * right.x, left * right.y, left * right.z);
    public static float3 operator /(float3 left, float3 right) => new(left.x / right.x, left.y / right.y, left.z / right.z);
    public static float3 operator /(float3 left, float right) => new(left.x / right, left.y / right, left.z / right);
    public static float3 operator /(float left, float3 right) => new(left / right.x, left / right.y, left / right.z);
    public static float3 operator %(float3 left, float3 right) => new(left.x % right.x, left.y % right.y, left.z % right.z);
    public static float3 operator %(float3 left, float right) => new(left.x % right, left.y % right, left.z % right);
    public static float3 operator %(float left, float3 right) => new(left % right.x, left % right.y, left % right.z);

    public static bool3 operator <(float3 left, float3 right) => new(left.x < right.x, left.y < right.y, left.z < right.z);
    public static bool3 operator <(float3 left, float right) => new(left.x < right, left.y < right, left.z < right);
    public static bool3 operator <(float left, float3 right) => new(left < right.x, left < right.y, left < right.z);
    public static bool3 operator >(float3 left, float3 right) => new(left.x > right.x, left.y > right.y, left.z > right.z);
    public static bool3 operator >(float3 left, float right) => new(left.x > right, left.y > right, left.z > right);
    public static bool3 operator >(float left, float3 right) => new(left > right.x, left > right.y, left > right.z);
    public static bool3 operator <=(float3 left, float3 right) => new(left.x <= right.x, left.y <= right.y, left.z <= right.z);
    public static bool3 operator <=(float3 left, float right) => new(left.x <= right, left.y <= right, left.z <= right);
    public static bool3 operator <=(float left, float3 right) => new(left <= right.x, left <= right.y, left <= right.z);
    public static bool3 operator >=(float3 left, float3 right) => new(left.x >= right.x, left.y >= right.y, left.z >= right.z);
    public static bool3 operator >=(float3 left, float right) => new(left.x >= right, left.y >= right, left.z >= right);
    public static bool3 operator >=(float left, float3 right) => new(left >= right.x, left >= right.y, left >= right.z);
    public static bool3 operator ==(float3 left, float3 right) => new(left.x == right.x, left.y == right.y, left.z == right.z);
    public static bool3 operator ==(float3 left, float right) => new(left.x == right, left.y == right, left.z == right);
    public static bool3 operator ==(float left, float3 right) => new(left == right.x, left == right.y, left == right.z);
    public static bool3 operator !=(float3 left, float3 right) => new(left.x != right.x, left.y != right.y, left.z != right.z);
    public static bool3 operator !=(float3 left, float right) => new(left.x != right, left.y != right, left.z != right);
    public static bool3 operator !=(float left, float3 right) => new(left != right.x, left != right.y, left != right.z);

    public readonly bool Equals(float3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);
    public override readonly bool Equals(object? obj) => obj is float3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => FormattableString.Invariant($"float3({x:R}, {y:R}, {z:R})");
}
