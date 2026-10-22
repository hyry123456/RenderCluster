#ifndef CUSTOM_COMMON_INCLUDED
#define CUSTOM_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "UnityInput.hlsl"

// ============================================================================
// BRG（BatchRendererGroup / DOTS Instancing）的变换矩阵元数据，必须由 shader 显式声明。
//
// 背景：本文件后面引入的 core UnityInstancing.hlsl，在 UNITY_DOTS_INSTANCING_ENABLED 时会
// 无条件 #undef UNITY_MATRIX_M 并重定义为
//     LoadDOTSInstancedData_float4x4_from_float3x4(
//         UNITY_DOTS_INSTANCED_METADATA_NAME(float3x4, unity_ObjectToWorld))
// 于是本文件原本的 #define UNITY_MATRIX_M unity_ObjectToWorld 被顶掉，
// TransformObjectToHClip / TransformObjectToWorld 全部改走 DOTS 加载路径，
// 引用标识符 unity_DOTSInstancingF48_Metadataunity_ObjectToWorld（F48 = float3x4 的 48 字节）。
// 漏声明即报：
//     undeclared identifier 'unity_DOTSInstancingF48_Metadataunity_ObjectToWorld'
//     'LoadDOTSInstancedData_float4x4_from_float3x4': no matching 1 parameter function
//
// 数据由 BatchRenderManager.PackedMatrix 提供（c0x,c0y,c0z, c1x..c3z 共 12 个 float，
// 按矩阵列的 x/y/z 顺序存放、不存每列 w），与官方 SimpleBRGExample 完全一致。
//
// 三个刻意的“不优雅”之处：
//  1) **必须放在这里**（UnityInput.hlsl 之后、SpaceTransforms.hlsl 之前）。
//     本工程的编译顺序是「声明先于使用」：SpaceTransforms.hlsl 在本文件后面才被包含，
//     它的 GetObjectToWorldMatrix() 当场就要展开 UNITY_MATRIX_M；
//     声明若放在 SpaceTransforms.hlsl 之后，该处展开时就还看不到它 —— 这正是前几轮
//     怎么改都不生效的真正原因（与宏、与 #ifdef 时机都无关，纯粹是位置）。
//  2) 不加 #ifdef UNITY_DOTS_INSTANCING_ENABLED，无条件声明。
//     非 DOTS 变体下这个 cbuffer 无人引用，编译器直接优化掉，行为零变化。
//  3) 不走 UNITY_DOTS_INSTANCED_PROP 宏，直接写标识符。
//     这两个矩阵只经 UNITY_DOTS_INSTANCED_METADATA_NAME 取用，不需要宏额外生成的
//     _DOTSInstancingOverrideMode 静态量，直接声明 uint 就够，少一层宏展开少一个出错点。
// ============================================================================
// 必须声明全部 4 个 DOTS 矩阵元数据（见 UnityInstancing.hlsl:404-413，DOTS 模式下
// 这 4 个 UNITY_*_MATRIX 全被重定义为 LoadDOTSInstancedData_float4x4_from_float3x4(...)）：
//   unity_ObjectToWorld      -> UNITY_MATRIX_M
//   unity_WorldToObject      -> UNITY_MATRIX_I_M
//   unity_MatrixPreviousM    -> UNITY_PREV_MATRIX_M    （GetPrevObjectToWorldMatrix，SpaceTransforms.hlsl:23）
//   unity_MatrixPreviousMI   -> UNITY_PREV_MRIX_I_M    （GetPrevWorldToObjectMatrix，SpaceTransforms.hlsl:28）
// 漏掉任何一个都会报 undeclared identifier + “no matching 1 parameter function”
// （后者是 secondary：标识符未声明导致 1 参数重载解析失败）。
// 注意：当前 BatchRenderManager 只通过 metadata 注册了前 2 个，后 2 个（上一帧矩阵）未注册，
// 运行时其 cbuffer 值会是 0 -> 从 metadata buffer 地址 0 取到零矩阵。GBuffer 不依赖它则无碍，
// 但若哪天要用运动向量，需要在 C# 侧也注册 unity_MatrixPreviousM / unity_MatrixPreviousMI。
cbuffer UnityDOTSInstancing_BuiltinPropertyMetadata {
	uint unity_DOTSInstancingF48_Metadataunity_ObjectToWorld;
	uint unity_DOTSInstancingF48_Metadataunity_WorldToObject;
	uint unity_DOTSInstancingF48_Metadataunity_MatrixPreviousM;
	uint unity_DOTSInstancingF48_Metadataunity_MatrixPreviousMI;
}

