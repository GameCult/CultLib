// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Phacelle noise. Copyright (c) 2025 Rune Skovbo Johansen (Shadertoy t3dyWl; MPL-2.0), read from the
// C# port PhacelleNoise in lpmitchell/AdvancedTerrainErosion, converted to burstable C# by Luke
// Mitchell (2026). Modified by GameCult: 3D cells, a caller-supplied stripe wave vector, an exact
// gradient and exact pruning. This file and CultPhasor.cs are the MPL-2.0 files of CultMath; the
// rest of the package is MIT (see THIRD-PARTY-NOTICES.md).

namespace CultMath;

public static partial class math
{
    // Johansen's Phacelle noise (phase + cell), generalised to 3D cells with a caller-supplied
    // stripe wave vector. Each of the 4x4x4 cells around p carries one jittered feature point
    // (jitter in [-0.5, 0.5) per axis, from pcg3d over the integer cell coordinate, the same
    // integer-hashing rule as cellular) and one cosine/sine wave that runs along `side` (the wave
    // vector: its length is 2*pi times the stripe frequency, and the caller chooses which
    // perpendicular it is; on a unit sphere that is cross(p_hat, flow)). The waves are blended with
    // the bell weight max(0, exp(-2 d^2) - 0.01111) (exactly 0 at distance 1.5, the nearest a
    // feature outside the 4x4x4 block can be in 2D, which is what keeps cell-grid lines out), and
    // the blended phasor is normalized: its length is stretched to 1 wherever the raw length is at
    // least 1 - normalization, and scaled by 1 / (1 - normalization) below that. `offset` is the
    // phase in cycles (1 is a full turn). Value and normalization follow Johansen's
    // PhacelleNoise as ported in lpmitchell/AdvancedTerrainErosion.
    //
    // `normalization` is meant to lie in [0, 1]. At 0 the output is the raw blend (a phasor of
    // length at most 1); at 1 the floor is 0 and every output is stretched to length 1. Values of
    // 1 or more saturate at that (the floor is never above the raw length), and a negative value
    // scales every output down by 1 / (1 - normalization) < 1. The normalized gradient scales as
    // 1 / |I| where I is the blended phasor (below), so it grows wherever the blend nearly
    // cancels; a normalization near 1 lets that happen at every point.
    //
    // The gradient is exact: it carries the weight derivative (grad w = -4 v exp(-2 |v|^2) where
    // w > 0) and the normalization, not upstream's convention grad(cos) ~ -sin * side, which
    // ignores the weight gradient (Asura probe: 16.8 deg off at the median, 97.8 at the 95th
    // percentile; the exact form is 0.00 and 0.03). With P = sum(w * (cos, sin)) and W = sum(w),
    // I = P / W has grad I = (grad P - I * grad W) / W; above the threshold the output is I / |I|
    // with gradient (grad I - out * grad|I|) / |I|, below it grad I / (1 - normalization). The
    // value is continuous at both seams below; the gradient is not.
    //  * Where |I| crosses the threshold, the gradient jumps between the two branches.
    //  * Where a cell's weight leaves its support (d^2 = ln(1 / 0.01111) / 2 = 2.24995, distance
    //    1.49998), grad w jumps from -4 * 0.01111 * v to 0: a per-cell jump of length
    //    0.0444 * |v|, 0.0667 at |v| = 1.5. Measured over random points, the resulting jump in
    //    the output gradient is a median 1.5% of its length and at most about 27%.
    //
    // W is the sum of 64 weights and is positive for every input pcg3d produces (the smallest
    // W found by hill-climbing the hash-driven cell contents is 0.056). It is 0, and the output
    // NaN, only if all 64 weights vanish, which takes adversarial jitter no hash yields.
    //
    // Pruning is exact: per axis, |local - g| - 0.5 (floored at 0) lower-bounds |v| for any jitter,
    // so a cell whose summed square is at least 2.25 lies outside the support (2.24995 < 2.25) and
    // has weight exactly 0: it contributes nothing to any sum. Cells whose bound sits just under
    // 2.25 can still have a nonzero weight, so the cut cannot be lowered. On average the prune
    // keeps about 45 of the 64 cells (a saving of about 30%); only about 14 have a nonzero weight.
    // Only the internal overload can switch the prune off; PhacelleTests uses it to pin bit equality.
    public static CultPhasor phacelle(float3 p, float3 side, float offset, float normalization) =>
        phacelle(p, side, offset, normalization, true);

    internal static CultPhasor phacelle(float3 p, float3 side, float offset, float normalization, bool prune)
    {
        var cell = floor(p);
        var local = p - cell;
        var phaseOffset = offset * TAU;
        var sumW = 0.0f;
        var sumCos = 0.0f;
        var sumSin = 0.0f;
        var gradW = new float3(0.0f, 0.0f, 0.0f);
        var gradCos = new float3(0.0f, 0.0f, 0.0f);
        var gradSin = new float3(0.0f, 0.0f, 0.0f);

        for (var dz = -1; dz <= 2; dz++)
        {
            var gz = max(0.0f, abs(local.z - (float)dz) - 0.5f);
            for (var dy = -1; dy <= 2; dy++)
            {
                var gy = max(0.0f, abs(local.y - (float)dy) - 0.5f);
                for (var dx = -1; dx <= 2; dx++)
                {
                    var gx = max(0.0f, abs(local.x - (float)dx) - 0.5f);
                    if (prune && gx * gx + gy * gy + gz * gz >= 2.25f)
                        continue;

                    var gridStep = new float3(dx, dy, dz);
                    var hash = pcg3d(int3(cell + gridStep));
                    var jitter = new float3(cellular_unit(hash.x) - 0.5f, cellular_unit(hash.y) - 0.5f, cellular_unit(hash.z) - 0.5f);
                    var v = local - gridStep - jitter;
                    var falloff = exp(-2.0f * dot(v, v));
                    var w = max(0.0f, falloff - 0.01111f);
                    var dw = w > 0.0f ? (-4.0f * falloff) * v : new float3(0.0f, 0.0f, 0.0f);
                    var phase = dot(v, side) + phaseOffset;
                    var c = cos(phase);
                    var s = sin(phase);

                    sumW += w;
                    sumCos += w * c;
                    sumSin += w * s;
                    gradW += dw;
                    gradCos += dw * c - (w * s) * side;
                    gradSin += dw * s + (w * c) * side;
                }
            }
        }

        var invW = 1.0f / sumW;
        var rawCos = sumCos * invW;
        var rawSin = sumSin * invW;
        var gradRawCos = (gradCos - rawCos * gradW) * invW;
        var gradRawSin = (gradSin - rawSin * gradW) * invW;
        var rawLength = sqrt(rawCos * rawCos + rawSin * rawSin);
        var floorLength = 1.0f - normalization;

        if (rawLength > floorLength)
        {
            var outCos = rawCos / rawLength;
            var outSin = rawSin / rawLength;
            var gradLength = (rawCos * gradRawCos + rawSin * gradRawSin) / rawLength;
            return new CultPhasor(
                new float4((gradRawCos - outCos * gradLength) / rawLength, outCos),
                new float4((gradRawSin - outSin * gradLength) / rawLength, outSin));
        }

        return new CultPhasor(
            new float4(gradRawCos / floorLength, rawCos / floorLength),
            new float4(gradRawSin / floorLength, rawSin / floorLength));
    }
}
