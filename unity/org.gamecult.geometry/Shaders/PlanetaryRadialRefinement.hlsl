// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef GAMECULT_GEOMETRY_PLANETARY_RADIAL_REFINEMENT_HLSL
#define GAMECULT_GEOMETRY_PLANETARY_RADIAL_REFINEMENT_HLSL

float3 gamecult_geometry_planetary_radial_refinement_step(
    float3 position,
    float3 ray_direction,
    float3 center,
    float target_radius,
    float minimum_derivative,
    float maximum_correction)
{
    float3 local=position-center;
    float radius=length(local);
    float3 radial=local/max(radius,0.0001);
    float error=radius-target_radius;
    float derivative=dot(ray_direction,radial);
    float safe_minimum=max(minimum_derivative,0.0001);
    float safe_derivative=abs(derivative)>safe_minimum?derivative:(derivative<0?-safe_minimum:safe_minimum);
    float correction=clamp(-error/safe_derivative,-abs(maximum_correction),abs(maximum_correction));
    return position+ray_direction*correction;
}

#endif
