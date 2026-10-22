#ifndef CUSTOMRP_PLANE_INPUT
#define CUSTOMRP_PLANE_INPUT

struct RenderPatch{
    float2 position;
    uint4 lod;      //xyz:nodeLod, w:patch offset
};
float4 _NodeSize[7];       //每一级的x, z的大小，xy=xz的大小
StructuredBuffer<RenderPatch> PatchList;
float _LodMapPixelSize[7];      //LodMap的每一级的像素量
uint _LodLevel;
float4x4 _ObjectToWorld;
TEXTURE2D(_LodMap);

static float NodeCount[] = {0, 4.0, 2.0, 1.0, 0.5, 0.25, 0.125};

float3 FixVertex(float3 vertex, uint4 lod, float2 objUV){
    uint row = lod.w / 8; uint col = lod.w % 8;
    float pixelSize = _LodMapPixelSize[lod.z];
    float2 uv = (float2)lod.xy * pixelSize + pixelSize / 2;      //node center

    if(objUV.x == 0.0 && row == 0){     //左边
        float left = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelSize, 0), 0).x;
        float offset = left * _LodLevel - (float)lod.z;
        int offsetI = floor(offset + 0.1);
        if(offsetI >= 1)
        {
            //将fixRange内的顶点都合并在同一个位置
            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
            float zpos = floor(vertex.z / fixRange);
            vertex.z = zpos * fixRange;
        }
    }
    else if(objUV.x == 1.0 && row == 7){
        float right = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelSize, 0), 0).x;
        float offset = right * _LodLevel - (float)lod.z;
        int offsetI = floor(offset + 0.1);
        if(offsetI >= 1)
        {
            //将fixRange内的顶点都合并在同一个位置
            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
            float zpos = floor(vertex.z / fixRange);
            vertex.z = zpos * fixRange;
        }
    }

    if(objUV.y == 1.0 && col == 7){     //上边
        float up = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelSize), 0).x;
        float offset = up * _LodLevel - (float)lod.z;
        int offsetI = floor(offset + 0.1);
        if(offsetI >= 1)
        {
            //将fixRange内的顶点都合并在同一个位置
            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
            float xpos = floor(vertex.x / fixRange);
            vertex.x = xpos * fixRange;
        }
    }
    else if(objUV.y == 0.0 && col == 0){     //下边
        float down = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelSize), 0).x;
        float offset = down * _LodLevel - (float)lod.z;
        int offsetI = floor(offset + 0.1);
        if(offsetI >= 1)
        {
            //将fixRange内的顶点都合并在同一个位置
            float fixRange = NodeCount[_LodLevel] * pow(2.0, offsetI);
            float xpos = floor(vertex.x / fixRange);
            vertex.x = xpos * fixRange;
        }
    }
    return vertex;
}

float3 BlendTriplanarNormal(float3 mappedNormal, float3 surfaceNormal){
    float3 n;
    n.xy = mappedNormal.xy + surfaceNormal.xy;
    n.z = mappedNormal.z * surfaceNormal.z;
    return n;
}

#endif