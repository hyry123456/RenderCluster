// 植被（草 / 蕨 / 灌木）着色器 —— SRP 路径专用，**不进 Cluster**。
//
// 场景里的草是 prefab 实例（Meadow_Grass_01_Var1..6），每个带 LODGroup（3 级），
// 走 SRP 的 OutGBuffer + SRP Batcher 合批。草这种「alpha 镂空 + 单面卡片 + 每帧在动」
// 的东西交给 SRP 比交给簇更合适：
//   · 镂空不进 RVT（省图集 slot，alpha 更锐）
//   · LODGroup 由 Unity 的剔除阶段处理，不用 Cluster 自己选 LOD
//   · 顶点每帧都在动，本来就不可能进静态簇集合
//
// 与 DefferStandard 的差异只有三处：
//   1) 顶点带风位移（风参数由 VegetationWind.cs 从场景里的 WindZone 推到全局属性）
//   2) 双面渲染时把背面法线翻过来（Cull Off 下不做这一步，草的背面会全黑）
//   3) 默认 Cull Off（草是单层卡片）
// 片元与 DefferStandard 的 LitPassFragment 逐行一致 —— GBuffer 四个渲染目标的通道语义
// 必须完全相同，否则延迟合成阶段会算出和普通物体不一样的光照。
Shader "CustomRP/Defferer/Vegetation"
{
    Properties
    {
        [Main(Surface, _, off, off)]
        _group ("Surface", float) = 0
        [Space()]
        [Sub(Surface)] _MainTex("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        [Sub(Surface)] [HDR]_Color("Color", Color) = (1, 1, 1, 1)

        [SubToggle(Surface, _MASK_MAP)] _MaskMapToggle("Mask Map", Float) = 1
        //r:metallic, g:AO, b:detail, a:smoothness（与本工程 GetMetallic / GetOcclusion / GetRoughness 的读取一致）
        [Sub(Surface._MASK_MAP)] _MaskMap("Mask (RGBA)", 2D) = "white" {}
        [Sub(Surface)] _Metallic("Metallic", Range(0, 1)) = 1
        [Sub(Surface)] _Roughness("Roughness", Range(0.05, 1)) = 1

        [SubToggle(Surface, _NORMAL_MAP)] _NormalMapToggle("Normal Map", Float) = 1
        [Tex(Surface._NORMAL_MAP, _NormalScale)] _NormalMap("Normals", 2D) = "bump" {}
        [HideInInspector] _NormalScale("Normal Scale", Range(0, 3.0)) = 1

        [Main(ClipSetting, _, off, off)]
        _group3 ("Clip Setting", float) = 0
        [Space()]
        [SubToggle(ClipSetting, _CLIPPING)] _Clipping("Draw Alpha Clipping", Float) = 1
        [Sub(ClipSetting)] _CullOff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [Main(Wind, _, off, off)]
        _groupWind ("Wind", float) = 0
        [Space()]
        [Sub(Wind)] _WindHeight("Plant Height (m)", Float) = 0.35
        [Sub(Wind)] _WindScale("Wind Scale", Range(0, 4)) = 1
        [Sub(Wind)] _WindBendMax("Max Bend (m)", Range(0, 1)) = 0.12
        [Sub(Wind)] _WindPhaseScale("Phase Scatter", Range(0, 4)) = 0.8

        [Enum(UnityEngine.Rendering.CullMode)] _CullBack("Cull", Float) = 0
    }
    SubShader
    {
        // 动态合批会把顶点烘到世界空间 ⇒ 风位移全错。草本来也超 300 顶点合不了批。
        Tags { "DisableBatching" = "True" }

        HLSLINCLUDE
            #include "../../../ShaderLibrary/Common.hlsl"

            // ---- 风（全局属性，由 Runtime/Core/VegetationWind.cs 每帧从 WindZone 推过来）----
            // _VegetationWind.xyz    = 世界风向（单位向量）
            // _VegetationWind.w      = 强度 0~1（windMain / turbulence / pulse 归一后的值）
            // _VegetationWindParams.x = 湍流 0~1
            // _VegetationWindParams.y = 阵风 0~1（慢速包络的调制深度）
            // _VegetationWindParams.z = 时间（秒）
            // _VegetationWindParams.w = 相位打散尺度（世界 xz 上的散列系数）
            float4 _VegetationWind;
            float4 _VegetationWindParams;

            // 顶点风位移：按「物体局部高度」加权，根部不动、梢部摆幅最大。
            // 用局部 Y 而不是 UV.y：草的卡片 UV 朝向不统一，但 pivot 都在根部。
            // 材质参数由调用方传进来（不在这里读 INPUT_PROP），这样本函数可以在
            // HLSLINCLUDE 层定义，两个 Pass 共用同一份实现。
            float3 VegetationWindOffset (float3 positionOS, float3 positionWS,
                                         float plantHeight, float windScale,
                                         float bendMax, float phaseScale)
            {
                float strength = _VegetationWind.w * windScale;
                if (strength <= 1e-5) return float3(0, 0, 0);

                float h = saturate(positionOS.y / max(plantHeight, 1e-3));
                h *= h;                              // 平方：根部几乎锁死，越往上摆幅越大

                // 每株独立相位：用世界 xz 打散，否则整片草像一块板子在动
                float phase = dot(positionWS.xz, float2(0.37, 0.63))
                            * _VegetationWindParams.w * phaseScale;
                float t = _VegetationWindParams.z;

                // 双频摆动（主摆 + 高频小抖） × 慢速阵风包络
                float sway = sin(t * 1.7 + phase) * 0.6 + sin(t * 3.9 + phase * 1.7) * 0.4;
                float gust = lerp(1.0, sin(t * 0.31 + phase * 0.13) * 0.5 + 0.5,
                                  saturate(_VegetationWindParams.y));
                float turb = sin(t * 7.3 + phase * 5.1) * _VegetationWindParams.x;

                float amount = bendMax * strength * (sway * gust + turb * 0.35);
                return _VegetationWind.xyz * (amount * h);
            }
        ENDHLSL

        // ---------------- GBuffer ----------------
        Pass
        {
            Tags {
                "Queue" = "Geometry"
                "LightMode" = "OutGBuffer"
            }
            Cull[_CullBack]
            ZWrite On

            HLSLPROGRAM
            #pragma vertex VegetationPassVertex
            #pragma fragment VegetationPassFragment

            #pragma target 4.5    // SV_IsFrontFace 需要 SM4+
            #pragma shader_feature _ _CLIPPING
            #pragma shader_feature _ _MASK_MAP
            #pragma shader_feature _ _NORMAL_MAP
            #pragma shader_feature _ _DETAIL_MAP

            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ _MY_LIGHTING
            #pragma multi_compile_instancing

            #include "../../../ShaderLibrary/GI.hlsl"
            #include "../../../ShaderLibrary/Surface.hlsl"
            #include "../../../ShaderLibrary/Shadows.hlsl"
            #include "../../../ShaderLibrary/Light.hlsl"
            #include "../../../ShaderLibrary/BRDF.hlsl"
            #include "../../../ShaderLibrary/Lighting.hlsl"
            #include "../../DeffererLibrary/DeffererInput.hlsl"
            #include "../../../CommonInput/PBRInput.hlsl"
            #include "../Input/GBufferPass.hlsl"      // 提供 Attributes / Varyings / TransformDetailUV 等

            Varyings VegetationPassVertex (Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                TRANSFER_GI_DATA(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS);
                positionWS += VegetationWindOffset(input.positionOS, positionWS,
                    INPUT_PROP(_WindHeight), INPUT_PROP(_WindScale),
                    INPUT_PROP(_WindBendMax), INPUT_PROP(_WindPhaseScale));

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                // 非等比缩放下切线/法线不再正交，必须 Gram-Schmidt（与 DefferStandard 同一套修复）
                float3 orthoT = tangentWS - normalWS * dot(normalWS, tangentWS);
                float orthoTLenSq = dot(orthoT, orthoT);
                if (orthoTLenSq > 1e-8)
                {
                    tangentWS = orthoT * rsqrt(orthoTLenSq);
                }
                else
                {
                    // 退化切线（植被卡片常常只导了 UV、没有切线）：随便造一个与法线正交的方向。
                    // 不兜底的话 binormal = cross(n, 0) = 0 ⇒ TBN 基塌掉 ⇒ 法线贴图方向全错、草发黑。
                    float3 up = abs(normalWS.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                    tangentWS = normalize(cross(up, normalWS));
                }
                float3 binormalWS = cross(normalWS, tangentWS) * input.tangentOS.w;

                output.positionCS_SS = TransformWorldToHClip(positionWS);
                output.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
                output.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
                output.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);
                output.baseUV = TransformBaseUV(input.baseUV);
                #if defined(_DETAIL_MAP)
                    output.detailUV = TransformDetailUV(input.baseUV);
                #endif
                return output;
            }

            // 与 DefferStandard 的 LitPassFragment 逐行一致，只多一件事：
            // SV_IsFrontFace —— Cull Off 下背面法线要翻过来，否则草的背面全黑。
            void VegetationPassFragment (Varyings input,
                bool isFrontFace : SV_IsFrontFace,
                out DefferTarget target)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                InputConfig config = GetInputConfig(input.baseUV);
                ClipLOD(input.positionCS_SS.xy, unity_LODFade.x);

                #if defined(_MASK_MAP)
                    config.useMask = true;
                #endif
                #if defined(_DETAIL_MAP)
                    config.detailUV = input.detailUV;
                    config.useDetail = true;
                #endif

                float4 base = GetBase(config);
                #if defined(_CLIPPING)
                    clip(base.a - GetCutoff());
                #endif

                float3 normal;
                float3 perNormal = float3(input.TtoW0.z, input.TtoW1.z, input.TtoW2.z);
                #if defined(_NORMAL_MAP)
                    normal = GetNormalTS(config);
                    normal = normalize(float3(dot(input.TtoW0.xyz, normal),
                                              dot(input.TtoW1.xyz, normal),
                                              dot(input.TtoW2.xyz, normal)));
                #else
                    normal = normalize(perNormal);
                #endif

                // 双面：背面法线取反。草是单层卡片，Cull Off 之后一半三角形的法线是朝里的。
                if (!isFrontFace) normal = -normal;

                float2 normalPack = PackNormal(normal);
                float metallic = GetMetallic(config);

                float3 positionWS = float3(input.TtoW0.w, input.TtoW1.w, input.TtoW2.w);
                float3 emissive = GetEmission(config);
                float roughness = GetRoughness(config);
                float ao = GetOcclusion(config);

                // 间接镜面：与 DefferStandard 同源（工程自己的天空盒），保证两种材质观感一致
                float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
                float3 reflect_dir = reflect(-viewDir, normal);
                float reflMip = clamp(roughness * 7.0, 0.0, 8.0);
                float3 refl = SamplerReflectProbe(reflect_dir, reflMip);
                float reflIntensity = lerp(saturate(0.05 + (1.0 - roughness) * 0.6), 1.0, metallic);
                refl *= reflIntensity;

                target._GBufferRT0 = float4(base.rgb, metallic);
                target._GBufferRT1 = float4(float3(normalPack, 1), 1);
                target._GBufferRT2 = float4(emissive, roughness);
                target._GBufferRT3 = float4(refl, ao);
            }
            ENDHLSL
        }

        // ---------------- 阴影投射 ----------------
        // 草必须投影（否则贴着地面看就是「飘着的卡片」），且要按 alpha 镂空，
        // 所以材质上要开 _SHADOWS_CLIP（ShadowCasterPassFragment 用它决定要不要 clip）。
        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            Cull[_CullBack]

            HLSLPROGRAM
            #pragma target 4.5    // SV_IsFrontFace 需要 SM4+
            #pragma shader_feature _ _SHADOWS_CLIP _SHADOWS_DITHER
            #pragma shader_feature _ _CLIPPING
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #pragma vertex VegetationShadowVertex
            #pragma fragment ShadowCasterPassFragment

            #include "../../../CommonInput/PBRInput.hlsl"
            #include "../Input/ShadowInput.hlsl"

            // 复用 ShadowInput.hlsl 的 Varyings_Shadow / ShadowCasterPassFragment，只重写 VS 加风。
            // 阴影不跟着草动的话，风一吹影子就和草分离，非常明显。
            Varyings_Shadow VegetationShadowVertex (Attributes_Shadow input)
            {
                Varyings_Shadow output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS);
                positionWS += VegetationWindOffset(input.positionOS, positionWS,
                    INPUT_PROP(_WindHeight), INPUT_PROP(_WindScale),
                    INPUT_PROP(_WindBendMax), INPUT_PROP(_WindPhaseScale));
                output.positionCS_SS = TransformWorldToHClip(positionWS);

                if (_ShadowPancaking)
                {
                    #if UNITY_REVERSED_Z
                        output.positionCS_SS.z = min(output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE);
                    #else
                        output.positionCS_SS.z = max(output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE);
                    #endif
                }

                output.baseUV = TransformBaseUV(input.baseUV);
                return output;
            }
            ENDHLSL
        }
    }
}
