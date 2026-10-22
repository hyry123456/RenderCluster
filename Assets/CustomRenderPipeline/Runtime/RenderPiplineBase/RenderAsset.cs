using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary> /// 渲染文件基类，用来调用实际的渲染管线 /// </summary>
    public abstract partial class RenderAsset : ScriptableObject
    {
        [SerializeField]
        protected RenderSetting renderSetting =
            new RenderSetting();

        [SerializeField]
        protected ShadowSetting shadowSetting = new ShadowSetting();

        /// <summary>   /// 渲染管线的调用方法     /// </summary>
        public abstract void Render(ScriptableRenderContext context, Camera camera,
            int renderLayer);

        public abstract void Dispose();
    }
}