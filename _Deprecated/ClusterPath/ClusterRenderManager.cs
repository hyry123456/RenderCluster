using Cluster;
using CustomRenderPipline.Custer;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// Step2 P7（稳健版）：meshlet 簇渲染管理器。
    ///
    /// 关键改进（对比旧版 DrawProcedural + StructuredBuffer 手动展开）：
    ///   1) 几何走【真实 Mesh + DrawMesh】—— 硬件顶点获取 + post-transform 缓存 + 索引复用，
    ///      彻底消除「每顶点 3 次依赖式缓冲读取」和「顶点放大 2.2×」。
    ///   2) 每个簇是 combinedMesh 的一个 submesh（索引区间引用共享顶点），无 padding、零放大。
    ///   3) CPU 视锥球剔除（包围球 vs 6 视锥面，O(簇数) 极廉价）：离屏簇直接不 DrawMesh。
    ///   4) 矩阵仅当【物体 transform 变化】时刷新并重新变换世界包围球；静止场景每帧只做廉价
    ///      的剔除判定 + DrawMesh 调用，无 GPU 缓冲重传、无逐顶点展开。
    ///
    /// 绘制粒度 = 可见簇数（每簇一次 DrawMesh，同材质相邻调用由 SRP Batcher 合并，开销极低）。
    /// 后续可选 P7c：把同 owner 的可见簇索引打包进单个动态 IB → 每 owner 仅 1 次绘制。
    /// </summary>
    public static class ClusterRenderManager
    {
        private class Item
        {
            public ClusterObject owner;
            public ClusterMeshData data;
            public Material material;
        }

        private static readonly List<Item> s_items = new List<Item>();
        private static bool s_dirty = true;          // 集合变化后置脏，下一帧重建单元

        // 每个注册物体的绘制单元（矩阵 / 世界包围球缓存）
        private class Unit
        {
            public ClusterObject owner;
            public ClusterMeshData data;
            public Material material;
            public Matrix4x4 cachedMatrix;
            public bool matrixDirty = true;
            public Vector3[] worldCenters;
            public float[] worldRadii;
        }

        private static readonly List<Unit> s_units = new List<Unit>();
        private static readonly Plane[] s_planes = new Plane[6];

        public static void Register(ClusterObject owner, ClusterMeshData data, Material material)
        {
            if (owner == null || data == null || material == null) return;
            s_items.Add(new Item { owner = owner, data = data, material = material });
            s_dirty = true;
        }

        public static void Unregister(ClusterObject owner)
        {
            if (owner == null) return;
            int removed = s_items.RemoveAll(it => it.owner == owner);
            if (removed > 0) s_dirty = true;
        }

        public static void Clear()
        {
            s_items.Clear();
            s_units.Clear();
            s_dirty = true;
        }

        /// <summary>集合变化（增删物体）时，按注册项重建绘制单元。</summary>
        private static void RebuildUnits()
        {
            s_units.Clear();
            foreach (var it in s_items)
            {
                if (it.owner == null || !it.owner.gameObject.activeInHierarchy) continue;
                s_units.Add(new Unit
                {
                    owner = it.owner,
                    data = it.data,
                    material = it.material,
                    matrixDirty = true,
                    worldCenters = new Vector3[it.data.clusterCount],
                    worldRadii = new float[it.data.clusterCount],
                });
            }
            s_dirty = false;
        }

        /// <summary>包围球 vs 6 视锥面：任一面上球完全在外 -> 剔除。</summary>
        private static bool FrustumCullSphere(Vector3 c, float r)
        {
            for (int i = 0; i < 6; i++)
            {
                float dist = s_planes[i].normal.x * c.x + s_planes[i].normal.y * c.y +
                             s_planes[i].normal.z * c.z + s_planes[i].distance;
                if (dist < -r) return true;
            }
            return false;
        }

        private static float MaxAxisScale(Matrix4x4 m)
        {
            return Mathf.Max(Mathf.Max(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude),
                             m.GetColumn(2).magnitude);
        }

        /// <summary>
        /// 在 GBuffer 命令缓冲里追加簇绘制（真实 Mesh + DrawMesh + CPU 视锥剔除）。
        /// </summary>
        public static void Render(CommandBuffer cmd, Camera camera)
        {
            if (s_dirty) RebuildUnits();
            if (s_units.Count == 0) return;

            GeometryUtility.CalculateFrustumPlanes(camera, s_planes);

            for (int i = 0; i < s_units.Count; i++)
            {
                var u = s_units[i];
                if (u.owner == null) continue;

                Matrix4x4 m = u.owner.transform.localToWorldMatrix;
                bool moved = u.matrixDirty || m != u.cachedMatrix;
                if (moved)
                {
                    u.cachedMatrix = m;
                    u.matrixDirty = false;
                    float scale = MaxAxisScale(m);
                    // 仅物体移动时重新把模型空间包围球变换到世界空间（供本帧剔除）。
                    for (int c = 0; c < u.data.clusterCount; c++)
                    {
                        Bounds b = u.data.clusterBoundsOS[c];
                        u.worldCenters[c] = m.MultiplyPoint(b.center);
                        u.worldRadii[c] = b.extents.magnitude * scale;   // 包住 AABB 的保守球半径
                    }
                }

                // 逐 (owner, submesh) 设置 RVT 页表索引（其下所有可见簇共享）。
                cmd.SetGlobalInteger("_ClusterVtId", u.data.vtNodeIndex);

                // 逐可见簇绘制（DrawMesh 自动提供 unity_ObjectToWorld，走硬件顶点管线）。
                for (int c = 0; c < u.data.clusterCount; c++)
                {
                    if (FrustumCullSphere(u.worldCenters[c], u.worldRadii[c])) continue;
                    cmd.DrawMesh(u.data.combinedMesh, m, u.material, c, 0);
                }
            }
        }
    }
}
