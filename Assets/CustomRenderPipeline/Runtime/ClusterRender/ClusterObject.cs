using UnityEditor;
using UnityEngine;

namespace Cluster
{
    [ExecuteAlways]
    public class ClusterObject : MonoBehaviour
    {

        public ClusterRenderDataAsset[] renderAssets;
        public int[] indexs;




        /// <summary>cutoff 物体专用 rendering layer 位的还原值（OnDisable 时恢复，避免脏化场景）。</summary>
        private uint m_originalRLM = 0;
        private bool m_rlmStored = false;

        // ===== 「静态才进 Cluster」的守卫 =====
        // 簇数据是世界空间烘焙的、世界矩阵只在注册时抓一次。物体一旦真的动起来
        //（水车/门/机关），继续留在 Cluster 里会一直画在旧位置，所以这里每帧比一次 Transform：
        // 变了就退出 Cluster、还原掩码，把绘制交回 SRP（动态物体走 SRP 本来就够快）。
        private Vector3 m_regPos;
        private Quaternion m_regRot;
        private bool m_registered;

        // ===== RVT 图集绑定（材质属性 + anisoLevel）=====
        // 图集在 ClusterCore.Initialize 里创建，时机晚于本组件的 OnEnable，所以用静态字段暂存，
        // 在两处应用：① ClusterCore 创建图集后调 ApplyAtlasTextures 回填【已经克隆/已使用】的材质；
        // ② 之后新建的克隆材质在 GetOrCreateClusterPBRMaterial 里立即绑定。
        // 为什么要绑成材质属性：Unity 只有在采样器对应「材质属性纹理」时才沿用该纹理自己的
        // anisoLevel。用纯全局纹理 + sampler_linear_repeat，aniso 恒为 1，掠射角会明显糊。
        private static Texture s_atlasAlbedo, s_atlasNormal, s_atlasMask;

        /// <summary>ClusterObject 用到的所有源材质（AssetMat），供 ApplyAtlasTextures 回填</summary>
        private static readonly System.Collections.Generic.HashSet<Material> s_sourceMaterials
            = new System.Collections.Generic.HashSet<Material>();

        public static void ApplyAtlasTextures(Texture albedo, Texture normal, Texture mask)
        {
            s_atlasAlbedo = albedo; s_atlasNormal = normal; s_atlasMask = mask;

            foreach (var src in s_sourceMaterials) BindAtlas(src);
            // Cluster 画 GBuffer 的材质也要绑（它同样靠「材质属性纹理」才拿到图集的 anisoLevel）
            if (ClusterGBufferRenderer.HasInstance)
                ClusterGBufferRenderer.Instance.BindAtlas(albedo, normal, mask);
        }

        /// <summary>供 ClusterGBufferRenderer 在「图集先建好、物体后注册」时回填。</summary>
        public static bool TryGetAtlas(out Texture albedo, out Texture normal, out Texture mask)
        {
            albedo = s_atlasAlbedo; normal = s_atlasNormal; mask = s_atlasMask;
            return albedo != null && normal != null && mask != null;
        }

        private static void BindAtlas(Material mat)
        {
            if (mat == null) return;
            if (s_atlasAlbedo   != null) mat.SetTexture("_VirtualAlbedo",   s_atlasAlbedo);
            if (s_atlasNormal   != null) mat.SetTexture("_VirtualNormal",   s_atlasNormal);
            if (s_atlasMask     != null) mat.SetTexture("_VirtualMask",     s_atlasMask);
        }


        // ===== RVT 材质构造：克隆场景材质再换到 Unlit/ClusterPBR =====
        // 为什么不用 ClusterRenderDataAsset 生成的 AssetMat：它是精简材质，根本没有
        // _DetailMap / _DetailNormalMap / _OcclusionMap，也没有场景材质那套 6× 平铺的
        // _DetailMap_ST。detail 直采时 _DetailMap 为 null ⇒ detail 层被门控跳过 ⇒
        // 只剩模糊的 RVT 基础层。克隆【场景材质】可把整套 detail 原样带过来。
        // 按 (源材质, _MapId) 二级缓存：① 避免每个物体克隆一份（泄漏 + 破坏合批）；
        // ② 不同 _MapId 各持正确材质，避免共享材质互相覆盖 _MapId。
        private static Shader s_clusterPBRShader;
        private static Shader ClusterPBRShader
        {
            get
            {
                if (s_clusterPBRShader == null)
                    s_clusterPBRShader = Shader.Find("Unlit/ClusterPBR");
                return s_clusterPBRShader;
            }
        }
        private static readonly System.Collections.Generic.Dictionary<Material,
            System.Collections.Generic.Dictionary<int, Material>> s_clusterPBRMaterialCache
            = new System.Collections.Generic.Dictionary<Material,
                System.Collections.Generic.Dictionary<int, Material>>();

