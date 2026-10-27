# CustomRenderPipeline 项目说明

> 这是一个 **自定义延迟渲染管线（Custom Deferred Render Pipeline）**，当前两大子系统为 **RVT（Runtime Virtual Texturing，运行时虚拟贴图）** 与 **Cluster 阴影（自定义逐簇 GPU 剔除阴影）**。
> 非 RVT 子系统（后处理、粒子、水面、地形、渲染特效等）已归档到项目根目录 `_Disabled/`，
> 归档清单与回退方式见同级 `ARCHIVE_NOTE.md`。

---

## 整体渲染流程

1. **Setup**：管线初始化相机、阴影、光照、天空盒（`GPUDravinSystem`）。
2. **GBuffer 阶段**：`ClusterPBR` 等材质把几何写入 GBuffer（albedo / normal / material 等）。
3. **Feedback 阶段**：Feedback pass 输出每个像素需要的虚拟页（`texId + mip level + 像素覆盖`），作为页请求。
4. **页分配 / LRU**：根据反馈请求从页池（`LevelCache`）分配虚拟页；淘汰采用标准 LRU（use 链表头最旧 / 尾最新）。
5. **GPU 请求压缩**：用 compute shader 把反馈 RT 压缩成固定大小的 flag 数组，回读后在 CPU 端处理页请求（已启用）。
6. **Assemble（拼装）**：把各贴图按页写入虚拟贴图图集（TextureArray / 3D 贴图），供材质采样。
7. **光照**：在 GBuffer 上计算光照。后处理当前已禁用（见归档）。

---

## RVT（运行时虚拟贴图）重点说明

### 核心概念
- 把"一张大贴图"拆成固定大小的"页（Tile / Page）"，按需常驻显存，避免一次性载入整张 2048+ 贴图。
- 页表（TileTable）记录每个 `(texId, mip, tile)` 对应的物理位置；`LevelCache` 在每个 mip 维护 `empty` / `use` 两条链表做 LRU。

### 关键模块 / 文件
| 模块 | 路径 | 职责 |
|------|------|------|
| 核心调度 | `Runtime/ClusterRender/ClusterCore.cs` | 反馈回读、页请求处理、淘汰调度 |
| 页池 | `Runtime/ClusterRender/LevelCache.cs` | 每 mip 级 empty/use 链表、LRU 淘汰 |
| 贴图映射 | `Runtime/ClusterRender/TextureNodesMap.cs` | texId ↔ 虚拟页映射、按贴图分组淘汰 |
| 场景对象 | `Runtime/ClusterRender/ClusterObject.cs` | 每个渲染物体注册自己的虚拟贴图 |
| Shader | `Renderer/Cluster/`、`Renderer/DefferPipeline/Cluster/` | ClusterPBR / ClusterInput / Feedback pass |
| 反馈压缩 | `Runtime/ClusterRender/RVTFeedbackCompact.compute` | GPU 端压缩反馈请求 |
| 配置 | `Resources/Cluster/assemble.asset` | 页大小、mip 层数、采样参数等 |

### 调参（assemble.asset）
- `mipsOffset`：默认 `0.2`（原 `0.74` 会让 1:1 时误用半分辨率 mip，已调小以提清晰度）。
- `FeedbackZoomScale`：默认 `1`（原 `4` 会丢失请求）。
- `CellCount`：默认 `2`（level0 页数量 3→12，缓解缺页）。
- `CellSize`：单页边长。`CellSize↓ + CellCount↑` 可在**图集总像素不变**（显存不变）的前提下把页池拆成更多小页，
  是"页不够"时的正确解法；直接加 `CellCount` 会让图集 X/Y 双轴同时放大 → 显存翻 4 倍。
