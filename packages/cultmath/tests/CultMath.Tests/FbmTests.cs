using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class FbmTests
{
    private static float3 Point(System.Random r) => new(r.NextSingle() * 20.0f - 10.0f, r.NextSingle() * 20.0f - 10.0f, r.NextSingle() * 20.0f - 10.0f);

    [Fact]
    public void OneOctaveIsSnoise()
    {
        var random = new System.Random(0xFB01);
        for (var i = 0; i < 64; i++)
        {
            var p = Point(random);
            Assert.Equal(math.snoise(p), math.fbm(p, 1, 1.0f + random.NextSingle() * 3.0f, 0.1f + random.NextSingle() * 0.8f));
        }
    }

    [Fact]
    public void ZeroOrNegativeOctavesSumNothing()
    {
        var p = new float3(0.3f, 1.7f, -2.2f);
        Assert.Equal(0.0f, math.fbm(p, 0, 2.0f, 0.5f));
        Assert.Equal(0.0f, math.fbm(p, -5, 2.0f, 0.5f));
    }

    [Fact]
    public void OctavesCompoundGainAndLacunarityIndependently()
    {
        // gain != lacunarity and neither is 1, so applying either once, or swapping them, changes the sum.
        const float lacunarity = 2.3f;
        const float gain = 0.4f;
        var random = new System.Random(0xFB02);
        for (var i = 0; i < 64; i++)
        {
            var p = Point(random);
            var expected = math.snoise(p) + gain * math.snoise(p * lacunarity) + gain * gain * math.snoise(p * (lacunarity * lacunarity));
            Assert.Equal(expected, math.fbm(p, 3, lacunarity, gain), 5);
        }
    }

    [Fact]
    public void ValueIsFbmGradsValue()
    {
        var random = new System.Random(0xFB03);
        for (var i = 0; i < 128; i++)
        {
            var p = Point(random);
            Assert.Equal(math.fbm_grad(p, 5, 2.0f, 0.55f).w, math.fbm(p, 5, 2.0f, 0.55f), 4);
        }
    }

    [Fact]
    public void OctaveCountClampsToSixteenLikeFbmGrad()
    {
        var p = new float3(0.3f, 1.7f, -2.2f);
        Assert.Equal(math.fbm(p, 16, 1.1f, 0.9f), math.fbm(p, 1000, 1.1f, 0.9f));
    }

    [Fact]
    public void OneArgumentFormIsFourOctavesLacunarityTwoGainHalf()
    {
        var random = new System.Random(0xFB04);
        for (var i = 0; i < 64; i++)
        {
            var p = Point(random);
            Assert.Equal(math.fbm(p, 4, 2.0f, 0.5f), math.fbm(p));
        }
    }
}
