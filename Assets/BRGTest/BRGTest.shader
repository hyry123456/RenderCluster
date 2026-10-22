// BRG 二分定位测试 shader —— 已针对你们的【延迟管线】修正：
//   原版只有 SRPDefaultUnlit（无 LightMode）Pass，而 DefferPipeline 只渲染
//   "OutGBuffer" / "CustomLit" / "CustomLitAdd" 三个 Tag（DefferPipeline.cs:21-24,331,408），
//   SRPDefaultUnlit 从未被任何 DrawingSettings 包含 → BRG 批次根本不会被绘制 → 全黑。
//   这里改成 LightMode="OutGBuffer"，于是它会在 DrawGBuffer 阶段（与真实 ClusterPBR 同一条
//   DrawRenderers 调用）被画进 GBuffer，最终由 BRDF(_GBufferDraw) 合成到屏幕。
//
//   判定逻辑：
//   · 画面出现亮色立方体（默认白/蓝，自发光，不依赖灯光）→ BRG 环境 + DOTS 实例矩阵链路 OK；
//   · 仍报 "wrong cbuffer setup / Missing DOTS_INSTANCING_ON variant" → 问题在管线环境层；
//   · 立方体位置/朝向错乱 → 实例矩阵（o2w/w2o）打包或 metadata 字节地址错。
//
//   注意：BRDF 在 DefferCameraPass.hlsl:145 无条件叠加 rt2.rgb(emissive)，故本探针写亮
//   emissive 以保证一定可见，与灯光/法线无关（法线仍写合法值，避免 BRDF 计算异常）。
Shader "Unlit/BRGTest"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.6, 0.8, 1.0, 1)
    }
    SubShader
    {
        // 关键：必须匹配管线 GBufferShaderTagId = "OutGBuffer"，否则 BRG 批次不绘制。
        Tags { "LightMode" = "OutGBuffer" }

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "../CustomRenderPipeline/Renderer/ShaderLibrary/Common.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
            #endif

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // 与管线 GBufferData.hlsl:4 完全一致的 MRT 输出结构。
            struct DefferTarget
            {
                float4 _GBufferRT0 : SV_Target0;   // rgb:albedo, w:metalness
                float4 _GBufferRT1 : SV_Target1;   // rgb:EncodeNormal, w:1
                float4 _GBufferRT2 : SV_Target2;   // rgb:emissive, w:roughness
                float4 _GBufferRT3 : SV_Target3;   // rgb:reflect, w:AO
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                // TransformObjectToWorld / TransformObjectToWorldNormal 在 DOTS 模式下
                // 走 UNITY_MATRIX_M / UNITY_MATRIX_I_M 的 DOTS 重定向（见 Common.hlsl），
                // 即从 BRG 经 metadata 提供的 unity_ObjectToWorld / unity_WorldToObject 加载。
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            DefferTarget frag (Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                DefferTarget o;

                float3 normal = normalize(input.normalWS);
                float2 nPack  = PackNormal(normal);          // octahedral，与 ClusterPBR 同款
                float3 nRT    = float3(nPack, 1.0);

                // emissive 被 BRDF 无条件叠加（DefferCameraPass.hlsl:145）→ 保证可见。
                float3 emissive = _BaseColor.rgb * 3.0;

                o._GBufferRT0 = float4(_BaseColor.rgb, 0.0);  // albedo, metallic=0
                o._GBufferRT1 = float4(nRT, 1.0);             // 合法法线
                o._GBufferRT2 = float4(emissive, 0.5);        // emissive, roughness
                o._GBufferRT3 = float4(0.0, 0.0, 0.0, 1.0);  // reflect, AO=1
                return o;
            }

            ENDHLSL
        }
    }
}