        private static Material GetOrCreateClusterPBRMaterial(Material src, int mapId, bool hasEmission)
        {
            if (src == null) return null;
            if (!s_clusterPBRMaterialCache.TryGetValue(src, out var byMapId))
            {
                byMapId = new System.Collections.Generic.Dictionary<int, Material>();
                s_clusterPBRMaterialCache[src] = byMapId;
            }
            if (byMapId.TryGetValue(mapId, out Material cached) && cached != null)
                return cached;
            if (ClusterPBRShader == null)
            {
                Debug.LogError("[Cluster] 找不到 shader Unlit/ClusterPBR，本次回退用源材质");
                return src;
            }
            Material m = new Material(src);
            m.shader = ClusterPBRShader;   // 克隆场景材质再换 shader：属性按名重映射，detail 贴图全部带过来

            m.SetInt("_MapId", mapId);

            // Alpha Cutout 开关（_CLIPPING）显式搬运
            if (src.IsKeywordEnabled("_CLIPPING")) m.EnableKeyword("_CLIPPING");
            else m.DisableKeyword("_CLIPPING");

            // mask / normal / emission 白回退开关（与 DefferStandard 的 GetMask 语义一致）
            m.SetFloat("_UseMask", src.IsKeywordEnabled("_MASK_MAP") ? 1f : 0f);
            m.SetFloat("_UseNormal", src.IsKeywordEnabled("_NORMAL_MAP") ? 1f : 0f);
            m.SetFloat("_UseEmission", hasEmission ? 1f : 0f);

            // detail 安全门控：没有对应贴图时置 0 ⇒ 整段分支不执行（NaN 历史根因之一）。
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

            m.name = src.name + " (ClusterPBR_" + mapId + ")";
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
        /// <summary>本物体是否有「需 alpha 镂空」的子网格（草/蕨/灌木）。⇒ 必须走 SRP 阴影路径。</summary>
        public bool UsesCutoff()
        {
            if (renderAssets != null)
                for (int i = 0; i < renderAssets.Length; i++)
                    if (renderAssets[i] != null && renderAssets[i].NeedsSRPShadow) return true;
            // 兜底：直接看运行时材质有没有 _CLIPPING（避免资产 clipping 字段未同步导致漏判）
            var mr = GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var mats = mr.sharedMaterials;
                if (mats != null)
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && mats[i].IsKeywordEnabled("_CLIPPING")) return true;
            }
            return false;
        }

        private void OnEnable()
        {
            bool cutoff = UsesCutoff();
            var cfg = Application.isPlaying ? ClusterControl.Instance.ClusterAssembleAsset : null;

            // rendering layer 位（Renderer.renderingLayerMask，不是 GameObject layer）：
            //   位 29 = 「需 alpha 镂空的投射物」⇒ Cluster 阴影跳过、改走 SRP 阴影（这里打）；
            //   位 28 = 「由 Cluster 画 GBuffer」⇒ 注册【成功之后】才打（见下面），
            //           否则注册失败（无 mesh / 无材质）的物体会被 SRP 剔掉却没人画它 ⇒ 整块消失。
            // 只在 Play 时动 renderingLayerMask，避免脏化场景资产。
            if (Application.isPlaying && cutoff)
                SetRenderingLayerBit(1u << ClusterProverty.CutoffRenderingLayerBit, keepOriginal: true);

            // cutout 物体（草/蕨/灌木）【不进 RVT】：它们本来就不参与 Cluster 绘制，又需要 _CLIPPING 镂空。
            // 改走 DefferStandard 直采 -> alpha 更锐、不占图集 slot，也让图集腾出 alpha 通道给 emission。
            if (Application.isPlaying && cfg.UseRVT && !cutoff)
            {
                var meshRender = GetComponent<MeshRenderer>();
                // 先抓取场景里【原本挂着的材质】，必须在下面 meshRender.materials 覆盖之前。
                // 它是克隆材质的源：只有它带着完整的 _DetailMap / _DetailNormalMap /
                // _OcclusionMap 与 _DetailMap_ST（本场景 6,6 平铺）。
                Material[] sceneMats = meshRender != null ? meshRender.sharedMaterials : null;
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
                        // 克隆【场景材质】再换到 Unlit/ClusterPBR，把 _DetailMap / _DetailNormalMap /
                        // _OcclusionMap 和 6× 平铺的 _DetailMap_ST 整套带过来（AssetMat 没有这些）。
                        // 场景材质缺失时退化为 AssetMat（仍无 detail，但 RVT 基础层正确）。
                        Material srcMat = (sceneMats != null && i < sceneMats.Length)
                            ? sceneMats[i] : renderAssets[i].AssetMat;
                        indexs[i] = instance.AllocateTextureId(
                            renderAssets[i].MapNode);
                        materials[i] = GetOrCreateClusterPBRMaterial(srcMat, indexs[i],
                            renderAssets[i].emission != null);
                        // 记下来供 ApplyAtlasTextures 回填（也用这份材质画 Feedback）
                        s_sourceMaterials.Add(materials[i]);
                    }
                }
                // MeshRenderer 仍然保留并换上克隆材质：SRP 阴影那条路还要靠它投阴影。
                // clusterMode 非 SRP 时，几何改由 Cluster 画进 GBuffer，GBuffer pass 按 layer 位排除这些物体。
                if (meshRender != null) meshRender.materials = materials;

