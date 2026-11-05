using System;
using System.Linq;
using CultMath;
using Xunit;

namespace CultMath.Tests;

/// <summary>
/// A ring-buffer terrain (Voxel Space) fills height rows in whatever order the camera uncovers them, so a
/// Phacelle row must be a pure function of world x, z and the seed: the same row, bit for bit, whether the
/// rows around it were generated forward, backward or shuffled. C# phacelle mirrors cultmath_phacelle in
/// shaders/CultMath.Phacelle.glsl; GlslMirrorTests pins the two together, and this pins that neither keeps
/// state between calls.
/// </summary>
public sealed class PhacelleRowOrderTests
{
    private const int Rows = 24;
    private const int Columns = 48;
    private const int Seed = 7919;

    // One height sample: world (x, z) and the seed alone choose the point, the stripe direction and the phase.
    private static int[] Sample(int worldX, int worldZ, int seed)
    {
        var p = new float3(worldX * 0.173f, (seed & 0xff) * 0.37f, worldZ * 0.173f);
        var angle = (seed >> 8) * 0.61f;
        var side = new float3(math.cos(angle), 0.0f, math.sin(angle)) * (math.TAU * 0.9f);
        var phasor = math.phacelle(p, side, (seed & 0xf) / 16.0f, 0.5f);
        return new[]
        {
            Bits(phasor.cosine.x), Bits(phasor.cosine.y), Bits(phasor.cosine.z), Bits(phasor.cosine.w),
            Bits(phasor.sine.x), Bits(phasor.sine.y), Bits(phasor.sine.z), Bits(phasor.sine.w),
        };
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static int[][] Row(int worldZ, int seed) =>
        Enumerable.Range(-Columns / 2, Columns).Select(x => Sample(x, worldZ, seed)).ToArray();

    private static int[][][] Generate(int[] order, int seed)
    {
        var rows = new int[Rows][][];
        foreach (var z in order) rows[z] = Row(z - Rows / 2, seed);
        return rows;
    }

    private static void AssertSameRows(int[][][] expected, int[][][] actual)
    {
        for (var z = 0; z < Rows; z++)
        for (var x = 0; x < Columns; x++)
            Assert.True(expected[z][x].SequenceEqual(actual[z][x]), $"row {z} column {x} differs bit for bit");
    }

    [Fact]
    public void RowsAreBitIdenticalForwardBackwardAndShuffled()
    {
        var forward = Enumerable.Range(0, Rows).ToArray();
        var backward = forward.Reverse().ToArray();
        var shuffled = (int[])forward.Clone();
        var random = new System.Random(20261104);
        for (var i = shuffled.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        Assert.False(shuffled.SequenceEqual(forward) || shuffled.SequenceEqual(backward));
        var reference = Generate(forward, Seed);
        AssertSameRows(reference, Generate(backward, Seed));
        AssertSameRows(reference, Generate(shuffled, Seed));
    }

    [Fact]
    public void ARowIsTheSameWhenRegeneratedAfterAnyOtherSeedAndRow()
    {
        var first = Row(5, Seed);
        _ = Row(-3, Seed + 1);
        _ = Row(5, Seed + 1);
        var again = Row(5, Seed);
        for (var x = 0; x < Columns; x++) Assert.True(first[x].SequenceEqual(again[x]));
    }

    // Guards the test itself: a different seed or a different row must change the bits, or the equalities
    // above would hold for a function that ignores its inputs.
    [Fact]
    public void RowsDependOnSeedAndWorldRow()
    {
        Assert.False(Row(5, Seed).SelectMany(s => s).SequenceEqual(Row(5, Seed + 1).SelectMany(s => s)));
        Assert.False(Row(5, Seed).SelectMany(s => s).SequenceEqual(Row(6, Seed).SelectMany(s => s)));
    }
}
