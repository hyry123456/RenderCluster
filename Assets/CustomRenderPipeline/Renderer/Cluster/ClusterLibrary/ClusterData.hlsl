#ifndef CLUSTER_DATA
#define CLUSTER_DATA


/// <summary>    /// 簇需要的顶点数据格式    /// </summary>
/// 必须为 4×float4（64 字节）：StructuredBuffer 中 float3 按 16 字节对齐，
/// 用 float3 会导致 C#(56) 与 HLSL(64) stride 错位；float4×4 两侧均为 64，严格对齐。
struct ClusterPointsData
{
    float4 pos;       // xyz = 位置
    float4 normal;    // xyz = 法线
    float4 tangle;    // xyz = tangent, w = handedness
    float4 uv01;      // xy = uv0(主), zw = uv1(灯光 uv)
};

/// <summary>    /// 簇指针数据    /// </summary>
struct ClustersIndicate
{
    // /// <summary>        /// 模型空间到世界空间矩阵        /// </summary>
    // float4x4 objectToWorld;
    /// <summary>
    /// 簇指针位置，不是起始位置，存储的值需要根据对应原物体的簇编号进行偏移
    /// </summary>
    int triangleIndex;
    /// <summary>
    /// 顶点起始位置，得到的指针用这个值进行偏移
    /// </summary>
    int pointIndex;

    /// <summary>        /// 渲染数据的结点位置        /// </summary>
    int renderIndex;

    int isUsing;
    /// <summary>        /// 这个簇的模型空间中心        /// </summary>
    float3 center;  float radius;
    /// <summary>        /// 世界空间的最大最小值        /// </summary>
    float3 worldCenter; float worldRadius;

    /// <summary>
    /// 簇级虚拟贴图页表索引（Step2 P1 新增，须与 C# 侧 ClustersIndicate.vtNodeIndex 同步）。
    /// 寻址单元是「簇」本身，取代旧的材质级 _MapId。
    /// </summary>
    int vtNodeIndex;

    // /// <summary>        /// 渲染遮罩        /// </summary>
    // int renderMask;
    // /// <summary>        /// 使用的贴图编号        /// </summary>
    // int textureId;

    // float4 emissionCol, mainColor;
    // float4 pbrData;     //x:metallic,y:smooth,z:normalScale,w:cullOff
    // float4 uvOffsetScale;

    // float4 detailUVOffsetScale;
    // float3 detailData;      //x:detail,y:normalScale,z:smoothn
};

struct MeshRenderData
{
    /// <summary>        /// 模型空间到世界空间矩阵        /// </summary>
    float4x4 objectToWorld;

    float4 emissionCol, mainColor;
    float4 pbrData;     //x:metallic,y:smooth,z:normalScale,w:cullOff
    float4 uvOffsetScale;

    float4 detailUVOffsetScale;
    float4 detailData;      //x:detail,y:normalScale,z:smoothn,w:ssr mask

    float4 lightMapScaleOffset;

    //更新范围计算方式，因为采用分块，此时块很小，使用球剔除效率是最高的
    //    因此不需要这些内容，只需要空间位置，以及球的半径即可
    //    不过模型空间和世界空间都要存储，方便应用于不同的矩阵，半径的缩放就用scale的最大值即可
    /// <summary>        /// 渲染遮罩        /// </summary>
    uint4 mask;
    /// <summary>        /// 使用的贴图编号        /// </summary>
    int textureId;
};

//顶点数据
StructuredBuffer<ClusterPointsData> _PointsBuffer;
//指针数据
StructuredBuffer<int> _TrianglesBuffer;
//簇映射数据
StructuredBuffer<ClustersIndicate> _ClustersBuffer;
StructuredBuffer<MeshRenderData>    _RenderDatasBuffer;

StructuredBuffer<int> _AppendBuffer;


//x：最高级的每一行的图片数量, y : 总图片数量, z:padding占贴图的比例
float4 _RVTTextureData;

