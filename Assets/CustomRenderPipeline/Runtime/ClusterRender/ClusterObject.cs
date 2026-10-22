using UnityEditor;
using UnityEngine;

namespace Cluster
{
    [ExecuteAlways]
    public class ClusterObject : MonoBehaviour
    {

        public ClusterRenderDataAsset[] renderAssets;
        public int[] indexs;

        // 当 BRG 作为该物体的【唯一】渲染路径时（useBRG 开），我们关闭了 MeshRenderer，
        // 让 BRG 独占绘制（避免与 DrawRenderers 双重绘制导致帧率崩塌）。本标志用于
        // OnDisable/退出播放时恢复显示。
        private bool m_brgDisabledRenderer = false;

        // BRG 专用 GBuffer shader（基于已验证可画的探针脚手架，写真实 RVT 数据）。
        // 用它彻底绕开 ClusterPBR 在 DOTS 变体下的 "wrong cbuffer setup" 问题。
        private static Shader s_brgShader;

        /// <summary>「BRG 材质的 pass 列表」只打印一次。</summary>
        private static bool s_loggedBRGShader;

        private static Shader BRGShader
        {
            get
            {
                if (s_brgShader == null)
                    s_brgShader = Shader.Find("Unlit/ClusterPBR_BRG");
                return s_brgShader;
            }
        }

        // ===== 「阴影交给 MeshRenderer、BRG 只画 GBuffer」模式 =====
        // 2026-10-20 实测：BRG 接管阴影后，阴影 pass 从 0.96ms 涨到 4.8ms
        //（BRG 每条 batch 一条 draw command，134 条；而 Unity 的 SRP Batcher 把
        //  1219 个投射物按【材质】压成 4 个 SRP Batch）。
        // ⇒ 方案：BRG 只画 GBuffer，阴影仍由 MeshRenderer 走 Unity 常规路径。
        //   · MeshRenderer 保持 enabled，材质换成【RVT AssetMat（Unlit/ClusterPBR）】
        //     ⇐ 2026-10-21 修正：原来"保留场景原材质"会让 ShadowCaster 走
        //       CustomRP/Defferer/DefferStandard，比 Unlit/ClusterPBR 贵 ~40%
        //       （实测阴影 3.956ms vs 2.859ms，投射物数量与 draw call 数都一样）。
        //       shadow-only 模式下 MeshRenderer 只投阴影，用极简 ShadowCaster 才对。
        //   · 物体挪到专用 layer，GBuffer pass 与 DrawUnsupportedShaders 排除该 layer
        //     （否则 MeshRenderer 会把 GBuffer 也画一遍 = 双重绘制）；
        //   · **最关键的一步（2026-10-21）**：`Unlit/ClusterPBR_BRG` 已经整段删掉了
        //     ShadowCaster pass。Unity 的 context.DrawShadows 是按
        //     **LightMode = "ShadowCaster"** 这个 tag 找 pass 的，找不到就完全不会画，
        //     这是最干净的拦截点（比 shadowCastingMode / 剔除回调里不发命令都可靠）。
        //     副作用：`brgGBufferExcludeLayer = -1` 的旧模式现在等价于"阴影全无"，
        //     要让 BRG 重新投阴影必须先恢复那个 pass。
        // 前提（运行时校验）：相机与灯光的 cullingMask 都要包含该 layer
        //   —— 不满足时会自动补位并告警一次。
        private bool m_shadowOnlyMode = false;
        private int m_originalLayer = -1;

        // shadow-only 模式切换材质用：
        //   m_rvtMaterials   = AssetMat（Unlit/ClusterPBR，ShadowCaster 极简）← 投阴影时用
        //   m_sceneMaterials = 场景原材质（DefferStandard）                    ← 退出该模式时还原
        // 两者都在 OnEnable 里填好，且 m_sceneMaterials 必须在 meshRender.materials 被覆盖前抓。
        private Material[] m_rvtMaterials;
        private Material[] m_sceneMaterials;

