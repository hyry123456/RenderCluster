
namespace Info
{
    /// <summary>  /// 角色状态，用来添加角色Buffer  /// </summary>
    public interface CharacterState
    {
        /// <summary>   /// 逐帧执行的方法     /// </summary>
        /// <param name="finalHp">最终改变的HP</param>
        /// <param name="finalSolid">最终改变的Solid</param>
        /// <returns>返回是否结束该状态，true为结束</returns>
        public bool OnUpdate(CharacterInfo info, int finalHp, int finalSolid);

        /// <summary>  /// 修改HP时执行的方法，注意HP可正可负   /// </summary>
        /// <param name="hp"></param>
        /// <param name="info">角色信息</param>
        /// <returns>改变成的hp</returns>
        public int OnHPChange(int hp, CharacterInfo info);

        /// <summary>     /// 修改坚硬值时执行的方法   /// </summary>
        /// <param name="solid">经过前面的处理后剩余的改变solid</param>
        /// <param name="info">角色信息</param>
        /// <returns>改变成的solid</returns>
        public int OnSolidChange(int solid, CharacterInfo info);

        /// <summary>   /// 得到跑步的速度   /// </summary>
        /// <param name="info">角色信息</param>
        /// <param name="runSpeed">跑步速度</param>
        /// <returns>调整后的跑步速度</returns>
        public float GetRunSpeed(CharacterInfo info, float runSpeed);

        /// <summary>   /// 得到走路的速度   /// </summary>
        /// <param name="info">角色信息</param>
        /// <param name="walkSpeed">走路速度</param>
        /// <returns>调整后的走路速度</returns>
        public float GetWalkSpeed(CharacterInfo info, float walkSpeed);

        /// <summary>   /// 得到伤害   /// </summary>
        /// <param name="info">角色信息</param>
        /// <param name="attack">伤害</param>
        /// <returns>调整后的伤害</returns>
        public int GetAttack(CharacterInfo info, int attack);


        /// <summary>   /// 得到防御   /// </summary>
        /// <param name="info">角色信息</param>
        /// <param name="defense">防御</param>
        /// <returns>调整后的防御</returns>
        public int GetDefense(CharacterInfo info, int defense);
    }
}