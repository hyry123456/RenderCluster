#ifndef BULK_LIGHT_PASS
#define BULK_LIGHT_PASS

//体积光裁剪后的数据
struct BulkLightStruct
{
	float3 center;
	float3 extend;
	/// <summary>  /// 灯光编号，最多一个体积块中支持4个灯光   /// </summary>
	// float4 lightIndex;
};

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"
#include "../../ShaderLibrary/Surface.hlsl"
#include "../../ShaderLibrary/Shadows.hlsl"
#include "../../ShaderLibrary/Light.hlsl"
#include "../../ShaderLibrary/BRDF.hlsl"
#include "../../ShaderLibrary/GI.hlsl"
#include "../../ShaderLibrary/Lighting.hlsl"
// #include "../../ShaderLibrary/Noise.hlsl"
// #include "../../CommonInput/CommonInput.hlsl"

StructuredBuffer<BulkLightStruct> _BoxsBuffer;      //组数据

float _BulkSampleCount;
float _BulkLightShrinkRadio;
float _BulkLightScatterRadio;

TEXTURE2D(_Noise);
TEXTURE2D(_RenderCameraDepth);

// struct GemoInput{
//     uint index : VAR_INDEX;
// };

struct appdata
{
    float3 positionOS : POSITION;
    uint instanceID : SV_InstanceID;
};


struct FragmentInput{
    float4 positionCS : SV_POSITION;
    //用W轴存储Box的距离
    float3 positionWS : VAR_POSITION;
    float3 boxMax : VAR_BOXMAX;
    float3 boxMin : VAR_BOXMIN;
};

FragmentInput vert(appdata input){
    BulkLightStruct bulk = _BoxsBuffer[input.instanceID];
    FragmentInput output;
    output.positionWS = input.positionOS * bulk.extend + bulk.center;
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.boxMax = 0.5 * bulk.extend + bulk.center;
    output.boxMin = -0.5 * bulk.extend + bulk.center;
    return output;
}

//depth是深度图深度
float3 GetWorldPos(float depth, float2 uv){
    #if defined(UNITY_REVERSED_Z)
        depth = 1 - depth;
    #endif
	float4 ndc = float4(uv.x * 2 - 1, uv.y * 2 - 1, depth * 2 - 1, 1);

	float4 worldPos = mul(_InverseVPMatrix, ndc);
	worldPos /= worldPos.w;
	return worldPos.xyz;
}

float3 GetStaticBulkLighting(float3 worldPos, float3 viewDirection, float scatterRadio){
	ShadowData shadowData = GetShadowDataByPosition(worldPos);
	// ShadowData shadowData = GetMyShadowDataByPosition(worldPos);

	float3 color = 0;

    float radio = 1;

#ifdef _DRAW_DIRECTION_LIGHT

    for (int i = 0; i < GetDirectionalLightCount(); i++) {
		Light light = GetDirectionalLightByPosition(i, worldPos, shadowData);
        light.attenuation *= radio;
		color += BulkIncomingLight(light, viewDirection, scatterRadio);
	}
#endif

#ifdef _DRAW_OTHER_LIGHT

	for (int j = 0; j < GetOtherLightCount(); j++) {
		Light light = GetOtherLightByPosition(j, worldPos, shadowData);
        light.attenuation *= radio;
		color += BulkIncomingLight(light, viewDirection, scatterRadio);
	}

#endif

	return color;
}



#define random(seed) sin(seed * 641.5467987313875 + 1.943856175)

void RayTracingBox(float3 direction, float3 maxBox, float3 minBox, out float inT, out float outT, float3 origin){
    outT = 1.#INF; inT = -10000;
    for(int i = 0; i < 3; i++){
        if(direction[i] > 0){
            outT = min((maxBox[i] - origin[i]) / direction[i], outT);
            inT = max((minBox[i] - origin[i]) / direction[i], inT);
        }
        else{
            outT = min((minBox[i] - origin[i]) / direction[i], outT);
            inT = max((maxBox[i] - origin[i]) / direction[i], inT);
        }
    }

}


//当点1、2和起点都在一条线上时，检查1是否离起点更近
bool Check1Near2(float3 beginPos, float3 firPos, float3 secPos) {
    float3 time1 = firPos - beginPos,
        time2 = secPos - beginPos;
    return dot(time1, time1) < dot(time2, time2);
}


float3 GetBulkLight(float3 worldPos, float4 positionCS, float3 boxMax, float3 boxMin, float3 direction){
    float3 viewDirection = -direction;

    float2 screenUV = positionCS.xy * _CameraBufferSize.zw * 2;	//体积光渲染在一张半贴图上

    float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_RenderCameraDepth, sampler_point_clamp, screenUV, 0);

    float eyeDepth = IsOrthographicCamera() ? OrthographicDepthBufferToLinear(bufferDepth) 
		: LinearEyeDepth(bufferDepth, _ZBufferParams);

    float3 bufferPos = GetWorldPos(bufferDepth, screenUV);

    float outT, inT;

    
    //追踪出盒子的起点以及终点耗时
    RayTracingBox(direction, boxMax, boxMin, inT, outT, _WorldSpaceCameraPos);

    float3 targetPos = _WorldSpaceCameraPos + direction * outT;


    float3 begin = _WorldSpaceCameraPos + direction * max(inT, 0);
    float3 end;

    //如果物体的起点比盒子的起点更近，就是被物体挡住，不渲染
    if (Check1Near2(_WorldSpaceCameraPos, bufferPos, begin)) return float3(0, 0, 0);

    if(Check1Near2(_WorldSpaceCameraPos, bufferPos, targetPos)) end = bufferPos;
    else end = targetPos;

    float3 color = 0;
    float seed = 0;
    // float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, (screenUV * 0.5) * _ScreenParams.xy, 0).xy;
    float2 jitter = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_linear_repeat, screenUV * (_ScreenParams.xy / 30), 0).xy;
    seed = frac(jitter.x + jitter.y);

    float3 step = (end - begin) / _BulkSampleCount;
    float3 current = begin;

    int i = 0;
    for(; i < _BulkSampleCount; i++){
        current += step;

        float3 tempPosition = current + step * seed;
        color += GetStaticBulkLighting(tempPosition, viewDirection, _BulkLightScatterRadio) ;
    }
    color *= _BulkLightShrinkRadio * length(step) / _BulkSampleCount;
    color.x = max(color.x, 0.0);
    color.y = max(color.y, 0.0);
    color.z = max(color.z, 0.0);

    return color;

}



float4 frag(FragmentInput input) : SV_TARGET{
    // return 1;
    float3 direction = normalize(input.positionWS.xyz - _WorldSpaceCameraPos);
    
    float3 color = GetBulkLight(input.positionWS, input.positionCS, input.boxMax, input.boxMin, direction);
    return float4(color, 1);
}



#endif