        // ===== 【合并批次的关键】源材质 -> BRG 专用材质 的缓存（按源材质去重）=====
        // BatchRenderManager 是以 (mesh, 材质) 为 key 来合并批次的：key 相同才并成同一条
        // 多实例 batch。若每个物体都克隆一份自己的材质，则每条 batch 只会有 1 个实例，
        // 合并彻底失效（Frame Debugger 里就是一个物体一个 "Hybrid Batch Group"）。
        // 所以这里让「共用同一个源材质的物体」复用同一个 BRG 材质实例。
        // BatchRenderManager 是以 (mesh, 材质) 为 key 来合并批次的：key 相同才并成同一条
        // 多实例 batch。若每个物体都克隆一份自己的材质，则每条 batch 只会有 1 个实例，
        // 合并彻底失效（Frame Debugger 里就是一个物体一个 "Hybrid Batch Group"）。
        //
        // 【注意】缓存 key 必须包含 _MapId（即 RVT 贴图索引）。
        // 之前只按「源材质」做 key，而 _MapId 写在【资产级共享材质】上，会被引用同一材质的
        // 其他 submesh / 其他物体覆盖，于是同一物体的多个 submesh 共用同一份 BRG 材质、
        // 只有最后写入的 _MapId 生效 —— 桶的不同部分全去采同一张 RVT 贴图，
        // 表现为「同一个桶颜色明显不同、法线/反射对不上」。
        // 现在按 (源材质, _MapId) 二级缓存：同贴图同材质的物体仍并进同一条 batch（合并不失效），
        // 不同贴图则各自持有带正确 _MapId 的材质实例。
        private static readonly System.Collections.Generic.Dictionary<Material,
            System.Collections.Generic.Dictionary<int, Material>> s_brgMaterialCache
            = new System.Collections.Generic.Dictionary<Material,
                System.Collections.Generic.Dictionary<int, Material>>();

        // ===== RVT 图集绑定（材质属性 + anisoLevel）=====
        // 图集在 ClusterCore.Initialize 里创建，时机晚于本组件的 OnEnable，所以用静态字段暂存，
        // 在两处应用：① ClusterCore 创建图集后调 ApplyAtlasTextures 回填【已经克隆/已使用】的材质；
        // ② 之后新建的克隆材质在 GetOrCreateBRGMaterial 里立即绑定。
        // 为什么要绑成材质属性：Unity 只有在采样器对应「材质属性纹理」时才沿用该纹理自己的
        // anisoLevel。用纯全局纹理 + sampler_linear_repeat，aniso 恒为 1，掠射角会明显糊。
        private static Texture s_atlasAlbedo, s_atlasNormal, s_atlasMask, s_atlasEmission, s_atlasDetail;

        /// <summary>ClusterObject 用到的所有源材质（AssetMat），供 ApplyAtlasTextures 回填</summary>
        private static readonly System.Collections.Generic.HashSet<Material> s_sourceMaterials
            = new System.Collections.Generic.HashSet<Material>();

        public static void ApplyAtlasTextures(Texture albedo, Texture normal, Texture mask,
            Texture emission, Texture detail)
        {
            s_atlasAlbedo = albedo; s_atlasNormal = normal; s_atlasMask = mask;
            s_atlasEmission = emission; s_atlasDetail = detail;

            foreach (var src in s_sourceMaterials) BindAtlas(src);
            foreach (var byMapId in s_brgMaterialCache.Values)
                foreach (var mat in byMapId.Values)
                    BindAtlas(mat);
        }

        private static void BindAtlas(Material mat)
        {
            if (mat == null) return;
            if (s_atlasAlbedo   != null) mat.SetTexture("_VirtualAlbedo",   s_atlasAlbedo);
            if (s_atlasNormal   != null) mat.SetTexture("_VirtualNormal",   s_atlasNormal);
            if (s_atlasMask     != null) mat.SetTexture("_VirtualMask",     s_atlasMask);
            if (s_atlasEmission != null) mat.SetTexture("_VirtualEmission", s_atlasEmission);
            if (s_atlasDetail   != null) mat.SetTexture("_VirtualDetail",   s_atlasDetail);
        }

