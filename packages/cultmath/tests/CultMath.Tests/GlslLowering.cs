using System.Text;
using System.Text.RegularExpressions;

namespace CultMath.Tests;

/// <summary>
/// The HLSL to GLSL ES 3.00 transformation that produces <c>shaders/CultMath.glsl</c>, and one file per
/// <see cref="SeparateFiles"/> entry, from <c>shaders/CultMath.hlsl</c> and its includes. The steps below,
/// in order, are the complete list documented in docs/design.md (GLSL Target: Source Transformations).
/// A dialect gap outside the list is fixed in the HLSL, in the subset both languages share, never by a
/// special case here; GlslMirrorTests fails on any HLSL-only token that survives.
/// </summary>
internal static class GlslLowering
{
    /// <summary>
    /// Includes under a file-level licence of their own: each is lowered into its own output file instead
    /// of being inlined, so CultMath.glsl stays MIT. This table is the lowering's only rule keyed by name.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Glsl, string Licence)> SeparateFiles =
        new Dictionary<string, (string, string)>
        {
            // Phacelle is a port of MPL-2.0 code, and MPL-2.0 covers the files that carry it.
            ["CultMath.Phacelle.hlsl"] = ("CultMath.Phacelle.glsl", "MPL-2.0"),
        };

    private const string Regenerate =
        "by GlslLowering (packages/cultmath/tests/CultMath.Tests/GlslLowering.cs). Do not edit; regenerate with " +
        "CULTMATH_WRITE_GLSL=1 dotnet test --filter GlslMirrorTests.";

    /// <summary>
    /// The lowered files as (name under shaders/, text): CultMath.glsl first, then one per
    /// <see cref="SeparateFiles"/> entry, which requires CultMath.glsl before it.
    /// </summary>
    internal static IReadOnlyList<(string File, string Text)> Lower(string cultMathRoot)
    {
        // 1. Each #include is inlined in place by the C# mirror's own resolver, except a SeparateFiles
        //    include, which leaves nothing behind and is lowered into its own file.
        var inlined = new List<string>();
        var library = HlslSourceCompatibilityTests.ReadShaderSource(cultMathRoot, name =>
        {
            if (SeparateFiles.ContainsKey(name))
                return false;
            inlined.Add(name);
            return true;
        });

        var outputs = new List<(string File, string Text)>();
        var pointers = new List<string>();
        foreach (var (hlsl, (glsl, licence)) in SeparateFiles)
        {
            var text = LowerText(
                File.ReadAllText(Path.Combine(cultMathRoot, "shaders", hlsl)),
                $"// Generated from shaders/{hlsl} {Regenerate} {licence}; requires CultMath.glsl before it.",
                glsl);
            var functions = Regex.Matches(text, @"(?m)^\w+\s+(cultmath_\w+)\s*\(").Select(m => m.Groups[1].Value);
            pointers.Add($" {string.Join(", ", functions)} is in {glsl}, to be concatenated after this file by consumers that call it.");
            outputs.Add((glsl, text));
        }

        var includes = string.Concat(inlined.Select(name => " and " + name));
        outputs.Insert(0, ("CultMath.glsl", LowerText(
            library, $"// Generated from shaders/CultMath.hlsl{includes} {Regenerate} MIT.{string.Concat(pointers)}", "CultMath.glsl")));
        return outputs;
    }

