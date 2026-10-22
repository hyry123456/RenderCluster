
using Skill;
using UnityEngine;

namespace StateMachine
{
    [CreateAssetMenu(menuName = "Custom/States/PillarBoss")]
    public class PillarBoss :  StateBase
    {
        public StateBase nextState;

        public override StateBase CheckState(StateMachineSystem executeObj)
        {
            object obj;
            executeObj.DataMap.TryGetValue(1, out obj);
            Info.CharacterInfo info = (Info.CharacterInfo)obj;
            if (info.isDie)
                return nextState;
            return null;
        }

        public override void EnterState(StateMachineSystem executeObj)
        {
            executeObj.DataMap.Add(0, executeObj.GetComponent<SkillManage>());
            executeObj.DataMap.Add(1, executeObj.GetComponent<Info.CharacterInfo>());
        }

        public override void ExitState(StateMachineSystem executeObj)
        {
        }

        public override void OnFixedUpdate(StateMachineSystem executeObj)
        {
            object obj;
            executeObj.DataMap.TryGetValue(0, out obj);
            SkillManage skillManage = (SkillManage)obj;
            var skill = skillManage.GetRandomSkillByType(SkillType.LongDisAttack | SkillType.NearDisAttack);
            if (skill != null)
            {
                skillManage.CheckAndRelase(skill);
            }
        }
    }
}