#ifndef GPU_TERRAIN_INCLUDE
#define GPU_TERRAIN_INCLUDE

#include "../../../ShaderLibrary/Surface.hlsl"
#include "../../../ShaderLibrary/Shadows.hlsl"
#include "../../../ShaderLibrary/Light.hlsl"
#include "../../../ShaderLibrary/BRDF.hlsl"
#include "../../../ShaderLibrary/GI.hlsl"
#include "../../../ShaderLibrary/Lighting.hlsl"


struct RenderPatch {
    float2 position;
    uint4 lod;
};

StructuredBuffer<RenderPatch> PatchList;
float4 _NodeSize[6];     //node size of each level
float4 _TerrainSize;      //Terrain Size, x:width,y:height,z:length
// float3 _WorldPosition;
float4x4 _ObjectToWorld;
// float4 _ObjectToWorld;


//不同lod等级的node数量,最大的160就是图片大小
static uint _NodeCount[] = {160, 80, 40, 20, 10, 5};

static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16, 32};

int _LayerCount;

TEXTURE2D_ARRAY(_SpiltArray);         //所有的比例纹理
TEXTURE2D_ARRAY(_AlbedoArray);         //所有的比例纹理
TEXTURE2D_ARRAY(_NormalArray);         //所有的比例纹理
TEXTURE2D_ARRAY(_MaskArray);         //所有的比例纹理

TEXTURE2D(_HeighMap);
TEXTURE2D(_NormalMap);
TEXTURE2D(_LodMap);

struct LayerData{
    float4 textureST;     //xy:scale, zw:offset
    float4 data;            //x:normal Scale, y:metallic, z:roughness
};

StructuredBuffer<LayerData> LayerDataBuffer;

struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};

StructuredBuffer<Bounds> BoundsBuffer;

//拼接顶点
float3 FixBeginPlane(float3 objPos, uint4 lod, float2 objUV){
    uint row = lod.w / 8; uint col = lod.w % 8;
    uint2 begIndex = lod.xy * _LodMapLevelSize[lod.z];
    float2 uv = (float2)lod.xy / (float2)_NodeCount[lod.z]  //begin uv
        + 1.0 / _NodeCount[lod.z] / 2;      //node center
    int lodLevel = lod.z;
    float pixelOffset = 1.0 / (_NodeCount[lod.z] * 2) + 0.005;

    uint2 objLevel = objPos.xz * 2;

    if(objUV.x < 0.05 && row == 0){     //左边
        int leftLod = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelOffset, 0), 0).x * 5;
        int offset = leftLod - lod.z;
        if(offset > 0){
            int mod = pow(2, offset);
            int index = objLevel.y / mod;   //第几块
            objLevel.y = index * mod;
        }
    }

    else if(objUV.x > 0.95 && row == 7){     //右边
        int righLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelOffset, 0), 0).x * 5);
        int offset = righLod - lod.z;
        if(offset > 0){
            int mod = pow(2, offset);
            int index = objLevel.y / mod;   //第几块
            objLevel.y = index * mod;
        }
    }

    if(objUV.y > 0.95 && col == 7){     //上边
        int upLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelOffset), 0).x * 5);
        int offset = upLod - lod.z;
        if(offset > 0){
            int mod = pow(2, offset);
            int index = objLevel.x / mod;   //第几块
            objLevel.x = index * mod;
        }
    }
    else if(objUV.y < 0.05 && col == 0){     //下边
        int downLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelOffset), 0).x * 5);
        int offset = downLod - lod.z;
        if(offset > 0){
            int mod = pow(2, offset);
            int index = objLevel.x / mod;   //第几块
            objLevel.x = index * mod;
        }
    }
    return float3(objLevel.x / 2.0, 0, objLevel.y / 2.0);
}

float IsFix(float3 objPos, uint4 lod, float2 objUV){
    uint row = lod.w / 8; uint col = lod.w % 8;
    uint2 begIndex = lod.xy * _LodMapLevelSize[lod.z];
    float2 uv = (float2)lod.xy / (float2)_NodeCount[lod.z]  //begin uv
        + 1.0 / _NodeCount[lod.z] / 2;      //node center
    int lodLevel = lod.z;
    float pixelOffset = 1.0 / (_NodeCount[lod.z] * 2) + 0.005;

    uint2 objLevel = objPos.xz * 2;

    uint count = 0;

    if(objUV.x < 0.05 || objUV.x > 0.95 || objUV.y < 0.05 || objUV.y > 0.95)
        return 1;


    if(objUV.x < 0.05 && row == 0){     //左边
        int leftLod = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelOffset, 0), 0).x * 5;
        int offset = leftLod - lod.z - 1;
        return 1;
        if(offset > 0){
            count++;
        }
    }

    else if(objUV.x > 0.95 && row == 7){     //右边
        int righLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelOffset, 0), 0).x * 5);
        int offset = righLod - lod.z - 1;
        return 1;
        if(offset > 0){
            count++;
        }
    }

    if(objUV.y > 0.95 && col == 7){     //上边
        int upLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelOffset), 0).x * 5);
        int offset = upLod - lod.z - 1;
        return 1;
        if(offset > 0){
            count++;
            
        }
    }
    else if(objUV.y < 0.05 && col == 0){     //下边
        int downLod = ceil(SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelOffset), 0).x * 5);
        int offset = downLod - lod.z - 1;
        return 1;
        if(offset > 0){
            count++;
            
        }
    }
    return count;
}