    private static string LowerText(string hlsl, string provenance, string file)
    {
        var source = hlsl.ReplaceLineEndings("\n");

        // 2. Include guards are dropped, and so are the functions taking Texture2D/SamplerState, exactly
        //    as the C# mirror drops them; the host shader samples its own textures.
        source = Regex.Replace(source, @"(?m)^[ \t]*#(ifndef|define|endif)\b.*$", string.Empty);
        source = Regex.Replace(source, @"(?ms)^\w+ \w+\([^)]*\b(Texture2D|SamplerState)\b[^)]*\)\s*\{.*?^\}[ \t]*$", string.Empty);

        // 3. Type names: floatN, intN, uintN and boolN become vecN, ivecN, uvecN and bvecN.
        source = Regex.Replace(source, @"\bfloat([234])\b", "vec$1");
        source = Regex.Replace(source, @"\bint([234])\b", "ivec$1");
        source = Regex.Replace(source, @"\buint([234])\b", "uvec$1");
        source = Regex.Replace(source, @"\bbool([234])\b", "bvec$1");

        // 4. File-scope `static const` becomes `const`.
        source = Regex.Replace(source, @"(?m)^static const ", "const ");

        // 5. C-style casts (float)x, (int)x and (uint)x become constructor calls float(x), int(x) and
        //    uint(x). The operand is found by a balanced scanner, not a regex: (int)((state >> 28) + 4u)
        //    nests parentheses.
        source = LowerCasts(source);

        // 6. Intrinsic renames: lerp -> mix, frac -> fract, rsqrt -> inversesqrt, asuint ->
        //    floatBitsToUint, asfloat -> uintBitsToFloat, and saturate(x) -> clamp(x, 0.0, 1.0).
        //    Identifier boundaries keep cultmath_lerp and the rest of the cultmath_ names intact.
        source = Regex.Replace(source, @"\blerp\(", "mix(");
        source = Regex.Replace(source, @"\bfrac\(", "fract(");
        source = Regex.Replace(source, @"\brsqrt\(", "inversesqrt(");
        source = Regex.Replace(source, @"\basuint\(", "floatBitsToUint(");
        source = Regex.Replace(source, @"\basfloat\(", "uintBitsToFloat(");
        source = LowerSaturate(source);

        // 7. Float literals pass through: HLSL's unsuffixed 0.5 and 1.0e30 are GLSL literals already.

        // 8. The guard named for the file (CultMath.glsl -> CULTMATH_GLSL), and 9. the provenance line.
        var guard = file.ToUpperInvariant().Replace('.', '_');
        return provenance + "\n#ifndef " + guard + "\n#define " + guard + "\n" + source.Trim('\n') + "\n\n#endif\n";
    }

    private static readonly Regex Cast = new(@"\((float|int|uint)\)");

    private static string LowerCasts(string source)
    {
        for (var match = Cast.Match(source); match.Success; match = Cast.Match(source))
        {
            var start = match.Index + match.Length;
            var end = OperandEnd(source, start);
            // A parenthesized operand lends its own parentheses to the constructor call.
            var operand = source[start] == '(' && Balanced(source, start) == end ? source[(start + 1)..(end - 1)] : source[start..end];
            source = source[..match.Index] + match.Groups[1].Value + "(" + operand + ")" + source[end..];
        }

        return source;
    }

    // The cast operand: a parenthesized group, or an identifier or number followed by any member
    // accesses, calls and indexing. Anything else is outside the list and throws.
    private static int OperandEnd(string source, int index)
    {
        if (index < source.Length && source[index] == '(')
            return Balanced(source, index);
        var token = Regex.Match(source[index..], @"^[A-Za-z_0-9]+");
        if (!token.Success)
            throw new InvalidOperationException($"cast operand outside the lowering list at: {source.Substring(index, Math.Min(40, source.Length - index))}");
        index += token.Length;
        while (index < source.Length)
        {
            if (source[index] == '(' || source[index] == '[')
                index = Balanced(source, index);
            else if (source[index] == '.' && index + 1 < source.Length && (char.IsLetter(source[index + 1]) || source[index + 1] == '_'))
                index += 1 + Regex.Match(source[(index + 1)..], @"^[A-Za-z_0-9]+").Length;
            else
                break;
        }

        return index;
    }

    // The index one past the bracket that closes the one at `open`.
    private static int Balanced(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] is '(' or '[')
                depth++;
            else if (source[i] is ')' or ']' && --depth == 0)
                return i + 1;
        }

        throw new InvalidOperationException($"unbalanced bracket at {open}");
    }

    private static string LowerSaturate(string source)
    {
        var saturate = new Regex(@"\bsaturate\(");
        for (var match = saturate.Match(source); match.Success; match = saturate.Match(source))
        {
            var open = match.Index + match.Length - 1;
            var close = Balanced(source, open);
            var argument = source[(open + 1)..(close - 1)];
            source = source[..match.Index] + "clamp(" + argument + ", 0.0, 1.0)" + source[close..];
        }

        return source;
    }
}
