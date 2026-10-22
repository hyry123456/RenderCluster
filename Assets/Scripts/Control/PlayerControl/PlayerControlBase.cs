using UnityEngine;

namespace Control
{
    /// <summary>
    /// 角色控制基类，定义一系列的抽象控制接口，
    /// 方便拓展不同游戏类型的角色控制
    /// </summary>
    public abstract class PlayerControlBase : MonoBehaviour
    {
        protected static PlayerControlBase instance;
        public static PlayerControlBase Instance => instance;
        protected Interaction.InteractionControl interactionControl;
        protected PlayerSkillControl skillControl;
        protected Motor.MotorBase motor;
        protected Skill.SkillManage skillManage;

        /// <summary>     /// 判断是否启动控制      /// </summary>
        protected bool useControl = true;
        public bool UseControl => useControl;

        protected virtual void Awake()
        {
            instance = this;
        }

        protected virtual void Start()
        {
            interactionControl = GetComponent<Interaction.InteractionControl>();
            skillControl = GetComponent<PlayerSkillControl>();
            motor = GetComponent<Motor.MotorBase>();
            skillManage = GetComponent<Skill.SkillManage>();
        }

        protected virtual void OnDestroy()
        {
            instance = null;
        }

        /// <summary>        /// 启动角色控制        /// </summary>
        public virtual void BeginControl()
        {
            useControl = true;
        }

        /// <summary>      /// 停止角色控制     /// </summary>
        public virtual void StopControl()
        {
            useControl = false;
        }

        /// <summary>/// 传入技能类名，添加技能/// </summary>
        /// <param name="skillName">类名</param>
        public virtual void AddSkill(string skillName)
        {
            skillControl.AddSkill(skillName);
        }

        /// <summary> /// 获得当前控制的主角看向的方向 /// </summary>
        public abstract Vector3 GetLookatDir();

    }

}