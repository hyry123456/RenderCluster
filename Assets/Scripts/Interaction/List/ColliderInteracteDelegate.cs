using UnityEngine;

namespace Interaction
{
    public delegate void TrigerDelegate(Collider other, GameObject origin);
    public delegate void CollisionDelegate(Collision collision, GameObject origin);

    /// <summary>
    /// 触发式交互事件，用于动态添加碰撞交互行为，
    /// 这个一般只是设置一些临时对象，如果是对象池对象，不适合用这个，因为会删除
    /// </summary>
    public class ColliderInteracteDelegate : InteractionBase
    {
        private bool isSustain = false;
        private string trigerTags;
        /// <summary>        /// 首次触发时执行的行为        /// </summary>
        private TrigerDelegate trigerEnter;
        public TrigerDelegate TrigerEnter
        {
            set { trigerEnter = value; }
        }
        /// <summary>        /// 触发结束时执行的行为        /// </summary>
        private TrigerDelegate trigerExit;
        public TrigerDelegate TrigerExit
        {
            set { trigerExit = value; }
        }

        private CollisionDelegate collisionEnter;
        public CollisionDelegate CollisionEnter
        {
            set { collisionEnter = value; }
        }
        private CollisionDelegate collisionExit;
        public CollisionDelegate CollisionExit
        {
            set { collisionExit = value; }
        }


        /// <summary>   /// 是否为持续存在的交互    /// </summary>
        public bool IsSustain
        {
            set
            {
                isSustain = value;
            }
        }
        /// <summary> /// 交互对象的标签   /// </summary>
        public string TrigerTags
        {
            set
            {
                trigerTags = value;
            }
        }

        public override void InteractionBehavior(Common.INonReturnAndNonParam recall)
        {
            recall();
        }

        /// <summary>/// 当触发发生时执行的方法，用来执行交互，当触发结束后立刻结束该交互/// </summary>
        private void OnTriggerEnter(Collider other)
        {
            //判断是否是目标
            if(other.tag == trigerTags)
            {
                if(trigerEnter != null)
                {
                    trigerEnter(other, gameObject);
                    if (!isSustain)
                        trigerEnter = null;
                }
                //非持续就删除自身
                if(!isSustain)
                    Destroy(gameObject);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            //判断是否是目标
            if (other.tag == trigerTags)
            {
                if (trigerExit != null)
                {
                    trigerExit(other, gameObject);
                    if (!isSustain)
                        trigerExit = null;
                }
                //非持续就删除自身
                if (!isSustain)
                    Destroy(gameObject);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            //判断是否是目标
            if (collision.collider.tag == trigerTags)
            {
                if (collisionEnter != null)
                {
                    collisionEnter(collision, gameObject);
                    if (!isSustain)
                        collisionEnter = null;
                }
                //非持续就删除自身
                if (!isSustain)
                    Destroy(gameObject);
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            //判断是否是目标
            if (collision.collider.tag == trigerTags)
            {
                if (collisionExit != null)
                {
                    collisionExit(collision, gameObject);
                    if (!isSustain)
                        collisionExit = null;
                }
                //非持续就删除自身
                if (!isSustain)
                    Destroy(gameObject);
            }
        }

        protected override void OnEnable()
        {
        }


    }
}