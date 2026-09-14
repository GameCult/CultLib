using System;
using System.Diagnostics.CodeAnalysis;

namespace CultMath;

/// <summary>
/// HLSL float3x3: row-major. Constructors take rows, <c>m[i]</c> is a writable reference to
/// row i (so <c>m[1][2] = s</c> works), and <c>_mRC</c> names row R, column C exactly as HLSL
/// does. Use <see cref="math.mul(float3x3, float3)"/> for products; there is no <c>*</c>
/// matrix product because HLSL's <c>*</c> is component-wise.
/// </summary>
public struct float3x3 : IEquatable<float3x3>
{
    public static readonly float3x3 identity = new(1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f);
    public static readonly float3x3 zero = new(0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);

    private float3 _row0;
    private float3 _row1;
    private float3 _row2;

    public float3x3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
    {
        _row0 = new float3(m00, m01, m02);
        _row1 = new float3(m10, m11, m12);
        _row2 = new float3(m20, m21, m22);
    }

    public float3x3(float3 row0, float3 row1, float3 row2)
    {
        _row0 = row0;
        _row1 = row1;
        _row2 = row2;
    }

    public float _m00 { readonly get => _row0.x; set => _row0.x = value; }
    public float _m01 { readonly get => _row0.y; set => _row0.y = value; }
    public float _m02 { readonly get => _row0.z; set => _row0.z = value; }
    public float _m10 { readonly get => _row1.x; set => _row1.x = value; }
    public float _m11 { readonly get => _row1.y; set => _row1.y = value; }
    public float _m12 { readonly get => _row1.z; set => _row1.z = value; }
    public float _m20 { readonly get => _row2.x; set => _row2.x = value; }
    public float _m21 { readonly get => _row2.y; set => _row2.y = value; }
    public float _m22 { readonly get => _row2.z; set => _row2.z = value; }

    /// <remarks>Writes through a readonly field, <c>in</c> parameter, property, or <c>identity</c> hit a defensive copy and are lost; write to a local and assign it back.</remarks>
    [UnscopedRef]
    public ref float3 this[int row]
    {
        get
        {
            switch (row)
            {
                case 0: return ref _row0;
                case 1: return ref _row1;
                case 2: return ref _row2;
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

    public readonly bool Equals(float3x3 other) => _row0.Equals(other._row0) && _row1.Equals(other._row1) && _row2.Equals(other._row2);
    public override readonly bool Equals(object? obj) => obj is float3x3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(_row0, _row1, _row2);

    public override readonly string ToString() =>
        FormattableString.Invariant($"float3x3({_row0.x:R}, {_row0.y:R}, {_row0.z:R}, {_row1.x:R}, {_row1.y:R}, {_row1.z:R}, {_row2.x:R}, {_row2.y:R}, {_row2.z:R})");
}
