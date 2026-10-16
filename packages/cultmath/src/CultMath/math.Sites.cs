namespace CultMath;

public static partial class math
{
    // Sites: the model page's span element float4(position.xyz, weight). A span function is a C# loop over
    // a per-site step that has a shader twin (site_cells_empty, then site_cells_insert per site); a shader
    // cannot take a span, so shader callers write the same loop over their own buffer. One fold, one rule.

    // Power score of a site: value |p - s|^2 - w^2, gradient 2 (p - s). With this score F2 - F1 is affine in
    // the query point, so site_cells_border's first-order distance is the exact distance to the power bisector.
    public static float4 power_score(float3 p, float4 site)
    {
        var offset = p - site.xyz;
        return new float4(offset * 2.0f, dot(offset, offset) - site.w * site.w);
    }

    // Multiplicative score of a site: value |p - s| / w, gradient (p - s) / (|p - s| w). At p = s the
    // gradient is zero, cellular's degenerate-gradient convention.
    public static float4 multiplicative_score(float3 p, float4 site)
    {
        var offset = p - site.xyz;
        var r = length(offset);
        var gradient = r > 0.0f ? offset / (r * site.w) : new float3(0.0f, 0.0f, 0.0f);
        return new float4(gradient, r / site.w);
    }

    // The fold's identity: no site yet. 1.0e30 is cellular's sentinel (GLSL ES has no inf literal).
    public static CultSiteCells site_cells_empty() =>
        new(new float4(0.0f, 0.0f, 0.0f, 1.0e30f), new float4(0.0f, 0.0f, 0.0f, 1.0e30f), -1.0f, -1.0f);

    // One fold step. Strict < against F1 (the old F1 moves to second), else strict < against F2, so an exact
    // tie keeps the earlier index first.
    public static CultSiteCells site_cells_insert(CultSiteCells cells, float4 score, int index)
    {
        var result = cells;
        if (score.w < cells.nearest.w)
        {
            result.second = cells.nearest;
            result.secondSite = cells.nearestSite;
            result.nearest = score;
            result.nearestSite = (float)index;
        }
        else if (score.w < cells.second.w)
        {
            result.second = score;
            result.secondSite = (float)index;
        }

        return result;
    }

    // float4(unit normal, distance) of the border between the two nearest sites: n = grad F2 - grad F1,
    // distance (F2 - F1) / |n|, normal n / |n|. First order; exact for power scores. n is the direction in
    // which F2 - F1 grows, into the nearest site's cell. (0, 0, 0, 1.0e30) when there is no second site or
    // |n| = 0.
    public static float4 site_cells_border(CultSiteCells cells)
    {
        var n = cells.second.xyz - cells.nearest.xyz;
        var len = length(n);
        if (cells.secondSite < 0.0f || len <= 0.0f)
            return new float4(0.0f, 0.0f, 0.0f, 1.0e30f);
        return new float4(n / len, (cells.second.w - cells.nearest.w) / len);
    }

    // Gabriel margin of the pair (a, b) against a third site: (|other - mid| - r) / r with mid = (a + b) / 2
    // and r = |a - b| / 2. 0 on the diameter circle, negative inside it, -1 when a and b coincide.
    public static float gabriel_margin(float3 a, float3 b, float3 other)
    {
        var r = distance(a, b) * 0.5f;
        if (r <= 0.0f)
            return -1.0f;
        return (distance(other, (a + b) * 0.5f) - r) / r;
    }

    /// <summary>
    /// The two nearest sites of <paramref name="p"/> by <see cref="power_score"/> (the power diagram), every
    /// site visited. A site is <c>float4(position.xyz, weight)</c>; use z = 0 for 2D. Ties keep the lower index first.
    /// </summary>
    /// <exception cref="ArgumentException">A site weight is not finite.</exception>
    public static CultSiteCells power_cells(float3 p, ReadOnlySpan<float4> sites)
    {
        var cells = site_cells_empty();
        for (var i = 0; i < sites.Length; i++)
        {
            if (!float.IsFinite(sites[i].w))
                throw new ArgumentException("Site weights must be finite.", nameof(sites));
            cells = site_cells_insert(cells, power_score(p, sites[i]), i);
        }

        return cells;
    }

    /// <summary>
    /// The two nearest sites of <paramref name="p"/> by <see cref="multiplicative_score"/> (distance divided by
    /// weight), every site visited. A site is <c>float4(position.xyz, weight)</c>; use z = 0 for 2D.
    /// </summary>
    /// <exception cref="ArgumentException">A site weight is not finite and greater than zero.</exception>
    public static CultSiteCells multiplicative_cells(float3 p, ReadOnlySpan<float4> sites)
    {
        var cells = site_cells_empty();
        for (var i = 0; i < sites.Length; i++)
        {
            if (!(float.IsFinite(sites[i].w) && sites[i].w > 0.0f))
                throw new ArgumentException("Site weights must be finite and greater than zero.", nameof(sites));
            cells = site_cells_insert(cells, multiplicative_score(p, sites[i]), i);
        }

        return cells;
    }

    /// <summary>
    /// Whether sites <paramref name="first"/> and <paramref name="second"/> are neighbours in the Gabriel graph
    /// of <paramref name="sites"/> (positions only; weights are ignored): smoothstep(-softness, softness, the
    /// least <see cref="gabriel_margin"/> over every other site). 1 when no other site exists.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the span.</exception>
    /// <exception cref="ArgumentException">The indices are equal, or softness is not finite and greater than zero.</exception>
    public static float soft_gabriel_gate(ReadOnlySpan<float4> sites, int first, int second, float softness)
    {
        if (first < 0 || first >= sites.Length)
            throw new ArgumentOutOfRangeException(nameof(first), "Index is outside the site span.");
        if (second < 0 || second >= sites.Length)
            throw new ArgumentOutOfRangeException(nameof(second), "Index is outside the site span.");
        if (first == second)
            throw new ArgumentException("The two indices must differ.", nameof(second));
        if (!(float.IsFinite(softness) && softness > 0.0f))
            throw new ArgumentException("Softness must be finite and greater than zero.", nameof(softness));

        var margin = float.PositiveInfinity;
        for (var k = 0; k < sites.Length; k++)
        {
            if (k == first || k == second)
                continue;
            margin = min(margin, gabriel_margin(sites[first].xyz, sites[second].xyz, sites[k].xyz));
        }

        return smoothstep(-softness, softness, margin);
    }
}