- `paddingSize`：**开 Tile-VT 时必须 ≥ 1（推荐 2），T=1 老路径下无影响**。
  它负责把每个 tile 的边缘像素外扩到 cell 的边缘带，挡住采样时的双线性串色；
  设 0 会在每个 tile 边界出现一行来自邻块的错误数据，法线表现为网格状"错乱"。
- `useTileVT` / `TilesPerAxis`：Tile 粒度虚拟贴图开关与每轴切分数（T=1 时全部公式退化为旧的整图一格）。

### Tile 粒度虚拟贴图（useTileVT）
- **页 = (texId, mip, tileIndex)**：`pageId = mip * _SumTextureCount + texId * (T*T) + tileIndex`，
  `_SumTextureCount = 贴图数 × T*T`，`tileIndex = tileY*T + tileX`。
- **tile 推导必须先 Wrap 再切**：`tileUV = frac(baseUV) * T`。`baseUV` 经 `_MainTex_ST` 后可能 >1，
  若直接 `*T` 再 `clamp(0,T-1)`，所有重复段都会被压到最后一个 tile（整片拉伸、法线方向全错）。
- **三处公式必须逐字一致**：`FeedbackPass.hlsl`（请求）、`ClusterInput.hlsl`（GetColor 取数）、
  `RVTFeedbackCompact.compute`（解码 pageId）。
  任何一处漏改都会变成"请求的页 ≠ 采样的页"。
  （第五处 `ClusterPBR_Cluster.shader` 已随「簇渲染路径」一起归档到 `_Deprecated/ClusterPath/`。）
- **采集侧 padding 映射**（`RVTShader.shader` Pass3）：正确写法是先剔 padding 再缩放到 tile 子矩形
  `uv = _TileOrigin + (uv - pad)/(1-2pad) * _TileScale`；写成 `(origin + uv*scale - pad)/(1-2pad)`
  只有 T=1 才等价，T>1 会让每个 tile 内容整体偏移约 padding 大小 → 拼不回原图（法线接缝）。
- **改了 CellSize/CellCount/paddingSize/TilesPerAxis 必须重进 Play**：图集与页池只在 `ClusterCore.Initialize` 建一次。

### 如何测试
1. 打开使用 RVT 的场景，确认相机下物体贴图正常、近处清晰、无剧烈跳动。
2. 控制台应无 shader 报错、无 `ObjectDisposedException` / `Leak Detected`。
3. 若近处剧烈跳动或漏页：优先检查 `mipsOffset` / `FeedbackZoomScale` / `CellCount`（见上）。
4. 本管线仅需在 PC 上测试，暂不考虑移动端兼容。

### 渲染路径（已定案：SRP + Cluster）
- 几何**统一由 MeshRenderer 走 SRP**（`context.DrawRenderers(cullingResults)`，逐物体剔除 + SRP Batcher）。
- 原 BRG（BatchRendererGroup）路径已**整体移除**：`BatchRenderManager`、`Unlit/ClusterPBR_BRG`、`CullDiagnostics`
  及相关开关（`useBRG`、`brgGBufferExcludeLayer`、`BRGShadowExcludeLayer`、`BRGCullLayer`）全部删除。
- `GPUDravinSystem` 已简化为「场景光照全局量上传」（`_MySkybox` 天空盒反射、`_Sh9` 间接漫反射），
  不再承担绘制调度（原 DrawNode 链表与各阶段 Draw 方法已删）。

### 已知限制 / 待办
- 已支持 Tile 粒度虚拟贴图（`useTileVT`），单页 = 一张贴图的一个 tile；页池容量（`CellCount`）不随 T 放大，
  故 T 越大越依赖流式换页（mip0 页数有限，远景自然落到低 mip）。
- 后处理、粒子、水面、地形等已归档，未参与当前 RVT 测试。

> 后续若对 RVT 流程、调参或模块结构做修改，请同步更新本文件的「RVT」小节。

---

## Cluster 阴影（自定义逐簇 GPU 剔除阴影）