// ----------------------------------------------------------------------------
// UNITY_SETUP_DOTS_SH_COEFFS —— DOTS 模式下 UNITY_SETUP_INSTANCE_ID 展开会调用它
// （UnityInstancing.hlsl:264-268），但本自定义 RP 没有包含定义它的 SRP 文件：
// 标准 Unity 里它在 URP/HDRP 的 ShaderVariablesFunctions.hlsl；全局搜索确认
// com.unity.render-pipelines.core@14.0.12 里它只有「使用点」(UnityInstancing.hlsl:268)、
// 没有任何 #define 定义点 —— 所以之前报 undeclared identifier。
//
// 本 shader 的间接光走自定义 GetBakeDate -> SampleLightProbe，读的是标准全局
// uniform unity_SHAr / unity_SHAg / unity_SHBr ...。经核查 C# 侧喂的是 _Sh9 /
// MySH9Array（见 MyLighting.cs:90、GPUDravinSystem.cs:406），与 shader 读的
// unity_SHAr 对不上 —— 即 cluster 路径的 unity_SHAr 实际【未被喂】，它在
// DrawRenderers 与 BRG 两条路径下都是同一个全局值（当前为 0）。
//
// 因此这里把它定义为【空操作】：BRG 直接复用与 DrawRenderers 完全相同的全局 SH，
// 画面与关闭 useBRG 保持一致，且不引入任何「算错的数据」（用户特别担心的一点）。
//
// 若日后要做 per-instance SH（每个 cluster 用各自光照探针），应在此改为从 DOTS
// 实例 metadata 加载 unity_SHAr 等 7 个 float4 —— 属于 Step 2 工作，不在 Step 1。
//
// ⚠️ 关键：必须【无条件】定义，不能包在 #ifdef UNITY_DOTS_INSTANCING_ENABLED 里！
// 原因：UNITY_DOTS_INSTANCING_ENABLED 是在本文件第 98 行的 UnityInstancing.hlsl
// （其 :37-38 由 DOTS_INSTANCING_ON 关键字派生）里才被定义的；若在这里用 #ifdef
// 守卫，此刻它还没定义 → 整段被跳过 → 报错依旧。本文件第 48 行的矩阵 cbuffer 之
// 所以生效，正因为它也是无条件声明的。宏展开是惰性的，真正用到时（pass 文件里
// 写 UNITY_SETUP_INSTANCE_ID(input)）UnityInstancing.hlsl 早已 include、宏已定义，
// 只需那时 UNITY_SETUP_DOTS_SH_COEFFS 这个符号存在即可——这里无条件给出即可满足。
#define UNITY_SETUP_DOTS_SH_COEFFS



#define UNITY_MATRIX_M unity_ObjectToWorld
#define UNITY_MATRIX_I_M unity_WorldToObject
#define UNITY_MATRIX_V unity_MatrixV
//错误的矩阵，不知道代表哪个值
#define UNITY_MATRIX_I_V unity_MatrixV
//错误的矩阵，不知道代表哪个值
#define UNITY_PREV_MATRIX_M unity_MatrixPreviousM
#define UNITY_PREV_MATRIX_I_M unity_MatrixPreviousMI

#define UNITY_MATRIX_VP unity_MatrixVP
#define UNITY_MATRIX_P glstate_matrix_projection

// #pragma multi_compile _ _ACCURACY_NORMAL


#if defined(_SHADOW_MASK_ALWAYS) || defined(_SHADOW_MASK_DISTANCE)
	#define SHADOWS_SHADOWMASK
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

#ifndef _SAMPLE_STATE
#define _SAMPLE_STATE
SAMPLER(sampler_linear_clamp);
SAMPLER(sampler_linear_repeat);
SAMPLER(sampler_point_clamp);
SAMPLER(sampler_point_repeat);

