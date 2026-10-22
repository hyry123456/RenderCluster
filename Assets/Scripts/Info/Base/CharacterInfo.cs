using UnityEngine;
using Common;
using System.Reflection;
using System.Collections.Generic;

namespace Info
{
     public enum CharacterType
    {
        NPC,
        Enemy,
        Player,
    }

     [System.Serializable]
    public abstract class CharacterInfo : MonoBehaviour
    {
        /// <summary>    /// 用来存储所有角色数据用的类    /// </summary>
        private static List<CharacterInfo> characters = new List<CharacterInfo>();

        public CharacterType CharacterType = CharacterType.Enemy;

        public static CharacterInfo GetNearCharacter(Vector3 beginPos, CharacterType tpye)
        {
            CharacterInfo re = null; float dis = float.MaxValue;
            for(int i = 0; i < characters.Count; i++)
            {
                if (characters[i].CharacterType == tpye)
                {
                    float curDis = (characters[i].transform.position - beginPos).sqrMagnitude;
                    if(curDis < dis)
                    {
                        dis = curDis;
                        re = characters[i];
                    }
                }
            }
            return re;
        }


        [SerializeField]
        /// <summary>  /// 默认技能，可以不赋予值   /// </summary>
        private string[] defaultSkills;

        public string characterName;

        [SerializeField]
        protected int hp = 10;
        [SerializeField]
        protected int maxHP = 10;
        [SerializeField]
        protected int maxSolidRadio = 10;
        [SerializeField]
        protected int solidRadio;

        [SerializeField]
        /// <summary>        /// 跑步速度        /// </summary>
        protected float runSpeed = 10;
        [SerializeField]
        /// <summary>        /// 行走速度        /// </summary>
        protected float walkSpeed = 5;
        [SerializeField]
        protected float rotateSpeed = 10;

        [SerializeField]
        protected int attack = 1;
        [SerializeField]
        protected int defense = 1;


        /// <summary>   /// 该人物可攻击到的敌人层，以及可遮挡层     /// </summary>
        public LayerMask attackLayer;
        public void SetSolidRadio(int solid)
        {
            solidRadio = solid;
        }

        //获取数据的一些列方法
        #region GetData
        /// <summary>  /// 跑步速度   /// </summary>
        public float RunSpeed
        {
            get
            {
                float speed = runSpeed;
                for(int i=0; i<states.Count; i++)
                {
                    speed = states.GetValue(i).GetRunSpeed(this, speed);
                }
                return speed;
            }
        }
        /// <summary>  /// 移动速度   /// </summary>
        public float WalkSpeed
        {
            get
            {
                float speed = walkSpeed;
                for (int i = 0; i < states.Count; i++)
                {
                    speed = states.GetValue(i).GetWalkSpeed(this, speed);
                }
                return speed;
            }
        /// <summary>  /// 旋转速度   /// </summary>
        }
        /// <summary>  /// 旋转速度   /// </summary>
        public float RotateSpeed
        {
            get
            {
                //旋转速度不进行调整
                return rotateSpeed;
            }
        }
        /// <summary>    /// 角色当前攻击力    /// </summary>
        public int Attack
        {
            get
            {
                int att = attack;
                for (int i = 0; i < states.Count; i++)
                {
                    att = states.GetValue(i).GetAttack(this, att);
                }
                return att;
            }
        }
        /// <summary>    /// 角色当前防御力    /// </summary>
        public int Defense
        {
            get
            {
                int def = defense;
                for (int i = 0; i < states.Count; i++)
                {
                    def = states.GetValue(i).GetDefense(this, def);
                }
                return def;
            }
        /// <summary>    /// 角色当前攻击力    /// </summary>
        }

        /// <summary>   /// 敌人最大坚硬值    /// </summary>
        public int MaxSolidRadio => maxSolidRadio;
        public int SolidRadio => solidRadio;

        #endregion


        /// <summary>        /// 判断角色是否死亡        /// </summary>
        public bool isDie => hp <= 0;

        protected PoolingList<CharacterState> states = new PoolingList<CharacterState>();

        private ExternalAction externalActions;

        /// <summary>        /// 初始化方法        /// </summary>

        protected virtual void OnEnable()
        {
            hp = maxHP;
            solidRadio = maxSolidRadio;
            if (defaultSkills == null)
                return;
            Assembly assembly = Assembly.GetExecutingAssembly();
            Skill.SkillManage skillManage = GetComponent<Skill.SkillManage>();
            externalActions = GetComponent<ExternalAction>();
            if (externalActions == null)
                externalActions = gameObject.AddComponent<ExternalAction>();
            characters.Add(this);


            if (skillManage == null) return;
            string prefit = "Skill.";
            for (int i = 0; i < defaultSkills.Length; i++)
            {
                Skill.SkillBase skillBase = (Skill.SkillBase)
                    assembly.CreateInstance(prefit + defaultSkills[i]);
                skillManage.AddSkill(skillBase);
            }

        }

        private void OnDisable()
        {
            characters.Remove(this);
        }

        /// <summary>        /// 判断是否死亡        /// </summary>
        protected void CheckDead()
        {
            if(isDie)
                DealWithDeath();
        }


        /// <summary>        /// 死亡后的操作        /// </summary>
        protected abstract void DealWithDeath();
        
        protected virtual void Update()
        {
            //接收外部影响
            PoolingList<ActionData> actions = externalActions.GetActions;
            int hpChange = 0, solidChange = 0;
            Stack<ActionRecall> recalls = 
                new Stack<ActionRecall>(actions.Count);

            while (actions.Count > 0)       //计算所有状态，获得最终操作的HP值
            {
                ActionData action = actions.GetValue(0);
                hpChange += action.hp; solidChange += action.solide;
                actions.RemoveAt(0);
                if (action.recall != null)
                    recalls.Push(action.recall);
                if (action.state != null)
                    states.Add(action.state);
            }
            //执行HP操作
            for (int i = 0; i < states.Count; i++)
            {
                hpChange = states.GetValue(i).OnHPChange(hpChange, this);
            }
            //检查死亡
            CheckDead();
            for (int i = 0; i < states.Count; i++)
            {
                solidChange = states.GetValue(i).OnSolidChange(solidChange, this);
            }

            for(int i=0; i<recalls.Count; i++)
            {
                ActionRecall recall = recalls.Pop();
                recall(this, hpChange, solidChange);
            }

            hp += hpChange;
            solidRadio += solidChange;

            //更新状态，此时两值可能为负，方便标识为第一次死亡或者破防
            for (int i = states.Count - 1; i >= 0; i--)
            {
                if (states.GetValue(i).OnUpdate(this, hpChange, solidChange))
                    states.RemoveAt(i);
            }

            hp = Mathf.Max(hp, 0);
            solidRadio = Mathf.Max(solidRadio, 0);
        }


    }
}