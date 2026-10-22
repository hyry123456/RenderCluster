using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static KWS.KWS_ShaderConstants;

namespace KWS
{

    public class WaterVolumetricLight 
    {
        RenderTexture _volumeLightRT;
        RenderTexture _volumeLightRT_Temp;
        RenderTexture _volumeLightRT_Blured;
        RenderTexture _lowResDepthRT;

        private Material volumeLightMat;
        private Material volumeLightBlurMat;

        public WaterVolumetricLight(WaterSystem waterInstance)
        {
            InitializeMaterials();
            UpdateConstantShaderParams(waterInstance.Settings);
            InitializeTextures(waterInstance, 1, 1);
        }

        public void CheckRT(int width, int heigh, WaterSystem waterInstance)
        {
            float scale = (int)waterInstance.Settings.VolumetricLightResolutionQuality / 100f;
            int _w = Mathf.CeilToInt(scale * width), _h = Mathf.CeilToInt(scale * heigh);
            if(_w != _volumeLightRT.width ||  _h != _volumeLightRT.height)
            {
                InitializeTextures(waterInstance, _w, _h);
            }
        }

        public void Execute(Camera cam, CommandBuffer cmd, WaterSystem waterInstance)
        {
            cmd.BeginSample("VolumetricLight");
            UpdateShaderParams(cam, cmd);
            DisableCausticForLowSettings(waterInstance.Settings);
            //OnSetRenderTarget?.Invoke(cmd, cam, _volumeLightRT);
            cmd.SetRenderTarget(_volumeLightRT);
            cmd.BlitTriangle(_volumeLightRT, volumeLightMat, 0);

            volumeLightBlurMat.SetTexture(VolumetricLightConstantsID.KWS_CameraDepthTextureLowRes, _lowResDepthRT);
            cmd.BlitTriangle(_lowResDepthRT, volumeLightBlurMat, 0);

            cmd.BlitTriangle(_volumeLightRT, _volumeLightRT_Temp, volumeLightBlurMat, 2);
            cmd.BlitTriangle(_volumeLightRT_Temp, _volumeLightRT, volumeLightBlurMat, 4);
            cmd.BlitTriangle(_volumeLightRT, _volumeLightRT_Blured, volumeLightBlurMat, 6);
            cmd.EndSample("VolumetricLight");
        }

        public void Release()
        {
            ReleaseTextures();

            KW_Extensions.SafeDestroy(volumeLightMat, volumeLightBlurMat);
        }

        public void Check(int width, int heigh, WaterSystem waterInstance)
        {
            float scale = (int)waterInstance.Settings.VolumetricLightResolutionQuality / 100f;
            int _w = Mathf.CeilToInt(width * scale), _h = Mathf.CeilToInt(heigh * scale);
            if (_w != _volumeLightRT.width || _h != _volumeLightRT.height)
                InitializeTextures(waterInstance, _w, _h);
        }

        private void DisableCausticForLowSettings(WaterSystemSetting setting)
        {
            //if (WaterInstance.Settings.VolumetricLightResolutionQuality == WaterSystem.VolumetricLightResolutionQualityEnum.VeryLow
            // || WaterInstance.Settings.VolumetricLightResolutionQuality == WaterSystem.VolumetricLightResolutionQualityEnum.Low) volumeLightMat.DisableKeyword(WaterKeywords.USE_CAUSTIC);
            //else if (WaterInstance.Settings.UseCausticEffect) volumeLightMat.EnableKeyword(WaterKeywords.USE_CAUSTIC);

            if(setting.VolumetricLightResolutionQuality == ScreenSpaceReflectionResolutionQualityEnum.VeryLow
                || setting.VolumetricLightResolutionQuality == ScreenSpaceReflectionResolutionQualityEnum.Low) volumeLightMat.DisableKeyword(WaterKeywords.USE_CAUSTIC);
            else if (setting.UseCaustic) volumeLightMat.EnableKeyword(WaterKeywords.USE_CAUSTIC);
        }

