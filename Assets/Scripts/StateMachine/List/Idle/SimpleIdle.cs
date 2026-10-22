
using UnityEngine;

namespace StateMachine
{
    /// <summary>
    /// 最坚定的待机状态，直接进入下一阶段
    /// </summary>
    [CreateAssetMenu(menuName = "Custom/States/Simple")]
    public class SimpleIdle : StateBase
    {
        public StateBase nextState;
        public override StateBase CheckState(StateMachineSystem executeObj)
        {
            return nextState;
        }

        public override void EnterState(StateMachineSystem executeObj)
        {

        }

        public override void ExitState(StateMachineSystem executeObj)
        {

        }

        public override void OnFixedUpdate(StateMachineSystem executeObj)
        {

        }
    }
}