using UnityEngine;

public class AutoAnimateSimple : AnimateManage
{

    protected override void AutoAnimate()
    {
        //Vector3 dir = transform.position - prePosition;
        //dir = transform.InverseTransformDirection(dir);
        //dir /= magnifySize;

        //Animator.SetFloat("Vertical", Mathf.Clamp(dir.z, -1, 1));
        //Animator.SetFloat("Horizontal", Mathf.Clamp(dir.x, -1, 1));
    }

    private void Update()
    {
        //AutoAnimate();
    }

    /// <summary>    /// 设置动画数据    /// </summary>
    /// <param name="datas">1：vertical，2：horizontal</param>
    public override void SetAnimate(float[] datas)
    {
        Animator.SetFloat("Move", datas[0]);
    }
}
