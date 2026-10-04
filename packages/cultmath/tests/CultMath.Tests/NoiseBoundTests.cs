using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The noise bounds (design.md, "Intervals" and "Affine forms"): iv_snoise_ball and iv_fbm_ball enclose
/// their functions over a ball, a domain warp bounded by D is enclosed by enlarging the radius by D, or
/// by the centred warp, SNOISE_LIPSCHITZ, SNOISE2_LIPSCHITZ and SNOISE_HESSIAN have measured provenance, iv_frustum_ball encloses a screen tile's rays over a depth segment, the bound
/// composed from an analytic envelope and the noise ball encloses the density, and a tile march over
/// fields with authored empty space is measured against a dense one for the same transmittance. Lines
/// starting IV-REPORT carry the numbers the cut report quotes.
///
/// Hand mutations of iv_frustum_ball that TileBallEnclosesEveryRaySegment must kill: z0 for z1 in the
/// lateral term (z0 * footprintPerDepth); |m_c| for |(m_c, 1)| in the depth term; the warp dropped; the
/// rounding widening dropped (which the degenerate family's enclosure check alone must catch). Hand
/// mutations of the test's own fields and marches: w(z0) for w(z1) in VoidField.WeightBound must fail
/// WeightBoundEnclosesOneSignedNoise; CarveRadius for HollowRadius in the lo test of VoidField.Compose
/// and BodyDepth without |dir| must fail AnalyticBodyMatchesFineMarch; the footprint march's pre-body
/// integration dropped, or LodStep doubled, must fail IntervalSkipHalvesEvaluations's depth agreement.
/// </summary>
public sealed class NoiseBoundTests
{
    private readonly ITestOutputHelper output;

    public NoiseBoundTests(ITestOutputHelper output) => this.output = output;

    private static float Uniform(System.Random random, float lo, float hi) => lo + (hi - lo) * random.NextSingle();

    private static float LogUniform(System.Random random, float lo, float hi) =>
        MathF.Exp(Uniform(random, MathF.Log(lo), MathF.Log(hi)));

    private static float3 UnitVector(System.Random random)
    {
        float3 v;
        do
        {
            v = new float3(Uniform(random, -1.0f, 1.0f), Uniform(random, -1.0f, 1.0f), Uniform(random, -1.0f, 1.0f));
        }
        while (dot(v, v) > 1.0f || dot(v, v) < 1.0e-4f);

        return normalize(v);
    }

    // The first eight points of a ball are on its surface, where the bound is closest to binding; the
    // rest are uniform in its volume.
    private static float3 PointInBall(System.Random random, float3 centre, float radius, int index) =>
        centre + UnitVector(random) * (index < 8 ? radius : radius * MathF.Cbrt(random.NextSingle()));

