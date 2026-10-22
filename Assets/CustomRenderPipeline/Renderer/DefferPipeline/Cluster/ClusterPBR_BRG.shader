// BRG 专用 GBuffer shader —— 用于「附加式 BRG 渲染」（在 DrawRenderers 之外，
// 让每个 cluster 物体在 GBuffer 阶段再被 BRG 画一次）。
//
// 设计要点（这是它能绕过 ClusterPBR 的 DOTS 变体报错的关键）：
//   · 采用与 BRGTest 探针【完全相同】的 DOTS 脚手架（UnityPerMaterial cbuffer +
//     MaterialPropertyMetadata 元数据 + WITH_DEFAULT 宏），该脚手架已实测可被 BRG 正常渲染；
//   · 不依赖 ClusterPBR 那套重 include 链（GI/Surface/Light/BRDF/Lighting/DeffererInput），
//     那套链在 DOTS 变体下会导致 "wrong cbuffer setup"，根因尚未定位，故彻底绕开。
//     本文件只 include Common.hlsl —— 而 Common.hlsl 又是【能画的探针】已经 include 过的，
//     所以它引入的全局资源（RVT 贴图 / _TextureNodesBuffer / _LevelData 等）在 DOTS 下是安全的。
//   · 材质属性名与 ClusterPBR.shader 的 Properties 逐字一致，这样注册时
//     `new Material(真实材质)` 再切 shader，Unity 会按【属性名】自动重映射，值全部带过来。
//
// 数据来源改造（本版重点）：albedo / normal / emission / metallic / roughness / AO
//   全部走工程自己的 RVT（虚拟贴图）路径，与 ClusterPBR 的 OutGBuffer 取数【同源同算法】：
//     _MapId -> _TextureNodesBuffer[mip * _SumTextureCount + _MapId * (T*T) + tileIndex] -> TextureNode
//     -> GetColor() 按 uv 切 tile 算 tileIndex，再按 _LevelData/_LevelPadding 把 tile-local uv 映射到 _VirtualAlbedo 等图集槽位。
//     （T=1 即旧「整图一格」路径，行为完全等价）
//   取数逻辑（SampleRVT / GetBase / GetNormalTS / GetRoughness）逐字移植自
//   ClusterInput.hlsl，数学完全一致，只是不再用 INPUT_PROP 宏而用本地 _XXX 宏。
//
// 本 pass 与 ClusterPBR 的 OutGBuffer 的【唯一差异】：不做光照。
//   · 间接光（烘焙 lightmap / 光照探针 SH）按既定方向【丢弃】（你要自建簇光照，
//     Unity 自带 lightmap 后续再想办法烘进 albedo / emission）；
//   · 间接镜面反射（RT3.rgb）暂写 0（ComputeIndirectSpecular 在重 include 链里，暂不引入）。
//   其余 RT0/R1/R2 的 albedo、packed normal、metallic、roughness、AO、emission 与主份一致。
//
// 注：BRG 这一次写入与 DrawRenderers 那一次写落在同一 MRT、同深度（ZTest LEqual），
//     所以最终 GBuffer 取的是 BRG 这份值（后写覆盖）。这正是 Step 1 想要的
//     「用 BRG 的数据替换/对齐主份」，也是为什么本 shader 必须写真实数据而不是占位色。
Shader "Unlit/ClusterPBR_BRG"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _CullOff("Cull Off", Range(0, 1)) = 0.3
        _MapId("Map Id", int) = 0

        // RVT 图集（运行时绑定）。声明为材质属性是为了让 Unity 沿用纹理自己的 anisoLevel，
        // 否则只能用全局 sampler_linear_repeat（aniso=1），掠射角比标准路径糊。
        [HideInInspector] _VirtualAlbedo("RVT Albedo", 2D) = "white" {}
        [HideInInspector] _VirtualNormal("RVT Normal", 2D) = "white" {}
        [HideInInspector] _VirtualMask("RVT Mask", 2D) = "white" {}
        [HideInInspector] _VirtualEmission("RVT Emission", 2D) = "white" {}
        [HideInInspector] _VirtualDetail("RVT Detail", 2D) = "white" {}
        _NormalScale("Normal Scale", Range(0, 1)) = 0.5
        _Metallic("Metallic", Range(0, 1)) = 0.5
        _Roughness("Roughness", Range(0.05, 1)) = 0.8
        // 1 = 图集 mask 有效；0 = mask 视为纯白(1,1,1,1)。
        // 必须与 DefferStandard 的 GetMask(c) 语义一致：那边是
        //   #if defined(_MASK_MAP) config.useMask = true;  → GetMask 返回白(=1)
        // 运行时由 C# 从【源材质】的 _MASK_MAP keyword 搬运（见 ClusterObject.GetOrCreateBRGMaterial）。
        _UseMask("Use Mask Map", Float) = 1
        // 1 = 图集法线有效；0 = 材质没有法线贴图 ⇒ 直接用平面法线，【跳过法线图集取样】。
        // 由 C# 侧根据源材质的 _NORMAL_MAP keyword 搬运。
        _UseNormal("Use Normal Map", Float) = 1
        // 1 = 图集 emission 有效；0 = 直接填 0，跳过这一次图集取样。
        // 全工程 35 个 RVT 资产里有 34 个 emission 槽是空的 ⇒ 绝大多数材质
        // 每个 tap 都在白采一张 emission 图集（占 RVT 取样量的 1/4）。
        // 由 C# 侧根据 ClusterRenderDataAsset.emission 是否为空搬运。
        _UseEmission("Use Emission", Float) = 1
        _DetailMap("Details", 2D) = "linearGrey" {}
        // detail 法线：与 Standard（DefferStandard）同名，从场景材质克隆时按名带过来。
        // 这一张【不走 RVT 图集】——平铺纹理直采，见 frag 内说明。
        _DetailNormalMap("Detail Normals", 2D) = "bump" {}
        _Color("Main Color", Color) = (1, 1, 1, 1)
        _DetailAlbedo("Detail Albedo", Range(0, 1)) = 0
        _DetailNormal("Detail Normal", Range(0, 1)) = 0
        _DetailRoughness("Detail Roughness", Range(0, 1)) = 0
    }
    SubShader
    {
        // 必须匹配管线 GBufferShaderTagId = "OutGBuffer"，否则 BRG 批次不绘制。
        Tags { "LightMode" = "OutGBuffer" }

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            // Alpha Cutout 开关：与 DefferStandard 的 `_CLIPPING` 同名同义。
            // 【必须】有这个 keyword 门控 —— 否则所有不透明材质都会被下方的 clip 误杀，见 frag 处说明。
            // 用 multi_compile 而不是 shader_feature：BRG 材质是运行时 `new Material(src)`
            // 克隆出来的，构建时没有任何材质资产引用本 shader 的这一变体，
            // shader_feature 会在打包时把 `_CLIPPING` 变体直接剥离掉（编辑器里看不出来）。
            #pragma multi_compile _ _CLIPPING

            #include "../../ShaderLibrary/Common.hlsl"

            // 天空盒 Cubemap（GPUDravinSystem 每帧设成场景天空盒，与 _Sh9 同源）。
            // 主份 GBufferPass 走 ComputeIndirectSpecular(读 unity_SpecCube0)，
            // 这里用工程自己的 _MySkybox 直接采样，避免引入 GI.hlsl 重链（否则 DOTS 下报
            // "wrong cbuffer setup"）。最终延迟光照阶段会据此做菲涅尔 / AO 调制，二者一致。
            TEXTURECUBE(_MySkybox);
            SAMPLER(sampler_MySkybox);

            // ===== detail 直采（业界标准做法）=====
            // detail / detail normal 是【平铺纹理】（本场景 _DetailMap_ST = 6,6），
            // 与 RVT 的「唯一 UV 空间分页」语义天然冲突：
            //   · 图集按 baseUV 分页，用 6× 的 detailUV 去采会越出页边界、串到邻居页；
            //   · 平铺纹理没有"按需驻留"的意义（任何一处都可能被访问，页全得常驻）。
            // 业界（UE Runtime VT / Decima / Far Cry）做法一致：
            //   VT 只承载 unique 的 base 层（albedo/normal/mask/emission），
            //   detail 层走传统 tiled 采样，享受硬件 mipmap + 各向异性过滤，叠加在 VT 结果之上。
            // 代价：每像素 1~2 次额外采样；收益：清晰度不再受 VT 虚拟分辨率限制
            //   （本项目 VT 虚拟分辨率 = CellSize×T = 2048，而 6× 平铺的 4096 detail
            //    有效密度是 4096×6 = 24576 texels/UV，差 12 倍）。
            TEXTURE2D(_DetailMap);
            TEXTURE2D(_DetailNormalMap);
            SAMPLER(sampler_DetailMap);

            // Common.hlsl -> UnityInput.hlsl 已声明：
            //   TextureNode / _TextureNodesBuffer / _SumTextureCount / _VTFeedbackParam
            //   _LevelData[7] / _LevelPadding[7] / _VirtualAlbedo|Normal|Mask|Emission|Detail
            //   以及 DecodeNormal / UnpackNormal / PackNormal / sampler_linear_repeat
            // core CommonMaterial.hlsl 已提供 BlendNormalRNM。

            // ---- 与探针同款：SRP Batcher 兼容的 UnityPerMaterial cbuffer（DOTS 保活的必需件）----
            CBUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(uint,   _MapId)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _DetailMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float,  _NormalScale)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Roughness)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailAlbedo)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailNormal)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailRoughness)
                UNITY_DEFINE_INSTANCED_PROP(float,  _CullOff)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseMask)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseNormal)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseEmission)
            CBUFFER_END

            // ---- DOTS 实例化路径：属性走 MaterialPropertyMetadata，WITH_DEFAULT 保活
            //      UnityInstancingDOTS_InstanceVisibility cbuffer（BRG 校验的必需件）----
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(uint,   _MapId)
                UNITY_DOTS_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DOTS_INSTANCED_PROP(float4, _DetailMap_ST)
                UNITY_DOTS_INSTANCED_PROP(float4, _Color)
                UNITY_DOTS_INSTANCED_PROP(float,  _NormalScale)
                UNITY_DOTS_INSTANCED_PROP(float,  _Metallic)
                UNITY_DOTS_INSTANCED_PROP(float,  _Roughness)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailAlbedo)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailNormal)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailRoughness)
                UNITY_DOTS_INSTANCED_PROP(float,  _CullOff)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseMask)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseNormal)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseEmission)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            // 运行期：BRG 只注册了矩阵 metadata，材质属性高位置 0 -> 回退到 UnityPerMaterial 全局值。
            #define _MapId           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(uint,   _MapId)
            #define _MainTex_ST      UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _MainTex_ST)
            #define _DetailMap_ST    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DetailMap_ST)
            #define _Color           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Color)
            #define _NormalScale     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _NormalScale)
            #define _Metallic        UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Metallic)
            #define _Roughness       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Roughness)
            #define _DetailAlbedo    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailAlbedo)
            #define _DetailNormal    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailNormal)
            #define _DetailRoughness UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailRoughness)
            #define _CullOff         UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _CullOff)
            #define _UseMask         UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseMask)
            #define _UseNormal       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseNormal)
            #define _UseEmission     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseEmission)
            #endif

            // ================= RVT 取数（逐字移植自 ClusterInput.hlsl）=================
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
                float4 baseST = _MainTex_ST;
                return baseUV * baseST.xy + baseST.zw;
            }

            float2 TransformDetailUV(float2 baseUV)
            {
                float4 detailST = _DetailMap_ST;
                return baseUV * detailST.xy + detailST.zw;
            }

            RVTColors SampleRVT(float2 uv, TextureNode node, float2 detailUV)
            {
                float4 levelData = _LevelData[node.mipsLevel];
                float4 levelPadding = _LevelPadding[node.mipsLevel];

                uv = frac(uv);                      //限制 uv 范围，默认都是 Repeat
                detailUV = frac(detailUV);

                //第一个是 offset，然后加到目标位置，再加 Padding
                float2 tileBegin = float2(levelData.x, 0) + levelData.yz * float2(node.width, node.height) + levelPadding.xy;
                uv = tileBegin + levelPadding.zw * uv;
                detailUV = tileBegin + levelPadding.zw * detailUV;

                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1.0 - uv.y;
                    detailUV.y = 1.0 - detailUV.y;
                #endif

                RVTColors colors;
                //每张图集用自己的采样器（跟随纹理 anisoLevel）；用全局 sampler_linear_repeat 会丢掉各向异性过滤
                colors.albedo   = SAMPLE_TEXTURE2D_LOD(_VirtualAlbedo,   sampler_VirtualAlbedo,   uv, 0);
                // 【按材质跳过用不到的图集】—— 本管线 per-pixel 成本的大头就在这些取样上：
                //   · 没有法线贴图的材质 ⇒ 法线图集恒为平面法线，跳过取样直接给 (0.5,0.5,1)；
                //   · 没有 mask map 的材质   ⇒ mask 图集被忽略，跳过取样直接给白 (1,1,1,1)；
                //   · 没有 emission 的材质   ⇒ emission 图集恒为黑，跳过取样。
                // 一次 tap 原本要采 4 张图集，现在典型材质只采 1~2 张 ⇒ per-pixel 取样量直接腰斩。
                colors.normal   = (_UseNormal > 0.5)
                    ? SAMPLE_TEXTURE2D_LOD(_VirtualNormal, sampler_VirtualNormal, uv, 0)
                    : float4(0.5, 0.5, 1.0, 0.0);
                colors.mask     = (_UseMask > 0.5)
                    ? SAMPLE_TEXTURE2D_LOD(_VirtualMask,   sampler_VirtualMask,   uv, 0)
                    : float4(1.0, 1.0, 1.0, 1.0);
                colors.emission = (_UseEmission > 0.5)
                    ? SAMPLE_TEXTURE2D_LOD(_VirtualEmission, sampler_VirtualEmission, uv, 0)
                    : float4(0.0, 0.0, 0.0, 0.0);
                // detail 通道【不再采样】：detail 已改为直采（见 GetBase / TEXTURE2D(_DetailMap) 的说明），
                // RVTColors.detail 现在没有任何读取者。留着这次采样等于每个 tap（每像素 1~8 次）
                // 白付一次图集取样 + 一次结构化随机访问 —— 占 RVT 采样总量的 1/5。
                // 结构体字段保留（AddColors/ScaleColors/LerpColors 与 ClusterInput.hlsl 的副本保持一致），
                // 只是填 0；等确认整条链路稳定后可以把第 5 张图集一起删掉。
                colors.detail   = 0;
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

            RVTColors AddColors(RVTColors a, RVTColors b)
            {
                a.albedo += b.albedo; a.normal += b.normal; a.mask += b.mask;
                a.emission += b.emission; a.detail += b.detail;
                return a;
            }

            RVTColors ScaleColors(RVTColors c, float s)
            {
                c.albedo *= s; c.normal *= s; c.mask *= s; c.emission *= s; c.detail *= s;
                return c;
            }

            // 按 (baseUV, mip) 取一整个 RVTColors：内部完成 tile 切分 + pageId 查表 + 图集采样。
            // 抽出来是为了让「手动各向异性」能在多个偏移位置上重复采样。
            // pageId 编码【唯一出处】：必须与 Feedback Pass、RVTFeedbackCompact.compute 逐字一致，
            // 否则「请求的页 ≠ 采样的页」→ 串色/黑块。法线诊断模式 2 也复用它。
            int GetPageId(float2 baseUV, uint index, uint mip)
            {
                int T = _TilesPerAxis;
                int Tsq = T * T;
                float2 tileUV = frac(baseUV) * (float)T;
                int tileX = clamp((int)floor(tileUV.x), 0, T - 1);
                int tileY = clamp((int)floor(tileUV.y), 0, T - 1);
                int tileIndex = tileY * T + tileX;
                return (int)mip * _SumTextureCount + (int)index * Tsq + tileIndex;
            }

            RVTColors SampleRVTAt(float2 baseUV, float2 detailUV, uint index, uint mip)
            {
                float2 tileUV = frac(baseUV) * (float)_TilesPerAxis;
                TextureNode node = _TextureNodesBuffer[GetPageId(baseUV, index, mip)];
                return SampleRVT(frac(tileUV), node, frac(detailUV * (float)_TilesPerAxis));
            }

            // detail 改为【直采】：不再读 RVT 图集的 _VirtualDetail（平铺语义与分页冲突，会串页）。
            // 与 Standard（PBRInput.GetBase）的 detail 混合公式逐字一致。
            // _DetailAlbedo 由 C# 侧保证：没有 detail 贴图时置 0 → 整段不执行（NaN 历史的根因之一）。
            float4 GetBase(RVTColors colors, float4 detailMap)
            {
                float4 map = colors.albedo;
                float4 color = _Color;

                if (_DetailAlbedo > 0.001)
                {
                    float detail = (detailMap.r * 2.0 - 1.0) * _DetailAlbedo;
                    float mask = colors.mask.b;
                    map.xyz = lerp(sqrt(map.xyz), detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
                    map.rgb *= map.rgb;
                }
                return map * color;
            }

            // ===== 法线诊断工具（ClusterAssembleAsset.normalDebugMode，Play 中实时可切）=====
            // 结果写进 RT0(albedo) 通道；非 0 时 DefferPipeline 的最终 blit 自动切 _DebugAlbedo，
            // 不必再去 RenderPipeline 资产改 Debug Mode。
            // 【排查决策树】（"表面没起伏/法线失效"时按 3 → 5 → 7 顺序看）：
            //   3 图集法线解码后 —— 若有起伏色块：图集数据没问题，问题在下游（TBN / Y 翻转 / GBuffer）
            //                       若一片死板的蓝紫色 (0.5,0.5,1)：图集没数据 → 去看 1 / 2
            //   5 顶点法线（剔掉法线贴图）—— 作为"完全没法线贴图"的基准画面
            //   7 世界法线（当前实际使用的）—— 与 5 完全一样 ⇒ 切线空间法线根本没起作用
            //      （要么 normalTS≈(0,0,1)，要么 TBN 退化/切线为零 → 只剩顶点法线）
            //                                 与 5 不同但方向怪 ⇒ 看 6（Y 翻转 A/B 对照）
            //   1 / 2 —— _MapId / pageId 色块：确认页表有没有按贴图正确分配（串页会张冠李戴）
            float _NormalDebugMode;

            // ===== RVT 取样的全局开关（由 C# 每帧下发，可在 assemble.asset 上切）=====
            // 0 = 单 mip（快，mip 跳变略明显）★默认
            // 1 = min/max 两个 mip 线性混合（原实现，慢一倍）
            float _RVTTrilinearBlend;
            // Dave Hoskins hash：把整数（_MapId / pageId）映射成差异明显的颜色
            float3 Hash01(float n)
            {
                float3 p = frac(float3(n, n, n) * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yzx + 33.33);
                return frac((p.xxy + p.yzz) * p.zyx);
            }

            // 切线空间 → 世界空间（TBN 三行分别存在 TtoW0/1/2 的 xyz 分量里）
            float3 TSToWorld(float3 ts, float3 t0, float3 t1, float3 t2)
            {
                return normalize(float3(dot(t0, ts), dot(t1, ts), dot(t2, ts)));
            }

            float3 GetNormalTS(RVTColors colors, float4 detailNormalMap)
            {
                float4 map = colors.normal;
                float normalScale = _NormalScale;
                // RVT 图集法线是「法线贴图原样拷贝」，用 DecodeNormal 解码 + DecodeNormalSafe 兜底。
                float3 normal = DecodeNormalSafe(map, normalScale);
                // 法线贴图绿通道为 DirectX 约定（Y 朝下），Unity 切线空间是 OpenGL 约定（Y 朝上）→ 必须翻转 Y。
                // 之前 flipNormalY=1 时法线才是对的；按“拆脚手架”删掉后整场景法线方向反、光照全错。
                normal.y *= -1.0;

                // detail 法线【按业界做法重新接入】：直采 _DetailNormalMap（平铺 + 硬件 mip/aniso），
                // 不再读 RVT 图集的 _VirtualDetail。与 Standard（PBRInput.GetNormalTS）公式一致
                // （Standard 的 detail 法线同样不翻 Y，保持一致以对齐参考画面）。
                // 三重安全 —— 历史上这里 NaN 把整条法线链路污染过一次（表面完全光滑）：
                //   ① _DetailNormal 由 C# 侧保证：没有 detail 法线贴图时置 0 → 整段不执行；
                //   ② detail.z 下限 0.05 → BlendNormalRNM 不会除零；
                //   ③ 混合结果非有限（NaN / Inf）→ 直接回退基础法线。
                if (_DetailNormal > 0.001)
                {
                    float3 detail = DecodeNormal(detailNormalMap, 1.0);
                    detail.z = max(detail.z, 0.05);
                    float detailIntensity = _DetailNormal * colors.mask.b;
                    float3 blendNor = BlendNormalRNM(normal, detail);
                    if (!(dot(blendNor, blendNor) < 1.0e6))     // NaN/Inf 兜底
                        return normal;
                    return lerp(normal, blendNor, detailIntensity);
                }
                return normal;
            }

            // 与 Standard（PBRInput.GetRoughness）逐字对齐：detail 用 b 通道、mask 用 g 通道。
            float GetRoughness(RVTColors colors, float4 detailMap)
            {
                float roughness = _Roughness;
                roughness *= colors.mask.w;
                if (_DetailRoughness > 0.001)
                {
                    float detail = (detailMap.b * 2.0 - 1.0) * _DetailRoughness;
                    float mask = colors.mask.g;
                    roughness = lerp(roughness, detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
                }
                return roughness;
            }

            // ================= 顶点 / 像素 =================
            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 baseUV     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS_SS : SV_POSITION;
                float4 TtoW0 : TEXCOORD0;
                float4 TtoW1 : TEXCOORD1;
                float4 TtoW2 : TEXCOORD2;
                float2 baseUV : TEXCOORD3;
                float2 detailUV : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
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
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // TransformObjectToWorld / TransformObjectToWorldNormal 在 DOTS 模式下走
                // UNITY_MATRIX_M / I_M 的 DOTS 重定向（Common.hlsl），即从 BRG 经 metadata
                // 提供的 unity_ObjectToWorld / unity_WorldToObject 按实例索引加载。
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS_SS = TransformWorldToHClip(positionWS);

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                // 【关键修复】非等比缩放下，切线与法线不再正交，直接 cross 得到的 TBN 基是「剪切」的，
                // 法线贴图所有方向会整体偏斜 —— 表现正是"起伏在、方向歪"，且 Y 翻转对它无效。
                // Unity 官方在 non-uniform scale 下同样做 Gram-Schmidt 正交化（CreateTangentToWorld 的前提）。
                float3 orthoT = tangentWS - normalWS * dot(normalWS, tangentWS);
                float orthoTLenSq = dot(orthoT, orthoT);
                // 退化保护：切线几乎平行于法线时 normalize(0) 会产生 NaN（整面变黑），退回原切线。
                tangentWS = orthoTLenSq > 1e-8 ? orthoT * rsqrt(orthoTLenSq) : tangentWS;
                float3 binormalWS = cross(normalWS, tangentWS) * input.tangentOS.w;

                output.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
                output.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
                output.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);

                output.baseUV = TransformBaseUV(input.baseUV);
                output.detailUV = TransformDetailUV(input.baseUV);
                return output;
            }

            DefferTarget frag (Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // ---- 1) 各向异性 mip 选择 + 多 tap 采样 ----
                // 【为什么必须手动做】RVT 图集是【没有 mipmap】的（mipCount=0；页表这种平铺布局也
                // 不能加 mipmap，否则 tile 之间会串页）。而各向异性过滤的本质是「沿长轴取粗 mip、
                // 沿短轴取细 mip」的多次采样——没有 mip 链，Texture.anisoLevel 就完全无效。
                //
                // 所以这里自己实现：
                //   · mip 由【短轴】导数决定 → 保住短轴方向的高频（桶的竖向木纹就是这一路）；
                //   · 再沿【长轴】方向做 2~4 次偏移采样求和 → 把长轴方向的欠采样积分掉。
                // 原写法是「max(dx,dy) 选 mip + 单次采样」，圆柱侧面、地面这类掠射角会整体糊掉
                // 一级以上，且表现为「横向的金属箍清楚、竖向木纹消失」——正是缺各向异性的典型症状。
                //
                // mip 仍按虚拟贴图总分辨率（CellSize × T）算；与 Feedback Pass 必须逐字一致，
                // 否则请求的页 ≠ 采样的页。
                float V = _VTFeedbackParam.y * (float)max(1, _TilesPerAxis);
                float2 uv = input.baseUV * V;
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);

                float lenX = length(dx), lenY = length(dy);
                float majorLen = max(lenX, lenY);
                float minorLen = min(lenX, lenY);
                float anisoRatio = majorLen / max(minorLen, 1e-6);
                //长轴方向的单位向量（缩放后 UV 空间），以及短轴导数向量
                float2 majorDir = (lenX >= lenY ? dx : dy) / max(majorLen, 1e-6);
                float2 minorVec = (lenX < lenY) ? dx : dy;

                float mipRadio = clamp(
                    _VTFeedbackParam.x * log2(max(dot(minorVec, minorVec), 1e-12)) + _VTFeedbackParam.w,
                    0.0, _VTFeedbackParam.z);

                float maxRadio = ceil(mipRadio), minRadio = floor(mipRadio);
                uint maxMip = (uint)maxRadio, minMip = (uint)minRadio;
                uint index = _MapId;

                //一个像素在长轴方向跨越的 UV 距离（转回 baseUV 空间）
                float2 majorStepUV = majorDir * (majorLen / V);
                // taps 阈值（2026-10-20 调高）：原来 >6 就 4 taps、>2 就 2 taps，
                // 掠射角（ratio 6~12）也要付 4 倍取样。现在 >12 才 4 taps、>4 才 2 taps，
                // 中间区间退回单 tap —— 取样量在掠射角上直接减半。
                int taps = (anisoRatio > 12.0) ? 4 : ((anisoRatio > 4.0) ? 2 : 1);

                RVTColors colors;
                colors.albedo = 0; colors.normal = 0; colors.mask = 0;
                colors.emission = 0; colors.detail = 0;

                for (int ti = 0; ti < taps; ti++)
                {
                    //taps=1 时 t=0（不偏移）；2 次取 ±0.5；4 次均匀铺满 [-0.5,0.5]
                    float t = (taps > 1) ? ((float)ti / (float)(taps - 1) - 0.5) : 0.0;
                    float2 uvTap = input.baseUV + majorStepUV * t;

                    RVTColors c;
                    if (_RVTTrilinearBlend > 0.5 && maxRadio > minRadio)
                    {
                        // 原实现：min/max 两个 mip 各采一次再线性混合（= mip 间三线性）
                        RVTColors cMin = SampleRVTAt(uvTap, input.detailUV, index, minMip);
                        RVTColors cMax = SampleRVTAt(uvTap, input.detailUV, index, maxMip);
                        c = LerpColors(cMin, cMax, smoothstep(minRadio, maxRadio, mipRadio));
                    }
                    else
                    {
                        // 【单 mip】—— 本管线 per-pixel 成本最大的一刀。
                        // 原来每个 tap 要 2 次 SampleRVTAt = 2 次页表随机读 + 2×N 张图集取样；
                        // 单 mip 直接取最近的一级，**这些全部减半**。
                        // 业界（UE RVT）同样是每页一个 mip、不做 mip 间三线性混合。
                        // 代价：mip 跳变（远处高频闪烁）会比原来明显一点。
                        uint mip = (uint)(mipRadio + 0.5);
                        if (mip > maxMip) mip = maxMip;
                        if (mip < minMip) mip = minMip;
                        c = SampleRVTAt(uvTap, input.detailUV, index, mip);
                    }
                    colors = AddColors(colors, c);
                }
                colors = ScaleColors(colors, 1.0 / (float)taps);

                // ===== mask 白回退：与 DefferStandard 的 GetMask(c) 逐字对齐 =====
                // Standard（GBufferPass.hlsl + PBRInput.hlsl）：
                //     #if defined(_MASK_MAP)  config.useMask = true;  #endif
                //     float4 GetMask(InputConfig c){ if(c.useMask) return SAMPLE_TEXTURE2D(_MaskMap,...); return 1.0; }
                //     float GetOcclusion(c){ float o = 1; if(c.useMask) o = GetMask(c).y; return o; }
                // 也就是说「材质没有 mask map」时 mask 恒为【白】：metallic=_Metallic、roughness=_Roughness、AO=1。
                //
                // 而本 shader 原来【无条件】吃图集 mask，而图集 mask 来自 ClusterRenderDataAsset.pbrData。
                // 本工程有 5 个资产的 pbrData 被填成了【高光/光泽图】而不是 mask
                //（场景材质的 _MaskMap 是空的、_MASK_MAP keyword 也没开，全工程 _MaskMap 一律是 *_Mask.png / *_o.tif）：
                //     Boulder_01 / Menhir_01 / Rune_01 → prop_boulderrunemenhirskull_01_sg.tif
                //     Fish_01                        → prop_fish_01_sg.tif
                //     Logpile_01                     → prop_barrelbucketboardlogpile_01_m.tif
                // 高光/光泽图的 g 通道普遍接近 0 → AO ≈ 0 → 最终延迟光照阶段
                //（DefferCameraPass.hlsl:139/150）把间接漫反射与反射【整体乘 0】
                // → 背光面在延迟管线里呈纯黑；而 Edit / 标准路径 AO=1 所以看起来完全正常。
                // 这就是「几何和法线都对，只有这几个物体颜色（发黑）不对」的原因。
                colors.mask = lerp(float4(1.0, 1.0, 1.0, 1.0), colors.mask, saturate(_UseMask));

                // ---- 2) detail 直采（不走 RVT 图集）----
                // 说明见文件顶部 TEXTURE2D(_DetailMap) 处的注释：
                // VT 只承载 unique 的 base 层，平铺的 detail 层用硬件 mipmap + 各向异性直采后叠加。
                // 三个强度（_DetailAlbedo / _DetailNormal / _DetailRoughness）由 C# 侧在
                // 没有对应贴图时置 0，所以这里的分支既是性能优化也是 NaN 安全闸门。
                float4 detailMap = float4(0.5, 0.5, 0.5, 1.0);
                float4 detailNormalMap = float4(0.5, 0.5, 1.0, 1.0);
                if (_DetailAlbedo > 0.001 || _DetailRoughness > 0.001)
                    detailMap = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, input.detailUV);
                if (_DetailNormal > 0.001)
                    detailNormalMap = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailMap, input.detailUV);

                // ---- 3) 取材质数据 ----
                float4 albedo = GetBase(colors, detailMap);
                // Alpha Cutout【只对 _CLIPPING 材质生效】，与 Standard 逐字对齐：
                //   DefferStandard（GBufferPass.hlsl）写的是
                //       #if defined(_CLIPPING)
                //           clip(base.a - GetCutoff());
                //       #endif
                // 而本文件此前是【无条件】clip(albedo.w - _CullOff) —— 这是个严重 bug：
                //   _CullOff 在材质上恒有值（DefferStandard 默认 0.5），一旦某张 albedo 的
                //   alpha 通道本来就不是"覆盖率"（很多贴图的 alpha 是空的/存别的数据/被压缩成 0），
                //   该物体就会被整片裁掉 → 表现为地面上"凭空出现黑洞/缺块"，且 Edit 模式
                //   （走原材质，没有 _CLIPPING）完全正常。
                // 全工程只有 3 个材质开了 _CLIPPING（mat_strawroof_cutout_01 / mat_fish_01 /
                // LitDefferer），其余一律不该裁剪。
                #if defined(_CLIPPING)
                    clip(albedo.w - _CullOff);
                #else
                    // 恒真（_CullOff ∈ [0,1] → +1 后 > 0），只为把 _CullOff 留在编译产物里：
                    // BRG/DOTS 的 cbuffer 布局校验要求 UnityPerMaterial 成员不能被优化掉，
                    // ShadowCaster pass 里那句 clip(INPUT_PROP(float,_CullOff)+1.0) 是同一用途。
                    clip(_CullOff + 1.0);
                #endif

                float3 normalTS = GetNormalTS(colors, detailNormalMap);
                float3 normal = normalize(float3(
                    dot(input.TtoW0.xyz, normalTS),
                    dot(input.TtoW1.xyz, normalTS),
                    dot(input.TtoW2.xyz, normalTS)));
                float2 normalPack = PackNormal(normal);

                float metallic = colors.mask.x * _Metallic;
                float roughness = GetRoughness(colors, detailMap);
                float ambientOcclusion = colors.mask.y;

                // 自发光：直接来自 RVT 的 emission 通道。
                // 注意：BRG 这一份【不再丢弃】天空盒相关间接光——
                //   · 镜面反射：上面已写入 RT3.rgb（天空盒 _MySkybox）；
                //   · 漫反射 SH9 间接光：由延迟最终光照阶段（DrawGBufferColorFragment）
                //     用 _Sh9 统一计算并叠加，主份与 BRG 共用同一处，避免 DOTS 下
                //     在 BRG shader 里再引入 GI.hlsl 重链报 "wrong cbuffer setup"。
                float3 emissive = colors.emission.xyz;

                // 天空盒反射：BRG 接管绘制后，这里单独补上天空盒（_MySkybox）的镜面反射，
                // 否则最终延迟光照阶段读到的 RT3.rgb 为 0，BRG 物体完全没有反射。
                // 主份 GBufferPass 写的是 ComputeIndirectSpecular(读 unity_SpecCube0)，
                // 这里用 _MySkybox（= 场景天空盒 Cubemap）采样，最终阶段的菲涅尔 / AO 调制保持一致。
                float3 positionWS = float3(input.TtoW0.w, input.TtoW1.w, input.TtoW2.w);
                float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
                float3 reflectDir = reflect(-viewDir, normal);
                // 与 Standard（DefferStandard / GBufferPass.hlsl:99-104）【逐字对齐】，用户明确要求
                // “抄 Standard”。之前 BRG 漏了 reflIntensity 这一项，导致无论粗糙度/金属度高低，
                // 反射都是满强度天空盒 —— 非金属/粗糙面也像镜子一样“过于光滑”，正是“该光滑还是很光滑”
                // 的根因之一。修正：
                //   · reflMip 改用 clamp(roughness*7, 0, 8)（与 GBufferPass.hlsl:101 一致）；
                //   · 关键补上 reflIntensity = lerp(saturate(0.05+(1-roughness)*0.6), 1, metallic)
                //     （GBufferPass.hlsl:103-104）：粗糙/非金属面反射大幅衰减，金属保持完整，
                //     与 Standard 反射强度完全一致。
                //   · _MySkybox 与 unity_SpecCube0 本场景同源于场景天空盒，内容一致，故替换采样源不影响强度。
                // 注：最终合成阶段（DrawGBufferColorFragment）仍会再乘菲涅尔(F0≈0.14)与 AO。
                float reflMip = clamp(roughness * 7.0, 0.0, 8.0);
                float3 skyRefl = SAMPLE_TEXTURECUBE_LOD(_MySkybox, sampler_MySkybox, reflectDir, reflMip).rgb;
                float reflIntensity = lerp(saturate(0.05 + (1.0 - roughness) * 0.6), 1.0, metallic);
                skyRefl *= reflIntensity;

                DefferTarget o;
                // ===== 法线诊断输出（ClusterAssembleAsset.normalDebugMode）=====
                // 结果覆盖 RT0(albedo)，配 DefferDebugMode.Albedo 查看；0 = Off，不改渲染。
                // 只留「法线链路逐层排查」这一条主线所需的 7 项：
                //   1 贴图索引 → 2 页索引 → 3 图集法线(解码后) / 15 图集法线(原始字节)
                //   → 5 顶点法线（剔除贴图因素）→ 6 / 7 世界法线的 Y 翻转 A/B 对照
                float ndm = _NormalDebugMode;
                if (ndm > 0.5f)
                {
                    float3 dbg;
                    if      (ndm < 1.5f) dbg = Hash01((float)index);        //1  _MapId：同贴图物体应同色
                    else if (ndm < 2.5f) dbg = Hash01((float)GetPageId(input.baseUV, index, minMip));    //2  pageId：页表是否按贴图分配
                    else if (ndm < 3.5f) dbg = DecodeNormal(colors.normal, 1.0) * 0.5 + 0.5;  //3  图集法线【解码后】
                    else if (ndm < 5.5f) dbg = TSToWorld(float3(0, 0, 1), input.TtoW0.xyz, input.TtoW1.xyz, input.TtoW2.xyz) * 0.5 + 0.5;  //5  顶点法线（剔掉法线贴图）
                    else if (ndm < 6.5f) dbg = TSToWorld(float3(normalTS.x, -normalTS.y, normalTS.z), input.TtoW0.xyz, input.TtoW1.xyz, input.TtoW2.xyz) * 0.5 + 0.5;  //6  世界法线【撤销 Y 翻转】对照组
                    else if (ndm < 7.5f) dbg = normal * 0.5 + 0.5;          //7  世界法线【翻 Y】当前默认行为
                    else                 dbg = colors.normal.rgb;           //15 图集法线【原始字节】判黑/白/常量
                    o._GBufferRT0 = float4(dbg, 1.0);
                }
                else
                {
                    o._GBufferRT0 = float4(albedo.xyz, metallic);
                }
                float3 normalRT = float3(normalPack, 1);
                o._GBufferRT1 = float4(normalRT, 1);
                o._GBufferRT2 = float4(emissive, roughness);
                o._GBufferRT3 = float4(skyRefl, ambientOcclusion);   //天空盒反射 + AO
                return o;
            }

            ENDHLSL
        }

        // ===== Feedback Pass（让 BRG 批次也能向 RVT 反馈 (texId,mip)）=====
        // 背景：BRG 接管绘制后物体的 MeshRenderer 被禁用，context.DrawRenderers("Feedback")
        // 根本画不到它们；而 GPUDravinSystem.DrawFeedback 只遍历注册了 feedbackEvent 的节点，
        // 本工程从未给 BRG 注册 feedbackEvent → BRG 物体的 (texId,mip) 永不进反馈 →
        // TextureNodesMap 永不分配/流式载入这些物体的图集页 → GetColor 采样到未初始化的页
        // （width/height/mipsLevel 全 0）→ 法线/粗糙度/AO 通道错乱（粗糙度≈0 → 异常光滑）、
        // albedo 取错页 → 与「BRG 关」对不上。这正是"开了 BRG 很光滑 + 颜色不对应"的根因。
        // 修复：本 pass 与 ClusterPBR.shader(ClusterInput.hlsl)/FeedbackPass.hlsl 用【完全一致】
        // 的 pageId 编码（mip * _SumTextureCount + texId*T*T + tileIndex）。RenderFeedback 里已有的
        // context.DrawRenderers(cullingResults, "Feedback") 会回调 BRG 的 OnPerformCulling，使其
        // 用本 pass（LightMode=Feedback）绘制，从而正常提交反馈（MeshRenderer 是否启用对此无影响，
        // BRG 批次由 OnPerformCulling 自行管理）。
        Pass
        {
            Tags { "LightMode" = "Feedback" }
            ZWrite On
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FeedbackVertex
            #pragma fragment FeedbackFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "../../ShaderLibrary/Common.hlsl"

            CBUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(uint,   _MapId)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _DetailMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float,  _NormalScale)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Roughness)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailAlbedo)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailNormal)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DetailRoughness)
                UNITY_DEFINE_INSTANCED_PROP(float,  _CullOff)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseMask)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseNormal)
                UNITY_DEFINE_INSTANCED_PROP(float,  _UseEmission)
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(uint,   _MapId)
                UNITY_DOTS_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DOTS_INSTANCED_PROP(float4, _DetailMap_ST)
                UNITY_DOTS_INSTANCED_PROP(float4, _Color)
                UNITY_DOTS_INSTANCED_PROP(float,  _NormalScale)
                UNITY_DOTS_INSTANCED_PROP(float,  _Metallic)
                UNITY_DOTS_INSTANCED_PROP(float,  _Roughness)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailAlbedo)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailNormal)
                UNITY_DOTS_INSTANCED_PROP(float,  _DetailRoughness)
                UNITY_DOTS_INSTANCED_PROP(float,  _CullOff)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseMask)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseNormal)
                UNITY_DOTS_INSTANCED_PROP(float,  _UseEmission)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            #define _MapId           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(uint,   _MapId)
            #define _MainTex_ST      UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _MainTex_ST)
            #define _DetailMap_ST    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DetailMap_ST)
            #define _Color           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Color)
            #define _NormalScale     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _NormalScale)
            #define _Metallic        UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Metallic)
            #define _Roughness       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Roughness)
            #define _DetailAlbedo    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailAlbedo)
            #define _DetailNormal    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailNormal)
            #define _DetailRoughness UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _DetailRoughness)
            #define _CullOff         UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _CullOff)
            #define _UseMask         UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseMask)
            #define _UseNormal       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseNormal)
            #define _UseEmission     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _UseEmission)
            #endif

            struct FeedbackAttributes
            {
                float3 positionOS : POSITION;
                float2 baseUV     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct FeedbackVaryings
            {
                float4 positionCS_SS : SV_POSITION;
                float2 baseUV        : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            FeedbackVaryings FeedbackVertex(FeedbackAttributes input)
            {
                FeedbackVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS_SS = TransformWorldToHClip(positionWS);
                float4 baseST = _MainTex_ST;
                output.baseUV = input.baseUV * baseST.xy + baseST.zw;
                return output;
            }

            float4 FeedbackFragment(FeedbackVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                //mip 按虚拟贴图总分辨率（CellSize × T）算，与 GBuffer Pass 逐字一致。
                //【必须用短轴(min)】GBuffer 现在按短轴选 mip 再做长轴多 tap，这里若还用 max，
                //就会「请求粗页、采样细页」→ 采到未分配的页，画面串色/黑块。两处绑定必须完全一致。
                float2 uv = input.baseUV * (_VTFeedbackParam.y * (float)max(1, _TilesPerAxis));
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);
                float lenX = length(dx), lenY = length(dy);
                float2 minorVec = (lenX < lenY) ? dx : dy;
                uint mip = clamp(uint(_VTFeedbackParam.x * log2(max(dot(minorVec, minorVec), 1e-12)) +
                    _VTFeedbackParam.w), 0, _VTFeedbackParam.z);
                uint index = _MapId;
                uint T = (uint)_TilesPerAxis;
                uint Tsq = T * T;
                //先 Wrap 再切 tile：_MainTex_ST 带 tiling 时 baseUV 会 >1，
                //直接 *T 再 clamp(0,T-1) 会把所有重复段压到最后一个 tile ——
                //整片被一张 tile 拉伸铺满（法线表现为"方向全错"）。T=1 时零回归。
                //注意：Feedback Pass 与本 Pass 必须逐字一致，否则请求的页和采样的页不是同一块。
                float2 wrappedBaseUV = frac(input.baseUV);
                float2 tileUV = wrappedBaseUV * (float)T;
                uint tileX = (uint)clamp(floor(tileUV.x), 0.0, (float)(T - 1u));
                uint tileY = (uint)clamp(floor(tileUV.y), 0.0, (float)(T - 1u));
                uint tileIndex = tileY * T + tileX;
                uint pageId = (uint)mip * (uint)_SumTextureCount + index * Tsq + tileIndex;
                uint r = pageId & 255u;
                uint g = (pageId >> 8) & 255u;
                uint b = (pageId >> 16) & 255u;
                uint a = (pageId >> 24) & 255u;
                return float4((float)r / 255.0, (float)g / 255.0, (float)b / 255.0, (float)a / 255.0);
            }

            ENDHLSL
        }

