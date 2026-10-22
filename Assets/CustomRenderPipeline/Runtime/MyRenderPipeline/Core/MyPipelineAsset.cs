using CustomRenderPipline.DefferPipeline;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline
{
    [CreateAssetMenu(menuName = "Rendering/RenderAsset/My Render Pipeline")]
    public class MyPipelineAsset : RenderAsset
    {
        public Shader cameraShader;

        [SerializeField]
        DefferSetting defferSetting = new DefferSetting();
        MyPipeline myPipeline;

        private bool CreateDefferPipeline()
        {
            if (myPipeline != null)
                return true;
            else
            {
                if (cameraShader == null)
                    return false;
                myPipeline = new MyPipeline(cameraShader);
                return true;
            }

        }

        public override void Dispose()
        {
            if (myPipeline != null)
            {
                myPipeline.Dispose();
                myPipeline = null;
            }
        }

        public override void Render(ScriptableRenderContext context, Camera camera, int renderLayer)
        {
            renderSetting.RenderLayer = renderLayer;
            if (CreateDefferPipeline())
                myPipeline.Render(context, camera,
                    renderSetting, shadowSetting, defferSetting);
        }
    }
}