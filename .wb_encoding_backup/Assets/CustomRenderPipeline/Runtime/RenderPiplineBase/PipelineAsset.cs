using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public class RenderingLayerMaskFieldAttribute : PropertyAttribute { }


    /// <summary>
    /// 管线文件，传递渲染文件创建的渲染管线文件
    /// </summary>
    [CreateAssetMenu(menuName = "Rendering/RenderPipeline")]
    public partial class PipelineAsset : RenderPipelineAsset
    {

        public RenderAsset renderAsset;

        public Shader[] BindShaders;
        protected override RenderPipeline CreatePipeline()
        {
            return new RenderPiplineBase(renderAsset);
        }

    }

    class RenderPiplineBase : RenderPipeline
    {
        [SerializeField]
        RenderAsset defaultRenderAsset;


        public RenderPiplineBase(RenderAsset renderAsset)
        {
            defaultRenderAsset = renderAsset;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            defaultRenderAsset?.Dispose();
        }



        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            foreach(var camera in cameras)
            {
                var pipeCam = camera.GetComponent<PipelineCamera>();
                if (pipeCam != null && pipeCam.cameraRenderAsset != null)
                {
                    //if(!assets.Have(pipeCam.cameraRenderAsset))
                    //    assets.Add(pipeCam.cameraRenderAsset);
                    pipeCam.cameraRenderAsset.Render(context, camera,
                        pipeCam.renderingLayerMask);
                }
                else
                    defaultRenderAsset.Render(context, camera, -1);
            }
            context.Submit();
        }
    }
}