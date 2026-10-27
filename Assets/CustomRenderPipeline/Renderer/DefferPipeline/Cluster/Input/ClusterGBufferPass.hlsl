#ifndef CLUSTER_GBUFFER_PASS
#define CLUSTER_GBUFFER_PASS

// ============================================================================
// 簇实例化绘制的两个入口：GBuffer 与 RVT Feedback。
//
// 与「MeshRenderer 走 SRP」的差别只在顶点来源：这里没有硬件顶点缓冲，几何靠
// 索引间接绘制 —— compute 每帧只给【可见簇】写索引，VS 用 SV_VertexID 直接取
// 世界空间顶点属性（重建期已烘死变换），片元与 SRP 路径完全共用
// （ClusterGBufferShade / FeedbackColor）。
// ============================================================================

// Varyings 里多一个 nointerpolation 的【材质槽位】；必须在 include ClusterPass.hlsl 之前定义。
#define CLUSTER_INSTANCED 1

#include "ClusterGBufferInput.hlsl"     // → ClusterInput.hlsl（材质取数与 RVT 采样）
#include "ClusterPass.hlsl"             // → ClusterGBufferShade
#include "FeedbackPass.hlsl"            // → FeedbackColor

// ---------------- GBuffer ----------------

Varyings ClusterGBufferVertex (ClusterAttributes input)
{
	uint matIndex;
	float3 positionWS, normalWS; float4 tangent; float2 uv;
	ClusterFetch(input.vertexID, matIndex, positionWS, normalWS, tangent, uv);

	// 材质槽位必须在调用 TransformBaseUV / TransformDetailUV 之前设好：
	// 这两个函数要读 _ClusterMaterials[_ClusterMatIndex] 里的 _MainTex_ST / _DetailMap_ST。
	_ClusterMatIndex = matIndex;

	// 副切线由世界空间法线/切线叉乘得到（烘焙时已做过 Gram-Schmidt，两者正交）
	float3 tangentWS  = tangent.xyz;
	float3 binormalWS = cross(normalWS, tangentWS) * tangent.w;

	Varyings o = (Varyings)0;
	o.positionCS_SS = mul(_ClusterGBufferW2C, float4(positionWS, 1.0));
	o.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
	o.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
	o.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);
	o.baseUV = TransformBaseUV(uv);
	o.detailUV = TransformDetailUV(uv);
	o.matIdx = matIndex;
	return o;
}

// 片元入口：先把本片元的材质槽位写进 _ClusterMatIndex，之后 INPUT_PROP 全部按它取参数。
void ClusterGBufferFragment (Varyings input, out DefferTarget target)
{
	_ClusterMatIndex = input.matIdx;
	ClusterGBufferShade(input, target);
}

// ---------------- RVT Feedback ----------------

struct ClusterFeedbackVaryings
{
	float4 positionCS_SS : SV_POSITION;
	float2 baseUV : VAR_BASE_UV;
	nointerpolation uint matIdx : VAR_MAT_IDX;
};

ClusterFeedbackVaryings ClusterFeedbackVertex (ClusterAttributes input)
{
	// Feedback 只写页表，只要【位置 + UV】——不读法线、切线。
	// 这段每帧和 GBuffer 跑一样多的顶点。注意 baseUV 要先设材质槽位才能算 ST。
	uint matIndex;
	float3 positionWS, normalWS; float4 tangent; float2 uv;
	ClusterFetch(input.vertexID, matIndex, positionWS, normalWS, tangent, uv);

	ClusterFeedbackVaryings o;
	o.positionCS_SS = mul(_ClusterGBufferW2C, float4(positionWS, 1.0));
	_ClusterMatIndex = matIndex;
	o.baseUV = TransformBaseUV(uv);
	o.matIdx = matIndex;
	return o;
}

float4 ClusterFeedbackFragment (ClusterFeedbackVaryings input) : SV_Target
{
	_ClusterMatIndex = input.matIdx;
	return FeedbackColor(input.baseUV);
}

#endif
