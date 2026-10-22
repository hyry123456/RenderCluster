using UnityEngine;

namespace CustomRenderPipline
{
    public class PipelineCamera : MonoBehaviour
    {
        /// <summary> /// 当前渲染摄像机的渲染文件  /// </summary>
        public RenderAsset cameraRenderAsset;

        /// <summary>	/// 渲染掩码，用来控制摄像机渲染的物体	/// </summary>
        [RenderingLayerMaskField]
        public int renderingLayerMask = -1;

        public void CopyFrom(PipelineCamera other)
        {
            renderingLayerMask = other.renderingLayerMask;
            cameraRenderAsset = other.cameraRenderAsset;
        }

        private void OnDestroy()
        {
            if(cameraRenderAsset != null)
                cameraRenderAsset.Dispose();
        }
    }
}