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
- 每帧 **4 个 compute dispatch** 一次性剔完【所有级联】：
  1. `Reset`：1 个线程组，清零 `_Counter`。
  2. `CullAll`：每簇一线程，循环所有级联做球-平面视锥剔除，可见簇原子追加进 `_Visible` 并写 `_SlotOf`。
  3. `BuildIndices`：每线程一个 (簇, 角点)，只给**可见**簇把全局顶点号铺进索引缓冲 `_DrawIdx`；一维展开，不可见簇靠 `_SlotOf` 早退。
  4. `BuildArgs`：每级联一线程，写 5 个 uint 的**索引** indirect args。
  - ⇒ 4 个级联 = **4 次 dispatch**（与级联数无关），因为级联被压进 `CullAll` / `BuildIndices` 的 shader 内循环里。
- 每级联一次 `DrawProceduralIndirect(indexBuffer, ...)`（共 4 次 draw），4 个级联的视口 + 矩阵 + 间接绘制全部累积进【同一个 CommandBuffer】，循环外只 `ExecuteBuffer()` 一次 → 材质 `PrepareKeywords` 从 4× 降到 1×。
- **cutout 分流**：`ClusterRenderDataAsset.NeedsSRPShadow = cutoff || clipping` 的物体在 `ScanScene` 里被 `continue` 跳过（不烘焙进 Cluster，这是性能收益来源），并由 `ClusterObject.OnEnable` 给 Renderer 打 `renderingLayerMask` bit29（CutoffRenderingLayerBit）；`Shadows.cs` 在 cluster 绘制之后临时把灯光掩码切成 bit29 单独跑一次 `context.DrawShadows`。无 cutout 物体时 `HasCutoffCasters()` 为 false ⇒ 整段跳过、零开销。

### 3. 数据布局（静态场景，世界空间摊平）
| 缓冲 | 内容 |
|------|------|
| `_Positions` | float3 世界空间顶点，**按簇去重**（每簇只存实际用到的约 66 个唯一顶点，不按 384 展开） |
| `_CornerGvid` | `[簇 × 384]` 角点 → 全局顶点号，**重建期烘死、静态** |
| `_SlotOf` | `[级联 × 簇]` 本帧可见槽位，不可见写 `0xFFFFFFFF`（`BuildIndices` 靠它早退） |
| `_DrawIdx` | `GraphicsBuffer(Target.Index \| Target.Raw)`，每帧只给可见簇重排索引 |
| `argsBuf` | **索引** indirect args（5 个 uint），必须 `ComputeBufferType.IndirectArguments`（否则运行报 "missing IndirectArguments flag"） |

- **为什么要真索引**：静态单位索引缓冲 `[0,1,2,...]` 每个索引只用一次 ⇒ GPU 顶点缓存**零复用**，一个 128 三角形的簇唯一顶点只有约 66 个却要跑 384 次 VS（约 5.8 倍浪费）。索引绘制下 VS 次数回落到唯一顶点数，与 SRP 同量级。
- VS 只剩一行：`pos = _Positions[SV_VertexID]`（索引值就是全局顶点号），无查表、无间接依赖。
- `Target.Index | Target.Raw`：D3D11 不允许索引缓冲同时是 Structured，但允许同时是 Raw ⇒ compute 当 `RWByteAddressBuffer` 写、draw 当索引读，**单缓冲零拷贝**。
- ⚠️ `BuildIndices` 的 dispatch 必须按【可见簇的实际量】开，别按上界开方阵：`(组数 × 组内簇数上限 × 384)` 会到千万级线程，实测把省下的 draw 全吃回去。也注意 D3D11 单维最多 65535 个线程组，需二维拆分。
- **阴影用「合并 submesh」的烘焙**（`ClusterMeshCache.Get(mesh, splitBySubmesh:false)`）：
  阴影纯几何、不采材质，合并后不会有「每个 submesh 最后一簇补齐到 128 三角形」的填充簇，
  簇数比 GBuffer 那份少 20~40%，而且不生成 normal/tangent/uv。

