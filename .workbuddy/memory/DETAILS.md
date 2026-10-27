# RenderCluster 项目长期记忆

## 1. Cluster 阴影管线（当前定案）
- 三条路径由资产 **`clusterMode`（唯一开关，2026-10-27 起）** 切换：SRP(0) / Cluster(1) / ClusterDebug(2)。
  切到 Cluster = GBuffer + RVT Feedback + 阴影**全部**走簇；不存在混搭。`clusterGBuffer` 已删除。
  `ClusterDebug` = 同 Cluster 但 compute 跳过剔除（等价 `clusterForceAll=true`）。
  - **SRP**：`context.DrawShadows`（MeshRenderer 走 Unity 原生路径），逐物体剔除 + SRP Batcher。（BRG 已于 2026-10-27 整体移除）
  - **Cluster / ClusterDebug**：**只跑** `cluster.Draw`（GPU 逐簇剔除 + 每级联 `DrawProceduralIndirect`），不跑 `context.DrawShadows`（alpha 镂空的 cutout 物体除外）。4 个级联绘制累积进同一 CommandBuffer，循环外**只 `ExecuteBuffer()` 一次**（PrepareKeywords 4x→1x）。
  - **ClusterDebug**：绘制路径与 Cluster 完全相同（早期 CPU `VerifyCulling()` 已在 debug 清理中移除，当前不做额外验证）。
- GPU 逐簇剔除（`ClusterShadowCull.compute`：Reset/CullAll/BuildArgs 三 kernel，整帧 3 dispatch + 每级联 1 次**非索引** `DrawProceduralIndirect`）。
- **顶点布局（2026-10-27 性能修复）**：`_Positions` 按「簇 × 384 角点序」平铺，VS 只做 `_Positions[簇号*384+corner]` 一次读取；
  **没有索引缓冲、没有 `_Indices`**（非索引绘制的 vertexCount 已给出 SV_VertexID 范围）。曾经用「静态单位索引缓冲」，
  它每个索引只用一次 ⇒ 顶点缓存零复用 + 白白多读 47MB，是阴影 pass 慢的大头。
- **阴影用合并 submesh 的烘焙**（`ClusterMeshCache.Get(mesh, false)`）：阴影不采材质，合并切簇可去掉
  「每个 submesh 最后一簇补齐 128 三角形」的填充簇；GBuffer 那份必须按 submesh 切（`true`）。**只画 LOD0**（`lodCount=1`，`_MaxLod=1` 守卫，`targetTexels<=0` 关闭 LOD 判据）。`clusterForceAll` = GBuffer 与阴影同时强制全画，区分「剔除错」vs「绘制链坏」。
- **是真正的 indirect draw**：`BuildArgs` 写 `_Args[k*4+1]=_Counter[k]`（GPU 可见簇数），`Draw()` 用 `DrawProceduralIndirect(argsBuf, 16*k)`；shader 用 `instanceID` 取 `_Visible[_ClusterBase+instanceID]` 得真实簇号。**`argsBuf` 必须用 `ComputeBufferType.IndirectArguments` 创建**（漏了报 "missing IndirectArguments flag"）。
- **cutout 分流（2026-10-22 定案）**：`ClusterRenderDataAsset.NeedsSRPShadow = cutoff || clipping` ⇒ `ClusterShadowRenderer.ScanScene` 里 `continue` 跳过（**不烘焙进 Cluster**，这是性能收益所在）+ `ClusterObject.OnEnable` 给 Renderer 打 `renderingLayerMask` bit29(`CutoffRenderingLayerBit`) ⇒ `Shadows.cs` 在 cluster 绘制之后，临时把灯光掩码切成 bit29 单独跑一次 `context.DrawShadows`。无 cutout 物体时 `HasCutoffCasters()` 为 false ⇒ 整段跳过、零开销（当前场景草地已 Disable，故该 pass 不执行）。

