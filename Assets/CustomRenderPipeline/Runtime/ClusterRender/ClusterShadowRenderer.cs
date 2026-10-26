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
    ///    每级联一次 DrawProceduralIndirect（先 Gather 拼索引缓冲，再索引绘制），
    ///    只提交该级联真正可见的簇，且 GPU 后变换缓存按索引值去重顶点。
    /// 3) LOD：当前只画 LOD0（4 级 LOD 是同一表面的 4 份几何，同图叠加会 z-fighting）。
    ///    LOD 判据的代码位置保留，由 targetTexels <= 0 关闭。
    ///
    /// 数据布局（摊平到世界空间，静态场景，运行时不乘实例矩阵）：
    ///   _Positions —— float3，世界空间顶点（已按簇内唯一顶点去重，每簇仅存唯一顶点）
    ///   _Indices  —— int32，【全局顶点号】。每簇固定 384 个（= 128 三角形 × 3），
    ///                簇 c 的索引区间 = [c * 384, c * 384 + 384)，值 = 全局顶点号。
    ///   _Visible  —— uint，compute 写出的可见簇列表
    ///   _DrawIndices —— int32，【运行时】由 compute 的 Gather 核把某级联可见簇的
    ///                _Indices 拼接成一段连续索引缓冲，交给 DrawProceduralIndirect。
    ///                索引绘制下 GPU 后变换缓存按【索引值】做键，共享顶点只跑一次 VS。
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

        /// <summary>每簇索引数 = TrisPerCluster × 3。Gather 核与 VerifyCulling 用；shader 不再硬编码顶点数。</summary>
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
        MaterialPropertyBlock m_mpb;     // [绘制] 逐 draw 显式传入 world→clip 矩阵（per-draw，互不污染）
        ComputeShader cullShader;
        ComputeBuffer posBuf, idxBuf, boundsBuf;
        // 真正的索引缓冲：Index|Raw 两用。
        // · Raw  ⇒ compute 经 RWByteAddressBuffer 直接写入（Gather 核把每级联可见簇的全局顶点号拼进来）
        // · Index ⇒ DrawProceduralIndirect 直接当索引缓冲读，GPU 后变换缓存按索引值去重顶点
        // 一缓冲两用，彻底绕开「Raw→Index 的 CopyBuffer 跨 Target 拷贝在 2022.3 静默失败 / 不可验证」的坑。
        // 这是 Unity 官方 GPU culling 的标准写法：compute 写、indirect 读同一缓冲。
        GraphicsBuffer drawIdxBuf;
        ComputeBuffer gatherProbe;       // [诊断] 普通 ComputeBuffer 探针：Gather 写入的前若干索引值（GetData 可靠）
        ComputeBuffer planesBuf, cascadeParamBuf, visibleBuf, counterBuf, argsBuf;

        // 【隔离测试】"全画旁路"所用：Rebuild 时保留一份 CPU 侧全量索引，
        // 旁路模式下一次性上传 drawIdxBuf 偏移 0，args 也由 CPU 写 ⇒ 全程不派发任何 compute kernel。
        int[] allIdxArray;
        bool fullIdxUploaded;
        // [诊断] 位置探针：GPU 回读的世界坐标缓冲 + CPU 侧期望值副本
        Vector3[] allPosArray;
        ComputeBuffer posProbe;

        int clusterCount;
        int vertexCount;          // 诊断用：世界空间顶点总数，用于判断回读索引是否落在合理范围
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
            vertexCount = allPos.Count;          // 诊断判据：合法索引值应 < 此数
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
            allIdxArray = fullIdx;          // 旁路模式要用（全量索引，不跑 compute）
            allPosArray = fullPos;          // 位置探针的 CPU 期望值
            fullIdxUploaded = false;
            cpuBounds = allBounds.ToArray();   // 缓存世界空间包围球，供 Debug CPU 剔除比对

            // 索引缓冲链路（2026-10-24 修复：单缓冲 Index|Raw，compute 直写，去掉 CopyBuffer）：
            //   drawIdxBuf —— 唯一缓冲，Index|Raw 两用：
            //     · Raw  ⇒ compute 经 RWByteAddressBuffer 直接把每级联可见簇的全局顶点号拼进它；
            //     · Index ⇒ DrawProceduralIndirect 直接当索引缓冲读，GPU 后变换缓存按索引值去重顶点。
            //   彻底绕开「Raw→Index 的 CopyBuffer 跨 Target 拷贝在 2022.3 静默失败 / 且 Index 缓冲 GetData 不可信」的坑。
            //   这是 Unity 官方 GPU culling 的标准写法：compute 写、indirect 读同一缓冲，无中间拷贝、可验证。
            // 容量按"实际级联数 × 簇数 × 384 索引"（方向光最多 4 级联，这里按 4 预留；
            // 若运行期 cascadeCount 更大，Cull() 里的 needed 检查会懒重建）。
            int idxCap = 4 * allIdx.Count;
            drawIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw
                                            | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination,
                                           idxCap, 4);

            // 剔除用的 buffer 大小都按"级联数 × 簇数"来，宁可多 alloc 也不要重分配
            int maxC = Mathf.Max(1, MaxCascades);
            planesBuf = new ComputeBuffer(maxC * PlanesPerCascade, 16, ComputeBufferType.Default);
            cascadeParamBuf = new ComputeBuffer(maxC, 16, ComputeBufferType.Default);
            visibleBuf = new ComputeBuffer(maxC * clusterCount, 4, ComputeBufferType.Default);
            counterBuf = new ComputeBuffer(maxC, 4, ComputeBufferType.Default);
            // ⚠️ 必须是 IndirectArguments 类型：DrawProceduralIndirect 要求参数 buffer 创建时带这个 flag，
            // 否则运行时会报 "GraphicsBuffer argument is missing IndirectArguments flag"。
            // （它仍可同时作为 RWStructuredBuffer 在 compute 里读写，BuildArgs kernel 就写它。）
            // 索引绘制 args = 5 个 uint：indexCount / instanceCount / startIndex / baseVertex / startInstance。
            argsBuf = new ComputeBuffer(maxC * 5, 4, ComputeBufferType.IndirectArguments);
            gatherProbe = new ComputeBuffer(8, 4, ComputeBufferType.Default);   // [诊断] Gather 执行探针：[0]=perCluster [1..7]=写入前7索引
            posProbe = new ComputeBuffer(4, 16, ComputeBufferType.Default);      // [诊断] 位置探针：前4个索引对应的世界坐标

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
            // 索引绘制下 shader 只用 _Positions（SV_VertexID = 全局顶点号，来自索引缓冲），
            // 不再需要 _Indices / _Visible / _ClusterBase。
            if (drawMat != null)
            {
                drawMat.SetBuffer("_Positions", posBuf);
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
        /// [诊断] 派发 ProbePositions 内核：让 GPU 按前 4 个索引去 _Positions 读世界坐标。
        /// 目的是单独验证「索引值 → _Positions[stride?] → 世界坐标」这一环，
        /// 排除排除 _Positions 的 float3 步长 / 去重映射 / 缓冲绑定三类底层问题。
        /// </summary>
        void DispatchPositionProbe(CommandBuffer cmd)
        {
            if (cullShader == null || posProbe == null || idxBuf == null || posBuf == null) return;
            if (allIdxArray == null || allPosArray == null) return;

            int kProbe = cullShader.FindKernel("ProbePositions");
            if (kProbe < 0) return;

            cullShader.SetBuffer(kProbe, "_Indices", idxBuf);
            cullShader.SetBuffer(kProbe, "_Positions", posBuf);
            cullShader.SetBuffer(kProbe, "_PosProbe", posProbe);
            cmd.DispatchCompute(cullShader, kProbe, 1, 1, 1);
        }

        /// <summary>
        /// 【隔离测试：只验证"这几何到底能不能画出来"】完全绕开 compute。
        ///   · 不派发任何 kernel（Reset / CullAll / BuildArgs / Gather 全不跑）
        ///   · 索引缓冲用 Rebuild 时保留的【全量原始索引】，一次性上传到 drawIdxBuf 偏移 0
        ///   · indirect args 由 CPU 直接写：indexCount = 全量索引数，startIndex = 0，instanceCount = 1
        /// 判读：
        ///   有阴影 ⇒ 绘制链（shader / 矩阵 / 深度状态 / 索引语义）通畅，问题在 compute 剔除或 Gather；
        ///   无阴影 ⇒ 问题就在绘制链本身，与剔除无关（查矩阵·UNITY_MATRIX_M·ZWrite/ZTest·索引取值）。
        /// </summary>
        void CullBypass(CommandBuffer cmd)
        {
            if (allIdxArray == null || drawIdxBuf == null || argsBuf == null) return;

            int total = allIdxArray.Length;

            // 全量索引一次性上传到偏移 0（该位置正是"级联 0 段"的起点，语义自洽）。
            if (!fullIdxUploaded)
            {
                try
                {
                    drawIdxBuf.SetData(allIdxArray, 0, 0, total);
                    fullIdxUploaded = true;
                    Debug.LogWarning($"[Cluster阴影][旁路] SetData 成功：上传全量索引 {total} 个到 drawIdxBuf 偏移 0，" +
                        "不派发任何 compute kernel。此模式只用于验证绘制链本身是否通。");
                }
                catch (System.Exception e)
                {
                    // 上传失败必须被明确看到 —— 否则会被误判成"绘制链坏"，其实是数据没上去。
                    Debug.LogError($"[Cluster阴影][旁路] SetData 到 drawIdxBuf 失败 ⇒ 本次旁路测试无效：" + e.Message);
                    return;
                }
            }

            // 每个级联都画【全部】几何，各自用自己的 proj*view 落到自己的 tile。
            var ags = new uint[5 * cascadeCount];
            for (int k = 0; k < cascadeCount; k++)
            {
                ags[5 * k + 0] = (uint)total;   // indexCount
                ags[5 * k + 1] = 1;             // instanceCount
                ags[5 * k + 2] = 0;             // startIndexLocation
                ags[5 * k + 3] = 0;             // baseVertexLocation
                ags[5 * k + 4] = 0;             // startInstanceLocation
            }
            try
            {
                argsBuf.SetData(ags, 0, 0, ags.Length);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Cluster阴影][旁路] SetData 到 argsBuf 失败 ⇒ 本次旁路测试无效：" + e.Message);
            }
        }

        /// <summary>
        /// 一次 GPU 剔除，产出每个级联的可见簇列表 + indirect args。
        /// 必须在【所有级联都 AddCascade 完】之后、任何 draw 之前调用。
        /// 整帧只有 3 个 dispatch（Reset / CullAll / BuildArgs）。
        /// </summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!built || cascadeCount == 0) return;

            // [诊断] 位置探针：正常模式与旁路模式都要跑 —— 它验证的是全链路最底层的一环
            //「按索引值去 _Positions 取世界坐标」，与剔除 / Gather / SV_VertexID 语义都无关。
            DispatchPositionProbe(cmd);

            // ⚠️ forceAll 现在是【真正旁路】：一个 compute kernel 都不派发。
            // 之前的 forceAll 只是跳过平面判定，四个 kernel 照跑 ⇒ "能不能画"始终没被单独隔离出来。
            if (debugForceAll != 0) { CullBypass(cmd); return; }

            if (cullShader == null) return;

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
            int kGather = cullShader.FindKernel("Gather");

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
                cullShader.SetBuffer(k, "_GatherProbe", gatherProbe);   // [诊断] 探针（Reset 清零 / Gather 置 1）
            }

            cmd.DispatchCompute(cullShader, kReset, 1, 1, 1);
            cmd.DispatchCompute(cullShader, kCull, groups, 1, 1);
            cmd.DispatchCompute(cullShader, kArgs, cGroups, 1, 1);

            // 每级联把可见簇索引 gather 到 drawIdxBuf（Index|Raw 两用缓冲）的【独立偏移段】。
            // compute 直接写进 drawIdxBuf，绘制时 DrawProceduralIndirect 直接当索引缓冲读，无需 CopyBuffer。
            // ⚠️ 关键：drawIdxBuf 同时带 Raw（compute 经 RWByteAddressBuffer 写）与 Index（绘制当索引读），
            //    一缓冲两用，是 Unity 官方 GPU culling 的标准写法，绕开跨 Target CopyBuffer 的坑。
            int needed = cascadeCount * clusterCount * VertsPerCluster;
            if (drawIdxBuf == null || drawIdxBuf.count < needed)
            {
                drawIdxBuf?.Release();
                drawIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw
                                                | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination,
                                               needed, 4);
            }
            int dGroups = Mathf.CeilToInt((float)clusterCount / 64f);
            for (int k = 0; k < cascadeCount; k++)
            {
                // ⚠️ 关键修复（2026-10-24）：ComputeShader.SetInt 是【按 kernel】生效的，
                // 上面的 foreach 只对 kReset/kCull/kArgs 设了 _TrisPerCluster/_ClusterCount 等标量，
                // Gather(kGather) 没被设到 ⇒ _TrisPerCluster=0 ⇒ perCluster=0 ⇒
                //   for(corner=0; corner<0; ...) 不执行 ⇒ drawIdxBuf 全 0
                //   ⇒ 所有三角形退化成一点 ⇒ 阴影全空；但 _GatherProbe[0] 写在循环前仍会被置 1
                //   ⇒ 表现为「probe=1 + args 正确 + 阴影空」，正是本次症状。
                // 故这里显式给 kGather 设全部标量参数（SetBuffer 本来就是按 kernel 绑的，无此问题）。
                cullShader.SetInt("_CascadeCount", cascadeCount);
                cullShader.SetInt("_ClusterCount", clusterCount);
                cullShader.SetInt("_PlanesPerCascade", PlanesPerCascade);
                cullShader.SetInt("_MaxLod", 1);
                cullShader.SetInt("_TrisPerCluster", TrisPerCluster);
                cullShader.SetInt("_DebugForceAll", debugForceAll);
                cullShader.SetInt("_CascadeIndex", k);
                cullShader.SetBuffer(kGather, "_Counter", counterBuf);
                cullShader.SetBuffer(kGather, "_Visible", visibleBuf);
                cullShader.SetBuffer(kGather, "_Indices", idxBuf);
                cullShader.SetBuffer(kGather, "_DrawIndices", drawIdxBuf);
                cullShader.SetBuffer(kGather, "_GatherProbe", gatherProbe);   // [诊断] 探针
                cmd.DispatchCompute(cullShader, kGather, dGroups, 1, 1);
            }
            // 无 CopyBuffer：compute 已直接写入 drawIdxBuf（Index|Raw），下一行 Draw 即可读到正确索引。
        }

        /// <summary>画一个级联：本级联的索引已由 Cull() 的 Gather 写到 _DrawIndices 的独立偏移段，
        /// 这里只做索引绘制（GPU 后变换缓存按索引值去重顶点）。
        /// argsBuf[cascadeIndex*5] 里的 startIndexLocation 已指向本级联的偏移段。</summary>
        public void Draw(CommandBuffer cmd, int cascadeIndex, Matrix4x4 worldToClip)
        {
            if (!built || drawMat == null || cullShader == null) return;
            if (cascadeIndex < 0 || cascadeIndex >= cascadeCount) return;

            // 索引绘制：用 CommandBuffer.DrawProceduralIndirect 的【索引重载】
            //   （首参 indexBuffer = drawIdxBuf，末参 bufferWithArgs = argsBuf）。
            //   indexCount / startIndexLocation 都来自 argsBuf（BuildArgs 已写好 5 个 uint，
            //   startIndexLocation = 本级联在 _DrawIndices 里的偏移），instanceCount=1，
            //   索引值即全局顶点号，VS 直接用 SV_VertexID 取 _Positions。
            //   args 偏移 = 每级联 5 个 uint（20 字节）。
            // ⚠️ world→clip 矩阵（= proj*view，由 C# 每级联算好，与采样侧 ConvertToAtlasMatrix
            //   输入逐位一致）【不走】DrawProceduralIndirect 的 worldPose 参数：自定义 HLSLPROGRAM
            //   在该 draw 下不会把 worldPose 可靠喂进内建 UNITY_MATRIX_M（会恒为单位矩阵 ⇒ 全裁光）。
            //   改为经 MaterialPropertyBlock 逐 draw 显式传自定义属性 _ClusterShadowW2C，100% 生效、
            //   且 4 个级联各自独立（per-draw，互不污染）。这是 2026-10-25 定位"无阴影"的真正根因修复。
            if (m_mpb == null) m_mpb = new MaterialPropertyBlock();
            m_mpb.SetMatrix("_ClusterShadowW2C", worldToClip);
            cmd.DrawProceduralIndirect(drawIdxBuf, Matrix4x4.identity, drawMat, 0,
                MeshTopology.Triangles, argsBuf, sizeof(uint) * 5 * cascadeIndex, m_mpb);
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

            // ===== [诊断] 位置探针比对：验证「索引值 → _Positions → 世界坐标」这一底层环节 =====
            // GPU 端结果来自 ProbePositions 内核（正常/旁路两种模式都派发了）。
            // C# 端期望值 = allPosArray[allIdxArray[t]]（完全不经过 GPU，纯 CPU 真相）。
            // 两者一致 ⇒ stride/去重映射/缓冲绑定全对，问题在上层（SV_VertexID 语义 / 矩阵 / 深度）。
            // 不一致 ⇒ 最底层的 _Positions 读取就是错的（典型：float3 被按 16 字节步长读，
            //   画面表现为几何整体错位、高度明显偏飞），必须先修这里。
            if (posProbe != null && allIdxArray != null && allPosArray != null)
            {
                var got = new Vector4[4];
                posProbe.GetData(got);
                var sbChk = new System.Text.StringBuilder();
                bool allMatch = true;
                for (int t = 0; t < 4; t++)
                {
                    int gi = allIdxArray[t];
                    if (gi < 0 || gi >= allPosArray.Length) { sbChk.Append($" #{t}:索引越界({gi})"); allMatch = false; continue; }
                    Vector3 want = allPosArray[gi];
                    Vector3 see = new Vector3(got[t].x, got[t].y, got[t].z);
                    float err = (see - want).magnitude;
                    bool okSample = err < 1e-3f;
                    allMatch &= okSample;
                    sbChk.Append($" #{t}[idx={gi}] 期望({want.x:F1},{want.y:F1},{want.z:F1}) GPU({see.x:F1},{see.y:F1},{see.z:F1}) 误差={err:F4} {(okSample ? "OK" : "不符")}");
                }
                Debug.LogWarning("[Cluster阴影][位置探针] " +
                    (allMatch ? "✅ GPU 取到的世界坐标与 CPU 期望完全一致 ⇒ _Positions 读取正确（stride/映射/绑定都没问题）"
                              : "❌ GPU 取到的世界坐标与 CPU 期望不符 ⇒ _Positions 读取有误（多为 float3 stride 被按16字节读 / 去重映射错）") +
                    sbChk);
            }

            if (debugForceAll != 0)
            {
                Debug.LogWarning("[Cluster阴影][旁路] 当前是「全画旁路」模式：不派发任何 compute kernel，" +
                    "直接用全量索引绘制整份几何。画面若无阴影 ⇒ 问题确定在绘制链本身，与剔除无关。");
                return;
            }

            // ===== 临时诊断（ClusterDebug 限频）：直接回读 GPU 产物，定位"阴影全空"根因 =====
            // 单缓冲(Index|Raw)方案下，compute 直接把索引写进 drawIdxBuf，不再有 CopyBuffer。
            // 故只需看 probe（普通 ComputeBuffer 回读可靠）：其值即 drawIdxBuf 的前若干索引，
            //   · probe=384 且前若干索引合理(<顶点总数) ⇒ drawIdxBuf 已填 ⇒ 索引链路打通，看画面阴影。
            //   · probe=0 或前若干索引全0/乱码 ⇒ Gather 没写（_TrisPerCluster 未传/缓冲绑定错）。
            {
                // ===== 临时诊断（ClusterDebug 限频）=====
                // 单缓冲(Index|Raw)方案下，compute 直接把索引写进 drawIdxBuf，不再有 CopyBuffer。
                // 因此 probe 里的值【就是】drawIdxBuf 的前若干索引：probe 合理 ⇒ drawIdxBuf 已填 ⇒ 链路通。
                // （GraphicsBuffer.GetData 在 2022.3 不可信，故不再回读 drawIdxBuf 做"段2"，以 probe 为准。）
                var args5 = new uint[5];
                argsBuf.GetData(args5);
                Debug.Log($"[Cluster阴影][诊断] args级联0：indexCount={args5[0]} instanceCount={args5[1]} startIndex={args5[2]} baseVertex={args5[3]} startInstance={args5[4]}");

                var probe = new uint[8];
                if (gatherProbe != null) gatherProbe.GetData(probe);
                // probe[0]=实际 perCluster(应384)；probe[1..7]=Gather 实际写入 drawIdxBuf 的前 7 个索引值。
                var sbP = new System.Text.StringBuilder();
                for (int i = 1; i < 8; i++) sbP.Append(probe[i]).Append(' ');
                Debug.Log($"[Cluster阴影][诊断] probe(perCluster)={probe[0]} | 写入drawIdxBuf前7索引={sbP}(合理值应<{vertexCount}；0/乱码⇒Gather未写/绑定错)");

                // 判读：
                //   · probe=384 且 前7索引合理 ⇒ drawIdxBuf 已被 compute 正确填充 ⇒ 索引链路打通，
                //     若画面仍无阴影，问题收敛到绘制侧 shader/SV_VertexID/矩阵（但那一侧已逐行核对）。
                //   · probe=0 或 前7索引全0/乱码 ⇒ Gather 没写（_TrisPerCluster 未传 / 缓冲绑定错）。
                bool ok = probe[0] == (uint)VertsPerCluster;
                Debug.Log($"[Cluster阴影][诊断] 结论：{(ok ? "drawIdxBuf 已填充(链路通)，看画面阴影是否出现" : "Gather 未写入，查 SetInt/缓冲绑定")}");
            }

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
                    // 逐级联同时打印「GPU 可见簇数 / 绘制索引数 / 起始偏移」：
                    //   · idxCount=0        ⇒ 该联 DrawProceduralIndirect 无三角形（剔除剔光 或 BuildArgs 没写）。
                    //   · 簇>0 但 idxCount=0 ⇒ BuildArgs/startIndexLocation 偏移错（绘制链坏）。
                    //   · 簇=0              ⇒ 该联视锥内确无几何（剔除正确，不是 bug）。
                    //   · 簇>0 且 idxCount>0 但画面仍空 ⇒ 问题在矩阵/深度/采样寻址（绘制链通但几何被变换错/采到空 tile）。
                    var argsAll = new uint[5 * n];
                    argsBuf.GetData(argsAll, 0, 0, 5 * n);
                    var counts = new System.Text.StringBuilder();
                    for (int k = 0; k < n; k++)
                        counts.Append($" 级联{k}[簇={counters[k]}, idx={argsAll[5*k+0]}, start={argsAll[5*k+2]}]");
                    Debug.LogWarning("[Cluster阴影][Debug] 各级联绘制量：" + counts +
                        $"（总簇={clusterCount}, 每簇{VertsPerCluster}索引）。" +
                        "img: 看哪联 idx=0（被剔光/参数错）还是全有（绘制链问题）。");
                }
            }
        }

        // ---------------- 释放 ----------------

        public void Release()
        {
            posBuf?.Release(); idxBuf?.Release(); boundsBuf?.Release();
            posProbe?.Release();
            planesBuf?.Release(); cascadeParamBuf?.Release();
            visibleBuf?.Release(); counterBuf?.Release(); argsBuf?.Release();
            drawIdxBuf?.Release();
            gatherProbe?.Release();
            allIdxArray = null;
            fullIdxUploaded = false;
            posBuf = idxBuf = boundsBuf = null;
            planesBuf = cascadeParamBuf = visibleBuf = counterBuf = argsBuf = null;
            drawIdxBuf = null;
            built = false;
        }
    }
}