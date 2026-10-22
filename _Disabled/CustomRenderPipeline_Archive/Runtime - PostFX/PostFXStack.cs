using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
//using static DefferedRender.PostFXSetting;

namespace CustomRenderPipline
{
    class PostFXProverty
    {
        #region Bloom
        public static int
            BloomThresholdId = Shader.PropertyToID("_BloomThreshold"),
            BloomBicubicUpsamplingId = Shader.PropertyToID("_BloomBicubicUpsampling"),
            FxSource2Id = Shader.PropertyToID("_SourceTexture2"),
            FxSource3Id = Shader.PropertyToID("_SourceTexture3"),
            BloomIntensityId = Shader.PropertyToID("_BloomIntensity"),
            BloomResultId = Shader.PropertyToID("_BloomResult"),
            BloomPrefilterId = Shader.PropertyToID("_BloomPrefilter");

        public static int BloomPyramidId;

        #endregion

        #region LUT
        public static int
            ColorGradingLUTParametersId = Shader.PropertyToID("_ColorGradingLUTParameters"),
            ColorGradingLUTInLogId = Shader.PropertyToID("_ColorGradingLUTInLogC"),
            ColorAdjustmentsId = Shader.PropertyToID("_ColorAdjustments"),
            ColorFilterId = Shader.PropertyToID("_ColorFilter"),
            WhiteBalanceId = Shader.PropertyToID("_WhiteBalance"),
            SplitToningShadowsId = Shader.PropertyToID("_SplitToningShadows"),
            SplitToningHighlightsId = Shader.PropertyToID("_SplitToningHighlights"),
            SmhShadowsId = Shader.PropertyToID("_SMHShadows"),
            SmhMidtonesId = Shader.PropertyToID("_SMHMidtones"),
            SmhHighlightsId = Shader.PropertyToID("_SMHHighlights"),
            SmhRangeId = Shader.PropertyToID("_SMHRange"),
            AutoExpourseTexId = Shader.PropertyToID("_AutoExpourseTex"),

            ColorGradingLUTId = Shader.PropertyToID("_ColorGradingLUT");
        #endregion

        #region FXAA
        public static int
            FxaaTempTexId = Shader.PropertyToID("_FXAATempTexture"),
            ContrastThresholdId = Shader.PropertyToID("_ContrastThreshold"),
            RelativeThresholdId = Shader.PropertyToID("_RelativeThreshold"),
            SubpixelBlending = Shader.PropertyToID("_SubpixelBlending");

        #endregion

        #region Bulk Light

        public static int
           BulkLightSampleCountId = Shader.PropertyToID("_BulkSampleCount"),
           BulkLightShrinkRadioId = Shader.PropertyToID("_BulkLightShrinkRadio");


        #endregion

        #region DepthOfFile
        public static int
            BokehRadiusId = Shader.PropertyToID("_BokehRadius"),
            FocusDistance = Shader.PropertyToID("_FocusDistance"),
            HalfTempRT2Id = Shader.PropertyToID("_HalfTempRT2"),
            TempRT2Id = Shader.PropertyToID("_TempRenderTexture2"),
            FocusRange = Shader.PropertyToID("_FocusRange");

        #endregion

        public static int 
            CameraDepthId = Shader.PropertyToID("_RenderCameraDepth"),
            HalfTempRTId = Shader.PropertyToID("_HalfTempRenderTexture"),
            PreviousViewProjectionMatrixId = Shader.PropertyToID("_PreviousViewProjectionMatrix"),
            BlurRadioId = Shader.PropertyToID("_BlurRadio"),
            TempRTId = Shader.PropertyToID("_TempRenderTexture");
    }


    public partial class PostFXStack 
    {
        enum PostFXPass
        {
            BloomAdd = 0,
            BlurHorizontal = 1,
            BlurVertical = 2,
            BloomPrefilter = 3,
            BloomPrefilterFireflies = 4,
            BloomScatter = 5,
            BloomScatterFinal = 6,

            CGNone = 7,
            CGACES = 8,
            CGNeutral = 9,
            CGReinhard = 10,
            Final = 11,

