#ifndef CUSTOMRP_POSTFX_PASS
#define CUSTOMRP_POSTFX_PASS

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"

#include "../../CommonInput/CommonInput.hlsl"
#include "PostFXInput.hlsl"


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

Varyings BlitPassVertex(Attributes input){
	Varyings output = (Varyings)0;
	output.positionCS_SS = TransformObjectToHClip(input.positionOS);
	output.screenUV = input.baseUV;
	return output;
}

float4 BloomAddPassFragment (Varyings input) : SV_TARGET {
	float3 lowRes;
	if (_BloomBicubicUpsampling) {
		lowRes = GetSourceBicubic(input.screenUV).rgb;
	}
	else {
		lowRes = GetSource(input.screenUV).rgb;
	}
	float4 highRes = GetSource2(input.screenUV);
	return float4(lowRes * _BloomIntensity + highRes.rgb, highRes.a);
}

float4 BloomHorizontalPassFragment (Varyings input) : SV_TARGET {
	float3 color = 0.0;
	float offsets[] = {
		-3.23076923, -1.38461538, 0.0, 1.38461538, 3.23076923
	};
	float weights[] = {
		0.07027027, 0.31621622, 0.22702703, 0.31621622, 0.07027027
	};
	for (int i = 0; i < 5; i++) {
		float offset = offsets[i] * GetSourceTexelSize().y;
		color += GetSource(input.screenUV + float2(offset, 0.0)).rgb * weights[i];
	}
	return float4(color, 1.0);
}

float4 BloomVerticalPassFragment(Varyings input) : SV_TARGET{
	float3 color = 0.0;
	float offsets[] = {
		-3.23076923, -1.38461538, 0.0, 1.38461538, 3.23076923
	};
	float weights[] = {
		0.07027027, 0.31621622, 0.22702703, 0.31621622, 0.07027027
	};
	for (int i = 0; i < 5; i++) {
		float offset = offsets[i] * GetSourceTexelSize().y;
		color += GetSource(input.screenUV + float2(0.0, offset)).rgb * weights[i];
	}
	return float4(color, 1.0);
}

float4 BloomPrefilterPassFragment (Varyings input) : SV_TARGET {
	float3 color = ApplyBloomThreshold(GetSource(input.screenUV).rgb);
	color = saturate(color);
	return float4(color, 1.0);
}


float4 BloomPrefilterFirefliesPassFragment (Varyings input) : SV_TARGET {
	float3 color = 0.0;
	float weightSum = 0.0;
	float2 offsets[] = {
		float2(0.0, 0.0),
		float2(-1.0, -1.0), float2(-1.0, 1.0), float2(1.0, -1.0), float2(1.0, 1.0)
	};
	for (int i = 0; i < 5; i++) {
		float3 c =
			GetSource(input.screenUV + offsets[i] * GetSourceTexelSize().xy * 2.0).rgb;
		c = ApplyBloomThreshold(c);
		float w = 1.0 / (Luminance(c) + 1.0);
		color += c * w;
		weightSum += w;
	}
	color /= weightSum;
	color = saturate(color);
	return float4(color, 1.0);
}

float4 BloomScatterPassFragment (Varyings input) : SV_TARGET {
	float3 lowRes;
	if (_BloomBicubicUpsampling) {
		lowRes = GetSourceBicubic(input.screenUV).rgb;
	}
	else {
		lowRes = GetSource(input.screenUV).rgb;
	}
	float3 highRes = GetSource2(input.screenUV).rgb;
	return float4(lerp(highRes, lowRes, _BloomIntensity), 1.0);
}


float4 BloomScatterFinalPassFragment (Varyings input) : SV_TARGET {
	float3 lowRes;
	if (_BloomBicubicUpsampling) {
		lowRes = GetSourceBicubic(input.screenUV).rgb;
	}
	else {
		lowRes = GetSource(input.screenUV).rgb;
	}
	float4 highRes = GetSource2(input.screenUV);
	lowRes += highRes.rgb - ApplyBloomThreshold(highRes.rgb);
	return float4(lerp(highRes.rgb, lowRes, _BloomIntensity), highRes.a);
}

