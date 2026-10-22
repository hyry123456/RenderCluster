#ifndef CUSTOM_GI_INCLUDED
#define CUSTOM_GI_INCLUDED

// ============================================================================
// 本文件原先是：
//     #include ".../core/ShaderLibrary/EntityLighting.hlsl"
//     #include ".../core/ShaderLibrary/ImageBasedLighting.hlsl"
// 两个都拿掉了，原因：
//
// 1) ImageBasedLighting.hlsl —— 全工程没有任何一处使用它提供的符号
//    （GlossyEnvironmentReflection / SampleEnvironment / PowerImportanceSampling 等全部无人调用），
//    却白白拖进 CommonLighting / CommonMaterial / BSDF / Random / Sampling 一大串核心文件。
//
// 2) EntityLighting.hlsl —— 里面有
//        #if defined(UNITY_DOTS_INSTANCING_ENABLED)
//        #define TEXTURE2D_LIGHTMAP_PARAM  TEXTURE2D_ARRAY_PARAM
//        #define SAMPLE_TEXTURE2D_LIGHTMAP SAMPLE_TEXTURE2D_ARRAY
//        #define LIGHTMAP_EXTRA_ARGS      float2 uv, float slice
//        #endif
//     DOTS 分支会把 lightmap 采样整套宏改成 Texture2DArray 版，并给 SampleSingleLightmap
//    多加一个 slice 参数。本工程只有 Unlit/ClusterPBR 的 OutGBuffer pass 会走进 DOTS 分支
//    （它是唯一既 include 本文件、又开 DOTS_INSTANCING_ON 的 pass），
//    结果就是 BRG 一开，该 pass 的 DOTS 变体编译失败。
//    注：DefferStandard.shader 没有 DOTS_INSTANCING_ON，永远走不到该分支，所以以前没暴露。
//
// 现在改为自包含：只保留本工程真正用到的几个函数，一律用非 DOTS 的 Texture2D 形式，
// 数学与 core 逐字一致，渲染行为零变化。
//
// SampleSH9 / SHEvalLinearL0L1 来自 core SphericalHarmonics.hlsl —— 该文件是干净的
// （只 include Color.hlsl → ACES.hlsl，无 include 环、无 DOTS 分支、不碰 unity_ObjectToWorld），
// 可以安全直连。
// ============================================================================
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"

// ---- 以下两段来自 core ImageBasedLighting.hlsl（原本靠 include 提供，已内联）----
// 全工程符号审计结论：ImageBasedLighting.hlsl 定义的 20 个符号里，只有
// PerceptualRoughnessToMipmapLevel 被 GI.hlsl 的调用链用到（ClusterPass.hlsl:119、
// CommonInput/PBRPass.hlsl:129、DefferPipeline/PBR/Input/GBufferPass.hlsl:105，三处都是单参重载）；
// 其余（ImportanceSampleGGX / GetAnisotropicModifiedNormal 等）都在工程自带的
// DeffererLibrary/ImageBasedLighting.hlsl 里有副本，不受影响。
#ifndef UNITY_SPECCUBE_LOD_STEPS
#define UNITY_SPECCUBE_LOD_STEPS 6
#endif

// core 原版： perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) 再乘 mip 级数
real PerceptualRoughnessToMipmapLevel(real perceptualRoughness, uint maxMipLevel)
{
    perceptualRoughness = perceptualRoughness * (1.7 - 0.7 * perceptualRoughness);

    return perceptualRoughness * maxMipLevel;
}

real PerceptualRoughnessToMipmapLevel(real perceptualRoughness)
{
    return PerceptualRoughnessToMipmapLevel(perceptualRoughness, UNITY_SPECCUBE_LOD_STEPS);
}

#define LIGHTMAP_RGBM_MAX_GAMMA     real(5.0)       // NB: Must match value in RGBMRanges.h
#define LIGHTMAP_RGBM_MAX_LINEAR    real(34.493242) // LIGHTMAP_RGBM_MAX_GAMMA ^ 2.2

