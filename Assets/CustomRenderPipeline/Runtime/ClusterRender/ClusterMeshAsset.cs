using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// 一个 mesh 烘焙出的「簇 + LOD 链」数据（【真正的 cluster 管线】的资产载体）。
    ///
    /// 布局（全部按簇对齐，方便 GPU 端用 SV_InstanceID 一次间接绘制）:
    ///   positions     —— 合并后的全部顶点位置（只存 position，阴影不需要 uv/normal）
    ///   indices       —— 按簇分块存储，每簇固定 clusterTriCount*3 个条目（不足的补退化三角形）
    ///   clusterMeta   —— 每簇 x=顶点起始 y=索引起始 z=LOD 级别 w=三角形数(含填充)
    ///   clusterBounds —— 每簇包围球 (xyz=中心, w=半径)，局部空间
    /// </summary>
    public class ClusterMeshAsset : ScriptableObject
    {
        public Mesh sourceMesh;

        /// <summary>每簇的目标三角形数（对齐到这个数，GPU 端才能一次画 384 个顶点）。</summary>
        public int clusterTriCount = 128;

        /// <summary>LOD 级别数（0 = 原始，越大越粗）。</summary>
        public int lodCount = 1;

        public Vector3[] positions;
        public int[] indices;

        /// <summary>每簇 (vertexStart, indexStart, lodLevel, triCount)</summary>
        public Vector4[] clusterMeta;

        /// <summary>每簇包围球 (cx, cy, cz, r)，局部空间。</summary>
        public Vector4[] clusterBounds;

        public int clusterCount => clusterMeta != null ? clusterMeta.Length : 0;

        /// <summary>该簇每提高一级 LOD，世界半径大约放大多少倍（snap-round 用的是 1/4 顶点密度）。</summary>
        public const float LodRadiusScale = 2f;
    }
}
