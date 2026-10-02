using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CultMath;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// shaders/CultMath.glsl is derived: it is exactly <see cref="GlslLowering.Lower"/> of the committed HLSL,
/// no HLSL-only token survives the lowering, no public function is dropped except the texture samplers,
/// and the golden fixture a WebGL2 consumer evaluates is C#'s current output. With CULTMATH_WRITE_GLSL=1
/// set, the first and last tests write the committed files before comparing, which is the one
/// regeneration path.
/// </summary>
public sealed class GlslMirrorTests
{
    private static bool Write => Environment.GetEnvironmentVariable("CULTMATH_WRITE_GLSL") == "1";

    private static string Root => HlslSourceCompatibilityTests.FindCultMathRoot();

    private static string GlslPath => Path.Combine(Root, "shaders", "CultMath.glsl");

    private static string FixturePath => Path.Combine(Root, "tests", "CultMath.Tests", "fixtures", "glsl-parity.json");

    private static string Hlsl => HlslSourceCompatibilityTests.ReadShaderSource(Root).ReplaceLineEndings("\n");

    private static string Lowered => GlslLowering.Lower(HlslSourceCompatibilityTests.ReadShaderSource(Root));

    [Fact]
    public void CommittedGlslEqualsLowering()
    {
        var expected = Lowered;
        if (Write)
            File.WriteAllText(GlslPath, expected);
        var committed = File.ReadAllText(GlslPath).ReplaceLineEndings("\n");
        Assert.True(committed == expected, "shaders/CultMath.glsl is not the lowering of the committed HLSL (regenerate with CULTMATH_WRITE_GLSL=1):\n" + UnifiedDiff(committed, expected));
    }

    // Identifier boundaries: cultmath_lerp is a CultMath function, lerp( is HLSL.
    private static readonly Regex HlslOnly = new(
        @"\b(float|u?int|bool)[234]\b|\blerp\(|\bfrac\(|\bsaturate\(|\brsqrt\(|\basuint\(|\basfloat\(|static const|\((float|int|uint)\)|Texture2D|SamplerState|#include|precision (high|medium|low)p");

    [Fact]
    public void NoHlslOnlyTokensSurvive()
    {
        var survivors = HlslOnly.Matches(Lowered).Select(m => m.Value).Distinct().ToArray();
        Assert.True(survivors.Length == 0, "HLSL-only tokens in the lowering: " + string.Join(", ", survivors));
    }

    private static readonly Regex Definition = new(@"(?m)^\w+\s+(cultmath_\w+)\s*\(([^)]*)\)");

    [Fact]
    public void EveryPublicFunctionIsLowered()
    {
        var hlsl = Definition.Matches(Hlsl).Where(m => !Regex.IsMatch(m.Groups[2].Value, @"\b(Texture2D|SamplerState)\b"))
            .Select(m => m.Groups[1].Value).ToHashSet();
        var glsl = Definition.Matches(Lowered).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Contains("cultmath_snoise", glsl);
        Assert.True(hlsl.SetEquals(glsl), "HLSL only: " + string.Join(", ", hlsl.Except(glsl)) + "; GLSL only: " + string.Join(", ", glsl.Except(hlsl)));
    }

    [Fact]
    public void GoldenFixtureMatchesCSharp()
    {
        var expected = GoldenFixture();
        if (Write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
            File.WriteAllText(FixturePath, expected);
        }

        var committed = File.ReadAllText(FixturePath).ReplaceLineEndings("\n");
        Assert.True(committed == expected, "fixtures/glsl-parity.json is not C#'s current output (regenerate with CULTMATH_WRITE_GLSL=1):\n" + UnifiedDiff(committed, expected));
    }

    // ---- The golden fixture ----

    private const int Seed = 20261002;
    private const int Points = 256;

    private sealed record Family(string Glsl, string Tolerance, Func<System.Random, object[]> Arguments);

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();
    private static float3 Point3(System.Random r) => new(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f));
    private static int Bits(System.Random r) => r.Next(int.MinValue, int.MaxValue);

    private static float2 Interval(System.Random r)
    {
        var centre = Uniform(r, -20.0f, 20.0f);
        var width = MathF.Pow(10.0f, Uniform(r, -4.0f, 1.0f));
        return new float2(centre - width * 0.5f, centre + width * 0.5f);
    }

    // Every family the site's WebGL2 readback evaluates. pcg3d and pcg4d are integer-only and must match
    // exactly; the float families are ulp-bounded, with the bound measured on the device by the consumer.
    // iv_exp is left out: it calls the platform's exp, so its C# bits are not the same on every OS.
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
    };

    private static string GoldenFixture()
    {
        var random = new System.Random(Seed);
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
            json.Append("      \"cases\": [\n");
            for (var p = 0; p < Points; p++)
            {
                var arguments = family.Arguments(random);
                var result = method.Invoke(null, arguments)!;
                json.Append("        \"").Append(Hex(arguments)).Append(" -> ").Append(Hex(new[] { result })).Append('"');
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
