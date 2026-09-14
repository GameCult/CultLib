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
    }

    // HLSL functions carry no access modifier, so they compile as private instance methods.
    private static MethodInfo? ShaderFunction(Type shader, string name, params Type[] parameters) =>
        shader.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null);

    // Mirror internals with no public C# math counterpart. Any other cultmath_* function must match one.
    private static readonly string[] MirrorOnly = { "cultmath_snoise_mod289", "cultmath_snoise_permute" };

    private static readonly float[] Specials = { 0.0f, -0.0f, 1.0f, -1.0f, 0.5f, -2.75f, 3.0f, 1.0e-7f, 1.0e4f, -1.0e4f, 1.0e7f, float.NaN };

    /// <summary>
    /// Every mirror function with a C# math counterpart returns the same bits (any NaN equals any NaN;
    /// -0 differs from 0) over random inputs, every special value in every argument (ties, zeros,
    /// negatives, NaN, large values, integer lattice points), special pairs, and vectors whose first two
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

        var mismatches = new List<string>();
        var compared = new HashSet<string>();
        foreach (var mirror in shaderType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name.StartsWith("cultmath_")))
        {
            var types = mirror.GetParameters().Select(p => p.ParameterType).ToArray();
            var counterpart = typeof(math).GetMethod(mirror.Name["cultmath_".Length..], BindingFlags.Public | BindingFlags.Static, null, types, null);
            if (counterpart is null)
            {
                Assert.Contains(mirror.Name, MirrorOnly);
                continue;
            }

            compared.Add(mirror.Name);
            foreach (var input in cases)
            {
                var slot = 0;
                var args = types.Select(t => t == typeof(float) ? (object)input(slot++)
                    : Activator.CreateInstance(t, Enumerable.Range(0, Components(t)).Select(_ => (object)input(slot++)).ToArray())!).ToArray();
                var expected = Floats(counterpart.Invoke(null, args)!);
                var actual = Floats(mirror.Invoke(shader, args)!);
                if (expected.Length != actual.Length || expected.Zip(actual).Any(p => !(float.IsNaN(p.First) && float.IsNaN(p.Second))
                    && BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second)))
                {
                    mismatches.Add($"{mirror.Name}({string.Join(", ", args.Select(a => string.Join(" ", Floats(a))))}): C# {string.Join(" ", expected)}, mirror {string.Join(" ", actual)}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(12))}");
        Assert.Equal(25, compared.Count);
    }

    private static int Components(Type t) => t.GetFields().Count(f => f.FieldType == typeof(float) && !f.IsStatic);

    private static float[] Floats(object value) => value is float f ? new[] { f }
        : value.GetType().GetFields().Where(field => field.FieldType == typeof(float) && !field.IsStatic).Select(field => (float)field.GetValue(value)!).ToArray();

    /// <summary>The documented HLSL-to-C# transformations, and nothing else.</summary>
    internal static string TransformHlslToCSharp(string hlsl)
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

        // 5. The file body is wrapped in a class; C# has no free functions.
        return "using CultMath;\nusing static CultMath.math;\nnamespace CultMathHlsl;\npublic class HlslShader\n{\n" + source + "\n}\n";
    }

    private static (Assembly? Assembly, IReadOnlyList<string> Errors) CompileShaderMirror()
    {
        var hlsl = File.ReadAllText(Path.Combine(FindCultMathRoot(), "shaders", "CultMath.hlsl"));
        var tree = CSharpSyntaxTree.ParseText(TransformHlslToCSharp(hlsl));
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

    private static string FindCultMathRoot()
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
