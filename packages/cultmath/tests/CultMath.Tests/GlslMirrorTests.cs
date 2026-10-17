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
public sealed partial class GlslMirrorTests
{
    private readonly ITestOutputHelper output;

    public GlslMirrorTests(ITestOutputHelper output) => this.output = output;

    // The fixture is written by Yggdrasil's mcr.microsoft.com/dotnet/sdk:10.0 image (Ubuntu 24.04) only: its platform families record
    // that platform's libm, so a write from another OS would move them.
    private static bool Write => Environment.GetEnvironmentVariable("CULTMATH_WRITE_GLSL") == "1"
        ? OperatingSystem.IsLinux() ? true : throw new InvalidOperationException("CULTMATH_WRITE_GLSL=1 writes glsl-parity.json, whose platform families belong to Yggdrasil's Ubuntu image (mcr.microsoft.com/dotnet/sdk:10.0): regenerate it there.")
        : false;

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

    // Counting stand-ins for the mirror's clamp, one per overload, declared in the mirror's class so they
    // hide math.clamp for every function in it. Each counts a call whose minimum exceeds its maximum in any
    // component and forwards to math.clamp. (The int vector overloads only forward: no mirror function
    // clamps integer vectors with swapped bounds, and counting them would be unreachable.)
    private const string CountingClampShadows = @"
public static int ClampMinAboveMax;
static float clamp(float x, float a, float b) { if (a > b) ClampMinAboveMax++; return math.clamp(x, a, b); }
static float2 clamp(float2 x, float2 a, float2 b) { if (a.x > b.x || a.y > b.y) ClampMinAboveMax++; return math.clamp(x, a, b); }
static float3 clamp(float3 x, float3 a, float3 b) { if (a.x > b.x || a.y > b.y || a.z > b.z) ClampMinAboveMax++; return math.clamp(x, a, b); }
static float4 clamp(float4 x, float4 a, float4 b) { if (a.x > b.x || a.y > b.y || a.z > b.z || a.w > b.w) ClampMinAboveMax++; return math.clamp(x, a, b); }
static int clamp(int x, int a, int b) { if (a > b) ClampMinAboveMax++; return math.clamp(x, a, b); }
static int2 clamp(int2 x, int2 a, int2 b) { if (a.x > b.x || a.y > b.y) ClampMinAboveMax++; return math.clamp(x, a, b); }
static int3 clamp(int3 x, int3 a, int3 b) { if (a.x > b.x || a.y > b.y || a.z > b.z) ClampMinAboveMax++; return math.clamp(x, a, b); }
static int4 clamp(int4 x, int4 a, int4 b) { if (a.x > b.x || a.y > b.y || a.z > b.z || a.w > b.w) ClampMinAboveMax++; return math.clamp(x, a, b); }
";

    /// <summary>
    /// The lowering keeps HLSL's statement order (CommittedGlslEqualsLowering pins the GLSL as that
    /// lowering), and GLSL leaves clamp(x, minVal, maxVal) undefined for minVal above maxVal, so no clamp
    /// reached by cultmath_integral_clamp_linear may see lo above hi, on any path and for any spelling of the
    /// normalisation. The mirror is compiled with counting clamps and the function is run over the edge grid
    /// (lo above hi, infinite bounds, zero-length spans, b = 0 and b != 0): the count of such clamp calls
    /// stays 0 after every call. The cases are HlslSourceCompatibilityTests.IntegralClampLinearCases: the grid fixes
    /// a in {-2.5, 0, 1.5}, so a normalisation keyed on a past 1.75 never clamps wrongly there; the committed
    /// fixture's random cases reach it.
    /// </summary>
    [Fact]
    public void IntegralClampLinearNeverClampsWithMinAboveMax()
    {
        var (assembly, errors) = HlslSourceCompatibilityTests.CompileShaderMirror(CountingClampShadows);
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);
        var count = shaderType.GetField("ClampMinAboveMax")!;
        var function = shaderType.GetMethod("cultmath_integral_clamp_linear", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var bad = new List<string>();
        foreach (var c in HlslSourceCompatibilityTests.IntegralClampLinearCases)
        {
            count.SetValue(null, 0);
            function.Invoke(shader, c.Cast<object>().ToArray());
            if ((int)count.GetValue(null)! > 0)
                bad.Add($"({string.Join(", ", c)})");
        }

        Assert.True(bad.Count == 0, $"{bad.Count} cases clamp with the minimum above the maximum:{Environment.NewLine}{string.Join(Environment.NewLine, bad.Take(12))}");
    }

    // Identifier boundaries: cultmath_lerp is a CultMath function, lerp( is HLSL.
    private static readonly Regex HlslOnly = new(
        @"\b(float|u?int|bool)[234]\b|\bfloat3x3\b|\blerp\(|\bfrac\(|\bsaturate\(|\brsqrt\(|\basuint\(|\basfloat\(|\batan2\(|static const|\((float|int|uint)\)|Texture2D|SamplerState|#include|precision (high|medium|low)p");

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

        // The Apache-2.0 code is in its own file too, and the MPL-2.0 file holds none of it.
        var cycles = lowered.Single(output => output.File == "CultMath.Cycles.glsl").Text;
        Assert.DoesNotContain("cultmath_cycles_divide(", library);
        Assert.DoesNotContain("cultmath_cycles_divide(", phacelle);
        Assert.Contains("float cultmath_cycles_divide(", cycles);
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

