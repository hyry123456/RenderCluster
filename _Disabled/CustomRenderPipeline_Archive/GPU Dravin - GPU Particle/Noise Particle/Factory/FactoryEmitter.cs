using UnityEngine;

namespace CustomRenderPipline
{
    public delegate void CollsionEnter(Collision collider, FactoryEmitter emitter);

    /// <summary>
    /// 粒子释放器对象，自动调用粒子工厂进行粒子释放，
    /// 且根据移动情况进行速度更新，并且封装碰撞方法，让粒子的更新更直观
    /// </summary>
    public class FactoryEmitter : MonoBehaviour
    {
        float waitTime;
        private FactoryEmitterData _data;
        private CollsionEnter collsionEnter;
        private Rigidbody _body;
        new Collider collider;
        //存储由该释放器申请的粒子碰撞器，当该释放器结束后会自动释放全部碰撞器
        Common.PoolingList<CollsionNode> particleCollsions;

        private void FixedUpdate()
        {
            if (_data.isReleasingWait)
            {
                waitTime += Time.deltaTime;
                if(waitTime > _data.releaseTime)
                {
                    waitTime = 0;
                    ReleaseParticle();
                }
            }
            else
                ReleaseParticle();
        }

        private void OnDestroy()
        {
            ReleaseCollsion();
            FactoryEmitterManage.Instance.RemoveEmitter(this);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collsionEnter != null)
                collsionEnter(collision, this);
        }

        public void ReleaseParticle()
        {
            Vector4 speed = _body.velocity * _data.beginSpeedScale * _data.beginSpeedScale;
            speed.w = _data.beginSpeed;
            ParticleFactory.Instance.ReleaseParticle(_data, transform.localToWorldMatrix, speed);
        }

        public void Initialize(FactoryEmitterData data, Vector3 position)
        {
            transform.position = position;
            transform.localScale = Vector3.one;
            transform.rotation = Quaternion.identity;
            _data = data;
            _body = GetComponent<Rigidbody>();
            if(_body == null)
                _body = gameObject.AddComponent<Rigidbody>();

            _body.useGravity = false;
            _body.velocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            gameObject.SetActive(true);
        }

        /// <summary>
        /// 关闭释放器调用的方法，该释放器会放回工厂中
        /// </summary>
        public void CloseEmitter()
        {
            gameObject.SetActive(false);
            if(collider != null)
                collider.enabled = false;
            FactoryEmitterManage.Instance.RecycleEmitter(this);
            ReleaseCollsion();
        }

        private void ReleaseCollsion()
        {
            if(particleCollsions != null)
            {
                for(int i = 0; i < particleCollsions.Count; i++)
                {
                    particleCollsions.GetValue(i).RemoveCollsion();
                }
                particleCollsions.RemoveAll();
            }
        }

        /// <summary> /// 彻底删除释放器时调用的算法，直接删除该对象  /// </summary>
        public void DisposeEmitter()
        {
            Destroy(gameObject);
        }

        /// <summary>
        /// 开启物理碰撞，以这个粒子的释放形状作为碰撞器
        /// </summary>
        public void BeginCollider(CollsionEnter collsionEnter, int layer = 1)
        {
            gameObject.layer = layer;
            this.collsionEnter = collsionEnter;
            if(_data.particleShadpeMode == InitialShapeMode.Sphere)
            {
                var collider = GetComponent<SphereCollider>();
                if(collider == null)
                    collider = gameObject.AddComponent<SphereCollider>();
                collider.enabled = true;
                collider.radius = _data.size;
                this.collider = collider;
            }
            else if(_data.particleShadpeMode == InitialShapeMode.Cube)
            {
                var collider = GetComponent<BoxCollider>();
                if (collider == null)
                    collider = gameObject.AddComponent<BoxCollider>();
                collider.enabled = true;
                collider.size = _data.size * Vector3.one;
                this.collider = collider;
            }
        }

        public Vector3 CurrentSpeed
        {
            get
            {
                return _body.velocity;
            }
        }

        /// <summary>  /// 调整当前速度   /// </summary>
        /// <param name="speed">用来替换的速度</param>
        public void AdjustSpeed(Vector3 speed)
        {
            _body.velocity = speed;
        }

        /// <summary>  /// 添加一个粒子碰撞器    /// </summary>
        /// <param name="collsionStruct">碰撞器结构</param>
        /// <returns>该碰撞器在释放器中的编号</returns>
        public int AddParticleCollsion(CollsionStruct collsionStruct)
        {
            if (particleCollsions == null)
                particleCollsions = new Common.PoolingList<CollsionNode>();
            particleCollsions.Add(ParticleFactory.Instance.CreateCollsionNode(collsionStruct));
            return particleCollsions.Count - 1;
        }

        /// <summary>  /// 更新碰撞器  /// </summary>
        /// <param name="index">要更新的位置</param>
        /// <param name="collsionStruct">更行为的结构</param>
        public void UpdateCollsion(int index, CollsionStruct collsionStruct)
        {
            if(particleCollsions == null || index > particleCollsions.Count)
            {
                Debug.LogError("碰撞器中不存在该节点");
            }
            particleCollsions.GetValue(index).UpdateCollsionStruct(collsionStruct);
        }


#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (particleCollsions != null && particleCollsions.Count > 0)
            {
                for (int i = 0; i < particleCollsions.Count; i++)
                {
                    CollsionStruct @struct = particleCollsions.GetValue(i).CollsionStruct();
                    Gizmos.color = Color.blue;
                    if (@struct.mode == 0)
                    {
                        Gizmos.matrix = @struct.localToWorld;
                        Gizmos.DrawWireCube(Vector3.zero, @struct.offset);
                    }
                    else
                    {
                        Gizmos.matrix = @struct.localToWorld;
                        Gizmos.DrawWireSphere(Vector3.zero, @struct.radius);
                    }
                }
            }

            if (_data != null)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.red;
                if (_data.particleShadpeMode == InitialShapeMode.Pos)
                    Gizmos.DrawWireSphere(Vector3.zero, 0.1f);
                else if (_data.particleShadpeMode == InitialShapeMode.Sphere)
                    Gizmos.DrawWireSphere(Vector3.zero, _data.size);
                else
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one * _data.size);
            }
        }
#endif
    }
}