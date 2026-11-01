#ifndef CUSTOM_LIGHT_INCLUDED
#define CUSTOM_LIGHT_INCLUDED

#define MAX_DIRECTIONAL_LIGHT_COUNT 4
#define MAX_OTHER_LIGHT_COUNT 1024
#define MAX_LIGHT_COUNT_PER_TILE 32


struct OtherLight
{
	float4 lightColor;			//w:range
	float4 lightPosition;		//w:1/range^2
	float4 lightDirectionsAndMasks;
	float4 lightSpotAngles;
	float4 lightShadowData;
	// 面光源：两条半轴（已乘半宽/半高）。lightAreaRight.w = 半宽，> 0 即为面光源。
	float4 lightAreaRight;
	float4 lightAreaUp;
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

/// 面光源的「代表点」（Most Representative Point）：
/// 把着色点沿发光面法线投到矩形平面上，钳制进矩形范围，取最近的合法点。
/// 着色点在发光面背面时退化到中心点（后续由 sideAttenuation 衰减到 0）。
float3 AreaLightRepresentativePoint (OtherLight otL, float3 surfacePos) {
	float3 center     = otL.lightPosition.xyz;
	float3 areaNormal = normalize(otL.lightDirectionsAndMasks.xyz);
	float3 right      = otL.lightAreaRight.xyz;		// 已含半宽
	float3 up         = otL.lightAreaUp.xyz;		// 已含半高

	float planeDist = dot(surfacePos - center, areaNormal);
	if (planeDist <= 0.0) return center;

	float3 proj = surfacePos - areaNormal * planeDist;
	float2 d = float2(
		dot(proj - center, right) / max(dot(right, right), 1e-6),
		dot(proj - center, up)    / max(dot(up, up),    1e-6)
	);
	d = clamp(d, -1.0, 1.0);
	return center + right * d.x + up * d.y;
}

Light GetOtherLight (int index, Surface surfaceWS, ShadowData shadowData) {
	Light light; 
	OtherLight otL = _OtherLightBuffer[index];

	light.color = otL.lightColor.rgb;

	// ---------- 面光源（矩形 / 圆盘）：MRP 代表点近似 ----------
	// 用矩形上离着色点最近的点当点光源，直接复用点光的 1/d² 模型。
	// 能得到的：正确的明暗边界（不再是从中心发散）、近处柔化、掠射角变宽。
	// 得不到的：真正的矩形高光形状（那需要 LTC/LUT）——代表点是单点，高光仍是点光状。
	// 成本与一个点光完全相同。
	if (otL.lightAreaRight.w > 0.0) {
		// ⚠️ 语义陷阱：lightDirectionsAndMasks.xyz 在本管线里存的是【指向光源的方向】
		//（Spot 走 `-column(2)`，为的是和 light.direction「表面→光源」点乘能算出锥形衰减）。
		// 而发光面的外法线必须朝【发射方向】，两者差一个负号 —— 之前漏了取反，
		// 导致 AreaLightRepresentativePoint 里 planeDist 恒为负 ⇒ 永远退化成中心点，
		// 面光源看起来就是一个大半径点光/射灯。
		float3 toLightAxis = normalize(otL.lightDirectionsAndMasks.xyz);	// 指向灯 = -发射方向
		float3 areaNormal = -toLightAxis;									// 发光面外法线 = 发射方向
		float3 rep = AreaLightRepresentativePoint(otL, surfaceWS.position);

		float3 ray = rep - surfaceWS.position;
		light.direction = normalize(ray);
		float distanceSqr = max(dot(ray, ray), 0.00001);

		// range 衰减必须用「真实距离」，才能和 tile 剔除用的包围球边界一致
		float rangeAttenuation = Square(
			saturate(1.0 - Square(distanceSqr * otL.lightPosition.w))
		);

		// 软半径：大面光源不该在代表点处出现一个过亮的针尖高光。
		// 把 1/d² 抹平为 1/(d² + r²) 是面积光最省事的近似 —— 高光随面积变宽、变柔。
		float softR = 0.5 * (otL.lightAreaRight.w + otL.lightAreaUp.w);
		float softDistSqr = distanceSqr + softR * softR * 0.5;

		// 只照发光面正前方（背面不亮），边缘做软过渡避免硬半球切边。
		// 用「表面 - 中心」而不是「中心 - 表面」：前者在正前方与发射方向同向 ⇒ side > 0。
		float side = dot(areaNormal,
			normalize(surfaceWS.position - otL.lightPosition.xyz));
		float sideAttenuation = saturate(side * 3.0);

		// 阴影：挂 Spot 时走 Spot 的【1 个 tile】。
		// 复用是零成本的 —— GetOtherShadow 只用到 tileIndex / _OtherShadowMatrices / 着色点，
		// 完全不看 spotAngles；lightPlane 用「指向灯」的轴（与 Spot 的 spotDirection 同语义，
		// 这样 dot(surfaceToLight, lightPlane) 是正的距离，normal bias 方向才对）。
		// 不投影时（挂 Point / Unity Rectangle）strength = 0，该函数恒返回 1。
		OtherShadowData otherShadowData = GetOtherShadowData(index);
		otherShadowData.lightPositionWS = otL.lightPosition.xyz;   // 面片中心
		otherShadowData.lightDirectionWS = light.direction;        // MRP 方向
		otherShadowData.spotDirectionWS = toLightAxis;             // 指向灯（同 Spot）

		light.attenuation = rangeAttenuation * sideAttenuation / softDistSqr
			* GetOtherShadowAttenuation(otherShadowData, shadowData, surfaceWS);
		return light;
	}

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