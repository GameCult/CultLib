using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The real shader mirror, <c>shaders/CultMath.hlsl</c>, compiles as C# against CultMath under
/// <c>using static CultMath.math;</c> after exactly the source transformations listed in
/// docs/design.md (HLSL Target: Source Transformations). <see cref="TransformHlslToCSharp"/> is
/// that list in code; if a body needs anything else, this test fails.
/// </summary>
public sealed class HlslSourceCompatibilityTests
{
    [Fact]
    public void ShaderMirrorBodiesCompileAsCSharp()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, "HLSL mirror did not compile as C#:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        var shader = assembly!.GetType("CultMathHlsl.HlslShader")!;
        Assert.NotNull(ShaderFunction(shader, "cultmath_snoise", typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_snoise", typeof(float2)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_value_noise_bicubic", typeof(float2)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_smin_grad", typeof(float4), typeof(float4), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_cellular", typeof(float3)));
        var siteCells = shader.GetNestedType("CultSiteCells", BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.NotNull(ShaderFunction(shader, "cultmath_power_score", typeof(float3), typeof(float4)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_multiplicative_score", typeof(float3), typeof(float4)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_site_cells_empty"));
        Assert.NotNull(ShaderFunction(shader, "cultmath_site_cells_insert", siteCells, typeof(float4), typeof(int)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_site_cells_border", siteCells));
        Assert.NotNull(ShaderFunction(shader, "cultmath_gabriel_margin", typeof(float3), typeof(float3), typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_power_border_step", typeof(float3), typeof(float4), typeof(float4), typeof(float4)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_segment_closest", typeof(float3), typeof(float3), typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_distance_to_segment", typeof(float3), typeof(float3), typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_snoise_grad", typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_snoise_hessian", typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_integral_clamp_linear", typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_fbm_grad", typeof(float3), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_ridged_grad", typeof(float3), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_phacelle", typeof(float3), typeof(float3), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_snoise_ball", typeof(float3), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_fbm_ball", typeof(float3), typeof(float), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_snoise_ball", typeof(float2), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_snoise", typeof(float3), typeof(float3), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_fbm", typeof(float3), typeof(float3), typeof(float), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_frustum_ball", typeof(float2), typeof(float), typeof(float), typeof(float), typeof(float)));
        var (spanPath, spanStage) = SpanTypes(shader);
        Assert.NotNull(ShaderFunction(shader, "cultmath_span_fbm", spanPath, spanStage, typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_span_ridged", spanPath, spanStage, typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_span_stage", typeof(bool), spanPath, spanStage, typeof(int), typeof(float), typeof(float)));
    }

    // The mirror's struct types are private nested types of HlslShader (a struct without a modifier).
    private static (Type Path, Type Stage) SpanTypes(Type shader) =>
        (shader.GetNestedType("SpanPath", BindingFlags.Public | BindingFlags.NonPublic)!, shader.GetNestedType("SpanStage", BindingFlags.Public | BindingFlags.NonPublic)!);

    // HLSL functions carry no access modifier, so they compile as private instance methods.
    private static MethodInfo? ShaderFunction(Type shader, string name, params Type[] parameters) =>
        shader.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null);

    // Shader constants with no C# twin of the same name (empty when every constant has one).
    private static readonly string[] ConstantsWithoutTwin = Array.Empty<string>();

    /// <summary>
    /// Every static const of the compiled HLSL mirror (a static literal CULTMATH_* field, whatever its access) is the
    /// same float32, bit for bit, as the math constant of the same name without the prefix (private ones
    /// included), so a pin edited in one runtime and not the other fails here whichever constant it is.
    /// A constant with neither a twin nor a listing in ConstantsWithoutTwin fails too.
    /// </summary>
    [Fact]
    public void EveryShaderConstantEqualsItsCSharpTwin()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shader = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var constants = shader.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("CULTMATH_", StringComparison.Ordinal))
            .ToArray();
        Assert.True(constants.Length >= 10, $"only {constants.Length} shader constants were found by reflection");
        foreach (var constant in constants)
        {
            var name = constant.Name["CULTMATH_".Length..];
            var twin = typeof(math).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (twin is null)
            {
                Assert.True(ConstantsWithoutTwin.Contains(constant.Name), $"{constant.Name} has no math.{name} and is not listed in ConstantsWithoutTwin");
                continue;
            }

            var shaderValue = (float)constant.GetRawConstantValue()!;
            var mathValue = (float)twin.GetValue(null)!;
            Assert.True(BitConverter.SingleToInt32Bits(shaderValue) == BitConverter.SingleToInt32Bits(mathValue),
                $"{constant.Name} is {shaderValue:R} in the shader and math.{name} is {mathValue:R}");
        }
    }

    // Special values every mirror argument takes: zeros, ties, negatives, large values, NaN and both infinities.
    private static readonly float[] Specials = { 0.0f, -0.0f, 1.0f, -1.0f, 0.5f, -2.75f, 3.0f, 1.0e-7f, 1.0e4f, -1.0e4f, 1.0e7f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };

    /// <summary>
    /// Every mirror function matches the math function of the same name, public or private (a function with no
    /// twin fails, and nothing is exempt), with a struct parameter mapped by name and fields to the CultMath
    /// type of the same name, and returns the same bits (any NaN equals any NaN;
    /// -0 differs from 0) over random inputs, every special value in every argument (ties, zeros,
    /// negatives, NaN, infinities, large values, integer lattice points), special pairs, and vectors whose first two
    /// components tie (x0.x == x0.y in snoise). This proves the text of CultMath.hlsl computes the same
    /// float32 results as C# math on the CPU. It does not prove GPU agreement: driver sin precision alone
    /// makes cultmath_hash and value noise differ bit for bit on hardware. The mirror's intrinsics are C#
    /// math itself (it compiles under using static CultMath.math), so this proves the composition in the
    /// text, not the intrinsic rules; HlslSemanticsTests alone pins those.
    /// </summary>
    [Fact]
    public void EveryMirrorFunctionMatchesCSharpMath()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);