### 4. 矩阵与剔除口径（正确性关键）
- 自定义 HLSLPROGRAM 在 `DrawProceduralIndirect` 下**不走 `SetViewProjectionMatrices`**，内建矩阵 `UNITY_MATRIX_M / VP` 恒为单位/零矩阵 ⇒ 几何以世界坐标当裁剪坐标，全被裁光 ⇒ 阴影图全空、零报错。
  - 故 `worldToClip` 由 C# 经 **`MaterialPropertyBlock` 逐 draw** 传自定义属性 `_ClusterShadowW2C`（`drawMat.SetMatrix` / `SetGlobalMatrix` 会因 4 级联共享而全拿到最后一次值，必须避免）。
- `worldToClip = GL.GetGPUProjectionMatrix(proj, true) * view`（与 SRP `TransformWorldToHClip` 逐位一致）。**裸 `proj*view` 在 D3D reversed-Z 下 NDC z 大量越界 ⇒ 阴影图全空、零报错**。
- 剔除口径：每级联用与绘制**同一矩阵**提 6 个世界空间平面（`GeometryUtility.CalculateFrustumPlanes(worldToClip)`），对簇世界空间包围球做球-平面测试。
  - ❌ 裸 `proj*view` 提平面：D3D reversed-Z 下近/远平面算反 ⇒ 过剔除（全图无阴影）。
  - ❌ `ShadowSplitData.cullingSphere` 球-球测试：该球是级联**内含球**、比真实视锥小 ⇒ 漏四角/边缘簇（大面积缺阴影）。
  - ✅ 与渲染同一矩阵提的平面：口径完全一致、不漏不重。
- **沿光轴的深度范围必须自建（2026-10-28 定案，别再踩）**：
  Unity 的级联正交矩阵只按 `splitData.cullingSphere` 拟合，沿光轴厚度 ≈ 2R，**它不看场景里有什么**。
  我们的投射物是自建簇集合 ⇒ 若不撑厚度，x/y 在盒内但沿光轴超出这层"板"的簇会被光栅化器**整块裁掉**，
  而它落下的阴影在盒内 ⇒ **阴影凭空缺一块**。症状：**贴近才缺、拉远就好、太阳没动**（级联越小越严重）。
  - Unity 自己是靠「剔除体积 ≡ 投影体积（同一个球）」天然自洽，外加 `shadowNearPlane` 给近侧加余量。
  - 我们做的是 x/y 盒子剔除（对平行光精确、比球更宽）⇒ 必须自己把 z 厚度撑到能覆盖全部簇。
  - 实现：`ClusterShadowRenderer.PadOrthoDepth(proj, pad)`（`m22`/`m23` 同除 pad ⇒ 中心不变、范围 ×pad），
    pad 由「簇集合整体包围球沿光轴的展开量 / 级联球半径」推出，上限 64。
    替换点在 `Shadows.cs` 拿到 `projectionMatrix` 后立刻做 ⇒ 图集采样矩阵 / 渲染矩阵 / `SetViewProjectionMatrices` 三处天然同源。
  - 代价只有深度精度（D32 撑到几百米仍是亚毫米级），阴影偏置是世界空间、投影前施加的，与 z 范围无关 ⇒ 不会引入 acne。

### 5. 关键文件
| 模块 | 路径 | 职责 |
|------|------|------|
| 渲染器 | `Runtime/ClusterRender/ClusterShadowRenderer.cs` | 场景登记、`Rebuild` 几何摊平、4 dispatch 剔除 + 索引重排、逐级联索引间接绘制 |
| 剔除 compute | `Resources/Cluster/ClusterShadowCull.compute` | `Reset` / `CullAll` / `BuildIndices` / `BuildArgs` 四 kernel |
| 阴影 shader | `Resources/Cluster/ClusterShadow.shader` | VS 只做 `_Positions[SV_VertexID]`，只写深度（不采任何贴图） |
| 调度接入 | `Runtime/Core/Shadows.cs` | 按 `clusterMode` 分派；Cluster 模式累积绘制到单 CommandBuffer |

