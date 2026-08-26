using System;

namespace CultMath;

public record struct double2(double x, double y)
{
    public static readonly double2 zero = new(0.0, 0.0);
    public static readonly double2 one = new(1.0, 1.0);

    public double this[int index] => index switch
    {
        0 => x,
        1 => y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator double2(double value) => new(value, value);

    public static double2 operator +(double2 value) => value;
    public static double2 operator -(double2 value) => new(-value.x, -value.y);
    public static double2 operator +(double2 left, double2 right) => new(left.x + right.x, left.y + right.y);
    public static double2 operator -(double2 left, double2 right) => new(left.x - right.x, left.y - right.y);
    public static double2 operator *(double2 left, double2 right) => new(left.x * right.x, left.y * right.y);
    public static double2 operator /(double2 left, double2 right) => new(left.x / right.x, left.y / right.y);
}
