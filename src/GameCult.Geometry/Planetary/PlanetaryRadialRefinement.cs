// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

using CultMath;

namespace GameCult.Geometry;

public static class PlanetaryRadialRefinement
{
    public static float3 Step(
        float3 position,
        float3 rayDirection,
        float3 center,
        float targetRadius,
        float minimumDerivative = 0.2f,
        float maximumCorrection = 0.08f)
    {
        var local = position - center;
        var radius = math.length(local);
        var radial = local / math.max(radius, 0.0001f);
        var error = radius - targetRadius;
        var derivative = math.dot(rayDirection, radial);
        var safeMinimum = math.max(minimumDerivative, 0.0001f);
        var safeDerivative = math.abs(derivative) > safeMinimum
            ? derivative
            : derivative < 0.0f ? -safeMinimum : safeMinimum;
        var correction = math.clamp(
            -error / safeDerivative,
            -math.abs(maximumCorrection),
            math.abs(maximumCorrection));
        return position + rayDirection * correction;
    }
}
