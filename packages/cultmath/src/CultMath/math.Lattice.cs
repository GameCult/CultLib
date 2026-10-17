namespace CultMath;

public static partial class math
{
    // Weighted lattice cells: a jittered, drifting cellular field whose cells are weighted by how close the cell's
    // centre lies to a border of the body diagram (the power diagram of the zone's body span). The public steps are
    // the four per-cell rules a tape or a shader composes; weighted_lattice_cells is the loop over them.

    /// <summary>The greatest boost <see cref="weighted_lattice_cells"/> accepts: weights span 0.5 to 5, a ratio of 10:1.</summary>
    public const float LatticeMaxBoost = 4.0f;

    /// <summary>The greatest stretch <see cref="weighted_lattice_cells"/> accepts.</summary>
    public const float LatticeMaxStretch = 2.0f;

    /// <summary>The greatest smoothing <see cref="weighted_lattice_cells"/> accepts, in cell units.</summary>
    public const float LatticeMaxSmoothing = 1.0f;

    /// <summary>
    /// The outermost ring the search can reach anywhere in the accepted range (see the proof above
    /// <see cref="weighted_lattice_cells"/>).
    /// </summary>
    public const int LatticeMaxRing = 37;

    // A query beyond this many cells from the origin has no integer cell id in float precision.
    private const float LatticeMaxCoordinate = 8388608.0f;

    /// <summary>
    /// The site of a lattice cell: <c>float3(site.xy, base weight)</c>. The site is <c>cell + 0.5 + 0.5 sin(drift
    /// time + 2 pi u)</c> per axis, so it stays within one cell of the cell's corner and drifts at rate
    /// <paramref name="drift"/>. The base weight is <c>0.5 + 0.5 u</c>, in [0.5, 1), from its own hash lane.
    /// </summary>
    public static float3 lattice_site(float2 cell, float time, float drift)
    {
        var h = pcg3d(new int3((int)cell.x, (int)cell.y, 0));
        var phase = new float2(cellular_unit(h.x), cellular_unit(h.y));
        var site = cell + 0.5f + 0.5f * new float2(sin(drift * time + TAU * phase.x), sin(drift * time + TAU * phase.y));
        return new float3(site, 0.5f + 0.5f * cellular_unit(h.z));
    }

    /// <summary>
    /// The weight of a cell: <c>base (1 + boost exp(-(border / falloff)^2))</c>, where <paramref name="border"/> is
    /// the distance from the cell's centre to the nearest body-diagram border.
    /// </summary>
    public static float lattice_weight(float @base, float border, float boost, float falloff)
    {
        var t = border / falloff;
        return @base * (1.0f + boost * exp(-(t * t)));
    }

    /// <summary>
    /// The weighted distance from <paramref name="q"/> to <paramref name="site"/>, in cell units:
    /// <c>weight |(stretch a, c)|</c> with <c>a</c> and <c>c</c> the offset's components along and across the unit
    /// <paramref name="normal"/>. Isotropic at stretch 1.
    /// </summary>
    public static float lattice_distance(float2 q, float2 site, float weight, float2 normal, float stretch)
    {
        var v = site - q;
        var a = dot(v, normal);
        var c = v.x * normal.y - v.y * normal.x;
        return weight * length(new float2(stretch * a, c));
    }

    /// <summary>
    /// One step of the smooth minimum fold: <paramref name="m"/> folded with distance <paramref name="d"/>, for a
    /// nearest distance <paramref name="f1"/>. A distance of at least <c>f1 + 2 smoothing</c> leaves
    /// <paramref name="m"/> unchanged, and the result never exceeds <c>f1 + smoothing</c>. Start the fold at
    /// <c>f1 + smoothing</c> and visit every cell once, in an order that does not depend on the search box.
    /// </summary>
    public static float lattice_smooth_step(float m, float d, float f1, float smoothing)
    {
        if (d >= f1 + 2.0f * smoothing)
            return m;
        var h = smoothstep(0.0f, 1.0f, 0.5f + 0.5f * (m - d) / smoothing);
        var next = m + (d - m) * h - h * (1.0f - h) * smoothing / (1.0f + 3.0f * smoothing);
        return min(next, f1 + smoothing);
    }

