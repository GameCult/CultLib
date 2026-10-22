// Probe A (local dxc): the prototype as a lib_6_3 SPIR-V library export, bodies as a fixed-size array parameter plus a count.
#include "CultMath.hlsl"
#include "prototype.hlsl"
export float4 probe_lattice(float2 p, float4 bodies[CULTMATH_MAX_SITES], int count)
{
    return cultmath_weighted_lattice_cells(p, 1.0, bodies, count, 260.0, 0.3, 1.6, 120.0, 0.35, float2(1.0, 2.0), 1.5);
}
