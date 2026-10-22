#ifndef CUSTOMRP_OCEAN_PASS
#define CUSTOMRP_OCEAN_PASS


struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    uint instanceID : SV_InstanceID;
	half4 color : COLOR;
};


struct OutVert {
    float4 pos : SV_POSITION;
    float4 positionWS : VAR_POSITION;     //XYZ:worldPos, local Height
    float2 baseUV : UV_BASE;        //01uv
    // float3 positionOS : WATER_HEIGHT;       //x:水高度值，即从水的最高点(为0)开始算，当前水的高度值, y:objPos.Y
    float3 baseNormal : NORMAL;
    float4 screenPos : SCREEN;
	float surfaceMask : MASK;
};

OutVert vert(appdata v) {
    OutVert o;
	uint4 node = _Nodes[v.instanceID];
	float4 nodeSize = _NodeSize[node.z];
	float2 planeSize = _NodeSize[4].xy * 5;

	v.vertex.xyz /= _PointCount;
	v.vertex.xz = nodeSize.xy * (node.xy + v.vertex.xz);
	uint mode = v.color.y * 255, modez = v.color.z * 255;

	if(mode & node.w)
	{
		v.vertex.y = nodeSize.z;
		o.surfaceMask = 0;
	}
	else o.surfaceMask = 1;

    v.vertex.xyz = GetWaterOffsetRelativeToMask(1, v.vertex.xyz);
	o.positionWS.w = v.vertex.y + 1.0;		//得到本地高度

/////////////////////////////边界检测，往下拉/////////////////////////////////////////////////////////

	// if(modez & )

/////////////////////////////边界检测，往下拉/////////////////////////////////////////////////////////
	
    float2 worldUV = v.vertex.xz / planeSize;


    // float3 normalWS = normalize(cross(binormal,tangent));

    // float heigh = SAMPLE_TEXTURE2D_LOD(_HeighMap, sampler_linear_clamp, worldUV, 0).x * _WorldSize.y;
    // v.vertex.y += heigh;
    // o.positionOS = v.vertex.xyz;
    v.vertex.xyz += _BeginPosition.xyz;

    o.baseUV = worldUV;
    
    o.pos = TransformWorldToHClip(v.vertex.xyz);
    o.positionWS.xyz = v.vertex.xyz;
    // o.baseNormal = 1;
    o.baseNormal = float3(0, 1, 0);
    o.screenPos = ComputeScreenPos(o.pos);
    
    return o;
}

inline float GetSceneDepth(float2 uv)
{  
	float depth = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).x;
	#ifndef UNITY_REVERSED_Z
         depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
    #endif
	return depth;
}