    // Proof that the ring search finds every cell that can change an output (ruling lattice-ring-exit-sound).
    //
    // Let c0 = floor(q) and let a cell lie on Chebyshev ring k around c0 (an offset of k on some axis). Its site lies
    // within [cell, cell + 1] per axis and q within [c0, c0 + 1), so on that axis |site - q| >= k - 1. The length of
    // the offset is at least that, the stretch is at least 1 (it only lengthens), and the weight is at least
    // 0.5 (base >= 0.5, boost >= 0). So every cell on ring k or beyond has d >= 0.5 (k - 1), and the same holds for
    // every farther ring.
    //
    // A cell with d >= F2 changes neither F1 nor F2, and a cell with d >= F1 + 2 smoothing leaves the smooth minimum
    // unchanged (lattice_smooth_step returns m before folding it). So the search stops before ring k once
    // 0.5 (k - 1) >= max(F2, F1 + 2 smoothing), with F1 and F2 taken over the rings visited. Those values only fall
    // as rings are added, so the running cutoff is at least the final one: stopping on it never stops early. Every
    // unvisited cell then changes no output, for every weight, stretch and drift.
    //
    // The loop terminates inside the accepted range. Cell c0 gives a distance below sqrt(2) in cell units. The
    // neighbour on the nearer side of the axis whose query fraction sits closest to a cell boundary (fraction m <= 1/2)
    // lies within (1 + m, 1) per axis, below sqrt(3.25). Weights are below 1 + LatticeMaxBoost and stretch is at most
    // LatticeMaxStretch, so F2 < 10 sqrt(3.25) = 18.03 and F1 + 2 smoothing < 10 sqrt(2) + 2 = 16.2. The exit holds at
    // 0.5 (k - 1) >= 18.03, that is at k = 38, so rings 0 to 37 (LatticeMaxRing) are the most visited.
    //
    // The result does not depend on the box visited. The second pass folds F1, F2 and the winning id in absolute
    // row-major order (strict <, so an exact tie keeps the earlier cell), and the smooth minimum starts at
    // F1 + smoothing and is clamped to it, so a cell with d >= F1 + 2 smoothing, visited or not, changes nothing.

    /// <summary>
    /// Weighted cellular noise over a jittered lattice: <c>float4(F1, F2, smooth, id)</c>. F1 and F2 are the two
    /// smallest weighted distances from <paramref name="p"/> in cell units times weight, smooth is the smooth minimum
    /// (<paramref name="smoothing"/>, in the same units), and id is a hash of the winning cell in [0, 1).
    /// </summary>
    /// <remarks>
    /// A cell's weight is <see cref="lattice_weight"/> of the exact power border distance (<see cref="power_border"/>)
    /// of the cell's centre in the body span <paramref name="bodies"/> (<c>float4(position.xyz, sigma)</c>, z = 0), so it is
    /// continuous as the bodies move. Distances are stretched by <paramref name="stretch"/> along
    /// <paramref name="stretchNormal"/> (isotropic at 1). The search is exact in the accepted range: boost 0 to
    /// <see cref="LatticeMaxBoost"/>, stretch 1 to <see cref="LatticeMaxStretch"/>, smoothing up to <see cref="LatticeMaxSmoothing"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">An argument is not finite, a size is not above zero, a value is outside its range, the normal is zero while stretch exceeds 1, or the body span is refused as <see cref="power_cells"/> refuses it.</exception>
    public static float4 weighted_lattice_cells(
        float2 p, float time, ReadOnlySpan<float4> bodies, float cellSize, float drift, float boost, float falloff,
        float smoothing, float2 stretchNormal, float stretch)
    {
        // Cell centres stay finite: the farthest one the search can reach is a ring past the query's cell.
        var reachSpan = cellSize * (LatticeMaxRing + 2);
        if (!float.IsFinite(cellSize) || !(cellSize > 0.0f) || !float.IsFinite(reachSpan))
            throw new ArgumentException("cellSize must be finite and greater than zero, and small enough for its cell centres to be finite.", nameof(cellSize));
        if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !(abs(p.x / cellSize) < LatticeMaxCoordinate) || !(abs(p.y / cellSize) < LatticeMaxCoordinate)
            || !float.IsFinite(abs(p.x) + reachSpan) || !float.IsFinite(abs(p.y) + reachSpan))
            throw new ArgumentException("p must be finite and within 2^23 cells of the origin.", nameof(p));
        if (!float.IsFinite(time))
            throw new ArgumentException("time must be finite.", nameof(time));
        if (!float.IsFinite(drift))
            throw new ArgumentException("drift must be finite.", nameof(drift));
        if (!float.IsFinite(drift * time))
            throw new ArgumentException("drift times time must be finite.", nameof(drift));
        if (!float.IsFinite(boost) || boost < 0.0f || boost > LatticeMaxBoost)
            throw new ArgumentException("boost must be within [0, LatticeMaxBoost].", nameof(boost));
        if (!float.IsFinite(falloff) || !(falloff > 0.0f))
            throw new ArgumentException("falloff must be finite and greater than zero.", nameof(falloff));
        if (!float.IsFinite(smoothing) || !(smoothing > 0.0f) || smoothing > LatticeMaxSmoothing)
            throw new ArgumentException("smoothing must be within (0, LatticeMaxSmoothing].", nameof(smoothing));
        if (!float.IsFinite(stretch) || stretch < 1.0f || stretch > LatticeMaxStretch)
            throw new ArgumentException("stretch must be within [1, LatticeMaxStretch].", nameof(stretch));
        if (!float.IsFinite(stretchNormal.x) || !float.IsFinite(stretchNormal.y))
            throw new ArgumentException("stretchNormal must be finite.", nameof(stretchNormal));
        // Scaled by its largest component first, so a tiny or huge normal normalises to unit length.
        var scale = max(abs(stretchNormal.x), abs(stretchNormal.y));
        if (scale > 0.0f)
        {
            stretchNormal /= scale;
            stretchNormal /= length(stretchNormal);
        }
        else if (stretch > 1.0f)
            throw new ArgumentException("stretchNormal must not be zero while stretch exceeds 1.", nameof(stretchNormal));
        else
            stretchNormal = new float2(1.0f, 0.0f);
        require_cell_span(new float3(p, 0.0f), bodies, positiveWeights: false, nameof(bodies));