#ifdef UNITY_LIGHTMAP_RGBM_ENCODING
    #ifdef UNITY_COLORSPACE_GAMMA
        #define LIGHTMAP_HDR_MULTIPLIER LIGHTMAP_RGBM_MAX_GAMMA
        #define LIGHTMAP_HDR_EXPONENT   real(1.0)   // Not used in gamma color space
    #else
        #define LIGHTMAP_HDR_MULTIPLIER LIGHTMAP_RGBM_MAX_LINEAR
        #define LIGHTMAP_HDR_EXPONENT   real(2.2)
    #endif
#elif defined(UNITY_LIGHTMAP_DLDR_ENCODING)
    #ifdef UNITY_COLORSPACE_GAMMA
        #define LIGHTMAP_HDR_MULTIPLIER real(2.0)
    #else
        #define LIGHTMAP_HDR_MULTIPLIER real(4.59) // 2.0 ^ 2.2
    #endif
    #define LIGHTMAP_HDR_EXPONENT real(0.0)
#else // (UNITY_LIGHTMAP_FULL_HDR)
    #define LIGHTMAP_HDR_MULTIPLIER real(1.0)
    #define LIGHTMAP_HDR_EXPONENT real(1.0)
#endif

real3 UnpackLightmapRGBM(real4 rgbmInput, real4 decodeInstructions)
{
#ifdef UNITY_COLORSPACE_GAMMA
    return rgbmInput.rgb * (rgbmInput.a * decodeInstructions.x);
#else
    return rgbmInput.rgb * (PositivePow(rgbmInput.a, decodeInstructions.y) * decodeInstructions.x);
#endif
}

real3 UnpackLightmapDoubleLDR(real4 encodedColor, real4 decodeInstructions)
{
    return encodedColor.rgb * decodeInstructions.x;
}

real3 DecodeLightmap(real4 encodedIlluminance, real4 decodeInstructions)
{
#if defined(UNITY_LIGHTMAP_RGBM_ENCODING)
    return UnpackLightmapRGBM(encodedIlluminance, decodeInstructions);
#elif defined(UNITY_LIGHTMAP_DLDR_ENCODING)
    return UnpackLightmapDoubleLDR(encodedIlluminance, decodeInstructions);
#else // (UNITY_LIGHTMAP_FULL_HDR)
    return encodedIlluminance.rgb;
#endif
}

real3 DecodeHDREnvironment(real4 encodedIrradiance, real4 decodeInstructions)
{
    // Take into account texture alpha if decodeInstructions.w is true(the alpha value affects the RGB channels)
    real alpha = max(decodeInstructions.w * (encodedIrradiance.a - 1.0) + 1.0, 0.0);
    return (decodeInstructions.x * PositivePow(alpha, decodeInstructions.y)) * encodedIrradiance.rgb;
}

// 非 DOTS 形式：Texture2D + 单个 uv，没有 slice 参数。
// （core 的 DOTS 分支会把签名改成 Texture2DArray + slice，正是那边导致的编译失败。）
real3 SampleSingleLightmap(TEXTURE2D_PARAM(lightmapTex, lightmapSampler), float2 uv, float4 transform, bool encodedLightmap, real4 decodeInstructions)
{
    // transform is scale and bias
    uv = uv * transform.xy + transform.zw;
    real3 illuminance = real3(0.0, 0.0, 0.0);
    // Remark: baked lightmap is RGBM for now, dynamic lightmap is RGB9E5
    if (encodedLightmap)
    {
        real4 encodedIlluminance = SAMPLE_TEXTURE2D(lightmapTex, lightmapSampler, uv).rgba;
        illuminance = DecodeLightmap(encodedIlluminance, decodeInstructions);
    }
    else
    {
        illuminance = SAMPLE_TEXTURE2D(lightmapTex, lightmapSampler, uv).rgb;
    }
    return illuminance;
}

