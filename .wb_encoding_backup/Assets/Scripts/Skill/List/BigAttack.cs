using Skill;

namespace Skill
{

    public class BigAttack : SkillBase
    {
        public BigAttack()
        {
            maxSkillCount = -1;
            skillCount = maxSkillCount;
            releaseTime = 2.5f;
            coolTime = 5;
            skillName = "¥Û’–";
            skillType = SkillType.LongDisAttack;
        }

        public override bool OnSkillRelease(SkillManage mana)
        {
            mana.AnimateManage.SetAttack(2, 1.5f);
            return true;
        }
    }
}