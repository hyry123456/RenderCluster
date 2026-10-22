#ifndef CUSTOMRP_DEFFER_SSR_PASS
#define CUSTOMRP_DEFFER_SSR_PASS

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "../../DeffererLibrary/DeffererInput.hlsl"
#include "../../../CommonInput/CommonInput.hlsl"
#include "DefferSSRInput.hlsl"

struct Attributes{
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
};

struct Varyings {
    float4 positionCS_SS : SV_POSITION;
    float2 screenUV : VAR_SCREEN_UV;
};


Varyings DefaultPassVertex (uint vertexID : SV_VertexID) {
	Varyings output = (Varyings)0;
	output.positionCS_SS = float4(
		vertexID <= 1 ? -1.0 : 3.0,
		vertexID == 1 ? 3.0 : -1.0,
		0.0, 1.0
	);
	output.screenUV = float2(
		vertexID <= 1 ? 0.0 : 2.0,
		vertexID == 1 ? 2.0 : 0.0
	);
	if (_ProjectionParams.x < 0.0) {
		output.screenUV.y = 1.0 - output.screenUV.y;
	}
	return output;
}



//SSR的渲染数据准备用的buffer，写入射中的pos等数据
void rayCast ( Varyings i, 	out half4 outRayCast : SV_Target0, out half4 outRayCastMask : SV_Target1) 
{	

    float2 uv = i.screenUV;

    float3 worldNormal = GetNormal (uv);	//计算世界法线

    float3 viewNormal = GetViewNormal (worldNormal.xyz);	//投影到视角空间
    // float4 specular = GetSpecular (uv);
    float roughness = GetRoughness (uv);		//得到粗糙程度

    float depth = GetDepth(uv);	//采样深度贴图

    float3 screenPos = GetScreenPos(uv, depth);			//屏幕空间，但是这实际上是ndc空间

    float3 worldPos = GetWorlPos(screenPos);			//计算世界空间坐标
    float3 viewDir = GetViewDir(worldPos);				//得到视线方向

    float3 viewPos = /*mul(_WorldToCameraMatrix, float4(worldPos, 1.0))*/ GetViewPos(screenPos);	//视角空间

	//读取噪声控制射线步长，达到更有效的检测效果
    float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, (uv + _JitterSizeAndOffset.zw) * _SSR_Size.xy / _NoiseSize.xy, 0).xy;

    float2 Xi = jitter;

    Xi.y = lerp(Xi.y, 0.0, _BRDFBias);		//控制噪声的y值大小

    //Epic的调整法线的公式
    float4 H = TangentToWorld(worldNormal, ImportanceSampleGGX(Xi, roughness));

    float3 dir = reflect(viewDir, H.xyz);
	// float3 dir = reflect(viewDir, worldNormal.xyz);

    //将方向投影至ndc空间
    dir = normalize(mul((float3x3)_WorldToCameraMatrix, dir));
    jitter += 0.5f;

    //步长
    float stepSize = (1.0 / (float)_NumSteps);
    //通过噪声调整每个像素的步长
    stepSize = stepSize * (jitter.x + jitter.y) + stepSize;

    float2 rayTraceHit = 0.0;
    float rayTraceZ = 0.0;
    float rayPDF = 0.0;
    float rayMask = 0.0;

    //最终结果
    float4 rayTrace = RayMarch(_ProjectionMatrix, dir, _NumSteps, viewPos, screenPos, uv, stepSize);

    rayTraceHit = rayTrace.xy;	//uv坐标
    rayTraceZ = rayTrace.z;		//深度值，这个深度是深度buffer的深度
    rayPDF = H.w;
    rayMask = rayTrace.w;		//Mask，用来判断是否碰撞到
    outRayCast = float4(float3(rayTraceHit, rayTraceZ), rayPDF);
    outRayCastMask = rayMask;
}

