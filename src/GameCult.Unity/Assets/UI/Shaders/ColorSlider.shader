Shader "UI/ColorSlider"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _H ("H", Range(0,1)) = 1
        _S ("S", Range(0,1)) = 1
        _V ("V", Range(0,1)) = 1
        [KeywordEnum(HSV, HSL, HCY)] _Mode("Mode", Integer) = 0
        [KeywordEnum(H, S, V)] Var("Var", int) = 0
        [KeywordEnum(Horizontal, Vertical)] Axis("Axis", int) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #include "ColorConversion.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #pragma multi_compile_local _MODE_HSV _MODE_HSL _MODE_HCY
            #pragma multi_compile_local VAR_H VAR_S VAR_V
            #pragma multi_compile_local AXIS_HORIZONTAL AXIS_VERTICAL

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord  : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                float2 texcoordRaw  : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed _H;
            fixed _S;
            fixed _V;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);

                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.texcoordRaw = v.vertex.xy;

                OUT.color = v.color;
                return OUT;
            }

            float invLerp(float from, float to, float value){
                return (value - from) / (to - from);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 sprite = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd);
                #ifdef AXIS_HORIZONTAL
                float x = IN.texcoordRaw.x;
                #elif AXIS_VERTICAL
                float x = invLerp(.28125,.71875,IN.texcoord.y);
                #endif
                
                #ifdef VAR_H
                float h = x;
                float s = _S;
                float v = _V;
                #elif VAR_S
                float h = _H;
                float s = x;
                float v = _V;
                #elif VAR_V
                float h = _H;
                float s = _S;
                float v = x;
                #endif
                
                #ifdef _MODE_HSV
                half4 color = half4(HSVtoRGB(float3(h, s, v)), 1);
                #elif _MODE_HSL
                half4 color = half4(HSLtoRGB(float3(h, s, v)), 1);
                #elif _MODE_HCY
                half4 color = half4(HCYtoRGB(float3(h, s, v)), 1);
                #endif

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip (color.a - 0.001);
                #endif

                return color * sprite;
            }
        ENDCG
        }
    }
}