using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// 【cluster 管线】阴影渲染器。资产开关切换三种模式：SRP / Cluster / ClusterDebug。
    ///
    /// 当前流程（Cluster / ClusterDebug）：
    ///   · 每帧 4 个 compute dispatch（Reset / CullAll / BuildIndices / BuildArgs）一次性剔完【所有级联】——
    ///     CullAll 每线程一个簇、内部循环所有级联做球-平面剔除，写出 _SlotOf（不可见 = 0xFFFFFFFF）；
    ///     BuildIndices 只给【可见】簇铺索引；BuildArgs 每线程一个级联写 indirect args。
    ///   · 每级联一次【索引】DrawProceduralIndirect（共 4 次 draw），VS 里只剩 _Positions[SV_VertexID]。
    ///   · LOD：当前只画 LOD0。
    ///
    /// 数据布局（摊平到世界空间，静态场景，运行时不乘实例矩阵）：
    ///   _Positions  —— float3 世界空间顶点
    ///   _CornerGvid —— int32[簇 × 384] 角点 → 全局顶点号（重建期烘死）
    ///   _SlotOf     —— [级联 × 簇] 本帧可见槽位
    ///   _DrawIdx    —— GraphicsBuffer(Index|Raw)，compute 每级联写 [slot*384 + corner]
    ///
    /// 剔除口径（2026-10-28 复核）：平行光的级联是正交投影，且投影方向就是光线方向 ⇒
    /// 「簇包围球在光空间 x/y 盒子外」严格等价于「它投出的阴影也在盒子外」，
    /// 所以 x/y 平面剔除是**精确**的，不会在盒子内部挖洞。反过来说：盒子内部的阴影缺块
    /// 一定来自别处（投射物没进簇集合 / 沿光轴的深度范围没覆盖 / 深度偏移把薄几何抹掉）。
    ///
    /// 纯几何链路：不采任何贴图。
    /// </summary>
    public class ClusterShadowRenderer
    {
        public const int MaxCascades = 8;          // 方向光级联上限（Unity 最多 4，留余量）

        /// <summary>
        /// 每簇三角形数。改这个值必须同步改 shader 里的 TRIS_PER_CLUSTER
        /// （= 本值），否则索引区间会错位。
        /// </summary>
        public const int TrisPerCluster = 128;

        /// <summary>每簇索引数 = TrisPerCluster × 3。shader 不再硬编码顶点数。</summary>
        public const int VertsPerCluster = TrisPerCluster * 3;

        const int PlanesPerCascade = 6;          // 标准视锥 6 平面（与 compute 内 _Planes[c*6+p] 口径一致）
        const int ThreadGroupSize = 64;

        // Resources.Load 的路径 = 相对任意 Resources 文件夹、不带扩展名
        const string DrawShaderName = "Cluster/ClusterShadow";
        const string CullShaderName = "Cluster/ClusterShadowCull";

        static ClusterShadowRenderer s_instance;
        public static ClusterShadowRenderer Instance
        {
            get
            {
                if (s_instance == null) s_instance = new ClusterShadowRenderer();
                return s_instance;
            }
        }

        // ---------------- 场景登记 ----------------
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Matrix4x4> worlds = new List<Matrix4x4>();
        readonly Dictionary<Mesh, ClusterMeshAsset> baked = new Dictionary<Mesh, ClusterMeshAsset>();

        // 全部簇的世界包围球（Rebuild 时算好）——深度范围推导用它反推所需 z 厚度。
        Vector3 m_sceneCenter;
        float m_sceneRadius;

        /// <summary>取簇集合的整体包围球（世界空间）。没建好时返回 false。</summary>
        public bool TryGetSceneBounds(out Vector3 center, out float radius)
        {
            center = m_sceneCenter;
            radius = m_sceneRadius;
            return built && m_sceneRadius > 0f;
        }

        /// <summary>
        /// 把正交投影的 z 范围按 pad 倍**围绕中心**扩张（pad &lt;= 1 原样返回）。
        ///
        /// 正交下 z_ndc = m22 · z_view + m23，范围中心 = -m23/m22。
        /// 让 m22、m23 同除 pad ⇒ 中心不变、斜率变小 ⇒ 可视 z 范围扩大 pad 倍。
        ///
        /// ⚠️ 必须对「渲染矩阵、图集采样矩阵、SetViewProjectionMatrices」用同一个结果，
        /// 否则两侧深度约定不一致 ⇒ 阴影整体错位。Shadows.cs 里在拿到 projectionMatrix 后
        /// 立刻替换，下游三处自然就一致了。
        /// </summary>
        public static Matrix4x4 PadOrthoDepth(Matrix4x4 proj, float pad)
        {
            if (pad <= 1.0001f) return proj;
            proj.m22 /= pad;
            proj.m23 /= pad;
            return proj;
        }

        // 最近一次场景扫描的统计：用来确认「该投影的物体是不是全部进了簇集合」。
        // 阴影缺块的第一嫌疑永远是「投射物压根没登记」，所以这几个数要在 Rebuild 日志里能看到。
        int m_scanTotal, m_scanSkipDisabled, m_scanSkipOff, m_scanSkipCutout, m_scanSkipNoMesh;

        // ---------------- GPU 资源 ----------------
        Material drawMat;
        MaterialPropertyBlock m_mpb;     // [绘制] 逐 draw 显式传入 world→clip 矩阵（per-draw，互不污染）
        ComputeShader cullShader;
        ComputeBuffer posBuf, idxBuf, boundsBuf;
        // 索引缓冲由 compute 每帧按【可见】簇重排（Target.Index | Raw ⇒ 可当 UAV 写、也可当索引读）。
        // 这样 GPU 顶点缓存能真正复用（每簇 384 角点 → 约 66 次 VS），VS 里只剩 _Positions[SV_VertexID]。
        GraphicsBuffer drawIdxBuf;
        ComputeBuffer planesBuf, visibleBuf, counterBuf, argsBuf;
        ComputeBuffer slotBuf;

        int clusterCount;
        bool built, dirty = true;
        /// <summary>本帧 ScanScene 是否跳过了「需 alpha 镂空」的投射物（草/蕨/灌木）。
        /// 有则 Shadows.cs 需用 SRP 路径单独画它们的阴影（Cluster 纯几何 shader 无法镂空）。</summary>
        bool m_hasCutoffCasters = false;

        // ---------------- 每帧级联数据 ----------------
        int cascadeCount;
        readonly Vector4[] cpuPlanes = new Vector4[MaxCascades * PlanesPerCascade];

        /// <summary>调试开关：非 0 时 compute 跳过所有剔除、强制全可见（隔离测试用）。</summary>
        public int debugForceAll = 0;

        public bool IsReady => built && !dirty;
        public int ClusterCount => clusterCount;
        public int CascadeCount => cascadeCount;

        /// <summary>本帧是否有「需 alpha 镂空」的投射物被 ScanScene 跳过（草/蕨/灌木等）。
        /// Shadows.cs 据此决定是否额外用 SRP 阴影路径画它们的镂空阴影。</summary>
        public bool HasCutoffCasters() => m_hasCutoffCasters;

        // ---------------- 注册 ----------------

        public void Register(Mesh mesh, Matrix4x4 world)
        {
            if (mesh == null) return;
            meshes.Add(mesh);
            worlds.Add(world);
            dirty = true;
        }

        public void Clear()
        {
            meshes.Clear();
            worlds.Clear();
            m_hasCutoffCasters = false;
            dirty = true;
        }

        /// <summary>扫描场景，把会投影的静态 MeshRenderer 全收进来。
        /// cluster 路径覆盖的是【整个场景】，SRP 路径也是，这样两条路才可比。</summary>
        void ScanScene()
        {
            m_hasCutoffCasters = false;
            m_scanTotal = m_scanSkipDisabled = m_scanSkipOff = m_scanSkipCutout = m_scanSkipNoMesh = 0;
            var mrs = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            m_scanTotal = mrs.Length;
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null) { m_scanSkipDisabled++; continue; }
                // 跳过 disabled 的 renderer：LODGroup 会把「非当前级」的 renderer 置为 disabled，
                // 全部登记会让多个 LOD 同时投影（重影 + 几何翻倍）。这里保持「只烘当前级」的老行为，
                // 但把数量记下来 —— 如果这个数很大，说明场景里 LODGroup 很多，
                // 阴影用的是「扫描那一刻恰好在看的 LOD」，远处会偏粗（不是缺块，但会不一致）。
                if (!mr.enabled) { m_scanSkipDisabled++; continue; }
                if (mr.shadowCastingMode == ShadowCastingMode.Off) { m_scanSkipOff++; continue; }
                // 需 alpha 镂空的物体（草/蕨/灌木）走 SRP 阴影路径：Cluster 纯几何 shader 不采样贴图，
                // 进 Cluster 会画成实心方块 + 巨量簇（浪费且形状不对），故跳过它们、不烘焙进 Cluster，
                // 并记下"有 cutout 投射物"供 Shadows.cs 单独用 SRP 路径画
                // （用物体自身材质投阴影即可；几何体本身即外壳卡片，无需额外镂空处理）。
                var co = mr.GetComponent<ClusterObject>();
                if (co != null && co.UsesCutoff()) { m_hasCutoffCasters = true; m_scanSkipCutout++; continue; }
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) { m_scanSkipNoMesh++; continue; }
                Register(mf.sharedMesh, mr.transform.localToWorldMatrix);
            }
            dirty = true;
        }

        /// <summary>给所有用到的 mesh 补烘焙。走全局 ClusterMeshCache 的【合并切簇】那一份
        /// （splitBySubmesh: false）：阴影是纯几何、不采材质，合并切簇可以去掉「每个 submesh
        /// 最后一簇都要补齐到 128 三角形」产生的填充簇，簇数少 20~40%，也不生成 normal/tangent/uv。</summary>
        public void EnsureBaked()
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                var m = meshes[i];
                if (m == null || baked.ContainsKey(m)) continue;
                baked[m] = ClusterMeshCache.Get(m, splitBySubmesh: false);
            }
        }

        // ---------------- 摊平成世界空间的一份大数组 ----------------

        public void Rebuild()
        {
            if (!dirty) return;
            // 没有外部登记过 ⇒ 直接扫描场景里所有【启用 + 会投影】的 MeshRenderer。
            if (meshes.Count == 0) ScanScene();
            EnsureBaked();
            Release();

            var allPos = new List<Vector3>();
            var allIdx = new List<int>();
            var allBounds = new List<Vector4>();

            for (int e = 0; e < meshes.Count; e++)
            {
                var asset = baked[meshes[e]];
                if (asset == null) continue;
                var m = worlds[e];

                var lp = asset.positions;
                var li = asset.indices;
                var lb = asset.clusterBounds;
                for (int c = 0; c < asset.clusterCount; c++)
                {
                    Vector4 meta = asset.clusterMeta[c];
                    int vStart = (int)meta.x, iStart = (int)meta.y;
                    int lod = (int)meta.z, triCount = (int)meta.w;

                    // 只画 LOD0。4 级 LOD 是同一表面的 4 份几何，叠在一起会 z-fighting
                    // 且剪影被粗 LOD 顶出来。将来做"级联 k → LOD k"时再放开这里。
                    if (lod != 0) continue;

                    int vBase = allPos.Count;
                    // 该簇的顶点数：索引里最大下标 +1（簇的顶点是连续排的）
                    int vCount = 0;
                    for (int k = 0; k < triCount * 3; k++)
                    {
                        int vi = li[iStart + k] - vStart;
                        if (vi + 1 > vCount) vCount = vi + 1;
                    }

                    Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int v = 0; v < vCount; v++)
                    {
                        var wp = m.MultiplyPoint3x4(lp[vStart + v]);
                        allPos.Add(wp);
                        bmin = Vector3.Min(bmin, wp);
                        bmax = Vector3.Max(bmax, wp);
                    }

                    // ⚠️ 索引里写【新数组的全局顶点号】（vBase + 簇内编号）。
                    //    历史上 shader 又额外加了一次簇顶点基址 ⇒ 双重偏移、必然越界。
                    // ⚠️ 镜像实例（负缩放，det < 0）会把三角形绕序整体翻掉；阴影 shader 是 Cull Back，
                    //    不翻绕序的话这类物体的阴影会**整块消失**（Cull Off 时代完全看不出来）。
                    //    烘焙期交换两个角点即可，运行时零成本。
                    bool flipWinding = m.determinant < 0f;
                    for (int t = 0; t < triCount * 3; t += 3)
                    {
                        int i0 = vBase + (li[iStart + t + 0] - vStart);
                        int i1 = vBase + (li[iStart + t + 1] - vStart);
                        int i2 = vBase + (li[iStart + t + 2] - vStart);
                        allIdx.Add(i0);
                        allIdx.Add(flipWinding ? i2 : i1);
                        allIdx.Add(flipWinding ? i1 : i2);
                    }

                    // 包围球：世界空间（剔除在 world space 判，必须是世界空间）
                    Vector3 ctr = (bmin + bmax) * 0.5f;
                    float rad = (bmax - bmin).magnitude * 0.5f + 1e-3f;
                    allBounds.Add(new Vector4(ctr.x, ctr.y, ctr.z, rad));
                }
            }

            clusterCount = allIdx.Count / VertsPerCluster;
            // 全部簇的整体包围球：靠它反推每个级联需要多厚的 z 范围。
            {
                Vector3 sMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 sMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < allBounds.Count; i++)
                {
                    Vector4 b = allBounds[i];
                    Vector3 ext = new Vector3(b.w, b.w, b.w);
                    sMin = Vector3.Min(sMin, new Vector3(b.x, b.y, b.z) - ext);
                    sMax = Vector3.Max(sMax, new Vector3(b.x, b.y, b.z) + ext);
                }
                m_sceneCenter = (sMin + sMax) * 0.5f;
                m_sceneRadius = (sMax - sMin).magnitude * 0.5f;
            }
            if (clusterCount == 0)
            {
                Debug.LogWarning("[Cluster阴影] 场景里没找到任何可投影的 MeshRenderer，" +
                                 "cluster 阴影路径不会画任何东西。");
                dirty = false; built = false; return;
            }

            posBuf = new ComputeBuffer(allPos.Count, 12, ComputeBufferType.Default);
            idxBuf = new ComputeBuffer(allIdx.Count, 4, ComputeBufferType.Default);
            boundsBuf = new ComputeBuffer(clusterCount, 16, ComputeBufferType.Default);
            var fullPos = allPos.ToArray();
            var fullIdx = allIdx.ToArray();
            posBuf.SetData(fullPos);
            idxBuf.SetData(fullIdx);
            boundsBuf.SetData(allBounds.ToArray());

            // 剔除用的 buffer 大小都按"级联数 × 簇数"来，宁可多 alloc 也不要重分配
            int maxC = Mathf.Max(1, MaxCascades);

            // 真索引缓冲：每级联一段（起点 = 级联 × 簇数 × 384），由 compute 每帧只给【可见】簇写。
            // Target.Index | Target.Raw：D3D11 不允许索引缓冲同时是 Structured，但允许同时是 Raw
            // ⇒ compute 用 RWByteAddressBuffer 写、draw 直接当索引读，单缓冲零拷贝。
            drawIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw,
                maxC * clusterCount * VertsPerCluster, 4);

            // [级联 × 簇] = 该簇在本级联的可见槽位（不可见 = 0xFFFFFFFF），BuildIndices 靠它早退。
            slotBuf = new ComputeBuffer(maxC * clusterCount, 4, ComputeBufferType.Default);
            planesBuf = new ComputeBuffer(maxC * PlanesPerCascade, 16, ComputeBufferType.Default);
            visibleBuf = new ComputeBuffer(maxC * clusterCount, 4, ComputeBufferType.Default);
            counterBuf = new ComputeBuffer(maxC, 4, ComputeBufferType.Default);
            // ⚠️ 必须是 IndirectArguments 类型：DrawProceduralIndirect 要求参数 buffer 创建时带这个 flag，
            // 否则运行时会报 "GraphicsBuffer argument is missing IndirectArguments flag"。
            // （它仍可同时作为 RWStructuredBuffer 在 compute 里读写，BuildArgs kernel 就写它。）
            // 索引绘制 args = 5 个 uint：indexCount / instanceCount / startIndex / baseVertex / startInstance。
            argsBuf = new ComputeBuffer(maxC * 5, 4, ComputeBufferType.IndirectArguments);

            Load();
            built = true;
            dirty = false;