## 2. 铁律（改代码前必看）
- **阴影矩阵**：`buffer.SetViewProjectionMatrices(view,proj)` 内部已自动 `GL.GetGPUProjectionMatrix`；shader 直接用 `unity_MatrixVP`（= SRP `TransformWorldToHClip`）。**绝不要手推 z 行改写**（曾致阴影全错）。
- **自定义 HLSLPROGRAM 经 `DrawProceduralIndirect` 画阴影时，worldToClip 必须自己套 `GL.GetGPUProjectionMatrix(proj,true)*view`**（2026-10-26 定案）：它不走 `SetViewProjectionMatrices`，少了这一步 ⇒ D3D 上 NDC z 大量越界被裁光 ⇒ 阴影图全空、零报错（位置探针全绿也查不出）。采样侧 `ConvertToAtlasMatrix(proj*view)` 已用 `usesReversedZBuffer` 做等价 z 取反，两侧深度约定一致。
- **`_Indices` 是全局顶点号**：shader 直接 `_Positions[_Indices[c*384+vid]]`，别再加簇基址（历史双重偏移坑）。
- **compute buffer 按 kernel 绑定**：`SetBuffer(kernelIndex,name,buf)`，无 `SetComputeBufferParam`。被多 kernel 共用的 buffer（如 `_Counter`）必须逐 kernel 绑全，漏绑的 dispatch 直接失效（症状：indirect instanceCount=0、无报错）。
- **方向光级联剔除口径（2026-10-26 三改定案）**：最终用 **`GeometryUtility.CalculateFrustumPlanes(worldToClip)` 提 6 个世界空间平面，对簇包围球做球-平面测试**。`worldToClip` **必须** = `GL.GetGPUProjectionMatrix(proj,true)*view`（与 `DrawProceduralIndirect` 绘制侧逐位一致）。
  - ❌ 第一版坑：用裸 `proj*view` 提平面 ⇒ D3D reversed-Z 下近/远平面算反 ⇒ 过剔除到 2~3 簇（全图无阴影）。
  - ❌ 第二版坑：改用 `ShadowSplitData.cullingSphere` 球-球 ⇒ 该球是级联**内含球**、比真实视锥小 ⇒ 漏掉视锥四角/边缘簇 ⇒ 阴影大面积缺失（现象：级联0只留 2 簇、全图无阴影）。且 GPU/CPU 同用这个小圈 ⇒ VerifyCulling 误报"通过"骗过诊断。
  - ✅ 正解：平面来自**与渲染同一矩阵** ⇒ 口径完全一致、不漏不重。`_ClusterBounds` 必须是世界空间包围球。

## 3. 架构 / 渲染
- 渲染 = MeshRenderer 走 SRP（`context.DrawRenderers(cullingResults)`）；逐簇 DrawMesh 已弃用（4FPS，归档 `_Deprecated/ClusterPath/`）。
- 纹理 = Tile-VT（`useTileVT`），与几何正交；T=1 公式退化为「整图一格」。
- 延迟管线 `DefferPipeline`：GBuffer MRT（RT0 albedo+metal…），renderScale=1，MSAA 1x。
- Edit 模式 `ClusterObject.OnEnable` 啥也不做 → 物体保持场景原材质（Edit 清晰/Play 糊分岔点）。Play+UseRVT=0 → `ApplyStandardMaterials()`（已是 DefferStandard 则原样沿用）；UseRVT=1 → `GetOrCreateClusterPBRMaterial` 克隆场景材质并换到 `Unlit/ClusterPBR`，MeshRenderer 直接画 GBuffer + 投阴影。

## 4. BRG 已整体移除（2026-10-27 定案）
- 后续只走 **SRP + Cluster**。BRG 对 134 个组合无收益（RVT+BRG 也打不过全 SRP 档 ~380fps，瓶颈是阴影 pass）。
- 已删文件：`BatchRenderManager.cs`、`Unlit/ClusterPBR_BRG.shader`、`CullDiagnostics.cs`、`GPUDravinBase.cs`、`RenderMethods.cs`、`TextureArrayCreate.cs`。
  - ⚠️ `ShaderStruct.cs` **不能删**：它定义 `uint2/uint3/uint4/float4...`，被 `PlaneTool` 与 `ClusterStruct` 使用。
