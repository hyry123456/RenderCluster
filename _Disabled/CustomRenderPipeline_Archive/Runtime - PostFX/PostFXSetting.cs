using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace CustomRenderPipline
{
	public enum BulkLightPass
	{
		StandardLight = 0,
		MyLight = 1,
	}
	/// <summary>	/// 体积光计算	/// </summary>
	[Serializable]
	public struct BulkLightSetting
	{
		public BulkLightPass pass;
		public bool useBulkLight;
		[Range(0, 1000f)]
		public float shrinkRadio;
		[Range(10, 100)]
		public int circleCount;
		public Texture noise;
		public bool drawDirectionLight;
		public bool drawOtherLight;
	}

	/// <summary>	/// Bloom参数设置	/// </summary>
	[System.Serializable]
	public struct BloomSettings
	{
		/// <summary>		/// 渐变等级		/// </summary>
		[Range(0f, 16f)]
		public int maxIterations;
		/// <summary>		/// Bloom进行到最小的像素，像素量小于该值就不进行下一步		/// </summary>
		[Min(1f)]
		public int downscaleLimit;
		/// <summary>		/// 是否使用三线性插值		/// </summary>
		public bool bicubicUpsampling;
		/// <summary>		/// Bloom的分割线		/// </summary>
		[Min(0f)]
		public float threshold;
		/// <summary>		/// 分割线下降的剧烈程度，不是直接截断		/// </summary>
		[Range(0f, 1f)]
		public float thresholdKnee;
		/// <summary>		/// Bloom叠加在主纹理的强度		/// </summary>
		[Min(0f)]
		public float intensity;
		/// <summary>		/// 是否使用范围颜色限制，即进行颜色限制时采样了周围的颜色		/// </summary>
		public bool fadeFireflies;
		/// <summary>		/// Bloom的混合模式，是添加还是lerp混合		/// </summary>
		public enum Mode { Additive, Scattering }
		public Mode mode;
		/// <summary>		/// lerp混合的比例		/// </summary>
		[Range(0.05f, 0.95f)]
		public float scatter;
	}

	/// <summary>	/// HDR映射方式	/// </summary>
	[Serializable]
	public struct ToneMappingSettings
	{

		public enum Mode { None, ACES, Neutral, Reinhard }

		public Mode mode;
	}

	/// <summary>	/// 颜色值调整	/// </summary>
	[Serializable]
	public struct ColorAdjustmentsSettings
	{
		/// <summary>		/// 曝光度		/// </summary>
		public float postExposure;

		/// <summary>		/// 对比度		/// </summary>
		[Range(-100f, 100f)]
		public float contrast;
		/// <summary>		/// 颜色过滤		/// </summary>
		[ColorUsage(false, true)]
		public Color colorFilter;

		/// <summary>		/// 色相转移		/// </summary>
		[Range(-180f, 180f)]
		public float hueShift;

		/// <summary>		/// 饱和度		/// </summary>
		[Range(-100f, 100f)]
		public float saturation;
	}

	[Serializable]
	public class AutoExposure
    {
		public bool useAutoExposure = false;
		public bool UseAutoExposure
		{
			get
			{
				if (useAutoExposure && exposureHistogram != null && autoExposure != null)
					return true;
				return false;
			}
		}

		//初始化以及存储曝光直方图用的shader
		public ComputeShader exposureHistogram;
		public ComputeShader autoExposure;

        [MinMax(1f, 99f), DisplayName("Filtering (%)"), 
			Tooltip("Filters the bright and dark parts of the histogram when computing the " +
			"average luminance. This is to avoid very dark pixels and very " +
			"bright pixels from contributing to the auto exposure. Unit is in percent.")]
		public Vector2 filtering = new Vector2(50f, 95f);
		[Range(-9f, 9f)]
        public float minLuminance = 0f;
		[Range(-9f, 9f)]
		public float maxLuminance = 0f;

        public float speedUp = 2f;
        public float speedDown = 1f;
		[Min(0f), DisplayName("Exposure Compensation")]
		public float exposureCompensation = 1f;
    }

    /// <summary>	/// 白平衡阐述控制	/// </summary>
    [Serializable]
	public struct WhiteBalanceSettings
	{

		/// <summary>		/// 色温以及色温强度		/// </summary>
		[Range(-100f, 100f)]
		public float temperature, tint;
	}


	/// <summary>	/// 色调分离	/// </summary>
	[Serializable]
	public struct SplitToningSettings
	{

		/// <summary>		/// 阴影颜色和高亮颜色		/// </summary>
		[ColorUsage(false)]
		public Color shadows, highlights;

		/// <summary>		/// 平衡度		/// </summary>
		[Range(-100f, 100f)]
		public float balance;
	}

	/// <summary>	/// 三重色调分离	/// </summary>
	[Serializable]
	public struct ShadowsMidtonesHighlightsSettings
	{

		[ColorUsage(false, true)]
		public Color shadows, midtones, highlights;

		[Range(0f, 2f)]
		public float shadowsStart, shadowsEnd, highlightsStart, highLightsEnd;
	}

	/// <summary>		/// 三种抗锯齿模式		/// </summary>
	public enum LuminanceMode { None, Green, Calculate }

	/// <summary>		/// 抗锯齿设置		/// </summary>
	[Serializable]
	public struct FXAASetting
	{
		public LuminanceMode luminanceMode;

		/// <summary>	/// 对比度阈值	/// </summary>
		[Range(0.0312f, 0.0833f)]
		public float contrastThreshold;
		[Range(0.063f, 0.333f)]
		public float relativeThreshold;     //对比度高度阈值，舍去高的部分
		[Range(0f, 1f)]
		public float subpixelBlending;      //模糊程度控制，调整细节显示比例
		/// <summary>			/// 高低质量控制			/// </summary>
		public bool lowQuality;

	}

	[Serializable]
	public struct CameraStickWater
	{
		[Range(0, 1)]
		public float rainAmount;        //水珠数量
		[Range(0, 1)]
		public float fixedDroplet;      //水珠拉伸程度
		[Range(0, 0.5f)]
		public float dropletSize;       //水珠大小
		[Range(0, 0.5f)]
		public float speed;             //移动速度
		public bool useStickWater;      //是否启用
	}

	[Serializable]
	public struct DepthOfFieldSetting
	{
		[Range(0.1f, 100f)]
		public float focusDistance;
		[Range(0.1f, 100f)]
		public float focusRange;
		[Range(2f, 10f)]
		public float bokehRadius;
		public bool useDepthOfField;
	}

	[Serializable]
	public struct DynamicBlur
	{
		public bool useDynamicBlur;
        [Range(0.00001f, 0.3f)]
		public float blurRadio;
    }


	[CreateAssetMenu(menuName = "Rendering/PostFX Setting")]
    public class PostFXSetting : ScriptableObject
	{
		Shader postFXShader;


		[SerializeField]
		BloomSettings bloom = new BloomSettings
		{
			scatter = 0.7f,
			maxIterations = 1,
			downscaleLimit = 30,
			threshold = 1,
			thresholdKnee = 0.2f,
			intensity = 0.5f,
		};
		public BloomSettings Bloom => bloom;


		public enum LUTSize
		{
			_16x = 16, _32x = 32, _64x = 64
		}
		[SerializeField]
		LUTSize colorLUTResolution = LUTSize._32x;
		public LUTSize LUTResolution => colorLUTResolution;
		[SerializeField]
		ToneMappingSettings toneMapping = default;
		public ToneMappingSettings ToneMapping => toneMapping;

		[SerializeField]
		ColorAdjustmentsSettings colorAdjustments = new ColorAdjustmentsSettings
		{
			colorFilter = Color.white
		};
		public ColorAdjustmentsSettings ColorAdjustments => colorAdjustments;

		[SerializeField]
		AutoExposure autoExposure = default;
        public AutoExposure AutoExposure => autoExposure;


        [SerializeField]
		WhiteBalanceSettings whiteBalance = default;

		public WhiteBalanceSettings WhiteBalance => whiteBalance;

		[SerializeField]
		SplitToningSettings splitToning = new SplitToningSettings
		{
			shadows = Color.gray,
			highlights = Color.gray
		};
		public SplitToningSettings SplitToning => splitToning;

		[SerializeField]
		ShadowsMidtonesHighlightsSettings shadowsMidtonesHighlights = 
			new ShadowsMidtonesHighlightsSettings
			{
				shadows = Color.white,
				midtones = Color.white,
				highlights = Color.white,
				shadowsEnd = 0.3f,
				highlightsStart = 0.55f,
				highLightsEnd = 1f
			};

		public ShadowsMidtonesHighlightsSettings ShadowsMidtonesHighlights =>
			shadowsMidtonesHighlights;

		[SerializeField]
		DynamicBlur dynamicBlur = new DynamicBlur
		{
			useDynamicBlur = false,
			blurRadio = 0.15f
		};
		public DynamicBlur DynamicBlur => dynamicBlur;


		[SerializeField]
		FXAASetting fXAA = new FXAASetting
		{
			contrastThreshold = 0.0312f,
			relativeThreshold = 0.063f,
			subpixelBlending = 1f
		};
		public FXAASetting FXAA => fXAA;

        [SerializeField]
		BulkLightSetting bulkLight = new BulkLightSetting
		{
			useBulkLight = false,
			shrinkRadio = 0.00005f,
			circleCount = 64,
		};
		public BulkLightSetting BulkLighting => bulkLight;

		[SerializeField]
		private DepthOfFieldSetting depthOfField = new DepthOfFieldSetting()
		{
			focusDistance = 10f,
			focusRange = 3f,
			bokehRadius = 4f,
			useDepthOfField = false,
		};

		public DepthOfFieldSetting DepthOfField => depthOfField;


		Material material;

		public Material Material
		{
			get
			{
				if (material == null)
				{
					if (postFXShader == null)
						postFXShader = Shader.Find("CustomRP/PostFX");
					material = new Material(postFXShader);
					material.hideFlags = HideFlags.HideAndDontSave;
				}
				return material;
			}
		}

    }
}