// Sample a 3D volume storing SH. Volume is stored as 3D texture with 4 R, G, B, Occ set of
// 4 coefficient stored atlas in the same 3D texture. Occ is used for occlusion.
// 完整重载。注意签名必须与 core EntityLighting.hlsl 逐字一致：
// positionWS, normalWS, **backNormalWS**, WorldToTexture, ...（backNormalWS 不能漏，
// 函数体第 132 行用到它； shortcut 重载靠它传 backNormalWSUnused 进来）
void SampleProbeVolumeSH4(TEXTURE3D_PARAM(SHVolumeTexture, SHVolumeSampler), float3 positionWS, float3 normalWS, float3 backNormalWS, float4x4 WorldToTexture,
                            float transformToLocal, float texelSizeX, float3 probeVolumeMin, float3 probeVolumeSizeInv,
                            inout float3 bakeDiffuseLighting, inout float3 backBakeDiffuseLighting)
{
    float3 position = (transformToLocal == 1.0) ? mul(WorldToTexture, float4(positionWS, 1.0)).xyz : positionWS;
    float3 texCoord = (position - probeVolumeMin) * probeVolumeSizeInv.xyz;
    // Each component is stored in the same 3D texture. Each uses one quarter on the x axis.
    // Here we get the R component then increase by step size (0.25) to get other components.
    // This assumes 4 components but the last one is not used.
    // Clamp to edge of the "internal" texture, as R is from half texel to size of R texture minus half texel.
    // This avoids leaking.
    texCoord.x = clamp(texCoord.x * 0.25, 0.5 * texelSizeX, 0.25 - 0.5 * texelSizeX);

    float4 shAr = SAMPLE_TEXTURE3D_LOD(SHVolumeTexture, SHVolumeSampler, texCoord, 0);
    texCoord.x += 0.25;
    float4 shAg = SAMPLE_TEXTURE3D_LOD(SHVolumeTexture, SHVolumeSampler, texCoord, 0);
    texCoord.x += 0.25;
    float4 shAb = SAMPLE_TEXTURE3D_LOD(SHVolumeTexture, SHVolumeSampler, texCoord, 0);

    bakeDiffuseLighting += SHEvalLinearL0L1(normalWS, shAr, shAg, shAb);
    backBakeDiffuseLighting += SHEvalLinearL0L1(backNormalWS, shAr, shAg, shAb);
}

// Just a shortcut that calls the function above
float3 SampleProbeVolumeSH4(TEXTURE3D_PARAM(SHVolumeTexture, SHVolumeSampler), float3 positionWS, float3 normalWS, float4x4 WorldToTexture,
                                float transformToLocal, float texelSizeX, float3 probeVolumeMin, float3 probeVolumeSizeInv)
{
    float3 backNormalWSUnused = 0.0;
    float3 bakeDiffuseLighting = 0.0;
    float3 backBakeDiffuseLightingUnused = 0.0;
    SampleProbeVolumeSH4(TEXTURE3D_ARGS(SHVolumeTexture, SHVolumeSampler), positionWS, normalWS, backNormalWSUnused, WorldToTexture,
                            transformToLocal, texelSizeX, probeVolumeMin, probeVolumeSizeInv,
                            bakeDiffuseLighting, backBakeDiffuseLightingUnused);
    return bakeDiffuseLighting;
}

TEXTURE2D(unity_Lightmap);
SAMPLER(samplerunity_Lightmap);

TEXTURE2D(unity_ShadowMask);
SAMPLER(samplerunity_ShadowMask);

TEXTURE3D_FLOAT(unity_ProbeVolumeSH);
SAMPLER(samplerunity_ProbeVolumeSH);

// TEXTURE2D(_SSSTargetTex);
// SAMPLER(sampler_SSSTargetTex);
// TEXTURE2D(_SSSPyramid1);
// SAMPLER(sampler_SSSPyramid2);
// TEXTURE2D(_SSSPyramid3);
// float4 _SSSTargetTex_TexelSize;

TEXTURECUBE(unity_SpecCube0);
SAMPLER(samplerunity_SpecCube0);
TEXTURECUBE(unity_SpecCube1);
SAMPLER(samplerunity_SpecCube1);

TEXTURECUBE(_MySkybox);
SAMPLER(sampler_MySkybox);



TEXTURE2D(_PlaneReflectTex);
float3 _PlaneFront;
float3 _PlaneRight;


