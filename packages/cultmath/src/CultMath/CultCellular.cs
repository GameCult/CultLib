namespace CultMath;

/// <summary>
/// The value-and-gradient result of <see cref="math.cellular(float3)"/> (invariant 8's one struct
/// return shape). Every field follows the same <c>float4(gradient.xyz, value.w)</c> layout as a
/// bare value-and-gradient return.
/// </summary>
public struct CultCellular
{
    /// <summary>(&#8711;F1, F1): the gradient and distance to the nearest feature point.</summary>
    public float4 nearest;

    /// <summary>(&#8711;F2 - &#8711;F1, F2 - F1): for lineae along cell borders.</summary>
    public float4 edge;

    /// <summary>The nearest cell's pcg3d hash, mapped to [0, 1).</summary>
    public float id;

    public CultCellular(float4 nearest, float4 edge, float id)
    {
        this.nearest = nearest;
        this.edge = edge;
        this.id = id;
    }
}