        private void UpdateShaderParams(Camera cam, CommandBuffer cmd)
        {
            var anisoMie = ComputeMieVector(0.05f);

            float volumeLightFade = 1;
            //if (WaterInstance.Settings.WaterMeshType == WaterSystem.WaterMeshTypeEnum.InfiniteOcean) volumeLightFade
            //    = Mathf.Clamp01(Mathf.Exp(-1 * (WaterInstance.transform.position.y - cam.transform.position.y) / WaterInstance.Settings.Transparent)); //todo add depth fading relative to the actual surface height

            var actualRTSize = new Vector2(_volumeLightRT.width, _volumeLightRT.height);
            cmd.SetGlobalFloat(VolumetricLightConstantsID.KWS_VolumeDepthFade, volumeLightFade);

            cmd.SetGlobalVector(CausticID.KW_CausticLodOffset, cam.transform.forward * 0.5f);
            cmd.SetGlobalVector(VolumetricLightConstantsID.KWS_LightAnisotropy, anisoMie);
            cmd.SetGlobalVector(VolumetricLightConstantsID.KWS_VolumeTexSceenSize, actualRTSize);
            cmd.SetGlobalVector(VolumetricLightConstantsID.KWS_WorldSpaceCameraPosCompillerFixed, cam.transform.position);
            //WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Caustic);
        }

        private void InitializeTextures(WaterSystem waterInstance, int width, int heigh)
        {
            ReleaseTextures();

            _volumeLightRT = RenderTexture.GetTemporary(width, heigh, 0, GraphicsFormat.R16G16B16A16_SFloat);
            _volumeLightRT_Blured = RenderTexture.GetTemporary(width, heigh, 0, GraphicsFormat.R16G16B16A16_SFloat);
            _volumeLightRT_Temp = RenderTexture.GetTemporary(width, heigh, 0, GraphicsFormat.R16G16B16A16_SFloat);
            _lowResDepthRT = RenderTexture.GetTemporary(width, heigh, 32, RenderTextureFormat.Depth);

            waterInstance.SharedData.VolumetricLightingRT = _volumeLightRT_Blured;
        }

        void InitializeMaterials()
        {
            if (volumeLightMat == null)
            {
                volumeLightMat = new Material(Shader.Find(ShaderNames.VolumetricLightingShaderName));
            }

            if (volumeLightBlurMat == null)
            {
                volumeLightBlurMat = new Material(Shader.Find(ShaderNames.BlurBilateralName));
            }
        }
        Vector4 ComputeMieVector(float MieG)
        {
            return new Vector4(1 - (MieG * MieG), 1 + (MieG * MieG), 2 * MieG, 1.0f / (4.0f * Mathf.PI));
        }

        private void UpdateConstantShaderParams(WaterSystemSetting settings)
        {
            var anisoMie = ComputeMieVector(0.05f);
            volumeLightMat.SetVector(VolumetricLightConstantsID.KWS_LightAnisotropy, anisoMie);
            volumeLightMat.SetInt(VolumetricLightConstantsID.KWS_RayMarchSteps, settings.VolumetricLightIteration);
            if (settings.UseCaustic)
            {
                volumeLightMat.SetFloat(CausticID.KW_DecalScale, KWS_Settings.Caustic.LodSettings[settings.CausticActiveLods - 1] * 2);
                KWS_CoreUtils.SetKeywords(volumeLightMat,
                                          (CausticKeywords.USE_LOD1, settings.CausticActiveLods == 2),
                                          (CausticKeywords.USE_LOD2, settings.CausticActiveLods == 3),
                                          (CausticKeywords.USE_LOD3, settings.CausticActiveLods == 4));
            }
            var volumeLightMaxDist = Mathf.Max(0.3f, settings.Transparent * 3);
            volumeLightMaxDist = Mathf.Min(40, volumeLightMaxDist);
            volumeLightMat.SetFloat(VolumetricLightConstantsID.KWS_VolumeLightMaxDistance, volumeLightMaxDist);
            volumeLightMat.SetFloat(ConstantWaterParams.KW_Transparent, settings.Transparent);
            volumeLightBlurMat.SetVector("_SourceRTHandleScale", Vector4.one);
        }

        void ReleaseTextures()
        {
            if (_volumeLightRT != null) _volumeLightRT.Release();
            if (_volumeLightRT_Blured != null) _volumeLightRT_Blured.Release();
            if (_volumeLightRT_Temp != null) _volumeLightRT_Temp.Release();
            if (_lowResDepthRT != null) _lowResDepthRT.Release();
        }
    }
}