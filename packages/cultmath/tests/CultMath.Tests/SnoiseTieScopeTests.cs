using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

// The 0.4.0 tie rule (math.snoise(float3), math.snoise_grad) changes values exactly where the cell
// offset x0 has three equal components, which is every input whose coordinate differences y - x and
// z - x are whole numbers (every integer point, and the whole (1, 1, 1) line through each). Off that
// set the results are bit-identical to 0.3.0. The goldens below were computed by the
// cultmath-unity-v0.3.0 tag's CultMath.dll (Runtime/Plugins), not by this code. Float32 rounding
// decides whether a given integer-difference input ties, because x0 is computed in float32: an
// input a few float32 ulps off the set can tie too and move (measured: none at a distance of 1e-5 or
// more from the set, a handful at 1e-6 near magnitude 30), and an input on the set may keep its
// value. The inputs below tie, and a tie moves the value or the gradient on most, not all, of them.
// TieSetNow pins what this code returns on the same inputs, so that each of the two tie-rule sites
// (the step in snoise, the step in snoise_grad) is pinned on its own.
public sealed class SnoiseTieScopeTests
{
    // (input, snoise bits, snoise_grad x y z w bits) from 0.3.0, each on the tie set and moved by the new rule.
    private static readonly (float3 P, uint Value, uint[] Grad)[] TieSet =
    {
        (new float3(8f, -9f, 1f), 0xBDD82D0D, new uint[] { 0x3FA7D14B, 0x40A68968, 0xC0EE5B16, 0xBDD82D0D }),
        (new float3(-15f, 1f, -3f), 0xBF29AEDB, new uint[] { 0x3ECABF0C, 0x40482713, 0x3F444FED, 0xBF29AEDB }),
        (new float3(9f, -14f, 7f), 0x3F3F28F4, new uint[] { 0x3F5FB228, 0x3F471A21, 0x40454A1F, 0x3F3F28F4 }),
        (new float3(15f, 8f, 17f), 0xBDEE4FE2, new uint[] { 0x3F1F3C06, 0x3FFA0CCF, 0xC00C8395, 0xBDEE4FE2 }),
        (new float3(3f, -12f, 10f), 0xBF3A1373, new uint[] { 0x3FC00B62, 0x3E28F20F, 0x403F02DA, 0xBF3A1373 }),
        (new float3(2f, -8f, 15f), 0x3F180CA2, new uint[] { 0x3F567351, 0xC0340E87, 0x40E74458, 0x3F180CA2 }),
        (new float3(-6.1645937f, -6.1645937f, -6.1645937f), 0xBF1C7CA6, new uint[] { 0xC04341A7, 0xBFE7241A, 0x3EE7511A, 0xBF1C7CA6 }),
        (new float3(22.580343f, 22.580343f, 22.580343f), 0x3F5D9075, new uint[] { 0x400096F0, 0xC0590215, 0x3FAAC79D, 0x3F5D9075 }),
        (new float3(-25.630196f, -25.630196f, -25.630196f), 0x3F4C7BDB, new uint[] { 0xBFCE54B7, 0x3FCD63EC, 0x403492AC, 0x3F4C7BDB }),
        (new float3(-26.818033f, -26.818033f, -26.818033f), 0x3DE34495, new uint[] { 0xBEF368D6, 0xBFD14FED, 0x3FE503E7, 0x3DE34495 }),
        (new float3(8.4097805f, -2.5902195f, -0.5902195f), 0xBF85F631, new uint[] { 0xBFE0D093, 0x3F8E6022, 0x3ECA4F57, 0xBF85F631 }),
        (new float3(8.618587f, 1.6185865f, 0.61858654f), 0xBE8FB513, new uint[] { 0xC02F9DDD, 0x4063CEA5, 0xBDF0BFBB, 0xBE8FB513 }),
        (new float3(-13.692368f, -9.692368f, -20.692368f), 0xBF28A1E3, new uint[] { 0xC012A224, 0xC0187F57, 0xBF57F2CB, 0xBF28A1E3 }),
        (new float3(9.495648f, 11.495648f, 15.495648f), 0x3E4FC9B5, new uint[] { 0x3EA26E55, 0x403CE620, 0xBF46F425, 0x3E4FC9B5 }),
        (new float3(-12.66157f, -19.66157f, -9.66157f), 0xBF179BF9, new uint[] { 0x3FD03786, 0x403A1C1E, 0xBEAD7581, 0xBF179BF9 }),
        (new float3(-8.801376f, -1.8013763f, -19.801376f), 0xBE82F2EE, new uint[] { 0xC0DCFC55, 0x405501DB, 0x401E6CF8, 0xBE82F2EE }),
    };

