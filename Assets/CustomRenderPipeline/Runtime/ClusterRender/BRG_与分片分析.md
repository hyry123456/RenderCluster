# BRG 重写 + GPU min-max-box 剔除 + Mesh→Cluster 分片 分析

> 本文档配合 `BatchRenderManager.cs` 重写、`ClustBase.cs`/`ClusterTools.cs` 修正一起阅读。
> 环境：Unity 2022.3.62f3c1，新版（Entity Graphics 风格）BatchRendererGroup API。

---

## 一、BatchRenderManager 编译错误根因 & 修复

### 1.1 根因
之前那版代码写的是 **旧版 BRG API**，而 2022.3 已经是新版（与 Entity Graphics 同款）。所有报错都源于此：

| 原代码 | 问题 | 2022.3 正确写法 |
|---|---|---|
| `new BatchRendererGroup(OnPerformCulling)` | 缺第 2 个 `IntPtr` 参数 (CS7036) | `new BatchRendererGroup(OnPerformCulling, IntPtr.Zero)` |
| `NativeArray<Metadata>` / `new Metadata(MetadataType.PerInstance, …)` | `Metadata`/`MetadataType` 不存在 (CS0246/CS0103) | `NativeArray<MetadataValue>`，用 `new MetadataValue { NameID, Value }` |
| `new BatchInfo { mesh, material, numInstances, shaderPass, flags }` | `BatchInfo` 不存在 (CS0246) | 删除；batch 由 `mesh`/`material` 的 `Register*` 结果 + `AddBatch` 决定 |
| `brg.AddBatch(metadata, batchInfo, targets)` | 签名不符 (CS0029/CS1503) | `brg.AddBatch(NativeArray<MetadataValue>, GraphicsBuffer.bufferHandle)` |
| `Entry.batchID` 是 `BatchMeshID` | `RemoveBatch` 要 `BatchID` (CS1503) | 应为 `BatchID` |
| `OnPerformCulling(brg, ctx)` 用 `ctx.batchVisibility[b]` / `ctx.visibleIndices` | `BatchVisibility`/`visibleIndices` 不存在 (CS1061) | 新版回调签名是 `(brg, ctx, BatchCullingOutput, IntPtr)`，可见性由你往 `BatchCullingOutput.drawCommands` 里填 draw command |

### 1.2 已落实的修复要点
- 构造函数带 `IntPtr.Zero`。
- `OnPerformCulling(brg, ctx, cullingOutput, IntPtr)`，用 `UnsafeUtility.Malloc(..., Allocator.TempJob)` 分配
  `drawCommands / drawRanges / visibleInstances`（Unity 渲染后自动释放，**不要**手动 Free）。
- `AddBatch` 传 `NativeArray<MetadataValue>` + `instanceBuffer.bufferHandle`。
- **matrix 必须用 PackedMatrix（float3x4）打包**，不能直接写 `Matrix4x4`：
  shader 端的 `unity_ObjectToWorld` 是按 `float3x4` 读的，直接塞 `Matrix4x4`（64 字节）会被错位读成
  `m30/m31/m32` 当成平移分量 → 物体跑到错误位置。官方 SimpleBRGExample 就是这么打包的，已照抄。
- `ClusterPBR.shader` 已声明 `#pragma multi_compile _ DOTS_INSTANCING_ON`，**所以 BRG 路线是通的**；
  每实例矩阵经 metadata 以 `0x80000000 | byteAddress` 标记「每实例数组」，shader 通过
  `DOTS_INSTANCING` 路径从 `unity_DOTSInstanceData` 取到正确的 `unity_ObjectToWorld` / `unity_WorldToObject`。
- 修了 submesh：原来每 (物体, submesh) 注册时**没记录 submeshIndex**，BRG 只会画 submesh 0。
  现在 `Register(owner, mesh, mat, submeshIndex, shaderPass)`，`ClusterObject` 调用处补上传入 `i`。

### 1.3 当前 Step1 的「真·视锥裁剪」
`OnPerformCulling` 现在不再「全部可见」，而是拿 `cullingContext.cullingPlanes`（世界空间视锥面）
对每个物体的世界 AABB（由 `mesh.bounds` 经世界矩阵变换得到）做 **box-vs-frustum** 测试，
不可见的 batch 直接不生成 draw command。这已经是「min-max box 剔除」的雏形（见第二节）。

**已知限制（Step1 脚手架，非最终）**：
- 一个物体 = 一条 batch、`numInstances=1`。BRG 的优势（多实例合一、一次 draw call 多物体）没用上。
  这是为了「附加式、零回归、先跑通」故意留的；Step2 才会下沉到 cluster 级别。
