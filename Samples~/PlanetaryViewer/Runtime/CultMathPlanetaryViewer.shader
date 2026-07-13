Shader "CultMath/Planetary Field Viewer"
{
    Properties
    {
        _LowColor ("Low Color", Color) = (0.055, 0.16, 0.09, 1)
        _HighColor ("High Color", Color) = (0.62, 0.48, 0.28, 1)
        _RidgeColor ("Ridge Color", Color) = (0.88, 0.83, 0.7, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Back
            ZWrite On
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Packages/org.gamecult.cultmath/shaders/CultMath.hlsl"

            float _Radius;
            float _SampleFootprint;
            int _Seed;
            float _ErosionScale, _ErosionStrength, _GullyWeight, _Detail;
            float4 _Rounding, _Onset, _AssumedSlope;
            float _CellScale, _Normalization, _Lacunarity, _Gain;
            int _Octaves;
            float3 _LightDirection;
            float4 _LowColor, _HighColor, _RidgeColor;

            struct Attributes { float4 vertex : POSITION; };
            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float height : TEXCOORD1;
                float ridge : TEXCOORD2;
            };

            CultMathPlanetarySurfaceSample viewer_sample(float3 direction)
            {
                direction = normalize(direction);
                float value = direction.x * 0.6 + direction.y * direction.z * 0.3;
                float3 angular_gradient = float3(0.6, direction.z * 0.3, direction.y * 0.3);
                angular_gradient -= direction * dot(angular_gradient, direction);
                CultMathPlanetaryBaseFieldSample base_sample;
                base_sample.radial_displacement = value * _Radius * 0.035;
                base_sample.radial_gradient = angular_gradient * 0.035;
                base_sample.field_value = value;
                base_sample.field_gradient = angular_gradient / _Radius;
                base_sample.fade_target = clamp(value, -1.0, 1.0);
                CultMathAdvancedErosionParameters erosion;
                erosion.scale = _ErosionScale; erosion.strength = _ErosionStrength;
                erosion.gully_weight = _GullyWeight; erosion.detail = _Detail;
                erosion.rounding = _Rounding; erosion.onset = _Onset;
                erosion.assumed_slope = _AssumedSlope.xy; erosion.cell_scale = _CellScale;
                erosion.normalization = _Normalization; erosion.octaves = _Octaves;
                erosion.lacunarity = _Lacunarity; erosion.gain = _Gain;
                CultMathPlanetaryFieldDefinition definition;
                definition.radius = _Radius; definition.seed = _Seed; definition.erosion = erosion;
                return cultmath_planetary_field_sample(definition, direction, base_sample, _SampleFootprint);
            }

            Varyings vert(Attributes input)
            {
                float3 direction = normalize(input.vertex.xyz);
                CultMathPlanetarySurfaceSample sample = viewer_sample(direction);
                float3 local = direction * (_Radius + sample.radial_displacement);
                Varyings output;
                output.position = UnityObjectToClipPos(float4(local, 1));
                output.worldNormal = UnityObjectToWorldNormal(sample.surface_normal);
                output.height = sample.radial_displacement / max(_Radius * 0.1, 1.0e-5);
                output.ridge = sample.ridge;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float height = saturate(input.height * 0.5 + 0.5);
                float3 albedo = lerp(_LowColor.rgb, _HighColor.rgb, height);
                albedo = lerp(albedo, _RidgeColor.rgb, saturate((input.ridge - 0.55) * 2.5));
                float diffuse = saturate(dot(normalize(input.worldNormal), normalize(-_LightDirection))) * 0.82 + 0.08;
                return float4(albedo * diffuse, 1);
            }
            ENDHLSL
        }
    }
}
