using Xunit;

namespace CultMath.Tests;

public sealed class HlslMirrorTests
{
    [Fact]
    public void HlslMirrorDoesNotPublishGeometryKernels()
    {
        var source = HlslSourceCompatibilityTests.ReadShaderSource(GetCultMathRoot());
        Assert.DoesNotContain("AdvancedErosionFilter.hlsl", source);
        Assert.DoesNotContain("Planetary.hlsl", source);
        Assert.DoesNotContain("spherical_erosion", source);
        Assert.DoesNotContain("planetary_radial_refinement", source);
    }

    [Fact]
    public void HlslMirrorPublishesCultMathPrimitives()
    {
        var include = HlslSourceCompatibilityTests.ReadShaderSource(GetCultMathRoot());
        var requiredSymbols = new[]
        {
            "cultmath_radians",
            "cultmath_degrees",
            "cultmath_frac",
            "cultmath_clamp",
            "cultmath_saturate",
            "cultmath_lerp",
            "cultmath_step",
            "cultmath_smoothstep",
            "cultmath_smootherstep",
            "cultmath_lengthsq",
            "cultmath_distance",
            "cultmath_reflect",
            "cultmath_rotate",
            "cultmath_csum",
            "cultmath_decay",
            "cultmath_damp",
            "cultmath_catmullrom",
            "cultmath_quadratic_bezier",
            "cultmath_cubic_bezier",
            "cultmath_hash",
            "cultmath_pcg(uint",
            "cultmath_pcg3d(int3",
            "cultmath_pcg4d(int4",
            "cultmath_value_noise",
            "cultmath_snoise(float2",
            "cultmath_snoise(float3",
            "cultmath_value_noise_bicubic",
            "cultmath_value_noise_texture",
            "cultmath_value_noise_texture_bicubic",
            "cultmath_smin_grad",
            "cultmath_cellular",
        };

        foreach (var symbol in requiredSymbols)
        {
            Assert.Contains(symbol, include);
        }

        // HLSL's normalize intrinsic is the contract; a mirror copy would only restate it.
        Assert.DoesNotContain("cultmath_normalize", include);
    }

    [Theory]
    [InlineData("CultMath.hlsl")]
    [InlineData("CultMath.Phacelle.hlsl")]
    [InlineData("CultMath.Interval.hlsl")]
    public void UnityPackageShaderIsIdenticalToTheCanonicalShader(string name)
    {
        var root = GetCultMathRoot();
        Assert.Equal(
            File.ReadAllText(Path.Combine(root, "shaders", name)).ReplaceLineEndings("\n"),
            File.ReadAllText(Path.Combine(root, "unity", "org.gamecult.cultmath", "Shaders", name)).ReplaceLineEndings("\n"));
    }

    private static string GetCultMathRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var monorepoPackage = Path.Combine(directory.FullName, "packages", "cultmath");
            if (File.Exists(Path.Combine(monorepoPackage, "shaders", "CultMath.hlsl")))
            {
                return monorepoPackage;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the CultMath package root.");
    }
}
