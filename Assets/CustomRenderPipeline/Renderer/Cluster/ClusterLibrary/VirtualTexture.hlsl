#ifndef CUSTOM_VIRTUAL_TEXTURE_INCLUDED
#define CUSTOM_VIRTUAL_TEXTURE_INCLUDED

#include "../../ShaderLibrary/Common.hlsl"

// ============================================================================
// RVT（贴图流式图集）的【唯一】寻址实现 —— 全部 pageId / 采样坐标都从这里出。
//
// 为什么必须只有一份：这套 RVT 是「按需流式」的，Feedback 决定【请求哪一页】，
// GBuffer 决定【采样哪一页】。两边公式只要差一点点（多一个常数、用 max 还是 min 轴、
// tile 边界差 1），就会出现「请求的是粗页、采样的是细页」⇒ 采到从没被写过的图集区域
// ⇒ 串色 / 黑块 / 周期性变糊。历史上这个问题重复出现过三次，所以抽成单点。
//
// 三处调用者：
//   ① Feedback 写入 pageId ...... VTFeedbackPageColor   （Cluster 的 FeedbackPass.hlsl、
//                                   地形的 TerrainFeedback pass）
//   ② MeshRenderer / 簇 GBuffer 采样 ... VTGetSampleUV  （ClusterInput.hlsl 的 GetColor）
//   ③ 地形多图层采样 ............ VTGetSampleUV         （TerrainLit.shader）
//
// 依赖的全局量（都在 ShaderLibrary/UnityInput.hlsl 声明，ClusterCore 每帧 SetGlobal）：
//   _VTFeedbackParam  = (x:mipsChangeSpeed, y:CellSize, z:mipsLevel-1, w:mipsOffset)
//   _TilesPerAxis / _SumTextureCount / _TextureNodesBuffer / _LevelData[7] / _LevelPadding[7]
// ============================================================================

/// <summary>
/// 由「已做过 _SplatN_ST / _MainTex_ST 变换的层 UV」算出需要的 mip。
/// 分辨率口径是【虚拟贴图总分辨率】= CellSize × T，不是源贴图尺寸。
/// 用【短轴】导数：图集没有 mipmap，各向异性靠「短轴选 mip + 长轴多次偏移采样」模拟，
/// 若用长轴，图集侧会请求粗页而采样侧又沿长轴多 tap 细页 ⇒ 两边对不上。
/// ⚠️ 内部含 ddx/ddy：只能在【片元】里、且在【统一控制流】上调用（不能在 if 分支里，
///    否则导数未定义 —— 地形多图层必须先算完 4 个 mip，再按权重分支采样）。
/// </summary>
float VTMipLevel (float2 uvLayer)
{
    float V = _VTFeedbackParam.y * (float)max(1, _TilesPerAxis);
    float2 uv = uvLayer * V;
    float2 dx = ddx(uv);
    float2 dy = ddy(uv);

    float lenX = length(dx), lenY = length(dy);
    float2 minorVec = (lenX < lenY) ? dx : dy;

    // 返回值直接 `(uint)` 截断即为 Feedback 侧请求的 mip（截断 == floor，因结果 >= 0）。
    return clamp(_VTFeedbackParam.x * log2(max(dot(minorVec, minorVec), 1e-12)) + _VTFeedbackParam.w,
        0.0, _VTFeedbackParam.z);
}

/// <summary>
/// (uv, texId, mip) → 图集里实际要采样的坐标。
/// 步骤：先 frac 再切 tile（tiling>1 时 uv 会 >1，直接 clamp 会把所有重复段压到最后一个 tile）
/// → 查页表拿该页所在 mip 级的 cell 布局 → 定位 cell 起点 + padding → 按平台翻 Y。
/// </summary>
float2 VTGetSampleUV (float2 uv, uint texId, uint mip)
{
	//pageId = mip * _SumTextureCount + texId * (T*T) + tileIndex，与 Feedback 编码完全一致
	int T = _TilesPerAxis;
	int Tsq = T * T;
	//先 Wrap 再切 tile：uv 已经过 _MainTex_ST 变换，tiling>1 时会 >1；
	//直接 *T 后 clamp 会把所有重复段压到最后一个 tile（整片拉伸、法线方向全错）。
	//T=1 时与旧写法等价（后续本来就要 frac）。
	float2 wrappedUV = frac(uv);
	float2 tileUV = wrappedUV * (float)T;
	int tileX = clamp((int)floor(tileUV.x), 0, T - 1);
	int tileY = clamp((int)floor(tileUV.y), 0, T - 1);
	float2 localUV = frac(tileUV);
	int tileIndex = tileY * T + tileX;
	int pageId = (int)mip * _SumTextureCount + (int)texId * Tsq + tileIndex;
	TextureNode node = _TextureNodesBuffer[pageId];

	// mipsLevel 直接索引 _LevelData[7] / _LevelPadding[7]：页表在「该页还没被分配」时是全 0
	// （合法），但任何异常值都会越界读全局数组。这里限幅兜住。
	int lv = clamp(node.mipsLevel, 0, 6);
	float4 levelData = _LevelData[lv],
		levelPadding = _LevelPadding[lv];

	localUV = frac(localUV);                                                    //限制uv范围，默认都是Repeat

	//第一个是offset，然后加到目标位置，再加Padding
	float2 tileBegin = float2(levelData.x, 0) + levelData.yz * float2(node.width, node.height) + levelPadding.xy;
	//贴图值起点加上uv值
	localUV = tileBegin + levelPadding.zw * localUV;

#if UNITY_UV_STARTS_AT_TOP
	localUV.y = 1.0 - localUV.y;
#endif
	return localUV;
}

/// <summary>
/// 反馈色 = 把 pageId 编码进 RGBA8（每通道 8bit 拼 32bit），由 compact compute 解码。
/// mip 必须与 VTGetSampleUV 的调用方用同一套公式（VTMipLevel）。
/// ⚠️ 同样含导数，只能在统一控制流里调用。
/// </summary>
float4 VTFeedbackPageColor (float2 uvLayer, uint texId)
{
	uint mip = (uint)VTMipLevel(uvLayer);

	uint T = (uint)_TilesPerAxis;
	uint Tsq = T * T;
	//先 Wrap 再切 tile（同 VTGetSampleUV 的说明，tiling>1 时不能直接 clamp）
	float2 wrappedUV = frac(uvLayer);
	float2 tileUV = wrappedUV * (float)T;
	uint tileX = (uint)clamp(floor(tileUV.x), 0.0, (float)(T - 1));
	uint tileY = (uint)clamp(floor(tileUV.y), 0.0, (float)(T - 1));
	uint tileIndex = tileY * T + tileX;

	uint pageId = mip * (uint)_SumTextureCount + texId * Tsq + tileIndex;
	uint r = pageId & 255u;
	uint g = (pageId >> 8) & 255u;
	uint b = (pageId >> 16) & 255u;
	uint a = (pageId >> 24) & 255u;
	return float4((float)r / 255.0, (float)g / 255.0, (float)b / 255.0, (float)a / 255.0);
}

#endif
