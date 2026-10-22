#ifndef CUSTOMRP_OCREANPLANE
#define CUSTOMRP_OCREANPLANE

float2 _WaveMainDirection;
float _MaxSteepness;
float _MinSteepness;
float _MaxWaveLength;
float _MinWaveLength;
float _RandomIntensity;
float KWS_SunCloudiness;
float KWS_SunStrength;
int _WaveCount;
uint KWS_WaterPassID;

//----------------------------------------------KWS Water---------------------------

#define KWS_WATER_MASK_DECODING_VALUE 1.0/255.0


float CalcMipLevel(float2 uv)
{
    float2 dx = ddx(uv);
    float2 dy = ddy(uv);
    float delta = max(dot(dx, dx), dot(dy, dy));
    return max(0.0, 0.5 * log2(delta));
}

half KW_FFTDomainSize;
half KW_FFTDomainSize_LOD1;
half KW_FFTDomainSize_LOD2;
uint KWS_UseMultipleSimulations;

float KWS_RefractionAproximatedDepth;
float KWS_RefractionSimpleStrength;
float KWS_RefractionDispersionStrength;

float KWS_ReflectionClipOffset;


float4 KW_TurbidityColor;
float4 KW_WaterColor;
float3 KWS_AmbientColor;
float KW_Transparent;
float KW_WindSpeed;
float KW_Turbidity;
float4x4 KWS_MATRIX_I_VP;
 
TEXTURE2D (KW_WaterMaskScatterNormals);
float4 KWS_WaterMask_RTHandleScale;


Texture2D					KW_DispTex;
Texture2D					KW_DispTex_LOD1;
Texture2D					KW_DispTex_LOD2;
Texture2D					KW_NormTex;
SamplerState sampler_KW_NormTex;
Texture2D					KW_NormTex_LOD1;
Texture2D					KW_NormTex_LOD2;

TextureCube KWS_CubemapReflectionRT;



uint KWS_UseRefractionIOR;
uint KWS_UseRefractionDispersion;
uint KWS_UnderwaterVisible;


inline float GetWorldToCameraDistance(float3 worldPos)
{
	return length(_WorldSpaceCameraPos.xyz - worldPos.xyz);
}

inline float3 WorldToLocalPosWithoutTranslation(float3 worldPos)
{
	return mul((float3x3)UNITY_MATRIX_I_M, worldPos).xyz;
}

inline half3 GetNormal_lod0(float2 uv)
{
	return KW_NormTex.Sample(sampler_KW_NormTex, uv).xyz;
}

inline half3 KWS_BlendNormals(half3 n1, half3 n2)
{
	return (half3(n1.xz + n2.xz, n1.y * n2.y).xzy);
}

inline half3 KWS_BlendNormals(half3 n1, half3 n2, half3 n3)
{
	return (half3(n1.xz + n2.xz + n3.xz, 1).xzy);
}


inline half3 GetNormal_lod1_lod2(float3 worldPos, half3 normal)
{
	half3 norm_lod1 = KW_NormTex_LOD1.Sample(sampler_KW_NormTex, worldPos.xz / KW_FFTDomainSize_LOD1).xyz;
	half3 norm_lod2 = KW_NormTex_LOD2.Sample(sampler_KW_NormTex, worldPos.xz / KW_FFTDomainSize_LOD2).xyz;
	return KWS_BlendNormals(normal, norm_lod1, norm_lod2);
}

inline float2 GetRefractedUV_Simple(float2 uv, half3 normal)
{
	return uv + normal.xz * KWS_RefractionSimpleStrength * 0.5;
}

inline float3 GetCameraRelativePosition(float3 worldPos)
{
	return GetCameraRelativePositionWS(worldPos);
}

half3 ComputeWaterRefractRay(half3 viewDir, half3 normal, half depth)
{
	half nv = dot(normal, viewDir);
	half v2 = dot(viewDir, viewDir);
	half knormal = (sqrt(((1.7689 - 1.0) * v2) / (nv * nv) + 1.0) - 1.0) * nv;
	half3 result = depth * (viewDir + (knormal * normal));
	result.y = result.y * 0.35; //fix information lost in the near camera
	return result;
}


