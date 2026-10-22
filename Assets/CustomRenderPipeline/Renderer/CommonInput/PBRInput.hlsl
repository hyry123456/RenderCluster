#ifndef CUSTOMRP_PBR_INPUT
#define CUSTOMRP_PBR_INPUT

// #include "../../ShaderLibrary/Surface.hlsl"
// #include "../../ShaderLibrary/Shadows.hlsl"
// #include "../../ShaderLibrary/Light.hlsl"
// #include "../../ShaderLibrary/BRDF.hlsl"
// #include "../../ShaderLibrary/GI.hlsl"
// #include "../../ShaderLibrary/Lighting.hlsl"
// #include "../DeffererLibrary/DeffererInput.hlsl"

TEXTURE2D(_MaskMap);		//r:metallic Mask, g:AO, b:detail Mask, a:roughness Mask
TEXTURE2D(_NormalMap);
TEXTURE2D(_EmissionMap);

TEXTURE2D(_DetailMap);
TEXTURE2D(_DetailNormalMap);
SAMPLER(sampler_DetailMap);
TEXTURE2D(_MainTex);            //一般材质的主纹理位置
SAMPLER(sampler_MainTex);

// #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"

// TEXTURECUBE(unity_SpecCube0);
// SAMPLER(samplerunity_SpecCube0);


UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)

	UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _DetailMap_ST)
	UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
	UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
	UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
	UNITY_DEFINE_INSTANCED_PROP(float, _Roughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailAlbedo)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailRoughness)
	UNITY_DEFINE_INSTANCED_PROP(float, _DetailNormalScale)
	UNITY_DEFINE_INSTANCED_PROP(float, _NormalScale)

	UNITY_DEFINE_INSTANCED_PROP(float, _CullOff)
UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)


#define INPUT_PROP(name) UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, name)

struct InputConfig {
	float2 baseUV;
	float2 detailUV;
	bool useMask;
	bool useDetail;
};

float2 TransformBaseUV (float2 baseUV) {
	float4 baseST = INPUT_PROP(_MainTex_ST);
	return baseUV * baseST.xy + baseST.zw;
}

float GetCutoff(){
    return INPUT_PROP(_CullOff);
}

InputConfig GetInputConfig (float2 baseUV, float2 detailUV = 0.0) {
	InputConfig c;
	c.baseUV = baseUV;
	c.detailUV = detailUV;
	c.useMask = false;
	c.useDetail = false;
	return c;
}

// float2 TransformBaseUV (float2 baseUV) {
// 	float4 baseST = _MainTex_ST;
// 	return baseUV * baseST.xy + baseST.zw;
// }

float2 TransformDetailUV (float2 detailUV) {
	float4 detailST = INPUT_PROP(_DetailMap_ST);
	return detailUV * detailST.xy + detailST.zw;
}

float4 GetMask (InputConfig c) {
	if (c.useMask) {
		return SAMPLE_TEXTURE2D(_MaskMap, sampler_MainTex, c.baseUV);
	}
	return 1.0;
}

float4 GetDetail (InputConfig c) {
	if (c.useDetail) {
		float4 map = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, c.detailUV);
		return map * 2.0 - 1.0;
	}
	return 0.0;
}

float4 GetBase (InputConfig c) {
	float4 map = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, c.baseUV);
	float4 color = INPUT_PROP(_Color);

	if (c.useDetail) {
		float detail = GetDetail(c).r * INPUT_PROP(_DetailAlbedo);

		float mask = GetMask(c).b;
		map.rgb =
			lerp(sqrt(map.rgb), detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
		map.rgb *= map.rgb;
	
	}
	return map * color;
}

float4 GetBase (float2 baseUV) {
	float4 map = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, baseUV);
	float4 color = INPUT_PROP(_Color);

	return map * color;
}

float3 GetNormalTS (InputConfig c) {
	float4 map = SAMPLE_TEXTURE2D(_NormalMap, sampler_MainTex, c.baseUV);
	float scale = INPUT_PROP(_NormalScale);
	float3 normal = DecodeNormalSafe(map, scale);
	// 法线贴图绿通道为 DirectX 约定（Y 朝下），Unity 切线空间是 OpenGL 约定（Y 朝上）→ 必须翻转 Y。
	normal.y *= -1.0;

	if (c.useDetail) {
		map = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailMap, c.detailUV);
		float3 detail = DecodeNormal(map, 1.0f);
		float detailIntensity = INPUT_PROP(_DetailNormalScale) * GetMask(c).b;      //法线大小乘以detail遮罩

		float3 blendNor = BlendNormalRNM(normal, detail);
		return lerp(normal, blendNor, detailIntensity);
	}
	
	return normal;
}

float3 GetEmission (InputConfig c) {
	float4 map = SAMPLE_TEXTURE2D(_EmissionMap, sampler_MainTex, c.baseUV);
	float4 color = INPUT_PROP(_EmissionColor);
	return map.rgb * color.rgb;
}

float GetMetallic (InputConfig c) {
	float metallic = INPUT_PROP(_Metallic);
	metallic *= GetMask(c).r;
	return metallic;
}

float GetRoughness (InputConfig c) {
	float roughness = INPUT_PROP(_Roughness);
	roughness *= GetMask(c).w;

	if (c.useDetail) {
		float detail = GetDetail(c).b * INPUT_PROP(_DetailRoughness);
		float mask = GetMask(c).g;
		roughness =
			lerp(roughness, detail < 0.0 ? 0.0 : 1.0, abs(detail) * mask);
	}
	
	return roughness;
}

float GetOcclusion(InputConfig c){
	float occlusion = 1;
	if(c.useMask){
		occlusion = GetMask(c).y;
	}
	return occlusion;
}

#endif