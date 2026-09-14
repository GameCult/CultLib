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

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f)]
    [InlineData(0.25f, -0.5f, 1.75f)]
    [InlineData(12.25f, -4.5f, 3.125f)]
    [InlineData(-7.3f, 2.9f, 101.4f)]
    public void CompiledShaderNoiseMatchesCSharpMirror(float x, float y, float z)
    {
        var (assembly, errors) = CompileShaderMirror();
        Assert.True(assembly is not null, string.Join(Environment.NewLine, errors));
        var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
        var shader = Activator.CreateInstance(shaderType);

        var snoise3 = (float)ShaderFunction(shaderType, "cultmath_snoise", typeof(float3))!.Invoke(shader, new object[] { float3(x, y, z) })!;
        var snoise2 = (float)ShaderFunction(shaderType, "cultmath_snoise", typeof(float2))!.Invoke(shader, new object[] { float2(x, y) })!;

        Assert.Equal(snoise(float3(x, y, z)), snoise3, precision: 6);
        Assert.Equal(snoise(float2(x, y)), snoise2, precision: 6);
    }

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