- 已删开关/字段：`useBRG`、`brgGBufferExcludeLayer`、`BRGShadowExcludeLayer`、`BRGCullLayer`、`normalDebugMode`（含枚举）、`ApplyShadowOnlyMode`。
- `GPUDravinSystem` 精简为「场景光照全局量上传」：只保留 `SetUp`（上传 `_MySkybox` + `_Sh9`）与 `SetSceneLighting`；DrawNode 链表与各阶段 Draw 全删。
- `PlaneTool` 脚本仍挂在 `The_Viking_Village.unity` 场景上，**不能删**；只删了零调用的 `CullingNode`/`CullingShadowNode`。
- 已删：`Assets/BRGTest/`（含 `Test.unity` 里挂的组件块）、`Runtime/ClusterRender/BRG_与分片分析.md`。
- **旧管线 `MyRenderPipeline/` 已整体删除（2026-10-27，用户确认）**，只保留 `DefferPipeline`：
  - 已删：`MyPipeline`、`MyPipelineAsset`、`MyLighting`、`MyShadow`、`MyRenderObject`、`MyMeshRender`、`CreateObject`、`Editor/MyMeshRenderEditor.cs`、`My Pipeline Asset.asset`。
  - **`SceneSH9` + `SH9Project` 必须保留**（两个场景挂载、负责 SH9 间接光烘焙）：已搬到 `Runtime/Core/SH9/`，`.meta` 同步搬迁 ⇒ GUID 不变、场景引用完好。
  - 当前激活链路：`GraphicsSettings` → `RenderPipeAsset.asset` → `DefferPipeline.asset` → `DefferPipeline`；Cluster 灯光裁剪在 `Core/Lighting.cs:ReadyClusterLight` → `Defferrd Render/Core/ClusterLight.cs`。

## 5. Tile-VT 不变式
- 单图虚拟分辨率 `V=CellSize×TilesPerAxis`（非 CellSize）；level0 容量=`CellCount²×mips/T²`。先算容量再调 mipsOffset。
- mip 用 `V`：`uv=baseUV*(_VTFeedbackParam.y*T)`，四处一致。mip 公式 `clamp(speed·log2(max(dot(dx,dx),dot(dy,dy)))+offset,0,mips-1)`，`speed=0.25,offset=0`（别加 `+1.0-speed`）。
- 图集边长 `=(padding·2^(mips-1)·2+CellSize)·CellCount·mips`；超 `maxTextureSize` 时 `Create()` 不抛异常只 `IsCreated()==false` ⇒ 黑屏。当前资产被调小到 CellSize256/T1 很糊，测完调回参考值(512/4/6/4)。