        private static Material GetOrCreateBRGMaterial(Material src, int mapId, bool hasEmission)
        {
            if (src == null) return null;
            if (!s_brgMaterialCache.TryGetValue(src, out var byMapId))
            {
                byMapId = new System.Collections.Generic.Dictionary<int, Material>();
                s_brgMaterialCache[src] = byMapId;
            }
            if (byMapId.TryGetValue(mapId, out Material cached) && cached != null)
                return cached;
            if (BRGShader == null)
            {
                Debug.LogError("[BRG] 找不到 shader Unlit/ClusterPBR_BRG，本次回退用真实材质" +
                    "（很可能再次报 wrong cbuffer setup）。");
                return src;
            }
            Material m = new Material(src);
            m.shader = BRGShader;   // Shader 切换时 Unity 按【属性名】自动重映射，属性值全部带过来

            // 【一次性诊断】确认运行时的 BRG 材质到底有没有 ShadowCaster pass。
            // 这是判断「BRG 还在不在投阴影」唯一可信的证据 —— 画面和耗时都能骗人，pass 列表不会。
            if (!s_loggedBRGShader)
            {
                s_loggedBRGShader = true;
                int pc = BRGShader.passCount;
                int shadowPass = -1;
                var sb = new System.Text.StringBuilder();
                var lmTag = new UnityEngine.Rendering.ShaderTagId("LightMode");
                for (int pi = 0; pi < pc; pi++)
                {
                    var v = BRGShader.FindPassTagValue(pi, lmTag);
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(pi).Append(':').Append(v.name);
                    if (v.name == "ShadowCaster") shadowPass = pi;
                }
                Debug.Log($"[BRG] BRG 材质 shader=\"{BRGShader.name}\" pass 列表 [{sb}] | ShadowCaster=" +
                          (shadowPass < 0 ? "无（符合预期）" : shadowPass + "（← 删 pass 没生效！）") +
                          $" | 源材质=\"{src.name}\"({src.shader.name})");
            }
            // 关键：_MapId 写在【克隆出来的 BRG 材质】上，而不是共享 AssetMat，
            // 否则多 submesh / 多物体之间会互相覆盖。
            m.SetInt("_MapId", mapId);

            // ===== Alpha Cutout 开关（_CLIPPING）必须显式搬运 =====
            // BRG shader 的 clip 现在与 DefferStandard 一样用 `#if defined(_CLIPPING)` 门控，
            // 不搬这个 keyword 的话，真正的镂空材质（草屋顶 / 鱼）会变成整块实心。
            // `new Material(src)` 会带上 keyword，但换 shader 时 Unity（2021.2+）会把
            // 新 shader 未声明的 keyword 挪进 invalid 列表，这里显式再设一次，
            // 避免以后调整 shader keyword 声明时静默失效。
            if (src.IsKeywordEnabled("_CLIPPING")) m.EnableKeyword("_CLIPPING");
            else m.DisableKeyword("_CLIPPING");

            // ===== mask 白回退开关（必须与 DefferStandard 的 GetMask 语义一致）=====
            // Standard：#if defined(_MASK_MAP) config.useMask = true; → GetMask 返回白(=1)
            //          → metallic = _Metallic、roughness = _Roughness、AO = 1
            // BRG 原来无条件吃图集 mask，而图集 mask 来自 ClusterRenderDataAsset.pbrData。
            // 本工程 5 个资产的 pbrData 填的是【高光/光泽图】（*_sg.tif）而不是 mask
            // （场景材质 _MaskMap 为空、_MASK_MAP 也没开）→ AO≈0 → 间接光与反射被整体乘 0
            // → 石头/巨石/鱼/木堆在 Play 里变成纯黑，Edit 里却正常。
            // 这里把源材质 keyword 的真值搬过来：没有 mask map 就让 shader 用白 mask。
            m.SetFloat("_UseMask", src.IsKeywordEnabled("_MASK_MAP") ? 1f : 0f);

            // ===== 没有法线贴图 ⇒ 跳过法线图集取样 =====
            // 与 Standard 的 `#if defined(_NORMAL_MAP)` 判定口径一致（没有就只用顶点法线）。
            // 图集里那份 normal 本来就是 Texture2D.normalTexture（平面），
            // 所以直接给 (0.5,0.5,1) 与采样结果等价，但省掉一次图集取样。
            m.SetFloat("_UseNormal", src.IsKeywordEnabled("_NORMAL_MAP") ? 1f : 0f);

            // ===== emission 图集按需采样 =====
            // 全工程 35 个 RVT 资产里 34 个的 emission 槽是空的 ⇒ 这些材质每个 tap
            // 都在白采一张 emission 图集（占 RVT 取样量的 1/4，纯浪费带宽）。
            // 这里按资产里 emission 是否为空关掉这次取样。
            // （与废弃的 Unlit/ClusterPBR 路径语义一致：那边采样到的是黑图集，结果同样是 0，
            //   只是那边仍然白采一次 —— 那边是死路径，不值得同步改。）
            m.SetFloat("_UseEmission", hasEmission ? 1f : 0f);

            // ===== detail 安全门控（业界「detail 直采」方案的关键一步）=====
            // shader 里用 _DetailAlbedo / _DetailNormal / _DetailRoughness 三个强度当闸门，
            // 没有对应贴图时必须置 0：
            //   ① 避免采到 Unity 的占位默认纹理（linearGrey / bump）产生错误的细节混合；
            //   ② 历史上 detail=null 走 grayTexture fallback → detail.z=0 → BlendNormalRNM 除零
            //      → NaN 把整条法线链路污染掉（表现为表面完全光滑）。置 0 后整段分支不执行。
            // 另外 DefferStandard 用 _DetailNormalScale 表达 detail 法线强度，而 BRG shader 用
            // _DetailNormal，属性名不同、切 shader 不会自动带过来 → 这里显式搬运。
            bool hasDetail = m.GetTexture("_DetailMap") != null;
            bool hasDetailNormal = m.GetTexture("_DetailNormalMap") != null;
            float detailNormalScale = 0f;
            if (src.HasProperty("_DetailNormalScale"))
                detailNormalScale = src.GetFloat("_DetailNormalScale");
            else if (src.HasProperty("_DetailNormal"))
                detailNormalScale = src.GetFloat("_DetailNormal");

            m.SetFloat("_DetailAlbedo", hasDetail ? m.GetFloat("_DetailAlbedo") : 0f);
            m.SetFloat("_DetailRoughness", hasDetail ? m.GetFloat("_DetailRoughness") : 0f);
            m.SetFloat("_DetailNormal", hasDetailNormal ? detailNormalScale : 0f);

            m.name = src.name + " (BRG_" + mapId + ")";
            byMapId[mapId] = m;
            BindAtlas(m);       // 图集可能已创建（ClusterCore 先于后来的物体初始化）
            return m;
        }