#if defined(LIGHTMAP_ON)
	#define GI_ATTRIBUTE_DATA float2 lightMapUV : TEXCOORD1;
	// #define GI_ATTRIBUTE_DATA float2 lightMapUV : TEXCOORD0;
	#define GI_VARYINGS_DATA float2 lightMapUV : VAR_LIGHT_MAP_UV;
	#define TRANSFER_GI_DATA(input, output) \
		output.lightMapUV = input.lightMapUV * \
		unity_LightmapST.xy + unity_LightmapST.zw;
	#define GI_FRAGMENT_DATA(input) input.lightMapUV
#else
	#define GI_ATTRIBUTE_DATA
	#define GI_VARYINGS_DATA
	#define TRANSFER_GI_DATA(input, output)
	#define GI_FRAGMENT_DATA(input) 0.0
#endif


float3 SampleLightMap (float2 lightMapUV) {
	#if defined(LIGHTMAP_ON)
  		return SampleSingleLightmap(
			TEXTURE2D_ARGS(unity_Lightmap, samplerunity_Lightmap), lightMapUV,
			float4(1.0, 1.0, 0.0, 0.0),
			#if defined(UNITY_LIGHTMAP_FULL_HDR)
				false,
			#else
				true,
			#endif
			float4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0, 0.0)
	);
	#else
		return 0.0;
	#endif
}


	//重新映射反射方向
inline half3 BoxProjectedDirection(half3 worldRefDir, float3 worldPos, float4 cubemapCenter, float4 boxMin, float4 boxMax)
{
	if (cubemapCenter.w > 0.0)
	{
		boxMax.xyz -= worldPos;
		boxMin.xyz -= worldPos;

		float x = (worldRefDir.x > 0 ? boxMax.x : boxMin.x) / worldRefDir.x;
		float y = (worldRefDir.y > 0 ? boxMax.y : boxMin.y) / worldRefDir.y;
		float z = (worldRefDir.z > 0 ? boxMax.z : boxMin.z) / worldRefDir.z;
		float scalar = min(min(x, y), z);

		return worldRefDir * scalar + (worldPos - cubemapCenter.xyz);
	}
	return worldRefDir;
}

inline half3 SamplerReflectProbe0(half3 refDir, float mip_Level)
{
	float4 environment = SAMPLE_TEXTURECUBE_LOD(
		unity_SpecCube0, samplerunity_SpecCube0, refDir, mip_Level
	);
	return DecodeHDREnvironment(environment, unity_SpecCube0_HDR);
}

inline half3 SamplerReflectProbe1(half3 refDir, float mip_Level)
{
	float4 environment = SAMPLE_TEXTURECUBE_LOD(
		unity_SpecCube1, samplerunity_SpecCube0, refDir, mip_Level
	);
	return DecodeHDREnvironment(environment, unity_SpecCube1_HDR);
}

inline half3 SamplerReflectProbe(half3 refDir, float mip_Level)
{
	float4 environment = SAMPLE_TEXTURECUBE_LOD(
		_MySkybox, sampler_MySkybox, refDir, mip_Level
	);
	return environment.xyz;
}