    // The values this code returns on TieSet's inputs, in the same order (the value, then the four
    // snoise_grad components).
    private static readonly (uint Value, uint[] Grad)[] TieSetNow =
    {
        (0xB37C25C6, new uint[] { 0x3F281943, 0x40521F92, 0xC0A8193D, 0xB37C25C6 }),
        (0xBF214750, new uint[] { 0xBD931F34, 0x40260272, 0x3E94E53D, 0xBF214750 }),
        (0x3F35B75A, new uint[] { 0x3EAD85CB, 0x3E78AB7B, 0x401F83B4, 0x3F35B75A }),
        (0xBDE33E78, new uint[] { 0x3F0705E4, 0x3FE9CB38, 0xC00DE5C4, 0xBDE33E78 }),
        (0xBF30DF87, new uint[] { 0x3F76B426, 0xBEAE857A, 0x401A3325, 0xBF30DF87 }),
        (0x00000000, new uint[] { 0x3FEC7798, 0xBF1DA4FF, 0x40C50E50, 0x00000000 }),
        (0xBF13F436, new uint[] { 0xC0219F00, 0xBFA829D9, 0x3F629666, 0xBF13F436 }),
        (0x3F0C1A1C, new uint[] { 0x404F76CC, 0xBFA6560B, 0x402B2291, 0x3F0C1A1C }),
        (0x3F2E5150, new uint[] { 0xC01F3830, 0x3F007F73, 0x3FD11DF1, 0x3F2E5150 }),
        (0x3DDE3B0E, new uint[] { 0xBED7A81C, 0xBFC89166, 0x3FE7E3CC, 0x3DDE3B0E }),
        (0xBF23BB4A, new uint[] { 0xC057D547, 0xBF7B66AE, 0xBFCA313F, 0xBF23BB4A }),
        (0xBE68D15B, new uint[] { 0xC0391D00, 0x40366C69, 0xBEFFC8AD, 0xBE68D15B }),
        (0xBF2659BE, new uint[] { 0xC0048291, 0xC00A5FC3, 0xBF21B992, 0xBF2659BE }),
        (0x3E4630E4, new uint[] { 0x3E35FE51, 0x40306ACC, 0xBF655794, 0x3E4630E4 }),
        (0xBF117B1D, new uint[] { 0x3F9D6F53, 0x401F35B7, 0xBF31BAFE, 0xBF117B1D }),
        (0xBDA5A39E, new uint[] { 0xC0B4E453, 0x3FF84A5D, 0x3FA74D5F, 0xBDA5A39E }),
    };

