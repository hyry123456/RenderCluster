using UnityEngine;

namespace CustomRenderPipline.DefferPipeline
{
    public enum PostAntiAliasing
    {
        None = 0,
        FXAA = 1,
    }

    /// <summary>
    /// 后处理的「管线级」配置 —— 独立资源，挂在 DefferPipelineAsset 上。
    ///
    /// 为什么不塞进 DefferSetting：
    ///   · 后处理的参数会持续长（SMAA 质量档、超分、各效果的全局默认值、profiling 开关…），
    ///     塞在 DefferSetting 里会让那个类越来越臃肿，而且每次改动都要改管线资产的序列化布局；
    ///   · 独立成资产以后，可以给不同管线资产（Deffer / Hight Deffer / 以后的新管线）
    ///     各挂一份，互不影响；也可以直接复制一份做 A/B。
    ///
    /// 注意分工：
    ///   · 【这里】只放「管线/相机级」的东西 —— 它不随场景氛围变化，由程序决定；
    ///   · 「美术参数」（Bloom 阈值、暗角强度、tonemap 模式…）放 Volume，由场景/关卡决定。
    ///     见 PostVolumeSettings.cs 里的那些 VolumeComponent。
    /// </summary>
    [CreateAssetMenu(
        menuName = "Rendering/RenderAsset/Post Process Settings",
        fileName = "PostProcessSettings")]
    public class PostProcessSettings : ScriptableObject
    {
        [Header("相机开关")]
        [Tooltip("Game 视图启用后处理。")]
        public bool useInGame = true;

        [Tooltip("Scene 视图启用后处理。默认关 —— 摆场景时看图更清楚，Gizmo 也不会被打上暗角/颗粒。\n" +
                 "想直接看最终效果就勾上。")]
        public bool useInScene = false;

        [Header("抗锯齿")]
        [Tooltip("在色调映射之后的 LDR 图上做。")]
        public PostAntiAliasing antiAliasing = PostAntiAliasing.FXAA;

        // ------------------------------------------------------------------
        // 扩展位
        // ------------------------------------------------------------------
        // 以后要加的管线级参数都往这里放，不用动 DefferSetting：
        //   · SMAA 质量档 / T2x；
        //   · 超分（FSR 之类）与 renderScale 的联动；
        //   · 后处理是否在 Scene 的 Gizmo 之前还是之后做；
        //   · 各效果在「Volume 里没有该组件」时的全局默认值。
        // 只要在这里加 public 字段，Inspector 上会自动出现（独立资产，改它不会脏化管线资产）。
    }
}
