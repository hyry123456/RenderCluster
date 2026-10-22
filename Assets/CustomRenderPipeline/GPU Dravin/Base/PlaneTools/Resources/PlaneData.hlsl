#ifndef CUSTOMRP_PLANEDATA
#define CUSTOMRP_PLANEDATA

struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};

uint _CurrentEdgeSize;      //当前边界的顶点值
uint _CurrentLod;            //当前执行到的Lod级别
float4 _NodeSize[5];       //每一级的x, z的大小，xy=xz的大小

float3 _CameraPositionOS;
float _NodeEvaluation;      //细分等级
float3 _BeginPosition;
float3 _Extent;

uniform float4 _CameraFrustumPlanes[6];

static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16};      //LodMap的每一级的像素量
RWTexture2D<float> _LodMap;


ConsumeStructuredBuffer<uint2> ConsumeNodeList;
AppendStructuredBuffer<uint2> AppendNodeList;           //传送到下一层时的目标buffer
AppendStructuredBuffer<uint4> AppendFinalNodeList;      //最终待渲染的buffer, xy:编号, z:Lod Level, w:边界情况

//四叉树遍历完成后，合适的节点存放在这里
StructuredBuffer<uint4> FinalNodeList;

AppendStructuredBuffer<Bounds> BoundsList;    //Bounds写入的目标位置
AppendStructuredBuffer<uint4> CulledPatchList;    //patch写入的目标位置

int _MinMaxHMapMipsOffset;
Texture2D<float4> _MinMaxHeightMap;
#endif