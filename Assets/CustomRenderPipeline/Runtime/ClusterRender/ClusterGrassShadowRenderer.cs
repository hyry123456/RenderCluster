using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// 【B 方案 · 已停用 2026-10-28，当前无任何调用点】
    ///
    /// 结论先说：这条路实测**无效且更慢**，已回滚到 SRP 的 context.DrawShadows（见 Shadows.cs
    /// 的 cutout 分支注释）。文件保留仅作参考，别再接回去。
    ///
    /// 为什么失败（重要，避免重复踩）：
    ///   · 原以为那 0.325ms 是「cluster 间接绘制 → SRP DrawShadows」的路径切换成本，
    ///     所以把草搬到同一个 DIPS 通道里画理论上是白赚。
    ///   · 实测证明：切换不是瓶颈（SRP 内部同样要从 SRPBatcher flush 到实例绘制，两边的
    ///     "切换"本质相同）。那 0.325ms 是 SRP 用**硬件顶点缓冲 + 顶点后变换缓存**画草几何的
    ///     真实工作量。
    ///   · 换成过程式顶点（SV_VertexID 查 _Positions/_UVs/_InstanceMatrices/_Visible）后，
    ///     每顶点多 4 次 structured buffer 读 + 丢失顶点缓存复用 ⇒ 比原来更贵。
    ///
    /// 背景（原本的设计意图，留作记录）：
    /// 草是 cutout，Cluster 阴影 shader 是纯几何、不采贴图 ⇒ 之前只能把草交给 SRP 的
    /// context.DrawShadows 单独画一条 pass。
    ///
    /// 本类把草阴影改成【和簇阴影同一类 draw】：
    ///   · 按 (mesh, 材质) 分组，组内所有实例共享同一份局部空间顶点/UV/索引缓冲；
    ///   · 每实例只存一个世界矩阵 + 一个世界包围球；
    ///   · 每帧用 compute 对实例做逐级联球-平面剔除，产出每级联的紧凑可见列表 + indirect args；
    ///   · 每级联一次 DrawProceduralIndirect（实例化），VS 用 SV_InstanceID 取矩阵、SV_VertexID 取顶点/UV，
    ///     FS 采 albedo 的 alpha 做 clip。
    ///
    /// LOD：阴影投射器用 LOD0 恒正确（阴影不需要跟相机 LOD 一致），草 LOD0 只是几张卡片，代价可忽略。
    /// 主渲染的草仍走 SRP，LOD 完全不受影响。
    /// </summary>
    public class ClusterGrassShadowRenderer
    {
        public const int MaxCascades = 8;          // 与 ClusterShadowRenderer 对齐
        const int PlanesPerCascade = 6;
        const int ThreadGroupSize = 64;

        const string ShaderName = "Hidden/ClusterGrassShadowCaster";
        const string CullShaderName = "Cluster/ClusterGrassShadowCull";

        static ClusterGrassShadowRenderer s_instance;
        public static bool HasInstance => s_instance != null;
        public static ClusterGrassShadowRenderer Instance =>
            s_instance ?? (s_instance = new ClusterGrassShadowRenderer());

        sealed class Group
        {
            public Mesh mesh;
            public int instanceCount;
            public int indexCount;

            public ComputeBuffer posBuf;      // float3 局部顶点
            public ComputeBuffer uvBuf;       // float2 局部 UV
            public ComputeBuffer instMatBuf;  // float4x4 世界矩阵（每实例）
            public ComputeBuffer instBoundsBuf; // float4 世界包围球（每实例）
            public GraphicsBuffer idxBuf;     // uint 索引（mesh.triangles）

            public ComputeBuffer visibleBuf;  // [cascade * instCount]
            public ComputeBuffer counterBuf;  // [cascade]
            public ComputeBuffer argsBuf;     // [cascade * 5]

            public Texture albedo;
            public float cutoff;
            public Vector4 albedoST;
            /// <summary>风参：x=height y=scale z=bendMax w=phaseScale（与 Vegetation.shader 一致）。</summary>
            public Vector4 windParams;
        }

        readonly List<Group> groups = new List<Group>();
        bool built, dirty = true;

        ComputeShader cull;
        Material drawMat;
        ComputeBuffer planesBuf;              // [MaxCascades * 6]
        MaterialPropertyBlock mpb;

        int cascadeCount;
        readonly Vector4[] cpuPlanes = new Vector4[MaxCascades * PlanesPerCascade];

        /// <summary>是否扫描到了任何草（无草时 Shadows.cs 完全不碰本类）。</summary>
        public bool HasGrass => groups.Count > 0;
        /// <summary>是否已构建出可绘制数据。</summary>
        public bool IsReady => built && groups.Count > 0;

        public void MarkDirty() => dirty = true;

        // =====================================================================
        // 扫描 / 构建
        // =====================================================================

        public void RebuildIfNeeded()
        {
            if (!dirty) return;
            dirty = false;
            Release();
            Scan();
            BuildGPU();
        }

        static bool IsLod0(Renderer r)
        {
            var lg = r.GetComponentInParent<LODGroup>();
            if (lg == null) return true;                 // 非 LODGroup：本身就是唯一档
            var lods = lg.GetLODs();
            if (lods.Length == 0 || lods[0].renderers.Length == 0) return false;
            for (int i = 0; i < lods[0].renderers.Length; i++)
                if (lods[0].renderers[i] == r) return true;  // 属于 LOD0
            return false;                                // 是 LOD1/2…，阴影不需要
        }

        void Scan()
        {
            var mrs = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            // 分组键：(mesh, 材质)。材质决定 albedo / cutoff / ST，所以必须按材质分，
            // 否则同一 mesh 不同材质的草会共用一套 clip 参数。
            var byKey = new Dictionary<long, Group>();
            var instOf = new Dictionary<Group, List<Matrix4x4>>();

            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null) continue;

                var mat = mr.sharedMaterial;
                if (mat == null || !mat.IsKeywordEnabled("_CLIPPING")) continue;  // 只收 cutout
                if (!IsLod0(mr)) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                // 空网格 / 无索引 ⇒ 没有可画的三角形；无 UV0 ⇒ shader 里 _UVs[vid] 越界/无意义。
                if (mesh.vertexCount == 0 || mesh.GetIndexCount(0) == 0) continue;
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) continue;

                long key = ((long)mesh.GetInstanceID() << 32) ^ (uint)mat.GetInstanceID();
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new Group { mesh = mesh, albedo = mat.mainTexture };
                    // 阈值：本工程植被材质的 alpha cutoff 属性名是 _CullOff（Vegetation.shader 的
                    // GetCutoff() 读的就是它）；ClusterPBR 也用 _CullOff。_Cutoff 仅作兜底。
                    g.cutoff = mat.HasProperty("_CullOff") ? mat.GetFloat("_CullOff")
                             : (mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f);
                    g.albedoST = mat.HasProperty("_MainTex_ST") ? mat.GetVector("_MainTex_ST")
                                                                 : new Vector4(1, 1, 0, 0);
                    // 风参：属性缺失时全 0 ⇒ 强度 0 ⇒ 不摆（静态阴影，安全兜底）。
                    g.windParams = new Vector4(
                        mat.HasProperty("_WindHeight") ? mat.GetFloat("_WindHeight") : 0f,
                        mat.HasProperty("_WindScale") ? mat.GetFloat("_WindScale") : 0f,
                        mat.HasProperty("_WindBendMax") ? mat.GetFloat("_WindBendMax") : 0f,
                        mat.HasProperty("_WindPhaseScale") ? mat.GetFloat("_WindPhaseScale") : 0f);
                    byKey[key] = g;
                    groups.Add(g);
                    instOf[g] = new List<Matrix4x4>();
                }
                instOf[g].Add(mr.transform.localToWorldMatrix);
            }

            // 实例矩阵字典留给 BuildGPU 用（Group 里不长期持有 CPU 侧矩阵数组）
            m_instOf = instOf;
        }

        // Scan → BuildGPU 之间的临时数据
        Dictionary<Group, List<Matrix4x4>> m_instOf;

        void BuildGPU()
        {
            if (groups.Count == 0) { built = false; return; }

            if (cull == null)
            {
                cull = Resources.Load<ComputeShader>(CullShaderName);
                if (cull == null) Debug.LogError($"[草阴影] 找不到 compute：Resources/{CullShaderName}");
            }
            if (drawMat == null)
            {
                var sh = Resources.Load<Shader>(ShaderName) ?? Shader.Find(ShaderName);
                if (sh == null) Debug.LogError($"[草阴影] 找不到 shader：{ShaderName}");
                else drawMat = new Material(sh) { name = "ClusterGrassShadow" };
            }
            if (mpb == null) mpb = new MaterialPropertyBlock();
            if (planesBuf == null) planesBuf = new ComputeBuffer(MaxCascades * PlanesPerCascade, 16);

            foreach (var g in groups)
            {
                var mesh = g.mesh;
                var verts = mesh.vertices;
                var uvs = mesh.uv;
                var tris = mesh.triangles;
                var insts = m_instOf[g];
                g.instanceCount = insts.Count;
                g.indexCount = tris.Length;

                g.posBuf = new ComputeBuffer(verts.Length, 12);
                g.posBuf.SetData(verts);
                g.uvBuf = new ComputeBuffer(uvs.Length, 8);
                g.uvBuf.SetData(uvs);

                uint[] idx = new uint[tris.Length];
                for (int i = 0; i < tris.Length; i++) idx[i] = (uint)tris[i];
                g.idxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index, tris.Length, 4);
                g.idxBuf.SetData(idx);

                g.instMatBuf = new ComputeBuffer(insts.Count, 64);
                g.instMatBuf.SetData(insts);

                // 每实例世界包围球：局部球心/半径经矩阵变换。
                var lb = mesh.bounds;
                Vector3 lc = lb.center;
                float lr = lb.extents.magnitude;
                var bounds = new Vector4[insts.Count];
                for (int i = 0; i < insts.Count; i++)
                {
                    Matrix4x4 m = insts[i];
                    Vector3 c = m.MultiplyPoint3x4(lc);
                    Vector3 sc = m.lossyScale;
                    float s = Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                    bounds[i] = new Vector4(c.x, c.y, c.z, lr * Mathf.Max(s, 1e-4f));
                }
                g.instBoundsBuf = new ComputeBuffer(insts.Count, 16);
                g.instBoundsBuf.SetData(bounds);

                g.visibleBuf = new ComputeBuffer(Mathf.Max(1, MaxCascades * insts.Count), 4);
                g.counterBuf = new ComputeBuffer(MaxCascades, 4);
                g.argsBuf = new ComputeBuffer(MaxCascades * 5, 4, ComputeBufferType.IndirectArguments);
            }

            built = true;
        }

        // =====================================================================
        // 每帧：登记级联 → 剔除 → 绘制
        // =====================================================================

        public void BeginLight() { cascadeCount = 0; }

        public void AddCascade(Matrix4x4 worldToClip)
        {
            if (!built || cascadeCount >= MaxCascades) return;
            int k = cascadeCount++;
            var planes = GeometryUtility.CalculateFrustumPlanes(worldToClip);
            for (int p = 0; p < PlanesPerCascade; p++)
            {
                var pl = planes[p];
                cpuPlanes[k * PlanesPerCascade + p] =
                    new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }
        }

        /// <summary>逐实例剔除。必须在所有级联 AddCascade 之后、任何 Draw 之前调用。</summary>
        public void Cull(CommandBuffer cmd)
        {
            if (!built || cascadeCount == 0 || cull == null) return;

            planesBuf.SetData(cpuPlanes);

            int kReset = cull.FindKernel("ResetGrass");
            int kCull = cull.FindKernel("CullGrass");
            int kArgs = cull.FindKernel("BuildGrassArgs");

            // _Planes 所有组共用
            cmd.SetComputeBufferParam(cull, kCull, "_Planes", planesBuf);

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (g.instanceCount <= 0) continue;

                cmd.SetComputeIntParam(cull, "_CascadeCount", cascadeCount);
                cmd.SetComputeIntParam(cull, "_InstanceCount", g.instanceCount);
                cmd.SetComputeIntParam(cull, "_IndexCount", g.indexCount);

                cmd.SetComputeBufferParam(cull, kReset, "_Counter", g.counterBuf);
                cmd.SetComputeBufferParam(cull, kCull, "_InstanceBounds", g.instBoundsBuf);
                cmd.SetComputeBufferParam(cull, kCull, "_Visible", g.visibleBuf);
                cmd.SetComputeBufferParam(cull, kCull, "_Counter", g.counterBuf);
                cmd.SetComputeBufferParam(cull, kArgs, "_Counter", g.counterBuf);
                cmd.SetComputeBufferParam(cull, kArgs, "_Args", g.argsBuf);

                cmd.DispatchCompute(cull, kReset, 1, 1, 1);
                cmd.DispatchCompute(cull, kCull,
                    Mathf.Max(1, Mathf.CeilToInt(g.instanceCount / (float)ThreadGroupSize)), 1, 1);
                cmd.DispatchCompute(cull, kArgs, 1, 1, 1);
            }
        }

        /// <summary>画一个级联：每组一次实例化间接绘制。只往 buffer 里塞，由调用方统一 Execute。</summary>
        public void Draw(CommandBuffer cmd, int cascade, Matrix4x4 worldToClip)
        {
            if (!built || drawMat == null || cascade >= cascadeCount) return;

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (g.instanceCount <= 0) continue;

                mpb.SetMatrix("_ClusterShadowW2C", worldToClip);
                mpb.SetBuffer("_Positions", g.posBuf);
                mpb.SetBuffer("_UVs", g.uvBuf);
                mpb.SetBuffer("_InstanceMatrices", g.instMatBuf);
                mpb.SetBuffer("_Visible", g.visibleBuf);
                mpb.SetTexture("_Albedo", g.albedo != null ? g.albedo : Texture2D.whiteTexture);
                mpb.SetFloat("_Cutoff", g.cutoff);
                mpb.SetVector("_Albedo_ST", g.albedoST);
                mpb.SetVector("_WindParams", g.windParams);
                mpb.SetInt("_CascadeBase", cascade * g.instanceCount);

                cmd.DrawProceduralIndirect(g.idxBuf, Matrix4x4.identity, drawMat, 0,
                    MeshTopology.Triangles, g.argsBuf, sizeof(uint) * 5 * cascade, mpb);
            }
        }

        // =====================================================================
        // 释放
        // =====================================================================

        public void Release()
        {
            foreach (var g in groups)
            {
                g.posBuf?.Release(); g.uvBuf?.Release();
                g.instMatBuf?.Release(); g.instBoundsBuf?.Release();
                g.idxBuf?.Release();
                g.visibleBuf?.Release(); g.counterBuf?.Release(); g.argsBuf?.Release();
            }
            groups.Clear();
            m_instOf = null;
            planesBuf?.Release(); planesBuf = null;
            built = false;
        }
    }
}