inline float2 GetRefractedUV_IOR(float3 viewDir, half3 normal, float3 worldPos, float surfaceTensionFade)
{
	float3 refractedRay = ComputeWaterRefractRay(-viewDir, normal, KWS_RefractionAproximatedDepth * surfaceTensionFade);

	float4 refractedClipPos = mul(UNITY_MATRIX_VP, float4(GetCameraRelativePosition(worldPos + refractedRay), 1.0));
	float4 refractionScreenPos = ComputeScreenPos(refractedClipPos);
	float2 uv = refractionScreenPos.xy / refractionScreenPos.w;
	return uv;
}

inline half3 GetSceneColorWithDispersion(float2 uv, float dispersionStrength)
{
	half3 refraction;
	refraction.r = GetBufferColor(uv - _CameraColorTexture_TexelSize.xy * dispersionStrength).r;
	refraction.g = GetBufferColor(uv).g;
	refraction.b = GetBufferColor(uv + _CameraColorTexture_TexelSize.xy * dispersionStrength).b;
	return refraction;
}

inline float3 GetAmbientColor()
{
	return KWS_AmbientColor;
}

float3 ComputeWaterOffset(float3 worldPos)
{
	float2 uv = worldPos.xz / KW_FFTDomainSize;
	// return float3(uv, 1);
	float3 offset = 0;
	#if defined(USE_FILTERING) || defined(USE_CAUSTIC_FILTERING)
		float3 disp = Texture2DSampleLevelBicubic(KW_DispTex, sampler_linear_repeat, uv, KW_DispTex_TexelSize, 0).xyz;
	#else
		float3 disp = KW_DispTex.SampleLevel(sampler_linear_repeat, uv, 0).xyz;
	#endif

	float distanceToCamera = GetWorldToCameraDistance(worldPos);
	
	float3 displacementScaleRelativeToDistance = lerp(float3(0.9, 1, 0.9), float3(0.25, 1, 0.25), saturate(distanceToCamera * 0.001));
	//displacementScaleRelativeToDistance = lerp(displacementScaleRelativeToDistance, float3(0.0, Test4.x, 0.0), KWS_UnderwaterVisible * saturate(distanceToCamera * 0.001));
	disp *= displacementScaleRelativeToDistance;
	
	#if defined(KW_FLOW_MAP) || defined(KW_FLOW_MAP_EDIT_MODE)
		float2 flowMapUV = (worldPos.xz - KW_FlowMapOffset.xz) / KW_FlowMapSize + 0.5;
		float2 flowmap = KW_FlowMapTex.SampleLevel(sampler_linear_clamp, flowMapUV, 0) * 2 - 1;
		disp = ComputeDisplaceUsingFlowMap(KW_DispTex, sampler_linear_repeat, flowmap, disp, uv, KW_Time * KW_FlowMapSpeed);
	#endif

	#if KW_DYNAMIC_WAVES
		float2 dynamicWavesUV = (worldPos.xz - KW_DynamicWavesWorldPos.xz) / KW_DynamicWavesAreaSize + 0.5;
		float dynamicWave = KW_DynamicWaves.SampleLevel(sampler_linear_clamp, dynamicWavesUV, 0).x;
		disp.y -= dynamicWave * 0.15;
	#endif

	#if defined(KW_FLOW_MAP_FLUIDS) && !defined(KW_FLOW_MAP_EDIT_MODE)
		float2 fluidsUV_lod0 = (worldPos.xz - KW_FluidsMapWorldPosition_lod0.xz) / KW_FluidsMapAreaSize_lod0 + 0.5;
		float2 fluids_lod0 = KW_Fluids_Lod0.SampleLevel(sampler_linear_clamp, fluidsUV_lod0, 0).xy;

		float2 fluidsUV_lod1 = (worldPos.xz - KW_FluidsMapWorldPosition_lod1.xz) / KW_FluidsMapAreaSize_lod1 + 0.5;
		float2 fluids_lod1 = KW_Fluids_Lod1.SampleLevel(sampler_linear_clamp, fluidsUV_lod1, 0).xy;

		float2 maskUV_lod0 = 1 - saturate(abs(fluidsUV_lod0 * 2 - 1));
		float lodLevelFluidMask_lod0 = saturate((maskUV_lod0.x * maskUV_lod0.y - 0.01) * 3);
		float2 maskUV_lod1 = 1 - saturate(abs(fluidsUV_lod0 * 2 - 1));
		float lodLevelFluidMask_lod1 = saturate((maskUV_lod1.x * maskUV_lod1.y - 0.01) * 3);

		float2 fluids = lerp(fluids_lod1, fluids_lod0, lodLevelFluidMask_lod0);
		fluids *= lodLevelFluidMask_lod1;
		disp = ComputeDisplaceUsingFlowMap(KW_DispTex, sampler_linear_repeat, fluids * KW_FluidsVelocityAreaScale * 0.75, disp, uv, KW_Time * KW_FlowMapSpeed).xyz;
	#endif

	if (KWS_UseMultipleSimulations > 0)
	{
		disp += KW_DispTex_LOD1.SampleLevel(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD1, 0).xyz * displacementScaleRelativeToDistance;
		disp += KW_DispTex_LOD2.SampleLevel(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD2, 0).xyz * displacementScaleRelativeToDistance;
	}
	

	#if USE_SHORELINE
		disp = ComputeShorelineOffset(worldPos, disp);
	#endif

	offset += disp;

	return offset;
}


