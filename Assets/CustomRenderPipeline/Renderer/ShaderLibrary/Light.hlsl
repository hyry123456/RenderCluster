#ifndef CUSTOM_LIGHT_INCLUDED
#define CUSTOM_LIGHT_INCLUDED

#define MAX_DIRECTIONAL_LIGHT_COUNT 4
#define MAX_OTHER_LIGHT_COUNT 256
#define MAX_LIGHT_COUNT_PER_TILE 32


struct OtherLight
{
	float4 lightColor;			//w:range
	float4 lightPosition;		//w:1/range^2
	float4 lightDirectionsAndMasks;
	float4 lightSpotAngles;
	float4 lightShadowData;
};

CBUFFER_START(_CustomLight)
	int _DirectionalLightCount;
	float4 _DirectionalLightColors[MAX_DIRECTIONAL_LIGHT_COUNT];
	float4 _DirectionalLightDirectionsAndMasks[MAX_DIRECTIONAL_LIGHT_COUNT];
	float4 _DirectionalLightShadowData[MAX_DIRECTIONAL_LIGHT_COUNT];

	int _OtherLightCount;
	StructuredBuffer<OtherLight> _OtherLightBuffer;

	StructuredBuffer<uint> _TileLightsArgsBuffer;
	StructuredBuffer<uint> _TileLightsIndicesBuffer;
	float4 _DeferredTileParams;

CBUFFER_END

struct Light {
	float3 color;
	float3 direction;
	float attenuation;
};


int GetDirectionalLightCount () {
	return _DirectionalLightCount;
}

DirectionalShadowData GetDirectionalShadowData (
	int lightIndex, ShadowData shadowData
) {
	DirectionalShadowData data;
	data.strength = _DirectionalLightShadowData[lightIndex].x;
	data.tileIndex =
		_DirectionalLightShadowData[lightIndex].y + shadowData.cascadeIndex;
	data.normalBias = _DirectionalLightShadowData[lightIndex].z;
	data.shadowMaskChannel = _DirectionalLightShadowData[lightIndex].w;
	return data;
}

//默认的获得直接光数据的方式，其中包含阴影数据等
Light GetDirectionalLight (int index, Surface surfaceWS, ShadowData shadowData) {
	Light light;
	light.color = _DirectionalLightColors[index].rgb;
	light.direction = _DirectionalLightDirectionsAndMasks[index].xyz;
	DirectionalShadowData dirShadowData =
		GetDirectionalShadowData(index, shadowData);
	light.attenuation =
		GetDirectionalShadowAttenuation(dirShadowData, shadowData, surfaceWS);
	return light;
}

Light GetDirectionalLightByPosition(int index, float3 worldPos, ShadowData shadowData){
	Light light;
	light.color = _DirectionalLightColors[index].rgb;
	light.direction = _DirectionalLightDirectionsAndMasks[index].xyz;
	DirectionalShadowData dirShadowData =
		GetDirectionalShadowData(index, shadowData);
	light.attenuation =
		GetDirectionalShadowAttenuation(dirShadowData, shadowData, worldPos);
	return light;
}


Light GetDirectionalLight_SSS (int index, Surface surfaceWS, ShadowData shadowData, out float width) {
	Light light;
	light.color = _DirectionalLightColors[index].rgb;
	light.direction = _DirectionalLightDirectionsAndMasks[index].xyz;
	DirectionalShadowData dirShadowData =
		GetDirectionalShadowData(index, shadowData);
	light.attenuation =
		GetDirectionalShadowAttenuation(dirShadowData, shadowData, surfaceWS, width);
	return light;
}

int GetOtherLightCount () {
	return _OtherLightCount;
}

OtherShadowData GetOtherShadowData (int lightIndex) {
	OtherShadowData data;
	OtherLight otL = _OtherLightBuffer[lightIndex];
	
	data.strength = otL.lightShadowData.x;
	data.tileIndex = otL.lightShadowData.y;
	data.shadowMaskChannel = otL.lightShadowData.w;
	data.isPoint = otL.lightShadowData.z == 1.0;
	data.lightPositionWS = 0.0;
	data.lightDirectionWS = 0.0;
	data.spotDirectionWS = 0.0;
	return data;
}