//----------------ColorGrading----------------------

float4 ColorGradingNonePassFragment (Varyings input) : SV_TARGET {
	float3 color = GetColorGradedLUT(input.screenUV);
	return float4(color, 1.0);
}

float4 ColorGradingACESPassFragment (Varyings input) : SV_TARGET {
	float3 color = GetColorGradedLUT(input.screenUV, true);
	color = AcesTonemap(color);
	return float4(color, 1.0);
}

float4 ColorGradingNeutralPassFragment (Varyings input) : SV_TARGET {
	float3 color = GetColorGradedLUT(input.screenUV);
	color = NeutralTonemap(color);
	return float4(color, 1.0);
}

float4 ColorGradingReinhardPassFragment (Varyings input) : SV_TARGET {
	float3 color = GetColorGradedLUT(input.screenUV);
	color /= color + 1.0;
	return float4(color, 1.0);
}

//最终影响后处理结果进行渲染到主摄像机的函数
float4 FinalPassFragment (Varyings input) : SV_TARGET {
	float4 color = GetSource(input.screenUV);
	float4 color2 = GetSource2(input.screenUV);
	color.rgb += color2.rgb;
	color.rgb = ApplyColorGradingLUT(color.rgb);
	return float4(color.rgb, 1);
}
//----------------ColorGrading----------------------


//----------------FFX----------------------

float4 CaculateGray(Varyings input) : SV_TARGET{

	float4 bufferColor = GetSource(input.screenUV);
	bufferColor.a = Luminance(bufferColor.rgb);
	return bufferColor;
}

//抗锯齿
float4 FXAAFragment(Varyings input) : SV_TARGET{
	return ApplyFXAA(input.screenUV);
}
//----------------FFX----------------------



//----------------Bulk Light----------------------

float4 BlendBulkLightFragment (Varyings input) : SV_TARGET{
	float4 originCol = GetSource(input.screenUV);
	float4 bulkCol = GetSource2(input.screenUV);
	
	return float4(originCol.xyz + bulkCol.xyz, 1);
}

//----------------Bulk Light----------------------




//---------------Depth Of Field-------------------
float4 CircleOfConfusionFragment(Varyings input) : SV_TARGET{
	float depth = GetSource(input.screenUV).r;
	depth = LinearEyeDepth(depth, _ZBufferParams);

	float coc = (depth - _FocusDistance) / _FocusRange;
	coc = clamp(coc, -1, 1) * _BokehRadius;
	return coc;
}

float4 PreFilterFragment(Varyings input) : SV_TARGET{
	float4 o = GetSourceTexelSize().xyxy * float2(-0.5, 0.5).xxyy;

	half3 s0 = saturate(GetSource(input.screenUV + o.xy).rgb);
	half3 s1 = saturate(GetSource(input.screenUV + o.zy).rgb);
	half3 s2 = saturate(GetSource(input.screenUV + o.xw).rgb);
	half3 s3 = saturate(GetSource(input.screenUV + o.zw).rgb);

	half w0 = Weigh(s0);
	half w1 = Weigh(s1);
	half w2 = Weigh(s2);
	half w3 = Weigh(s3);

	half3 color = s0 * w0 + s1 * w1 + s2 * w2 + s3 * w3;
	color /= max(w0 + w1 + w2 + s3, 0.00001);
	color = saturate(color);

	half coc0 = GetSource2(input.screenUV + o.xy).r;
	half coc1 = GetSource2(input.screenUV + o.zy).r;
	half coc2 = GetSource2(input.screenUV + o.xw).r;
	half coc3 = GetSource2(input.screenUV + o.zw).r;

	half cocMin = min(min(min(coc0, coc1), coc2), coc3);
	half cocMax = max(max(max(coc0, coc1), coc2), coc3);
	half coc = cocMax >= -cocMin ? cocMax : cocMin;

	return float4(color, coc);
}


