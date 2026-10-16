namespace CultMath;

/// <summary>
/// The two nearest sites of a query point under one site score (<see cref="math.power_score"/> or
/// <see cref="math.multiplicative_score"/>), built by folding sites through
/// <see cref="math.site_cells_insert"/>. Each score field follows the
/// <c>float4(gradient.xyz, value.w)</c> layout of a bare value-and-gradient return.
/// </summary>
public struct CultSiteCells
{
    /// <summary>(&#8711;F1, F1): the smallest score and its gradient. 1.0e30 in w when no site was inserted.</summary>
    public float4 nearest;

    /// <summary>(&#8711;F2, F2): the second smallest score and its gradient. 1.0e30 in w when fewer than two sites were inserted.</summary>
    public float4 second;

    /// <summary>Index of the nearest site as inserted, -1 when none. A float so the struct holds one scalar type, as <see cref="CultCellular.id"/> does; exact below 2^24.</summary>
    public float nearestSite;

    /// <summary>Index of the second nearest site as inserted, -1 when none.</summary>
    public float secondSite;

    public CultSiteCells(float4 nearest, float4 second, float nearestSite, float secondSite)
    {
        this.nearest = nearest;
        this.second = second;
        this.nearestSite = nearestSite;
        this.secondSite = secondSite;
    }
}
