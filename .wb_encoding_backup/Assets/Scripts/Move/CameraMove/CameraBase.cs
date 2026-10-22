using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Motor
{
    public abstract class CameraBase : MonoBehaviour
    {
        private static CameraBase instance;
        public static CameraBase Instance => instance;

        /// <summary>   /// 停止跟随主角    /// </summary>
        public abstract void StopFollow();
        /// <summary>   /// 开始跟随主角    /// </summary>
        public abstract void BeginFollow();

        /// <summary>   /// 调整方向，用来制作屏幕抖动   /// </summary>
        /// <param name="offset">偏移的方向</param>
        public abstract void AdjustPosition(Vector3 offset);
        /// <summary>   /// 回到初始位置    /// </summary>
        public abstract void BackToBegin();

        //进行注册
        protected virtual void Awake()
        {
            instance = this;
        }

        //取消注册
        protected virtual void OnDestroy()
        {
            instance = null;
        }



    }
}