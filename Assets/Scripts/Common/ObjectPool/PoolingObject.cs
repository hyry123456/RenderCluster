
using UnityEngine;

namespace Common
{
    /// <summary>
    /// 池化物体，是一个通用的池化挂载组件，因此注意名称区分,
    /// 一般都是临时创建的物体，因此只带自动关闭流程
    /// </summary>
    public class PoolingObject : ObjectPoolingBase
    {
        [SerializeField]
        float waitTime = -1;    //初始化为负数
        INonReturnOneParam<PoolingObject> updateEvent;
        INonReturnOneParam<PoolingObject> endEvent;
        /// <summary>
        /// 持续运行waitTime的事件，在期间不停调用updateEvent，最后调用endEvent，
        /// 调用endEvent后不会直接关闭，而是等待下一步行为
        /// </summary>
        /// <param name="waitTime"></param>
        /// <param name="updateEvent"></param>
        public void WaitTimeToClose(float waitTime, INonReturnOneParam<PoolingObject> updateEvent,
            INonReturnOneParam<PoolingObject> endEvent)
        {
            //这个定时流程是可以直接替换的，因此不需要判断，只需要替换
            this.waitTime = waitTime;
            this.updateEvent = updateEvent;
            this.endEvent = endEvent;
        }
        public override void OnInitialize()
        {
        }
        //public INonReturnAndNonParam UpdateEvent

        private void Update()
        {
            if (waitTime < 0) return;       //不需要等待关闭
            if (updateEvent != null) updateEvent(this);
            waitTime -= Time.deltaTime;
            if(waitTime < 0)
            {
                var tempEnd = endEvent;
                updateEvent = null;
                endEvent = null;        //提前置空，方便嵌套

                if (tempEnd != null) tempEnd(this);
                //CloseObject();
            }
        }
    }
}