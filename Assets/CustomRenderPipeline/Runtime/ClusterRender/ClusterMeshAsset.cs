using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// 一个 mesh 烘焙出的「簇 + LOD 链」数据（【真正的 cluster 管线】的资产载体）。
    ///
    /// 布局（全部按簇对齐，方便 GPU 端用 SV_InstanceID 一次间接绘制）:
    ///   positions     —— 全部顶点位置（按 submesh 分开烘，簇不跨材质）
    ///   normals/tangents/uvs —— 与 positions 对齐的顶点属性（GBuffer 需要，阴影不读）
    ///   indices       —— 按簇分块存储，每簇固定 clusterTriCount*3 个条目（不足的补退化三角形）
    ///   clusterMeta   —— 每簇 x=顶点起始 y=索引起始 z=LOD 级别 w=三角形数(含填充)
    ///   clusterBounds —— 每簇包围球 (xyz=中心, w=半径)，局部空间
    ///   clusterSubmesh—— 每簇归属的 submesh（决定用哪套材质 / _MapId）
    /// </summary>
    public class ClusterMeshAsset : ScriptableObject
    {
        public Mesh sourceMesh;

        /// <summary>每簇的目标三角形数（对齐到这个数，GPU 端才能一次画 384 个顶点）。</summary>
        public int clusterTriCount = 128;

        /// <summary>LOD 级别数（0 = 原始，越大越粗）。</summary>
        public int lodCount = 1;

        /// <summary>
        /// 是否按 submesh 分开切簇。
        ///   true  —— GBuffer 用：一个簇只能属于一个纹理集（一套 `_MapId`/材质），否则没法按材质画。
        ///   false —— 阴影用：阴影是纯几何、不采材质，合并切簇可以避免「每个 submesh 的最后一簇
        ///            都要补齐到 128 三角形」造成的填充簇，簇数明显更少（实测可省 20~40%）。
        /// </summary>
        public bool splitBySubmesh = true;

        public Vector3[] positions;

        /// <summary>与 positions 一一对应的顶点属性（只有 GBuffer 路径读，阴影路径不用）。</summary>
        public Vector3[] normals;
        public Vector4[] tangents;
        public Vector2[] uvs;

        public int[] indices;

        /// <summary>每簇 (vertexStart, indexStart, lodLevel, triCount)</summary>
        public Vector4[] clusterMeta;

        /// <summary>每簇包围球 (cx, cy, cz, r)，局部空间。</summary>
        public Vector4[] clusterBounds;

        /// <summary>每簇属于哪个 submesh。GBuffer 路径靠它拿到该 submesh 的材质与 _MapId
        /// （一个簇必须只属于一个纹理集，否则没法按材质画）。</summary>
        public int[] clusterSubmesh;

        public int clusterCount => clusterMeta != null ? clusterMeta.Length : 0;

        /// <summary>该簇每提高一级 LOD，世界半径大约放大多少倍（snap-round 用的是 1/4 顶点密度）。</summary>
        public const float LodRadiusScale = 2f;
    }
}
