using CustomRenderPipline.Custer;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// Step2 P7（稳健版）：mesh-cluster 数据构建器。
    ///
    /// 设计要点（与上一版「GraphicsBuffer 手动顶点缓冲」的根本区别）：
    ///   · 不再尝试 SetVertexBufferParams / DrawProcedural(indexBuffer) —— 这些 API 在本工程
    ///     的 Unity 2022.3 构建里并不可用（SRP Core 14 也只用矩阵版 DrawProcedural）。
    ///   · 改为构建一个【真实 Mesh】：顶点 = 源 Mesh 的原始属性（pos/normal/tangent/uv），
    ///     簇 = 该 Mesh 的【逐簇 submesh】（索引区间引用共享顶点）。
    ///   · 这样零顶点放大（簇共享原始顶点），且绘制走 DrawMesh 的【硬件顶点获取 + 索引复用 +
    ///     post-transform 缓存】—— 与 BRG 内部同一条 GPU 最快路径。
    ///   · 簇仅作为更细的【剔除 / 绘制粒度】：离屏簇用 CPU 视锥球剔除后不绘制。
    ///
    /// 复用 Step1 已验证的 RVT 页表基础设施（_TextureNodesBuffer / _Virtual* / TextureNodesMap）。
    /// </summary>
    public static class ClusterMeshBuilder
    {
        private static readonly Dictionary<(Mesh, int), ClusterMeshData> s_clusterCache
            = new Dictionary<(Mesh, int), ClusterMeshData>();

        /// <summary>构建（或取缓存）某 mesh 某 submesh 的簇数据（真实 Mesh + 逐簇 submesh）。</summary>
        /// <param name="mesh">源网格</param>
        /// <param name="subMeshIndex">子网格下标（对应材质槽）</param>
        /// <param name="vtNodeIndex">该 submesh 的虚拟贴图页表索引</param>
        public static ClusterMeshData BuildClusters(Mesh mesh, int subMeshIndex, int vtNodeIndex)
        {
            var key = (mesh, subMeshIndex);
            if (s_clusterCache.TryGetValue(key, out var cached))
                return cached;

            var data = new ClusterMeshData
            {
                vtNodeIndex = vtNodeIndex,
                sourceMesh = mesh,
                subMeshIndex = subMeshIndex,
            };

            var sub = mesh.GetSubMesh(subMeshIndex);
            int[] allTris = mesh.triangles;
            Vector3[] points = mesh.vertices;
            ClustersData cd = ClusterTools.CreateClusterData(
                allTris, sub.indexStart, sub.indexCount, points, isSimple: true);

            int clusterCount = cd.clusters.Length;
            data.clusterCount = clusterCount;

            int vertexCount = mesh.vertexCount;
            bool valid = true;

            var clusterBoundsOS = new Bounds[clusterCount];
            var clusterCenters = new Vector3[clusterCount];
            var clusterRadii = new float[clusterCount];
            var clusterTriCounts = new int[clusterCount];

            // 逐簇取出真实索引，并钳制越界（防御），同时算模型空间包围球。
            var clusterIdx = new int[clusterCount][];
            int totalIndices = 0;
            for (int c = 0; c < clusterCount; c++)
            {
                var cl = cd.clusters[c];
                int tc = cl.triCount;

                var idx = new int[tc];
                for (int k = 0; k < tc; k++)
                {
                    int v = cl.tris[k];
                    if (v < 0 || v >= vertexCount) { valid = false; v = 0; }
                    idx[k] = v;
                }
                clusterIdx[c] = idx;
                clusterTriCounts[c] = tc;
                totalIndices += tc;

                Vector3 center = (cl.boundsMin + cl.boundsMax) * 0.5f;
                float radius = (cl.boundsMin == cl.boundsMax)
                    ? 0f : Vector3.Distance(center, cl.boundsMax);
                clusterBoundsOS[c] = new Bounds(center, cl.boundsMax - cl.boundsMin);
                clusterCenters[c] = center;
                clusterRadii[c] = radius;
            }

            // 构建真实 Mesh：用源 Mesh 的原始顶点（零放大），逐簇一个 submesh。
            Mesh combined = new Mesh();
            combined.name = $"Cluster_{mesh.name}_sub{subMeshIndex}";
            combined.indexFormat = mesh.indexFormat;   // 顶点数 > 65535 时保持 UInt32
            combined.vertices = mesh.vertices;
            if (mesh.normals != null && mesh.normals.Length == vertexCount)
                combined.normals = mesh.normals;
            else
                combined.RecalculateNormals();
            if (mesh.tangents != null && mesh.tangents.Length == vertexCount)
                combined.tangents = mesh.tangents;
            else
                combined.RecalculateTangents();
            if (mesh.uv != null && mesh.uv.Length == vertexCount)
                combined.uv = mesh.uv;
            else
                combined.uv = new Vector2[vertexCount];
            combined.bounds = mesh.bounds;

            combined.subMeshCount = clusterCount;
            for (int c = 0; c < clusterCount; c++)
            {
                combined.SetIndices(clusterIdx[c], MeshTopology.Triangles, c, false);
            }

            data.combinedMesh = combined;
            data.clusterBoundsOS = clusterBoundsOS;
            data.clusterCenters = clusterCenters;
            data.clusterRadii = clusterRadii;
            data.clusterTriangleCounts = clusterTriCounts;
            data.isValid = valid && clusterCount > 0 && totalIndices == sub.indexCount;

            s_clusterCache[key] = data;
            LogBuild(mesh, subMeshIndex, data, sub.indexCount / 3, totalIndices);
            return data;
        }

        private static void LogBuild(Mesh mesh, int subMeshIndex, ClusterMeshData data, int submeshTriCount, int totalIndices)
        {
            Debug.Log($"[ClusterBuild] mesh={mesh.name} submesh={subMeshIndex} | " +
                $"clusterCount={data.clusterCount}, submeshTriangles={submeshTriCount}, " +
                $"builtIndices={totalIndices}, vtNodeIndex={data.vtNodeIndex}, " +
                $"valid={data.isValid}");
        }

        /// <summary>释放所有缓存的 Mesh（管线销毁时调用）。</summary>
        public static void DisposeAll()
        {
            foreach (var kv in s_clusterCache)
            {
                if (kv.Value.combinedMesh != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value.combinedMesh);
                    else UnityEngine.Object.DestroyImmediate(kv.Value.combinedMesh);
                }
            }
            s_clusterCache.Clear();
        }
    }

    /// <summary>某 mesh + submesh 的簇构建产物（P7 稳健版：真实 Mesh + 逐簇 submesh）。</summary>
    public class ClusterMeshData
    {
        public Mesh sourceMesh;
        public Mesh combinedMesh;            // 真实 Mesh：共享原始顶点 + 逐簇 submesh
        public int subMeshIndex;
        public int vtNodeIndex;
        public bool isValid;
        public int clusterCount;

        // 模型空间包围球（供 CPU 视锥剔除）
        public Bounds[] clusterBoundsOS;
        public Vector3[] clusterCenters;
        public float[] clusterRadii;

        // 日志/自校验用
        public int[] clusterTriangleCounts;
    }
}