> 方向光阴影的三条路径由资产 `clusterShadowMode` 切换：**SRP(0)** / **Cluster(1)** / **ClusterDebug(2)**。
> 当前场景的 alpha 镂空投射物（草 / 蕨 / 灌木）不烘焙进 Cluster，由 `Shadows.cs` 在 cluster 绘制之后用 SRP 路径单独补画（见下「cutout 分流」）。

### 1. 三条路径的分工
| 模式 | 走什么 | 说明 |
|------|--------|------|
| SRP(0) | `context.DrawShadows` | Unity 原生 ShadowCaster，逐物体剔除 + SRP Batcher。 |
| Cluster(1) | 只跑 `cluster.Draw` | GPU 逐簇剔除 + 每级联一次 `DrawProceduralIndirect`；不跑 `context.DrawShadows`（alpha 镂空的 cutout 物体除外，见下）。 |
| ClusterDebug(2) | 同 Cluster | 与 Cluster 完全相同的绘制路径（早期版本的 CPU 侧 `VerifyCulling` 已在 debug 清理中移除，当前不做额外 CPU 验证）。 |

### 2. 整体流程（Cluster / ClusterDebug）
- 每帧 **3 个 compute dispatch** 一次性剔完【所有级联】：
  1. `Reset`：1 个线程组，清零 `_Counter`。
  2. `CullAll`：每簇一线程，循环所有级联做球-平面视锥剔除，可见簇原子追加进 `_Visible`。
  3. `BuildArgs`：每级联一线程，写 indirect args。
  - ⇒ 4 个级联 = **3 次 dispatch**（不是 4 次），因为级联被压进 `CullAll` 的 shader 内循环里。
- 每级联一次 `DrawProceduralIndirect`（共 4 次 draw），4 个级联的视口 + 矩阵 + 间接绘制全部累积进【同一个 CommandBuffer】，循环外只 `ExecuteBuffer()` 一次 → 材质 `PrepareKeywords` 从 4× 降到 1×。
- **LOD**：当前只画 LOD0（`_MaxLod=1` 守卫；4 级 LOD 是同一表面的 4 份几何，叠加会 z-fighting）。
- **cutout 分流**：`ClusterRenderDataAsset.NeedsSRPShadow = cutoff || clipping` 的物体在 `ScanScene` 里被 `continue` 跳过（不烘焙进 Cluster，这是性能收益来源），并由 `ClusterObject.OnEnable` 给 Renderer 打 `renderingLayerMask` bit29（CutoffRenderingLayerBit）；`Shadows.cs` 在 cluster 绘制之后临时把灯光掩码切成 bit29 单独跑一次 `context.DrawShadows`。无 cutout 物体时 `HasCutoffCasters()` 为 false ⇒ 整段跳过、零开销。

### 3. 数据布局（静态场景，世界空间摊平）
| 缓冲 | 内容 |
|------|------|
| `_Positions` | float3 世界空间顶点 |
| `_Indices` | int32 全局顶点号，每簇固定 384（= 128 三角形 × 3） |
| `_Visible` | compute 写出的可见簇紧凑列表 `[cascade * 簇数 + slot] = 簇下标` |
| `drawIdxBuf` | **静态单位缓冲** `[0..N]`，创建时写入后永不再写 |
| `argsBuf` | indirect args，必须 `ComputeBufferType.IndirectArguments`（否则运行报 "missing IndirectArguments flag"） |

- 索引缓冲是**静态单位缓冲**：compute 只产出可见簇列表 `_Visible`（RWStructuredBuffer，UAV 可写、无跨缓冲拷贝）；绘制时由 shader 在 VS 里用 `SV_VertexID` 查 `_Visible + _Indices + _Positions` 还原几何。
- ⚠️ 坑：**D3D11 下 Index 缓冲不能当 compute UAV 写**，故绝不能让 compute 直接写本缓冲（会静默丢弃）；这正是改为「compute 只写 `_Visible`、VS 查表」的原因。

