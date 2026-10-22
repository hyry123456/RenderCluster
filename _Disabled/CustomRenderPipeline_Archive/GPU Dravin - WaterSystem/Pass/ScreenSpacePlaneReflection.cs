using CustomRenderPipline;
using UnityEngine;
using UnityEngine.Rendering;
using static KWS.KWS_ShaderConstants;

namespace KWS
{
    public class ScreenSpacePlaneReflection
    {
        readonly PassData _currentPassData = new PassData();
        readonly PassData _currentPassDataEditor = new PassData();
        private const string PASS_NAME = "Screen Space Plane Reflection";

        private Material _filteringMaterial;
        private ComputeShader _cs;

        int _kernelClear;
        int _kernelRenderHash;
        int _kernelRenderColorFromHash;

        const int shaderNumthreadX = 8;
        const int shaderNumthreadY = 8;

        class PassData
        {
            //public RenderTexture[] rts = new RenderTexture[2];
            public RenderTexture rt;
            public ComputeBuffer HashBuffer;
            public int Frame = 0;

            public Matrix4x4 PrevVPMatrix;
            public Matrix4x4[] PrevVPMatrixStereo = new Matrix4x4[2];

            public Vector2Int GetCurrentResolution()
            {
                return new Vector2Int(rt.width, rt.height);
            }

            Vector2Int ScaleFunc(Vector2Int size, WaterSystemSetting setting)
            {
                float scale = (int)setting.ScreenSpaceReflectionResolutionQuality / 100f;
                return new Vector2Int(Mathf.RoundToInt(scale * size.x), Mathf.RoundToInt(scale * size.y));
            }

            public void CheckSize(Vector2Int size, WaterSystemSetting setting)
            {
                Vector2Int target = ScaleFunc(size, setting);
                if(GetCurrentResolution() != target)
                {
                    ReInitializeTextures(size, setting);
                }
            }

            public void ReInitializeTextures(Vector2Int size, WaterSystemSetting setting)
            {
                Release();
                size = ScaleFunc(size, setting);

                //for (int idx = 0; idx < 2; idx++)
                //{
                //    RenderTexture rt = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB64);
                //    rt.enableRandomWrite = true;
                //    rt.Create();
                //    rts[idx] = rt;
                //}
                rt = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB64);
                rt.enableRandomWrite = true;
                rt.name = "Screen Space Plane Reflection";
                rt.Create();

                //if (waterInstance.Settings.UseAnisotropicReflections)
                //{
                //    for (int idx = 0; idx < 2; idx++) ReflectionRT[idx] = KWS_CoreUtils.RTHandleAllocVR(ScaleFunc, name: "_reflectionRT" + idx, colorFormat: colorFormat, enableRandomWrite: true);
                //    ReflectionRTFilteredMip = KWS_CoreUtils.RTHandleAllocVR(ScaleFunc, name: "_reflectionRT_Mip", colorFormat: colorFormat, useMipMap: true, autoGenerateMips: true, mipMapCount: 5);
                //}
                //else
                //{
                //    for (int idx = 0; idx < 2; idx++)
                //        ReflectionRT[idx] = KWS_CoreUtils.RTHandleAllocVR(ScaleFunc, name: "_reflectionRT" + idx, colorFormat: colorFormat,
                //                                                  enableRandomWrite: true, useMipMap: true, autoGenerateMips: false, mipMapCount: 5);
                //}

                //KW_Extensions.WaterLog(this, ReflectionRT[0], ReflectionRT[1], ReflectionRTFilteredMip);
            }

            public void InitializeHashBuffer(Vector2Int resolution)
            {
                var size = resolution.x * resolution.y;
                if (WaterSystem.IsSinglePassStereoEnabled) size *= 2;
                HashBuffer = KWS_CoreUtils.GetOrUpdateBuffer<uint>(ref HashBuffer, size);
            }


            public void Release()
            {
                if(rt != null && rt.IsCreated())
                    rt.Release();

                if (HashBuffer != null) HashBuffer.Release();
                HashBuffer = null;
            }
        }

        public ScreenSpacePlaneReflection(WaterSystem waterSystem)
        {
            InitializeMaterials();

            SetShaderSetting(waterSystem.Settings, waterSystem.WaterMat);
            _currentPassData.ReInitializeTextures(Vector2Int.one * 100, waterSystem.Settings);
#if UNITY_EDITOR
            _currentPassDataEditor.ReInitializeTextures(Vector2Int.one * 100, waterSystem.Settings);
#endif
        }

        public void UpdateSetting(WaterSystem waterSystem)
        {
            SetShaderSetting(waterSystem.Settings, waterSystem.WaterMat);
        }

