# CustomRenderPipeline_Archive —— 暂存区（待完善 / 非 RVT 核心）

> 本文件夹**已移到 `Assets/` 之外**（项目根目录 `_Disabled/` 下），因此 **Unity 不再编译其中的
> 任何 .cs / .shader / .compute**，控制台也不会再出现这些文件的报错。
> 这正是“不占据主干”的目标：文件仍保留在磁盘上，需要时可整体移回 `Assets/CustomRenderPipeline/`。

本文件夹收纳**与当前 RVT（Cluster 虚拟贴图）技术无关、暂不稳定或有 bug 的子系统**。

## 已归档内容（原路径 → 现位置）

| 原路径 | 现位置 | 说明 |
| --- | --- | --- |
| `GPU Dravin/GPU Particle` | `_Disabled/CustomRenderPipeline_Archive/GPU Dravin - GPU Particle` | GPU 粒子系统（bug/未完成） |
| `GPU Dravin/WaterSystem` | `_Disabled/CustomRenderPipeline_Archive/GPU Dravin - WaterSystem` | KWS 水面系统（FFT/焦散等） |
| `GPU Dravin/Terrain` | `_Disabled/CustomRenderPipeline_Archive/GPU Dravin - Terrain` | GPU 地形 |
| `RenderEffect` | `_Disabled/CustomRenderPipeline_Archive/RenderEffect` | 平面反射 / 天气 |
| `RenderTools` | `_Disabled/CustomRenderPipeline_Archive/RenderTools` | 贴图处理工具（高斯模糊/法线/Ramp 等） |
| `Runtime/PostFX` | `_Disabled/CustomRenderPipeline_Archive/Runtime - PostFX` | 后处理运行时代码（BulkLight 等） |
| `Renderer/PostFX` | `_Disabled/CustomRenderPipeline_Archive/Renderer - PostFX` | 后处理 shader / compute |
| `Renderer/CommonShaders/Particles` | `_Disabled/CustomRenderPipeline_Archive/CommonShaders - Particles` | 粒子 shader |
| `Renderer/CommonShaders/Skybox` | `_Disabled/CustomRenderPipeline_Archive/CommonShaders - Skybox` | 天空盒 shader |
| `Renderer/CommonShaders/DropNormal` | `_Disabled/CustomRenderPipeline_Archive/CommonShaders - DropNormal` | 平面法线 shader |
| `Renderer/CommonShaders/Terrain` | `_Disabled/CustomRenderPipeline_Archive/CommonShaders - Terrain` | 地形 shader（VT* 旧版已拆到 ToRemove） |
| `Renderer/CommonShaders/Untility` | `_Disabled/CustomRenderPipeline_Archive/CommonShaders - Untility` | 工具类 shader |
| `Renderer/ComputShader/Particles` | `_Disabled/CustomRenderPipeline_Archive/ComputShader - Particles` | 粒子 compute |
| `Renderer/ComputShader/Terrain` | `_Disabled/CustomRenderPipeline_Archive/ComputShader - Terrain` | 地形 compute |
| `Renderer/Resources` | `_Disabled/CustomRenderPipeline_Archive/Renderer - Resources` | KWS 水面 / FFT 资源 |
| `Renderer/DefferPipeline/Terrain` | `_Disabled/CustomRenderPipeline_Archive/DefferPipeline - Terrain` | 地形 deferred pass |
| `PostFX Setting.asset`（原根目录） | `_Disabled/CustomRenderPipeline_Archive/PostFX Setting.asset` | 后处理配置资产（脚本已随 Runtime-PostFX 移出，故一并移出） |

## 后处理（PostFX）已与管线彻底解耦

`PostFXStack` / `PostFXSetting` 已从以下 kept 文件中移除所有引用，因此可以整体移出 `Assets/`
而不会破坏 RVT 主构建：

- `Runtime/MyRenderPipeline/Core/MyPipeline.cs`
- `Runtime/Defferrd Render/Core/DefferPipeline.cs`
- `Runtime/RenderPiplineBase/RenderPipelineBase.cs`（移除 `postFXStack` 字段）
- `Runtime/RenderPiplineBase/RenderAsset.cs`（移除 `postFXSetting` 字段）
- `Runtime/MyRenderPipeline/Core/MyPipelineAsset.cs`
- `Runtime/Defferrd Render/Core/DefferPipelineAsset.cs`

管线现在渲染完 GBuffer 后直接走 `DrawFinal()`，不再有后处理阶段。

## 重要约束

- `GPU Dravin/Base/` **保留在主区**：其中的 `GPUDravinSystem` 被管线硬依赖
  （天空盒 / SH9 光照）。该子系统的核心基类不能移走。
- 若以后想恢复后处理：把 `Runtime - PostFX` 与 `Renderer - PostFX` 移回原路径，
  并在上述 6 个管线文件中重新加回 `postFXStack` 的 Setup/Render/Dispose 调用
  （参考本归档内的 `PostFXStack.cs` / `PostFXSetting.cs` 接口）。

## 回退方法

把对应子文件夹 `mv` 回 `Assets/CustomRenderPipeline/` 下的“原路径”（`.meta` 已一并带走，
GUID 不变），Unity 重新导入后引用自动恢复。