//计算间接光镜面反射
inline float3 ComputeIndirectSpecular(half3 refDir, float3 worldPos, float mip_Level)
{
	half3 refDir1 = BoxProjectedDirection(refDir, worldPos, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
	half3 ref1 = SamplerReflectProbe0(refDir1, mip_Level);
	float3 specular;

	if (unity_SpecCube0_BoxMin.w < 0.99999)
	{
		//重新映射第二个反射探头的方向
		half3 refDir2 = BoxProjectedDirection(refDir, worldPos, unity_SpecCube1_ProbePosition, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
		//对第二个反射探头进行采样,这个UNITY_PASS_TEXCUBE_SAMPLER可以用来防止如果未定义unity_SpecCube1时的报错，因为其会自动转化为采集unity_SpecCube0
		//如果超过了范围那么这个值也会小于1的
		half3 ref2 = SamplerReflectProbe1(refDir2, mip_Level);

		//进行混合
		specular = lerp(ref2, ref1, unity_SpecCube0_BoxMin.w);
	}
	else
	{
		specular = ref1;
	}

	return ref1;
}

inline half3 PlaneReflect(float2 screenUV, half3 normalDir, float mips){
	//噪声
	float2 offset = float2(
		dot(_PlaneFront, normalDir) * 60 * _CameraBufferSize.z,
		dot(_PlaneRight, normalDir) * 60 * _CameraBufferSize.w
	);

	return SAMPLE_TEXTURE2D_LOD(_PlaneReflectTex, sampler_linear_clamp, screenUV + offset, mips).xyz;
}



float3 SampleLightProbe (float3 positionWS, float3 normalWS) {
	#if defined(LIGHTMAP_ON)
		return 0.0;
	#else
		if (unity_ProbeVolumeParams.x) {
			return SampleProbeVolumeSH4(
				TEXTURE3D_ARGS(unity_ProbeVolumeSH, samplerunity_ProbeVolumeSH),
				positionWS, normalWS,
				unity_ProbeVolumeWorldToObject,
				unity_ProbeVolumeParams.y, unity_ProbeVolumeParams.z,
				unity_ProbeVolumeMin.xyz, unity_ProbeVolumeSizeInv.xyz
			);
		}
		else {
			float4 coefficients[7];
			coefficients[0] = unity_SHAr;
			coefficients[1] = unity_SHAg;
			coefficients[2] = unity_SHAb;
			coefficients[3] = unity_SHBr;
			coefficients[4] = unity_SHBg;
			coefficients[5] = unity_SHBb;
			coefficients[6] = unity_SHC;
			return max(0.0, SampleSH9(coefficients, normalWS));
		}
	#endif
}

float3 SampleSH(float3 normalWS){
	float4 coefficients[7];
	coefficients[0] = unity_SHAr;
	coefficients[1] = unity_SHAg;
	coefficients[2] = unity_SHAb;
	coefficients[3] = unity_SHBr;
	coefficients[4] = unity_SHBg;
	coefficients[5] = unity_SHBb;
	coefficients[6] = unity_SHC;
	return max(0.0, SampleSH9(coefficients, normalWS));
}


float3 GetBakeDate(float2 lightMapUV, float3 positionWS, float3 normalWS){
	return SampleLightMap(lightMapUV) + SampleLightProbe(positionWS, normalWS);
}


static float RCP_PI = rcp(PI);

//==============直角坐标系下的3阶球谐函数============//

//l = 0,m = 0
float GetY00(float3 xyz){
    return 0.5 * sqrt(RCP_PI);
}

//l = 1,m = 0
float GetY10(float3 p){
    return 0.5 * sqrt(3 * RCP_PI) * p.z;
}

//l = 1,m = 1
float GetY1p1(float3 p){
    return 0.5 * sqrt(3 * RCP_PI) * p.x;
}

//l = 1,m = -1
float GetY1n1(float3 p){
    return 0.5 * sqrt(3 * RCP_PI) * p.y;
}

//l = 2, m = 0
float GetY20(float3 p){
    return 0.25 * sqrt(5 * RCP_PI) * (2*p.z*p.z - p.x*p.x -p.y*p.y);
}

//l = 2, m = 1
float GetY2p1(float3 p){
    return 0.5 * sqrt(15 * RCP_PI) * p.z * p.x;
}

//l = 2, m = -1
float GetY2n1(float3 p){
    return 0.5 * sqrt(15 * RCP_PI) * p.z * p.y;
}

//l = 2, m = 2
float GetY2p2(float3 p){
    return 0.25 * sqrt(15 * RCP_PI) * (p.x * p.x - p.y * p.y);
}

//l = 2, m = -2
float GetY2n2(float3 p){
    return 0.5 * sqrt(15 * RCP_PI) * p.x * p.y;
}


float4 GetIndirectLight(float3 normalWS){
	float4 color =
	_Sh9[0] * GetY00(normalWS) +
	_Sh9[1] * GetY1n1(normalWS) +
	_Sh9[2] * GetY10(normalWS) +
	_Sh9[3] * GetY1p1(normalWS) +
	_Sh9[4] * GetY2n2(normalWS) +
	_Sh9[5] * GetY2n1(normalWS) +
	_Sh9[6] * GetY20(normalWS) +
	_Sh9[7] * GetY2p1(normalWS) +
	_Sh9[8] * GetY2p2(normalWS);
	return color;
}

#endif
