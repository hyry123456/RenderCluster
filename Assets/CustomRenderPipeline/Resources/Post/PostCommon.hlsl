#ifndef CUSTOM_POST_COMMON_INCLUDED
#define CUSTOM_POST_COMMON_INCLUDED

// SRP Core 的 Common.hlsl：提供 TEXTURE2D/SAMPLER/SAMPLE_TEXTURE2D 这些宏，
// 以及最关键的 UNITY_UV_STARTS_AT_TOP（它由 API/D3D11.hlsl 定义）。
// 自己按平台手写那套 #define 很容易出错，直接用官方的。
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

// 后处理公用：全屏三角形 + 色调映射 + 小工具
// 刻意【不 include 工程里的 Common.hlsl】：后处理只需要极少数函数，
// 引进来反而会和它的宏/结构体打架（那个 Common 是给 GBuffer/Cluster 用的）。

// ---------------------------------------------------------------------------
// 全屏三角形
// 顶点 0/1/2 → 位置 (-1,-1)/(-1,3)/(3,-1)，UV (0,0)/(0,2)/(2,0)，
// 一个三角形盖住 [0,2]²，比 quad 少一次光栅化边界。
//
// ⚠️【翻转判据必须是 _ProjectionParams.x，不是 UNITY_UV_STARTS_AT_TOP】
//   UNITY_UV_STARTS_AT_TOP 是【编译期平台常量】，D3D 下恒为 1 ⇒ 它对所有摄像机都给同一个答案。
//   但「采样一张 RT 要不要翻 y」是【逐摄像机】的：
//     · Game 相机渲染到后缓冲  ⇒ _ProjectionParams.x = +1 ⇒ 不翻
//     · Scene / 渲染到贴图的相机 ⇒ _ProjectionParams.x = -1 ⇒ 要翻
//   这就是为什么原来那版在 Scene 视图是正的、Game 视图却上下颠倒：
//   它按平台常量翻了一次，Game 相机其实不该翻。工程里 DefaultPassVertex
//   （DefferCamera.shader 的 _CopyColor pass，早就验证正确）用的正是 _ProjectionParams.x，
//   这里跟它保持逐字一致。
//
// ⚠️【pos 和 uv 只能翻一个】两者同时翻会互相抵消成"翻一次"，同样导致上下颠倒。
//   这里选翻 uv：pos.y 取反会把三角形绕序从顺时针翻成逆时针（当前靠 Cull Off 兜着，
//   但那是隐性依赖），翻 uv 只改采样方向，几何一个顶点都不动。
//
//   （1.0 - uvy 而不是 2.0 - uvy：位置是 rawUv*2-1，从位置反解出来的采样坐标恰好是
//     1 - rawUv，于是 NDC -1 → uv 1、NDC +1 → uv 0。）
// ---------------------------------------------------------------------------

// Unity 的逐摄像机全局。HLSLPROGRAM 不会自动声明它（工程的
// Renderer/ShaderLibrary/UnityInput.hlsl 也是自己写了一份），所以这里必须自己声明。
// x = +1 正常 / -1 投影被翻转（渲染到贴图），其余分量与本文件无关。
float4 _ProjectionParams;

float4 PostFullscreenVertexPosition(uint vertexID)
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    return float4(uv * 2.0 - 1.0, 0.0, 1.0);
}

float2 PostFullscreenTexCoord(uint vertexID)
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    if (_ProjectionParams.x < 0.0) uv.y = 1.0 - uv.y;
    return uv;
}

// ---------------------------------------------------------------------------
// 亮度 / 组合工具
// ---------------------------------------------------------------------------
float PostMax3(float3 c) { return max(c.r, max(c.g, c.b)); }
float PostMin3(float3 c) { return min(c.r, min(c.g, c.b)); }

// Rec.709 亮度（线性空间）
float PostLuminance(float3 c) { return dot(c, float3(0.2126729, 0.7151522, 0.0721750)); }

// ---------------------------------------------------------------------------
// 色调映射
// ---------------------------------------------------------------------------

// Khronos PBR Neutral —— URP 的 "Neutral" 就是它。
// 特点：几乎不动暗部色彩，只在接近 1.0 时压缩高光，非常适合"想保持美术原色"的项目。
float3 PostTonemapNeutral(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;

    float peak = PostMax3(color);
    if (peak < startCompression) return color;

    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, float3(newPeak, newPeak, newPeak), g);
}

// ACES（Stephen Hill 拟合，URP / HDRP 同款）—— 高光滚降更"电影感"，但会吃掉一点饱和度。
float3 PostTonemapACES(float3 color)
{
    // sRGB → ACEScg 的近似
    const float3x3 ACESInputMat = float3x3(
        0.59719, 0.07600, 0.02840,
        0.35458, 0.90834, 0.13383,
        0.04823, 0.01566, 0.83777);
    // ACEScg → sRGB 的近似
    const float3x3 ACESOutputMat = float3x3(
         1.60475, -0.10208, -0.00327,
        -0.53108,  1.10813, -0.07276,
        -0.07367, -0.00605,  1.07602);

    color = mul(ACESInputMat, color);

    // RRT + ODT 的拟合（Narkowicz / Hill 的 rational 形式）
    float3 a = color * (color + 0.0245786) - 0.000090537;
    float3 b = color * (0.983729 * color + 0.4329510) + 0.238081;
    color = a / b;

    color = mul(ACESOutputMat, color);
    return saturate(color);
}

// mode: 0 = 不映射，1 = Neutral，2 = ACES
float3 PostTonemap(float3 color, int mode)
{
    if (mode == 1) return saturate(PostTonemapNeutral(color));
    if (mode == 2) return PostTonemapACES(color);
    return color;
}

// ---------------------------------------------------------------------------
// 噪声（胶片颗粒用）—— 不用贴图，省一张资源
// ---------------------------------------------------------------------------
float PostHash(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

#endif // CUSTOM_POST_COMMON_INCLUDED
