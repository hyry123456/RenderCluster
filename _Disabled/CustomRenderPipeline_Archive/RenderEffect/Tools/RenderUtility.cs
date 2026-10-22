using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public enum UtilityPass
    {
        BlurHorizontal,
        BlurVertical,
    }
    /// <summary> /// 渲染助手类，用来执行一些通用的渲染行为 /// </summary>
    public class RenderUtility
    {
        Material mat;
        private static RenderUtility instance;
        public static RenderUtility Instance
        {
            get
            {
                if (instance == null)
                    instance = new RenderUtility();
                return instance;
            }
        }

        private RenderUtility()
        {
            mat = new Material(Shader.Find("CustomRP/Untility"));
        }

        /// <summary>        /// 进行纹理绘制        /// </summary>
        /// <param name="from">根据纹理</param>
        /// <param name="to">目标纹理</param>
        public void Draw(
            RenderTargetIdentifier from, RenderTargetIdentifier to, UtilityPass pass,
            CommandBuffer buffer
        )
        {
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, from);

            buffer.SetRenderTarget(to, RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.DontCare);

            buffer.DrawProcedural(
                Matrix4x4.identity, mat, (int)pass,
                MeshTopology.Triangles, 3);
        }
    }
}