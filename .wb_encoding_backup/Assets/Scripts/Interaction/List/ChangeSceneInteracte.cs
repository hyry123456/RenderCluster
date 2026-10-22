using Common;
using UnityEngine;

namespace Interaction
{
    /// <summary> /// 改变场景用的交互  /// </summary>
    public class ChangeSceneInteracte : InteractionBase
    {
        public string sceneName;
        public int targetIndex;

        public override void InteractionBehavior(INonReturnAndNonParam recall)
        {
            //加载场景
            Common.SceneControl.Instance.ChangeScene(sceneName, targetIndex);
        }

        public override string GetInteracteRemain()
        {
            return "按下" + Common.ResetInput.MyInput.
                Instance.GetAxisKey("Interaction").ToString() + "切换场景";
        }

        protected override void OnEnable()
        {
        }
    }
}