        private void Awake()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if(meshFilter == null )
            {
                DestroyImmediate(this);
                return;
            }
            if(renderAssets == null )
                renderAssets = new ClusterRenderDataAsset[meshFilter.sharedMesh.subMeshCount];
        }
        private void OnEnable()
        {

            //material = meshRender.sharedMaterial;
            //material = new Material(Shader.Find("Unlit/ClusterPBR"));
            //meshRender.sharedMaterial = material;
            if (Application.isPlaying && ClusterControl.Instance.ClusterAssembleAsset.UseRVT)
            {
                var meshRender = GetComponent<MeshRenderer>();
                // 先抓取场景里【原本挂着的材质】，必须在下面 meshRender.materials 覆盖之前。
                // 它是 BRG 材质的克隆源：只有它带着完整的 _DetailMap / _DetailNormalMap /
                // _OcclusionMap 与 _DetailMap_ST（本场景 6,6 平铺）。
                Material[] sceneMats = meshRender != null ? meshRender.sharedMaterials : null;
                // 必须在下面 meshRender.materials 被覆盖之前抓，供 shadow-only 模式还原
                m_sceneMaterials = sceneMats;
                Material[] materials = new Material[renderAssets.Length];
                indexs = new int[renderAssets.Length];
                for (int i = 0; i < indexs.Length; i++)
                    indexs[i] = -1;      //-1 = 未分配，防止 OnDisable 误释放贴图0
                var instance = TextureNodesMap.CreateInstance();
                for (int i = 0; i < renderAssets.Length; i++)
                {
                    if (renderAssets[i] == null)
                        continue;
                    else
                    {
                        materials[i] = renderAssets[i].AssetMat;
                        indexs[i] = instance.AllocateTextureId(
                            renderAssets[i].MapNode);
                        materials[i].SetInt("_MapId", indexs[i]);

                        // ===== 把「素材真值」搬到这个 RVT 材质上 =====
                        // AssetMat 走的是 Unlit/ClusterPBR 路径（不开 BRG 时的唯一渲染方式），
                        // 下面三个开关决定它的取数与裁剪行为：
                        //   _UseMask     —— 源材质没有 _MASK_MAP ⇒ mask 视为白(1)，且跳过 mask 图集取样
                        //   _UseEmission —— 没有 emission ⇒ 跳过 emission 图集取样
                        //   _CLIPPING    —— 只有真镂空材质才裁剪（否则不透明材质会被按 alpha 误裁）
                        // 【真值一律从【场景材质】的 keyword 取】——与 BRG 路径同源，
                        // 不依赖资产里的字段（旧资产没有该字段，会是 false 而导致丢 mask / 丢裁剪）。
                        Material srcMat = (sceneMats != null && i < sceneMats.Length) ? sceneMats[i] : null;
                        bool srcHasMask = srcMat != null
                            ? srcMat.IsKeywordEnabled("_MASK_MAP")
                            : renderAssets[i].hasMaskMap;
                        bool srcClip = srcMat != null
                            ? srcMat.IsKeywordEnabled("_CLIPPING")
                            : renderAssets[i].clipping;
                        materials[i].SetFloat("_UseMask", srcHasMask ? 1f : 0f);
                        materials[i].SetFloat("_UseEmission", renderAssets[i].emission != null ? 1f : 0f);
                        if (srcClip) materials[i].EnableKeyword("_CLIPPING");
                        else materials[i].DisableKeyword("_CLIPPING");
                        // 记下来供 ApplyAtlasTextures 回填（非 BRG 路径也用这份材质画 Feedback）
                        s_sourceMaterials.Add(materials[i]);
                        BindAtlas(materials[i]);
                    }
                }
                //=== BRG：开启 useBRG 时，把该物体（按 submesh）注册进 BRG ===
                // BRG 是 RVT 路径的主画面绘制方式。
                // 阴影归属有【两种】模式（见 SetShadowOnlyMode）：
                //   · BRG 自己投阴影（旧行为）：MeshRenderer 必须关掉（否则双重绘制），
                //     代价是 BRG 的阴影 pass 要逐 batch 提交（实测 0.96ms → 4.8ms）。
                //   · 阴影交给 MeshRenderer（新行为）：MeshRenderer 保持开启 +
                //     材质换成 AssetMat（Unlit/ClusterPBR，ShadowCaster 极简），
                //     物体挪到专用 layer 排除出 GBuffer pass，阴影走 Unity 的 SRP Batcher 路径（快）。
                // 记下 AssetMat 数组，供 ApplyShadowOnlyMode 在模式切换时搬材质
                //（必须在下面 ApplyShadowOnlyMode 调用之前填好）。
                m_rvtMaterials = materials;

                bool useBRG = ClusterControl.Instance.ClusterAssembleAsset.useBRG;
                if (useBRG)
                {
                    var mf = GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        BatchRenderManager.EnsureCreated();
                        for (int i = 0; i < renderAssets.Length; i++)
                        {
                            if (renderAssets[i] == null || materials[i] == null) continue;
                            // BRG 附加绘制用「专用 ClusterPBR_BRG 材质」（克隆真实材质 + 切换到
                            // BRG 专用 shader），彻底绕开 ClusterPBR 的 DOTS 变体报错。
                            // 按源材质缓存复用（GetOrCreateBRGMaterial），使同材质的物体能并进同一条 batch。
                            // BRG 材质源改用【场景原始材质】，不再用 AssetMat：
                            // AssetMat 由稀疏的 ClusterRenderDataAsset 填充（只有 albedo/normal/
                            // pbrData/emission/detail 五槽，detail 还是打包语义 R/G/B/A），
                            // 根本没有 _DetailMap / _DetailNormalMap / _OcclusionMap，
                            // 也没有 6× 平铺的 _DetailMap_ST。切 shader 时 Unity 按【属性名】重映射，
                            // 所以从场景材质克隆能把整套 detail 原样带过来 ——
                            // 这是 RVT 能追上 Standard 清晰度的前提（业界「detail 直采」方案）。
                            Material brgSrc = (sceneMats != null && i < sceneMats.Length
                                              && sceneMats[i] != null) ? sceneMats[i] : materials[i];
                            Material brgMat = GetOrCreateBRGMaterial(brgSrc, indexs[i],
                                renderAssets[i].emission != null);
                            BatchRenderManager.Instance.Register(this, mf.sharedMesh, brgMat,
                                i, BatchRenderManager.GBufferPass);
                        }

                        // 阴影归属模式由全局开关决定（管线每帧根据相机的 cullingMask 校验后下发）。
                        ApplyShadowOnlyMode(ClusterProverty.BRGShadowExcludeLayer);
                    }
                }
                else
                {
                    // 非 BRG：MeshRenderer 直接用 AssetMat 画（Unlit/ClusterPBR 路径）。
                    // BRG 模式下【不覆盖】材质 —— 要保留场景原材质给阴影用。
                    if (meshRender != null) meshRender.materials = materials;
                }

                // 说明：旧的「mesh 拆簇 + 逐簇 DrawMesh」路径（useClusterBuild / useClusterRender）
                // 已整体移除。它只是把每个 submesh 再切成小簇逐个提交，并没有做簇级 LOD 选择，
                // 因此相对 BRG 没有任何收益，反而因为 CPU 端逐簇提交把帧率打到个位数。
                // 现在几何走 BRG（Unity 原生硬件批处理 + 自动实例合并），
                // 纹理精度走 Tile-VT（按 tile 分页 + 逐页 mip），两者正交、各管一件事。
            //{
            //    currentIndex = instance.AllocateTextureId(
            //        new TextureMapNode(albedo, normal, mask, emission, detail));
            //    material.SetInt("_MapId", currentIndex);
            //}
        }
        else if (Application.isPlaying)
        {
            // 不用 RVT：切到工程自己的 DefferStandard（带 OutGBuffer tag，直采贴图），
            // 即「Scene 视图模式」在延迟管线里的等价物。RVT 相关代码全部保留，随时可再开对比。
            ApplyStandardMaterials();
        }
        }   // OnEnable 结束

        // ===== 回退路径：不用 RVT，直接用工程自己的 DefferStandard =====
        // 这是「Scene 视图模式」在延迟管线里的等价物：DefferStandard 带 LightMode="OutGBuffer"，
        // 直采 _MainTex/_NormalMap/_MaskMap，完全不经过虚拟贴图（RVT）。
        // RVT 路径实测存在「桶颜色不一致 + 过亮无阴影」的问题（A/B 已验证：关 RVT 即正常），
        // 故默认走这条路径；RVT 相关代码全部保留，随时可以再开 UseRVT 对比。
        private static Shader s_standardShader;

        private void ApplyStandardMaterials()
        {
            if (s_standardShader == null)
                s_standardShader = Shader.Find("CustomRP/Defferer/DefferStandard");
            if (s_standardShader == null)
            {
                Debug.LogError("[Cluster] 找不到 shader CustomRP/Defferer/DefferStandard，回退路径失效");
                return;
            }
            var meshRender = GetComponent<MeshRenderer>();
            if (meshRender == null) return;
            if (renderAssets == null || renderAssets.Length == 0) return;

            // 【关键】优先直接沿用场景里已经挂好的材质，不要重建。
            // 之前这里无条件 new Material(s_standardShader) 再逐属性手工拷贝，会丢掉两套整层细节：
            //   1) _DETAIL_MAP：6x 平铺的 4096 detail map / detail normal。这是画面高频细节的
            //      【主要来源】（mat_barrel_01 的 _DetailMap_ST = 6,6，_DetailAlbedoMap_ST = 3,3）。
            //      丢掉它 = 「Play 明显比 Edit 低一个档次」，而且 RVT 开与不开糊得一模一样。
            //   2) _OCCLUSIONMAP：_OcclusionStrength = 0.8 的 AO。
            // 重建材质的贴图槽为空、keyword 也没开，这两层静默消失。
            // 场景材质本来就已经是 DefferStandard（工程 40 个材质里 38 个），直接沿用即可：
            // 这样 Play(UseRVT=0) 与 Edit 走的是【完全相同】的材质和 keyword，可作为精度基准。
            Material[] original = meshRender.sharedMaterials;
            Material[] mats = new Material[renderAssets.Length];
            for (int i = 0; i < renderAssets.Length; i++)
            {
                if (i < original.Length && original[i] != null
                    && original[i].shader == s_standardShader)
                {
                    mats[i] = original[i];      // 原样沿用，保留 detail / occlusion 全部细节层
                    continue;
                }

                var ra = renderAssets[i];
                Material m = new Material(s_standardShader);
                if (ra == null) { mats[i] = m; continue; }
                m.name = ra.name + " (Standard)";

                // 贴图：缺失时的 fallback 与 RVT 路径保持一致（白/normal/黑），保证观感统一
                m.SetTexture("_MainTex",    ra.albedo  != null ? ra.albedo  : Texture2D.whiteTexture);
                m.SetTexture("_MaskMap",    ra.pbrData != null ? ra.pbrData : Texture2D.whiteTexture);
                m.SetTexture("_NormalMap",  ra.normal  != null ? ra.normal  : Texture2D.normalTexture);
                m.SetTexture("_EmissionMap", ra.emission != null ? ra.emission : Texture2D.blackTexture);

                // 标量
                m.SetColor ("_Color",         ra.mainColor);
                m.SetColor ("_EmissionColor", ra.emissionColor);
                m.SetFloat("_Metallic",   ra.metallic);
                m.SetFloat("_Roughness",  ra.roughness);
                m.SetFloat("_NormalScale", ra.normalScale);
                m.SetFloat("_CullOff",    ra.cullOff);
                m.SetVector("_MainTex_ST",
                    new Vector4(ra.uv_Scale.x, ra.uv_Scale.y, ra.uv_Offset.x, ra.uv_Offset.y));

                // keyword：只在贴图真实存在时开对应功能，与 RVT 路径的行为对齐
                if (ra.normal  != null) m.EnableKeyword("_NORMAL_MAP");
                if (ra.pbrData != null) m.EnableKeyword("_MASK_MAP");
                // Alpha Cutout 用源材质的 _CLIPPING keyword 真值判定，不能用 cullOff 阈值
                // （阈值每个材质默认 0.5，"有阈值≠该裁"，否则会误把不透明材质当 cutout 裁掉）。
                if (ra.clipping) m.EnableKeyword("_CLIPPING");
                // detail 默认不开：RVT 路径下 detail 基本没生效（多数资产 detail=null），
                // 这里同样保持关闭，避免引入新变量。需要时再开 _DETAIL_MAP 并填 _DetailMap 等。

                mats[i] = m;
            }
            // 用 sharedMaterials 而不是 materials：materials 的 setter 会再实例化一层副本，
            // 我们刚 new 出来的材质就被丢掉了（白费一次分配，且改属性时操作的是副本）。
            meshRender.sharedMaterials = mats;
        }

        /// <summary>
        /// 切换「阴影归属模式」：
        ///   excludeLayer &gt;= 0 ⇒ 阴影交给 MeshRenderer：
        ///     · 物体挪到 excludeLayer（GBuffer pass 与 DrawUnsupportedShaders 会排除该 layer，
        ///       所以 MeshRenderer 只画阴影、不画 GBuffer → 没有双重绘制）；
        ///     · MeshRenderer 保持 enabled，材质换成【RVT AssetMat（Unlit/ClusterPBR）】。
        ///       ⇐ 这是 2026-10-21 的关键修正，详见下面注释。
        ///   excludeLayer &lt; 0 ⇒ 旧行为：MeshRenderer 关掉，阴影由 BRG 批次投。
        /// </summary>
        public void ApplyShadowOnlyMode(int excludeLayer)
        {
            if (m_originalLayer < 0) m_originalLayer = gameObject.layer;
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;

            bool wantShadowOnly = excludeLayer >= 0 && excludeLayer < 32;

            // ===== 材质也要跟着模式切（2026-10-21）=====
            // 之前 shadow-only 模式【保留场景原材质】= CustomRP/Defferer/DefferStandard。
            // 但 DefferStandard 的 ShadowCaster 比 Unlit/ClusterPBR 的贵约 40%：
            //   · 全开档：投射物 = DefferStandard × 1159 → 阴影 3.956ms（0.99ms/级联）
            //   · 没开档：投射物 = Unlit/ClusterPBR × 1158 → 阴影 2.859ms（0.71ms/级联）
            // 两档的投射物数量、draw call 数、级联数都一致，唯一差别就是这个 shader。
            //
            // 既然 shadow-only 模式下 MeshRenderer 的【唯一职责就是投阴影】
            // （GBuffer 由 BRG 画，且该 layer 已被 GBuffer pass 排除），
            // 那就直接用 AssetMat：它的 shader 是 Unlit/ClusterPBR，ShadowCaster 极简
            // （只有两次坐标变换 + 一次保活 clip，不采任何贴图），正是"没开档"用的那条快路径。
            //
            // 注意：Unlit/ClusterPBR 的 ShadowCaster 带 _CLIPPING 门控，
            // AssetMat 在 OnEnable 里已按源材质 keyword 设置好 ⇒ 镂空物体的阴影仍是镂空的。
            if (m_shadowOnlyMode != wantShadowOnly)
            {
                var target = wantShadowOnly ? m_rvtMaterials : m_sceneMaterials;
                if (target != null) mr.materials = target;
            }

            if (wantShadowOnly)
            {
                if (gameObject.layer != excludeLayer) gameObject.layer = excludeLayer;
                if (!mr.enabled)
                {
                    mr.enabled = true;
                    m_brgDisabledRenderer = false;   // 现在是"开启"，不是"被 BRG 关掉"
                }
                // 显式打开投射：这个模式的全部意义就是"阴影由 MeshRenderer 画"，
                // 万一别处把 shadowCastingMode 关过，这里一拨就恢复，不会出现"模式生效了但没阴影"。
                if (mr.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off)
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                m_shadowOnlyMode = true;
            }
            else
            {
                if (m_shadowOnlyMode && m_originalLayer >= 0 && gameObject.layer != m_originalLayer)
                    gameObject.layer = m_originalLayer;
                m_shadowOnlyMode = false;
                if (mr.enabled)
                {
                    // BRG 接管主画面 ⇒ MeshRenderer 必须关掉（否则 GBuffer 被画两遍）
                    mr.enabled = false;
                    m_brgDisabledRenderer = true;
                }
            }
        }

        private void OnDisable()
        {
            //BRG 注销（仅当 BRG 管理器存在时才需要；useBRG 关闭时 Instance 为 null，直接跳过）
            if (BatchRenderManager.Instance != null)
                BatchRenderManager.Instance.Unregister(this);

            // BRG 作为唯一渲染路径时关闭过 MeshRenderer，需恢复。
            if (m_brgDisabledRenderer)
            {
                var mr = GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = true;
                m_brgDisabledRenderer = false;
            }

            // 「阴影交给 MeshRenderer」模式挪过 layer，退出时必须还原，
            // 否则物体在编辑器里就永久留在排除 layer 上了。
            if (m_shadowOnlyMode && m_originalLayer >= 0 && gameObject.layer != m_originalLayer)
                gameObject.layer = m_originalLayer;
            m_shadowOnlyMode = false;

            if (indexs == null || indexs.Length == 0) return;
            //退出播放时物体 OnDisable 可能晚于渲染管线 Dispose，此时 Instance 已置空，
            //无处释放（页表数组已销毁），直接返回
            var instance = TextureNodesMap.Instance;
            if (instance == null) return;
            for (int i = 0; i < indexs.Length; i++)
            {
                //-1 = 该槽位 renderAssets[i] 为 null，从未分配，不能释放
                if (indexs[i] < 0) continue;
                instance.RemoveTextureId(indexs[i]);
                indexs[i] = -1;      //防止重复释放（禁用与销毁可能都触发 OnDisable）
            }
        }

        //private void Update()
        //{
        //    material.SetInt("_MapId", (int)index);
        //}

