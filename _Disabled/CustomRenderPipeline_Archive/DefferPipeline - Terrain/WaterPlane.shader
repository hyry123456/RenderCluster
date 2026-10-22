Shader "Terrain/WaterPlane"
{
    SubShader
    {
        HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../../ShaderLibrary/Noise.hlsl"
            #include "../../CommonInput/CommonInput.hlsl"
            #include "../../ShaderLibrary/Surface.hlsl"
            #include "../../ShaderLibrary/Shadows.hlsl"
            #include "../../ShaderLibrary/Light.hlsl"
            #include "../../ShaderLibrary/BRDF.hlsl"
            #include "../../ShaderLibrary/GI.hlsl"
            #include "../../ShaderLibrary/Lighting.hlsl"
            #include "../../ShaderLibrary/Fragment.hlsl"

            #include "../../DefferPipeline/DeffererLibrary/DeffererInput.hlsl"

            #include "Input/PlaneInput.hlsl"
            #include "Input/OceanPlane.hlsl"
            #include "Input/OceanPass.hlsl"

            #define _BumpSize 0.3
            TEXTURE2D(_HeighMap);

            TEXTURE2D(_WaterColorRamp);
            float4 _PlaneSize;                   //x：水面长度，z:水面宽度, y:水面深度，用来控制深度映射效果,w:水面高度
            float _RampRange;
            TEXTURE2D(_FlowMap);        //水面法线偏移
            TEXTURE2D(_FoamMap);        //边界泡沫
            TEXTURE2D(_FoamMask);        //边界泡沫
            TEXTURE2D(_NormalMap);      //水面法线
            float4 _NormalMap_ST;
            // float _WaveSpeed;
            float _FlowSpeed;
            float _FlowStrength;
            float _Tiling;              //控制整体的缩放情况
            float _Roughness;
            float _NormalScale;
            float2 _WaveTopMask;
            TEXTURE2D(_ColorMap);
            // TEXTURE2D(_GrandDepthTex);
            
            inline float3x3 TBN() {
                float3 T = float3(1, 0, 0);
                float3 B = float3(0, 0, 1);
                float3 N = float3(0, 1, 0);
                float3x3 TBN = float3x3(T, B, N);
                return TBN;
            }

            float3 getFlowNormal_float(float2 uv, float4 normalTex_ST, out float2 flowDir) {
                float4 v_flowTex = SAMPLE_TEXTURE2D(_FlowMap, sampler_linear_repeat, uv) * 2.0 - 1.0;
                float2 flow_dir = v_flowTex.xy * _FlowStrength;  // uv 扰动方向向量
                float noise = v_flowTex.a;

                float phase = _Time.x * _FlowSpeed;
                float phase0 = frac(phase + noise);
                float phase1 = frac(phase + 0.5 + noise);  // +0.5 使两个相位的波峰波谷交错出现

                float2 uv_jump = float2(0.25, 0.1);
                float2 phase0_jump = (phase - phase0) * uv_jump;
                float2 phase1_jump = (phase - phase1) * uv_jump;

                float2 uv_st = uv * normalTex_ST.xy + normalTex_ST.zw;

                float2 uv0 = uv_st - phase0 * flow_dir + phase0_jump;
                float2 uv1 = uv_st - phase1 * flow_dir + phase1_jump;

                float3 tex0 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv0), _NormalScale);
                float3 tex1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv1), _NormalScale) ;

                float3 normalWS_0 = normalize(mul(tex0, TBN()));
                float3 normalWS_1 = normalize(mul(tex1, TBN()));

                // float2 flowDir0 = phase0 * flow_dir + phase0_jump;
                // float2 flowDir1 = phase1 * flow_dir + phase1_jump;

                float flowLerp = abs(1 - 2 * phase0);
                flowDir = flow_dir;
                return normalize(lerp(normalWS_0, normalWS_1, flowLerp));
            }

        ENDHLSL


        Pass{
            Name "Draw Water Color"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest On
            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment SimpleColorfrag

                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
                #pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
                #pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER

			    #pragma multi_compile _ USE_SHORELINE
			    #pragma multi_compile _ SSPR_REFLECTION
			    #pragma multi_compile _ REFLECT_SUN
			    #pragma multi_compile _ USE_VOLUMETRIC_LIGHT
                
            ENDHLSL
        }

        Pass{
            Name "Draw Water Normal Depth Mask"
			Blend One Zero
			ZWrite On
			Cull Off
            HLSLPROGRAM

                #pragma vertex vert
                #pragma fragment fragDepth

                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile _ _DIRECTIONAL_PCF3 _DIRECTIONAL_PCF5 _DIRECTIONAL_PCF7
                #pragma multi_compile _ _OTHER_PCF3 _OTHER_PCF5 _OTHER_PCF7
                #pragma multi_compile _ _CASCADE_BLEND_SOFT _CASCADE_BLEND_DITHER

			    #pragma multi_compile _ USE_SHORELINE
            ENDHLSL
        }


    }
}
