using UnityEditor;
using UnityEngine;

namespace Cluster.Editor
{
    /// <summary>
    /// 诊断工具：把场景里所有 MeshRenderer 按「网格体积」从大到小列出前 N 个，
    /// 并标注是否带 ClusterObject。体积最大的那个通常就是误入场景的「山/大地形」。
    /// 用法：菜单 Tools / Cluster / List Largest Meshes（在场景打开时运行）。
    /// </summary>
    public static class ListLargestMeshes
    {
        [MenuItem("Tools/Cluster/List Largest Meshes")]
        public static void Run()
        {
            var renderers = Object.FindObjectsOfType<MeshRenderer>(true); // includeInactive
            if (renderers == null || renderers.Length == 0)
            {
                Debug.Log("[ListLargestMeshes] 场景里没有 MeshRenderer。");
                return;
            }

            var rows = new System.Collections.Generic.List<(string name, string path, string mesh, float vol, bool cluster)>();
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds.size;
                float vol = b.x * b.y * b.z;
                bool hasCO = r.GetComponent<Cluster.ClusterObject>() != null;
                string p = GetPath(r.transform);
                rows.Add((r.gameObject.name, p, mf.sharedMesh.name, vol, hasCO));
            }

            rows.Sort((a, b) => b.vol.CompareTo(a.vol));

            int top = Mathf.Min(25, rows.Count);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== 最大网格 Top {top}（体积 = bounds.x*y*z）===");
            for (int i = 0; i < top; i++)
            {
                var r = rows[i];
                sb.AppendLine($"  [{i + 1}] vol={r.vol:F0}  CO={(r.cluster ? "Y" : "-")}  {r.name}  mesh={r.mesh}");
                sb.AppendLine($"        path= {r.path}");
            }
            Debug.Log(sb.ToString());
        }

        static string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + " / " + p; }
            return p;
        }
    }
}
