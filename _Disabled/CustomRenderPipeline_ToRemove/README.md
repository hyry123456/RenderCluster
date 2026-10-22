# CustomRenderPipeline_ToRemove —— 待删除区（重复 / 临时 / 遗留）

> 本文件夹**已移到 `Assets/` 之外**（项目根目录 `_Disabled/` 下），Unity 不再识别或编译其中内容。
> 确认项目无影响后，可**直接删除整个 `CustomRenderPipeline_ToRemove` 文件夹**，
> 不影响 `Assets/` 下的任何东西。

本文件夹放的是**明显冗余、临时或未完成的文件**，与当前 RVT 主线无关。

## 内容

- `Temp/` ← 原 `Renderer/CommonShaders/Temp`
  - `Grass.shader`、`Near.shader`：临时 / 未完成着色器，无任何 kept 代码引用。
- `LegacyTerrainVT/` ← 原 `Renderer/CommonShaders/Terrain/Shaders` 中的 `VT*` 一组
  - `VirtualTexture.cginc`、`VTFeedback.cginc`、`VTDrawTexture.cginc`、`VTDebug.cginc`
    及其对应 `.shader`（Feedback / DownScale / DrawLookup / DrawTexture / Debug / DebugMipmap）。
  - 这是**旧版 VirtualTexture（地形虚拟贴图）实现**，与当前 `Cluster/RVT` 思路重复，
    且只被自身互相 `#include`，不被任何 kept RVT/管线代码引用，属历史遗留。

## 处理建议

- 若想让 Unity 彻底忽略并释放这些文件：它们已在 `Assets/` 之外，无需额外操作。
- 若确认无用：直接删除 `_Disabled/CustomRenderPipeline_ToRemove/` 整个目录即可。

## 回退方法（极少需要）

若发现某个文件其实还需要，把对应文件 `mv` 回原路径
（`Renderer/CommonShaders/Temp` 或 `Renderer/CommonShaders/Terrain/Shaders`，
`.meta` 已一并带走）即可恢复；但请注意 `.meta` 回到原路径后 GUID 会变化，
需重新在材质/资源里指定。
