using UnityEngine;
using UnityEngine.Rendering;
using static KWS.KWS_ShaderConstants;

namespace KWS
{
    public class MaskDepthNormal
    {
        public RenderTexture _waterMaskRT;
        public RenderTexture _waterIdRT;
        RenderTexture _waterDepthRT;
        RenderTargetIdentifier[] _mrt = new RenderTargetIdentifier[2];
        const string PASS_NAME = "KWS Water Mask Depth";

        public MaskDepthNormal()
        {
            InitializeTextures(1, 1);
        }

        public void Execute(CommandBuffer cmd, WaterSystem instance)
        {
            _mrt[0] = _waterMaskRT;
            _mrt[1] = _waterIdRT;
            //设置后顺便清除
            //CoreUtils.SetRenderTarget(cmd, _mrt, _waterDepthRT, ClearFlag.All, Color.clear);
            cmd.BeginSample(PASS_NAME);
            cmd.SetRenderTarget(_mrt, _waterDepthRT);
            cmd.ClearRenderTarget(true, true, Color.clear);
            ExecuteRender(cmd, instance);
            cmd.EndSample(PASS_NAME);

            //只有使用了UnderWater之后才会使用这个
            Shader.SetGlobalTexture(MaskPassID.KW_WaterDepth_ID, _waterDepthRT);
            Shader.SetGlobalTexture(MaskPassID.KWS_WaterTexID, _waterIdRT);
            //Shader.SetGlobalTexture(MaskPassID.KW_WaterMaskScatterNormals_ID, _waterMaskRT);
            Shader.SetGlobalTexture(MaskPassID.KW_WaterMaskScatterNormals_ID, _waterMaskRT);
            Shader.SetGlobalVector(MaskPassID.KWS_WaterMask_RTHandleScale, Vector4.one);
        }

        public void ExecuteRender(CommandBuffer cmd, WaterSystem currentInstance)
        {
            cmd.SetGlobalVector(DynamicWaterParams.KW_WaterPosition, currentInstance.WaterRelativeWorldPosition);
            //cmd.SetGlobalFloat(DynamicWaterParams.KW_Time, currentInstance.UseNetworkTime ? currentInstance.NetworkTime : KW_Extensions.TotalTime());
            cmd.SetGlobalInt(DynamicWaterParams.KWS_WaterPassID, currentInstance.SharedData.WaterShaderPassID);
            //cmd.SetGlobalInt(DynamicWaterParams.KWS_UnderwaterVisible, currentInstance.IsCameraUnderwater ? 1 : 0);

            //要渲染水底了，先提前设置了FFT数据先
            currentInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.FFT);
            //if (currentInstance.Settings.UseFlowMap) currentInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Flowing);
            //if (currentInstance.Settings.UseFluidsSimulation) currentInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.FluidsSimulation);
            //if (currentInstance.Settings.UseDynamicWaves) currentInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.DynamicWaves);
            //if (currentInstance.Settings.UseShorelineRendering) currentInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Shoreline);

            currentInstance.DrawWaterMesh(cmd, 1);
        }




        public void CheckTextures(int width, int height)
        {
            int _w = width / 2, _h = height / 2;
            if(_waterMaskRT.width != _w || _waterMaskRT.height != _h)
                InitializeTextures(_w, _h);
        }
        
        void InitializeTextures(int width, int height)
        {
            ReleaseTextures();
            _waterMaskRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            _waterMaskRT.name = "KWS Water Mask Map";
            _waterIdRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.R8);
            _waterIdRT.name = "KWS Water Id Map";
            _waterDepthRT = RenderTexture.GetTemporary(width, height, 32, RenderTextureFormat.Depth);
            _waterDepthRT.name = "KWS Water Depth Map";
        }

        void ReleaseTextures()
        {
            if( _waterMaskRT != null && _waterMaskRT.IsCreated()) {
                _waterMaskRT.Release();
                _waterIdRT.Release();
                _waterDepthRT.Release();
            }
        }

    }
}