using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The affine forms of math.Affine.cs (design.md, "Affine forms"). A form (x0, a, e) is the set
/// x0 + a eps + e delta at the region's shared eps; every check evaluates that set in double and the
/// pointwise function in float32, with no tolerance. Operand values are float32 values drawn inside
/// their operands' sets at one shared eps, endpoints (eps and delta at -1 and 1) included.
/// </summary>
public sealed class AffineTests
{
    private readonly ITestOutputHelper output;

    public AffineTests(ITestOutputHelper output) => this.output = output;

    private static float Uniform(System.Random random, float lo, float hi) => lo + (hi - lo) * random.NextSingle();

    private static float LogUniform(System.Random random, float lo, float hi) =>
        MathF.Exp(Uniform(random, MathF.Log(lo), MathF.Log(hi)));

    private static float Signed(System.Random random, float magnitude) => random.Next(2) == 0 ? -magnitude : magnitude;

    // Whether value lies in the form's set at eps, in double.
    private static bool InForm(float3 form, float eps, double value) =>
        Math.Abs(value - (form.x + (double)form.y * eps)) <= form.z;

    // A form with its centre in [-20, 20] and |a| and e log-uniform over [1e-5, 20]; one in eight has
    // a = 0 and one in eight e = 0.
    private static float3 DrawForm(System.Random random)
    {
        var x0 = Uniform(random, -20.0f, 20.0f);
        var a = random.Next(8) == 0 ? 0.0f : Signed(random, LogUniform(random, 1.0e-5f, 20.0f));
        var e = random.Next(8) == 0 ? 0.0f : LogUniform(random, 1.0e-5f, 20.0f);
        return new float3(x0, a, e);
    }

    private static float2 DrawInterval(System.Random random)
    {
        var lo = Uniform(random, -20.0f, 20.0f);
        return new float2(lo, lo + (random.Next(8) == 0 ? 0.0f : LogUniform(random, 1.0e-5f, 40.0f)));
    }

    // The tiny family: magnitudes from 1e-21 to 1e-17, products near and under 2^-126 = 1.2e-38, where a
    // float32 result rounds to a subnormal and the absorption S 2^-20 is smaller than that loss.
    private static float3 DrawTiny(System.Random random) =>
        new(random.Next(8) == 0 ? 0.0f : Signed(random, LogUniform(random, 1.0e-21f, 1.0e-17f)),
            random.Next(4) == 0 ? 0.0f : Signed(random, LogUniform(random, 1.0e-21f, 1.0e-17f)),
            random.Next(4) == 0 ? 0.0f : LogUniform(random, 1.0e-21f, 1.0e-17f));

    private static float2 DrawTinyInterval(System.Random random)
    {
        var lo = Signed(random, LogUniform(random, 1.0e-21f, 1.0e-17f));
        return new float2(lo, lo + (random.Next(8) == 0 ? 0.0f : LogUniform(random, 1.0e-21f, 1.0e-17f)));
    }

    // A float32 value in the form's set at eps, delta at an endpoint when asked; NaN when rounding leaves
    // no float of the set near the drawn point (a set narrower than an ulp).
    private static float Member(System.Random random, float3 form, float eps, bool endpoint)
    {
        var centre = form.x + (double)form.y * eps;
        var delta = endpoint ? (random.Next(2) == 0 ? -1.0 : 1.0) : Uniform(random, -1.0f, 1.0f);
        var value = (float)(centre + form.z * delta);
        for (var k = 0; k < 4 && !InForm(form, eps, value); k++)
            value = value > centre ? MathF.BitDecrement(value) : MathF.BitIncrement(value);
        return InForm(form, eps, value) ? value : float.NaN;
    }

    private static float MemberOf(System.Random random, float2 interval, bool endpoint) =>
        endpoint ? (random.Next(2) == 0 ? interval.x : interval.y) : Math.Clamp(Uniform(random, interval.x, interval.y), interval.x, interval.y);

    private readonly record struct Operands(float3 X, float3 Y, float2 Iv, float S);

    private readonly record struct Values(float X, float Y, float V, float S);

    // One op under the harness: Build gives the result's membership test (eps, value) from the operand
    // forms, F the pointwise float32 function of operand values.
    private sealed record Op(string Name, Func<Operands, Func<float, double, bool>> Build, Func<Values, float> F);

    private static Func<float, double, bool> Form(float3 form) => (eps, value) => InForm(form, eps, value);

    private static Func<float, double, bool> Range(float2 range) => (_, value) => value >= range.x && value <= range.y;

    private static readonly Op[] Ops =
    {
        new("af_point", o => Form(af_point(o.X.x)), v => v.S),
        new("af_symbol", o => Form(af_symbol(o.X.x, o.X.y)), v => v.S),
        new("af_from_iv", o => Form(af_from_iv(o.Iv)), v => v.V),
        new("af_range", o => Range(af_range(o.X)), v => v.X),
        new("af_add", o => Form(af_add(o.X, o.Y)), v => v.X + v.Y),
        new("af_sub", o => Form(af_sub(o.X, o.Y)), v => v.X - v.Y),
        new("af_neg", o => Form(af_neg(o.X)), v => -v.X),
        new("af_scale", o => Form(af_scale(o.X, o.S)), v => v.X * v.S),
        new("af_add_iv", o => Form(af_add_iv(o.X, o.Iv)), v => v.X + v.V),
        new("af_mul", o => Form(af_mul(o.X, o.Y)), v => v.X * v.Y),
    };

