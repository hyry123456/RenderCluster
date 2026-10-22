using Common;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

    public delegate void ExtendRender(ScriptableRenderContext context, CommandBuffer buffer, 
        Camera camera, int layerMask, CullingResults cullingResults);

    [CreateAssetMenu(menuName = "Custom/Rendering/RenderAsset/ExtentAsset")]
    public class ExtentCameraAsset : RenderAsset
    {
        private ExtentPipeline pipeline;
        private List<ExtendRender> actions = new List<ExtendRender>();

        /// <summary>
        /// 加入到拓展管线时执行的方法,在加入前，要先设置好摄像机位置，
        /// 保证调用该方法时，摄像机就已经是正确的摄像机位置
        /// </summary>
        /// <param name="action">设置好位置后执行的内容，一般可以在执行完后关闭摄像机，
        /// 因为关闭后不会影响结果</param>
        public void AddExtendAction(ExtendRender action)
        {
            CreatePipeline();
            actions.Add(action);       //放到这里，方便逐个执行
        }


        private bool CreatePipeline()
        {
            if (pipeline != null)
                return true;
            else
            {
                pipeline = new ExtentPipeline();
                return true;
            }
        }

        public override void Dispose()
        {
            pipeline?.Dispose();
        }


        public override void Render(ScriptableRenderContext context, Camera camera, int renderLayer)
        {
            CreatePipeline();
            if(actions.Count > 0) {
                pipeline.Render(context, camera, renderLayer, actions[0]);
                actions.RemoveAt(0);
            }
        }
    }

}