                var meshFilter = GetComponent<MeshFilter>();
                // GBuffer 是否交给 Cluster 由 clusterGBuffer 独立控制（默认 false = GBuffer 仍走 SRP）。
                // 不开时【不要注册、也不要动 renderingLayerMask】，否则物体被贴上 bit28 后
                // SRP 的 DrawRenderers 就再也画不到它们了。
                if (cfg.clusterMode != ClusterMode.SRP && cfg.clusterGBuffer && meshRender != null
                    && meshFilter != null && meshFilter.sharedMesh != null
                    && ClusterGBufferRenderer.Instance.Register(this, meshFilter.sharedMesh,
                        transform.localToWorldMatrix, materials, indexs))
                {
                    m_regPos = transform.position;
                    m_regRot = transform.rotation;
                    m_registered = true;
                    // ⚠️ rendering layer mask 是【包含式】的：过滤判定 (renderer.rlm & filter) != 0。
                    // 只把 bit28 从 filter 里清掉【排不掉】这些物体（它们还有 bit0 能通过），
                    // 所以这里必须让它们【只】保留 bit28 —— 这样才能从 SRP 的所有 pass 一起消失。
                    SetRenderingLayerBit(1u << ClusterProverty.ClusterRenderingLayerBit, keepOriginal: false);
                }
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
                // 优先取【场景原材质】的 keyword：旧资产的 renderAssets[i].clipping 可能没同步成 true，
                // 只信字段会把草/蕨这类 cutout 材质画成实心卡片。
                bool srcClip = (i < original.Length && original[i] != null)
                    ? original[i].IsKeywordEnabled("_CLIPPING") : ra.clipping;
                if (srcClip) m.EnableKeyword("_CLIPPING");
                // detail 默认不开：RVT 路径下 detail 基本没生效（多数资产 detail=null），
                // 这里同样保持关闭，避免引入新变量。需要时再开 _DETAIL_MAP 并填 _DetailMap 等。

                mats[i] = m;
            }
            // 用 sharedMaterials 而不是 materials：materials 的 setter 会再实例化一层副本，
            // 我们刚 new 出来的材质就被丢掉了（白费一次分配，且改属性时操作的是副本）。
            meshRender.sharedMaterials = mats;
        }


        /// <summary>改 Renderer.renderingLayerMask。
        /// keepOriginal = true  → 原掩码 | bit（cutout：还要留在 SRP 阴影里）；
        /// keepOriginal = false → 只保留 bit（Cluster 接管：必须丢掉 bit0 才能真正排掉）。</summary>
        private void SetRenderingLayerBit(uint bit, bool keepOriginal)
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;
            if (!m_rlmStored) { m_originalRLM = mr.renderingLayerMask; m_rlmStored = true; }
            mr.renderingLayerMask = keepOriginal ? (m_originalRLM | bit) : bit;
        }

        /// <summary>物体动起来了 ⇒ 退出 Cluster 绘制、掩码还原，交回 SRP（只掉一次）。</summary>
        private void DropToSRP()
        {
            m_registered = false;
            if (ClusterGBufferRenderer.HasInstance)
                ClusterGBufferRenderer.Instance.Unregister(this);
            if (m_rlmStored)
            {
                var mr = GetComponent<MeshRenderer>();
                if (mr != null) mr.renderingLayerMask = m_originalRLM;
                m_rlmStored = false;
            }
        }

        private void Update()
        {
            if (!m_registered) return;
            if ((transform.position - m_regPos).sqrMagnitude > 1e-6f
                || Quaternion.Angle(transform.rotation, m_regRot) > 0.05f)
                DropToSRP();
        }

        private void OnDisable()
        {
            m_registered = false;
            if (ClusterGBufferRenderer.HasInstance)
                ClusterGBufferRenderer.Instance.Unregister(this);

            // 还原 rendering layer 位（cutoff / Cluster GBuffer 专用），避免脏化场景资产
            if (m_rlmStored)
            {
                var rlmMr = GetComponent<MeshRenderer>();
                if (rlmMr != null) rlmMr.renderingLayerMask = m_originalRLM;
                m_rlmStored = false;
            }

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