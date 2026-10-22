#ifndef CUSTOMRP_DEFFER_CAMERA_PASS
#define CUSTOMRP_DEFFER_CAMERA_PASS



#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"
#include "../../../CommonInput/CommonInput.hlsl"

struct Attributes{
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
};

struct RayVaryings {
    float4 positionCS_SS : SV_POSITION;
    float2 screenUV : VAR_SCREEN_UV;
    float2 screenUV_depth : VAR_SCREEN_UV_DEPTH;
    float4 interpolatedRay : TEXCOORD2;
    float3 viewRay : VAR_VIEWRAY;
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


RayVaryings BlitPassRayVertex(Attributes input){
    RayVaryings output = (RayVaryings)0;
    output.positionCS_SS = TransformObjectToHClip(input.positionOS);
    output.screenUV = input.baseUV;
    output.screenUV_depth = input.baseUV;

    #if UNITY_UV_STARTS_AT_TOP
        if (_SourceTexture_TexelSize.y < 0)
            output.screenUV_depth.y = 1 - output.screenUV_depth.y;
    #endif

    int index = 0;
    if (output.screenUV.x < 0.5 && output.screenUV.y < 0.5) {         //位于左下方区域，使用左下方的方向
        index = 0;
    } 
    else if (output.screenUV.x > 0.5 && output.screenUV.y < 0.5) {    //位于右下方
        index = 1;
    } 
    else if (output.screenUV.x > 0.5 && output.screenUV.y > 0.5) {    //右上方
        index = 2;
    } 
    else {                                                  //左上方
        index = 3;
    }

    #if UNITY_UV_STARTS_AT_TOP
    if (_SourceTexture_TexelSize.y < 0)
        index = 3 - index;
    #endif

    //获得对应的方向值，之所以我们只用射向远平面的4个点的原因是对于Unity来说，屏幕纹理就是一个正好填充屏幕的4个顶点形成的纹理
    //因此这里描述的顶点也只有4个，因此在传入片元着色器时我们会进行线性插值，插值后的结果就是正确的该纹理方向了
    output.interpolatedRay = _FrustumCornersRay[index];
    return output;
}

Varyings BlitPassSimpleVertex(Attributes input){
    Varyings output = (Varyings)0;
    output.positionCS_SS = TransformObjectToHClip(input.positionOS);
    output.screenUV = input.baseUV;
    return output;
}

float4 CopyFragment(Varyings i) : SV_Target
{
    // return 1;
	return SAMPLE_TEXTURE2D_LOD(_SourceTexture, sampler_point_clamp, i.screenUV, 0);
}

float CopyDepthPassFragment (Varyings input) : SV_DEPTH {
	return
        SAMPLE_DEPTH_TEXTURE_LOD(_SourceTexture, sampler_point_clamp, input.screenUV, 0);
}


float4 DrawGBufferColorFragment(RayVaryings i) : SV_Target
{
    float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_GBufferDepthTex, sampler_point_clamp, i.screenUV, 0);
	float depth01 = Linear01Depth(bufferDepth, _ZBufferParams);
	float eyeDepth = IsOrthographicCamera() ? OrthographicDepthBufferToLinear(bufferDepth)
		: LinearEyeDepth(bufferDepth, _ZBufferParams);
    // bufferDepth = 
    Surface surface = (Surface)0;
    surface.position = _WorldSpaceCameraPos + eyeDepth * i.interpolatedRay.xyz;

	float4 rt0 = SAMPLE_TEXTURE2D(_GBufferRT0, sampler_linear_clamp, i.screenUV);     	//rgb:abledo,w:metalness
	float4 rt1 = SAMPLE_TEXTURE2D(_GBufferRT1, sampler_linear_clamp, i.screenUV);     	//R,G,B:normal
	float4 rt2 = SAMPLE_TEXTURE2D(_GBufferRT2, sampler_linear_clamp, i.screenUV);     	//rgb:emissive,w:roughness
	// float4 rt3 = SAMPLE_TEXTURE2D(_SSRTargetTex, sampler_linear_clamp, i.screenUV);     	//rgb:reflect,w:AO
	float4 rt3 = SAMPLE_TEXTURE2D(_GBufferRT3, sampler_linear_clamp, i.screenUV);     	//rgb:reflect,w:AO
	float3 normalWS = UnpackNormal(rt1.xyz);
    surface.normal = normalWS;     //法线
	// return float4(normalWS, 1);
    surface.viewDirection = normalize(_WorldSpaceCameraPos - surface.position);                                     //视线方向
    surface.depth = eyeDepth;                                                                                    //深度
    surface.metallic = rt0.w;
    surface.roughness = rt2.w;
    surface.dither = InterleavedGradientNoise(i.positionCS_SS.xy, 0);
	surface.ambientOcclusion = rt3.w;
	surface.color = rt0.rgb;


    float3 color;
	color = GetGBufferLight(surface, i.screenUV);