            CaculateGray = 12,
            FXAA = 13,

            BlendBulkLight = 14,

            CircleOfConfusion = 15,
            PreFilter = 16,
            Bokeh = 17,
            PostFilter = 18,
            Combine = 19,

            DymaicBlur = 20,

            AutoExprosure = 21,
        }

        const string bufferName = "PostFX";

        const int maxBloomPyramidLevels = 16;


		CommandBuffer buffer = new CommandBuffer
        {
            name = bufferName
        };

        ScriptableRenderContext context;

        PostFXSetting settings;
		bool useHDR;
		public bool IsActive => settings != null;
		int width, height, antiAiasing;
		Camera camera;
        int eyeIndex;
        Material material;

		public PostFXStack()
        {
            PostFXProverty.BloomPyramidId = Shader.PropertyToID("_BloomPyramid0");
			for (int i = 1; i < maxBloomPyramidLevels * 2; i++)
            {
                Shader.PropertyToID("_BloomPyramid" + i);
            }

            for (int eye = 0; eye < k_NumEyes; eye++)
            {
                m_AutoExposurePool[eye] = new RenderTexture[k_NumAutoExposureTextures];
                m_AutoExposurePingPong[eye] = 0;
            }
        }

        /// <summary>	/// 准备后处理前的数据准备	/// </summary>
        public void Setup(
            ScriptableRenderContext context, PostFXSetting settings,
            bool useHDR, int width, int height,	Camera camera, int antiAiasing
        )
        {
            this.useHDR = useHDR;
            this.context = context;
            this.settings = settings;
			this.width = width;
			this.height = height;
			this.camera = camera;
            this.antiAiasing = antiAiasing;

            eyeIndex = camera.cameraType == CameraType.Game ? 0 : 1;
        }

        public void Dispose()
        {
            if(exposureBuffer != null) exposureBuffer.Dispose();
            foreach (var rtEyeSet in m_AutoExposurePool)
            {
                foreach (var rt in rtEyeSet)
                    rt?.Release();
            }
        }

		/// <summary>
		/// 对传入的纹理进行后处理
		/// </summary>
		/// <param name="sourceId">影响的纹理</param>
		public void Render(int sourceId, int depthId)
        {
            material = settings.Material;
            RenderTextureFormat format = useHDR ? RenderTextureFormat.DefaultHDR 
                : RenderTextureFormat.Default;
            buffer.GetTemporaryRT(PostFXProverty.TempRTId, width,
                height, 0, FilterMode.Bilinear, format,
                RenderTextureReadWrite.Default, antiAiasing, false);

            buffer.GetTemporaryRT(PostFXProverty.HalfTempRTId,
                width / 2, height / 2, 0, FilterMode.Bilinear, format,
                RenderTextureReadWrite.Default, antiAiasing);

            buffer.SetGlobalTexture(PostFXProverty.CameraDepthId, depthId);

            if(settings.DynamicBlur.useDynamicBlur)
                DynamicBlur(sourceId);

            if (settings.BulkLighting.useBulkLight && BulkLight.UseBulkLight)
            {
                DrawBulkLight(sourceId, depthId);
            }

            if (UseExpostion)
            {
                AutoExpostion(sourceId);
            }

            if (settings.DepthOfField.useDepthOfField)
            {
                //景深需要多2张RT
                buffer.GetTemporaryRT(PostFXProverty.HalfTempRT2Id,
                    width / 2, height / 2, 0, FilterMode.Bilinear, format,
                    RenderTextureReadWrite.Default, antiAiasing);
                buffer.GetTemporaryRT(PostFXProverty.TempRT2Id,
                    width, height, 0, FilterMode.Bilinear, format,
                    RenderTextureReadWrite.Default, antiAiasing);
                DepthOfField(sourceId, depthId);
                buffer.ReleaseTemporaryRT(PostFXProverty.HalfTempRT2Id);
                buffer.ReleaseTemporaryRT(PostFXProverty.TempRT2Id);
            }

            if (DoBloom(sourceId))
			{
                DoColorGradingAndToneMapping(PostFXProverty.BloomResultId);
                buffer.ReleaseTemporaryRT(PostFXProverty.BloomResultId);
            }
			else
			{
                DoColorGradingAndToneMapping(sourceId);
            }

            buffer.ReleaseTemporaryRT(PostFXProverty.TempRTId);
            buffer.ReleaseTemporaryRT(PostFXProverty.HalfTempRTId);

            context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}