float3 GetWaterOffsetRelativeToMask(float surfaceMaskY, float3 worldPos)
{
	if (surfaceMaskY > 0.001)
	{
		float3 waterOffset = ComputeWaterOffset(worldPos);
		// waterOffset = 0;
		// if (surfaceMaskY < 0.51) waterOffset.xz *= 0;
		// return WorldToLocalPosWithoutTranslation(waterOffset);
		return waterOffset + worldPos;		//可以直接返回，但是要限制为范围型，估计无限水的波纹有区别
	}
	else return float3(0, 0, 0);
}

float2 GetScreenSpaceReflectionUV(float3 reflDir)
{
	reflDir.y = -reflDir.y;
	float4 projected = mul(UNITY_MATRIX_VP, float4(reflDir, 0));
	float2 uv = (projected.xy / projected.w) * 0.5f + 0.5f;
	
	#ifdef UNITY_UV_STARTS_AT_TOP
		uv.y = 1 - uv.y;
	#endif

	return uv;
}

TEXTURE2D(KWS_ScreenSpaceReflectionRT);
float4 KWS_ScreenSpaceReflectionRT_TexelSize;
float4 KWS_ScreenSpaceReflection_RTHandleScale;
#define KWS_ScreenSpaceReflectionMaxMip 5

inline float2 GetScreenSpaceReflectionNormalizedUV(float2 uv)
{
	return clamp(uv, 0.001, 0.999) * KWS_ScreenSpaceReflection_RTHandleScale.xy;
}



inline half4 GetScreenSpaceReflection(float2 uv)
{
	float2 ssrUV = GetScreenSpaceReflectionNormalizedUV(uv);

	float mipLevel = CalcMipLevel(ssrUV * KWS_ScreenSpaceReflectionRT_TexelSize.zw);
	float lod = min(mipLevel, KWS_ScreenSpaceReflectionMaxMip);
	float4 res = SAMPLE_TEXTURE2D_LOD(KWS_ScreenSpaceReflectionRT, sampler_linear_clamp, ssrUV, 0);
	res.a = saturate(res.a); //I use negative alpha to minimize edge bilinear interpolation artifacts. 
	return res;
}

