using UnityEngine;


namespace Motor
{
    /// <summary>
    /// 移动类的抽象基类，用来定义最基本的移动需求
    /// </summary>
    public abstract class MotorBase : MonoBehaviour
    {
        /// <summary>/// 移动方法，一般只移动X以及Z轴，Y轴的移动靠跳跃或者其他方式 /// </summary>
        public abstract void Move(float horizontal, float vertical);
        /// <summary> /// 跳跃方法  /// </summary>
        public abstract void DesireJump();

        /// <summary> /// 检测是否在地上，用来控制跳跃动画以及下降动画   /// </summary>
        public abstract bool OnGround();

        /// <summary>/// 开启自动旋转，一般的运动引擎都需要自动旋转的 /// </summary>
        public abstract void BeginAutoRotate();

        /// <summary>/// 关闭自动旋转，比如模型需要看向主角时，禁止转向移动方向 /// </summary>
        public abstract void EndAutoRotate();

    }
}