        var cycles = lowered.Single(output => output.File == "CultMath.Cycles.glsl").Text.Split('\n')[0];
        Assert.Contains("cultmath_cycles_ramp_index is in CultMath.Cycles.glsl", library);
        Assert.Contains(" Apache-2.0;", cycles);
        Assert.DoesNotContain("MIT", cycles);
        Assert.DoesNotContain("MPL", cycles);
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
                output.WriteLine($"{name}: {Families[f].Verify!(want["cases"]!.AsArray())}");
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
    private static string AssertFrustumBall(JsonArray cases)
    {
        double low = double.PositiveInfinity, high = double.PositiveInfinity;
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
            low = Math.Min(low, (b[3] - reach) / radius);
            high = Math.Min(high, (reach + Math.ScaleB(radius + c1, -19) - b[3]) / Math.ScaleB(radius + c1, -19));
        }
        return $"least slack: {low:E3} of r above the exact ball, {high:F3} of 2^-19 (r + |c|_1) below its widening";
    }

    private static readonly double[] CheckEps = { -1.0, -0.5, 0.0, 0.5, 1.0 };

    // The origin, the six axis directions and the eight cube corners, normalised.
    private static readonly (double X, double Y, double Z)[] CheckDirections = CheckDirectionList().ToArray();

    private static IEnumerable<(double X, double Y, double Z)> CheckDirectionList()
    {
        yield return (0.0, 0.0, 0.0);
        foreach (var s in new[] { -1.0, 1.0 })
        {
            yield return (s, 0.0, 0.0);
            yield return (0.0, s, 0.0);
            yield return (0.0, 0.0, s);
        }

        var corner = 1.0 / Math.Sqrt(3.0);
        foreach (var sx in new[] { -corner, corner })
        foreach (var sy in new[] { -corner, corner })
        foreach (var sz in new[] { -corner, corner })
            yield return (sx, sy, sz);
    }

    // SampledForm applied to C#'s own results, with C#'s own snoise or fBm as the reference value. A form
    // must enclose its reference at every sample, and be no wider than the Lipschitz form's allowance.
    // allowance is the part of e that is the float32 allowance; a family that allowanceDecides must have a
    // sample that falls outside the form once it is removed, so the family can tell a device whose error
    // passes the allowance from one that does not.
    private static string AssertSampledForm(JsonArray cases, string name, Func<string[], float3, float> reference, Func<string[], double[], double> widthBound,
        Func<string[], double[], double> allowance, bool allowanceDecides)
    {
        double least = double.PositiveInfinity, leastRelative = double.PositiveInfinity, tightest = double.PositiveInfinity;
        var decided = 0;
        foreach (var entry in cases)
        {
            var sides = ((string)entry!).Split(" -> ");
            var tokens = sides[0].Split(' ');
            double[] a = Floats(string.Join(' ', tokens.Take(7))), form = Floats(sides[1]);
            double x0 = form[0], slope = form[1], e = form[2];
            var withoutAllowance = e - allowance(tokens, a);
            var allowanceNeeded = false;
            foreach (var eps in CheckEps)
            foreach (var d in CheckDirections)
            {
                var p = new float3((float)(a[0] + a[3] * eps + a[6] * d.X), (float)(a[1] + a[4] * eps + a[6] * d.Y), (float)(a[2] + a[5] * eps + a[6] * d.Z));
                double value = reference(tokens, p), middle = x0 + slope * eps;
                var inside = Math.Min(value - (middle - e), middle + e - value);
                Assert.True(inside >= 0.0, $"{name} case {entry}: the reference {value:R} at eps {eps} and direction {d} is outside the form's range [{middle - e:R}, {middle + e:R}] by {-inside:R}");
                least = Math.Min(least, inside);
                leastRelative = Math.Min(leastRelative, inside / e);
                allowanceNeeded |= Math.Abs(value - middle) > withoutAllowance;
            }

            if (allowanceNeeded)
                decided++;

            var bound = widthBound(tokens, a);
            Assert.True(Math.Abs(slope) + e <= bound + Math.ScaleB(1.0 + bound, -16), $"{name} case {entry}: the width {Math.Abs(slope) + e:R} is past B + 2^-16 (1 + B) = {bound + Math.ScaleB(1.0 + bound, -16):R}");
            tightest = Math.Min(tightest, (bound + Math.ScaleB(1.0 + bound, -16) - Math.Abs(slope) - e) / bound);
        }

        Assert.True(!allowanceDecides || decided > 0, $"{name}: no sample falls outside the form once the float32 allowance is removed, so the family cannot fail a device whose error passes it");
        return $"least enclosure slack {least:E3} ({leastRelative:E3} of e), least tightness slack {tightest:E3} of B, {decided} of {cases.Count} cases need the allowance";
    }

    // The float32 allowance of one octave of af_snoise: 2^-14 + 2^-12 R + L 2^-20 (|c|_1 + R).
    private static double SnoiseAllowance(double centre1, double reach) =>
        math.SNOISE_READ_VALUE + math.SNOISE_READ_GRADIENT * reach + math.SNOISE_LIPSCHITZ * Math.ScaleB(centre1 + reach, -20);

    // The Lipschitz form's width bound for one octave of the sampled-form check.
    private static double SnoiseWidthBound(double centre1, double reach) => math.SNOISE_LIPSCHITZ * reach + SnoiseAllowance(centre1, reach);

    private static double Length3(double[] a, int first) => Math.Sqrt(a[first] * a[first] + a[first + 1] * a[first + 1] + a[first + 2] * a[first + 2]);

    private static double SnoiseOctave(Func<double, double, double> octave, double[] a, double frequency) =>
        octave((Math.Abs(a[0]) + Math.Abs(a[1]) + Math.Abs(a[2])) * frequency, (Length3(a, 3) + a[6]) * frequency);

    // The amplitude-weighted sum of a per-octave quantity over the fBm's octaves.
    private static double FbmOctaves(string[] t, double[] a, Func<double, double, double> octave)
    {
        var (octaves, lacunarity, gain) = ((int)Convert.ToUInt32(t[7], 16), Floats(t[8])[0], Floats(t[9])[0]);
        double sum = 0.0, amplitude = 1.0, frequency = 1.0;
        for (var i = 0; i < Math.Clamp(octaves, 0, 16); i++)
        {
            sum += amplitude * SnoiseOctave(octave, a, frequency);
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return sum;
    }

    private static float SnoiseReference(string[] tokens, float3 p) => math.snoise(p);

    private static float FbmReference(string[] t, float3 p) => math.fbm_grad(p, (int)Convert.ToUInt32(t[7], 16), (float)Floats(t[8])[0], (float)Floats(t[9])[0]).w;

    private static string AssertSnoiseForm(JsonArray cases, bool allowanceDecides = false, Func<string[], float3, float>? reference = null) =>
        AssertSampledForm(cases, "af_snoise", reference ?? SnoiseReference,
            (_, a) => SnoiseOctave(SnoiseWidthBound, a, 1.0), (_, a) => SnoiseOctave(SnoiseAllowance, a, 1.0), allowanceDecides);

    private static string AssertFbmForm(JsonArray cases, bool allowanceDecides = false, Func<string[], float3, float>? reference = null) =>
        AssertSampledForm(cases, "af_fbm",
            reference ?? FbmReference,
            (t, a) => FbmOctaves(t, a, SnoiseWidthBound), (t, a) => FbmOctaves(t, a, SnoiseAllowance), allowanceDecides);

    // AffineFrustumBall applied to C#'s own results.
    private static string AssertAffineFrustumBall(JsonArray cases)
    {
        double enclosure = double.PositiveInfinity, lower = double.PositiveInfinity, upper = double.PositiveInfinity;
        var diagonal = new[] { (0.0, 0.0), (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0), (Math.Sqrt(0.5), Math.Sqrt(0.5)), (Math.Sqrt(0.5), -Math.Sqrt(0.5)), (-Math.Sqrt(0.5), Math.Sqrt(0.5)), (-Math.Sqrt(0.5), -Math.Sqrt(0.5)) };
        foreach (var entry in cases)
        {
            var sides = ((string)entry!).Split(" -> ");
            var a = Floats(sides[0]);
            var b = Floats(sides[1]);
            double mx = a[0], my = a[1], z0 = a[2], z1 = a[3], fp = a[4], warp = a[5];
            double zm = (z0 + z1) / 2.0, h = (z1 - z0) / 2.0;
            var r0 = z1 * fp + warp;
            var c1 = Math.Abs(mx * zm) + Math.Abs(my * zm) + Math.Abs(zm);
            var spread = (Math.Abs(mx) + Math.Abs(my) + 1.0) * h;
            double dx = b[0] - mx * zm, dy = b[1] - my * zm, dz = b[2] - zm;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            foreach (var z in new[] { z0, (3.0 * z0 + z1) / 4.0, zm, (z0 + 3.0 * z1) / 4.0, z1 })
            {
                var eps = h > 0.0 ? Math.Clamp((z - zm) / h, -1.0, 1.0) : 0.0;
                foreach (var (ex, ey) in diagonal)
                {
                    double px = (mx + fp * ex) * z - (b[0] + mx * h * eps), py = (my + fp * ey) * z - (b[1] + my * h * eps), pz = z - (b[2] + h * eps);
                    var slack = b[3] - (Math.Sqrt(px * px + py * py + pz * pz) + warp);
                    Assert.True(slack >= 0.0, $"af_frustum_ball case {entry}: the ray point at depth {z:R} and offset ({ex}, {ey}) is outside the ball by {-slack:R}");
                    enclosure = Math.Min(enclosure, slack / b[3]);
                }
            }

            Assert.True(b[3] >= r0 + d, $"af_frustum_ball case {entry}: radius short of R0 + d by {r0 + d - b[3]}");
            Assert.True(b[3] <= r0 + d + Math.ScaleB(r0 + c1 + spread, -19), $"af_frustum_ball case {entry}: radius past R0 + d by {b[3] - r0 - d}, more than 2^-19 (R0 + |c0|_1 + spread)");
            lower = Math.Min(lower, (b[3] - r0 - d) / r0);
            upper = Math.Min(upper, (r0 + d + Math.ScaleB(r0 + c1 + spread, -19) - b[3]) / Math.ScaleB(r0 + c1 + spread, -19));
        }

        return $"least enclosure slack {enclosure:E3} of the radius, least slack above R0 + d {lower:E3} of R0, below its widening {upper:F3} of 2^-19 (R0 + |c0|_1 + spread)";
    }

    private const string SpanAllowanceRead =
        "Read at the case's point x, with S = |x|_1 and (e_n, e_g, e_h) = math.span_slack(math.span_rounding(1, x, zero stage, 0, 0, 0)), " +
        "against the kernel's exact values in double: |n - n_exact| <= e_n and |g - g_exact|_2 <= e_g for snoise_grad, " +
        "||H - H_exact||_F <= e_h for snoise_hessian, no tolerance. " +
        "Report the largest fraction of each allowance per decade of S and the largest absolute error at S < 1e-2.";

    // SpanAllowanceRead applied to a family's results (C#'s own, or a device's): every read is within the
    // allowance math.span_slack gives at its point, the owner's own numbers and no restated literal. A small
    // family (bands 1e-4 to 1e2) must also fail the check once the absolute term is removed in at least 16
    // cases, and hold at least 48 cases with S <= 1e-2: the allowance's absolute term is what decides there.
    private static string AssertSpanAllowance(JsonArray cases, bool hessian, bool small)
    {
        var absolute = math.span_slack(0.0f);
        var fractions = new SortedDictionary<int, (double Value, double Gradient)>();
        double smallestError = 0.0;
        int absoluteDecides = 0, nearOrigin = 0;
        foreach (var entry in cases)
        {
            var sides = ((string)entry!).Split(" -> ");
            double[] x = Floats(sides[0]), result = Floats(sides[1]);
            var s = Math.Abs(x[0]) + Math.Abs(x[1]) + Math.Abs(x[2]);
            var slack = math.span_slack(math.span_rounding(1.0f, new float3((float)x[0], (float)x[1], (float)x[2]), default, new float3(0.0f, 0.0f, 0.0f), 0.0f, 0.0f));
            double valueError = 0.0, gradientError, valueFraction = 0.0, gradientFraction;
            bool needsAbsolute;
            if (hessian)
            {
                var exact = NoiseBoundTests.HessianDouble(x[0], x[1], x[2]);
                var sum = 0.0;
                for (var i = 0; i < 9; i++)
                    sum += (result[i] - exact[i]) * (result[i] - exact[i]);
                gradientError = Math.Sqrt(sum);
                Assert.True(gradientError <= slack.z, $"snoise_hessian case {entry}: ||H - H_exact||_F is {gradientError:R}, past the allowance {slack.z:R}");
                gradientFraction = gradientError / slack.z;
                needsAbsolute = gradientError > slack.z - absolute.z;
            }
            else
            {
                var exact = NoiseBoundTests.GradDouble(x[0], x[1], x[2]);
                valueError = Math.Abs(result[3] - NoiseBoundTests.ValueDouble(x[0], x[1], x[2]));
                gradientError = Math.Sqrt((result[0] - exact[0]) * (result[0] - exact[0]) + (result[1] - exact[1]) * (result[1] - exact[1]) + (result[2] - exact[2]) * (result[2] - exact[2]));
                Assert.True(valueError <= slack.x, $"snoise_grad case {entry}: |n - n_exact| is {valueError:R}, past the allowance {slack.x:R}");
                Assert.True(gradientError <= slack.y, $"snoise_grad case {entry}: |g - g_exact|_2 is {gradientError:R}, past the allowance {slack.y:R}");
                valueFraction = valueError / slack.x;
                gradientFraction = gradientError / slack.y;
                needsAbsolute = valueError > slack.x - absolute.x || gradientError > slack.y - absolute.y;
            }

            var decade = (int)Math.Floor(Math.Log10(s));
            fractions.TryGetValue(decade, out var largest);
            fractions[decade] = (Math.Max(largest.Value, valueFraction), Math.Max(largest.Gradient, gradientFraction));
            if (needsAbsolute)
                absoluteDecides++;
            if (s < 1.0e-2)
                smallestError = Math.Max(smallestError, Math.Max(valueError, gradientError));
            if (s <= 1.0e-2)
                nearOrigin++;
        }

        if (small)
        {
            Assert.True(absoluteDecides >= 16, $"only {absoluteDecides} of {cases.Count} cases fail once the absolute term is removed: the family cannot tell a device that needs it from one that does not");
            Assert.True(nearOrigin >= 48, $"only {nearOrigin} of {cases.Count} cases have |x|_1 <= 1e-2");
        }

        var report = string.Join(", ", fractions.Select(f => hessian ? $"1e{f.Key}: {f.Value.Gradient:F3}" : $"1e{f.Key}: {f.Value.Value:F3}/{f.Value.Gradient:F3}"));
        return $"largest fraction of {(hessian ? "e_h" : "e_n/e_g")} per decade of |x|_1: {report}; largest absolute error at |x|_1 < 1e-2 {smallestError:E3}; {absoluteDecides} of {cases.Count} cases need the absolute term";
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
        if (type.IsArray)
        {
            var array = Array.CreateInstance(type.GetElementType()!, SiteArrays.Capacity);
            for (var i = 0; i < array.Length; i++)
                array.SetValue(FromScalars(type.GetElementType()!, scalars), i);
            return array;
        }

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

    private sealed record Family(string Glsl, string Tolerance, Func<System.Random, object[]> Arguments, string? Check = null, Platform Platform = Platform.None, Func<JsonArray, string>? Verify = null);

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();
    private static float2 Point2(System.Random r) => new(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f));

    private static float3 Point3(System.Random r) => new(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f));

    // A point on the simplex tie set, where snoise's x0 has equal components (NoiseGradTests.SnoiseIsContinuousOnTheSimplexDiagonal):
    // seven of eight draws lie on a lattice diagonal v + s (1, 1, 1), v = L - sum(L) / 6 for an integer L, and one of eight on
    // the line (t, t, t). A device that runs a different tie rule reads a different function here.
    private static float3 TiePoint3(System.Random r)
    {
        if (r.Next(8) == 0)
        {
            var t = Uniform(r, -50.0f, 50.0f);
            return new float3(t, t, t);
        }

        var lx = r.Next(-64, 65);
        var ly = r.Next(-64, 65);
        var lz = r.Next(-64, 65);
        var shift = (lx + ly + lz) / 6.0f;
        var s = r.NextSingle();
        return new float3(lx - shift + s, ly - shift + s, lz - shift + s);
    }
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

    // An affine form (x0, a, e): e >= 0, spanning the widths a probe's forms take.
    private static float3 Form(System.Random r) => new(Uniform(r, -20.0f, 20.0f), Uniform(r, -5.0f, 5.0f), MathF.Pow(10.0f, Uniform(r, -4.0f, 1.0f)));

    // A slice's axis and radius in noise space, log-uniform from 1e-3 to 0.5: below about 0.15 the
    // centred form of af_snoise wins, above it the Lipschitz form does, so both are drawn.
    private static float3 Axis3(System.Random r)
    {
        var scale = MathF.Pow(10.0f, Uniform(r, -3.0f, -0.3f));
        return new float3(Uniform(r, -1.0f, 1.0f) * scale, Uniform(r, -1.0f, 1.0f) * scale, Uniform(r, -1.0f, 1.0f) * scale);
    }

    private static float BallRadius(System.Random r) => MathF.Pow(10.0f, Uniform(r, -3.0f, -0.3f));

    // A slice of reach 1e-6 to 1e-3, the short slices and small footprints near a camera, where the float32
    // allowance is most of the form's width: the centre in [-50, 50] or [-1, 1], an axis of random direction
    // (zero in one case of four) and a radius, each log-uniform in 1e-6 to 1e-3.
    private static object[] SmallReachSlice(System.Random r)
    {
        var span = r.Next(2) == 0 ? 50.0f : 1.0f;
        var centre = new float3(Uniform(r, -span, span), Uniform(r, -span, span), Uniform(r, -span, span));
        var axis = new float3(0.0f, 0.0f, 0.0f);
        if (r.Next(4) != 0)
        {
            var direction = math.normalize(new float3(Uniform(r, -1.0f, 1.0f), Uniform(r, -1.0f, 1.0f), Uniform(r, -1.0f, 1.0f)));
            axis = direction * MathF.Pow(10.0f, Uniform(r, -6.0f, -3.0f));
        }

        return new object[] { centre, axis, MathF.Pow(10.0f, Uniform(r, -6.0f, -3.0f)) };
    }

    private const string SampledForm =
        "the arguments are (cx, cy, cz, ax, ay, az, radius, ...) and the result is the form (x0, a, e). Enclosure: for eps in " +
        "{-1, -0.5, 0, 0.5, 1} and d in {0, +-ex, +-ey, +-ez, (+-1, +-1, +-1) / sqrt(3)} (15 directions), form the point " +
        "p = c + axis eps + radius d in double and round it to float32; the device's own reference value at p must lie in " +
        "[x0 + a eps - e, x0 + a eps + e] with no tolerance, so a device whose gradient error exceeds the allowance fails. " +
        "Tightness: with L = 7.9640074 and, for each octave (one for af_snoise), R = |axis| + radius, " +
        "B = L R + 2^-14 + 2^-12 R + L 2^-20 (|c|_1 + R), the width |a| + e must be at most B + 2^-16 (1 + B). " +
        "Report the least slack of each side, absolutely and as a fraction of e; only the two bounds fail the check. ";

    private const string SnoiseForm = SampledForm + "The reference value is cultmath_snoise(p).";

    private const string FbmForm = SampledForm +
        "The reference value is cultmath_fbm_grad(p, octaves, lacunarity, gain).w, and B is the sum over octaves i of gain^i B_i, " +
        "where B_i is B for the centre lacunarity^i c, the axis lacunarity^i axis and the radius lacunarity^i radius.";

    // The two small-reach families add the centre leg to every af_snoise and af_fbm family. The device's
    // reference value is its own noise, so a device whose noise is wrong everywhere encloses its own wrong
    // values; C#'s value at the centre, the form's own x0 in the fixture, is the independent reading.
    private const string CentreLeg =
        " Centre leg, for every cultmath_af_snoise and cultmath_af_fbm family of this fixture, the earlier ones included: " +
        "the fixture's own x0 in the result (C#'s value at the centre) must lie in [x0 - e, x0 + e] of the GPU's form, with " +
        "no tolerance, so a device whose noise differs from C#'s by more than its e fails.";

    private const string SmallReach =
        " These cases have reach |axis| + radius of 1e-6 to 1e-3, where the float32 allowance is most of e and the second order " +
        "remainder is not; the enclosure above fails a device whose gradient error exceeds the allowance.";

    private const string SnoiseSmallForm = SnoiseForm + SmallReach + CentreLeg;

    private const string FbmSmallForm = FbmForm + SmallReach + CentreLeg;

    private const string AffineFrustumBall =
        "enclosure and tightness: from the arguments (mx, my, z0, z1, fp, warpVariation) compute in double zm = (z0 + z1) / 2, " +
        "h = (z1 - z0) / 2, axis = (mx h, my h, h), c0 = (mx zm, my zm, zm), |c0|_1 = |mx zm| + |my zm| + |zm|, R0 = z1 fp + warpVariation, " +
        "spread = (|mx| + |my| + 1) h and d = |GPU centre - c0|. Enclosure: for z in {z0, (3 z0 + z1) / 4, zm, (z0 + 3 z1) / 4, z1}, " +
        "eps = clamp((z - zm) / h, -1, 1) and the nine footprint offsets e in {(0, 0), (+-1, 0), (0, +-1), (+-1, +-1) / sqrt(2)}, " +
        "the point P = ((mx + fp e.x) z, (my + fp e.y) z, z) satisfies |P - (GPU centre + axis eps)| + warpVariation <= GPU radius. " +
        "Tightness: R0 + d <= GPU radius <= R0 + d + 2^-19 (R0 + |c0|_1 + spread). " +
        "Report the least slack of each side; only the bounds fail the check.";

    // The Cycles node families' operands: values past [0, 1] on both sides, so the clamps, the extrapolating
    // factors and the divisions by a small number all show.
    private static float CyclesValue(System.Random r) => Uniform(r, -20.0f, 20.0f);
    private static float CyclesFactor(System.Random r) => Uniform(r, -0.5f, 1.5f);
    private static float3 CyclesColor(System.Random r) => new(Uniform(r, -0.25f, 1.25f), Uniform(r, -0.25f, 1.25f), Uniform(r, -0.25f, 1.25f));

    // A Phacelle stripe wave vector: each component in [-6, 6], about one stripe per cell.
    private static float3 Side(System.Random r) => new(Uniform(r, -6.0f, 6.0f), Uniform(r, -6.0f, 6.0f), Uniform(r, -6.0f, 6.0f));

    // Every family the site's WebGL2 readback evaluates, in a fixed order. A new family is appended, so the
    // seeded stream and every earlier family's cases stay as they were. pcg3d and pcg4d are "exact"; the
    // float families are ulp-bounded, with the bound measured on the device by the consumer, and
    // iv_frustum_ball also carries an enclosure check; the four coordinate-band snoise_grad and snoise_hessian
    // families carry an allowance check, applied to C#'s own results here and to a device's by the device judge. phacelle is in CultMath.Phacelle.glsl, so its
    // evaluator concatenates that file after CultMath.glsl; the site, which does not vendor it, skips it.
    private static readonly Family[] Families = new Family[]
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
        }, FrustumBall, Verify: AssertFrustumBall),
        new("vec2 cultmath_iv_exp(vec2)", "ulp-bounded, platform exp", r => new object[] { Interval(r) }, Platform: Platform.Ulp),
        new("CultPhasor cultmath_phacelle(vec3, vec3, float, float)", "ulp-bounded, platform exp, sin, cos; across platforms |diff| <= 2^-20 max(|v|, 1); on a device |diff| <= 2^-18 max(|v|, 1)",
            r => new object[] { Point3(r), Side(r), Uniform(r, 0.0f, 1.0f), 0.5f }, Platform: Platform.Scaled),
        new("float cultmath_snoise(vec3)", "ulp-bounded, on the simplex tie set", r => new object[] { TiePoint3(r) }),
        new("vec4 cultmath_snoise_grad(vec3)", "ulp-bounded, on the simplex tie set", r => new object[] { TiePoint3(r) }),
        new("vec3 cultmath_af_point(float)", "ulp-bounded", r => new object[] { Uniform(r, -20.0f, 20.0f) }),
        new("vec3 cultmath_af_symbol(float, float)", "ulp-bounded", r => new object[] { Uniform(r, -20.0f, 20.0f), Uniform(r, -5.0f, 5.0f) }),
        new("vec3 cultmath_af_from_iv(vec2)", "ulp-bounded", r => new object[] { Interval(r) }),
        new("vec2 cultmath_af_range(vec3)", "ulp-bounded", r => new object[] { Form(r) }),
        new("vec3 cultmath_af_add(vec3, vec3)", "ulp-bounded", r => new object[] { Form(r), Form(r) }),
        new("vec3 cultmath_af_sub(vec3, vec3)", "ulp-bounded", r => new object[] { Form(r), Form(r) }),
        new("vec3 cultmath_af_neg(vec3)", "ulp-bounded", r => new object[] { Form(r) }),
        new("vec3 cultmath_af_scale(vec3, float)", "ulp-bounded", r => new object[] { Form(r), Uniform(r, -4.0f, 4.0f) }),
        new("vec3 cultmath_af_add_iv(vec3, vec2)", "ulp-bounded", r => new object[] { Form(r), Interval(r) }),
        new("vec3 cultmath_af_mul(vec3, vec3)", "ulp-bounded", r => new object[] { Form(r), Form(r) }),
        new("vec3 cultmath_af_snoise(vec3, vec3, float)", "ulp-bounded", r => new object[] { Point3(r), Axis3(r), BallRadius(r) }, SnoiseForm, Verify: cases => AssertSnoiseForm(cases)),
        new("vec3 cultmath_af_fbm(vec3, vec3, float, int, float, float)", "ulp-bounded", r => new object[] { Point3(r), Axis3(r), BallRadius(r), 4, 2.0f, 0.5f }, FbmForm, Verify: cases => AssertFbmForm(cases)),
        new("vec3 cultmath_af_frustum_axis(vec2, float, float)", "ulp-bounded", r =>
        {
            var z0 = Uniform(r, 0.1f, 100.0f);
            return new object[] { new float2(Uniform(r, -1.0f, 1.0f), Uniform(r, -1.0f, 1.0f)), z0, z0 + Uniform(r, 0.01f, 50.0f) };
        }),
        new("vec4 cultmath_af_frustum_ball(vec2, float, float, float, float)", "ulp-bounded", r =>
        {
            var slope = new float2(Uniform(r, -1.0f, 1.0f), Uniform(r, -1.0f, 1.0f));
            var z0 = Uniform(r, 0.1f, 100.0f);
            return new object[] { slope, z0, z0 + Uniform(r, 0.01f, 50.0f), Uniform(r, 0.0001f, 0.05f), Uniform(r, 0.0f, 2.0f) };
        }, AffineFrustumBall, Verify: AssertAffineFrustumBall),
        new("vec2 cultmath_iv_snoise_ball(vec2, float)", "ulp-bounded", r => new object[] { new float2(Uniform(r, -50.0f, 50.0f), Uniform(r, -50.0f, 50.0f)), Uniform(r, 0.001f, 2.0f) }),
        new("vec3 cultmath_af_snoise(vec3, vec3, float)", "ulp-bounded, reach 1e-6 to 1e-3", SmallReachSlice, SnoiseSmallForm, Verify: cases => AssertSnoiseForm(cases, allowanceDecides: true)),
        new("vec3 cultmath_af_fbm(vec3, vec3, float, int, float, float)", "ulp-bounded, reach 1e-6 to 1e-3", r => SmallReachSlice(r).Concat(new object[] { 4, 2.0f, 0.5f }).ToArray(), FbmSmallForm, Verify: cases => AssertFbmForm(cases, allowanceDecides: true)),
        new("float cultmath_cycles_divide(float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_power(float, float)", "ulp-bounded, platform pow", r => new object[] { Uniform(r, -4.0f, 4.0f), r.Next(2) == 0 ? r.Next(-4, 5) : Uniform(r, -3.0f, 3.0f) }, Platform: Platform.Ulp),
        new("float cultmath_cycles_logarithm(float, float)", "ulp-bounded, platform log", r => new object[] { Uniform(r, 0.01f, 20.0f), Uniform(r, 0.01f, 20.0f) }, Platform: Platform.Ulp),
        new("float cultmath_cycles_sqrt(float)", "ulp-bounded", r => new object[] { CyclesValue(r) }),
        new("float cultmath_cycles_inverse_sqrt(float)", "ulp-bounded", r => new object[] { CyclesValue(r) }),
        new("float cultmath_cycles_modulo(float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_floored_modulo(float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_wrap(float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), Uniform(r, -10.0f, 10.0f), Uniform(r, -10.0f, 10.0f) }),
        new("float cultmath_cycles_snap(float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_pingpong(float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_smooth_min(float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), Uniform(r, -5.0f, 5.0f) }),
        new("float cultmath_cycles_smooth_max(float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), Uniform(r, -5.0f, 5.0f) }),
        new("float cultmath_cycles_compare(float, float, float)", "ulp-bounded", r => { var a = CyclesValue(r); return new object[] { a, a + Uniform(r, -1.0f, 1.0f) * 1.0e-3f, Uniform(r, 0.0f, 2.0e-3f) }; }),
        new("float cultmath_cycles_round(float)", "ulp-bounded", r => new object[] { CyclesValue(r) }),
        new("float cultmath_cycles_trunc(float)", "ulp-bounded", r => new object[] { CyclesValue(r) }),
        new("float cultmath_cycles_fract(float)", "ulp-bounded", r => new object[] { CyclesValue(r) }),
        new("float cultmath_cycles_sign(float)", "ulp-bounded", r => new object[] { r.Next(4) == 0 ? 0.0f : CyclesValue(r) }),
        new("float cultmath_cycles_map_range_linear(float, float, float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_map_range_stepped(float, float, float, float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r), Uniform(r, -1.0f, 8.0f) }),
        new("float cultmath_cycles_map_range_smoothstep(float, float, float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_map_range_smootherstep(float, float, float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_clamp_minmax(float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_clamp_range(float, float, float)", "ulp-bounded", r => new object[] { CyclesValue(r), CyclesValue(r), CyclesValue(r) }),
        new("float cultmath_cycles_mix_float(float, float, float)", "ulp-bounded", r => new object[] { CyclesFactor(r), CyclesValue(r), CyclesValue(r) }),
        new("vec3 cultmath_cycles_mix_vector(float, vec3, vec3)", "ulp-bounded", r => new object[] { CyclesFactor(r), CyclesColor(r), CyclesColor(r) }),
        new("vec3 cultmath_cycles_rgb_to_hsv(vec3)", "ulp-bounded", r => new object[] { CyclesColor(r) }),
        new("vec3 cultmath_cycles_hsv_to_rgb(vec3)", "ulp-bounded", r => new object[] { new float3(r.NextSingle(), r.NextSingle(), Uniform(r, 0.0f, 2.0f)) }),
        new("float cultmath_cycles_rgb_to_bw(vec3)", "ulp-bounded", r => new object[] { CyclesColor(r) }),
        new("vec2 cultmath_cycles_ramp_index(float, int)", "ulp-bounded", r => new object[] { Uniform(r, -0.25f, 1.25f), 257 }),
    }.Concat(new[]
    {
        "blend", "add", "mul", "screen", "overlay", "sub", "div", "diff", "exclusion", "dark", "light", "dodge", "burn", "hue", "sat", "val", "color", "soft", "linear",
    }.Select(mode => new Family($"vec3 cultmath_cycles_mix_{mode}(float, vec3, vec3)", "ulp-bounded", r => new object[] { CyclesFactor(r), CyclesColor(r), CyclesColor(r) }))).Concat(new[]
    {
        new Family("float cultmath_cycles_arcsine(float)", "ulp-bounded, platform asin", r => new object[] { Uniform(r, -1.5f, 1.5f) }, Platform: Platform.Ulp),
        new Family("float cultmath_cycles_arccosine(float)", "ulp-bounded, platform acos", r => new object[] { Uniform(r, -1.5f, 1.5f) }, Platform: Platform.Ulp),
        new Family("float cultmath_cycles_arctan2(float, float)", "ulp-bounded, platform atan", r => new object[] { Uniform(r, -20.0f, 20.0f), Uniform(r, -20.0f, 20.0f) }, Platform: Platform.Ulp),
        new Family("mat3 cultmath_snoise_hessian(vec3)", "ulp-bounded", r => new object[] { Point3(r) }),
        new Family("mat3 cultmath_snoise_hessian(vec3)", "ulp-bounded, on the simplex tie set", r => new object[] { TiePoint3(r) }),
        new Family("float cultmath_integral_clamp_linear(float, float, float, float, float, float)", "ulp-bounded",
            r =>
            {
                var lo = Uniform(r, -2.0f, 0.5f);
                return new object[] { Uniform(r, -4.0f, 4.0f), r.Next(8) == 0 ? 0.0f : Uniform(r, -4.0f, 4.0f), lo, lo + Uniform(r, 0.0f, 3.0f), Uniform(r, -3.0f, 3.0f), Uniform(r, -3.0f, 3.0f) };
            }),
        // The edge cases the family above never draws: lo above hi (b = 0 and b != 0; the lowering must
        // normalise before any clamp sees it) and infinite bounds (hi = +inf, lo = -inf, both).
        new Family("float cultmath_integral_clamp_linear(float, float, float, float, float, float)", "ulp-bounded, lo above hi and infinite bounds",
            r =>
            {
                var a = Uniform(r, -4.0f, 4.0f);
                var slope = Uniform(r, -4.0f, 4.0f);
                var (t0, t1) = (Uniform(r, -3.0f, 3.0f), Uniform(r, -3.0f, 3.0f));
                return r.Next(5) switch
                {
                    0 => new object[] { a, 0.0f, Uniform(r, 0.5f, 2.0f), Uniform(r, -2.0f, 0.4f), t0, t1 },
                    1 => new object[] { a, slope, Uniform(r, 0.5f, 2.0f), Uniform(r, -2.0f, 0.4f), t0, t1 },
                    2 => new object[] { a, slope, Uniform(r, -2.0f, 1.0f), float.PositiveInfinity, t0, t1 },
                    3 => new object[] { a, slope, float.NegativeInfinity, Uniform(r, -1.0f, 2.0f), t0, t1 },
                    _ => new object[] { a, slope, float.NegativeInfinity, float.PositiveInfinity, t0, t1 },
                };
            }),
        // Zero-length spans (t1 = t0 three times in four) with a flat or sloped line, and infinite clamp bounds
        // on either side or both: an infinite bound times a zero length must give +0, and a flat line clamped
        // to an infinite bound over a nonzero span is infinite, never NaN.
        new Family("float cultmath_integral_clamp_linear(float, float, float, float, float, float)", "ulp-bounded, zero-length spans and flat infinite clamps",
            r =>
            {
                float Line() => r.Next(3) switch { 0 => 0.0f, 1 => -0.0f, _ => Uniform(r, -4.0f, 4.0f) };
                float Bound() => r.Next(3) switch { 0 => float.NegativeInfinity, 1 => float.PositiveInfinity, _ => Uniform(r, -2.0f, 2.0f) };
                var (a, b, lo, hi) = (Line(), Line(), Bound(), Bound());
                var t0 = Uniform(r, -3.0f, 3.0f);
                var t1 = r.Next(4) != 0 ? t0 : Uniform(r, -3.0f, 3.0f);
                return new object[] { a, b, lo, hi, t0, t1 };
            }),
        // The span stages over their struct arguments: realistic stages and spans (SpanStageTests' own draw), the
        // midpoint a Point3, one span in four a point.
        new Family("SpanNoise cultmath_span_fbm(SpanPath, SpanStage, int, float, float)", "ulp-bounded", SpanArguments),
        new Family("SpanNoise cultmath_span_ridged(SpanPath, SpanStage, int, float, float)", "ulp-bounded", SpanArguments),
        // The kernel's reads over coordinate bands, the magnitude log-uniform in the band: the allowance of
        // math.span_slack is checked against the exact kernel at every case (SpanAllowanceRead).
        new Family("vec4 cultmath_snoise_grad(vec3)", "ulp-bounded, coordinate bands 1e-4 to 1e2", r => new object[] { BandPoint3(r, 1.0e-4, 1.0e2) }, SpanAllowanceRead, Verify: cases => AssertSpanAllowance(cases, hessian: false, small: true)),
        new Family("vec4 cultmath_snoise_grad(vec3)", "ulp-bounded, coordinate bands 1e2 to 4e6", r => new object[] { BandPoint3(r, 1.0e2, 4.0e6) }, SpanAllowanceRead, Verify: cases => AssertSpanAllowance(cases, hessian: false, small: false)),
        new Family("mat3 cultmath_snoise_hessian(vec3)", "ulp-bounded, coordinate bands 1e-4 to 1e2", r => new object[] { BandPoint3(r, 1.0e-4, 1.0e2) }, SpanAllowanceRead, Verify: cases => AssertSpanAllowance(cases, hessian: true, small: true)),
        new Family("mat3 cultmath_snoise_hessian(vec3)", "ulp-bounded, coordinate bands 1e2 to 4e6", r => new object[] { BandPoint3(r, 1.0e2, 4.0e6) }, SpanAllowanceRead, Verify: cases => AssertSpanAllowance(cases, hessian: true, small: false)),
        // Segment distance in 2D and 3D (one segment in eight degenerate), then the site cells: scores, fold step
        // over states built by inserting 0-3 random scores (one in four with an exactly tied score), border, margin.
        new Family("float cultmath_distance_to_segment(vec2, vec2, vec2)", "ulp-bounded", r =>
        {
            var start = Point2(r);
            return new object[] { Point2(r), start, r.Next(8) == 0 ? start : Point2(r) };
        }),
        new Family("vec2 cultmath_segment_closest(vec2, vec2, vec2)", "ulp-bounded", r =>
        {
            var start = Point2(r);
            return new object[] { Point2(r), start, r.Next(8) == 0 ? start : Point2(r) };
        }),
        new Family("float cultmath_distance_to_segment(vec3, vec3, vec3)", "ulp-bounded", r =>
        {
            var start = Point3(r);
            return new object[] { Point3(r), start, r.Next(8) == 0 ? start : Point3(r) };
        }),
        new Family("vec3 cultmath_segment_closest(vec3, vec3, vec3)", "ulp-bounded", r =>
        {
            var start = Point3(r);
            return new object[] { Point3(r), start, r.Next(8) == 0 ? start : Point3(r) };
        }),
        // The border step: owner and other random (one in six coincident), a running border either empty or a
        // random (normal, distance); one in six other is a distinct site within 1e-3 of the owner (inside the 1e-2 guard a
        // changed length test would trip), one in six coincident.
        new Family("vec4 cultmath_power_border_step(vec3, vec4, vec4, vec4)", "ulp-bounded", r =>
        {
            var owner = Site(r, 0.1f, 20.0f);
            var border = r.Next(2) == 0 ? new float4(0.0f, 0.0f, 0.0f, 1.0e30f) : new float4(Point3(r), Uniform(r, 0.0f, 50.0f));
            var pick = r.Next(6);
            var near = new float4(owner.xyz + new float3(Uniform(r, 1.0e-4f, 1.0e-3f), Uniform(r, -1.0e-3f, 1.0e-3f), Uniform(r, -1.0e-3f, 1.0e-3f)), Uniform(r, 0.1f, 20.0f));
            return new object[] { Point3(r), owner, pick == 0 ? owner : pick == 1 ? near : Site(r, 0.1f, 20.0f), border };
        }),
        new Family("vec4 cultmath_power_score(vec3, vec4)", "ulp-bounded", r => new object[] { Point3(r), Site(r, 0.1f, 20.0f) }),
        new Family("vec4 cultmath_multiplicative_score(vec3, vec4)", "ulp-bounded", r => new object[] { Point3(r), Site(r, 0.1f, 100.0f, logWeight: true) }),
        new Family("CultSiteCells cultmath_site_cells_insert(CultSiteCells, vec4, int)", "ulp-bounded", r =>
        {
            var (cells, scores) = CellsState(r);
            var score = r.Next(4) == 0 && scores.Count > 0 ? scores[r.Next(scores.Count)] : PowerOrMultiplicative(r);
            return new object[] { cells, score, scores.Count };
        }),
        new Family("vec4 cultmath_site_cells_border(CultSiteCells)", "ulp-bounded", r =>
        {
            var (cells, _) = CellsState(r, minimum: r.Next(8) == 0 ? 0 : 2);
            return new object[] { cells };
        }),
        new Family("float cultmath_gabriel_margin(vec3, vec3, vec3)", "ulp-bounded", r =>
        {
            var a = Point3(r);
            return new object[] { a, r.Next(8) == 0 ? a : Point3(r), Point3(r) };
        }),
        // The lattice steps. The cell is an integer-valued vec2 (negative and large included), time and drift span
        // zero and the accepted range; the weight's border covers 0, near the falloff, and the 1.0e30 of no body.
        new Family("vec3 cultmath_lattice_site(vec2, float, float)", "ulp-bounded", r =>
            new object[] { new float2(r.Next(-4000, 4000), r.Next(-4000, 4000)), r.Next(6) == 0 ? 0.0f : Uniform(r, -200.0f, 200.0f), r.Next(6) == 0 ? 0.0f : Uniform(r, -3.0f, 3.0f) }),
        new Family("float cultmath_lattice_weight(float, float, float, float)", "ulp-bounded", r =>
        {
            var falloff = Uniform(r, 0.05f, 40.0f);
            var border = r.Next(5) switch { 0 => 0.0f, 1 => 1.0e30f, 2 => falloff * Uniform(r, 0.9f, 1.1f), _ => Uniform(r, 0.0f, 3.0f * falloff) };
            var boost = r.Next(5) switch { 0 => 0.0f, 1 => 4.0f, _ => Uniform(r, 0.0f, 4.0f) };
            return new object[] { Uniform(r, 0.5f, 1.0f), border, boost, falloff };
        }),
        new Family("float cultmath_lattice_distance(vec2, vec2, float, vec2, float)", "ulp-bounded", r =>
        {
            var angle = Uniform(r, 0.0f, 6.2831855f);
            var normal = r.Next(6) == 0 ? new float2(1.0f, 0.0f) : new float2(MathF.Cos(angle), MathF.Sin(angle));
            var stretch = r.Next(5) switch { 0 => 1.0f, 1 => 2.0f, _ => Uniform(r, 1.0f, 2.0f) };
            return new object[] { Point2(r), Point2(r), Uniform(r, 0.5f, 5.0f), normal, stretch };
        }),
        // The smooth step: d exactly at f1 + 2 smoothing one in four (the equal branch), near it one in four, the
        // smoothing near 0 and at 1, m anywhere up to f1 + smoothing.
        // One draw in four is the cap point: m = f1 + smoothing exactly and d = f1 + smoothing (1 + U(0.3, 0.9)) with smoothing in
        // [0.5, 1], where next exceeds f1 + smoothing and the min clamps; a step that returns next reads another function there.
        new Family("float cultmath_lattice_smooth_step(float, float, float, float)", "ulp-bounded", r =>
        {
            if (r.Next(4) == 0)
            {
                var cappedSmoothing = Uniform(r, 0.5f, 1.0f);
                var cappedF1 = Uniform(r, 0.0f, 10.0f);
                return new object[] { cappedF1 + cappedSmoothing, cappedF1 + cappedSmoothing * (1.0f + Uniform(r, 0.3f, 0.9f)), cappedF1, cappedSmoothing };
            }

            var smoothing = r.Next(5) switch { 0 => 1.0f, 1 => Uniform(r, 1.0e-3f, 1.0e-2f), _ => Uniform(r, 0.01f, 1.0f) };
            var f1 = Uniform(r, 0.0f, 10.0f);
            var edge = f1 + 2.0f * smoothing;
            var d = r.Next(4) switch { 0 => edge, 1 => edge * Uniform(r, 0.99f, 1.01f), _ => Uniform(r, f1, edge + 1.0f) };
            return new object[] { Uniform(r, f1, f1 + smoothing), d, f1, smoothing };
        }),
        // The site-array functions: a fixed-capacity array and a count, killed on the device (the lattice at rings up to 17).
        new Family($"vec4 cultmath_power_border(vec3, vec4[{SiteArrays.Capacity}], int)", "ulp-bounded", SiteArrays.PowerBorderArguments),
        new Family($"vec4 cultmath_weighted_lattice_cells(vec2, float, vec4[{SiteArrays.Capacity}], int, float, float, float, float, float, vec2, float)", "ulp-bounded", SiteArrays.LatticeArguments),
    }).ToArray();

    // A point of magnitude m, m log-uniform in [lo, hi]: one component at +-m, the other two uniform in [-m, m],
    // the axis of the large one random.
    private static float3 BandPoint3(System.Random r, double lo, double hi)
    {
        var m = lo * Math.Pow(hi / lo, r.NextDouble());
        var v = new double[] { (r.NextDouble() * 2.0 - 1.0) * m, (r.NextDouble() * 2.0 - 1.0) * m, (r.NextDouble() * 2.0 - 1.0) * m };
        v[r.Next(3)] = r.Next(2) == 0 ? m : -m;
        return new float3((float)v[0], (float)v[1], (float)v[2]);
    }


    // A site float4(position.xyz, weight): the weight uniform in [lo, hi], or log-uniform when logWeight.
    private static float4 Site(System.Random r, float lo, float hi, bool logWeight = false)
    {
        var p = Point3(r);
        return new float4(p.x, p.y, p.z, logWeight ? lo * MathF.Pow(hi / lo, r.NextSingle()) : Uniform(r, lo, hi));
    }

    private static float4 PowerOrMultiplicative(System.Random r) =>
        r.Next(2) == 0 ? math.power_score(Point3(r), Site(r, 0.1f, 20.0f)) : math.multiplicative_score(Point3(r), Site(r, 0.1f, 100.0f, logWeight: true));

    // A fold state made by the production path: site_cells_insert over a few random scores (at least minimum).
    private static (CultSiteCells Cells, List<float4> Scores) CellsState(System.Random r, int minimum = 0)
    {
        var scores = new List<float4>();
        var cells = math.site_cells_empty();
        for (var i = minimum + r.Next(4 - minimum); i > 0; i--)
        {
            var score = r.Next(4) == 0 && scores.Count > 0 ? scores[^1] : PowerOrMultiplicative(r);
            cells = math.site_cells_insert(cells, score, scores.Count);
            scores.Add(score);
        }

        return (cells, scores);
    }

    private static object[] SpanArguments(System.Random r)
    {
        var stage = SpanStageTests.Stage.Draw(r);
        var halfLength = r.Next(4) == 0 ? 0.0f : new[] { 0.02f, 0.1f, 0.5f }[r.Next(3)];
        var span = SpanStageTests.Span.Draw(r, Point3(r), halfLength, Uniform(r, 0.0f, 2.0f));
        return new object[] { span.Path, stage.S, stage.Octaves, stage.Lacunarity, stage.Gain };
    }

    /// <summary>
    /// The committed fixture's cases of the three cultmath_integral_clamp_linear families, decoded into the
    /// C# arguments (a, b, lo, hi, t0, t1): the random cases the clamp-normalisation tests also run.
    /// </summary>
    internal static IReadOnlyList<float[]> IntegralClampLinearFixtureCases
    {
        get
        {
            const string clamp = "float cultmath_integral_clamp_linear(float, float, float, float, float, float)";
            var method = Resolve(clamp);
            return JsonNode.Parse(File.ReadAllText(FixturePath))!["functions"]!.AsArray()
                .Where(family => (string)family!["glsl"]! == clamp)
                .SelectMany(family => family!["cases"]!.AsArray())
                .Select(entry => Arguments(method, ((string)entry!).Split(" -> ")[0]).Select(a => (float)a).ToArray())
                .ToArray();
        }
    }

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
        ["SpanPath"] = typeof(SpanPath), ["SpanStage"] = typeof(SpanStage),
        ["CultSiteCells"] = typeof(CultSiteCells),
        [$"vec4[{SiteArrays.Capacity}]"] = typeof(float4[]),
    };

    // The C# math method a GLSL signature names: cultmath_ dropped, parameter types mapped.
    private static MethodInfo Resolve(string glsl)
    {
        var match = Regex.Match(glsl, @"^\w+ cultmath_(\w+)\(([^)]*)\)$");
        var types = match.Groups[2].Value.Split(", ").Select(t => GlslTypes[t]).ToArray();
        return typeof(math).GetMethod(match.Groups[1].Value, BindingFlags.Public | BindingFlags.Static, null, types, null)
            ?? typeof(SiteArrays).GetMethod(match.Groups[1].Value, BindingFlags.NonPublic | BindingFlags.Static, null, types, null)
            ?? throw new InvalidOperationException("no C# math counterpart for " + glsl);
    }

    private static string Hex(IEnumerable<object> values) => string.Join(" ", values.SelectMany(Scalars).Select(v => v switch
    {
        float f => BitConverter.SingleToInt32Bits(f).ToString("x8"),
        int i => i.ToString("x8"),
        uint u => u.ToString("x8"),
        _ => throw new InvalidOperationException(v.GetType().Name),
    }));

    // A float3x3 flattens row by row; the matrices the library exports are symmetric, so the order is
    // the same read as GLSL's columns.
    private static IEnumerable<object> Scalars(object value) => value is float or int or uint
        ? new[] { value }
        : value is Array array
        ? array.Cast<object>().SelectMany(Scalars)
        : value is float3x3 matrix
        ? new object[] { matrix._m00, matrix._m01, matrix._m02, matrix._m10, matrix._m11, matrix._m12, matrix._m20, matrix._m21, matrix._m22 }
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
