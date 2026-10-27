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

> 整条管线的绘制路径由资产 `clusterMode` **唯一开关**切换：**SRP(0)** / **Cluster(1)** / **ClusterDebug(2)**。
> 不存在「GBuffer 走 Cluster、阴影走 SRP」这种混搭：切到 Cluster 就是 GBuffer + Feedback + 阴影全走簇。
> 当前场景的 alpha 镂空投射物（草 / 蕨 / 灌木）不烘焙进 Cluster，由 `Shadows.cs` 在 cluster 绘制之后用 SRP 路径单独补画（见下「cutout 分流」）。

### 1. 三条路径的分工
| 模式 | 走什么 | 说明 |
|------|--------|------|
| SRP(0) | `context.DrawShadows` | Unity 原生 ShadowCaster，逐物体剔除 + SRP Batcher。 |
| Cluster(1) | 只跑 `cluster.Draw` | GPU 逐簇剔除 + 每级联一次 `DrawProceduralIndirect`；不跑 `context.DrawShadows`（alpha 镂空的 cutout 物体除外，见下）。 |
| ClusterDebug(2) | 同 Cluster + 强制全可见 | 绘制路径与 Cluster 完全相同，只是 compute 跳过剔除（`debugForceAll=1`）。<br>判据：Debug 画得出来而 Cluster 画不出来 ⇒ 剔除口径/包围球问题；两者都空 ⇒ 绘制链问题。 |

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
| `_Positions` | float3 世界空间顶点，**按「簇 × 384（角点序）」平铺**（同簇内共用顶点会重复存） |
| `_Visible` | compute 写出的可见簇紧凑列表 `[cascade * 簇数 + slot] = 簇下标` |
| `argsBuf` | **非索引** indirect args（4 个 uint），必须 `ComputeBufferType.IndirectArguments`（否则运行报 "missing IndirectArguments flag"） |

- **没有索引缓冲、没有 `_Indices`**：非索引绘制的 `vertexCount = 可见簇数 × 384` 本身就给出 `SV_VertexID` 范围；
  VS 只需 `_Visible`（小缓冲、同簇 384 个顶点共用）+ 一次顺序的 `_Positions[簇号*384+corner]`。
- 为什么删掉「静态单位索引缓冲」：单位缓冲每个索引只用一次，GPU 的后变换顶点缓存**零复用**，
  却还要按 4B/顶点 反复读一遍索引 —— 实测这是阴影 pass 的大头之一（1.72ms → 明显下降）。
- ⚠️ 坑：**D3D11 下 Index 缓冲不能当 compute UAV 写**（会静默丢弃），这也是当初绕成「单位索引缓冲」的原因；
  既然现在改成非索引绘制，这条限制就彻底绕开了。
- **阴影用「合并 submesh」的烘焙**（`ClusterMeshCache.Get(mesh, splitBySubmesh:false)`）：
  阴影纯几何、不采材质，合并后不会有「每个 submesh 最后一簇补齐到 128 三角形」的填充簇，
  簇数比 GBuffer 那份少 20~40%，而且不生成 normal/tangent/uv。
- 可选加速：`clusterShadowMinTexels`（默认 0 = 关）。簇投影到阴影图上的半径小于该 texel 数就跳过 ——
  这类簇投出来只有一两个像素、再经 PCF 基本不可见，但顶点开销照付。

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
| 调度接入 | `Runtime/Core/Shadows.cs` | 按 `clusterMode` 分派；Cluster 模式累积绘制到单 CommandBuffer |

### 6. 调试开关
- `ClusterAssembleAsset.clusterForceAll`（bool）：true ⇒ GBuffer 与阴影的 compute 都跳过剔除、强制全可见，用于隔离「剔除错」vs「绘制链坏」（`clusterMode = ClusterDebug` 时自动等效开启）。
- `clusterMode`：0/1/2 切换三条路径（见第 1 节）。切模式必须在进 Play **之前**设好 —— rendering layer 位是在 `ClusterObject.OnEnable` 里打的。

