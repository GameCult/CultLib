using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// span_fbm and span_ridged (math.Span.cs): the octave stages of a warp chain over a span. At the identity
/// transform they are fbm_grad and ridged_grad; their gradient is the derivative of their value through the
/// stretch and the per-octave scaling; curvature bounds the Hessian of the field in q and fold the departure
/// of a ridged sum from its midpoint branch, at every point of every span the tests draw, in the regime
/// where the local third-derivative term is what holds the bound and in the one where the global pin is.
/// Lines starting IV-REPORT carry the numbers the cut report quotes.
/// </summary>
public sealed class SpanStageTests
{
    private readonly ITestOutputHelper output;

    public SpanStageTests(ITestOutputHelper output) => this.output = output;

    private static float Uniform(System.Random random, float lo, float hi) => lo + (hi - lo) * random.NextSingle();

    private static float3 Vector(System.Random random, float lo, float hi) => new(Uniform(random, lo, hi), Uniform(random, lo, hi), Uniform(random, lo, hi));

    private static float3 UnitVector(System.Random random)
    {
        float3 v;
        do
        {
            v = Vector(random, -1.0f, 1.0f);
        }
        while (dot(v, v) is > 1.0f or < 0.01f);
        return normalize(v);
    }

    private static float LogUniform(System.Random random, float lo, float hi) => MathF.Exp(Uniform(random, MathF.Log(lo), MathF.Log(hi)));

    private static float Signed(System.Random random, float magnitude) => random.Next(2) == 0 ? magnitude : -magnitude;

    private static readonly float3 None = new(0.0f, 0.0f, 0.0f);

    private static double L1(double[] v) => Math.Abs(v[0]) + Math.Abs(v[1]) + Math.Abs(v[2]);

    // One draw of the stage parameters: every one of them off its identity value, over the ranges the stage
    // takes in use (stage-parameter-range): |frequency| 1e-3..10, stretch 1e-2..1e2, |lacunarity| 0.5..3,
    // |gain| 0.1..1.2, the last three and frequency with random signs where a sign is meaningful,
    // octaveStep nonzero in every component.
    private readonly record struct Stage(SpanStage S, int Octaves, float Lacunarity, float Gain)
    {
        public static Stage Draw(System.Random random, int maxOctaves = 8, int minOctaves = 1) => new(
            new SpanStage
            {
                frequency = Signed(random, LogUniform(random, 1.0e-3f, 10.0f)),
                stretch = LogUniform(random, 1.0e-2f, 1.0e2f),
                axis = UnitVector(random) * Uniform(random, 0.5f, 3.0f),
                shift = Vector(random, -8.0f, 8.0f),
                offset = Vector(random, -8.0f, 8.0f),
                octaveStep = new float3(Signed(random, Uniform(random, 0.1f, 3.0f)), Signed(random, Uniform(random, 0.1f, 3.0f)), Signed(random, Uniform(random, 0.1f, 3.0f))),
            },
            random.Next(minOctaves, maxOctaves + 1),
            Signed(random, Uniform(random, 0.5f, 3.0f)),
            Signed(random, Uniform(random, 0.1f, 1.2f)));

        // A draw whose octave coordinates stay under `limit` for every q with |q|_1 <= qL1: the fastest
        // octave's |f_o| |M| is capped by rejection. For the derivative checks, where float rounding of the
        // coordinate must not swamp a difference quotient.
        public static Stage DrawSmooth(System.Random random, float limit, float qL1, int maxOctaves = 8, int minOctaves = 1)
        {
            while (true)
            {
                var stage = Draw(random, maxOctaves, minOctaves);
                var worst = 0.0;
                for (var o = 0; o < stage.Octaves; o++)
                    worst = Math.Max(worst, Math.Abs(stage.Scale(o)) * (stage.Norm() * qL1 + L1(Components(stage.S.shift))) + L1(Components(stage.S.offset)) + o * L1(Components(stage.S.octaveStep)));
                if (worst < limit)
                    return stage;
            }
        }

        public SpanNoise Fbm(SpanPath path) => span_fbm(path, S, Octaves, Lacunarity, Gain);

        public SpanNoise Ridged(SpanPath path) => span_ridged(path, S, Octaves, Lacunarity, Gain);

        // M = frequency (I + (1/stretch - 1) a a^T) in double, a the normalised axis (none for a zero axis).
        public double[,] Matrix()
        {
            var m = new double[3, 3];
            var a = Components(S.axis);
            var length = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = S.frequency * ((i == j ? 1.0 : 0.0) + (length > 0.0 && S.stretch != 1.0f ? (1.0 / S.stretch - 1.0) * a[i] * a[j] / (length * length) : 0.0));
            return m;
        }

        // Octave o's noise-space point for the span point q, in double from the same inputs.
        public double[] PointD(float3 q, int octave)
        {
            var m = Matrix();
            var scale = Math.Pow(Lacunarity, octave);
            var x = new double[3];
            var qv = Components(q);
            var shift = Components(S.shift);
            var offset = Components(S.offset);
            var step = Components(S.octaveStep);
            for (var i = 0; i < 3; i++)
            {
                var mq = 0.0;
                for (var j = 0; j < 3; j++)
                    mq += m[i, j] * qv[j];
                x[i] = scale * (mq + shift[i]) + offset[i] + octave * step[i];
            }

            return x;
        }

        public float3 Point(float3 q, int octave)
        {
            var x = PointD(q, octave);
            return new float3((float)x[0], (float)x[1], (float)x[2]);
        }

        // S_o: the magnitudes octave o's coordinate roundings are relative to, plus the span's reach.
        public double Magnitude(SpanPath path, int octave)
        {
            var m = Matrix();
            var qv = Components(path.midpoint);
            var mq = new double[3];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                mq[i] += m[i, j] * qv[j];
            var scale = Math.Abs(Scale(octave));
            return scale * (L1(mq) + L1(Components(S.shift))) + L1(Components(S.offset)) + octave * L1(Components(S.octaveStep))
                + Math.Sqrt(3.0) * scale * Norm() * path.speed * path.halfLength;
        }

        public double MaxMagnitude(SpanPath path)
        {
            var largest = 0.0;
            for (var o = 0; o < Octaves; o++)
                largest = Math.Max(largest, Magnitude(path, o));
            return largest;
        }

        // Octave o as a stage of its own: one octave at the scaled frequency, shift and offset.
        public Stage OneOctave(int octave) => new(
            new SpanStage { frequency = S.frequency * (float)Scale(octave), stretch = S.stretch, axis = S.axis, shift = S.shift * (float)Scale(octave), offset = S.offset + (float)octave * S.octaveStep, octaveStep = None },
            1, 1.0f, 1.0f);

        public double Amplitude(int octave) => Math.Pow(Gain, octave);

        public double Scale(int octave) => Math.Pow(Lacunarity, octave);

        // The spectral norm of M: frequency times the larger of 1 and 1/stretch.
        public double Norm() => Math.Abs(S.frequency) * Math.Max(1.0, 1.0 / S.stretch);
    }

