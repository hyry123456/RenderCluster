using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary> /// 渲染管线工具类，只负责按照特定的流程执行，不走完整流程 /// </summary>
    public class ToolPipeline : RenderPipelineBase
    {
        public void Render(ScriptableRenderContext context, Camera camera)
        {
        }


        public override void Dispose()
        {
            throw new System.NotImplementedException();
        }
    }

    public class ToolContext
    {

    }

    public class ToolRegisterSystem : RenderAsset
    {


        public override void Dispose()
        {
        }

        public override void Render(ScriptableRenderContext context, Camera camera, int renderLayer)
        {

        }
    }
}