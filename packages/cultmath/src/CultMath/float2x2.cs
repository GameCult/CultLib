using System;
using System.Diagnostics.CodeAnalysis;

namespace CultMath;

/// <summary>
/// HLSL float2x2: row-major. Constructors take rows, <c>m[i]</c> is a writable reference to
/// row i (so <c>m[1][0] = s</c> works), and <c>_mRC</c> names row R, column C exactly as HLSL
/// does. Use <see cref="math.mul(float2x2, float2)"/> for products; there is no <c>*</c>
/// matrix product because HLSL's <c>*</c> is component-wise.
/// </summary>
public struct float2x2 : IEquatable<float2x2>
{
    public static readonly float2x2 identity = new(1.0f, 0.0f, 0.0f, 1.0f);
    public static readonly float2x2 zero = new(0.0f, 0.0f, 0.0f, 0.0f);

    private float2 _row0;
    private float2 _row1;

    public float2x2(float m00, float m01, float m10, float m11)
    {
        _row0 = new float2(m00, m01);
        _row1 = new float2(m10, m11);
    }

    public float2x2(float2 row0, float2 row1)
    {
        _row0 = row0;
        _row1 = row1;
    }

    public float _m00 { readonly get => _row0.x; set => _row0.x = value; }
    public float _m01 { readonly get => _row0.y; set => _row0.y = value; }
    public float _m10 { readonly get => _row1.x; set => _row1.x = value; }
    public float _m11 { readonly get => _row1.y; set => _row1.y = value; }

    [UnscopedRef]
    public ref float2 this[int row]
    {
        get
        {
            switch (row)
            {
                case 0: return ref _row0;
                case 1: return ref _row1;
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

    public readonly bool Equals(float2x2 other) => _row0.Equals(other._row0) && _row1.Equals(other._row1);
    public override readonly bool Equals(object? obj) => obj is float2x2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(_row0, _row1);
    public override readonly string ToString() => FormattableString.Invariant($"float2x2({_row0.x:R}, {_row0.y:R}, {_row1.x:R}, {_row1.y:R})");
}
