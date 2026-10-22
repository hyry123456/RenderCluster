#ifndef CUSTOMRP_PBR_PASS
#define CUSTOMRP_PBR_PASS


#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
#include "../ShaderLibrary/BRDF.hlsl"
#include "../ShaderLibrary/GI.hlsl"
#include "../ShaderLibrary/Lighting.hlsl"



struct Attributes {
	float3 positionOS : POSITION;
	float3 normalOS : NORMAL;
	float4 tangentOS : TANGENT;
	float2 baseUV : TEXCOORD0;
	GI_ATTRIBUTE_DATA
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
	float4 positionCS_SS : SV_POSITION;
	float3 positionWS : VAR_POSITION;
	float3 normalWS : VAR_NORMAL;
	#if defined(_NORMAL_MAP)
		float4 tangentWS : VAR_TANGENT;
	#endif
	float2 baseUV : VAR_BASE_UV;
	#if defined(_DETAIL_MAP)
		float2 detailUV : VAR_DETAIL_UV;
	#endif
	GI_VARYINGS_DATA
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings LitPassVertex (Attributes input) {
	Varyings output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);
	TRANSFER_GI_DATA(input, output);
	output.positionWS = TransformObjectToWorld(input.positionOS);
	output.positionCS_SS = TransformWorldToHClip(output.positionWS);
	output.normalWS = TransformObjectToWorldNormal(input.normalOS);
	#if defined(_NORMAL_MAP)
		output.tangentWS = float4(
			TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w
		);
	#endif
	output.baseUV = TransformBaseUV(input.baseUV);
	#if defined(_DETAIL_MAP)
		output.detailUV = TransformDetailUV(input.baseUV);
	#endif
	return output;
}

float3 BlendTriplanarNormal(float3 mappedNormal, float3 surfaceNormal){
    float3 n;
    n.xy = mappedNormal.xy + surfaceNormal.xy;
    n.z = abs(mappedNormal.z) * surfaceNormal.z;
    return n;
}


float4 LitPassFragment (Varyings input) : SV_TARGET {
	UNITY_SETUP_INSTANCE_ID(input);
	InputConfig config = GetInputConfig(input.baseUV);
	ClipLOD(input.positionCS_SS.xy, unity_LODFade.x);
	
	#if defined(_MASK_MAP)
		config.useMask = true;
	#endif
	#if defined(_DETAIL_MAP)
		config.detailUV = input.detailUV;
		config.useDetail = true;
	#endif
	
	float4 base = GetBase(config);
	#if defined(_CLIPPING)
		clip(base.a - GetCutoff());
	#endif
	
	Surface surface = (Surface)0;
	surface.position = input.positionWS;
	#if defined(_NORMAL_MAP)
		// float3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, config.baseUV), _NormalScale);
		// normalTS.xy *= _NormalScale; normalTS.z = sqrt(1.0 - saturate(dot(input.normalWS.xy, input.normalWS.xy)));
		// float3 normal = BlendTriplanarNormal(normalTS, input.normalWS.xyz);
		// float3 normal += 
		// float3 normalTS = SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, config.baseUV);
		// return float4(normal, 1);
		// surface.normal = normalTS;
		surface.normal = NormalTangentToWorld(
			GetNormalTS(config), input.normalWS, input.tangentWS
		);
	#else
		surface.normal = normalize(input.normalWS);
	#endif


	surface.viewDirection = normalize(_WorldSpaceCameraPos - input.positionWS);
	surface.depth = -TransformWorldToView(input.positionWS).z;
	surface.color = base.rgb;
	surface.alpha = base.a;
	surface.metallic = GetMetallic(config);
	surface.roughness = GetRoughness(config);
	surface.ambientOcclusion = GetOcclusion(config);
	surface.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
	
	float3 color = 0;

	float3 sh = 0;
#ifdef _RENDER_LIGHTS
	color = GetLight(surface);
	sh = GetBakeDate(GI_FRAGMENT_DATA(input), input.positionWS, surface.normal) * surface.ambientOcclusion;
#endif




	sh *= surface.color;


	float3 refl = 0;
	float3 viewDir = normalize(_WorldSpaceCameraPos - input.positionWS);
	float3 reflect_dir = reflect(-viewDir, surface.normal);
	// float3 reflect_dir = reflect(-viewDir, perNormal);
	float mip = PerceptualRoughnessToMipmapLevel(surface.roughness);
	half oneMinusReflectivity = (1 - surface.metallic) * 0.96;

#ifdef _PLANE_REFLECT
	float2 screenUV = input.positionCS_SS.xy * _CameraBufferSize.zw;
	refl = PlaneReflect(screenUV, surface.normal, mip);

#else
	refl = SamplerReflectProbe0(reflect_dir, mip);
#endif

	half nv = saturate(dot(surface.normal, viewDir));

	half3 specColor = lerp(0.2, base.rgb, surface.metallic);

	//计算掠射角时反射率，金属度越大，这个值越大，粗糙度越大，这个值越小
	half grazingTerm = saturate((1 - surface.roughness) + (1 - oneMinusReflectivity));
	refl *= ComputeFresnelLerp(specColor, grazingTerm, nv);


	color.xyz += refl * surface.color * surface.ambientOcclusion + sh;
	color += GetEmission(config);

	return float4(color, surface.alpha);
}

#endif