		/// <summary>	/// 进行Bloom操作	/// </summary>
		/// <returns>是否成功进行Bloom</returns>
		bool DoBloom(int sourceId)
		{

            BloomSettings bloom = settings.Bloom;
            int width = this.width / 2, height = this.height / 2;
            if (
                bloom.maxIterations == 0 || bloom.intensity <= 0f ||
                height < bloom.downscaleLimit * 2 || width < bloom.downscaleLimit * 2
            )
            {
                return false;
            }

            buffer.BeginSample("Bloom");
            Vector4 threshold;
            threshold.x = Mathf.GammaToLinearSpace(bloom.threshold);
            threshold.y = threshold.x * bloom.thresholdKnee;
            threshold.z = 2f * threshold.y;
            threshold.w = 0.25f / (threshold.y + 0.00001f);
            threshold.y -= threshold.x;
            buffer.SetGlobalVector(PostFXProverty.BloomThresholdId, threshold);        //Bloom参数准备

            RenderTextureFormat format = useHDR ?
                RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            buffer.GetTemporaryRT(
                PostFXProverty.BloomPrefilterId, width, height, 0, FilterMode.Bilinear, format
            );



            Draw(
                sourceId, PostFXProverty.BloomPrefilterId, bloom.fadeFireflies ?
                    PostFXPass.BloomPrefilterFireflies : PostFXPass.BloomPrefilter
            );
            width /= 2;
            height /= 2;

            int fromId = PostFXProverty.BloomPrefilterId, toId = PostFXProverty.BloomPyramidId + 1;
            int i;
            //逐个循环，进行逐级高斯模糊
            for (i = 0; i < bloom.maxIterations; i++)
            {

                if (height < bloom.downscaleLimit || width < bloom.downscaleLimit)
                {
                    break;
                }
                int midId = toId - 1;
                buffer.GetTemporaryRT(
                    midId, width, height, 0, FilterMode.Bilinear, format
                );
                buffer.GetTemporaryRT(
                    toId, width, height, 0, FilterMode.Bilinear, format
                );
                //水平以及垂直高斯模糊
                Draw(fromId, midId, PostFXPass.BlurHorizontal);
                Draw(midId, toId, PostFXPass.BlurVertical);
                //模糊后减少纹理像素大小
                fromId = toId;
                toId += 2;
                width /= 2;
                height /= 2;
            }

            buffer.ReleaseTemporaryRT(PostFXProverty.BloomPrefilterId);
            buffer.SetGlobalFloat(
                PostFXProverty.BloomBicubicUpsamplingId, bloom.bicubicUpsampling ? 1f : 0f
            );

            //控制纹理的混合模式，是使用渐变混合还是直接相加
            PostFXPass combinePass, finalPass;
            float finalIntensity;
            if (bloom.mode == BloomSettings.Mode.Additive)
            {
                combinePass = finalPass = PostFXPass.BloomAdd;
                buffer.SetGlobalFloat(PostFXProverty.BloomIntensityId, 1f);
                finalIntensity = bloom.intensity;
            }
            else
            {
                combinePass = PostFXPass.BloomScatter;
                finalPass = PostFXPass.BloomScatterFinal;
                buffer.SetGlobalFloat(PostFXProverty.BloomIntensityId, bloom.scatter);
                finalIntensity = Mathf.Min(bloom.intensity, 1f);
            }

            //循环混合到最上级纹理上
            if (i > 1)
            {
                buffer.ReleaseTemporaryRT(fromId - 1);
                toId -= 5;
                for (i -= 1; i > 0; i--)
                {
                    buffer.SetGlobalTexture(PostFXProverty.FxSource2Id, toId + 1);
                    Draw(fromId, toId, combinePass);
                    buffer.ReleaseTemporaryRT(fromId);
                    buffer.ReleaseTemporaryRT(toId + 1);
                    fromId = toId;
                    toId -= 2;
                }
            }
            else
            {
                buffer.ReleaseTemporaryRT(PostFXProverty.BloomPyramidId);
            }
            buffer.SetGlobalFloat(PostFXProverty.BloomIntensityId, finalIntensity);
            buffer.SetGlobalTexture(PostFXProverty.FxSource2Id, sourceId);
            buffer.GetTemporaryRT(
                PostFXProverty.BloomResultId, this.width, this.height, 0,
                FilterMode.Bilinear, format
            );
            //绘制到目标纹理上
            Draw(fromId, PostFXProverty.BloomResultId, finalPass);
            buffer.ReleaseTemporaryRT(fromId);
            buffer.EndSample("Bloom");
            return true;
        }