## 6. 雷区集合
- `ClusterPBR*.shader` 内嵌 SampleRVT/GetColor 逐字副本 → 改 pageId 多处同步。
- `ClusterPBR.shader` 必须自带 `LightMode="Feedback"` Pass（RVT 反馈靠它）；`ClusterPBR_BRG.shader` 已随 BRG 删除。
- RVT 反馈只在 `cameraType==Game`；Scene 视图不更新页表。
- `TileTable` 写入属性名带 `_RVT*Src` 前缀（否则 `SetTexture` 静默跳过）。
- 图集 RT `mipCount=0` ⇒ anisoLevel 无效，shader 手动各向异性。
- Unity 2022 全局 keyword 跟随 CommandBuffer：无材质 `buffer.Blit` → `Incompatible keyword states`，改走管线材质 `Draw()`。
- 活动代码=`Core/Shadows.cs`+`Core/Lighting.cs`；`MyRenderPipeline/Lighting/MyShadow.cs` 死代码。
- `ClusterCore.cs` 同时 `using Common;using System;using UnityEngine;` ⇒ 用 `UnityEngine.Object.`。
- `FilteringSettings.layerMask` 是 `int`（按位当 uint 需 `unchecked((int)...)`）；类名 `ClusterProverty`（非 Proverty）；`RenderPipelineBase.cs` 无 `using Cluster` 写全限定名。
- Profiler 用 C# `Profiler.BeginSample`（`using UnityEngine.Profiling`）；`ExecuteBuffer()` 会 `buffer.Clear()`，跨提交 Begin/End 必失衡。
- 2022.3 `AsyncGPUReadback.Request` 区域版无 depth 参数；读 GPU 用 `RenderTexture.active+ReadPixels+GetPixelData<T>(0)`。`SetVertexBufferParams`/带索引 `DrawProcedural` 不可用。
- 法线链路：图集存法线原样，读取侧 `DecodeNormal(map,scale)+normal.y*=-1.0`（DX vs Unity 切线空间）。5 处一致。别动八面体。
- detail 直采不走虚拟化（越页串色）；RVT 材质源 = 克隆的场景原材质（AssetMat 只有精简槽位，没有 `_DetailMap`/`_DetailNormalMap`）。
- mask 白回退：无 mask 图时 mask 恒白（AO=1），移植必须带白回退；判 mask 看 `_MASK_MAP` keyword 真值。
- UnityPerMaterial cbuffer「三处逐字副本」：漏一处 ⇒ cbuffer 不一致 ⇒ pass 不画（阴影缺失但画面正常，易误判）。
- `_CLIPPING`：`_CullOff` 有值≠该裁，看 keyword 真值。
- **`ShadowDrawingSettings` 没有 `renderingLayerMask` 属性**（任何 Unity 版本都没有，别臆造）。2022.3 过滤阴影投射物只能：`useRenderingLayerMaskTest=true` + **灯光自身**的 `Light.renderingLayerMask`，判据 `(Renderer.renderingLayerMask & Light.renderingLayerMask)!=0`。要只画某一批物体 ⇒ 临时把灯光掩码切成目标位，画完还原。
- **`Light.renderingLayerMask` 是 `int`**（`Renderer.renderingLayerMask` 才是 `uint`）⇒ 写 `1 << n`，别写 `1u << n`。
- 诊断：管线资产 Debug Mode（`normalDebugMode` 法线诊断已随 BRG 删除）；两套 Debug 别混（assemble 只管预览槽，画面由 DefferPipeline.asset 决定）；截图对比固定相机+1920×1080+只切 UseRVT。
- **⚠️ CommandBuffer 内逐派发设 compute uniform 必须用 `cmd.SetComputeIntParam`/`SetComputeBufferParam`，别用 `ComputeShader.SetInt/SetBuffer`**：后者改的是着色器对象状态，CommandBuffer 在 `ExecuteBuffer` 时统一重放 ⇒ 同一 buffer 里多个 `DispatchCompute` 全拿到"最后一次设置的值"。典型症状：`Gather` 之类按 `_CascadeIndex` 寻址的核，4 个派发全写到同一段/读同一数据 ⇒ 逐联数据错位、其余段空 ⇒ 整片无阴影且零报错（2026-10-27 真实剔除"全空"根因）。常量(uniform 对所有 dispatch 同值)用 `cullShader.SetInt` 无妨，只有**逐派发变化**的必须走 `cmd.SetCompute*Param`。
- **逐级联数据隔离优先用「每级联独立缓冲」而非「单缓冲内分字节偏移段」**：前者由 C# 按循环变量选缓冲（绝不依赖着色器 uniform），后者一旦 uniform 不捕获就整段错位。Cluster 阴影已改为 `drawIdxBufs[k]`（每级联从偏移 0 写/读，startIndexLocation 恒 0）。