    // The same, off the tie set: coordinate differences that are not whole numbers.
    private static readonly (float3 P, uint Value, uint[] Grad)[] OffSet =
    {
        (new float3(-3.7841978f, -12.899288f, 1.5972604f), 0x3EA04649, new uint[] { 0xBF74444B, 0x3F87A8A7, 0xC021229C, 0x3EA04649 }),
        (new float3(8.898657f, 12.611845f, 4.790286f), 0xBEC97F79, new uint[] { 0xBD25992C, 0xC02EF328, 0xBF17ECAB, 0xBEC97F79 }),
        (new float3(0.7057237f, 11.097378f, 13.493794f), 0xBE00B70D, new uint[] { 0xBEDBFFF7, 0xBFDA79F7, 0xBF07EA57, 0xBE00B70D }),
        (new float3(-15.702414f, 3.5919724f, -7.0502086f), 0xBDC6607C, new uint[] { 0xBF113459, 0x3EB74352, 0xBF4D2C56, 0xBDC6607C }),
        (new float3(-13.623555f, -10.574037f, -15.697628f), 0x3E823717, new uint[] { 0xBF4926AE, 0x4076E757, 0x40710AFC, 0x3E823717 }),
        (new float3(-10.028356f, 5.4002185f, -13.579105f), 0x3EF1651D, new uint[] { 0x3FF0CA03, 0xC004B90C, 0xBFAF91DA, 0x3EF1651D }),
        (new float3(12.788626f, 14.914132f, -0.39256755f), 0x3DB39ABE, new uint[] { 0xC06D6427, 0xBF941974, 0x3E8B556D, 0x3DB39ABE }),
        (new float3(-2.4998372f, 10.696207f, 15.391607f), 0xBF0D1821, new uint[] { 0x3FAB00CF, 0x4044DAE4, 0x3F1E2E21, 0xBF0D1821 }),
        (new float3(10.437524f, -3.4692447f, 15.652484f), 0x3EFCC588, new uint[] { 0xC083735E, 0x3FA4A10F, 0xBE7D19D2, 0x3EFCC588 }),
        (new float3(-4.4040494f, 14.969201f, -2.2152362f), 0xBD4192BF, new uint[] { 0xBF3F69AE, 0xBF843396, 0xBFF88079, 0xBD4192BF }),
        (new float3(2.7095387f, 13.3163395f, -0.28273603f), 0x3EF0704A, new uint[] { 0xBF75C892, 0x3F0C95B0, 0x3F7401B5, 0x3EF0704A }),
        (new float3(-10.564334f, -5.6527104f, -6.6908674f), 0x3EA35AEA, new uint[] { 0xC05CB149, 0x3F928B08, 0x3EB68FCB, 0x3EA35AEA }),
        (new float3(-13.572381f, -2.2560256f, -12.702176f), 0x3D2013BE, new uint[] { 0xC0236256, 0x3D02D65C, 0x3DB57CBC, 0x3D2013BE }),
        (new float3(0.11813756f, -7.2100754f, 7.144552f), 0xBF244AB4, new uint[] { 0xBF6B4A5E, 0xBFFC1BFA, 0xBE868270, 0xBF244AB4 }),
        (new float3(9.668022f, -0.32686335f, 2.333893f), 0xBDE9567A, new uint[] { 0x3F359CBB, 0x3F2B104D, 0x3FEFAB81, 0xBDE9567A }),
        (new float3(-6.9417686f, -6.2886133f, -13.874224f), 0x3CC28786, new uint[] { 0xC0291AB6, 0x3E57B89E, 0xBE884AF7, 0x3CC28786 }),
        (new float3(9.9228945f, 13.366786f, 1.9626095f), 0x3DDCC7CA, new uint[] { 0xBFA57791, 0x40277CF0, 0xBF816FCF, 0x3DDCC7CA }),
        (new float3(1.3210233f, -5.5900016f, 9.691286f), 0x3D140957, new uint[] { 0x4003DBED, 0x3E35DCDD, 0xBF9CFAAE, 0x3D140957 }),
        (new float3(2.5023124f, 14.715639f, -7.5954947f), 0x3EB522F9, new uint[] { 0x3FB54F05, 0x4003D90A, 0x3F4CCE6D, 0x3EB522F9 }),
        (new float3(2.2056534f, 14.461132f, 0.081089176f), 0xBE0FBF83, new uint[] { 0xBF2E2FDB, 0x4014B7C5, 0x3F8C8EC2, 0xBE0FBF83 }),
        (new float3(5.137802f, -10.890227f, 12.128177f), 0xBEAEF52F, new uint[] { 0xBFBC5CA4, 0xBFD159C2, 0x402EBCD4, 0xBEAEF52F }),
        (new float3(-0.290969f, -2.4119518f, 6.2756324f), 0x3EE30F7E, new uint[] { 0x3E94AD2B, 0x3F8DCBA0, 0x4055B8BB, 0x3EE30F7E }),
        (new float3(9.232528f, 12.940779f, 11.078674f), 0x3E380DF3, new uint[] { 0x3F8B7DBE, 0x3F1596ED, 0xC0527328, 0x3E380DF3 }),
        (new float3(6.9932394f, -12.455975f, -8.343756f), 0x3E929F5A, new uint[] { 0x3E5564B6, 0x3FF1BE81, 0xBF77638A, 0x3E929F5A }),
        (new float3(15.123071f, 12.623071f, 20.12307f), 0x3E9527A3, new uint[] { 0x3D084582, 0xC07DE925, 0xBECA2112, 0x3E9527A3 }),
        (new float3(8.46288f, 11.96288f, 4.46288f), 0x3E8C0F92, new uint[] { 0x3FD2DFD3, 0x3FB49817, 0x3F023648, 0x3E8C0F92 }),
        (new float3(-2.9422863f, -1.4422863f, -12.9422865f), 0xBEBAEE39, new uint[] { 0x3FECB31B, 0xBFD33138, 0x3F9A17A8, 0xBEBAEE39 }),
        (new float3(13.526707f, 15.026707f, 2.5267067f), 0x3EF22701, new uint[] { 0x3F1D389B, 0x3FCABBBE, 0xBF66EE93, 0x3EF22701 }),
        (new float3(11.822154f, 1.322154f, 13.822154f), 0x3E85ACC7, new uint[] { 0x3FA7C0C7, 0xC067C262, 0x3FF35925, 0x3E85ACC7 }),
        (new float3(6.025978f, 8.525978f, 3.025978f), 0x3E9964B0, new uint[] { 0xBEE7F346, 0x3F1918E9, 0x3EF3DA8A, 0x3E9964B0 }),
        (new float3(-15.095373f, -14.595373f, -16.095373f), 0xBE6EF82D, new uint[] { 0xC01F9511, 0x3FEDB679, 0xBFDA5058, 0xBE6EF82D }),
        (new float3(3.2167344f, 12.716734f, -6.7832656f), 0x3EA3C041, new uint[] { 0xBFFA5E11, 0xBF41778D, 0xBFCFD96A, 0x3EA3C041 }),
        (new float3(12.25f, -0.25f, -3.25f), 0x3EB8C88C, new uint[] { 0xBFA0E2A2, 0x40101407, 0x402D2F5B, 0x3EB8C88C }),
        (new float3(-9f, -13.25f, 10f), 0x3F032C5D, new uint[] { 0x3F9FFBCB, 0x3E6ED505, 0x401D75F4, 0x3F032C5D }),
        (new float3(-5.75f, 2.25f, 13.75f), 0x3E62496B, new uint[] { 0x3FC789CA, 0x3EE63ACD, 0xBEE57812, 0x3E62496B }),
        (new float3(-12.5f, 10f, -5.25f), 0x3EEA194F, new uint[] { 0x3F182206, 0xC0282BDE, 0xBFA51540, 0x3EEA194F }),
        (new float3(13.5f, -11f, -3f), 0x3E6B5E9A, new uint[] { 0xBF2F5213, 0x3FE97BC2, 0x3F072FB4, 0x3E6B5E9A }),
        (new float3(-2.75f, 7.75f, 3.5f), 0x3E91F33E, new uint[] { 0x3E2546E0, 0xBF63D038, 0xBFC3C500, 0x3E91F33E }),
        (new float3(10.5f, 4f, -3.75f), 0x3EBCDE1F, new uint[] { 0xBFDC4426, 0x400DCD14, 0x3F7CACF6, 0x3EBCDE1F }),
        (new float3(-12.75f, -13f, -6f), 0x3D5F6FB0, new uint[] { 0xBFBF0ED8, 0xC0109EED, 0xC007C568, 0x3D5F6FB0 }),
        (new float3(-0.069121994f, 0.76107657f, 0.41525742f), 0x3DA72363, new uint[] { 0x3E5DF1FF, 0x3F7E17BE, 0x3FA89CB3, 0x3DA72363 }),
        (new float3(1.0466753f, -0.51170796f, 0.21126905f), 0xBDD1FE81, new uint[] { 0x3DFD066D, 0x3E5E0061, 0x3FDBADAF, 0xBDD1FE81 }),
        (new float3(0.30550694f, 1.6808131f, -0.76585627f), 0x3F0F3AB5, new uint[] { 0x3FF9EAC8, 0xC001ADBF, 0x3FD97B97, 0x3F0F3AB5 }),
        (new float3(-0.8827523f, 0.12493165f, -1.8058014f), 0x3EC3BE7E, new uint[] { 0xC02395D6, 0x3F00EEB9, 0x3F976926, 0x3EC3BE7E }),
    };

