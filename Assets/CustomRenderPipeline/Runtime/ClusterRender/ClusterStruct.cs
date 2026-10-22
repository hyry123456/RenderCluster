using Common;
using UnityEditor;
using UnityEngine;

namespace CustomRenderPipline.Custer
{
    /// <summary>    /// 簇需要的顶点数据格式    /// </summary>
    /// <remarks>
    /// 必须为 4×Vector4（64 字节）：StructuredBuffer 中 float3 会被 HLSL 按 16 字节对齐，
    /// 若用 Vector3（12 字节）则 C# Marshal.SizeOf=56 与 HLSL stride=64 错位，GPU 读 _PointsBuffer
    /// 会整体偏移（旧 mesh-cluster 代码“很多问题”之一）。改用 float4 后 C#/HLSL 均为 64，严格对齐。
    /// </remarks>
    public struct ClusterPointsData
    {
        public Vector4 pos;       // xyz = 位置, w 未用
        public Vector4 normal;    // xyz = 法线, w 未用
        public Vector4 tangle;    // xyz = tangent, w = handedness
        public Vector4 uv01;      // xy = uv0(主), zw = uv1(灯光 uv)
    }

    /// <summary>    /// 簇指针数据    /// </summary>
    public struct ClustersIndicate
    {
        /// <summary>
        /// 簇指针位置，不是起始位置，存储的值需要根据对应原物体的簇编号进行偏移
        /// </summary>
        public int triangleIndex;
        /// <summary>
        /// 顶点起始位置，得到的指针用这个值进行偏移
        /// </summary>
        public int pointIndex;
        /// <summary>        /// 渲染数据的结点位置        /// </summary>
        public int renderIndex;

        public int isUsing;
        /// <summary>        /// 这个簇的模型空间中心        /// </summary>
        public Vector3 center;  public float radius;
        /// <summary>        /// 世界空间的最大最小值        /// </summary>
        public Vector3 worldCenter; public float worldRadius;

        /// <summary>
        /// 簇级虚拟贴图页表索引（Step2 P1 新增）。
        /// 取代旧的材质级 _MapId：寻址单元从「材质」变成「簇」本身，使簇级虚拟贴图成为可能。
        /// P1 阶段它与同材质的 _MapId 取值相同（同材质的所有簇共用一张贴图页表槽位）；
        /// 真正的「每簇各自独立的虚拟贴图区域」在后续阶段通过 cluster-keyed 页表实现。
        /// 注意：字段必须放在结构体末尾，且 HLSL 侧 ClustersIndicate 需同步追加，
        /// 否则 ComputeBuffer 上传的字节布局会错位。
        /// </summary>
        public int vtNodeIndex;
    }
    
    public struct MeshRenderData
    {
        /// <summary>        /// 模型空间到世界空间矩阵        /// </summary>
        public Matrix4x4 objectToWorld;

        public Vector4 emissionCol, mainColor;
        public Vector4 pbrData;     //x:metallic,y:smooth,z:normalScale,w:cullOff
        public Vector4 uvOffsetScale;

        public Vector4 detailUVOffsetScale;
        public Vector4 detailData;      //x:detail,y:normalScale,z:smoothn,w:ssr mask

        public Vector4 lightMapScaleOffset;

        //更新范围计算方式，因为采用分块，此时块很小，使用球剔除效率是最高的
        //    因此不需要这些内容，只需要空间位置，以及球的半径即可
        //    不过模型空间和世界空间都要存储，方便应用于不同的矩阵，半径的缩放就用scale的最大值即可
        /// <summary>        /// 渲染遮罩        /// </summary>
        public uint4 mask;
        /// <summary>        /// 使用的贴图编号        /// </summary>
        public int textureId;
    }

    /// <summary>    /// 贴图的结构体节点    /// </summary>
    public struct RVTTransferNode
    {
        public int width;
        public int height;
        public int mipsLevel;
    }

    //public class TextureDataNode
    //{
    //    public CacheNode cacheNode;
    //}

    /// <summary>
    /// 簇组节点，用来确定这个簇块，分配的了多少数据
    /// </summary>
    public class TrianglesNode
    {
        /// <summary>     /// 簇组的起始编号       /// </summary>
        public int triangleIndex;
        /// <summary>     /// 簇组的大小       /// </summary>
        public int triangleSize;
    }


    /// <summary>
    /// 顶点数组节点，用来确定这个数组的数据范围
    /// </summary>
    public class PointsNode
    {
        /// <summary>     /// 顶点数组的起始编号       /// </summary>
        public int pointIndex;
        /// <summary>     /// 顶点数组的大小       /// </summary>
        public int pointsSize;

        /// <summary>        /// 对应的簇名称        /// </summary>
        public string clusterName;
        /// <summary>        /// 使用该Cluster的数量        /// </summary>
        public int useCount;

        ///// <summary>        /// 属于该点的指针序列数据        /// </summary>
        //public TrianglesNode trianglesNode;
        /// <summary>        /// 每一个簇的最大最小范围数组，存储值所有最大最小值        /// </summary>
        public Pair<Vector3, Vector3>[] minMaxs;
    }

    /// <summary>    /// 簇数据，存储着该簇数据，以及顶点和指针数据    /// </summary>
    public class ClustersNode
    {
        public int clusterIndex;
        public int clusterSize;
        public int textureIndex;
        public int renderDataIndex;
        public PointsNode pointsNode;
        public TrianglesNode trianglesNode;
    }

    /// <summary>    /// 渲染PBR时的贴图结点，存储贴图和使用情况    /// </summary>
    public class TexturesNode
    {
        public Texture2D albedo;
        public Texture2D normal;
        public Texture2D pbrTex;
        public Texture2D emison;
        public Texture2D detial;
        public Texture2D litMap;
        public int useCount;
    }

}