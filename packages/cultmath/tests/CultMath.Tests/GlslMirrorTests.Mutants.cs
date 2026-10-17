using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace CultMath.Tests;

public sealed partial class GlslMirrorTests
{
    private const string Weighted = "cultmath_weighted_lattice_cells(";
    private const string SmoothStep = "cultmath_lattice_smooth_step(";

    // Edits of the shader text that each break one rule of the lattice loop. M7 (the exit one ring late) is absent: by the
    // window-independence proof a later exit visits more cells and changes nothing, so no case can kill it.
    private static readonly (string Name, string Function, string Old, string New, int Kills)[] LatticeMutants =
    {
        ("M1 exit one ring early", Weighted, "0.5 * (float)(k - 1) >= max(f2", "0.5 * (float)k >= max(f2", 3),
        ("M2 exit ignores the smoothing band", Weighted, ">= max(f2, f1 + 2.0 * smoothing))", ">= f2)", 1),
        ("M3 no pre-normalisation of the normal", Weighted, "normal = stretchNormal / scale;", "normal = stretchNormal;", 1),
        ("M4 no unit length for the normal", Weighted, "normal = normal / length(normal);", "normal = normal * 1.0;", 1),
        ("M5 smooth step without its clamp", SmoothStep, "return min(next, f1 + smoothing);", "return next;", 1),
        ("M6 box pass skips its first row", Weighted, "for (int dz = -reach; dz <= reach; dz++)", "for (int dz = -reach + 1; dz <= reach; dz++)", 1),
    };

    /// <summary>
    /// Each rule of the lattice loop is pinned by the committed fixture's own cases: the shader text with one rule broken (a
    /// mutant mirror, the text a device runs) returns another result than the fixture on at least the stated number of
    /// cases of the family that exercises it, the exit one ring early on at least three. The device judge holds the same
    /// cases to their recorded bound, so a mutant these cases separate fails it too.
    /// </summary>
    [Fact]
    public void TheFixtureCasesSeparateEveryLatticeMutantFromTheShader()
    {
        var families = FixtureFamilies();
        foreach (var (name, function, old, replacement, kills) in LatticeMutants)
        {
            var (assembly, errors) = HlslSourceCompatibilityTests.CompileShaderMirror(mutate: text =>
            {
                Assert.True(text.Split(old).Length == 2, $"{name}: the shader text no longer holds exactly one \"{old}\"");
                return text.Replace(old, replacement);
            });
            Assert.True(assembly is not null, name + ": " + string.Join(Environment.NewLine, errors));
            var shaderType = assembly!.GetType("CultMathHlsl.HlslShader")!;
            var shader = Activator.CreateInstance(shaderType);
            var mirror = shaderType.GetMethod(function[..^1], BindingFlags.Instance | BindingFlags.NonPublic)!;

            var f = Array.FindIndex(Families, family => family.Glsl.Contains(function));
            var method = Resolve(Families[f].Glsl);
            var killed = 0;
            foreach (var entry in families[f]!["cases"]!.AsArray())
            {
                var sides = Sides(entry);
                if (Hex(new[] { mirror.Invoke(shader, Arguments(method, sides[0]))! }) != sides[1])
                    killed++;
            }

            Assert.True(killed >= kills, $"{name}: the {Families[f].Glsl} fixture cases separate it on {killed} of {Points}, fewer than {kills}");
        }
    }
}
