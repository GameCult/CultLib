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
    private static string AssertSampledForm(JsonArray cases, string name, Func<string[], float3, float> reference, Func<string[], double[], double> widthBound)
    {
        double least = double.PositiveInfinity, leastRelative = double.PositiveInfinity, tightest = double.PositiveInfinity;
        foreach (var entry in cases)
        {
            var sides = ((string)entry!).Split(" -> ");
            var tokens = sides[0].Split(' ');
            double[] a = Floats(string.Join(' ', tokens.Take(7))), form = Floats(sides[1]);
            double x0 = form[0], slope = form[1], e = form[2];
            foreach (var eps in CheckEps)
            foreach (var d in CheckDirections)
            {
                var p = new float3((float)(a[0] + a[3] * eps + a[6] * d.X), (float)(a[1] + a[4] * eps + a[6] * d.Y), (float)(a[2] + a[5] * eps + a[6] * d.Z));
                double value = reference(tokens, p), middle = x0 + slope * eps;
                var inside = Math.Min(value - (middle - e), middle + e - value);
                Assert.True(inside >= 0.0, $"{name} case {entry}: the reference {value:R} at eps {eps} and direction {d} is outside the form's range [{middle - e:R}, {middle + e:R}] by {-inside:R}");
                least = Math.Min(least, inside);
                leastRelative = Math.Min(leastRelative, inside / e);
            }

            var bound = widthBound(tokens, a);
            Assert.True(Math.Abs(slope) + e <= bound + Math.ScaleB(1.0 + bound, -16), $"{name} case {entry}: the width {Math.Abs(slope) + e:R} is past B + 2^-16 (1 + B) = {bound + Math.ScaleB(1.0 + bound, -16):R}");
            tightest = Math.Min(tightest, (bound + Math.ScaleB(1.0 + bound, -16) - Math.Abs(slope) - e) / bound);
        }

        return $"least enclosure slack {least:E3} ({leastRelative:E3} of e), least tightness slack {tightest:E3} of B";
    }

    // The Lipschitz form's width bound for one octave of the sampled-form check.
    private static double SnoiseWidthBound(double centre1, double reach) =>
        math.SNOISE_LIPSCHITZ * reach + Math.ScaleB(1.0, -14) + Math.ScaleB(reach, -12) + math.SNOISE_LIPSCHITZ * Math.ScaleB(centre1 + reach, -20);

    private static double Length3(double[] a, int first) => Math.Sqrt(a[first] * a[first] + a[first + 1] * a[first + 1] + a[first + 2] * a[first + 2]);

    private static string AssertSnoiseForm(JsonArray cases) =>
        AssertSampledForm(cases, "af_snoise", (_, p) => math.snoise(p),
            (_, a) => SnoiseWidthBound(Math.Abs(a[0]) + Math.Abs(a[1]) + Math.Abs(a[2]), Length3(a, 3) + a[6]));

    private static string AssertFbmForm(JsonArray cases) =>
        AssertSampledForm(cases, "af_fbm",
            (t, p) => math.fbm_grad(p, (int)Convert.ToUInt32(t[7], 16), (float)Floats(t[8])[0], (float)Floats(t[9])[0]).w,
            (t, a) =>
            {
                var (octaves, lacunarity, gain) = ((int)Convert.ToUInt32(t[7], 16), Floats(t[8])[0], Floats(t[9])[0]);
                double sum = 0.0, amplitude = 1.0, frequency = 1.0;
                for (var i = 0; i < Math.Clamp(octaves, 0, 16); i++)
                {
                    sum += amplitude * SnoiseWidthBound((Math.Abs(a[0]) + Math.Abs(a[1]) + Math.Abs(a[2])) * frequency, (Length3(a, 3) + a[6]) * frequency);
                    amplitude *= gain;
                    frequency *= lacunarity;
                }

                return sum;
            });

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

    private sealed record Family(string Glsl, string Tolerance, Func<System.Random, object[]> Arguments, string? Check = null, Platform Platform = Platform.None, Func<JsonArray, string>? Verify = null);

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();
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

    private const string AffineFrustumBall =
        "enclosure and tightness: from the arguments (mx, my, z0, z1, fp, warpVariation) compute in double zm = (z0 + z1) / 2, " +
        "h = (z1 - z0) / 2, axis = (mx h, my h, h), c0 = (mx zm, my zm, zm), |c0|_1 = |mx zm| + |my zm| + |zm|, R0 = z1 fp + warpVariation, " +
        "spread = (|mx| + |my| + 1) h and d = |GPU centre - c0|. Enclosure: for z in {z0, (3 z0 + z1) / 4, zm, (z0 + 3 z1) / 4, z1}, " +
        "eps = clamp((z - zm) / h, -1, 1) and the nine footprint offsets e in {(0, 0), (+-1, 0), (0, +-1), (+-1, +-1) / sqrt(2)}, " +
        "the point P = ((mx + fp e.x) z, (my + fp e.y) z, z) satisfies |P - (GPU centre + axis eps)| + warpVariation <= GPU radius. " +
        "Tightness: R0 + d <= GPU radius <= R0 + d + 2^-19 (R0 + |c0|_1 + spread). " +
        "Report the least slack of each side; only the bounds fail the check.";

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
        }, FrustumBall, Verify: AssertFrustumBall),
        new("vec2 cultmath_iv_exp(vec2)", "ulp-bounded, platform exp", r => new object[] { Interval(r) }, Platform: Platform.Ulp),
        new("CultPhasor cultmath_phacelle(vec3, vec3, float, float)", "ulp-bounded, platform exp, sin, cos; across platforms |diff| <= 2^-20 max(|v|, 1)",
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
        new("vec3 cultmath_af_snoise(vec3, vec3, float)", "ulp-bounded", r => new object[] { Point3(r), Axis3(r), BallRadius(r) }, SnoiseForm, Verify: AssertSnoiseForm),
        new("vec3 cultmath_af_fbm(vec3, vec3, float, int, float, float)", "ulp-bounded", r => new object[] { Point3(r), Axis3(r), BallRadius(r), 4, 2.0f, 0.5f }, FbmForm, Verify: AssertFbmForm),
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
