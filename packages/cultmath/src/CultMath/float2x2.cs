using System;

namespace CultMath;

/// <summary>
/// HLSL float2x2: row-major. Constructors take rows, <c>m[i]</c> is row i, and
/// <c>_mRC</c> names row R, column C exactly as HLSL does. Use <see cref="math.mul(float2x2, float2)"/>
/// for products; there is no <c>*</c> matrix product because HLSL's <c>*</c> is component-wise.
/// </summary>
public struct float2x2 : IEquatable<float2x2>
{
    public static readonly float2x2 identity = new(1.0f, 0.0f, 0.0f, 1.0f);
    public static readonly float2x2 zero = new(0.0f, 0.0f, 0.0f, 0.0f);

    public float _m00;
    public float _m01;
    public float _m10;
    public float _m11;

    public float2x2(float m00, float m01, float m10, float m11)
    {
        _m00 = m00;
        _m01 = m01;
        _m10 = m10;
        _m11 = m11;
    }

    public float2x2(float2 row0, float2 row1)
        : this(row0.x, row0.y, row1.x, row1.y)
    {
    }

    public float2 this[int row]
    {
        readonly get => row switch
        {
            0 => new float2(_m00, _m01),
            1 => new float2(_m10, _m11),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        set
        {
            switch (row)
            {
                case 0: _m00 = value.x; _m01 = value.y; break;
                case 1: _m10 = value.x; _m11 = value.y; break;
                default: throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    /// <summary>
    /// Counter-clockwise rotation for column vectors: <c>mul(Rotate(a), v)</c> equals
    /// <c>math.rotate(v, a)</c>; <c>mul(v, Rotate(a))</c> rotates the other way.
    /// </summary>
    public static float2x2 Rotate(float radians)
    {
        var s = MathF.Sin(radians);
        var c = MathF.Cos(radians);
        return new float2x2(c, -s, s, c);
    }

    public readonly bool Equals(float2x2 other) =>
        _m00.Equals(other._m00) && _m01.Equals(other._m01) && _m10.Equals(other._m10) && _m11.Equals(other._m11);

    public override readonly bool Equals(object? obj) => obj is float2x2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(_m00, _m01, _m10, _m11);
    public override readonly string ToString() => FormattableString.Invariant($"float2x2({_m00:R}, {_m01:R}, {_m10:R}, {_m11:R})");
}
