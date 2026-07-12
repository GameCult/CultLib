using System;

namespace CultMath;

public record struct double3(double x, double y, double z)
{
    public static readonly double3 zero = new(0.0, 0.0, 0.0);
    public static readonly double3 one = new(1.0, 1.0, 1.0);

    public double2 xy
    {
        get => new(x, y);
        set
        {
            x = value.x;
            y = value.y;
        }
    }

    public double this[int index] => index switch
    {
        0 => x,
        1 => y,
        2 => z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator double3(double value) => new(value, value, value);

    public static double3 operator +(double3 value) => value;
    public static double3 operator -(double3 value) => new(-value.x, -value.y, -value.z);
    public static double3 operator +(double3 left, double3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static double3 operator -(double3 left, double3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static double3 operator *(double3 left, double3 right) => new(left.x * right.x, left.y * right.y, left.z * right.z);
    public static double3 operator /(double3 left, double3 right) => new(left.x / right.x, left.y / right.y, left.z / right.z);
}
