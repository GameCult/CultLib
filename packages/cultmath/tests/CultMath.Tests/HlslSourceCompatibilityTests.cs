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
        Assert.NotNull(ShaderFunction(shader, "cultmath_snoise_grad", typeof(float3)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_fbm_grad", typeof(float3), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_ridged_grad", typeof(float3), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_phacelle", typeof(float3), typeof(float3), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_snoise_ball", typeof(float3), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_fbm_ball", typeof(float3), typeof(float), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_iv_snoise_ball", typeof(float2), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_snoise", typeof(float3), typeof(float3), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_fbm", typeof(float3), typeof(float3), typeof(float), typeof(int), typeof(float), typeof(float)));
        Assert.NotNull(ShaderFunction(shader, "cultmath_af_frustum_ball", typeof(float2), typeof(float), typeof(float), typeof(float), typeof(float)));
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
                var args = types.Select(t => Scalars.Contains(t) ? Scalar(t, input(slot++))
                    : Activator.CreateInstance(t, Components(t).Select(f => Scalar(f.FieldType, input(slot++))).ToArray())!).ToArray();
                var expected = Values(counterpart.Invoke(null, args)!);
                var actual = Values(mirror.Invoke(shader, args)!);
                if (!BitwiseEqual(expected, actual))
                {
                    mismatches.Add($"{mirror.Name}({string.Join(", ", args.Select(a => string.Join(" ", Values(a).Select(v => v.Value))))}): C# {string.Join(" ", expected.Select(v => v.Value))}, mirror {string.Join(" ", actual.Select(v => v.Value))}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(12))}");
        Assert.Equal(67, compared.Count);
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
    private static readonly Type[] Scalars = { typeof(float), typeof(int), typeof(uint) };

    private static object Scalar(Type t, float f) => t == typeof(float) ? f
        : t == typeof(int) ? BitConverter.SingleToInt32Bits(f) : (object)(uint)BitConverter.SingleToInt32Bits(f);

    // Scoped to scalar-typed fields: this only ever builds function ARGUMENTS (float2/3/4, int2/3/4, ...),
    // never a struct-of-struct return, so it must not recurse.
    private static FieldInfo[] Components(Type t) => t.GetFields().Where(f => !f.IsStatic && Scalars.Contains(f.FieldType)).ToArray();

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

        // 5. Struct fields gain `public`; HLSL has no field access-modifier concept, and a C# struct's
        //    fields default to private, which would hide them from the mirror test's reflection-based
        //    field comparison (invariant 8's one struct return shape, e.g. CultCellular).
        source = Regex.Replace(source, @"(?ms)(struct\s+\w+\s*\{)(.*?)(\};)", m =>
            m.Groups[1].Value + Regex.Replace(m.Groups[2].Value, @"(?m)^(\s*)(\w+\s+\w+;)", "$1public $2") + m.Groups[3].Value);

        // 6. The file body is wrapped in a class; C# has no free functions.
        return "using CultMath;\nusing static CultMath.math;\nnamespace CultMathHlsl;\npublic class HlslShader\n{\n" + source + "\n}\n";
    }

    private static (Assembly? Assembly, IReadOnlyList<string> Errors) CompileShaderMirror()
    {
        var hlsl = ReadShaderSource(FindCultMathRoot());
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