    // 天空盒 SH9 间接漫反射（之前延迟管线完全没有这一步，导致阴影 / 背光面全黑）。
    // _Sh9 由 SceneSH9 从天空盒 Cubemap 投影得到，经 GPUDravinSystem 每帧设成全局量。
    // 金属度高的表面漫反射应趋近 0，故乘 (1 - metallic*0.96)；再乘 albedo 与 AO。
    float3 indirectDiffuse = max(GetIndirectLight(normalWS), 0.0)
        * rt0.rgb * (1.0 - surface.metallic * 0.96) * surface.ambientOcclusion;
    color.xyz += indirectDiffuse;

	float3 reflect = rt3.rgb;

    half oneMinusReflectivity = (1 - surface.metallic) * 0.96;
    half nv = saturate(dot(normalWS, surface.viewDirection));

    half3 specColor = lerp(0.14, rt0.xyz, surface.metallic);

    half grazingTerm = saturate((1 - surface.roughness) + (1 - oneMinusReflectivity));
    reflect *= ComputeFresnelLerp(specColor, grazingTerm, nv) * surface.ambientOcclusion;
	// reflect *= surface.ambientOcclusion;
	color.xyz += reflect;
    color += rt2.rgb;

    return float4(color, 1);
}

float4 AddReflectFragment(RayVaryings i) : SV_Target
{
    float bufferDepth = SAMPLE_DEPTH_TEXTURE_LOD(_GBufferDepthTex, sampler_point_clamp, i.screenUV, 0);
	float depth01 = Linear01Depth(bufferDepth, _ZBufferParams);
	float eyeDepth = IsOrthographicCamera() ? OrthographicDepthBufferToLinear(bufferDepth)
		: LinearEyeDepth(bufferDepth, _ZBufferParams);
    float3 position = _WorldSpaceCameraPos + eyeDepth * i.interpolatedRay.xyz;

	float4 rt0 = SAMPLE_TEXTURE2D(_GBufferRT0, sampler_linear_clamp, i.screenUV);     	//rgb:abledo,w:metalness
	float4 rt1 = SAMPLE_TEXTURE2D(_GBufferRT1, sampler_linear_clamp, i.screenUV);     	//R,G,B:normal
	float4 rt2 = SAMPLE_TEXTURE2D(_GBufferRT2, sampler_linear_clamp, i.screenUV);     	//rgb:emissive,w:roughness
	float4 rt3 = SAMPLE_TEXTURE2D(_SSRTargetTex, sampler_linear_clamp, i.screenUV); 
	// float4 rt3 = SAMPLE_TEXTURE2D(_GBufferRT3, sampler_linear_clamp, i.screenUV); 

    float metallic = rt0.w, roughness = rt2.w;
    float3 baseCol = rt0.xyz;
	float3 reflect = rt3.rgb;
	float3 normalWS = UnpackNormal(rt1.xyz);
    float3 viewDirection = normalize(_WorldSpaceCameraPos - position);
	float oneMinusReflectivity = OneMinusReflectivity(metallic);


	float3 specColor = lerp(0.219, baseCol, metallic);

	float grazingTerm = saturate((1 - roughness) + (1 - oneMinusReflectivity));

	reflect *= ComputeFresnelLerp(specColor, grazingTerm, saturate(dot(normalWS, -viewDirection)));
	reflect *= baseCol * rt3.w;
    return float4(reflect, 1);
}

float4 DebugNormal(Varyings i) : SV_Target{
	float4 rt1 = SAMPLE_TEXTURE2D(_GBufferRT1, sampler_linear_clamp, i.screenUV);     	//R,G,B:normal
	float3 normalWS = UnpackNormal(rt1.xyz);
    // 注意：绝不能直接输出 normalWS —— 渲染目标的负分量会被硬截成 0，画面变成纯红/绿/品红色块，
    // 完全无法判断方向对错（此前的调试图就是这个坑）。标准法线可视化必须重映射到 [0,1]：
    //   (-1,0,1) -> (0,0.5,1)，0.5 表示分量为 0。
    // 因此：正对相机的平面 = (0.5,0.5,1) 淡蓝紫；地面(法线朝上) = (0.5,1,0.5) 淡绿。
    return float4(normalWS * 0.5 + 0.5, 1);

}

float4 DebugAlbedo(Varyings i) : SV_Target{
	float4 albedo = SAMPLE_TEXTURE2D(_GBufferRT0, sampler_linear_clamp, i.screenUV);
    return float4(albedo.xyz, 1);
}

float4 DebugPBR(Varyings i) : SV_Target{
	float4 rt0 = SAMPLE_TEXTURE2D(_GBufferRT0, sampler_linear_clamp, i.screenUV);
	float4 rt2 = SAMPLE_TEXTURE2D(_GBufferRT2, sampler_linear_clamp, i.screenUV);     	//rgb:emissive,w:roughness
	float4 rt3 = SAMPLE_TEXTURE2D(_GBufferRT3, sampler_linear_clamp, i.screenUV);     	//rgb:reflect,w:AO
    return float4(rt0.a, rt2.w, rt3.w, 1);

}


#endif