        /// <summary>	/// 进行色彩分级以及渲染到摄像机上	/// </summary>
        /// <param name="sourceId">源纹理</param>
        void DoColorGradingAndToneMapping(int sourceId)
        {
            ConfigureColorAdjustments();
            ConfigureWhiteBalance();
            ConfigureSplitToning();
            ConfigureShadowsMidtonesHighlights();


            int lutHeight = (int)settings.LUTResolution;
            int lutWidth = lutHeight * lutHeight;
            buffer.GetTemporaryRT(
                PostFXProverty.ColorGradingLUTId, lutWidth, lutHeight, 0,
                FilterMode.Bilinear, RenderTextureFormat.DefaultHDR
            );
            buffer.SetGlobalVector(PostFXProverty.ColorGradingLUTParametersId, new Vector4(
                lutHeight, 0.5f / lutWidth, 0.5f / lutHeight, lutHeight / (lutHeight - 1f)
            ));

            ToneMappingSettings.Mode mode = (useHDR)? settings.ToneMapping.mode : 0;
            PostFXPass pass = PostFXPass.CGNone + (int)mode;
            buffer.SetGlobalFloat(
                PostFXProverty.ColorGradingLUTInLogId, useHDR && 
                pass != PostFXPass.CGNone ? 1f : 0f
            );

            //绘制查找集纹理
            Draw(sourceId, PostFXProverty.ColorGradingLUTId, pass);

            buffer.SetGlobalVector(PostFXProverty.ColorGradingLUTParametersId,
                new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f)
            );

            //DrawFinal(sourceId);


            if (settings.FXAA.luminanceMode == LuminanceMode.None)
            {
                //绘制最终图像
                DrawFinal(sourceId);
            }
            else
            {
                Draw(sourceId, PostFXProverty.TempRTId, PostFXPass.Final);
                DrawFXAAInFinal(PostFXProverty.TempRTId, sourceId);
            }
            buffer.ReleaseTemporaryRT(PostFXProverty.ColorGradingLUTId);
        }

        /// <summary>	/// 颜色调整参数赋值	/// </summary>
        void ConfigureColorAdjustments()
        {
            ColorAdjustmentsSettings colorAdjustments = settings.ColorAdjustments;
            buffer.SetGlobalVector(PostFXProverty.ColorAdjustmentsId, new Vector4(
                Mathf.Pow(2f, colorAdjustments.postExposure),
                colorAdjustments.contrast * 0.01f + 1f,
                colorAdjustments.hueShift * (1f / 360f),
                colorAdjustments.saturation * 0.01f + 1f
            ));
            buffer.SetGlobalColor(PostFXProverty.ColorFilterId, colorAdjustments.colorFilter.linear);
        }

        /// <summary>	/// 白平衡	/// </summary>
        void ConfigureWhiteBalance()
        {
            WhiteBalanceSettings whiteBalance = settings.WhiteBalance;
            buffer.SetGlobalVector(PostFXProverty.WhiteBalanceId, ColorUtils.ColorBalanceToLMSCoeffs(
                whiteBalance.temperature, whiteBalance.tint
            ));
        }