#endif

bool IsOrthographicCamera () {
	return unity_OrthoParams.w;
}

float OrthographicDepthBufferToLinear (float rawDepth) {
	#if UNITY_REVERSED_Z
		rawDepth = 1.0 - rawDepth;
	#endif
	return (_ProjectionParams.z - _ProjectionParams.y) * rawDepth + _ProjectionParams.y;
}


float Square (float x) {
	return x * x;
}

float DistanceSquared(float3 pA, float3 pB) {
	return dot(pA - pB, pA - pB);
}

void ClipLOD (float2 positionSS, float fade) {
	#if defined(LOD_FADE_CROSSFADE)
		float dither = InterleavedGradientNoise(positionSS, 0);
		clip(fade + (fade < 0.0 ? dither : -dither));
	#endif
}

float3 DecodeNormal (float4 sample, float scale) {
	#if defined(UNITY_NO_DXT5nm)
	    return UnpackNormalRGB(sample, scale);
	#else
	    return UnpackNormalmapRGorAG(sample, scale);
	#endif
}

// 带有效性判定的法线解码。
// 采到的可能是「非法样本」而不是一张法线贴图：
//   · RVT 图集里该 tile 尚未调入 / 已被 LRU 换走 → 全黑 (0,0,0,0)
//   · 材质没有法线贴图，绑定的是默认图 → 纯白或近似常量
// 这两种样本经 DecodeNormal 都会得到 z≈0 的「掠射法线」（全黑→(-1,-1,0)，全白→(1,1,0)），
// 再经 TBN 变换后整面变成同一个固定朝向 —— 就是「表面像一块平板、方向全错」的经典症状，
// 与 GBuffer 法线压缩方式无关。
// 合法法线贴图解码后 z 必定明显 > 0（z=0.02 已经代表 89° 的极端斜面），
// 因此以 z 判定有效性，退化时退回切线空间中值 (0,0,1)，等价于只用顶点法线。
float3 DecodeNormalSafe (float4 sample, float scale) {
	float3 n = DecodeNormal(sample, scale);
	return (n.z < 0.02) ? float3(0.0, 0.0, 1.0) : n;
}


// Returns ±1
float2 signNotZero(float2 v) {
	return float2((v.x >= 0.0) ? +1.0 : -1.0, (v.y >= 0.0) ? +1.0 : -1.0);
}
// Assume normalized input. Output is on [-1, 1] for each component.
float2 float32x3_to_oct(in float3 v) {
	// Project the sphere onto the octahedron, and then onto the xy plane
	float2 p = v.xy * (1.0 / (abs(v.x) + abs(v.y) + abs(v.z)));
	// Reflect the folds of the lower hemisphere over the diagonals
	return (v.z <= 0.0) ? ((1.0 - abs(p.yx)) * signNotZero(p)) : p;
}
float3 oct_to_float32x3(float2 e) {
	float3 v = float3(e.xy, 1.0 - abs(e.x) - abs(e.y));
	if (v.z < 0) v.xy = (1.0 - abs(v.yx)) * signNotZero(v.xy);
	return normalize(v);
}

static float2 PackNormal(float3 normalWS){
	float2 re = 0;
	re.xy = float32x3_to_oct(normalWS);
	re.xy = re.xy * 0.5 + 0.5;
	return re;
}

static float3 UnpackNormal(float3 rt1){
	float2 pre = rt1.xy * 2.0 - 1.0;

	return oct_to_float32x3(pre);
}

float3 NormalTangentToWorld (float3 normalTS, float3 normalWS, float4 tangentWS) {
	float3x3 tangentToWorld =
		CreateTangentToWorld(normalWS, tangentWS.xyz, tangentWS.w);
	return TransformTangentToWorld(normalTS, tangentToWorld);
}

//数值重映射，x:输入值, inMin:输入的最小值, inMax:输入的最大值, outMin:输出的最小值, outMax:输出的最大值
float Remap(float x, float inMin, float inMax, float outMin, float outMax)
{
	x = clamp(x, inMin, inMax);
	return (outMax - outMin) / (inMax - inMin) * (x - inMin) + outMin;
}




#endif