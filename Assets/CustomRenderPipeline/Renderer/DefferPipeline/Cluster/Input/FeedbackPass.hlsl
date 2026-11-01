#ifndef FEED_BACK_PASS
#define FEED_BACK_PASS

// pageId 编码的唯一实现（mip 选择 + tile 定位 + 32bit 打包）。
// 与 GBuffer 采样侧（VTGetSampleUV）逐字同源，保证「请求的页 == 采样的页」。
#include "../../../Cluster/ClusterLibrary/VirtualTexture.hlsl"


struct Attributes_Simple {
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings_Simple {
	float4 positionCS_SS : SV_POSITION;
	float2 baseUV : VAR_BASE_UV;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings_Simple VertexSimple (Attributes_Simple input) {
	Varyings_Simple output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);

	output.positionCS_SS = TransformObjectToHClip(input.positionOS);

	output.baseUV = TransformBaseUV(input.baseUV);
	return output;
}

// 反馈色（= 编码后的 pageId）。
// 抽成独立函数：MeshRenderer 路径（fragSimple）和簇实例化路径（ClusterGBuffer.shader 的
// Feedback pass）必须用【同一份】编码，否则请求的页 ≠ 采样的页。
float4 FeedbackColor (float2 baseUV)
{
	// 编码细节全部在 VirtualTexture.hlsl 的 VTFeedbackPageColor 里（与地形共用）。
	// 这里只保留调试色（按 mip 着色，用来看「这一帧请求了多细的页」）。
#ifdef _FEED_BACK_DEBUG
	uint mip = (uint)VTMipLevel(baseUV);
	return 1.0 - ((float)mip / _VTFeedbackParam.z);
#else
	return VTFeedbackPageColor(baseUV, INPUT_PROP(uint, _MapId));
#endif
}

float4 fragSimple (Varyings_Simple input) : SV_Target
{
	UNITY_SETUP_INSTANCE_ID(input);
	return FeedbackColor(input.baseUV);
}

#endif
