using Common.ResetInput;
using UnityEngine;

namespace Control
{
    public class PlayerControl_3D : PlayerControlBase
    {
        AnimateManage animateManage;

        protected override void Start()
        {
            base.Start();
            animateManage = GetComponent<AnimateManage>();
        }

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
            PlayerButtonControl.Instance.OnFixUpdate(motor, skillControl, skillManage.AnimateManage);



        }

        //3D控制移动，直接用摄像机的前方即可
        public override Vector3 GetLookatDir()
        {
            if (Camera.main == null)
                return Vector3.forward;
            return Camera.main.transform.forward;
        }
    }
}