
using UnityEngine;

namespace StateMachine
{

    public abstract class StateBase : ScriptableObject
    {
        /// <summary>   /// 退出状态的行为     /// </summary>
        public abstract void ExitState(StateMachineSystem executeObj);
        /// <summary>   /// 进入状态的行为     /// </summary>
        public abstract void EnterState(StateMachineSystem executeObj);
        /// <summary>     /// 逐固定帧执行的行为      /// </summary>
        public abstract void OnFixedUpdate(StateMachineSystem executeObj);
        /// <summary>    /// 检查该帧行为，判断是否需要切换状态    /// </summary>
        public abstract StateBase CheckState(StateMachineSystem executeObj);
    }
}