inline half4 GetScreenSpaceReflectionWithStretchingMask(float2 refl_uv)
{
	#if defined(STEREO_INSTANCING_ON)
		refl_uv -= mul((float2x2)UNITY_MATRIX_V, float2(0, KWS_ReflectionClipOffset)).xy;
	#else
		refl_uv.y -= KWS_ReflectionClipOffset;
	#endif
	

	float stretchingMask = 1 - abs(refl_uv.x * 2 - 1);
	//stretchingMask = min(1, stretchingMask * 3);
	refl_uv.x = lerp(refl_uv.x * 0.98 + 0.01, refl_uv.x, stretchingMask);
	// return float4(refl_uv, 0, 1);
	return GetScreenSpaceReflection(refl_uv);

	//reflection.xyz = lerp(reflection.xyz, ssr.xyz, ssr.a);

}

inline half3 GetCubemapReflection(float3 reflDir)
{
	// reflDir.y = -reflDir.y;
	
	//#if defined(STEREO_INSTANCING_ON)
	//		return UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, 1.0);
	//#else 
			return KWS_CubemapReflectionRT.Sample(sampler_linear_clamp, reflDir).xyz;
	//#endif
	
}

float ComputeWaterFresnel(half3 normal, half3 viewDir)
{
	float x = 1 - saturate(dot(normal, viewDir));
	return 0.02 + 0.98 * x * x * x * x * x * x; //fresnel aproximation http://wiki.nuaj.net/images/thumb/1/16/Fresnel.jpg/800px-Fresnel.jpg

}

inline float3 GetWorldSpaceViewDirNorm(float3 worldPos)
{
	return normalize(_WorldSpaceCameraPos.xyz - worldPos.xyz);
}

inline half GetWaterRawFade(float3 worldPos, float surfaceDepthZ, float refractedSceneZ, half surfaceMask, half depthAngleFix)
{
	return (LinearEyeDepth(refractedSceneZ, _ZBufferParams) - surfaceDepthZ) * depthAngleFix;
}

float3 GetWorldSpacePositionFromDepth(float2 uv, float deviceDepth)
{
	return GetAbsolutePositionWS(ComputeWorldSpacePosition(uv, deviceDepth, KWS_MATRIX_I_VP)); //UNITY_MATRIX_I_VP have bugs in VR
}

TEXTURE2D (KWS_VolumetricLightRT);
float4 KWS_VolumetricLightRT_TexelSize;
// float4 KWS_VolumetricLight_RTHandleScale;

inline float2 GetVolumetricLightNormalizedUV(float2 uv)
{
	return clamp(uv, 0.005, 0.995);
}

inline half4 GetVolumetricLight(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(KWS_VolumetricLightRT, sampler_linear_clamp, GetVolumetricLightNormalizedUV(uv), 0);
}


inline void FixAboveWaterRendering(float2 refractionUV, float refractedSceneZ, float3 worldPos, float sceneZ, float surfaceDepthZ, float depthAngleFix, float2 screenUV, half surfaceMask, inout float fade, inout half3 refraction, inout half4 volumeScattering)
{
	float3 worldSpaceDepth = GetWorldSpacePositionFromDepth(refractionUV, refractedSceneZ);

	UNITY_BRANCH if (surfaceMask > 0.5 && worldSpaceDepth.y > worldPos.y + 0.25)
	{
		fade = (LinearEyeDepth(sceneZ, _ZBufferParams) - surfaceDepthZ) * depthAngleFix;
		refraction = GetBufferColor(screenUV).xyz;
		#if USE_VOLUMETRIC_LIGHT
			volumeScattering = GetVolumetricLight(screenUV);
		#endif
	}
}


