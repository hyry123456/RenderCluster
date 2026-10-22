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

        // ---------------- 每帧级联数据 ----------------
        int cascadeCount;
        readonly Vector4[] cpuPlanes = new Vector4[MaxCascades * PlanesPerCascade];
		readonly Vector4[] cpuCascadeParams = new Vector4[MaxCascades];
		readonly int[] cpuPlaneCount = new int[MaxCascades];
		/// <summary>世界空间包围球（xyz=中心 w=半径），按簇下标排布，供 Debug 模式 CPU 剔除比对用。</summary>
		Vector4[] cpuBounds;

        /// <summary>LOD 判据的目标 texel 数。<= 0 = 不做 LOD 判据（当前只有 LOD0）。</summary>
        public float targetTexels = 0f;

        /// <summary>调试探头：非 0 时 compute 跳过所有剔除，全画。</summary>
        public int debugForceAll = 0;

        public bool IsReady => built && !dirty;
        public int ClusterCount => clusterCount;
        public int CascadeCount => cascadeCount;

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
            dirty = true;
        }

        /// <summary>扫描场景，把会投影的静态 MeshRenderer 全收进来。
        /// cluster 路径覆盖的是【整个场景】，SRP 路径也是，这样两条路才可比。</summary>
        void ScanScene()
        {
            var mrs = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null || !mr.enabled) continue;
                if (mr.shadowCastingMode == ShadowCastingMode.Off) continue;
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

        /// <summary>
        /// ClusterDebug 模式：在 GPU 剔除 + 绘制之外，CPU 用【完全相同】的视锥测试口径再剔除一次，
        /// 把 CPU 可见数 与 GPU 计数(_Counter) 比对，不一致就说明裁剪逻辑有错。
        ///
        /// CPU 测试口径严格对齐 ClusterShadowCull.compute 的 CullAll kernel：
        ///   对每簇包围球 center/radius 与级联的 6 个世界空间平面做
        ///   dot(plane.xyz, center) + plane.w &lt; -radius ⇒ 完全在外侧 ⇒ 剔除。
        /// （LOD 判据因 targetTexels&lt;=0 且 _MaxLod=1 恒跳过，CPU 也不做；forceAll 时两边都不判。）
        ///
        /// ⚠️ 同步 GetData 会卡 GPU 流水线，限频到 1/120 帧，绝不要用它测性能。
        /// </summary>
        public void VerifyCulling()
        {
            if (!built || cascadeCount == 0 || cpuBounds == null) return;
            if (Time.frameCount - s_lastStatFrame < 120) return;
            s_lastStatFrame = Time.frameCount;

            int n = Mathf.Min(cascadeCount, MaxCascades);
            var counters = new uint[MaxCascades];
            counterBuf.GetData(counters, 0, 0, n);

            // CPU 侧：逐簇逐级联用包围球 vs 世界空间平面做测试，口径与 compute 完全一致
            long[] cpuVisible = new long[n];
            for (int k = 0; k < n; k++)
            {
                int pc = cpuPlaneCount[k];
                int baseIdx = k * PlanesPerCascade;
                int cnt = 0;
                for (int c = 0; c < clusterCount; c++)
                {
                    Vector4 bb = cpuBounds[c];
                    Vector3 center = new Vector3(bb.x, bb.y, bb.z);
                    float radius = bb.w;
                    bool ok = true;
                    if (debugForceAll == 0)
                    {
                        for (int i = 0; i < pc; i++)
                        {
                            Vector4 pl = cpuPlanes[baseIdx + i];
                            if (Vector3.Dot(new Vector3(pl.x, pl.y, pl.z), center) + pl.w < -radius)
                            { ok = false; break; }
                        }
                    }
                    if (ok) cnt++;
                }
                cpuVisible[k] = cnt;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[Cluster阴影][Debug] 簇数 = ").Append(clusterCount)
              .Append(" | 级联数 = ").Append(cascadeCount)
              .Append(" | 每簇顶点 = ").Append(VertsPerCluster)
              .Append(" | LOD 判据 = ")
              .Append(targetTexels > 0 ? $"开({targetTexels} texel)" : "关(只画 LOD0)")
              .Append(" | forceAll = ").Append(debugForceAll)
              .Append("\n  各级联 [k] = CPU可见 / GPU可见: ");
            bool mismatch = false;
            long gpuSum = 0;
            for (int k = 0; k < n; k++)
            {
                long gpu = (long)counters[k];
                if (k > 0) sb.Append(", ");
                sb.Append('[').Append(k).Append("]=").Append(cpuVisible[k]).Append('/').Append(gpu);
                if (cpuVisible[k] != gpu) mismatch = true;
                gpuSum += gpu;
            }
            Debug.Log(sb.ToString());

            if (mismatch)
            {
                Debug.LogWarning("[Cluster阴影][Debug] CPU 与 GPU 剔除结果不一致 ⇒ 裁剪逻辑有错，" +
                    "优先检查 _Planes 平面空间 / _ClusterBounds 世界空间 / 矩阵来源是否与 compute 一致。");
            }
            else if (gpuSum == 0 && debugForceAll == 0)
            {
                Debug.LogWarning("[Cluster阴影][Debug] 所有级联 GPU 可见簇数都是 0 ⇒ 剔除条件有问题（平面/包围球/矩阵），" +
                    "不是绘制链问题。可把 debugForceAll 临时设为 1 验证：强制全画后可见数=簇数即证明 compute 链正常。");
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