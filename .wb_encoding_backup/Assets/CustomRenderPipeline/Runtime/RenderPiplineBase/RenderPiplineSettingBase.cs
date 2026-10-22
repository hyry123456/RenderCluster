using UnityEngine;

namespace CustomRenderPipline
{
    /// <summary>
    /// 渲染管线设置基类，用来设置通用数据
    /// </summary>
    public abstract class RenderPiplineSettingBase : ScriptableObject
    {
        [SerializeField]
        RenderSetting renderSetting = new RenderSetting
        {
            allowHDR = false,
            useDynamicBatching = true,
            useGPUInstancing = true,
            renderScale = 1.0f,
        };

        [SerializeField]
        protected ShadowSetting shadowSetting = new ShadowSetting();


    }
}