- 多物体若共享同一 mesh+material+submesh，目前仍是 N 条 batch；理想是合并成 1 条 batch 的 N 实例。
- 编辑器勾 `assemble.asset → useBRG` 进 Play 即可对比：画面应与关闭时无差异（叠加绘制），
  出异常关掉 `useBRG` 立刻恢复。

---

## 二、GPU min-max-box 剔除：现状 & BRG 该怎么接

### 2.1 现状：它是「休眠」的，不是「报错」的
- `ClusterAssembleAsset.cs:48` 的 `public ComputeShader CullingCompute;` **整行被注释掉了**。
- 全工程搜不到任何「对象级 / cluster 级」GPU 剔除的 compute dispatch；
  现有 `PlaneCompute.compute` 的 `FrustumCull` 是地形 patch 用，`ClusterLight.compute` 的
  tile-frustum 是光照 tile 用，都**不是**你说的「用 GPU 对 min-max box 做物体剔除」。
- 也就是说：**当前运行时走的是 Unity 内建的 `context.DrawRenderers` CPU 剔除**（`ClusterCore.cs:640`），
  `ClustersData` 里每 cluster 的 `boundsMin/boundsMax`（即 min-max box，由 `GetClusterMinMaxs()` 产出）
  **目前完全没有被运行时消费**——它只是被 `SaveMesh` 烘焙进 `*.cluster` 文本文件，然后就没人读了。

所以你看到的「Culling 出错」，更可能是 **编辑器烘焙阶段 / 历史遗留代码** 的问题，而不是线上渲染路径。
线上根本没在跑你那个 GPU min-max-box 剔除。

### 2.2 BRG 该不该复用它？—— 该，而且「min-max box」的概念本就该落在 BRG 上
把思路对齐一下：你那个 min-max box 剔除的本质是 **「对每个包围盒做视锥测试，只有可见的才画」**。
这件事在 2022.3 里**最自然的落点就是 `BatchRendererGroup.OnPerformCulling`**：

> BRG 每帧调用一次 `OnPerformCulling`，让你决定哪些实例可见、生成哪些 draw command。
> 这就是「用 min-max box 做剔除」的官方回调。CPU 上做就是 box-vs-frustum（已写在 Step1）；
> 要做成你想要的「GPU 比 CPU 快很多」，就把「每个 box × 每个视锥面」的测试丢进一个 compute，
> 结果写回一个 `GraphicsBuffer`，`OnPerformCulling` 里读这个 buffer 来填 `visibleInstances`。

**正确的演进路线（建议）**：
1. **Step1（已做）**：物体级 box-vs-frustum，CPU，写在 `OnPerformCulling`。验证 BRG 能正确画、能正确剔除。
2. **Step2（推荐下一步）**：把「物体」下沉为「cluster」。
   - 一条 BRG batch = 一个 mesh；**一个 BRG draw command = 一个可见 cluster**，
     用 `BatchDrawCommand.firstIndex / indexCount` 指向该 mesh 索引缓冲里的那个 cluster 区间。
   - cluster 的 min-max box（烘焙好的 `boundsMin/boundsMax`）作为剔除输入，
     测试逻辑与 Step1 完全一样（box-vs-frustum），只是粒度从「整个物体」变「物体内的 cluster」。
   - 这才是 RVT 分片真正的收益点：一个大楼被视锥切掉一半时，只剩可见的那几个 cluster 被提交。
3. **Step3（性能）**：把 Step2 的「每 cluster box × 视锥面」测试搬到 compute（即你原来的 GPU min-max-box 思路），
   compute 输出可见性位图，`OnPerformCulling` 读位图生成 draw command。
   这样「GPU 剔除」和 BRG **是同一个系统**，而不是两套并行、互不相干的实现。

> 一句话：别再把它当成一个独立的「GPU CullingCompute」挂件去接 BRG；**它就是 BRG 的 `OnPerformCulling`
> 在 cluster 粒度上的实现**，先 CPU 跑通，再上 compute 加速。

---

## 三、Mesh → Cluster 分片：到底该怎么做，以及现有代码的 Bug

### 3.1 正确的分片流程（meshlet / cluster 通用范式）
「分片」= 把一张大 mesh 切成很多 **固定预算的小三角形组（cluster / meshlet）**，每组带一个包围盒，
作为「剔除 + LOD + 合批」的最小单元。标准做法：

1. **取三角形**：`mesh.triangles`（索引网格）是全局顶点索引数组；按 submesh 的 `indexStart/indexCount` 切出本 submesh 的三角形。
2. **分组（核心）**：把三角形聚成每组约 16 个 quad（32 三角）的 cluster，且**同一 cluster 内三角形要空间相邻**。
   - *简单模式*：按三角形出现顺序每 16 个 quad 切一刀。快、不需要邻接信息，但 cluster 可能横跨不相干几何 → 剔除粒度差。
   - *优质模式*：建「三角形邻接图」（共顶点/共边即相邻），从种子三角形**区域生长（region growing）**，
     贪心地吞并相邻且共享顶点的三角形，直到达到预算（16 quad）。这样 cluster 内部空间连贯，剔除/合批收益最大。
   - 业界（Nanite 风格）还会额外约束「cluster 内唯一顶点数 ≤ 顶点预算」，否则一个 cluster 跨太多顶点会爆索引缓冲。
