// 地形（Unity Terrain）走本工程的延迟 GBuffer —— 与 DefferStandard / Cluster 共用同一套 BRDF。
//
// 为什么这样写就能「自动拿到数据」：
//   Terrain 引擎会按 TerrainData + TerrainLayer 自动往【材质模板】上灌：
//     _Control、_Splat0..3（albedo）、_Normal0..3、_NormalScale0..3、
//     _Metallic0..3、_Smoothness0..3、_Splat0_ST.._Splat3_ST（每层自己的 tileSize/tileOffset）
//   —— URP 的 TerrainLit 就是靠这套约定工作的，所以本 shader 不需要任何 C# 搬数据，
//   只要 Properties 里把名字声明对。声明成 2D 属性会自动获得 `_SplatN_ST`。
//   （_TerrainMapId0..3 / _Virtual* / _UseRVT 是我们自己加的，由 TerrainRenderSetup 设置。）
//
// 输出布局必须与 DeffererInput.hlsl 的 DefferTarget 逐字一致：
//   RT0 rgb=albedo  a=metallic
//   RT1 rg=oct 编码的【世界空间】法线（PackNormal），z/w=1
//   RT2 rgb=emissive a=roughness
//   RT3 rgb=reflect   a=AO
// 之后由延迟合成阶段（DefferCameraPass.hlsl）统一算直接光/间接光（_Sh9）—— 也就是标准 BRDF。
//
// ===== RVT（贴图流式图集）=====
// `_TERRAIN_RVT` 开启时，每层的 albedo/normal 从图集采（texId = _TerrainMapIdN），
// 否则直采 _SplatN / _NormalN 并用硬件 mipmap。
// 图集寻址统一走 ClusterLibrary/VirtualTexture.hlsl（与 Feedback、Cluster 采样同一份公式）。
// 反馈由 4 个独立 pass（LightMode = TerrainFeedback0..3）完成 —— 一个 pass 只能编码 1 个
// pageId（RGBA8 = 32bit），所以 4 层要画 4 次。为什么不做成「一次画主层」：
// 权重 50/50 的过渡带上，主层会逐像素翻转 ⇒ 一层清晰一层糊，看起来像噪点。
//
// 【本版限制】
//   · 只支持 4 层（一张 _Control 的 RGBA）。8 层要第二张控制图，先不做。
//   · 法线用「世界轴近似切空间」：x→世界 X、y→世界 Z、z→世界 Y。平地/缓坡正确，
//     陡坡（>60°）细节方向会偏 —— 与 Unity 内建地形同一档次的近似。
//   · 单 mip 采样（不做 mip 间三线性过渡）：Feedback 请求的是 floor(mip) 那一页，
//     采样也只能用同一页，否则采到没被请求的页。远景会有轻微 mip 台阶。
//   · 需要关掉 Terrain.drawInstanced（由 TerrainRenderSetup 负责），否则 Unity 会走
//     instanced terrain 变体，本 shader 没有那条通路。
Shader "CustomRP/Defferer/TerrainLit"
{
    Properties
    {
        // 全部 HideInInspector：这些值由 Terrain 引擎按图块/图层自动灌，手工改没有意义。
        [HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0 Albedo", 2D) = "white" {}
        [HideInInspector] _Splat1 ("Layer 1 Albedo", 2D) = "white" {}
        [HideInInspector] _Splat2 ("Layer 2 Albedo", 2D) = "white" {}
        [HideInInspector] _Splat3 ("Layer 3 Albedo", 2D) = "white" {}
        [HideInInspector] _Normal0 ("Layer 0 Normal", 2D) = "bump" {}
        [HideInInspector] _Normal1 ("Layer 1 Normal", 2D) = "bump" {}
        [HideInInspector] _Normal2 ("Layer 2 Normal", 2D) = "bump" {}
        [HideInInspector] _Normal3 ("Layer 3 Normal", 2D) = "bump" {}
        [HideInInspector] _NormalScale0 ("Layer 0 Normal Scale", Float) = 1
        [HideInInspector] _NormalScale1 ("Layer 1 Normal Scale", Float) = 1
        [HideInInspector] _NormalScale2 ("Layer 2 Normal Scale", Float) = 1
        [HideInInspector] _NormalScale3 ("Layer 3 Normal Scale", Float) = 1
        [HideInInspector] _Metallic0 ("Layer 0 Metallic", Float) = 0
        [HideInInspector] _Metallic1 ("Layer 1 Metallic", Float) = 0
        [HideInInspector] _Metallic2 ("Layer 2 Metallic", Float) = 0
        [HideInInspector] _Metallic3 ("Layer 3 Metallic", Float) = 0
        [HideInInspector] _Smoothness0 ("Layer 0 Smoothness", Float) = 0.5
        [HideInInspector] _Smoothness1 ("Layer 1 Smoothness", Float) = 0.5
        [HideInInspector] _Smoothness2 ("Layer 2 Smoothness", Float) = 0.5
        [HideInInspector] _Smoothness3 ("Layer 3 Smoothness", Float) = 0.5

        // ===== RVT（由 TerrainRenderSetup 在运行时设置）=====
        [HideInInspector] _UseRVT ("Use RVT", Float) = 0
        [HideInInspector] _TerrainMapId0 ("Layer 0 Map Id", Float) = 0
        [HideInInspector] _TerrainMapId1 ("Layer 1 Map Id", Float) = 0
        [HideInInspector] _TerrainMapId2 ("Layer 2 Map Id", Float) = 0
        [HideInInspector] _TerrainMapId3 ("Layer 3 Map Id", Float) = 0
        // 图集必须声明成【材质属性】才会被绑定到 sampler_Virtual*（否则 sampling 恒黑）。
        // 同时 Unity 只有对「材质属性纹理」才沿用其 anisoLevel —— 见 UnityInput.hlsl。
        [HideInInspector] _VirtualAlbedo ("RVT Albedo", 2D) = "white" {}
        [HideInInspector] _VirtualNormal ("RVT Normal", 2D) = "white" {}
        [HideInInspector] _VirtualMask ("RVT Mask", 2D) = "white" {}

        // 唯一手调项：法线细节强度（0 = 只用网格法线，等于关掉地形法线贴图）。
        // 与每层 _NormalScaleN（TerrainLayer.normalScale）相乘。
        _NormalScale ("Normal Scale", Range(0, 2)) = 1
    }

    SubShader
    {
        HLSLINCLUDE
            #include "../../ShaderLibrary/Common.hlsl"
            #include "../../ShaderLibrary/GI.hlsl"          // SamplerReflectProbe(_MySkybox)
            #include "../DeffererLibrary/DeffererInput.hlsl" // DefferTarget（RT0..RT3 布局）
            #include "../../Cluster/ClusterLibrary/VirtualTexture.hlsl" // RVT 寻址唯一实现

            // 采样器按「每张纹理自己的」声明（sampler_Xxx ↔ 纹理 Xxx）—— 这样会沿用贴图的
            // anisoLevel；用全局 sampler_linear_repeat 的话 aniso 恒为 1，地形这种大平面掠射角会明显糊。
            TEXTURE2D(_Control);   SAMPLER(sampler_Control);
            TEXTURE2D(_Splat0);    SAMPLER(sampler_Splat0);
            TEXTURE2D(_Splat1);    SAMPLER(sampler_Splat1);
            TEXTURE2D(_Splat2);    SAMPLER(sampler_Splat2);
            TEXTURE2D(_Splat3);    SAMPLER(sampler_Splat3);
            TEXTURE2D(_Normal0);   SAMPLER(sampler_Normal0);
            TEXTURE2D(_Normal1);   SAMPLER(sampler_Normal1);
            TEXTURE2D(_Normal2);   SAMPLER(sampler_Normal2);
            TEXTURE2D(_Normal3);   SAMPLER(sampler_Normal3);
            // 每层自己的平铺（TerrainLayer.tileSize / tileOffset，由地形引擎灌进 _SplatN_ST）。
            // Properties 里声明成 2D 只是让【材质】有这份数据；shader 侧必须自己声明才能读，
            // 否则 TRANSFORM_TEX 展开成 _Splat0_ST.xy 会报 undeclared identifier。
            float4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;
            float _Metallic0, _Metallic1, _Metallic2, _Metallic3;
            float _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
            float _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
            float _NormalScale;
            float _UseRVT;
            float _TerrainMapId0, _TerrainMapId1, _TerrainMapId2, _TerrainMapId3;

            struct TerrainAttributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uvMain     : TEXCOORD0;     // 归一化的地形范围 [0,1]，splat 与各层平铺都基于它
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct TerrainVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uvMain     : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TerrainVaryings TerrainVertex (TerrainAttributes input)
            {
                TerrainVaryings o = (TerrainVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                o.positionWS = positionWS;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uvMain = input.uvMain;
                return o;
            }

            // ---- 每层直接采样（RVT 关闭时）----
            float3 DirectAlbedo (TEXTURE2D_PARAM(tex, samp), float2 uv)
            {
                return SAMPLE_TEXTURE2D(tex, samp, uv).rgb;
            }

            float3 DirectNormalTS (TEXTURE2D_PARAM(tex, samp), float2 uv, float scale)
            {
                float4 s = SAMPLE_TEXTURE2D(tex, samp, uv);
                float3 n = DecodeNormalSafe(s, scale);       // 缺贴图/黑图时自动退回 (0,0,1)
                // 法线贴图绿通道为 DirectX 约定（Y 朝下），Unity 切线空间是 OpenGL 约定（Y 朝上）
                // ⇒ 必须翻 Y。与 PBRInput.hlsl / ClusterInput.hlsl 的读侧完全一致。
                n.y *= -1.0;
                return n;
            }

            // ---- 每层从 RVT 图集采样 ----
            // 图集布局（TileTable.cs / RVTShader.shader Pass3）：
            //   _VirtualAlbedo : rgb=albedo
            //   _VirtualNormal : rg = decoded normal.xy 编码成 0..1（z 由读取侧重建）
            // 单 mip：mip 由调用方用 VTMipLevel 算好（必须在统一控制流里算，见调用处说明）。
            void RVTAlbedoNormal (float2 uv, uint texId, uint mip, float nScale,
                out float3 albedo, out float3 normalTS)
            {
                float2 auv = VTGetSampleUV(uv, texId, mip);
                albedo = SAMPLE_TEXTURE2D_LOD(_VirtualAlbedo, sampler_VirtualAlbedo, auv, 0).rgb;

                float4 ns = SAMPLE_TEXTURE2D_LOD(_VirtualNormal, sampler_VirtualNormal, auv, 0);
                float2 nxy = ns.rg * 2.0 - 1.0;
                normalTS = float3(nxy * nScale, sqrt(saturate(1.0 - dot(nxy, nxy))));
                // 非法样本（页未调入 / 无贴图）：与 DecodeNormalSafe 同一判据
                if (normalTS.z < 0.02) normalTS = float3(0.0, 0.0, 1.0);
                // 图集里存的是 DecodeNormal 的结果（DX 约定），读侧同样要翻 Y
                normalTS.y *= -1.0;
            }

            void TerrainFragment (TerrainVaryings i, out DefferTarget target)
            {
                UNITY_SETUP_INSTANCE_ID(i);

                // ---- splat 权重 ----
                // _Control 缺失时是默认红图 (1,0,0,0) ⇒ 只用第 0 层，不会整片黑。
                float4 ctrl = SAMPLE_TEXTURE2D(_Control, sampler_Control, i.uvMain);
                float wsum = dot(ctrl, float4(1, 1, 1, 1));
                float4 w = (wsum > 1e-4) ? ctrl / wsum : float4(1, 0, 0, 0);

                // ---- 每层各自的平铺 UV ----
                float2 uv0 = TRANSFORM_TEX(i.uvMain, _Splat0);
                float2 uv1 = TRANSFORM_TEX(i.uvMain, _Splat1);
                float2 uv2 = TRANSFORM_TEX(i.uvMain, _Splat2);
                float2 uv3 = TRANSFORM_TEX(i.uvMain, _Splat3);

                float3 albedoL0, albedoL1, albedoL2, albedoL3;
                float3 normL0, normL1, normL2, normL3;

            #if defined(_TERRAIN_RVT)
                // ⚠️ 4 个 mip 必须在【任何分支之前】全部算完：VTMipLevel 内部是 ddx/ddy，
                // 放进 if 分支里导数未定义（会算出随像素跳变的垃圾 mip）。
                float mip0 = VTMipLevel(uv0);
                float mip1 = VTMipLevel(uv1);
                float mip2 = VTMipLevel(uv2);
                float mip3 = VTMipLevel(uv3);

                // 权重为 0 的层不采样：图集采样是这里最贵的一步（每层 2 次）。
                // SAMPLE_TEXTURE2D_LOD(...,0) 没有隐式导数，放在分支里是安全的；
                // splat 权重是连续场，2×2 四像素几乎总在同一分支上，不会有明显发散代价。
                albedoL0 = 0; albedoL1 = 0; albedoL2 = 0; albedoL3 = 0;
                normL0 = 0;   normL1 = 0;   normL2 = 0;   normL3 = 0;
                const float W_EPS = 1e-3;
                if (w.x > W_EPS) RVTAlbedoNormal(uv0, (uint)_TerrainMapId0, (uint)mip0, _NormalScale0 * _NormalScale, albedoL0, normL0);
                if (w.y > W_EPS) RVTAlbedoNormal(uv1, (uint)_TerrainMapId1, (uint)mip1, _NormalScale1 * _NormalScale, albedoL1, normL1);
                if (w.z > W_EPS) RVTAlbedoNormal(uv2, (uint)_TerrainMapId2, (uint)mip2, _NormalScale2 * _NormalScale, albedoL2, normL2);
                if (w.w > W_EPS) RVTAlbedoNormal(uv3, (uint)_TerrainMapId3, (uint)mip3, _NormalScale3 * _NormalScale, albedoL3, normL3);
            #else
                albedoL0 = DirectAlbedo(TEXTURE2D_ARGS(_Splat0, sampler_Splat0), uv0);
                albedoL1 = DirectAlbedo(TEXTURE2D_ARGS(_Splat1, sampler_Splat1), uv1);
                albedoL2 = DirectAlbedo(TEXTURE2D_ARGS(_Splat2, sampler_Splat2), uv2);
                albedoL3 = DirectAlbedo(TEXTURE2D_ARGS(_Splat3, sampler_Splat3), uv3);
                normL0 = DirectNormalTS(TEXTURE2D_ARGS(_Normal0, sampler_Normal0), uv0, _NormalScale0 * _NormalScale);
                normL1 = DirectNormalTS(TEXTURE2D_ARGS(_Normal1, sampler_Normal1), uv1, _NormalScale1 * _NormalScale);
                normL2 = DirectNormalTS(TEXTURE2D_ARGS(_Normal2, sampler_Normal2), uv2, _NormalScale2 * _NormalScale);
                normL3 = DirectNormalTS(TEXTURE2D_ARGS(_Normal3, sampler_Normal3), uv3, _NormalScale3 * _NormalScale);
            #endif

                float3 albedo = albedoL0 * w.x + albedoL1 * w.y + albedoL2 * w.z + albedoL3 * w.w;

                float smoothness = _Smoothness0 * w.x + _Smoothness1 * w.y
                                 + _Smoothness2 * w.z + _Smoothness3 * w.w;
                float metallic   = _Metallic0 * w.x + _Metallic1 * w.y
                                 + _Metallic2 * w.z + _Metallic3 * w.w;

                // ---- 法线：切线空间加权 → 世界空间 ----
                // 直接对已解码的方向加权再归一化（最省的一种白化近似）。关键是不要
                // 4 个 (0,0,1) 直接相加就完事（那就退回顶点法线了）。
                float3 nTS = normL0 * w.x + normL1 * w.y + normL2 * w.z + normL3 * w.w;
                nTS = normalize(float3(nTS.x, nTS.y, max(nTS.z, 1e-3)));

                // 世界轴近似切空间（地形网格没有切线数据）：T = 世界 X 在切平面上的投影，B = cross(T, N)。
                // 平地时 T=(1,0,0)、B=(0,0,1) ⇒ 法线贴图的 x 对应世界 X、y 对应世界 Z，与 Unity 地形一致。
                float3 N = normalize(i.normalWS);
                float3 T = float3(1, 0, 0) - N * N.x;
                T = normalize(T + 1e-6);
                float3 B = cross(T, N);
                float3 normalWS = normalize(T * nTS.x + B * nTS.y + N * nTS.z);

                float roughness = max(0.05, 1.0 - smoothness);
                float2 normalPack = PackNormal(normalWS);

                // ---- 间接镜面：与 DefferStandard / BRG / Cluster 完全同一套（工程自己的天空盒）----
                // 不能用 unity_SpecCube0：那是逐 Renderer 绑定的全局量，地形同理不能用。
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.positionWS);
                float3 reflectDir = reflect(-viewDir, normalWS);
                float reflMip = clamp(roughness * 7.0, 0.0, 8.0);
                float3 refl = SamplerReflectProbe(reflectDir, reflMip);
                refl *= lerp(saturate(0.05 + (1.0 - roughness) * 0.6), 1.0, metallic);

                target._GBufferRT0 = float4(albedo, metallic);
                target._GBufferRT1 = float4(normalPack, 1.0, 1.0);
                target._GBufferRT2 = float4(0.0, 0.0, 0.0, roughness);   // 地形没有自发光
                target._GBufferRT3 = float4(refl, 1.0);                  // AO 恒 1（地形没有 AO 图）
            }
        ENDHLSL

        // ---------- GBuffer ----------
        Pass
        {
            Tags { "Queue" = "Geometry" "LightMode" = "OutGBuffer" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainVertex
            #pragma fragment TerrainFragment
            // 0 = 直采 _SplatN / _NormalN；1 = 从 RVT 图集采（_TerrainMapIdN）
            #pragma multi_compile _ _TERRAIN_RVT
            ENDHLSL
        }

        // ---------- 阴影投射 ----------
        // 地形要能投影（山影落进村庄）。纯几何、无 alpha clip，所以只走位置。
        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainShadowVertex
            #pragma fragment TerrainShadowFragment

            struct ShadowAttributes { float3 positionOS : POSITION; };
            struct ShadowVaryings   { float4 positionCS : SV_POSITION; };

            // _ShadowPancaking：与本工程 ShadowInput.hlsl 的口径一致（方向光开启，把顶点压到近裁剪面，
            // 防"投影体在阴影图后方被裁掉"）。地形是大地物，压平对它无害。
            bool _ShadowPancaking;

            ShadowVaryings TerrainShadowVertex (ShadowAttributes input)
            {
                ShadowVaryings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(positionWS);
                if (_ShadowPancaking)
                {
                    #if UNITY_REVERSED_Z
                        o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                    #else
                        o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                    #endif
                }
                return o;
            }

            void TerrainShadowFragment (ShadowVaryings i) { }
            ENDHLSL
        }

        // ---------- RVT 反馈（每层一个 pass）----------
        // 一次 DrawRenderers 只能选用一个 pass，而一个 pass 只能输出一个 pageId（RGBA8=32bit），
        // 所以 4 层 = 4 个 LightMode。ClusterCore.RenderFeedback 按 tag 逐个画，
        // 只有地形材质带这些 tag ⇒ 不会重复画别的物体。
        // 为什么不用 overrideMaterial 传 4 份材质：override 会绕过 Terrain 引擎往
        // materialTemplate 灌的 _SplatN_ST / _Control，反馈算出来的 UV 就全错了。
        // ⚠️ TERRAIN_FB_LAYER 必须在 #include 之前 define（HLSLINCLUDE 的内容是前置拼接的，
        //   写在 HLSLINCLUDE 里的函数看不到 HLSLPROGRAM 里的 define）。
        Pass
        {
            Tags { "LightMode" = "TerrainFeedback0" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainFeedbackVertex
            #pragma fragment TerrainFeedbackFragment
            #define TERRAIN_FB_LAYER 0
            #include "TerrainFeedbackPass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "TerrainFeedback1" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainFeedbackVertex
            #pragma fragment TerrainFeedbackFragment
            #define TERRAIN_FB_LAYER 1
            #include "TerrainFeedbackPass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "TerrainFeedback2" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainFeedbackVertex
            #pragma fragment TerrainFeedbackFragment
            #define TERRAIN_FB_LAYER 2
            #include "TerrainFeedbackPass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "TerrainFeedback3" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainFeedbackVertex
            #pragma fragment TerrainFeedbackFragment
            #define TERRAIN_FB_LAYER 3
            #include "TerrainFeedbackPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
