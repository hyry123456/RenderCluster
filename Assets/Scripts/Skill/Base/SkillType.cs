
namespace Skill
{
    [System.Serializable]
    public enum SkillType 
    {
        /// <summary>        /// 远程攻击技能        /// </summary>
        LongDisAttack = 1,
        /// <summary>        /// 近战技能        /// </summary>
        NearDisAttack = 2,
        /// <summary>        /// 辅助技能，加伤害之类的        /// </summary>
        Auxiliary = 4,
        /// <summary>        /// 治疗技能        /// </summary>
        Heal = 8,
        /// <summary>        /// 闪避，防御也归为此类技能        /// </summary>
        Dodge = 16,
        /// <summary>
        /// 状态技能，因为技能是挂载在一个角色上的，并且需要实时与AI以及控制交互，
        /// 因此就将角色状态也绑定在技能上，需要注意的是这个是静态状态，
        /// 不能释放，不会进行显示
        /// </summary>
        StaticState = 32,
    }
}