    // Runs one op over `forms` seeded operand draws x 16 (eps, delta) draws; returns the misses and up to
    // four witnesses. The first four draws per form put eps at an endpoint, the first eight delta.
    // af_point and af_symbol are checked on their defining value: the point x0 itself, and x0 + a eps in
    // double (the symbol defines eps, so it has no float32 evaluation to enclose).
    private static (int Misses, List<string> Witness) Harness(Op op, int seed, int forms = 10_000, Func<System.Random, float3>? draw = null, bool tiny = false)
    {
        draw ??= tiny ? DrawTiny : DrawForm;
        var random = new System.Random(seed);
        var misses = 0;
        var witness = new List<string>();
        for (var t = 0; t < forms; t++)
        {
            var operands = new Operands(draw(random), draw(random), tiny ? DrawTinyInterval(random) : DrawInterval(random),
                random.Next(8) == 0 ? 0.0f : tiny ? Signed(random, LogUniform(random, 1.0e-32f, 1.0e-19f)) : Uniform(random, -4.0f, 4.0f));
            var contains = op.Build(operands);
            for (var d = 0; d < 16; d++)
            {
                var eps = d < 4 ? (d % 2 == 0 ? -1.0f : 1.0f) : Uniform(random, -1.0f, 1.0f);
                var endpoint = d < 8;
                var x = Member(random, operands.X, eps, endpoint);
                var y = Member(random, operands.Y, eps, endpoint);
                if (float.IsNaN(x) || float.IsNaN(y))
                    continue;
                var v = MemberOf(random, operands.Iv, endpoint);
                double f = op.Name switch
                {
                    "af_point" => operands.X.x,
                    "af_symbol" => operands.X.x + (double)operands.X.y * eps,
                    _ => op.F(new Values(x, y, v, operands.S)),
                };
                if (!contains(eps, f))
                {
                    misses++;
                    if (witness.Count < 4)
                        witness.Add($"{op.Name}: X {operands.X}, Y {operands.Y}, iv {operands.Iv}, s {operands.S:R}; eps {eps:R}, values ({x:R}, {y:R}, {v:R}) give {f:R}");
                }
            }
        }

        return (misses, witness);
    }

