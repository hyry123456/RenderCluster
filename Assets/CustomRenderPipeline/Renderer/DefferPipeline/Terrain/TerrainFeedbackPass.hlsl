#ifndef TERRAIN_FEEDBACK_PASS
#define TERRAIN_FEEDBACK_PASS

// ============================================================================
// 地形的 RVT 反馈 pass（每个图层一份，用 TERRAIN_FB_LAYER 区分）。
//
// 由 ClusterCore.RenderFeedback 用 LightMode = TerrainFeedback0..3 各画一次：
// 一个 pass 只能输出一个 pageId（RGBA8 = 32bit），4 层必须画 4 次。
// 不用 overrideMaterial 传 4 份材质：override 会绕过 Terrain 引擎往 materialTemplate
// 灌的 _SplatN_ST / _Control，反馈算出来的 UV 就全错（请求的页 ≠ 采样的页）。
//
// pageId 编码与采样侧同源（VirtualTexture.hlsl 的 VTFeedbackPageColor / VTMipLevel）。
// ============================================================================

struct TerrainFeedbackAttributes
{
    float3 positionOS : POSITION;
    float2 uvMain     : TEXCOORD0;
};

struct TerrainFeedbackVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uvMain     : TEXCOORD0;
};

TerrainFeedbackVaryings TerrainFeedbackVertex (TerrainFeedbackAttributes input)
{
    TerrainFeedbackVaryings o = (TerrainFeedbackVaryings)0;
    o.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS));
    o.uvMain = input.uvMain;
    return o;
}

float4 TerrainFeedbackFragment (TerrainFeedbackVaryings i) : SV_Target
{
#if TERRAIN_FB_LAYER == 0
    float2 uvL = TRANSFORM_TEX(i.uvMain, _Splat0);
    uint texId = (uint)_TerrainMapId0;
#elif TERRAIN_FB_LAYER == 1
    float2 uvL = TRANSFORM_TEX(i.uvMain, _Splat1);
    uint texId = (uint)_TerrainMapId1;
#elif TERRAIN_FB_LAYER == 2
    float2 uvL = TRANSFORM_TEX(i.uvMain, _Splat2);
    uint texId = (uint)_TerrainMapId2;
#else
    float2 uvL = TRANSFORM_TEX(i.uvMain, _Splat3);
    uint texId = (uint)_TerrainMapId3;
#endif
    return VTFeedbackPageColor(uvL, texId);
}

#endif
