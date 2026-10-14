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

    // One draw of the stage parameters: every one of them off its identity value.
    private readonly record struct Stage(float Frequency, float Stretch, float3 Axis, float3 Shift, float3 Offset, float3 Step, int Octaves, float Lacunarity, float Gain)
    {
        public static Stage Draw(System.Random random, int octaves) => new(
            Uniform(random, 0.3f, 1.6f),
            random.Next(2) == 0 ? Uniform(random, 0.2f, 0.8f) : Uniform(random, 1.25f, 5.0f),
            UnitVector(random) * Uniform(random, 0.5f, 3.0f),
            Vector(random, -8.0f, 8.0f),
            Vector(random, -8.0f, 8.0f),
            Vector(random, -3.0f, 3.0f),
            octaves,
            Uniform(random, 1.5f, 2.3f),
            Uniform(random, 0.35f, 0.7f));

        public SpanNoise Fbm(float3 q, float3 velocity, float speed, float acceleration, float halfLength) =>
            span_fbm(q, velocity, speed, acceleration, halfLength, Frequency, Stretch, Axis, Shift, Offset, Step, Octaves, Lacunarity, Gain);

        public SpanNoise Ridged(float3 q, float3 velocity, float speed, float acceleration, float halfLength) =>
            span_ridged(q, velocity, speed, acceleration, halfLength, Frequency, Stretch, Axis, Shift, Offset, Step, Octaves, Lacunarity, Gain);

        // M = frequency (I + (1/stretch - 1) a a^T) in double, a the normalised axis (none for a zero axis).
        public double[,] Matrix()
        {
            var m = new double[3, 3];
            var a = new double[] { Axis.x, Axis.y, Axis.z };
            var length = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = Frequency * ((i == j ? 1.0 : 0.0) + (length > 0.0 ? (1.0 / Stretch - 1.0) * a[i] * a[j] / (length * length) : 0.0));
            return m;
        }

        // Octave o's noise-space point for the span point q.
        public float3 Point(float3 q, int octave)
        {
            var m = Matrix();
            var scale = Math.Pow(Lacunarity, octave);
            var x = new double[3];
            var qv = new double[] { q.x, q.y, q.z };
            var shift = new double[] { Shift.x, Shift.y, Shift.z };
            var offset = new double[] { Offset.x, Offset.y, Offset.z };
            var step = new double[] { Step.x, Step.y, Step.z };
            for (var i = 0; i < 3; i++)
            {
                var mq = 0.0;
                for (var j = 0; j < 3; j++)
                    mq += m[i, j] * qv[j];
                x[i] = scale * (mq + shift[i]) + offset[i] + octave * step[i];
            }

            return new float3((float)x[0], (float)x[1], (float)x[2]);
        }

        public double Amplitude(int octave) => Math.Pow(Gain, octave);

        public double Scale(int octave) => Math.Pow(Lacunarity, octave);

        // The spectral norm of M: frequency times the larger of 1 and 1/stretch.
        public double Norm() => Math.Abs(Frequency) * Math.Max(1.0, 1.0 / Stretch);
    }

    // A span through q: velocity v, constant acceleration c of norm `acceleration`, so q(t) = q + v t + c t^2 / 2;
    // the speed bound is |v| plus what c can add over halfLength.
    private readonly record struct Span(float3 Q, float3 Velocity, float3 Acceleration, float Speed, float AccelerationBound, float HalfLength)
    {
        public static Span Draw(System.Random random, float3 q, float halfLength, float acceleration)
        {
            var velocity = UnitVector(random) * Uniform(random, 0.2f, 3.0f);
            return new Span(q, velocity, UnitVector(random) * acceleration, length(velocity) + acceleration * halfLength, acceleration, halfLength);
        }

        public float3 At(float t) => Q + Velocity * t + Acceleration * (0.5f * t * t);
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

    // The Hessian in q of the stage field at span point q (signed per octave for a ridged sum), by the chain
    // rule from snoise_hessian: sum over octaves of weight_o f_o^2 M H_o M.
    private static double[] FieldHessian(Stage stage, float3 q, bool ridged)
    {
        var m = stage.Matrix();
        var total = new double[9];
        for (var o = 0; o < stage.Octaves; o++)
        {
            var x = stage.Point(q, o);
            var h = snoise_hessian(x);
            var weight = stage.Amplitude(o) * stage.Scale(o) * stage.Scale(o) * (ridged ? -Math.Sign(snoise(x)) : 1.0);
            var hd = new double[3, 3];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                hd[i, j] = h[i][j];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < 3; k++)
                for (var l = 0; l < 3; l++)
                    sum += m[i, k] * hd[k, l] * m[l, j];
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
    /// At frequency 1, stretch 1 (with and without an axis) and zero shift, offset and octaveStep, the field of
    /// span_fbm and span_ridged is fbm_grad's and ridged_grad's, bit for bit, for 0 to 20 octaves (the clamp
    /// included), every lacunarity and gain drawn, over random and simplex-tie points: any change to the octave
    /// loop's order, scaling or accumulation fails.
    /// </summary>
    [Fact]
    public void SpanStagesAtIdentityAreFbmAndRidged()
    {
        var random = new System.Random(0x57A6E1);
        var none = new float3(0.0f, 0.0f, 0.0f);
        foreach (var p in Points(random))
        {
            var octaves = random.Next(0, 21);
            var lacunarity = Uniform(random, 1.4f, 2.6f);
            var gain = Uniform(random, 0.3f, 0.8f);
            var axis = random.Next(2) == 0 ? none : UnitVector(random) * 2.0f;
            var fbm = span_fbm(p, UnitVector(random), 1.0f, 0.5f, 0.25f, 1.0f, 1.0f, axis, none, none, none, octaves, lacunarity, gain);
            var ridged = span_ridged(p, UnitVector(random), 1.0f, 0.5f, 0.25f, 1.0f, 1.0f, axis, none, none, none, octaves, lacunarity, gain);
            Assert.True(SameBits(fbm.field, fbm_grad(p, octaves, lacunarity, gain)), $"span_fbm at {p}, {octaves} octaves: {fbm.field} is not fbm_grad {fbm_grad(p, octaves, lacunarity, gain)}");
            Assert.True(SameBits(ridged.field, ridged_grad(p, octaves, lacunarity, gain)), $"span_ridged at {p}, {octaves} octaves: {ridged.field} is not ridged_grad {ridged_grad(p, octaves, lacunarity, gain)}");
            Assert.Equal(0.0f, fbm.fold);
        }
    }

    /// <summary>
    /// The gradient is the derivative of the value through the whole transform: with every parameter off its
    /// identity (frequency, stretch both ways, axis, shift, offset, octaveStep, lacunarity, gain), field.xyz
    /// equals the fourth-order central difference of field.w along each axis, within a tolerance built from the
    /// third-derivative pin (truncation) and float32 rounding of the coordinates. A dropped M or lacunarity
    /// factor, or M applied to the wrong side, moves it by the gradient itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanStageGradientMatchesDifferences(bool ridged)
    {
        var random = new System.Random(ridged ? 0x6A1D : 0x6A1C);
        var worst = 0.0;
        var checkedCases = 0;
        for (var k = 0; k < 400; k++)
        {
            var stage = Stage.Draw(random, ridged ? random.Next(1, 3) : random.Next(1, 4));
            var q = Vector(random, -3.0f, 3.0f);
            // The fold's crease at n = 0 is not differentiable: keep every octave's stencil clear of it.
            if (ridged && Enumerable.Range(0, stage.Octaves).Any(o => abs(snoise(stage.Point(q, o))) < 0.3f))
                continue;
            var span = Span.Draw(random, q, 0.1f, 0.1f);
            SpanNoise At(float3 p) => ridged ? stage.Ridged(p, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength) : stage.Fbm(p, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            var centre = At(q);
            // A step of 0.02 in the noise space of the fastest octave.
            var fastest = stage.Norm() * Math.Pow(stage.Lacunarity, stage.Octaves - 1);
            var h = (float)(0.02 / fastest);
            var scale = 0.0;
            for (var o = 0; o < stage.Octaves; o++)
                scale += stage.Amplitude(o) * stage.Scale(o) * stage.Norm();
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
            var stage = Stage.Draw(random, 3);
            var q = Vector(random, -5.0f, 5.0f);
            var span = Span.Draw(random, q, 0.2f, 0.3f);
            var plain = stage with { Stretch = 1.0f };
            var noAxis = stage with { Axis = new float3(0.0f, 0.0f, 0.0f), Stretch = 3.0f };
            var reference = plain.Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            var isotropic = (plain with { Axis = new float3(0.0f, 0.0f, 0.0f) }).Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            var viaNoAxis = noAxis.Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            Assert.True(SameBits(reference.field, isotropic.field) && reference.curvature == isotropic.curvature && reference.fold == isotropic.fold, "stretch 1 with an axis differs from no axis");
            Assert.True(SameBits(isotropic.field, viaNoAxis.field) && isotropic.curvature == viaNoAxis.curvature && isotropic.fold == viaNoAxis.fold, "a zero axis with stretch 3 differs from the isotropic stage");

            var unit = stage with { Axis = new float3(0.0f, 1.0f, 0.0f) };
            var scaled = stage with { Axis = new float3(0.0f, 2.0f, 0.0f) };
            var a = unit.Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            var b = scaled.Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            Assert.True(SameBits(a.field, b.field) && a.curvature == b.curvature, "a non-unit axis differs from its normalisation");
            var stretched = unit.Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
            Assert.False(SameBits(stretched.field, (unit with { Stretch = 1.0f }).Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength).field), "a stretch of " + unit.Stretch + " did not change the field");
        }
    }

    // The stage parameters and per-octave bookkeeping the curvature checks share.
    private static void CheckCurvature(Stage stage, bool ridged, Span span, SpanNoise result, int samples, ref double tightest, ref int checkedPoints)
    {
        for (var s = 0; s < samples; s++)
        {
            var t = span.HalfLength * (2.0f * s / (samples - 1) - 1.0f);
            var norm = SymmetricNorm(FieldHessian(stage, span.At(t), ridged));
            Assert.True(norm <= result.curvature * (1.0 + 1.0e-3), $"stage {stage}, span {span}: the Hessian norm at t = {t:R} is {norm:R}, above curvature {result.curvature:R}");
            tightest = Math.Min(tightest, result.curvature > 0.0 ? 1.0 - norm / result.curvature : 1.0);
            checkedPoints++;
        }
    }

    /// <summary>
    /// span-bounds-enclose, curvature: over seeded spans the operator norm of the field's q-Hessian (chain rule
    /// from snoise_hessian, M and the octave scales) at 64 points per span never exceeds curvature, in the
    /// local regime (short spans, where the third-derivative term is the bound) and the global one (long spans,
    /// where SNOISE_HESSIAN is); at halfLength 0 curvature is the local tier, the Frobenius norm of the
    /// midpoint Hessians scaled by amplitude, f_o^2 and |M|^2, to within float rounding; and a span through
    /// MeasureHessian's witness stays enclosed in 200 directions, which the Frobenius norm at the midpoint alone
    /// does not guarantee.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanCurvatureEnclosesTheHessianAlongTheSpan(bool ridged)
    {
        var random = new System.Random(ridged ? 0xC0DE2 : 0xC0DE1);
        var tightest = 1.0;
        var checkedPoints = 0;
        foreach (var halfLength in new[] { 0.002f, 0.02f, 0.2f, 1.0f, 8.0f })
            for (var k = 0; k < 24; k++)
            {
                var stage = Stage.Draw(random, random.Next(1, 4));
                var span = Span.Draw(random, Vector(random, -30.0f, 30.0f), halfLength, Uniform(random, 0.0f, 2.0f));
                var result = ridged ? stage.Ridged(span.Q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength) : stage.Fbm(span.Q, span.Velocity, span.Speed, span.AccelerationBound, span.HalfLength);
                CheckCurvature(stage, ridged, span, result, 64, ref tightest, ref checkedPoints);
            }

        // halfLength 0: the local tier exactly.
        for (var k = 0; k < 40; k++)
        {
            var stage = Stage.Draw(random, random.Next(1, 4));
            var q = Vector(random, -30.0f, 30.0f);
            var span = Span.Draw(random, q, 0.0f, 1.0f);
            var result = ridged ? stage.Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, 0.0f) : stage.Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, 0.0f);
            var expected = 0.0;
            for (var o = 0; o < stage.Octaves; o++)
            {
                var h = snoise_hessian(stage.Point(q, o));
                var frobenius = Math.Sqrt(Enumerable.Range(0, 9).Sum(i => (double)h[i / 3][i % 3] * h[i / 3][i % 3]));
                expected += stage.Amplitude(o) * stage.Scale(o) * stage.Scale(o) * stage.Norm() * stage.Norm() * Math.Min(SNOISE_HESSIAN, frobenius);
            }

            Assert.InRange(result.curvature, expected * (1.0 - 1.0e-3), expected * (1.0 + 1.0e-3));
        }

        // Through the Hessian witness: one octave at the identity transform, the span's midpoint 0.1 to 0.5 away,
        // moving toward the witness at speed 1, so the witness lies on the span.
        var witness = new float3(-13.7920685f, -9.672134f, 61.563934f);
        var identity = new Stage(1.0f, 1.0f, new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), 1, 2.0f, 0.5f);
        var through = 0.0;
        for (var k = 0; k < 200; k++)
        {
            var u = UnitVector(random);
            var distance = Uniform(random, 0.1f, 0.5f);
            var span = new Span(witness + u * distance, -u, new float3(0.0f, 0.0f, 0.0f), 1.0f, 0.0f, 0.6f);
            var result = ridged ? identity.Ridged(span.Q, span.Velocity, 1.0f, 0.0f, 0.6f) : identity.Fbm(span.Q, span.Velocity, 1.0f, 0.0f, 0.6f);
            CheckCurvature(identity, ridged, span, result, 64, ref tightest, ref checkedPoints);
            through = Math.Max(through, SymmetricNorm(FieldHessian(identity, witness, ridged)));
        }

        Assert.True(through > 40.0, $"the witness Hessian norm is {through:R}: the through-the-witness spans do not reach the curvature");
        output.WriteLine($"IV-REPORT span curvature ({(ridged ? "ridged" : "fbm")}): {checkedPoints} points enclosed, least relative slack {tightest:R}, witness norm {through:R}");
    }

    /// <summary>
    /// span-bounds-enclose, fold: for span_ridged, the value's departure from the midpoint branch (each
    /// octave's gain^o (s n - |n|), s the sign of n at the midpoint, summed) never exceeds fold at 64 points of
    /// every span; fold is positive on spans that do cross zero (and the departure there is not trivially
    /// zero), exactly 0 when no octave can reach zero and for span_fbm, and 0 at halfLength 0.
    /// </summary>
    [Fact]
    public void SpanFoldEnclosesTheBranchDeviation()
    {
        var random = new System.Random(0xF01D);
        var crossing = 0;
        var largest = 0.0;
        var tightest = 1.0;
        for (var k = 0; k < 400; k++)
        {
            var stage = Stage.Draw(random, random.Next(1, 4));
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
            var fold = stage.Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, halfLength).fold;
            Assert.Equal(0.0f, stage.Fbm(q, span.Velocity, span.Speed, span.AccelerationBound, halfLength).fold);
            Assert.Equal(0.0f, stage.Ridged(q, span.Velocity, span.Speed, span.AccelerationBound, 0.0f).fold);
            var signs = Enumerable.Range(0, stage.Octaves).Select(o => Math.Sign(snoise(stage.Point(q, o)))).ToArray();
            var peak = 0.0;
            for (var s = 0; s < 64; s++)
            {
                var t = halfLength * (2.0f * s / 63 - 1.0f);
                var point = span.At(t);
                var departure = 0.0;
                for (var o = 0; o < stage.Octaves; o++)
                {
                    var n = (double)snoise(stage.Point(point, o));
                    departure += stage.Amplitude(o) * (signs[o] * n - Math.Abs(n));
                }

                peak = Math.Max(peak, Math.Abs(departure));
                Assert.True(Math.Abs(departure) <= fold * (1.0 + 1.0e-3), $"stage {stage}, span {span}: the branch departure at t = {t:R} is {departure:R}, above fold {fold:R}");
            }

            if (fold > 0.0)
                crossing++;
            largest = Math.Max(largest, peak);
            if (fold > 0.0 && peak > 0.0)
                tightest = Math.Min(tightest, peak / fold);
        }

        Assert.True(crossing >= 100, $"only {crossing} of 400 spans had a positive fold");
        Assert.True(largest > 0.1, $"the largest branch departure drawn is {largest:R}: the check has no teeth");
        output.WriteLine($"IV-REPORT span fold: {crossing} spans with fold > 0, largest departure {largest:R}, tightest departure / fold {tightest:R}");

        // An octave that cannot reach zero: a midpoint where |n| is large and a short span give fold exactly 0.
        var calm = new Stage(1.0f, 1.0f, new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), new float3(0.0f, 0.0f, 0.0f), 1, 2.0f, 0.5f);
        var found = 0;
        for (var k = 0; k < 400 && found < 20; k++)
        {
            var q = Vector(random, -30.0f, 30.0f);
            if (abs(snoise(q)) < 0.6f)
                continue;
            found++;
            Assert.Equal(0.0f, calm.Ridged(q, new float3(1.0f, 0.0f, 0.0f), 1.0f, 0.0f, 0.01f).fold);
        }

        Assert.True(found >= 5, $"only {found} calm midpoints found");
    }
}
