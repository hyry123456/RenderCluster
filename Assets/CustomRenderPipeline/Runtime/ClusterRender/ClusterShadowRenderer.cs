using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// 【真正的 cluster 管线】阴影渲染器（资产开关切换：SRP / Cluster / ClusterDebug）。
    ///
    /// ── 2026-10-21 修订 ──
    /// 1) 阴影矩阵：不再自己推导。用 Shadows.cs 里已有的 SetViewProjectionMatrices
    ///    设好的 unity_MatrixVP —— 与 SRP 路径 ShadowCaster pass 的
    ///    TransformWorldToHClip 逐位相同 ⇒ 深度值/方向/裁剪全部一致。
    ///    （之前手推 z 行改写是错的，见 shader 顶部注释。）
    /// 2) 恢复 GPU 剔除：视锥剔除在 compute 里做，级联各剔一次；
    ///    每级联一次 DrawProceduralIndirect，只提交该级联真正可见的簇。
    /// 3) LOD：当前只画 LOD0（4 级 LOD 是同一表面的 4 份几何，同图叠加会 z-fighting）。
    ///    LOD 判据的代码位置保留，由 targetTexels <= 0 关闭。
    ///
    /// 数据布局（摊平到世界空间，静态场景，运行时不乘实例矩阵）：
    ///   _Positions —— float3，世界空间顶点
    ///   _Indices  —— int32，【全局顶点号】。每簇固定 384 个（= 128 三角形 × 3），
    ///                簇 c 的索引区间 = [c * 384, c * 384 + 384)。
    ///                ⇒ 顶点拉取：vi = _Indices[c*384 + vertexID]; p = _Positions[vi];
    ///   _Visible  —— uint，compute 写出的可见簇列表
    ///
    /// 纯几何链路：不采任何贴图。
    /// </summary>
    public class ClusterShadowRenderer
    {
        public const int MaxCascades = 8;          // 方向光级联上限（Unity 最多 4，留余量）

        /// <summary>
        /// 每簇三角形数。改这个值必须同步改 shader 里的 VERTS_PER_CLUSTER
        /// （= 本值 × 3），否则索引区间会错位。
        /// </summary>
        public const int TrisPerCluster = 128;

        /// <summary>每簇顶点数 = TrisPerCluster × 3。shader 里的 VERTS_PER_CLUSTER 必须一致！</summary>
        public const int VertsPerCluster = TrisPerCluster * 3;

        const int PlanesPerCascade = 8;          // 只用 6 个，留余量
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

        // ---------------- GPU 资源 ----------------
        Material drawMat;
        ComputeShader cullShader;
        ComputeBuffer posBuf, idxBuf, boundsBuf;
        ComputeBuffer planesBuf, cascadeParamBuf, visibleBuf, counterBuf, argsBuf;

        int clusterCount;
        bool built, dirty = true;
        /// <summary>本帧 ScanScene 是否跳过了「需 alpha 镂空」的投射物（草/蕨/灌木）。
        /// 有则 Shadows.cs 需用 SRP 路径单独画它们的阴影（Cluster 纯几何 shader 无法镂空）。</summary>
        bool m_hasCutoffCasters = false;

        // ---------------- 每帧级联数据 ----------------
        int cascadeCount;
        readonly Vector4[] cpuPlanes = new Vector4[MaxCascades * PlanesPerCascade];
		readonly Vector4[] cpuCascadeParams = new Vector4[MaxCascades];
		readonly int[] cpuPlaneCount = new int[MaxCascades];
		/// <summary>世界空间包围球（xyz=中心 w=半径），按簇下标排布，供 Debug 模式 CPU 剔除比对用。</summary>
		Vector4[] cpuBounds;
		/// <summary>每帧级联的真实绘制矩阵 proj*view（与 Draw 用的 SetViewProjectionMatrices 同源），
		/// 供 Debug 模式做"包围盒投进 NDC 立方"的地面真值交叉检验。</summary>
		readonly Matrix4x4[] cpuProjView = new Matrix4x4[MaxCascades];
		/// <summary>复用的 GPU 可见集合回读缓冲（避免每帧分配）。</summary>
		uint[] visibleReadback;

        /// <summary>LOD 判据的目标 texel 数。<= 0 = 不做 LOD 判据（当前只有 LOD0）。</summary>
        public float targetTexels = 0f;

        /// <summary>调试探头：非 0 时 compute 跳过所有剔除，全画。</summary>
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
            var mrs = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null || !mr.enabled) continue;
                if (mr.shadowCastingMode == ShadowCastingMode.Off) continue;
                // 需 alpha 镂空的物体（草/蕨/灌木）走 SRP 阴影路径：Cluster 纯几何 shader 不采样贴图，
                // 进 Cluster 会画成实心方块 + 巨量簇（浪费且形状不对），故跳过它们、不烘焙进 Cluster，
                // 并记下"有 cutout 投射物"供 Shadows.cs 单独用 SRP 路径画
                // （用物体自身材质投阴影即可；几何体本身即外壳卡片，无需额外镂空处理）。
                var co = mr.GetComponent<ClusterObject>();
                if (co != null && co.UsesCutoff()) { m_hasCutoffCasters = true; continue; }
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Register(mf.sharedMesh, mr.transform.localToWorldMatrix);
            }
            dirty = true;
        }

        /// <summary>给所有用到的 mesh 补烘焙（没有 ClusterMeshAsset 的就现场烘一份，只烘一次）。</summary>
        public void EnsureBaked()
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                var m = meshes[i];
                if (m == null || baked.ContainsKey(m)) continue;
                var asset = ScriptableObject.CreateInstance<ClusterMeshAsset>();
                asset.sourceMesh = m;
                asset.clusterTriCount = TrisPerCluster;
                asset.lodCount = 1;          // 只烘 LOD0：当前阴影只画 LOD0
                ClusterMeshBaker.Bake(asset);
                baked[m] = asset;
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
                    for (int k = 0; k < triCount * 3; k++)
                        allIdx.Add(vBase + (li[iStart + k] - vStart));

                    // 包围球：世界空间（剔除在 world space 判，必须是世界空间）
                    Vector3 ctr = (bmin + bmax) * 0.5f;
                    float rad = (bmax - bmin).magnitude * 0.5f + 1e-3f;
                    allBounds.Add(new Vector4(ctr.x, ctr.y, ctr.z, rad));
                }
            }

            clusterCount = allIdx.Count / VertsPerCluster;
            if (clusterCount == 0)
            {
                Debug.LogWarning("[Cluster阴影] 场景里没找到任何可投影的 MeshRenderer，" +
                                 "cluster 阴影路径不会画任何东西。");
                dirty = false; built = false; return;
            }

            posBuf = new ComputeBuffer(allPos.Count, 12, ComputeBufferType.Default);
            idxBuf = new ComputeBuffer(allIdx.Count, 4, ComputeBufferType.Default);
            boundsBuf = new ComputeBuffer(clusterCount, 16, ComputeBufferType.Default);
            posBuf.SetData(allPos.ToArray());
            idxBuf.SetData(allIdx.ToArray());
            boundsBuf.SetData(allBounds.ToArray());
            cpuBounds = allBounds.ToArray();   // 缓存世界空间包围球，供 Debug CPU 剔除比对

            // 剔除用的 buffer 大小都按"级联数 × 簇数"来，宁可多 alloc 也不要重分配
            int maxC = Mathf.Max(1, MaxCascades);
            planesBuf = new ComputeBuffer(maxC * PlanesPerCascade, 16, ComputeBufferType.Default);
            cascadeParamBuf = new ComputeBuffer(maxC, 16, ComputeBufferType.Default);
            visibleBuf = new ComputeBuffer(maxC * clusterCount, 4, ComputeBufferType.Default);
            counterBuf = new ComputeBuffer(maxC, 4, ComputeBufferType.Default);
            // ⚠️ 必须是 IndirectArguments 类型：DrawProceduralIndirect 要求参数 buffer 创建时带这个 flag，
            // 否则运行时会报 "GraphicsBuffer argument is missing IndirectArguments flag"。
            // （它仍可同时作为 RWStructuredBuffer 在 compute 里读写，BuildArgs kernel 就写它。）
            argsBuf = new ComputeBuffer(maxC * 4, 4, ComputeBufferType.IndirectArguments);

            Load();
            built = true;
            dirty = false;

            Debug.Log($"[Cluster阴影] 重建完成：{clusterCount} 簇 / {allPos.Count} 顶点 / " +
                      $"{allIdx.Count / 3} 三角形（仅 LOD0）");
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
            // 每次都重新绑：Rebuild 开头会 Release 旧 buffer，
            // drawMat 若留着已释放 buffer 的引用，画出去就是未定义行为（典型症状：什么都不画）。
            if (drawMat != null)
            {
                drawMat.SetBuffer("_Positions", posBuf);
                drawMat.SetBuffer("_Indices", idxBuf);
                drawMat.SetBuffer("_Visible", visibleBuf);
            }
        }

        // ---------------- 每帧：登记级联 → 剔除 → 逐级联画 ----------------

        /// <summary>一个光源开始登记它的级联（方向光通常 4 个）。</summary>
        public void BeginLight()
        {
            cascadeCount = 0;
        }

        /// <summary>
        /// 登记一个级联。只做 CPU 计算（世界空间平面 + LOD 参数），不往 CommandBuffer 塞东西。
        /// </summary>
        /// <param name="projView">引擎给的 projectionMatrix * viewMatrix。</param>
        /// <param name="proj">引擎给的 projectionMatrix（取 projScale 用）。</param>
        /// <param name="tileSize">该级联在图集里占多少像素（算 projScale 用）。</param>
        public void AddCascade(Matrix4x4 projView, Matrix4x4 proj, float tileSize)
        {
            if (!built || cascadeCount >= MaxCascades) return;
            int k = cascadeCount++;

            // 世界空间视锥平面（官方文档保证 CalculateFrustumPlanes 返回世界空间）。
            // 这就是历史上"剔完恒为 0"的坑：之前用的是 GetCullingPlane，空间不明确。
            var planes = GeometryUtility.CalculateFrustumPlanes(projView);
            int n = Mathf.Min(planes.Length, PlanesPerCascade);
            for (int i = 0; i < PlanesPerCascade; i++)
            {
                if (i < n)
                {
                    var pl = planes[i];
                    cpuPlanes[k * PlanesPerCascade + i] =
                        new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
                }
                else cpuPlanes[k * PlanesPerCascade + i] = Vector4.zero;
            }
            cpuPlaneCount[k] = n;

            // projScale：把世界半径换算成阴影图 texel 半径（LOD 判据用）。
            // 正交投影下 proj[1][1] = 2/(top-bottom)，世界半径 r 对应 NDC y = r*proj11，
            // 再乘 tileSize/2 得 texel。
            float projScale = 0f;
            if (Mathf.Abs(proj.m11) > 1e-6f) projScale = tileSize * 0.5f * Mathf.Abs(proj.m11);
            cpuCascadeParams[k] = new Vector4(projScale, targetTexels, n, 0f);
            cpuProjView[k] = projView;   // 真实绘制矩阵，Debug 地面真值检验用
        }

        /// <summary>
        /// 一次 GPU 剔除，产出每个级联的可见簇列表 + indirect args。
        /// 必须在【所有级联都 AddCascade 完】之后、任何 draw 之前调用。
        /// 整帧只有 3 个 dispatch（Reset / CullAll / BuildArgs）。
        /// </summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!built || cullShader == null || cascadeCount == 0) return;

            planesBuf.SetData(cpuPlanes);
            cascadeParamBuf.SetData(cpuCascadeParams);

            int groups = Mathf.CeilToInt(clusterCount / (float)ThreadGroupSize);
            int cGroups = Mathf.Max(1, Mathf.CeilToInt(cascadeCount / 8f));

            // ⚠️ buffer 绑定按 kernel 分开设：ComputeShader.SetBuffer(kernelIndex, name, buf)
            //    只对当前 kernel 生效。
            //    _Counter 被 Reset / CullAll / BuildArgs 三个 kernel 用 ⇒ 三个都要绑。
            int kReset = cullShader.FindKernel("Reset");
            int kCull = cullShader.FindKernel("CullAll");
            int kArgs = cullShader.FindKernel("BuildArgs");

            foreach (int k in new[] { kReset, kCull, kArgs })
            {
                cullShader.SetInt("_CascadeCount", cascadeCount);
                cullShader.SetInt("_ClusterCount", clusterCount);
                cullShader.SetInt("_PlanesPerCascade", PlanesPerCascade);
                cullShader.SetInt("_MaxLod", 1);          // 只画 LOD0
                cullShader.SetInt("_TrisPerCluster", TrisPerCluster);
                cullShader.SetInt("_DebugForceAll", debugForceAll);

                cullShader.SetBuffer(k, "_Counter", counterBuf);
                cullShader.SetBuffer(k, "_Args", argsBuf);
                cullShader.SetBuffer(k, "_Visible", visibleBuf);
                cullShader.SetBuffer(k, "_Planes", planesBuf);
                cullShader.SetBuffer(k, "_CascadeParams", cascadeParamBuf);
                cullShader.SetBuffer(k, "_ClusterBounds", boundsBuf);
            }

            cmd.DispatchCompute(cullShader, kReset, 1, 1, 1);
            cmd.DispatchCompute(cullShader, kCull, groups, 1, 1);
            cmd.DispatchCompute(cullShader, kArgs, cGroups, 1, 1);
        }

        /// <summary>画一个级联：一次间接绘制，只画该级联真正可见的簇。</summary>
        public void Draw(CommandBuffer cmd, int cascadeIndex)
        {
            if (!built || drawMat == null) return;
            if (cascadeIndex < 0 || cascadeIndex >= cascadeCount) return;

            // ⚠️ 必须用全局属性：材质属性在命令执行时才读，四个级联的 draw 会互相覆盖。
            // 基址 = 该级联在可见列表里的起始段。
            cmd.SetGlobalFloat("_ClusterBase", (float)(cascadeIndex * clusterCount));

            cmd.DrawProceduralIndirect(Matrix4x4.identity, drawMat, 0, MeshTopology.Triangles,
                argsBuf, sizeof(uint) * 4 * cascadeIndex);
        }

        // ---------------- 统计（ClusterDebug 模式） ----------------

        static int s_lastStatFrame = -999;
        static bool s_cleanLogged = false;

        /// <summary>
        /// ClusterDebug 模式：CPU 用与 compute 逐位相同的球体视锥口径重算每级联可见集合，
        /// 再回读 GPU 真正提交的 _Visible 做逐簇集合比对，直接回答"有没有真·遗漏"。
        ///
        /// · 漏剔 = CPU 明确在视锥内的簇、却被 GPU 漏画 ⇒ 真·错误剔除。
        /// · 多画 = GPU 画了、却明确在视锥外的簇 ⇒ _Visible 写入 / buffer bug。
        /// 另：clusterShadowForceAll=1 时两边都不判 ⇒ cpuSet=gpuSet=全部簇，应完全吻合，
        /// 可快速验证 compute 写入链本身是否正常。
        ///
        /// ⚠️ 边界容差：C# 与 HLSL 在视锥平面附近的浮点评估有 ~mm 级差异，会让贴边簇在两边
        /// 翻面（不是真错误；包围球保守，球在视锥外才剔，绝不会把视锥内几何剔掉）。
        /// 只有"明确在内却被漏画 / 明确在外却被画了"才上报，过滤掉边界噪声。
        /// ⚠️ 同步 GetData 会卡 GPU 流水线，限频到 1/120 帧，绝不要用它测性能。
        /// </summary>
        const float CullTolerance = 1e-2f;   // 视锥平面边界容差（米）。只上报距平面 > 此值的"明确"错，过滤浮点噪声。
        public void VerifyCulling()
        {
            if (!built || cascadeCount == 0 || cpuBounds == null) return;
            if (Time.frameCount - s_lastStatFrame < 120) return;
            s_lastStatFrame = Time.frameCount;

            int n = Mathf.Min(cascadeCount, MaxCascades);
            var counters = new uint[MaxCascades];
            counterBuf.GetData(counters, 0, 0, n);

            // 回读 GPU 实际写出的可见簇列表（_Visible[k*clusterCount + slot] = 簇号）
            int visCap = MaxCascades * Mathf.Max(1, clusterCount);
            if (visibleReadback == null || visibleReadback.Length < visCap)
                visibleReadback = new uint[visCap];
            visibleBuf.GetData(visibleReadback, 0, 0, visCap);

            var sb = new System.Text.StringBuilder();
            bool anyError = false;

            for (int k = 0; k < n; k++)
            {
                int pc = cpuPlaneCount[k];
                int baseIdx = k * PlanesPerCascade;

                // ---- CPU 用与 compute 完全相同的球体口径重算本级联可见集合 ----
                // 并区分"明确在内 / 明确在外 / 边界模糊"，只上报明确的错，过滤 C#/HLSL 浮点边界噪声。
                var cpuSet = new HashSet<int>();      // 明确在视锥内（应被画）
                var cpuCulled = new HashSet<int>();   // 明确在视锥外（不应被画）
                for (int c = 0; c < clusterCount; c++)
                {
                    Vector4 bb = cpuBounds[c];
                    Vector3 center = new Vector3(bb.x, bb.y, bb.z);
                    float radius = bb.w;
                    if (debugForceAll != 0) { cpuSet.Add(c); continue; }

                    float dmin = float.MaxValue;     // 距各平面最近的距离（最"危险"的那个平面）
                    for (int i = 0; i < pc; i++)
                    {
                        Vector4 pl = cpuPlanes[baseIdx + i];
                        float d = Vector3.Dot(new Vector3(pl.x, pl.y, pl.z), center) + pl.w;
                        if (d < dmin) dmin = d;
                    }
                    // 保守包围球：dmin < -radius 才"整球在视锥外"⇒ 剔除；否则球与视锥相交/在内 ⇒ 保留。
                    if (dmin < -radius - CullTolerance)
                        cpuCulled.Add(c);            // 明确在外
                    else if (dmin >= -radius + CullTolerance)
                        cpuSet.Add(c);               // 明确在内（±Tolerance 内的模糊簇两边都忽略）
                }

                // ---- GPU 实际可见集合（来自回读的 _Visible）----
                int cnt = (int)counters[k];
                if (cnt < 0) continue;               // 缓冲区尚未就绪（首帧/刚重建的脏读），跳过本次比对
                cnt = Mathf.Min(cnt, clusterCount);  // 防越界（脏读成超大正数时不崩）
                var gpuSet = new HashSet<int>();
                for (int s = 0; s < cnt; s++)
                    gpuSet.Add((int)visibleReadback[k * clusterCount + s]);

                // ---- 比对：只报"明确"的错 ----
                // 漏剔：明确在内、却被 GPU 漏画 ⇒ 真·错误剔除。
                // 多画：GPU 画了、却明确在外 ⇒ _Visible 写入 / buffer 内容错。
                // 边界模糊簇（dmin 落在 ±Tolerance 内）两边都忽略，避免浮点噪声误报。
                var missing = new List<int>();
                foreach (var c in cpuSet) if (!gpuSet.Contains(c)) missing.Add(c);
                var extra = new List<int>();
                foreach (var c in gpuSet) if (cpuCulled.Contains(c)) extra.Add(c);

                if (missing.Count > 0 || extra.Count > 0)
                {
                    anyError = true;
                    s_cleanLogged = false;
                    sb.Append($"\n[级联 {k}] CPU可见={cpuSet.Count} GPU可见={cnt}");
                    sb.Append($" | 漏剔(真遗漏)={missing.Count} 多画(buffer错)={extra.Count}");
                    if (missing.Count > 0)
                    {
                        sb.Append(" | 漏剔簇(世界坐标): ");
                        for (int i = 0; i < Mathf.Min(missing.Count, 6); i++)
                        {
                            Vector4 bb = cpuBounds[missing[i]];
                            sb.Append($"#{missing[i]}({bb.x:F0},{bb.y:F0},{bb.z:F0}) ");
                        }
                    }
                    if (extra.Count > 0)
                    {
                        sb.Append(" | 多画簇: ");
                        for (int i = 0; i < Mathf.Min(extra.Count, 6); i++)
                            sb.Append('#').Append(extra[i]).Append(' ');
                    }
                }
            }

            if (anyError)
            {
                Debug.LogWarning("[Cluster阴影][Debug] 发现裁剪集合不一致（真·遗漏或 buffer 内容错）：" + sb +
                    "\n  · 漏剔≠0 ⇒ 确有簇被错误剔除，按世界坐标去场景核对这些簇（多半是包围球/平面空间问题）；" +
                    "\n  · 多画≠0 ⇒ _Visible 写入的簇号与预期不符（compute 写入 / buffer 绑定 bug）。");
            }
            else if (debugForceAll == 0)
            {
                long sum = 0; foreach (var cc in counters) sum += cc;
                if (sum == 0)
                {
                    Debug.LogWarning("[Cluster阴影][Debug] 所有级联 GPU 可见簇数都是 0 ⇒ 剔除条件有问题（平面/包围球/矩阵），" +
                        "不是绘制链问题。可把 debugForceAll 临时设为 1 验证：强制全画后可见数=簇数即证明 compute 链正常。");
                }
                else if (!s_cleanLogged)
                {
                    s_cleanLogged = true;
                    Debug.Log("[Cluster阴影][Debug] 裁剪集合逐簇比对一致：零遗漏。" +
                        "若画面仍有阴影洞 ⇒ 问题在绘制/采样侧（顶点拉取·矩阵·图集tile·接收端 cascade 选择），与剔除无关。");
                }
            }
        }

        // ---------------- 释放 ----------------

        public void Release()
        {
            posBuf?.Release(); idxBuf?.Release(); boundsBuf?.Release();
            planesBuf?.Release(); cascadeParamBuf?.Release();
            visibleBuf?.Release(); counterBuf?.Release(); argsBuf?.Release();
            posBuf = idxBuf = boundsBuf = null;
            planesBuf = cascadeParamBuf = visibleBuf = counterBuf = argsBuf = null;
            built = false;
        }
    }
}