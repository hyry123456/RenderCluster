#ifndef CUSTOMRP_DEFFER_PBR_PASS
#define CUSTOMRP_DEFFER_PBR_PASS

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
	float4 TtoW0 : VAR_TT0;
	float4 TtoW1 : VAR_TT1;
	float4 TtoW2 : VAR_TT2;
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
	float3 positionWS = TransformObjectToWorld(input.positionOS);
	float3 normalWS = TransformObjectToWorldNormal( input.normalOS );
	float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
	// 非等比缩放下切线/法线不再正交，必须 Gram-Schmidt 正交化，否则 TBN 基被剪切、
	// 法线贴图方向整体偏斜。与 ClusterPBR_BRG.shader 保持同一套修复。
	float3 orthoT = tangentWS - normalWS * dot(normalWS, tangentWS);
	float orthoTLenSq = dot(orthoT, orthoT);
	tangentWS = orthoTLenSq > 1e-8 ? orthoT * rsqrt(orthoTLenSq) : tangentWS;
	float3 binormalWS = cross(normalWS, tangentWS) * input.tangentOS.w;

	output.positionCS_SS = TransformWorldToHClip(positionWS);

	output.TtoW0 = float4(tangentWS.x, binormalWS.x, normalWS.x, positionWS.x);
	output.TtoW1 = float4(tangentWS.y, binormalWS.y, normalWS.y, positionWS.y);
	output.TtoW2 = float4(tangentWS.z, binormalWS.z, normalWS.z, positionWS.z);

	output.baseUV = TransformBaseUV(input.baseUV);
	#if defined(_DETAIL_MAP)
		output.detailUV = TransformDetailUV(input.baseUV);
	#endif
	return output;
}



void LitPassFragment (Varyings input,
        out DefferTarget target
    ) 
	{
	
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
	
	//纹理颜色
	float4 base = GetBase(config);
	#if defined(_CLIPPING)
		clip(base.a - GetCutoff());
	#endif
	
	float3 normal;
	float3 perNormal = float3(input.TtoW0.z, input.TtoW1.z, input.TtoW2.z);
	#if defined(_NORMAL_MAP)
		normal = GetNormalTS(config);
		normal = normalize(float3(dot(input.TtoW0.xyz, normal), dot(input.TtoW1.xyz, normal), dot(input.TtoW2.xyz, normal)));
	#else
		normal = normalize(perNormal);
	#endif
	float2 normalPack = PackNormal(normal);
	float metallic = GetMetallic(config);

	float3 positionWS = float3(input.TtoW0.w, input.TtoW1.w, input.TtoW2.w);
	float3 emissive = GetEmission(config);
	float roughness = GetRoughness(config);
	float ao = GetOcclusion(config);

	// 间接漫反射（SH9）统一交由延迟最终合成阶段（DefferCameraPass.hlsl）用 _Sh9 计算并叠加，
	// 这里不再把 SH 烘焙进 emissive，避免与主份/ BRG 双重叠加，保证两种材质 GBuffer 格式一致。
	// （DefferPipeline 走 #else 分支时 GetBakeDate 读 unity_SHAr 恒为 0 —— 旧逻辑等于没加；
	//   BRG 路径同样由合成阶段统一加 SH9，故此处统一丢弃 SH 烘焙。）

	// 间接镜面反射：与主份/ BRG 一致，统一从工程自己的天空盒 _MySkybox 采样
	// （不再用 unity_SpecCube0 内置反射探针 —— 否则 DefferStandard 与 BRG 反射来源不同、肉眼可见差异）。
	// 粗糙度越高越模糊（mip 上探）；非金属（木质）按 (1-roughness) 平滑衰减、保留 ~5% 下限；
	// 金属保持完整反射。与 ClusterPBR_BRG.shader 的 RT3 写入保持同一套公式，保证两材质一致。
	float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
	float3 reflect_dir = reflect(-viewDir, normal);
	float reflMip = clamp(roughness * 7.0, 0.0, 8.0);
	float3 refl = SamplerReflectProbe(reflect_dir, reflMip);
	float reflIntensity = lerp(saturate(0.05 + (1.0 - roughness) * 0.6), 1.0, metallic);
	refl *= reflIntensity;

	// half nv = saturate(dot(normal, viewDir));

	// //计算镜面反射率,unity_ColorSpaceDielectricSpec是一个固定值，rgb都大概在65的位置，不太确定有什么用
	// //菲涅尔效应的F0值，也就是菲涅尔效应的最低值的计算
	// half3 specColor = lerp(0.2, base.rgb, metallic);

	// //unity_ColorSpaceDielectricSpec.a通常为0.96，这个实际上就是计算漫反射系数，漫反射系数都是一至的，因此我们用F0来求
	// //unity_ColorSpaceDielectricSpec.a是为了确保金属都为0时都会有的基本反射值
	// // half oneMinusReflectivity = (1 - metallic) * 0.96;
	// //计算掠射角时反射率，金属度越大，这个值越大，粗糙度越大，这个值越小
	// half grazingTerm = saturate((1 - roughness) + (1 - oneMinusReflectivity));
	// refl *= ComputeFresnelLerp(specColor, grazingTerm, nv);


	float3 normalRT = 0;
// #ifdef _SSR_AFFECT_OBJECT
	normalRT = float3(normalPack, 1);
// #else
// 	normalRT = float3(normalPack, 0);
// #endif

    target._GBufferRT0 = float4(base.rgb, metallic);
	target._GBufferRT1 = float4(normalRT, 1);
	target._GBufferRT2 = float4(emissive, roughness);
	target._GBufferRT3 = float4(refl, ao);
	// target._GBufferRT3 = float4(1, 1, 1, ao);
}


struct TemAttributes {
	float3 positionOS : POSITION;
	float2 baseUV : TEXCOORD0;
	float3 normalOS : NORMAL;
};

struct TemVaryings {
	float4 positionCS_SS : SV_POSITION;
	float2 baseUV : VAR_BASE_UV;
	float3 normal : VAR_NORMAL;
};

TemVaryings TemoVert (Attributes input) {
	TemVaryings output = (TemVaryings)0;
	float3 positionWS = TransformObjectToWorld(input.positionOS);
	float3 normalWS = TransformObjectToWorldNormal( input.normalOS );

	output.positionCS_SS = TransformWorldToHClip(positionWS);
	output.baseUV = input.baseUV;
	output.normal = normalWS;
	return output;
}


void TemoFrag (TemVaryings input,
        out DefferTarget target
    ) 
	{
	float2 normalPack = PackNormal(input.normal);

    target._GBufferRT0 = float4(input.baseUV.xy, 0, 0);
	target._GBufferRT1 = float4(normalPack, 0, 1);
	target._GBufferRT2 = float4(0, 0, 0, 1);
	target._GBufferRT3 = float4(0, 0, 0, 1);
	// target._GBufferRT3 = float4(1, 1, 1, ao);
}


#endif