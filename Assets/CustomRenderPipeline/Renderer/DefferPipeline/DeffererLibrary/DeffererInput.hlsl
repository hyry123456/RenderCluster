#ifndef CUSTOMRP_DEFFER_INPUT
#define CUSTOMRP_DEFFER_INPUT

struct DefferTarget{
    float4 _GBufferRT0 : SV_Target0;	//rgb:abledo,w:metalness
    float4 _GBufferRT1 : SV_Target1;	//normal
    float4 _GBufferRT2 : SV_Target2;	//rgb:emissive,w:roughness
    float4 _GBufferRT3 : SV_Target3;	//rgb:reflect,w:AO
};

TEXTURE2D(_GBufferRT0);     //rgb:abledo,w:metalness
TEXTURE2D(_GBufferRT1);     //R,G:EncodeNormal
TEXTURE2D(_GBufferRT2);     //rgb:emissive,w:roughness
TEXTURE2D(_GBufferRT3);     //rgb:reflect,w:AO
TEXTURE2D(_GBufferDepthTex);     //rgb:reflect,w:AO

// UNITY_INSTANCING_BUFFER_START(DeffererInput)
CBUFFER_START(DeffererInput)

float4x4 _InverseProjectionMatrix;

float4x4 _ViewToScreenMatrix;

CBUFFER_END

// UNITY_INSTANCING_BUFFER_END(DeffererInput)

float3 GetNormal(float2 screenUV){
	float4 rt1 = SAMPLE_TEXTURE2D(_GBufferRT1, sampler_linear_clamp, screenUV);     	//R,G,B:normal
	return UnpackNormal(rt1.xyz);
}

float3 GetNormal_LOD(float2 screenUV, int lod){
	float4 rt1 = SAMPLE_TEXTURE2D_LOD(_GBufferRT1, sampler_linear_clamp, screenUV, lod);     	//R,G,B:normal
	return UnpackNormal(rt1.xyz);
}


// //depth是深度图深度
// float3 GetWorldPos(float depth, float2 uv){
//     #if defined(UNITY_REVERSED_Z)
//         depth = 1 - depth;
//     #endif
// 	float4 ndc = float4(uv.x * 2 - 1, uv.y * 2 - 1, depth * 2 - 1, 1);

// 	float4 worldPos = mul(_InverseVPMatrix, ndc);
// 	worldPos /= worldPos.w;
// 	return worldPos.xyz;
// }


#endif