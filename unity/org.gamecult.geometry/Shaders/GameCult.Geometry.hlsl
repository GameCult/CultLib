#ifndef GAMECULT_GEOMETRY_HLSL
#define GAMECULT_GEOMETRY_HLSL

#if defined(GAMECULT_GEOMETRY_UNITY_PACKAGE)
#include "Packages/org.gamecult.cultmath/shaders/CultMath.hlsl"
#else
#include "CultMath/CultMath.hlsl"
#endif
#include "AdvancedErosionFilter.hlsl"
#include "Planetary.hlsl"
#include "SphericalErosion.hlsl"
#include "PlanetaryRadialRefinement.hlsl"

#endif
