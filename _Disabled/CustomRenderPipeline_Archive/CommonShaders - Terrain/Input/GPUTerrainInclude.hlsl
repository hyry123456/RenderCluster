#ifndef GPU_TERRAIN_INCLUDE
#define GPU_TERRAIN_INCLUDE

#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"



static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16, 32};

float4 _TerrainSize;
float3 _BeginPosition;

TEXTURE2D(_HeighMap);
TEXTURE2D(_NormalMap);

// struct LayerData{
//     float4 textureST;     //xy:scale, zw:offset
//     float4 data;            //x:normal Scale, y:metallic, z:roughness
// };

// float3 SampleNormal_LOD(float2 uv){
//     float3 normal;
//     normal.xz = SAMPLE_TEXTURE2D_LOD(_NormalMap, sampler_linear_clamp, uv, 0).xy * 2 - 1;
//     normal.y = sqrt(max(0,1 - dot(normal.xz,normal.xz)));
//     normal = normalize(normal);
//     // normal = TransformObjectToWorldNormal(normal);
//     return normal;
// }

//采样法线
float3 SampleNormal(float2 uv){
    float3 normal;
    normal.xyz = SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_repeat, uv).xyz * 2 - 1;
    // normal = SafeNormalize(normal);
    // half3 tangentWS = cross(GetObjectToWorldMatrix()._13_23_33, normalWS);
    // normalWS = TransformTangentToWorld(normalTS, half3x3(-tangentWS, cross(normalWS, tangentWS), normalWS));

    // normal = TransformObjectToWorldNormal(normal);
    return normal;
}

struct VertexNormalInputs
{
    real3 tangentWS;
    real3 bitangentWS;
    float3 normalWS;
};

VertexNormalInputs GetVertexNormalInputs(float3 normalOS)
{
    VertexNormalInputs tbn;
    tbn.tangentWS = real3(1.0, 0.0, 0.0);
    tbn.bitangentWS = real3(0.0, 1.0, 0.0);
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    return tbn;
}
// struct VertexNormalInputs
// {
//     real3 tangentWS;
//     real3 bitangentWS;
//     float3 normalWS;
// };

// VertexNormalInputs GetVertexNormalInputs(float3 normalOS)
// {
//     VertexNormalInputs tbn;
//     tbn.tangentWS = real3(1.0, 0.0, 0.0);
//     tbn.bitangentWS = real3(0.0, 1.0, 0.0);
//     tbn.normalWS = TransformObjectToWorldNormal(normalOS);
//     return tbn;
// }
VertexNormalInputs GetVertexNormalInputs(float3 normalOS, float4 tangentOS)
{
    VertexNormalInputs tbn;

    // mikkts space compliant. only normalize when extracting normal at frag.
    real sign = tangentOS.w * GetOddNegativeScale();
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    tbn.tangentWS = TransformObjectToWorldDir(tangentOS.xyz);
    tbn.bitangentWS = cross(tbn.normalWS, tbn.tangentWS) * sign;
    return tbn;
}


//float3 BlendTriplanarNormal(float3 mappedNormal, float3 surfaceNormal){
//    float3 n;
//    n.xy = mappedNormal.xy + surfaceNormal.xy;
//    n.z = mappedNormal.z * surfaceNormal.z;
//    return n;
//}



// void ReadTerrainData(int beginIndex, TriplanarUV triUV, inout TerrainData data, float4 splitCol, float3 normal){
//     int maxIndex = min(beginIndex + 4, _LayerCount);
//     int channel = 0;
//     for(int i = beginIndex; i < maxIndex; i++){
//         float radio = splitCol[channel++];
        
//         LayerData layerData = LayerDataBuffer[i];
//         float2 uvX = triUV.x * layerData.textureST.xy + layerData.textureST.zw;
//         float2 uvY = triUV.y * layerData.textureST.xy + layerData.textureST.zw;
//         float2 uvZ = triUV.z * layerData.textureST.xy + layerData.textureST.zw;

//         data.color += (SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvX, i) * triUV.radio.x
//             + SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvY, i) * triUV.radio.y
//             + SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvZ, i) * triUV.radio.z).xyz * radio;
        
//         float3 normalX = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvX, i), layerData.data.x);
//         float3 normalY = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvY, i), layerData.data.x);
//         float3 normalZ = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvZ, i), layerData.data.x);
        

//         if(normal.x < 0){
//             normalX.x = -normalX.x;
//         }
//         if(normal.y < 0){
//             normalY.x = -normalY.x;
//         }
//         if(normal.z >= 0){
//             normalZ.x = -normalZ.x;
//         }

//         float3 worldNormalX = BlendTriplanarNormal(normalX, normal.zyx).zyx;
//         float3 worldNormalY = BlendTriplanarNormal(normalY, normal.xzy).xzy;
//         float3 worldNormalZ = BlendTriplanarNormal(normalZ, normal);

//         data.normal += (worldNormalX * triUV.radio.x + worldNormalY * triUV.radio.y + worldNormalZ * triUV.radio.z) * radio;

//         data.mask += (SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvX, i) * triUV.radio.x
//             + SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvY, i) * triUV.radio.y
//             + SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvZ, i) * triUV.radio.z) * radio;

//         data.metallic += layerData.data.y * radio;
//         data.roughness += layerData.data.z * radio;
//     }


// }

// TerrainData GetTerrainData(float3 worldPos, float3 normal, float2 worldUV){
//     TriplanarUV triUV = GetTriplanarUV(worldPos, normal);

//     TerrainData data = (TerrainData)0;
//     int splitCount = ceil(_LayerCount / 4.0);
//     for(int i = 0; i < splitCount; i++){
//         float4 splitCol = SAMPLE_TEXTURE2D_ARRAY(_SpiltArray, sampler_linear_repeat, worldUV, i);
//         ReadTerrainData(i * 4, triUV, data, splitCol, normal);
//     }
//     return data;
// }



#endif