    [Fact]
    public void EveryOpEnclosesItsPoints()
    {
        var failures = new List<string>();
        foreach (var op in Ops)
        {
            var (misses, witness) = Harness(op, 0xAF00);
            if (misses > 0)
                failures.Add($"{op.Name}: {misses} misses" + Environment.NewLine + string.Join(Environment.NewLine, witness));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static float3 Shrink(float3 form) => new(form.x, form.y, form.z > 0.0f ? MathF.BitDecrement(form.z) : 0.0f);

    // Each op with its rounding absorption removed: the propagated error alone, computed in float32.
    private static readonly Op[] Unabsorbed =
    {
        new("af_from_iv", o => Form(new float3((o.Iv.x + o.Iv.y) * 0.5f, 0.0f, (o.Iv.y - o.Iv.x) * 0.5f)), v => v.V),
        new("af_range", o => Range(new float2(o.X.x - (MathF.Abs(o.X.y) + o.X.z), o.X.x + (MathF.Abs(o.X.y) + o.X.z))), v => v.X),
        new("af_add", o => Form(new float3(o.X.x + o.Y.x, o.X.y + o.Y.y, o.X.z + o.Y.z)), v => v.X + v.Y),
        new("af_sub", o => Form(new float3(o.X.x - o.Y.x, o.X.y - o.Y.y, o.X.z + o.Y.z)), v => v.X - v.Y),
        new("af_scale", o => Form(new float3(o.X.x * o.S, o.X.y * o.S, o.X.z * MathF.Abs(o.S))), v => v.X * v.S),
        new("af_add_iv", o => Form(new float3(o.X.x + (o.Iv.x + o.Iv.y) * 0.5f, o.X.y, o.X.z + (o.Iv.y - o.Iv.x) * 0.5f)), v => v.X + v.V),
        new("af_mul", o => Form(new float3(o.X.x * o.Y.x, o.X.x * o.Y.y + o.Y.x * o.X.y,
            MathF.Abs(o.X.x) * o.Y.z + MathF.Abs(o.Y.x) * o.X.z + (MathF.Abs(o.X.y) + o.X.z) * (MathF.Abs(o.Y.y) + o.Y.z))), v => v.X * v.Y),
    };

    // One-ulp mutants that survive, each with the reason. A rounding absorption bounds the worst case
    // over every operand value, and 2^-20 S is 16u S, at least eight ulps of the larger of |x0|, |a| and
    // e; one ulp less of e leaves the rest of that margin, which no sampled rounding can cross.
    private static readonly Dictionary<string, string> OneUlpSurvivors = new()
    {
        ["af_from_iv"] = "the absorption (|x0| + e) 2^-20 is at least eight ulps of e over the one rounding of x0 and e it covers",
        ["af_range"] = "each endpoint moves outward by (|x0| + |a| + e) 2^-20, eight ulps past the one rounding it covers",
        ["af_add"] = "the absorption 2^-20 S is eight ulps of S's largest term past the three roundings it covers",
        ["af_sub"] = "as af_add",
        ["af_scale"] = "as af_add",
        ["af_add_iv"] = "two absorptions, af_from_iv's and af_add's",
        ["af_mul"] = "the absorption 2^-20 (|x0| + |x0 a_y| + |y0 a_x| + e) is eight ulps past the products it covers",
    };

    /// <summary>
    /// The rounding absorptions are load-bearing: with each op's absorption removed (Unabsorbed), the
    /// harness finds a miss for every op. Each op's e shrunk by one ulp is run too; it survives for every
    /// op, because the absorption is a worst-case bound many ulps wide, and each survivor is named with
    /// its reason in OneUlpSurvivors. A one-ulp mutant that starts failing, or one that is not named,
    /// fails this test, so the list cannot go stale.
    /// </summary>
    [Fact]
    public void EveryRoundingTermIsCaught()
    {
        var problems = new List<string>();
        foreach (var mutant in Unabsorbed)
        {
            var (misses, _) = Harness(mutant, 0xAF01);
            output.WriteLine($"AF-REPORT {mutant.Name} without absorption: {misses} misses");
            if (misses == 0)
                problems.Add($"{mutant.Name}: removing the rounding absorption is not caught");
        }

        foreach (var op in Ops.Where(o => o.Name is not ("af_point" or "af_symbol" or "af_neg")))
        {
            var shrunk = op.Name == "af_range"
                ? op with { Build = o => { var r = af_range(o.X); return Range(new float2(MathF.BitIncrement(r.x), MathF.BitDecrement(r.y))); } }
                : op with { Build = o => Form(Shrink(BuildForm(op.Name, o))) };
            var (misses, _) = Harness(shrunk, 0xAF02);
            output.WriteLine($"AF-REPORT {op.Name} with e one ulp short: {misses} misses");
            var named = OneUlpSurvivors.ContainsKey(op.Name);
            if (misses == 0 && !named)
                problems.Add($"{op.Name}: e one ulp short survives and is not named");
            if (misses > 0 && named)
                problems.Add($"{op.Name}: e one ulp short is now caught; remove it from OneUlpSurvivors");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static float3 BuildForm(string name, Operands o) => name switch
    {
        "af_from_iv" => af_from_iv(o.Iv),
        "af_add" => af_add(o.X, o.Y),
        "af_sub" => af_sub(o.X, o.Y),
        "af_scale" => af_scale(o.X, o.S),
        "af_add_iv" => af_add_iv(o.X, o.Iv),
        "af_mul" => af_mul(o.X, o.Y),
        _ => throw new ArgumentException(name),
    };

    /// <summary>
    /// The shared symbol correlates: for x = af_symbol(x0, a), af_sub(x, x) and af_add(x, af_neg(x)) have
    /// a = 0 and e at most their own rounding, and af_sub(af_scale(x, 2), af_add(x, x)) has a = 0. A
    /// mutant of af_add, af_sub or af_scale that condensed a into e would leave e of order |a|.
    /// </summary>
    [Fact]
    public void SharedSymbolCorrelates()
    {
        var random = new System.Random(0xAF03);
        for (var t = 0; t < 10_000; t++)
        {
            var x = af_symbol(Uniform(random, -20.0f, 20.0f), Signed(random, LogUniform(random, 1.0e-5f, 20.0f)));
            foreach (var (name, form) in new[]
            {
                ("af_sub(x, x)", af_sub(x, x)),
                ("af_add(x, af_neg(x))", af_add(x, af_neg(x))),
                ("af_sub(af_scale(x, 2), af_add(x, x))", af_sub(af_scale(x, 2.0f), af_add(x, x))),
            })
            {
                var rounding = (MathF.Abs(x.x) + MathF.Abs(x.y)) * 1.0e-5f;
                Assert.True(form.y == 0.0f && form.z <= rounding, $"{name} for x = {x} is {form}: a must be 0 and e at most {rounding:R}");
            }
        }
    }

    // Forms whose range straddles zero (|x0| below |a| + e) or is one-signed (|x0| above it).
    private static float3 DrawSigned(System.Random random, bool straddle)
    {
        var a = Signed(random, LogUniform(random, 1.0e-3f, 10.0f));
        var e = LogUniform(random, 1.0e-3f, 10.0f);
        var reach = MathF.Abs(a) + e;
        return new float3(Signed(random, straddle ? Uniform(random, 0.0f, 0.9f * reach) : reach * Uniform(random, 1.1f, 4.0f)), a, e);
    }

    /// <summary>
    /// af_mul encloses the product over forms that straddle zero and forms that are one-signed, every
    /// sign combination. The eps^2 term a_x a_y is what the condensed e carries for eps at +-1: a mutant
    /// dropping |a_x a_y| from e misses there.
    /// </summary>
    [Fact]
    public void MulEnclosesEverySignCase()
    {
        var mul = Ops.Single(o => o.Name == "af_mul");
        var dropped = mul with
        {
            Build = o =>
            {
                var r = af_mul(o.X, o.Y);
                return Form(new float3(r.x, r.y, r.z - MathF.Abs(o.X.y * o.Y.y)));
            },
        };
        foreach (var (sx, sy) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var seed = 0xAF04 + (sx ? 1 : 0) + (sy ? 2 : 0);
            var flip = 0;
            float3 Draw(System.Random r) => DrawSigned(r, (flip++ % 2 == 0) ? sx : sy);
            var (misses, witness) = Harness(mul, seed, 5_000, Draw);
            Assert.True(misses == 0, $"af_mul, straddling ({sx}, {sy}): {misses} misses" + Environment.NewLine + string.Join(Environment.NewLine, witness));
            flip = 0;
            var (caught, _) = Harness(dropped, seed, 5_000, Draw);
            Assert.True(caught > 0, $"af_mul without |a_x a_y|, straddling ({sx}, {sy}), is not caught");
        }
    }

    // af_snoise's float32 allowance, as its doc comment states it, for the width comparison below.
    private static double Allowance(float3 centre, double reach) =>
        Math.ScaleB(1.0, -14) + Math.ScaleB(reach, -12)
        + SNOISE_LIPSCHITZ * Math.ScaleB(Math.Abs(centre.x) + Math.Abs(centre.y) + Math.Abs(centre.z) + reach, -20);

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

    // A float32 point of the set centre + axis eps + w, |w| <= radius, checked in double; the first eight
    // w are on the surface. Where the float32 sum rounds a point out of the set, w is pulled in by the
    // overshoot and 2^-21 (|centre|_1 + |axis|_1) and the sum retried; a point that still rounds out is
    // reported as null and skipped.
    private static float3? PointOf(System.Random random, float3 centre, float3 axis, float radius, float eps, int index)
    {
        var w = UnitVector(random) * (index < 8 ? radius : radius * MathF.Cbrt(random.NextSingle()));
        for (var k = 0; k < 6; k++)
        {
            var p = centre + axis * eps + w;
            var (dx, dy, dz) = (p.x - (centre.x + (double)axis.x * eps), p.y - (centre.y + (double)axis.y * eps), p.z - (centre.z + (double)axis.z * eps));
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance <= radius)
                return p;
            w *= (float)(Math.Max(0.0, 2.0 * radius - distance - Math.ScaleB(Math.Abs(centre.x) + Math.Abs(centre.y) + Math.Abs(centre.z) + Math.Abs(axis.x) + Math.Abs(axis.y) + Math.Abs(axis.z), -21)) / radius);
        }

        return null;
    }

    /// <summary>
    /// af_snoise and af_fbm over 2,000 (centre, axis, radius) each: centres in [-20, 20]^3, radius
    /// log-uniform in [1e-4, 2], |axis| uniform in [0, 2] (an eighth exactly 0). 64 points
    /// centre + axis eps + w per case, eps at -1 and 1 for the first eight; snoise (fbm_grad(...).w) in
    /// float32 lies in the form at eps, no tolerance. Dropping the M R^2 / 2 remainder from a centred
    /// form misses. af_snoise's total width is never more than the Lipschitz form's, L R plus the
    /// allowance.
    /// </summary>
    [Fact]
    public void SnoiseFormEnclosesBall()
    {
        var random = new System.Random(0xAF10);
        var (misses, remainderMisses, skipped, centredCases) = (0, 0, 0, 0);
        var witness = new List<string>();
        for (var b = 0; b < 4000; b++)
        {
            var fbm = b >= 2000;
            var centre = new float3(Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f));
            var axis = random.Next(8) == 0 ? new float3(0.0f, 0.0f, 0.0f) : UnitVector(random) * Uniform(random, 0.0f, 2.0f);
            var radius = LogUniform(random, 1.0e-4f, 2.0f);
            var (octaves, lacunarity, gain) = (random.Next(0, 7), Uniform(random, 1.5f, 2.5f), Uniform(random, 0.3f, 0.7f));
            var form = fbm ? af_fbm(centre, axis, radius, octaves, lacunarity, gain) : af_snoise(centre, axis, radius);
            var reach = (double)length(axis) + radius;
            var remainder = 0.5 * SNOISE_HESSIAN * reach * reach;
            var mutant = new float3(form.x, form.y, (float)(form.z - remainder));
            if (!fbm)
            {
                var lipschitz = (SNOISE_LIPSCHITZ * reach + Allowance(centre, reach) + Math.ScaleB(Math.Abs(form.x), -20)) * (1.0 + Math.ScaleB(1.0, -18));
                Assert.True(Math.Abs(form.y) + form.z <= lipschitz, $"af_snoise({centre}, {axis}, {radius:R}) = {form} is wider than the Lipschitz form's half-width {lipschitz:R}");
                centredCases += form.y != 0.0f ? 1 : 0;
            }

            for (var i = 0; i < 64; i++)
            {
                var eps = i < 8 ? (i % 2 == 0 ? -1.0f : 1.0f) : Uniform(random, -1.0f, 1.0f);
                var p = PointOf(random, centre, axis, radius, eps, i);
                if (p is not { } point)
                {
                    skipped++;
                    continue;
                }

                var value = fbm ? fbm_grad(point, octaves, lacunarity, gain).w : snoise(point);
                if (!InForm(form, eps, value))
                {
                    misses++;
                    if (witness.Count < 4)
                        witness.Add($"{(fbm ? $"af_fbm(..., {octaves}, {lacunarity:R}, {gain:R})" : "af_snoise")}({centre}, {axis}, {radius:R}) = {form} misses {value:R} at eps {eps:R}, {point}");
                }

                if (!fbm && form.y != 0.0f && !InForm(mutant, eps, value))
                    remainderMisses++;
            }
        }

        output.WriteLine($"AF-REPORT af_snoise/af_fbm enclosure: {misses} misses, {skipped} points skipped (rounded out of the set), {centredCases} of 2000 af_snoise cases centred, {remainderMisses} misses with the remainder dropped");
        Assert.True(misses == 0, string.Join(Environment.NewLine, witness));
        Assert.True(skipped < 64 * 4000 / 100, $"{skipped} points rounded out of their sets");
        Assert.True(remainderMisses > 0, "dropping M R^2 / 2 from the centred form is not caught");
    }

    /// <summary>
    /// At radius 1e-3 and |axis| = 1e-3, af_snoise takes the centred form in every one of 2,000 seeded
    /// cases (a is nonzero), and its mean total width is under half of the Lipschitz form's 2 L R. A
    /// mutant that always returns the Lipschitz form has a = 0.
    /// </summary>
    [Fact]
    public void SnoiseChoosesTheCentredFormWhereNarrower()
    {
        var random = new System.Random(0xAF11);
        var sum = 0.0;
        for (var b = 0; b < 2000; b++)
        {
            var centre = new float3(Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f));
            var axis = UnitVector(random) * 1.0e-3f;
            var form = af_snoise(centre, axis, 1.0e-3f);
            var lipschitz = 2.0 * SNOISE_LIPSCHITZ * 2.0e-3;
            var width = 2.0 * (Math.Abs(form.y) + form.z);
            Assert.True(form.y != 0.0f && width < lipschitz, $"af_snoise({centre}, {axis}, 1e-3) = {form}: not the centred form, or no narrower than 2 L R = {lipschitz:R}");
            sum += width / lipschitz;
        }

        output.WriteLine($"AF-REPORT af_snoise at radius 1e-3: mean width over 2 L R {sum / 2000:F3}");
        Assert.True(sum / 2000 < 0.5, $"mean width over 2 L R is {sum / 2000:F3}, not under half");
    }

    /// <summary>
    /// af_frustum_ball and af_frustum_axis over iv_frustum_ball's families (NoiseBoundTests.DrawBallCase:
    /// the r2 domain and the degenerate, thin, wide-slope, far, from-the-camera, tiny-focal and huge-warp
    /// extremes), 2,000 tiles each, warp passed as the warp variation: every exact ray point (m z, z),
    /// formed in double from the float32 slope and depth at the footprint's corners at both depth ends
    /// (and 56 more for the r2 domain), moved by a full-length outward flow of length warp, is within
    /// radius of centre + axis eps(z), eps(z) = clamp((z - z_m) / h, -1, 1) with z_m and h the centre's z
    /// and the axis's z (0 when h is 0). The radius is pinned to the derivation both ways:
    /// r &lt;= w &lt;= r + 2^-19 (r + |c|_1 + |axis|_1), r = z1 footprintPerDepth + warp in double.
    /// </summary>
    [Fact]
    public void FrustumSliceEnclosesEveryRayPoint()
    {
        var random = new System.Random(0xAF12);
        string[] families = { "r2 domain", "degenerate", "thin", "wide slope", "far", "from the camera", "tiny focal", "huge warp" };
        var failures = new Dictionary<string, int>();
        var witness = new List<string>();
        foreach (var family in families)
        {
            failures[family] = 0;
            for (var t = 0; t < 2000; t++)
            {
                var (n, focal, slope, z0, z1, warp) = NoiseBoundTests.DrawBallCase(random, family);
                var half = n / (2.0f * focal);
                var footprint = n / (MathF.Sqrt(2.0f) * focal);
                var axis = af_frustum_axis(slope, z0, z1);
                var ball = af_frustum_ball(slope, z0, z1, footprint, warp);
                var (cx, cy, cz, w) = ((double)ball.x, (double)ball.y, (double)ball.z, (double)ball.w);
                var r = z1 * (double)footprint + warp;
                var c1 = Math.Abs(cx) + Math.Abs(cy) + Math.Abs(cz);
                var a1 = Math.Abs((double)axis.x) + Math.Abs((double)axis.y) + Math.Abs((double)axis.z);
                if (!(r <= w && w <= r + Math.ScaleB(r + c1 + a1, -19)))
                {
                    failures[family]++;
                    if (witness.Count < 8)
                        witness.Add($"{family} pin: af_frustum_ball({slope}, {z0:R}, {z1:R}, {footprint:R}, {warp:R}) = {ball}, derivation {r:R}");
                }

                // The axis is (m_c, 1) h exactly, and the radius carries the documented widening, the
                // rounding of the centre, the axis (spread) and the radius at 2^-20 each, to 15% (the
                // radius rounds too).
                var h = (z1 - z0) * 0.5f;
                var spread = ((double)Math.Abs(slope.x) + Math.Abs(slope.y) + 1.0) * h;
                var widening = Math.ScaleB(c1 + spread + r, -20);
                if (!(axis.z == h && axis.x == slope.x * h && axis.y == slope.y * h && Math.Abs(w - r - widening) <= 0.15 * widening + Math.ScaleB(w, -22)))
                {
                    failures[family]++;
                    if (witness.Count < 8)
                        witness.Add($"{family} axis or widening: af_frustum_ball({slope}, {z0:R}, {z1:R}, {footprint:R}, {warp:R}) = {ball}, axis {axis}, widening {w - r:R} against {widening:R}");
                }

                for (var i = 0; i < (family == "r2 domain" ? 64 : 8); i++)
                {
                    var offset = i < 8
                        ? new float2((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half)
                        : new float2(Uniform(random, -half, half), Uniform(random, -half, half));
                    var z = i < 8 ? ((i & 4) == 0 ? z0 : z1) : Uniform(random, z0, z1);
                    var m = slope + offset;
                    var eps = axis.z == 0.0f ? 0.0 : Math.Clamp((z - cz) / axis.z, -1.0, 1.0);
                    var (qx, qy, qz) = (cx + axis.x * eps, cy + axis.y * eps, cz + axis.z * eps);
                    var (dx, dy, dz) = (m.x * (double)z - qx, m.y * (double)z - qy, z - qz);
                    var away = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    var distance = away + warp;
                    if (!(distance <= w))
                    {
                        failures[family]++;
                        if (witness.Count < 8)
                            witness.Add($"{family}: af_frustum_ball({slope}, {z0:R}, {z1:R}, {footprint:R}, {warp:R}) = {ball}, axis {axis} misses slope {m}, depth {z:R} by {(distance - w) / w:R} r");
                    }
                }
            }
        }

        Assert.True(failures.Values.Sum() == 0, string.Join(", ", families.Select(f => $"{f} {failures[f]}")) + Environment.NewLine + string.Join(Environment.NewLine, witness));
    }

    /// <summary>
    /// Underflow. Operands of magnitude 1e-21 to 1e-17 put products near and under 2^-126, where float32
    /// rounds to a subnormal and the relative absorption S 2^-20 is smaller than the loss: every op
    /// still encloses, no tolerance. af_mul and af_scale carry the 2^-126 term; a mutant that subtracts
    /// it misses here.
    /// </summary>
    [Fact]
    public void TinyOperandsEncloseTheirPoints()
    {
        var failures = new List<string>();
        foreach (var op in Ops)
        {
            var (misses, witness) = Harness(op, 0xAF05, 10_000, tiny: true);
            if (misses > 0)
                failures.Add($"{op.Name}: {misses} misses" + Environment.NewLine + string.Join(Environment.NewLine, witness));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private const float MinNormal = 1.17549435e-38f;

    private static bool Near(double observed, double expected, double relative = 1.0e-5) =>
        Math.Abs(observed - expected) <= relative * Math.Abs(expected);

    /// <summary>
    /// The rounding absorption of each op, pinned both ways. With no propagated error in e, af_add,
    /// af_sub, af_scale, af_mul and af_from_iv return an e equal to their documented absorption (and
    /// 2^-126 where documented) to a part in 1e5 of its double value, and af_range moves each endpoint
    /// outward by (|x0| + |a| + e) 2^-20 to 30% (the endpoints round once). A factor, a sign or an
    /// operator changed in any of them fails here; the one-ulp changes of the worst-case absorption are
    /// the OneUlpSurvivors set EveryRoundingTermIsCaught names.
    /// </summary>
    [Fact]
    public void AbsorptionsArePinned()
    {
        var u20 = Math.ScaleB(1.0, -20);
        var random = new System.Random(0xAF06);
        for (var t = 0; t < 2_000; t++)
        {
            var (x0, y0) = (Uniform(random, -20.0f, 20.0f), Uniform(random, -20.0f, 20.0f));
            var (ax, ay) = (Signed(random, LogUniform(random, 1.0e-3f, 20.0f)), Signed(random, LogUniform(random, 1.0e-3f, 20.0f)));
            var scale = Signed(random, LogUniform(random, 1.0e-2f, 4.0f));
            var x = new float3(x0, ax, 0.0f);
            var y = new float3(y0, ay, 0.0f);

            var sum = af_add(x, y);
            Assert.True(Near(sum.z, (Math.Abs((double)sum.x) + Math.Abs((double)sum.y)) * u20), $"af_add({x}, {y}) = {sum}: e is not (|x0| + |a|) 2^-20");
            var difference = af_sub(x, y);
            Assert.True(Near(difference.z, (Math.Abs((double)difference.x) + Math.Abs((double)difference.y)) * u20), $"af_sub({x}, {y}) = {difference}: e is not (|x0| + |a|) 2^-20");
            var scaled = af_scale(x, scale);
            Assert.True(Near(scaled.z, (Math.Abs((double)scaled.x) + Math.Abs((double)scaled.y)) * u20 + MinNormal), $"af_scale({x}, {scale:R}) = {scaled}: e is not (|x0| + |a|) 2^-20 + 2^-126");

            // a_x = 0 leaves the product's propagated error 0 and its a = x0 a_y.
            var px = new float3(x0, 0.0f, 0.0f);
            var product = af_mul(px, y);
            var terms = Math.Abs((double)product.x) + Math.Abs((double)x0 * ay);
            Assert.True(Near(product.z, terms * u20 + MinNormal), $"af_mul({px}, {y}) = {product}: e is not (|x0 y0| + |x0 a_y|) 2^-20 + 2^-126");

            var iv = DrawInterval(random);
            var form = af_from_iv(iv);
            var (mid, half) = (((double)iv.x + iv.y) / 2.0, ((double)iv.y - iv.x) / 2.0);
            var expected = half + (Math.Abs(mid) + half) * u20 + MinNormal;
            Assert.True(Math.Abs(form.z - expected) <= 1.0e-5 * (Math.Abs(mid) + half) * u20 + 1.0e-6 * half + 1.0e-12 * expected,
                $"af_from_iv({iv}) = {form}: e is not half-width + (|mid| + half-width) 2^-20 + 2^-126 = {expected:R}");

            var wide = new float3(Uniform(random, -20.0f, 20.0f), Signed(random, LogUniform(random, 1.0e-3f, 20.0f)), LogUniform(random, 1.0e-3f, 20.0f));
            var range = af_range(wide);
            var reach = Math.Abs((double)wide.y) + wide.z;
            var absorption = (Math.Abs((double)wide.x) + reach) * u20;
            var outward = ((double)range.y - range.x) / 2.0 - reach;
            Assert.True(Math.Abs(outward - absorption) <= 0.3 * absorption, $"af_range({wide}) = {range}: endpoints are {outward:R} past x0 -+ (|a| + e), not (|x0| + |a| + e) 2^-20 = {absorption:R}");
        }
    }

    /// <summary>
    /// af_from_iv and af_range at the float32 extremes. Subnormal endpoints (including [0, the least
    /// subnormal], whose halved width underflows to 0) and endpoints near the float maximum (where lo +
    /// hi, hi - lo or the absorption would overflow) give a form whose set holds both endpoints and the
    /// midpoint, and an af_range that holds them with no NaN endpoint. A form with an infinite or NaN
    /// component, as an overflowed op returns, gives an af_range with no NaN endpoint.
    /// </summary>
    [Fact]
    public void FromIntervalAndRangeHoldAtTheExtremes()
    {
        var least = BitConverter.Int32BitsToSingle(1);
        var max = float.MaxValue;
        var random = new System.Random(0xAF07);
        var specials = new[] { 0.0f, least, 2 * least, 100 * least, MinNormal - least, MinNormal, 3.0f * MinNormal, 1.0e-30f, 1.0f, 1.0e20f, 1.0e30f, max / 4, max / 2, max * 0.75f, max };
        var misses = new List<string>();
        void Check(float lo, float hi)
        {
            var form = af_from_iv(new float2(lo, hi));
            var range = af_range(form);
            if (float.IsNaN(form.x) || float.IsNaN(form.z) || float.IsNaN(range.x) || float.IsNaN(range.y))
                misses.Add($"af_from_iv([{lo:R}, {hi:R}]) = {form}, af_range = {range}: NaN");
            foreach (var value in new[] { lo, hi, (float)(((double)lo + hi) / 2.0) })
            {
                if (!InForm(form, 0.0f, value))
                    misses.Add($"af_from_iv([{lo:R}, {hi:R}]) = {form} misses {value:R}");
                if (!(range.x <= value && value <= range.y))
                    misses.Add($"af_range(af_from_iv([{lo:R}, {hi:R}])) = {range} misses {value:R}");
            }

            // Tight where the answer is representable: an endpoint is no farther than 2^-18 (|lo| + |hi|)
            // from the interval's, unless that bound passes the float maximum, where infinity is the answer.
            var slack = Math.ScaleB(Math.Abs((double)lo) + Math.Abs((double)hi), -18) + 1.0e-37;
            if ((double)lo - slack > -(double)max && range.x < (double)lo - slack)
                misses.Add($"af_range(af_from_iv([{lo:R}, {hi:R}])).x = {range.x:R}, more than 2^-18 (|lo| + |hi|) under lo");
            if ((double)hi + slack < (double)max && range.y > (double)hi + slack)
                misses.Add($"af_range(af_from_iv([{lo:R}, {hi:R}])).y = {range.y:R}, more than 2^-18 (|lo| + |hi|) over hi");
        }

        foreach (var a in specials)
        foreach (var b in specials)
        {
            Check(-a, b);
            Check(-Math.Max(a, b), -Math.Min(a, b));
            Check(Math.Min(a, b), Math.Max(a, b));
        }

        for (var t = 0; t < 200_000; t++)
        {
            var sign = random.Next(2) == 0 ? 1 : -1;
            var lo = sign * BitConverter.Int32BitsToSingle(random.Next(0, random.Next(2) == 0 ? 1 << 20 : 0x00800000));
            var width = BitConverter.Int32BitsToSingle(random.Next(0, random.Next(2) == 0 ? 1 << 12 : 1 << 22));
            Check(lo, lo + width);
        }

        var odd = new[] { float.PositiveInfinity, float.NegativeInfinity, float.NaN, 0.0f, 1.0f, max };
        foreach (var x0 in odd)
        foreach (var a in odd)
        foreach (var e in odd)
        {
            var range = af_range(new float3(x0, a, e));
            if (float.IsNaN(range.x) || float.IsNaN(range.y))
                misses.Add($"af_range(({x0}, {a}, {e})) = {range}");
        }

        Assert.True(misses.Count == 0, $"{misses.Count} misses:" + Environment.NewLine + string.Join(Environment.NewLine, misses.Take(8)));
    }

    /// <summary>
    /// af_snoise's width, pinned both ways. Over 6,000 seeded (centre, axis, radius) with reach R from 1e-3
    /// to 3 (centres in [-20, 20]^3, a third out to [-1000, 1000]^3, a third of the axes and a third of the
    /// radii 0), the form is the documented rule evaluated in double from snoise_grad: x0 = n; the centred
    /// form when |g . axis| + |g| radius + M R^2 / 2 is under L R (checked away from that boundary), else
    /// the Lipschitz form; e = that half-width + the float32 allowance (2^-14 + 2^-12 R + L 2^-20 (|centre|_1 + R))
    /// plus the form's own rounding absorption (|n| + |a| + e) 2^-20; each of x0, a and e within a part in
    /// 1e6 of it. The enclosure tests pass on an allowance or an absorption of any size on the CPU, where
    /// float32 is exact; this is the test that sees them.
    /// </summary>
    [Fact]
    public void SnoiseFormCarriesItsDocumentedWidth()
    {
        var u20 = Math.ScaleB(1.0, -20);
        double lipschitz = SNOISE_LIPSCHITZ, hessian = SNOISE_HESSIAN;
        var random = new System.Random(0xAF13);
        var (centred, wide) = (0, 0);
        for (var t = 0; t < 6_000; t++)
        {
            var spread = t % 3 == 2 ? 1000.0f : 20.0f;
            var centre = new float3(Uniform(random, -spread, spread), Uniform(random, -spread, spread), Uniform(random, -spread, spread));
            var axis = random.Next(3) == 0 ? new float3(0.0f, 0.0f, 0.0f) : UnitVector(random) * LogUniform(random, 1.0e-3f, 3.0f);
            var radius = random.Next(3) == 0 ? 0.0f : LogUniform(random, 1.0e-3f, 3.0f);
            var g = snoise_grad(centre);
            var reach = (double)length(axis) + radius;
            var lip = lipschitz * reach;
            var a = (double)g.x * axis.x + (double)g.y * axis.y + (double)g.z * axis.z;
            var remainder = Math.Sqrt((double)g.x * g.x + (double)g.y * g.y + (double)g.z * g.z) * radius + 0.5 * hessian * reach * reach;
            var allowance = Math.ScaleB(1.0, -14) + Math.ScaleB(reach, -12)
                + lipschitz * u20 * (Math.Abs((double)centre.x) + Math.Abs((double)centre.y) + Math.Abs((double)centre.z) + reach);

            double Width(bool useCentred, out double slope)
            {
                slope = useCentred ? a : 0.0;
                var e = (useCentred ? remainder : lip) + allowance;
                return e + (Math.Abs((double)g.w) + Math.Abs(slope) + e) * u20;
            }

            var zCentred = Width(true, out var slopeCentred);
            var zLipschitz = Width(false, out _);
            var form = af_snoise(centre, axis, radius);
            var observedCentred = Math.Abs(form.z - zCentred) <= Math.Abs(form.z - zLipschitz);
            var expectedCentred = Math.Abs(a) + remainder < lip;
            if (Math.Abs(Math.Abs(a) + remainder - lip) > 1.0e-3 * lip)
                Assert.True(observedCentred == expectedCentred || zCentred == zLipschitz, $"af_snoise({centre}, {axis}, {radius:R}) = {form} took the {(observedCentred ? "centred" : "Lipschitz")} form; the rule takes the {(expectedCentred ? "centred" : "Lipschitz")} one (|a| + remainder = {Math.Abs(a) + remainder:R}, L R = {lip:R})");
            var z = observedCentred ? zCentred : zLipschitz;
            var slope = observedCentred ? slopeCentred : 0.0;
            Assert.True(form.x == g.w, $"af_snoise({centre}, {axis}, {radius:R}).x0 = {form.x:R}, snoise = {g.w:R}");
            Assert.True(Math.Abs(form.y - slope) <= 1.0e-5 * Math.Abs((double)length(new float3(g.x, g.y, g.z)) * length(axis)) + 1.0e-12, $"af_snoise({centre}, {axis}, {radius:R}).a = {form.y:R}, g . axis = {slope:R}");
            Assert.True(Math.Abs(form.z - z) <= 1.0e-6 * z, $"af_snoise({centre}, {axis}, {radius:R}) = {form}: e is {form.z:R}, the documented rule gives {z:R}");
            centred += observedCentred ? 1 : 0;
            wide += !observedCentred ? 1 : 0;
        }

        output.WriteLine($"AF-REPORT af_snoise width: {centred} centred cases, {wide} Lipschitz cases");
        Assert.True(centred > 1000 && wide > 1000, $"the cases do not exercise both forms: {centred} centred, {wide} Lipschitz");
    }
}
