using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.DefferPipeline
{
    // ===========================================================================
    // 后处理的「参数层」：全部继承 VolumeComponent。
    //
    // 这是刻意照 URP 的分层来的 —— VolumeComponent 来自 com.unity.render-pipelines.core
    // （工程已装 14.0.12），和 URP 无关，任何自定义 SRP 都能直接用。于是白拿一整套：
    //   · Inspector 里每个参数都有 Override 勾选和滑条；
    //   · 一个 VolumeProfile 可以同时挂多个效果；
    //   · Global / Local Volume + blendDistance 按距离混合；
    //   · 多个 Volume 按 Priority 叠加。
    // 每个效果的开关就是它自己的 Active 复选框（和 URP 一致），所以不需要额外的 bool 参数。
    //
    // 用法：Hierarchy 里加一个空物体 → Add Component → Volume → 新建 Profile →
    //       Add Override → CustomRP → 挑效果。Global 勾上就是全局生效。
    // ===========================================================================

    public enum PostTonemappingMode
    {
        None = 0,
        Neutral = 1,
        ACES = 2,
    }

    /// <summary>枚举参数必须自己开一个子类：VolumeParameter&lt;T&gt; 的 (value, overrideState)
    /// 构造函数是 protected，不能直接 new（URP 的 TonemappingModeParameter 也是这么写的）。</summary>
    [System.Serializable]
    public class TonemappingModeParameter : VolumeParameter<PostTonemappingMode>
    {
        public TonemappingModeParameter(PostTonemappingMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    /// <summary>色调映射。**这是唯一默认就生效的效果** —— HDR 缓冲必须做映射，
    /// 否则超过 1 的部分会在最终 blit 被硬切掉（现在就是硬切的）。</summary>
    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Tonemapping")]
    public sealed class TonemappingSettings : VolumeComponent
    {
        [Tooltip("None = 不映射（保留原有的硬切观感，仅用于对比）\n" +
                 "Neutral = Khronos PBR Neutral，几乎不动暗部色彩，只压高光（推荐）\n" +
                 "ACES = 电影感，高光滚降更柔，但会吃一点饱和度")]
        public TonemappingModeParameter mode =
            new TonemappingModeParameter(PostTonemappingMode.Neutral, false);

        [Tooltip("曝光（EV）。0 = 不变，+1 = 亮一倍，-1 = 暗一半。在映射【之前】乘。")]
        public ClampedFloatParameter postExposure = new ClampedFloatParameter(0f, -5f, 5f, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Bloom")]
    public sealed class BloomSettings : VolumeComponent
    {
        [Tooltip("开始发光的亮度阈值。场景整体偏亮时调高，避免整屏发雾。")]
        public ClampedFloatParameter threshold = new ClampedFloatParameter(0.9f, 0f, 20f, false);

        [Tooltip("阈值软过渡宽度。0 = 硬切（亮部会突然一块开始发光），0.5 左右比较自然。")]
        public ClampedFloatParameter softKnee = new ClampedFloatParameter(0.5f, 0f, 1f, false);

        [Tooltip("发光强度。")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 20f, false);

        [Tooltip("散射。小 = 光晕紧贴高光；大 = 铺得很开（偏雾）。0.7 是 URP 的默认观感。")]
        public ClampedFloatParameter scatter = new ClampedFloatParameter(0.7f, 0f, 1f, false);

        [Tooltip("发光染色。一般白色；想模拟暖光灯可以偏黄。")]
        public ColorParameter tint = new ColorParameter(Color.white, false, false, true, false);

        [Tooltip("金字塔级数。越多越铺开、越贵；5 级在 1080p 下够用。")]
        public ClampedIntParameter mipCount = new ClampedIntParameter(5, 1, 6, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Color Adjustments")]
    public sealed class ColorAdjustmentsSettings : VolumeComponent
    {
        [Tooltip("对比度，[-100,100]。以 0.5 为支点，作用在色调映射【之后】的显示值上。")]
        public ClampedFloatParameter contrast = new ClampedFloatParameter(0f, -100f, 100f, false);

        [Tooltip("饱和度，[-100,100]。-100 = 黑白。")]
        public ClampedFloatParameter saturation = new ClampedFloatParameter(0f, -100f, 100f, false);

        [Tooltip("整体染色（乘法）。做暖调/冷调最省事的一招。")]
        public ColorParameter colorFilter = new ColorParameter(Color.white, false, false, true, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Vignette")]
    public sealed class VignetteSettings : VolumeComponent
    {
        [Tooltip("暗角颜色。一般纯黑；想做出租屋那种脏感可以用深褐。")]
        public ColorParameter color = new ColorParameter(Color.black, false, false, true, false);

        [Tooltip("暗角中心，屏幕空间的 0~1。默认 (0.5,0.5)。")]
        public Vector2Parameter center = new Vector2Parameter(new Vector2(0.5f, 0.5f), false);

        [Tooltip("暗角强度。0 = 关。")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f, false);

        [Tooltip("暗角过渡的柔和程度。越大边缘越糊、越自然。")]
        public ClampedFloatParameter smoothness = new ClampedFloatParameter(0.4f, 0.01f, 1f, false);

        [Tooltip("1 = 正圆（按宽高比补偿），0 = 跟随画面比例变成椭圆。")]
        public ClampedFloatParameter roundness = new ClampedFloatParameter(1f, 0f, 1f, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Film Grain")]
    public sealed class FilmGrainSettings : VolumeComponent
    {
        [Tooltip("颗粒强度。0.1~0.3 已经很明显，别开太大。")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f, false);

        [Tooltip("亮度响应。越大，越亮的区域颗粒越少（真实胶片的样子）。0 = 全屏均匀。")]
        public ClampedFloatParameter response = new ClampedFloatParameter(0.8f, 0f, 1f, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Chromatic Aberration")]
    public sealed class ChromaticAberrationSettings : VolumeComponent
    {
        [Tooltip("色差强度。沿「指向画面中心」的径向偏移，中心无偏移、边缘最强。")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f, false);
    }

    [System.Serializable]
    [VolumeComponentMenu("CustomRP/Lens Distortion")]
    public sealed class LensDistortionSettings : VolumeComponent
    {
        [Tooltip("畸变强度。>0 向外拉（广角/枕形），<0 向内收（桶形/鱼眼）。")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, -100f, 100f, false);

        [Tooltip("强度换算系数。同样的 intensity 下，scale 越大畸变越小。")]
        public ClampedFloatParameter scale = new ClampedFloatParameter(1f, 0.01f, 5f, false);
    }

    // 管线级开关（useInGame / useInScene / antiAliasing）已经挪到独立资源
    // PostProcessSettings.cs —— 那是"挂到管线资产上"的配置，不属于 Volume，也不属于 DefferSetting。
    // 这里只留"随场景氛围变化"的美术参数。
}
