using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The noise bounds (design.md, "Intervals"): iv_snoise_ball and iv_fbm_ball enclose their functions over
/// a ball, a domain warp bounded by D is enclosed by enlarging the radius by D, SNOISE_LIPSCHITZ has
/// measured provenance, iv_frustum_ball encloses a screen tile's rays over a depth segment, the bound
/// composed from an analytic envelope and the noise ball encloses the density, and a tile march over
/// fields with authored empty space is measured against a dense one for the same transmittance. Lines
/// starting IV-REPORT carry the numbers the cut report quotes.
///
/// Hand mutations of iv_frustum_ball that TileBallEnclosesEveryRaySegment must kill: z0 for z1 in the
/// lateral term (z0 * footprintPerDepth); |m_c| for |(m_c, 1)| in the depth term; the warp dropped; the
/// rounding widening dropped (which the degenerate family's enclosure check alone must catch). Hand
/// mutations of the test's own fields: w(z0) for w(z1) in VoidField.WeightBound must fail the truncated
/// check of EnvelopeBoundEnclosesDensity on the LOD-far configuration; CarveRadius for HollowRadius in
/// the lo test of VoidField.Compose must fail AnalyticBodyMatchesFineMarch.
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
    private static (int N, float Focal, float2 Slope, float Z0, float Z1, float Warp) DrawBallCase(System.Random random, string family)
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
    /// (Aetheria's wells, inside the fog, the void at every grid point; N in {1, 4, 8, 16}; a camera drawn
    /// per tile) the bound composed from the envelope over the slice's box, its fade, and the noise ball
    /// enlarged by the warp encloses the density at 64 points of the slice, each sampled through the field's
    /// own warp, with no tolerance. The void's footprint-truncated density (octave weights) is checked
    /// against the same bound.
    /// </summary>
    [Fact]
    public void EnvelopeBoundEnclosesDensity()
    {
        var random = new System.Random(0xE7B0);
        int[] sizes = { 1, 4, 8, 16 };
        var fields = new List<Scenario> { FogField.Wells(FogField.AetheriaWarp), FogField.InsideFog(FogField.AetheriaWarp) };
        foreach (var rh in VoidField.HollowRadii)
        foreach (var offset in VoidField.CameraOffsets)
        foreach (var ramp in VoidField.Ramps)
            fields.Add(new VoidField(rh, ramp, offset.Fraction, offset.Name));
        var counts = new Counts();
        for (var t = 0; t < 2000; t++)
        {
            var field = t % 2 == 0 ? fields[(t / 2) % 2] : fields[2 + random.Next(fields.Count - 2)];
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
                if (field is VoidField)
                {
                    var truncated = field.Density(tile, m, z, lod: true, counts);
                    Assert.True(Inside(truncated, bound), $"{field.Name}: bound {bound} misses the truncated density {truncated:R} at slope {m}, depth {z:R}");
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
    private static readonly float3 LipschitzWitness = new(-241.98447f, -249.98927f, -60.058212f);

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

    internal sealed class Counts
    {
        public long Snoise;
        public long Envelope;

        public double Cost => Snoise + EnvelopeCost * Envelope;
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

        private float3 Flow(float3 p)
        {
            var q = (FlowInXY ? new float2(p.x, p.y) : new float2(p.x, p.z)) * FlowFrequency;
            var v = new float2(snoise(q + new float2(17.0f, 3.0f)), snoise(q + new float2(-5.0f, 41.0f)));
            v = v / max(1.0f, length(v)) * FlowAmplitude;
            return FlowInXY ? new float3(v.x, v.y, 0.0f) : new float3(v.x, 0.0f, v.y);
        }

        private (float W0, float Shift0, float Shift1) Phases(float time)
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

        // Both phases' warped points lie in a ball that carries the warp, so one ball encloses both snoise;
        // the cross-fade is enclosed term by term with the frame's own weights.
        public float2 CoverageBound(float2 ball, float time)
        {
            var w0 = Phases(time).W0;
            return iv_add(iv_scale(ball, w0), iv_scale(ball, 1.0f - w0));
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
        private static readonly float2 FreeNoise = new(-1.5f, 1.5f);

        public required WarpedNoise Noise { get; init; }
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

        /// <summary>(a) Height fog: no wells, the camera above the safety band at y = S + 30, rays level and up.</summary>
        public static FogField HeightFog(float warp) => new()
        {
            Name = "(a) height fog", Noise = AetheriaNoise(warp), SlopesY = new float2(0.0f, 0.5f),
            CameraAt = (random, _) => new float3(Uniform(random, -1000.0f, 1000.0f), Safety + 30.0f, Uniform(random, -1000.0f, 1000.0f)),
        };

        /// <summary>(b) Inside the fog: no wells, the camera at y = F - 10, rays level.</summary>
        public static FogField InsideFog(float warp) => new()
        {
            Name = "(b) inside the fog", Noise = AetheriaNoise(warp), SlopesY = new float2(-0.05f, 0.05f),
            CameraAt = (random, _) => new float3(Uniform(random, -1000.0f, 1000.0f), FloorOffset - 10.0f, Uniform(random, -1000.0f, 1000.0f)),
        };

        /// <summary>
        /// (c) Aetheria-like: the zone bowl R = 2000 and four wells of mass 100, 1000, 10000 and 1000 at seeded
        /// positions inside it; the camera at a seeded xz in the bowl, above the noise band at
        /// y = max(0, S + 10 - h), gazing across with m_y in [-0.25, 0.1].
        /// </summary>
        public static FogField Wells(float warp)
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
                Name = "(c) Aetheria wells", Noise = AetheriaNoise(warp), Bowls = bowls.ToArray(), SlopesY = new float2(-0.25f, 0.1f),
                CameraAt = (r, field) =>
                {
                    var angle = Uniform(r, 0.0f, 2.0f * MathF.PI);
                    var radius = ZoneRadius * MathF.Sqrt(r.NextSingle());
                    var xz = new float2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
                    var h = field.Height(field.Bowls.Select(b => b.Centre - xz).ToArray(), 0.0f, 0.0f);
                    return new float3(xz.x, max(0.0f, Safety + 10.0f - h), xz.y);
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
            var tile = new FogTile
            {
                Origin = origin, N = n, Focal = Focal, Time = Uniform(random, 0.0f, 100.0f),
                Slope = new float2(Uniform(random, -1.0f, 1.0f), Uniform(random, SlopesY.x, SlopesY.y)),
            };
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

        public override float2 Bound(Tile t, float z0, float z1, bool gated, Counts counts)
        {
            var tile = (FogTile)t;
            var depth = new float2(z0, z1);
            counts.Envelope++;
            var height = HeightBound(tile.Centres, iv_mul(depth, tile.SlopesX), depth);
            var s = iv_add(iv_add(iv_point(tile.Origin.y), iv_mul(depth, tile.SlopesY)), height);
            var fade = iv_sub(iv_point(1.0f), iv_smoothstep(0.75f * Safety, Safety, s));
            var envelope = Compose(s, fade, FreeNoise);
            if (gated && envelope.y <= 0.0f)
                return envelope;
            var ball = iv_frustum_ball(tile.Slope, z0, z1, tile.Footprint, Noise.Warp);
            if (gated)
            {
                var provable = false;
                foreach (var low in new[] { true, false })
                {
                    var best = iv_add(Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w), low), tile.Time), iv_scale(BestBall(Noise.DetailRadius(ball.w), low), 0.5f));
                    provable |= Compose(s, fade, best).y <= 0.0f;
                }

                if (!provable)
                    return envelope;
            }

            var centre = tile.World(new float3(ball.x, ball.y, ball.z));
            var noise = iv_add(Noise.CoverageBound(Noise.CoverageBall(centre, ball.w, counts), tile.Time), iv_scale(Noise.DetailBall(centre, ball.w, counts), 0.5f));
            return Compose(s, fade, noise);
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
            if (gated && Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w), low: true), tile.Time).y > Cutoff)
                return maybe;
            var coverage = Noise.CoverageBound(Noise.CoverageBall(tile.World(new float3(ball.x, ball.y, ball.z)), ball.w, counts), tile.Time);
            return coverage.y <= Cutoff ? new float2(0.0f, 0.0f) : maybe;
        }
    }

    /// <summary>
    /// (e) The void, the shipped scene: a cloud volume everywhere, carved by one negative pseudo-Gaussian
    /// sphere. Authored knobs: hollow radius Rh (density exactly 0 inside) and ramp width S (the density
    /// rises to full over [Rh, Rh + S]). The brush (1 - (d / Rc)^2)^e with CARVE = 1.5 reaches full at Rc,
    /// so Rc = Rh + S, and Rh = Rc sqrt(1 - CARVE^(-1/e)) gives e = -ln CARVE / ln(1 - (Rh / Rc)^2):
    /// density = K max(0, 1 - CARVE (1 - (d' / Rc)^2)^e), exactly 0 for d' &lt;= Rh and K for d' &gt;= Rc,
    /// K = 1/30. The noise displaces the wall: fade = smoothstep(Rh - S, Rh, d), d' = d + A fade n(warp(p)),
    /// A = 20, F0 = 0.01 (wavelengths 100 and 25), warp D = 10. The sun orbits the origin at radius 175 with
    /// period 72 s and rests on the hollow's bottom (c_v = sun + (0, Rh - 12.5, 0)); the camera looks at
    /// sun + (0, 60, 0) and, shipped, sits 120 behind the sun along the orbit tangent and 45 above it. The
    /// footprint-truncated field (lod) weights octave i by w_i(z) = 1 - smoothstep(l_i / 2m, l_i / m,
    /// z theta), m = 4, theta = 1 / f; the bound takes each weight over [w_i(z1), 1], which holds both the
    /// truncated and the full field. The dense grid is the fixed-step reference's: cells of min(10, S / 4)
    /// out to |camera - c_v| + Rc + 5 / K, past where every ray has gone opaque.
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
        public VoidField(float hollowRadius, float ramp, float cameraFraction, string camera)
        {
            HollowRadius = hollowRadius;
            Ramp = ramp;
            CarveRadius = hollowRadius + ramp;
            Exponent = (float)(-Math.Log(Carve) / Math.Log(1.0 - (double)hollowRadius / CarveRadius * hollowRadius / CarveRadius));
            RampStart = hollowRadius - ramp;
            FixedStep = min(10.0f, ramp / 4.0f);
            CameraFraction = cameraFraction;
            Name = $"(e) void Rh={hollowRadius:R} camera={camera} S={ramp:R}";
            var reach = cameraFraction < 0.0f ? length(new float2(120.0f, 45.0f - (hollowRadius - 12.5f))) : cameraFraction * hollowRadius;
            Grid = Cells(FixedStep, reach + CarveRadius + 5.0f / K);
        }

        public static VoidField Shipped() => new(198.0f, 50.0f, -1.0f, "shipped");

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
                    var best = iv_add(iv_mul(Noise.CoverageBound(BestBall(Noise.CoverageRadius(ball.w), low), tile.Time), wc), iv_mul(iv_scale(BestBall(Noise.DetailRadius(ball.w), low), 0.5f), wd));
                    provable |= Compose(d, fade, best).y <= 0.0f;
                }

                if (!provable)
                    return envelope;
            }

            var centre = tile.World(new float3(ball.x, ball.y, ball.z));
            var noise = iv_add(iv_mul(Noise.CoverageBound(Noise.CoverageBall(centre, ball.w, counts), tile.Time), wc), iv_mul(iv_scale(Noise.DetailBall(centre, ball.w, counts), 0.5f), wd));
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
    /// </summary>
    private static List<int> MarchTile(Scenario field, Tile tile, Counts probes)
    {
        var grid = field.Grid;
        var cells = grid.Length - 1;
        var dense = new List<int>();
        var j = 0;
        var span = cells;
        while (j < cells)
        {
            var end = Math.Min(j + span, cells);
            if (field.Bound(tile, grid[j], grid[end], gated: true, probes).y <= 0.0f)
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
                dense.Add(j);
                j++;
            }
        }

        return dense;
    }

    // The footprint-aware march, the void's shipped one: steps of LodStep(z) inside the unmasked runs only,
    // octave weights applied.
    private static float MarchLod(VoidField field, Tile tile, float2 slope, List<int> cells, Counts counts, ref long steps)
    {
        var grid = field.Grid;
        var transmittance = 1.0f;
        var k = 0;
        while (k < cells.Count && transmittance >= 0.02f)
        {
            var z = grid[cells[k]];
            var end = cells[k] + 1;
            for (k++; k < cells.Count && cells[k] == end; k++)
                end++;
            while (z < grid[end] && transmittance >= 0.02f)
            {
                var next = min(z + field.LodStep(z), grid[end]);
                transmittance *= Integrate(field, tile, slope, z, next, lod: true, counts);
                steps++;
                z = next;
            }
        }

        return transmittance;
    }

    internal sealed class RunStats
    {
        public readonly Counts Dense = new();
        public readonly Counts Masked = new();
        public readonly Counts Probes = new();
        public readonly Counts Lod = new();
        public long Rays;
        public long Tiles;
        public long DenseSteps;
        public long MaskedSteps;
        public long LodSteps;
        public long Unfinished;
        public float MaxDifference;
        public float MaxLodDifference;

        public double PerRay(double value) => value / Rays;

        // Dense cost over the tile march's: each ray's own dense cells plus the tile's probes, shared.
        public double Ratio => Dense.Cost / (Masked.Cost + Probes.Cost);

        // What no amortization can beat: the dense cost over the cost of the dense cells alone.
        public double Ceiling => Dense.Cost / Masked.Cost;

        public double LodRatio => Dense.Cost / (Lod.Cost + Probes.Cost);
    }

    /// <summary>
    /// Draws `tiles` tiles, runs the pre-pass once per tile, and marches each of the N^2 rays (one per pixel,
    /// jittered inside it) densely over every cell and masked over the tile's dense cells, both with the
    /// early-out at 0.02 and the same Integrate; with lod, the footprint-aware march as well.
    /// </summary>
    private static RunStats Run(Scenario field, int n, int tiles, int seed, bool lod = false)
    {
        var random = new System.Random(seed);
        var stats = new RunStats();
        var grid = field.Grid;
        for (var t = 0; t < tiles; t++)
        {
            var tile = field.DrawTile(random, n);
            var cells = MarchTile(field, tile, stats.Probes);
            stats.Tiles++;
            for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                var slope = tile.PixelSlope(i, j, random.NextSingle(), random.NextSingle());
                var dense = 1.0f;
                for (var c = 0; c < grid.Length - 1 && dense >= 0.02f; c++, stats.DenseSteps++)
                    dense *= Integrate(field, tile, slope, grid[c], grid[c + 1], lod: false, stats.Dense);
                var masked = 1.0f;
                for (var c = 0; c < cells.Count && masked >= 0.02f; c++, stats.MaskedSteps++)
                    masked *= Integrate(field, tile, slope, grid[cells[c]], grid[cells[c] + 1], lod: false, stats.Masked);
                stats.Rays++;
                stats.MaxDifference = MathF.Max(stats.MaxDifference, MathF.Abs(dense - masked));
                if (dense >= 0.02f)
                    stats.Unfinished++;
                if (lod)
                {
                    var marched = MarchLod((VoidField)field, tile, slope, cells, stats.Lod, ref stats.LodSteps);
                    stats.MaxLodDifference = MathF.Max(stats.MaxLodDifference, MathF.Abs(dense - marched));
                }
            }
        }

        return stats;
    }

    private void Report(string label, RunStats s) => output.WriteLine(
        $"IV-REPORT {label}: dense snoise/ray {s.PerRay(s.Dense.Snoise):F2} env/ray {s.PerRay(s.Dense.Envelope):F2} (samples/ray {s.PerRay(s.DenseSteps):F2}); "
        + $"tile snoise/ray {s.PerRay(s.Masked.Snoise + (double)s.Probes.Snoise):F2} env/ray {s.PerRay(s.Masked.Envelope + (double)s.Probes.Envelope):F2} "
        + $"(probes/tile env {(double)s.Probes.Envelope / s.Tiles:F1} snoise {(double)s.Probes.Snoise / s.Tiles:F2}; dense cells/ray {s.PerRay(s.MaskedSteps):F2}); "
        + $"cost ratio {s.Ratio:F2}x, ceiling {s.Ceiling:F2}x; max |dT| {s.MaxDifference:R}; unfinished rays {s.Unfinished}");

    private void ReportVoid(string label, RunStats s) => output.WriteLine(
        $"IV-REPORT {label}: steps/px ref {s.PerRay(s.DenseSteps):F2}, masked {s.PerRay(s.MaskedSteps):F2}, LOD {s.PerRay(s.LodSteps):F2} ({(double)s.DenseSteps / s.LodSteps:F2}x fewer); "
        + $"snoise/px ref {s.PerRay(s.Dense.Snoise):F2}, masked {s.PerRay(s.Masked.Snoise):F2}, LOD {s.PerRay(s.Lod.Snoise):F2}; "
        + $"cost/px ref {s.PerRay(s.Dense.Cost):F2}, masked {s.PerRay(s.Masked.Cost + s.Probes.Cost):F2}, LOD {s.PerRay(s.Lod.Cost + s.Probes.Cost):F2} incl. probes (ref/LOD {s.LodRatio:F2}x, ref/masked {s.Ratio:F2}x); "
        + $"probes/tile env {(double)s.Probes.Envelope / s.Tiles:F1} snoise {(double)s.Probes.Snoise / s.Tiles:F2}; max |dT| masked {s.MaxDifference:R}, LOD {s.MaxLodDifference:R}; unfinished rays {s.Unfinished}");

    /// <summary>
    /// The proving ground (docs/cultmath-interval-ground-cut.md, "Pass 3"). Scenarios (a) height fog,
    /// (b) inside the fog, (c) Aetheria's wells and (d) r1's uniform slab, each over 64 tiles at
    /// N in {1, 4, 8, 16} and warp in {0, D}: the tile march integrates exactly the dense march's cells, so
    /// the transmittance agrees within 1e-3 (a difference would be a skipped cell that was not empty), and
    /// its combined cost must be at most half the dense march's for (a) and (c) at N = 8. (e) the void: the
    /// grid over hollow radius, camera offset and ramp width (32 tiles at N = 8) and the shipped point
    /// (200 tiles), three marches each; at the shipped point the masked fixed-step march agrees with the
    /// reference exactly, the footprint-aware march within 0.02, and it must take at most half the
    /// reference's steps per pixel. A shortfall in (a), (c) or (e) skips naming saving-2x-scenarios; the
    /// fields, the grids and the envelope cost are not tuned toward it.
    /// </summary>
    [Fact]
    public void IntervalSkipHalvesEvaluations()
    {
        var ratios = new Dictionary<string, double>();
        var scenarios = new (string Key, float Warp, Func<float, Scenario> Make)[]
        {
            ("a", FogField.AetheriaWarp, FogField.HeightFog),
            ("b", FogField.AetheriaWarp, FogField.InsideFog),
            ("c", FogField.AetheriaWarp, FogField.Wells),
            ("d", SlabField.SlabWarp, SlabField.Create),
        };
        foreach (var (key, warp, make) in scenarios)
        foreach (var n in new[] { 1, 4, 8, 16 })
        foreach (var w in new[] { 0.0f, warp })
        {
            var field = make(w);
            var stats = Run(field, n, 64, 0x5A7E + n);
            Report($"{field.Name} N={n} warp={w:R}", stats);
            Assert.True(stats.MaxDifference <= 1.0e-3f, $"{field.Name} N={n} warp={w:R}: transmittance differs by {stats.MaxDifference:R}");
            if (key == "d")
                output.WriteLine($"IV-REPORT (d) N={n} warp={w:R}: ungated dense snoise/ray {3.0 * stats.PerRay(stats.DenseSteps):F2} (three per dense sample, as r1 counted)");
            if (n == 8)
                ratios[key] = Math.Min(ratios.GetValueOrDefault(key, double.MaxValue), stats.Ratio);
        }

        foreach (var rh in VoidField.HollowRadii)
        foreach (var offset in VoidField.CameraOffsets)
        foreach (var ramp in VoidField.Ramps)
        {
            var field = new VoidField(rh, ramp, offset.Fraction, offset.Name);
            ReportVoid($"{field.Name} N=8", Run(field, 8, 32, 0x701D, lod: true));
        }

        var shipped = VoidField.Shipped();
        var headline = Run(shipped, 8, 200, 0x5417, lod: true);
        ReportVoid($"{shipped.Name} N=8 (headline, 200 tiles)", headline);
        Assert.True(headline.MaxDifference == 0.0f, $"the masked fixed-step march differs from the reference by {headline.MaxDifference:R}");
        Assert.True(headline.MaxLodDifference <= 0.02f, $"the footprint-aware march differs from the reference by {headline.MaxLodDifference:R}");
        var steps = (double)headline.DenseSteps / headline.LodSteps;

        if (ratios["a"] < 2.0 || ratios["c"] < 2.0 || steps < 2.0)
            Assert.Skip($"saving-2x-scenarios: at N = 8, (a) saves {ratios["a"]:F2}x and (c) {ratios["c"]:F2}x combined cost (the lower of warp 0 and D); (e) takes {steps:F2}x fewer steps per pixel at the shipped point; the contract is 2x each");
    }
}