/* =====================================================================================
   ShadowCaster Pass —— 已整段注释掉（2026-10-21）===== 不要随便恢复 =====

   为什么删：
   BRG 接管阴影后，阴影 pass 从 0.96ms 涨到 4.8ms。根因是 BRG 只能按
   (mesh, 材质, submesh) 合批，本场景 134 条 draw command × 高固定开销 ≈ 4.3ms，
   而 MeshRenderer 走 SRP Batcher 按材质合批（4 个 batch）只要 ~1ms。
   结论：**BRG 只画 GBuffer，阴影还给 MeshRenderer**（物体挪到专用 layer，
   GBuffer pass 排除该 layer，MeshRenderer 保持开启并保留场景原材质）。

   为什么用「删 pass」而不是运行时开关：
   Unity 的 context.DrawShadows 是按 **LightMode = "ShadowCaster"** 这个 tag 找 pass 的。
   没有这个 tag ⇒ 根本找不到可画的 pass ⇒ 不会被画进阴影图。
   这比下面的做法都干净：
     · 在 OnPerformCulling 里不发命令 —— 仍要为阴影视图跑一遍 Pass A + 分配内存；
     · 设 BatchFilterSettings.shadowCastingMode = Off —— 实测 2022.3 的 SRP
       context.DrawShadows **并没有干净地拦住**（剔除回调照跑、耗时照旧 4.3ms）。

   ⚠️ 恢复本 pass 的后果：BRG 批次会重新投阴影，阴影 pass 回到 ~4.3ms；
      且 UnityPerMaterial cbuffer 必须三处逐字一致（GBuffer / Feedback / 本 pass
      引入的 ClusterDOTSMaterial.hlsl），漏一处就是 "CBuffer inconsistent size"
      ⇒ pass 静默不画（画面正常但阴影缺失）。

   下面是原实现，保留作参考（与 ClusterPBR.shader 的 ShadowCaster 一致：只写深度
   ColorMask 0、处理 _ShadowPancaking、用 INPUT_PROP(_CullOff) 保活 cbuffer）。
======================================================================================
        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "../../ShaderLibrary/Common.hlsl"
            #include "Input/ClusterDOTSMaterial.hlsl"

            bool _ShadowPancaking;

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS_SS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowCasterPassVertex (Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // 与 GBuffer pass 同款变换链：TransformObjectToWorld -> TransformWorldToHClip
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS_SS = TransformWorldToHClip(positionWS);

                if (_ShadowPancaking)
                {
                    #if UNITY_REVERSED_Z
                        output.positionCS_SS.z = min(output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE);
                    #else
                        output.positionCS_SS.z = max(output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE);
                    #endif
                }
                return output;
            }

            void ShadowCasterPassFragment (Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                // 保活引用：把 UnityPerMaterial / metadata cbuffer 留在编译产物里，
                // 防止被编译器优化掉后 BRG 校验 "wrong cbuffer setup"。
                clip(INPUT_PROP(float, _CullOff) + 1.0);
            }

            ENDHLSL
        }
   ========================== ShadowCaster Pass 结束（已注释掉） ========================== */
    }
}