        var random = new System.Random(0x5EED);
        float Next() => random.NextSingle() * 200.0f - 100.0f;
        var cases = new List<Func<int, float>>();
        foreach (var s in Specials) cases.Add(_ => s);
        for (var k = 0; k < Specials.Length; k++) { var o = k; cases.Add(i => Specials[(i + o + 1) % Specials.Length]); }
        for (var k = 0; k < 64; k++) { var values = Enumerable.Range(0, 32).Select(_ => Next()).ToArray(); cases.Add(i => values[i]); }
        for (var k = 0; k < 16; k++) { var values = Enumerable.Range(0, 32).Select(_ => Next()).ToArray(); cases.Add(i => values[i & ~1]); }

        // Points aimed squarely at cellular's HLSL mirror (F6/F3 Soul findings): the generic random
        // and special-value cases above pass ~92 inputs through cultmath_cellular and never happen to
        // hit a point where a radius-2 cell wins (about 3.5e-5 of points, Soul measured) or an
        // integer point at or past 2^23, so an HLSL mutant of the search radius, the prune, or the
        // removed F2=0 guard can survive this test even though it changes cellular's real output.
        void AddPointCase(float3 point)
        {
            var values = new[] { point.x, point.y, point.z };
            cases.Add(i => i < values.Length ? values[i] : 0.0f);
        }

        // The six constructed points from CellularAndSminGradTests.SearchReachesEveryAxisAlignedRadiusTwoSlice:
        // each one realizes a different axis-aligned radius-2 offset as F1 or F2, which is exactly
        // the case an `if (lowerBound >= f2)` prune written as `>= f1`, or a search radius narrowed on
        // one axis (`dz < 2`, `dx` starting at -1), changes.
        foreach (var offset in CellularAndSminGradTests.AxisAlignedRadiusTwoOffsets)
            AddPointCase(CellularAndSminGradTests.FindPointRealizingOffset(offset));

        // Integer points at 2^24, several, both signs: past |p| ~= 2^23 distinct cells' jittered
        // features can round onto the same float32 value (design.md, "Precision domain"), making
        // F2 = 0 reachable and exercising the grad2 = f2 > 0 ? ... : zero guard this test would
        // otherwise never touch.
        const float twoTo24 = 16777216.0f;
        AddPointCase(new float3(twoTo24, twoTo24, twoTo24));
        AddPointCase(new float3(-twoTo24, -twoTo24, -twoTo24));
        AddPointCase(new float3(twoTo24 + 3.0f, -twoTo24 + 5.0f, twoTo24 - 7.0f));
        AddPointCase(new float3(-twoTo24 + 11.0f, twoTo24 - 13.0f, -twoTo24 + 17.0f));

        // One exact F1 = F2 tie point: the midpoint of two adjacent cells' feature points is
        // bit-exact-equidistant from both by construction (negating a vector does not change its
        // length, and p - a = -(p - b) here by symmetry), without needing to search for one.
        var tieMidpoint = (CellularAndSminGradTests.FeaturePoint(new float3(0.0f, 0.0f, 0.0f))
            + CellularAndSminGradTests.FeaturePoint(new float3(1.0f, 0.0f, 0.0f))) * 0.5f;
        AddPointCase(tieMidpoint);

