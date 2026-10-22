using UnityEngine;

namespace Common
{

    /// <summary>
    /// 碰撞事件类，这个类是挂到对象池类的物体上的，不需要动态加入和删除，会一直挂在物体上
    /// </summary>
    public class CollisionEvent : MonoBehaviour
    {
        private void Start()
        {
            GetComponent<Collider>().isTrigger = false; //碰撞物体，不应该是触发类
        }

        private void OnDisable()
        {
            collsionEnter = null;       //关闭时自动清理事件
        }

        private IOneReturnTwoParam<Collision, GameObject, bool> collsionEnter;

        /// <summary>
        /// 设置碰撞进入事件
        /// </summary>
        /// <param name="enterEvent">进入时执行的方法，由该方法的返回值判断何时结束</param>
        public void SetCollsionEnterEvent(IOneReturnTwoParam<Collision, GameObject, bool> enterEvent)
        {
            collsionEnter = enterEvent;
        }


        private void OnCollisionEnter(Collision collision)
        {
            if(collsionEnter != null)
            {
                //触发时根据返回值判断是否是目标要触发的效果
                if(collsionEnter(collision, gameObject))
                {
                    collsionEnter = null;
                }
            }
        }



    }
}