Light GetOtherLight (int index, Surface surfaceWS, ShadowData shadowData) {
	Light light; 
	OtherLight otL = _OtherLightBuffer[index];

	light.color = otL.lightColor.rgb;
	float3 position = otL.lightPosition.xyz;
	float3 ray = position - surfaceWS.position;
	light.direction = normalize(ray);
	float distanceSqr = max(dot(ray, ray), 0.00001);
	float rangeAttenuation = Square(
		saturate(1.0 - Square(distanceSqr * otL.lightPosition.w))
	);
	float4 spotAngles = otL.lightSpotAngles;
	float3 spotDirection = otL.lightDirectionsAndMasks.xyz;
	float spotAttenuation = Square(
		saturate(dot(spotDirection, light.direction) *
		spotAngles.x + spotAngles.y)
	);
	OtherShadowData otherShadowData = GetOtherShadowData(index);
	otherShadowData.lightPositionWS = position;
	otherShadowData.lightDirectionWS = light.direction;
	otherShadowData.spotDirectionWS = spotDirection;
	light.attenuation =
		GetOtherShadowAttenuation(otherShadowData, shadowData, surfaceWS) *
		spotAttenuation * rangeAttenuation / distanceSqr;
	return light;
}


Light GetOtherLight_SSS (int index, Surface surfaceWS, ShadowData shadowData, out float width) {
	Light light;
	OtherLight otL = _OtherLightBuffer[index];

	light.color = otL.lightColor.rgb;
	float3 position = otL.lightPosition.xyz;
	float3 ray = position - surfaceWS.position;
	light.direction = normalize(ray);
	float distanceSqr = max(dot(ray, ray), 0.00001);
	float rangeAttenuation = Square(
		saturate(1.0 - Square(distanceSqr * otL.lightPosition.w))
	);
	float4 spotAngles = otL.lightSpotAngles;
	float3 spotDirection = otL.lightShadowData.xyz;
	float spotAttenuation = Square(
		saturate(dot(spotDirection, light.direction) *
		spotAngles.x + spotAngles.y)
	);
	OtherShadowData otherShadowData = GetOtherShadowData(index);
	otherShadowData.lightPositionWS = position;
	otherShadowData.lightDirectionWS = light.direction;
	otherShadowData.spotDirectionWS = spotDirection;
	light.attenuation =
		GetOtherShadowAttenuation_SSS(otherShadowData, shadowData, surfaceWS, width) *
		spotAttenuation * rangeAttenuation / distanceSqr;
	return light;
}


Light GetOtherLightByPosition(int index, float3 worldPos, ShadowData shadowData){
	Light light;
	OtherLight otL = _OtherLightBuffer[index];

	light.color = otL.lightColor.rgb;
	float3 position = otL.lightPosition.xyz;
	float3 ray = position - worldPos;
	light.direction = normalize(ray);
	float distanceSqr = max(dot(ray, ray), 0.00001);
	float rangeAttenuation = Square(
		saturate(1.0 - Square(distanceSqr * otL.lightPosition.w))
	);
	float4 spotAngles = otL.lightSpotAngles;
	float3 spotDirection = otL.lightDirectionsAndMasks.xyz;
	float spotAttenuation = Square(
		saturate(dot(spotDirection, light.direction) *
		spotAngles.x + spotAngles.y)
	);
	OtherShadowData otherShadowData = GetOtherShadowData(index);
	otherShadowData.lightPositionWS = position;
	otherShadowData.lightDirectionWS = light.direction;
	otherShadowData.spotDirectionWS = spotDirection;
	light.attenuation =
		GetOtherShadowAttenuationByPosition(otherShadowData, shadowData, worldPos) *
		spotAttenuation * rangeAttenuation / distanceSqr;
	return light;
}

float3 GetMainLightDir(){
	return _DirectionalLightDirectionsAndMasks[0].xyz;
}

float3 GetMainLightColor(){
	return _DirectionalLightColors[0].xyz;
}

Light GetMainLight(){
	Light light;
	light.color = _DirectionalLightColors[0].xyz;
	light.direction = _DirectionalLightDirectionsAndMasks[0].xyz;
	light.attenuation = 1;
	return light;
}

float MainLightRealtimeShadow(float3 worldPos){
	ShadowData data = GetShadowDataByPosition(worldPos);
	DirectionalShadowData dirShadowData =
		GetDirectionalShadowData(0, data);
	return	GetDirectionalShadowAttenuation(dirShadowData, data, worldPos);
}

#endif