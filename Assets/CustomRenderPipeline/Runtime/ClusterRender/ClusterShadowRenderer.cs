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
        // 方向光级联上限。必须与 ShadowSetting.Directional.cascadeCount 的 [Range(1,4)] 一致：
        // 它直接决定阴影侧所有缓冲的分配规模（`_DrawIdx` = 级联数 × 簇数 × 384 × 4B）。
        // 31854 簇时 8 → 391MB，4 → 196MB，白占的那一倍没有任何用处。
        // ⚠️ 若以后把 cascadeCount 的上限改大，这里必须同步，否则 AddCascade 会静默丢弃新增级联。
        public const int MaxCascades = 4;

        /// <summary>
        /// 每簇三角形数。改这个值必须同步改 shader 里的 TRIS_PER_CLUSTER
        /// （= 本值），否则索引区间会错位。
        /// </summary>
        public const int TrisPerCluster = 128;

        /// <summary>每簇索引数 = TrisPerCluster × 3。shader 不再硬编码顶点数。</summary>
        public const int VertsPerCluster = TrisPerCluster * 3;

        /// <summary>6 个视锥平面 + 2 个光轴 z 边界平面（补 reversed-Z 下 CalculateFrustumPlanes 不剔深度的问题）。
        /// ⚠️ 改这个值必须同步改 compute 里的 `p &lt; 8` 循环。</summary>
        const int PlanesPerCascade = 8;
        const int ThreadGroupSize = 64;

        /// <summary>
        /// 可见率阈值（百分比）：某级联可见簇 ≥ 总簇数 × 该值% ⇒ 直接画【静态全量段】，跳过索引重排。
        /// 依据：被视锥剔掉的簇 x/y 落不进本级联盒内 ⇒ 其影子也落不进 ⇒ 多画它们视觉无害，
        /// 只是多走几何；换掉的是整整 48.9MB/级的索引写入 + 同量读回。
        /// 调到 101 可完全禁用这条捷径（每级都重排，回到老行为）。
        /// 场景可见率低时（比如大部分簇看不到）自动落回重排，因为那时重排是真的在省几何。
        /// </summary>
        const int FullSkipPercent = 90;

        // Resources.Load 的路径 = 相对任意 Resources 文件夹、不带扩展名
        const string DrawShaderName = "Cluster/ClusterShadow";
        const string CullShaderName = "Cluster/ClusterShadowCull";

        static ClusterShadowRenderer s_instance;
        /// <summary>是否已经创建过（Dispose 时用来避免「为了释放反而 new 一个」）。</summary>
        public static bool HasInstance => s_instance != null;
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

        // ⚠️ 曾经的「全场景包围球」（m_sceneCenter / m_sceneRadius）已删除：
        //    它被用来给每个级联算 pad，等于要求每级联覆盖整场景沿光轴跨度（级联 0 要 56×）。
        //    现在改成逐级联只统计 x/y 相交的簇（见 ComputeDepthPad），不再需要全场景球。

        /// <summary>
        /// 把正交投影的 z 范围按 pad 倍**围绕板中心**扩张（pad &lt;= 1 原样返回）。
        ///
        /// 正交下 ndc = m22 · z_view + m23。簇集合是沿光轴双侧都超出原板的球，
        /// 必须把"板中心(ndc=0.5 处)"两侧一起拓宽，否则只扩远侧、朝光近侧仍被裁 ⇒ 近级联阴影缺块。
        ///
        /// 做法（与旧实现的关键区别）：
        ///   · m22 /= pad            → 斜率变小，可视 z 范围变宽 pad 倍（旧实现也是这个）。
        ///   · m23 同步平移，使板中心(0.5 处)不动 ⇒ 近/远两侧都扩，而不是只扩一侧。
        /// 旧实现把 m22/m23 同除 pad，等价于"围绕 ndc=0 边"展开（只扩远侧），
        /// 朝光一侧(更靠近光、对应近处级联的物体)超出原板的部分照样被裁光 ⇒ 阴影缺块/全错。
        /// 新公式：中心 z_c=(0.5-m23)/m22 在变换后仍为 ndc=0.5，且新 z 范围 = pad× 原范围（对称）。
        ///
        /// ⚠️ 必须对「渲染矩阵、图集采样矩阵、SetViewProjectionMatrices」用同一个结果，
        /// 否则两侧深度约定不一致 ⇒ 阴影整体错位。Shadows.cs 里在拿到 projectionMatrix 后
        /// 立刻替换，下游三处自然就一致了。
        /// </summary>
        public static Matrix4x4 PadOrthoDepth(Matrix4x4 proj, float pad)
        {
            if (pad <= 1.0001f) return proj;
            float newM23 = (proj.m23 + 0.5f * (pad - 1f)) / pad;   // 平移到板中心，近/远对称扩
            proj.m22 /= pad;
            proj.m23 = newM23;
            return proj;
        }

        // 最近一次场景扫描的统计：用来确认「该投影的物体是不是全部进了簇集合」。
        // 阴影缺块的第一嫌疑永远是「投射物压根没登记」，所以这几个数要在 Rebuild 日志里能看到。
        int m_scanTotal, m_scanSkipDisabled, m_scanSkipOff, m_scanSkipCutout, m_scanSkipNoMesh;

        // ---------------- 级联深度范围：按「真正会投进该级联的簇」精确推导 ----------------
        // CPU 侧簇包围球副本（与 GPU 的 _ClusterBounds 同序、同内容）。
        // 只用于逐级联推导「沿光轴需要多厚」，不参与任何绘制。
        Vector4[] m_cpuBounds;

        // pad 缓存：静止场景下这段是「数千簇 × 级联数」的 CPU 遍历，绝不能每帧跑。
        // key = 该级联的剔除球 + 光方向；两者任一变化才重算。
        readonly Vector4[] m_padSphereKey = new Vector4[MaxCascades];
        readonly Vector3[] m_padLightKey = new Vector3[MaxCascades];
        readonly bool[] m_padHasKey = new bool[MaxCascades];
        readonly float[] m_padCache = new float[MaxCascades];

        /// <summary>
        /// 该级联沿光轴需要多厚的 pad（1 = 原样不撑）。
        ///
        /// 【为什么不能用全场景包围球】旧实现是
        ///     need = |投影(全场景球心 − 级联球心)| + 全场景半径  ⇒  pad = need / 级联球半径
        /// 这等于要求每个级联都覆盖【整个场景沿光轴的跨度】。实测级联 0 要 56.8×、
        /// 级联 1 要 16.8× —— 光栅化器彻底不再裁近侧，整场景的三角形都要走完 setup + raster
        /// 才被深度测试丢掉。阴影 pass 本来就是 fill-rate bound，这是纯浪费，而且
        /// 「场景里物体越多越大 ⇒ 全场景半径越大 ⇒ pad 越大 ⇒ 阴影越慢」，正是内容变多拖慢阴影的通道。
        ///
        /// 【正确口径】平行光级联是正交投影且投影方向 = 光线方向 ⇒ 沿光轴不改变光空间 x/y。
        /// 所以一个簇的阴影只可能落进「光空间 x/y 与其相交」的那个级联；
        /// 其余簇沿光轴再远也与本盒无关，不需要覆盖。
        /// ⇒ 只统计 x/y 相交的簇，取它们沿光轴的 [minZ, maxZ]，pad 就是「刚好盖住这段」的倍数。
        ///
        /// 数学：
        ///   正交下 ndc = m22·z + m23，原可视范围 ndc∈[-1,1] ⇒ z∈[z0,z1]，中心 zc、半厚 h。
        ///   要让扩张后的范围（围绕 zc 对称、半厚 h·pad）盖住 [minZ,maxZ]：
        ///       pad = max(zc − minZ, maxZ − zc) / h
        /// </summary>
        public float ComputeDepthPad(int cascade, Vector3 lightDir, Matrix4x4 lightView,
                                     Vector4 cullingSphere, Matrix4x4 proj, float tileSize)
        {
            if (!built || m_cpuBounds == null || m_cpuBounds.Length == 0) return 1f;
            if (Mathf.Abs(proj.m22) < 1e-6f) return 1f;
            if (cascade < 0 || cascade >= MaxCascades) return 1f;

            // ---- 缓存判定：球没动、光没转 ⇒ 直接返回上次数值 ----
            bool valid = m_padHasKey[cascade]
                && (cullingSphere - m_padSphereKey[cascade]).sqrMagnitude <= 1f
                && Vector3.Angle(lightDir, m_padLightKey[cascade]) <= 1f;
            if (valid) return m_padCache[cascade];

            float R = Mathf.Max(cullingSphere.w, 1e-3f);

            // 原 z 范围（view space）：正交下 ndc = m22·z + m23。
            // ⚠️ 深度约定必须是 [0,1]（D3D 风格），依据：
            //    PadOrthoDepth 的平移式 newM23 = (m23 + 0.5(pad-1))/pad
            //    只在 [0,1] 下等价于「围绕板中心(ndc=0.5)对称扩张 pad 倍」——
            //    而这正是实测修好缺块的那一版。若按 [-1,1] 反解，厚度会算成 2 倍 ⇒ pad 偏小 ⇒ 缺块。
            float za = (0f - proj.m23) / proj.m22;
            float zb = (1f - proj.m23) / proj.m22;
            float z0 = Mathf.Min(za, zb), z1 = Mathf.Max(za, zb);
            float zc = (z0 + z1) * 0.5f;
            float half = Mathf.Max((z1 - z0) * 0.5f, 1e-3f);

            // 级联球心在光空间的位置：只用来算 x/y 偏移（不依赖 view 原点在哪）
            Vector3 sLight = lightView.MultiplyPoint3x4(
                new Vector3(cullingSphere.x, cullingSphere.y, cullingSphere.z));

            float minZ = float.MaxValue, maxZ = float.MinValue;
            int hit = 0;
            // 诊断：记录「最撑大板」的那个簇，用来定位到底是哪个物体逼得 pad 变大。
            float worstNeed = 0f, worstR = 0f;
            Vector3 worstC = Vector3.zero;
            var bounds = m_cpuBounds;
            for (int i = 0; i < bounds.Length; i++)
            {
                Vector4 b = bounds[i];
                float r = b.w;
                Vector3 c = lightView.MultiplyPoint3x4(new Vector3(b.x, b.y, b.z));

                // x/y 与该级联方块（光空间半宽 = R）相交？这是「阴影可能落进本盒」的充要条件。
                // 用「绝对值 − 半径 > R」判外，等价于把方块按簇半径外扩（保守方向，不会漏）。
                float dx = c.x - sLight.x;
                if (dx < 0f) dx = -dx;
                if (dx - r > R) continue;
                float dy = c.y - sLight.y;
                if (dy < 0f) dy = -dy;
                if (dy - r > R) continue;

                hit++;
                float zn = c.z - r, zf = c.z + r;
                if (zn < minZ) minZ = zn;
                if (zf > maxZ) maxZ = zf;

                float needHere = Mathf.Max(zc - zn, zf - zc);
                if (needHere > worstNeed)
                {
                    worstNeed = needHere;
                    worstR = r;
                    worstC = new Vector3(b.x, b.y, b.z);
                }
            }

            float pad = 1f;
            if (hit > 0 && minZ <= maxZ)
            {
                float need = Mathf.Max(zc - minZ, maxZ - zc);
                pad = need / half;
                pad *= 1.10f;                // 10% 余量：缓存不保证每帧精确，留一点防缺块
            }
            pad = Mathf.Clamp(pad, 1f, 16f);

            m_padCache[cascade] = pad;
            m_padSphereKey[cascade] = cullingSphere;
            m_padLightKey[cascade] = lightDir;
            m_padHasKey[cascade] = true;

#if UNITY_EDITOR
            // 本帧该级联的 pad 有多少是「真需要」、覆盖了多少簇，
            // 以及【是谁在撑大板】—— 定位到具体物体才能判断该不该排除它。
            // 只在 ClusterDebug 模式下输出（debugStats），平时全静默。
            if (debugStats && Time.frameCount % 240 == 0)
            {
                float texelPerMeter = tileSize / Mathf.Max(2f * R, 1e-3f);
                Debug.Log($"[Cluster阴影] 级联 {cascade} pad={pad:F2}×（x/y 命中 {hit} 簇 / 共 {bounds.Length}）" +
                          $"\n            最撑板的簇：世界中心 ({worstC.x:F1},{worstC.y:F1},{worstC.z:F1})，半径 {worstR:F2}m" +
                          $"，需深 {worstNeed:F1}m（原板半厚 {half:F1}m ⇒ {worstNeed / half:F1}×）" +
                          $"，投影约 {worstR * texelPerMeter:F1} texel");
            }
#endif
            return pad;
        }

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

        /// <summary>诊断用：可见簇数读回是否还在飞行中（防止每帧重复请求）。</summary>
        bool m_statsPending;

        /// <summary>_DrawIdx 的段 0（静态全量副本）还没填 ⇒ 下次 Cull 时跑一次 CopyStatic。</summary>
        bool m_needStaticCopy;

        // ---------------- 每帧级联数据 ----------------
        int cascadeCount;
        readonly Vector4[] cpuPlanes = new Vector4[MaxCascades * PlanesPerCascade];

        /// <summary>调试开关：非 0 时 compute 跳过所有剔除、强制全可见（隔离测试用）。</summary>
        public int debugForceAll = 0;

        /// <summary>
        /// 诊断输出开关：由 Shadows.cs 按 `clusterMode == ClusterDebug` 置位（平时全静默）。
        /// 打开后每 240 帧打 pad 推导明细，每 120 帧异步读回 _Counter 打「可见簇 ⇒ 倍数」。
        ///
        /// ⚠️ ClusterDebug 同时会把 debugForceAll 置 1（强制全可见）⇒ 此时「倍数」恒等于级联数（4.00×），
        /// 不是真实值。要复测真实倍数，临时注释掉 Shadows.cs 里 debugForceAll 那一行即可。
        /// </summary>
        public bool debugStats = false;

        // ================= Hi-Z 遮挡剔除（灯光空间）=================
        // 由 Shadows.cs 每帧按「资产的 clusterHiZOcclusion && 当前是 Game 相机」置位。
        // 和 GBuffer 那套同一个开关、同一套判据；Scene 相机恒 false（Scene 不做遮挡剔除）。
        public bool hiZEnable = false;
        /// <summary>当前渲染的相机是不是 Game 相机。由 DefferPipeline 每相机在渲染阴影【之前】置位；
        /// Scene 视图恒 false ⇒ 阴影不做遮挡剔除（用户要求：只有 Game 视图做）。</summary>
        public static bool HiZGameCamera = true;
        // 四级金字塔：1/2 → 1/4 → 1/8 → 1/16 图集（1024 图集 → 512/256/128/64）。
        // ⚠️ 不能加级：CullAll 的 UAV 是 _SlotOf/_Visible/_Counter + _HiZ0..3 = 7，再加就撞
        // D3D11.0 的 8 上限（超了 dispatch 会被静默跳过）。大簇靠最粗一级 8×8 texel 的预算覆盖。
        RenderTexture m_hiz0, m_hiz1, m_hiz2, m_hiz3;
        RenderTexture m_hizDummy;
        bool m_hizValid;
        int m_hizAtlasSize;
        const float HiZRelBias = 0.01f;
        const float HiZMinScreenSize = 4f;
        readonly Matrix4x4[] m_clipMtx = new Matrix4x4[MaxCascades];
        readonly Matrix4x4[] m_atlasMtx = new Matrix4x4[MaxCascades];
        readonly Matrix4x4[] m_atlasMtxZero = new Matrix4x4[MaxCascades];

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
        // ---------------- cutout 投射物的 SRP 阴影分流 ----------------
        // 草 / 蕨 / 灌木这类 alpha 镂空物体不进 Cluster：纯几何 shader 没法镂空，
        // 烘进簇只会得到一堆实心方块阴影（还白烧几十万个顶点）。
        // 它们改由 Shadows.cs 在 cluster 绘制之后，用 SRP 的 ShadowCaster 单独画一次；
        // 那条路按 renderingLayerMask 的 bit29 过滤 ⇒ 这里必须给它们打上这一位。
        //
        // 为什么判定看【材质 keyword】而不是看 ClusterObject 组件：
        // 场景里的草是几百份 prefab 实例，绝大多数没挂 ClusterObject（挂了反而会被
        // ApplyStandardMaterials 换成空白的 DefferStandard 材质）。组件可以漏，材质不会。
        readonly List<MeshRenderer> m_markedCutout = new List<MeshRenderer>();
        readonly List<uint> m_markedCutoutMask = new List<uint>();

        static bool MaterialNeedsClip(MeshRenderer mr)
        {
            var mats = mr.sharedMaterials;
            if (mats == null) return false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].IsKeywordEnabled("_CLIPPING")) return true;
            return false;
        }

        /// <summary>
        /// 镂空投射物的 renderingLayerMask：打上 bit29（SRP 阴影通道用它过滤），
        /// 并【清掉 bit28】——bit28 是「这个物体由 Cluster 画 GBuffer」的标记，
        /// SRP 的 DrawRenderers 是按「排除 bit28」来过滤的；草没注册进 Cluster，
        /// 而新建 MeshRenderer 的 mask 默认是 0xFFFFFFFF（含 bit28）⇒ 不清掉就会被排除，直接不画。
        /// </summary>
        /// <remarks>不看 enabled：LODGroup 会随相机来回切子 renderer 的 enabled，
        /// 只给"扫描那一刻恰好在看的那级"处理，切 LOD 后草就没阴影了。</remarks>
        void MarkCutoutCasters(MeshRenderer[] mrs)
        {
            if (!Application.isPlaying) return;
            const uint cutoffBit = 1u << ClusterProverty.CutoffRenderingLayerBit;
            const uint clusterBit = 1u << ClusterProverty.ClusterRenderingLayerBit;
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null) continue;

                // cutout（草/蕨/灌木）：打 bit29、清 bit28 ⇒ 进 SRP 阴影、不进 Cluster。
                // 其余物体：必须【清掉 bit29】。渲染器 renderingLayerMask 默认是 0xFFFFFFFF，
                // 本来就含 bit29；Cluster 阴影后的「cutout SRP 阴影 pass」按 light.mask=1<<29 过滤，
                // 不清的话【所有非 cutout 物体】都会被它再投一遍阴影（实测多出 0.4~0.5ms）。
                // 簇物体此时已是「只留 bit28」（ClusterObject.OnEnable 设好），& ~bit29 对它是 no-op，
                // 所以这里统一处理不会破坏簇物体的掩码。
                uint want = MaterialNeedsClip(mr)
                    ? (mr.renderingLayerMask | cutoffBit) & ~clusterBit
                    : mr.renderingLayerMask & ~cutoffBit;
                if (want == mr.renderingLayerMask) continue;        // 已经是想要的值，不必记录
                m_markedCutout.Add(mr);
                m_markedCutoutMask.Add(mr.renderingLayerMask);
                mr.renderingLayerMask = want;
            }
        }

        /// <summary>还原本渲染器打过的位（退出 Play / 重建前调用），避免脏化场景。</summary>
        void RestoreCutoutMarks()
        {
            for (int i = 0; i < m_markedCutout.Count; i++)
                if (m_markedCutout[i] != null)
                    m_markedCutout[i].renderingLayerMask = m_markedCutoutMask[i];
            m_markedCutout.Clear();
            m_markedCutoutMask.Clear();
        }

        void ScanScene()
        {
            m_hasCutoffCasters = false;
            m_scanTotal = m_scanSkipDisabled = m_scanSkipOff = m_scanSkipCutout = m_scanSkipNoMesh = 0;
            // 必须 Include（含 disabled）：草 prefab 是 LODGroup，扫描那一刻只有「当前那级」的
            // renderer 是 enabled 的。若只扫 enabled，bit29 只打在当前级 ⇒ 相机切 LOD 后新启用的
            // 那级还带着默认 mask 0xFFFFFFFF（含 bit28）⇒ 既被 SRP GBuffer 的 ~bit28 过滤掉（整片消失），
            // 又没 bit29（不投影）。所以所有 LOD 级（含 disabled）都要先打上 bit29 + 清 bit28。
            var mrs = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            m_scanTotal = mrs.Length;

            // 先把所有镂空投射物（含 LODGroup 当前没启用的那几级）的 bit29 打上、bit28 清掉
            MarkCutoutCasters(mrs);

            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null) { m_scanSkipDisabled++; continue; }
                // ① 跳过「整条 hierarchy 被禁用」的物体（例如用户把父级 Terrain 关掉，
                //    子物体 terrain_far_01 / terrain_near_01 的 MeshRenderer.enabled 仍是 true，
                //    但 activeInHierarchy == false ⇒ 场景里根本不可见，SRP 剔除也按这个判）。
                //    只查 mr.enabled 会漏掉它，导致它钻进 cluster 阴影 buffer、凭空投出一道山影
                //    （几何体本身不被画，于是「有山影却看不到山」）。所以必须先按 activeInHierarchy 排。
                if (!mr.gameObject.activeInHierarchy) { m_scanSkipDisabled++; continue; }
                // ② 跳过 LODGroup 当前没启用的级：LODGroup 会把「非当前级」的 renderer 置为 disabled，
                //    全部登记会让多个 LOD 同时投影（重影 + 几何翻倍）。这里保持「只烘当前级」的老行为，
                //    但把数量记下来 —— 如果这个数很大，说明场景里 LODGroup 很多，
                //    阴影用的是「扫描那一刻恰好在看的 LOD」，远处会偏粗（不是缺块，但会不一致）。
                if (!mr.enabled) { m_scanSkipDisabled++; continue; }
                if (mr.shadowCastingMode == ShadowCastingMode.Off) { m_scanSkipOff++; continue; }
                // 注：曾经试过按包围盒半径自动排除「超大投射物」（地形 plane 之类），
                // 实测没必要 —— 这类物体本来就不该投阴影，直接在场景里把它的
                // ShadowCastingMode 设成 Off 更直接、更可控（也更容易在 Inspector 里看见）。
                // 判断口径保持只有一条：shadowCastingMode != Off 才进簇集合。
                // 需 alpha 镂空的物体（草/蕨/灌木）走 SRP 阴影路径：Cluster 纯几何 shader 不采样贴图，
                // 进 Cluster 会画成实心方块 + 巨量簇（浪费且形状不对），故跳过它们、不烘焙进 Cluster，
                // 并记下"有 cutout 投射物"供 Shadows.cs 单独用 SRP 路径画
                // （用物体自身材质投阴影即可；几何体本身即外壳卡片，无需额外镂空处理）。
                // 判定「是否需要 SRP 阴影」：挂了 ClusterObject 的看它的资产标记，
                // 没挂的（草/蕨这类 prefab 实例）直接看材质的 _CLIPPING keyword。
                var co = mr.GetComponent<ClusterObject>();
                if ((co != null && co.UsesCutoff()) || MaterialNeedsClip(mr))
                {
                    m_hasCutoffCasters = true; m_scanSkipCutout++; continue;
                }
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

            // ⚠️ 簇数必须按 allBounds 算，不能用 allIdx/384：
            //    合并切簇时最后一个簇可能不满 128 三角形，allIdx 不是 384 的整数倍，
            //    向下取整会让 clusterCount < 簇数 ⇒ boundsBuf 分配偏小、SetData 失败/截断，
            //    且 compute 会漏掉尾簇（不剔除也不画）。这里补齐索引到 簇数×384：
            //    补 0 只是退化三角形（面积 0），不产生深度，安全。
            clusterCount = allBounds.Count;
            while (allIdx.Count < clusterCount * VertsPerCluster) allIdx.Add(0);
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
            // CPU 副本：逐级联推导 pad 要在 CPU 上遍历它（见 ComputeDepthPad）。
            // 只存一份 float4/簇，几千簇 ≈ 几十 KB，可忽略；rebuild 时同步更新、Release 时清空。
            m_cpuBounds = allBounds.ToArray();
            for (int i = 0; i < MaxCascades; i++) m_padHasKey[i] = false;

            // 剔除用的 buffer 大小都按"级联数 × 簇数"来，宁可多 alloc 也不要重分配
            int maxC = Mathf.Max(1, MaxCascades);

            // 真索引缓冲：(级联数 + 1) 段，每段一份全量索引（簇数 × 384）。
            //   段 0        = 静态全量副本（Rebuild 后由 CopyStatic 填一次，永不改变）
            //   段 (k+1)    = 级联 k 的压实段（compute 每帧只给【可见】簇写）
            // 可见率高的级联直接画段 0 ⇒ 完全跳过每帧重排（等价于 Unity 用 mesh 自带静态索引）。
            // Target.Index | Target.Raw：D3D11 不允许索引缓冲同时是 Structured，但允许同时是 Raw
            // ⇒ compute 用 RWByteAddressBuffer 写、draw 直接当索引读，单缓冲零拷贝。
            drawIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw,
                (maxC + 1) * clusterCount * VertsPerCluster, 4);
            m_needStaticCopy = true;      // 段 0 尚未填，首次 Cull 时用 CopyStatic 补

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
            // 只在 ClusterDebug 模式输出（debugStats 由 Shadows.cs 按 clusterMode 置位），
            // 平时静默——每次 Rebuild 都刷这条太吵。
            if (debugStats)
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
        public void AddCascade(Matrix4x4 worldToClip) => AddCascade(worldToClip, default(Matrix4x4), 0f, 0f, false);

        /// <summary>
        /// 登记一个级联（带【光轴 z 范围】剔除）。
        ///
        /// 为什么要额外剔 z：`GeometryUtility.CalculateFrustumPlanes` 在 reversed-Z 下给出的
        /// 近/远两个平面几乎恒真（深度方向不剔）⇒ 沿光轴远在板外的簇也会被提交。
        /// 而它们**根本写不进阴影图**（光栅化器按投影矩阵的 z 范围整块裁掉），
        /// 提交它们纯属白付顶点+三角形 setup。实测阴影 pass 是 setup bound（atlas 仅 4×512²，
        /// 填充 ≈0.01ms），所以这批"必被裁掉"的簇就是 DrawProcedural 那 1ms 的大头。
        ///
        /// 正确性：只剔「整个簇都在 z 板之外」的，它们对阴影图零贡献 ⇒ 剔掉无任何视觉变化。
        /// 压住跨板的簇（部分在内）一律保留，所以不会缺块。
        ///
        /// 这也正是 Unity 的口径：它按级联包容球（半径 R）剔除，球外的物体本来就不画——
        /// 而球外的物体恰好也在 ±R 的 z 板之外，所以两者等价。
        /// </summary>
        /// <param name="view">光空间的 view 矩阵（z 范围就定义在它的空间里）。</param>
        /// <param name="zMin">该级联在光空间的近端 z（含 pad 后的真实裁剪范围）。</param>
        /// <param name="zMax">该级联在光空间的远端 z。</param>
        public void AddCascade(Matrix4x4 worldToClip, Matrix4x4 view, float zMin, float zMax, bool cullZ)
        {
            if (!built || cascadeCount >= MaxCascades) return;
            int k = cascadeCount++;

            // Hi-Z 遮挡测试要用：clip 矩阵算深度（ndc.z 就是阴影图里存的值），
            // atlas 矩阵算图集 texel 矩形（由 SetCascadeAtlasMatrix 填）。
            m_clipMtx[k] = worldToClip;

            // 视锥平面：用与绘制侧完全一致的 worldToClip 提 6 个世界空间平面。
            // 关键：必须用 GL.GetGPUProjectionMatrix(proj,true)*view（即渲染时实际用的矩阵），
            // 不能用裸 proj*view —— 后者在 D3D reversed-Z 下近/远平面算反 ⇒ 过剔除。
            var planes = GeometryUtility.CalculateFrustumPlanes(worldToClip);
            for (int p = 0; p < 6; p++)
            {
                var pl = planes[p];
                cpuPlanes[k * PlanesPerCascade + p] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }

            // 第 6/7 个平面 = 光轴 z 的两个边界，写成「dot(n,p)+w >= -r 即在内部」的统一形式：
            //   z_view = view.row2 · (p,1)  ⇒  z_view >= zMin  ⟺  dot(row2.xyz, p) + (row2.w - zMin) >= 0
            //                                  z_view <= zMax  ⟺  dot(-row2.xyz, p) + (zMax - row2.w) >= 0
            // cullZ=false（或退化范围）时把这俩写成恒真平面（w=+∞ 的等效：法线 0、w 取大值），
            // 保持 buffer 长度一致、kernel 不必分支。
            if (cullZ && zMax > zMin)
            {
                Vector4 r2 = new Vector4(view.m20, view.m21, view.m22, view.m23);
                cpuPlanes[k * PlanesPerCascade + 6] = new Vector4(r2.x, r2.y, r2.z, r2.w - zMin);
                cpuPlanes[k * PlanesPerCascade + 7] = new Vector4(-r2.x, -r2.y, -r2.z, zMax - r2.w);
            }
            else
            {
                cpuPlanes[k * PlanesPerCascade + 6] = new Vector4(0f, 0f, 0f, 1e9f);
                cpuPlanes[k * PlanesPerCascade + 7] = new Vector4(0f, 0f, 0f, 1e9f);
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
            // Hi-Z：只有「开关打开 + 金字塔已建好（上一帧的阴影图）」才做；否则整体关闭。
            bool hzOk = hiZEnable && m_hizValid && m_hiz0 != null && m_hizAtlasSize > 0;
            cullShader.SetInt("_HiZEnable", hzOk ? 1 : 0);
            cullShader.SetInt("_ZReversed", SystemInfo.usesReversedZBuffer ? 1 : 0);
            cullShader.SetFloat("_HiZBias", 0f);
            cullShader.SetFloat("_HiZRelBias", HiZRelBias);
            cullShader.SetFloat("_HiZMinSize", HiZMinScreenSize);
            cullShader.SetInt("_AtlasW", m_hizAtlasSize);
            cullShader.SetInt("_AtlasH", m_hizAtlasSize);
            cullShader.SetMatrixArray("_ClipMtx", m_clipMtx);
            cullShader.SetMatrixArray("_AtlasMtx", hzOk ? m_atlasMtx : m_atlasMtxZero);

            int kReset = cullShader.FindKernel("Reset");
            int kCull = cullShader.FindKernel("CullAll");
            int kIdx = cullShader.FindKernel("BuildIndices");
            int kArgs = cullShader.FindKernel("BuildArgs");
            int kCopy = cullShader.FindKernel("CopyStatic");

            // _HiZ0..3 是 compute 里的【全局声明】⇒ CullAll 必须绑；没建金字塔时塞 1×1 占位
            // （不绑会报 "Property (_HiZ0) ... is not set"；绑占位时 _HiZEnable=0，kernel 不会去采）。
            var hz0 = hzOk ? m_hiz0 : DummyHiZ();
            var hz1 = (hzOk && m_hiz1 != null) ? m_hiz1 : hz0;
            var hz2 = (hzOk && m_hiz2 != null) ? m_hiz2 : hz0;
            var hz3 = (hzOk && m_hiz3 != null) ? m_hiz3 : hz0;
            cmd.SetComputeTextureParam(cullShader, kCull, "_HiZ0", hz0);
            cmd.SetComputeTextureParam(cullShader, kCull, "_HiZ1", hz1);
            cmd.SetComputeTextureParam(cullShader, kCull, "_HiZ2", hz2);
            cmd.SetComputeTextureParam(cullShader, kCull, "_HiZ3", hz3);
            cmd.SetComputeTextureParam(cullShader, kCull, "_SrcDepth", hz0);
            cullShader.SetInt("_HiZ0SizeX", hz0.width); cullShader.SetInt("_HiZ0SizeY", hz0.height);
            cullShader.SetInt("_HiZ1SizeX", hz1.width); cullShader.SetInt("_HiZ1SizeY", hz1.height);
            cullShader.SetInt("_HiZ2SizeX", hz2.width); cullShader.SetInt("_HiZ2SizeY", hz2.height);
            cullShader.SetInt("_HiZ3SizeX", hz3.width); cullShader.SetInt("_HiZ3SizeY", hz3.height);

            foreach (int k in new[] { kReset, kCull, kIdx, kArgs, kCopy })
            {
                cullShader.SetInt("_CascadeCount", cascadeCount);
                cullShader.SetInt("_ClusterCount", clusterCount);
                cullShader.SetInt("_TrisPerCluster", TrisPerCluster);
                cullShader.SetInt("_DebugForceAll", dbg);
                cullShader.SetInt("_FullSkipPercent", FullSkipPercent);

                cullShader.SetBuffer(k, "_Counter", counterBuf);
                cullShader.SetBuffer(k, "_Args", argsBuf);
                cullShader.SetBuffer(k, "_Visible", visibleBuf);
                cullShader.SetBuffer(k, "_Planes", planesBuf);
                cullShader.SetBuffer(k, "_ClusterBounds", boundsBuf);
                cullShader.SetBuffer(k, "_SlotOf", slotBuf);
            }
            // BuildIndices / CopyStatic 用到这两份（一个读源、一个写目标）
            cullShader.SetBuffer(kIdx, "_CornerGvid", idxBuf);
            cullShader.SetBuffer(kIdx, "_DrawIdx", drawIdxBuf);
            cullShader.SetBuffer(kCopy, "_CornerGvid", idxBuf);
            cullShader.SetBuffer(kCopy, "_DrawIdx", drawIdxBuf);

            // 一维展开的 x 维长度（CopyStatic 与 BuildIndices 共用）
            int totalGroups = Mathf.Max(1,
                Mathf.CeilToInt(clusterCount * (float)VertsPerCluster / ThreadGroupSize));
            int gx = Mathf.Min(65535, totalGroups);
            int gy = Mathf.Max(1, Mathf.CeilToInt(totalGroups / (float)gx));
            cullShader.SetInt("_IdxDimX", gx * ThreadGroupSize);

            // ===== 段 0：静态全量索引副本（只在 Rebuild 后跑一次，之后永不变）=====
            // 有了它，可见率高的级联不用每帧重排索引 —— 等价于「Unity 用 mesh 自带静态索引」。
            if (m_needStaticCopy)
            {
                cmd.BeginSample("CCull.CopyStatic");
                cmd.DispatchCompute(cullShader, kCopy, gx, gy, 1);
                cmd.EndSample("CCull.CopyStatic");
                m_needStaticCopy = false;
            }

            // 逐 dispatch 打 sample：ClusterCull 是 4 个 dispatch 的合计，
            // 不拆开就只能知道总量、不知道谁是主要耗时（BuildIndices 的索引写入 vs CullAll 的原子追加）。
            cmd.BeginSample("CCull.Reset");
            cmd.DispatchCompute(cullShader, kReset, 1, 1, 1);
            cmd.EndSample("CCull.Reset");

            cmd.BeginSample("CCull.CullAll");
            cmd.DispatchCompute(cullShader, kCull, groups, 1, 1);
            cmd.EndSample("CCull.CullAll");

            // ⚠️ BuildArgs 必须在 BuildIndices【之前】：BuildIndices 读 _Args[k*5+2]（本段起点）
            //    判断该级联走静态段（=0，跳过重排）还是压实段（写索引）。
            cmd.BeginSample("CCull.BuildArgs");
            cmd.DispatchCompute(cullShader, kArgs, cGroups, 1, 1);
            cmd.EndSample("CCull.BuildArgs");

            // 索引重排：总簇数 × 384 个角点，每线程一个，内部循环所有级联
            // （_CornerGvid 对 4 个级联是同一个值，只读一次）。
            // ⚠️【试过并回退】dispatch 加 z 维=级联（线程数 ×4、无循环）：ClusterCull 0.490 → 0.879。
            //    4892 万线程的调度开销远超串行循环延迟。保持循环 + 单层 dispatch。
            cmd.BeginSample("CCull.BuildIndices");
            cmd.DispatchCompute(cullShader, kIdx, gx, gy, 1);
            cmd.EndSample("CCull.BuildIndices");
        }

        // ---------------- Hi-Z：灯光空间的遮挡剔除 ----------------

        /// <summary>
        /// 登记某级联的「世界 → 阴影图集 UV」矩阵（= Shadows.cs 里的 dirShadowMatrices[tileIndex]，
        /// 由 ConvertToAtlasMatrix 生成，与采样侧 SAMPLE_TEXTURE2D_SHADOW 口径逐位一致）。
        /// Hi-Z 用它把簇的 8 个角点投到图集上算 texel 矩形。必须在 Cull 之前调用。
        /// </summary>
        public void SetCascadeAtlasMatrix(int cascade, Matrix4x4 atlasMtx)
        {
            if (cascade < 0 || cascade >= MaxCascades) return;
            m_atlasMtx[cascade] = atlasMtx;
        }

        /// <summary>
        /// 用【本帧】画完的阴影图集深度重建遮挡金字塔，供【下一帧】的 CullAll 使用。
        /// 必须在所有级联都画完（ExecuteBuffer 之后）、且图集还没被 ReleaseTemporaryRT 之前调用。
        /// </summary>
        public void BuildHiZ(CommandBuffer cmd, RenderTargetIdentifier srcDepth, int atlasSize)
        {
            if (!built || cullShader == null || !hiZEnable) return;
            if (atlasSize <= 0) return;
            EnsureHiZ(atlasSize);
            if (m_hiz0 == null || m_hiz3 == null) return;

            int k0 = cullShader.FindKernel("BuildOccluder0");
            int k1 = cullShader.FindKernel("BuildOccluder1");
            int k2 = cullShader.FindKernel("BuildOccluder2");
            int k3 = cullShader.FindKernel("BuildOccluder3");

            cullShader.SetInt("_AtlasW", atlasSize);
            cullShader.SetInt("_AtlasH", atlasSize);
            cullShader.SetInt("_HiZ0SizeX", m_hiz0.width); cullShader.SetInt("_HiZ0SizeY", m_hiz0.height);
            cullShader.SetInt("_HiZ1SizeX", m_hiz1.width); cullShader.SetInt("_HiZ1SizeY", m_hiz1.height);
            cullShader.SetInt("_HiZ2SizeX", m_hiz2.width); cullShader.SetInt("_HiZ2SizeY", m_hiz2.height);
            cullShader.SetInt("_HiZ3SizeX", m_hiz3.width); cullShader.SetInt("_HiZ3SizeY", m_hiz3.height);

            // 所有全局纹理在【每个】kernel 上都绑一遍（漏绑会报 "Property (...) is not set"）。
            void Bind(int k)
            {
                cmd.SetComputeTextureParam(cullShader, k, "_SrcDepth", srcDepth);
                cmd.SetComputeTextureParam(cullShader, k, "_HiZ0", m_hiz0);
                cmd.SetComputeTextureParam(cullShader, k, "_HiZ1", m_hiz1);
                cmd.SetComputeTextureParam(cullShader, k, "_HiZ2", m_hiz2);
                cmd.SetComputeTextureParam(cullShader, k, "_HiZ3", m_hiz3);
            }
            void Run(int k, RenderTexture dst)
                => cmd.DispatchCompute(cullShader, k,
                    Mathf.Max(1, Mathf.CeilToInt(dst.width / 8f)),
                    Mathf.Max(1, Mathf.CeilToInt(dst.height / 8f)), 1);

            Bind(k0); Run(k0, m_hiz0);
            Bind(k1); Run(k1, m_hiz1);
            Bind(k2); Run(k2, m_hiz2);
            Bind(k3); Run(k3, m_hiz3);

            m_hizValid = true;      // 从下一帧开始生效（本帧的 Cull 已经跑过了）
        }

        void EnsureHiZ(int atlasSize)
        {
            int w0 = Mathf.Max(1, atlasSize / 2), h0 = w0;
            int w1 = Mathf.Max(1, w0 / 2), h1 = w1;
            int w2 = Mathf.Max(1, w1 / 2), h2 = w2;
            int w3 = Mathf.Max(1, w2 / 2), h3 = w3;
            if (m_hizAtlasSize == atlasSize && m_hiz0 != null && m_hiz3 != null
                && m_hiz3.width == w3) return;

            ReleaseHiZ();
            m_hiz0 = NewHiZRT(w0, h0, "ClusterShadowHiZ0");
            m_hiz1 = NewHiZRT(w1, h1, "ClusterShadowHiZ1");
            m_hiz2 = NewHiZRT(w2, h2, "ClusterShadowHiZ2");
            m_hiz3 = NewHiZRT(w3, h3, "ClusterShadowHiZ3");
            m_hizAtlasSize = atlasSize;
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
            if (m_hiz0 != null) { m_hiz0.Release(); UnityEngine.Object.DestroyImmediate(m_hiz0); m_hiz0 = null; }
            if (m_hiz1 != null) { m_hiz1.Release(); UnityEngine.Object.DestroyImmediate(m_hiz1); m_hiz1 = null; }
            if (m_hiz2 != null) { m_hiz2.Release(); UnityEngine.Object.DestroyImmediate(m_hiz2); m_hiz2 = null; }
            if (m_hiz3 != null) { m_hiz3.Release(); UnityEngine.Object.DestroyImmediate(m_hiz3); m_hiz3 = null; }
            m_hizAtlasSize = 0;
            m_hizValid = false;
        }

        /// <summary>1×1 占位金字塔：只为满足「compute 里全局声明的纹理必须已绑定」。
        /// 绑占位时 _HiZEnable 一定是 0，kernel 不会去采样。</summary>
        RenderTexture DummyHiZ()
        {
            if (m_hizDummy == null) m_hizDummy = NewHiZRT(1, 1, "ClusterShadowHiZDummy");
            return m_hizDummy;
        }

        /// <summary>
        /// 【诊断】异步读回逐级联的可见簇数，算出「同一批簇被画了几遍」的倍数。
        /// 这个倍数就是 _DrawIdx 每帧写入量的直接乘数（写入 = 总角点 × 倍数 × 4B），
        /// 也是阴影 draw 的几何倍数。异步读回不 stall，每 120 帧一次。
        /// </summary>
        public void RequestVisibilityStats()
        {
            if (!debugStats) return;
            if (!built || counterBuf == null || m_statsPending) return;
            if (Time.frameCount % 120 != 0) return;
            m_statsPending = true;
            UnityEngine.Rendering.AsyncGPUReadback.Request(counterBuf, req =>
            {
                m_statsPending = false;
                if (req.hasError || !built) return;
                var data = req.GetData<uint>();
                int n = Mathf.Min(cascadeCount, data == null ? 0 : data.Length);
                if (n <= 0) return;

                long sum = 0;
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < n; k++)
                {
                    sum += data[k];
                    sb.Append($" 级联{k}={data[k]}({100f * data[k] / Mathf.Max(1, clusterCount):F0}%)");
                }
                float mult = sum / (float)Mathf.Max(1, clusterCount);
                double writeMB = clusterCount * (double)VertsPerCluster * 4.0 / (1024 * 1024) * mult;
                // ClusterDebug 会强制全可见（debugForceAll=1）⇒ 此时倍数是假值，必须提示，否则误导。
                string warn = (debugForceAll != 0) ? "  ⚠️debugForceAll=1（强制全可见）⇒ 倍数非真实值" : "";
                Debug.Log($"[Cluster阴影] 可见簇{sb} ⇒ 倍数 {mult:F2}×" +
                          $"（索引每帧写 {writeMB:F0}MB，读同量）{warn}");
            });
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
            m_cpuBounds = null;
            m_needStaticCopy = false;
            ReleaseHiZ();
            if (m_hizDummy != null) { m_hizDummy.Release(); UnityEngine.Object.DestroyImmediate(m_hizDummy); m_hizDummy = null; }
            for (int i = 0; i < MaxCascades; i++) m_padHasKey[i] = false;
            RestoreCutoutMarks();
            built = false;
        }
    }
}