//采样法线
float3 SampleNormal_LOD(float2 uv){
    float3 normal;
    normal.xz = SAMPLE_TEXTURE2D_LOD(_NormalMap, sampler_linear_clamp, uv, 0).xy * 2 - 1;
    normal.y = sqrt(max(0,1 - dot(normal.xz,normal.xz)));
    normal = normalize(normal);
    // normal = TransformObjectToWorldNormal(normal);
    return normal;
}

//采样法线
float3 SampleNormal(float2 uv){
    float3 normal;
    normal.xz = SAMPLE_TEXTURE2D(_NormalMap, sampler_linear_clamp, uv).xy * 2 - 1;
    normal.y = sqrt(max(0,1 - dot(normal.xz,normal.xz)));
    normal = normalize(normal);
    // normal = TransformObjectToWorldNormal(normal);
    return normal;
}

#ifdef _DEBUG
    void AdjustObjPos(inout float3 objPos, uint4 nodeLoc, float2 offsetSize){
        //先偏移初始位置
        objPos.xz += float2(nodeLoc.x * offsetSize.x, nodeLoc.y * offsetSize.y);
    }
#endif

struct TerrainData{
    float3 color;
    float metallic;
    float roughness;
    float3 normal;
    float4 mask;
};

struct TriplanarUV{
    float2 x, y, z;
    float3 radio;
};

TriplanarUV GetTriplanarUV(float3 worldPos, float3 normal){
    TriplanarUV re;
    re.x = worldPos.zy;
    re.y = worldPos.xz;
    re.z = worldPos.xy;

    float3 triW = abs(normal);
    float3 radio = triW / (triW.x + triW.y + triW.z);

    re.radio = radio;
    return re;
}

float3 BlendTriplanarNormal(float3 mappedNormal, float3 surfaceNormal){
    float3 n;
    n.xy = mappedNormal.xy + surfaceNormal.xy;
    n.z = mappedNormal.z * surfaceNormal.z;
    return n;
}

void ReadTerrainData(int beginIndex, TriplanarUV triUV, inout TerrainData data, float4 splitCol, float3 normal){
    int maxIndex = min(beginIndex + 4, _LayerCount);
    int channel = 0;
    for(int i = beginIndex; i < maxIndex; i++){
        float radio = splitCol[channel++];
        
        LayerData layerData = LayerDataBuffer[i];
        float2 uvX = triUV.x * layerData.textureST.xy + layerData.textureST.zw;
        float2 uvY = triUV.y * layerData.textureST.xy + layerData.textureST.zw;
        float2 uvZ = triUV.z * layerData.textureST.xy + layerData.textureST.zw;

        data.color += (SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvX, i) * triUV.radio.x
            + SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvY, i) * triUV.radio.y
            + SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_linear_repeat, uvZ, i) * triUV.radio.z).xyz * radio;
        
        float3 normalX = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvX, i), layerData.data.x);
        float3 normalY = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvY, i), layerData.data.x);
        float3 normalZ = DecodeNormal(SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_linear_repeat, uvZ, i), layerData.data.x);
        
        if(normal.x < 0){
            normalX.x = -normalX.x;
        }
        if(normal.y < 0){
            normalY.x = -normalY.x;
        }
        if(normal.z >= 0){
            normalZ.x = -normalZ.x;
        }

        float3 worldNormalX = BlendTriplanarNormal(normalX, normal.zyx).zyx;
        float3 worldNormalY = BlendTriplanarNormal(normalY, normal.xzy).xzy;
        float3 worldNormalZ = BlendTriplanarNormal(normalZ, normal);

        data.normal += (worldNormalX * triUV.radio.x + worldNormalY * triUV.radio.y + worldNormalZ * triUV.radio.z) * radio;

        data.mask += (SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvX, i) * triUV.radio.x
            + SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvY, i) * triUV.radio.y
            + SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_linear_repeat, uvZ, i) * triUV.radio.z) * radio;

        data.metallic += layerData.data.y * radio;
        data.roughness += layerData.data.z * radio;
    }


}

TerrainData GetTerrainData(float3 worldPos, float3 normal, float2 worldUV){
    TriplanarUV triUV = GetTriplanarUV(worldPos, normal);

    TerrainData data = (TerrainData)0;
    int splitCount = ceil(_LayerCount / 4.0);
    for(int i = 0; i < splitCount; i++){
        float4 splitCol = SAMPLE_TEXTURE2D_ARRAY(_SpiltArray, sampler_linear_repeat, worldUV, i);

        ReadTerrainData(i * 4, triUV, data, splitCol, normal);
    }
    return data;
}



#endif
