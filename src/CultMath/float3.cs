using System;

namespace CultMath;

public record struct float3(float x, float y, float z)
{
    public static readonly float3 zero = new(0.0f, 0.0f, 0.0f);
    public static readonly float3 one = new(1.0f, 1.0f, 1.0f);

    public float3(float2 xy, float z)
        : this(xy.x, xy.y, z)
    {
    }

    public float2 xy
    {
        get => new(x, y);
        set
        {
            x = value.x;
            y = value.y;
        }
    }

    public float2 xz
    {
        get => new(x, z);
        set
        {
            x = value.x;
            z = value.y;
        }
    }

    public float2 yz
    {
        get => new(y, z);
        set
        {
            y = value.x;
            z = value.y;
        }
    }

    public float2 zy
    {
        get => new(z, y);
        set
        {
            z = value.x;
            y = value.y;
        }
    }

    public float3 yzx => new(y, z, x);
    public float3 zyx => new(z, y, x);

    public float this[int index] => index switch
    {
        0 => x,
        1 => y,
        2 => z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator float3(float value) => new(value, value, value);
    public static explicit operator System.Numerics.Vector3(float3 value) => new(value.x, value.y, value.z);
    public static explicit operator float3(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

    public static float3 operator +(float3 value) => value;
    public static float3 operator -(float3 value) => new(-value.x, -value.y, -value.z);
    public static float3 operator +(float3 left, float3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static float3 operator -(float3 left, float3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static float3 operator *(float3 left, float3 right) => new(left.x * right.x, left.y * right.y, left.z * right.z);
    public static float3 operator /(float3 left, float3 right) => new(left.x / right.x, left.y / right.y, left.z / right.z);
    public static float3 operator %(float3 left, float3 right) => new(left.x % right.x, left.y % right.y, left.z % right.z);
}