// sampler2D _RVTAlbedo0, _RVTAlbedo1, _RVTAlbedo2, _RVTAlbedo3, _RVTAlbedo4, _RVTAlbedo5, _RVTAlbedo6, _RVTAlbedo7;
TEXTURE2D(_RVTAlbedo0); TEXTURE2D(_RVTAlbedo1); TEXTURE2D(_RVTAlbedo2); TEXTURE2D(_RVTAlbedo3);
TEXTURE2D(_RVTAlbedo4); TEXTURE2D(_RVTAlbedo5); TEXTURE2D(_RVTAlbedo6); TEXTURE2D(_RVTAlbedo7);
TEXTURE2D(_RVTAlbedo8); TEXTURE2D(_RVTAlbedo9);

TEXTURE2D(_RVTNormal0); TEXTURE2D(_RVTNormal1); TEXTURE2D(_RVTNormal2); TEXTURE2D(_RVTNormal3);
TEXTURE2D(_RVTNormal4); TEXTURE2D(_RVTNormal5); TEXTURE2D(_RVTNormal6); TEXTURE2D(_RVTNormal7);
TEXTURE2D(_RVTNormal8); TEXTURE2D(_RVTNormal9);

TEXTURE2D(_RVTPbrData0); TEXTURE2D(_RVTPbrData1); TEXTURE2D(_RVTPbrData2); TEXTURE2D(_RVTPbrData3);
TEXTURE2D(_RVTPbrData4); TEXTURE2D(_RVTPbrData5); TEXTURE2D(_RVTPbrData6); TEXTURE2D(_RVTPbrData7); 
TEXTURE2D(_RVTPbrData8); TEXTURE2D(_RVTPbrData9); 

TEXTURE2D(_RVTEmission0); TEXTURE2D(_RVTEmission1); TEXTURE2D(_RVTEmission2); TEXTURE2D(_RVTEmission3);
TEXTURE2D(_RVTEmission4); TEXTURE2D(_RVTEmission5); TEXTURE2D(_RVTEmission6); TEXTURE2D(_RVTEmission7); 
TEXTURE2D(_RVTEmission8); TEXTURE2D(_RVTEmission9); 

//R:Detail map albedo，G:Detail normal y, B: Detail smoothness, A:Detail Normal Map x
TEXTURE2D(_RVTDetail0); TEXTURE2D(_RVTDetail1); TEXTURE2D(_RVTDetail2); TEXTURE2D(_RVTDetail3);
TEXTURE2D(_RVTDetail4); TEXTURE2D(_RVTDetail5); TEXTURE2D(_RVTDetail6); TEXTURE2D(_RVTDetail7); 
TEXTURE2D(_RVTDetail8); TEXTURE2D(_RVTDetail9); 

TEXTURE2D(_RVTLightMap0); TEXTURE2D(_RVTLightMap1); TEXTURE2D(_RVTLightMap2); TEXTURE2D(_RVTLightMap3);
TEXTURE2D(_RVTLightMap4); TEXTURE2D(_RVTLightMap5); TEXTURE2D(_RVTLightMap6); TEXTURE2D(_RVTLightMap7); 
TEXTURE2D(_RVTLightMap8); TEXTURE2D(_RVTLightMap9); 

TEXTURECUBE(_BaseCubeMap);
SAMPLER(sampler_BaseCubeMap);

// sampler2D _RVTNormal0, _RVTNormal1, _RVTNormal2, _RVTNormal3, _RVTNormal4, _RVTNormal5, _RVTNormal6, _RVTNormal7;
//r：Metallic，g：Occlusion，b：Detail Mask，a：Smoothness
// sampler2D _RVTPbrData0, _RVTPbrData1, _RVTPbrData2, _RVTPbrData3, _RVTPbrData4, _RVTPbrData5, _RVTPbrData6, _RVTPbrData7;
// sampler2D _RVTEmission0, _RVTEmission1, _RVTEmission2, _RVTEmission3, _RVTEmission4, _RVTEmission5, _RVTEmission6, _RVTEmission7;

float _PaddingSizes[8];

#endif