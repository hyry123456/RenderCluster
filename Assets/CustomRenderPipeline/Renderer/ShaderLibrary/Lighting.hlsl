#ifndef CUSTOM_LIGHTING_INCLUDED
#define CUSTOM_LIGHTING_INCLUDED

float3 GetGBufferLight(Surface surface, float2 screenUV){

#if defined _MY_LIGHTING
	ShadowData shadowData = GetMyShadowData(surface);
#else
	ShadowData shadowData = GetShadowData(surface);
#endif

	float3 color = 0;
	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLight(i, surface, shadowData);
		color += DirectBRDF(surface, light);
	}

#ifdef _USE_CLUSTER

	float2 coord = _CameraBufferSize.xy * screenUV; 
	uint2 tileId = floor(coord / _DeferredTileParams.xy);
	uint tileIndex = tileId.y * _DeferredTileParams.z + tileId.x;
	int lightCount = _TileLightsArgsBuffer[tileIndex];

	uint tileLightOffset = tileIndex * MAX_LIGHT_COUNT_PER_TILE;

	for(int j = 0; j < lightCount; j++){
		Light light = GetOtherLight(_TileLightsIndicesBuffer[tileLightOffset + j], surface, shadowData);
		color += DirectBRDF(surface, light);
	}

	#else

		for (int j = 0; j < GetOtherLightCount(); j++) {
			Light light = GetOtherLight(j, surface, shadowData);
			color += DirectBRDF(surface, light);
		}
#endif
	return color;
}

float3 GetLight(Surface surface){
	ShadowData shadowData = GetShadowData(surface);

	float3 color = 0;
	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLight(i, surface, shadowData);
		color += DirectBRDF(surface, light);
		// return light.color;

	}

	for (int j = 0; j < GetOtherLightCount(); j++) {
		Light light = GetOtherLight(j, surface, shadowData);
		color += DirectBRDF(surface, light);
	}
	return color;
}

float3 GetLightIntensity(float3 worldPos){
	ShadowData shadowData = GetShadowDataByPosition(worldPos);

	float3 color = 0;
	// for (int i = 0; i < GetDirectionalLightCount(); i++) {
	// 	Light light = GetDirectionalLightByPosition(i, worldPos, shadowData);
	// 	color += light.attenuation * light.color;
	// }


	for (int j = 0; j < GetOtherLightCount(); j++) {
		Light light = GetOtherLightByPosition(j, worldPos, shadowData);
		color += light.attenuation * light.color;
	}
	return color;

}

float3 GetLight_Spec(Surface surface){
	ShadowData shadowData = GetShadowData(surface);

	float3 color = 0;
	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLight(i, surface, shadowData);
		color += DirectBRDF_Spec(surface, light);
		// return light.color;

	}

	// for (int j = 0; j < GetOtherLightCount(); j++) {
	// 	Light light = GetOtherLight(j, surface, shadowData);
	// 	color += DirectBRDF_Spec(surface, light);
	// }
	return color;
}


void AdjustWidth(inout float width, float scale){
	width = saturate(width * scale);
}

float3 GetGBufferBSDFLight(Surface surface, float3 uv_Depth, float normalDistorion, float power, float width, float scale){
	ShadowData shadowData = GetShadowData(surface);

	float3 color = 0;
	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLight(i, surface, shadowData);
		color += DirectBSDF(surface, light, normalDistorion, power, width);
	}

	for (int j = 0; j < GetOtherLightCount(); j++) {
		Light light = GetOtherLight(j, surface, shadowData);
		color += DirectBSDF(surface, light, normalDistorion, power, width);
	}
	return color;
}

float3 GetGBufferBSDFLight(Surface surface, float4 clipPos, float normalDistorion, float power, float scale){
	ShadowData shadowData = GetShadowData(surface);

	float3 color = 0;
	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		float width;
		Light light = GetDirectionalLight_SSS(i, surface, shadowData, width);
		AdjustWidth(width, scale);
		color += DirectBSDF(surface, light, normalDistorion, power, width);
	}


	for (int j = 0; j < GetOtherLightCount(); j++) {
		float width;
		Light light = GetOtherLight_SSS(j, surface, shadowData, width);
		AdjustWidth(width, scale);
		// return width;
		color += DirectBSDF(surface, light, normalDistorion, power, width);
	}
	return color;
}



//因为是视线方向作为法线的，因此要注意相反的情况，但是相反应该要也可以看见
float3 BulkIncomingLight(Light light, float3 viewDirection, float g){
	float cosTheta = saturate( dot(-viewDirection, light.direction) );
	return 1 / (4 * 3.14) * (1 - g * g) / pow(abs( 1 + g * g - 2 * g * cosTheta ), 1.5) * light.attenuation * light.color * 10.0;
	// return light.attenuation * light.color;
}

//获得体积光的光照计算方式
float3 GetBulkLighting(float3 worldPos, float3 viewDirection, float2 screenUV, float scatterRadio, float depth){
	ShadowData shadowData = GetShadowDataByPosition(worldPos);

	float3 color = 0;

	for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLightByPosition(i, worldPos, shadowData);
		color += BulkIncomingLight(light, viewDirection, scatterRadio);
	}


	for (int j = 0; j < GetOtherLightCount(); j++) {
		Light light = GetOtherLightByPosition(j, worldPos, shadowData);
		color += BulkIncomingLight(light, viewDirection, scatterRadio);
	}

	return color;
}


#endif