# RenderCluster 项目长期记忆

## 1. Cluster 阴影管线（当前定案）
- 三条路径由资产 `clusterShadowMode` 切换：SRP(0) / Cluster(1) / ClusterDebug(2)。
  - **SRP**：`GPUDravinSystem.DrawShadows`（画 BRG/RVT）+ `context.DrawShadows`（画 MeshRenderer），逐物体剔除 + SRP Batcher。
  - **Cluster / ClusterDebug**：**只跑** `cluster.Draw`（GPU 逐簇剔除 + 每级联 `DrawProceduralIndirect`），彻底不跑 GPUDravinSystem / context.DrawShadows（用户选纯 Cluster，丢 BRG/RVT 阴影）。4 个级联绘制累积进同一 CommandBuffer，循环外**只 `ExecuteBuffer()` 一次**（PrepareKeywords 4x→1x）。
  - **ClusterDebug**：同 Cluster，额外 `VerifyCulling()`：CPU 用与 `ClusterShadowCull.compute` 完全相同的视锥口径剔除一次，与 GPU 读回的 `_Counter` 比对，不一致即报裁剪错误（限频 1/120，同步 GetData 会卡流水，勿测性能）。
- GPU 逐簇剔除（`ClusterShadowCull.compute`：Reset/CullAll/BuildArgs 三 kernel，整帧 3 dispatch + 每级联 1 次 `DrawProceduralIndirect`）。**只画 LOD0**（`lodCount=1`，`_MaxLod=1` 守卫，`targetTexels<=0` 关闭 LOD 判据）。`clusterShadowForceAll` = 强制全画，区分「剔除错」vs「绘制链坏」。
- **是真正的 indirect draw**：`BuildArgs` 写 `_Args[k*4+1]=_Counter[k]`（GPU 可见簇数），`Draw()` 用 `DrawProceduralIndirect(argsBuf, 16*k)`；shader 用 `instanceID` 取 `_Visible[_ClusterBase+instanceID]` 得真实簇号。**`argsBuf` 必须用 `ComputeBufferType.IndirectArguments` 创建**（漏了报 "missing IndirectArguments flag"）。
- **cutout 分流（2026-10-22 定案）**：`ClusterRenderDataAsset.NeedsSRPShadow = cutoff || clipping` ⇒ `ClusterShadowRenderer.ScanScene` 里 `continue` 跳过（**不烘焙进 Cluster**，这是性能收益所在）+ `ClusterObject.OnEnable` 给 Renderer 打 `renderingLayerMask` bit29(`CutoffRenderingLayerBit`) ⇒ `Shadows.cs` 在 cluster 绘制之后，临时把灯光掩码切成 bit29 单独跑一次 `context.DrawShadows`。无 cutout 物体时 `HasCutoffCasters()` 为 false ⇒ 整段跳过、零开销（当前场景草地已 Disable，故该 pass 不执行）。

## 2. 铁律（改代码前必看）
- **阴影矩阵**：`buffer.SetViewProjectionMatrices(view,proj)` 内部已自动 `GL.GetGPUProjectionMatrix`；shader 直接用 `unity_MatrixVP`（= SRP `TransformWorldToHClip`）。**绝不要手推 z 行改写**（曾致阴影全错）。
- **`_Indices` 是全局顶点号**：shader 直接 `_Positions[_Indices[c*384+vid]]`，别再加簇基址（历史双重偏移坑）。
- **compute buffer 按 kernel 绑定**：`SetBuffer(kernelIndex,name,buf)`，无 `SetComputeBufferParam`。被多 kernel 共用的 buffer（如 `_Counter`）必须逐 kernel 绑全，漏绑的 dispatch 直接失效（症状：indirect instanceCount=0、无报错）。
- **剔除平面**：用 `GeometryUtility.CalculateFrustumPlanes(viewProj)`（世界空间，官方保证）；**别用 `ShadowSplitData.GetCullingPlane`**（空间不明，实测剔光世界）。`_ClusterBounds` 必须是世界空间包围球。

