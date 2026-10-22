using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{


    /// <summary>
    /// 直接调用GPU接口进行直接渲染的方法基类，定义统一的调用格式
    /// </summary>
    public abstract class GPUDravinBase : MonoBehaviour
    {
        protected DrawNode drawNode;

        protected void ExecuteBuffer(ref CommandBuffer buffer, ScriptableRenderContext context)
        {
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }


    }
}