#if UNITY_EDITOR
        public bool CopyFromMaterial;

        /// <summary>
        /// 从场景里挂着的材质，同步本物体的 renderAssets（贴图 + 标量 + keyword 真值）。
        /// OnValidate（勾选 CopyFromMaterial）与组件右键菜单都走这里，保证只有一份实现。
        ///
        /// 【为什么要它】renderAssets 里的 pbrData 必须与材质「有没有 _MASK_MAP」严格对应：
        /// 材质没有 mask map 时 OnValidate 会把它写成 Texture2D.whiteTexture（= 白 mask，
        /// 与 Standard 的 GetMask 返回 1.0 同义）。若这里被填成别的东西（例如高光/光泽图），
        /// RVT 路径会把那张图的 g 通道当 AO，把间接光与反射整体乘 0 → 物体在 Play 里变纯黑。
        /// </summary>
        [ContextMenu("从场景材质同步 RVT 资产")]
        public void SyncRenderAssetsFromMaterials()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) return;
            int size = renderAssets.Length;

            var meshRender = GetComponent<MeshRenderer>();
            if (meshRender == null) return;

            var mats = meshRender.sharedMaterials; if (mats == null) return;
            Shader target = Shader.Find("CustomRP/Defferer/DefferStandard");
            for (int i = 0; i < size; i++)
            {
                if (renderAssets[i] == null || i >= mats.Length || mats[i] == null
                    || mats[i].shader != target)
                    continue;
                renderAssets[i].albedo = (Texture2D)mats[i].GetTexture("_MainTex");
                renderAssets[i].normal = (Texture2D)mats[i].GetTexture("_NormalMap");
                renderAssets[i].pbrData = (Texture2D)mats[i].GetTexture("_MaskMap");
                renderAssets[i].emission = (Texture2D)mats[i].GetTexture("_EmissionMap");

                if (renderAssets[i].albedo == null) renderAssets[i].albedo = Texture2D.whiteTexture;
                if (renderAssets[i].normal == null) renderAssets[i].normal = Texture2D.normalTexture;
                // 关键：材质没有 _MaskMap 时这里是 null → 必须落成【白图】，
                // 否则 pbrData 会保留上一张无关贴图，RVT 侧就会拿它当 metallic/roughness/AO 用。
                if (renderAssets[i].pbrData == null) renderAssets[i].pbrData = Texture2D.whiteTexture;
                if (renderAssets[i].emission == null) renderAssets[i].emission = Texture2D.blackTexture;
                if (renderAssets[i].detail == null) renderAssets[i].detail = Texture2D.grayTexture;
                //detailӦȡ

                renderAssets[i].mainColor = mats[i].GetColor("_Color");
                renderAssets[i].emissionColor = mats[i].GetColor("_EmissionColor");
                renderAssets[i].cullOff = mats[i].GetFloat("_CullOff");
                // Alpha Cutout 开关：搬运源材质的 _CLIPPING keyword 状态（不是看 cullOff 阈值）。
                // 阈值每个材质都有(0.5)，"有阈值≠该裁"，必须用 keyword 真值。
                renderAssets[i].clipping = mats[i].IsKeywordEnabled("_CLIPPING");
                // mask map 的真值：决定 RVT 路径要不要采 mask 图集（没有就当白）
                renderAssets[i].hasMaskMap = mats[i].IsKeywordEnabled("_MASK_MAP");
                renderAssets[i].metallic = mats[i].GetFloat("_Metallic");
                renderAssets[i].roughness = mats[i].GetFloat("_Roughness");
                renderAssets[i].normalScale = mats[i].GetFloat("_NormalScale");

                renderAssets[i].detailAlbedo = mats[i].GetFloat("_DetailAlbedo");
                renderAssets[i].detailNormal = mats[i].GetFloat("_DetailNormalScale");
                renderAssets[i].detailRoughness = mats[i].GetFloat("_DetailRoughness");

                EditorUtility.SetDirty(renderAssets[i]);
            }
        }

        private void OnValidate()
        {
            if (CopyFromMaterial)
            {
                SyncRenderAssetsFromMaterials();
                CopyFromMaterial = false;
            }
        }

#endif
    }
}