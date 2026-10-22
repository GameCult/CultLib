// Probe C: a compute entry passing a constant-buffer array, and one passing a StructuredBuffer copied to a local array.
#include "lattice-array.hlsl"
cbuffer Zone : register(b0) { float4 zoneBodies[CULTMATH_MAX_SITES]; int zoneCount; };
StructuredBuffer<float4> bufferBodies : register(t1);
RWStructuredBuffer<float4> result : register(u2);
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float2 p = float2(id.x * 7.0, id.y * 11.0);
    float4 local[CULTMATH_MAX_SITES];
    for (int i = 0; i < CULTMATH_MAX_SITES; i++)
        local[i] = i < zoneCount ? bufferBodies[i] : float4(0.0, 0.0, 0.0, 1.0);
    result[id.x] = cultmath_weighted_lattice_cells(p, 1.0, zoneBodies, zoneCount, 260.0, 0.3, 1.6, 120.0, 0.35, float2(1.0, 2.0), 1.5)
        + cultmath_weighted_lattice_cells(p, 1.0, local, zoneCount, 260.0, 0.3, 1.6, 120.0, 0.35, float2(1.0, 2.0), 1.5);
}