### 6. 调试开关
**资产上只剩一个开关**（其余排查用字段已全部删除）：

- `clusterMode`：0/1/2 切换三条路径（见第 1 节）。`ClusterDebug(2)` = 同 Cluster 但强制不做剔除，
  用于隔离「剔除错」vs「绘制链坏」。切模式必须在进 Play **之前**设好 —— rendering layer 位是在 `ClusterObject.OnEnable` 里打的。

排查时若还需要更细的数据，改代码加临时采样即可，不要往资产上重加字段（历史上这一区一度堆了 7 个开关，全是排查期遗留）。
- `Rebuild` 启动日志会打出「扫描 N 个 MeshRenderer → 登记 M 个 mesh（跳过：disabled / 关投影 / cutout / 无 Mesh）」。
  阴影缺块的**第一嫌疑**永远是「投射物压根没登记」，先看这行。

### 7. 如何测试 / 排错
1. `assemble.asset` 把 `clusterMode` 设 1（Cluster）或 2（ClusterDebug），进 Play。
2. 预期：方向光阴影正常、级联过渡无镂空缺口；与 SRP 模式（=0）同屏对比应一致（草地等 cutout 走 SRP 补画）。
3. **整张阴影图空、零报错** → 先查 `worldToClip` 是否套了 `GetGPUProjectionMatrix`；再查 `argsBuf` 是否 `IndirectArguments` 类型、以及 `_DrawIdx` 是否真的被 `BuildIndices` 写过（索引全 0 ⇒ 全画到同一个顶点 ⇒ 片状/空）。
4. **某个物体/某段阴影凭空缺一块，且「贴近才缺、拉远就好」** → 是沿光轴深度范围没覆盖，见第 4 节最后一条；不是剔除、也不是 `Cull Back`（剔除模式只和光方向有关，与相机距离无关）。
5. **镜像实例（负缩放）整块没阴影/没模型** → 烘焙期绕序翻转是否生效（`det < 0` 时索引写 `i0,i2,i1`）。Unity 内建渲染器会自动翻 culling，`DrawProceduralIndirect` 不会。
6. **大面积缺阴影** → 查剔除口径（是否误用 `proj*view` 提平面，或用了 `cullingSphere` 球-球测试而非世界空间 6 平面）。
7. **位置探针全绿但阴影仍空** → 典型是矩阵 / 间接绘制链路问题（见第 4 节），不是剔除本身。

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

- 顶点来源不同：没有硬件顶点缓冲，几何靠**索引间接绘制** —— compute 每帧只给可见簇铺索引，
  VS 用 `SV_VertexID` 直接取世界空间顶点（变换在重建期烘死），只剩 2 次 `float4` 读取 + 一次八面体解码。
- 片元**完全共用**：`ClusterInput.hlsl`（取数/解码）+ `ClusterPass.hlsl` 的 `ClusterGBufferShade`
  + `FeedbackPass.hlsl` 的 `FeedbackColor`。做法是在 `ClusterGBufferInput.hlsl` 里把 `INPUT_PROP`
  重定向到 `_ClusterMaterials[_ClusterMatIndex]`，并用 `CLUSTER_INSTANCED_MATERIAL` 屏蔽
  `ClusterDOTSMaterial.hlsl` 的 `UnityPerMaterial`。
- 簇号逐片元变化，所以由 VS 经 `nointerpolation` varying 传到片元，片元开头写进
  `static uint _ClusterMatIndex`（HLSL 的全局 static 是每线程局部量，不是 uniform）。

### 3. 数据布局