half3 ComputeUnderwaterColor(half3 refraction, half3 volumeLight, half fade, half transparent, half3 waterColor, half turbidity, half3 turbidityColor)
{
	fade = max(0, fade);
	float fadeExp = saturate(1 - exp(-5 * fade / transparent));
	half3 absorbedColor = pow(clamp(waterColor.xyz, 0.1, 0.95), 25 * fade / transparent); //min range ~ 0.0  with pow(x, 70)
	half3 waterColorInDepth = pow(waterColor.xyz, 15.0) * 0.05 * volumeLight.rgb;
	half3 turbidityColorInDepth = turbidityColor * volumeLight.rgb;
	
	absorbedColor = lerp(waterColorInDepth, refraction, absorbedColor);
	turbidityColor = lerp(refraction, turbidityColorInDepth, fadeExp);
	absorbedColor = lerp(absorbedColor, turbidityColor, turbidity);

	return absorbedColor;
}

half3 ComputeUnderwaterColor(half3 refraction, half3 volumeLight, half fade, half transparent, half3 waterColor, half turbidity, half3 turbidityColor, half3 fogOpacity, half3 fogColor)
{
	fade = max(0, fade);
	float fadeExp = saturate(1 - exp(-5 * fade / transparent));
	half3 absorbedColor = pow(clamp(waterColor.xyz, 0.1, 0.95), 25 * fade / transparent); //min range ~ 0.0  with pow(x, 70)
	half3 waterColorInDepth = lerp(pow(waterColor.xyz, 15.0) * 0.05 * volumeLight.rgb, fogColor, fogOpacity);
	half3 turbidityColorInDepth = lerp(turbidityColor * volumeLight.rgb, fogColor, fogOpacity);
	
	absorbedColor = lerp(waterColorInDepth, refraction, absorbedColor);
	turbidityColor = lerp(refraction, turbidityColorInDepth, fadeExp);
	absorbedColor = lerp(absorbedColor, turbidityColor, turbidity);

	return absorbedColor;
}

inline float2 GetWaterDepthNormalizedUV(float2 uv)
{
	return clamp(uv, 0.01, 0.99) * KWS_WaterMask_RTHandleScale.xy;
}

inline half4 GetWaterMaskScatterNormals(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(KW_WaterMaskScatterNormals, sampler_linear_clamp, GetWaterDepthNormalizedUV(uv), 0);
}

half3 ComputeSSS(float2 screenUV, half3 underwaterColor, half shadowMask, half KW_Transparent)
{
	half sssMask = GetWaterMaskScatterNormals(screenUV).y;
	float3 sssColor = dot(underwaterColor, 0.333) * 0.25 + underwaterColor * 0.75;
	return sssMask * shadowMask * sssColor * saturate(1 - KW_Transparent / 100);
}

inline half SelfSmithJointGGXVisibilityTerm(half NdotL, half NdotV, half roughness)
{
	half a = roughness;
	half lambdaV = NdotL * (NdotV * (1 - a) + a);
	half lambdaL = NdotV * (NdotL * (1 - a) + a);

	return 0.5f / (lambdaV + lambdaL + 1e-5f);
}

inline half SelfGGXTerm(half NdotH, half roughness)
{
	half a2 = roughness * roughness;
	half d = (NdotH * a2 - NdotH) * NdotH + 1.0f; // 2 mad
	return 0.31830988618f * a2 / (d * d + 1e-7f); // This function is not intended to be running on Mobile,
	// therefore epsilon is smaller than what can be represented by half
}


inline half ComputeSpecular(half nl, half nv, half nh, half viewDistNormalized, half smoothness)
{
	half V = SelfSmithJointGGXVisibilityTerm(nl, nv, smoothness);
	half D = SelfGGXTerm(nh, viewDistNormalized * 0.1 + smoothness);

	half specularTerm = V * D;
	specularTerm = max(0, specularTerm * nl * KWS_SunStrength);

	return specularTerm;
}

