using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{


    /// <summary>
    /// 拓展管线，只进行简单的裁剪处理，具体渲染全部由外部设置
    /// </summary>
    public class ExtentPipeline : RenderPipelineBase
    {
        public void Render(ScriptableRenderContext context, Camera camera, int layerMask,
            ExtendRender extendRender)
        {
            this.context = context;
            this.camera = camera;

            if(!Cull())
                return;
            Setup();
            //执行插入行为
            //context.ExecuteCommandBuffer(buffer);
            if(extendRender != null)
            {
                extendRender(context, buffer, camera, layerMask, cullingResults);
                ExecuteBuffer();
                context.Submit();
            }
        }

        /// <summary>  /// 进行剔除，仅执行最简单的剔除   /// </summary>
        bool Cull()
        {

            //执行剔除，有可能剔除失败，因为要进行区分
            if (camera.TryGetCullingParameters(out ScriptableCullingParameters p))
            {
                //控制阴影距离，其不需要阴影，因为本身就是用来渲染阴影等数据用的
                p.shadowDistance = 0;
                //赋值裁剪结果
                cullingResults = context.Cull(ref p);
                return true;
            }

            return false;
        }

        void Setup()
        {
            //准备摄像机数据，剔除
            context.SetupCameraProperties(camera);
        }

        public override void Dispose()
        {

        }
    }


    public class ExtentCamera : MonoBehaviour
    {
        [SerializeField]
        ExtentCameraAsset extentAsset;
        ExtentCameraAsset ExtentAsset
        {
            get
            {
                if(extentAsset == null)
                {
                    PipelineCamera pipelineC = GetComponent<PipelineCamera>();
                    if (pipelineC == null)
                        pipelineC = gameObject.AddComponent<PipelineCamera>();
                    pipelineC.cameraRenderAsset = extentAsset;
                }
                return extentAsset;
            }
        }

        public void AddExtentAction(ExtendRender action)
        {
            ExtentAsset.AddExtendAction(action);
        }

        private void Start()
        {
            PipelineCamera pipelineCam = gameObject.GetComponent<PipelineCamera>();
            if (pipelineCam == null)
                pipelineCam = gameObject.AddComponent<PipelineCamera>();
            pipelineCam.cameraRenderAsset = extentAsset;
        }
    }
}