using System;

namespace CultMath;

public struct double2 : IEquatable<double2>
{
    public static readonly double2 zero = new(0.0, 0.0);
    public static readonly double2 one = new(1.0, 1.0);

    public double x;
    public double y;

    public double2(double x, double y)
    {
        this.x = x;
        this.y = y;
    }

    public double this[int index]
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

    public static implicit operator double2(double value) => new(value, value);

    public static double2 operator +(double2 value) => value;
    public static double2 operator -(double2 value) => new(-value.x, -value.y);
    public static double2 operator +(double2 left, double2 right) => new(left.x + right.x, left.y + right.y);
    public static double2 operator -(double2 left, double2 right) => new(left.x - right.x, left.y - right.y);
    public static double2 operator *(double2 left, double2 right) => new(left.x * right.x, left.y * right.y);
    public static double2 operator /(double2 left, double2 right) => new(left.x / right.x, left.y / right.y);

    public static bool2 operator <(double2 left, double2 right) => new(left.x < right.x, left.y < right.y);
    public static bool2 operator >(double2 left, double2 right) => new(left.x > right.x, left.y > right.y);
    public static bool2 operator <=(double2 left, double2 right) => new(left.x <= right.x, left.y <= right.y);
    public static bool2 operator >=(double2 left, double2 right) => new(left.x >= right.x, left.y >= right.y);
    public static bool2 operator ==(double2 left, double2 right) => new(left.x == right.x, left.y == right.y);
    public static bool2 operator !=(double2 left, double2 right) => new(left.x != right.x, left.y != right.y);

    public readonly bool Equals(double2 other) => x.Equals(other.x) && y.Equals(other.y);
    public override readonly bool Equals(object? obj) => obj is double2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    public override readonly string ToString() => FormattableString.Invariant($"double2({x:R}, {y:R})");
}
