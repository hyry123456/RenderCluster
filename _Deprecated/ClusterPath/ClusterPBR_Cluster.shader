// 簇级 GBuffer shader —— Step2 P7（稳健版：真实 Mesh + DrawMesh 硬件顶点管线 + 簇级虚拟贴图寻址）。
//
// 与上一版（SetVertexBufferParams + DrawProcedural(indexBuffer)）及更早版（SV_VertexID 手动展开）的根本区别：
//   · 几何来自【真实 Mesh 的顶点/索引缓冲】，由 DrawMesh 走硬件顶点获取 + post-transform 缓存 +
//     索引复用，享受全部 GPU 顶点缓存优化，且零顶点放大（簇仅作为 submesh 引用共享顶点）。
//   · vs 输入是标准顶点属性（POSITION/NORMAL/TANGENT/TEXCOORD0），与源 Mesh 顶点布局严格对应。
//   · object-to-world 由 DrawMesh 自动经 unity_ObjectToWorld 提供，无需手动逐 (owner) uniform。
//   · 【簇级虚拟贴图寻址】RVT 页表索引用全局 _ClusterVtId（逐 (owner,submesh) 设置，
//     等价于原 per-submesh 的 _MapId）；簇级独立虚拟贴图区域为后续 P5 任务。
//
// RVT 取数算法与 ClusterPBR / ClusterPBR_BRG 同源同公式（保证像素一致）。
// 间接光按既定方向丢弃；RT3.rgb 反射暂为 0，AO 正常。
Shader "Unlit/ClusterPBR_Cluster"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _CullOff("Cull Off", Range(0, 1)) = 0.3
        _MapId("Map Id", int) = 0
        _NormalScale("Normal Scale", Range(0, 1)) = 0.5
        _Metallic("Metallic", Range(0, 1)) = 0.5
        _Roughness("Roughness", Range(0.05, 1)) = 0.8
        _DetailMap("Details", 2D) = "linearGrey" {}
        _Color("Main Color", Color) = (1, 1, 1, 1)
        _DetailAlbedo("Detail Albedo", Range(0, 1)) = 0
        _DetailNormal("Detail Normal", Range(0, 1)) = 0
        _DetailRoughness("Detail Roughness", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "LightMode" = "OutGBuffer" }

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            // RVT 全局资源（_TextureNodesBuffer / _Virtual* / _LevelData / _VTFeedbackParam 等）
            // 来自 Common.hlsl -> UnityInput.hlsl，已在主渲染路径里经 Shader.SetGlobal* 绑定。
            #include "../../ShaderLibrary/Common.hlsl"

            // ---- 材质属性（SRP Batcher 兼容的 UnityPerMaterial cbuffer；本路径无实例化/DOTS）----
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _DetailMap_ST;
                float4 _Color;
                float  _NormalScale;
                float  _Metallic;
                float  _Roughness;
                float  _DetailAlbedo;
                float  _DetailNormal;
                float  _DetailRoughness;
                float  _CullOff;
                uint   _MapId;
            CBUFFER_END

            // 逐 (owner, submesh) 设置：簇级 RVT 页表索引。
            int _ClusterVtId;

            // ================= RVT 取数（与 ClusterPBR_BRG 同源同算法）=================
            struct RVTColors
            {
                float4 albedo;
                float4 normal;
                float4 mask;
                float4 emission;
                float4 detail;
            };

            float2 TransformBaseUV(float2 baseUV)
            {
                return baseUV * _MainTex_ST.xy + _MainTex_ST.zw;
            }

            float2 TransformDetailUV(float2 baseUV)
            {
                return baseUV * _DetailMap_ST.xy + _DetailMap_ST.zw;
            }

            RVTColors SampleRVT(float2 uv, TextureNode node, float2 detailUV)
            {
                float4 levelData = _LevelData[node.mipsLevel];
                float4 levelPadding = _LevelPadding[node.mipsLevel];

                uv = frac(uv);
                detailUV = frac(detailUV);

                float2 tileBegin = float2(levelData.x, 0) + levelData.yz * float2(node.width, node.height) + levelPadding.xy;
                uv = tileBegin + levelPadding.zw * uv;
                detailUV = tileBegin + levelPadding.zw * detailUV;

                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1.0 - uv.y;
                    detailUV.y = 1.0 - detailUV.y;
                #endif

                RVTColors colors;
                colors.albedo   = SAMPLE_TEXTURE2D_LOD(_VirtualAlbedo,   sampler_linear_repeat, uv, 0);
                colors.normal   = SAMPLE_TEXTURE2D_LOD(_VirtualNormal,   sampler_linear_repeat, uv, 0);
                colors.mask     = SAMPLE_TEXTURE2D_LOD(_VirtualMask,     sampler_linear_repeat, uv, 0);
                colors.emission = SAMPLE_TEXTURE2D_LOD(_VirtualEmission, sampler_linear_repeat, uv, 0);
                colors.detail   = SAMPLE_TEXTURE2D_LOD(_VirtualDetail,   sampler_linear_repeat, detailUV, 0);
                return colors;
            }

            RVTColors LerpColors(RVTColors c1, RVTColors c2, float radio)
            {
                c1.albedo   = c1.albedo   * (1.0 - radio) + c2.albedo   * radio;
                c1.normal   = c1.normal   * (1.0 - radio) + c2.normal   * radio;
                c1.mask     = c1.mask     * (1.0 - radio) + c2.mask     * radio;
                c1.emission = c1.emission * (1.0 - radio) + c2.emission * radio;
                c1.detail   = c1.detail   * (1.0 - radio) + c2.detail   * radio;
                return c1;
            }

            float4 GetBase(RVTColors colors)
            {
                float4 map = colors.albedo;
                float4 color = _Color;

                float detail = (colors.detail.r * 2.0 - 1.0) * _DetailAlbedo;
                float mask = colors.mask.b;
                map.xyz = lerp(sqrt(map.xyz), detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
                map.rgb *= map.rgb;
                return map * color;
            }

            float3 GetNormalTS(RVTColors colors)
            {
                float4 map = colors.normal;
                float normalScale = _NormalScale;
                // RVT 图集法线是「法线贴图原样拷贝」，用 DecodeNormal 解码 + DecodeNormalSafe 兜底。
                float3 normal = DecodeNormalSafe(map, normalScale);
                // 法线贴图绿通道为 DirectX 约定（Y 朝下），Unity 切线空间是 OpenGL 约定（Y 朝上）→ 必须翻转 Y。
                normal.y *= -1.0;

                // detail 法线链路【禁用】——同 ClusterPBR_BRG / ClusterInput 的说明：
                // fallback 数据让 BlendNormalRNM 除零产生 NaN，detailIntensity=1 时把正常法线整个覆盖。
                //float3 unpackNormal = UnpackNormal(float3(colors.detail.y, colors.detail.w, 1));
                //float3 detail = DecodeNormal(float4(unpackNormal, 1.0f), 1.0f);
                //float detailIntensity = _DetailNormal * colors.mask.b;
                //float3 blendNor = BlendNormalRNM(normal, detail);
                //return lerp(normal, blendNor, detailIntensity);
                return normal;
            }

            float GetRoughness(RVTColors colors)
            {
                float roughness = _Roughness;
                roughness *= colors.mask.w;
                roughness = lerp(roughness, 1.0f, _DetailRoughness * colors.detail.z * colors.mask.b);
                return roughness;
            }

            // ================= vertex-shader（标准顶点属性输入，DrawMesh 硬件顶点管线）=================
            struct Attributes
            {
                float3 position : POSITION;     // 与源 Mesh 一致：float3
                float3 normal   : NORMAL;      // float3
                float4 tangent  : TANGENT;     // float4，w=handedness
                float2 uv01     : TEXCOORD0;   // float2
            };

            struct Varyings
            {
                float4 positionCS_SS : SV_POSITION;
                float4 TtoW0 : TEXCOORD0;
                float4 TtoW1 : TEXCOORD1;
                float4 TtoW2 : TEXCOORD2;
                float2 baseUV : TEXCOORD3;
                float2 detailUV : TEXCOORD4;
            };

            // 与管线 GBufferData.hlsl 完全一致的 MRT 输出结构。
            struct DefferTarget
            {
                float4 _GBufferRT0 : SV_Target0;   // rgb:albedo, w:metalness
                float4 _GBufferRT1 : SV_Target1;   // rgb:EncodeNormal, w:1
                float4 _GBufferRT2 : SV_Target2;   // rgb:emissive, w:roughness
                float4 _GBufferRT3 : SV_Target3;   // rgb:reflect, w:AO
            };

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                // DrawMesh 自动提供 unity_ObjectToWorld，走硬件顶点管线。
                float3 positionOS = input.position;
                float3 normalOS   = input.normal;
                float4 tangentOS  = input.tangent;
                float2 baseUV     = input.uv01;

                float4x4 o2w = unity_ObjectToWorld;
                float3 positionWS = mul(o2w, float4(positionOS, 1.0)).xyz;
                output.positionCS_SS = mul(UNITY_MATRIX_VP, float4(positionWS, 1.0));

                float3 normalWS   = normalize(mul((float3x3)o2w, normalOS));
                float3 tangentWS  = normalize(mul((float3x3)o2w, tangentOS.xyz));
                float3 binormalWS = cross(normalWS, tangentWS) * tangentOS.w;

                output.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
                output.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
                output.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);

                output.baseUV   = TransformBaseUV(baseUV);
                output.detailUV = TransformDetailUV(baseUV);
                return output;
            }

            DefferTarget frag (Varyings input)
            {
                // ---- 1) 按屏幕导数选 RVT mip（与 ClusterPass 同算法，含三线性混合）----
                float2 uv = input.baseUV * _VTFeedbackParam.y;
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);

                float mipRadio = clamp(_VTFeedbackParam.x * log2(max(dot(dx, dx), dot(dy, dy))) + 1.0 -
                    _VTFeedbackParam.x + _VTFeedbackParam.w, 0.0, _VTFeedbackParam.z);

                float maxRadio = ceil(mipRadio), minRadio = floor(mipRadio);
                uint maxMip = (uint)maxRadio, minMip = (uint)minRadio;
                // 【簇级虚拟贴图寻址】用逐 (owner,submesh) 的 _ClusterVtId 取代材质级 _MapId。
                uint index = (uint)_ClusterVtId;

                //Tile 粒度虚拟贴图：pageId = mip * _SumTextureCount + index * (T*T) + tileIndex，
                //与 ClusterInput.hlsl 的 GetColor 完全一致；T=1 时退化为旧公式（_SumTextureCount * mip + index）。
                int T = _TilesPerAxis;
                int Tsq = T * T;
                //先 Wrap 再切 tile：_MainTex_ST 带 tiling 时 baseUV 会 >1，
                //直接 *T 再 clamp 会把所有重复段压到最后一个 tile（整片拉伸、法线方向全错）。
                //必须与 ClusterInput.hlsl / FeedbackPass.hlsl 逐字一致。T=1 时零回归。
                float2 wrappedBaseUV = frac(input.baseUV);
                float2 tileUV = wrappedBaseUV * (float)T;
                int tileX = clamp((int)floor(tileUV.x), 0, T - 1);
                int tileY = clamp((int)floor(tileUV.y), 0, T - 1);
                int tileIndex = tileY * T + tileX;
                int pageIdMin = (int)minMip * _SumTextureCount + (int)index * Tsq + tileIndex;
                int pageIdMax = (int)maxMip * _SumTextureCount + (int)index * Tsq + tileIndex;
                TextureNode nodeMin = _TextureNodesBuffer[pageIdMin];
                TextureNode nodeMax = _TextureNodesBuffer[pageIdMax];
                float2 localUV = frac(tileUV);
                float2 localDetailUV = frac(input.detailUV * (float)T);
                RVTColors colorsMin = SampleRVT(localUV, nodeMin, localDetailUV);

                RVTColors colors;
                if (maxRadio > minRadio)
                {
                    RVTColors colorsMax = SampleRVT(localUV, nodeMax, localDetailUV);
                    colors = LerpColors(colorsMin, colorsMax, smoothstep(minRadio, maxRadio, mipRadio));
                }
                else
                {
                    colors = colorsMin;
                }

                // ---- 2) 取材质数据 ----
                float4 albedo = GetBase(colors);
                clip(albedo.w - _CullOff);

                float3 normalTS = GetNormalTS(colors);
                float3 normal = normalize(float3(
                    dot(input.TtoW0.xyz, normalTS),
                    dot(input.TtoW1.xyz, normalTS),
                    dot(input.TtoW2.xyz, normalTS)));
                float2 normalPack = PackNormal(normal);

                float metallic = colors.mask.x * _Metallic;
                float roughness = GetRoughness(colors);
                float ambientOcclusion = colors.mask.y;

                // 自发光：直接来自 RVT 的 emission 通道（间接光按既定方向丢弃）。
                float3 emissive = colors.emission.xyz;

                DefferTarget o;
                o._GBufferRT0 = float4(albedo.xyz, metallic);
                float3 normalRT = float3(normalPack, 1);
                o._GBufferRT1 = float4(normalRT, 1);
                o._GBufferRT2 = float4(emissive, roughness);
                o._GBufferRT3 = float4(0.0, 0.0, 0.0, ambientOcclusion);   //反射暂缺，AO 正常
                return o;
            }

            ENDHLSL
        }
    }
}