3. **算包围盒（min-max box）**：用该 cluster 所有三角形的**真实顶点位置**算出 `boundsMin/boundsMax`。
   这正是你 `ClusterData.boundsMin/Max` 的用途，也是上面 GPU 剔除的输入。
4. **存储**：每个 cluster 存「三角形索引列表（或 局部索引+顶点重映射）」+ 包围盒。
   运行时渲染 = 用**原始 mesh 的顶点缓冲** + cluster 的索引区间去 draw（所以 cluster 本质是「索引子集」，
   不需要复制顶点）。

### 3.2 现有 `ClusterTools.cs` 流程复盘（它大体走对了，但有真 bug）
链路：`ClusterControl.SaveMesh`（仅 `#if UNITY_EDITOR`）→ 取 `mesh.triangles` + `mesh.vertices`，
按 submesh 调 `ClusterTools.CreateClusterData` → 三角形 → 配对成 quad → 聚成 cluster → 写 `*.cluster`。

逐段结论：
- ✅ **submesh 切割是对的**：`subMeshRange[i] = (GetSubMesh(i).indexStart, indexCount)`，
  三角形是全局索引、`points = mesh.vertices` 也是全局顶点，索引对得上。
- ⚠️ **配对成 quad 用位置距离判断相邻 —— 这是真 bug**（已修，见 3.3）。
- ⚠️ **`CreateClustersByQuard`（优质模式）是 O(n²) 生长**，大数据网格会很慢；且跑在后台 `Task` 上，
  没有进度/取消机制。功能正确但性能差，大 mesh 慎用。
- ⚠️ **cluster 不足 16 quad 时，用「复制最后一个 quad」补齐**（`CreateCluster` 的 `for` 填充）。
  这会让补齐部分的三角形变成「重复索引的退化三角」（第二个三角形=第一个自己），
  几何上多画了无用面、顶点数虚高，但不崩。属于「凑数」写法，建议改成「重复最后一个**真实** quad」或留空。
- ⚠️ **`CreateCluster` 在 `quardrangles` 为空时会 `quardrangles[-1]` 越界崩**（已加防御，见 3.3）。
  当前调用方不会传空，但属于定时炸弹。
- ❌ **更大的架构问题**：分片产出的 `*.cluster`（含 min-max box）**没有运行时消费者**。
  运行时仍然是 `DrawRenderers` + Unity CPU 剔除，cluster 数据白烤了。要让分片「有用」，
  必须接第二节的 Step2（BRG 按 cluster 出 draw command）。否则分片流程再对，画面也不会变。
- ⚠️ **`CreateClustersData` 文本解析器只读了 clusters**，把 `vecs/normals/uvs` 解析代码注释掉了。
  这其实没问题——cluster 引用的是原始 mesh 的全局顶点索引，运行时直接拿原始 mesh 顶点缓冲画即可，
  不需要在 cluster 文件里再存一份顶点。

### 3.3 本次已修的两处
1. **`ClustBase.Triangle.IsBorderUpon`**：原先用 `Vector3.Distance(p1,p2) < 0.1f` 判断两三角形是否共边。
   对**索引网格**这是错的——同一坐标可能是不同索引（UV 接缝、硬边），而距离阈值 0.1 又随模型尺度失效。
   改成**直接比顶点索引**：共享 ≥2 个索引 = 共边（相邻）。这正是原作者自己注释掉的那行意图。
2. **`ClusterTools.CreateCluster` 空列表防御**：空列表直接返回空 cluster，避免越界。

---

## 四、建议的下一步（按优先级）
1. 编辑器勾 `useBRG` 进 Play，确认 BRG 叠加绘制与关闭时画面一致（验证 Step1）。
2. 把分片产物接进运行时：实现 **Step2（cluster 级 BRG draw command + 用烘焙好的 min-max box 做 box-vs-frustum）**，
   这是「分片真正起作用」的唯一方式，也顺带实现了你要的 min-max-box 剔除。
3. 性能 OK 后，把 Step2 的剔除测试搬到 compute（即你原本的 GPU min-max-box 思路），`OnPerformCulling` 读结果。
4. （可选）把 `CreateClustersByQuard` 的区域生长改成「预算内贪心 + 顶点预算约束」，并对齐 Nanite 式 meshlet 规范。