    private static double[] Components(float3 v) => new double[] { v.x, v.y, v.z };

    private static SpanPath PathAt(float3 q) => new() { midpoint = q, velocity = None, speed = 0.0f, acceleration = 0.0f, halfLength = 0.0f };

    // A span through q: velocity v, constant acceleration c of norm `acceleration`, so q(t) = q + v t + c t^2 / 2;
    // the speed bound is |v| plus what c can add over halfLength.
    private readonly record struct Span(SpanPath Path, float3 Acceleration)
    {
        public static Span Draw(System.Random random, float3 q, float halfLength, float acceleration)
        {
            var velocity = UnitVector(random) * Uniform(random, 0.2f, 3.0f);
            return new Span(new SpanPath { midpoint = q, velocity = velocity, speed = length(velocity) + acceleration * halfLength, acceleration = acceleration, halfLength = halfLength }, UnitVector(random) * acceleration);
        }

        public float3 At(float t) => Path.midpoint + Path.velocity * t + Acceleration * (0.5f * t * t);
    }

    // The operator norm of a symmetric 3x3 matrix (entries in row-major order): the larger |eigenvalue|.
    private static double SymmetricNorm(double[] a)
    {
        var p1 = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
        var q = (a[0] + a[4] + a[8]) / 3.0;
        if (p1 == 0.0)
            return Math.Max(Math.Abs(a[0]), Math.Max(Math.Abs(a[4]), Math.Abs(a[8])));
        var p2 = (a[0] - q) * (a[0] - q) + (a[4] - q) * (a[4] - q) + (a[8] - q) * (a[8] - q) + 2.0 * p1;
        var s = Math.Sqrt(p2 / 6.0);
        var b = new[] { (a[0] - q) / s, a[1] / s, a[2] / s, a[3] / s, (a[4] - q) / s, a[5] / s, a[6] / s, a[7] / s, (a[8] - q) / s };
        var r = Math.Clamp((b[0] * (b[4] * b[8] - b[5] * b[7]) - b[1] * (b[3] * b[8] - b[5] * b[6]) + b[2] * (b[3] * b[7] - b[4] * b[6])) / 2.0, -1.0, 1.0);
        var phi = Math.Acos(r) / 3.0;
        return Math.Max(Math.Abs(q + 2.0 * s * Math.Cos(phi)), Math.Abs(q + 2.0 * s * Math.Cos(phi + 2.0 * Math.PI / 3.0)));
    }

