using UnityEngine;


namespace CustomRenderPipline
{
    [System.Serializable]
    public enum State
    {
        Playing,        //自动释放
        ReleasePaused,  //释放暂停，粒子正常运行
        AllPaused,      //释放以及粒子都暂停
    }

    /// <summary>
    /// 粒子系统基类，定义粒子的对外接口
    /// </summary>
    public abstract class ParticleBase : GPUDravinBase
    {
        [SerializeField]
        private State defaultState = State.Playing;


        [SerializeField]
        private State currentState;

        protected virtual void Start()
        {

        }

        /// <summary>   /// 当前粒子状态    /// </summary>
        public State CurrentState
        {
            get { return currentState; }
        }
        public void ChangeState(State state)
        {
            currentState = state;
        }

        private void LateUpdate()
        {
            switch (currentState)
            {
                case State.Playing:
                    ReleaseParticle();
                    UpdateParticle();

                    break;
                case State.ReleasePaused:
                    UpdateParticle();
                    break;
                default:
                    break;
            }
        }

        private void FixedUpdate()
        {
            FixUpdateParticle();
        }

        /// <summary>  /// 粒子释放，当需要粒子释放时会逐帧调用该方法    /// </summary>
        protected abstract void ReleaseParticle();

        /// <summary> /// 粒子逐帧运行，逐帧调用粒子运算    /// </summary>
        protected abstract void UpdateParticle();

        /// <summary> /// 粒子逐固定帧运行，进行粒子运算    /// </summary>
        protected abstract void FixUpdateParticle();


#if UNITY_EDITOR
        private State tempDefaultState;
        private State tempCurrentState;



        private void OnValidate()
        {
            if(tempCurrentState != currentState
                || tempDefaultState != defaultState)
            {
                tempCurrentState = currentState;
                tempDefaultState = defaultState;
                return;
            }
            Validate();
        }

        protected abstract void Validate();
#endif
    }
}