### 7. 如何测试 / 排错
1. `assemble.asset` 把 `clusterMode` 设 1（Cluster）或 2（ClusterDebug），进 Play。
2. 预期：方向光阴影正常、级联过渡无镂空缺口；与 SRP 模式（=0）同屏对比应一致（草地等 cutout 走 SRP 补画）。
3. **整张阴影图空、零报错** → 先查 `worldToClip` 是否套了 `GetGPUProjectionMatrix`；再查 `argsBuf` 是否 `IndirectArguments` 类型、以及 `_Positions` 是否按「簇 × 384 角点序」平铺（旧的按簇去重布局会让 VS 取到错位顶点）。
4. **大面积缺阴影** → 查剔除口径（是否误用 `proj*view` 提平面，或用了 `cullingSphere` 球-球测试而非世界空间 6 平面）。
5. **位置探针全绿但阴影仍空** → 典型是矩阵 / 间接绘制链路问题（见第 4 节），不是剔除本身。

> 后续若对 Cluster 阴影的剔除口径、数据布局或调度方式做修改，请同步更新本小节。

---

## Cluster 画 GBuffer（把整个不透明场景交给簇绘制）

开关：和阴影共用同一个 `ClusterAssembleAsset.clusterMode`（`SRP` 时完全不参与）。
`clusterMode` 为 Cluster / ClusterDebug 时**只对 Game 摄像机**生效，
Scene 视图始终走 SRP，天然可作为 A/B 对照。

### 1. 为什么能合并成很少几次 draw

| 输入 | 归属 | 合并方式 |
|------|------|----------|
| albedo / normal / mask / emission | RVT 图集（3 张） | 全局同一张图，零绑定量 |
| `_MapId` / `_MainTex_ST` / `_DetailMap_ST` / `_Color` / `_EmissionColor` / 粗糙度金属度 / detail 三强度 / `_UseMask` 等 | 每材质 | **每簇常量缓冲**（112 B/簇） |
| `_DetailMap` / `_DetailNormalMap` | 每材质的两张贴图 | **按贴图对分组**，一组一次间接绘制（本场景 ≤17 组） |

> 注意：`_CLIPPING` 不参与（cutout 物体不进 Cluster，见下），所以不需要「分两轮 draw」。

### 2. 与 SRP 路径共用同一份着色代码

- 顶点来源不同：没有顶点缓冲，VS 用 `SV_VertexID` 推 `slot = vid/384`、`corner = vid%384`，
  查 `_Visible → _ClusterInfo → _Indices → _Positions/Normals/Tangents/UVs` 还原几何。
- 片元**完全共用**：`ClusterInput.hlsl`（取数/解码）+ `ClusterPass.hlsl` 的 `ClusterGBufferShade`
  + `FeedbackPass.hlsl` 的 `FeedbackColor`。做法是在 `ClusterGBufferInput.hlsl` 里把 `INPUT_PROP`
  重定向到 `_ClusterMaterials[_ClusterMatIndex]`，并用 `CLUSTER_INSTANCED_MATERIAL` 屏蔽
  `ClusterDOTSMaterial.hlsl` 的 `UnityPerMaterial`。
- 簇号逐片元变化，所以由 VS 经 `nointerpolation` varying 传到片元，片元开头写进
  `static uint _ClusterMatIndex`（HLSL 的全局 static 是每线程局部量，不是 uniform）。

### 3. 数据布局

| 缓冲 | 内容 |
|------|------|
| `_Positions/_Normals/_Tangents/_UVs` | **按唯一 mesh 上传一份**（mesh 局部空间），同 mesh 多实例共享 |
| `_Indices` | int32 全局顶点号，每簇连续 384 |
| `_ClusterInfo` | `uint4`：x=索引段起点，y=材质槽位 |
| `_ClusterToWorld` / `_ClusterNormal` | 每簇 **4 个 float4 行**（不用 `float4x4`：矩阵在 StructuredBuffer 里的行/列主序取决于编译器打包约定，存行 + `dot` 手工乘没有歧义） |
| `_ClusterMaterials` | 每材质 112 B 参数 |
| `_Visible` | `[组 * 簇数 + slot] = 簇下标`（compute 产出的紧凑列表） |
| `_ClusterGroup` | 每簇所属 detail 贴图组 |
| `m_argsBuf` | 每组 4 个 uint（**非索引**间接绘制），必须 `IndirectArguments` 类型 |

- 顶点在**局部空间**、每簇带变换矩阵：比「CPU 逐顶点变换到世界空间」省一次 4M 顶点的重算，
  也让同一 mesh 的多个实例共享顶点缓冲。
- 索引绘制不需要 GPU 索引缓冲：非索引 `DrawProceduralIndirect` 的 `vertexCount = 可见簇数 × 384`
  已经给出 `SV_VertexID` 范围，shader 自己查 `_Indices` 即可。

### 4. 调度（每帧 3 dispatch + 每组 1 draw）

