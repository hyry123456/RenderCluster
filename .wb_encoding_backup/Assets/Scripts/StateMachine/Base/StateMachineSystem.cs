using System.Collections.Generic;
using UnityEngine;

namespace StateMachine
{
    /// <summary>
    /// 这个状态机的内容很少，只负责进入最初状态和状态切换，具体内容全部由状态类实现
    /// </summary>
    public class StateMachineSystem : MonoBehaviour
    {
        /// <summary>  /// 初始状态，用来作为状态机的初始行为，以及方便直接回退  /// </summary>
        public StateBase beginState;
        [SerializeField]
        /// <summary>    /// 当前状态，用来作为实时行为    /// </summary>
        StateBase nowState;

        private Dictionary<int, object> dataMap = new Dictionary<int, object>();
        /// <summary>
        /// 每一个对象的数据表，因为执行文件无法存储数据，因此数据只能由这个存储。
        /// 使用一般是进入时插入，退出时移除，避免编号重复
        /// </summary>
        public Dictionary<int, object> DataMap => dataMap;

        private void Start()
        {
            if (beginState != null)
            {
                beginState.EnterState(this);
                nowState = beginState;
            }
            else
            {
                Debug.LogError("无初始运行状态");
                DestroyImmediate(this);
            }

        }

        /// <summary>    /// 逐固定帧运行当前状态机的行为     /// </summary>
        private void FixedUpdate()
        {
            if (nowState == null)
                return;
            nowState.OnFixedUpdate(this);

            StateBase tempState = nowState.CheckState(this);
            if (tempState != null)
            {
                nowState.ExitState(this);
                tempState.EnterState(this);
                nowState = tempState;
            }
        }


    }
}