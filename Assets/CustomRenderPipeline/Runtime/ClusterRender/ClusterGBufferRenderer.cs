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

        ComputeBuffer m_posBuf, m_nrmBuf, m_tanBuf, m_uvBuf, m_vertMatBuf, m_cornerBuf;
        ComputeBuffer m_boundsBuf, m_groupBuf, m_infoBuf;
        ComputeBuffer m_matBuf;
        ComputeBuffer m_planesBuf, m_visibleBuf, m_counterBuf, m_argsBuf;
        GraphicsBuffer m_drawIdxBuf;

        ComputeBuffer m_drawBaseBuf, m_slotBuf;

        int m_clusterCount;
        bool m_built;
        bool m_atlasBound;
        Matrix4x4 m_w2c;
        readonly Vector4[] m_planes = new Vector4[PlanesPerView];

        // ---------------- 分组（detail 贴图对） ----------------
        int m_groupCount;
        readonly List<Texture> m_groupDetail = new List<Texture>();
        readonly List<Texture> m_groupDetailNormal = new List<Texture>();
        readonly Dictionary<long, int> m_groupLookup = new Dictionary<long, int>();

        /// <summary>排查用：非 0 时跳过视锥剔除，全部簇都画。</summary>
        public int debugForceAll = 0;

        /// <summary>本帧是否由管线启用（DefferPipeline 每帧设置）。
        /// 关掉资产开关后必须立刻停用，否则 GBuffer 走 SRP、Feedback 还走 Cluster，两边会分叉。</summary>
        public bool active;

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

            var posArr = new Vector3[totalVerts];
            var nrmArr = new Vector3[totalVerts];
            var tanArr = new Vector4[totalVerts];
            var uvArr = new Vector2[totalVerts];
            var matArr = new uint[totalVerts];

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

                for (int v = 0; v < vCount; v++)
                {
                    Vector3 pL = asset.positions[vStart + v];
                    Vector3 nL = asset.normals[vStart + v];
                    Vector4 tL = asset.tangents[vStart + v];
                    int o = vBase + v;

                    posArr[o] = m.MultiplyPoint3x4(pL);

                    Vector3 nW = nm.MultiplyVector(nL);
                    float nl = nW.magnitude;
                    nW = nl > 1e-8f ? nW / nl : Vector3.up;
                    nrmArr[o] = nW;

                    // 切线只乘线性部分；非均匀缩放下做 Gram-Schmidt 正交化。
                    // 退化时（切线平行法线 / 零切线）normalize(0)=NaN ⇒ 整面法线 NaN ⇒ 全黑，故兜底。
                    Vector3 tW = m.MultiplyVector(new Vector3(tL.x, tL.y, tL.z));
                    Vector3 t = tW - nW * Vector3.Dot(nW, tW);
                    tW = t.sqrMagnitude > 1e-12f ? t.normalized : Vector3.Cross(nW, Vector3.up).normalized;

                    tanArr[o] = new Vector4(tW.x, tW.y, tW.z, tL.w);
                    uvArr[o] = asset.uvs[vStart + v];
                    matArr[o] = (uint)r.matIndex;
                }

                // 角点 → 全局顶点号（静态，绘制时 compute 直接照抄进索引缓冲）
                int dst = i * VertsPerCluster;
                for (int k = 0; k < VertsPerCluster; k++)
                    cornerGvid[dst + k] = vBase + (asset.indices[iStart + k] - vStart);

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

            m_posBuf = new ComputeBuffer(totalVerts, 12); m_posBuf.SetData(posArr);
            m_nrmBuf = new ComputeBuffer(totalVerts, 12); m_nrmBuf.SetData(nrmArr);
            m_tanBuf = new ComputeBuffer(totalVerts, 16); m_tanBuf.SetData(tanArr);
            m_uvBuf  = new ComputeBuffer(totalVerts, 8);  m_uvBuf.SetData(uvArr);
            m_vertMatBuf = new ComputeBuffer(totalVerts, 4); m_vertMatBuf.SetData(matArr);
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
                m_mat.SetBuffer("_Positions", m_posBuf);
                m_mat.SetBuffer("_Normals", m_nrmBuf);
                m_mat.SetBuffer("_Tangents", m_tanBuf);
                m_mat.SetBuffer("_UVs", m_uvBuf);
                m_mat.SetBuffer("_MatIndex", m_vertMatBuf);
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

        /// <summary>一次 GPU 剔除，产出每组的可见簇列表与 indirect args。
        /// 必须在任何绘制之前、且同一帧只调一次（GBuffer 与 Feedback 共用这份结果）。</summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!m_built || m_cull == null || m_clusterCount == 0) return;

            int groups = Mathf.CeilToInt(m_clusterCount / (float)ThreadGroupSize);
            int gGroups = Mathf.Max(1, Mathf.CeilToInt(m_groupCount / 8f));

            int kReset = m_cull.FindKernel("Reset");
            int kCull = m_cull.FindKernel("CullAll");
            int kIdx = m_cull.FindKernel("BuildIndices");
            int kArgs = m_cull.FindKernel("BuildArgs");

            foreach (int k in new[] { kReset, kCull, kIdx, kArgs })
            {
                m_cull.SetInt("_GroupCount", m_groupCount);
                m_cull.SetInt("_ClusterCount", m_clusterCount);
                m_cull.SetInt("_TrisPerCluster", TrisPerCluster);
                m_cull.SetInt("_DebugForceAll", debugForceAll == 0 ? 0 : 1);
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
            // 一维展开：总簇数 × 384 个角点，每线程一个角点。不可见的簇在 kernel 里
            // 靠 _SlotOf 直接退出 ⇒ 规模只和「场景簇数」有关，与组数无关。
            // ⚠️ D3D11 单维最多 65535 个线程组，簇数上万会超 ⇒ 二维拆开，kernel 用 _IdxDimX 拼回。
            int totalThreads = m_clusterCount * VertsPerCluster;
            int totalGroups = Mathf.Max(1, Mathf.CeilToInt(totalThreads / (float)ThreadGroupSize));
            int gx = Mathf.Min(65535, totalGroups);
            int gy = Mathf.Max(1, Mathf.CeilToInt(totalGroups / (float)gx));
            m_cull.SetInt("_IdxDimX", gx * ThreadGroupSize);
            cmd.DispatchCompute(m_cull, kIdx, gx, gy, 1);
            cmd.DispatchCompute(m_cull, kArgs, gGroups, 1, 1);
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
            for (int g = 0; g < m_groupCount; g++)
            {
                m_mpb.SetTexture("_DetailMap",
                    m_groupDetail[g] != null ? m_groupDetail[g] : (Texture)Texture2D.grayTexture);
                m_mpb.SetTexture("_DetailNormalMap",
                    m_groupDetailNormal[g] != null ? m_groupDetailNormal[g] : (Texture)Texture2D.normalTexture);
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
            // 图集 RT 也会随 ClusterCore.Cleanup 销毁：下次进 Play 必须重新回填，
            // 否则材质里留着的是已销毁的纹理引用。
            m_atlasBound = false;
            m_dirty = true;
        }

        void ReleaseBuffers()
        {
            m_posBuf?.Release(); m_nrmBuf?.Release(); m_tanBuf?.Release();
            m_uvBuf?.Release(); m_vertMatBuf?.Release(); m_cornerBuf?.Release();
            m_boundsBuf?.Release(); m_groupBuf?.Release(); m_infoBuf?.Release();
            m_matBuf?.Release();
            m_planesBuf?.Release(); m_visibleBuf?.Release();
            m_counterBuf?.Release(); m_argsBuf?.Release();
            m_drawIdxBuf?.Release(); m_drawBaseBuf?.Release(); m_slotBuf?.Release();
            m_posBuf = m_nrmBuf = m_tanBuf = m_uvBuf = m_vertMatBuf = m_cornerBuf = null;
            m_boundsBuf = m_groupBuf = m_infoBuf = m_matBuf = null;
            m_planesBuf = m_visibleBuf = m_counterBuf = m_argsBuf = null;
            m_drawIdxBuf = null; m_drawBaseBuf = null; m_slotBuf = null;
            m_built = false;
        }
    }
}