### 4. 矩阵与剔除口径（正确性关键）
- 自定义 HLSLPROGRAM 在 `DrawProceduralIndirect` 下**不走 `SetViewProjectionMatrices`**，内建矩阵 `UNITY_MATRIX_M / VP` 恒为单位/零矩阵 ⇒ 几何以世界坐标当裁剪坐标，全被裁光 ⇒ 阴影图全空、零报错。
  - 故 `worldToClip` 由 C# 经 **`MaterialPropertyBlock` 逐 draw** 传自定义属性 `_ClusterShadowW2C`（`drawMat.SetMatrix` / `SetGlobalMatrix` 会因 4 级联共享而全拿到最后一次值，必须避免）。
- `worldToClip = GL.GetGPUProjectionMatrix(proj, true) * view`（与 SRP `TransformWorldToHClip` 逐位一致）。**裸 `proj*view` 在 D3D reversed-Z 下 NDC z 大量越界 ⇒ 阴影图全空、零报错**。
- 剔除口径：每级联用与绘制**同一矩阵**提 6 个世界空间平面（`GeometryUtility.CalculateFrustumPlanes(worldToClip)`），对簇世界空间包围球做球-平面测试。
  - ❌ 裸 `proj*view` 提平面：D3D reversed-Z 下近/远平面算反 ⇒ 过剔除（全图无阴影）。
  - ❌ `ShadowSplitData.cullingSphere` 球-球测试：该球是级联**内含球**、比真实视锥小 ⇒ 漏四角/边缘簇（大面积缺阴影）。
  - ✅ 与渲染同一矩阵提的平面：口径完全一致、不漏不重。

### 5. 关键文件
| 模块 | 路径 | 职责 |
|------|------|------|
| 渲染器 | `Runtime/ClusterRender/ClusterShadowRenderer.cs` | 场景登记、`Rebuild` 几何摊平、3 dispatch 剔除、逐级联间接绘制 |
| 剔除 compute | `Resources/Cluster/ClusterShadowCull.compute` | `Reset` / `CullAll` / `BuildArgs` 三 kernel，逐簇 GPU 剔除 |
| 阴影 shader | `Resources/Cluster/ClusterShadow.shader` | VS 查 `_Visible+_Indices+_Positions` 还原几何，只写深度（不采任何贴图） |
| 调度接入 | `Runtime/Core/Shadows.cs` | 按 `clusterShadowMode` 分派；Cluster 模式累积绘制到单 CommandBuffer |

### 6. 调试开关
- `ClusterAssembleAsset.clusterShadowForceAll`（bool）：true ⇒ compute 跳过剔除、强制全可见（`debugForceAll=1`），用于隔离「剔除错」vs「绘制链坏」。
- `clusterShadowMode`：0/1/2 切换三条路径（见第 1 节）。

### 7. 如何测试 / 排错
1. `assemble.asset` 把 `clusterShadowMode` 设 1（Cluster）或 2（ClusterDebug），进 Play。
2. 预期：方向光阴影正常、级联过渡无镂空缺口；与 SRP 模式（=0）同屏对比应一致（草地等 cutout 走 SRP 补画）。
3. **整张阴影图空、零报错** → 先查 `worldToClip` 是否套了 `GetGPUProjectionMatrix`；再查 `argsBuf` 是否 `IndirectArguments` 类型、`drawIdxBuf` 是否为 `[0..N]` 单位缓冲。
4. **大面积缺阴影** → 查剔除口径（是否误用 `proj*view` 提平面，或用了 `cullingSphere` 球-球测试而非世界空间 6 平面）。
5. **位置探针全绿但阴影仍空** → 典型是矩阵 / 间接绘制链路问题（见第 4 节），不是剔除本身。

> 后续若对 Cluster 阴影的剔除口径、数据布局或调度方式做修改，请同步更新本小节。
