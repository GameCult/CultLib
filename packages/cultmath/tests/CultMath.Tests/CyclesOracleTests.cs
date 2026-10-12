using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// math.cycles_* against the results of Blender 5.2.2's own Cycles on the CPU. The fixtures under
/// fixtures/cycles-oracle are written by tools/cycles-oracle/oracle.py (never by hand): each case is the
/// operands and the node's baked results as float32 bit patterns. Every function is compared at every case
/// within the tolerance its family states below; a function not named in <see cref="Tolerances"/> is held to
/// 0 ulp. Signed zeros are not distinguished (-0 equals 0), and NaN equals NaN.
/// </summary>
public sealed class CyclesOracleTests
{
    // The ulp distance each function is allowed from Cycles, with the measured worst case that earned it.
    // Anything absent is 0 ulp.
    private static readonly Dictionary<string, (int Ulp, string Measured)> Tolerances = new();

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

        Assert.Equal(tokens.Length, next);
        return values.ToArray();
    }

    private static float[] Flatten(object result) => result switch
    {
        float f => new[] { f },
        float3 v => new[] { v.x, v.y, v.z },
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static MethodInfo Method(string name) =>
        typeof(math).GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("no CultMath function " + name);

    [Theory]
    [InlineData("math")]
    [InlineData("map-range")]
    [InlineData("clamp")]
    [InlineData("mix")]
    [InlineData("rgb-to-bw")]
    public void FamilyMatchesBlender(string family)
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
            var allowed = Tolerances.TryGetValue(name + (clamped ? "+clamp" : string.Empty), out var tolerance) ? tolerance.Ulp : 0;
            var worst = 0L;
            string? worstCase = null;
            var cases = entry["cases"]!.AsArray();
            Assert.True(cases.Count >= 1000, $"{name}: only {cases.Count} fixture cases");
            foreach (var line in cases)
            {
                var sides = ((string)line!).Split(" -> ");
                var operands = sides[0].Split(' ');
                var have = Flatten(method.Invoke(null, Arguments(method, operands))!);
                if (clamped)
                {
                    // A Map Range node's Clamp option is a Clamp node of type Range on its result.
                    var toMin = Float(operands[3]);
                    var toMax = Float(operands[4]);
                    have = new[] { math.cycles_clamp_range(have[0], toMin, toMax) };
                }

                var want = sides[1].Split(' ').Select(Float).ToArray();
                Assert.Equal(want.Length, have.Length);
                for (var k = 0; k < want.Length; k++)
                {
                    var distance = Distance(want[k], have[k]);
                    if (distance > worst)
                    {
                        worst = distance;
                        worstCase = $"{line} (C# {BitConverter.SingleToInt32Bits(have[k]):x8})";
                    }
                }
            }

            if (worst > allowed)
                failures.Add($"{name}{(clamped ? " with Clamp" : string.Empty)}: {worst} ulp from Blender, allowed {allowed}; worst case {worstCase}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RampIndexAndWeightReproduceBlendersLookup()
    {
        var fixture = Load("ramp");
        var entry = fixture["functions"]!.AsArray().Single()!;
        var size = (int)entry["table_entries"]!;
        var cases = entry["cases"]!.AsArray().Select(line => ((string)line!).Split(" -> ")).ToArray();

        // The first `size` cases are the table: each t reads its own entry (weight 0), so the result is the entry.
        var table = new float[size];
        for (var k = 0; k < size; k++)
        {
            var t = Float(cases[k][0]);
            var lookup = math.cycles_ramp_index(t, size);
            Assert.Equal((float)k, lookup.x);
            Assert.Equal(0.0f, lookup.y);
            table[k] = Float(cases[k][1]);
        }

        var interpolated = 0;
        for (var c = size; c < cases.Length; c++)
        {
            var t = Float(cases[c][0]);
            var lookup = math.cycles_ramp_index(t, size);
            var index = (int)lookup.x;
            Assert.InRange(index, 0, size - 1);
            var have = lookup.y > 0.0f ? (1.0f - lookup.y) * table[index] + lookup.y * table[index + 1] : table[index];
            interpolated += lookup.y > 0.0f ? 1 : 0;
            var want = Float(cases[c][1]);
            Assert.True(Distance(want, have) == 0, $"ramp t {cases[c][0]}: index {index}, weight {lookup.y}, got {have}, Blender {want}");
        }

        Assert.True(interpolated > 100, "the fixture holds too few interpolated lookups");
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