half3 ComputeSunlight(half3 normal, half3 viewDir, float3 lightDir, float3 lightColor, half shadowMask, float viewDist, float waterFarDistance, half KW_Transparent)
{
	half3 halfDir = normalize(lightDir + viewDir);

	half nh = saturate(dot(normal, halfDir));
	half nl = saturate(dot(normal, lightDir));
	half lh = saturate(dot(lightDir, halfDir));
	half fresnel = saturate(dot(normal, viewDir));
	
	float viewDistNormalized = saturate(viewDist / (waterFarDistance * 2));
	half3 specular = ComputeSpecular(nl, fresnel, nh, viewDistNormalized, KWS_SunCloudiness);


	specular = clamp(specular * 10 - 2.5 * saturate(1 - KWS_SunCloudiness * 10), 0, 3);
	//half sunset = saturate(0.01 + dot(lightDir, float3(0, 1, 0))) * 30;

	return shadowMask * specular * lightColor;
}




//----------------------------------------------KWS Water---------------------------

struct Gerstner
{
     float3 positionWS;
     float3 binormal;
     float3 tangent;
};

float Random(int seed)
{         
    return frac(sin(dot(float2(seed,2), float2(12.9898, 78.233))) ) * 2 - 1;
}

Gerstner GerstnerWave(float2 direction,float3 positionWS,int waveCount,float wavelengthMax,float wavelengthMin,
    float steepnessMax,float steepnessMin,float randomdirection)
{
    Gerstner gerstner;

    float3 P;
    float3 B;
    float3 T;

    for (int i = 0; i < waveCount; i++)
    {
        float step = (float) i / (float) waveCount;

        float2 d = float2(Random(i),Random(2*i));
        d = normalize(lerp(normalize(direction.xy), d, randomdirection));

        //step = pow(step, 0.75f);可以使用线性插值或其他插值曲线
        float wavelength = lerp(wavelengthMax, wavelengthMin, step);
        float steepness = lerp(steepnessMax, steepnessMin, step)/waveCount;

        float k = 2 * PI / wavelength;
        float g = 9.81f;
        float w = sqrt(g * k);
        float a = steepness / k;
        float2 wavevector = k * d;
        float value = dot(wavevector, positionWS.xz) - w * _Time.y;

        P.x += d.x * a * cos(value);
        P.z += d.y * a * cos(value);
        P.y += a * sin(value);

        T.x += d.x * d.x * k * a * -sin(value);
        T.y += d.x * k * a * cos(value);
        T.z += d.x * d.y * k * a * -sin(value);

        B.x += d.x * d.y * k * a * -sin(value);
        B.y += d.y * k * a * cos(value);
        B.z += d.y * d.y * k * a * -sin(value);
    }
    
    gerstner.positionWS.x = positionWS.x + P.x;
    gerstner.positionWS.y = P.y;
    gerstner.positionWS.z = positionWS.z + P.z;
    gerstner.tangent = float3(1 + T.x, T.y, T.z);
    gerstner.binormal = float3(B.x,B.y,1 + B.z);

    return gerstner;               
}

static float3x3 causticMat = float3x3( -2,-1,2, 3,-2,1, 1,2,2);

float Caustic(float va, float3 k){
    return length(.5-frac(mul(causticMat * va, k)));
}


float CausticCreate(float2 p, float s)
{
    float4 k;
    float t = sin(s * 0.3);

    p = abs(fmod(abs(p), 30) - 15);
    
    // k.xz = p*(sin(k = t *.01).w+2.) / abs(p * 0.01);
    k.xz = p*(sin(k = t * 0.5).w+2.) / 3;
    k.y = sin(s);
    
    // float radio = (sin(s * 0.05) * 0.5 + 0.5);
    float col0 = Caustic(0.5, k.xyz);
    float col1 = Caustic(0.42, k.xyz);
    float col2 = Caustic(0.37, k.xyz);

    float col = pow(min(min(col0, col1), col2), 7) * 20.0;
    // float col = col2;
    // float col = pow(min(min(Caustic(lerp(0.45, 0.44, radio), k.xyz), Caustic(lerp(0.44, 0.43, radio), k.xyz)), 
    //     Caustic(lerp(0.43, 0.42, radio), k.xyz)), 7.)*40.;
    return saturate(col);
}




#endif