float4 SimpleColorfrag (OutVert i) : SV_TARGET
{
    float2 uv = i.positionWS.xz / KW_FFTDomainSize;
    float3 tangentNormal;
	float3 viewDir = normalize(_WorldSpaceCameraPos - i.positionWS.xyz);
	float surfaceMask = i.surfaceMask > 0.9999 ? 1 : 0;

/////////////////////////////////////////////////////////////  normal  ////////////////////////////////////////////////////////////////////////////////////////////////////////
    #if USE_FILTERING
        float normalFilteringMask;
        tangentNormal = GetFilteredNormal_lod0(uv, surfaceDepthZ, normalFilteringMask);
    #else
        tangentNormal = GetNormal_lod0(uv);
    #endif
    if (KWS_UseMultipleSimulations > 0)	tangentNormal = GetNormal_lod1_lod2(i.positionWS.xyz, tangentNormal);

    float3 worldNormal = normalize(tangentNormal);

	// return float4(worldNormal.xz, 0, 1);

/////////////////////////////////////////////////////////////  end normal  ////////////////////////////////////////////////////////////////////////////////////////////////////////
    Fragment fragment = GetFragment(i.pos);
	float surfaceDepthZ = LinearEyeDepth(i.screenPos.z / i.screenPos.w, _ZBufferParams);
	half surfaceTensionFade = saturate(abs(fragment.bufferDepth - i.pos.w) * 8);

/////////////////////////////////////////////////////////////////////  REFRACTION  ///////////////////////////////////////////////////////////////////
	float2 refractionUV;
	half3 refraction;

	UNITY_BRANCH
	if (KWS_UseRefractionIOR > 0) refractionUV = GetRefractedUV_IOR(viewDir, worldNormal, i.positionWS.xyz, surfaceTensionFade);
	else		refractionUV = GetRefractedUV_Simple(fragment.screenUV, tangentNormal);
	
	UNITY_BRANCH
	if(KWS_UseRefractionDispersion > 0)	refraction = GetSceneColorWithDispersion(refractionUV, KWS_RefractionDispersionStrength);
	else refraction = GetBufferColor(refractionUV).xyz;
	
/////////////////////////////////////////////////////////////  end refraction  ////////////////////////////////////////////////////////////////////////////////////////////////////////
	

/////////////////////////////////////////////////////////////  REFLECTION  ////////////////////////////////////////////////////////////////////////////////////////////////////////
	half3 finalReflection = 0;


	float3 reflDir = reflect(-viewDir, worldNormal);

	#if defined(PLANAR_REFLECTION) || defined(SSPR_REFLECTION)
		float2 refl_uv = GetScreenSpaceReflectionUV(reflDir);
	#endif
	#if SSPR_REFLECTION
		finalReflection = GetScreenSpaceReflectionWithStretchingMask(refl_uv).xyz;
	#else
		half3 cubemapReflection = GetCubemapReflection(reflDir);

		// cubemapReflection.xyz *= exposure;
		finalReflection = cubemapReflection.xyz;
		// #if PLANAR_REFLECTION
		// 	float3 planarReflection = GetPlanarReflectionWithClipOffset(refl_uv);
		// 	planarReflection.xyz *= exposure;
		// 	float planarAlpha = planarReflection.x + planarReflection.y + planarReflection.z > 0.00001;
		// 	finalReflection = lerp(finalReflection, planarReflection.xyz, planarAlpha);
		// #endif
	#endif
	// finalReflection *= surfaceMask;


/////////////////////////////////////////////////////////////  end reflection  ////////////////////////////////////////////////////////////////////////////////////////////////////////


/////////////////////////////////////////////////////////////////////  UNDERWATER  ///////////////////////////////////////////////////////////////////
	#if USE_VOLUMETRIC_LIGHT
		half4 volumeScattering = GetVolumetricLight(refractionUV);
	#else
		half4 volumeScattering = half4(GetAmbientColor(), 1.0);
	#endif
	
	float depthAngleFix = saturate(GetWorldSpaceViewDirNorm(i.positionWS.xyz - float3(0, KW_WindSpeed * 0.5, 0)).y);
	float refractedSceneZ = GetSceneDepth(refractionUV);
	float fade = GetWaterRawFade(i.positionWS.xyz, surfaceDepthZ, refractedSceneZ, 1, depthAngleFix);

	FixAboveWaterRendering(refractionUV, refractedSceneZ, i.positionWS.xyz, fragment.bufferDepth, surfaceDepthZ, 
        depthAngleFix, fragment.screenUV, 0, fade, refraction, volumeScattering);
	
	half3 underwaterColor = ComputeUnderwaterColor(refraction.xyz, volumeScattering.rgb, fade, KW_Transparent, KW_WaterColor.xyz, KW_Turbidity, KW_TurbidityColor.xyz);
	
	// #if defined(KW_FLOW_MAP_FLUIDS) && !defined(KW_FLOW_MAP_EDIT_MODE)
	// 	underwaterColor = GetFluidsColor(underwaterColor, volumeScattering, fluidsFoam);
	// #endif
	underwaterColor += ComputeSSS(fragment.screenUV, underwaterColor, volumeScattering.a > 0.5, KW_Transparent) * 5;

	// #if defined(USE_FOAM)
	// 	underwaterColor = ComputeDepthFoam(underwaterColor, fade, i.worldPos, GetVolumetricLight(fragment.screenUV).a, KW_WaterColor.xyz, exposure);
	// #endif
/////////////////////////////////////////////////////////////  end underwater  ////////////////////////////////////////////////////////////////////////////////////////////////////////

    half waterFresnel = ComputeWaterFresnel(worldNormal, viewDir);
	waterFresnel *= surfaceMask;
	half3 finalColor = lerp(underwaterColor, finalReflection, waterFresnel);

	#if REFLECT_SUN
		finalColor += ComputeSunlight(worldNormal, viewDir, GetMainLightDir(), GetMainLightColor(), volumeScattering.a > 0.5, surfaceDepthZ, 6000, KW_Transparent);
	#endif

    return float4(finalColor, 1);
}