        void InitializeMaterials()
        {
            if (_cs == null)
            {
                _cs = (ComputeShader)Resources.Load($"PlatformSpecific/KWS_ScreenSpaceReflectionCompute");

                _kernelClear = _cs.FindKernel("Clear_kernel");
                _kernelRenderHash = _cs.FindKernel("RenderHash_kernel");
                _kernelRenderColorFromHash = _cs.FindKernel("RenderColorFromHash_kernel");

                //waterInstance.AddShaderToWaterRendering(_cs, 1, 2);
            }

            if (_filteringMaterial == null)
            {
                _filteringMaterial = KWS_CoreUtils.CreateMaterial(KWS_ShaderConstants.ShaderNames.ReflectionFiltering);
                //waterInstance.AddShaderToWaterRendering(_filteringMaterial);
            }
        }

        void SetShaderSetting(WaterSystemSetting Settings, Material waterMat)
        {
            _cs.SetKeyword(WaterKeywords.SSPR_REFLECTION, Settings.UseScreenSpaceReflection);
            //_cs.SetKeyword(WaterKeywords.USE_HOLES_FILLING, Settings.UseScreenSpaceReflectionHolesFilling);
            _cs.SetKeyword(WaterKeywords.REFLECT_SUN, Settings.ReflectSun);


            _cs.SetFloat(ConstantWaterParams.KWS_ScreenSpaceBordersStretching, Settings.ScreenSpaceBordersStretching);
            _cs.SetFloat(ConstantWaterParams.KWS_SunCloudiness, Settings.ReflectedSunCloudinessStrength);
            _cs.SetFloat(ConstantWaterParams.KWS_SunStrength, Settings.ReflectedSunStrength);

            _cs.SetInt(ReflectionsID.KWS_IsCubemapReflectionPlanar, 1);


            waterMat.SetKeyword(WaterKeywords.SSPR_REFLECTION, Settings.UseScreenSpaceReflection);
            //waterMat.SetKeyword(WaterKeywords.USE_HOLES_FILLING, Settings.UseScreenSpaceReflectionHolesFilling);
            waterMat.SetKeyword(WaterKeywords.REFLECT_SUN, Settings.ReflectSun);

            waterMat.SetFloat(ConstantWaterParams.KWS_ScreenSpaceBordersStretching, Settings.ScreenSpaceBordersStretching);
            waterMat.SetFloat(ConstantWaterParams.KWS_SunCloudiness, Settings.ReflectedSunCloudinessStrength);
            waterMat.SetFloat(ConstantWaterParams.KW_ReflectionClipOffset, Settings.ReflectionClipPlaneOffset);
            waterMat.SetFloat(ConstantWaterParams.KWS_SunStrength, Settings.ReflectedSunStrength);
        }

        Vector2Int vei;
        public void InitializeRT(int width, int height,
            WaterSystem WaterInstance, bool isEditor)
        {
            vei.x = width; vei.y = height;

            if(isEditor)
                _currentPassDataEditor.CheckSize(vei, WaterInstance.Settings);
            else
                _currentPassData.CheckSize(vei, WaterInstance.Settings);
        }


