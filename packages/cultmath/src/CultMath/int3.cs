using System;

namespace CultMath;

public struct int3 : IEquatable<int3>
{
    public static readonly int3 zero = new(0, 0, 0);
    public static readonly int3 one = new(1, 1, 1);

    public int x;
    public int y;
    public int z;

    public int3(int x, int y, int z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public int this[int index]
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

    public static implicit operator int3(int value) => new(value, value, value);
    public static implicit operator float3(int3 value) => new(value.x, value.y, value.z);

    public static int3 operator +(int3 value) => value;
    public static int3 operator -(int3 value) => new(-value.x, -value.y, -value.z);
    public static int3 operator +(int3 left, int3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static int3 operator -(int3 left, int3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static int3 operator *(int3 left, int3 right) => new(left.x * right.x, left.y * right.y, left.z * right.z);
    public static int3 operator /(int3 left, int3 right) => new(left.x / right.x, left.y / right.y, left.z / right.z);
    public static int3 operator %(int3 left, int3 right) => new(left.x % right.x, left.y % right.y, left.z % right.z);

    public static bool3 operator <(int3 left, int3 right) => new(left.x < right.x, left.y < right.y, left.z < right.z);
    public static bool3 operator >(int3 left, int3 right) => new(left.x > right.x, left.y > right.y, left.z > right.z);
    public static bool3 operator <=(int3 left, int3 right) => new(left.x <= right.x, left.y <= right.y, left.z <= right.z);
    public static bool3 operator >=(int3 left, int3 right) => new(left.x >= right.x, left.y >= right.y, left.z >= right.z);
    public static bool3 operator ==(int3 left, int3 right) => new(left.x == right.x, left.y == right.y, left.z == right.z);
    public static bool3 operator !=(int3 left, int3 right) => new(left.x != right.x, left.y != right.y, left.z != right.z);

    public readonly bool Equals(int3 other) => x == other.x && y == other.y && z == other.z;
    public override readonly bool Equals(object? obj) => obj is int3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => FormattableString.Invariant($"int3({x}, {y}, {z})");
}
