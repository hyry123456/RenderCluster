// 【B 方案】草（cutout）阴影投射器。
//
// 与 ClusterShadow.shader（纯几何、不采贴图）的区别：本 shader 需要按 albedo 的 alpha 做 clip，
// 否则草会投出实心方块。几何是【实例化】的：
//   · _Positions / _UVs      —— 唯一 mesh 的局部空间顶点与 UV（每 (mesh,材质) 组一份）
//   · _InstanceMatrices      —— 每株草的世界矩阵（组内一组）
//   · _Visible               —— 该级联的可见实例紧凑列表（compute 每帧写好）
//   索引绘制下 SV_VertexID 就是索引值（= 顶点号），SV_InstanceID 是实例号。
//
// 矩阵来源与 ClusterShadow.shader 完全一致：C# 逐级联算 worldToClip（=GetGPUProjectionMatrix(proj)*view），
// 经 MaterialPropertyBlock 传入 _ClusterShadowW2C。绝不用内建 UNITY_MATRIX_M/V（DrawProceduralIndirect 下不可靠）。
Shader "Hidden/ClusterGrassShadowCaster"
{
    Properties
    {
        _Albedo("Albedo", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Name "ClusterGrassShadow"
            ColorMask 0
            ZWrite On
            ZTest LEqual
            // 草是单层薄卡片：SRP 的草 ShadowCaster 是双面（Cull Off），这里保持一致，
            // 否则背向相机的卡片不投影 ⇒ 草阴影缺一半。卡片三角形极少，光栅化 ×2 可忽略。
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #if SHADER_API_D3D11
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/D3D11.hlsl"
            #elif SHADER_API_METAL
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/Metal.hlsl"
            #elif SHADER_API_VULKAN
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/Vulkan.hlsl"
            #elif SHADER_API_GLCORE
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLCore.hlsl"
            #elif SHADER_API_GLES3
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLES3.hlsl"
            #elif SHADER_API_GLES
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/GLES2.hlsl"
            #endif

            #include "UnityShaderVariables.cginc"

            StructuredBuffer<float3>   _Positions;          // 局部空间顶点（唯一 mesh 一份）
            StructuredBuffer<float2>   _UVs;                // 局部空间 UV
            StructuredBuffer<float4x4> _InstanceMatrices;   // 每实例世界矩阵
            StructuredBuffer<uint>     _Visible;            // [cascade * instCount + slot]

            Texture2D    _Albedo;
            SamplerState sampler_Albedo;
            float        _Cutoff;
            float4       _Albedo_ST;
            float4x4     _ClusterShadowW2C;
            int          _CascadeBase;                      // = cascade * instCount

            // 风（全局，由 Runtime/Core/VegetationWind.cs 每帧从 WindZone 推过来）
            float4 _VegetationWind;
            float4 _VegetationWindParams;
            // 每组材质的风参：x=height y=scale z=bendMax w=phaseScale
            float4 _WindParams;

            struct Attributes
            {
                uint vertexID : SV_VertexID;   // 索引值 = 顶点号
                uint instID   : SV_InstanceID; // 实例号
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            Varyings vert(Attributes i)
            {
                uint inst = _Visible[_CascadeBase + (int)i.instID];

                float3 pLocal = _Positions[i.vertexID];
                float4 wPos   = mul(_InstanceMatrices[inst], float4(pLocal, 1.0));

                // 风位移：与 Vegetation.shader 的 VegetationWindOffset 逐行一致。
                // 阴影必须跟着草一起摆，否则风一吹影子和草就分离（很明显）。
                float strength = _VegetationWind.w * _WindParams.y;
                if (strength > 1e-5)
                {
                    float h = saturate(pLocal.y / max(_WindParams.x, 1e-3));
                    h *= h;                                  // 根部锁死、梢部摆幅最大
                    float phase = dot(wPos.xz, float2(0.37, 0.63)) * _VegetationWindParams.w * _WindParams.w;
                    float t = _VegetationWindParams.z;
                    float sway = sin(t * 1.7 + phase) * 0.6 + sin(t * 3.9 + phase * 1.7) * 0.4;
                    float gust = lerp(1.0, sin(t * 0.31 + phase * 0.13) * 0.5 + 0.5,
                                      saturate(_VegetationWindParams.y));
                    float turb = sin(t * 7.3 + phase * 5.1) * _VegetationWindParams.x;
                    float amount = _WindParams.z * strength * (sway * gust + turb * 0.35) * h;
                    wPos.xyz += _VegetationWind.xyz * amount;
                }

                Varyings o;
                o.pos = mul(_ClusterShadowW2C, wPos);
                o.uv  = _UVs[i.vertexID] * _Albedo_ST.xy + _Albedo_ST.zw;

                // Pankaking：把近平面外几何拉到近平面（与 SRP ShadowInput 同一套写法）。
                #if UNITY_REVERSED_Z
                    o.pos.z = min(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.pos.z = max(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                return o;
            }

            void frag(Varyings i)
            {
                float a = _Albedo.Sample(sampler_Albedo, i.uv).a;
                clip(a - _Cutoff);
            }
            ENDHLSL
        }
    }
}