```
DefferPipeline.Setup
  ├─ gbuf.Prepare(camera)      重建（如脏）+ 由 GL.GetGPUProjectionMatrix(proj,true)*view 提 6 平面
  ├─ gbuf.Cull(buffer)         Reset / CullAll / BuildArgs
  └─ clusterCore.RenderFeedback → gbuf.DrawFeedback   ← 与 GBuffer 共用同一份可见簇列表
DefferPipeline.DrawGBuffer
  ├─ FilteringSettings.renderingLayerMask 清掉位 28（配合下面的「只留 bit28」才真正排掉）
  ├─ context.DrawRenderers(...)   ← 剩余物体（非 RVT / cutout / 透明等）
  └─ gbuf.DrawGBuffer(buffer)     按组间接绘制
```

- **Feedback 也交给 Cluster 画**：用同一份可见簇列表，天然保证「请求的页 = 采样的页」
  （两套几何各画一遍才会分叉）。
- **不 Disable MeshRenderer**：SRP 阴影那条路还要靠它投阴影；只用 rendering layer 位 28 把它们
  从 GBuffer pass 剔出去。位 29 仍是 cutout 阴影专用。
- ⚠️ **rendering layer mask 是「包含式」的**：判定是 `(renderer.rlm & filter) != 0`。
  所以只把 bit28 从 filter 里清掉**排不掉**这些物体（它们还有 bit0 能通过）——
  必须让被接管的物体 **只保留 bit28**（丢掉原掩码），filter 再清掉 bit28，两边才都不匹配。
  cutout 相反：它要留在 SRP 阴影里，所以是「原掩码 | bit29」。
- cutout 物体（草/蕨/灌木）**不进 Cluster**（`_CLIPPING` 需要采样贴图，Cluster 分两轮不划算），
  继续走 `DefferStandard` 直采。

### 5. 关键文件

| 模块 | 路径 |
|------|------|
| 渲染器 | `Runtime/ClusterRender/ClusterGBufferRenderer.cs` |
| 剔除 compute | `Resources/Cluster/ClusterGBufferCull.compute` |
| 绘制 shader | `Renderer/DefferPipeline/Cluster/ClusterGBuffer.shader`（pass0 GBuffer / pass1 Feedback） |
| 簇实例化取数 | `Renderer/DefferPipeline/Cluster/Input/ClusterGBufferInput.hlsl` |
| 顶点/片元入口 | `Renderer/DefferPipeline/Cluster/Input/ClusterGBufferPass.hlsl` |
| 烘焙缓存（阴影共用） | `Runtime/ClusterRender/ClusterMeshCache.cs` |

### 6. 已知限制 / 待办

- **只支持静态物体**：世界矩阵在 `ClusterObject.OnEnable` 抓一次。
  `ClusterObject.Update` 每帧比一次 Transform，一旦真的移动/旋转（水车、门、机关）就自动
  `Unregister` + 还原掩码，交回 SRP —— 动态物体不进簇，也不需要重建整份簇数据。
- **只画 LOD0**：以后接多级 LOD + GPU 选 LOD（判据与阴影同一套 `targetTexels` 思路）。
- 顶点属性显存约 200 MB（4.15M 顶点 × 48 B），后续可压到 ~28 B（八面体法线 / half2 UV）。
- 反射 `unity_SpecCube0` 走全局默认值（手动 draw 不经过 `PerObjectData.ReflectionProbes`），
  场景里若有独立反射探针可能与原路径略有差异。

### 7. 排错

1. **物体整块消失** → 看 `ClusterGBufferRenderer.IsReady` 是否为 false（图集未绑 / 未建 / 组数为 0）；
   确认 `ClusterObject` 确实注册了（`clusterMode` 是 Play 前设的）。
2. **画面全白/全黑** → 先查两件事：① `_VirtualAlbedo` 等有没有绑上（`ClusterObject.ApplyAtlasTextures`
   → `ClusterGBufferRenderer.BindAtlas`）；② 片元里的材质下标是不是错写成了**簇号**
   （必须是 `_ClusterInfo[簇号].y`，写成簇号会越界读 `_ClusterMaterials` ⇒ 参数全 0 ⇒ 整片黑）。
3. **几何错位/炸开** → 查 `_ClusterToWorld` 行序（C# `Matrix4x4.GetRow`）与 shader `dot` 顺序是否一致。
4. **部分簇不显示** → 先开 `debugForceAll = 1`（跳过剔除），能全显示说明剔除平面口径有问题。