        const int Width = 2 * LatticeMaxRing + 1;
        Span<float> cache = stackalloc float[Width * Width];
        var q = p / cellSize;
        var origin = floor(q);
        var cx0 = (int)origin.x;
        var cz0 = (int)origin.y;
        var f1 = 1.0e30f;
        var f2 = 1.0e30f;
        var reach = 0;

        for (var k = 0; ; k++)
        {
            if (k > 1 && 0.5f * (k - 1) >= max(f2, f1 + 2.0f * smoothing))
                break;
            if (k > LatticeMaxRing)
                throw new InvalidOperationException("The lattice ring search left its proven range.");
            for (var dz = -k; dz <= k; dz++)
            {
                var step = dz == -k || dz == k || k == 0 ? 1 : 2 * k;
                for (var dx = -k; dx <= k; dx += step)
                {
                    var d = lattice_cell_distance(q, new float2(cx0 + dx, cz0 + dz), time, drift, bodies, cellSize, boost, falloff, stretchNormal, stretch);
                    cache[(dz + LatticeMaxRing) * Width + dx + LatticeMaxRing] = d;
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
            }
            reach = k;
        }

        var m = f1 + smoothing;
        var best = 1.0e30f;
        var second = 1.0e30f;
        var winDx = 0;
        var winDz = 0;
        for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var d = cache[(dz + LatticeMaxRing) * Width + dx + LatticeMaxRing];
                if (d < best) { second = best; best = d; winDx = dx; winDz = dz; }
                else if (d < second) second = d;
                m = lattice_smooth_step(m, d, f1, smoothing);
            }

        var id = pcg4d(new int4(cx0 + winDx, cz0 + winDz, 0, 0));
        return new float4(best, second, m, cellular_unit(id.w));
    }

    private static float lattice_cell_distance(
        float2 q, float2 cell, float time, float drift, ReadOnlySpan<float4> bodies, float cellSize, float boost, float falloff,
        float2 normal, float stretch)
    {
        var site = lattice_site(cell, time, drift);
        var centre = (cell + 0.5f) * cellSize;
        var border = power_border_unchecked(new float3(centre, 0.0f), bodies).w;
        return lattice_distance(q, site.xy, lattice_weight(site.z, border, boost, falloff), normal, stretch);
    }
}
