#ifndef DEFFER_SSR_INCLUDE
#define DEFFER_SSR_INCLUDE

float4x4 _WorldToCameraMatrix;
float4x4 _ProjectionMatrix;
float4x4 _PrevViewProjectionMatrix;


TEXTURE2D(_Noise);
TEXTURE2D(_RayCastTex);
TEXTURE2D(_SSRBackDepth);
TEXTURE2D(_RayCastMaskTex);
TEXTURE2D(_PreviousBuffer);
TEXTURE2D(_ReflectionBuffer);

float4 _SSR_Size;
float4 _NoiseSize;
float4 _JitterSizeAndOffset;
float _BRDFBias;
float _EdgeFactor;

int _NumSteps;
int _RayReuse;
int _UseNormalization;
int _Fireflies;

float _TScale;
float _TResponse;

static const float2 offset[4] =
{
	float2(0, 0),
	float2(2, -2),
	float2(-2, -2),
	float2(0, 2)
};

inline half SmithJointGGXVisibilityTerm (half NdotL, half NdotV, half roughness)
{
    half a = roughness;
    half lambdaV = NdotL * (NdotV * (1 - a) + a);
    half lambdaL = NdotV * (NdotL * (1 - a) + a);

    return 0.5f / (lambdaV + lambdaL + 1e-5f);
}

inline float GGXTerm (float NdotH, float roughness)
{
    float a2 = roughness * roughness;
    float d = (NdotH * a2 - NdotH) * NdotH + 1.0f; // 2 mad
    return INV_PI * a2 / (d * d + 1e-7f); // This function is not intended to be running on Mobile,
}


float BRDF_Unity_Weight(float3 V, float3 L, float3 N, float Roughness)
{
	float3 H = normalize(L + V);

	float NdotH = saturate(dot(N,H));
	float NdotL = saturate(dot(N,L));
	float NdotV = saturate(dot(N,V));

	half G = SmithJointGGXVisibilityTerm (NdotL, NdotV, Roughness);
	half D = GGXTerm (NdotH, Roughness);

	return (D * G) * (PI / 4.0);
}

float RayAttenBorder (float2 pos, float value)
{
	float borderDist = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y));
	return saturate(borderDist > value ? 1.0 : borderDist / value);
}

float3 GetViewNormal (float3 normal)
{
	float3 viewNormal =  mul((float3x3)_WorldToCameraMatrix, normal.rgb);
	return normalize(viewNormal);
}

float GetRoughness(float2 screenUV){
	float4 rt2 = SAMPLE_TEXTURE2D(_GBufferRT2, sampler_linear_clamp, screenUV);     	//rgb:emissive,w:roughness
    return rt2.w;
}

float GetDepth(float2 screenUV){
    float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_GBufferDepthTex, sampler_point_clamp, screenUV, 0);

	#if defined(UNITY_REVERSED_Z)
		bufferDepth = 1.0f - bufferDepth;
	#endif
    return bufferDepth;
}

float GetDepthBack(float2 screenUV){
    float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_SSRBackDepth, sampler_point_clamp, screenUV, 0);

	#if defined(UNITY_REVERSED_Z)
		bufferDepth = 1.0f - bufferDepth;
	#endif
    return bufferDepth;
}

float3 GetScreenPos (float2 screenUV, float depth)
{
	return float3(screenUV * 2 - 1, depth);	//转化为屏幕空间坐标，也就是View空间
}

float3 GetWorlPos (float3 screenPos)
{
	float4 worldPos = mul(_InverseVPMatrix, float4(screenPos, 1));
	return worldPos.xyz / worldPos.w;
}

float3 GetViewDir (float3 worldPos)
{
	return normalize(worldPos - _WorldSpaceCameraPos);
}

float3 GetViewPos (float3 screenPos)
{
	float4 viewPos = mul(_InverseProjectionMatrix, float4(screenPos, 1));
	return viewPos.xyz / viewPos.w;
}

