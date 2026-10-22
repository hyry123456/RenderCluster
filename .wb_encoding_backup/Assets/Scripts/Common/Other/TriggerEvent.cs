using CustomRenderPipline;
using UnityEngine;

namespace Common
{

    /// <summary>
    /// 触发事件类，这个类是挂到对象池类的物体上的，不需要动态加入和删除，会一直挂在物体上
    /// </summary>
    public class TriggerEvent : MonoBehaviour
    {
        private void Start()
        {
            GetComponent<Collider>().isTrigger = true; //设置为触发状态
        }
        private void OnDisable()
        {
            triggerEnter = null;       //关闭时自动清理事件
        }

        private IOneReturnTwoParam<Collider, GameObject, bool> triggerEnter;

        /// <summary>
        /// 设置碰撞进入事件
        /// </summary>
        /// <param name="enterEvent">进入时执行的方法，由该方法的返回值判断何时结束</param>
        public void SetCollsionEnterEvent(IOneReturnTwoParam<Collider, GameObject, bool> enterEvent)
        {
            triggerEnter = enterEvent;
        }

        private void OnTriggerEnter(Collider other)
        {
            if(triggerEnter != null)
            {
                if (triggerEnter(other, gameObject))
                    triggerEnter = null;
            }
        }

    }
}