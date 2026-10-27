using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>
    /// 场景光照全局量上传（原「GPU 绘制节点调度器」的精简版）。
    ///
    /// 历史：这里曾是一套 DrawNode 链表 + 多阶段绘制调度（GBuffer / 透明 / 不透明 / 天空盒 /
    /// 阴影 / Feedback）。BRG 移除后节点再也不会被插入，那些 Draw 调用全是空转，已全部删除。
    ///
    /// 当前唯一职责：每帧上传场景光照相关的两个全局量——
    ///   · _MySkybox —— 场景天空盒 Cubemap，供 GBuffer 的间接镜面反射采样（GI.hlsl）；
    ///   · _Sh9      —— 由 SceneSH9 从天空盒投影出的 SH9 系数，供延迟最终合成的间接漫反射。
    /// </summary>
    public class GPUDravinSystem
    {
        /// <summary>SH9 系数数量，与 shader 里的 float4 _Sh9[9] 对应。</summary>
        private const int Sh9Count = 9;

        /// <summary>未烘焙 SH9 时的全零系数（SetGlobalVectorArray 不接受空数组）。</summary>
        private static readonly Vector4[] s_defaultSh9 = new Vector4[Sh9Count];

        private static List<Vector4> s_sh9 = new List<Vector4>();
        private static Cubemap s_sceneSkybox;

        private static GPUDravinSystem s_instance;
        public static GPUDravinSystem Instance
        {
            get
            {
                if (s_instance == null) s_instance = new GPUDravinSystem();
                return s_instance;
            }
        }

        private readonly Plane[] m_cameraPlanes = new Plane[6];

        /// <summary>由 SceneSH9 烘焙完成后调用，缓存本帧要上传的光照数据。</summary>
        public static void SetSceneLighting(List<Vector4> sh9, Cubemap cubemap)
        {
            s_sh9 = sh9;
            s_sceneSkybox = cubemap;
        }

        /// <summary>
        /// 每帧上传天空盒与 SH9 全局量；顺带缓存相机视锥平面供 GetFrustumPlanes 使用。
        /// width / height / renderSetting 目前不参与计算，保留签名以兼容调用方。
        /// </summary>
        public void SetUp(ScriptableRenderContext context, CommandBuffer buffer,
            Camera camera, int width, int height, RenderSetting renderSetting)
        {
            if (camera != null)
                GeometryUtility.CalculateFrustumPlanes(camera, m_cameraPlanes);

            buffer.SetGlobalTexture("_MySkybox", s_sceneSkybox);

            if (s_sh9 != null && s_sh9.Count == Sh9Count)
                buffer.SetGlobalVectorArray("_Sh9", s_sh9);
            else
                buffer.SetGlobalVectorArray("_Sh9", s_defaultSh9);

            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        /// <summary>相机视锥平面（xyz = 法线，w = 距离）。</summary>
        public Vector4[] GetFrustumPlanes()
        {
            Vector4[] planes = new Vector4[m_cameraPlanes.Length];
            for (int i = 0; i < m_cameraPlanes.Length; i++)
            {
                Vector3 normal = m_cameraPlanes[i].normal;
                planes[i] = new Vector4(normal.x, normal.y, normal.z, m_cameraPlanes[i].distance);
            }
            return planes;
        }
    }
}
