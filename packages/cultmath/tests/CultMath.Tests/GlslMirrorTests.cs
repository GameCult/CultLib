using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CultMath;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// shaders/CultMath.glsl and shaders/CultMath.Phacelle.glsl are derived: each is exactly its
/// <see cref="GlslLowering.Lower"/> output from the committed HLSL, no HLSL-only token survives the
/// lowering, no public function is dropped except the texture samplers, the MPL-2.0 Phacelle code lives
/// only in its own file, and the golden fixture a WebGL2 consumer evaluates is C#'s current output. With
/// CULTMATH_WRITE_GLSL=1 set, the first and last tests write the committed files before comparing, which
/// is the one regeneration path.
/// </summary>
public sealed class GlslMirrorTests
{
    private readonly ITestOutputHelper output;

    public GlslMirrorTests(ITestOutputHelper output) => this.output = output;

    private static bool Write => Environment.GetEnvironmentVariable("CULTMATH_WRITE_GLSL") == "1";

    private static string Root => HlslSourceCompatibilityTests.FindCultMathRoot();

    private static string FixturePath => Path.Combine(Root, "tests", "CultMath.Tests", "fixtures", "glsl-parity.json");

    private static string Hlsl => HlslSourceCompatibilityTests.ReadShaderSource(Root).ReplaceLineEndings("\n");

    private static IReadOnlyList<(string File, string Text)> Lowered => GlslLowering.Lower(Root);

    [Fact]
    public void CommittedGlslEqualsLowering()
    {
        foreach (var (file, expected) in Lowered)
        {
            var path = Path.Combine(Root, "shaders", file);
            if (Write)
                File.WriteAllText(path, expected);
            var committed = File.ReadAllText(path).ReplaceLineEndings("\n");
            Assert.True(committed == expected, $"shaders/{file} is not the lowering of the committed HLSL (regenerate with CULTMATH_WRITE_GLSL=1):\n" + UnifiedDiff(committed, expected));
        }
    }

    // Identifier boundaries: cultmath_lerp is a CultMath function, lerp( is HLSL.
    private static readonly Regex HlslOnly = new(
        @"\b(float|u?int|bool)[234]\b|\blerp\(|\bfrac\(|\bsaturate\(|\brsqrt\(|\basuint\(|\basfloat\(|static const|\((float|int|uint)\)|Texture2D|SamplerState|#include|precision (high|medium|low)p");

    [Fact]
    public void NoHlslOnlyTokensSurvive()
    {
        foreach (var (file, text) in Lowered)
        {
            var survivors = HlslOnly.Matches(text).Select(m => m.Value).Distinct().ToArray();
            Assert.True(survivors.Length == 0, $"HLSL-only tokens in {file}: " + string.Join(", ", survivors));
        }
    }

    private static readonly Regex Definition = new(@"(?m)^\w+\s+(cultmath_\w+)\s*\(([^)]*)\)");