//渲染方式贴图的方法
float4 resolve ( Varyings i ) : SV_Target
{
	float2 uv = i.screenUV;
	int2 pos = uv * _CameraBufferSize.zw;

	float3 worldNormal = GetNormal (uv);
	float3 viewNormal = GetViewNormal (worldNormal);
	float roughness = GetRoughness (uv);

	float depth = GetDepth(uv);
	float3 screenPos = GetScreenPos(uv, depth);
	float3 worldPos = GetWorlPos(screenPos);
	float3 viewPos = GetViewPos(screenPos);
	float3 viewDir = GetViewDir(worldPos);

	//噪声
	float2 blueNoise = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, (uv + _JitterSizeAndOffset.zw) * _SSR_Size.xy / _NoiseSize.xy, 0).xy * 2.0 - 1.0; // works better with [-1, 1] range
	float2x2 offsetRotationMatrix = float2x2(blueNoise.x, blueNoise.y, -blueNoise.y, blueNoise.x);

	int NumResolve = 1;
	if(_RayReuse == 1)	NumResolve = 4;		//调整循环次数

	float NdotV = saturate(dot(worldNormal, -viewDir));
	float coneTangent = lerp(0.0, roughness * (1.0 - _BRDFBias), NdotV * sqrt(roughness));

	float maxMipLevel = -1.0;	//我并没有拓展Minmip，直接用-1就行了

	float4 result = 0.0;
	float weightSum = 0.0;	
	for(int i = 0; i < NumResolve; i++)
	{
		float2 offsetUV = offset[i] * (1.0 / _SSR_Size.xy);
		offsetUV =  mul(offsetRotationMatrix, offsetUV);

		float2 neighborUv = uv + offsetUV;

		//采样碰撞数据
		// float4 hitPacked = tex2Dlod(_RayCast, float4(neighborUv, 0.0, 0.0));
		float4 hitPacked = SAMPLE_TEXTURE2D_LOD(_RayCastTex, sampler_linear_repeat, neighborUv, 0);
		float2 hitUv = hitPacked.xy;
		float hitZ = hitPacked.z;
		float hitPDF = hitPacked.w;
		//采样遮罩数据
		// float hitMask = tex2Dlod(_RayCastMask, float4(neighborUv, 0.0, 0.0)).r;
		float hitMask = SAMPLE_TEXTURE2D_LOD(_RayCastMaskTex, sampler_linear_repeat, neighborUv, 0).r;

		float3 hitViewPos = GetViewPos(GetScreenPos(hitUv, hitZ));

		float weight = 1.0;
		if(_UseNormalization == 1)
				weight = BRDF_Unity_Weight(normalize(-viewPos) /*V*/, normalize(hitViewPos - viewPos) /*L*/, viewNormal /*N*/, roughness) / max(1e-5, hitPDF);

		float intersectionCircleRadius = coneTangent * length(hitUv - uv);
		float mip = clamp(log2(intersectionCircleRadius * max(_SSR_Size.x, _SSR_Size.y)), 0.0, maxMipLevel);

		float4 sampleColor = float4(0.0,0.0,0.0,1.0);

		sampleColor.rgb = GetSource(hitUv).rgb;
		sampleColor.a = RayAttenBorder (hitUv, _EdgeFactor) * hitMask;
		
		if(_Fireflies == 1)
			sampleColor.rgb /= 1 + Luminance(sampleColor.rgb);

		result += sampleColor * weight;
		weightSum += weight;
	}
	result /= weightSum;

	//减轻萤火虫，也就是小光点时使用
	if(_Fireflies == 1)
		result.rgb /= 1 - Luminance(result.rgb);

	return  max(1e-5, result);
}


void temporal (Varyings i, out half4 reflection : SV_Target)
{	
	float2 uv = i.screenUV;

	float depth = GetDepth(uv);
	float3 screenPos = GetScreenPos(uv, depth);
	// float3 hitPacked = tex2Dlod(_RayCast, float4(uv, 0.0, 0.0));
	float3 hitPacked = SAMPLE_TEXTURE2D_LOD(_RayCastTex, sampler_linear_repeat, uv, 0).xyz;

	float2 velocity;
	#if defined(UNITY_REVERSED_Z)
	hitPacked.z = 1.0f - hitPacked.z;
	#endif
	velocity = CalculateMotion(hitPacked.z,uv); // Reflection Depth derived motion. Removes smudghing cause by screen motion vectors.

	float2 averageDepthMin = min(hitPacked.xy, velocity);
	float2 averageDepthMax = max(hitPacked.xy, velocity);

	float2 prevUV = uv - velocity;

	float4 current = GetSource(uv);
	float4 previous = SAMPLE_TEXTURE2D_LOD(_PreviousBuffer, sampler_linear_repeat, prevUV, 0);

	float2 du = float2(_CameraBufferSize.x, 0.0);
	float2 dv = float2(0.0, _CameraBufferSize.y);

	float4 currentTopLeft = GetSource(uv.xy - dv - du);
	float4 currentTopCenter = GetSource(uv.xy - dv);
	float4 currentTopRight = GetSource(uv.xy - dv + du);
	float4 currentMiddleLeft = GetSource(uv.xy - du);
	float4 currentMiddleCenter = GetSource(uv.xy);
	float4 currentMiddleRight = GetSource(uv.xy + du);
	float4 currentBottomLeft = GetSource(uv.xy + dv - du);
	float4 currentBottomCenter = GetSource(uv.xy + dv);
	float4 currentBottomRight = GetSource(uv.xy + dv + du);

	float4 currentMin = min(currentTopLeft, min(currentTopCenter, min(currentTopRight, min(currentMiddleLeft, min(currentMiddleCenter, min(currentMiddleRight, min(currentBottomLeft, min(currentBottomCenter, currentBottomRight))))))));
	float4 currentMax = max(currentTopLeft, max(currentTopCenter, max(currentTopRight, max(currentMiddleLeft, max(currentMiddleCenter, max(currentMiddleRight, max(currentBottomLeft, max(currentBottomCenter, currentBottomRight))))))));

	float scale = _TScale;

	float4 center = (currentMin + currentMax) * 0.5f;
	currentMin = (currentMin - center) * scale + center;
	currentMax = (currentMax - center) * scale + center;

	previous = clamp(previous, currentMin, currentMax);

	reflection = lerp(current, previous, saturate(_TResponse *  (1 - length(velocity) * 8)) );
}

float4 combine(Varyings i ) : SV_Target
{
	float4 gbufferRef = SAMPLE_TEXTURE2D_LOD(_GBufferRT3, sampler_linear_clamp, i.screenUV, 0);
	float4 ssr = SAMPLE_TEXTURE2D_LOD(_ReflectionBuffer, sampler_linear_clamp, i.screenUV, 0);
	// float mask = SAMPLE_TEXTURE2D_LOD(_RayCastMaskTex, sampler_linear_clamp, i.screenUV, 0);
	return lerp(gbufferRef, float4(ssr.xyz, gbufferRef.w), sqrt(ssr.a));
}



#endif