struct FragmentOutput
{
	half4 dest0 : SV_Target0;
	half dest1 : SV_Target1;
};


FragmentOutput fragDepth(OutVert i, float facing : VFACE)
{
	FragmentOutput o;

	float facingColor = 0.75 - facing * 0.25;
	i.screenPos.xyz /= i.screenPos.w;
	float sceneDepth = GetSceneDepth(i.screenPos.xy);

	if (KWS_UnderwaterVisible == 0 && LinearEyeDepth(sceneDepth, _ZBufferParams) < LinearEyeDepth(i.screenPos.z, _ZBufferParams))
	{
		o.dest0 = float4(facing > 0.5 ?       0.0 : facingColor, 0, 0, 0);
		// o.dest1 = facing > 0.5 ?       0 : KWS_WaterPassID * KWS_WATER_MASK_DECODING_VALUE + KWS_WATER_MASK_DECODING_VALUE;
		o.dest1 = facing > 0.5 ?       0 : KWS_WATER_MASK_DECODING_VALUE;
		return o;
	}

	float3 worldPos = i.positionWS.xyz;
	float waveLocalHeight = i.positionWS.w;
	float2 uv = worldPos.xz / KW_FFTDomainSize;
	
	half3 norm = KW_NormTex.Sample(sampler_linear_repeat, uv).xyz;
	half3 normScater = KW_NormTex.SampleLevel(sampler_linear_repeat, uv, 3).xyz;
	
	if (KWS_UseMultipleSimulations > 0)
	{
		half3 normScater_lod1 = KW_NormTex_LOD1.SampleLevel(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD1, 2).xyz;
		half3 normScater_lod2 = KW_NormTex_LOD2.SampleLevel(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD2, 1).xyz;
		normScater = normalize(half3(normScater.xz + normScater_lod1.xz + normScater_lod2.xz, normScater.y * normScater_lod1.y * normScater_lod2.y)).xzy;

		half3 norm_lod1 = KW_NormTex_LOD1.Sample(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD1).xyz;
		half3 norm_lod2 = KW_NormTex_LOD2.Sample(sampler_linear_repeat, worldPos.xz / KW_FFTDomainSize_LOD2).xyz;
		norm = normalize(half3(norm.xz + norm_lod1.xz + norm_lod2.xz, norm.y * norm_lod1.y * norm_lod2.y)).xzy;
	}

	int idx;
	half sss = 0;
	half windLimit = clamp((KW_WindSpeed - 1), 0, 1);
	windLimit = lerp(windLimit, windLimit * 0.25, saturate(KW_WindSpeed / 15.0));


	float3 viewDir = GetWorldSpaceViewDirNorm(worldPos);
	float3 lightDir = GetMainLightDir();
	float distanceToCamera = 1 - saturate(GetWorldToCameraDistance(worldPos) * 0.002);
	
	half zeroScattering = saturate(dot(viewDir, - (lightDir + float3(0, 1, 0))));

	float3 H = (lightDir + normScater * float3(-1, 1, -1));
	float scattering = pow(saturate(dot(viewDir, -H)), 3);

	sss += windLimit * (scattering - zeroScattering * 0.95);

	norm.xz *= i.surfaceMask > 0.999 ?       1 : 0;
	
	o.dest0 = half4(0.75 - facing * 0.25, saturate(scattering * waveLocalHeight * distanceToCamera * windLimit), norm.xz * 0.5 + 0.5);
	o.dest1 = (KWS_WaterPassID * KWS_WATER_MASK_DECODING_VALUE + KWS_WATER_MASK_DECODING_VALUE);
	return o;
}

#endif