// Probe B: bodies through a caller-supplied accessor macro. The library build defines no accessor.
#include "CultMath.hlsl"
#ifdef CULTMATH_BODY
float4 cultmath_power_border_m(float3 p)
{
    float4 border = float4(0.0, 0.0, 0.0, 1.0e30);
    for (int k = 1; k < CULTMATH_BODY_COUNT; k++)
        border = cultmath_power_border_step(p, CULTMATH_BODY(0), CULTMATH_BODY(k), border);
    return border;
}
#endif
export float4 probe_macro(float3 p) { return cultmath_power_border_m(p); }
