#ifndef CLUSTER_DOTS_MATERIAL
#define CLUSTER_DOTS_MATERIAL

// 簇实例化绘制（Cluster 画 GBuffer / Feedback）时，材质参数来自每簇 StructuredBuffer，
// 不存在 UnityPerMaterial —— 由 ClusterGBufferInput.hlsl 预先定义 CLUSTER_INSTANCED_MATERIAL
// 并接管 INPUT_PROP，所以下面整段（UnityPerMaterial + INPUT_PROP）都要跳过。
#if !(defined(CLUSTER_INSTANCED_MATERIAL) && CLUSTER_INSTANCED_MATERIAL)

// ============================================================================
// DOTS(BRG) 变体共用的材质声明脚手架。
//
// 每个【声明了 DOTS_INSTANCING_ON 变体】的 pass 都必须 include 本文件：
//   1) UnityPerMaterial cbuffer —— SRP Batcher 硬性要求名字恰好是 UnityPerMaterial；
//      BRG 校验 "wrong cbuffer setup / Missing DOTS_INSTANCING_ON variant?" 的
//      组成条件之一（该 pass 必须有 SRP Batcher 兼容的材质 cbuffer 声明）。
//   2) MaterialPropertyMetadata 元数据块 —— 让 DOTS 相关 cbuffer（含
//      UnityInstancingDOTS_InstanceVisibility）在编译期存活。
//   3) INPUT_PROP 访问宏 —— DOTS 下走 WITH_DEFAULT（属性未注册 per-instance
//      metadata 时运行期自动回退到 UnityPerMaterial 全局值）。
//
// ⚠️ 属性列表必须与 ClusterPBR.shader 的 Properties 块、ClusterInput.hlsl 保持一致；
//    此前 ShadowCaster / WriteDeep 两个 pass 缺这套声明（只有实例 ID 链），导致：
//    真实场景开阴影后，BRG 批次被 ShadowCaster 的 DrawRenderers 带上，Submit 校验
//    该变体失败 → 整帧报 "wrong cbuffer setup"，BRG 一个都画不出来。
//    （Test 场景探针无灯光无阴影，ShadowCaster 变体从未被校验，所以探针能画 —— 这
//    正是二分结果"探针 OK、真实场景报错"的原因。）
//
// ⚠️【同一个 SubShader 内所有 pass 的 UnityPerMaterial 成员必须完全一致】（2026-10-20）
//    新增任何材质属性时，本文件的成员列表 + ClusterPBR_BRG.shader 里
//    GBuffer pass 与 Feedback pass 的两处 cbuffer，三处必须【同时】加。
//    只加其中一处 ⇒ BRG 报 "UnityPerMaterial CBuffer inconsistent size inside a
//    SubShader (<Unnamed Pass N>)"，该 pass 直接不画（表现为船/栈桥等物体阴影缺失）。
//
// 编译顺序说明：本文件必须在使用方已 include Common.hlsl（→ UnityInstancing.hlsl）
// 之后使用；ClusterPBR.shader 的 HLSLINCLUDE 已满足。UNITY_DOTS_INSTANCING_ENABLED
// 由 DOTS_INSTANCING_ON 关键字在 UnityInstancing.hlsl:37-38 派生，include 时已定义。
// ============================================================================
#ifdef UNITY_DOTS_INSTANCING_ENABLED

// ── 材质属性本体：SRP Batcher 要求的 UnityPerMaterial cbuffer ──
// （不能用 UNITY_INSTANCING_BUFFER_START：DOTS 模式下会重命名为
//   UnityInstancing_UnityPerMaterial，SRP Batcher 拒绝 —— 见 ClusterInput.hlsl 注释）
CBUFFER_START(UnityPerMaterial)
	UNITY_DEFINE_INSTANCED_PROP(uint, _MapId)
	UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _DetailMap_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
	UNITY_DEFINE_INSTANCED_PROP(float, _NormalScale)
	UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
	UNITY_DEFINE_INSTANCED_PROP(float, _Roughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailAlbedo)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailNormal)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailRoughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _CullOff)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseMask)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseNormal)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseEmission)
	UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
CBUFFER_END

// ── DOTS 实例元数据：保持 DOTS cbuffer 存活（本 BRG 只注册矩阵 metadata，
//    材质属性高位置 0，运行期 INPUT_PROP 回退到上面 UnityPerMaterial 全局值）──
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
	UNITY_DOTS_INSTANCED_PROP(uint, _MapId)
	UNITY_DOTS_INSTANCED_PROP(float4, _MainTex_ST)
	UNITY_DOTS_INSTANCED_PROP(float4, _DetailMap_ST)
	UNITY_DOTS_INSTANCED_PROP(float4, _Color)
	UNITY_DOTS_INSTANCED_PROP(float, _NormalScale)
	UNITY_DOTS_INSTANCED_PROP(float, _Metallic)
	UNITY_DOTS_INSTANCED_PROP(float, _Roughness)
	UNITY_DOTS_INSTANCED_PROP(float, _DetailAlbedo)
	UNITY_DOTS_INSTANCED_PROP(float, _DetailNormal)
	UNITY_DOTS_INSTANCED_PROP(float, _DetailRoughness)
	UNITY_DOTS_INSTANCED_PROP(float, _CullOff)
	UNITY_DOTS_INSTANCED_PROP(float, _UseMask)
	UNITY_DOTS_INSTANCED_PROP(float, _UseNormal)
	UNITY_DOTS_INSTANCED_PROP(float, _UseEmission)
	UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

#define INPUT_PROP(type, name) UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(type, name)

#else
// ── 非 DOTS 变体 ──
// 【关键】这里也必须声明 UnityPerMaterial，且成员与上面的 DOTS 分支【完全一致】。
// 原因：SRP Batcher 要求**同一个 SubShader 内所有 pass 的 UnityPerMaterial 成员完全一致** ——
// 只要有一个 pass 缺这个 cbuffer，整个 shader 就不是 batch 兼容材质，
// 阴影投射物（1219 个）只能一个个 draw，不能走 `Shadows.DrawSRP Batcher`（实测 0.45ms → 2.39ms）。
// 写法与 DefferStandard 用的 PBRInput.hlsl 一致：UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)。
UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
	UNITY_DEFINE_INSTANCED_PROP(uint, _MapId)
	UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _DetailMap_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
	UNITY_DEFINE_INSTANCED_PROP(float, _NormalScale)
	UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
	UNITY_DEFINE_INSTANCED_PROP(float, _Roughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailAlbedo)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailNormal)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailRoughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _CullOff)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseMask)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseNormal)
	UNITY_DEFINE_INSTANCED_PROP(float, _UseEmission)
	UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)

#define INPUT_PROP(type, name) UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, name)
#endif

#endif // !CLUSTER_INSTANCED_MATERIAL

#endif // CLUSTER_DOTS_MATERIAL
