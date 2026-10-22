
namespace Info
{
    public delegate int HPChangeDelegate(int hp, CharacterInfo info);
    public delegate bool StateUpdateDelegate(CharacterInfo info, int finalHp, int finalSolid);
    public delegate int SolidChangeDelegate(int solid, CharacterInfo info);

    /// <summary>
    /// 角色状态委托，也就是一个全空状态
    /// </summary>
    public class StateDelegate : CharacterState
    {
        public HPChangeDelegate hpChangeDelegate;
        public StateUpdateDelegate stateUpdateDelegate;
        public SolidChangeDelegate solidChangeDelegate;

        public int GetAttack(CharacterInfo info, int attack)
        {
            return attack;
        }

        public int GetDefense(CharacterInfo info, int defense)
        {
            return defense;
        }

        public float GetRunSpeed(CharacterInfo info, float runSpeed)
        {            
            return runSpeed;
        }

        public float GetWalkSpeed(CharacterInfo info, float walkSpeed)
        {
            return walkSpeed;
        }

        public int OnHPChange(int hp, CharacterInfo info)
        {
            if (hpChangeDelegate != null)
                return hpChangeDelegate(hp, info);
            return hp;
        }

        public int OnSolidChange(int solid, CharacterInfo info)
        {
            if (solidChangeDelegate != null)
                return solidChangeDelegate(solid, info);
            return solid;
        }


        public bool OnUpdate(CharacterInfo info, int finalHp, int finalSolid)
        {
            if (stateUpdateDelegate != null)
                return stateUpdateDelegate(info, finalHp, finalSolid);
            //没有直接移除
            return true;
        }
    }
}