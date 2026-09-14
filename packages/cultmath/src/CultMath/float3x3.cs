using System;

namespace CultMath;

/// <summary>
/// HLSL float3x3: row-major. Constructors take rows, <c>m[i]</c> is row i, and
/// <c>_mRC</c> names row R, column C exactly as HLSL does. Use <see cref="math.mul(float3x3, float3)"/>
/// for products; there is no <c>*</c> matrix product because HLSL's <c>*</c> is component-wise.
/// </summary>
public struct float3x3 : IEquatable<float3x3>
{
    public static readonly float3x3 identity = new(1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f);
    public static readonly float3x3 zero = new(0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);

    public float _m00;
    public float _m01;
    public float _m02;
    public float _m10;
    public float _m11;
    public float _m12;
    public float _m20;
    public float _m21;
    public float _m22;

    public float3x3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
    {
        _m00 = m00;
        _m01 = m01;
        _m02 = m02;
        _m10 = m10;
        _m11 = m11;
        _m12 = m12;
        _m20 = m20;
        _m21 = m21;
        _m22 = m22;
    }

    public float3x3(float3 row0, float3 row1, float3 row2)
        : this(row0.x, row0.y, row0.z, row1.x, row1.y, row1.z, row2.x, row2.y, row2.z)
    {
    }

    public float3 this[int row]
    {
        readonly get => row switch
        {
            0 => new float3(_m00, _m01, _m02),
            1 => new float3(_m10, _m11, _m12),
            2 => new float3(_m20, _m21, _m22),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        set
        {
            switch (row)
            {
                case 0: _m00 = value.x; _m01 = value.y; _m02 = value.z; break;
                case 1: _m10 = value.x; _m11 = value.y; _m12 = value.z; break;
                case 2: _m20 = value.x; _m21 = value.y; _m22 = value.z; break;
                default: throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    /// <summary>Rotation about +X for column vectors (<c>mul(m, v)</c>).</summary>
    public static float3x3 RotateX(float radians)
    {
        var s = MathF.Sin(radians);
        var c = MathF.Cos(radians);
        return new float3x3(1.0f, 0.0f, 0.0f, 0.0f, c, -s, 0.0f, s, c);
    }

    /// <summary>Rotation about +Y for column vectors (<c>mul(m, v)</c>).</summary>
    public static float3x3 RotateY(float radians)
    {
        var s = MathF.Sin(radians);
        var c = MathF.Cos(radians);
        return new float3x3(c, 0.0f, s, 0.0f, 1.0f, 0.0f, -s, 0.0f, c);
    }

    /// <summary>Rotation about +Z for column vectors (<c>mul(m, v)</c>).</summary>
    public static float3x3 RotateZ(float radians)
    {
        var s = MathF.Sin(radians);
        var c = MathF.Cos(radians);
        return new float3x3(c, -s, 0.0f, s, c, 0.0f, 0.0f, 0.0f, 1.0f);
    }

    /// <summary>
    /// Extrinsic Euler rotation in radians. The order names the axis applied first:
    /// <see cref="math.RotationOrder.YXZ"/> rotates about Y, then X, then Z, so the
    /// matrix is <c>mul(RotateZ, mul(RotateX, RotateY))</c> for column vectors.
    /// </summary>
    public static float3x3 Euler(float3 xyz, math.RotationOrder order = math.RotationOrder.Default)
    {
        var x = RotateX(xyz.x);
        var y = RotateY(xyz.y);
        var z = RotateZ(xyz.z);
        return order switch
        {
            math.RotationOrder.XYZ => math.mul(z, math.mul(y, x)),
            math.RotationOrder.XZY => math.mul(y, math.mul(z, x)),
            math.RotationOrder.YXZ => math.mul(z, math.mul(x, y)),
            math.RotationOrder.YZX => math.mul(x, math.mul(z, y)),
            math.RotationOrder.ZXY => math.mul(y, math.mul(x, z)),
            math.RotationOrder.ZYX => math.mul(x, math.mul(y, z)),
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
    }

    public static float3x3 Euler(float x, float y, float z, math.RotationOrder order = math.RotationOrder.Default) =>
        Euler(new float3(x, y, z), order);

    public readonly bool Equals(float3x3 other) =>
        _m00.Equals(other._m00) && _m01.Equals(other._m01) && _m02.Equals(other._m02) &&
        _m10.Equals(other._m10) && _m11.Equals(other._m11) && _m12.Equals(other._m12) &&
        _m20.Equals(other._m20) && _m21.Equals(other._m21) && _m22.Equals(other._m22);

    public override readonly bool Equals(object? obj) => obj is float3x3 other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(_m00, _m01, _m02), HashCode.Combine(_m10, _m11, _m12), HashCode.Combine(_m20, _m21, _m22));

    public override readonly string ToString() =>
        FormattableString.Invariant($"float3x3({_m00:R}, {_m01:R}, {_m02:R}, {_m10:R}, {_m11:R}, {_m12:R}, {_m20:R}, {_m21:R}, {_m22:R})");
}
