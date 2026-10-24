using System.Collections.Generic;
using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// 【真正的 cluster 管线】离线烘焙器。
    ///
    /// 做的事（对应 Nanite 的简化版，缺 DAG 的那部分）：
    ///   1. 合并 mesh 的所有 submesh，得到完整三角形列表（阴影不需要区分材质）；
    ///   2. 各级 LOD 用「顶点吸附snap-round」生成：
    ///        把顶点按 3D 网格量化 → 同一格的顶点合并 → 丢掉退化三角形。
    ///      这是最简单、且能保证【同一级别内部不会有裂缝】的简化方式
    ///      （整块一起量化，所有相邻三角形吸附到同一个格点，不会互相错位）；
    ///   3. 每一级都按三角形重心的 Morton 码排序，再切成 128 三角形一簇
    ///      —— Morton 排序保证同簇三角形空间相邻，簇的包围球才紧，剔除才有效；
    ///   4. 每簇补齐到 128 三角形（退化三角形填充），这样 GPU 端才能
    ///      「一次 indirect draw + 每实例固定 384 顶点」。
    /// </summary>
    public static class ClusterMeshBaker
    {
        const int MortonBits = 10;          // 每轴 10 bit
        const int MortonMask = (1 << MortonBits) - 1;

        struct Level
        {
            public Vector3[] positions;
            public int[] tris;              // 长度是 3 的倍数
        }

        public static void Bake(ClusterMeshAsset asset)
        {
            if (asset == null || asset.sourceMesh == null)
            {
                Debug.LogWarning("[Cluster烘焙] 缺少 sourceMesh。");
                return;
            }
            var mesh = asset.sourceMesh;
            int clusterTris = Mathf.Max(1, asset.clusterTriCount);
            int lodLevels = Mathf.Max(1, asset.lodCount);

            // ---- 1. 合并所有 submesh ----
            Vector3[] srcPos = mesh.vertices;
            var triList = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                triList.AddRange(mesh.GetIndices(s));

            var levels = new List<Level>
            {
                new Level { positions = srcPos, tris = triList.ToArray() }
            };

            // ---- 2. 逐级简化（网格分辨率每级减半）----
            for (int l = 1; l < lodLevels; l++)
            {
                int grid = Mathf.Max(4, 64 >> (l - 1));
                var lv = SnapRound(levels[l - 1], grid);
                // 极小的 mesh 在某些级别会被吸附到"一个三角形都不剩"。
                // 退化成空会导致运行时那一块【没有簇可选 ⇒ 破洞】。
                // 这里退回上一级：宁可多画一点，也不能漏。
                if (lv.tris.Length == 0) lv = levels[l - 1];
                levels.Add(lv);
            }

            // ---- 3. 每级：Morton 排序 → 切簇 ----
            var allPos = new List<Vector3>();
            var allIdx = new List<int>();
            var metas = new List<Vector4>();
            var bounds = new List<Vector4>();

            for (int l = 0; l < levels.Count; l++)
            {
                var lv = levels[l];
                int triCount = lv.tris.Length / 3;
                var order = MortonOrder(lv.positions, lv.tris, triCount);

                int clusters = (triCount + clusterTris - 1) / clusterTris;
                for (int c = 0; c < clusters; c++)
                {
                    int vStart = allPos.Count;
                    int iStart = allIdx.Count;
                    int n = Mathf.Min(clusterTris, triCount - c * clusterTris);

                    // 簇内顶点重编号（簇很小，可以直接 O(n) 去重）
                    var remap = new Dictionary<int, int>(n * 3);
                    for (int t = 0; t < n; t++)
                    {
                        int tri = order[c * clusterTris + t];
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = lv.tris[tri * 3 + k];
                            if (!remap.TryGetValue(vi, out int ni))
                            {
                                ni = allPos.Count - vStart;
                                remap[vi] = ni;
                                allPos.Add(lv.positions[vi]);
                            }
                            allIdx.Add(vStart + ni);
                        }
                    }

                    // 补齐到 clusterTris（三个索引相同 = 退化三角形，光栅化直接丢弃）
                    int pad = clusterTris - n;
                    for (int t = 0; t < pad; t++)
                        for (int k = 0; k < 3; k++)
                            allIdx.Add(vStart);

                    // 包围球（用簇内所有顶点的 AABB 转成球，保守）
                    Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int v = vStart; v < allPos.Count; v++)
                    {
                        var p = allPos[v];
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                    Vector3 center = (min + max) * 0.5f;
                    float radius = (max - min).magnitude * 0.5f + 1e-4f;

                    metas.Add(new Vector4(vStart, iStart, l, clusterTris));
                    bounds.Add(new Vector4(center.x, center.y, center.z, radius));
                }
            }

            asset.positions = allPos.ToArray();
            asset.indices = allIdx.ToArray();
            asset.clusterMeta = metas.ToArray();
            asset.clusterBounds = bounds.ToArray();
            // 烘焙只发生一次（EnsureBaked 按 mesh 缓存），不打日志，避免 Play/重建时刷屏；
            // 集群总数由 ClusterShadowRenderer.Rebuild 的统一汇总日志输出。
        }

        // ---------- 简化：把顶点吸附到 3D 网格，再丢掉退化三角形 ----------
        static Level SnapRound(Level src, int grid)
        {
            int n = src.positions.Length;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++)
            {
                min = Vector3.Min(min, src.positions[i]);
                max = Vector3.Max(max, src.positions[i]);
            }
            Vector3 size = max - min;
            float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) / grid;
            if (cell <= 0f) return src;

            // 顶点 -> 格点
            var cellOf = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                var p = src.positions[i] - min;
                cellOf[i * 3] = Mathf.Clamp((int)(p.x / cell), 0, grid);
                cellOf[i * 3 + 1] = Mathf.Clamp((int)(p.y / cell), 0, grid);
                cellOf[i * 3 + 2] = Mathf.Clamp((int)(p.z / cell), 0, grid);
            }

            // 同一个格点只保留第一次出现的顶点
            var rep = new int[n];
            var newPos = new List<Vector3>(n);
            var lookup = new Dictionary<int, int>(n);
            for (int i = 0; i < n; i++)
            {
                int key = (cellOf[i * 3] << 20) | (cellOf[i * 3 + 1] << 10) | cellOf[i * 3 + 2];
                if (!lookup.TryGetValue(key, out int ni))
                {
                    ni = newPos.Count;
                    lookup[key] = ni;
                    newPos.Add(src.positions[i]);
                }
                rep[i] = ni;
            }

            // 重映射三角形，丢掉三个顶点相同的退化三角形
            var newIdx = new List<int>(src.tris.Length);
            for (int t = 0; t < src.tris.Length; t += 3)
            {
                int a = rep[src.tris[t]], b = rep[src.tris[t + 1]], c = rep[src.tris[t + 2]];
                if (a == b || b == c || a == c) continue;
                newIdx.Add(a); newIdx.Add(b); newIdx.Add(c);
            }
            return new Level { positions = newPos.ToArray(), tris = newIdx.ToArray() };
        }

        // ---------- 三角形按重心的 Morton 码排序，返回三角形下标顺序 ----------
        static int[] MortonOrder(Vector3[] pos, int[] tris, int triCount)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < pos.Length; i++)
            {
                min = Vector3.Min(min, pos[i]);
                max = Vector3.Max(max, pos[i]);
            }
            Vector3 size = max - min;
            if (size.x <= 0f) size.x = 1f;
            if (size.y <= 0f) size.y = 1f;
            if (size.z <= 0f) size.z = 1f;

            var keys = new uint[triCount];
            var order = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 c = (pos[tris[t * 3]] + pos[tris[t * 3 + 1]] + pos[tris[t * 3 + 2]]) / 3f - min;
                int x = Mathf.Clamp((int)(c.x / size.x * MortonMask), 0, MortonMask);
                int y = Mathf.Clamp((int)(c.y / size.y * MortonMask), 0, MortonMask);
                int z = Mathf.Clamp((int)(c.z / size.z * MortonMask), 0, MortonMask);
                keys[t] = Part1By2(x) | (Part1By2(y) << 1) | (Part1By2(z) << 2);
                order[t] = t;
            }
            System.Array.Sort(keys, order);     // 按 key 排序，order 被打乱成 Morton 序
            return order;
        }

        static uint Part1By2(int x)
        {
            uint v = (uint)x & 0x000003ffu;
            v = (v | (v << 16)) & 0x030000ffu;
            v = (v | (v << 8)) & 0x0300f00fu;
            v = (v | (v << 4)) & 0x030c30c3u;
            v = (v | (v << 2)) & 0x09249249u;
            return v;
        }
    }
}