        //执行sspr的代码
        public void Execute(Camera cam, CommandBuffer cmd,
            WaterSystem WaterInstance)
        {
            //检测释放需要使用
            cmd.BeginSample(PASS_NAME);

            var camType = cam.cameraType;
            //创建了两个，区分编译器和正常渲染状态，在里面已经有目标贴图了
            var passData = camType == CameraType.SceneView ? _currentPassDataEditor : _currentPassData;

            //var targetRT = passData.Frame % 2 == 0 ? passData.rts[0] : passData.rts[1];
            //var lastTargetRT = passData.Frame % 2 == 0 ? passData.rts[1] : passData.rts[0];
            var targetRT = passData.rt;
            //设置渲染目标
            //OnSetRenderTarget?.Invoke(cmd, cam, targetRT);
            cmd.SetRenderTarget(targetRT);

            var currentResolution = passData.GetCurrentResolution();
            var dispatchSize = new Vector2Int(Mathf.CeilToInt((float)currentResolution.x / shaderNumthreadX), Mathf.CeilToInt((float)currentResolution.y / shaderNumthreadY));
            var waterOffsetFix = 0.0025f * 15;
            var stereoPasses = WaterSystem.IsSinglePassStereoEnabled ? 2 : 1;
            //if (targetRT.rt.volumeDepth != stereoPasses) return;
            passData.InitializeHashBuffer(currentResolution);

            cmd.SetComputeIntParam(_cs, KWS_ShaderConstants.ReflectionsID.KWS_ReprojectedFrameReady, passData.Frame > 0 ? 1 : 0);
            cmd.SetComputeFloatParam(_cs, KWS_ShaderConstants.SSR_ID._HorizontalPlaneHeightWS, WaterInstance.transform.position.y + waterOffsetFix);
            cmd.SetComputeVectorParam(_cs, KWS_ShaderConstants.SSR_ID._RTSize, new Vector4(currentResolution.x, currentResolution.y, 1f / currentResolution.x, 1f / currentResolution.y));
            if (WaterSystem.IsSinglePassStereoEnabled) cmd.SetComputeMatrixArrayParam(_cs, KWS_ShaderConstants.CameraMatrix.KWS_PREV_MATRIX_VP_STEREO, passData.PrevVPMatrixStereo);
            else cmd.SetComputeMatrixParam(_cs, KWS_ShaderConstants.CameraMatrix.KWS_PREV_MATRIX_VP, passData.PrevVPMatrix);


            ///////////////////////////clear pass//////////////////////////////////
            cmd.SetComputeBufferParam(_cs, _kernelClear, KWS_ShaderConstants.SSR_ID.HashRT, passData.HashBuffer);
            cmd.SetComputeTextureParam(_cs, _kernelClear, KWS_ShaderConstants.SSR_ID.ColorRT, targetRT);
            cmd.DispatchCompute(_cs, _kernelClear, dispatchSize.x, dispatchSize.y, stereoPasses);
            ///////////////////////////clear pass//////////////////////////////////


            ///////////////////////////render to hash pass//////////////////////////////////
            cmd.SetComputeBufferParam(_cs, _kernelRenderHash, KWS_ShaderConstants.SSR_ID.HashRT, passData.HashBuffer);
            cmd.SetComputeTextureParam(_cs, _kernelRenderHash, KWS_ShaderConstants.SSR_ID.ColorRT, targetRT);
            cmd.SetComputeTextureParam(_cs, _kernelRenderHash, KWS_ShaderConstants.SSR_ID._CameraDepthTexture, ShaderProperty.CameraDepthTexId);
            cmd.DispatchCompute(_cs, _kernelRenderHash, dispatchSize.x, dispatchSize.y, stereoPasses);
            ///////////////////////////render to hash pass//////////////////////////////////


            ///////////////////////////render color pass//////////////////////////////////
            //cmd.SetComputeTextureParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.ReflectionsID.KWS_PlanarReflectionRT, WaterInstance.SharedData.PlanarReflectionRaw);
            cmd.SetComputeTextureParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.ReflectionsID.KWS_CubemapReflectionRT, WaterInstance.SharedData.CubemapReflection);
            //WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.PlanarReflection, _cs, _kernelRenderColorFromHash);
            WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.CubemapReflection, _cs, _kernelRenderColorFromHash);

            //cmd.SetComputeTextureParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.SSR_ID.KWS_LastTargetRT, lastTargetRT);
            cmd.SetComputeBufferParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.SSR_ID.HashRT, passData.HashBuffer);
            cmd.SetComputeTextureParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.SSR_ID.ColorRT, targetRT);
            cmd.SetComputeTextureParam(_cs, _kernelRenderColorFromHash, KWS_ShaderConstants.SSR_ID._CameraOpaqueTexture, ShaderProperty.CameraColorTexId);
            cmd.DispatchCompute(_cs, _kernelRenderColorFromHash, dispatchSize.x, dispatchSize.y, stereoPasses);
            ///////////////////////////render color pass//////////////////////////////////

            //if (settings.UseAnisotropicReflections)
            //{
            //    cmd.SetKeyword("USE_STEREO_ARRAY", true);
            //    cmd.BlitTriangleRTHandle(targetRT, targetRT.rtHandleProperties.rtHandleScale, passData.ReflectionRTFilteredMip, _filteringMaterial, ClearFlag.None, Color.clear, settings.AnisotropicReflectionsHighQuality ? 1 : 0);
            //    cmd.SetKeyword("USE_STEREO_ARRAY", false);
            //}
            //else
            //{
            //}
            //cmd.GenerateMips(targetRT);

            cmd.EndSample(PASS_NAME);
            passData.Frame++;
            if (WaterSystem.IsSinglePassStereoEnabled) passData.PrevVPMatrixStereo = WaterInstance.CurrentVPMatrixStereo;
            else passData.PrevVPMatrix = WaterInstance.CurrentVPMatrix;


            WaterInstance.SharedData.SsrReflection = targetRT;
        }
        public void Release()
        {
            _currentPassData.Release();
            _currentPassDataEditor.Release();

            Resources.UnloadAsset(_cs);
            KW_Extensions.SafeDestroy(_filteringMaterial);
        }

    }
}
