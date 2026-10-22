using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// BRG（BatchRendererGroup）管理器 —— RVT 的 Step 1 冒泡实现（已对齐 Unity 2022.3 新版 BRG API）。
    ///
    /// 设计原则（附加式，零回归）：
    ///   useBRG 开启时，场景里的 ClusterObject 在原有 DrawRenderers 路径之外，
    ///   额外通过 BRG 再绘制一次 GBuffer（shader 的 GBuffer pass）。
    ///   原 DrawRenderers 路径保留作兜底，便于对比排查。
    ///
    /// 【本版核心改造】从「一物体一 batch」改为「一 (mesh, 材质, submesh) 一 batch，多实例」：
    ///   上一版每个 (物体, submesh) 都 AddBatch 一次、numInstances = 1，
    ///   于是 N 个物体 = N 条 batch = N 个 draw command = Frame Debugger 里 N 个
    ///   "Hybrid Batch Group"。本版把 key 相同的物体收进同一条 batch：
    ///     · 同一条 batch 里放多个实例（每个实例一份 unity_ObjectToWorld / WorldToObject）；
    ///     · OnPerformCulling 对每条 batch 只发 1 个 draw command，
    ///       visibleCount = 该 batch 中通过视锥测试的实例数，
    ///       visibleInstances[] 里依次列出这些实例在 batch 内的下标；
    ///     · 于是「同 mesh + 同材质」的物体合并成 1 次 draw call。
    ///   ⚠️ 合并粒度天然被 mesh 与材质限制：不同 mesh 无法合并（一次 draw 只能一个 mesh）；
    ///      不同材质的物体也不会合并（材质属性是 per-batch 的，不是 per-instance 的）。
    ///      所以要让合并真正生效，ClusterObject 侧必须复用同一个 BRG 材质实例
    ///      （见 ClusterObject.GetOrCreateBRGMaterial 的材质缓存），否则每个物体一份克隆材质
    ///      → 每个物体依然自成一组、依然是一个 draw。
    ///
    /// 每实例数据缓冲布局（与 Unity 官方 SimpleBRGExample 逐字一致，勿擅改）：
    ///   Offset                      | 内容
    ///   0                           | 64 字节零矩阵（使 address 0 的加载返回零，作合理默认值）
    ///   64                          | 32 字节留白（仅为让 SetData 起点能对齐到 48 的倍数）
    ///   96                          | unity_ObjectToWorld，capacity 个 packed float3x4（步长 48）
    ///   96 + 48 * capacity          | unity_WorldToObject，capacity 个 packed float3x4
    ///   注意：
    ///    · 每实例步长固定 48 字节 —— Unity 按 shader 里声明的类型（float3x4）计算
    ///      「实例索引 * 48」，所以两个矩阵区必须各占 capacity*48 字节、不能交错，
    ///      于是 WorldToObject 的基址 = 96 + 48 * capacity（首版常量 144 只是 capacity == 1 的特例）。
    ///    · metadata 在 AddBatch 之后【不可修改】，而 WorldToObject 基址依赖 capacity，
    ///      所以容量变化时必须 RemoveBatch + AddBatch 重建（见 GrowBatch），
    ///      容量按 2 的幂次增长，故一个 batch 一生最多重建 log2(N) 次。
    ///    · 必须走 PackedMatrix 打包，直接写 Matrix4x4 会被 shader 当成 float3x4 错位读取
    ///      （m30/m31/m32 被当成平移分量）。
    ///    · SetData 的 graphicsBufferStartIndex 以【源数组元素大小】为单位（见
    ///      GraphicsBuffer.SetData -> InternalSetData(..., Marshal.SizeOf(elementType))），
    ///      所以索引 = 字节地址 / 48，这也是 ObjectToWorld 从 96 而非 128 开始的原因
    ///      （128 不能被 48 整除）。
    /// </summary>
    public class BatchRenderManager
    {
        /// <summary>ClusterPBR.shader 的 Pass：0=Feedback, 1=OutGBuffer, 2=ShadowCaster, 3=WriteDeep</summary>
        public const int FeedbackPass = 0;
        public const int GBufferPass = 1;

        public static BatchRenderManager Instance { get; private set; }

        /// <summary>当前是否还有任何 batch（供管线判断是否需要对 BRG 单独提交）。
        /// 当簇渲染作为主渲染器、同类物体不再注册进 BRG 时，此值为 false，可跳过 BRG 提交，
        /// 避免空批次触发 "BatchRendererGroup deleted Batch ID set size inconsistent"。</summary>
        public bool HasBatches => batches.Count > 0;

        public static void EnsureCreated()
        {
            if (Instance == null)
                Instance = new BatchRenderManager();
        }

        private BatchRendererGroup brg;

        // PackedMatrix：把 Matrix4x4 打包成 shader 期望的 float3x4（列主序前 3 行）。
        // 顺序与官方 SimpleBRGExample 完全一致，不能改。
        private struct PackedMatrix
        {
            public float c0x, c0y, c0z;
            public float c1x, c1y, c1z;
            public float c2x, c2y, c2z;
            public float c3x, c3y, c3z;

            public PackedMatrix(Matrix4x4 m)
            {
                c0x = m.m00; c0y = m.m10; c0z = m.m20;
                c1x = m.m01; c1y = m.m11; c1z = m.m21;
                c2x = m.m02; c2y = m.m12; c2z = m.m22;
                c3x = m.m03; c3y = m.m13; c3z = m.m23;
            }
        }

        // 与官方示例一致的字节布局常量（Raw buffer 以 int 为单位分配）。
        private const int kSizeOfMatrix = sizeof(float) * 4 * 4;            // 64
        private const int kSizeOfPackedMatrix = sizeof(float) * 4 * 3;      // 48
        private const int kBytesPerInstance = kSizeOfPackedMatrix * 2;      // o2w + w2o = 96
        private const int kExtraBytes = kSizeOfMatrix * 2;                  // 128（首部零矩阵 + 对齐留白）
        private const uint kByteAddressObjectToWorld = kSizeOfPackedMatrix * 2;  // 96

        // 单条 batch 的初始实例容量。按 2 的幂次增长（GrowBatch），
        // 增长才需要重建 batch，故取个不太小的初值以减少启动期的重建次数。
        private const int kInitialCapacity = 16;

        /// <summary>batch 内一个实例（= 场景里一个 ClusterObject 的某个 submesh 的一次绘制）。</summary>
        private class BatchInstance
        {
            public ClusterObject owner;
            public Mesh meshOfOwner;            // 缓存 owner 的 mesh（剔除时要用 mesh.bounds）
            public Matrix4x4 worldMatrix;       // 上次记录的世界矩阵，用于跳过无变化的实例
            public Bounds worldBounds;          // mesh.bounds 经 worldMatrix 变换后的世界 AABB（剔除用）
        }

        /// <summary>一条 BRG batch：固定 (mesh, 材质, submesh) + 一组实例。</summary>
        private class Batch
        {
            public Mesh mesh;
            public Material material;
            public int submeshIndex;
            public BatchMeshID meshID;
            public BatchMaterialID matID;
            public BatchID batchID;
            public GraphicsBuffer instanceBuffer;
            public int capacity;
            public readonly List<BatchInstance> instances = new List<BatchInstance>();
        }

        private readonly List<Batch> batches = new List<Batch>();

        // 【已删除】阴影视图的 splitVisibilityMask 逐级联剔除机制（2026-10-21）。
        // 两个理由：
        //   1) BRG 现在完全不投阴影（`Unlit/ClusterPBR_BRG` 已删掉 ShadowCaster pass），
        //      阴影视图的代码路径不可达；
        //   2) 该机制经实测在 2022.3 的 SRP `context.DrawShadows` 下**根本不生效**
        //      （GPU 阴影时间不变，CPU 反而变差）。
        // 将来若要恢复「BRG 自己投阴影」：先恢复 shader 的 ShadowCaster pass，
        // 再从 git 历史取回这套实现。

        // ===== 剔除诊断统计（Editor 排查用）=====
        // 目的：回答「BRG 的剔除到底有没有在工作」——光看画面看不出来。
        //   StatCameraInstances：Camera 视图里通过视锥测试的实例数（应 ≈ 屏幕内实例数）
        //   StatLightInstances  ：Light（阴影）视图里提交的实例数。
        //     故意【不做】逐实例剔除（见 OnPerformCulling 里的说明）⇒ 这个数会等于实例总数，
        //     也就是「每个阴影级联都在画全部实例」，这是阴影 pass 的隐藏成本来源。
        //   StatDrawCommands    ：本帧生成的 draw command 数（= 并批后的 draw call 数）
        public static int StatCameraInstances;
        public static int StatLightInstances;
        public static int StatDrawCommands;
        public static int StatInstanceTotal;
        /// <summary>Camera 视图下「用 Unity 内置 GeometryUtility 测出可见」的实例数。
        /// 与 StatCameraInstances 对照：两者一致 ⇒ 我们的剔除与 Unity 口径相同；
        /// 两者都等于实例总数 ⇒ 相机本来就看得见全部，不是剔除写错。</summary>
        public static int StatUnityTestVisible;
        /// <summary>我们的 IsVisible 与 Unity 内置测试判定不一致的实例数（应恒为 0）。</summary>
        public static int StatCullMismatch;
        /// <summary>Light 视图回调次数 / 其中被「阴影不归 BRG」直接跳过的次数。
        /// 两者相等 ⇒ BRG 对阴影视图零提交（阴影完全归 MeshRenderer）。</summary>
        public static int StatLightViewCalls;
        public static int StatLightViewSkipped;
        private static int s_statFrame;
        private static Plane[] s_unityTestPlanes;

        // (mesh, 材质, submesh) -> Batch。同一批 key 的物体合并进同一条 batch。
        private readonly Dictionary<(Mesh, Material, int), Batch> batchLookup =
            new Dictionary<(Mesh, Material, int), Batch>();

        // mesh / 材质的注册引用计数：不同 batch 可能共用同一 mesh 或同一材质，
        // 必须等最后一个使用者移除后才能 Unregister。
        private readonly Dictionary<Mesh, (BatchMeshID id, int refs)> meshRefs =
            new Dictionary<Mesh, (BatchMeshID id, int refs)>();
        private readonly Dictionary<Material, (BatchMaterialID id, int refs)> matRefs =
            new Dictionary<Material, (BatchMaterialID id, int refs)>();

        private BatchRenderManager()
        {
            // 2022.3：构造函数必须传 (callback, IntPtr userContext)
            brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            // ★ 关键：设置 BRG 组级（group-level）包围盒。
            // Unity 在 OnPerformCulling 之前会用这个全局 bounds 做整组剔除——
            // 包括阴影（光照）视图。若不设置，默认 bounds 可能让远处 / 阴影视图
            // 下整组被剔除，表现为“近处物体有阴影、远处没有”。
            // 这里先用官方 BRGSetup.cs 同款超大 bounds 兜底，后续 Register/UpdateMatrices
            // 会据实例 worldBounds 动态刷新成真实并集。
            UpdateGlobalBounds();
        }

        /// <summary>
        /// 把一个 ClusterObject 的某 submesh 注册进 BRG。
        /// 若已有 (mesh, 材质, submesh) 相同的 batch，则作为新实例并入该 batch（多实例合并绘制），
        /// 否则新建一条 batch。shaderPass 参数保留未用（BRG 的 draw command 只能引用
        /// mesh + material，没有 pass 字段；实际画哪个 pass 由管线的 DrawRenderers tag 决定）。
        /// </summary>
        public void Register(ClusterObject co, Mesh mesh, Material material, int submeshIndex, int shaderPass)
        {
            EnsureCreated();
            if (brg == null || mesh == null || material == null) return;

            var key = (mesh, material, submeshIndex);
            if (!batchLookup.TryGetValue(key, out Batch batch))
            {
                batch = CreateBatch(mesh, material, submeshIndex, kInitialCapacity);
                batchLookup[key] = batch;
                batches.Add(batch);
            }

            var inst = new BatchInstance { owner = co };
            RefreshInstance(inst);
            batch.instances.Add(inst);

            if (batch.instances.Count > batch.capacity)
                GrowBatch(batch, Mathf.NextPowerOfTwo(batch.instances.Count));

            WriteInstanceBuffer(batch);
            UpdateGlobalBounds();
        }

        public void Unregister(ClusterObject co)
        {
            if (brg == null) return;
            for (int b = batches.Count - 1; b >= 0; b--)
            {
                var batch = batches[b];
                bool removed = false;
                for (int i = batch.instances.Count - 1; i >= 0; i--)
                {
                    if (batch.instances[i].owner != co) continue;
                    batch.instances.RemoveAt(i);
                    removed = true;
                }
                if (!removed) continue;

                if (batch.instances.Count == 0)
                {
                    DestroyBatch(batch);
                    batchLookup.Remove((batch.mesh, batch.material, batch.submeshIndex));
                    batches.RemoveAt(b);
                }
            else
            {
                WriteInstanceBuffer(batch);
            }
        }
        UpdateGlobalBounds();
    }

        /// <summary>每帧刷新实例的世界矩阵（物体移动后必须调用）。矩阵未变的实例跳过 SetData。</summary>
        public void UpdateMatrices()
        {
            // Profiler 采样点：BRG.UpdateMatrices
            // 这里量的是 CPU 侧（遍历实例 + 上传 instance buffer），
            // 真正的绘制成本在 GPU，要看 Frame Debugger / RenderDoc 的 Hybrid Batch Group 条目。
            Profiler.BeginSample("BRG.UpdateMatrices");
            int instanceTotal = 0;
            for (int b = 0; b < batches.Count; b++)
                instanceTotal += batches[b].instances.Count;
            StatInstanceTotal = instanceTotal;
            for (int b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                bool anyChanged = false;
                for (int i = 0; i < batch.instances.Count; i++)
                {
                    var inst = batch.instances[i];
                    if (inst.owner == null) continue;
                    var m = inst.owner.transform.localToWorldMatrix;
                    if (m == inst.worldMatrix) continue;
                    inst.worldMatrix = m;
                    if (inst.meshOfOwner != null)
                        inst.worldBounds = TransformBounds(inst.meshOfOwner.bounds, m);
                    anyChanged = true;
                }
                if (anyChanged)
                {
                    WriteInstanceBuffer(batch);
                    UpdateGlobalBounds();
                }
            }
            Profiler.EndSample();
            DumpCullStats();
        }

        /// <summary>BRG 批次自己是否投阴影。
        /// true  = BRG 投（MeshRenderer 关掉）—— 阴影 pass 慢 4 倍（0.96ms → 4.8ms）；
        /// false = 阴影交给 MeshRenderer（BRG 只画 GBuffer）—— 当前默认。
        /// 【唯一真值来源】直接读 ClusterProverty.BRGShadowExcludeLayer，不再另存一个静态开关。
        /// 曾经另存过 s_brgCastsShadows，结果「物体已经被挪到排除 layer（MeshRenderer 打开）」和
        /// 「批次仍 shadowCastingMode=On」能同时成立 ⇒ 阴影被画两遍（4.4ms → 4.96ms），
        /// 而画面完全正常，极难发现。</summary>
        public static bool BRGCastsShadows => ClusterProverty.BRGShadowExcludeLayer < 0;

        /// <summary>切换「阴影归属模式」并同步到所有已注册的 ClusterObject。
        /// excludeLayer &gt;= 0 ⇒ 阴影交给 MeshRenderer（物体挪到该 layer，MeshRenderer 保持开启）；
        /// excludeLayer &lt; 0 ⇒ 旧行为（MeshRenderer 关掉，BRG 投阴影）。
        /// 批次侧的 shadowCastingMode 由 BRGCastsShadows 实时派生，这里只需改 ClusterObject。</summary>
        public void ApplyShadowOnlyMode(int excludeLayer)
        {
            int n = 0;
            for (int b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                for (int i = 0; i < batch.instances.Count; i++)
                {
                    var owner = batch.instances[i].owner;
                    if (owner != null) { owner.ApplyShadowOnlyMode(excludeLayer); n++; }
                }
            }
            s_shadowOnlyInstanceCount = n;
        }

        /// <summary>最近一次 ApplyShadowOnlyMode 处理到的实例数（诊断用）。</summary>
        private static int s_shadowOnlyInstanceCount;
        public static int ShadowOnlyInstanceCount => s_shadowOnlyInstanceCount;

        /// 剔除统计的节流输出（每 300 帧一次）。
        /// 读法：
        ///   StatCameraInstances ≈ 屏幕内实例数 ⇒ 逐实例视锥剔除【在正常工作】；
        ///   StatLightInstances  = 实例总数 × 阴影级联相关视图数 ⇒ 阴影视图按级联球剔，
        ///     若接近实例总数说明级联范围罩住了整个场景（本工程阴影距离 300 > 场景尺度，属正常）；
        ///   StatDrawCommands   = 并批后的 draw call 数（远小于实例数才说明合批生效）。
        ///   StatUnityTestVisible / StatCullMismatch = 与 Unity 内置 GeometryUtility 的对照，
        ///     Mismatch 应恒为 0；两者都等于实例总数 ⇒ 相机本来就看得见全部，不是剔除写错。
        /// </summary>
        private static void DumpCullStats()
        {
            s_statFrame++;
            if (s_statFrame % 300 != 0) return;
            Debug.Log($"[BRG剔除统计] 实例总数={StatInstanceTotal} | Camera视图可见实例={StatCameraInstances}" +
                      $" | Unity内置测试可见={StatUnityTestVisible} | 判定不一致={StatCullMismatch}" +
                      $" | Light(阴影)视图提交实例={StatLightInstances}" +
                      $" | 阴影视图回调={StatLightViewCalls} 其中跳过={StatLightViewSkipped}" +
                      $" | 阴影归属={(BRGCastsShadows ? "BRG" : "MeshRenderer")}" +
                      $" | draw command={StatDrawCommands}" +
                      $" | batch={BatchRenderManager.Instance?.batches.Count ?? 0}");
            StatCameraInstances = 0;
            StatLightInstances = 0;
            StatDrawCommands = 0;
            StatUnityTestVisible = 0;
            StatCullMismatch = 0;
            StatLightViewCalls = 0;
            StatLightViewSkipped = 0;
        }

        // ---------------- 内部实现 ----------------

        private Batch CreateBatch(Mesh mesh, Material material, int submeshIndex, int capacity)
        {
            var batch = new Batch
            {
                mesh = mesh,
                material = material,
                submeshIndex = submeshIndex,
                capacity = Mathf.Max(1, capacity),
            };

            // mesh / 材质 注册（引用计数）
            if (meshRefs.TryGetValue(mesh, out var mr))
            {
                meshRefs[mesh] = (mr.id, mr.refs + 1);
                batch.meshID = mr.id;
            }
            else
            {
                var id = brg.RegisterMesh(mesh);
                meshRefs[mesh] = (id, 1);
                batch.meshID = id;
            }

            if (matRefs.TryGetValue(material, out var tr))
            {
                matRefs[material] = (tr.id, tr.refs + 1);
                batch.matID = tr.id;
            }
            else
            {
                var id = brg.RegisterMaterial(material);
                matRefs[material] = (id, 1);
                batch.matID = id;
            }

            batch.instanceBuffer = AllocateBuffer(batch.capacity);
            batch.batchID = AddBatchInternal(batch, batch.capacity);
            return batch;
        }

        private GraphicsBuffer AllocateBuffer(int capacity)
        {
            int intCount = BufferCountForInstances(kBytesPerInstance, capacity, kExtraBytes);
            return new GraphicsBuffer(GraphicsBuffer.Target.Raw, intCount, sizeof(int));
        }

        /// <summary>
        /// 创建 BRG batch。WorldToObject 基址依赖实例容量（= 96 + 48 * capacity），
        /// 因为 metadata 建立后不可改，容量变化必须走 GrowBatch 重建。
        /// </summary>
        private BatchID AddBatchInternal(Batch batch, int capacity)
        {
            uint byteAddressWorldToObject = kByteAddressObjectToWorld + (uint)(kSizeOfPackedMatrix * capacity);

            // 0x80000000 最高位告诉 shader：这是「每实例数组」，按实例索引寻址。
            var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
            metadata[0] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_ObjectToWorld"),
                Value = 0x80000000 | kByteAddressObjectToWorld,
            };
            metadata[1] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_WorldToObject"),
                Value = 0x80000000 | byteAddressWorldToObject,
            };

            var id = brg.AddBatch(metadata, batch.instanceBuffer.bufferHandle);
            metadata.Dispose();
            return id;
        }

        /// <summary>容量不足时重建 batch（metadata 与缓冲都要按新容量重来）。</summary>
        private void GrowBatch(Batch batch, int newCapacity)
        {
            brg.RemoveBatch(batch.batchID);
            batch.instanceBuffer?.Dispose();

            batch.capacity = newCapacity;
            batch.instanceBuffer = AllocateBuffer(batch.capacity);
            batch.batchID = AddBatchInternal(batch, batch.capacity);
            // 新缓冲内容为空，随后的 WriteInstanceBuffer 会把所有实例全量写入。
        }

        private void DestroyBatch(Batch batch)
        {
            brg.RemoveBatch(batch.batchID);
            batch.instanceBuffer?.Dispose();
            batch.instanceBuffer = null;

            if (meshRefs.TryGetValue(batch.mesh, out var mr))
            {
                if (mr.refs <= 1) { brg.UnregisterMesh(mr.id); meshRefs.Remove(batch.mesh); }
                else meshRefs[batch.mesh] = (mr.id, mr.refs - 1);
            }
            if (matRefs.TryGetValue(batch.material, out var tr))
            {
                if (tr.refs <= 1) { brg.UnregisterMaterial(tr.id); matRefs.Remove(batch.material); }
                else matRefs[batch.material] = (tr.id, tr.refs - 1);
            }
        }

        private static void RefreshInstance(BatchInstance inst)
        {
            if (inst.owner == null) return;
            var m = inst.owner.transform.localToWorldMatrix;
            inst.worldMatrix = m;
            var mf = inst.owner.GetComponent<MeshFilter>();
            inst.meshOfOwner = mf != null ? mf.sharedMesh : null;
            if (inst.meshOfOwner != null)
                inst.worldBounds = TransformBounds(inst.meshOfOwner.bounds, m);
        }

        /// <summary>
        /// 把 batch 内每个实例的矩阵写进 Raw buffer。
        /// 起点索引以 PackedMatrix（48 字节）为单位：o2w 从 96/48 = 2 开始，
        /// w2o 从 (96 + 48*capacity)/48 = 2 + capacity 开始（与 metadata 的基址严格对应）。
        /// </summary>
        private void WriteInstanceBuffer(Batch batch)
        {
            int n = batch.instances.Count;
            if (n == 0) return;

            var zero = new Matrix4x4[1] { Matrix4x4.zero };     // 首部零矩阵：使 address 0 加载返回零
            var objectToWorld = new PackedMatrix[n];
            var worldToObject = new PackedMatrix[n];
            for (int i = 0; i < n; i++)
            {
                var m = batch.instances[i].worldMatrix;
                objectToWorld[i] = new PackedMatrix(m);
                worldToObject[i] = new PackedMatrix(m.inverse);
            }

            int o2wStart = (int)(kByteAddressObjectToWorld / kSizeOfPackedMatrix);            // 2
            int w2oStart = o2wStart + batch.capacity;                                        // 2 + capacity

            batch.instanceBuffer.SetData(zero, 0, 0, 1);
            batch.instanceBuffer.SetData(objectToWorld, 0, o2wStart, n);
            batch.instanceBuffer.SetData(worldToObject, 0, w2oStart, n);
        }

        // BRG 回调：对每条 batch 做逐实例视锥裁剪，为可见实例生成 draw command。
        // 一条 batch 只发一个 draw command（visibleCount = 可见实例数），这就是「合并成一个批」的关键。
        public unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            // Profiler 采样点：BRG.Cull
            // 注意这个回调跑在【渲染工作线程】上，不是主线程 —— 在 Profiler 里要切到
            // "Render Thread"/"Job" 那一行才看得到，别在 Main Thread 里找不到就以为没执行。
            Profiler.BeginSample("BRG.Cull");
            try
            {
            int alignment = UnsafeUtility.AlignOf<long>();
            var drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

            // ★ 「阴影交给 MeshRenderer」模式下，阴影视图【一条 draw command 都不提交】。
            //
            // 【正确性】由 `Unlit/ClusterPBR_BRG` 删掉 ShadowCaster pass 保证 ——
            // Unity 的 DrawShadows 按 LightMode="ShadowCaster" 找 pass，找不到就不画。
            // 之前只设 BatchFilterSettings.shadowCastingMode = Off 是**不够**的：
            // 实测 2022.3 的 SRP context.DrawShadows 仍会为此回调本函数
            // （日志里 Light(阴影)视图提交实例 依旧 ~1155/帧），阴影 pass 照旧 4.3ms。
            //
            // 【这里只是一项 CPU 优化】直接返回空，连下面的逐实例遍历和 UnsafeUtility.Malloc
            // 都省掉 —— 4 个级联 × 1159 实例/帧的纯浪费。
            // 即便没有 shader 那层兜底也不会画错，本段只是为了少干活。
            //
            // 判定用 BRGCastsShadows（唯一真值来源），开关一拨立刻生效，不需要重建 batch。
            //
            // 注：2022.3 的 BatchCullingOutputDrawCommands 没有 drawBatchCount 字段
            //（那是更新版本才有的），所以只清这三个计数 —— 其余字段 Unity 已零初始化。
            if (cullingContext.viewType == BatchCullingViewType.Light)
            {
                StatLightViewCalls++;
                if (!BRGCastsShadows)
                {
                    StatLightViewSkipped++;
                    drawCommands->drawCommandCount = 0;
                    drawCommands->drawRangeCount = 0;
                    drawCommands->visibleInstanceCount = 0;
                    return default;
                }
            }

            var planes = cullingContext.cullingPlanes;
            int commandCount = 0;       // 已生成的 draw command 数
            int visibleCount = 0;       // visibleInstances 数组的已写入长度

            // ★★ 阴影"只用了第一级 / 远处没阴影"的真正根因（2026-10-17）★★
            // 之前对【所有】视图类型都用 cullingContext.cullingPlanes 做逐实例剔除。
            // 但在 Light（阴影视图）下，2022.3 的 cullingContext.cullingPlanes 并不是
            // 该级联阴影视锥的真实平面（通常是主相机视锥平面，或只覆盖首级联），
            // 于是把「主相机视锥之外、但仍落在远处级联阴影视锥内」的投射物整批剔掉，
            // 结果：近处（相机可见范围）有阴影，远处级联的阴影图是空的 → 远处全无阴影，
            // 观感就是"好像只用了第一级级联"。
            // 修复：Camera 视图才用 cullingPlanes；Light 视图【不做逐实例剔除】（全提交）。
            // （2026-10-21 起 BRG 已完全不投阴影，Light 视图在本函数开头就 return 了，
            //  下面这段实质只服务 Camera 视图。）
            bool cullInstances = cullingContext.viewType == BatchCullingViewType.Camera;


            // 对照用：把 NativeArray<Plane> 拷成托管数组，好接 Unity 内置的
            // GeometryUtility.TestPlanesAABB（它只接受 Plane[]）。每帧复用同一个数组，不产生 GC。
            Plane[] unityTestPlanes = null;
            if (cullInstances && CullDiagnostics.Enabled)
            {
                int planeCount = planes.Length;
                if (planeCount > 0)
                {
                    if (s_unityTestPlanes == null || s_unityTestPlanes.Length != planeCount)
                        s_unityTestPlanes = new Plane[planeCount];
                    for (int i = 0; i < planeCount; i++) s_unityTestPlanes[i] = planes[i];
                    unityTestPlanes = s_unityTestPlanes;
                }
            }

            int totalInstances = 0;
            for (int b = 0; b < batches.Count; b++) totalInstances += batches[b].instances.Count;

            int neededCommands = batches.Count;      // 每个 batch 一条 draw command

            // ---------- 分配（Unity 渲染后自动释放，切勿手动 Free）----------
            // ⚠️ 这段必须在逐实例遍历【之前】执行：下面所有 drawCommands->xxx 的写入
            // 都依赖这三块内存。曾因重构被误删 ⇒ visibleInstances 为 null，
            // 第一次写入就 NullReferenceException。
            drawCommands->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawCommand>() * Mathf.Max(1, neededCommands), alignment, Allocator.TempJob);
            drawCommands->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawRange>() * 1, alignment, Allocator.TempJob);
            drawCommands->visibleInstances = (int*)UnsafeUtility.Malloc(
                sizeof(int) * Mathf.Max(1, totalInstances), alignment, Allocator.TempJob);
            drawCommands->drawCommandPickingInstanceIDs = null;
            drawCommands->instanceSortingPositions = null;
            drawCommands->instanceSortingPositionFloatCount = 0;

            {
                for (int b = 0; b < batches.Count; b++)
                {
                    var batch = batches[b];
                    int firstVisible = visibleCount;

                    // 逐实例用物体世界 AABB 做视锥测试，可见的实例下标依次填入 visibleInstances。
                    for (int i = 0; i < batch.instances.Count; i++)
                    {
                        var wb = batch.instances[i].worldBounds;

                        if (cullInstances)
                        {
                            bool ours = IsVisible(planes, wb);
                            // 对照：Unity 内置几何测试（与 CullDiagnostics 场景侧同一套口径）。
                            // 两者一致 ⇒ 我们的剔除与 Unity 同口径；都等于总数 ⇒ 相机本来就看得见全部。
                            if (unityTestPlanes != null)
                            {
                                bool unity = GeometryUtility.TestPlanesAABB(unityTestPlanes, wb);
                                if (unity) StatUnityTestVisible++;
                                if (ours != unity) StatCullMismatch++;
                            }
                            if (!ours) continue;
                        }

                        drawCommands->visibleInstances[visibleCount++] = i;
                    }

                    int visibleInBatch = visibleCount - firstVisible;
                    if (visibleInBatch == 0) continue;

                    //剔除统计：Camera 视图记可见实例，Light 视图记提交的实例
                    if (cullInstances)
                        StatCameraInstances += visibleInBatch;
                    else
                        StatLightInstances += visibleInBatch;

                    drawCommands->drawCommands[commandCount].visibleOffset = (uint)firstVisible;
                    drawCommands->drawCommands[commandCount].visibleCount = (uint)visibleInBatch;
                    drawCommands->drawCommands[commandCount].batchID = batch.batchID;
                    drawCommands->drawCommands[commandCount].materialID = batch.matID;
                    drawCommands->drawCommands[commandCount].meshID = batch.meshID;
                    drawCommands->drawCommands[commandCount].submeshIndex = (ushort)batch.submeshIndex;
                    drawCommands->drawCommands[commandCount].splitVisibilityMask = 0xff;
                    drawCommands->drawCommands[commandCount].flags = 0;
                    drawCommands->drawCommands[commandCount].sortingPosition = 0;
                    commandCount++;
                }
            }

            drawCommands->drawCommandCount = commandCount;
            drawCommands->drawRangeCount = commandCount > 0 ? 1 : 0;
            drawCommands->visibleInstanceCount = visibleCount;
            StatDrawCommands += commandCount;

            if (commandCount > 0)
            {
                drawCommands->drawRanges[0].drawCommandsBegin = 0;
                drawCommands->drawRanges[0].drawCommandsCount = (uint)commandCount;

                // ★★ 阴影修复（BRG 不投影的真正根因）★★
                // filterSettings 是 Unity 决定「这条 draw range 该不该进某个 pass」的开关，
                // 之前只写了 renderingLayerMask，其余字段全取默认值 —— 而
                // BatchFilterSettings.shadowCastingMode 的默认值是 ShadowCastingMode.Off(0)，
                // 于是 BRG 批次被阴影流程（Shadows.cs 的 context.DrawShadows）整体跳过：
                //   · 主画面正常（相机 pass 不看 shadowCastingMode）；
                //   · 阴影图里一个 Hybrid Batch Group 都没有 —— 只有地板等普通 MeshRenderer 的
                //     SRP Batch（Frame Debugger: Shadows 段只有 4 个 SRP Batch，无 Hybrid Batch Group）。
                // 这与 shader 里有没有 ShadowCaster pass 无关：pass 只是"能画"，filterSettings
                // 才决定"要不要画"。Unity 官方 BRG 示例（BRGSetup.cs）同样显式写了这几个字段。
                //
                // 字段含义（与官方示例对齐）：
                //   · shadowCastingMode = On —— 本批参与阴影投射（核心修复项）；
                //   · receiveShadows     = true —— 与本工程 MeshRenderer 的默认行为一致；
                //   · motionMode = ForceNoMotion —— 本工程无 MotionVectors pass 也不需要运动矢量，
                //     显式声明"无运动"可避免 Unity 向该批次索要运动矢量；
                //   · layer —— 单值图层索引，Unity 用【视图自己的 cullingMask】来过滤批次。
                //     这是让「相机看得见、灯光看不见」的唯一手段：
                //       相机 cullingMask 含该层（DefferPipeline 里自动补）；
                //       灯光 cullingMask 去掉该层（Shadows.ReserveDirectionalShadows 里自动去）；
                //     ⇒ BRG 批次进得了主画面，进不了阴影视图（剔除层面直接掉，不依赖
                //        shadowCastingMode / 有没有 ShadowCaster pass）。
                //     旧值 0（Default）时相机与灯光都是 Everything，等于不过滤 —— 这也是
                //     之前"删了 ShadowCaster pass 阴影还是慢 1.6ms"的嫌疑来源。
                //   · staticShadowCaster = false / allDepthSorted = false —— 默认行为。
                drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings
                {
                    layer = (byte)Mathf.Clamp(ClusterProverty.BRGCullLayer, 0, 31),
                    renderingLayerMask = 0xffffffff,
                    // 「阴影交给 MeshRenderer」模式下 BRG 不投阴影（阴影由 MeshRenderer 走
                    // Unity 的 SRP Batcher + per-cascade 剔除路径，快 4 倍）。
                    // 读的唯一真值是 ClusterProverty.BRGShadowExcludeLayer（见 BRGCastsShadows）。
                    shadowCastingMode = BRGCastsShadows
                        ? ShadowCastingMode.On
                        : ShadowCastingMode.Off,
                    receiveShadows = true,
                    motionMode = MotionVectorGenerationMode.ForceNoMotion,
                    staticShadowCaster = false,
                    allDepthSorted = false,
                };
            }

            return default;
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        // box-vs-frustum：被任意裁剪面整体剔除则不可见；只要部分在内即保留（保守，不漏渲染）。
        private static bool IsVisible(NativeArray<Plane> planes, Bounds worldBounds)
        {
            Vector3 c = worldBounds.center, e = worldBounds.extents;
            for (int i = 0; i < planes.Length; i++)
            {
                Plane p = planes[i];
                Vector3 n = p.normal;
                // 沿法线方向半径：AABB 在法线方向上的投影半长
                float r = e.x * Mathf.Abs(n.x) + e.y * Mathf.Abs(n.y) + e.z * Mathf.Abs(n.z);
                // 面方程：dot(n, x) + distance = 0；内法线时内部 s>=0
                float d = n.x * c.x + n.y * c.y + n.z * c.z + p.distance;
                if (d < -r) return false; // 整个盒子在该面外侧 -> 被裁掉
            }
            return true;
        }

        // 把局部 AABB 经世界矩阵变换成世界空间 AABB（保守：旋转后的盒会略大，剔除安全）。
        private static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            Vector3 c = local.center, e = local.extents;
            Vector3[] corner = new Vector3[8];
            int k = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        corner[k++] = new Vector3(c.x + x * e.x, c.y + y * e.y, c.z + z * e.z);
            Bounds b = new Bounds(m.MultiplyPoint(corner[0]), Vector3.zero);
            for (int i = 1; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint(corner[i]));
            return b;
        }

        // 计算整个 BRG 组的世界包围盒（所有实例 worldBounds 的并集），
        // 用来设置 BatchRendererGroup 的组级 bounds，避免 Unity 在阴影 / 远裁剪等
        // 视图下把整组错误剔除（这正是“近处阴影有、远处没有”的头号嫌疑）。
        private Bounds ComputeGlobalBounds()
        {
            if (batches.Count == 0)
            {
                // 没有实例时退回官方 BRGSetup.cs 同款超大 bounds，保证任何视图都不被整组剔除。
                return new Bounds(Vector3.zero, new Vector3(1048576f, 1048576f, 1048576f));
            }
            Bounds b = new Bounds();
            bool initialized = false;
            for (int bi = 0; bi < batches.Count; bi++)
            {
                var batch = batches[bi];
                for (int i = 0; i < batch.instances.Count; i++)
                {
                    var wb = batch.instances[i].worldBounds;
                    if (!initialized)
                    {
                        b = new Bounds(wb.center, wb.size);
                        initialized = true;
                    }
                    else
                    {
                        b.Encapsulate(wb);
                    }
                }
            }
            return b;
        }

        private void UpdateGlobalBounds()
        {
            if (brg != null)
                brg.SetGlobalBounds(ComputeGlobalBounds());
        }

        // Raw buffer 以 int 为单位分配；把字节数向上取整到 int 倍数。
        private static int BufferCountForInstances(int bytesPerInstance, int numInstances, int extraBytes = 0)
        {
            bytesPerInstance = (bytesPerInstance + sizeof(int) - 1) / sizeof(int) * sizeof(int);
            extraBytes = (extraBytes + sizeof(int) - 1) / sizeof(int) * sizeof(int);
            int totalBytes = bytesPerInstance * numInstances + extraBytes;
            return totalBytes / sizeof(int);
        }

        public void Dispose()
        {
            if (brg != null)
            {
                for (int i = batches.Count - 1; i >= 0; i--)
                    DestroyBatch(batches[i]);
                batches.Clear();
                batchLookup.Clear();
                meshRefs.Clear();
                matRefs.Clear();
                brg.Dispose();
                brg = null;
            }
            if (Instance == this) Instance = null;
        }
    }
}
