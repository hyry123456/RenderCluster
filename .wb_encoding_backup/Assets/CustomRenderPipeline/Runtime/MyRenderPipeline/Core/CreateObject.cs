using UnityEngine;

namespace CustomRenderPipline.MyRenderPipeline
{
    /// <summary>
    /// 在游戏场景中创建我的渲染管线的注册对象的类，方便设置为全局对象
    /// </summary>
    public class CreateObject : MonoBehaviour
    {
        private void Awake()
        {
            if(MyRenderObject.Instance == null)
            {
                GameObject gameObject = new GameObject("My Render Objects");
                gameObject.AddComponent<MyRenderObject>();
            }
        }
    }
}