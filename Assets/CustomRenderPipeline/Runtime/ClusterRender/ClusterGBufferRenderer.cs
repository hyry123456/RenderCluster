using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// 【cluster 管线】GBuffer 渲染器：把场景里接管过来的静态不透明物体，
    /// 用「逐簇 GPU 剔除 + 按 detail 贴图组间接绘制」画进现有 GBuffer MRT，
    /// 并用同一份可见簇列表画 RVT Feedback（请求的页必须与采样的页一致）。
    ///
    /// 为什么能合并成很少几次 draw：
    ///   · albedo / normal / mask / emission 全在 RVT 图集里 ⇒ 全局同一张图，零绑定量；
    ///   · _MapId / ST / 颜色 / 粗糙度 / detail 强度 等参数走【每簇常量缓冲】；
    ///   · 唯一还绑在图上的「每材质」资源只剩 _DetailMap / _DetailNormalMap 两张，
    ///     所以按「detail 贴图对」分组，一组一次间接绘制（本工程实测 ≤17 组）。
    ///
    /// 顶点数据按【唯一 mesh】上传一份（mesh 局部空间），每簇只记索引段 + 变换矩阵，
    /// 同一 mesh 的多个实例共享顶点，不随摆放数量膨胀显存。
    ///
    /// 每帧 3 个 compute dispatch（Reset / CullAll / BuildArgs）+ 每组 1 次 DrawProceduralIndirect。
    /// </summary>
    public class ClusterGBufferRenderer
    {
        public const int TrisPerCluster = ClusterMeshCache.TrisPerCluster;
        public const int VertsPerCluster = TrisPerCluster * 3;

        const int PlanesPerView = 6;
        const int ThreadGroupSize = 64;

        const string DrawShaderName = "Cluster/ClusterGBuffer";
        const string CullShaderName = "Cluster/ClusterGBufferCull";

        /// <summary>每簇材质参数。字段顺序/大小必须与 ClusterGBufferInput.hlsl 的
        /// ClusterMaterialData 逐字一致（7 个 float4 = 112 字节）。</summary>
        public struct ClusterMaterialData
        {
            public uint mapId;
            public float normalScale, metallic, roughness;
            public Vector4 color, emissionColor;
            public Vector4 mainTexST, detailMapST;
            public float detailAlbedo, detailNormal, detailRoughness, cullOff;
            public float useMask, useNormal, useEmission, pad;
        }

        sealed class Sub
        {
            public ClusterMaterialData mat;
            public Texture detail, detailNormal;
        }

        sealed class Entry
        {
            public Mesh mesh;
            public Matrix4x4 localToWorld;
            public Sub[] subs;
        }

        struct ClusterRecord
        {
            public ClusterMeshAsset asset;
            public Matrix4x4 localToWorld;
            public int cluster;
            public int matIndex;
            public int group;
        }

        static ClusterGBufferRenderer s_instance;
        public static ClusterGBufferRenderer Instance =>
            s_instance ?? (s_instance = new ClusterGBufferRenderer());
        /// <summary>是否已创建过（退出 Play 时别因为一次访问就重新 new 一个）。</summary>
        public static bool HasInstance => s_instance != null;

        // ---------------- 场景登记 ----------------
        readonly Dictionary<Component, Entry> m_entries = new Dictionary<Component, Entry>();
        bool m_dirty = true;

        // ---------------- GPU 资源 ----------------
        Material m_mat;
        MaterialPropertyBlock m_mpb;
        ComputeShader m_cull;

        ComputeBuffer m_vertBuf, m_cornerBuf;
        ComputeBuffer m_boundsBuf, m_groupBuf, m_infoBuf;
        ComputeBuffer m_matBuf;
        ComputeBuffer m_planesBuf, m_visibleBuf, m_counterBuf, m_argsBuf;
        GraphicsBuffer m_drawIdxBuf;

        ComputeBuffer m_drawBaseBuf, m_slotBuf;

        int m_clusterCount;
        bool m_built;
        bool m_atlasBound;
        // 是否已经有相机跑过一次 Cull（可见列表 / args 已经有效）。
        // Scene 视图「复用主相机可见集」必须先满足它，否则缓冲是空的 ⇒ 什么都画不出来。
        bool m_culled;
        Matrix4x4 m_w2c;
        // 【上一帧】的 world→clip。Hi-Z 金字塔是上一帧深度建的，遮挡测试必须配上一帧的矩阵，
        // 否则相机一动，簇被投影到"这一帧的屏幕"却拿"上一帧的深度"去比 ⇒ 系统性错位 ⇒ 误剔。
        Matrix4x4 m_prevVP = Matrix4x4.identity;
        readonly Vector4[] m_planes = new Vector4[PlanesPerView];

        // ---------------- 分组（detail 贴图对） ----------------
        int m_groupCount;
        readonly List<Texture> m_groupDetail = new List<Texture>();
        readonly List<Texture> m_groupDetailNormal = new List<Texture>();
        readonly Dictionary<long, int> m_groupLookup = new Dictionary<long, int>();

        /// <summary>排查用：非 0 时跳过视锥剔除，全部簇都画。</summary>
        public int debugForceAll = 0;
        /// <summary>只在 ClusterDebug 模式下为 true：开启 Hi-Z 判定统计与额外日志。</summary>
        public bool debugStats = false;

        /// <summary>本帧是否由管线启用（DefferPipeline 每帧设置）。
        /// 关掉资产开关后必须立刻停用，否则 GBuffer 走 SRP、Feedback 还走 Cluster，两边会分叉。</summary>
        public bool active;

        // ===== Hi-Z 遮挡剔除 =====
        // 用【上一帧】的相机深度金字塔，剔掉被墙挡住整块的簇。
        // 之所以能"上一帧"：金字塔在本帧 GBuffer 画完后重建，供下一帧使用，
        // 所以既不需要跨帧保存深度图，也不存在 depth→color 的拷贝格式问题。
        // 失败模式是安全的：深度读不到 ⇒ 金字塔全是 0（无穷远）⇒ 判据永不成立 ⇒ 不剔除、不改变画面。
        public bool hiZEnable = false;
        // 四级 HZB：1/2 → 1/4 → 1/8 → 1/16 屏，每级 2×2 归约取「最远」。
        // ⚠️【不能加级】CullAll 的 UAV 已占满 8 个（_HiZ0..3 + _HiZStat + _SlotOf + _Visible + _Counter），
        // D3D11.0 一个 kernel 最多 8 个 UAV。再加一级 ⇒ dispatch 被跳过、剔除整个失效。
        // 大簇的覆盖范围靠 compute 里的 texel 预算（最粗一级给 8×8）来扩，不靠加级。
        RenderTexture m_hiz0, m_hiz1, m_hiz2, m_hiz3;
        bool m_hizValid;
        // 占位纹理：_HiZ0/_HiZ1 在 compute 里是【全局声明】，所有 kernel 都要求绑定。
        // Hi-Z 关闭或首帧还没建金字塔时也必须塞一张（否则报
        // "Property (_HiZ0) at kernel index (1) is not set"）。1×1 的 RFloat 开销可忽略。
        RenderTexture m_hizDummy;
        /// <summary>绝对深度余量（深度空间）。0 = 严格按判据。</summary>
        const float HiZBias = 0f;
        /// <summary>
        /// 相对深度余量（乘 nearestZ）。远处深度值趋近 0（reversed-Z），此时绝对 bias 等于没加，
        /// 全靠这个相对量兜住浮点误差 → 误差方向永远偏「不剔除」，只会漏剔、不会穿孔。
        /// 1% ≈ 200m 处 1.6m、10m 处 0.03m 的深度余量：够吃浮点与半 texel 的错位，又不至于挡住正常剔除。
        /// </summary>
        const float HiZRelBias = 0.01f;
        /// <summary>
        /// 簇的屏幕跨度小于它就不做遮挡测试（像素）。
        /// 金字塔最细一级是 1/2 屏（1 texel = 2×2 像素），4 像素 = 2 texel 起步；
        /// 更小的簇在块里几乎全是邻居，宁可漏剔（本来也几乎没收益）。
        /// </summary>
        const float HiZMinScreenSize = 4f;
        static readonly int DepthTexId = Shader.PropertyToID("_GBufferDepthTex");

        // 调试统计（只在 ClusterDebug 模式累加）：0=测了且保留 1=被判遮挡剔除 3=跳过(太小/太大) 4=矩形不可靠
        // 5/6/7 由 C# 填「本帧状态」：5=Hi-Z 实际启用 6=金字塔有效 7=剔选用屏宽
        ComputeBuffer m_hizStat;
        bool m_hizStatPending;
        /// <summary>本帧是否该读一次统计——在 Cull 里置位，由管线在 ExecuteBuffer 之后调 FlushDebugStats。</summary>
        bool m_hizStatDue;
        uint[] m_dbgStat = new uint[8];
        /// <summary>剔选用到的视口尺寸（只给调试日志用）。</summary>
        int m_dbgPicW;

        public bool IsReady => active && m_built && m_clusterCount > 0 && m_groupCount > 0
            && m_atlasBound && m_mat != null;
        public int ClusterCount => m_clusterCount;
        public int GroupCount => m_groupCount;

        // =====================================================================
        // 登记 / 注销
        // =====================================================================

        /// <summary>把一个物体交给 cluster GBuffer 绘制。
        /// materials / mapIds 与 submesh 一一对应（就是 ClusterObject 已经构造好的
        /// Unlit/ClusterPBR 克隆材质与 RVT texId）。</summary>
        /// <returns>是否真的登记成功。调用方只有拿到 true 才可以把物体从 SRP 绘制里排除
        /// （否则会出现「SRP 不画、Cluster 也没画」⇒ 整块消失）。</returns>
        public bool Register(Component owner, Mesh mesh, Matrix4x4 localToWorld,
            Material[] materials, int[] mapIds)
        {
            if (owner == null || mesh == null || materials == null) return false;
            int n = Mathf.Min(materials.Length, mesh.subMeshCount);
            if (n <= 0) return false;
            var subs = new Sub[n];
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                var m = materials[i];
                if (m == null) continue;
                subs[i] = new Sub
                {
                    mat = ToMaterialData(m, mapIds != null && i < mapIds.Length ? mapIds[i] : 0),
                    detail = m.GetTexture("_DetailMap"),
                    detailNormal = m.GetTexture("_DetailNormalMap"),
                };
                valid++;
            }
            if (valid == 0) return false;
            m_entries[owner] = new Entry { mesh = mesh, localToWorld = localToWorld, subs = subs };
            m_dirty = true;
            return true;
        }

        public void Unregister(Component owner)
        {
            if (owner != null && m_entries.Remove(owner)) m_dirty = true;
        }

        static ClusterMaterialData ToMaterialData(Material m, int mapId)
        {
            return new ClusterMaterialData
            {
                mapId = (uint)mapId,
                normalScale = m.GetFloat("_NormalScale"),
                metallic = m.GetFloat("_Metallic"),
                roughness = m.GetFloat("_Roughness"),
                color = m.GetColor("_Color"),
                emissionColor = m.GetColor("_EmissionColor"),
                mainTexST = m.GetVector("_MainTex_ST"),
                detailMapST = m.GetVector("_DetailMap_ST"),
                detailAlbedo = m.GetFloat("_DetailAlbedo"),
                detailNormal = m.GetFloat("_DetailNormal"),
                detailRoughness = m.GetFloat("_DetailRoughness"),
                cullOff = m.GetFloat("_CullOff"),
                useMask = m.GetFloat("_UseMask"),
                useNormal = m.GetFloat("_UseNormal"),
                useEmission = m.GetFloat("_UseEmission"),
            };
        }

        /// <summary>图集建好后由 ClusterObject.ApplyAtlasTextures 回填。
        /// 声明成材质属性是为了让采样器沿用图集自己的 anisoLevel（全局纹理的采样器 aniso 恒为 1）。</summary>
        public void BindAtlas(Texture albedo, Texture normal, Texture mask)
        {
            if (m_mat != null)
            {
                if (albedo != null) m_mat.SetTexture("_VirtualAlbedo", albedo);
                if (normal != null) m_mat.SetTexture("_VirtualNormal", normal);
                if (mask != null) m_mat.SetTexture("_VirtualMask", mask);
            }
            m_atlasBound = albedo != null && normal != null && mask != null;
        }

        // =====================================================================
        // 重建（摊平成 GPU 缓冲）
        // =====================================================================

        void Rebuild()
        {
            ReleaseBuffers();
            m_dirty = false;

            var records = new List<ClusterRecord>();
            var mats = new List<ClusterMaterialData>();
            m_groupDetail.Clear(); m_groupDetailNormal.Clear(); m_groupLookup.Clear();
            m_groupCount = 0;

            // ---- 1. 收集：逐物体 → 逐 submesh → 逐簇（只 LOD0）----
            foreach (var e in m_entries.Values)
            {
                var asset = ClusterMeshCache.Get(e.mesh);
                if (asset == null || asset.clusterCount == 0) continue;
                int subCount = Mathf.Min(e.subs != null ? e.subs.Length : 0, asset.sourceMesh.subMeshCount);
                for (int s = 0; s < subCount; s++)
                {
                    var sub = e.subs[s];
                    if (sub == null) continue;
                    int matIndex = mats.Count;
                    mats.Add(sub.mat);
                    int group = GroupOf(sub.detail, sub.detailNormal);
                    for (int c = 0; c < asset.clusterCount; c++)
                    {
                        if (asset.clusterSubmesh[c] != s) continue;
                        if ((int)asset.clusterMeta[c].z != 0) continue;      // 只画 LOD0
                        records.Add(new ClusterRecord
                        {
                            asset = asset, localToWorld = e.localToWorld,
                            cluster = c, matIndex = matIndex, group = group
                        });
                    }
                }
            }

            m_clusterCount = records.Count;
            if (m_clusterCount == 0)
            {
                m_built = false;
                return;
            }
            m_groupCount = Mathf.Max(1, m_groupCount);

            // ---- 2. 顶点：按【每簇】去重后烘成【世界空间】----
            // 索引绘制要求「同一顶点必须用同一个索引值」才能命中 GPU 顶点缓存，
            // 而不同摆放的同名顶点变换不同 ⇒ 顶点只能按摆放（= 簇记录）展开。
            // 展开后每簇只存去重后的约 66 个顶点（不是 384 个角点），
            // 总量与「按唯一 mesh 一份」同量级，不会像「整份 mesh 按摆放复制」那样爆掉。
            // 既然已经展开了，就把世界变换 / 法线逆转置 / Gram-Schmidt 一起烘死，
            // VS 里只剩 5 次缓冲读取、零矩阵运算。
            var cornerGvid = new int[m_clusterCount * VertsPerCluster];
            var recVertBase = new int[m_clusterCount];
            var recVertCount = new int[m_clusterCount];
            var bounds = new Vector4[m_clusterCount];
            var groups = new uint[m_clusterCount];
            var info = new uint[m_clusterCount * 4];

            // 第一遍：算每簇去重顶点数 ⇒ 总顶点数（一次分配，避免 List 反复扩容）
            int totalVerts = 0;
            for (int i = 0; i < m_clusterCount; i++)
            {
                var asset = records[i].asset;
                int vStart = (int)asset.clusterMeta[records[i].cluster].x;
                int iStart = (int)asset.clusterMeta[records[i].cluster].y;
                int vCount = 0;
                for (int k = 0; k < VertsPerCluster; k++)
                {
                    int local = asset.indices[iStart + k] - vStart;
                    if (local + 1 > vCount) vCount = local + 1;
                }
                recVertBase[i] = totalVerts;
                recVertCount[i] = vCount;
                totalVerts += vCount;
            }

            // 每顶点 2 个 uint4 = 32B（原来是 5 个独立缓冲共 52B）：
            //   e0 = (pos.x, pos.y, pos.z, matIndex | sign<<31)   ← 位置用 asuint 原样透传，零精度损失
            //   e1 = (nrmOct16x2, tanOct16x2, uv.x, uv.y)         ← 法线/切线八面体编码，UV 仍是完整 float
            // 两个 uint4 地址连续 ⇒ 一次缓存行往返就能拿全属性；VS 里也从 5 次 load 降到 2 次。
            var vertArr = new Vector4[totalVerts * 2];

            // 第二遍：逐簇烘顶点 + 角点索引表 + 包围球
            for (int i = 0; i < m_clusterCount; i++)
            {
                var r = records[i];
                var asset = r.asset;
                int vStart = (int)asset.clusterMeta[r.cluster].x;
                int iStart = (int)asset.clusterMeta[r.cluster].y;
                int vBase = recVertBase[i];
                int vCount = recVertCount[i];

                Matrix4x4 m = r.localToWorld;
                Matrix4x4 nm = NormalMatrix(m);
                // 镜像实例（负缩放）：绕序要在索引里翻，副切线符号也要反过来。
                // 反射变换 R 满足 cross(Rn,Rt) = -R·cross(n,t)，而真实副切线是 R·b，
                // 所以 b = -cross(n',t')·sign ⇒ 符号取反才对得上 UV 手性。
                bool flipWinding = m.determinant < 0f;

                for (int v = 0; v < vCount; v++)
                {
                    Vector3 pL = asset.positions[vStart + v];
                    Vector3 nL = asset.normals[vStart + v];
                    Vector4 tL = asset.tangents[vStart + v];
                    int o = vBase + v;

                    Vector3 pW = m.MultiplyPoint3x4(pL);

                    Vector3 nW = nm.MultiplyVector(nL);
                    float nl = nW.magnitude;
                    nW = nl > 1e-8f ? nW / nl : Vector3.up;

                    // 切线只乘线性部分；非均匀缩放下做 Gram-Schmidt 正交化。
                    // 退化时（切线平行法线 / 零切线）normalize(0)=NaN ⇒ 整面法线 NaN ⇒ 全黑，故兜底。
                    Vector3 tW = m.MultiplyVector(new Vector3(tL.x, tL.y, tL.z));
                    Vector3 t = tW - nW * Vector3.Dot(nW, tW);
                    tW = t.sqrMagnitude > 1e-12f ? t.normalized : Vector3.Cross(nW, Vector3.up).normalized;

                    Vector2 uv = asset.uvs[vStart + v];
                    bool sign = tL.w < 0f;
                    if (flipWinding) sign = !sign;      // 镜像实例的副切线手性
                    uint matSign = (uint)r.matIndex | (sign ? 0x80000000u : 0u);

                    // 全部按【位】存进 float4，shader 侧用 asuint 原样取回（不是数值转换，零误差）
                    vertArr[o * 2 + 0] = new Vector4(pW.x, pW.y, pW.z, BitsToFloat(matSign));
                    vertArr[o * 2 + 1] = new Vector4(
                        BitsToFloat(OctEncode(nW)), BitsToFloat(OctEncode(tW)), uv.x, uv.y);
                }

                // 角点 → 全局顶点号（静态，绘制时 compute 直接照抄进索引缓冲）
                // ⚠️ 镜像实例（负缩放，det < 0）会把三角形绕序整体翻掉。shader 是 Cull Back，
                //    不翻绕序的话这类物体的**整块几何会被背面剔除**（模型上直接缺一块）。
                //    烘焙期交换两个角点即可救回来，运行时零成本。
                int dst = i * VertsPerCluster;
                for (int t = 0; t < VertsPerCluster; t += 3)
                {
                    int i0 = vBase + (asset.indices[iStart + t + 0] - vStart);
                    int i1 = vBase + (asset.indices[iStart + t + 1] - vStart);
                    int i2 = vBase + (asset.indices[iStart + t + 2] - vStart);
                    cornerGvid[dst + t + 0] = i0;
                    cornerGvid[dst + t + 1] = flipWinding ? i2 : i1;
                    cornerGvid[dst + t + 2] = flipWinding ? i1 : i2;
                }

                info[i * 4 + 0] = (uint)vBase;
                info[i * 4 + 1] = (uint)r.matIndex;
                groups[i] = (uint)r.group;

                // 包围球：局部球心变换 + 半径乘最大缩放（保守，O(1)，不必逐顶点）
                Vector4 lb = asset.clusterBounds[r.cluster];
                Vector3 c = m.MultiplyPoint3x4(new Vector3(lb.x, lb.y, lb.z));
                Vector3 sc = m.lossyScale;
                float s = Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                bounds[i] = new Vector4(c.x, c.y, c.z, lb.w * Mathf.Max(s, 1e-4f));
            }

            m_vertBuf = new ComputeBuffer(vertArr.Length, 16); m_vertBuf.SetData(vertArr);
            m_cornerBuf = new ComputeBuffer(cornerGvid.Length, 4); m_cornerBuf.SetData(cornerGvid);
            m_boundsBuf = new ComputeBuffer(m_clusterCount, 16); m_boundsBuf.SetData(bounds);
            m_groupBuf = new ComputeBuffer(m_clusterCount, 4); m_groupBuf.SetData(groups);
            m_infoBuf = new ComputeBuffer(m_clusterCount, 16); m_infoBuf.SetData(info);

            m_matBuf = new ComputeBuffer(Mathf.Max(1, mats.Count), 112);
            m_matBuf.SetData(mats.ToArray());

            // ---- 4. 剔除 / 索引 / args（按组）----
            m_planesBuf = new ComputeBuffer(PlanesPerView, 16);
            m_visibleBuf = new ComputeBuffer(m_groupCount * m_clusterCount, 8);
            // 簇 → 本帧可见槽位（不可见 = 0xFFFFFFFF）。BuildIndices 靠它把 dispatch
            // 压成恒定规模的一维展开，而不是按「组内簇数上限 × 组数」开方阵。
            m_slotBuf = new ComputeBuffer(m_clusterCount, 4);
            m_counterBuf = new ComputeBuffer(m_groupCount, 4);

            // 每组在 _DrawIdx 里占「该组簇数 × 384」个 uint（可见数不会超过它），
            // 起点由前缀和给出 ⇒ startIndex 是纯常量，不需要每帧回读计数器。
            var groupCap = new int[Mathf.Max(1, m_groupCount)];
            for (int i = 0; i < m_clusterCount; i++) groupCap[records[i].group]++;
            var drawBase = new uint[groupCap.Length];
            uint cursor = 0;
            for (int g = 0; g < groupCap.Length; g++)
            {
                drawBase[g] = cursor;
                cursor += (uint)groupCap[g] * (uint)VertsPerCluster;
            }
            m_drawBaseBuf = new ComputeBuffer(drawBase.Length, 4);
            m_drawBaseBuf.SetData(drawBase);

            // Target.Index | Target.Raw：D3D11 不允许 index buffer 同时是 Structured，
            // 但允许同时是 Raw ⇒ compute 用 RWByteAddressBuffer 写、draw 直接当索引读，单缓冲零拷贝。
            m_drawIdxBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw,
                Mathf.Max(1, (int)cursor), 4);

            // 索引间接绘制 args = 每组 5 个 uint（indexCount/instanceCount/startIndex/baseVertex/startInstance）
            m_argsBuf = new ComputeBuffer(Mathf.Max(1, m_groupCount * 5), 4,
                ComputeBufferType.IndirectArguments);

            LoadResources();
            m_built = true;
        }

        /// <summary>八面体编码：单位向量 → 2 × unorm16（打包进一个 uint）。
        /// 与 HLSL 侧 OctDecode 必须逐位对应：存的是 0..65535，解码侧乘 1/65535 再 *2-1。
        /// 16 bit/轴 的角度误差约 1e-5，法线/切线完全够用（图集那边的 EncodeNormal 是另一套，别混）。</summary>
        static float BitsToFloat(uint bits) => System.BitConverter.Int32BitsToSingle((int)bits);

        static uint OctEncode(Vector3 n)
        {
            float l = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (l < 1e-8f) { n = Vector3.up; l = 1f; }
            float ex = n.x / l;
            float ey = n.y / l;
            if (n.z < 0f)
            {
                float ox = (1f - Mathf.Abs(ey)) * (ex >= 0f ? 1f : -1f);
                float oy = (1f - Mathf.Abs(ex)) * (ey >= 0f ? 1f : -1f);
                ex = ox; ey = oy;
            }
            uint ux = (uint)Mathf.Clamp(ex * 32767.5f + 32767.5f, 0f, 65535f);
            uint uy = (uint)Mathf.Clamp(ey * 32767.5f + 32767.5f, 0f, 65535f);
            return ux | (uy << 16);
        }

        /// <summary>法线的变换矩阵 = 世界矩阵左上 3x3 的逆转置（非均匀缩放下才正确）。</summary>
        static Matrix4x4 NormalMatrix(Matrix4x4 m)
        {
            Matrix4x4 r = Matrix4x4.identity;
            Vector3 x = new Vector3(m.m00, m.m01, m.m02);
            Vector3 y = new Vector3(m.m10, m.m11, m.m12);
            Vector3 z = new Vector3(m.m20, m.m21, m.m22);
            // 逆转置 = 余子式矩阵 / det；这里直接按行列式构造，退化时回退单位阵
            Vector3 cx = Vector3.Cross(y, z), cy = Vector3.Cross(z, x), cz = Vector3.Cross(x, y);
            float det = Vector3.Dot(x, cx);
            if (Mathf.Abs(det) < 1e-9f) return r;
            float inv = 1f / det;
            r.m00 = cx.x * inv; r.m01 = cx.y * inv; r.m02 = cx.z * inv;
            r.m10 = cy.x * inv; r.m11 = cy.y * inv; r.m12 = cy.z * inv;
            r.m20 = cz.x * inv; r.m21 = cz.y * inv; r.m22 = cz.z * inv;
            return r;
        }

        int GroupOf(Texture detail, Texture detailNormal)
        {
            long key = ((long)Id(detail) << 32) | (uint)Id(detailNormal);
            if (m_groupLookup.TryGetValue(key, out int g)) return g;
            g = m_groupCount++;
            m_groupLookup[key] = g;
            m_groupDetail.Add(detail);
            m_groupDetailNormal.Add(detailNormal);
            return g;
        }

        static int Id(Texture t) => t == null ? 0 : t.GetInstanceID();

        void LoadResources()
        {
            if (m_cull == null)
            {
                m_cull = Resources.Load<ComputeShader>(CullShaderName);
                if (m_cull == null)
                    Debug.LogError($"[ClusterGBuffer] 找不到剔除 compute：Resources/{CullShaderName}");
            }
            if (m_mat == null)
            {
                var sh = Shader.Find(DrawShaderName);
                if (sh == null)
                    Debug.LogError($"[ClusterGBuffer] 找不到 shader：{DrawShaderName}");
                else
                {
                    m_mat = new Material(sh) { name = "ClusterGBuffer" };
                    m_atlasBound = false;   // 图集由 ClusterObject 回填，回填前不画
                }
            }
            if (m_mat != null)
            {
                m_mat.SetBuffer("_Vertices", m_vertBuf);
                m_mat.SetBuffer("_ClusterMaterials", m_matBuf);
            }
            // 图集若已存在（先建图集、后建物体），立刻回填
            if (!m_atlasBound && ClusterObject.TryGetAtlas(out var a, out var n, out var msk))
                BindAtlas(a, n, msk);
        }

        // =====================================================================
        // 每帧：准备 → 剔除 → 绘制
        // =====================================================================

        /// <summary>重建（如需要）+ 算摄像机视锥平面。返回本帧是否可用 cluster 画 GBuffer。</summary>
        public bool Prepare(Camera camera)
        {
            if (m_dirty) Rebuild();
            if (!m_built || camera == null) return false;

            // 与绘制侧逐位一致的 world→clip：GL.GetGPUProjectionMatrix(proj, true) * view。
            // 坑：裸 proj*view 在 D3D reversed-Z 下提平面会把近/远算反 ⇒ 过剔除（阴影路径踩过）。
            var gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            m_w2c = gpuProj * camera.worldToCameraMatrix;
            var planes = GeometryUtility.CalculateFrustumPlanes(m_w2c);
            for (int p = 0; p < PlanesPerView; p++)
            {
                var pl = planes[p];
                m_planes[p] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }
            m_planesBuf?.SetData(m_planes);
            return IsReady;
        }

        /// <summary>
        /// 只更新 world→clip（绘制用的那个矩阵），【不动】剔除平面与可见列表缓冲。
        /// 用途：Scene 视图直接用主相机已经算好的可见簇 / indirect args 来画 ——
        /// 可见性跟主相机走，Scene 不跑 Cull、也就没有额外的剔除开销。
        ///
        /// 为什么要这样：Scene 相机的视锥远大于游戏相机，独立剔除几乎什么都剔不掉，
        /// 只会白跑一遍 31856 簇的 dispatch，还看不出任何"被剔掉"的效果。
        /// 复用主相机的可见集才是"看到剔除结果"的正确方式：主相机看不到的簇，
        /// Scene 里也不会画出来。
        ///
        /// 返回 false ⇒ 主相机还没剔除过（刚进 Play / 主相机被禁用），调用方应回退 SRP。
        /// </summary>
        public bool PrepareViewOnly(Camera camera)
        {
            if (m_dirty) Rebuild();
            if (!m_built || camera == null || !m_culled) return false;
            var gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            m_w2c = gpuProj * camera.worldToCameraMatrix;
            return IsReady;
        }

        /// <summary>一次 GPU 剔除，产出每组的可见簇列表与 indirect args。
        /// 必须在任何绘制之前、且同一帧只调一次（GBuffer 与 Feedback 共用这份结果）。</summary>
        public void Cull(CommandBuffer cmd) => Cull(cmd, 0, 0);

        public void Cull(CommandBuffer cmd, int picW, int picH)
        {
            if (!m_built || m_cull == null || m_clusterCount == 0) return;

            int groups = Mathf.CeilToInt(m_clusterCount / (float)ThreadGroupSize);
            int gGroups = Mathf.Max(1, Mathf.CeilToInt(m_groupCount / 8f));

            int kReset = m_cull.FindKernel("Reset");
            int kCull = m_cull.FindKernel("CullAll");
            int kIdx = m_cull.FindKernel("BuildIndices");
            int kArgs = m_cull.FindKernel("BuildArgs");

            m_cull.SetInt("_HiZEnable", (hiZEnable && m_hizValid) ? 1 : 0);
            m_cull.SetFloat("_HiZBias", HiZBias);
            m_cull.SetFloat("_HiZRelBias", HiZRelBias);
            m_cull.SetFloat("_HiZMinSize", HiZMinScreenSize);
            // 深度方向：D3D 恒为 reversed-Z（1=近），但别硬编码——GL/Vulkan 上就是相反的。
            // 传错的表现是「近的被剔、远的还在，一堆穿孔」。
            m_cull.SetInt("_ZReversed", SystemInfo.usesReversedZBuffer ? 1 : 0);
            if (picW > 0 && picH > 0)
            {
                m_cull.SetVector("_PicSize", new Vector4(picW, picH, 0f, 0f));
                // 遮挡测试用【上一帧】的 VP：金字塔是上一帧深度建的，两者必须同源。
                m_cull.SetMatrix("_ViewProj", m_prevVP);
            }
            // 无论开不开都必须绑：_HiZ0..3 / _SrcDepth 是 compute 里的全局声明，
            // 所有 kernel 都会要求它们已绑定（否则 Submit 时报 "Property (_HiZ0) ... is not set"）。
            // 没建金字塔时塞 1×1 占位，_HiZEnable=0 保证 kernel 根本不会去采样它们。
            bool hzOk = hiZEnable && m_hizValid && m_hiz0 != null;
            var hz0 = hzOk ? m_hiz0 : DummyHiZ();
            var hz1 = (hzOk && m_hiz1 != null) ? m_hiz1 : hz0;
            var hz2 = (hzOk && m_hiz2 != null) ? m_hiz2 : hz0;
            var hz3 = (hzOk && m_hiz3 != null) ? m_hiz3 : hz0;
            cmd.SetComputeTextureParam(m_cull, kCull, "_HiZ0", hz0);
            cmd.SetComputeTextureParam(m_cull, kCull, "_HiZ1", hz1);
            cmd.SetComputeTextureParam(m_cull, kCull, "_HiZ2", hz2);
            cmd.SetComputeTextureParam(m_cull, kCull, "_HiZ3", hz3);
            cmd.SetComputeTextureParam(m_cull, kCull, "_SrcDepth", hz0);
            m_cull.SetInt("_HiZ0SizeX", hz0.width);
            m_cull.SetInt("_HiZ0SizeY", hz0.height);
            m_cull.SetInt("_HiZ1SizeX", hz1.width);
            m_cull.SetInt("_HiZ1SizeY", hz1.height);
            m_cull.SetInt("_HiZ2SizeX", hz2.width);
            m_cull.SetInt("_HiZ2SizeY", hz2.height);
            m_cull.SetInt("_HiZ3SizeX", hz3.width);
            m_cull.SetInt("_HiZ3SizeY", hz3.height);

            // Hi-Z 统计缓冲：_HiZStat 是 compute 里的全局声明 ⇒ 每个 kernel 都得绑。
            if (m_hizStat == null)
                m_hizStat = new ComputeBuffer(8, sizeof(uint), ComputeBufferType.Structured);
            if (debugStats)
            {
                // 0..4 清零（只统计本帧），5..7 填本帧状态，让日志能自证"到底跑没跑 Hi-Z"。
                m_dbgStat[0] = m_dbgStat[1] = m_dbgStat[2] = m_dbgStat[3] = m_dbgStat[4] = 0;
                m_dbgStat[5] = (uint)(hzOk ? 1 : 0);
                m_dbgStat[6] = (uint)(m_hizValid ? 1 : 0);
                m_dbgStat[7] = (uint)Mathf.Max(0, picW);
                m_hizStat.SetData(m_dbgStat);
                m_dbgPicW = picW;
                m_cull.SetInt("_HiZDebug", 1);
            }
            else m_cull.SetInt("_HiZDebug", 0);

            foreach (int k in new[] { kReset, kCull, kIdx, kArgs })
            {
                m_cull.SetInt("_GroupCount", m_groupCount);
                m_cull.SetInt("_ClusterCount", m_clusterCount);
                m_cull.SetInt("_TrisPerCluster", TrisPerCluster);
                m_cull.SetInt("_DebugForceAll", debugForceAll == 0 ? 0 : 1);
                m_cull.SetBuffer(k, "_HiZStat", m_hizStat);
                m_cull.SetBuffer(k, "_DrawBase", m_drawBaseBuf);
                m_cull.SetBuffer(k, "_Counter", m_counterBuf);
                m_cull.SetBuffer(k, "_Args", m_argsBuf);
                m_cull.SetBuffer(k, "_Visible", m_visibleBuf);
                m_cull.SetBuffer(k, "_SlotOf", m_slotBuf);
                m_cull.SetBuffer(k, "_Planes", m_planesBuf);
                m_cull.SetBuffer(k, "_ClusterGroup", m_groupBuf);
                m_cull.SetBuffer(k, "_ClusterBounds", m_boundsBuf);
                m_cull.SetBuffer(k, "_ClusterInfo", m_infoBuf);
            }
            // 只有 BuildIndices 用到这两份
            m_cull.SetBuffer(kIdx, "_CornerGvid", m_cornerBuf);
            m_cull.SetBuffer(kIdx, "_DrawIdx", m_drawIdxBuf);

            cmd.DispatchCompute(m_cull, kReset, Mathf.Max(1, Mathf.CeilToInt(m_groupCount / 64f)), 1, 1);
            cmd.DispatchCompute(m_cull, kCull, groups, 1, 1);
            // 从这一刻起缓冲里的可见列表 / args 有效，Scene 视图可以复用它直接画。
            m_culled = true;
            // 一维展开：总簇数 × 384 个角点，每线程一个角点。不可见的簇在 kernel 里
            // 靠 _SlotOf 直接退出 ⇒ 规模只和「场景簇数」有关，与组数无关。
            // ⚠️【试过并回退】每簇一个 384 线程组（组数 ÷6）：occupancy 下降，反而变慢。
            // ⚠️ D3D11 单维最多 65535 个线程组，簇数上万会超 ⇒ 二维拆开，kernel 用 _IdxDimX 拼回。
            int totalThreads = m_clusterCount * VertsPerCluster;
            int totalGroups = Mathf.Max(1, Mathf.CeilToInt(totalThreads / (float)ThreadGroupSize));
            int gx = Mathf.Min(65535, totalGroups);
            int gy = Mathf.Max(1, Mathf.CeilToInt(totalGroups / (float)gx));
            m_cull.SetInt("_IdxDimX", gx * ThreadGroupSize);
            cmd.DispatchCompute(m_cull, kIdx, gx, gy, 1);
            cmd.DispatchCompute(m_cull, kArgs, gGroups, 1, 1);

            // ⚠️ 不要在这里直接发起 AsyncGPUReadback：此刻 CullAll 还没进命令队列
            // （ExecuteBuffer 在管线里稍后才调），读回拿到的是刚才 SetData 写进去的 0。
            // 改成打标记，由管线在 ExecuteBuffer 之后调 FlushDebugStats()。
            if (debugStats && !m_hizStatPending && Time.frameCount % 120 == 0)
                m_hizStatDue = true;
        }

        /// <summary>
        /// 由管线在【ExecuteBuffer 之后】调用：真正发起 Hi-Z 统计的异步读回。
        /// 只能在这时读——AsyncGPUReadback 与已入队的图形命令是有序的，提前读只会拿到清零值。
        /// </summary>
        public void FlushDebugStats()
        {
            if (!m_hizStatDue) return;
            m_hizStatDue = false;
            RequestHiZStats();
        }

        /// <summary>
        /// 调试：异步读回 Hi-Z 判定统计（不 stall GPU）。只在 ClusterDebug 模式下调用。
        /// 用来判断"到底剔了多少 / 有多少被跳过"——如果剔除比例明显偏高，就是误剔。
        /// </summary>
        void RequestHiZStats()
        {
            if (m_hizStat == null) return;
            m_hizStatPending = true;
            int picW = m_dbgPicW;
            AsyncGPUReadback.Request(m_hizStat, req =>
            {
                m_hizStatPending = false;
                if (req.hasError) return;
                var d = req.GetData<uint>();
                Debug.Log($"[Cluster Hi-Z] 启用={d[5]} 金字塔有效={d[6]} 剔选屏宽={picW} ｜ " +
                          $"测了 {d[0]} 保留 / 剔 {d[1]} / 跳过 {d[3]}（太小或太大）/ 矩形不可靠 {d[4]}");
            });
        }

        /// <summary>
        /// 用【本帧】的相机深度重建遮挡金字塔，供【下一帧】的 CullAll 使用。
        ///
        /// ⚠️ 必须传【非 MSAA】的深度源。`_GBufferDepthTex` 是带 antiAliasing 的 depth RT，
        /// 直接当 SRV `Load` 读到的是未 resolve 的样本（实测：画面一堆穿孔，近的被剔、远的还在）。
        /// 所以调用方要传管线里那份「为后处理/粒子准备的深度拷贝」`_CameraDepthTexture`（MSAA=1）。
        ///
        /// ⚠️ 也正因为要用那份拷贝，本函数必须在深度拷贝【之后】调用（DrawGBufferFinal 里）。
        /// </summary>
        public void BuildHiZ(CommandBuffer cmd, RenderTargetIdentifier srcDepth, int picW, int picH)
        {
            if (!m_built || m_cull == null || !hiZEnable) return;
            if (picW <= 0 || picH <= 0) return;
            EnsureHiZ(picW, picH);
            if (m_hiz0 == null || m_hiz3 == null) return;

            int k0 = m_cull.FindKernel("BuildOccluder0");
            int k1 = m_cull.FindKernel("BuildOccluder1");
            int k2 = m_cull.FindKernel("BuildOccluder2");
            int k3 = m_cull.FindKernel("BuildOccluder3");

            m_cull.SetInt("_DepthW", picW);
            m_cull.SetInt("_DepthH", picH);
            m_cull.SetInt("_HiZ0SizeX", m_hiz0.width);
            m_cull.SetInt("_HiZ0SizeY", m_hiz0.height);
            m_cull.SetInt("_HiZ1SizeX", m_hiz1.width);
            m_cull.SetInt("_HiZ1SizeY", m_hiz1.height);
            m_cull.SetInt("_HiZ2SizeX", m_hiz2.width);
            m_cull.SetInt("_HiZ2SizeY", m_hiz2.height);
            m_cull.SetInt("_HiZ3SizeX", m_hiz3.width);
            m_cull.SetInt("_HiZ3SizeY", m_hiz3.height);

            // 所有全局纹理在【每个】kernel 上都绑一遍：Unity 的 kernel 反射在部分平台/驱动下
            // 会把未使用的全局资源也算进来，漏绑就报 "not set"。多绑一次零成本。
            void Bind(CommandBuffer c, int k)
            {
                c.SetComputeTextureParam(m_cull, k, "_SrcDepth", srcDepth);
                c.SetComputeTextureParam(m_cull, k, "_HiZ0", m_hiz0);
                c.SetComputeTextureParam(m_cull, k, "_HiZ1", m_hiz1);
                c.SetComputeTextureParam(m_cull, k, "_HiZ2", m_hiz2);
                c.SetComputeTextureParam(m_cull, k, "_HiZ3", m_hiz3);
                if (m_hizStat != null) m_cull.SetBuffer(k, "_HiZStat", m_hizStat);
            }
            void Run(int k, RenderTexture dst)
                => cmd.DispatchCompute(m_cull, k,
                    Mathf.Max(1, Mathf.CeilToInt(dst.width / 8f)),
                    Mathf.Max(1, Mathf.CeilToInt(dst.height / 8f)), 1);

            // 逐级 2×2 归约：1/1 → 1/2 → 1/4 → 1/8 → 1/16
            Bind(cmd, k0); Run(k0, m_hiz0);
            Bind(cmd, k1); Run(k1, m_hiz1);
            Bind(cmd, k2); Run(k2, m_hiz2);
            Bind(cmd, k3); Run(k3, m_hiz3);

            m_hizValid = true;      // 从下一帧开始生效（本帧的 Cull 已经跑过了）
            m_prevVP = m_w2c;       // 记下"这金字塔是哪套 VP 建的"，下一帧遮挡测试要用它
        }

        void EnsureHiZ(int picW, int picH)
        {
            // 四级金字塔：1/2 → 1/4 → 1/8 → 1/16 屏（级数被 UAV 上限锁死，见字段注释）。
            // 最细一级必须是 1/2（甚至 1/1）：块越大越容易把邻居的最远深度算进来。
            int w0 = Mathf.Max(1, picW / 2), h0 = Mathf.Max(1, picH / 2);
            int w1 = Mathf.Max(1, w0 / 2), h1 = Mathf.Max(1, h0 / 2);
            int w2 = Mathf.Max(1, w1 / 2), h2 = Mathf.Max(1, h1 / 2);
            int w3 = Mathf.Max(1, w2 / 2), h3 = Mathf.Max(1, h2 / 2);
            if (m_hiz0 != null && m_hiz0.width == w0 && m_hiz0.height == h0
                && m_hiz3 != null && m_hiz3.width == w3 && m_hiz3.height == h3)
                return;

            ReleaseHiZ();
            m_hiz0 = NewHiZRT(w0, h0, "ClusterHiZ0");
            m_hiz1 = NewHiZRT(w1, h1, "ClusterHiZ1");
            m_hiz2 = NewHiZRT(w2, h2, "ClusterHiZ2");
            m_hiz3 = NewHiZRT(w3, h3, "ClusterHiZ3");
            m_hizValid = false;
        }

        static RenderTexture NewHiZRT(int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                useMipMap = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = name,
            };
            rt.Create();
            return rt;
        }

        void ReleaseHiZ()
        {
            if (m_hiz0 != null) { m_hiz0.Release(); Object.DestroyImmediate(m_hiz0); m_hiz0 = null; }
            if (m_hiz1 != null) { m_hiz1.Release(); Object.DestroyImmediate(m_hiz1); m_hiz1 = null; }
            if (m_hiz2 != null) { m_hiz2.Release(); Object.DestroyImmediate(m_hiz2); m_hiz2 = null; }
            if (m_hiz3 != null) { m_hiz3.Release(); Object.DestroyImmediate(m_hiz3); m_hiz3 = null; }
            m_hizValid = false;
        }

        /// <summary>
        /// 1×1 占位金字塔，只为了满足「compute 里全局声明的纹理必须已绑定」这个要求。
        /// 内容不会被读：绑占位时 _HiZEnable 一定是 0，kernel 里那句 if 直接跳过。
        /// </summary>
        RenderTexture DummyHiZ()
        {
            if (m_hizDummy == null) m_hizDummy = NewHiZRT(1, 1, "ClusterHiZDummy");
            return m_hizDummy;
        }

        /// <summary>画 GBuffer（pass 0）。返回 false = 没画，调用方应保留 SRP 绘制。</summary>
        public bool DrawGBuffer(CommandBuffer cmd) => Draw(cmd, 0);

        /// <summary>画 RVT Feedback（pass 1）。返回 false = 没画，调用方应回退 DrawRenderers。</summary>
        public bool DrawFeedback(CommandBuffer cmd) => Draw(cmd, 1);

        bool Draw(CommandBuffer cmd, int pass)
        {
            if (!IsReady) return false;
            if (m_mpb == null) m_mpb = new MaterialPropertyBlock();

            // 阴影路径的同款坑：DrawProceduralIndirect 不喂内建矩阵，w2c 必须逐 draw 显式传。
            m_mpb.SetMatrix("_ClusterGBufferW2C", m_w2c);

            // Feedback(pass 1) 只写页表：fragment(FeedbackColor) 只用 _MapId(每簇材质缓冲) + baseUV，
            // 从不采样 _DetailMap / _DetailNormalMap。这两张【每材质】贴图是 GBuffer(pass 0) 才需要的，
            // 故 Feedback 绝不为它们按组拆分/绑定 —— 否则就是 GBuffer 路径的死工作遗物。
            bool needsDetail = (pass == 0);
            for (int g = 0; g < m_groupCount; g++)
            {
                if (needsDetail)
                {
                    m_mpb.SetTexture("_DetailMap",
                        m_groupDetail[g] != null ? m_groupDetail[g] : (Texture)Texture2D.grayTexture);
                    m_mpb.SetTexture("_DetailNormalMap",
                        m_groupDetailNormal[g] != null ? m_groupDetailNormal[g] : (Texture)Texture2D.normalTexture);
                }
                // 索引间接绘制：indexBuffer = m_drawIdxBuf（compute 只给【可见】簇写索引），
                // args 每组 5 个 uint，startIndex 由 BuildArgs 写成 group × stride。
                cmd.DrawProceduralIndirect(m_drawIdxBuf, Matrix4x4.identity, m_mat, pass,
                    MeshTopology.Triangles, m_argsBuf, sizeof(uint) * 5 * g, m_mpb);
            }
            return true;
        }

        // =====================================================================
        // 释放
        // =====================================================================

        public void ClearEntries()
        {
            m_entries.Clear();
            m_dirty = true;
        }

        public void Release()
        {
            m_entries.Clear();
            ReleaseBuffers();
            ReleaseHiZ();           // Hi-Z 金字塔是自建 RT，不随 Unity 的临时 RT 管理，必须自己放
            m_hizStat?.Release();
            m_hizStat = null;
            if (m_hizDummy != null)
            {
                m_hizDummy.Release();
                Object.DestroyImmediate(m_hizDummy);
                m_hizDummy = null;
            }
            // 图集 RT 也会随 ClusterCore.Cleanup 销毁：下次进 Play 必须重新回填，
            // 否则材质里留着的是已销毁的纹理引用。
            m_atlasBound = false;
            m_dirty = true;
        }

        void ReleaseBuffers()
        {
            m_vertBuf?.Release(); m_cornerBuf?.Release();
            m_boundsBuf?.Release(); m_groupBuf?.Release(); m_infoBuf?.Release();
            m_matBuf?.Release();
            m_planesBuf?.Release(); m_visibleBuf?.Release();
            m_counterBuf?.Release(); m_argsBuf?.Release();
            m_drawIdxBuf?.Release(); m_drawBaseBuf?.Release(); m_slotBuf?.Release();
            m_vertBuf = m_cornerBuf = null;
            m_boundsBuf = m_groupBuf = m_infoBuf = m_matBuf = null;
            m_planesBuf = m_visibleBuf = m_counterBuf = m_argsBuf = null;
            m_drawIdxBuf = null; m_drawBaseBuf = null; m_slotBuf = null;
            m_built = false;
            m_culled = false;   // 缓冲全换了，旧的可见列表 / args 作废
        }
    }
}
