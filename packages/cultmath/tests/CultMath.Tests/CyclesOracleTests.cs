using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// math.cycles_* and the shader mirror's cultmath_cycles_* (shaders/CultMath.Cycles.hlsl, compiled as C#) against
/// the results of Blender 5.2.2's own Cycles on the CPU, the reference of ruling
/// cultmath-tapes:ruling:cycles-kernel-reference-ruled: Cycles' formulas with one IEEE single rounding per
/// operation. The fixtures under fixtures/cycles-oracle are written by
/// tools/cycles-oracle/oracle.py (never by hand): each case is the operands and the node's baked results as
/// float32 bit patterns. Every function is compared at every case within the tolerance named in
/// <see cref="Tolerances"/>; a function not named there is held to 0 ulp. Signed zeros are not distinguished (-0
/// equals 0), and NaN equals NaN. The fixtures come from Cycles' SSE4.2 CPU kernel, which does not fuse
/// multiply-adds as C# does not (oracle.py checks this before it bakes); other builds are other tolerance classes
/// (tools/cycles-oracle/README.md). Cycles' film writes 0 for an infinite or
/// NaN result, so a case whose result here is not finite and whose fixture result is 0 holds nothing to compare.
/// </summary>
public sealed class CyclesOracleTests
{
    // The ulp distance a function is allowed from the reference. Empty: the fixtures are the SSE4.2 kernel's results
    // and every function equals them, power and logarithm included (glibc's powf and logf, 0 ulp from MathF on Linux).
    // An entry needs a measured case on the reference named in it (a failing case's bits and ulp); other builds'
    // tolerances belong to the class table in tools/cycles-oracle/README.md, not here.
    private static readonly Dictionary<string, (int Ulp, string Measured)> Tolerances = new();

    private const string CSharp = "C#";
    private const string Mirror = "HLSL mirror";

    private static readonly Lazy<(object Shader, Type Type)> ShaderMirror = new(() =>
    {
        var (assembly, errors) = HlslSourceCompatibilityTests.CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var type = assembly!.GetType("CultMathHlsl.HlslShader")!;
        return (Activator.CreateInstance(type)!, type);
    });

    /// <summary>One implementation of the Cycles functions: CultMath's C# or the HLSL text compiled as C#.</summary>
    private static object Call(string target, string function, params object[] arguments)
    {
        if (target == CSharp)
            return Method(function).Invoke(null, arguments)!;
        var (shader, type) = ShaderMirror.Value;
        var method = type.GetMethod("cultmath_" + function, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("no mirror function cultmath_" + function);
        return method.Invoke(shader, arguments)!;
    }

    public static IEnumerable<object[]> Families() =>
        from family in new[] { "math", "map-range", "clamp", "mix", "rgb-to-bw" }
        from target in new[] { CSharp, Mirror }
        select new object[] { family, target };

    public static IEnumerable<object[]> Targets() => new[] { CSharp, Mirror }.Select(target => new object[] { target });

    private static string FixturePath(string family) =>
        Path.Combine(HlslSourceCompatibilityTests.FindCultMathRoot(), "tests", "CultMath.Tests", "fixtures", "cycles-oracle", family + ".json");

    private static JsonObject Load(string family) => JsonNode.Parse(File.ReadAllText(FixturePath(family)))!.AsObject();

    private static float Float(string token) => BitConverter.Int32BitsToSingle((int)Convert.ToUInt32(token, 16));

    // A float32 bit pattern as an integer whose order and differences are the floats' order and ulps.
    private static int Ordered(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? int.MinValue - bits : bits;
    }

    private static long Distance(float want, float have) =>
        want == have || (float.IsNaN(want) && float.IsNaN(have)) ? 0 : float.IsNaN(want) || float.IsNaN(have) ? long.MaxValue : Math.Abs((long)Ordered(want) - Ordered(have));

    // The method takes floats and float3s only; each float3 is three consecutive tokens.
    private static object[] Arguments(MethodInfo method, string[] tokens)
    {
        var values = new List<object>();
        var next = 0;
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(float))
                values.Add(Float(tokens[next++]));
            else if (parameter.ParameterType == typeof(float3))
            {
                values.Add(new float3(Float(tokens[next]), Float(tokens[next + 1]), Float(tokens[next + 2])));
                next += 3;
            }
            else
                throw new InvalidOperationException($"{method.Name}: parameter type {parameter.ParameterType.Name} is not a fixture operand");
        }

