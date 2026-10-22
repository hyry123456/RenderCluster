#ifndef DEFFER_TERRAIN_PASS
#define DEFFER_TERRAIN_PASS

#include "../../../ShaderLibrary/Tesseilation.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"


struct FragInput{
    float4 positionCS : SV_POSITION;
    float3 worldPos : VAR_POSITION;
    float3 normal : NORMAL;
    float4 tangentWS : VAR_TANGENT;
    float2 uv : VAR_UV;
    float2 worldUV : VAR_WORLDUV;
};



[maxvertexcount(3)]
void TerrainGeom(triangle TessOutPutSimple IN[3], inout TriangleStream<FragInput> tristream)
{
    FragInput output[3] = (FragInput[3])0;

    IN[0].vertex.xyz += _BeginPosition;
    IN[1].vertex.xyz += _BeginPosition;
    IN[2].vertex.xyz += _BeginPosition;

    IN[0].vertex.y += SAMPLE_TEXTURE2D_LOD(_HeightTex, sampler_HeightTex, IN[0].uv, 0).r * _Height;
    IN[1].vertex.y += SAMPLE_TEXTURE2D_LOD(_HeightTex, sampler_HeightTex, IN[1].uv, 0).r * _Height;
    IN[2].vertex.y += SAMPLE_TEXTURE2D_LOD(_HeightTex, sampler_HeightTex, IN[2].uv, 0).r * _Height;



    output[0].positionCS = TransformWorldToHClip(IN[0].vertex.xyz);
    output[1].positionCS = TransformWorldToHClip(IN[1].vertex.xyz);
    output[2].positionCS = TransformWorldToHClip(IN[2].vertex.xyz);

    output[0].uv = IN[0].uv;
    output[1].uv = IN[1].uv;
    output[2].uv = IN[2].uv;

    output[0].worldPos = IN[0].vertex.xyz;
    output[1].worldPos = IN[1].vertex.xyz;
    output[2].worldPos = IN[2].vertex.xyz;

    float3 po0To1 = IN[1].vertex.xyz - IN[0].vertex.xyz;
    float3 po1To2 = IN[2].vertex.xyz - IN[1].vertex.xyz;
    float3 po2To0 = IN[0].vertex.xyz - IN[2].vertex.xyz;

    float3 normal0 = normalize(cross(po0To1, -po2To0));
    float3 normal = normal0;

    float3 halfNormal = normal * 0.5 + 0.5;
    float3 radio = halfNormal / (halfNormal.x + halfNormal.y + halfNormal.z);
    output[0].worldUV = IN[0].vertex.xz;

    output[1].worldUV = IN[1].vertex.xz;

    output[2].worldUV = IN[2].vertex.xz;

    float4 tangentWS = float4(
			TransformObjectToWorldDir(float3(1, 0, 0)) , 1
		);

    output[0].normal = normal;
    output[1].normal = normal;
    output[2].normal = normal;

    output[0].tangentWS = tangentWS;
    output[1].tangentWS = tangentWS;
    output[2].tangentWS = tangentWS;

    tristream.Append(output[0]);
    tristream.Append(output[1]);
    tristream.Append(output[2]);
    tristream.RestartStrip();

}

float2 TransferUV(float2 uv){
    return uv * _Texture_ST.xy + _Texture_ST.zw;
}

float4 TerrainFragment(FragInput input) : SV_TARGET
{
    TriplanarUV triUV = GetWorldUV(input.worldPos, input.normal);
    // worldUV = _Texture_ST.xy * worldUV + _Texture_ST.zw;
    triUV.x = TransferUV(triUV.x); triUV.y = TransferUV(triUV.y); triUV.z = TransferUV(triUV.z);

    float split = SAMPLE_TEXTURE2D(_SplitTex, sampler_linear_clamp, input.uv)[_Channel];
    float3 albedo = SAMPLE_TEXTURE2D(_AlbedoTex, sampler_linear_repeat, triUV.x).xyz * triUV.radio.x
        + SAMPLE_TEXTURE2D(_AlbedoTex, sampler_linear_repeat, triUV.y).xyz * triUV.radio.y
        + SAMPLE_TEXTURE2D(_AlbedoTex, sampler_linear_repeat, triUV.z).xyz * triUV.radio.z;

    Surface surface = (Surface)0;
    surface.position = input.worldPos;
    // surface.normal = input.normal;
    surface.normal = NormalTangentToWorld(
        GetNormalTS(triUV), input.normal, input.tangentWS
    );

    surface.viewDirection = normalize(_WorldSpaceCameraPos - input.worldPos);
    surface.depth = -TransformWorldToView(input.worldPos).z;
    surface.color = albedo;
    surface.alpha = 1;
    surface.metallic = _PBR_Data.x;
    surface.roughness = _PBR_Data.y;
    surface.ambientOcclusion = 1;
    surface.dither = InterleavedGradientNoise(input.positionCS.xy, 0);

    float3 color = GetGBufferLight(surface, input.positionCS);

    float3 reflect_dir = reflect(-surface.viewDirection, surface.normal);
    float mip_Level = surface.metallic * (1.7 - 0.7 * surface.metallic);
    float3 refl = ComputeIndirectSpecular(reflect_dir, input.worldPos, mip_Level);
    float oneMinusReflectivity = OneMinusReflectivity(surface.metallic);

    float3 specColor = lerp(0.219, surface.color, surface.metallic);

    half grazingTerm = saturate((1 - surface.roughness) + (1 - oneMinusReflectivity));

    refl *= ComputeFresnelLerp(specColor, grazingTerm, saturate(dot(surface.normal, -surface.viewDirection)));

	color.xyz += refl * surface.color * surface.ambientOcclusion;

    float2 worldUV = input.worldPos.xz;
    float2 noiseValue = SAMPLE_TEXTURE2D_LOD(_NoiseTex, sampler_linear_repeat, worldUV, 0) - 0.5;
    return float4(color, split);
}


#endif