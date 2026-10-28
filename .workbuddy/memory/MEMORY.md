# RenderCluster 长期记忆

> 完整历史细节见 `.workbuddy/memory/DETAILS.md`；每日进展见 `YYYY-MM-DD.md`。
> 本文件只放「改代码前必须知道」的部分。

## 状态（2026-10-27）
- 开关：`ClusterAssembleAsset.clusterMode` = SRP / Cluster / ClusterDebug —— **资产上唯一的开关**
  （2026-10-28 起 `clusterGBuffer` 等 8 个排查字段已全删，别再往资产上加）。
  `Cluster` ⇒ 静态物体的 GBuffer / RVT Feedback / 阴影全走簇，动态物体留 SRP。
  **切开关必须 Play 前设**（RLM 位在 OnEnable 打）。
- 实测（Viking Village 固定相机）：纯 SRP 337 / 簇阴影+SRP GBuffer 374 / 全簇 350 fps。
  GPU 3.78ms → **2.94ms**，与 CPU 2.96ms 相等 ⇒ **当前已转为 CPU 瓶颈**，继续压 GPU 不再涨帧。
  要继续提速应打 CPU：Batches 87、`RenderLoop.CleanupNodeQueue` 0.5ms、`ExecuteBuffer` 次数。
- BRG 与旧管线 `MyRenderPipeline/` 已删；`SceneSH9`+`SH9Project` 保留于 `Runtime/Core/SH9/`。
  链路：GraphicsSettings → RenderPipeAsset → DefferPipeline.asset → DefferPipeline。

## 阴影（性能敏感，已定案）
- 三 dispatch：Reset / CullAll / BuildArgs；每级联一次 `DrawProceduralIndirect`，4 次累积后只 `ExecuteBuffer()` 一次。
- 数据：`_Positions` 按簇去重 + `_Indices`（全局顶点号）+ **静态单位索引缓冲**。
  VS：`vid/384 → _Visible[slot] → _Indices → _Positions`。
  ❌ 已试并回滚：非索引每簇 384 全展开（VS×4，1.77ms）；compute 每帧重排索引（BuildIndices 多烧 1.6ms）。
- **`Cull Back`**（曾误写 `Cull Off` ⇒ 光栅化翻倍，是阴影慢的真凶）。
- 阴影用合并 submesh `ClusterMeshCache.Get(mesh,false)`；GBuffer 用 `Get(mesh)`（=splitBySubmesh true）。
- 只画 LOD0（`_CascadeParams` / `_MaxLod` / 小簇剔除分支已于 2026-10-28 删除）；
  `argsBuf` 必须 `ComputeBufferType.IndirectArguments`。

## Cluster GBuffer（2026-10-27 改为索引绘制）
- **索引间接绘制**：compute `BuildIndices` 每帧只给【可见】簇铺索引，`_DrawIdx` 用
  `GraphicsBuffer.Target.Index | Target.Raw`（D3D11 允许 Index+Raw 同时存在）⇒ compute 当
  `RWByteAddressBuffer` 写、draw 直接当 index buffer 读，单缓冲零拷贝。args 每组 5 个 uint，
  `startIndex = _DrawBase[g]`（重建期算好的前缀和）。
  ❌ 别按「全部簇 × 全部组」写索引：会到上百 MB，compute 反烧 1.6ms（阴影那次就是）。
- **顶点按【每簇（每个摆放的每个簇）】去重后烘成世界空间**，约 52B/顶点
  （`_Positions`12 + `_Normals`12 + `_Tangents`16 + `_UVs`8 + `_MatIndex`4）。
  索引值 = 全局顶点号，`_CornerGvid[簇×384+corner]` 重建期烘死；世界变换 / 法线逆转置 /
  Gram-Schmidt / 副切线符号全部烘死 ⇒ VS 只剩 5 次缓冲读取、零矩阵运算。
  - ❌ 顶点池 key 掺材质下标（UV 预乘 ST）⇒ N-submesh 的 mesh 整份复制 N 遍 ⇒ 316→297fps。
  - ❌ 顶点按「整份 mesh × 摆放」烘 64B ⇒ 显存爆炸崩溃。按「簇 × 簇内去重顶点」才是对的。
  - 非索引绘制（vertexCount = 簇数 × 384）每簇白跑 318 次 VS，是慢的真凶。
- 材质参数走 `ClusterMaterialData`(112B)，**C# 与 HLSL 字段顺序逐字一致**；
  只剩 `_DetailMap`/`_DetailNormalMap` 两张贴图 ⇒ **按 detail 贴图对分组**（≤17 组）。
