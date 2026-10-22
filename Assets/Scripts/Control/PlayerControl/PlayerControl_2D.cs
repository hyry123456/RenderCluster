using UnityEngine;


namespace Control
{
    public class PlayerControl_2D : PlayerControlBase
    {

        /// <summary> /// 时时刷新的控制属性的存放位置  /// </summary>
        private void Update()
        {
            if (!useControl) return;
            PlayerButtonControl.Instance.OnUpdate(skillControl);
        }

        /// <summary>
        /// 物理帧刷新的属性计算位置，一些没有必要逐帧计算的可以在这里进行计算
        /// </summary>
        private void FixedUpdate()
        {
            if (!useControl) return;
            if(skillManage.IsReleasing)     //锁移动，不锁定技能
            {
                motor.Move(0, 0);
                return;
            }
            PlayerButtonControl.Instance.OnFixUpdate(motor, skillControl, skillManage.AnimateManage);
        }

        //3D控制移动，直接用摄像机的前方即可
        public override Vector3 GetLookatDir()
        {
            return transform.right;
        }
    }
}