float4 BokehFragment(Varyings input) : SV_TARGET{
	half coc = GetSource(input.screenUV).a;
	
	half3 bgColor = 0, fgColor = 0;
	half bgWeight = 0, fgWeight = 0;
	for (int k = 0; k < kernelSampleCount; k++) {
		float2 o = kernel[k] * _BokehRadius;
		half radius = length(o);
		o *= GetSourceTexelSize().xy;
		half4 s = GetSource(input.screenUV + o);

		half bgw = Weigh(max(0, min(s.a, coc)), radius);
		bgColor += s.rgb * bgw;
		bgWeight += bgw;

		half fgw = Weigh(-s.a, radius);
		fgColor += s.rgb * fgw;
		fgWeight += fgw;
	}
	bgColor *= 1 / (bgWeight + (bgWeight == 0));
	fgColor *= 1 / (fgWeight + (fgWeight == 0));
	half bgfg =
		min(1, fgWeight * 3.14159265359 / kernelSampleCount);
	half3 color = lerp(bgColor, fgColor, bgfg);
	return half4(color, bgfg);
}


float4 PostFilterFragment(Varyings input) : SV_TARGET{
	float4 o = GetSourceTexelSize().xyxy * float2(-0.5, 0.5).xxyy;
	half4 s =
		GetSource(input.screenUV + o.xy) +
		GetSource(input.screenUV + o.zy) +
		GetSource(input.screenUV + o.xw) +
		GetSource(input.screenUV + o.zw);
	return s * 0.25;
}

float4 CombineFragment(Varyings input) : SV_TARGET{
	half4 source = GetSource(input.screenUV);	//颜色图
	half coc = GetSource2(input.screenUV).r;	//coc图
	half4 dof = GetSource3(input.screenUV);		//Filter图

	half dofStrength = smoothstep(0.1, 1, abs(coc));
	half3 color = lerp(
		source.rgb, dof.rgb,
		dofStrength + dof.a - dofStrength * dof.a
	);
	return float4(color, source.a);
}


//---------------Depth Of Field-------------------


//------------------Dynamic Blur-----------------
#define BlurStep 3

float4 DynamicBlurFragment(Varyings input) : SV_TARGET
{
	float2 screenUV = input.screenUV;
	float depth = SAMPLE_TEXTURE2D_LOD(_RenderCameraDepth, sampler_linear_clamp, screenUV, 0).x;

    #if defined(UNITY_REVERSED_Z)
        depth = 1 - depth;
    #endif
	float4 ndc = float4(screenUV.x * 2 - 1, screenUV.y * 2 - 1, depth * 2 - 1, 1);

	float4 worldPos = mul(_InverseVPMatrix, ndc);
	worldPos /= worldPos.w;

	float4 previousPos = mul(_PreviousViewProjectionMatrix, worldPos);

	previousPos /= previousPos.w;

	float2 velocity = (ndc.xy - previousPos.xy) * _BlurRadio;

	float2 target = saturate(screenUV + velocity * BlurStep);
	velocity = (target - screenUV) / BlurStep;

	// 获得当前点的颜色值.
   float4 color = GetSource(screenUV);
   screenUV += velocity;
	//采样
	for(uint i = 1; i < BlurStep ; ++i, screenUV += velocity)
	{
		float4 currentColor = GetSource(screenUV);
		// // 当前点的颜色加上速度向量后获取的颜色
		color += currentColor;
	}
// 除以采样次数求得颜色平均值
   float4 finalColor = color / BlurStep;
   return finalColor;
}

//------------------Dynamic Blur-----------------


float4 AutoExprosure(Varyings input) : SV_TARGET{
	float4 source = GetSource(input.screenUV);	//颜色图

    source.xyz *= pow(2, SAMPLE_TEXTURE2D_LOD(_AutoExpourseTex, sampler_linear_clamp, float2(0, 0), 0).x);
    // source.xyz = SAMPLE_TEXTURE2D_LOD(_AutoExpourseTex, sampler_linear_clamp, float2(0, 0), 0);

	return source;
}


#endif