    private static bool OnTieSet(float3 p)
    {
        var a = (double)p.y - p.x;
        var b = (double)p.z - p.x;
        return a == Math.Floor(a) && b == Math.Floor(b);
    }

    // Each output is asserted on its own: a tie rule reverted at one site must fail here by itself,
    // whatever the other site returns. A given input moves some outputs and not others, so each
    // output must differ from 0.3.0 on at least one tie-set input.
    [Fact]
    public void EachOutputDiffersFromZeroPointThreeSomewhereOnTheTieSet()
    {
        Assert.NotEmpty(TieSet);
        var moved = new bool[5];
        foreach (var (p, value, grad) in TieSet)
        {
            Assert.True(OnTieSet(p));
            var g = math.snoise_grad(p);
            moved[0] |= BitConverter.SingleToUInt32Bits(math.snoise(p)) != value;
            moved[1] |= BitConverter.SingleToUInt32Bits(g.x) != grad[0];
            moved[2] |= BitConverter.SingleToUInt32Bits(g.y) != grad[1];
            moved[3] |= BitConverter.SingleToUInt32Bits(g.z) != grad[2];
            moved[4] |= BitConverter.SingleToUInt32Bits(g.w) != grad[3];
        }
        Assert.True(moved[0], "snoise(float3) still reads its 0.3.0 value on the tie set");
        Assert.True(moved[1], "snoise_grad.x still reads its 0.3.0 value on the tie set");
        Assert.True(moved[2], "snoise_grad.y still reads its 0.3.0 value on the tie set");
        Assert.True(moved[3], "snoise_grad.z still reads its 0.3.0 value on the tie set");
        Assert.True(moved[4], "snoise_grad.w still reads its 0.3.0 value on the tie set");
    }

