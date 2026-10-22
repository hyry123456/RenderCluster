using UnityEngine;

namespace Cluster
{
    /// <summary>
    /// 剔除对照诊断（2026-10-20）。
    ///
    /// 要回答的问题：「BRG 模式下 1159 个实例一个都没被剔掉，是我们的剔除写错了，
    /// 还是相机本来就看得见全部？」
    ///
    /// 做法：**两侧用同一套 Unity 内置测试**（GeometryUtility.CalculateFrustumPlanes +
    /// TestPlanesAABB），避免"我们的剔除 vs Unity 的剔除"两边口径不同而没法比：
    ///   · 本文件：对场景里所有 MeshRenderer（不管 enabled —— BRG 模式下它们被禁用了，
    ///     若按 enabled 过滤，两种模式的总数就不一样，无法对照）测一遍视锥。
    ///     ⇒ 无论 Use RVT 开还是关，这个数字都一样，是稳定基线。
    ///   · BatchRenderManager.OnPerformCulling：对每个 BRG 实例既用我们自己的 IsVisible，
    ///     也用同一套 GeometryUtility 测一遍，统计两者是否一致。
    ///     若「我们的可见数 == GeometryUtility 的可见数 == 实例总数」，
    ///     就证明【剔除代码没错，真的是相机看得见全部】。
    ///
    /// 只在编辑器/开发构建里按节流输出，不影响发布性能。
    /// </summary>
    public static class CullDiagnostics
    {
        /// <summary>总开关（默认开）。</summary>
        public static bool Enabled = true;

        private const int kLogIntervalFrames = 300;
        private static int s_frame;

        /// <summary>
        /// 用 Unity 内置视锥测试统计场景里所有 MeshRenderer 的可见性。
        /// 由 DefferPipeline.Cull 每帧调用，内部自行节流。
        /// </summary>
        public static void ReportSceneRenderers(Camera cam)
        {
            if (!Enabled || cam == null) return;
            // Scene/预览相机不参与：它们的视锥与 Game 相机不同，会污染对照
            if (cam.cameraType != CameraType.Game) return;

            s_frame++;
            if (s_frame % kLogIntervalFrames != 0) return;

            Plane[] planes;
            try
            {
                planes = GeometryUtility.CalculateFrustumPlanes(cam);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[剔除对照] CalculateFrustumPlanes 失败: " + e.Message);
                return;
            }

            // 2022.2+ 的新 API（FindObjectsOfType 已进入废弃流程）。
            // includeInactive = true：场景里被禁用的 MeshRenderer 也要统计进来
            //（BRG 模式会把它们全禁用，但它们仍然是"这个场景有多少物体"的真实基数）。
            var all = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int total = 0, enabledCount = 0, inFrustum = 0, inFrustumEnabled = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                total++;
                // 【注意】不按 enabled 过滤：BRG 模式把 MeshRenderer 全禁用了，
                // 过滤掉的话两种模式统计到的总数不同，就没法和 BRG 的实例数对照。
                bool vis = GeometryUtility.TestPlanesAABB(planes, r.bounds);
                if (vis) inFrustum++;
                if (!r.enabled) continue;
                enabledCount++;
                if (vis) inFrustumEnabled++;
            }

            Debug.Log($"[剔除对照] 相机视锥内 MeshRenderer = {inFrustum} / 场景总数 {total} " +
                      $"({(total > 0 ? inFrustum * 100f / total : 0f):F1}%)  |  " +
                      $"其中 MeshRenderer 仍启用 = {enabledCount}，启用且在视锥内 = {inFrustumEnabled}" +
                      $"(BRG 接管后这批会被 enabled=false 关掉，由 BRG 批次接管绘制)");
        }
    }
}