    private static float3 RandomCentre(System.Random random) =>
        new(Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f));

    private static bool Inside(float value, float2 bound) => value >= bound.x && value <= bound.y;

    [Fact]
    public void SnoiseBallEnclosesPoints()
    {
        var random = new System.Random(0xBA11);
        for (var b = 0; b < 2000; b++)
        {
            var centre = RandomCentre(random);
            var radius = LogUniform(random, 1.0e-3f, 1.0e3f);
            var bound = iv_snoise_ball(centre, radius);
            for (var i = 0; i < 64; i++)
            {
                var x = PointInBall(random, centre, radius, i);
                Assert.True(Inside(snoise(x), bound), $"iv_snoise_ball({centre}, {radius:R}) = {bound} misses snoise({x}) = {snoise(x):R}");
            }
        }
    }

    [Fact]
    public void FbmBallEnclosesPoints()
    {
        var random = new System.Random(0xFB11);
        for (var b = 0; b < 2000; b++)
        {
            var centre = RandomCentre(random);
            var radius = LogUniform(random, 1.0e-3f, 1.0e3f);
            var octaves = random.Next(0, 7);
            var lacunarity = Uniform(random, 1.5f, 2.5f);
            var gain = Uniform(random, 0.3f, 0.7f);
            var bound = iv_fbm_ball(centre, radius, octaves, lacunarity, gain);
            for (var i = 0; i < 64; i++)
            {
                var x = PointInBall(random, centre, radius, i);
                var value = fbm_grad(x, octaves, lacunarity, gain).w;
                Assert.True(Inside(value, bound), $"iv_fbm_ball({centre}, {radius:R}, {octaves}, {lacunarity:R}, {gain:R}) = {bound} misses {value:R} at {x}");
            }
        }
    }

    /// <summary>
    /// The site's march rests on this: a point x in the ball (c, r), displaced by a flow of length at most
    /// D, lies in the ball (c, r + D), so snoise of the warped point lies in iv_snoise_ball(c, r + D). The
    /// geometric check allows one part in 1e5 for the rounding of x + flow.
    /// </summary>
    [Fact]
    public void WarpedPointsStayEnclosed()
    {
        var random = new System.Random(0x3A4B);
        for (var b = 0; b < 2000; b++)
        {
            var centre = RandomCentre(random);
            var radius = LogUniform(random, 1.0e-3f, 2.0f);
            var warp = Uniform(random, 0.0f, 1.0f);
            var bound = iv_snoise_ball(centre, radius + warp);
            for (var i = 0; i < 64; i++)
            {
                var x = PointInBall(random, centre, radius, i);
                var flow = UnitVector(random) * (warp * MathF.Sqrt(random.NextSingle()));
                var warped = x + flow;
                Assert.True(length(warped - centre) <= (radius + warp) * (1.0f + 1.0e-5f), $"warped point {warped} leaves the ball ({centre}, {radius + warp:R})");
                Assert.True(Inside(snoise(warped), bound), $"iv_snoise_ball({centre}, {radius + warp:R}) = {bound} misses snoise({warped}) = {snoise(warped):R}");
            }
        }
    }

    // ---- The tile ball and the composed bound ----

    // One tile case of TileBallEnclosesEveryRaySegment: the r2 domain, or one of the extreme families
    // where float32 rounding of the centre is largest against the radius (Soul's verdict s1).
    internal static (int N, float Focal, float2 Slope, float Z0, float Z1, float Warp) DrawBallCase(System.Random random, string family)
    {
        int[] sizes = { 1, 4, 8, 16 };
        float[] depths = { 12.0f, 1000.0f, 2400.0f };
        var n = sizes[random.Next(sizes.Length)];
        var focal = Uniform(random, 500.0f, 3000.0f);
        var slope = new float2(Uniform(random, -0.77f, 0.77f), Uniform(random, -0.77f, 0.77f));
        var depth = depths[random.Next(depths.Length)];
        var z0 = random.Next(4) == 0 ? 0.0f : Uniform(random, 0.0f, depth * 0.9f);
        var z1 = z0 + LogUniform(random, 1.0e-3f * depth, depth - z0);
        var warp = random.Next(4) == 0 ? 0.0f : LogUniform(random, 1.0e-3f, 60.0f);
        float Signed(float magnitude) => random.Next(2) == 0 ? -magnitude : magnitude;
        switch (family)
        {
            case "degenerate":
                z0 = z1 = LogUniform(random, 1.0e-3f * depth, depth);
                break;
            case "thin":
                z0 = z1 - 1.0e-6f * z1;
                break;
            case "wide slope":
                slope = new float2(Signed(LogUniform(random, 1.0e-3f, 707.0f)), Signed(LogUniform(random, 1.0e-3f, 707.0f)));
                break;
            case "far":
                z1 = LogUniform(random, 1.0e4f, 1.0e7f);
                z0 = z1 - z1 * LogUniform(random, 1.0e-6f, 1.0f);
                break;
            case "from the camera":
                z0 = 0.0f;
                z1 = LogUniform(random, 1.0e-3f, 1.0f);
                break;
            case "tiny focal":
                focal = LogUniform(random, 1.0f, 500.0f);
                break;
            case "huge warp":
                warp = LogUniform(random, 60.0f, 1.0e6f);
                break;
        }

        return (n, focal, slope, z0, z1, warp);
    }

    /// <summary>
    /// iv_frustum_ball encloses every exact point of every ray of an N x N tile over a depth segment,
    /// sub-pixel jitter included, each point then moved by a flow of length at most warp, with no
    /// tolerance: the distance is taken in double from the float32 ball to the point (m z, z) formed in
    /// double from the float32 slope and depth. 2,000 seeded tiles of the r2 domain (N in {1, 4, 8, 16},
    /// f in [500, 3000], |m_c| up to 1.1, segments over the slab's, the fog's and the void's depth ranges,
    /// a quarter starting at the camera) x 64 points, and 2,000 tiles of each extreme family (z0 == z1;
    /// z1 - z0 = 1e-6 z1; |m_c| up to 1000; z up to 1e7; z0 = 0, z1 down to 1e-3; f down to 1; warp up to
    /// 1e6) x the 8 points at the footprint's corners at both depth ends. Every corner point's flow is
    /// full-length and points outward from the ball's centre. It also pins the ball to the derivation in
    /// double, both ways: the centre within 2^-22 |c|_1, and r &lt;= ball.w &lt;= r + 2^-19 (r + |c|_1), so a
    /// ball that forgets its rounding widening fails and one that grows past it fails. Misses and pin
    /// failures are counted per family and reported together. Each tile's camera is drawn on its own, as a
    /// moving camera's frames are: no ball, mask or probe result is carried from one to the next.
    /// </summary>
    [Fact]
    public void TileBallEnclosesEveryRaySegment()
    {
        var random = new System.Random(0x711E);
        string[] families = { "r2 domain", "degenerate", "thin", "wide slope", "far", "from the camera", "tiny focal", "huge warp" };
        var misses = new Dictionary<string, int>();
        var pins = new Dictionary<string, int>();
        var witness = new List<string>();
        foreach (var family in families)
        {
            misses[family] = 0;
            pins[family] = 0;
            for (var t = 0; t < 2000; t++)
            {
                var (n, focal, slope, z0, z1, warp) = DrawBallCase(random, family);
                var half = n / (2.0f * focal);
                var footprint = n / (MathF.Sqrt(2.0f) * focal);
                var ball = iv_frustum_ball(slope, z0, z1, footprint, warp);
                var (cx, cy, cz, w) = ((double)ball.x, (double)ball.y, (double)ball.z, (double)ball.w);

                var zm = (z0 + (double)z1) / 2.0;
                var (ex, ey) = (slope.x * zm, slope.y * zm);
                var c1 = Math.Abs(ex) + Math.Abs(ey) + Math.Abs(zm);
                var radius = (z1 - (double)z0) / 2.0 * Math.Sqrt((double)slope.x * slope.x + (double)slope.y * slope.y + 1.0) + z1 * (double)footprint + warp;
                var centreOff = Math.Abs(cx - ex) + Math.Abs(cy - ey) + Math.Abs(cz - zm);
                if (!(centreOff <= Math.ScaleB(c1, -22) && radius <= w && w <= radius + Math.ScaleB(radius + c1, -19)))
                {
                    pins[family]++;
                    if (witness.Count < 8)
                        witness.Add($"{family} pin: iv_frustum_ball({slope}, {z0:R}, {z1:R}, {footprint:R}, {warp:R}) = {ball}; derivation centre ({ex:R}, {ey:R}, {zm:R}), radius {radius:R}");
                }

                for (var i = 0; i < (family == "r2 domain" ? 64 : 8); i++)
                {
                    var offset = i < 8
                        ? new float2((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half)
                        : new float2(Uniform(random, -half, half), Uniform(random, -half, half));
                    var z = i < 8 ? ((i & 4) == 0 ? z0 : z1) : Uniform(random, z0, z1);
                    var m = slope + offset;
                    var (px, py, pz) = (m.x * (double)z, m.y * (double)z, (double)z);
                    double fx, fy, fz;
                    if (i < 8)
                    {
                        // Outward: along the corner's direction from the centre, full length.
                        var (dx, dy, dz) = (px - cx, py - cy, pz - cz);
                        var away = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        (fx, fy, fz) = away > 0.0 ? (dx / away * warp, dy / away * warp, dz / away * warp) : (warp, 0.0, 0.0);
                    }
                    else
                    {
                        var flow = UnitVector(random) * (warp * MathF.Sqrt(random.NextSingle()));
                        (fx, fy, fz) = (flow.x, flow.y, flow.z);
                    }

                    var (qx, qy, qz) = (px + fx - cx, py + fy - cy, pz + fz - cz);
                    var distance = Math.Sqrt(qx * qx + qy * qy + qz * qz);
                    if (!(distance <= w))
                    {
                        misses[family]++;
                        if (witness.Count < 8)
                            witness.Add($"{family}: iv_frustum_ball({slope}, {z0:R}, {z1:R}, {footprint:R}, {warp:R}) = {ball} misses slope {m}, depth {z:R} by {(distance - w) / w:R} r");
                    }
                }
            }
        }

        Assert.True(misses.Values.Sum() + pins.Values.Sum() == 0,
            "misses " + string.Join(", ", families.Select(f => $"{f} {misses[f]}")) + "; pin failures " + string.Join(", ", families.Select(f => $"{f} {pins[f]}"))
            + Environment.NewLine + string.Join(Environment.NewLine, witness));
    }

    /// <summary>
    /// intervals-enclose for the composition the site and Aetheria use: over 2,000 seeded tile slices
    /// (the deep well, the shallow wells and inside the fog, each under the centred-warp interval bound
    /// and the affine bound; the void at every grid point, the shipped void and LOD-far; N in
    /// {1, 4, 8, 16}; a camera drawn per tile) the bound composed from the envelope over the slice's box,
    /// its fade, and the noise enclosure encloses the density at 64 points of the slice, each sampled
    /// through the field's own warp, with no tolerance. The void's footprint-truncated density (octave weights) is checked
    /// against the same bound; a quarter of the slices are LOD-far's, where the weights are strictly
    /// between 0 and 1 and at 0, and the test asserts it sampled both.
    /// </summary>
    [Fact]
    public void EnvelopeBoundEnclosesDensity()
    {
        var random = new System.Random(0xE7B0);
        int[] sizes = { 1, 4, 8, 16 };
        var fog = new List<Scenario>();
        foreach (var mode in new[] { BoundMode.Interval, BoundMode.Affine })
        {
            fog.Add(FogField.DeepWell(FogField.AetheriaWarp, mode));
            fog.Add(FogField.Wells(FogField.AetheriaWarp, mode));
            fog.Add(FogField.InsideFog(FogField.AetheriaWarp, mode));
        }

        var fields = new List<Scenario>();
        foreach (var rh in VoidField.HollowRadii)
        foreach (var offset in VoidField.CameraOffsets)
        foreach (var ramp in VoidField.Ramps)
            fields.Add(new VoidField(rh, ramp, offset.Fraction, offset.Name));
        fields.Add(VoidField.Shipped());
        fields.Add(VoidField.LodFar());
        var counts = new Counts();
        var (partial, zero) = (0, 0);
        for (var t = 0; t < 2000; t++)
        {
            var field = t % 2 == 0 ? fog[(t / 2) % fog.Count] : t % 4 == 1 ? fields[^1] : fields[random.Next(fields.Count)];
            var tile = field.DrawTile(random, sizes[random.Next(sizes.Length)]);
            var near = field.Grid[0];
            var far = field.Grid[^1];
            var z0 = Uniform(random, near, far * 0.9f);
            var z1 = z0 + LogUniform(random, 1.0e-3f * far, far - z0);
            var bound = field.Bound(tile, z0, z1, gated: false, counts);
            for (var i = 0; i < 64; i++)
            {
                var m = tile.PixelSlope(random.Next(tile.N), random.Next(tile.N), random.NextSingle(), random.NextSingle());
                var z = i < 2 ? (i == 0 ? z0 : z1) : Uniform(random, z0, z1);
                var density = field.Density(tile, m, z, lod: false, counts);
                Assert.True(Inside(density, bound), $"{field.Name}: bound {bound} over [{z0:R}, {z1:R}] misses density {density:R} at slope {m}, depth {z:R}");
                if (field is VoidField voidField)
                {
                    var (wc, wd) = voidField.Weights(z);
                    partial += (wc > 0.0f && wc < 1.0f) || (wd > 0.0f && wd < 1.0f) ? 1 : 0;
                    zero += wd == 0.0f ? 1 : 0;
                    var truncated = field.Density(tile, m, z, lod: true, counts);
                    Assert.True(Inside(truncated, bound), $"{field.Name}: bound {bound} misses the truncated density {truncated:R} at slope {m}, depth {z:R}");
                }
            }
        }

        // The truncated check is not vacuous: some samples carry an octave weight strictly inside (0, 1)
        // and some a fine weight of 0 (LOD-far reaches both).
        Assert.True(partial > 0 && zero > 0, $"octave weights in (0, 1) at {partial} samples and fine weight 0 at {zero}");
    }

    /// <summary>
    /// VoidField's octave weight bound over [z0, z1] encloses each octave's weight at every depth of the
    /// segment, and, for a one-signed noise interval n = [0.25, 1], the weighted noise w(z) n_s lies in
    /// iv_mul(bound, n). 2,000 LOD-far segments with z0 ~ U(1460, 0.95 grid end), z1 ~ U(z0, grid end),
    /// 16 depths each including both ends. A bound whose lower end is w(z0) misses w(z1) 0.25 wherever the
    /// weight falls across the segment.
    /// </summary>
    [Fact]
    public void WeightBoundEnclosesOneSignedNoise()
    {
        var field = VoidField.LodFar();
        var random = new System.Random(0x3B0D);
        var end = field.Grid[^1];
        var n = new float2(0.25f, 1.0f);
        for (var i = 0; i < 2000; i++)
        {
            var z0 = Uniform(random, 1460.0f, 0.95f * end);
            var z1 = Uniform(random, z0, end);
            var (coarseBound, fineBound) = field.WeightBounds(z0, z1);
            for (var s = 0; s < 16; s++)
            {
                var z = s == 0 ? z0 : s == 15 ? z1 : Uniform(random, z0, z1);
                var (coarse, fine) = field.Weights(z);
                foreach (var (octave, w, bound) in new[] { ("coarse", coarse, coarseBound), ("fine", fine, fineBound) })
                {
                    Assert.True(Inside(w, bound), $"{octave} weight {w:R} at depth {z:R} lies outside its bound {bound} over [{z0:R}, {z1:R}]");
                    var weighted = iv_mul(bound, n);
                    foreach (var ns in new[] { 0.25f, Uniform(random, 0.25f, 1.0f), 1.0f })
                        Assert.True(Inside(w * ns, weighted), $"{octave} weight {w:R} x noise {ns:R} at depth {z:R} lies outside iv_mul({bound}, {n}) = {weighted} over [{z0:R}, {z1:R}]");
                }
            }
        }
    }

    // ---- The Lipschitz constant ----

    private static float GradientNorm(float3 p)
    {
        var g = snoise_grad(p);
        return MathF.Sqrt(g.x * g.x + g.y * g.y + g.z * g.z);
    }

    // Hill-climbs |snoise_grad| from p: the ascent direction is the central difference of |grad|, the
    // step grows on success and halves on failure. Returns the largest |grad| it reached.
    private static float AscendGradientNorm(float3 p)
    {
        const float h = 1.0e-3f;
        var step = 1.0e-2f;
        var best = GradientNorm(p);
        for (var k = 0; k < 200 && step > 1.0e-6f; k++)
        {
            var d = new float3(
                GradientNorm(p + new float3(h, 0.0f, 0.0f)) - GradientNorm(p - new float3(h, 0.0f, 0.0f)),
                GradientNorm(p + new float3(0.0f, h, 0.0f)) - GradientNorm(p - new float3(0.0f, h, 0.0f)),
                GradientNorm(p + new float3(0.0f, 0.0f, h)) - GradientNorm(p - new float3(0.0f, 0.0f, h)));
            if (dot(d, d) == 0.0f)
                break;
            var q = p + normalize(d) * step;
            var value = GradientNorm(q);
            if (value > best)
            {
                p = q;
                best = value;
                step *= 1.5f;
            }
            else
            {
                step *= 0.5f;
            }
        }

        return best;
    }

    // Same climb on |snoise|, along the value's own analytic gradient.
    private static float AscendAbsValue(float3 p)
    {
        var step = 1.0e-2f;
        var best = MathF.Abs(snoise(p));
        for (var k = 0; k < 200 && step > 1.0e-6f; k++)
        {
            var g = snoise_grad(p);
            var d = new float3(g.x, g.y, g.z) * (g.w < 0.0f ? -1.0f : 1.0f);
            if (dot(d, d) == 0.0f)
                break;
            var q = p + normalize(d) * step;
            var value = MathF.Abs(snoise(q));
            if (value > best)
            {
                p = q;
                best = value;
                step *= 1.5f;
            }
            else
            {
                step *= 0.5f;
            }
        }

        return best;
    }

    // The largest |snoise_grad| over `samples` seeded points in [-256, 256]^3, the largest after refining
    // the `refine` largest of them by ascent, and the start whose ascent reached it (the witness).
    private static (float Sampled, float Refined, float3 Witness) MeasureGradient(int seed, int samples, int refine)
    {
        var random = new System.Random(seed);
        var points = new float3[samples];
        var norms = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            points[i] = new float3(Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f));
            norms[i] = GradientNorm(points[i]);
        }

        var order = Enumerable.Range(0, samples).OrderByDescending(i => norms[i]).Take(Math.Max(refine, 1)).ToArray();
        var refined = norms[order[0]];
        var witness = points[order[0]];
        foreach (var i in order.Take(refine))
        {
            var value = AscendGradientNorm(points[i]);
            if (value > refined)
            {
                refined = value;
                witness = points[i];
            }
        }

        return (norms[order[0]], refined, witness);
    }

    /// <summary>
    /// The provenance of SNOISE_LIPSCHITZ: the largest |snoise_grad| over 1e6 seeded points, the 1e4
    /// largest refined by gradient ascent, times 1.10. Prints the start whose ascent reached the maximum,
    /// which LipschitzConstantPinsSampledGradients climbs from again. Also prints the largest |snoise|
    /// reached the same way, which is what lets iv_snoise_ball intersect with [-1, 1]. Slow; run
    /// explicitly after any change to the snoise kernel and re-pin the constant and the witness.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Slow")]
    public void MeasureLipschitz()
    {
        var (sampled, refined, witness) = MeasureGradient(0x11B5, 1_000_000, 10_000);
        var random = new System.Random(0xAB5);
        var valueStarts = Enumerable.Range(0, 10_000)
            .Select(_ => new float3(Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f)));
        var maxValue = valueStarts.Max(AscendAbsValue);
        output.WriteLine($"IV-REPORT lipschitz: max |snoise_grad| sampled {sampled:R}, refined {refined:R}, x 1.10 = {refined * 1.10f:R}, witness ({witness.x:R}, {witness.y:R}, {witness.z:R}); max |snoise| refined {maxValue:R}");
        Assert.True(refined <= SNOISE_LIPSCHITZ, $"measured {refined:R} exceeds SNOISE_LIPSCHITZ {SNOISE_LIPSCHITZ:R}");
        Assert.True(maxValue < 1.0f, $"|snoise| reaches {maxValue:R}; iv_snoise_ball may not intersect with [-1, 1]");
    }

    // The start MeasureLipschitz printed as its witness.
    private static readonly float3 LipschitzWitness = new(229.92526f, 73.37927f, -66.14595f);

    /// <summary>
    /// Pins SNOISE_LIPSCHITZ: no |snoise_grad| over 1e5 seeded points exceeds it, and the ascent from
    /// MeasureLipschitz's witness reaches a maximum that, times 1.10, is the constant to within half a
    /// percent, so a constant lowered (or raised) by 1% fails here.
    /// </summary>
    [Fact]
    public void LipschitzConstantPinsSampledGradients()
    {
        var (sampled, _, _) = MeasureGradient(0x11B6, 100_000, 0);
        Assert.True(sampled <= SNOISE_LIPSCHITZ, $"|snoise_grad| reaches {sampled:R}, above SNOISE_LIPSCHITZ {SNOISE_LIPSCHITZ:R}");
        var refined = AscendGradientNorm(LipschitzWitness);
        Assert.True(refined <= SNOISE_LIPSCHITZ, $"|snoise_grad| reaches {refined:R}, above SNOISE_LIPSCHITZ {SNOISE_LIPSCHITZ:R}");
        var ratio = SNOISE_LIPSCHITZ / (1.10f * refined);
        Assert.True(MathF.Abs(ratio - 1.0f) <= 0.005f, $"SNOISE_LIPSCHITZ is {ratio:R} times 1.10 x the re-measured {refined:R}; re-run MeasureLipschitz and re-pin");
    }

    // ---- The curvature and 2D Lipschitz constants (math.Affine.cs, math.Interval.cs) ----

    // Central differences over exact float32 steps: the step actually taken is (p + h) - p in float32, so
    // the quotient is not skewed by the rounding of p + h far from the origin.
    private static double Step(float x, float h, out double back) { back = x - (double)(x - h); return (x + h) - (double)x; }

    // The spectral norm (largest absolute eigenvalue) of the symmetrised Hessian of snoise(float3) at p,
    // from central differences of snoise_grad at h = 1e-3.
    private static double HessianNorm(float3 p)
    {
        const float h = 1.0e-3f;
        var m = new double[3, 3];
        for (var a = 0; a < 3; a++)
        {
            var e = new float3(a == 0 ? h : 0.0f, a == 1 ? h : 0.0f, a == 2 ? h : 0.0f);
            var forward = Step(a == 0 ? p.x : a == 1 ? p.y : p.z, h, out var back);
            var gp = snoise_grad(p + e);
            var gm = snoise_grad(p - e);
            m[0, a] = (gp.x - (double)gm.x) / (forward + back);
            m[1, a] = (gp.y - (double)gm.y) / (forward + back);
            m[2, a] = (gp.z - (double)gm.z) / (forward + back);
        }

        var (a00, a11, a22) = (m[0, 0], m[1, 1], m[2, 2]);
        var (a01, a02, a12) = ((m[0, 1] + m[1, 0]) / 2, (m[0, 2] + m[2, 0]) / 2, (m[1, 2] + m[2, 1]) / 2);
        var p1 = a01 * a01 + a02 * a02 + a12 * a12;
        if (p1 == 0.0)
            return Math.Max(Math.Abs(a00), Math.Max(Math.Abs(a11), Math.Abs(a22)));
        var q = (a00 + a11 + a22) / 3;
        var p2 = (a00 - q) * (a00 - q) + (a11 - q) * (a11 - q) + (a22 - q) * (a22 - q) + 2 * p1;
        var s = Math.Sqrt(p2 / 6);
        var (b00, b11, b22, b01, b02, b12) = ((a00 - q) / s, (a11 - q) / s, (a22 - q) / s, a01 / s, a02 / s, a12 / s);
        var r = Math.Clamp((b00 * (b11 * b22 - b12 * b12) - b01 * (b01 * b22 - b12 * b02) + b02 * (b01 * b12 - b11 * b02)) / 2, -1.0, 1.0);
        var phi = Math.Acos(r) / 3;
        var largest = q + 2 * s * Math.Cos(phi);
        var smallest = q + 2 * s * Math.Cos(phi + 2 * Math.PI / 3);
        return Math.Max(Math.Abs(largest), Math.Abs(smallest));
    }

    // The gradient length of snoise(float2) at p, from central differences at h = 1e-3.
    private static double Gradient2Norm(float2 p)
    {
        const float h = 1.0e-3f;
        var fx = Step(p.x, h, out var bx);
        var fy = Step(p.y, h, out var by);
        var gx = (snoise(p + new float2(h, 0.0f)) - (double)snoise(p - new float2(h, 0.0f))) / (fx + bx);
        var gy = (snoise(p + new float2(0.0f, h)) - (double)snoise(p - new float2(0.0f, h))) / (fy + by);
        return Math.Sqrt(gx * gx + gy * gy);
    }

    // Hill-climbs f from p as AscendGradientNorm does: the ascent direction is f's central difference,
    // the step grows on success and halves on failure. Returns the largest value reached.
    private static double Ascend(Func<float3, double> f, float3 p) => AscendPoint(f, p).Best;

    private static (double Best, float3 At) AscendPoint(Func<float3, double> f, float3 p)
    {
        const float h = 1.0e-3f;
        var step = 1.0e-2f;
        var best = f(p);
        for (var k = 0; k < 200 && step > 1.0e-6f; k++)
        {
            var d = new float3(
                (float)(f(p + new float3(h, 0.0f, 0.0f)) - f(p - new float3(h, 0.0f, 0.0f))),
                (float)(f(p + new float3(0.0f, h, 0.0f)) - f(p - new float3(0.0f, h, 0.0f))),
                (float)(f(p + new float3(0.0f, 0.0f, h)) - f(p - new float3(0.0f, 0.0f, h))));
            if (dot(d, d) == 0.0f)
                break;
            var q = p + normalize(d) * step;
            var value = f(q);
            if (value > best)
            {
                p = q;
                best = value;
                step *= 1.5f;
            }
            else
            {
                step *= 0.5f;
            }
        }

        return (best, p);
    }

    private static double Ascend2(Func<float2, double> f, float2 p) => Ascend(q => f(new float2(q.x, q.y)), new float3(p.x, p.y, 0.0f));

    // The largest f over `samples` seeded points of [-256, 256]^dims, the largest after refining the
    // `refine` largest by ascent, and the start whose ascent reached it (the witness).
    private static (double Sampled, double Refined, float3 Witness) MeasureMax(Func<float3, double> f, bool planar, int seed, int samples, int refine)
    {
        var random = new System.Random(seed);
        var points = new float3[samples];
        var values = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            points[i] = new float3(Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f), planar ? 0.0f : Uniform(random, -256.0f, 256.0f));
            values[i] = f(points[i]);
        }

        var order = Enumerable.Range(0, samples).OrderByDescending(i => values[i]).Take(Math.Max(refine, 1)).ToArray();
        var refined = values[order[0]];
        var witness = points[order[0]];
        foreach (var i in order.Take(refine))
        {
            var value = planar ? Ascend2(q => f(new float3(q.x, q.y, 0.0f)), new float2(points[i].x, points[i].y)) : Ascend(f, points[i]);
            if (value > refined)
            {
                refined = value;
                witness = points[i];
            }
        }

        return (values[order[0]], refined, witness);
    }

    private static double Gradient2At(float3 p) => Gradient2Norm(new float2(p.x, p.y));

    /// <summary>
    /// The provenance of SNOISE_HESSIAN: the largest spectral norm of snoise's Hessian (HessianNorm) over
    /// 1e6 seeded points of [-256, 256]^3, the 1e4 largest refined by ascent, times 1.10. Prints the
    /// start whose ascent reached the maximum, which HessianConstantPinsSampledCurvature climbs from
    /// again. Slow; run explicitly after any change to the snoise kernel and re-pin the constant and the
    /// witness.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Slow")]
    public void MeasureHessian()
    {
        var (sampled, refined, witness) = MeasureMax(HessianNorm, false, 0x4E55, 1_000_000, 10_000);
        output.WriteLine($"IV-REPORT hessian: max |H| sampled {sampled:R}, refined {refined:R}, x 1.10 = {(float)(refined * 1.10):R}, witness ({witness.x:R}, {witness.y:R}, {witness.z:R})");
        Assert.True(refined <= SNOISE_HESSIAN, $"measured {refined:R} exceeds SNOISE_HESSIAN {SNOISE_HESSIAN:R}");
    }

    // The start MeasureHessian printed as its witness.
    private static readonly float3 HessianWitness = new(-245.19885f, -53.01323f, -91.47363f);

    /// <summary>
    /// Pins SNOISE_HESSIAN: no Hessian norm over 1e5 seeded points exceeds it, and the ascent from
    /// MeasureHessian's witness reaches a maximum that, times 1.10, is the constant to within half a
    /// percent, so a constant moved by 1% fails here.
    /// </summary>
    [Fact]
    public void HessianConstantPinsSampledCurvature()
    {
        var (sampled, _, _) = MeasureMax(HessianNorm, false, 0x4E56, 100_000, 0);
        Assert.True(sampled <= SNOISE_HESSIAN, $"the Hessian norm reaches {sampled:R}, above SNOISE_HESSIAN {SNOISE_HESSIAN:R}");
        var refined = Ascend(HessianNorm, HessianWitness);
        Assert.True(refined <= SNOISE_HESSIAN, $"the Hessian norm reaches {refined:R}, above SNOISE_HESSIAN {SNOISE_HESSIAN:R}");
        var ratio = SNOISE_HESSIAN / (1.10 * refined);
        Assert.True(Math.Abs(ratio - 1.0) <= 0.005, $"SNOISE_HESSIAN is {ratio:R} times 1.10 x the re-measured {refined:R}; re-run MeasureHessian and re-pin");
    }

    /// <summary>
    /// SNOISE_HESSIAN where the curvature is realised. At the point the ascent from MeasureHessian's witness
    /// reaches, the absolute second difference of snoise along 4,000 seeded directions u at step 0.05 has a
    /// largest value within 10% of SNOISE_HESSIAN / 1.10 (the realised curvature is there, so the check
    /// has teeth), and af_snoise(witness, r u, 0) encloses snoise(witness + eps r u) at eps in
    /// {-1, -1/2, 0, 1/2, 1} for r in {0.1, 0.3} and every one of those directions, no tolerance: a constant
    /// under the realised curvature (40 against about 50.6) misses on the directions near the top
    /// eigenvector. The enclosure tests on random balls cannot see a constant 20% low; this and
    /// HessianConstantPinsSampledCurvature are its defence.
    /// </summary>
    [Fact]
    public void SnoiseFormEnclosesTheCurvatureWitness()
    {
        var (_, witness) = AscendPoint(HessianNorm, HessianWitness);
        var random = new System.Random(0xCA7E);
        var directions = Enumerable.Range(0, 4000).Select(_ => UnitVector(random)).ToArray();
        const float step = 0.05f;
        var centreValue = snoise(witness);
        var realised = directions.Max(u => Math.Abs(snoise(witness + u * step) + (double)snoise(witness - u * step) - 2.0 * centreValue) / ((double)step * step));
        output.WriteLine($"IV-REPORT curvature witness ({witness.x:R}, {witness.y:R}, {witness.z:R}): largest second difference {realised:R} against SNOISE_HESSIAN {SNOISE_HESSIAN:R}");
        Assert.True(realised >= 0.9 * SNOISE_HESSIAN / 1.10, $"the largest second difference at the witness is {realised:R}, under 0.9 SNOISE_HESSIAN / 1.10 = {0.9 * SNOISE_HESSIAN / 1.10:R}: the check has no teeth, or the constant moved");
        foreach (var u in directions)
        foreach (var r in new[] { 0.1f, 0.3f })
        {
            var form = af_snoise(witness, u * r, 0.0f);
            foreach (var eps in new[] { -1.0f, -0.5f, 0.0f, 0.5f, 1.0f })
            {
                var value = snoise(witness + u * (r * eps));
                Assert.True(Math.Abs(value - (form.x + (double)form.y * eps)) <= form.z, $"af_snoise(witness, {u * r}, 0) = {form} misses snoise = {value:R} at eps {eps:R}, direction {u}");
            }
        }
    }

    /// <summary>
    /// The provenance of SNOISE2_LIPSCHITZ: the largest gradient length of snoise(float2) (Gradient2Norm)
    /// over 1e6 seeded points of [-256, 256]^2, the 1e4 largest refined by ascent, times 1.10; and the
    /// largest |snoise(float2)| reached by ascent from 1e4 starts, which is what lets iv_snoise_ball(float2)
    /// intersect with [-1, 1]. Slow; run explicitly after any change to the 2D kernel and re-pin.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Slow")]
    public void MeasureLipschitz2()
    {
        var (sampled, refined, witness) = MeasureMax(Gradient2At, true, 0x2D15, 1_000_000, 10_000);
        var random = new System.Random(0x2D16);
        var maxValue = Enumerable.Range(0, 10_000)
            .Select(_ => new float2(Uniform(random, -256.0f, 256.0f), Uniform(random, -256.0f, 256.0f)))
            .Max(p => Ascend2(q => Math.Abs(snoise(q)), p));
        output.WriteLine($"IV-REPORT lipschitz2: max |grad snoise2| sampled {sampled:R}, refined {refined:R}, x 1.10 = {(float)(refined * 1.10):R}, witness ({witness.x:R}, {witness.y:R}); max |snoise2| refined {maxValue:R}");
        Assert.True(refined <= SNOISE2_LIPSCHITZ, $"measured {refined:R} exceeds SNOISE2_LIPSCHITZ {SNOISE2_LIPSCHITZ:R}");
        Assert.True(maxValue < 1.0, $"|snoise2| reaches {maxValue:R}; iv_snoise_ball(float2) may not intersect with [-1, 1]");
    }

    // The start MeasureLipschitz2 printed as its witness.
    private static readonly float2 Lipschitz2Witness = new(-67.12454f, -129.53296f);

    /// <summary>
    /// Pins SNOISE2_LIPSCHITZ as LipschitzConstantPinsSampledGradients pins SNOISE_LIPSCHITZ: 1e5 seeded
    /// points under it, and the ascent from MeasureLipschitz2's witness within half a percent of it / 1.10.
    /// </summary>
    [Fact]
    public void Lipschitz2ConstantPins()
    {
        var (sampled, _, _) = MeasureMax(Gradient2At, true, 0x2D17, 100_000, 0);
        Assert.True(sampled <= SNOISE2_LIPSCHITZ, $"|grad snoise2| reaches {sampled:R}, above SNOISE2_LIPSCHITZ {SNOISE2_LIPSCHITZ:R}");
        var refined = Ascend2(Gradient2Norm, Lipschitz2Witness);
        Assert.True(refined <= SNOISE2_LIPSCHITZ, $"|grad snoise2| reaches {refined:R}, above SNOISE2_LIPSCHITZ {SNOISE2_LIPSCHITZ:R}");
        var ratio = SNOISE2_LIPSCHITZ / (1.10 * refined);
        Assert.True(Math.Abs(ratio - 1.0) <= 0.005, $"SNOISE2_LIPSCHITZ is {ratio:R} times 1.10 x the re-measured {refined:R}; re-run MeasureLipschitz2 and re-pin");
    }

    /// <summary>
    /// iv_snoise_ball(float2) encloses snoise(float2) over 2,000 seeded discs of radius log-uniform in
    /// [1e-3, 1e3] x 64 points (the first eight on the rim), no tolerance.
    /// </summary>
    [Fact]
    public void Snoise2BallEnclosesPoints()
    {
        var random = new System.Random(0xBA12);
        for (var b = 0; b < 2000; b++)
        {
            var centre = new float2(Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f));
            var radius = LogUniform(random, 1.0e-3f, 1.0e3f);
            var bound = iv_snoise_ball(centre, radius);
            for (var i = 0; i < 64; i++)
            {
                var angle = Uniform(random, 0.0f, 2.0f * MathF.PI);
                var x = centre + new float2(MathF.Cos(angle), MathF.Sin(angle)) * (i < 8 ? radius : radius * MathF.Sqrt(random.NextSingle()));
                Assert.True(Inside(snoise(x), bound), $"iv_snoise_ball({centre}, {radius:R}) = {bound} misses snoise({x}) = {snoise(x):R}");
            }
        }
    }

    /// <summary>
    /// The centred warp (design.md, "Affine forms"): over 2,000 seeded balls of the deep well's flow (warp
    /// D = 60, period 8, flow frequency 1/512; centres in [-2000, 2000]^3, radius log-uniform in [1e-3, 500],
    /// a time per ball) x 64 points, each phase's warped point p + flow(p) shift_k lies in the ball
    /// (c + flow(c) shift_k, r + |shift_k| rho), r the radius widened by (|c|_1 + r) 2^-20 as iv_frustum_ball
    /// widens its ball (the float32 point p is inside only up to that, which matters below a radius of 1),
    /// geometry allowing one part in 1e5 for the rounding of the warped point, as WarpedPointsStayEnclosed does, and snoise of it, at the coverage frequency, lies
    /// in iv_snoise_ball of that ball with no tolerance. Dropping rho fails: some warped points lie
    /// farther than r from the moved centre.
    /// </summary>
    [Fact]
    public void CentredWarpStaysEnclosed()
    {
        var noise = new WarpedNoise { F0 = 1.0f / FogField.NoiseScale, Warp = FogField.AetheriaWarp };
        var random = new System.Random(0xCE17);
        var beyond = 0;
        for (var b = 0; b < 2000; b++)
        {
            var centre = new float3(Uniform(random, -2000.0f, 2000.0f), Uniform(random, -2000.0f, 2000.0f), Uniform(random, -2000.0f, 2000.0f));
            var radius = LogUniform(random, 1.0e-3f, 500.0f);
            var reach = radius + (abs(centre.x) + abs(centre.y) + abs(centre.z) + radius) * 9.5367431640625e-7f;
            var time = Uniform(random, 0.0f, 100.0f);
            var (_, shift0, shift1) = noise.Phases(time);
            var (c0, g0, c1, g1) = noise.CentredWarp(centre, reach, time);
            for (var i = 0; i < 64; i++)
            {
                var p = PointInBall(random, centre, radius, i);
                var flow = noise.Flow(p);
                foreach (var (shift, moved, grow) in new[] { (shift0, c0, g0), (shift1, c1, g1) })
                {
                    var warped = p + flow * shift;
                    var distance = length(warped - moved);
                    Assert.True(distance <= (reach + grow) * (1.0f + 1.0e-5f), $"warped point {warped} leaves the centred ball ({moved}, {reach + grow:R})");
                    beyond += distance > radius ? 1 : 0;
                    var bound = iv_snoise_ball(moved * noise.F0, (reach + grow) * noise.F0);
                    var value = snoise(warped * noise.F0);
                    Assert.True(Inside(value, bound), $"iv_snoise_ball over the centred ball ({moved}, {reach + grow:R}) = {bound} misses snoise = {value:R} at {warped}");
                }
            }
        }

        output.WriteLine($"IV-REPORT centred warp: {beyond} of {2000 * 64 * 2} warped points lie beyond r from the moved centre");
        Assert.True(beyond > 0, "no warped point needs rho: the check cannot tell a centred warp without it");
    }

    /// <summary>
    /// FlowVariation's width, pinned both ways: over 2,000 seeded balls (centres in [-2000, 2000]^3, radius
    /// log-uniform in [1e-3, 500]) it equals min(sqrt(u^2 + v^2), 2) A, A = FlowAmplitude, with each of u and v
    /// the reach of iv_snoise_ball(float2) at the flow plane's argument (plus its offset) plus the float32
    /// allowance 2^-14 + L2 2^-20 (|q|_1 + r FlowFrequency), to 0.3% (the float32 sum n + e rounds at 6e-8, against an allowance of 6e-5). The enclosure test sees the
    /// allowance only where a float32 flow differs from the exact one by more than the bound's slack, which
    /// on the CPU is rare (CentredWarpStaysEnclosed); this is the test that sees it.
    /// </summary>
    [Fact]
    public void FlowVariationCarriesItsAllowance()
    {
        var noise = new WarpedNoise { F0 = 1.0f / FogField.NoiseScale, Warp = FogField.AetheriaWarp };
        var amplitude = noise.Warp * 2.0 / noise.Period;
        var random = new System.Random(0xF10E);
        double Reach(float2 q, double r)
        {
            var n = snoise(q);
            var e = SNOISE2_LIPSCHITZ * r;
            return Math.Max(Math.Min(e, 1.0 - n), Math.Min(e, n + 1.0));
        }

        for (var b = 0; b < 2000; b++)
        {
            var centre = new float3(Uniform(random, -2000.0f, 2000.0f), Uniform(random, -2000.0f, 2000.0f), Uniform(random, -2000.0f, 2000.0f));
            var radius = LogUniform(random, 1.0e-3f, 500.0f);
            var rq = (double)radius * noise.FlowFrequency;
            double Component(float2 offset)
            {
                var q = new float2(centre.x, centre.z) * noise.FlowFrequency + offset;
                return Reach(q, rq) + Math.ScaleB(1.0, -14) + SNOISE2_LIPSCHITZ * Math.ScaleB(Math.Abs((double)q.x) + Math.Abs((double)q.y) + rq, -20);
            }

            var (u, v) = (Component(new float2(17.0f, 3.0f)), Component(new float2(-5.0f, 41.0f)));
            var expected = Math.Min(Math.Sqrt(u * u + v * v), 2.0) * amplitude;
            var observed = noise.FlowVariation(centre, radius);
            Assert.True(Math.Abs(observed - expected) <= 3.0e-3 * expected, $"FlowVariation({centre}, {radius:R}) = {observed:R}, the documented rule gives {expected:R}");
        }
    }

    /// <summary>
    /// The centred warp over an affine slice (FogField.Slice, the owner AffineBound reads): over 3,000
    /// seeded tile slices (the deep well, the shallow wells and inside the fog; N in {1, 4, 8, 16}; a
    /// slice of length log-uniform in [0.5, 1500] from a drawn depth, so the axis is from far under to far
    /// over the ball's radius) x 64 rays each (the first two at the slice's ends), each phase's warped ray
    /// point p + flow(p) shift_k lies within radius + growth_k of moved_k + axis eps(z), eps(z) =
    /// clamp((z - z_m) / h, -1, 1), no tolerance but one part in 1e5 for the rounding of the warped point.
    /// The check sees each term of the slice: a reach of the ball's radius instead of |axis| + radius, a
    /// phase's growth dropped, or rho halved leaves rays outside; the report counts the rays that need
    /// each phase's growth, and the test asserts both phases have some.
    /// </summary>
    [Fact]
    public void AffineWarpStaysEnclosed()
    {
        var random = new System.Random(0xAF77);
        int[] sizes = { 1, 4, 8, 16 };
        var fields = new[]
        {
            FogField.DeepWell(FogField.AetheriaWarp, BoundMode.Affine), FogField.Wells(FogField.AetheriaWarp, BoundMode.Affine), FogField.InsideFog(FogField.AetheriaWarp, BoundMode.Affine),
        };
        var needing = new[] { 0, 0 };
        for (var t = 0; t < 3000; t++)
        {
            var field = fields[t % fields.Length];
            var tile = field.DrawTile(random, sizes[random.Next(sizes.Length)]);
            var z0 = Uniform(random, field.Grid[0], field.Grid[^1] * 0.9f);
            var z1 = MathF.Min(z0 + LogUniform(random, 0.5f, 1500.0f), field.Grid[^1]);
            var slice = field.Slice(tile, z0, z1);
            var (_, shift0, shift1) = field.Noise.Phases(tile.Time);
            var (zm, h) = (slice.Ball.z, slice.AxisCamera.z);

            // The arguments are built from the centred warp of the slice's own centre and reach, the detail
            // from the unwarped set: no phase takes another's growth, nor the detail a warp.
            var (moved0, grow0, moved1, grow1) = field.Noise.CentredWarp(slice.Centre, slice.Reach, tile.Time);
            var (coverageFrequency, detailFrequency) = (field.Noise.F0, 4.0f * field.Noise.F0);
            var expected = new[]
            {
                (slice.Coverage0, moved0 * coverageFrequency, (slice.Ball.w + grow0) * coverageFrequency),
                (slice.Coverage1, moved1 * coverageFrequency, (slice.Ball.w + grow1) * coverageFrequency),
                (slice.Detail, slice.Centre * detailFrequency + 17.0f, slice.Ball.w * detailFrequency),
            };
            foreach (var (arg, centre, radius) in expected)
            {
                Assert.True(length(arg.Centre - centre) <= 1.0e-6f * (1.0f + length(centre)) && Math.Abs(arg.Radius - radius) <= 1.0e-6f * radius, $"{field.Name}: argument ({arg.Centre}, {arg.Radius:R}) is not the documented ({centre}, {radius:R})");
            }

            var axisTolerance = 1.0e-6f * length(slice.Coverage0.Axis);
            Assert.True(length(slice.Coverage1.Axis - slice.Coverage0.Axis) <= axisTolerance && length(slice.Detail.Axis - slice.Coverage0.Axis * (detailFrequency / coverageFrequency)) <= 4.0f * axisTolerance, $"{field.Name}: the arguments do not share one axis");

            for (var i = 0; i < 64; i++)
            {
                var m = tile.PixelSlope(random.Next(tile.N), random.Next(tile.N), random.NextSingle(), random.NextSingle());
                var z = i < 2 ? (i == 0 ? z0 : z1) : Uniform(random, z0, z1);
                var p = tile.World(new float3(m.x * z, m.y * z, z));
                var flow = field.Noise.Flow(p);
                var eps = h == 0.0f ? 0.0f : clamp((z - zm) / h, -1.0f, 1.0f);
                var phases = new[] { (shift0, slice.Coverage0, coverageFrequency), (shift1, slice.Coverage1, coverageFrequency) };
                for (var k = 0; k < 2; k++)
                {
                    var (shift, arg, frequency) = phases[k];
                    var point = (p + flow * shift) * frequency;
                    var distance = length(point - (arg.Centre + arg.Axis * eps));
                    Assert.True(distance <= arg.Radius * (1.0f + 1.0e-5f) + 1.0e-6f * length(point), $"{field.Name}: phase {k} warped ray point at depth {z:R} is {distance:R} from the argument's centre + axis eps, outside radius {arg.Radius:R} (reach {slice.Reach:R}, slice [{z0:R}, {z1:R}])");
                    needing[k] += distance > slice.Ball.w * frequency ? 1 : 0;
                }

                var detail = p * detailFrequency + 17.0f;
                var detailDistance = length(detail - (slice.Detail.Centre + slice.Detail.Axis * eps));
                Assert.True(detailDistance <= slice.Detail.Radius * (1.0f + 1.0e-5f) + 1.0e-6f * length(detail), $"{field.Name}: the detail ray point at depth {z:R} is {detailDistance:R} from the argument's centre + axis eps, outside radius {slice.Detail.Radius:R} (slice [{z0:R}, {z1:R}])");
            }
        }

        output.WriteLine($"IV-REPORT affine warp: rays beyond the unwarped radius, phase 0 {needing[0]}, phase 1 {needing[1]}");
        Assert.True(needing[0] > 0 && needing[1] > 0, $"no ray needs phase growth ({needing[0]}, {needing[1]}): the check cannot tell a slice without it");
    }

    /// <summary>
    /// The affine composition earns its place: over seeded slices of the deep well and the shallow wells
    /// (warp 0 and the Aetheria warp, N = 8, slices of length log-uniform in [0.5, 100] from a drawn
    /// depth), AffineBound is exactly the intersection of its two sound pieces (the affine arithmetic's
    /// own, and the interval composition over the forms' ranges), and where the interval piece does not
    /// already prove the slice empty the affine piece is the narrower of the two in at least
    /// MinAffineWins of the slices and narrower in total width by at least MinWidthGain. The
    /// cost contract (IntervalSkipHalvesEvaluations) sees only the sum, in which af_snoise's Hessian forms
    /// already beat iv_snoise_ball; this is the test that sees af_add, af_mul and af_range, the shared
    /// symbol, break.
    /// </summary>
    [Fact]
    public void AffineCompositionIsTighterThanItsRanges()
    {
        var random = new System.Random(0xAC0F);
        var (open, wins, affineWidth, intervalWidth) = (0, 0, 0.0, 0.0);
        foreach (var warp in new[] { 0.0f, FogField.AetheriaWarp })
        foreach (var field in new[] { FogField.DeepWell(warp, BoundMode.Affine), FogField.Wells(warp, BoundMode.Affine) })
        for (var t = 0; t < 1500; t++)
        {
            var tile = field.DrawTile(random, 8);
            var z0 = Uniform(random, field.Grid[0], field.Grid[^1] * 0.9f);
            var z1 = MathF.Min(z0 + LogUniform(random, 0.5f, 100.0f), field.Grid[^1]);
            var (affine, interval) = field.AffinePieces(tile, z0, z1, new Counts());
            var bound = field.Bound(tile, z0, z1, false, new Counts());
            Assert.True(bound.x == max(affine.x, interval.x) && bound.y == min(affine.y, interval.y), $"{field.Name}: Bound {bound} is not the intersection of the affine piece {affine} and the interval piece {interval}");
            if (!(interval.y > 0.0f))
                continue;
            open++;
            wins += affine.y - affine.x < interval.y - interval.x ? 1 : 0;
            affineWidth += affine.y - affine.x;
            intervalWidth += interval.y - interval.x;
        }

        output.WriteLine($"IV-REPORT affine composition: {open} open slices, affine narrower in {wins}, total width {affineWidth:F1} against {intervalWidth:F1} ({affineWidth / intervalWidth:F3})");
        Assert.True(open > 0, "no slice is open");
    }

    // Why a field is not ten bands deep and wide, or null. The band G is the span of s where only the
    // noise decides density: s in [F + A lo, F + A hi] for the free noise range [lo, hi], with the fade at 1.
    private static string? TenBandsShortfall(FogField field, int cameras)
    {
        var band = FogField.Amplitude * (FogField.FreeNoise.y - FogField.FreeNoise.x);
        var well = field.Bowls[^1];
        var rim = well.Scale / 2.0f;
        float At(float r) => field.HeightAt(well.Centre + new float2(r, 0.0f));
        var depth = At(0.0f) - At(rim);
        var half = (At(0.0f) + At(rim)) / 2.0f;
        float lo = 0.0f, hi = rim;
        for (var k = 0; k < 60; k++)
        {
            var mid = (lo + hi) / 2.0f;
            if (At(mid) > half)
                lo = mid;
            else
                hi = mid;
        }

        var problems = new List<string>();
        if (depth < 10.0f * band)
            problems.Add($"depth {depth:R} is under 10 G = {10.0f * band:R}");
        if (lo < 10.0f * band)
            problems.Add($"half-depth radius {lo:R} is under 10 G = {10.0f * band:R}");
        var random = new System.Random(0xD33B);
        for (var c = 0; c < cameras; c++)
        {
            var origin = field.DrawTile(random, 8).Origin;
            var xz = new float2(origin.x, origin.z);
            var s = origin.y + field.HeightAt(xz);
            if (!(length(xz - well.Centre) < lo && origin.y < FogField.FloorOffset - At(rim) && s >= FogField.FloorOffset + FogField.Amplitude * FogField.FreeNoise.y))
            {
                problems.Add($"camera {origin} is not inside the well, below the rim's fog surface, in empty air");
                break;
            }
        }

        return problems.Count == 0 ? null : $"{field.Name}: G = {band:R}; " + string.Join("; ", problems);
    }

    /// <summary>
    /// Ruling wells-scale-order-of-magnitude: the band G is computed from FogField's FloorOffset, Amplitude
    /// and free noise range (60). The deep well's depth, h(centre) - h(rim), and its half-depth radius,
    /// found by bisection on the well map, are each at least 10 G, and each of 1,000 drawn cameras sits
    /// inside that radius, below the rim's fog surface and in empty air (s above the band). A well of depth
    /// 150 fails the same check.
    /// </summary>
    [Fact]
    public void DeepWellIsTenBandsDeepAndWide()
    {
        var deep = FogField.DeepWell(FogField.AetheriaWarp, BoundMode.Interval);
        Assert.Null(TenBandsShortfall(deep, 1000));
        Assert.NotNull(TenBandsShortfall(FogField.DeepWell(FogField.AetheriaWarp, BoundMode.Interval, depth: 150.0f), 1000));
        var band = FogField.Amplitude * (FogField.FreeNoise.y - FogField.FreeNoise.x);
        output.WriteLine($"IV-REPORT deep well: G = {band:R}, depth {deep.HeightAt(new float2(0.0f, 0.0f)) - deep.HeightAt(new float2(FogField.DeepWellScale / 2.0f, 0.0f)):R}");
    }

    // ---- The cull map ----

    // An approximation of viridis at nine anchors, interpolated linearly, for t in [0, 1].
    private static readonly (byte R, byte G, byte B)[] Viridis =
    {
        (68, 1, 84), (72, 40, 120), (62, 74, 137), (49, 104, 142), (38, 130, 142), (31, 158, 137), (53, 183, 121), (110, 206, 88), (253, 231, 37),
    };

    private static (byte, byte, byte) Colour(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0) * (Viridis.Length - 1);
        var i = Math.Min((int)t, Viridis.Length - 2);
        var f = t - i;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * f);
        return (Mix(Viridis[i].R, Viridis[i + 1].R), Mix(Viridis[i].G, Viridis[i + 1].G), Mix(Viridis[i].B, Viridis[i + 1].B));
    }

    private static uint[] CrcTable() => Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    // A minimal 8-bit RGB PNG: one IDAT of zlib-compressed rows, filter 0.
    private static void WritePng(string path, int width, int height, byte[] rgb)
    {
        var table = CrcTable();
        using var file = File.Create(path);
        void Chunk(string type, byte[] data)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(type);
            var length = new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
            file.Write(length);
            file.Write(name);
            file.Write(data);
            var crc = 0xFFFFFFFFu;
            foreach (var b in name.Concat(data))
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            crc ^= 0xFFFFFFFFu;
            file.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        }

        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", new byte[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 2, 0, 0, 0 });
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }

        Chunk("IDAT", packed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    /// <summary>
    /// The cull map of one deep-well frame at warp D, for the operator: a camera inside the well at xz
    /// (0, -200), y = F - h / 2, looking along +z across the well, 960 x 540 pixels at a 60 degree vertical
    /// field of view, in 8 x 8 tiles (the last row of tiles half off the frame). Each tile runs the
    /// pre-pass under the centred-warp interval bound and under the affine bound; each pixel's ray (through
    /// the pixel's centre) is marched densely for its transmittance and over each tile mask and the oracle
    /// mask (the cells some ray of the tile samples nonzero), with the early-out at 0.02. The PNG is
    /// 1920 x 1080 in four panels: top left the cells per ray the interval march integrates, top right the
    /// affine march's, bottom left the oracle march's (all three on one viridis scale, logarithmic,
    /// log(1 + n) / log(1 + the largest count)), bottom right the transmittance (black opaque, white clear). It is written only when
    /// CULTMATH_WRITE_CULLMAP names a directory; the mean cells per ray are printed either way.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Slow")]
    public void DeepWellCullMap()
    {
        const int width = 960, height = 540, size = 8;
        var interval = FogField.DeepWell(FogField.AetheriaWarp, BoundMode.Interval);
        var affine = FogField.DeepWell(FogField.AetheriaWarp, BoundMode.Affine);
        var focal = height / 2.0f / MathF.Tan(MathF.PI / 6.0f);
        var xz = new float2(0.0f, -200.0f);
        var origin = new float3(xz.x, FogField.FloorOffset - 0.5f * interval.HeightAt(xz), xz.y);
        var grid = interval.Grid;
        var (cellsInterval, cellsAffine, cellsOracle) = (new int[width * height], new int[width * height], new int[width * height]);
        var transmittance = new float[width * height];
        var (tilesX, tilesY) = (width / size, (height + size - 1) / size);
        var probes = new Counts[tilesX * tilesY];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, tilesX * tilesY, index =>
        {
            var (tx, ty) = (index % tilesX, index / tilesX);
            var slope = new float2((tx * size + size / 2.0f - width / 2.0f) / focal, (height / 2.0f - (ty * size + size / 2.0f)) / focal);
            var tile = interval.TileAt(origin, 0.0f, slope, size, focal, 3.0f);
            probes[index] = new Counts();
            var (maskInterval, _, _) = MarchTile(interval, tile, probes[index]);
            var (maskAffine, _, _) = MarchTile(affine, tile, new Counts());
            var scratch = new Counts();
            var rays = new List<(int Pixel, float2 Slope)>();
            for (var j = 0; j < size; j++)
            for (var i = 0; i < size; i++)
            {
                var (px, py) = (tx * size + i, ty * size + (size - 1 - j));
                rays.Add((py < height ? py * width + px : -1, tile.PixelSlope(i, j, 0.5f, 0.5f)));
            }

            var oracle = Enumerable.Range(0, grid.Length - 1).Where(c => rays.Any(r => interval.Density(tile, r.Slope, (grid[c] + grid[c + 1]) * 0.5f, false, scratch) > 0.0f)).ToList();
            foreach (var (pixel, ray) in rays)
            {
                if (pixel < 0)
                    continue;
                int Steps(List<int> mask)
                {
                    var t = 1.0f;
                    var k = 0;
                    for (; k < mask.Count && t >= 0.02f; k++)
                        t *= Integrate(interval, tile, ray, grid[mask[k]], grid[mask[k] + 1], lod: false, scratch);
                    return k;
                }

                var dense = 1.0f;
                for (var c = 0; c < grid.Length - 1 && dense >= 0.02f; c++)
                    dense *= Integrate(interval, tile, ray, grid[c], grid[c + 1], lod: false, scratch);
                transmittance[pixel] = dense;
                cellsInterval[pixel] = Steps(maskInterval);
                cellsAffine[pixel] = Steps(maskAffine);
                cellsOracle[pixel] = Steps(oracle);
            }
        });

        var most = Math.Max(cellsInterval.Max(), Math.Max(cellsAffine.Max(), cellsOracle.Max()));
        output.WriteLine($"IV-REPORT cull map (deep well, warp {FogField.AetheriaWarp:R}, {width}x{height}, {size}x{size} tiles, {clock.Elapsed.TotalSeconds:F0} s): cells per ray interval {cellsInterval.Average():F2}, affine {cellsAffine.Average():F2}, oracle {cellsOracle.Average():F2}; "
            + $"largest {most}; interval probes per tile env {probes.Average(p => p.Envelope):F1} snoise {probes.Average(p => p.Snoise):F2}; mean transmittance {transmittance.Average():F3}");
        var directory = Environment.GetEnvironmentVariable("CULTMATH_WRITE_CULLMAP");
        if (string.IsNullOrEmpty(directory))
            return;
        var rgb = new byte[2 * width * 2 * height * 3];
        void Put(int x, int y, (byte R, byte G, byte B) c)
        {
            var o = (y * 2 * width + x) * 3;
            (rgb[o], rgb[o + 1], rgb[o + 2]) = c;
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = y * width + x;
            Put(x, y, Colour(Math.Log(1.0 + cellsInterval[p]) / Math.Log(1.0 + most)));
            Put(width + x, y, Colour(Math.Log(1.0 + cellsAffine[p]) / Math.Log(1.0 + most)));
            Put(x, height + y, Colour(Math.Log(1.0 + cellsOracle[p]) / Math.Log(1.0 + most)));
            var grey = (byte)Math.Round(255.0 * transmittance[p]);
            Put(width + x, height + y, (grey, grey, grey));
        }

        for (var k = 0; k < 2 * width; k++)
        for (var d = -1; d <= 0; d++)
            Put(k, height + d, (255, 255, 255));
        for (var k = 0; k < 2 * height; k++)
        for (var d = -1; d <= 0; d++)
            Put(width + d, k, (255, 255, 255));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deep-well-cull-map.png");
        WritePng(path, 2 * width, 2 * height, rgb);
        output.WriteLine($"IV-REPORT cull map written to {path}");
    }

    /// <summary>
    /// Mean of interval width over the true range (max - min of the 64 sampled values) per ball: how
    /// loose the Lipschitz bound is, for a later affine cut to beat. Asserts only finiteness.
    /// </summary>
    [Fact]
    public void TightnessReport()
    {
        double Ratio(Func<float3, float, float2> bound, Func<float3, float> f, float radius, int seed)
        {
            var random = new System.Random(seed);
            var sum = 0.0;
            for (var b = 0; b < 2000; b++)
            {
                var centre = RandomCentre(random);
                var interval = bound(centre, radius);
                float lo = float.MaxValue, hi = float.MinValue;
                for (var i = 0; i < 64; i++)
                {
                    var v = f(PointInBall(random, centre, radius, i));
                    lo = MathF.Min(lo, v);
                    hi = MathF.Max(hi, v);
                }

                sum += (interval.y - interval.x) / (double)(hi - lo);
            }

            return sum / 2000;
        }

        var snoiseRatios = new[] { 0.05f, 0.25f, 1.0f }.Select(r => (r, Ratio(iv_snoise_ball, snoise, r, 0x7167))).ToArray();
        var fbmRatios = new[] { 0.05f, 0.25f }.Select(r => (r, Ratio((c, rr) => iv_fbm_ball(c, rr, 4, 2.0f, 0.5f), x => fbm_grad(x, 4, 2.0f, 0.5f).w, r, 0x7168))).ToArray();
        output.WriteLine("IV-REPORT tightness (mean width / sampled range): " +
            string.Join(", ", snoiseRatios.Select(t => $"snoise r={t.r:R} {t.Item2:F2}")) + "; " +
            string.Join(", ", fbmRatios.Select(t => $"fbm4 r={t.r:R} {t.Item2:F2}")));
        Assert.All(snoiseRatios.Concat(fbmRatios), t => Assert.True(double.IsFinite(t.Item2)));
    }


    // ---- The saving: envelope-then-noise fields, marched over screen tiles ----

    // Combined cost in snoise units: one envelope evaluation, pointwise or as an interval probe, costs this
    // much. An estimate (a handful of pulses or one height-map fetch against ~150 flops of snoise); both
    // counts are printed, so any other weight can be applied to them.
    internal const double EnvelopeCost = 0.2;

    // One snoise_grad evaluation (af_snoise's one call) costs this much in snoise units: the value and
    // three gradient components over the same corners. An estimate, printed beside the count as the
    // envelope weight is, and not tuned toward any contract.
    internal const double GradCost = 2.0;

    internal sealed class Counts
    {
        public long Snoise;
        public long Envelope;
        public long Grad;

        public double Cost => Snoise + EnvelopeCost * Envelope + GradCost * Grad;
    }

    /// <summary>
    /// One screen tile of one frame: a pinhole camera (origin and orthonormal basis; camera space looks
    /// down +z and the ray of slope m is the points (m z, z)), the tile's central slope, its size N, the
    /// focal length in pixels and the frame time. Every tile draws its own camera, so a probe result is
    /// never carried to another frame.
    /// </summary>
    internal class Tile
    {
        public float3 Origin;
        public float3 Right = new(1.0f, 0.0f, 0.0f);
        public float3 Up = new(0.0f, 1.0f, 0.0f);
        public float3 Forward = new(0.0f, 0.0f, 1.0f);
        public float2 Slope;
        public int N;
        public float Focal;
        public float Time;

        public float Half => N / (2.0f * Focal);
        public float Footprint => N / (MathF.Sqrt(2.0f) * Focal);
        public float2 SlopesX => new(Slope.x - Half, Slope.x + Half);
        public float2 SlopesY => new(Slope.y - Half, Slope.y + Half);

        // Pixel (i, j) of the tile at sub-pixel offset (u, v) in [0, 1): its slope lies in SlopesX x SlopesY
        // in float32, because (i + u) / f - N / (2f) is at most N / f - N / (2f) = N / (2f) exactly.
        public float2 PixelSlope(int i, int j, float u, float v) =>
            Slope + new float2((i + u) / Focal - Half, (j + v) / Focal - Half);

        public float3 World(float3 c) => Origin + Right * c.x + Up * c.y + Forward * c.z;

        public float3 Camera(float3 w)
        {
            var d = w - Origin;
            return new float3(dot(d, Right), dot(d, Up), dot(d, Forward));
        }

        public void Look(float3 forward)
        {
            Forward = normalize(forward);
            Right = normalize(cross(new float3(0.0f, 1.0f, 0.0f), Forward));
            Up = cross(Forward, Right);
        }
    }

    /// <summary>
    /// A field with an analytic envelope and the dense grid it is marched on. Density is the pointwise
    /// field, envelope-gated (noise only where the envelope leaves the density undecided), counted. Bound
    /// is the one composition of the envelope's interval over a tile slice's box and the noise ball over the
    /// slice's iv_frustum_ball: an interval enclosing Density at every point of every ray of the tile in the
    /// slice. A slice is provably empty iff Bound(...).y &lt;= 0. gated is the march's form: it returns the
    /// envelope's verdict without snoise when the envelope alone proves the slice empty, and attempts the
    /// noise ball only when the best outcome any centre value could give would prove it; ungated, the ball
    /// is always evaluated.
    /// </summary>
    /// <summary>FogField's bound: the centred-warp interval composition, or the affine one (design.md, "Affine forms").</summary>
    internal enum BoundMode
    {
        Interval,
        Affine,
    }

    internal abstract class Scenario
    {
        public required string Name { get; init; }
        public float[] Grid { get; protected init; } = Array.Empty<float>();
        public float Focal { get; init; } = 935.0f;  // 1080 rows at a 60 degree vertical field of view
        public float Extinction { get; init; } = 1.0f;

        public abstract Tile DrawTile(System.Random random, int n);

        public abstract float Density(Tile tile, float2 slope, float z, bool lod, Counts counts);

        public abstract float2 Bound(Tile tile, float z0, float z1, bool gated, Counts counts);

        // Cell boundaries near + (j / J)^2 (far - near): Aetheria's quadratic sample spacing
        // (Assets/Shaders/Raymarching/CloudShader.shader:116).
        protected static float[] Quadratic(float near, float far, int cells) =>
            Enumerable.Range(0, cells + 1).Select(j => near + (float)j / cells * ((float)j / cells) * (far - near)).ToArray();

        protected static float[] Cells(float step, float end) =>
            Enumerable.Range(0, (int)MathF.Ceiling(end / step) + 1).Select(j => j * step).ToArray();
    }

    // iv_snoise_ball, with the snoise skipped where L r >= 2: there the bound is [-1, 1] whatever snoise(c)
    // is (|snoise| < 1), so the result is the same and costs nothing.
    private static float2 NoiseBall(float3 centre, float radius, Counts counts)
    {
        if (SNOISE_LIPSCHITZ * radius >= 2.0f)
            return new float2(-1.0f, 1.0f);
        counts.Snoise++;
        return iv_snoise_ball(centre, radius);
    }

    // The best interval any centre value could give a ball of this radius, toward low or high values; the
    // gate on whether a probe can prove anything. snoise(c) > -1, so a real ball's hi is at least -1 + L r,
    // and likewise its lo is at most 1 - L r.
    private static float2 BestBall(float radius, bool low)
    {
        var e = SNOISE_LIPSCHITZ * radius;
        if (e >= 2.0f)
            return new float2(-1.0f, 1.0f);
        return low ? new float2(-1.0f, min(-1.0f + e, 1.0f)) : new float2(max(1.0f - e, -1.0f), 1.0f);
    }

    // Least and greatest distance along one axis from a box to a point, exact in float32: a coordinate in
    // [lo, hi] is at least `near` and at most `far` from c, and squares, sums and sqrt are monotone.
    private static (float Near, float Far) AxisDistance(float2 range, float c) =>
        (c < range.x ? range.x - c : c > range.y ? c - range.y : 0.0f, max(abs(range.x - c), abs(range.y - c)));

    /// <summary>
    /// The noise of the fog and the void (r1's field shape): coverage is two snoise of the warped point,
    /// cross-faded over two half-period phases; n = coverage + detail / 2, the detail one snoise at four
    /// times the frequency. The flow is a unit-bounded 2D field times FlowAmplitude, so a phase moves a point
    /// by at most Warp = FlowAmplitude * Period / 2. The flow is not counted: the site reads it from a
    /// texture and Aetheria from its flow map.
    /// </summary>
    internal sealed class WarpedNoise
    {
        public required float F0 { get; init; }
        public required float Warp { get; init; }
        public float Period { get; init; } = 8.0f;
        public float FlowFrequency { get; init; } = 1.0f / 512.0f;
        public bool FlowInXY { get; init; }

        private float FlowAmplitude => Warp * 2.0f / Period;

        public float3 Flow(float3 p)
        {
            var q = (FlowInXY ? new float2(p.x, p.y) : new float2(p.x, p.z)) * FlowFrequency;
            var v = new float2(snoise(q + new float2(17.0f, 3.0f)), snoise(q + new float2(-5.0f, 41.0f)));
            v = v / max(1.0f, length(v)) * FlowAmplitude;
            return FlowInXY ? new float3(v.x, v.y, 0.0f) : new float3(v.x, 0.0f, v.y);
        }

        public (float W0, float Shift0, float Shift1) Phases(float time)
        {
            var phase0 = frac(time / Period);
            var phase1 = frac(time / Period + 0.5f);
            return (1.0f - abs(2.0f * phase0 - 1.0f), (phase0 - 0.5f) * Period, (phase1 - 0.5f) * Period);
        }

        public float Coverage(float3 p, float time, Counts counts)
        {
            var (w0, shift0, shift1) = Phases(time);
            var flow = Flow(p);
            counts.Snoise += 2;
            return w0 * snoise((p + flow * shift0) * F0) + (1.0f - w0) * snoise((p + flow * shift1) * F0);
        }

        public float Detail(float3 p, Counts counts)
        {
            counts.Snoise++;
            return snoise(p * (4.0f * F0) + 17.0f);
        }

        // The cross-fade of the two phases' noise enclosures, term by term with the frame's own weights.
        // A field whose ball carries the whole warp passes the same enclosure for both phases.
        public float2 CoverageBound(float2 phase0, float2 phase1, float time)
        {
            var w0 = Phases(time).W0;
            return iv_add(iv_scale(phase0, w0), iv_scale(phase1, 1.0f - w0));
        }

        /// <summary>
        /// How far the flow can move across a ball of radius r about c: each flow component is snoise(float2)
        /// of the point projected to the flow plane and scaled by FlowFrequency, which varies over the ball by
        /// at most iv_snoise_ball(float2)'s reach from its centre value, plus Deviation's float32 allowance; the
        /// projection and the clamp to the unit disc are nonexpansive, and two unit-disc vectors differ by at
        /// most 2.
        /// </summary>
        public float FlowVariation(float3 centre, float radius)
        {
            var q = (FlowInXY ? new float2(centre.x, centre.y) : new float2(centre.x, centre.z)) * FlowFrequency;
            var u = Deviation(q + new float2(17.0f, 3.0f), radius * FlowFrequency);
            var v = Deviation(q + new float2(-5.0f, 41.0f), radius * FlowFrequency);
            return min(sqrt(u * u + v * v), 2.0f) * FlowAmplitude;
        }

        // How far snoise(float2) can differ from its value at q over radius, in float32: the ball's reach plus
        // the allowance of af_snoise's convention for the 2D value, 2^-14 + L2 2^-20 (|q|_1 + r). The point
        // q + offset rounds at the magnitude of q + offset (a unit in the last place of 20 is 2^-19), so the
        // flow at two points of a ball differs by more than the exact function can, and the iv_snoise_ball
        // bound, which is exact for the point it is given, does not carry that.
        private static float Deviation(float2 q, float radius)
        {
            var n = snoise(q);
            var ball = iv_snoise_ball(q, radius);
            var allowance = 6.103515625e-5f + SNOISE2_LIPSCHITZ * 9.5367431640625e-7f * (abs(q.x) + abs(q.y) + radius);
            return max(ball.y - n, n - ball.x) + allowance;
        }

        /// <summary>
        /// The centred warp (design.md, "Affine forms"): phase k moves a point p of a set within reach of c
        /// to p + flow(p) shift_k, which lies within |shift_k| rho of (p - c) + c + flow(c) shift_k, rho =
        /// FlowVariation(c, reach). So phase k's noise argument is the set moved by flow(c) shift_k and
        /// grown by |shift_k| rho, instead of grown by the whole warp. At warp 0 both phases are the set.
        /// </summary>
        public (float3 Centre0, float Grow0, float3 Centre1, float Grow1) CentredWarp(float3 centre, float reach, float time)
        {
            var (_, shift0, shift1) = Phases(time);
            var flow = Flow(centre);
            var rho = FlowVariation(centre, reach);
            return (centre + flow * shift0, abs(shift0) * rho, centre + flow * shift1, abs(shift1) * rho);
        }

        public float2 CoverageBall(float3 centre, float radius, Counts counts) => NoiseBall(centre * F0, radius * F0, counts);

        public float2 DetailBall(float3 centre, float radius, Counts counts) => NoiseBall(centre * (4.0f * F0) + 17.0f, radius * (4.0f * F0), counts);

        public float CoverageRadius(float radius) => radius * F0;

        public float DetailRadius(float radius) => radius * (4.0f * F0);
    }

    /// <summary>
    /// Aetheria's nebula (Assets/Shaders/Volumetric.cginc:165-221, cloudDensity :91-102) over its zone
    /// height map (Assets/Scripts/ServerShared/Zone.cs:369-397 GetHeight, :426 PowerPulse), in its units
    /// (Assets/Resources/Settings.asset: ZoneDepth :97-98, DefaultEnvironment :386-411). The pre-distortion
    /// SDF is s(p) = p.y + h(p.xz), h &gt;= 0 the well map: the zone bowl PowerPulse(|xz| / 2R, 2) * 64 plus,
    /// per well, PowerPulse(|xz - c_b| / r_b, 16) * d_b, with r_b = 500 M^0.25, d_b = 30 M^0.175 and
    /// PowerPulse(x, e) = (1 - 4x^2)^e on [0, 1/2], 0 beyond. Noise displaces only below the safety distance:
    /// fade = 1 - smoothstep(0.75 S, S, s), s' = s + A fade n(warp(p)), density = max(0, (F - s') / B); the
    /// patch band and the 1e-9 fill are left out. The warp moves only the noise argument; the height is read
    /// at the unwarped xz, as Aetheria reads _NebulaSurfaceHeight at pos.xz. Envelope coordinates are the
    /// tile's camera frame (the camera yaws about y, so horizontal distances are the world's). Extinction
    /// 0.5 is the environment's (Settings.asset:397, applied at CloudShader.shader:91).
    /// </summary>
    internal sealed class FogField : Scenario
    {
        public const float Safety = 30.0f;          // SafetyDistance, Settings.asset:399
        public const float FloorOffset = -20.0f;    // :393
        public const float FloorBlend = 10.0f;      // :394
        public const float Amplitude = 20.0f;
        public const float NoiseScale = 414.2167f;  // :409
        public const float AetheriaWarp = 60.0f;    // Flow GlobalAmplitude 15 x Period 8 / 2, :402-405
        public const float ZoneRadius = 2000.0f;
        internal static readonly float2 FreeNoise = new(-1.5f, 1.5f);

        public required WarpedNoise Noise { get; init; }
        public required BoundMode Mode { get; init; }
        public (float2 Centre, float Scale, float Exponent, float Depth)[] Bowls { get; init; } = Array.Empty<(float2, float, float, float)>();
        public required Func<System.Random, FogField, float3> CameraAt { get; init; }
        public required float2 SlopesY { get; init; }

        public FogField()
        {
            // Near 0.3 and far 2048: the ARPG scene's Main Camera, which carries the cloud march
            // (Aetheria d3075eb0 Assets/Scenes/ARPG.unity:31777 m_Name, :31830 far clip plane;
            // Assets/Scripts/Zone Display/VolumeCloudRenderer.cs:36 RequireComponent(Camera)).
            Grid = Quadratic(0.3f, 2048.0f, 256);
            Extinction = 0.5f;
        }

        private static WarpedNoise AetheriaNoise(float warp) => new() { F0 = 1.0f / NoiseScale, Warp = warp };

        private static string Named(string name, BoundMode mode) => $"{name} [{(mode == BoundMode.Affine ? "affine" : "interval")}]";

        /// <summary>(a) Height fog: no wells, the camera above the safety band at y = S + 30, rays level and up.</summary>
        public static FogField HeightFog(float warp, BoundMode mode) => new()
        {
            Name = Named("(a) height fog", mode), Mode = mode, Noise = AetheriaNoise(warp), SlopesY = new float2(0.0f, 0.5f),
            CameraAt = (random, _) => new float3(Uniform(random, -1000.0f, 1000.0f), Safety + 30.0f, Uniform(random, -1000.0f, 1000.0f)),
        };

        /// <summary>(b) Inside the fog: no wells, the camera at y = F - 10, rays level.</summary>
        public static FogField InsideFog(float warp, BoundMode mode) => new()
        {
            Name = Named("(b) inside the fog", mode), Mode = mode, Noise = AetheriaNoise(warp), SlopesY = new float2(-0.05f, 0.05f),
            CameraAt = (random, _) => new float3(Uniform(random, -1000.0f, 1000.0f), FloorOffset - 10.0f, Uniform(random, -1000.0f, 1000.0f)),
        };

        // The deep well's geometry (ruling wells-scale-order-of-magnitude): one PowerPulse well of exponent
        // 2, scale 2220 and depth 600 at the zone's centre, so its depth and half-depth radius are each
        // about 600, ten times the band (DeepWellIsTenBandsDeepAndWide measures both).
        public const float DeepWellScale = 2220.0f;
        public const float DeepWellDepth = 600.0f;
        public const float DeepWellCameraRadius = 300.0f;

        /// <summary>
        /// (c) The deep well, the case Aetheria plays in: there the camera is always in some gravity well,
        /// gazing across a bowl a sun's gravity carves (ruling wells-scale-order-of-magnitude). The zone bowl
        /// R = 2000 plus one well at its centre, PowerPulse exponent 2, scale 2220, depth 600, so the well's
        /// depth and half-depth radius are each about ten times G = 3 A = 60, the band of s where only the
        /// noise decides density (s in [F - 1.5 A, F + 1.5 A] with the fade at 1). The camera's xz is uniform
        /// within 300 of the centre, at y = F - h(xz) / 2, halfway between the sunken fog surface and the
        /// rim's, in empty air; slopes and yaw as the shallow wells, m_y in [-0.25, 0.1]. A level ray crosses
        /// hundreds of units of empty well before the far wall's band.
        /// </summary>
        public static FogField DeepWell(float warp, BoundMode mode, float depth = DeepWellDepth) => new()
        {
            Name = Named("(c) deep well", mode), Mode = mode, Noise = AetheriaNoise(warp), SlopesY = new float2(-0.25f, 0.1f),
            Bowls = new (float2, float, float, float)[] { (new float2(0.0f, 0.0f), 2.0f * ZoneRadius, 2.0f, 64.0f), (new float2(0.0f, 0.0f), DeepWellScale, 2.0f, depth) },
            CameraAt = (r, field) =>
            {
                var angle = Uniform(r, 0.0f, 2.0f * MathF.PI);
                var radius = DeepWellCameraRadius * MathF.Sqrt(r.NextSingle());
                var xz = new float2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
                return new float3(xz.x, FloorOffset - 0.5f * field.HeightAt(xz), xz.y);
            },
        };

        /// <summary>
        /// (c-band) Shallow wells, the band-dominated row, printed only: the zone bowl R = 2000 (64 deep) and
        /// four wells of mass 100, 1000, 10000 and 1000 at seeded positions inside it, 30 M^0.175 deep (67 to
        /// 150, 1.1 to 2.5 times G) and 500 M^0.25 wide at exponent 16; the camera at a seeded xz in the bowl,
        /// just above the noise band at y = max(0, S + 10 - h), gazing across with m_y in [-0.25, 0.1]. Most
        /// of its cost is the band itself, which is where affine noise gains most.
        /// </summary>
        public static FogField Wells(float warp, BoundMode mode)
        {
            var random = new System.Random(0xA37E);
            var bowls = new List<(float2, float, float, float)> { (new float2(0.0f, 0.0f), 2.0f * ZoneRadius, 2.0f, 64.0f) };
            foreach (var mass in new[] { 100.0f, 1000.0f, 10000.0f, 1000.0f })
            {
                var angle = Uniform(random, 0.0f, 2.0f * MathF.PI);
                var radius = ZoneRadius * MathF.Sqrt(random.NextSingle());
                bowls.Add((new float2(radius * MathF.Cos(angle), radius * MathF.Sin(angle)), 500.0f * MathF.Pow(mass, 0.25f), 16.0f, 30.0f * MathF.Pow(mass, 0.175f)));
            }

            return new FogField
            {
                Name = Named("(c-band) shallow wells", mode), Mode = mode, Noise = AetheriaNoise(warp), Bowls = bowls.ToArray(), SlopesY = new float2(-0.25f, 0.1f),
                CameraAt = (r, field) =>
                {
                    var angle = Uniform(r, 0.0f, 2.0f * MathF.PI);
                    var radius = ZoneRadius * MathF.Sqrt(r.NextSingle());
                    var xz = new float2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
                    return new float3(xz.x, max(0.0f, Safety + 10.0f - field.HeightAt(xz)), xz.y);
                },
            };
        }

        private sealed class FogTile : Tile
        {
            public float2[] Centres = Array.Empty<float2>();  // bowl centres in the camera's (x, z)
        }

        public override Tile DrawTile(System.Random random, int n)
        {
            var origin = CameraAt(random, this);
            var yaw = Uniform(random, 0.0f, 2.0f * MathF.PI);
            var time = Uniform(random, 0.0f, 100.0f);
            var slope = new float2(Uniform(random, -1.0f, 1.0f), Uniform(random, SlopesY.x, SlopesY.y));
            return TileAt(origin, yaw, slope, n, Focal, time);
        }

        /// <summary>The tile of a camera at origin, yawed about y (0 looks along +z), at the given slope, size, focal length and time.</summary>
        public Tile TileAt(float3 origin, float yaw, float2 slope, int n, float focal, float time)
        {
            var tile = new FogTile { Origin = origin, N = n, Focal = focal, Time = time, Slope = slope };
            tile.Look(new float3(MathF.Sin(yaw), 0.0f, MathF.Cos(yaw)));
            tile.Centres = Bowls.Select(b =>
            {
                var c = tile.Camera(new float3(b.Centre.x, origin.y, b.Centre.y));
                return new float2(c.x, c.z);
            }).ToArray();
            return tile;
        }

        private static float Pulse(float distance, float scale, float exponent)
        {
            var x = distance / scale;
            var t = 1.0f - 4.0f * x * x;
            return t <= 0.0f ? 0.0f : pow(t, exponent);
        }

        // h at the camera-frame point (x, z), bowl centres in the same frame.
        private float Height(float2[] centres, float x, float z)
        {
            var h = 0.0f;
            for (var b = 0; b < Bowls.Length; b++)
            {
                var dx = x - centres[b].x;
                var dz = z - centres[b].y;
                h += Pulse(sqrt(dx * dx + dz * dz), Bowls[b].Scale, Bowls[b].Exponent) * Bowls[b].Depth;
            }

            return h;
        }

        /// <summary>The well map h at the world point (x, z).</summary>
        public float HeightAt(float2 xz) => Height(Bowls.Select(b => b.Centre).ToArray(), xz.x, xz.y);

        // Each bowl's term over the box is [pulse(far), pulse(near)], pulse decreasing in distance; pow is
        // not required to be correctly rounded, so both ends are widened by one ulp.
        private float2 HeightBound(float2[] centres, float2 x, float2 z)
        {
            var h = iv_point(0.0f);
            for (var b = 0; b < Bowls.Length; b++)
            {
                var (nx, fx) = AxisDistance(x, centres[b].x);
                var (nz, fz) = AxisDistance(z, centres[b].y);
                var near = Pulse(sqrt(nx * nx + nz * nz), Bowls[b].Scale, Bowls[b].Exponent);
                var far = Pulse(sqrt(fx * fx + fz * fz), Bowls[b].Scale, Bowls[b].Exponent);
                var pulse = new float2(far > 0.0f ? MathF.BitDecrement(far) : 0.0f, near > 0.0f ? MathF.BitIncrement(near) : 0.0f);
                h = iv_add(h, iv_scale(pulse, Bowls[b].Depth));
            }

            return h;
        }

        private static float2 Compose(float2 s, float2 fade, float2 n)
        {
            var displaced = iv_add(s, iv_scale(iv_mul(fade, n), Amplitude));
            return new float2(max(0.0f, (FloorOffset - displaced.y) / FloorBlend), max(0.0f, (FloorOffset - displaced.x) / FloorBlend));
        }

        public override float Density(Tile t, float2 slope, float z, bool lod, Counts counts)
        {
            var tile = (FogTile)t;
            var x = slope.x * z;
            var y = slope.y * z;
            counts.Envelope++;
            var s = tile.Origin.y + y + Height(tile.Centres, x, z);
            var fade = 1.0f - smoothstep(0.75f * Safety, Safety, s);
            if (Compose(iv_point(s), iv_point(fade), FreeNoise).y <= 0.0f)
                return 0.0f;
            var p = tile.World(new float3(x, y, z));
            var coverage = Noise.Coverage(p, tile.Time, counts);
            if (Compose(iv_point(s), iv_point(fade), iv_add(iv_point(coverage), iv_scale(new float2(-1.0f, 1.0f), 0.5f))).y <= 0.0f)
                return 0.0f;
            var n = coverage + Noise.Detail(p, counts) * 0.5f;
            return max(0.0f, (FloorOffset - (s + fade * n * Amplitude)) / FloorBlend);
        }

        /// <summary>
        /// The envelope first: the height and slope intervals over the slice's box, the fade, and the noise
        /// at its free range. Where that already proves the slice empty the envelope is the answer;
        /// otherwise Mode's bound, which (gated) returns the envelope when the best any probe could give
        /// would not prove the slice either.
        /// </summary>
        public override float2 Bound(Tile t, float z0, float z1, bool gated, Counts counts)
        {
            var tile = (FogTile)t;
            var frame = FrameOf(tile, z0, z1, counts);
            if (gated && frame.Envelope.y <= 0.0f)
                return frame.Envelope;
            return Mode == BoundMode.Affine
                ? AffineBound(tile, z0, z1, frame, gated, counts)
                : IntervalBound(tile, z0, z1, frame.S, frame.Fade, frame.Envelope, gated, counts);
        }

        // The height interval, the density level s over the slice's box, its fade and the envelope.
        private readonly record struct Frame(float2 Height, float2 S, float2 Fade, float2 Envelope);

        private Frame FrameOf(FogTile tile, float z0, float z1, Counts counts)
        {
            var depth = new float2(z0, z1);
            counts.Envelope++;
            var height = HeightBound(tile.Centres, iv_mul(depth, tile.SlopesX), depth);
            var s = iv_add(iv_add(iv_point(tile.Origin.y), iv_mul(depth, tile.SlopesY)), height);
            var fade = iv_sub(iv_point(1.0f), iv_smoothstep(0.75f * Safety, Safety, s));
            return new Frame(height, s, fade, Compose(s, fade, FreeNoise));
        }

        /// <summary>
        /// The two sound bounds AffineBound intersects, ungated: the affine arithmetic's own (the shared
        /// symbol through af_add, af_mul and af_range), and the interval composition over the same forms'
        /// ranges (NoiseBoundTests.AffineCompositionIsTighterThanItsRanges).
        /// </summary>
        internal (float2 Affine, float2 Interval) AffinePieces(Tile t, float z0, float z1, Counts counts)
        {
            var tile = (FogTile)t;
            return AffinePieces(tile, z0, z1, FrameOf(tile, z0, z1, counts), counts);
        }

        private static bool Same(float3 a, float3 b, float ga, float gb) => all(a == b) && ga == gb;

        /// <summary>
        /// The centred-warp interval bound. The slice's ball is iv_frustum_ball with no warp; each phase's
        /// coverage ball is that ball moved by the flow at its centre and grown by what the flow can vary
        /// across it (WarpedNoise.CentredWarp), one ball when the phases coincide (warp 0). The detail octave
        /// reads the unwarped point, so its ball is the unwarped one.
        /// </summary>
        private float2 IntervalBound(FogTile tile, float z0, float z1, float2 s, float2 fade, float2 envelope, bool gated, Counts counts)
        {
            var ball = iv_frustum_ball(tile.Slope, z0, z1, tile.Footprint, 0.0f);
            var centre = tile.World(new float3(ball.x, ball.y, ball.z));
            var (c0, g0, c1, g1) = Noise.CentredWarp(centre, ball.w, tile.Time);
            if (gated)
            {
                var provable = false;
                foreach (var low in new[] { true, false })
                {
                    var coverage = Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w + g0), low), BestBall(Noise.CoverageRadius(ball.w + g1), low), tile.Time);
                    provable |= Compose(s, fade, iv_add(coverage, iv_scale(BestBall(Noise.DetailRadius(ball.w), low), 0.5f))).y <= 0.0f;
                }

                if (!provable)
                    return envelope;
            }

            var n0 = Noise.CoverageBall(c0, ball.w + g0, counts);
            var n1 = Same(c0, c1, g0, g1) ? n0 : Noise.CoverageBall(c1, ball.w + g1, counts);
            var noise = iv_add(Noise.CoverageBound(n0, n1, tile.Time), iv_scale(Noise.DetailBall(centre, ball.w, counts), 0.5f));
            return Compose(s, fade, noise);
        }

        /// <summary>
        /// The affine slice of a tile over [z0, z1] in the world frame, with the centred warp applied: the
        /// camera-frame axis and ball (af_frustum_axis, af_frustum_ball with no warp), the ball's world
        /// centre, the world axis, and the reach R = |axis| + radius of the whole set from that centre (the
        /// distance the flow is varied over). It returns the final af_snoise arguments, so a consumer has no
        /// term left to wire: each phase's coverage argument is the set moved by the flow at its centre
        /// (WarpedNoise.CentredWarp over R) and grown by what the flow can vary across it, and the detail
        /// argument is the unwarped set. The rays at depth z lie within an argument's radius of its centre
        /// + axis eps(z), eps(z) = clamp((z - z_m) / h, -1, 1) (NoiseBoundTests.AffineWarpStaysEnclosed).
        /// </summary>
        internal AffineSlice Slice(Tile t, float z0, float z1)
        {
            var tile = (FogTile)t;
            var axisCamera = af_frustum_axis(tile.Slope, z0, z1);
            var ball = af_frustum_ball(tile.Slope, z0, z1, tile.Footprint, 0.0f);
            var centre = tile.World(new float3(ball.x, ball.y, ball.z));
            var axis = tile.Right * axisCamera.x + tile.Up * axisCamera.y + tile.Forward * axisCamera.z;
            var reach = length(axis) + ball.w;
            var (c0, g0, c1, g1) = Noise.CentredWarp(centre, reach, tile.Time);
            var coverageFrequency = Noise.F0;
            var detailFrequency = 4.0f * Noise.F0;
            return new AffineSlice(
                axisCamera, ball, centre, reach,
                new NoiseArg(c0 * coverageFrequency, axis * coverageFrequency, (ball.w + g0) * coverageFrequency),
                new NoiseArg(c1 * coverageFrequency, axis * coverageFrequency, (ball.w + g1) * coverageFrequency),
                new NoiseArg(centre * detailFrequency + 17.0f, axis * detailFrequency, ball.w * detailFrequency));
        }

        /// <summary>One af_snoise argument in noise space: the centre, the axis along eps, and the radius.</summary>
        internal readonly record struct NoiseArg(float3 Centre, float3 Axis, float Radius)
        {
            /// <summary>The distance of the whole set from its centre: the gate's reach.</summary>
            public float Reach => length(Axis) + Radius;

            public float3 Snoise() => af_snoise(Centre, Axis, Radius);
        }

        internal readonly record struct AffineSlice(float3 AxisCamera, float4 Ball, float3 Centre, float Reach, NoiseArg Coverage0, NoiseArg Coverage1, NoiseArg Detail);

        // The best interval any centre value and gradient could give af_snoise over a set of reach R (the
        // gradient 0, the value at the far end), toward low or high values: the affine gate.
        private static float2 BestForm(float reach, bool low)
        {
            var e = min(SNOISE_LIPSCHITZ * reach, 0.5f * SNOISE_HESSIAN * reach * reach);
            if (e >= 2.0f)
                return new float2(-1.0f, 1.0f);
            return low ? new float2(-1.0f, min(-1.0f + e, 1.0f)) : new float2(max(1.0f - e, -1.0f), 1.0f);
        }

        private static float2 Clip(float2 range) => new(max(range.x, -1.0f), min(range.y, 1.0f));

        /// <summary>
        /// The affine bound, one shared symbol: depth z = z_m + h eps over the slice (af_frustum_axis and
        /// af_frustum_ball give the noise argument the same eps). s = O_y + m_c,y z + (m_y - m_c,y) z + h(xz)
        /// is affine in eps, with the lateral slope-depth term (at most Half z1, plus the rounding of the
        /// pointwise m_y z) and the height interval in e. Each phase's coverage and the unwarped detail are
        /// af_snoise over the slice moved and grown by the centred warp, the fade enters as af_from_iv, and
        /// s' = s + A fade n is ranged by af_range. The result is intersected with the interval composition
        /// over the same forms' ranges, each clipped to [-1, 1] (both sound, no further evaluation). The gate
        /// is BestForm's.
        /// </summary>
        private float2 AffineBound(FogTile tile, float z0, float z1, Frame frame, bool gated, Counts counts)
        {
            if (gated)
            {
                var slice = Slice(tile, z0, z1);
                var (s, fade) = (frame.S, frame.Fade);
                var provable = false;
                foreach (var low in new[] { true, false })
                {
                    var coverage = Noise.CoverageBound(BestForm(slice.Coverage0.Reach, low), BestForm(slice.Coverage1.Reach, low), tile.Time);
                    provable |= Compose(s, fade, iv_add(coverage, iv_scale(BestForm(slice.Detail.Reach, low), 0.5f))).y <= 0.0f;
                }

                if (!provable)
                    return frame.Envelope;
            }

            var (affine, interval) = AffinePieces(tile, z0, z1, frame, counts);
            return new float2(max(affine.x, interval.x), min(affine.y, interval.y));
        }

        private (float2 Affine, float2 Interval) AffinePieces(FogTile tile, float z0, float z1, Frame frame, Counts counts)
        {
            var slice = Slice(tile, z0, z1);
            var (ball, axisCamera) = (slice.Ball, slice.AxisCamera);
            var (height, s, fade) = (frame.Height, frame.S, frame.Fade);
            var shared = slice.Coverage0 == slice.Coverage1;
            counts.Grad += shared ? 2 : 3;
            var n0 = slice.Coverage0.Snoise();
            var n1 = shared ? n0 : slice.Coverage1.Snoise();
            var nd = slice.Detail.Snoise();
            var w0 = Noise.Phases(tile.Time).W0;
            var noise = af_add(af_add(af_scale(n0, w0), af_scale(n1, 1.0f - w0)), af_scale(nd, 0.5f));

            // The clamp residual of z = z_m + h eps(z), 2^-23 (|z_m| + h) (af_frustum_ball).
            var residual = (abs(ball.z) + axisCamera.z) * 1.1920928955078125e-7f;
            var depth = af_add_iv(af_symbol(ball.z, axisCamera.z), new float2(-residual, residual));
            var lateral = tile.Half * z1 + (abs(tile.Slope.y) + tile.Half) * z1 * 2.384185791015625e-7f;
            var level = af_add_iv(af_add_iv(af_add(af_point(tile.Origin.y), af_scale(depth, tile.Slope.y)), new float2(-lateral, lateral)), height);
            var displaced = af_range(af_add(level, af_scale(af_mul(af_from_iv(fade), noise), Amplitude)));
            var affine = new float2(max(0.0f, (FloorOffset - displaced.y) / FloorBlend), max(0.0f, (FloorOffset - displaced.x) / FloorBlend));
            var interval = Compose(s, fade, iv_add(Noise.CoverageBound(Clip(af_range(n0)), Clip(af_range(n1)), tile.Time), iv_scale(Clip(af_range(nd)), 0.5f)));
            return (affine, interval);
        }
    }

    /// <summary>
    /// (d) r1's uniform slab, kept as the worst case: the map's warped coverage field (F0 0.35, cutoff 0.25,
    /// depth 12, period 4, extinction 2, flow in xy at 0.15, warp 0.05) with no envelope, so every cell is a
    /// noise question. The dense sample evaluates the coverage first and the detail only where the coverage
    /// passes the cutoff; ungated it would cost three snoise, as r1 counted.
    /// </summary>
    internal sealed class SlabField : Scenario
    {
        public const float F0 = 0.35f;
        public const float Cutoff = 0.25f;
        public const float Depth = 12.0f;
        public const float SlabExtinction = 2.0f;
        public const float SlabWarp = 0.05f;

        public required WarpedNoise Noise { get; init; }

        public SlabField() => Grid = Cells(0.125f, Depth);

        public static SlabField Create(float warp) => new()
        {
            Name = "(d) uniform slab", Noise = new WarpedNoise { F0 = F0, Warp = warp, Period = 4.0f, FlowFrequency = 0.15f, FlowInXY = true },
        };

        public override Tile DrawTile(System.Random random, int n) => new()
        {
            Origin = new float3(Uniform(random, -50.0f, 50.0f), Uniform(random, -50.0f, 50.0f), 0.0f),
            Slope = new float2(Uniform(random, -0.5f, 0.5f), Uniform(random, -0.5f, 0.5f)),
            N = n, Focal = Focal, Time = Uniform(random, 0.0f, 100.0f),
        };

        public override float Density(Tile tile, float2 slope, float z, bool lod, Counts counts)
        {
            var p = tile.World(new float3(slope.x * z, slope.y * z, z));
            var coverage = Noise.Coverage(p, tile.Time, counts);
            if (coverage <= Cutoff)
                return 0.0f;
            counts.Snoise++;
            var detail = 1.0f - abs(snoise(p * (4.0f * F0) + 17.0f));
            return max(0.0f, coverage - Cutoff) * SlabExtinction * lerp(0.6f, 1.0f, detail);
        }

        public override float2 Bound(Tile tile, float z0, float z1, bool gated, Counts counts)
        {
            var maybe = new float2(0.0f, float.MaxValue);
            var ball = iv_frustum_ball(tile.Slope, z0, z1, tile.Footprint, Noise.Warp);
            if (gated && Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w), low: true), BestBall(Noise.CoverageRadius(ball.w), low: true), tile.Time).y > Cutoff)
                return maybe;
            var coverageBall = Noise.CoverageBall(tile.World(new float3(ball.x, ball.y, ball.z)), ball.w, counts);
            var coverage = Noise.CoverageBound(coverageBall, coverageBall, tile.Time);
            return coverage.y <= Cutoff ? new float2(0.0f, 0.0f) : maybe;
        }
    }

    /// <summary>
    /// (e) The void: a cloud volume everywhere, carved by one negative pseudo-Gaussian sphere. Authored
    /// knobs: hollow radius Rh (density exactly 0 inside) and ramp width S (the density rises to full over
    /// [Rh, Rh + S]); everything else is derived from them. The brush (1 - (d / Rc)^2)^e with CARVE = 1.5
    /// reaches full at Rc, so Rc = Rh + S, and Rh = Rc sqrt(1 - CARVE^(-1/e)) gives
    /// e = -ln CARVE / ln(1 - (Rh / Rc)^2): density = K max(0, 1 - CARVE (1 - (d' / Rc)^2)^e), exactly 0 for
    /// d' &lt;= Rh and K for d' &gt;= Rc, K = 1/30. The noise displaces the wall: fade =
    /// smoothstep(Rh - S, Rh, d), d' = d + A fade n(warp(p)), A = 20, F0 = 0.01 (wavelengths 100 and 25),
    /// warp D = 10. The sun orbits the origin at radius 175 with period 72 s and rests on the hollow's
    /// bottom (c_v = sun + (0, Rh - 12.5, 0)); the camera looks at sun + (0, 60, 0) and, shipped, sits 120
    /// behind the sun along the orbit tangent and 45 above it.
    ///
    /// The one shipped void (ruling operator-shipped-void) is Shipped(): (Rh, S) = (198, 50), so Rc = 248
    /// and e = -ln 1.5 / ln(1 - (198 / 248)^2), which the constructor derives; no other void is shipped.
    ///
    /// The footprint-truncated field (lod) weights octave i by w_i(z) = 1 - smoothstep(l_i / 2m, l_i / m,
    /// z theta), m = 4, theta = 1 / f. Deviation from the spec's [w_i(z1), w_i(z0)]: the bound takes each
    /// weight over [w_i(z1), 1], because one Bound serves the full field (w = 1) and the truncated one
    /// (w in [w_i(z1), w_i(z0)], w decreasing in z); [w_i(z1), 1] contains both and iv_mul is monotone in
    /// its interval argument, so the composed interval encloses both fields. The slack is only in far
    /// tiles, whose step the footprint already sets. At f = 935 every weight is 1 inside every grid;
    /// LodFar() (f = 467) reaches weights in (0, 1) and at 0.
    ///
    /// The dense grid is the fixed-step reference's: cells of min(10, S / 4) out to
    /// |camera - c_v| + Rc + 6 / K. The density is provably K only from d &gt;= Rc + A 1.5 (the free noise
    /// interval), so 6 / K leaves at least 4.9 of optical depth past it and every ray goes opaque before
    /// the grid ends.
    /// </summary>
    internal sealed class VoidField : Scenario
    {
        public const float Carve = 1.5f;
        public const float Amplitude = 20.0f;
        public const float K = 1.0f / 30.0f;
        public const float StepFootprints = 4.0f;    // k
        public const float OctaveFootprints = 4.0f;  // m
        private static readonly float2 FreeNoise = new(-1.5f, 1.5f);

        public static readonly float[] HollowRadii = { 100.0f, 200.0f, 400.0f, 1000.0f };
        public static readonly float[] Ramps = { 10.0f, 50.0f, 150.0f };
        public static readonly (string Name, float Fraction)[] CameraOffsets = { ("centred", 0.0f), ("mid", 0.5f), ("low", 0.94f) };

        public readonly float HollowRadius;
        public readonly float Ramp;
        public readonly float CarveRadius;
        public readonly float Exponent;
        public readonly float RampStart;
        public readonly float FixedStep;
        public readonly float CameraFraction;  // negative: the shipped camera
        public readonly WarpedNoise Noise = new() { F0 = 0.01f, Warp = 10.0f };

        private float CoarseWavelength => 1.0f / Noise.F0;
        private float FineWavelength => 1.0f / (4.0f * Noise.F0);
        private float Theta => 1.0f / Focal;

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public VoidField(float hollowRadius, float ramp, float cameraFraction, string camera, float focal = 935.0f)
        {
            Focal = focal;
            HollowRadius = hollowRadius;
            Ramp = ramp;
            CarveRadius = hollowRadius + ramp;
            Exponent = (float)(-Math.Log(Carve) / Math.Log(1.0 - (double)hollowRadius / CarveRadius * hollowRadius / CarveRadius));
            RampStart = hollowRadius - ramp;
            FixedStep = min(10.0f, ramp / 4.0f);
            CameraFraction = cameraFraction;
            Name = $"(e) void Rh={hollowRadius:R} camera={camera} S={ramp:R}" + (focal == 935.0f ? "" : $" f={focal:R}");
            var reach = cameraFraction < 0.0f ? length(new float2(120.0f, 45.0f - (hollowRadius - 12.5f))) : cameraFraction * hollowRadius;
            Grid = Cells(FixedStep, reach + CarveRadius + 6.0f / K);
        }

        public static VoidField Shipped() => new(198.0f, 50.0f, -1.0f, "shipped");

        /// <summary>
        /// The configuration that reaches the octave weights: Rh = 4000, S = 50, the low camera, f = 467
        /// (1080 rows at a 98 degree vertical field of view). The fine weight is below 1 past z = 1460 and 0
        /// past 2920, the coarse below 1 past 5840, and the grid reaches about 7990. Printed, not asserted.
        /// </summary>
        public static VoidField LodFar() => new(4000.0f, 50.0f, 0.94f, "low", 467.0f);

        /// <summary>The coarse and fine octave weights at depth z (1 for the full field).</summary>
        public (float Coarse, float Fine) Weights(float z) => (Weight(CoarseWavelength, z * Theta), Weight(FineWavelength, z * Theta));

        /// <summary>The coarse and fine octave weight bounds Bound uses over [z0, z1].</summary>
        public (float2 Coarse, float2 Fine) WeightBounds(float z0, float z1) => (WeightBound(CoarseWavelength, z0, z1), WeightBound(FineWavelength, z0, z1));

        private sealed class VoidTile : Tile
        {
            public float3 Cavity;  // c_v in the camera frame
        }

        public override Tile DrawTile(System.Random random, int n)
        {
            var time = Uniform(random, 0.0f, 72.0f);
            var angle = 2.0f * MathF.PI * time / 72.0f;
            var sun = new float3(175.0f * MathF.Cos(angle), 0.0f, 175.0f * MathF.Sin(angle));
            var tangent = new float3(-MathF.Sin(angle), 0.0f, MathF.Cos(angle));
            var cavity = sun + new float3(0.0f, HollowRadius - 12.5f, 0.0f);
            var target = sun + new float3(0.0f, 60.0f, 0.0f);
            float3 origin;
            float3 forward;
            if (CameraFraction < 0.0f)
            {
                origin = sun - tangent * 120.0f + new float3(0.0f, 45.0f, 0.0f);
                forward = target - origin;
            }
            else
            {
                // The shipped camera's direction from the centre, at a fraction of this hollow's radius;
                // centred, it looks along the orbit, slightly down.
                origin = cavity + normalize(tangent * -120.0f + new float3(0.0f, 45.0f - (198.0f - 12.5f), 0.0f)) * (CameraFraction * HollowRadius);
                forward = CameraFraction == 0.0f ? tangent * -1.0f + new float3(0.0f, -0.2f, 0.0f) : target - origin;
            }

            var tile = new VoidTile
            {
                Origin = origin, N = n, Focal = Focal, Time = time,
                Slope = new float2((random.Next(1920 / n) * n + n * 0.5f - 960.0f) / Focal, (random.Next(1080 / n) * n + n * 0.5f - 540.0f) / Focal),
            };
            tile.Look(forward);
            tile.Cavity = tile.Camera(cavity);
            return tile;
        }

        private static float Weight(float wavelength, float footprint) =>
            1.0f - smoothstep(wavelength / (2.0f * OctaveFootprints), wavelength / OctaveFootprints, footprint);

        private float2 WeightBound(float wavelength, float z0, float z1) =>
            new(iv_sub(iv_point(1.0f), iv_smoothstep(wavelength / (2.0f * OctaveFootprints), wavelength / OctaveFootprints, new float2(z0 * Theta, z1 * Theta))).x, 1.0f);

        /// <summary>The footprint step: max(k z theta, min(S / 4, half the finest live wavelength)).</summary>
        public float LodStep(float z)
        {
            var footprint = z * Theta;
            var fine = Weight(FineWavelength, footprint) > 0.0f ? FineWavelength : Weight(CoarseWavelength, footprint) > 0.0f ? CoarseWavelength : float.PositiveInfinity;
            return max(StepFootprints * footprint, min(Ramp / 4.0f, fine / 2.0f));
        }

        private float Profile(float displaced)
        {
            if (displaced <= HollowRadius)
                return 0.0f;
            var x = displaced / CarveRadius;
            var g = displaced < CarveRadius ? pow(max(0.0f, 1.0f - x * x), Exponent) : 0.0f;
            return K * max(0.0f, 1.0f - Carve * g);
        }

        private float2 Compose(float2 d, float2 fade, float2 n)
        {
            var displaced = iv_add(d, iv_scale(iv_mul(fade, n), Amplitude));
            return new float2(displaced.x >= CarveRadius ? K : 0.0f, displaced.y <= HollowRadius ? 0.0f : K);
        }

        public override float Density(Tile t, float2 slope, float z, bool lod, Counts counts)
        {
            var tile = (VoidTile)t;
            var x = slope.x * z;
            var y = slope.y * z;
            counts.Envelope++;
            var dx = x - tile.Cavity.x;
            var dy = y - tile.Cavity.y;
            var dz = z - tile.Cavity.z;
            var d = sqrt(dx * dx + dy * dy + dz * dz);
            var fade = smoothstep(RampStart, HollowRadius, d);
            var free = Compose(iv_point(d), iv_point(fade), FreeNoise);
            if (free.x == free.y)
                return free.x;
            var p = tile.World(new float3(x, y, z));
            var wc = lod ? Weight(CoarseWavelength, z * Theta) : 1.0f;
            var wd = lod ? Weight(FineWavelength, z * Theta) : 1.0f;
            var coverage = wc > 0.0f ? Noise.Coverage(p, tile.Time, counts) * wc : 0.0f;
            var partial = Compose(iv_point(d), iv_point(fade), iv_add(iv_point(coverage), iv_scale(new float2(-wd, wd), 0.5f)));
            if (partial.x == partial.y)
                return partial.x;
            var detail = wd > 0.0f ? Noise.Detail(p, counts) * 0.5f * wd : 0.0f;
            return Profile(d + fade * (coverage + detail) * Amplitude);
        }

        /// <summary>
        /// The field with no envelope gate: both noise terms always evaluated, the profile applied to the
        /// displaced distance. Density's shortcuts go through Compose; this does not, so it is the value a
        /// wrong Compose cannot also corrupt. Not counted.
        /// </summary>
        public float Exact(Tile t, float2 slope, float z, bool lod)
        {
            var tile = (VoidTile)t;
            var counts = new Counts();
            var (x, y) = (slope.x * z, slope.y * z);
            var (dx, dy, dz) = (x - tile.Cavity.x, y - tile.Cavity.y, z - tile.Cavity.z);
            var d = sqrt(dx * dx + dy * dy + dz * dz);
            var fade = smoothstep(RampStart, HollowRadius, d);
            var p = tile.World(new float3(x, y, z));
            var wc = lod ? Weight(CoarseWavelength, z * Theta) : 1.0f;
            var wd = lod ? Weight(FineWavelength, z * Theta) : 1.0f;
            var n = Noise.Coverage(p, tile.Time, counts) * wc + Noise.Detail(p, counts) * 0.5f * wd;
            return Profile(d + fade * n * Amplitude);
        }

        public override float2 Bound(Tile t, float z0, float z1, bool gated, Counts counts)
        {
            var tile = (VoidTile)t;
            var depth = new float2(z0, z1);
            counts.Envelope++;
            var (nx, fx) = AxisDistance(iv_mul(depth, tile.SlopesX), tile.Cavity.x);
            var (ny, fy) = AxisDistance(iv_mul(depth, tile.SlopesY), tile.Cavity.y);
            var (nz, fz) = AxisDistance(depth, tile.Cavity.z);
            var d = new float2(sqrt(nx * nx + ny * ny + nz * nz), sqrt(fx * fx + fy * fy + fz * fz));
            var fade = iv_smoothstep(RampStart, HollowRadius, d);
            var envelope = Compose(d, fade, FreeNoise);
            if (gated && envelope.y <= 0.0f)
                return envelope;
            var ball = iv_frustum_ball(tile.Slope, z0, z1, tile.Footprint, Noise.Warp);
            var wc = WeightBound(CoarseWavelength, z0, z1);
            var wd = WeightBound(FineWavelength, z0, z1);
            if (gated)
            {
                var provable = false;
                foreach (var low in new[] { true, false })
                {
                    var best = iv_add(iv_mul(Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w), low), BestBall(Noise.CoverageRadius(ball.w), low), tile.Time), wc), iv_mul(iv_scale(BestBall(Noise.DetailRadius(ball.w), low), 0.5f), wd));
                    provable |= Compose(d, fade, best).y <= 0.0f;
                }

                if (!provable)
                    return envelope;
            }

            var centre = tile.World(new float3(ball.x, ball.y, ball.z));
            var coverage = Noise.CoverageBall(centre, ball.w, counts);
            var noise = iv_add(iv_mul(Noise.CoverageBound(coverage, coverage, tile.Time), wc), iv_mul(iv_scale(Noise.DetailBall(centre, ball.w, counts), 0.5f), wd));
            return Compose(d, fade, noise);
        }
    }

    // One cell's transmittance: the density at the cell's mid-depth, over the ray's length through it.
    private static float Integrate(Scenario field, Tile tile, float2 slope, float z0, float z1, bool lod, Counts counts) =>
        exp(-(field.Density(tile, slope, (z0 + z1) * 0.5f, lod, counts) * field.Extinction) * ((z1 - z0) * sqrt(slope.x * slope.x + slope.y * slope.y + 1.0f)));

    /// <summary>
    /// The tile pre-pass on cell-index ranges: probe the slice of the range; provably empty skips it and
    /// doubles the next range; otherwise a range of more than one cell halves, and a single cell is dense.
    /// It starts with the whole grid, runs to the grid's end (a pre-pass knows no ray's transmittance) and
    /// yields the dense cells in order. Its probes are counted once per tile.
    ///
    /// The body (ruling operator-analytic-wall-body): when a single dense cell's own bound is a positive
    /// point (the cell is provably constant), one more probe over [grid[j], grid end]; if that is a
    /// positive point too, every point of every ray of the tile over the rest of the grid has exactly that
    /// density, so BodyStart = j, BodyDensity = its value, and every remaining cell is appended dense
    /// without probing (adding cells is always sound, so the mask keeps its meaning). BodyStart is -1 when
    /// no body is proven.
    /// </summary>
    private static (List<int> Dense, int BodyStart, float BodyDensity) MarchTile(Scenario field, Tile tile, Counts probes)
    {
        var grid = field.Grid;
        var cells = grid.Length - 1;
        var dense = new List<int>();
        var j = 0;
        var span = cells;
        while (j < cells)
        {
            var end = Math.Min(j + span, cells);
            var bound = field.Bound(tile, grid[j], grid[end], gated: true, probes);
            if (bound.y <= 0.0f)
            {
                j = end;
                span = Math.Min(span * 2, cells);
            }
            else if (end - j > 1)
            {
                span = (end - j + 1) / 2;
            }
            else
            {
                if (bound.x == bound.y && bound.x > 0.0f)
                {
                    var rest = field.Bound(tile, grid[j], grid[cells], gated: true, probes);
                    if (rest.x == rest.y && rest.x > 0.0f)
                    {
                        dense.AddRange(Enumerable.Range(j, cells - j));
                        return (dense, j, rest.x);
                    }
                }

                dense.Add(j);
                j++;
            }
        }

        return (dense, -1, 0.0f);
    }

    // The body's optical depth from z to the grid's end, at the density the field's pointwise envelope gives
    // at z (one envelope evaluation; no probe in the main pass). MarchLod takes exp(-BodyDepth) as one step.
    private static float BodyDepth(Scenario field, Tile tile, float2 slope, float z, bool lod, Counts counts) =>
        field.Density(tile, slope, z, lod, counts) * field.Extinction * ((field.Grid[^1] - z) * sqrt(slope.x * slope.x + slope.y * slope.y + 1.0f));

    // The footprint-aware march, the void's shipped one: steps of LodStep(z) inside the unmasked runs only,
    // octave weights applied, early-out below cutoff; at the body's start, one analytic step to the grid's
    // end, and stop. AtBody is the transmittance when the march reaches the body's start, before that step,
    // or the final transmittance when there is no body.
    private static (float Transmittance, float AtBody) MarchLod(VoidField field, Tile tile, float2 slope, List<int> cells, int bodyStart, float cutoff, Counts counts, ref long steps, ref long bodySteps)
    {
        var grid = field.Grid;
        var body = bodyStart >= 0 ? grid[bodyStart] : float.PositiveInfinity;
        var transmittance = 1.0f;
        var k = 0;
        while (k < cells.Count && transmittance >= cutoff)
        {
            var z = grid[cells[k]];
            var end = cells[k] + 1;
            for (k++; k < cells.Count && cells[k] == end; k++)
                end++;
            while (z < grid[end] && transmittance >= cutoff)
            {
                if (z >= body)
                {
                    var atBody = transmittance;
                    transmittance *= exp(-BodyDepth(field, tile, slope, z, lod: true, counts));
                    steps++;
                    bodySteps++;
                    return (transmittance, atBody);
                }

                var next = min(min(z + field.LodStep(z), grid[end]), z < body ? body : grid[end]);
                transmittance *= Integrate(field, tile, slope, z, next, lod: true, counts);
                steps++;
                z = next;
            }
        }

        return (transmittance, transmittance);
    }

    // The accuracy oracle before the body: the optical depth over every grid cell below `stop` (never the
    // mask's, so it trusts no pre-pass), by midpoint quadrature at step h, accumulated in double. Not counted.
    private static double ConvergedDepth(Scenario field, Tile tile, float2 slope, int stop, float h, bool lod)
    {
        var grid = field.Grid;
        var counts = new Counts();
        var dir = Math.Sqrt(slope.x * (double)slope.x + slope.y * (double)slope.y + 1.0);
        var tau = 0.0;
        for (var c = 0; c < stop; c++)
        {
            var samples = Math.Max(1, (int)Math.Ceiling((grid[c + 1] - grid[c]) / h));
            var dz = (grid[c + 1] - grid[c]) / (double)samples;
            for (var s = 0; s < samples; s++)
                tau += field.Density(tile, slope, (float)(grid[c] + (s + 0.5) * dz), lod, counts) * (double)field.Extinction * dz * dir;
        }

        return tau;
    }

    internal sealed class RunStats
    {
        public readonly Counts Dense = new();
        public readonly Counts Masked = new();
        public readonly Counts Probes = new();
        public readonly Counts Lod = new();
        public readonly Counts Oracle = new();
        public readonly Counts Range = new();
        public long Rays;
        public long Tiles;
        public long DenseSteps;
        public long MaskedSteps;
        public long LodSteps;
        public long BodySteps;
        public long BodyTiles;
        public long Unfinished;
        public float MaxDifference;
        public float MaxLodDifference;
        public float MaxOracleDifference;
        public long OracleEmpty;
        public long OracleEmptySkipped;
        public long RangeEmpty;
        public long RangeEmptySkipped;
        public long RangeViolations;
        public bool Ranged;
        public double MaxLodDepthError;
        public double MaxRefDepthError;

        public double PerRay(double value) => value / Rays;

        // Dense cost over the tile march's: each ray's own dense cells plus the tile's probes, shared.
        public double Ratio => Dense.Cost / (Masked.Cost + Probes.Cost);

        public double LodRatio => Dense.Cost / (Lod.Cost + Probes.Cost);

        // The oracle mask (the cells some ray of the tile samples nonzero) is a ceiling no pre-pass can move.
        public double OracleCeiling => Dense.Cost / Oracle.Cost;

        public double Efficiency => Oracle.Cost / (Masked.Cost + Probes.Cost);

        // The tile march over the tile march plus its probes: what the probes cost, not what they cull.
        public double ProbeOverhead => Masked.Cost / (Masked.Cost + Probes.Cost);

        // The oracle-empty cells the pre-pass proves empty; a pre-pass that skips nothing scores 0.
        public double CullFraction => (double)OracleEmptySkipped / OracleEmpty;

        // The range ceiling: the oracle march's cost over the range mask's, with probes free. The range
        // mask is what an exact enclosure of each cell over the tile's whole slice could prove, so no sound
        // bound can reach the oracle, only this; it is sampled, so it is an upper estimate.
        public double RangeCeiling => Oracle.Cost / Range.Cost;

        // The tile march's efficiency against the range ceiling: the range mask's cost over the tile
        // march's with its probes.
        public double RangeEfficiency => Range.Cost / (Masked.Cost + Probes.Cost);

        public double RangeCull => (double)RangeEmptySkipped / RangeEmpty;
    }

    /// <summary>
    /// Draws `tiles` tiles, runs the pre-pass once per tile, draws the tile's N^2 ray slopes (one per pixel,
    /// jittered inside it), builds the oracle mask (the cells whose mid-depth density is nonzero for at least
    /// one of those slopes), and marches each ray densely over every cell, masked over the tile's dense cells
    /// and over the oracle cells, all with the early-out at 0.02 and the same Integrate. With lod, the
    /// footprint-aware march as well, and the agreement at the body start (or the grid's end): the optical
    /// depth of a cutoff-0 footprint march and of the fixed-step cells, each against ConvergedDepth at
    /// h = 0.5. The oracle mask and the agreement marches count into throwaway Counts, the oracle march into
    /// its own, so the cost rows do not move. With range, the range mask as well: the oracle mask plus
    /// every cell where the density is positive at any of 5 x 5 slopes over the tile's slope square
    /// (corners included) x 9 depths over the cell; each ray is marched over it into Range, and a cell of
    /// it the pre-pass skipped is counted as a range violation (a sampled point a bound failed to enclose).
    /// </summary>
    private static RunStats Run(Scenario field, int n, int tiles, int seed, bool lod = false, bool range = false)
    {
        var random = new System.Random(seed);
        var stats = new RunStats { Ranged = range };
        var scratch = new Counts();
        var grid = field.Grid;
        for (var t = 0; t < tiles; t++)
        {
            var tile = field.DrawTile(random, n);
            var (cells, bodyStart, _) = MarchTile(field, tile, stats.Probes);
            Assert.True(field is VoidField || bodyStart < 0, $"{field.Name}: a body reported at cell {bodyStart}; only the void's density can be provably constant");
            stats.Tiles++;
            stats.BodyTiles += bodyStart >= 0 ? 1 : 0;
            var slopes = new List<float2>();
            for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
                slopes.Add(tile.PixelSlope(i, j, random.NextSingle(), random.NextSingle()));
            var inMask = new HashSet<int>(cells);
            var oracle = new List<int>();
            for (var c = 0; c < grid.Length - 1; c++)
            {
                var mid = (grid[c] + grid[c + 1]) * 0.5f;
                if (slopes.Any(s => field.Density(tile, s, mid, false, scratch) > 0.0f))
                {
                    oracle.Add(c);
                }
                else
                {
                    stats.OracleEmpty++;
                    stats.OracleEmptySkipped += inMask.Contains(c) ? 0 : 1;
                }
            }

            var ranged = new List<int>();
            if (range)
            {
                var inOracle = new HashSet<int>(oracle);
                for (var c = 0; c < grid.Length - 1; c++)
                {
                    var sampled = inOracle.Contains(c);
                    for (var a = 0; a < 5 && !sampled; a++)
                    for (var b = 0; b < 5 && !sampled; b++)
                    for (var k = 0; k < 9 && !sampled; k++)
                    {
                        var slope = new float2(tile.SlopesX.x + (tile.SlopesX.y - tile.SlopesX.x) * a / 4.0f, tile.SlopesY.x + (tile.SlopesY.y - tile.SlopesY.x) * b / 4.0f);
                        sampled = field.Density(tile, slope, grid[c] + (grid[c + 1] - grid[c]) * k / 8.0f, false, scratch) > 0.0f;
                    }

                    if (sampled)
                    {
                        ranged.Add(c);
                        stats.RangeViolations += inMask.Contains(c) ? 0 : 1;
                    }
                    else
                    {
                        stats.RangeEmpty++;
                        stats.RangeEmptySkipped += inMask.Contains(c) ? 0 : 1;
                    }
                }
            }

            var stop = bodyStart >= 0 ? bodyStart : grid.Length - 1;
            foreach (var slope in slopes)
            {
                var dense = 1.0f;
                for (var c = 0; c < grid.Length - 1 && dense >= 0.02f; c++, stats.DenseSteps++)
                    dense *= Integrate(field, tile, slope, grid[c], grid[c + 1], lod: false, stats.Dense);
                var masked = 1.0f;
                for (var c = 0; c < cells.Count && masked >= 0.02f; c++, stats.MaskedSteps++)
                    masked *= Integrate(field, tile, slope, grid[cells[c]], grid[cells[c] + 1], lod: false, stats.Masked);
                var exact = 1.0f;
                for (var c = 0; c < oracle.Count && exact >= 0.02f; c++)
                    exact *= Integrate(field, tile, slope, grid[oracle[c]], grid[oracle[c] + 1], lod: false, stats.Oracle);
                var rangedT = 1.0f;
                for (var c = 0; c < ranged.Count && rangedT >= 0.02f; c++)
                    rangedT *= Integrate(field, tile, slope, grid[ranged[c]], grid[ranged[c] + 1], lod: false, stats.Range);
                stats.Rays++;
                stats.MaxDifference = MathF.Max(stats.MaxDifference, MathF.Abs(dense - masked));
                stats.MaxOracleDifference = MathF.Max(stats.MaxOracleDifference, MathF.Abs(dense - exact));
                if (dense >= 0.02f)
                    stats.Unfinished++;
                if (lod)
                {
                    var (marched, _) = MarchLod((VoidField)field, tile, slope, cells, bodyStart, 0.02f, stats.Lod, ref stats.LodSteps, ref stats.BodySteps);
                    stats.MaxLodDifference = MathF.Max(stats.MaxLodDifference, MathF.Abs(dense - marched));
                    long ignoredSteps = 0, ignoredBody = 0;
                    var (_, atBody) = MarchLod((VoidField)field, tile, slope, cells, bodyStart, 0.0f, scratch, ref ignoredSteps, ref ignoredBody);
                    var reference = 1.0;
                    for (var c = 0; c < stop; c++)
                        reference *= Integrate(field, tile, slope, grid[c], grid[c + 1], lod: false, scratch);
                    var tauConv = ConvergedDepth(field, tile, slope, stop, 0.5f, lod: true);
                    var tauLod = -Math.Log(Math.Max(atBody, 1.0e-30f));
                    var tauRef = -Math.Log(Math.Max(reference, 1.0e-300));
                    var scale = Math.Max(tauConv, 1.0e-3);
                    stats.MaxLodDepthError = Math.Max(stats.MaxLodDepthError, Math.Abs(tauLod - tauConv) / scale);
                    stats.MaxRefDepthError = Math.Max(stats.MaxRefDepthError, Math.Abs(tauRef - tauConv) / scale);
                }
            }
        }

        return stats;
    }

    private void Report(string label, RunStats s) => output.WriteLine(
        $"IV-REPORT {label}: dense snoise/ray {s.PerRay(s.Dense.Snoise):F2} env/ray {s.PerRay(s.Dense.Envelope):F2} (samples/ray {s.PerRay(s.DenseSteps):F2}); "
        + $"tile snoise/ray {s.PerRay(s.Masked.Snoise + (double)s.Probes.Snoise):F2} env/ray {s.PerRay(s.Masked.Envelope + (double)s.Probes.Envelope):F2} grad/ray {s.PerRay(s.Probes.Grad):F2} "
        + $"(probes/tile env {(double)s.Probes.Envelope / s.Tiles:F1} snoise {(double)s.Probes.Snoise / s.Tiles:F2} grad {(double)s.Probes.Grad / s.Tiles:F2}; dense cells/ray {s.PerRay(s.MaskedSteps):F2}); "
        + $"cost ratio {s.Ratio:F2}x, oracle ceiling {s.OracleCeiling:F2}x, efficiency {s.Efficiency:F3}, cull fraction {s.CullFraction:F3} ({s.OracleEmptySkipped}/{s.OracleEmpty}), "
        + $"probe overhead {s.ProbeOverhead:F3}; max |dT| {s.MaxDifference:R}, oracle {s.MaxOracleDifference:R}; unfinished rays {s.Unfinished}"
        + (s.Ranged
            ? $"; range ceiling {s.RangeCeiling:F3} (oracle cost over the range mask's), efficiency against it {s.RangeEfficiency:F3}, range cull {s.RangeCull:F3} ({s.RangeEmptySkipped}/{s.RangeEmpty}), range violations {s.RangeViolations}"
            : ""));

    private void ReportVoid(string label, RunStats s) => output.WriteLine(
        $"IV-REPORT {label}: steps/px ref {s.PerRay(s.DenseSteps):F2}, masked {s.PerRay(s.MaskedSteps):F2}, LOD {s.PerRay(s.LodSteps):F2} ({(double)s.DenseSteps / s.LodSteps:F2}x fewer); "
        + $"snoise/px ref {s.PerRay(s.Dense.Snoise):F2}, masked {s.PerRay(s.Masked.Snoise):F2}, LOD {s.PerRay(s.Lod.Snoise):F2}; "
        + $"cost/px ref {s.PerRay(s.Dense.Cost):F2}, masked {s.PerRay(s.Masked.Cost + s.Probes.Cost):F2}, LOD {s.PerRay(s.Lod.Cost + s.Probes.Cost):F2} incl. probes (ref/LOD {s.LodRatio:F2}x, ref/masked {s.Ratio:F2}x); "
        + $"probes/tile env {(double)s.Probes.Envelope / s.Tiles:F1} snoise {(double)s.Probes.Snoise / s.Tiles:F2}; body tiles {s.BodyTiles}/{s.Tiles}, analytic steps/px {s.PerRay(s.BodySteps):F2}; "
        + $"depth error at the body start vs converged: LOD {s.MaxLodDepthError:G4}, fixed-step {s.MaxRefDepthError:G4} (ratio {s.MaxLodDepthError / s.MaxRefDepthError:F2}); "
        + $"end-of-ray max |dT| masked {s.MaxDifference:R}, LOD {s.MaxLodDifference:R}; unfinished rays {s.Unfinished}");

    /// <summary>
    /// The analytic body's soundness pin (ruling operator-analytic-wall-body). Over 2,000 seeded tiles of
    /// the (e) grid, the shipped void and LOD-far at N in {1, 4, 8, 16}, wherever the pre-pass reports a
    /// body start b: (1) the gated bound over [grid[b], grid end] is a positive point equal to the reported
    /// density, every cell from b to the end is in the mask, and at 64 random (pixel, sub-pixel, cell &gt;= b,
    /// depth in cell) points the full and the truncated Density and the ungated Exact field all equal it
    /// exactly, no tolerance; (2) BodyDepth, the optical depth MarchLod's one closed-form step uses, equals
    /// the summed fixed-step depth -ln Integrate of the body cells (no early-out, same cells) within 1e-4
    /// relative, compared in the depth domain so LOD-far's deep bodies do not underflow. (3) No tile of (a)-(d), at either warp, reports a body: their bounds are never a positive
    /// point.
    /// </summary>
    [Fact]
    public void AnalyticBodyMatchesFineMarch()
    {
        var random = new System.Random(0xB0D1);
        int[] sizes = { 1, 4, 8, 16 };
        var voids = new List<VoidField>();
        foreach (var rh in VoidField.HollowRadii)
        foreach (var offset in VoidField.CameraOffsets)
        foreach (var ramp in VoidField.Ramps)
            voids.Add(new VoidField(rh, ramp, offset.Fraction, offset.Name));
        voids.Add(VoidField.Shipped());
        voids.Add(VoidField.LodFar());
        var counts = new Counts();
        var bodies = new Dictionary<string, int>();
        for (var t = 0; t < 2000; t++)
        {
            var field = t % 4 == 0 ? voids[^2] : t % 4 == 1 ? voids[^1] : voids[random.Next(voids.Count)];
            var tile = field.DrawTile(random, sizes[random.Next(sizes.Length)]);
            var (cells, b, density) = MarchTile(field, tile, counts);
            if (b < 0)
                continue;
            bodies[field.Name] = bodies.GetValueOrDefault(field.Name) + 1;
            var grid = field.Grid;
            var last = grid.Length - 1;
            var rest = field.Bound(tile, grid[b], grid[last], gated: true, counts);
            Assert.True(rest.x == rest.y && rest.x > 0.0f && rest.x == density, $"{field.Name}: body at cell {b} with density {density:R}, but the bound over the rest is {rest}");
            Assert.Equal(Enumerable.Range(b, last - b), cells.Skip(cells.Count - (last - b)));
            for (var i = 0; i < 64; i++)
            {
                var slope = tile.PixelSlope(random.Next(tile.N), random.Next(tile.N), random.NextSingle(), random.NextSingle());
                var c = random.Next(b, last);
                var z = Uniform(random, grid[c], grid[c + 1]);
                foreach (var lod in new[] { false, true })
                {
                    var pointwise = field.Density(tile, slope, z, lod, counts);
                    var exact = field.Exact(tile, slope, z, lod);
                    Assert.True(pointwise == density && exact == density, $"{field.Name}: body from cell {b} claims {density:R}; at slope {slope}, depth {z:R} (cell {c}, lod {lod}) Density is {pointwise:R} and the ungated field {exact:R}");
                }
            }

            var ray = tile.PixelSlope(random.Next(tile.N), random.Next(tile.N), random.NextSingle(), random.NextSingle());
            var depth = BodyDepth(field, tile, ray, grid[b], lod: true, counts);
            var sum = 0.0;
            for (var c = b; c < last; c++)
                sum += -Math.Log(Integrate(field, tile, ray, grid[c], grid[c + 1], lod: true, counts));
            Assert.True(Math.Abs(depth - sum) <= 1.0e-4 * sum, $"{field.Name}: the body's depth from cell {b}, BodyDepth {depth:R}, differs from the fixed-step cells' summed depth {sum:R} over cells {b}..{last}");
        }

        output.WriteLine("IV-REPORT analytic body: tiles with a body " + string.Join(", ", bodies.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.True(bodies.ContainsKey(VoidField.Shipped().Name) && bodies.ContainsKey(VoidField.LodFar().Name), "no body proven at the shipped void or at LOD-far");

        var scenarios = new (float Warp, Func<float, Scenario> Make)[]
        {
            (FogField.AetheriaWarp, w => FogField.HeightFog(w, BoundMode.Interval)), (FogField.AetheriaWarp, w => FogField.InsideFog(w, BoundMode.Interval)),
            (FogField.AetheriaWarp, w => FogField.DeepWell(w, BoundMode.Interval)), (FogField.AetheriaWarp, w => FogField.DeepWell(w, BoundMode.Affine)),
            (FogField.AetheriaWarp, w => FogField.Wells(w, BoundMode.Interval)), (FogField.AetheriaWarp, w => FogField.Wells(w, BoundMode.Affine)),
            (SlabField.SlabWarp, SlabField.Create),
        };
        foreach (var (warp, make) in scenarios)
        foreach (var w in new[] { 0.0f, warp })
        {
            var field = make(w);
            for (var t = 0; t < 250; t++)
            {
                var (_, b, _) = MarchTile(field, field.DrawTile(random, sizes[random.Next(sizes.Length)]), counts);
                Assert.True(b < 0, $"{field.Name} warp {w:R}: a body reported at cell {b}");
            }
        }
    }

    /// <summary>
    /// The proving ground (docs/cultmath-interval-ground-cut.md, "Pass 3", "Pass 4" and "Pass 5b").
    /// Scenarios (a) height fog and (b) inside the fog under the centred-warp interval bound, (c) the deep
    /// well and (c-band) the shallow wells each under the centred-warp interval bound and the affine bound
    /// on the same seeds, and (d) r1's uniform slab, each over 64 tiles at N in {1, 4, 8, 16} and warp in
    /// {0, D}; (c) and (c-band) also print the range ceiling at N = 8, and no range-mask cell is skipped: the tile march integrates exactly the dense march's
    /// cells, so the transmittance agrees within 1e-3 (a difference would be a skipped cell that was not
    /// empty), the oracle march (each ray over the oracle mask: the cells whose mid-depth density is nonzero
    /// for at least one of the tile's rays) agrees with the dense march exactly, and no (a)-(d) tile reports
    /// a body. (e) the void: the grid over hollow radius, camera offset and ramp width (32 tiles at N = 8),
    /// LOD-far (32 tiles) and the one shipped void (200 tiles), three marches each, the footprint-aware one
    /// taking the analytic body step. Contracts at N = 8: (a) at least 2x combined cost, the lower of warp
    /// 0 and D; (c) (ruling affine-wells-contract-cheaper-than-interval) on the deep well the affine
    /// bound's combined cost ratio exceeds the centred interval's at warp 0 and at warp D, and, hard
    /// (ruling wells-overhead-plus-floor), the pre-pass proves at least half of the oracle-empty cells
    /// empty under both bounds at both warps; probe overhead, the efficiency against the oracle and against
    /// the range ceiling, the ceilings and the cull fraction are printed, not asserted; (c-band) is
    /// printed only; (e) at the shipped void the masked fixed-step march agrees with the reference exactly, the
    /// footprint-aware march's optical depth at the body start is off a converged march (h = 0.5, every
    /// cell) by at most twice the fixed-step march's error, and it takes at most half the reference's
    /// steps per pixel. A broken (c) contract fails the test; a shortfall of the (a) or (e) saving skips
    /// naming saving-2x-scenarios and every such shortfall. The fields, the grids and the envelope cost are
    /// not tuned toward either.
    /// </summary>
    [Fact]
    public void IntervalSkipHalvesEvaluations()
    {
        var atEight = new Dictionary<(string Key, float Warp), RunStats>();
        var scenarios = new (string Key, float Warp, Func<float, Scenario> Make)[]
        {
            ("a", FogField.AetheriaWarp, w => FogField.HeightFog(w, BoundMode.Interval)),
            ("b", FogField.AetheriaWarp, w => FogField.InsideFog(w, BoundMode.Interval)),
            ("c interval", FogField.AetheriaWarp, w => FogField.DeepWell(w, BoundMode.Interval)),
            ("c affine", FogField.AetheriaWarp, w => FogField.DeepWell(w, BoundMode.Affine)),
            ("c-band interval", FogField.AetheriaWarp, w => FogField.Wells(w, BoundMode.Interval)),
            ("c-band affine", FogField.AetheriaWarp, w => FogField.Wells(w, BoundMode.Affine)),
            ("d", SlabField.SlabWarp, SlabField.Create),
        };
        foreach (var (key, warp, make) in scenarios)
        foreach (var n in new[] { 1, 4, 8, 16 })
        foreach (var w in new[] { 0.0f, warp })
        {
            var field = make(w);
            var stats = Run(field, n, 64, 0x5A7E + n, range: n == 8 && key.StartsWith('c'));
            Report($"{field.Name} N={n} warp={w:R}", stats);
            Assert.True(stats.RangeViolations == 0, $"{field.Name} N={n} warp={w:R}: the pre-pass skipped {stats.RangeViolations} cells where a sampled density is positive");
            Assert.True(stats.MaxDifference <= 1.0e-3f, $"{field.Name} N={n} warp={w:R}: transmittance differs by {stats.MaxDifference:R}");
            Assert.True(stats.MaxOracleDifference == 0.0f, $"{field.Name} N={n} warp={w:R}: the oracle march differs from the dense march by {stats.MaxOracleDifference:R}");
            if (key == "d")
                output.WriteLine($"IV-REPORT (d) N={n} warp={w:R}: ungated dense snoise/ray {3.0 * stats.PerRay(stats.DenseSteps):F2} (three per dense sample, as r1 counted)");
            if (n == 8)
                atEight[(key, w)] = stats;
        }

        var warps = new[] { 0.0f, FogField.AetheriaWarp };
        var costA = warps.Min(w => atEight[("a", w)].Ratio);
        var cullFloor = double.MaxValue;
        var gains = new List<string>();
        var shortfalls = new List<string>();
        var broken = new List<string>();
        foreach (var w in warps)
        {
            var interval = atEight[("c interval", w)];
            var affine = atEight[("c affine", w)];
            foreach (var (mode, stats) in new[] { ("interval", interval), ("affine", affine) })
            {
                Assert.True(stats.CullFraction >= 0.5, $"(c) {mode} warp={w:R}: the pre-pass proves {stats.CullFraction:F3} of the oracle-empty cells empty, under the 0.5 floor (ruling wells-overhead-plus-floor)");
                cullFloor = Math.Min(cullFloor, stats.CullFraction);
            }

            var band = (atEight[("c-band interval", w)], atEight[("c-band affine", w)]);
            gains.Add($"warp {w:R}: (c) affine {affine.Ratio:F2}x against centred interval {interval.Ratio:F2}x ({affine.Ratio / interval.Ratio:F2}x), probe overhead {affine.ProbeOverhead:F3} and {interval.ProbeOverhead:F3}, "
                + $"efficiency {affine.Efficiency:F3} and {interval.Efficiency:F3} against the oracle, {affine.RangeEfficiency:F3} and {interval.RangeEfficiency:F3} against the range ceiling {affine.RangeCeiling:F3}; "
                + $"(c-band) {band.Item2.Ratio:F2}x against {band.Item1.Ratio:F2}x ({band.Item2.Ratio / band.Item1.Ratio:F2}x)");
            if (!(affine.Ratio > interval.Ratio))
                broken.Add($"(c) warp {w:R}: affine {affine.Ratio:F2}x is not cheaper than the centred interval's {interval.Ratio:F2}x");
        }

        foreach (var rh in VoidField.HollowRadii)
        foreach (var offset in VoidField.CameraOffsets)
        foreach (var ramp in VoidField.Ramps)
        {
            var field = new VoidField(rh, ramp, offset.Fraction, offset.Name);
            ReportVoid($"{field.Name} N=8", Run(field, 8, 32, 0x701D, lod: true));
        }

        var lodFar = VoidField.LodFar();
        ReportVoid($"{lodFar.Name} N=8 (LOD far)", Run(lodFar, 8, 32, 0x701D, lod: true));

        var shipped = VoidField.Shipped();
        var headline = Run(shipped, 8, 200, 0x5417, lod: true);
        ReportVoid($"{shipped.Name} N=8 (headline, 200 tiles)", headline);
        Assert.True(headline.MaxDifference == 0.0f, $"the masked fixed-step march differs from the reference by {headline.MaxDifference:R}");
        Assert.True(headline.MaxLodDepthError <= 2.0 * headline.MaxRefDepthError, $"at the body start the footprint-aware march's optical depth is off the converged march's by {headline.MaxLodDepthError:G6} relative, more than twice the fixed-step march's {headline.MaxRefDepthError:G6}");
        var steps = (double)headline.DenseSteps / headline.LodSteps;

        if (costA < 2.0)
            shortfalls.Add($"(a) saves {costA:F2}x combined cost, under 2x");
        if (steps < 2.0)
            shortfalls.Add($"(e) takes {steps:F2}x fewer steps per pixel at the shipped void, under 2x");
        output.WriteLine($"IV-REPORT contracts at N=8: (a) {costA:F2}x cost (lower of warp 0 and D, at least 2x); (c) affine cheaper than the centred interval at both warps, cull fraction {cullFloor:F3} (at least 0.5 under both bounds, hard); "
            + $"(e) {steps:F2}x fewer steps/px, depth error at the body start LOD {headline.MaxLodDepthError:G4} vs fixed-step {headline.MaxRefDepthError:G4} (at most 2x)");
        foreach (var line in gains)
            output.WriteLine($"IV-REPORT (c) at N=8, {line}");
        Assert.True(broken.Count == 0, "ruling affine-wells-contract-cheaper-than-interval: " + string.Join("; ", broken));
        if (shortfalls.Count > 0)
            Assert.Skip("saving-2x-scenarios: " + string.Join("; ", shortfalls));
    }
}
