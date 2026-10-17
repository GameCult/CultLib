// Probe compute entry (HLSL route): 76 words in per case (p, 16 bodies, count, 9 parameters), 4 words out.
#include "CultMath.hlsl"
[[vk::binding(0)]] StructuredBuffer<uint> In;
[[vk::binding(1)]] RWStructuredBuffer<uint> Out;
[numthreads(64, 1, 1)] void main(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x; if (i >= NCASES) return; uint b = i * 76u;
    float4 bodies[CULTMATH_MAX_SITES];
    for (int k = 0; k < CULTMATH_MAX_SITES; k++)
        bodies[k] = float4(asfloat(In[b + 2 + 4 * k]), asfloat(In[b + 3 + 4 * k]), asfloat(In[b + 4 + 4 * k]), asfloat(In[b + 5 + 4 * k]));
    uint c = b + 67u;
    float4 r = cultmath_weighted_lattice_cells(float2(asfloat(In[b]), asfloat(In[b + 1])), asfloat(In[c]), bodies, asint(In[b + 66]),
        asfloat(In[c + 1]), asfloat(In[c + 2]), asfloat(In[c + 3]), asfloat(In[c + 4]), asfloat(In[c + 5]),
        float2(asfloat(In[c + 6]), asfloat(In[c + 7])), asfloat(In[c + 8]));
    Out[i * 4] = asuint(r.x); Out[i * 4 + 1] = asuint(r.y); Out[i * 4 + 2] = asuint(r.z); Out[i * 4 + 3] = asuint(r.w);
}