## 7. 用户偏好 / 工具
- 重视先想清楚再动手，反感返工；改"疑似数据源"文件前先确认是否被引用；中文/Windows；不复用旧 cluster 剔除代码。
- **工程已用 Git，政策：仅代码+配置进 Git，数据/资源留本地（不进 Git、不花 LFS）**（2026-10-22 定案）。
  - 根目录 `.git`/`.gitignore`/`.gitattributes`；分支 `main`；远程 `origin` = GitHub 私有仓（网页看提交图/diff/一键 Revert）。
  - `.gitignore` 排除：模型(.fbx/.obj/.blend…)、大贴图(.tif/.hdr/.exr/.tga/.psd/.dds)、音频(.wav/.mp3/.ogg…)、`.unitypackage`；**保留** `.png/.jpg` 小图与全部 YAML 文本(.asset/.prefab/.unity/.meta)。
  - 已**移除 LFS**（数据不进 Git，LFS 无用）；仓库仅 ~94MB、零大文件，直接推 GitHub 免费额度。
  - 推送：`git push`（已全局设 `http.proxy=http://127.0.0.1:7890`，**代理开着即可**；代理关掉则 github 不可达，直连也失败）。⚠️ 代理(Clash 7890)不稳且偏慢——94MB 首次全量推约 5min；**这是一次性成本，之后增量 push 只传改动文件很快**。若代理太慢/连不上，后续可脚本化重试或换出口，再正常 `git push`。
  - ⚠️ Unity 坑：每个被排除二进制的 `.meta`（tiny 文本含 GUID）**必须留在 Git**，只剔二进制本体，否则引用全断。
  - 历史已重写为单一干净初始提交（旧基线含 1.71GB LFS，已 prune）；大资源如需完整备份用 `robocopy Assets D:\备份` 另做，与 Git 无关。
  - **回退边界（重要）**：Git 只保证「进 Git 的内容」（代码/YAML/.meta/小图）可 `checkout`/`revert` 精确回退；被排除的二进制资源（模型/大贴图/音频）只在本地磁盘，**删了/盘坏 Git 救不回**，项目会缺引用跑不起来。`.meta` 在 Git 里 ⇒ 从备份恢复二进制后 Unity 能自动重新接上。**严禁 `git clean -fdx`**（会删掉这些 ignored 二进制）；回退用 `git checkout -- <文件>`/`git restore`。完整容灾=Git(代码+引用) + `robocopy Assets D:\备份 /MIR`(载荷)，两者叠加才完整。
- 编译验证：Unity 编辑器常被占用时，用 `.workbuddy/compile_check.py`（Unity 自带 Roslyn 直编 Assembly-CSharp）；矩阵数值验证 `.workbuddy/verify_matrix.py`（纯 Python）。
- 未来：Hi-Z 层级深度遮挡剔除；逐贴图 `T=ceil(texSize/CellSize)`；occlusion 接入；清理 `_VirtualDetail` 与旧 System B 九图集。

## 8. Cluster 画 GBuffer（2026-10-27 落地，跟随 `clusterMode`）
- 只对 **Game 摄像机 + UseRVT + clusterMode≠SRP** 生效；Scene 视图永远走 SRP（天然 A/B）。
- **两个致命坑（都踩过，症状都是「物体全黑 / 又 SRP 又 Cluster」）**：
  1. 片元的材质下标必须是 `_ClusterInfo[簇号].y`，**写成簇号会越界读 `_ClusterMaterials`**（材质数≈1121、簇≈30665）
     ⇒ 参数全 0 ⇒ 整片黑；VS 的 `TransformBaseUV/TransformDetailUV` 同样要它。
  2. `FilteringSettings.renderingLayerMask` 是**包含式**（`(renderer.rlm & filter) != 0`）：
     只从 filter 里清 bit28 排不掉物体（还有 bit0）。必须让被接管物体 **只保留 bit28**（丢掉原掩码）。
     cutout 相反，仍是 `orig | bit29`（它要留在 SRP 阴影里）。打位必须在 `Register` 成功之后。
