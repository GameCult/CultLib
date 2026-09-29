// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// The result type of math.phacelle (math.Phacelle.cs), Rune Skovbo Johansen's Phacelle noise
// (Copyright (c) 2025, MPL-2.0; see THIRD-PARTY-NOTICES.md).

namespace CultMath;

/// <summary>
/// The value-and-gradient result of <see cref="math.phacelle(float3, float3, float, float)"/>
/// (invariant 8's struct return shape). Both fields use the <c>float4(gradient.xyz, value.w)</c>
/// layout of a bare value-and-gradient return.
/// </summary>
public struct CultPhasor
{
    /// <summary>(&#8711;cos, cos): the normalized cosine wave and its gradient.</summary>
    public float4 cos;

    /// <summary>(&#8711;sin, sin): the normalized sine wave and its gradient.</summary>
    public float4 sin;

    public CultPhasor(float4 cos, float4 sin)
    {
        this.cos = cos;
        this.sin = sin;
    }
}
