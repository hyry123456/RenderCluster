#ifndef DEFFER_SSR_BACK_PASS
#define DEFFER_SSR_BACK_PASS

struct Attributes {
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
	float4 positionCS_SS : SV_POSITION;
    float2 baseUV : BASE_UV;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings SSRBACKPassVertex (Attributes input) {
	Varyings output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);
	output.positionCS_SS = TransformObjectToHClip(input.positionOS);
    output.baseUV = input.baseUV;
	return output;
}

void SSRBACKPassFragment (Varyings input) {
	UNITY_SETUP_INSTANCE_ID(input);
	// InputConfig config = GetInputConfig(input.baseUV);
	ClipLOD(input.positionCS_SS.xy, unity_LODFade.x);
	
	float4 base = GetBase(input.baseUV);
	#if defined(_SHADOWS_CLIP)
		clip(base.a - GetCutoff());
	#endif
}

#endif
