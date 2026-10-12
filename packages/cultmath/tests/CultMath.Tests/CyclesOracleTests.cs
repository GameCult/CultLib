using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// math.cycles_* against the results of Blender 5.2.2's own Cycles on the CPU. The fixtures under
/// fixtures/cycles-oracle are written by tools/cycles-oracle/oracle.py (never by hand): each case is the
/// operands and the node's baked results as float32 bit patterns. Every function is compared at every case
/// within the tolerance its family states below; a function not named in <see cref="Tolerances"/> is held to
/// 0 ulp. Signed zeros are not distinguished (-0 equals 0), and NaN equals NaN. The fixtures come from Cycles'
/// SSE4.2 CPU kernel, which does not fuse multiply-adds as C# does not (oracle.py checks this before it bakes).
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
                    // Cycles' film writes 0 for an infinite or NaN result; the operands are finite, so such a case holds
                    // nothing to compare.
                    if (!float.IsFinite(have[k]) && want[k] == 0.0f)
                        continue;
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
    public void RampIndexNamesTheEntryBlenderReads()
    {
        var entry = Load("ramp")["functions"]!.AsArray().Single(e => (string)e!["node"]!.ToString() == "ShaderNodeValToRGB CONSTANT")!;
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
            var t = Float(line[0]);
            var lookup = math.cycles_ramp_index(t, size);
            var index = (int)lookup.x;
            Assert.InRange(index, 0, size - 1);
            Assert.True(Distance(Float(line[1]), table[index]) == 0, $"ramp t {line[0]}: index {index} reads {table[index]}, Blender {Float(line[1])}");
        }

        // Every stop's value appears in the results at its own boundary, so the cases do cross the entries.
        Assert.True(cases.Select(line => line[1]).Distinct().Count() >= 32, "the cases never cross the stops");
    }

    [Fact]
    public void RampWeightBlendsTheEntryWithTheNextOne()
    {
        // A linear ramp from 0 to 1: entry i holds i / 255 to within the float rounding of Blender's table, and a
        // lookup at (index, weight) reads (index + weight) / 255. A weight that is off by a fraction of a
        // percent, or an index that is off by one, moves the result by more than the bound.
        var entry = Load("ramp")["functions"]!.AsArray().Single(e => (string)e!["node"]!.ToString() == "ShaderNodeValToRGB LINEAR")!;
        var size = (int)entry["table_entries"]!;
        var worst = 0.0;
        var fractional = 0;
        foreach (var line in entry["cases"]!.AsArray().Select(line => ((string)line!).Split(" -> ")))
        {
            var lookup = math.cycles_ramp_index(Float(line[0]), size);
            Assert.InRange(lookup.y, 0.0f, 1.0f);
            fractional += lookup.y > 0.0f ? 1 : 0;
            var expected = (lookup.x + lookup.y) / (size - 1);
            worst = Math.Max(worst, Math.Abs(expected - Float(line[1])));
        }

        Assert.True(fractional > 500, "the cases hold too few interpolated lookups");
        Assert.True(worst <= Math.ScaleB(1.0, -22), $"a lookup is {worst} from (index + weight) / 255, more than 2^-22");
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
