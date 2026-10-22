
namespace Skill
{
    [System.Serializable]
    public abstract class SkillBase : ISkill
    {
        public abstract bool OnSkillRelease(SkillManage mana);
        /// <summary>    /// 技能最大数量,负数表示无限     /// </summary>
        public int maxSkillCount;
        /// <summary>        /// 技能当前数量       /// </summary>
        public int skillCount;
        /// <summary>        /// 当前冷却时间，用来给技能控制器判断技能能不能释放        /// </summary>
        public float preReleaseTime;
        /// <summary>        /// 技能冷却时间，冷却时间没有结束，不能停止技能        /// </summary>
        public float coolTime;
        /// <summary>       /// 技能的释放时间，不会限制技能释放       /// </summary>
        public float releaseTime;
        /// <summary>        /// 技能名称，必须不同方便区分        /// </summary>
        public string skillName;
        /// <summary>        /// 技能类型，用来分类        /// </summary>
        public SkillType skillType;

        /// <summary>
        /// 初始化案例
        /// </summary>
        //public SkillBase()
        //{
        //    maxSkillCount = 10;
        //    nowCoolTime = 0;
        //    skillCount = maxSkillCount;
        //    releaseTime = 0;
        //    coolTime = 0;
        //    skillName = "Skill Name;
        //    skillType = SkillType.TYPE;
        //}
    }
}