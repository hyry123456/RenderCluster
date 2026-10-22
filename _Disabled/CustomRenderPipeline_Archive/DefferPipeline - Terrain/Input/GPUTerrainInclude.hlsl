#ifndef GPU_TERRAIN_INCLUDE
#define GPU_TERRAIN_INCLUDE

#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"



static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16, 32};

float4 _TerrainSize;
// float3 _BeginPosition;

TEXTURE2D(_HeighMap);
TEXTURE2D(_NormalMap);


float4 _TileData;



//采样法线
float3 SampleNormal(float2 uv){
    float3 normal;
    normal.xyz = SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv).xyz * 2 - 1;
    return normal;
}

struct VertexNormalInputs
{
    real3 tangentWS;
    real3 bitangentWS;
    float3 normalWS;
};

VertexNormalInputs GetVertexNormalInputs(float3 normalOS)
{
    VertexNormalInputs tbn;
    tbn.tangentWS = real3(1.0, 0.0, 0.0);
    tbn.bitangentWS = real3(0.0, 1.0, 0.0);
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    return tbn;
}
VertexNormalInputs GetVertexNormalInputs(float3 normalOS, float4 tangentOS)
{
    VertexNormalInputs tbn;

    // mikkts space compliant. only normalize when extracting normal at frag.
    real sign = tangentOS.w * GetOddNegativeScale();
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    tbn.tangentWS = TransformObjectToWorldDir(tangentOS.xyz);
    tbn.bitangentWS = cross(tbn.normalWS, tbn.tangentWS) * sign;
    return tbn;
}

struct TerrainColors {
    float4 albedo;
    float4 normal;
    float4 mask;
};

TerrainColors GetColor(float2 uv, TextureNode node){
	float4 levelData = _LevelData[node.mipsLevel],
		levelPadding = _LevelPadding[node.mipsLevel];

	//第一个是offset，然后加到目标位置，再加Padding
	float2 tileBegin = float2(levelData.x, 0) + levelData.yz * float2(node.width, node.height) + levelPadding.xy;
	//贴图值起点加上uv值
	uv = tileBegin + levelPadding.zw * uv;
    // detailUV = tileBegin + levelPadding.zw * detailUV;

#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
    // detailUV.y = 1.0 - detailUV.y;
#endif
	
	TerrainColors colors;
	colors.albedo   = SAMPLE_TEXTURE2D_LOD(_VirtualAlbedo,     sampler_linear_repeat, uv, 0);
    colors.normal   = SAMPLE_TEXTURE2D_LOD(_VirtualNormal,     sampler_linear_repeat, uv, 0);
    colors.mask     = SAMPLE_TEXTURE2D_LOD(_VirtualMask,   	   sampler_linear_repeat, uv, 0);
    colors.normal   = normalize(colors.normal * 2.0f - 1.0f);
	return colors;
}



TEXTURE2D(_AlbedoMap0);
TEXTURE2D(_AlbedoMap1);
TEXTURE2D(_AlbedoMap2);
TEXTURE2D(_AlbedoMap3);
TEXTURE2D(_NormalMap0);
TEXTURE2D(_NormalMap1);
TEXTURE2D(_NormalMap2);
TEXTURE2D(_NormalMap3);
TEXTURE2D(_MaskMap0);
TEXTURE2D(_MaskMap1);
TEXTURE2D(_MaskMap2);
TEXTURE2D(_MaskMap3);
TEXTURE2D(_BlendMap);

float4 _ScaleOffset0;
float4 _ScaleOffset1;
float4 _ScaleOffset2;
float4 _ScaleOffset3;
float3 _PBRData0;       //metallic, smoothness, normalScale
float3 _PBRData1;       //metallic, smoothness, normalScale
float3 _PBRData2;       //metallic, smoothness, normalScale
float3 _PBRData3;       //metallic, smoothness, normalScale

float4 _Color0;
float4 _Color1;
float4 _Color2;
float4 _Color3;

float2 _TileScale;
// float3 _TerrainSize;

TerrainColors DirectGetColor(float2 worldUV, float3 positionWS, out float alpha){
    float4 blend = SAMPLE_TEXTURE2D(_BlendMap, sampler_linear_repeat, worldUV);
    // float2 tileWorldSize = _TerrainSize.xz * _TileScale;
    float2 uv0 = positionWS.xz / _ScaleOffset0.xy + _ScaleOffset0.zw,
           uv1 = positionWS.xz / _ScaleOffset1.xy + _ScaleOffset1.zw,
           uv2 = positionWS.xz / _ScaleOffset2.xy + _ScaleOffset2.zw,
           uv3 = positionWS.xz / _ScaleOffset3.xy + _ScaleOffset3.zw;


    float4 albedo = SAMPLE_TEXTURE2D(_AlbedoMap0, sampler_linear_repeat, uv0) * blend.x * _Color0;
    albedo += SAMPLE_TEXTURE2D(_AlbedoMap1, sampler_linear_repeat, uv1) * blend.y * _Color1;
    albedo += SAMPLE_TEXTURE2D(_AlbedoMap2, sampler_linear_repeat, uv2) * blend.z * _Color2;
    albedo += SAMPLE_TEXTURE2D(_AlbedoMap3, sampler_linear_repeat, uv3) * blend.w * _Color3;

// albedo = SAMPLE_TEXTURE2D(_AlbedoMap0, sampler_linear_repeat, worldUV);
    // albedo = float4(frac(uv0), 0, 1);
    // albedo = 1;

    float3 normal = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap0, sampler_linear_repeat, uv0), _PBRData0.z) * blend.x;
    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap1, sampler_linear_repeat, uv1), _PBRData1.z) * blend.y;
    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap2, sampler_linear_repeat, uv2), _PBRData2.z) * blend.z;
    normal += UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap3, sampler_linear_repeat, uv3), _PBRData3.z) * blend.w;

    float3 m_r_a = 0;
    float4 mask = SAMPLE_TEXTURE2D(_MaskMap0, sampler_linear_repeat, uv0) * blend.x;
    m_r_a.x += mask.x * _PBRData0.x; m_r_a.y += mask.w * _PBRData0.y; m_r_a.z += mask.y;

    mask = SAMPLE_TEXTURE2D(_MaskMap1, sampler_linear_repeat, uv1) * blend.y;
    m_r_a.x += mask.x * _PBRData1.x; m_r_a.y += mask.w * _PBRData1.y; m_r_a.z += mask.y;

    mask = SAMPLE_TEXTURE2D(_MaskMap2, sampler_linear_repeat, uv2) * blend.z;
    m_r_a.x += mask.x * _PBRData2.x; m_r_a.y += mask.w * _PBRData2.y; m_r_a.z += mask.y;

    mask = SAMPLE_TEXTURE2D(_MaskMap3, sampler_linear_repeat, uv3) * blend.w;
    m_r_a.x += mask.x * _PBRData3.x; m_r_a.y += mask.w * _PBRData3.y; m_r_a.z += mask.y;

    alpha = blend.x + blend.y + blend.z + blend.w;

    TerrainColors colors;
    colors.albedo = albedo;
    colors.normal = float4(normal, 1);
    colors.mask = float4(m_r_a, 1);
    return colors;
}


#endif
