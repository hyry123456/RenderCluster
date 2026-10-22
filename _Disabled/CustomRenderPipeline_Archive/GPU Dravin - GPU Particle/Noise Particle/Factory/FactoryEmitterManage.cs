using UnityEngine;

namespace CustomRenderPipline
{
    /// <summary>
    /// 粒子释放器的控制类，用来管理所有的释放器对象以及申请释放器
    /// </summary>
    public class FactoryEmitterManage : MonoBehaviour
    {
        private static FactoryEmitterManage instance;
        public static FactoryEmitterManage Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject go = new GameObject("EmitterManage");
                    go.AddComponent<FactoryEmitterManage>();
                }
                return instance;
            }
        }

        private void Awake()
        {
            if(instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private Common.PoolingList<FactoryEmitter> emitters = new Common.PoolingList<FactoryEmitter>();

        public FactoryEmitter GetFactoryEmitter(FactoryEmitterData data, Vector3 position)
        {
            if (emitters.Count == 0)
            {
                GameObject emitterObj = new GameObject("Emitter");
                emitterObj.transform.parent = transform;
                FactoryEmitter emitter = emitterObj.AddComponent<FactoryEmitter>();
                emitters.Add(emitter);
            }
            FactoryEmitter emitter1 = emitters.GetValue(0);
            emitters.RemoveAt(0);
            emitter1.Initialize(data, position);
            return emitter1;
        }

        /// <summary>    /// 移除释放器    /// </summary>
        public void RemoveEmitter(FactoryEmitter emitter)
        {
            emitters.Remove(emitter);
        }

        /// <summary>  /// 回收释放器   /// </summary>
        public void RecycleEmitter(FactoryEmitter emitter)
        {
            emitters.Add(emitter);
        }

    }
}