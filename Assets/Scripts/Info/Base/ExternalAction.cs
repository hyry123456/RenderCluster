using System.Collections.Generic;
using UnityEngine;

namespace Info
{
    //事件回调，用来告知受击对象，以及最终的影响数据
    public delegate void ActionRecall(CharacterInfo info, int finalHp, int finalSolid);
    /// <summary> /// 外部影响的输入数据  /// </summary>
    public struct ActionData
    {
        /// <summary>     /// 对防御值的影响      /// </summary>
        public int solide;
        /// <summary>    /// 对hp的影响      /// </summary>
        public int hp;
        /// <summary>    /// 需要添加的状态    /// </summary>
        public CharacterState state;
        /// <summary>    /// 回调事件    /// </summary>
        public ActionRecall recall;
    }
    
    /// <summary>
    /// 外部影响类，外部对物体的影响都添加到该类中，
    /// 而不是直接控制角色数据，方便其他物体也能获得伤害数，
    /// 也方便后面拓展防御力
    /// </summary>
    public class ExternalAction : MonoBehaviour
    {
        private Common.PoolingList<ActionData> actions =
            new Common.PoolingList<ActionData>();
        public void AddAction(ActionData action)
        {
            actions.Add(action);
        }

        public Common.PoolingList<ActionData> GetActions
        {
            get
            {
                return actions;
            }
        }

        /// <summary>   /// 既然是外部接口，不用时直接销毁    /// </summary>
        private void OnDisable()
        {
            Destroy(this);
        }

        /// <summary>
        /// 直接获取改变的hp值,对于一些不需要修改实际改变HP值的物体，
        /// 可以直接调用该函数
        /// </summary>
        public int GetHPChange
        {
            get
            {
                int hp = 0;
                while(actions.Count > 0)
                {
                    ActionData action = actions.GetValue(0);
                    actions.RemoveAt(0);
                    hp += action.hp;
                }
                return hp;
            }
        }
    }
}