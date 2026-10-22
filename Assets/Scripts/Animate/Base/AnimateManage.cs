using UnityEngine;

/// <summary>
/// 动画类基类，用来进行动画播放
/// </summary>
[RequireComponent(typeof(Animator))]
public abstract class AnimateManage : MonoBehaviour
{
    /// <summary>  /// 动画控制类,用来切换主角动画  /// </summary>
    Animator animator;
    public Animator Animator => animator;
    /// <summary>/// 移动的大小，大于该值就会播放移动动画/// </summary>
    [Range(0.001f, 0.1f)]
    public float magnifySize = 0.05f;
    [SerializeField]
    //得到左手以及右手位置，经常动画需要用
    private Transform rightHand,     //左手
        leftHand;                   //右手
    public Transform RightHand => rightHand;
    public Transform LeftHand => leftHand;

    protected virtual void Start()
    {
        animator = GetComponent<Animator>();
    }

    protected virtual void FixedUpdate()
    {
        AutoAnimate();
        //SavePosition();
    }

    /// <summary>  
    /// 自动播放动画,播放运动动画，逐固定帧的动画刷新函数，一般重写该函数，
    /// 然后加上一定的数据设置就够了
    /// </summary>
    protected abstract void AutoAnimate();
    //{
    //    Vector3 dir = transform.position - prePosition;
    //    dir = transform.InverseTransformDirection(dir);
    //    dir /= magnifySize;

    //    animator.SetFloat("Forward", Mathf.Clamp( dir.z, -1, 1));
    //    animator.SetFloat("Horizontal", Mathf.Clamp( dir.x, -1, 1));
    //}

    //Vector3 prePosition;

    //private void SavePosition()
    //{
    //    prePosition = transform.position;
    //}

    /// <summary>   /// 动画事件，为true是正常动画触发，false是动画中断   /// </summary>
    public Common.INonReturnAndNonParam animateMidEvent;
    public Common.INonReturnAndNonParam animateEndEvent;
    public Common.INonReturnAndNonParam animateBreakEvent;

    /// <summary>
    /// 动画中期事件，对于技能动画，一般中间需要开始技能特效
    /// </summary>
    public void AnimateMidEvent()
    {
        if(animateMidEvent != null)
        {
            animateMidEvent();
            animateMidEvent = null;
        }
    }
    /// <summary>
    /// 动画结束事件，到这里基本就是技能彻底释放
    /// </summary>
    public void AnimateEndEvent()
    {
        if(animateEndEvent != null)
        {
            animateEndEvent();
            animateEndEvent = null;
        }
    }

    /// <summary>
    /// 技能被打断后执行的事件，用于技能释放停止
    /// </summary>
    private void BeBreakExit()
    {
        if (animateBreakEvent != null)
        {
            animateBreakEvent();
            animateBreakEvent = null;
        }
    }

    public abstract void SetAnimate(float[] datas);



    /// <summary>   /// 设置攻击动画编号   /// </summary>
    /// <param name="index">攻击动画编号</param>
    /// <param name="sustainTime">攻击持续时间</param>
    public bool SetAttack(int index, float sustainTime)
    {
        if (nowWaitTime < this.sustainTime)
            return false;
        Animator.SetInteger("Attack", index);
        this.sustainTime = sustainTime/2;
        nowWaitTime = 0;
        Common.SustainCoroutine.Instance.AddCoroutine(WaitToCloseAttack);
        return true;
    }

    float nowWaitTime, sustainTime;
    bool WaitToCloseAttack()
    {
        nowWaitTime += Time.deltaTime;
        if(nowWaitTime > sustainTime)
        {
            animator.SetInteger("Attack", 0);
            //顺便设置退出值，因为退出也是用这个协程
            animator.SetInteger("UnderAttack", 0);
            return true;
        }
        return false;
    }

    /// <summary>  /// 中途退出动画  /// </summary>
    public void ExitAnimate(int exitIndex, float waitTime)
    {
        animator.SetInteger("UnderAttack", exitIndex);      //设置退出值
        animator.SetInteger("Attack", 0);
        BeBreakExit();
        if (nowWaitTime < this.sustainTime)       //正在等待动画
        {
            nowWaitTime = 0;
            sustainTime = waitTime / 2;             //提前关了，不要等到动画结束后在关闭
            return;
        }
        nowWaitTime = 0;
        sustainTime = waitTime / 2;             //提前关了，不要等到动画结束后在关闭
        Common.SustainCoroutine.Instance.AddCoroutine(WaitToCloseAttack);
    }

}