        /// <summary>	/// 色调分离	/// </summary>
        void ConfigureSplitToning()
        {
            SplitToningSettings splitToning = settings.SplitToning;
            Color splitColor = splitToning.shadows;
            splitColor.a = splitToning.balance * 0.01f;
            buffer.SetGlobalColor(PostFXProverty.SplitToningShadowsId, splitColor);
            buffer.SetGlobalColor(PostFXProverty.SplitToningHighlightsId, splitToning.highlights);
        }

        /// <summary>	/// 三重阴影映射	/// </summary>
        void ConfigureShadowsMidtonesHighlights()
        {
            ShadowsMidtonesHighlightsSettings smh = settings.ShadowsMidtonesHighlights;
            buffer.SetGlobalColor(PostFXProverty.SmhShadowsId, smh.shadows.linear);
            buffer.SetGlobalColor(PostFXProverty.SmhMidtonesId, smh.midtones.linear);
            buffer.SetGlobalColor(PostFXProverty.SmhHighlightsId, smh.highlights.linear);
            buffer.SetGlobalVector(PostFXProverty.SmhRangeId, new Vector4(
                smh.shadowsStart, smh.shadowsEnd, smh.highlightsStart, smh.highLightsEnd
            ));
        }

        #region Auto Exposure

        public const int rangeMin = -9; // ev
        public const int rangeMax = 9; // ev

        // Don't forget to update 'ExposureHistogram.hlsl' if you change these values !
        const int k_Bins = 128;

        public ComputeBuffer exposureBuffer { get; private set; }

        const int k_NumEyes = 2;
        const int k_NumAutoExposureTextures = 2;

        readonly RenderTexture[][] m_AutoExposurePool = new RenderTexture[k_NumEyes][];
        int[] m_AutoExposurePingPong = new int[k_NumEyes];
        RenderTexture m_CurrentAutoExposure;

        private bool UseExpostion
        {
            get
            {
                return settings.AutoExposure.useAutoExposure;
            }
        }

        void CheckTexture(int eye, int id)
        {
            if (m_AutoExposurePool[eye][id] == null || !m_AutoExposurePool[eye][id].IsCreated())
            {
                m_AutoExposurePool[eye][id] = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
                m_AutoExposurePool[eye][id].Create();
            }
        }

