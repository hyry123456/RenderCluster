using System.Collections.Generic;
using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// 烘焙结果缓存（按 mesh 去重，全局共享）。
    ///
    /// **同一份 mesh 会烘两份**（切簇方式不同，不能共用）：
    ///   splitBySubmesh = true  → GBuffer 用：一个簇只能属于一个纹理集（材质 / _MapId 按 submesh），
    ///                            并带 normal/tangent/uv。
    ///   splitBySubmesh = false → 阴影用：纯几何、不采材质，合并切簇 ⇒ 少掉「每个 submesh 最后一簇
    ///                            都要补齐到 128 三角形」的填充簇（簇数可少 20~40%），也不生成顶点属性。
    /// 两份都只烘一次（同参数命中缓存）。
    ///
    /// 当前只烘 LOD0（lodCount = 1）。以后要接多级 LOD 时改这里的参数即可。
    /// </summary>
    public static class ClusterMeshCache
    {
        /// <summary>每簇三角形数。改这个值必须同步改
        /// ClusterShadowRenderer / ClusterGBufferRenderer / compute / shader 里的 384。</summary>
        public const int TrisPerCluster = 128;

        static readonly Dictionary<Mesh, ClusterMeshAsset> s_split
            = new Dictionary<Mesh, ClusterMeshAsset>();
        static readonly Dictionary<Mesh, ClusterMeshAsset> s_merged
            = new Dictionary<Mesh, ClusterMeshAsset>();

        /// <summary>默认取「按 submesh 切」的那份（GBuffer 用）。</summary>
        public static ClusterMeshAsset Get(Mesh mesh) => Get(mesh, true);

        public static ClusterMeshAsset Get(Mesh mesh, bool splitBySubmesh)
        {
            if (mesh == null) return null;
            var cache = splitBySubmesh ? s_split : s_merged;
            if (cache.TryGetValue(mesh, out var asset) && asset != null) return asset;

            asset = ScriptableObject.CreateInstance<ClusterMeshAsset>();
            asset.sourceMesh = mesh;
            asset.clusterTriCount = TrisPerCluster;
            asset.lodCount = 1;                 // 只烘 LOD0
            asset.splitBySubmesh = splitBySubmesh;
            ClusterMeshBaker.Bake(asset);
            cache[mesh] = asset;
            return asset;
        }

        /// <summary>退出 Play / 管线销毁时清空（烘焙结果是纯 CPU 数据，丢掉即可重建）。
        /// 只清引用、不 Destroy：阴影渲染器可能还持有同一批对象。</summary>
        public static void Clear()
        {
            s_split.Clear();
            s_merged.Clear();
        }
    }
}