| 缓冲 | 内容 |
|------|------|
| `_Vertices` | 每顶点 **2 个 float4 = 32B**：`e0=(posWS.xyz, matIndex\|tanSign<<31)`、`e1=(nrmOct, tanOct, uv.xy)` |
| `_CornerGvid` | `[簇 × 384]` 角点 → 全局顶点号（重建期烘死） |
| `_Visible` | `uint2`：`[组基址 + slot] = (簇下标, 材质槽位)` —— 材质槽位随可见列表一起写出，省掉查 `_ClusterInfo` 那一级 |
| `_ClusterGroup` | 每簇所属 detail 贴图组 |
| `_DrawBase` | `[组]` 该组在 `_DrawIdx` 里的起点（前缀和，常量 ⇒ `startIndex` 不用回读计数器） |
| `_SlotOf` | `[簇]` 本帧可见槽位，不可见 `0xFFFFFFFF` |
| `_DrawIdx` | `GraphicsBuffer(Target.Index \| Target.Raw)`，compute 写、draw 直接当索引读 |
| `_ClusterMaterials` | 每材质 112 B 参数 |
| `m_argsBuf` | 每组 5 个 uint（**索引**间接绘制），必须 `IndirectArguments` 类型 |

- **顶点按「每簇（每个摆放的每个簇）」去重后烘成世界空间**，不是按唯一 mesh 一份：
  索引绘制要求「同一顶点用同一个索引值」才能命中 GPU 顶点缓存，而不同摆放的同名顶点变换不同 ⇒ 只能按摆放展开。
  展开后每簇只存去重后的约 66 个顶点（不是 384 个角点），总量与「按唯一 mesh 一份」同量级。
- 既然展开了就把世界变换 / 法线逆转置 / Gram-Schmidt 一起烘死，VS 里零矩阵运算。
- 法线/切线走**八面体编码**（16 bit/轴），实测重构角度误差 0.0073°；副切线符号塞进 matIndex 最高位（matIndex < 2^23）。
- ❌ 顶点池 key 掺材质下标会让 N 个 submesh 的 mesh 被整份复制 N 遍（实测 316→297fps）。
- ❌ 按「每摆放」整份复制 mesh 顶点会爆显存（直接崩溃）。**只复制该簇去重后的那 60~70 个**。

### 4. 调度（每帧 4 dispatch + 每组 1 draw）

```
DefferPipeline.Setup
  ├─ gbuf.Prepare(camera)      重建（如脏）+ 由 GL.GetGPUProjectionMatrix(proj,true)*view 提 6 平面
  ├─ gbuf.Cull(buffer)         Reset / CullAll / BuildIndices / BuildArgs
  └─ clusterCore.RenderFeedback → gbuf.DrawFeedback   ← 与 GBuffer 共用同一份可见簇列表
DefferPipeline.DrawGBuffer
  ├─ FilteringSettings.renderingLayerMask 清掉位 28（配合下面的「只留 bit28」才真正排掉）
  ├─ context.DrawRenderers(...)   ← 剩余物体（非 RVT / cutout / 透明等）
  └─ gbuf.DrawGBuffer(buffer)     按组索引间接绘制
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
   （必须是 `_Visible[slot].y`，写成簇号会越界读 `_ClusterMaterials` ⇒ 参数全 0 ⇒ 整片黑）。
3. **几何错位/炸开** → 查 `_Vertices` 的打包顺序（C# 填 `Vector4` 的位序 vs shader `asuint` 取值）是否逐字一致。
   位置用 `BitConverter.Int32BitsToSingle` 按位存、`asfloat` 取回；法线/切线是八面体编码，编码器与 `OctDecode` 必须逐位对应。
4. **部分簇不显示** → 把 `clusterMode` 切到 `ClusterDebug`（跳过剔除），能全显示说明剔除平面口径有问题；
   若仍缺，查 `_DrawIdx` 是否被 `BuildIndices` 写过、以及 `_DrawBase` 前缀和。

