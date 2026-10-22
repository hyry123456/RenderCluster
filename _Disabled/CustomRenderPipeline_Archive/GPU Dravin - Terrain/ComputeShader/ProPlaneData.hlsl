#ifndef CUSTOMRP_TERRAIN_PLANEDATA
#define CUSTOMRP_TERRAIN_PLANEDATA

struct NodeDescriptor{
    uint branch;
};
struct RenderPatch{
    float2 position;
    uint4 lod;      //xyz:nodeLod, w:patch offset
};
struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};


float3 _CameraPositionWS;   //摄像机位置
float3 _WorldSize;          //这个Plane的世界大小
float3 _BoundExtent;       //盒子拓展，用来缩放实际的裁剪大小

// int _NodeCounts[5];        //每一级中每一行的节点数量，最多为11级，所以大小为11
float4 _NodeSize[7];       //每一级的x, z的大小，xy=xz的大小

float _NodeEvaluation;      //评估值

int _CurrentLod;            //当前执行到的Lod级别

//float4x4 _ObjectToWorld;    //这个物体的投影矩阵

//the Object WorldSpace Begin Position, plane renderObj don't need scale and rotate,
//just World Begin Position can caculate final world position
float4 _BeginPosition;

uniform float4 _CameraFrustumPlanes[6];

ConsumeStructuredBuffer<uint2> ConsumeNodeList;
AppendStructuredBuffer<uint2> AppendNodeList;           //传送到下一层时的目标buffer
AppendStructuredBuffer<uint3> AppendFinalNodeList;      //最终待渲染的buffer

//四叉树遍历完成后，合适的节点存放在这里
StructuredBuffer<uint3> FinalNodeList;

AppendStructuredBuffer<RenderPatch> CulledPatchList;    //patch写入的目标位置
AppendStructuredBuffer<Bounds> BoundsList;    //Bounds写入的目标位置
RWTexture2D<float> _LodMap;


static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16, 32, 64};      //LodMap的每一级的像素量
#define PATCH_COUNT_PER_NODE 8


#endif