        var mismatches = new List<string>();
        var compared = new HashSet<string>();
        foreach (var mirror in shaderType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name.StartsWith("cultmath_")))
        {
            var types = mirror.GetParameters().Select(p => p.ParameterType).ToArray();
            var counterpart = Twin(shaderType, mirror.Name["cultmath_".Length..], types);
            Assert.True(counterpart is not null, $"{mirror.Name} has no C# math function of the same name and parameter types, public or private");

            compared.Add(mirror.Name);
            foreach (var input in cases)
            {
                var slot = 0;
                var args = types.Select(t => Build(t, () => input(slot++))).ToArray();
                var expected = Values(counterpart!.Invoke(null, args.Select(a => CopyFields(a, TwinType(shaderType, a.GetType())!)).ToArray())!);
                var actual = Values(mirror.Invoke(shader, args)!);
                if (!BitwiseEqual(expected, actual))
                {
                    mismatches.Add($"{mirror.Name}({string.Join(", ", args.Select(a => string.Join(" ", Values(a).Select(v => v.Value))))}): C# {string.Join(" ", expected.Select(v => v.Value))}, mirror {string.Join(" ", actual.Select(v => v.Value))}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(12))}");
        Assert.Equal(136, compared.Count);
    }

    /// <summary>
    /// The 1500 arguments (a, b, lo, hi, t0, t1) at which integral_clamp_linear's zero-length rule, its lo
    /// normalisation and its infinite bounds all bite: a in {-2.5, 0, 1.5}, b in {-1.5, -0, 0, 2}, lo in
    /// {-inf, -1, 0.5, 3, +inf}, hi in {-inf, -0.5, 1, 4, +inf}, and (t0, t1) in {(0, 0), (1.25, 1.25), (-1, 2),
    /// (2, -1), (0, 1e-30)}. One spelling, read by the mirror grid test and by the GLSL clamp shadow test.
    /// </summary>
    internal static IReadOnlyList<float[]> IntegralClampLinearEdgeGrid { get; } = BuildIntegralClampLinearEdgeGrid();

    private static List<float[]> BuildIntegralClampLinearEdgeGrid()
    {
        var inf = float.PositiveInfinity;
        var grid = new List<float[]>();
        foreach (var a in new[] { -2.5f, 0.0f, 1.5f })
        foreach (var b in new[] { -1.5f, -0.0f, 0.0f, 2.0f })
        foreach (var lo in new[] { -inf, -1.0f, 0.5f, 3.0f, inf })
        foreach (var hi in new[] { -inf, -0.5f, 1.0f, 4.0f, inf })
        foreach (var (t0, t1) in new[] { (0.0f, 0.0f), (1.25f, 1.25f), (-1.0f, 2.0f), (2.0f, -1.0f), (0.0f, 1.0e-30f) })
            grid.Add(new[] { a, b, lo, hi, t0, t1 });
        return grid;
    }

    /// <summary>
    /// The edge grid followed by the committed GLSL fixture's cases of the three integral_clamp_linear families
    /// (GlslMirrorTests.IntegralClampLinearFixtureCases, decoded): one list, read by the mirror test and by the
    /// clamp-shadow test. The grid fixes a in {-2.5, 0, 1.5}, so a guard keyed on a past 1.75 hides from it
    /// (Soul s4); the fixture's random a in [-4, 4] closes that.
    /// </summary>
    internal static IReadOnlyList<float[]> IntegralClampLinearCases => IntegralClampLinearEdgeGrid.Concat(GlslMirrorTests.IntegralClampLinearFixtureCases).ToArray();

    /// <summary>
    /// The compiled mirror's cultmath_integral_clamp_linear returns C#'s bits on every edge case (the grid and
    /// the fixture's cases), and C#'s result is never NaN there. The generic case set catches each of the function's guards (the held and
    /// kept stretch guards, the flat branch's zero-length guard) only by its rotation: with the kept guard
    /// removed it finds 0 mismatches, with the held guard removed 1 once infinities join the specials, and the
    /// edge grid finds 246 for either every time.
    /// </summary>
    [Fact]
    public void IntegralClampLinearMirrorMatchesCSharpOnTheEdgeCases()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var mirror = ShaderFunction(shaderType, "cultmath_integral_clamp_linear", Enumerable.Repeat(typeof(float), 6).ToArray())!;

        var mismatches = new List<string>();
        var cases = IntegralClampLinearCases;
        foreach (var c in cases)
        {
            var expected = integral_clamp_linear(c[0], c[1], c[2], c[3], c[4], c[5]);
            var actual = (float)mirror.Invoke(shader, c.Cast<object>().ToArray())!;
            Assert.False(float.IsNaN(expected), $"C# returns NaN at ({string.Join(", ", c)})");
            if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
                mismatches.Add($"({string.Join(", ", c)}): C# {expected:R}, mirror {actual:R}");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {cases.Count} cases differ:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(12))}");
    }

    /// <summary>
    /// At a SpanStage the shader has no identity constant for, so C#'s identity (copied field by field) is the
    /// argument: the mirror's cultmath_span_fbm and cultmath_span_ridged return the field of the mirror's own
    /// cultmath_fbm_grad and cultmath_ridged_grad, bit for bit, for 0 to 20 octaves over random and simplex-tie
    /// points. The shader-side statement of math.SpanStage.Identity (design decision a: no shader identity).
    /// </summary>
    [Fact]
    public void SpanStagesAtIdentityAreFbmAndRidgedInTheMirror()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var (pathType, stageType) = SpanTypes(shaderType);
        var spanArguments = new[] { pathType, stageType, typeof(int), typeof(float), typeof(float) };
        var pairs = new[]
        {
            (Span: ShaderFunction(shaderType, "cultmath_span_fbm", spanArguments)!, Plain: ShaderFunction(shaderType, "cultmath_fbm_grad", typeof(float3), typeof(int), typeof(float), typeof(float))!),
            (Span: ShaderFunction(shaderType, "cultmath_span_ridged", spanArguments)!, Plain: ShaderFunction(shaderType, "cultmath_ridged_grad", typeof(float3), typeof(int), typeof(float), typeof(float))!),
        };
        var identity = CopyFields(SpanStage.Identity, stageType);

        var random = new System.Random(0x57A6E2);
        var compared = 0;
        foreach (var p in SpanStageTests.Points(random))
        {
            var octaves = random.Next(0, 21);
            var lacunarity = 1.4f + 1.2f * random.NextSingle();
            var gain = 0.3f + 0.5f * random.NextSingle();
            var path = CopyFields(new SpanPath { midpoint = p, velocity = new float3(0.6f, 0.0f, 0.8f), speed = 1.0f, acceleration = 0.5f, halfLength = 0.25f }, pathType);
            foreach (var (span, plain) in pairs)
            {
                var field = FieldOf(span.Invoke(shader, new object[] { path, identity, octaves, lacunarity, gain })!, "field");
                var expected = plain.Invoke(shader, new object[] { p, octaves, lacunarity, gain })!;
                Assert.True(BitwiseEqual(Values(expected), Values(field)), $"{span.Name} at {p}, {octaves} octaves: {string.Join(" ", Values(field).Select(v => v.Value))} is not {plain.Name} {string.Join(" ", Values(expected).Select(v => v.Value))}");
                compared++;
            }
        }

        Assert.True(compared >= 460, $"only {compared} identity comparisons ran");
    }

    /// <summary>
    /// The generic comparison draws every argument from [-100, 100] (and specials), where the octave loop's
    /// frequency, stretch and span terms saturate: 0 or 16 octaves, a Hessian bound pinned at its global value.
    /// This sweep draws SpanStageTests' realistic stages and spans (4000, one in four with a zero axis, one in
    /// four with a point span, halfLength 0), both stages, and the mirror must return C#'s bits for the whole
    /// SpanNoise. Each of stretched and unstretched stages, and of positive and zero fold, must be met at least
    /// 500 times, or the sweep proves nothing about that branch.
    /// </summary>
    [Fact]
    public void SpanStageMirrorMatchesCSharpBitForBitOnRealisticSpans()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var (pathType, stageType) = SpanTypes(shaderType);
        var spanArguments = new[] { pathType, stageType, typeof(int), typeof(float), typeof(float) };
        var mirrors = new[] { ShaderFunction(shaderType, "cultmath_span_fbm", spanArguments)!, ShaderFunction(shaderType, "cultmath_span_ridged", spanArguments)! };

        var random = new System.Random(0x5EED4);
        float Next(float extent) => (random.NextSingle() * 2.0f - 1.0f) * extent;
        int stretched = 0, unstretched = 0, folded = 0, flat = 0;
        for (var i = 0; i < 4000; i++)
        {
            var drawn = SpanStageTests.Stage.Draw(random);
            var stage = drawn.S;
            if (i % 4 == 1)
                stage.axis = new float3(0.0f, 0.0f, 0.0f);
            var halfLength = i % 4 == 2 ? 0.0f : new[] { 0.02f, 0.1f, 0.5f, 2.0f }[random.Next(4)];
            var span = SpanStageTests.Span.Draw(random, new float3(Next(30.0f), Next(30.0f), Next(30.0f)), halfLength, random.NextSingle() * 2.0f);
            var arguments = new object[] { CopyFields(span.Path, pathType), CopyFields(stage, stageType), drawn.Octaves, drawn.Lacunarity, drawn.Gain };
            if (dot(stage.axis, stage.axis) > 0.0f && stage.stretch != 1.0f)
                stretched++;
            else
                unstretched++;
            for (var m = 0; m < 2; m++)
            {
                var expected = m == 0 ? span_fbm(span.Path, stage, drawn.Octaves, drawn.Lacunarity, drawn.Gain) : span_ridged(span.Path, stage, drawn.Octaves, drawn.Lacunarity, drawn.Gain);
                var actual = mirrors[m].Invoke(shader, arguments)!;
                Assert.True(BitwiseEqual(Values(expected), Values(actual)),
                    $"{mirrors[m].Name} differs from C# at midpoint {span.Path.midpoint}, halfLength {halfLength:R}, {drawn.Octaves} octaves: C# {string.Join(" ", Values(expected).Select(v => v.Value))}, mirror {string.Join(" ", Values(actual).Select(v => v.Value))}");
                if (m == 1 && expected.fold > 0.0f)
                    folded++;
                else if (m == 1)
                    flat++;
            }
        }

        Assert.True(stretched >= 500 && unstretched >= 500 && folded >= 500 && flat >= 500,
            $"the sweep meets a branch too rarely: stretched {stretched}, unstretched {unstretched}, fold > 0 {folded}, fold = 0 {flat} of 4000");
    }

    /// <summary>
    /// The generic comparison above passes about a hundred inputs through <c>cultmath_phacelle</c>, and
    /// its prune only changes an output for the rare cell whose weight is nonzero but whose lower
    /// bound sits just under the 2.25 cut (Soul's F6 lesson for cellular): a mirror whose cut is 2.0
    /// survives a hundred inputs. A dense sweep of realistic arguments closes that gap.
    /// </summary>
    [Fact]
    public void PhacelleMirrorMatchesCSharpBitForBitAcrossADenseSweep()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var mirror = ShaderFunction(shaderType, "cultmath_phacelle", typeof(float3), typeof(float3), typeof(float), typeof(float))!;

        var random = new System.Random(0x5EED2);
        float Next(float extent) => (random.NextSingle() * 2.0f - 1.0f) * extent;
        for (var i = 0; i < 20000; i++)
        {
            var p = new float3(Next(50.0f), Next(50.0f), Next(50.0f));
            var side = new float3(Next(9.0f), Next(9.0f), Next(9.0f));
            var offset = random.NextSingle();
            var normalization = random.NextSingle();
            var expected = Values(phacelle(p, side, offset, normalization));
            var actual = Values(mirror.Invoke(shader, new object[] { p, side, offset, normalization })!);
            Assert.True(BitwiseEqual(expected, actual), $"mirror differs from C# at p = {p}, side = {side}");
        }
    }

    /// <summary>
    /// The generic comparison draws centre, axis and radius from [-100, 100], where the reach is far past the
    /// 0.15 or so below which <c>cultmath_af_snoise</c>'s centred form is the narrower one, so an HLSL edit
    /// of the choice between the centred and the Lipschitz form (a threshold, a reordered sum) survives it.
    /// This sweep draws realistic slices (axis and radius log-uniform in 1e-3 to 0.5, the reach of a tile
    /// near a camera), which land on both sides of the choice and next to it, for <c>cultmath_af_snoise</c>
    /// and <c>cultmath_af_fbm</c>.
    /// </summary>
    [Fact]
    public void AffineSnoiseMirrorMatchesCSharpBitForBitOnBothSidesOfItsBranch()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var snoiseMirror = ShaderFunction(shaderType, "cultmath_af_snoise", typeof(float3), typeof(float3), typeof(float))!;
        var fbmMirror = ShaderFunction(shaderType, "cultmath_af_fbm", typeof(float3), typeof(float3), typeof(float), typeof(int), typeof(float), typeof(float))!;

        var random = new System.Random(0x5EED3);
        float Next(float extent) => (random.NextSingle() * 2.0f - 1.0f) * extent;
        float Reach() => 1.0e-3f * MathF.Pow(500.0f, random.NextSingle());
        int centred = 0, lipschitz = 0;
        for (var i = 0; i < 20000; i++)
        {
            var centre = new float3(Next(50.0f), Next(50.0f), Next(50.0f));
            var axis = normalize(new float3(Next(1.0f), Next(1.0f), Next(1.0f))) * Reach();
            var radius = Reach();
            var expected = af_snoise(centre, axis, radius);
            var actual = Values(snoiseMirror.Invoke(shader, new object[] { centre, axis, radius })!);
            Assert.True(BitwiseEqual(Values(expected), actual), $"cultmath_af_snoise differs from C# at centre = {centre}, axis = {axis}, radius = {radius}");
            if (expected.y != 0.0f)
                centred++;
            else
                lipschitz++;

            if (i % 8 == 0)
            {
                var fbmExpected = Values(af_fbm(centre, axis, radius, 4, 2.0f, 0.5f));
                var fbmActual = Values(fbmMirror.Invoke(shader, new object[] { centre, axis, radius, 4, 2.0f, 0.5f })!);
                Assert.True(BitwiseEqual(fbmExpected, fbmActual), $"cultmath_af_fbm differs from C# at centre = {centre}, axis = {axis}, radius = {radius}");
            }
        }

        Assert.True(centred > 2000 && lipschitz > 2000, $"the sweep meets one branch too rarely: centred {centred}, Lipschitz {lipschitz} of 20000");
    }

    /// <summary>
    /// The generic comparison stops at 1e7, so an HLSL edit of <c>cultmath_af_range</c>'s NaN test that
    /// misreads the finite values near the float maximum (or an overflowed endpoint) survives it. Forms
    /// from zero to the float maximum, infinite and NaN components, both signs, in every slot: the mirror
    /// returns C#'s bits, and neither endpoint is ever NaN.
    /// </summary>
    [Fact]
    public void AffineRangeMirrorMatchesCSharpBitForBitAtTheFloatExtremes()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var mirror = ShaderFunction(shaderType, "cultmath_af_range", typeof(float3))!;

        var magnitudes = new[] { 0.0f, 1.0f, 1.0e30f, 1.0e38f, float.MaxValue / 2.0f, float.MaxValue, float.PositiveInfinity, float.NaN };
        var values = magnitudes.Concat(magnitudes.Where(m => !float.IsNaN(m)).Select(m => -m)).ToArray();
        foreach (var x0 in values)
        foreach (var a in values)
        foreach (var e in values)
        {
            var form = new float3(x0, a, e);
            var expected = af_range(form);
            var actual = Values(mirror.Invoke(shader, new object[] { form })!);
            Assert.True(BitwiseEqual(Values(expected), actual), $"cultmath_af_range differs from C# at {form}");
            Assert.False(float.IsNaN(expected.x) || float.IsNaN(expected.y), $"af_range({form}) is {expected}, a NaN endpoint");
        }
    }

    /// <summary>
    /// The dense sweep almost never meets a cell whose box bound is just under the 2.25 cut and whose
    /// weight is still nonzero: a mirror cut of 2.245, 2.24 or 2.2 survives it. These points sit next
    /// to such cells (<see cref="PhacelleTests.PruneCutPoints"/>), so the mirror must match C# bit for
    /// bit, and must match the unpruned C# sum (the shader has no prune switch, so that is the
    /// independent statement that the cut is exact).
    /// </summary>
    [Fact]
    public void PhacelleMirrorMatchesCSharpBitForBitNextToCellsJustInsideTheCut()
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var mirror = ShaderFunction(shaderType, "cultmath_phacelle", typeof(float3), typeof(float3), typeof(float), typeof(float))!;

        foreach (var p in PhacelleTests.PruneCutPoints)
        {
            Assert.True(PhacelleTests.HasCellJustInsideTheCut(p), $"no cell just inside the cut at {p}");
            var actual = Values(mirror.Invoke(shader, new object[] { p, PhacelleTests.PruneCutSide, PhacelleTests.PruneCutOffset, PhacelleTests.PruneCutNormalization })!);
            var pruned = Values(phacelle(p, PhacelleTests.PruneCutSide, PhacelleTests.PruneCutOffset, PhacelleTests.PruneCutNormalization));
            var unpruned = Values(phacelle(p, PhacelleTests.PruneCutSide, PhacelleTests.PruneCutOffset, PhacelleTests.PruneCutNormalization, false));
            Assert.True(BitwiseEqual(pruned, actual), $"mirror differs from C# at p = {p}");
            Assert.True(BitwiseEqual(unpruned, actual), $"mirror differs from the unpruned C# sum at p = {p}");
        }
    }

    /// <summary>
    /// A hand-built pair of structs standing in for a mirror struct return and its C# counterpart
    /// (invariant 8's one struct return shape, e.g. <see cref="CultCellular"/>): same field shape,
    /// different concrete type, exactly like the real mirror comparison. Proves the field-by-field,
    /// bit-for-bit walk actually rejects a struct whose field differs; without this, the walk could
    /// have been comparing lengths only, or skipping the nested vector's own components.
    /// </summary>
    private struct StructMirrorProbeExpected { public float4 first; public float second; }
    private struct StructMirrorProbeActual { public float4 first; public float second; }

    [Fact]
    public void StructReturnComparisonRejectsAMismatchedField()
    {
        var expected = new StructMirrorProbeExpected { first = new float4(1.0f, 2.0f, 3.0f, 4.0f), second = 5.0f };
        var matching = new StructMirrorProbeActual { first = new float4(1.0f, 2.0f, 3.0f, 4.0f), second = 5.0f };
        var mismatchedLeaf = new StructMirrorProbeActual { first = new float4(1.0f, 2.0f, 3.0f, 4.0f), second = 5.5f };
        var mismatchedNestedComponent = new StructMirrorProbeActual { first = new float4(1.0f, 2.0f, -3.0f, 4.0f), second = 5.0f };

        Assert.True(BitwiseEqual(Values(expected), Values(matching)));
        Assert.False(BitwiseEqual(Values(expected), Values(mismatchedLeaf)));
        Assert.False(BitwiseEqual(Values(expected), Values(mismatchedNestedComponent)));
    }

    /// <summary>
    /// Struct shapes standing in for CultCellular's own field order (<c>nearest; edge; id</c>), used
    /// to falsify two named mutants (F2): H2 declares the mirror struct with its first two fields
    /// swapped (<c>edge; nearest; id</c>) and swaps the assignments to match, so every leaf VALUE
    /// still lands in the same POSITION as the correct struct; only the field NAME at each position
    /// differs. H3 renames and retypes every field (id becomes an int carrying the same bits) while
    /// keeping the original position order, so only NAME and TYPE differ, never position.
    /// </summary>
    private struct NearestEdgeIdExpected { public float4 nearest; public float4 edge; public float id; }
    private struct EdgeNearestIdSwappedNames { public float4 edge; public float4 nearest; public float id; }
    private struct RenamedAndRetypedFields { public float4 a; public float4 b; public int c; }

    [Fact]
    public void StructReturnComparisonRejectsFieldsSwappedByNameEvenWhenValuesAlign()
    {
        // H2: the mutant struct's field ORDER is (edge, nearest, id), and its assignments are
        // swapped to match, so declaration-position values are identical to the correct struct's.
        // Only checking names (not just position) can tell these apart.
        var expected = new NearestEdgeIdExpected
        {
            nearest = new float4(1.0f, 2.0f, 3.0f, 4.0f),
            edge = new float4(5.0f, 6.0f, 7.0f, 8.0f),
            id = 9.0f,
        };
        var swapped = new EdgeNearestIdSwappedNames
        {
            edge = new float4(1.0f, 2.0f, 3.0f, 4.0f), // holds what should be "nearest"'s value.
            nearest = new float4(5.0f, 6.0f, 7.0f, 8.0f), // holds what should be "edge"'s value.
            id = 9.0f,
        };

        Assert.False(BitwiseEqual(Values(expected), Values(swapped)));
    }

    [Fact]
    public void StructReturnComparisonRejectsRenamedAndRetypedFields()
    {
        // H3: field order matches the correct struct exactly, but every field is renamed, and id is
        // retyped from float to int carrying the same bit pattern the correct id would round to.
        var expected = new NearestEdgeIdExpected
        {
            nearest = new float4(1.0f, 2.0f, 3.0f, 4.0f),
            edge = new float4(5.0f, 6.0f, 7.0f, 8.0f),
            id = 9.0f,
        };
        var renamed = new RenamedAndRetypedFields
        {
            a = new float4(1.0f, 2.0f, 3.0f, 4.0f),
            b = new float4(5.0f, 6.0f, 7.0f, 8.0f),
            c = BitConverter.SingleToInt32Bits(9.0f),
        };

        Assert.False(BitwiseEqual(Values(expected), Values(renamed)));
    }

    // Integer arguments take the bit pattern of the float input, so specials become 0, 0x80000000, NaN bits, ...
    // A bool argument is true when its float input is positive.
    private static readonly Type[] Scalars = { typeof(float), typeof(int), typeof(uint), typeof(bool) };

    private static object Scalar(Type t, float f) => t == typeof(float) ? f
        : t == typeof(bool) ? f > 0.0f
        : t == typeof(int) ? BitConverter.SingleToInt32Bits(f) : (object)(uint)BitConverter.SingleToInt32Bits(f);

    private static FieldInfo[] InstanceFields(Type t) => t.GetFields(BindingFlags.Public | BindingFlags.Instance);

    // One function ARGUMENT from the case's scalars, in field order: a scalar from its float, a vector by the
    // constructor taking its scalar fields, a struct field by field, recursively.
    private static object Build(Type t, Func<float> next)
    {
        if (Scalars.Contains(t))
            return Scalar(t, next());
        var fields = InstanceFields(t);
        var constructor = t.GetConstructor(fields.Select(f => f.FieldType).ToArray());
        if (constructor is not null)
            return constructor.Invoke(fields.Select(f => Build(f.FieldType, next)).ToArray());
        var value = Activator.CreateInstance(t)!;
        foreach (var field in fields)
            field.SetValue(value, Build(field.FieldType, next));
        return value;
    }

    // The CultMath type a mirror parameter type stands for: itself, or for a type nested in HlslShader the
    // CultMath type of the same name, only if both have the same instance fields by name, type (mapped
    // likewise) and order. Null when there is no such twin.
    private static Type? TwinType(Type shaderType, Type mirror)
    {
        if (mirror.DeclaringType != shaderType)
            return mirror;
        var twin = typeof(math).Assembly.GetType("CultMath." + mirror.Name);
        if (twin is null)
            return null;
        var mine = InstanceFields(mirror);
        var theirs = InstanceFields(twin);
        return mine.Length == theirs.Length && mine.Zip(theirs).All(p => p.First.Name == p.Second.Name && TwinType(shaderType, p.First.FieldType) == p.Second.FieldType) ? twin : null;
    }

    // The math function of the mirror function's name, with its parameter types mapped, public or private.
    private static MethodInfo? Twin(Type shaderType, string name, Type[] mirrorTypes)
    {
        var types = mirrorTypes.Select(t => TwinType(shaderType, t)).ToArray();
        return types.Any(t => t is null) ? null
            : typeof(math).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, types!, null);
    }

    // A struct value as another struct type of the same fields by name: itself when the type is the same.
    private static object CopyFields(object source, Type target)
    {
        if (source.GetType() == target)
            return source;
        var copy = Activator.CreateInstance(target)!;
        foreach (var field in InstanceFields(target))
            field.SetValue(copy, CopyFields(source.GetType().GetField(field.Name)!.GetValue(source)!, field.FieldType));
        return copy;
    }

    private static object FieldOf(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;

    /// <summary>
    /// One flattened scalar leaf of a (possibly nested) return value: its declaration path (field
    /// names joined by '.'), its declared type, and its value. Carrying path and type, not just the
    /// value, is what lets the struct-return comparison reject a field that was renamed or retyped
    /// but landed in the same position (F2): a positional-only comparison cannot tell "edge" holding
    /// nearest's value apart from "nearest" holding it.
    /// </summary>
    private readonly record struct Leaf(string Path, Type Type, object Value);

    /// <summary>
    /// Flattens a return value to its scalar leaves, recursing through struct fields of any type
    /// (invariant 8's one struct return shape, e.g. <see cref="CultCellular"/>'s float4/float4/float
    /// fields) so a struct return is compared field by field, down to bit-for-bit scalars, exactly
    /// like a bare vector return already is. Each leaf keeps the field name (and its ancestors') and
    /// declared type it was read from.
    /// </summary>
    private static Leaf[] Values(object value, string path = "") => Scalars.Contains(value.GetType())
        ? new[] { new Leaf(path, value.GetType(), value) }
        : value is float3x3 matrix
        ? new[] { matrix._m00, matrix._m01, matrix._m02, matrix._m10, matrix._m11, matrix._m12, matrix._m20, matrix._m21, matrix._m22 }
            .Select((entry, k) => new Leaf((path.Length == 0 ? "" : path + ".") + "_m" + k / 3 + k % 3, typeof(float), entry)).ToArray()
        : value.GetType().GetFields().Where(f => !f.IsStatic)
            .SelectMany(f => Values(f.GetValue(value)!, path.Length == 0 ? f.Name : path + "." + f.Name))
            .ToArray();

    // Compares leaves by NAME and TYPE as well as value, in declaration order, so a mirror struct
    // whose fields are reordered, renamed, or retyped relative to its C# counterpart fails here even
    // when the flattened values happen to line up positionally (F2's H2/H3 mutants).
    private static bool BitwiseEqual(Leaf[] expected, Leaf[] actual) =>
        expected.Length == actual.Length && expected.Zip(actual).All(p =>
            p.First.Path == p.Second.Path && p.First.Type == p.Second.Type &&
            (p.First.Value is float a && p.Second.Value is float b
                ? (float.IsNaN(a) && float.IsNaN(b)) || BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)
                : p.First.Value.Equals(p.Second.Value)));

    /// <summary>
    /// The text of <c>CultMath.hlsl</c> with each <c>#include "name"</c> replaced in place by the
    /// included file from the same directory, as the shader compiler would resolve it. Only the
    /// package's own includes exist, so a missing file is an error rather than a skip. An include for
    /// which <paramref name="inline"/> returns false is dropped with its line instead (the GLSL
    /// lowering's separately licensed files); the C# mirror inlines every one.
    /// </summary>
    internal static string ReadShaderSource(string cultMathRoot, Func<string, bool>? inline = null)
    {
        var shaders = Path.Combine(cultMathRoot, "shaders");
        return Regex.Replace(File.ReadAllText(Path.Combine(shaders, "CultMath.hlsl")), @"(?m)^#include ""([^""]+)""[ \t]*\r?$(\n?)",
            match => inline is null || inline(match.Groups[1].Value)
                ? File.ReadAllText(Path.Combine(shaders, match.Groups[1].Value)) + match.Groups[2].Value
                : string.Empty);
    }

    /// <summary>
    /// The documented HLSL-to-C# transformations, and nothing else. <paramref name="members"/> is C# source a
    /// test supplies, placed first in the class body: it is not a transformation of the HLSL.
    /// </summary>
    internal static string TransformHlslToCSharp(string hlsl, string members = "")
    {
        // 1. Include guards (#ifndef/#define/#endif) are dropped; C# has no textual include.
        var source = Regex.Replace(hlsl, @"(?m)^\s*#(ifndef|define|endif)\b.*$", string.Empty);

        // 2. Functions taking Texture2D/SamplerState resources are dropped; GPU resources have
        //    no CultMath analog.
        source = Regex.Replace(source, @"(?ms)^\w+ \w+\([^)]*\b(Texture2D|SamplerState)\b[^)]*\)\s*\{.*?^\}[ \t]*\r?$", string.Empty);

        // 3. File-scope `static const` becomes `const` (C# consts are implicitly static).
        source = Regex.Replace(source, @"(?m)^static const ", "const ");

        // 4. Floating literals gain the `f` suffix; an unsuffixed C# literal is a double.
        source = Regex.Replace(source, @"(?<![\w.])(\d+\.\d*(?:[eE][+-]?\d+)?|\d+[eE][+-]?\d+)(?![\w.])", "$1f");

        // 5. Struct fields gain `public`; HLSL has no field access-modifier concept, and a C# struct's
        //    fields default to private, which would hide them from the mirror test's reflection-based
        //    field comparison (invariant 8's one struct return shape, e.g. CultCellular) and the struct
        //    arguments' copies (SpanPath, SpanStage), which reflection reads and writes by field name.
        source = Regex.Replace(source, @"(?ms)(struct\s+\w+\s*\{)(.*?)(\};)", m =>
            m.Groups[1].Value + Regex.Replace(m.Groups[2].Value, @"(?m)^(\s*)(\w+\s+\w+;)", "$1public $2") + m.Groups[3].Value);

        // 6. The file body is wrapped in a class; C# has no free functions.
        return "using CultMath;\nusing static CultMath.math;\nnamespace CultMathHlsl;\npublic class HlslShader\n{\n" + members + source + "\n}\n";
    }

    internal static (Assembly? Assembly, IReadOnlyList<string> Errors) CompileShaderMirror(string members = "")
    {
        var hlsl = ReadShaderSource(FindCultMathRoot());
        var tree = CSharpSyntaxTree.ParseText(TransformHlslToCSharp(hlsl, members));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(math).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "CultMathHlslMirror",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var errors = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        return (result.Success ? Assembly.Load(stream.ToArray()) : null, errors);
    }

    internal static string FindCultMathRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "packages", "cultmath", "shaders", "CultMath.hlsl")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("CultMath package root not found."), "packages", "cultmath");
    }

    // ---- Hand-written HLSL-shaped host code: swizzles, mixed constructors, matrix rows. ----
    static float3 shade(float3 position, float3 normal, float2 wind, float time)
    {
        float3 p = position;
        p.xz = p.xz + wind * time;
        p.y = saturate(p.y);

        float3x3 basis = float3x3(float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1));
        basis[1][1] = 1.0f;
        float3 n = normalize(mul(basis, normal));
        float ndotl = saturate(dot(n, normalize(float3(0.3f, 1, 0.2f))));
        float4 color = float4(lerp(float3(0.1f, 0.1f, 0.2f), float3(1, 0.9f, 0.7f), ndotl), 1);

        if (any(p < 0.0f))
            color.rgb *= 0.5f;
        if (all(abs(n) <= 1.0f))
            color.bgr = color.bgr * float3(1, 1, 1);

        color.rgb = select(p > 10.0f, float3(0), color.rgb);
        float2 uv = frac(p.xz * 0.25f);
        color.rg = lerp(color.rg, uv, 0.1f);
        return float4(color.xy, color.zw).xyz;
    }

    static float2 steer(float2 direction, float torque)
    {
        float2x2 turn = float2x2(cos(torque), -sin(torque), sin(torque), cos(torque));
        float2 turned = mul(direction, turn);
        return normalize(turned) * pow(length(turned), 1.0f);
    }

    [Fact]
    public void HandWrittenShaderStyleMathEvaluates()
    {
        var color = shade(float3(1.0f, 0.5f, 2.0f), float3(0.0f, 1.0f, 0.0f), float2(1.0f, 0.0f), 0.0f);
        Assert.True(color.x > 0.1f && color.x <= 1.0f);
        Assert.True(float.IsFinite(color.y) && float.IsFinite(color.z));

        var far = shade(float3(20.0f, 20.0f, 20.0f), float3(0.0f, 1.0f, 0.0f), float2(0.0f), 0.0f);
        Assert.Equal(0.0f, far.z);

        var steered = steer(float2(1.0f, 0.0f), 0.5f);
        var viaRotate = mul(float2(1.0f, 0.0f), CultMath.float2x2.Rotate(0.5f));
        Assert.Equal(viaRotate.x, steered.x, precision: 5);
        Assert.Equal(viaRotate.y, steered.y, precision: 5);
    }
}
