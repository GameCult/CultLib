namespace CultMath;

public readonly record struct float3(float x, float y, float z)
{
    public static readonly float3 zero = new(0.0f, 0.0f, 0.0f);
    public static readonly float3 one = new(1.0f, 1.0f, 1.0f);

    public float2 xy => new(x, y);
    public float2 xz => new(x, z);
    public float2 yz => new(y, z);

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
