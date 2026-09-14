using System;

namespace CultMath;

public partial struct int4 : IEquatable<int4>
{
    public static readonly int4 zero = new(0, 0, 0, 0);
    public static readonly int4 one = new(1, 1, 1, 1);

    public int x;
    public int y;
    public int z;
    public int w;

    public int4(int x, int y, int z, int w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public int this[int index]
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

    public static implicit operator int4(int value) => new(value, value, value, value);
    public static implicit operator float4(int4 value) => new(value.x, value.y, value.z, value.w);

    public static int4 operator +(int4 value) => value;
    public static int4 operator -(int4 value) => new(-value.x, -value.y, -value.z, -value.w);
    public static int4 operator +(int4 left, int4 right) => new(left.x + right.x, left.y + right.y, left.z + right.z, left.w + right.w);
    public static int4 operator -(int4 left, int4 right) => new(left.x - right.x, left.y - right.y, left.z - right.z, left.w - right.w);
    public static int4 operator *(int4 left, int4 right) => new(left.x * right.x, left.y * right.y, left.z * right.z, left.w * right.w);
    public static int4 operator /(int4 left, int4 right) => new(left.x / right.x, left.y / right.y, left.z / right.z, left.w / right.w);
    public static int4 operator %(int4 left, int4 right) => new(left.x % right.x, left.y % right.y, left.z % right.z, left.w % right.w);

    public static bool4 operator <(int4 left, int4 right) => new(left.x < right.x, left.y < right.y, left.z < right.z, left.w < right.w);
    public static bool4 operator >(int4 left, int4 right) => new(left.x > right.x, left.y > right.y, left.z > right.z, left.w > right.w);
    public static bool4 operator <=(int4 left, int4 right) => new(left.x <= right.x, left.y <= right.y, left.z <= right.z, left.w <= right.w);
    public static bool4 operator >=(int4 left, int4 right) => new(left.x >= right.x, left.y >= right.y, left.z >= right.z, left.w >= right.w);
    public static bool4 operator ==(int4 left, int4 right) => new(left.x == right.x, left.y == right.y, left.z == right.z, left.w == right.w);
    public static bool4 operator !=(int4 left, int4 right) => new(left.x != right.x, left.y != right.y, left.z != right.z, left.w != right.w);

    public readonly bool Equals(int4 other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public override readonly bool Equals(object? obj) => obj is int4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override readonly string ToString() => FormattableString.Invariant($"int4({x}, {y}, {z}, {w})");
}