## 3. 架构 / 渲染
- 渲染 = BRG 批处理；逐簇 DrawMesh 已弃用（4FPS，归档 `_Deprecated/ClusterPath/`）。
- 纹理 = Tile-VT（`useTileVT`），与几何正交；T=1 公式退化为「整图一格」。
- 延迟管线 `DefferPipeline`：GBuffer MRT（RT0 albedo+metal…），renderScale=1，MSAA 1x。
- Edit 模式 `ClusterObject.OnEnable` 啥也不做 → 物体保持场景原材质（Edit 清晰/Play 糊分岔点）。Play+UseRVT=0 → `ApplyStandardMaterials()`（已是 DefferStandard 则原样沿用）；UseRVT=1 → BRG 注册。

## 4. BRG 阴影（layer 排除法，2026-10-21）
- 让 BRG 不投影的正解 = 删 `Unlit/ClusterPBR_BRG` 的 ShadowCaster pass（Unity 按 `LightMode="ShadowCaster"` 找 pass）。别走 `BatchFilterSettings.shadowCastingMode=Off`（2022.3 SRP 拦不干净）。
- 替代：`brgGBufferExcludeLayer`（默认 31；-1 关闭）。物体挪到该 layer + 材质换 `AssetMat(Unlit/ClusterPBR)`（其 ShadowCaster 比 DefferStandard 便宜 ~40%）；BRG `shadowCastingMode` 由 `BRGCastsShadows => layer<0` 实时派生。
- GBuffer/Unsupported pass 排除该层防双重绘制；相机/灯光 cullingMask 缺层自动补位+警告一次。退 Play 还原 layer（`OnDisable`）。
- 结论：BRG 对 134 个组合无收益，RVT+BRG 也打不过全 SRP 档（A 档 ~380fps）。瓶颈是阴影 pass 不是 RVT。

## 5. Tile-VT 不变式
- 单图虚拟分辨率 `V=CellSize×TilesPerAxis`（非 CellSize）；level0 容量=`CellCount²×mips/T²`。先算容量再调 mipsOffset。
- mip 用 `V`：`uv=baseUV*(_VTFeedbackParam.y*T)`，四处一致。mip 公式 `clamp(speed·log2(max(dot(dx,dx),dot(dy,dy)))+offset,0,mips-1)`，`speed=0.25,offset=0`（别加 `+1.0-speed`）。
- 图集边长 `=(padding·2^(mips-1)·2+CellSize)·CellCount·mips`；超 `maxTextureSize` 时 `Create()` 不抛异常只 `IsCreated()==false` ⇒ 黑屏。当前资产被调小到 CellSize256/T1 很糊，测完调回参考值(512/4/6/4)。

## 6. 雷区集合
- `ClusterPBR*.shader` 内嵌 SampleRVT/GetColor 逐字副本 → 改 pageId 多处同步。
- `ClusterPBR_BRG.shader` 必须自带 `LightMode="Feedback"` Pass。
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
- detail 直采不走虚拟化（越页串色）；BRG 材质源=场景原材质。
- mask 白回退：无 mask 图时 mask 恒白（AO=1），移植必须带白回退；判 mask 看 `_MASK_MAP` keyword 真值。
- UnityPerMaterial cbuffer「三处逐字副本」：漏一处 ⇒ cbuffer 不一致 ⇒ pass 不画（阴影缺失但画面正常，易误判）。
- `_CLIPPING`：`_CullOff` 有值≠该裁，看 keyword 真值。
- **`ShadowDrawingSettings` 没有 `renderingLayerMask` 属性**（任何 Unity 版本都没有，别臆造）。2022.3 过滤阴影投射物只能：`useRenderingLayerMaskTest=true` + **灯光自身**的 `Light.renderingLayerMask`，判据 `(Renderer.renderingLayerMask & Light.renderingLayerMask)!=0`。要只画某一批物体 ⇒ 临时把灯光掩码切成目标位，画完还原。
- **`Light.renderingLayerMask` 是 `int`**（`Renderer.renderingLayerMask` 才是 `uint`）⇒ 写 `1 << n`，别写 `1u << n`。
- 诊断：`NormalDebugMode` 决策树；两套 Debug 别混（assemble 只管预览槽，画面由 DefferPipeline.asset 决定）；截图对比固定相机+1920×1080+只切 UseRVT。

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
