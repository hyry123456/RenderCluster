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
    ///   · 每帧 3 个 compute dispatch（Reset / CullAll / BuildArgs）一次性剔完【所有级联】——
    ///     CullAll 每线程一个簇、内部循环所有级联做球-平面视锥剔除，可见簇原子追加进 _Visible；
    ///     BuildArgs 每线程一个级联写 indirect args。故 4 个级联 = 3 次 dispatch，不是 4 次。
    ///   · 每级联一次 DrawProceduralIndirect（共 4 次 draw）：shader 在 VS 里查
    ///     _Visible + _Indices + _Positions 还原几何，不依赖任何跨缓冲拷贝。
    ///   · LOD：当前只画 LOD0。
    ///
    /// 数据布局（摊平到世界空间，静态场景，运行时不乘实例矩阵）：
    ///   _Positions —— float3 世界空间顶点
    ///   _Indices  —— int32 全局顶点号，每簇固定 384（= 128 三角形×3）
    ///   _Visible  —— compute 写出的可见簇紧凑列表 [cascade*簇数 + slot] = 簇下标
    ///   索引缓冲是【静态单位缓冲 [0..N]】，VS 用 SV_VertexID 推出 slot=vid/384、corner=vid%384，
    ///   查 _Visible 得簇号 → _Indices 得全局顶点号 → _Positions。
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

        // ---------------- GPU 资源 ----------------
        Material drawMat;
        MaterialPropertyBlock m_mpb;     // [绘制] 逐 draw 显式传入 world→clip 矩阵（per-draw，互不污染）
        ComputeShader cullShader;
        ComputeBuffer posBuf, idxBuf, boundsBuf;
        // 索引缓冲退化为【单一静态单位缓冲】：创建时写入 [0,1,2,...,N]，之后永不再写。
        // compute 只产出可见簇列表 _Visible（RWStructuredBuffer，可写、无拷贝）；绘制时由 shader 在 VS 内
        // 用 SV_VertexID 查 _Visible + _Indices + _Positions 还原几何。
        // 坑：D3D11 下 Index 缓冲不能当 compute UAV 写，故绝不能让 compute 直接写本缓冲（会静默丢弃）。
        GraphicsBuffer drawIdxBuf;
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

        /// <summary>LOD 判据的目标 texel 数。<= 0 = 不做 LOD 判据（当前只有 LOD0）。</summary>
        public float targetTexels = 0f;

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
            var fullPos = allPos.ToArray();
            var fullIdx = allIdx.ToArray();
            posBuf.SetData(fullPos);
            idxBuf.SetData(fullIdx);
            boundsBuf.SetData(allBounds.ToArray());

            // 静态单位索引缓冲：内容 [0,1,2,...,clusterCount*384-1]，创建一次、永不再写（不参与任何拷贝）。
            // DrawProceduralIndirect 必须传 indexBuffer，shader 的 VS 用 SV_VertexID(= 单位值) 推出
            // 可见簇槽位与簇内角点，再查 _Visible + _Indices + _Positions 还原几何。
            int idxCap = clusterCount * VertsPerCluster;
            drawIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index, idxCap, 4);
            var identity = new int[idxCap];
            for (int n = 0; n < idxCap; n++) identity[n] = n;
            drawIdxBuf.SetData(identity);

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

            Load();
            built = true;
            dirty = false;

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
        /// <param name="worldToClip">该级联的世界→裁剪矩阵，= GL.GetGPUProjectionMatrix(proj,true)*view，
        ///   与绘制侧逐位一致。用它的 6 个平面做簇包围球的球-平面剔除（口径与渲染完全相同，不漏不重）。</param>
        /// <param name="proj">引擎给的 projectionMatrix（取 projScale 用）。</param>
        /// <param name="cullSphere">Unity ShadowSplitData.cullingSphere（仅留作诊断参考，不参与剔除）。</param>
        /// <param name="tileSize">该级联在图集里占多少像素（算 projScale 用）。</param>
        public void AddCascade(Matrix4x4 worldToClip, Matrix4x4 proj, Vector4 cullSphere, float tileSize)
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

            // projScale：把世界半径换算成阴影图 texel 半径（LOD 判据用，当前 _MaxLod=1 时恒不生效）。
            // 正交投影下 proj[1][1] = 2/(top-bottom)，世界半径 r 对应 NDC y = r*proj11，再乘 tileSize/2 得 texel。
            float projScale = 0f;
            if (Mathf.Abs(proj.m11) > 1e-6f) projScale = tileSize * 0.5f * Mathf.Abs(proj.m11);
            cpuCascadeParams[k] = new Vector4(projScale, targetTexels, 0f, 0f);
        }

        /// <summary>
        /// 一次 GPU 剔除，产出每个级联的可见簇列表(_Visible) + indirect args(_Args)。
        /// 必须在【所有级联都 AddCascade 完】之后、任何 draw 之前调用。
        /// 整帧只有 3 个 dispatch（Reset / CullAll / BuildArgs）。
        /// compute 只写 _Visible（可见簇紧凑列表，UAV 可写，无拷贝）；绘制时 shader 在 VS 自行查表还原几何。
        /// </summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!built || cascadeCount == 0) return;
            if (cullShader == null) return;

            // debugForceAll 非 0 ⇒ compute 跳过剔除、强制全可见（隔离测试用，全程不另走 CPU 旁路）。
            int dbg = (debugForceAll == 0) ? 0 : 1;

            planesBuf.SetData(cpuPlanes);
            cascadeParamBuf.SetData(cpuCascadeParams);

            int groups = Mathf.CeilToInt(clusterCount / (float)ThreadGroupSize);
            int cGroups = Mathf.Max(1, Mathf.CeilToInt(cascadeCount / 8f));

            // buffer 绑定按 kernel 分开设：SetBuffer(kernelIndex, name, buf) 只对该 kernel 生效。
            // _Counter / _Args / _Visible 被 Reset / CullAll / BuildArgs 共用 ⇒ 三个都要绑。
            int kReset = cullShader.FindKernel("Reset");
            int kCull = cullShader.FindKernel("CullAll");
            int kArgs = cullShader.FindKernel("BuildArgs");

            foreach (int k in new[] { kReset, kCull, kArgs })
            {
                cullShader.SetInt("_CascadeCount", cascadeCount);
                cullShader.SetInt("_ClusterCount", clusterCount);
                cullShader.SetInt("_MaxLod", 1);          // 只画 LOD0
                cullShader.SetInt("_TrisPerCluster", TrisPerCluster);
                cullShader.SetInt("_DebugForceAll", dbg);

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
            // compute 只写 _Visible（可见簇紧凑列表，UAV 可写，无需任何跨缓冲拷贝）。
            // 绘制时 shader 在 VS 里查 _Visible + _Indices + _Positions 还原几何。
        }

        /// <summary>画一个级联：索引缓冲是静态单位缓冲 drawIdxBuf（[0..N]），shader 用 SV_VertexID 推出
        /// 可见簇槽位与簇内角点，再查 _Visible + _Indices + _Positions 还原几何。
        /// 经 MaterialPropertyBlock 逐 draw 传 _CascadeBase（= cascadeIndex*簇数，定位 _Visible 段）与 _ClusterShadowW2C。
        /// argsBuf[cascadeIndex*5] 里 startIndexLocation 恒为 0。</summary>
        public void Draw(CommandBuffer cmd, int cascadeIndex, Matrix4x4 worldToClip)
        {
            if (!built || drawMat == null || cullShader == null) return;
            if (cascadeIndex < 0 || cascadeIndex >= cascadeCount) return;
            if (drawIdxBuf == null) return;

            // 索引绘制：用 CommandBuffer.DrawProceduralIndirect 的【索引重载】，
            //   indexBuffer = drawIdxBuf（静态单位缓冲 [0..clusterCount*384-1]，SV_VertexID = 0..N），
            //   bufferWithArgs = argsBuf（BuildArgs 写好 5 个 uint，startIndexLocation 恒 0），
            //   instanceCount = 1。shader 在 VS 里用 vid/384 取 _Visible 槽位、vid%384 取簇内角点，
            //   再查 _Visible + _Indices + _Positions 还原几何（不再依赖任何跨缓冲拷贝）。
            //   args 偏移 = 每级联 5 个 uint（20 字节）。
            // world→clip 不走 DrawProceduralIndirect 的 worldPose：自定义 HLSLPROGRAM 下它不喂内建矩阵
            //（恒为单位矩阵 ⇒ 全裁光），改为逐 draw 经 MaterialPropertyBlock 传自定义 _ClusterShadowW2C。
            if (m_mpb == null) m_mpb = new MaterialPropertyBlock();
            m_mpb.SetMatrix("_ClusterShadowW2C", worldToClip);
            m_mpb.SetInt("_CascadeBase", cascadeIndex * clusterCount);
            cmd.DrawProceduralIndirect(drawIdxBuf, Matrix4x4.identity, drawMat, 0,
                MeshTopology.Triangles, argsBuf, sizeof(uint) * 5 * cascadeIndex, m_mpb);
        }

        // ---------------- 释放 ----------------

        public void Release()
        {
            posBuf?.Release(); idxBuf?.Release(); boundsBuf?.Release();
            planesBuf?.Release(); cascadeParamBuf?.Release();
            visibleBuf?.Release(); counterBuf?.Release(); argsBuf?.Release();
            drawIdxBuf?.Release(); drawIdxBuf = null;
            posBuf = idxBuf = boundsBuf = null;
            planesBuf = cascadeParamBuf = visibleBuf = counterBuf = argsBuf = null;
            built = false;
        }
    }
}