        /// <summary>        /// 自动曝光        /// </summary>
        void AutoExpostion(int source)
        {
            if (exposureBuffer == null)
                exposureBuffer = new ComputeBuffer(k_Bins, sizeof(uint));

            buffer.BeginSample("AutoExpostion");

            uint threadX, threadY, threadZ;
            var scaleOffsetRes = GetHistogramScaleOffsetRes();
            var compute = settings.AutoExposure.exposureHistogram;
            //var cmd = context.command;
            buffer.BeginSample("LogHistogram");

            // Clear the buffer on every frame as we use it to accumulate luminance values on each frame
            int kernel = compute.FindKernel("KEyeHistogramClear");
            buffer.SetComputeBufferParam(compute, kernel, "_HistogramBuffer", exposureBuffer);
            compute.GetKernelThreadGroupSizes(kernel, out threadX, out threadY, out threadZ);
            buffer.DispatchCompute(compute, kernel, Mathf.CeilToInt(k_Bins / (float)threadX), 1, 1);

            // Get a log histogram
            kernel = compute.FindKernel("KEyeHistogram");
            buffer.SetComputeBufferParam(compute, kernel, "_HistogramBuffer", exposureBuffer);
            buffer.SetComputeTextureParam(compute, kernel, "_Source", source);
            buffer.SetComputeVectorParam(compute, "_ScaleOffsetRes", scaleOffsetRes);

            compute.GetKernelThreadGroupSizes(kernel, out threadX, out threadY, out threadZ);
            buffer.DispatchCompute(compute, kernel,
                Mathf.CeilToInt(scaleOffsetRes.z / 2f / threadX),
                Mathf.CeilToInt(scaleOffsetRes.w / 2f / threadY),
                1
            );

            buffer.EndSample("LogHistogram");


            buffer.BeginSample("AutoExposureLookup");
            var setting = settings.AutoExposure;
            compute = setting.autoExposure;

            // Prepare autoExpo texture pool
            CheckTexture(eyeIndex, 0);
            CheckTexture(eyeIndex, 1);


            float lowPercent = setting.filtering.x;
            float highPercent = setting.filtering.y;
            const float kMinDelta = 1e-2f;
            highPercent = Mathf.Clamp(highPercent, 1f + kMinDelta, 99f);
            lowPercent = Mathf.Clamp(lowPercent, 1f, highPercent - kMinDelta);

            float minLum = setting.minLuminance;
            float maxLum = setting.maxLuminance;
            setting.minLuminance = Mathf.Min(minLum, maxLum);
            setting.maxLuminance = Mathf.Max(minLum, maxLum);

            string adaptation = null;

            adaptation = "KAutoExposureAvgLuminance_progressive";

            kernel = compute.FindKernel(adaptation);
            buffer.SetComputeBufferParam(compute, kernel, "_HistogramBuffer", exposureBuffer);
            buffer.SetComputeVectorParam(compute, "_Params1", new Vector4(lowPercent * 0.01f, 
                highPercent * 0.01f, RuntimeUtilities.Exp2(setting.minLuminance), 
                RuntimeUtilities.Exp2(setting.maxLuminance)));
            buffer.SetComputeVectorParam(compute, "_Params2", new Vector4(setting.speedDown, 
                setting.speedUp, setting.exposureCompensation, Time.deltaTime));
            buffer.SetComputeVectorParam(compute, "_ScaleOffsetRes", scaleOffsetRes);

            int pp = m_AutoExposurePingPong[eyeIndex];
            var src = m_AutoExposurePool[eyeIndex][++pp % 2];
            var dst = m_AutoExposurePool[eyeIndex][++pp % 2];

            buffer.SetComputeTextureParam(compute, kernel, "_Source", src);
            buffer.SetComputeTextureParam(compute, kernel, "_Destination", dst);
            buffer.DispatchCompute(compute, kernel, 1, 1, 1);

            m_AutoExposurePingPong[eyeIndex] = ++pp % 2;
            m_CurrentAutoExposure = dst;
            buffer.SetGlobalTexture(PostFXProverty.AutoExpourseTexId, dst);

            //Draw(source, PostFXProverty.TempRTId, PostFXPass.co)
            buffer.CopyTexture(source, PostFXProverty.TempRTId);
            Draw(PostFXProverty.TempRTId, source, PostFXPass.AutoExprosure);

            buffer.EndSample("AutoExposureLookup");
            buffer.EndSample("AutoExpostion");
        }

        public Vector4 GetHistogramScaleOffsetRes()
        {
            float diff = rangeMax - rangeMin;
            float scale = 1f / diff;
            float offset = -rangeMin * scale;
            return new Vector4(scale, offset, width, height);
        }


        #endregion

        void DrawFinal(RenderTargetIdentifier from)
        {
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, from);

