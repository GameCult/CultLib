namespace CultMath;

public static partial class math
{
    // Sites: the model page's span element float4(position.xyz, weight). A span function is a C# loop over
    // a per-site step that has a shader twin (site_cells_empty, then site_cells_insert per site); a shader
    // cannot take a span, so shader callers write the same loop over their own buffer. One fold, one rule.

    // Power score of a site: value |p - s|^2 - w^2, gradient 2 (p - s). The cell of a site is the intersection
    // of the half-spaces bounded by its radical planes against every other site (power_border_step).
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

    // float4(unit normal, distance) of the bisector of the two lowest scores: n = grad F2 - grad F1, distance
    // (F2 - F1) / |n|, normal n / |n| (into the nearest site's cell). First order, for either metric. It is NOT
    // the distance to the nearest site's cell boundary, which the second-lowest score does not give in general;
    // for power scores that is power_border. (0, 0, 0, 1.0e30) when there is no second site or |n| = 0.
    public static float4 site_cells_border(CultSiteCells cells)
    {
        var n = cells.second.xyz - cells.nearest.xyz;
        var len = length(n);
        if (cells.secondSite < 0.0f || len <= 0.0f)
            return new float4(0.0f, 0.0f, 0.0f, 1.0e30f);
        return new float4(n / len, (cells.second.w - cells.nearest.w) / len);
    }

    // One step of the exact power cell border. owner is the site whose cell p lies in (the fold's nearest);
    // other is another site. The radical plane of the pair is {x : |x - s1|^2 - w1^2 = |x - s2|^2 - w2^2};
    // with d = s2 - s1 the signed distance from p to it, positive on the owner's side, is
    // ((s1 - p).d + (|d|^2 + w1^2 - w2^2) / 2) / |d|. It is built from the offset s1 - p and the weight
    // difference (w1 - w2)(w1 + w2), never from a difference of two large squared distances, so it does not
    // cancel for a far query or close sites. border is the running (unit normal toward the owner, distance),
    // starting at (0, 0, 0, 1.0e30): the cell's boundary distance is the least plane distance over every other
    // site. A site coincident with the owner has no plane and leaves border unchanged.
    public static float4 power_border_step(float3 p, float4 owner, float4 other, float4 border)
    {
        var d = other.xyz - owner.xyz;
        var len = length(d);
        if (len <= 0.0f)
            return border;
        var plane = max((dot(owner.xyz - p, d) + 0.5f * (dot(d, d) + (owner.w - other.w) * (owner.w + other.w))) / len, 0.0f);
        return plane < border.w ? new float4(-d / len, plane) : border;
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

    // Index floats hold integers exactly up to 2^24, so a longer span is refused.
    private const int MaxSiteSpan = 1 << 24;

    private static bool all_finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // The one validation of a cell span: finite query, finite positions and weights, weights above zero when
    // positiveWeights, at most 2^24 sites. Messages name the parameter, never a value.
    private static void require_cell_span(float3 p, ReadOnlySpan<float4> sites, bool positiveWeights)
    {
        if (!all_finite(p))
            throw new ArgumentException("The query point must be finite.", nameof(p));
        if (sites.Length > MaxSiteSpan)
            throw new ArgumentException("A site span holds at most 2^24 sites.", nameof(sites));
        for (var i = 0; i < sites.Length; i++)
        {
            if (!all_finite(sites[i].xyz))
                throw new ArgumentException("Site positions must be finite.", nameof(sites));
            if (!float.IsFinite(sites[i].w) || (positiveWeights && !(sites[i].w > 0.0f)))
                throw new ArgumentException(positiveWeights ? "Site weights must be finite and greater than zero." : "Site weights must be finite.", nameof(sites));
        }
    }

    /// <summary>
    /// The two nearest sites of <paramref name="p"/> by <see cref="power_score"/> (the power diagram), every
    /// site visited. A site is <c>float4(position.xyz, weight)</c>; use z = 0 for 2D. Ties keep the lower index first.
    /// </summary>
    /// <exception cref="ArgumentException">The query or a site position or weight is not finite, or the span holds more than 2^24 sites.</exception>
    public static CultSiteCells power_cells(float3 p, ReadOnlySpan<float4> sites)
    {
        require_cell_span(p, sites, positiveWeights: false);
        var cells = site_cells_empty();
        for (var i = 0; i < sites.Length; i++)
            cells = site_cells_insert(cells, power_score(p, sites[i]), i);
        return cells;
    }

    /// <summary>
    /// The exact distance from <paramref name="p"/> to the boundary of its power cell, and the unit normal of the
    /// nearest boundary plane pointing into the cell: <c>float4(normal, distance)</c>. The cell is the
    /// intersection of the half-spaces bounded by the radical planes against every other site, so the distance is
    /// the least <see cref="power_border_step"/> over them. A site coincident with the owner has no plane; a cell
    /// with no bounding plane (one site, or only coincident ones) gives <c>(0, 0, 0, 1.0e30)</c>.
    /// </summary>
    /// <exception cref="ArgumentException">As <see cref="power_cells"/>.</exception>
    public static float4 power_border(float3 p, ReadOnlySpan<float4> sites)
    {
        var border = new float4(0.0f, 0.0f, 0.0f, 1.0e30f);
        var owner = (int)power_cells(p, sites).nearestSite;
        if (owner < 0)
            return border;
        for (var k = 0; k < sites.Length; k++)
            if (k != owner)
                border = power_border_step(p, sites[owner], sites[k], border);
        return border;
    }

    /// <summary>
    /// The two nearest sites of <paramref name="p"/> by <see cref="multiplicative_score"/> (distance divided by
    /// weight), every site visited. A site is <c>float4(position.xyz, weight)</c>; use z = 0 for 2D.
    /// </summary>
    /// <exception cref="ArgumentException">The query or a site position is not finite, a weight is not finite and greater than zero, or the span holds more than 2^24 sites.</exception>
    public static CultSiteCells multiplicative_cells(float3 p, ReadOnlySpan<float4> sites)
    {
        require_cell_span(p, sites, positiveWeights: true);
        var cells = site_cells_empty();
        for (var i = 0; i < sites.Length; i++)
            cells = site_cells_insert(cells, multiplicative_score(p, sites[i]), i);
        return cells;
    }

    /// <summary>
    /// Whether sites <paramref name="first"/> and <paramref name="second"/> are neighbours in the Gabriel graph
    /// of <paramref name="sites"/> (positions only; weights are ignored): smoothstep(-softness, softness, the
    /// least <see cref="gabriel_margin"/> over every other site). 1 when no other site exists.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the span.</exception>
    /// <exception cref="ArgumentException">The indices are equal, softness is not finite and greater than zero, or a site position is not finite.</exception>
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

        for (var k = 0; k < sites.Length; k++)
            if (!all_finite(sites[k].xyz))
                throw new ArgumentException("Site positions must be finite.", nameof(sites));

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
