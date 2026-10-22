#ifndef CUSTOMRP_PLANE_INPUT
#define CUSTOMRP_PLANE_INPUT

struct RenderPatch{
    float2 position;
    uint4 lod;      //xyz:nodeLod, w:patch offset
};

struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};


float4 _NodeSize[5];       //每一级的x, z的大小，xy=xz的大小
StructuredBuffer<RenderPatch> PatchList;
StructuredBuffer<Bounds> BoundsList;
float _LodMapPixelSize[7];      //LodMap的每一级的像素量
uint _LodLevel;
float4x4 _ObjectToWorld;
TEXTURE2D(_LodMap);

float4 _BeginPosition;
float3 _WorldSize;



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

// float3 BlendTriplanarNormal(float3 mappedNormal, float3 surfaceNormal){
//     float3 n;
//     // n.xy = mappedNormal.xy + surfaceNormal.xy;
//     // n.z = abs(mappedNormal.z) * surfaceNormal.z;
//     // // return surfaceNormal;

//     return n;
// }

float3 GetWorldNormal(half3 mappedNormal, half3 surfaceNormal){
    half3 tangentWS = cross(_ObjectToWorld._13_23_33, surfaceNormal);
    return TransformTangentToWorld(mappedNormal, half3x3(-mappedNormal, cross(surfaceNormal, tangentWS), surfaceNormal));
}



StructuredBuffer<uint4> _Nodes;
static float _LodMapPixelSizes[] = {0.0125, 0.025, 0.05, 0.1, 0.2};      //LodMap的每一级的像素量
float _PointCount;

//接面
void CaculatePositionOS(inout float3 vertex, uint4 lod, half4 color){
    uint mode = color.r * 255;
    uint near = lod.z;
    int offset = 0;      //隔壁的级别
    // uint row = lod.w / 8; uint col = lod.w % 8;
    float pixelSize = _LodMapPixelSizes[lod.z];
    float2 uv = (float2)lod.xy * pixelSize + pixelSize / 2;      //node center

    float pixelVal; 
    switch(mode){
        case 1:
            pixelVal = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, -pixelSize), 0).x;
            near = pixelVal * 4; offset = near - lod.z;
            // return near / 4.0f;
            if(offset > 0){
                //将fixRange内的顶点都合并在同一个位置
                float fixRange = pow(2.0, offset);
                vertex.x = floor(vertex.x / fixRange) * fixRange;
            }
            break;
        case 2:
            pixelVal = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(pixelSize, 0), 0).x;
            near = ceil(pixelVal * 4); offset = near - lod.z;
            if(offset > 0){
                //将fixRange内的顶点都合并在同一个位置
                float fixRange = pow(2.0, offset);
                vertex.z = floor(vertex.z / fixRange) * fixRange;

            }
            break;
        case 3:
            pixelVal = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(0, pixelSize), 0).x;
            near = pixelVal * 4; offset = near - lod.z;
            if(offset > 0){
                //将fixRange内的顶点都合并在同一个位置
                float fixRange = pow(2.0, offset);
                vertex.x = floor(vertex.x / fixRange) * fixRange;
            }
            break;
        case 4:
            pixelVal = SAMPLE_TEXTURE2D_LOD(_LodMap, sampler_point_clamp, uv + float2(-pixelSize, 0), 0).x;
            near = pixelVal * 4; offset = near - lod.z;
            if(offset > 0){
                //将fixRange内的顶点都合并在同一个位置
                float fixRange = pow(2.0, offset);
                vertex.z = floor(vertex.z / fixRange) * fixRange;
            }
            break;
        default:
            break;
    }
}


#endif