    [Fact]
    public void TieSetInputsReturnTheirZeroPointFourGoldens()
    {
        Assert.Equal(TieSet.Length, TieSetNow.Length);
        for (var k = 0; k < TieSet.Length; k++)
        {
            var p = TieSet[k].P;
            var (value, grad) = TieSetNow[k];
            var g = math.snoise_grad(p);
            Assert.Equal(value, BitConverter.SingleToUInt32Bits(math.snoise(p)));
            Assert.Equal(grad[0], BitConverter.SingleToUInt32Bits(g.x));
            Assert.Equal(grad[1], BitConverter.SingleToUInt32Bits(g.y));
            Assert.Equal(grad[2], BitConverter.SingleToUInt32Bits(g.z));
            Assert.Equal(grad[3], BitConverter.SingleToUInt32Bits(g.w));
        }
    }

    [Fact]
    public void InputsOffTheTieSetKeepTheirZeroPointThreeBits()
    {
        Assert.NotEmpty(OffSet);
        foreach (var (p, value, grad) in OffSet)
        {
            Assert.False(OnTieSet(p));
            var g = math.snoise_grad(p);
            Assert.Equal(value, BitConverter.SingleToUInt32Bits(math.snoise(p)));
            Assert.Equal(grad[0], BitConverter.SingleToUInt32Bits(g.x));
            Assert.Equal(grad[1], BitConverter.SingleToUInt32Bits(g.y));
            Assert.Equal(grad[2], BitConverter.SingleToUInt32Bits(g.z));
            Assert.Equal(grad[3], BitConverter.SingleToUInt32Bits(g.w));
        }
    }
}
