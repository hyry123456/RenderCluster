#ifndef CUSTOMRP_SHADOW_INPUT
#define CUSTOMRP_SHADOW_INPUT

struct Attributes_Shadow {
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings_Shadow {
	float4 positionCS_SS : SV_POSITION;
	float2 baseUV : VAR_BASE_UV;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};
bool _ShadowPancaking;


Varyings_Shadow ShadowCasterPassVertex (Attributes_Shadow input) {
	Varyings_Shadow output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);
	float3 positionWS = TransformObjectToWorld(input.positionOS);
	output.positionCS_SS = TransformWorldToHClip(positionWS);

	if (_ShadowPancaking) {
		#if UNITY_REVERSED_Z
			output.positionCS_SS.z = min(
				// output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE
				output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE
			);
		#else
			output.positionCS_SS.z = max(
				output.positionCS_SS.z, UNITY_NEAR_CLIP_VALUE
				// output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE
			);
		#endif
	}

	output.baseUV = TransformBaseUV(input.baseUV);
	return output;
}

void ShadowCasterPassFragment (Varyings_Shadow input) {
	UNITY_SETUP_INSTANCE_ID(input);
	// InputConfig config = GetInputConfig(input.baseUV);
	ClipLOD(input.positionCS_SS.xy, unity_LODFade.x);
	
	float4 base = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.baseUV);
	#if defined(_SHADOWS_CLIP)
		clip(base.a - GetCutoff());
	#elif defined(_SHADOWS_DITHER)
		float dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
		clip(base.a - dither);
	#endif
}


#endif