    // A definition's signature, its name and parameter types, so an overload counts on its own:
    // "cultmath_pcg3d(vec2)". HLSL type names are mapped by the lowering's own step 3.
    private static string Signature(Match definition) =>
        definition.Groups[1].Value + "(" + string.Join(", ", definition.Groups[2].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => GlslLowering.LowerTypeNames(string.Join(' ', Regex.Split(parameter, @"\s+")[..^1])))) + ")";

    [Fact]
    public void EveryPublicFunctionIsLowered()
    {
        var lowered = Lowered;
        var hlsl = Definition.Matches(Hlsl).Where(m => !Regex.IsMatch(m.Groups[2].Value, @"\b(Texture2D|SamplerState)\b"))
            .Select(Signature).ToHashSet();
        var glsl = lowered.SelectMany(output => Definition.Matches(output.Text)).Select(Signature).ToHashSet();
        Assert.Contains("cultmath_pcg3d(vec2)", glsl);
        Assert.True(hlsl.SetEquals(glsl), "HLSL only: " + string.Join(", ", hlsl.Except(glsl)) + "; GLSL only: " + string.Join(", ", glsl.Except(hlsl)));

        // The MPL-2.0 code is in its own file and nowhere in the MIT library.
        var library = lowered.Single(output => output.File == "CultMath.glsl").Text;
        var phacelle = lowered.Single(output => output.File == "CultMath.Phacelle.glsl").Text;
        foreach (var token in new[] { "cultmath_phacelle(", "CultPhasor" })
        {
            Assert.DoesNotContain(token, library);
            Assert.Contains(token, phacelle);
        }
    }

    // The two-file contract a consumer relies on when it concatenates CultMath.Phacelle.glsl after
    // CultMath.glsl: distinct guards (a shared one would hide the second file's body), each provenance
    // line naming its file's licence, and the MIT file pointing at the one that holds cultmath_phacelle.
    [Fact]
    public void TwoFileHeaderContract()
    {
        var lowered = Lowered;
        var guards = lowered.Select(output => Regex.Match(output.Text, @"^[^\n]*\n#ifndef (\w+)\n#define \1\n").Groups[1].Value).ToArray();
        Assert.All(guards, guard => Assert.NotEqual(string.Empty, guard));
        Assert.Equal(guards.Length, guards.Distinct().Count());

        var library = lowered.Single(output => output.File == "CultMath.glsl").Text.Split('\n')[0];
        var phacelle = lowered.Single(output => output.File == "CultMath.Phacelle.glsl").Text.Split('\n')[0];
        Assert.Contains(" MIT.", library);
        Assert.DoesNotContain("MPL", library);
        Assert.Contains("cultmath_phacelle is in CultMath.Phacelle.glsl", library);
        Assert.Contains(" MPL-2.0;", phacelle);
        Assert.DoesNotContain("MIT", phacelle);
    }

    [Fact]
    public void GoldenFixtureMatchesCSharp()
    {
        // Arguments are drawn only here, when regenerating: Interval draws widths through MathF.Pow, whose
        // bits differ by OS. Checking evaluates C# on the committed fixture's own arguments.
        if (Write)
        {
            var random = new System.Random(Seed);
            Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
            File.WriteAllText(FixturePath, GoldenFixture((f, _) => Families[f].Arguments(random)));
        }

        var committed = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        var committedFamilies = committed["functions"]!.AsArray();
        Assert.Equal(Families.Length, committedFamilies.Count);
        for (var f = 0; f < Families.Length; f++)
            Assert.True(committedFamilies[f]!["cases"]!.AsArray().Count == Points, $"{Families[f].Glsl}: the fixture does not hold {Points} cases");
        var current = JsonNode.Parse(GoldenFixture((f, p) =>
            Arguments(Resolve(Families[f].Glsl), ((string)committedFamilies[f]!["cases"]![p]!).Split(" -> ")[0])))!.AsObject();
        var currentFamilies = current["functions"]!.AsArray();

        // Family by family: every family is compared as text, except those whose C# bits come from the
        // platform's libm, which are compared case by case within their family's platform bound. A family
        // with a check must also pass it on C#'s own results, so the rule the WebGL2 consumer applies holds
        // for the reference.
        for (var f = 0; f < Families.Length; f++)
        {
            var want = currentFamilies[f]!.AsObject();
            var have = committedFamilies[f]!.AsObject();
            var name = (string)want["glsl"]!;
            Assert.Equal(name, (string?)have["glsl"]);
            Assert.Equal((string)want["tolerance"]!, (string?)have["tolerance"]);
            Assert.Equal((string?)want["check"], (string?)have["check"]);
            switch (Families[f].Platform)
            {
                case Platform.Ulp:
                    var ulps = MaxDistance(have["cases"]!.AsArray(), want["cases"]!.AsArray(), (x, y) => Math.Abs((long)Ordered(x) - Ordered(y)));
                    output.WriteLine($"{name}: within {ulps} ulp of this platform's libm ({RuntimeInformation.OSDescription}); fixture generated on {have["platform"]}");
                    Assert.True(ulps <= 1, $"{name}: {ulps} ulp from this platform's result, more than 1");
                    break;
                case Platform.Scaled:
                    var scaled = MaxDistance(have["cases"]!.AsArray(), want["cases"]!.AsArray(), (x, y) =>
                    {
                        double v = BitConverter.Int32BitsToSingle(x), w = BitConverter.Int32BitsToSingle(y);
                        return x == y ? 0.0 : Math.Abs(v - w) / Math.Max(Math.Abs(v), 1.0);
                    });
                    output.WriteLine($"{name}: |diff| / max(|v|, 1) at most {scaled:R} from this platform's libm ({RuntimeInformation.OSDescription}); fixture generated on {have["platform"]}");
                    Assert.True(scaled <= ScaledPlatformBound, $"{name}: {scaled:R} max(|v|, 1) from this platform's result, more than 2^-20");
                    break;
                default:
                    Assert.True(JsonNode.DeepEquals(want, have), $"fixtures/glsl-parity.json family {name} is not C#'s current output (regenerate with CULTMATH_WRITE_GLSL=1)");
                    break;
            }

            if (want["check"] is not null)
                AssertFrustumBall(want["cases"]!.AsArray());
        }

        committed.Remove("functions");
        current.Remove("functions");
        Assert.True(JsonNode.DeepEquals(current, committed), "fixtures/glsl-parity.json header is not the generator's (regenerate with CULTMATH_WRITE_GLSL=1)");
    }

    // The largest distance, component by component, between two families' results (committed first).
    private static double MaxDistance(JsonArray committed, JsonArray current, Func<int, int, double> distance)
    {
        var max = 0.0;
        for (var c = 0; c < current.Count; c++)
        {
            var haveBits = ((string)committed[c]!).Split(" -> ")[1].Split(' ').Select(h => (int)Convert.ToUInt32(h, 16)).ToArray();
            var wantBits = ((string)current[c]!).Split(" -> ")[1].Split(' ').Select(h => (int)Convert.ToUInt32(h, 16)).ToArray();
            Assert.Equal(wantBits.Length, haveBits.Length);
            max = Math.Max(max, haveBits.Zip(wantBits, distance).Max());
        }

        return max;
    }

    // FrustumBall applied to C#'s own results: every case's ball encloses the exact one and is no wider
    // than its rounding widening allows.
    private static void AssertFrustumBall(JsonArray cases)
    {
        foreach (var entry in cases)
        {
            var sides = ((string)entry!).Split(" -> ");
            var a = Floats(sides[0]);
            var b = Floats(sides[1]);
            double mx = a[0], my = a[1], z0 = a[2], z1 = a[3], footprint = a[4], warp = a[5];
            var zm = (z0 + z1) / 2.0;
            var radius = (z1 - z0) / 2.0 * Math.Sqrt(mx * mx + my * my + 1.0) + z1 * footprint + warp;
            double dx = b[0] - mx * zm, dy = b[1] - my * zm, dz = b[2] - zm;
            var reach = radius + Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var c1 = Math.Abs(mx * zm) + Math.Abs(my * zm) + Math.Abs(zm);
            Assert.True(b[3] >= reach, $"iv_frustum_ball case {entry}: radius short of the exact ball by {reach - b[3]}");
            Assert.True(b[3] <= reach + Math.ScaleB(radius + c1, -19), $"iv_frustum_ball case {entry}: radius past the exact ball by {b[3] - reach}, more than 2^-19 (r + |c|_1)");
        }
    }

    // A committed case's arguments, decoded into the C# method's parameter types in Hex's field order.
    private static object[] Arguments(MethodInfo method, string hex)
    {
        var scalars = new Queue<uint>(hex.Split(' ').Select(h => Convert.ToUInt32(h, 16)));
        var arguments = method.GetParameters().Select(parameter => FromScalars(parameter.ParameterType, scalars)).ToArray();
        Assert.Empty(scalars);
        return arguments;
    }

    private static object FromScalars(Type type, Queue<uint> scalars)
    {
        if (type == typeof(float))
            return BitConverter.Int32BitsToSingle((int)scalars.Dequeue());
        if (type == typeof(int))
            return (int)scalars.Dequeue();
        if (type == typeof(uint))
            return scalars.Dequeue();
        var value = Activator.CreateInstance(type)!;
        foreach (var field in type.GetFields().Where(f => !f.IsStatic))
            field.SetValue(value, FromScalars(field.FieldType, scalars));
        return value;
    }

    private static double[] Floats(string hex) =>
        hex.Split(' ').Select(h => (double)BitConverter.Int32BitsToSingle((int)Convert.ToUInt32(h, 16))).ToArray();

    // A float32 bit pattern as an integer whose order and differences are the floats' order and ulps.
    private static int Ordered(int bits) => bits < 0 ? int.MinValue - bits : bits;

    // ---- The golden fixture ----

    private const int Seed = 20261002;
    private const int Points = 256;

    // How GoldenFixtureMatchesCSharp compares a family whose C# bits come from the platform's libm (exp,
    // sin, cos) and so differ by OS; such an entry records the platform that generated it. Every other
    // family is compared as text.
    private enum Platform { None, Ulp, Scaled }

    // Platform.Scaled: |diff| <= 2^-20 max(|v|, 1) per component. Ulps mean nothing near zero, where
    // phacelle's sums of exp, sin and cos cancel; Windows differs from Linux by up to 3.6e-7 of it.
    private const double ScaledPlatformBound = 9.5367431640625e-7;

    private sealed record Family(string Glsl, string Tolerance, Func<System.Random, object[]> Arguments, string? Check = null, Platform Platform = Platform.None);

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();
    private static float3 Point3(System.Random r) => new(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f));
    private static int Bits(System.Random r) => r.Next(int.MinValue, int.MaxValue);

    private static float2 Interval(System.Random r)
    {
        var centre = Uniform(r, -20.0f, 20.0f);
        var width = MathF.Pow(10.0f, Uniform(r, -4.0f, 1.0f));
        return new float2(centre - width * 0.5f, centre + width * 0.5f);
    }

    // iv_frustum_ball's sqrt is not correctly rounded on WebGL2 and its compilers may reassociate, so its
    // bits are not portable; what the march needs is enclosure, and what culling needs is a ball no wider
    // than its rounding widening. The consumer checks both in double from each case's arguments. It is
    // written into the family's fixture entry, where the consumer reads it.
    private const string FrustumBall =
        "enclosure and tightness: from the arguments (mx, my, z0, z1, fp, warp) compute in double zm = (z0 + z1) / 2, " +
        "c = (mx zm, my zm, zm), |c|_1 = |mx zm| + |my zm| + |zm|, r = (z1 - z0) / 2 sqrt(mx^2 + my^2 + 1) + z1 fp + warp " +
        "and d = |GPU centre - c| (Euclidean); every case must have r + d <= GPU radius <= r + d + 2^-19 (r + |c|_1). " +
        "Report the largest ulp distance per component as for any ulp-bounded family; only the two bounds fail the check.";

    // A Phacelle stripe wave vector: each component in [-6, 6], about one stripe per cell.
    private static float3 Side(System.Random r) => new(Uniform(r, -6.0f, 6.0f), Uniform(r, -6.0f, 6.0f), Uniform(r, -6.0f, 6.0f));

    // Every family the site's WebGL2 readback evaluates, in a fixed order. A new family is appended, so the
    // seeded stream and every earlier family's cases stay as they were. pcg3d and pcg4d are "exact"; the
    // float families are ulp-bounded, with the bound measured on the device by the consumer, and
    // iv_frustum_ball also carries an enclosure check. phacelle is in CultMath.Phacelle.glsl, so its
    // evaluator concatenates that file after CultMath.glsl; the site, which does not vendor it, skips it.
    private static readonly Family[] Families =
    {
        new("float cultmath_snoise(vec3)", "ulp-bounded", r => new object[] { Point3(r) }),
        new("float cultmath_snoise(vec2)", "ulp-bounded", r => new object[] { new float2(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f)) }),
        new("vec4 cultmath_snoise_grad(vec3)", "ulp-bounded", r => new object[] { Point3(r) }),
        new("vec4 cultmath_fbm_grad(vec3, int, float, float)", "ulp-bounded", r => new object[] { Point3(r), 4, 2.0f, 0.5f }),
        new("vec4 cultmath_ridged_grad(vec3, int, float, float)", "ulp-bounded", r => new object[] { Point3(r), 4, 2.0f, 0.5f }),
        new("CultCellular cultmath_cellular(vec3)", "ulp-bounded", r => new object[] { Point3(r) }),
        new("ivec3 cultmath_pcg3d(ivec3)", "exact", r => new object[] { new int3(Bits(r), Bits(r), Bits(r)) }),
        new("ivec4 cultmath_pcg4d(ivec4)", "exact", r => new object[] { new int4(Bits(r), Bits(r), Bits(r), Bits(r)) }),
        new("vec2 cultmath_iv_point(float)", "ulp-bounded", r => new object[] { Uniform(r, -20.0f, 20.0f) }),
        new("vec2 cultmath_iv_add(vec2, vec2)", "ulp-bounded", r => new object[] { Interval(r), Interval(r) }),
        new("vec2 cultmath_iv_sub(vec2, vec2)", "ulp-bounded", r => new object[] { Interval(r), Interval(r) }),
        new("vec2 cultmath_iv_neg(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_iv_mul(vec2, vec2)", "ulp-bounded", r => new object[] { Interval(r), Interval(r) }),
        new("vec2 cultmath_iv_scale(vec2, float)", "ulp-bounded", r => new object[] { Interval(r), Uniform(r, -4.0f, 4.0f) }),
        new("vec2 cultmath_iv_abs(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_iv_min(vec2, vec2)", "ulp-bounded", r => new object[] { Interval(r), Interval(r) }),
        new("vec2 cultmath_iv_max(vec2, vec2)", "ulp-bounded", r => new object[] { Interval(r), Interval(r) }),
        new("vec2 cultmath_iv_sqr(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_iv_sqrt(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_iv_clamp(vec2, float, float)", "ulp-bounded", r => new object[] { Interval(r), Uniform(r, -25.0f, 0.0f), Uniform(r, 0.0f, 25.0f) }),
        new("vec2 cultmath_iv_saturate(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_iv_smoothstep(float, float, vec2)", "ulp-bounded", r => new object[] { Uniform(r, -25.0f, 0.0f), Uniform(r, 0.0f, 25.0f), Interval(r) }),
        new("vec2 cultmath_iv_lerp(vec2, vec2, float)", "ulp-bounded", r => new object[] { Interval(r), Interval(r), Uniform(r, 0.0f, 1.0f) }),
        new("vec2 cultmath_iv_snoise_ball(vec3, float)", "ulp-bounded", r => new object[] { Point3(r), Uniform(r, 0.001f, 2.0f) }),
        new("vec2 cultmath_iv_fbm_ball(vec3, float, int, float, float)", "ulp-bounded", r => new object[] { Point3(r), Uniform(r, 0.001f, 2.0f), 4, 2.0f, 0.5f }),
        new("vec4 cultmath_iv_frustum_ball(vec2, float, float, float, float)", "ulp-bounded", r =>
        {
            var slope = new float2(Uniform(r, -1.0f, 1.0f), Uniform(r, -1.0f, 1.0f));
            var z0 = Uniform(r, 0.1f, 100.0f);
            return new object[] { slope, z0, z0 + Uniform(r, 0.01f, 50.0f), Uniform(r, 0.0001f, 0.05f), Uniform(r, 0.0f, 2.0f) };
        }, FrustumBall),
        new("vec2 cultmath_iv_exp(vec2)", "ulp-bounded, platform exp", r => new object[] { Interval(r) }, Platform: Platform.Ulp),
        new("CultPhasor cultmath_phacelle(vec3, vec3, float, float)", "ulp-bounded, platform exp, sin, cos; across platforms |diff| <= 2^-20 max(|v|, 1)",
            r => new object[] { Point3(r), Side(r), Uniform(r, 0.0f, 1.0f), 0.5f }, Platform: Platform.Scaled),
    };

    // The fixture text, each case's arguments given by family and point index.
    private static string GoldenFixture(Func<int, int, object[]> arguments)
    {
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"generator\": \"GlslMirrorTests.GoldenFixtureMatchesCSharp (packages/cultmath/tests/CultMath.Tests); regenerate with CULTMATH_WRITE_GLSL=1\",\n");
        json.Append($"  \"seed\": {Seed},\n");
        json.Append($"  \"points\": {Points},\n");
        json.Append("  \"encoding\": \"each case is 'arguments -> result', every scalar a float32 or 32-bit integer bit pattern in hex, vectors and structs flattened in field order\",\n");
        json.Append("  \"functions\": [\n");
        for (var f = 0; f < Families.Length; f++)
        {
            var family = Families[f];
            var method = Resolve(family.Glsl);
            json.Append("    {\n");
            json.Append($"      \"glsl\": \"{family.Glsl}\",\n");
            json.Append($"      \"tolerance\": \"{family.Tolerance}\",\n");
            if (family.Check is not null)
                json.Append($"      \"check\": \"{family.Check}\",\n");
            if (family.Platform != Platform.None)
                json.Append($"      \"platform\": \"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}\",\n");
            json.Append("      \"cases\": [\n");
            for (var p = 0; p < Points; p++)
            {
                var values = arguments(f, p);
                var result = method.Invoke(null, values)!;
                json.Append("        \"").Append(Hex(values)).Append(" -> ").Append(Hex(new[] { result })).Append('"');
                json.Append(p + 1 < Points ? ",\n" : "\n");
            }

            json.Append("      ]\n");
            json.Append(f + 1 < Families.Length ? "    },\n" : "    }\n");
        }

        json.Append("  ]\n}\n");
        return json.ToString();
    }

    private static readonly Dictionary<string, Type> GlslTypes = new()
    {
        ["float"] = typeof(float), ["int"] = typeof(int), ["vec2"] = typeof(float2), ["vec3"] = typeof(float3),
        ["vec4"] = typeof(float4), ["ivec3"] = typeof(int3), ["ivec4"] = typeof(int4),
    };

    // The C# math method a GLSL signature names: cultmath_ dropped, parameter types mapped.
    private static MethodInfo Resolve(string glsl)
    {
        var match = Regex.Match(glsl, @"^\w+ cultmath_(\w+)\(([^)]*)\)$");
        var types = match.Groups[2].Value.Split(", ").Select(t => GlslTypes[t]).ToArray();
        return typeof(math).GetMethod(match.Groups[1].Value, BindingFlags.Public | BindingFlags.Static, null, types, null)
            ?? throw new InvalidOperationException("no C# math counterpart for " + glsl);
    }

    private static string Hex(IEnumerable<object> values) => string.Join(" ", values.SelectMany(Scalars).Select(v => v switch
    {
        float f => BitConverter.SingleToInt32Bits(f).ToString("x8"),
        int i => i.ToString("x8"),
        uint u => u.ToString("x8"),
        _ => throw new InvalidOperationException(v.GetType().Name),
    }));

    private static IEnumerable<object> Scalars(object value) => value is float or int or uint
        ? new[] { value }
        : value.GetType().GetFields().Where(f => !f.IsStatic).SelectMany(f => Scalars(f.GetValue(value)!));

    // ---- A small unified diff for the failure message ----

    private static string UnifiedDiff(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        if ((long)a.Length * b.Length > 4_000_000)
        {
            var first = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], Math.Min(a.Length, b.Length));
            return $"@@ first difference at line {first + 1} @@\n-{(first < a.Length ? a[first] : "")}\n+{(first < b.Length ? b[first] : "")}";
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var lines = new List<string> { "--- committed", "+++ lowering" };
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; continue; }
            lines.Add($"@@ -{x + 1} +{y + 1} @@");
            while (x < a.Length && (y >= b.Length || lcs[x + 1, y] >= lcs[x, y + 1]) && !(y < b.Length && a[x] == b[y]))
                lines.Add("-" + a[x++]);
            while (y < b.Length && (x >= a.Length || a[x] != b[y]))
                lines.Add("+" + b[y++]);
            if (lines.Count > 200)
                break;
        }

        return string.Join("\n", lines.Take(200));
    }
}
