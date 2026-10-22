using UnityEngine;

namespace Interaction
{
    /// <summary>    /// 交互信息类，用来存储交互内容    /// </summary>
    public struct InteracteInfo
    {
        public string data;
        public int id;
    }

    /// <summary>    /// 交互类基类，挂在物体上，用来执行简单的交互    /// </summary>
    public abstract class InteractionBase : MonoBehaviour
    {
        /// <summary>
        /// 初始化InteractionType还有interactionName
        /// </summary>
        protected abstract void OnEnable();

        /// <summary>        /// 该交互行为需要干的事情        /// </summary>
        public abstract void InteractionBehavior(Common.INonReturnAndNonParam recall);

        public virtual string GetInteracteRemain()
        {
            return "按下" + Common.ResetInput.MyInput.
                Instance.GetAxisKey("Interaction").ToString() + "开启交互";
        }

    }
}