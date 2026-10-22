#ifndef CUSTOMRP_DEFFER_PBR_PASS
#define CUSTOMRP_DEFFER_PBR_PASS


#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"



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
	
	float3 color = GetGBufferLight(surface, input.positionCS_SS);

    float3 reflect_dir = reflect(-surface.viewDirection, surface.normal);
    float mip_Level = surface.metallic * (1.7 - 0.7 * surface.metallic);
    float3 refl = SamplerReflectProbe0(reflect_dir, mip_Level);

    float oneMinusReflectivity = OneMinusReflectivity(surface.metallic);
    float3 specColor = lerp(0.219, surface.color, surface.metallic);
    half grazingTerm = saturate((1 - surface.roughness) + (1 - oneMinusReflectivity));

	float3 sh = GetBakeDate(GI_FRAGMENT_DATA(input), input.positionWS, surface.normal) * surface.ambientOcclusion;
	sh *= surface.color;
#ifdef _GRAY_TO_ALPHA
	sh = 0;
#endif


    refl *= ComputeFresnelLerp(specColor, grazingTerm, saturate(dot(surface.normal, -surface.viewDirection)));
	color.xyz += refl * surface.ambientOcclusion + sh;
	color += GetEmission(config);

#ifdef _GRAY_TO_ALPHA

	float am = dot(color, half3(0.2126729f,  0.7151522f, 0.0721750f));
	surface.alpha = saturate(saturate(am - INPUT_PROP(_GraySetp)) + surface.alpha);

#endif

	return float4(color, surface.alpha);
}

#endif
