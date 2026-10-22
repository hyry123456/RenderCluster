using CustomRenderPipline;
using UnityEngine;
using UnityEngine.Rendering;

namespace KWS
{

    public class Underwater
    {
        private Material _underwaterMaterial;
        private const string PassName = "UnderwaterPass";
        void InitializeMaterials(WaterSystemSetting setting)
        {
            if (_underwaterMaterial == null)
            {
                _underwaterMaterial = KWS_CoreUtils.CreateMaterial(KWS_ShaderConstants.ShaderNames.UnderwaterShaderName);
                //WaterInstance.AddShaderToWaterRendering(_underwaterMaterial);
                UpdateSetting(setting);
            }
        }

        public void UpdateSetting(WaterSystemSetting setting)
        {
            if (_underwaterMaterial == null) return;
            _underwaterMaterial.SetVector(KWS_ShaderConstants.ConstantWaterParams.KW_WaterColor,
                setting.WaterColor);
            _underwaterMaterial.SetVector(KWS_ShaderConstants.ConstantWaterParams.KW_TurbidityColor,
                setting.TurbidityColor);
            _underwaterMaterial.SetFloat(KWS_ShaderConstants.ConstantWaterParams.KW_Turbidity,
                setting.Turbidity);
            _underwaterMaterial.SetFloat(KWS_ShaderConstants.ConstantWaterParams.KW_Transparent,
                setting.Transparent);
        }

        public void Execute(Camera cam, CommandBuffer cmd, WaterSystemSetting setting)
        {
            InitializeMaterials(setting);
            cmd.BeginSample(PassName);
            cmd.BlitTriangle(ShaderProperty.ColorAttachmentId, _underwaterMaterial);
            cmd.EndSample(PassName);
        }
    }
}