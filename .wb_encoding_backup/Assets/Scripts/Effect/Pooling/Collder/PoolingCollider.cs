using UnityEngine;

namespace Common
{
    public delegate void CollisionEnterEvent(Collider collider);

    /// <summary> /// 池化的球形碰撞体   /// </summary>
    class PoolingSphere : ObjectPoolingBase
    {
        private CollisionEnterEvent enterEvent;
        protected SphereCollider sphere;


        /// <summary>  /// 初始化球体碰撞器，不控制坐标，因为坐标由外部控制     /// </summary>
        /// <param name="radius">球体角度</param>
        /// <param name="enterEvent">执行的碰撞事件</param>
        public void InitializeSphere(float radius, CollisionEnterEvent enterEvent)
        {
            sphere.radius = radius;
            this.enterEvent = enterEvent;
        }

        private void OnCollisionEnter(Collision collision)
        {

        }

        private void OnTriggerEnter(Collider collider)
        {
            if (enterEvent != null)
                enterEvent(collider);
        }

        public override void CloseObject()
        {
            base.CloseObject();
            enterEvent = null;      //清除事件
        }

        public override void OnInitialize()
        {
            if (sphere == null)
                sphere = GetComponent<SphereCollider>();
        }
    }

    /// <summary>  /// 池化的方形碰撞体 /// </summary>
    class PoolingBox : ObjectPoolingBase
    {
        private CollisionEnterEvent enterEvent;
        protected BoxCollider box;


        /// <summary>  
        /// 初始化盒子碰撞器，不控制坐标以及角度，由外部控制
        /// </summary>
        /// <param name="size">xyz的大小</param>
        /// <param name="enterEvent">执行的碰撞事件</param>
        public void InitializeBox(Vector3 size, CollisionEnterEvent enterEvent)
        {
            box.size = size;
            this.enterEvent = enterEvent;
        }

        private void OnTriggerEnter(Collider collider)
        {
            if (enterEvent != null)
                enterEvent(collider);
        }

        public override void CloseObject()
        {
            base.CloseObject();
            enterEvent = null;      //清除事件
        }

        public override void OnInitialize()
        {
            if (box == null)
                box = GetComponent<BoxCollider>();
        }
    }

    /// <summary>  /// 为了方便池化使用，使用代码创建物体 /// </summary>
    class PoolingCollisionOrigin
    {
        private static PoolingSphere poolingSphere;
        public static PoolingSphere PoolingSphereOrigin
        {
            get
            {
                //没有就创建一个
                if(poolingSphere == null)
                {
                    GameObject game = new GameObject("PoolingSphere");
                    poolingSphere = game.AddComponent<PoolingSphere>();
                    SphereCollider sphere = game.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    game.layer = LayerMask.NameToLayer("Effect");
                    game.AddComponent<Rigidbody>();
                    GameObject.DontDestroyOnLoad(game);
                    game.SetActive(false);
                }
                return poolingSphere;
            }
        }

        private static PoolingBox poolingBox;
        /// <summary>
        /// 创建池化方形碰撞体的根据物体
        /// </summary>
        public static PoolingBox PoolingBoxOrigin
        {
            get
            {
                //没有就创建一个
                if (poolingBox == null)
                {
                    GameObject game = new GameObject("PoolingBox");
                    poolingBox = game.AddComponent<PoolingBox>();
                    BoxCollider box = game.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    game.layer = LayerMask.NameToLayer("Effect");
                    game.AddComponent<Rigidbody>();
                    GameObject.DontDestroyOnLoad(game);
                    game.SetActive(false);
                }
                return poolingBox;
            }
        }
    }
}