            RenderTargetIdentifier target = camera.targetTexture == null ?
                BuiltinRenderTextureType.CameraTarget
                : (RenderTargetIdentifier)camera.targetTexture;
            buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.Store);
            buffer.SetViewport(camera.pixelRect);
            buffer.DrawProcedural(Matrix4x4.identity,
                settings.Material, (int)PostFXPass.Final, MeshTopology.Triangles, 3);
        }

        Dictionary<Camera, Matrix4x4> preVPs = new Dictionary<Camera, Matrix4x4>();

        /// <summary>  /// 运动模糊   /// </summary>
        void DynamicBlur(int source)
        {
            buffer.SetGlobalFloat(PostFXProverty.BlurRadioId, settings.DynamicBlur.blurRadio);
            Matrix4x4 preVP;
            if(preVPs.TryGetValue(camera, out preVP))
            {
                buffer.SetGlobalMatrix(PostFXProverty.PreviousViewProjectionMatrixId,
                    preVP);
            }
            else
            {
                preVPs.Add(camera, camera.projectionMatrix * camera.worldToCameraMatrix);
                return;
            }
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, source);
            buffer.Blit(source, PostFXProverty.TempRTId, material, (int)PostFXPass.DymaicBlur);
            buffer.Blit(PostFXProverty.TempRTId, source);

            preVPs[camera] = camera.projectionMatrix * camera.worldToCameraMatrix;
        }

        /// <summary>		/// 对输出到最终图像的位置进行抗锯齿		/// </summary>
        public void DrawFXAAInFinal(int soure, int source2)
        {
            buffer.BeginSample("FXAA");
            FXAASetting fXAA = settings.FXAA;

            buffer.SetGlobalFloat(PostFXProverty.ContrastThresholdId, fXAA.contrastThreshold);
            buffer.SetGlobalFloat(PostFXProverty.RelativeThresholdId, fXAA.relativeThreshold);
            buffer.SetGlobalFloat(PostFXProverty.SubpixelBlending, fXAA.subpixelBlending);


            if (fXAA.lowQuality)
                buffer.EnableShaderKeyword("LOW_QUALITY");
            else
                buffer.DisableShaderKeyword("LOW_QUALITY");

            RenderTargetIdentifier target = camera.targetTexture == null ?
                BuiltinRenderTextureType.CameraTarget
                : (RenderTargetIdentifier)camera.targetTexture;

            if (fXAA.luminanceMode == LuminanceMode.Green)
            {
                buffer.DisableShaderKeyword("LUMINANCE_GREEN");
                buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, soure);
                buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.Store);
                buffer.SetViewport(camera.pixelRect);
                buffer.DrawProcedural(Matrix4x4.identity,
                    settings.Material, (int)PostFXPass.FXAA, MeshTopology.Triangles, 3);
            }
            else
            {
                buffer.EnableShaderKeyword("LUMINANCE_GREEN");
                Draw(soure, source2, PostFXPass.CaculateGray);

                buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, source2);
                buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.Store);
                buffer.SetViewport(camera.pixelRect);
                buffer.DrawProcedural(Matrix4x4.identity,
                    settings.Material, (int)PostFXPass.FXAA, MeshTopology.Triangles, 3);
            }
            buffer.EndSample("FXAA");
        }

        /// <summary>		/// 渲染体积光		/// </summary>
        public void DrawBulkLight(int source, int depthId)
        {
            buffer.BeginSample("BulkLight");

            buffer.SetGlobalFloat(PostFXProverty.BulkLightShrinkRadioId, settings.BulkLighting.shrinkRadio * 0.01f);
            buffer.SetGlobalInt(PostFXProverty.BulkLightSampleCountId, settings.BulkLighting.circleCount);
            buffer.SetGlobalTexture("_Noise", settings.BulkLighting.noise);
            if (settings.BulkLighting.drawDirectionLight)
                buffer.EnableShaderKeyword("_DRAW_DIRECTION_LIGHT");
            else buffer.DisableShaderKeyword("_DRAW_DIRECTION_LIGHT");

            if (settings.BulkLighting.drawOtherLight)
                buffer.EnableShaderKeyword("_DRAW_OTHER_LIGHT");
            else buffer.DisableShaderKeyword("_DRAW_OTHER_LIGHT");

            //RenderTextureFormat format = (useHDR) ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            //int width = this.width / 2, height = this.height / 2;

            buffer.SetRenderTarget(
               PostFXProverty.HalfTempRTId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
               PostFXProverty.HalfTempRTId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            buffer.ClearRenderTarget(false, true, Color.clear);


            BulkLight.Instance.DrawBulkLight(buffer, (int)settings.BulkLighting.pass);

            buffer.SetGlobalTexture(PostFXProverty.FxSource2Id,
                PostFXProverty.HalfTempRTId); //模糊后的强度图

            //buffer.GetTemporaryRT(bulkLightTemp2TexId, this.width, this.height,
            //	0, FilterMode.Bilinear, format);
            //buffer.GetTemporaryRT(bulkLightTempTexId, this.width, this.height,
            //    0, FilterMode.Bilinear, format);

            Draw(source, PostFXProverty.TempRTId, PostFXPass.BlendBulkLight);         //最终目标纹理
            //Draw(PostFXProverty.TempRTId, source, PostFXPass.);         //拷贝到目标纹理原纹理
            buffer.Blit(PostFXProverty.TempRTId, source);

            //buffer.ReleaseTemporaryRT(bulkLightTemp2TexId);
            //buffer.ReleaseTemporaryRT(bulkLightTempTexId);
            //buffer.ReleaseTemporaryRT(bulkLightTargetTexId);

            //buffer.SetGlobalVector(bufferSizeId, new Vector4(
            //    1f / this.width, 1f / this.height, this.width, this.height));
            buffer.EndSample("BulkLight");
            ExecuteBuffer();
        }

        void DepthOfField(int sourceId, int depthId)
        {
            buffer.BeginSample("DepthOfField");
            DepthOfFieldSetting depthOfField = settings.DepthOfField;
            buffer.SetGlobalFloat(PostFXProverty.BokehRadiusId, depthOfField.bokehRadius);
            buffer.SetGlobalFloat(PostFXProverty.FocusDistance, depthOfField.focusDistance);
            buffer.SetGlobalFloat(PostFXProverty.FocusRange, depthOfField.focusRange);
            //buffer.GetTemporaryRT(bulkLightTempTexId, width, height, 0,
            //    FilterMode.Bilinear, RenderTextureFormat.RHalf);

            //buffer.GetTemporaryRT(bulkLightTemp2TexId, widthT, heightT, 0,
            //    FilterMode.Bilinear, format);
            //buffer.GetTemporaryRT(fxaaTempTexId, widthT, heightT, 0,
            //    FilterMode.Bilinear, format);

            //buffer.GetTemporaryRT(finalTempTexId, width, height, 0,
            //    FilterMode.Bilinear, format);

            //buffer.SetGlobalTexture(fxSource3Id, bulkLightTemp2TexId);

            //Draw(sourceId, bulkLightTempTexId, Pass.CircleOfConfusion);
            //Draw(sourceId, bulkLightTemp2TexId, Pass.PreFilter);
            //Draw(bulkLightTemp2TexId, fxaaTempTexId, Pass.Bokeh);
            //Draw(fxaaTempTexId, bulkLightTemp2TexId, Pass.PostFilter);

            Draw(depthId, PostFXProverty.TempRTId, PostFXPass.CircleOfConfusion);
            buffer.SetGlobalTexture(PostFXProverty.FxSource2Id, PostFXProverty.TempRTId);

            Draw(sourceId, PostFXProverty.HalfTempRTId, PostFXPass.PreFilter);

            Draw(PostFXProverty.HalfTempRTId, PostFXProverty.HalfTempRT2Id, PostFXPass.Bokeh);
            Draw(PostFXProverty.HalfTempRT2Id, PostFXProverty.HalfTempRTId, PostFXPass.PostFilter);
            buffer.SetGlobalTexture(PostFXProverty.FxSource3Id, PostFXProverty.HalfTempRTId);

            Draw(sourceId, PostFXProverty.TempRT2Id, PostFXPass.Combine);
            //Draw(PostFXProverty.TempRT2Id, sourceId, PostFXPass.);
            buffer.Blit(PostFXProverty.TempRT2Id, sourceId);

            //buffer.ReleaseTemporaryRT(bulkLightTempTexId);
            //buffer.ReleaseTemporaryRT(bulkLightTemp2TexId);
            //buffer.ReleaseTemporaryRT(fxaaTempTexId);
            //buffer.ReleaseTemporaryRT(finalTempTexId);
            buffer.EndSample("DepthOfField");
        }


        void ExecuteBuffer()
        {
			context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}

        /// <summary>        /// 进行纹理绘制        /// </summary>
        /// <param name="from">根据纹理</param>
        /// <param name="to">目标纹理</param>
        void Draw(
            RenderTargetIdentifier from, RenderTargetIdentifier to, PostFXPass pass
        )
        {
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, from);

            buffer.SetRenderTarget(to, RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.DontCare);

            buffer.DrawProcedural(
                Matrix4x4.identity, material, (int)pass,
                MeshTopology.Triangles, 3);
        }
    }
}