    // The exact Hessian in q of the stage field at span point q (signed per octave for a ridged sum), by the
    // chain rule from the double kernel's Hessian: sum over octaves of weight_o f_o^2 M H_o M, with every
    // octave point computed in double from the same SpanStage.
    private static double[] FieldHessian(Stage stage, float3 q, bool ridged)
    {
        var m = stage.Matrix();
        var total = new double[9];
        for (var o = 0; o < stage.Octaves; o++)
        {
            var x = stage.PointD(q, o);
            var h = NoiseBoundTests.HessianDouble(x[0], x[1], x[2]);
            var weight = stage.Amplitude(o) * stage.Scale(o) * stage.Scale(o) * (ridged ? -Math.Sign(NoiseBoundTests.ValueDouble(x[0], x[1], x[2])) : 1.0);
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < 3; k++)
                for (var l = 0; l < 3; l++)
                    sum += m[i, k] * h[k * 3 + l] * m[l, j];
                total[i * 3 + j] += weight * sum;
            }
        }

        return total;
    }

    private static bool SameBits(float4 a, float4 b) =>
        BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) && BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y)
        && BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z) && BitConverter.SingleToInt32Bits(a.w) == BitConverter.SingleToInt32Bits(b.w);

    private static IEnumerable<float3> Points(System.Random random)
    {
        for (var k = 0; k < 200; k++)
            yield return Vector(random, -60.0f, 60.0f);
        // The simplex tie set: x = y = z, and integer lattice points.
        for (var k = 0; k < 30; k++)
        {
            var t = Uniform(random, -50.0f, 50.0f);
            yield return new float3(t, t, t);
            yield return new float3(random.Next(-40, 41), random.Next(-40, 41), random.Next(-40, 41));
        }
    }

    /// <summary>
    /// The exact reference the enclosure tests lean on is the kernel: the double ValueDouble is snoise to
    /// within float rounding at small coordinates, so a sign taken from it is the kernel's sign.
    /// </summary>
    [Fact]
    public void ExactReferenceIsTheKernel()
    {
        var random = new System.Random(0xE4AC7);
        for (var k = 0; k < 300; k++)
        {
            var p = Vector(random, -20.0f, 20.0f);
            Assert.InRange(NoiseBoundTests.ValueDouble(p.x, p.y, p.z) - snoise(p), -2.0e-4, 2.0e-4);
        }
    }

    /// <summary>
    /// At SpanStage.Identity (also with an axis, stretch 1) and zero shift, offset and octaveStep, the field of
    /// span_fbm and span_ridged is fbm_grad's and ridged_grad's, bit for bit, for 0 to 20 octaves (the clamp
    /// included), every lacunarity and gain drawn, over random and simplex-tie points: any change to the octave
    /// loop's order, scaling or accumulation, and any bit the rounding allowances put in the field, fails.
    /// </summary>
    [Fact]
    public void SpanStagesAtIdentityAreFbmAndRidged()
    {
        var random = new System.Random(0x57A6E1);
        foreach (var p in Points(random))
        {
            var octaves = random.Next(0, 21);
            var lacunarity = Uniform(random, 1.4f, 2.6f);
            var gain = Uniform(random, 0.3f, 0.8f);
            var stage = SpanStage.Identity;
            if (random.Next(2) == 1)
                stage.axis = UnitVector(random) * 2.0f;
            var path = new SpanPath { midpoint = p, velocity = UnitVector(random), speed = 1.0f, acceleration = 0.5f, halfLength = 0.25f };
            var fbm = span_fbm(path, stage, octaves, lacunarity, gain);
            var ridged = span_ridged(path, stage, octaves, lacunarity, gain);
            Assert.True(SameBits(fbm.field, fbm_grad(p, octaves, lacunarity, gain)), $"span_fbm at {p}, {octaves} octaves: {fbm.field} is not fbm_grad {fbm_grad(p, octaves, lacunarity, gain)}");
            Assert.True(SameBits(ridged.field, ridged_grad(p, octaves, lacunarity, gain)), $"span_ridged at {p}, {octaves} octaves: {ridged.field} is not ridged_grad {ridged_grad(p, octaves, lacunarity, gain)}");
            Assert.Equal(0.0f, fbm.fold);
        }
    }

    /// <summary>
    /// The gradient is the derivative of the value through the whole transform: with every parameter off its
    /// identity (frequency of either sign, stretch both ways, axis, shift, offset, octaveStep, lacunarity and
    /// gain of either sign, 4 to 8 octaves) field.xyz equals the fourth-order central difference of field.w
    /// along each axis, within a tolerance built from the third-derivative pin (truncation) and float32
    /// rounding of the coordinates, and both agree with an independent octave sum. A dropped M or lacunarity
    /// factor, M applied to the wrong side, a clamp on frequency, stretch or the octaveStep index, or a dropped
    /// sign (abs of the scale or the amplitude) moves the field by the gradient itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanStageGradientMatchesDifferences(bool ridged)
    {
        var random = new System.Random(ridged ? 0x6A1D : 0x6A1C);
        var worst = 0.0;
        var checkedCases = 0;
        var spans = 0;
        for (var k = 0; k < 6000 && spans < 80; k++)
        {
            var stage = Stage.DrawSmooth(random, 300.0f, 9.0f, 8, 4);
            var q = Vector(random, -3.0f, 3.0f);
            // The fold's crease at n = 0 is not differentiable: keep every octave's stencil clear of it.
            if (ridged && Enumerable.Range(0, stage.Octaves).Any(o => abs(snoise(stage.Point(q, o))) < 0.15f))
                continue;
            spans++;
            var span = Span.Draw(random, q, 0.1f, 0.1f);
            SpanNoise At(float3 p)
            {
                var path = span.Path;
                path.midpoint = p;
                return ridged ? stage.Ridged(path) : stage.Fbm(path);
            }

            var centre = At(q);
            // The value and gradient from an independent assembly of the octaves: pins shift, offset, octaveStep,
            // M, lacunarity and gain themselves, which a value/gradient self-consistency check cannot see.
            var matrix = stage.Matrix();
            var referenceValue = 0.0;
            var referenceGradient = new double[3];
            for (var o = 0; o < stage.Octaves; o++)
            {
                var n = snoise_grad(stage.Point(q, o));
                var sign = ridged ? -Math.Sign(n.w) : 1.0;
                referenceValue += stage.Amplitude(o) * (ridged ? 1.0 - Math.Abs(n.w) : n.w);
                var g = new double[] { n.x, n.y, n.z };
                for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    referenceGradient[i] += sign * stage.Amplitude(o) * stage.Scale(o) * matrix[i, j] * g[j];
            }

            var scale = 0.0;
            for (var o = 0; o < stage.Octaves; o++)
                scale += Math.Abs(stage.Amplitude(o) * stage.Scale(o)) * stage.Norm();
            var amplitudes = Enumerable.Range(0, stage.Octaves).Sum(o => Math.Abs(stage.Amplitude(o)));
            Assert.True(Math.Abs(referenceValue - centre.field.w) <= 2.0e-3 * (1.0 + amplitudes), $"stage {stage}: the value is {centre.field.w:R}, an independent octave sum gives {referenceValue:R}");
            for (var i = 0; i < 3; i++)
                Assert.True(Math.Abs(referenceGradient[i] - centre.field[i]) <= 1.0e-2 * (1.0 + scale), $"stage {stage}: gradient {"xyz"[i]} is {centre.field[i]:R}, an independent octave sum gives {referenceGradient[i]:R}");
            // A step of 0.02 in the noise space of the fastest octave.
            var fastest = stage.Norm() * Enumerable.Range(0, stage.Octaves).Max(o => Math.Abs(stage.Scale(o)));
            var h = (float)(0.02 / fastest);
            for (var axis = 0; axis < 3; axis++)
            {
                var e = new float3(axis == 0 ? h : 0.0f, axis == 1 ? h : 0.0f, axis == 2 ? h : 0.0f);
                double F(float3 p) => At(p).field.w;
                var step = (double)(q + e)[axis] - (q - e)[axis];
                var d1 = (F(q + e) - F(q - e)) / step;
                var d2 = (F(q + e * 2.0f) - F(q - e * 2.0f)) / ((double)(q + e * 2.0f)[axis] - (q - e * 2.0f)[axis]);
                var estimate = (4.0 * d1 - d2) / 3.0;
                var tolerance = 4.0e-2 * scale;
                var error = Math.Abs(estimate - centre.field[axis]);
                worst = Math.Max(worst, error / scale);
                Assert.True(error <= tolerance, $"stage {stage}: d/d{"xyz"[axis]} of the value is {estimate:R} by differences and {centre.field[axis]:R} by span_{(ridged ? "ridged" : "fbm")} (tolerance {tolerance:R})");
                checkedCases++;
            }
        }

        Assert.True(checkedCases >= 120, $"only {checkedCases} derivatives were compared");
        output.WriteLine($"IV-REPORT span gradient ({(ridged ? "ridged" : "fbm")}): {checkedCases} derivatives, worst error {worst:R} of the summed octave gain");
    }

    /// <summary>
    /// stretch 1, or a zero axis, is the isotropic stage bit for bit whatever the other of the two is; a non-unit
    /// axis is its normalisation (the tested axes normalise exactly); a stretch that does apply changes the
    /// result.
    /// </summary>
    [Fact]
    public void SpanStageStretchAndAxis()
    {
        var random = new System.Random(0x57E7C4);
        for (var k = 0; k < 60; k++)
        {
            var stage = Stage.DrawSmooth(random, 300.0f, 15.0f, 3, 3);
            var q = Vector(random, -5.0f, 5.0f);
            var span = Span.Draw(random, q, 0.2f, 0.3f);
            var plain = stage with { S = stage.S with { stretch = 1.0f } };
            var noAxis = stage with { S = stage.S with { axis = None, stretch = 3.0f } };
            var reference = plain.Ridged(span.Path);
            var isotropic = (plain with { S = plain.S with { axis = None } }).Ridged(span.Path);
            var viaNoAxis = noAxis.Ridged(span.Path);
            var narrow = (stage with { S = stage.S with { axis = None, stretch = 0.25f } }).Ridged(span.Path);
            Assert.True(SameBits(reference.field, isotropic.field) && reference.curvature == isotropic.curvature && reference.fold == isotropic.fold, "stretch 1 with an axis differs from no axis");
            Assert.True(SameBits(isotropic.field, viaNoAxis.field) && isotropic.curvature == viaNoAxis.curvature && isotropic.fold == viaNoAxis.fold, "a zero axis with stretch 3 differs from the isotropic stage");
            Assert.True(SameBits(isotropic.field, narrow.field) && isotropic.curvature == narrow.curvature && isotropic.fold == narrow.fold, "a zero axis with stretch 0.25 differs from the isotropic stage");

            var unit = stage with { S = stage.S with { axis = new float3(0.0f, 1.0f, 0.0f) } };
            var scaled = stage with { S = stage.S with { axis = new float3(0.0f, 2.0f, 0.0f) } };
            var a = unit.Fbm(span.Path);
            var b = scaled.Fbm(span.Path);
            Assert.True(SameBits(a.field, b.field) && a.curvature == b.curvature, "a non-unit axis differs from its normalisation");
            var stretched = unit.Fbm(span.Path);
            Assert.False(SameBits(stretched.field, (unit with { S = unit.S with { stretch = 1.0f } }).Fbm(span.Path).field), "a stretch of " + unit.S.stretch + " did not change the field");
        }
    }

    // The coordinate bands the enclosure tests draw: a large offset (0 keeps the drawn one) lifts S_o through
    // the decades; Bucket names the decade a span's largest S_o is in.
    private static readonly float[] OffsetScales = { 0.0f, 40.0f, 400.0f, 4.0e3f, 4.0e4f, 4.0e5f, 4.0e6f };

    private static int Bucket(double magnitude) => magnitude < 1.0e2 ? 0 : magnitude < 1.0e3 ? 1 : magnitude < 1.0e4 ? 2 : magnitude < 1.0e5 ? 3 : magnitude < 1.0e6 ? 4 : 5;

    // byStep lifts the coordinate through octaveStep (octave o adds o octaveStep) instead of offset.
    private static Stage Banded(System.Random random, float offsetScale, int octaves, bool byStep)
    {
        var stage = Stage.Draw(random, octaves);
        if (offsetScale > 0.0f)
            stage = stage with { S = byStep ? stage.S with { octaveStep = Vector(random, -offsetScale, offsetScale) } : stage.S with { offset = Vector(random, -offsetScale, offsetScale) } };
        return stage;
    }

    private static void CheckCurvature(Stage stage, bool ridged, Span span, SpanNoise result, int samples, ref double tightest, ref int checkedPoints)
    {
        for (var s = 0; s < samples; s++)
        {
            var t = span.Path.halfLength * (2.0f * s / (samples - 1) - 1.0f);
            var norm = SymmetricNorm(FieldHessian(stage, span.At(t), ridged));
            Assert.True(norm <= result.curvature * (1.0 + 1.0e-3), $"stage {stage}, span {span}: the Hessian norm at t = {t:R} is {norm:R}, above curvature {result.curvature:R}");
            tightest = Math.Min(tightest, result.curvature > 0.0 ? 1.0 - norm / result.curvature : 1.0);
            checkedPoints++;
        }
    }

    /// <summary>
    /// span-bounds-enclose, curvature: over seeded spans the operator norm of the exact field q-Hessian (chain
    /// rule from the double kernel's Hessian, every octave point in double, M and the octave scales) at 64
    /// points per span never exceeds curvature, in the local regime (short spans, where the third-derivative
    /// term is the bound) and the global one (long spans, where SNOISE_HESSIAN is), and in every coordinate
    /// band from S_o under 1e2 to over 1e6, where the float32 kernel is no longer the exact one; at
    /// halfLength 0 curvature is the widened local tier, the Frobenius norm of the midpoint Hessians plus
    /// e_h, capped at the pin, scaled by amplitude, f_o^2 and |M|^2; and a span through MeasureHessian's
    /// witness stays enclosed in 200 directions, which the Frobenius norm at the midpoint alone does not
    /// guarantee.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanCurvatureEnclosesTheHessianAlongTheSpan(bool ridged)
    {
        var random = new System.Random(ridged ? 0xC0DE2 : 0xC0DE1);
        var tightest = 1.0;
        var checkedPoints = 0;
        var buckets = new int[6];
        foreach (var offsetScale in OffsetScales)
            foreach (var halfLength in new[] { 0.02f, 0.5f, 8.0f })
                for (var k = 0; k < 8; k++)
                {
                    var stage = Banded(random, offsetScale, 3, k % 2 == 1);
                    var span = Span.Draw(random, Vector(random, -30.0f, 30.0f), halfLength, Uniform(random, 0.0f, 2.0f));
                    var result = ridged ? stage.Ridged(span.Path) : stage.Fbm(span.Path);
                    buckets[Bucket(stage.MaxMagnitude(span.Path))]++;
                    CheckCurvature(stage, ridged, span, result, 64, ref tightest, ref checkedPoints);
                }

        Assert.True(buckets.All(count => count >= 3), $"the coordinate bands drawn are {string.Join(", ", buckets)} spans: a band is not reached");

        // halfLength 0: the widened local tier.
        for (var k = 0; k < 40; k++)
        {
            var stage = Stage.DrawSmooth(random, 100.0f, 90.0f, 3);
            var q = Vector(random, -30.0f, 30.0f);
            var span = Span.Draw(random, q, 0.0f, 1.0f);
            var result = ridged ? stage.Ridged(span.Path) : stage.Fbm(span.Path);
            var expected = 0.0;
            for (var o = 0; o < stage.Octaves; o++)
            {
                var h = snoise_hessian(stage.Point(q, o));
                var frobenius = Math.Sqrt(Enumerable.Range(0, 9).Sum(i => (double)h[i / 3][i % 3] * h[i / 3][i % 3]));
                var slack = Math.Pow(2.0, -10) + SNOISE_THIRD * Math.Pow(2.0, -20) * stage.Magnitude(span.Path, o);
                expected += Math.Abs(stage.Amplitude(o)) * stage.Scale(o) * stage.Scale(o) * stage.Norm() * stage.Norm() * Math.Min(SNOISE_HESSIAN, frobenius + slack);
            }

            Assert.InRange(result.curvature, expected * (1.0 - 1.0e-3), expected * (1.0 + 1.0e-3));
        }

        // Through the Hessian witness: one octave at the identity transform, the span's midpoint 0.1 to 0.5 away,
        // moving toward the witness at speed 1, so the witness lies on the span.
        var witness = new float3(-13.7920685f, -9.672134f, 61.563934f);
        var identity = new Stage(SpanStage.Identity, 1, 2.0f, 0.5f);
        var through = 0.0;
        for (var k = 0; k < 200; k++)
        {
            var u = UnitVector(random);
            var distance = Uniform(random, 0.1f, 0.5f);
            var span = new Span(new SpanPath { midpoint = witness + u * distance, velocity = -u, speed = 1.0f, acceleration = 0.0f, halfLength = 0.6f }, None);
            var result = ridged ? identity.Ridged(span.Path) : identity.Fbm(span.Path);
            CheckCurvature(identity, ridged, span, result, 64, ref tightest, ref checkedPoints);
            through = Math.Max(through, SymmetricNorm(FieldHessian(identity, witness, ridged)));
        }

        Assert.True(through > 40.0, $"the witness Hessian norm is {through:R}: the through-the-witness spans do not reach the curvature");
        output.WriteLine($"IV-REPORT span curvature ({(ridged ? "ridged" : "fbm")}): {checkedPoints} points enclosed, bands {string.Join("/", buckets)}, least relative slack {tightest:R}, witness norm {through:R}");
    }

    /// <summary>
    /// local-tier-coefficients-unpinned: a fixed span (found by an audit, kept) at SpanStage.Identity, one
    /// octave, where the Hessian's operator norm at the end of the span (11.768) exceeds the local tier built
    /// with half of SNOISE_THIRD's term (about 10.1) and the exact norm along the 64 sampled points stays
    /// under curvature (12.526 before the allowance).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanCurvatureEnclosesThePinnedRankOneSpan(bool ridged)
    {
        var stage = new Stage(SpanStage.Identity, 1, 2.0f, 0.5f);
        var span = new Span(new SpanPath { midpoint = new float3(144.93297f, -286.08800f, -53.59360f), velocity = new float3(-0.02139f, 0.17122f, -0.98500f), speed = 1.0f, acceleration = 0.0f, halfLength = 0.01f }, None);
        var result = ridged ? stage.Ridged(span.Path) : stage.Fbm(span.Path);
        var tightest = 1.0;
        var points = 0;
        CheckCurvature(stage, ridged, span, result, 64, ref tightest, ref points);
        var end = SymmetricNorm(FieldHessian(stage, span.At(span.Path.halfLength), ridged));
        Assert.True(end > 10.5 && result.curvature >= end, $"the end-of-span norm is {end:R} and curvature {result.curvature:R}: the span no longer pins the local tier");
    }

    /// <summary>
    /// span-bounds-enclose, fold: for span_ridged, the value's departure from the midpoint branch (each
    /// octave's gain^o (s n - |n|), s the sign of n at the midpoint, summed) never exceeds fold at 64 points of
    /// every span, measured twice: for the float field span_ridged itself evaluates at the path point, and for
    /// the exact octave sum (double kernel); in every coordinate band from S_o under 1e2 to over 1e6. fold is
    /// positive on spans that do cross zero (and the departure there is not trivially zero), exactly 0 for
    /// span_fbm and when no octave can reach zero.
    /// </summary>
    [Fact]
    public void SpanFoldEnclosesTheBranchDeviation()
    {
        var random = new System.Random(0xF01D);
        var crossing = 0;
        var largest = 0.0;
        var tightestFloat = 1.0;
        var tightestExact = 1.0;
        var buckets = new int[6];
        foreach (var offsetScale in OffsetScales)
            for (var k = 0; k < 60; k++)
            {
                var stage = Banded(random, offsetScale, 3, k / 2 % 2 == 1);
                var halfLength = new[] { 0.02f, 0.1f, 0.5f, 2.0f }[random.Next(4)];
                // Draw midpoints with small noise values so spans cross zero, as well as arbitrary ones.
                var q = Vector(random, -30.0f, 30.0f);
                if (k % 2 == 0)
                {
                    var best = q;
                    var smallest = float.MaxValue;
                    for (var trial = 0; trial < 40; trial++)
                    {
                        var candidate = Vector(random, -30.0f, 30.0f);
                        var value = abs(snoise(stage.Point(candidate, 0)));
                        if (value < smallest)
                            (smallest, best) = (value, candidate);
                    }

                    q = best;
                }

                var span = Span.Draw(random, q, halfLength, Uniform(random, 0.0f, 2.0f));
                buckets[Bucket(stage.MaxMagnitude(span.Path))]++;
                var fold = stage.Ridged(span.Path).fold;
                Assert.Equal(0.0f, stage.Fbm(span.Path).fold);
                var signs = Enumerable.Range(0, stage.Octaves).Select(o => Math.Sign(stage.OneOctave(o).Fbm(PathAt(q)).field.w)).ToArray();
                var peakFloat = 0.0;
                var peakExact = 0.0;
                for (var s = 0; s < 64; s++)
                {
                    var t = halfLength * (2.0f * s / 63 - 1.0f);
                    var point = span.At(t);
                    var departureFloat = 0.0;
                    var departureExact = 0.0;
                    for (var o = 0; o < stage.Octaves; o++)
                    {
                        var octave = stage.OneOctave(o);
                        var nFloat = (double)octave.Fbm(PathAt(point)).field.w;
                        var absFloat = 1.0 - octave.Ridged(PathAt(point)).field.w;
                        departureFloat += stage.Amplitude(o) * (signs[o] * nFloat - absFloat);
                        var x = stage.PointD(point, o);
                        var nExact = NoiseBoundTests.ValueDouble(x[0], x[1], x[2]);
                        departureExact += stage.Amplitude(o) * (signs[o] * nExact - Math.Abs(nExact));
                    }

                    peakFloat = Math.Max(peakFloat, Math.Abs(departureFloat));
                    peakExact = Math.Max(peakExact, Math.Abs(departureExact));
                    Assert.True(Math.Abs(departureFloat) <= fold * (1.0 + 1.0e-3) + 1.0e-6, $"stage {stage}, span {span}: the float branch departure at t = {t:R} is {departureFloat:R}, above fold {fold:R}");
                    Assert.True(Math.Abs(departureExact) <= fold * (1.0 + 1.0e-3) + 1.0e-6, $"stage {stage}, span {span}: the exact branch departure at t = {t:R} is {departureExact:R}, above fold {fold:R}");
                }

                if (fold > 0.0)
                    crossing++;
                largest = Math.Max(largest, Math.Max(peakFloat, peakExact));
                if (fold > 0.0 && peakFloat > 0.0)
                    tightestFloat = Math.Min(tightestFloat, peakFloat / fold);
                if (fold > 0.0 && peakExact > 0.0)
                    tightestExact = Math.Min(tightestExact, peakExact / fold);
            }

        Assert.True(buckets.All(count => count >= 3), $"the coordinate bands drawn are {string.Join(", ", buckets)} spans: a band is not reached");
        Assert.True(crossing >= 100, $"only {crossing} of {OffsetScales.Length * 60} spans had a positive fold");
        Assert.True(largest > 0.1, $"the largest branch departure drawn is {largest:R}: the check has no teeth");
        output.WriteLine($"IV-REPORT span fold: {crossing} spans with fold > 0, bands {string.Join("/", buckets)}, largest departure {largest:R}, tightest departure / fold {tightestFloat:R} (float) {tightestExact:R} (exact)");

        // An octave that cannot reach zero: a midpoint where |n| is large and a short span give fold exactly 0.
        var calm = new Stage(SpanStage.Identity, 1, 2.0f, 0.5f);
        var found = 0;
        for (var k = 0; k < 400 && found < 20; k++)
        {
            var q = Vector(random, -30.0f, 30.0f);
            if (abs(snoise(q)) < 0.6f)
                continue;
            found++;
            Assert.Equal(0.0f, calm.Ridged(new SpanPath { midpoint = q, velocity = new float3(1.0f, 0.0f, 0.0f), speed = 1.0f, acceleration = 0.0f, halfLength = 0.01f }).fold);
        }

        Assert.True(found >= 5, $"only {found} calm midpoints found");
    }

    /// <summary>
    /// The fold's acceleration term, alone, at the identity stage and under a transform (frequency 2.5, stretch
    /// 0.5, shift and offset): the midpoint velocity is zero and the acceleration points along the gradient
    /// (in q), against the sign of n, so n reaches zero and crosses it on the way to the span's ends by
    /// acceleration only. Without the acceleration term, with the gradient bound under the gradient the path
    /// reaches, or with the transform's norm f_o |M| misplaced in it, the departure from the midpoint branch
    /// exceeds fold.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanFoldCoversAccelerationAlone(bool transformed)
    {
        var random = new System.Random(transformed ? 0xACCE2 : 0xACCE1);
        var stage = transformed
            ? new Stage(new SpanStage { frequency = 2.5f, stretch = 0.5f, axis = new float3(0.0f, 1.0f, 0.0f), shift = new float3(0.3f, -1.2f, 2.1f), offset = new float3(5.0f, -3.0f, 7.0f), octaveStep = None }, 1, 2.0f, 0.5f)
            : new Stage(SpanStage.Identity, 1, 2.0f, 0.5f);
        var m = stage.Matrix();
        var cases = 0;
        var crossings = 0;
        var tightest = double.MaxValue;
        for (var trial = 0; trial < 400_000 && cases < 150; trial++)
        {
            var q = Vector(random, -60.0f, 60.0f);
            var n = snoise_grad(stage.Point(q, 0));
            var gradient = new float3((float)(m[0, 0] * n.x + m[0, 1] * n.y + m[0, 2] * n.z), (float)(m[1, 0] * n.x + m[1, 1] * n.y + m[1, 2] * n.z), (float)(m[2, 0] * n.x + m[2, 1] * n.y + m[2, 2] * n.z));
            var halfLength = Uniform(random, 0.05f, 0.15f);
            var acceleration = Uniform(random, 4.0f, 12.0f);
            // n at the midpoint close enough to zero that the acceleration alone carries it across.
            if (length(gradient) < 1.5f || abs(n.w) > 0.4f * length(gradient) * acceleration * halfLength * halfLength * 0.5f)
                continue;
            var c = -(float)Math.Sign(n.w) * acceleration * normalize(gradient);
            var span = new Span(new SpanPath { midpoint = q, velocity = None, speed = acceleration * halfLength, acceleration = acceleration, halfLength = halfLength }, c);
            var fold = stage.Ridged(span.Path).fold;
            var departure = 0.0;
            for (var s = 0; s < 64; s++)
            {
                var value = (double)snoise(stage.Point(span.At(halfLength * (2.0f * s / 63 - 1.0f)), 0));
                departure = Math.Max(departure, Math.Abs(Math.Sign(n.w) * value - Math.Abs(value)));
            }

            if (departure > 0.0)
                crossings++;
            Assert.True(departure <= fold * (1.0 + 1.0e-3), $"q {q}, acceleration {acceleration:R}, half-length {halfLength:R}: the branch departure is {departure:R}, above fold {fold:R}");
            if (departure > 0.0)
                tightest = Math.Min(tightest, departure / fold);
            cases++;
        }

        Assert.True(crossings >= 100, $"only {crossings} of {cases} paths crossed zero by acceleration alone");
        output.WriteLine($"IV-REPORT span fold, acceleration only ({(transformed ? "transformed" : "identity")}): {crossings} of {cases} paths cross zero, tightest departure / fold {tightest:R}");
    }

    /// <summary>
    /// The fold's velocity term under a stretch: the velocity runs along the stretched axis (M scales it by
    /// 1/stretch), the midpoint is where n reaches zero inside the span, so the departure is about twice
    /// |grad n . w| halfLength and a velocity stretched the wrong way (or not at all) puts fold under it.
    /// </summary>
    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(4.0f)]
    public void SpanFoldCoversTheStretchedVelocity(float stretch)
    {
        var random = new System.Random(0x57E1C + (int)(stretch * 8.0f));
        var cases = 0;
        var crossings = 0;
        var tightest = double.MaxValue;
        for (var trial = 0; trial < 400_000 && cases < 60; trial++)
        {
            var axis = UnitVector(random);
            var stage = new Stage(new SpanStage { frequency = Uniform(random, 0.5f, 2.0f), stretch = stretch, axis = axis, shift = Vector(random, -4.0f, 4.0f), offset = Vector(random, -4.0f, 4.0f), octaveStep = None }, 1, 2.0f, 0.5f);
            var q = Vector(random, -30.0f, 30.0f);
            var velocity = axis * Uniform(random, 0.5f, 2.0f);
            var halfLength = 0.05f;
            var path = new SpanPath { midpoint = q, velocity = velocity, speed = length(velocity), acceleration = 0.0f, halfLength = halfLength };
            var n = snoise_grad(stage.Point(q, 0));
            var m = stage.Matrix();
            var w = new double[3];
            for (var i = 0; i < 3; i++)
                w[i] = m[i, 0] * velocity.x + m[i, 1] * velocity.y + m[i, 2] * velocity.z;
            var along = Math.Abs(n.x * w[0] + n.y * w[1] + n.z * w[2]);
            if (along < 2.0 || Math.Abs(n.w) > 0.5 * along * halfLength)
                continue;
            var fold = stage.Ridged(path).fold;
            var departure = 0.0;
            for (var s = 0; s < 64; s++)
            {
                var t = halfLength * (2.0f * s / 63 - 1.0f);
                var value = (double)snoise(stage.Point(q + velocity * t, 0));
                departure = Math.Max(departure, Math.Abs(Math.Sign(n.w) * value - Math.Abs(value)));
            }

            if (departure > 0.0)
                crossings++;
            Assert.True(departure <= fold * (1.0 + 1.0e-3), $"stage {stage}, q {q}: the branch departure is {departure:R}, above fold {fold:R}");
            if (departure > 0.0)
                tightest = Math.Min(tightest, departure / fold);
            cases++;
        }

        Assert.True(crossings >= 30, $"only {crossings} of {cases} paths crossed zero along the stretched velocity");
        output.WriteLine($"IV-REPORT span fold, stretched velocity {stretch}: {crossings} of {cases} paths cross zero, tightest departure / fold {tightest:R}");
    }

    /// <summary>
    /// Both bounds are sums over the octaves, weighted by |gain|^o: for a stage of several octaves,
    /// curvature and fold equal the weighted sum of the bounds of each octave taken as a stage of its own,
    /// to within the rounding of forming the octave's coordinate another way. A weight misplaced (gain
    /// instead of |gain|, a division for a product), a dropped octave or a mis-scaled octave fails.
    /// </summary>
    [Fact]
    public void SpanBoundsAreSumsOverTheOctaves()
    {
        var random = new System.Random(0x0C7A5);
        var compared = 0;
        for (var k = 0; k < 80; k++)
        {
            var stage = Stage.DrawSmooth(random, 300.0f, 90.0f, 5, 2);
            var span = Span.Draw(random, Vector(random, -30.0f, 30.0f), new[] { 0.02f, 0.1f, 0.5f }[k % 3], Uniform(random, 0.0f, 2.0f));
            var whole = stage.Ridged(span.Path);
            double curvature = 0.0, fold = 0.0;
            for (var o = 0; o < stage.Octaves; o++)
            {
                var one = stage.OneOctave(o).Ridged(span.Path);
                var weight = Math.Abs(stage.Amplitude(o));
                curvature += weight * one.curvature;
                fold += weight * one.fold;
            }

            Assert.InRange(whole.curvature, curvature * (1.0 - 5.0e-3) - 1.0e-4, curvature * (1.0 + 5.0e-3) + 1.0e-4);
            Assert.InRange(whole.fold, fold * (1.0 - 5.0e-3) - 1.0e-4, fold * (1.0 + 5.0e-3) + 1.0e-4);
            if (fold > 1.0e-3)
                compared++;
        }

        Assert.True(compared >= 20, $"only {compared} of 80 stages had a fold to compare");
    }
}
