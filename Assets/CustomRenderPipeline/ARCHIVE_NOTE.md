# CustomRenderPipeline 目录整理说明（ARCHIVE NOTE）

为聚焦 **RVT（Cluster 虚拟贴图）** 主线，已将非 RVT 核心、未完成或有 bug 的子系统
**移出 `Assets/`**，使 Unity 不再编译它们，控制台不再出现相关 shader 报错。

## 现在的主干（保留在 Assets 内，RVT 相关）

- `Runtime/ClusterRender/` —— RVT 核心（页池 / 反馈压缩 / 页表等）
- `Renderer/Cluster/`、`Renderer/DefferPipeline/{Cluster,PBR,Camera,DeffererLibrary}` —— RVT shader
- `Resources/Cluster/`（assemble.asset、RVTFeedbackCompact.compute）、`Resources/SH9/`
- `GPU Dravin/Base/` —— 管线硬依赖（天空盒 / SH9）
- `Runtime/Defferrd Render/`（AO、SSR 仍实例化）、`Runtime/MyRenderPipeline/`、`Runtime/RenderPiplineBase/`
- `Renderer/ShaderLibrary`、`Renderer/CommonInput`、`Renderer/CommonMaterial`、`Renderer/CommonShaders/RVT`

## 已移出（在 `Assets/` 之外，不参与编译）

项目根目录 `_Disabled/` 下：

- `CustomRenderPipeline_Archive/` —— 待完善：后处理 / 粒子 / 水面 / 地形 / 旧版 VT 等
  （含已与管线解耦的 `PostFX` C# 与 shader）。详见该目录内 `README.md`。
- `CustomRenderPipeline_ToRemove/` —— 待删除：临时 shader（`Temp/`）、旧版地形 VT（`LegacyTerrainVT/`）。
  确认无用后可直接删掉整个目录。详见该目录内 `README.md`。

## 关键变更

- **后处理（PostFX）已从管线彻底移除**：`MyPipeline` / `DefferPipeline` / `RenderPipelineBase` /
  `RenderAsset` 及其两个 Asset 文件中所有 `PostFXStack` / `PostFXSetting` 引用已删除，
  渲染完 GBuffer 后直接走 `DrawFinal()`。
- 移出后 Unity 不再编译这些文件，因此也不会再出现
  `Couldn't open include file ... Common.hlsl`、`shader is not supported on this GPU`、
  `Both vertex and fragment programs must be present` 等报错。

## 如何还原

把 `_Disabled/CustomRenderPipeline_Archive/` 或 `CustomRenderPipeline_ToRemove/` 下对应子文件夹
`mv` 回 `Assets/CustomRenderPipeline/`（`.meta` 已随迁，GUID 不变），
Unity 重新导入即恢复。后处理还需在 6 个管线文件里重新接回 `postFXStack` 调用。

## 注意事项（预期内的副作用）

- Skybox / 地形 相关 shader 随归档移出 `Assets/`；若场景中材质引用了这些 shader，
  编辑器里会显示 “shader missing”。聚焦 RVT 测试时无关；需要时从上述 `_Disabled` 还原即可。

## 2026-10-14 补充归档（修复因归档引发的编译 / 运行错误）

### 因依赖被归档而移出的无用代码
- `GPU Dravin/Base/PlaneTools/TerrainPlane/` → `_Disabled/CustomRenderPipeline_Archive/GPU Dravin - TerrainPlane/`
  （`TerrainPlane` 未被任何保留代码实例化，且依赖已归档的 `TerrainPlaneData`，属地形专用辅助，非 RVT 核心）
- `Runtime/Tool Pipelines/Shadow Pipeline/LightMap3D.cs` → `_Disabled/CustomRenderPipeline_Archive/Runtime - ShadowPipeline - LightMap3D.cs`
  （未被实例化，依赖后处理 `BulkLight` 中的 `ShadowMapSize`/`ShadowFilter`/`Texture3DSize` 枚举）

### 游戏脚本对归档类型的引用（已中性化处理，不影响编译）
- `Assets/Scripts/Info/InfoList/EnemyInfo.cs`：移除未使用的 `using DefferedRender;`
- `Assets/Scripts/Skill/List/EnemySkill/SimpleAttack.cs`、`MagicAttack.cs`：
  注释掉对 `CustomRenderPipline.GPUParticleSystem` 的引用（粒子系统按"先放着"意图停用；攻击逻辑/对象池保留，仅无粒子特效）。
  恢复粒子方法：把 `_Disabled/CustomRenderPipeline_Archive/GPU Dravin - GPU Particle` 移回 `Assets/CustomRenderPipeline/GPU Dravin/`，并取消这两处注释。

### 运行时说明
- `WaterSystem` 已归档 → 不再向 `GPUDravinSystem.DrawSkyboxLater` 注册回调，原先的 `ArgumentNullException: computeShader` 运行期报错已消失（属陈旧报错）。
