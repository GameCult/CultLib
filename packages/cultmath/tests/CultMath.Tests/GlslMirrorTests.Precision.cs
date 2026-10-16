using System.Text.RegularExpressions;
using Xunit;

namespace CultMath.Tests;

public sealed partial class GlslMirrorTests
{
    // Tripwire: lavapipe and desktop ANGLE ignore mediump and min16float, so a reduced-precision qualifier in the
    // committed shaders would change the computation on a phone GPU and nowhere the conforming run can see it.
    // Stated limits: a syntax guardrail only. It reads whole words in the committed .glsl and .hlsl under shaders/
    // with comments removed; it does not see a qualifier spelled through a macro, an include or a generated
    // shader, and it says nothing about a driver that relaxes highp itself. It names file and line, never text.
    private static readonly Regex ReducedPrecision = new(
        @"\b(mediump|lowp|min(10|12|16)(float|int|uint)[1-4]?(x[1-4])?|half[1-4]?(x[1-4])?)\b|\bprecision\s+(?!highp\b)\w+", RegexOptions.Compiled);

    private static IEnumerable<int> ReducedPrecisionLines(string source)
    {
        var text = Regex.Replace(source, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var code = lines[i].Split("//")[0];
            if (ReducedPrecision.IsMatch(code))
                yield return i + 1;
        }
    }

    [Fact]
    public void CommittedShadersCarryNoReducedPrecisionQualifier()
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "shaders")).Where(f => f.EndsWith(".glsl") || f.EndsWith(".hlsl")).OrderBy(f => f, StringComparer.Ordinal))
            found.AddRange(ReducedPrecisionLines(File.ReadAllText(file)).Select(line => $"{Path.GetFileName(file)}:{line}"));
        Assert.True(found.Count == 0, "reduced-precision qualifier at " + string.Join(", ", found));
    }

    [Theory]
    [InlineData("mediump float x;", true)]
    [InlineData("lowp vec3 c;", true)]
    [InlineData("precision mediump float;", true)]
    [InlineData("precision highp float;", false)]
    [InlineData("min16float x;", true)]
    [InlineData("min10float2 x;", true)]
    [InlineData("min16int i;", true)]
    [InlineData("half3x3 m;", true)]
    [InlineData("float half_width;", false)]
    [InlineData("float x; // mediump is not used", false)]
    [InlineData("/* half\n min16float */ float x;", false)]
    public void TheTripwireReadsQualifiersAndNotComments(string source, bool trips) => Assert.Equal(trips, ReducedPrecisionLines(source).Any());

    [Fact]
    public void TheTripwireNamesTheLine()
    {
        Assert.Equal(new[] { 3 }, ReducedPrecisionLines("float a;\n/* c */\nmediump float b;\n").ToArray());
    }
}