#if UNITY_EDITOR
            // 排查/对比用：阴影 pass 的几何量就是「簇数 × 每簇顶点」。
            // 和 SRP 对比时看 Profiler 里 Shadows 的顶点数，这两个数直接决定光栅化开销。
            // 扫描统计是「阴影缺块」的第一现场：如果 m_scanSkip* 里有大批物体，缺的就是它们的阴影。
            Debug.Log($"[Cluster阴影] 重建：{clusterCount} 簇 / {allPos.Count} 顶点 / {allIdx.Count} 索引" +
                      $"/ {clusterCount * TrisPerCluster} 三角形（合并切簇，仅 LOD0，Cull Back）\n" +
                      $"           扫描 {m_scanTotal} 个 MeshRenderer → 登记 {meshes.Count} 个 mesh" +
                      $"（跳过：disabled {m_scanSkipDisabled} / 关投影 {m_scanSkipOff}" +
                      $" / cutout {m_scanSkipCutout} / 无 Mesh {m_scanSkipNoMesh}）");
#endif
        }

        void Load()
        {
            if (cullShader == null)
            {
                cullShader = Resources.Load<ComputeShader>(CullShaderName);
                if (cullShader == null)
                    Debug.LogError($"[Cluster阴影] 找不到剔除 compute：Resources/{CullShaderName}");
            }
            if (drawMat == null)
            {
                // 优先 Resources.Load（shader 就在 Resources/Cluster/ 下，最稳）；
                // 找不到再退回 Shader.Find（Editor 下能搜到未进包名的 shader）。
                var sh = Resources.Load<Shader>(DrawShaderName) ?? Shader.Find("Hidden/ClusterShadowCaster");
                if (sh == null)
                    Debug.LogError($"[Cluster阴影] 找不到 shader：Resources/{DrawShaderName} 或 Hidden/ClusterShadowCaster");
                else
                    drawMat = new Material(sh) { name = "ClusterShadowCaster" };
            }
            // 每次 Rebuild 都会 Release 旧 buffer，drawMat 若留着已释放 buffer 的引用，画出去就是未定义行为
            // （典型症状：什么都不画）。故重新绑定。三者皆为稳定 buffer（仅 GPU 内容每帧变，对象不变）。
            // 绘制侧只需要 _Positions：索引由 compute 写成真索引缓冲，VS 里不再查 _Visible / _Indices。
            if (drawMat != null) drawMat.SetBuffer("_Positions", posBuf);
        }

        // ---------------- 每帧：登记级联 → 剔除 → 逐级联画 ----------------

        /// <summary>一个光源开始登记它的级联（方向光通常 4 个）。</summary>
        public void BeginLight()
        {
            cascadeCount = 0;
        }

        /// <summary>
        /// 登记一个级联：只做 CPU 计算（6 个世界空间视锥平面），不往 CommandBuffer 塞任何东西。
        /// </summary>
        /// <param name="worldToClip">该级联的世界→裁剪矩阵，= GL.GetGPUProjectionMatrix(proj,true)*view，
        ///   与绘制侧逐位一致。用它的 6 个平面做簇包围球的球-平面剔除（口径与渲染完全相同，不漏不重）。</param>
        public void AddCascade(Matrix4x4 worldToClip)
        {
            if (!built || cascadeCount >= MaxCascades) return;
            int k = cascadeCount++;

            // 视锥平面：用与绘制侧完全一致的 worldToClip 提 6 个世界空间平面。
            // 关键：必须用 GL.GetGPUProjectionMatrix(proj,true)*view（即渲染时实际用的矩阵），
            // 不能用裸 proj*view —— 后者在 D3D reversed-Z 下近/远平面算反 ⇒ 过剔除。
            var planes = GeometryUtility.CalculateFrustumPlanes(worldToClip);
            for (int p = 0; p < PlanesPerCascade; p++)
            {
                var pl = planes[p];
                cpuPlanes[k * PlanesPerCascade + p] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }

        }

        /// <summary>
        /// 一次 GPU 剔除 + 索引重排，产出每个级联的可见簇槽位(_SlotOf)、索引缓冲(_DrawIdx)、args(_Args)。
        /// 必须在【所有级联都 AddCascade 完】之后、任何 draw 之前调用。
        /// 整帧只有 4 个 dispatch（Reset / CullAll / BuildIndices / BuildArgs），与级联数无关。
        /// </summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!built || cascadeCount == 0) return;
            if (cullShader == null) return;

            // debugForceAll 非 0 ⇒ compute 跳过剔除、强制全可见（隔离测试用，全程不另走 CPU 旁路）。
            int dbg = (debugForceAll == 0) ? 0 : 1;

            planesBuf.SetData(cpuPlanes);

            int groups = Mathf.CeilToInt(clusterCount / (float)ThreadGroupSize);
            int cGroups = Mathf.Max(1, Mathf.CeilToInt(cascadeCount / 8f));

            // buffer 绑定按 kernel 分开设：SetBuffer(kernelIndex, name, buf) 只对该 kernel 生效。
            // _Counter / _Args / _Visible 被 Reset / CullAll / BuildArgs 共用 ⇒ 三个都要绑。
            int kReset = cullShader.FindKernel("Reset");
            int kCull = cullShader.FindKernel("CullAll");
            int kIdx = cullShader.FindKernel("BuildIndices");
            int kArgs = cullShader.FindKernel("BuildArgs");

            foreach (int k in new[] { kReset, kCull, kIdx, kArgs })
            {
                cullShader.SetInt("_CascadeCount", cascadeCount);
                cullShader.SetInt("_ClusterCount", clusterCount);
                cullShader.SetInt("_TrisPerCluster", TrisPerCluster);
                cullShader.SetInt("_DebugForceAll", dbg);

                cullShader.SetBuffer(k, "_Counter", counterBuf);
                cullShader.SetBuffer(k, "_Args", argsBuf);
                cullShader.SetBuffer(k, "_Visible", visibleBuf);
                cullShader.SetBuffer(k, "_Planes", planesBuf);
                cullShader.SetBuffer(k, "_ClusterBounds", boundsBuf);
                cullShader.SetBuffer(k, "_SlotOf", slotBuf);
            }
            // 只有 BuildIndices 用到这两份
            cullShader.SetBuffer(kIdx, "_CornerGvid", idxBuf);
            cullShader.SetBuffer(kIdx, "_DrawIdx", drawIdxBuf);

            cmd.DispatchCompute(cullShader, kReset, 1, 1, 1);
            cmd.DispatchCompute(cullShader, kCull, groups, 1, 1);

            // 索引重排：总簇数 × 384 个角点，每线程一个，内部循环所有级联
            // （_CornerGvid 对 4 个级联是同一个值，只读一次）。
            // ⚠️ D3D11 单维最多 65535 个线程组 ⇒ 二维拆开，kernel 用 _IdxDimX 拼回一维。
            int totalGroups = Mathf.Max(1,
                Mathf.CeilToInt(clusterCount * (float)VertsPerCluster / ThreadGroupSize));
            int gx = Mathf.Min(65535, totalGroups);
            int gy = Mathf.Max(1, Mathf.CeilToInt(totalGroups / (float)gx));
            cullShader.SetInt("_IdxDimX", gx * ThreadGroupSize);
            cmd.DispatchCompute(cullShader, kIdx, gx, gy, 1);

            cmd.DispatchCompute(cullShader, kArgs, cGroups, 1, 1);
        }

        /// <summary>画一个级联：索引缓冲由 compute 每帧按【可见】簇重排，索引值就是全局顶点号
        /// ⇒ VS 只有一次 _Positions[SV_VertexID] 顺序读取。
        /// 经 MaterialPropertyBlock 逐 draw 传 _ClusterShadowW2C（自定义 HLSL 拿不到内建矩阵）。
        /// argsBuf[cascadeIndex*5] 的 startIndexLocation = 该级联在索引缓冲里的起点。</summary>
        public void Draw(CommandBuffer cmd, int cascadeIndex, Matrix4x4 worldToClip)
        {
            if (!built || drawMat == null || cullShader == null) return;
            if (cascadeIndex < 0 || cascadeIndex >= cascadeCount) return;
            if (drawIdxBuf == null) return;

            // 索引绘制：CommandBuffer.DrawProceduralIndirect 的【索引重载】——
            //   indexBuffer = drawIdxBuf（compute 已按可见簇铺好全局顶点号），
            //   bufferWithArgs = argsBuf（BuildArgs 写好 5 个 uint），instanceCount = 1。
            //   args 偏移 = 每级联 5 个 uint（20 字节）。
            // world→clip 不走 DrawProceduralIndirect 的 worldPose：自定义 HLSLPROGRAM 下它不喂内建矩阵
            //（恒为单位矩阵 ⇒ 全裁光），改为逐 draw 经 MaterialPropertyBlock 传自定义 _ClusterShadowW2C。
            if (m_mpb == null) m_mpb = new MaterialPropertyBlock();
            m_mpb.SetMatrix("_ClusterShadowW2C", worldToClip);
            cmd.DrawProceduralIndirect(drawIdxBuf, Matrix4x4.identity, drawMat, 0,
                MeshTopology.Triangles, argsBuf, sizeof(uint) * 5 * cascadeIndex, m_mpb);
        }

        // ---------------- 释放 ----------------

        public void Release()
        {
            posBuf?.Release(); idxBuf?.Release(); boundsBuf?.Release();
            planesBuf?.Release();
            visibleBuf?.Release(); counterBuf?.Release(); argsBuf?.Release();
            drawIdxBuf?.Release(); drawIdxBuf = null;
            slotBuf?.Release(); slotBuf = null;
            posBuf = idxBuf = boundsBuf = null;
            planesBuf = visibleBuf = counterBuf = argsBuf = null;
            built = false;
        }
    }
}
