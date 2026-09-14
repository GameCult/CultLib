using System;

namespace CultMath;

public struct double3 : IEquatable<double3>
{
    public static readonly double3 zero = new(0.0, 0.0, 0.0);
    public static readonly double3 one = new(1.0, 1.0, 1.0);

    public double x;
    public double y;
    public double z;

    public double3(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public double2 xy
    {
        readonly get => new(x, y);
        set
        {
            x = value.x;
            y = value.y;
        }
    }

    public double this[int index]
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

    public static implicit operator double3(double value) => new(value, value, value);

    public static double3 operator +(double3 value) => value;
    public static double3 operator -(double3 value) => new(-value.x, -value.y, -value.z);
    public static double3 operator +(double3 left, double3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static double3 operator -(double3 left, double3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static double3 operator *(double3 left, double3 right) => new(left.x * right.x, left.y * right.y, left.z * right.z);
    public static double3 operator /(double3 left, double3 right) => new(left.x / right.x, left.y / right.y, left.z / right.z);

    public static bool3 operator <(double3 left, double3 right) => new(left.x < right.x, left.y < right.y, left.z < right.z);
    public static bool3 operator >(double3 left, double3 right) => new(left.x > right.x, left.y > right.y, left.z > right.z);
    public static bool3 operator <=(double3 left, double3 right) => new(left.x <= right.x, left.y <= right.y, left.z <= right.z);
    public static bool3 operator >=(double3 left, double3 right) => new(left.x >= right.x, left.y >= right.y, left.z >= right.z);
    public static bool3 operator ==(double3 left, double3 right) => new(left.x == right.x, left.y == right.y, left.z == right.z);
    public static bool3 operator !=(double3 left, double3 right) => new(left.x != right.x, left.y != right.y, left.z != right.z);

    public readonly bool Equals(double3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);
    public override readonly bool Equals(object? obj) => obj is double3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => FormattableString.Invariant($"double3({x:R}, {y:R}, {z:R})");
}
