namespace CultMath;

public readonly record struct float4(float x, float y, float z, float w)
{
    public static readonly float4 zero = new(0.0f, 0.0f, 0.0f, 0.0f);
    public static readonly float4 one = new(1.0f, 1.0f, 1.0f, 1.0f);

    public float4(float2 xy, float z, float w)
        : this(xy.x, xy.y, z, w)
    {
    }

    public float4(float3 xyz, float w)
        : this(xyz.x, xyz.y, xyz.z, w)
    {
    }

    public float2 xy => new(x, y);
    public float3 xyz => new(x, y, z);

    public float this[int index] => index switch
    {
        0 => x,
        1 => y,
        2 => z,
        3 => w,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator float4(float value) => new(value, value, value, value);
    public static explicit operator System.Numerics.Vector4(float4 value) => new(value.x, value.y, value.z, value.w);
    public static explicit operator float4(System.Numerics.Vector4 value) => new(value.X, value.Y, value.Z, value.W);

    public static float4 operator +(float4 value) => value;
    public static float4 operator -(float4 value) => new(-value.x, -value.y, -value.z, -value.w);
    public static float4 operator +(float4 left, float4 right) => new(left.x + right.x, left.y + right.y, left.z + right.z, left.w + right.w);
    public static float4 operator -(float4 left, float4 right) => new(left.x - right.x, left.y - right.y, left.z - right.z, left.w - right.w);
    public static float4 operator *(float4 left, float4 right) => new(left.x * right.x, left.y * right.y, left.z * right.z, left.w * right.w);
    public static float4 operator /(float4 left, float4 right) => new(left.x / right.x, left.y / right.y, left.z / right.z, left.w / right.w);
    public static float4 operator %(float4 left, float4 right) => new(left.x % right.x, left.y % right.y, left.z % right.z, left.w % right.w);
}