- **为什么能合并**：albedo/normal/mask/emission 全在 RVT 图集（全局同图，零绑定）；`_MapId`/`_MainTex_ST`/`_DetailMap_ST`/`_Color`/`_EmissionColor`/粗糙金属/detail 三强度/`_UseMask` 等走**每簇常量缓冲**（`ClusterMaterialData` 112B，C# 与 HLSL 字段顺序必须逐字一致）；唯一还剩的每材质资源是 `_DetailMap`+`_DetailNormalMap` 两张贴图 ⇒ **按 detail 贴图对分组**，一组一次间接绘制（实测 ≤17 组）。
- **片元零复制**：`ClusterGBufferInput.hlsl` 里 `#define CLUSTER_INSTANCED_MATERIAL 1` 屏蔽 `ClusterDOTSMaterial.hlsl` 的 UnityPerMaterial，并把 `INPUT_PROP(type,name)` 重定向为 `_ClusterMaterials[_ClusterMatIndex].name`；`INPUT_PROP` 在被 include 的函数体里就地展开，所以这套 `#define` **必须写在 include ClusterInput.hlsl 之前**。
  - 簇号逐片元变化 ⇒ VS 取 `_Visible[...]` 得簇号，经 `nointerpolation` varying 传片元，片元开头写 `static uint _ClusterMatIndex`（全局 static = 每线程局部量，不是 uniform）。
  - `ClusterPass.hlsl` 的片元主体已抽成 `ClusterGBufferShade()`；`FeedbackPass.hlsl` 的 pageId 编码抽成 `FeedbackColor(baseUV)`，两条路共用一份。
- **顶点**：按**唯一 mesh** 上传一份（mesh 局部空间）+ 每簇 4 行 localToWorld / 4 行法线矩阵（行存储 + `dot` 手工乘，**不要用 `float4x4`**：它在 StructuredBuffer 里的行/列主序取决于编译器打包约定）。同一 mesh 的多实例因此共享顶点，不随摆放数量膨胀。
- **非索引间接绘制**：`DrawProceduralIndirect(matrix, mat, pass, Triangles, argsBuf, offset, mpb)`，`vertexCount = 可见簇数×384`，VS 用 `vid/384`、`vid%384` 自推 → 不需要 GPU 索引缓冲（阴影路径那根静态单位索引缓冲在这里省掉了）。
- **Feedback 也由 Cluster 画**（同一个可见列表，保证「请求的页 = 采样的页」）；`DrawFeedback` 返回 false 时回退 `DrawRenderers(feedbackTagId)`。
- **不 Disable MeshRenderer**（否则 SRP 阴影那条路一起失效），改用 rendering layer 位 **28** 把物体从 SRP 所有 pass 里排掉；位 29 仍是 cutout 阴影专用。
  ⚠️ 掩码是包含式的：接管物体要设成**只有 bit28**（`orig | bit` 那种写法排不掉，会 SRP+Cluster 双画）。
- 动态物体（Transform 真的动了）由 `ClusterObject.Update` 自动 `Unregister` + 还原掩码交回 SRP，只掉一次（掉一次会触发整份 `Rebuild`，有一次性开销）。
- **烘焙共享**：`ClusterMeshCache.Get(mesh)` 静态缓存（`TrisPerCluster=128`、`lodCount=1`），阴影 `EnsureBaked` 也改用它 —— 否则两条路各烘一遍，白花两倍时间与内存。
- **剔除**：`GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix` 提 6 平面（与绘制侧逐位一致；裸 `proj*view` 在 D3D reversed-Z 下近/远算反 ⇒ 过剔除，阴影路径同款坑）。
- **已知限制**：只支持静态物体（世界矩阵 OnEnable 抓一次）；只有 LOD0；顶点属性约 200MB（可压到 ~28B）；反射 `unity_SpecCube0` 走全局默认值（手动 draw 不走 `PerObjectData.ReflectionProbes`），有独立反射探针时可能有差异。
- **切开关必须 Play 前设**：RLM 位与注册都发生在 `OnEnable`，Play 中改不会生效。