- `#define CLUSTER_INSTANCED_MATERIAL` 与 `INPUT_PROP` 重定向**必须写在 include ClusterInput.hlsl 之前**。
- Feedback 由 Cluster 画（与 GBuffer 同一可见列表，保证「请求的页 = 采样的页」），
  有独立精简 VS；且受异步回读节流，几帧才重绘一次（不是每帧）。
- **SRP 与 Cluster 都画两遍几何（GBuffer + Feedback）**，所以把 Feedback 并进 MRT
  不会缩小两条路径的差距，只在绝对性能上有收益。

## 铁律 / 雷区
- 正交级联的 **x/y 剔除是精确的**（光方向 = 投影方向 ⇒ 盒外投不到盒内）；
  但**沿光轴的深度范围 Unity 只按 cullingSphere 拟合（≈2R），不看场景** ⇒ 自建簇必须自己撑开，
  否则「x/y 在盒内、沿光轴出板」的投射物被光栅化器整块裁掉 ⇒ 盒内阴影凭空缺块。
  症状：**贴近才缺、拉远就好、太阳没动**。已由 `clusterShadowAutoDepth` 自动撑（上限 64 倍）。
- **镜像实例（det<0）**：Unity 内建渲染器会自动翻 culling，`DrawProceduralIndirect` 不会
  ⇒ Cull Back 下镜像物体的几何/阴影会整块消失。已在烘焙期翻绕序（阴影 + GBuffer 都做，
  GBuffer 还要把副切线符号取反）。**任何 Cull Back 的 cluster pass 都必须带这个翻转。**
- 平行光级联的正交剔除是**精确**的：x/y 外的投射物投不到盒内 ⇒ 盒内缺块不是剔除的锅。
  reversed-Z 下 `CalculateFrustumPlanes` 的两个 z 平面恒真（深度方向不剔，只是多画）。
- 自定义 HLSL 经 `DrawProceduralIndirect`：worldToClip 必须自己算 `GL.GetGPUProjectionMatrix(proj,true)*view`
  （漏 ⇒ D3D 上 z 全越界被裁光、阴影全空、**零报错**）。
- 剔除平面用**同一矩阵** `GeometryUtility.CalculateFrustumPlanes(...)` 对簇包围球做球-平面；
  裸 `proj*view` 在 reversed-Z 下近/远算反 ⇒ 过剔除。
- CommandBuffer 内**逐派发变化**的 uniform 必须 `cmd.SetComputeIntParam/SetComputeBufferParam`；
  用 `ComputeShader.SetInt/SetBuffer` 会被最后一次设置覆盖 ⇒ 逐级联数据错位、整片无阴影零报错。
- compute buffer 逐 kernel 绑全（`SetBuffer(kernel,...)`），漏绑静默失效。
- `renderingLayerMask` 是**包含式**：接管物体必须**只保留 bit28**。`ShadowDrawingSettings` **没有** rlm 字段，
  只能用灯光自身的 `Light.renderingLayerMask`（`int`，写 `1<<n`）+ `useRenderingLayerMaskTest`。
- 片元材质下标 = `_Visible[slot].y`（= `_ClusterInfo[簇号].y`），写成簇号 ⇒ 越界读 `_ClusterMaterials` ⇒ 整片黑。
  打位必须在 `Register` 成功之后。
- Tile-VT：`V = CellSize×TilesPerAxis`；mip 别加 `+1.0-speed`；图集超 `maxTextureSize` ⇒ `IsCreated()==false` 黑屏。
- 法线读侧 `DecodeNormal` 后 `normal.y *= -1`（DX vs Unity 切线空间，5 处一致）；UnityPerMaterial 三处逐字副本。
- 2022.3：`DrawProceduralIndirect(indexBuffer,...)` **可用**（args 5 个 uint，索引缓冲需 `Index|Raw`）；
  非索引重载是 4 个 uint。区域版 `AsyncGPUReadback` 无 depth；`SetVertexBufferParams`/带索引 `DrawProcedural` 不可用。

## 工具 / 政策
- 编译验证 `.workbuddy/compile_check.py`（只编 C#；**HLSL 必须进 Unity 验证**）；`.workbuddy/verify_matrix.py`。
- Git 只放代码+配置，资源留本地（无 LFS）；被排除二进制的 `.meta` 必须进 Git；**严禁 `git clean -fdx`**；
  push 需代理 `http://127.0.0.1:7890` 开着。
- 用户极度反感返工：大改前先讲方案与代价；改「疑似数据源」文件前先确认引用。
