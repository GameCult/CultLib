using Xunit;

namespace CultMath.Tests;

public sealed class HlslMirrorTests
{
    [Fact]
    public void HlslMirrorPublishesCultMathPrimitives()
    {
        var include = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "shaders", "CultMath.hlsl"));
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
            "cultmath_normalize",
            "cultmath_reflect",
            "cultmath_csum",
            "cultmath_decay",
            "cultmath_damp",
            "cultmath_catmullrom",
            "cultmath_quadratic_bezier",
            "cultmath_cubic_bezier",
            "cultmath_hash",
            "cultmath_value_noise",
            "cultmath_value_noise_bicubic",
            "cultmath_value_noise_texture",
            "cultmath_value_noise_texture_bicubic",
        };

        foreach (var symbol in requiredSymbols)
        {
            Assert.Contains(symbol, include);
        }

        Assert.Contains("CULTMATH_NORMALIZE_EPSILON", include);
        Assert.Contains("max(length(value), CULTMATH_NORMALIZE_EPSILON)", include);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CultMath.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate CultMath repository root.");
    }
}