// Brian Karis, Epic Games "Real Shading in Unreal Engine 4"
float4 ImportanceSampleGGX(float2 Xi, float Roughness)
{
	float m = Roughness * Roughness;
	float m2 = m * m;
		
	float Phi = 2 * PI * Xi.x;
				 
	float CosTheta = sqrt((1.0 - Xi.y) / (1.0 + (m2 - 1.0) * Xi.y));
	float SinTheta = sqrt(max(1e-5, 1.0 - CosTheta * CosTheta));
				 
	float3 H;
	H.x = SinTheta * cos(Phi);
	H.y = SinTheta * sin(Phi);
	H.z = CosTheta;
		
	float d = (CosTheta * m2 - CosTheta) * CosTheta + 1;
	float D = m2 / (PI * d * d);
	float pdf = D * CosTheta;

	return float4(H, pdf); 
}

float4 TangentToWorld(float3 N, float4 H)
{
	float3 UpVector = abs(N.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
	float3 T = normalize( cross( UpVector, N ) );
	float3 B = cross( N, T );
				 
	return float4((T * H.x) + (B * H.y) + (N * H.z), H.w);
}


//追踪算法
float4 RayMarch(float4x4 _ProjectionMatrix, float3 viewDir, int NumSteps, float3 viewPos, float3 screenPos, float2 screenUV, float stepSize)
{
	//float3 dirProject = _Project;
	float4 dirProject = float4
	(
		abs(unity_CameraProjection._m00 * 0.5), 
		abs(unity_CameraProjection._m11 * 0.5), 
		((_ProjectionParams.z * _ProjectionParams.y) / (_ProjectionParams.y - _ProjectionParams.z)) * 0.5,
		0.0
	);

	//采样摄像机深度，转化为视角深度
	float linearDepth  =  LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_GBufferDepthTex, sampler_point_clamp, screenUV.xy, 0), _ZBufferParams);

	float3 ray = viewPos / viewPos.z;
	float3 rayDir = normalize(float3(viewDir.xy - ray.xy * viewDir.z, viewDir.z / linearDepth) * dirProject.xyz);
	rayDir.xy *= 0.5;

	float3 rayStart = float3(screenPos.xy * 0.5 + 0.5,  screenPos.z);

	float3 samplePos = rayStart;

	float project = ( _ProjectionParams.z * _ProjectionParams.y) / (_ProjectionParams.y - _ProjectionParams.z); 

	//float thickness = thickness;//1.0 / _ZBufferParams.x * (float)NumSteps * linearDepth;//project / linearDepth;

	float mask = 0.0;

	float oldDepth = samplePos.z;
	// float oldDelta = 0.0;
	// float3 oldSamplePos = samplePos;
	float stepV = stepSize;

	for (int i = 0;  i < NumSteps; i++)
	{
		float depth = GetDepth (samplePos.xy);
		float delta = samplePos.z - depth;
		float depthBack;
#if _SSR_BACK_CLIP
		depthBack = GetDepthBack(samplePos.xy);

		if (delta > 0.0)
#else
		depthBack = depth + 0.001;

		if (0.00000001 < delta)
#endif
		{
			if(samplePos.z <= depthBack){
				mask = 1.0;		//读到了就标记为1
				break;
			}
			// else{
			// 	stepSize = stepV;
			// }
		}
		// else
		// {
		// 	oldDelta = -delta;
		// 	oldSamplePos = samplePos;
		// }
		oldDepth = depth; 
		samplePos += rayDir * stepSize;
		// stepSize += stepV;
	}
	return float4(samplePos, mask);
}

inline half2 CalculateMotion(float rawDepth, float2 inUV)
{
	float3 screenPos = GetScreenPos(inUV, rawDepth);
	float4 worldPos = float4(GetWorlPos(screenPos),1);

	float4 prevClipPos = mul(_PrevViewProjectionMatrix, worldPos);
	// float4 curClipPos = mul(_ViewProjectionMatrix, worldPos);
	float4 curClipPos = mul(_ViewToScreenMatrix, worldPos);

	float2 prevHPos = prevClipPos.xy / prevClipPos.w;
	float2 curHPos = curClipPos.xy / curClipPos.w;

	// V is the viewport position at this pixel in the range 0 to 1.
	float2 vPosPrev = (prevHPos.xy + 1.0f) / 2.0f;
	float2 vPosCur = (curHPos.xy + 1.0f) / 2.0f;
	return vPosCur - vPosPrev;
}


#endif