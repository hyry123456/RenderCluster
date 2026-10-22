using UnityEngine;

namespace Skill
{
    public class NearAttack : SkillBase
    {
        public NearAttack()
        {
            maxSkillCount = -1;
            skillCount = maxSkillCount;
            releaseTime = 2.5f;
            coolTime = 5;
            skillName = "½üÕ½¹¥»÷";
            skillType = SkillType.NearDisAttack;
        }

        public override bool OnSkillRelease(SkillManage mana)
        {
            mana.AnimateManage.SetAttack(3, 1.5f);
            return true;
        }
    }
}