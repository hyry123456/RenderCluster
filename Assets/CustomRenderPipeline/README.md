# CustomRenderPipeline 项目说明

> 这是一个 **自定义延迟渲染管线（Custom Deferred Render Pipeline）**，当前开发重点为 **RVT（Runtime Virtual Texturing，运行时虚拟贴图）**。
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
- **四处公式必须逐字一致**：`FeedbackPass.hlsl`（请求）、`ClusterPBR_BRG.shader`（GBuffer + 自带 Feedback 两个 Pass）、
  `ClusterInput.hlsl`（GetColor）、`RVTFeedbackCompact.compute`（解码 pageId）。
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

### BRG 迁移（进行中）
- **Step 0（已完成）**：SRP Batcher 已开；`csc.rsp(-unsafe)` 已加；`ClusterPBR.shader` 四个 pass 注入 `DOTS_INSTANCING_ON`（`#pragma target 4.5`）；`ClusterInput.hlsl` 改双路径（`UNITY_DOTS_INSTANCED_PROP_WITH_DEFAULT` 回退材质值）；`INPUT_PROP(type,name)` 宏 12 处调用补类型。
- **Step 1（已实现，待 Unity 验证）**：新增 `Runtime/ClusterRender/BatchRenderManager.cs`，在 `ClusterAssembleAsset` 加 `useBRG` 开关（默认 **false**，零回归）。
  - 集成方式为**「附加式」**：`useBRG` 开启时，`ClusterObject` 仍走原 `DrawRenderers` 路径，并额外把自身（按 submesh）注册进 BRG；`DefferPipeline.DrawGBuffer` 在 GBuffer MRT 仍绑定时多 `Submit` 一次让 BRG 再画一遍 GBuffer。
  - 这样即便 BRG 的提交时机/裁剪有误，原路径仍作画面兜底，便于对比排查。
  - **批次合并（已实现）**：`BatchRenderManager` 以 `(mesh, 材质, submesh)` 为 key 合并——同 key 的物体收进同一条「多实例」batch（每个实例一份 `unity_ObjectToWorld`/`unity_WorldToObject`，按 2 的次幂容量增长重建）；`OnPerformCulling` 对每条 batch 只发 **1 个 draw command**（逐实例做 box-vs-frustum，可见下标进 `visibleInstances`）。`ClusterObject` 侧用 `GetOrCreateBRGMaterial` 按**源材质缓存复用**同一个 BRG 材质实例，这是合并生效的前提（否则每个物体一份克隆材质 → 又退化成「一物体一批」）。
    - ⚠️ 合并粒度天然受 mesh/材质限制：不同 mesh 或不同材质的物体**不会**合并（一次 draw 只能一个 mesh，材质属性是 per-batch 非 per-instance），故「全场景一个批」不可能，只能「同 mesh+同材质」合并。
    - 变换矩阵经 `unity_ObjectToWorld` / `unity_WorldToObject` metadata 传入（首部 64 字节零矩阵 + 32 字节对齐留白，o2w 基址 96、w2o 基址 96+48*capacity，与官方 SimpleBRGExample 一致）。
    - `_MapId` 等其余材质属性走 `UnityPerMaterial` cbuffer 全局值（BRG 未注册它们的 per-instance metadata，高位置 0 → 回退材质值）；裁剪回调已做真实视锥剔除以避免多实例下整批漏裁。
  - **验证步骤**：编辑器里把 `assemble.asset` 的 `useBRG` 勾上 → 进 Play。预期：画面与关闭时无差异（叠加绘制）；若 BRG 路径异常（黑块/错位），关掉 `useBRG` 即恢复，并把现象反馈。
- **Step 2+（待定）**：改为「替换式」+ 真·多实例合并（per-instance `_MapId` 的 metadata 布局）、视锥裁剪、动画物体支持。

### 已知限制 / 待办
- 已支持 Tile 粒度虚拟贴图（`useTileVT`），单页 = 一张贴图的一个 tile；页池容量（`CellCount`）不随 T 放大，
  故 T 越大越依赖流式换页（mip0 页数有限，远景自然落到低 mip）。
- 后处理、粒子、水面、地形等已归档，未参与当前 RVT 测试。

> 后续若对 RVT 流程、调参或模块结构做修改，请同步更新本文件的「RVT」小节。