        // The operands a function does not take (Map Range's Steps, for the three kinds that ignore it) are left unread.
        Assert.True(next <= tokens.Length, $"{method.Name}: {next} operands wanted, {tokens.Length} in the case");
        return values.ToArray();
    }

    private static float[] Flatten(object result) => result switch
    {
        float f => new[] { f },
        float2 v => new[] { v.x, v.y },
        float3 v => new[] { v.x, v.y, v.z },
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static MethodInfo Method(string name) =>
        typeof(math).GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("no CultMath function " + name);

    [Theory]
    [MemberData(nameof(Families))]
    public void FamilyMatchesBlender(string family, string target)
    {
        var fixture = Load(family);
        Assert.Equal("5.2.2 LTS", (string?)fixture["blender"]);
        Assert.Equal("d13f752e3b9c", (string?)fixture["build_hash"]);
        var functions = fixture["functions"]!.AsArray();
        Assert.NotEmpty(functions);
        var failures = new List<string>();
        foreach (var entry in functions)
        {
            var name = (string)entry!["function"]!;
            var method = Method(name);
            var clamped = (string?)entry["then"] is not null;
            var allowed = Tolerances.TryGetValue(name, out var tolerance) ? tolerance.Ulp : 0;
            var worst = 0L;
            string? worstCase = null;
            var cases = entry["cases"]!.AsArray();
            Assert.True(cases.Count >= 1000, $"{name}: only {cases.Count} fixture cases");
            foreach (var line in cases)
            {
                var sides = ((string)line!).Split(" -> ");
                var operands = sides[0].Split(' ');
                var have = Flatten(Call(target, name, Arguments(method, operands)));
                if (clamped)
                {
                    // A Map Range node's Clamp option is a Clamp node of type Range on its result.
                    have = Flatten(Call(target, "cycles_clamp_range", have[0], Float(operands[3]), Float(operands[4])));
                }

                var want = sides[1].Split(' ').Select(Float).ToArray();
                Assert.Equal(want.Length, have.Length);
                for (var k = 0; k < want.Length; k++)
                {
                    if (!float.IsFinite(have[k]) && want[k] == 0.0f)
                        continue;
                    var distance = Distance(want[k], have[k]);
                    if (distance > worst)
                    {
                        worst = distance;
                        worstCase = $"{line} ({target} {BitConverter.SingleToInt32Bits(have[k]):x8})";
                    }
                }
            }

            if (worst > allowed)
                failures.Add($"{target} {name}{(clamped ? " with Clamp" : string.Empty)}: {worst} ulp from Blender, allowed {allowed}; worst case {worstCase}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static JsonNode RampEntry(string interpolation) =>
        Load("ramp")["functions"]!.AsArray().Single(e => e!["node"]!.ToString() == "ShaderNodeValToRGB " + interpolation)!;

    [Theory]
    [MemberData(nameof(Targets))]
    public void RampIndexNamesTheEntryBlenderReads(string target)
    {
        var entry = RampEntry("CONSTANT");
        var size = (int)entry["table_entries"]!;
        var stops = entry["stops"]!.AsArray().Select(stop => (Position: Float((string)stop![0]!), Value: Float((string)stop[1]!))).ToArray();

        // A constant ramp's table: the value of the last stop at or before the entry's position.
        var table = Enumerable.Range(0, size).Select(i =>
        {
            var position = (double)i / (size - 1);
            var stop = stops.LastOrDefault(s => s.Position <= position);
            return stop == default ? stops[0].Value : stop.Value;
        }).ToArray();

        var cases = entry["cases"]!.AsArray().Select(line => ((string)line!).Split(" -> ")).ToArray();
        Assert.True(table.Distinct().Count() > 30, "the constant ramp's table should name 32 entries apart");
        foreach (var line in cases)
        {
            var lookup = Flatten(Call(target, "cycles_ramp_index", Float(line[0]), size));
            var index = (int)lookup[0];
            Assert.InRange(index, 0, size - 1);
            Assert.True(Distance(Float(line[1]), table[index]) == 0, $"ramp t {line[0]}: index {index} reads {table[index]}, Blender {Float(line[1])}");
        }

        // Every stop's value appears in the results at its own boundary, so the cases do cross the entries.
        Assert.True(cases.Select(line => line[1]).Distinct().Count() >= 32, "the cases never cross the stops");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void RampWeightBlendsTheEntryWithTheNextOne(string target)
    {
        // A linear ramp from 0 to 1: entry i holds i / (size - 1) to within the float rounding of Blender's table, and
        // a lookup at (index, weight) reads (index + weight) / (size - 1). A weight that is off by a fraction of a
        // percent, or an index that is off by one, moves the result by more than the bound.
        var entry = RampEntry("LINEAR");
        var size = (int)entry["table_entries"]!;
        var worst = 0.0;
        var fractional = 0;
        foreach (var line in entry["cases"]!.AsArray().Select(line => ((string)line!).Split(" -> ")))
        {
            var lookup = Flatten(Call(target, "cycles_ramp_index", Float(line[0]), size));
            Assert.InRange(lookup[1], 0.0f, 1.0f);
            fractional += lookup[1] > 0.0f ? 1 : 0;
            var expected = (lookup[0] + lookup[1]) / (size - 1);
            worst = Math.Max(worst, Math.Abs(expected - Float(line[1])));
        }

        Assert.True(fractional > 500, "the cases hold too few interpolated lookups");
        Assert.True(worst <= Math.ScaleB(1.0, -22), $"a lookup is {worst} from (index + weight) / (size - 1), more than 2^-22");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void ResultsTheFilmCannotShowFollowTheCyclesSource(string target)
    {
        // Cycles' film writes 0 for an infinite or NaN result, so the fixtures cannot tell the guards that return 0
        // from the division that would return an infinity. These are the guards in svm_math (util/math_base.h):
        // safe_logf, safe_divide and inversesqrtf return 0 where the plain operation is not finite.
        float One(string function, params float[] arguments) => (float)Call(target, function, arguments.Cast<object>().ToArray());
        Assert.Equal(0.0f, One("cycles_logarithm", 0.0f, 2.0f));
        Assert.Equal(0.0f, One("cycles_logarithm", -1.0f, 2.0f));
        Assert.Equal(0.0f, One("cycles_logarithm", 2.0f, 0.0f));
        Assert.Equal(0.0f, One("cycles_logarithm", 2.0f, -1.0f));
        Assert.Equal(0.0f, One("cycles_divide", 1.0f, 0.0f));
        Assert.Equal(0.0f, One("cycles_inverse_sqrt", 0.0f));
        Assert.Equal(0.0f, One("cycles_modulo", 1.0f, 0.0f));
        Assert.Equal(0.0f, One("cycles_floored_modulo", 1.0f, 0.0f));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void InverseTrigonometryFollowsTheCyclesGuards(string target)
    {
        // util/math_base.h: safe_asinf and safe_acosf clamp to [-1, 1] before asinf and acosf; compatible_atan2 returns 0
        // at the origin (x == 0 && y == 0) and is atan2f everywhere else, so its guard is only the origin.
        float One(string function, params float[] arguments) => (float)Call(target, function, arguments.Cast<object>().ToArray());
        Assert.Equal(MathF.Asin(1.0f), One("cycles_arcsine", 2.0f));
        Assert.Equal(MathF.Asin(-1.0f), One("cycles_arcsine", -1.5f));
        Assert.Equal(MathF.Asin(1.0f), One("cycles_arcsine", 1.0000001f));
        Assert.Equal(0.0f, One("cycles_arccosine", 2.0f));
        Assert.Equal(MathF.Acos(-1.0f), One("cycles_arccosine", -2.0f));
        Assert.Equal(0.0f, One("cycles_arctan2", 0.0f, 0.0f));
        Assert.Equal(0.0f, One("cycles_arctan2", 0.0f, -0.0f));
        Assert.Equal(0.0f, One("cycles_arctan2", -0.0f, -0.0f));
        Assert.Equal(MathF.Atan2(-0.0f, -1.0f), One("cycles_arctan2", -0.0f, -1.0f));
        Assert.Equal(MathF.Atan2(0.0f, -1.0f), One("cycles_arctan2", 0.0f, -1.0f));
        Assert.Equal(MathF.Atan2(1.0f, 0.0f), One("cycles_arctan2", 1.0f, 0.0f));
        Assert.Equal(MathF.Atan2(-1.0f, 0.0f), One("cycles_arctan2", -1.0f, 0.0f));
        // The guard is the origin alone: a guard on either zero would zero these (the plain atan2 values are pi and pi/2).
        Assert.NotEqual(0.0f, One("cycles_arctan2", 0.0f, -1.0f));
        Assert.NotEqual(0.0f, One("cycles_arctan2", 1.0f, 0.0f));
        foreach (var value in new[] { One("cycles_arcsine", 2.0f), One("cycles_arccosine", -2.0f), One("cycles_arctan2", 0.0f, -0.0f), One("cycles_arctan2", 1.0f, 0.0f) })
            Assert.True(float.IsFinite(value));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void MapRangeSmoothStepDecidesTheTopEdgeBeforeDividing(string target)
    {
        // util/math_base.h smoothstep returns 1 for x >= edge1 before it forms t. With an overflowing range
        // (high - low = infinity) the division would be infinity / infinity, NaN.
        float Smooth(params float[] arguments) => (float)Call(target, "cycles_map_range_smoothstep", arguments.Cast<object>().ToArray());
        Assert.Equal(1.0f, Smooth(3.0e38f, -3.0e38f, 3.0e38f, 0.0f, 1.0f));
        Assert.Equal(0.0f, Smooth(3.0e38f, 3.0e38f, -3.0e38f, 0.0f, 1.0f));
        Assert.Equal(0.0f, Smooth(-3.0e38f, -3.0e38f, 3.0e38f, 0.0f, 1.0f));
    }

    [Fact]
    public void FlooredModuloAndWrapUnderContractionStayWithinTheBound()
    {
        // Ruling cycles-kernel-reference-ruled: a build that contracts a - floor(a / b) b into one fused operation differs
        // from the reference by at most 2^-23 (|a| + |b|), and the wrap model by 2^-23 (|value| + |min| + |range|). The fused
        // model is MathF.FusedMultiplyAdd, test-only; no cycles_* function uses it.
        var bound = Math.ScaleB(1.0, -23);
        var worstModulo = 0.0;
        var worstWrap = 0.0;
        var seenPair = false;
        foreach (var entry in Load("math")["functions"]!.AsArray())
        {
            var name = (string)entry!["function"]!;
            if (name != "cycles_floored_modulo" && name != "cycles_wrap")
                continue;
            foreach (var line in entry["cases"]!.AsArray())
            {
                var o = ((string)line!).Split(" -> ")[0].Split(' ').Select(Float).ToArray();
                if (name == "cycles_floored_modulo")
                {
                    float a = o[0], b = o[1];
                    if (b == 0.0f)
                        continue;
                    var fused = MathF.FusedMultiplyAdd(-MathF.Floor(a / b), b, a);
                    var scale = Math.Abs((double)a) + Math.Abs((double)b);
                    var difference = Math.Abs((double)fused - math.cycles_floored_modulo(a, b));
                    Assert.True(difference <= bound * scale, $"floored modulo ({line}): fused {fused} is {difference / scale} of |a| + |b| from the reference");
                    worstModulo = Math.Max(worstModulo, difference / scale);
                    if (a == 10000.0f && b == 0.1f)
                    {
                        seenPair = true;
                        Assert.Equal(-1.4901161e-4f, fused);
                        Assert.Equal(0.0f, math.cycles_floored_modulo(a, b));
                    }
                }
                else
                {
                    float value = o[0], max = o[1], min = o[2];
                    var range = max - min;
                    if (range == 0.0f)
                        continue;
                    var fused = MathF.FusedMultiplyAdd(-range, MathF.Floor((value - min) / range), value);
                    var scale = Math.Abs((double)value) + Math.Abs((double)min) + Math.Abs((double)range);
                    var difference = Math.Abs((double)fused - math.cycles_wrap(value, max, min));
                    Assert.True(difference <= bound * scale, $"wrap ({line}): fused {fused} is {difference / scale} of |value| + |min| + |range| from the reference");
                    worstWrap = Math.Max(worstWrap, difference / scale);
                }
            }
        }

        Assert.True(seenPair, "the fixture holds no (10000, 0.1) floored modulo case");
        // The cases reach the contraction regime: a bound of 2^-25 would fail.
        Assert.True(worstModulo > Math.ScaleB(1.0, -25), $"the worst floored modulo contraction is only {worstModulo} of |a| + |b|");
        Assert.True(worstWrap > Math.ScaleB(1.0, -25), $"the worst wrap contraction is only {worstWrap} of |value| + |min| + |range|");
    }

    // The values oracle.py draws the specials of a function from (SPECIAL, plus the arcsine and arccosine edge rows); a row with any other operand holds a random draw.
    private static readonly HashSet<float> Alphabet = new[]
    {
        0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0, 3.0, -2.75, 0.1, -0.1, 7.5, -7.5, 1.0e-7, -1.0e-7, 100.0, -100.0, 1.0e4, 0.3, 0.7, 1.5, 1.0000001, -1.0000001,
    }.Select(v => (float)v).ToHashSet();

    [Fact]
    public void OperandStreamsDifferPerFunction()
    {
        // oracle.py seeds every function, every blend mode's second colours and every ramp from its own name, so no two
        // share a stream of draws. At e48f9488 the seed was the name's first four bytes, and 'cycles_' shared them all.
        var functions = Load("math")["functions"]!.AsArray()
            .Select(e => (Name: (string)e!["function"]!, Rows: e["cases"]!.AsArray().Select(l => ((string)l!).Split(" -> ")[0]).ToArray()))
            .ToArray();
        Assert.True(functions.Length >= 17, "the math fixture lost functions");
        foreach (var f in functions)
        {
            var draws = f.Rows.Where(row => row.Split(' ').Any(token => !Alphabet.Contains(Float(token)))).ToHashSet();
            Assert.True(draws.Count >= 8, $"{f.Name} holds only {draws.Count} random-draw rows");
            foreach (var g in functions.Where(g => string.CompareOrdinal(f.Name, g.Name) < 0 && g.Rows[0].Split(' ').Length == f.Rows[0].Split(' ').Length))
            {
                var shared = g.Rows.Count(row => draws.Contains(row));
                Assert.True(shared == 0, $"{f.Name} and {g.Name} share {shared} random-draw rows");
            }
        }

        var modes = Load("mix")["functions"]!.AsArray()
            .Where(e => ((string)e!["function"]!).StartsWith("cycles_mix_") && ((string)e["cases"]![0]!).Split(" -> ")[0].Split(' ').Length == 7)
            .Select(e => (Name: (string)e!["function"]!, Rows: e["cases"]!.AsArray().Select(l => ((string)l!).Split(" -> ")[0].Split(' ')).ToArray()))
            .ToArray();
        Assert.True(modes.Length >= 15, "the mix fixture lost blend modes");
        static string Multiset(IEnumerable<string[]> rows, int first) => string.Join("|", rows.Select(r => string.Join(" ", r.Skip(first).Take(3))).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var mode in modes)
            Assert.True(Multiset(mode.Rows, 1) != Multiset(mode.Rows, 4), $"{mode.Name}: the second colours are the first colours");
        foreach (var f in modes)
            foreach (var g in modes.Where(g => string.CompareOrdinal(f.Name, g.Name) < 0))
                Assert.True(Multiset(f.Rows, 4) != Multiset(g.Rows, 4), $"{f.Name} and {g.Name} draw the same second colours");

        var ramps = Load("ramp")["functions"]!.AsArray()
            .Select(e => e!["cases"]!.AsArray().Select(l => ((string)l!).Split(" -> ")[0]).ToArray()).ToArray();
        Assert.Equal(2, ramps.Length);
        var tail = ramps[0].TakeLast(100).ToHashSet();
        Assert.True(!ramps[1].TakeLast(100).Any(tail.Contains), "the ramps' random t tails overlap");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void HueSaturationAndValueOfThePrimariesFollowCycles(string target)
    {
        // util/color.h rgb_to_hsv: hue 0 for red, 1/3 for green, 2/3 for blue, saturation 1, value 1; a grey has
        // hue 0 and saturation 0. Red's hue is exactly 0 and not 1, which hsv_to_rgb would read the same.
        float[] Hsv(float r, float g, float b) => Flatten(Call(target, "cycles_rgb_to_hsv", new float3(r, g, b)));
        Assert.Equal(new[] { 0.0f, 1.0f, 1.0f }, Hsv(1.0f, 0.0f, 0.0f));
        Assert.Equal(new[] { 0.0f, 0.0f, 0.5f }, Hsv(0.5f, 0.5f, 0.5f));
        Assert.Equal(1.0f / 3.0f, Hsv(0.0f, 1.0f, 0.0f)[0], 6);
        Assert.Equal(2.0f / 3.0f, Hsv(0.0f, 0.0f, 1.0f)[0], 6);
        var back = Flatten(Call(target, "cycles_hsv_to_rgb", new float3(0.0f, 1.0f, 1.0f)));
        Assert.Equal(new[] { 1.0f, 0.0f, 0.0f }, back);
    }

    [Fact]
    public void RgbToBwCoefficientsAreTheOnesBlenderReads()
    {
        var entry = Load("rgb-to-bw")["functions"]!.AsArray().Single()!["cases"]!.AsArray().Select(line => ((string)line!).Split(" -> ")).ToArray();
        float Primary(int channel) => Float(entry.First(c =>
        {
            var operands = c[0].Split(' ').Select(Float).ToArray();
            return Enumerable.Range(0, 3).All(k => operands[k] == (k == channel ? 1.0f : 0.0f));
        })[1]);
        Assert.Equal(math.CYCLES_RGB_TO_Y_R, Primary(0));
        Assert.Equal(math.CYCLES_RGB_TO_Y_G, Primary(1));
        Assert.Equal(math.CYCLES_RGB_TO_Y_B, Primary(2));
    }
}
