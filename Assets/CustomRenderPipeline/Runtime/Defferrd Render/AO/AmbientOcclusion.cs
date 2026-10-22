
using CustomRenderPipline.DefferPipeline;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public class AmbientOcclusion
    {
        private static int OcclisionTexture1 = Shader.PropertyToID("_OcclusionTexture1"),
            OcclisionTexture = Shader.PropertyToID("_OcclusionTexture"),
            SampleCount = Shader.PropertyToID("_SampleCount"),
            Intensity = Shader.PropertyToID("_Intensity"),
            Downsample = Shader.PropertyToID("_Downsample"),
            Radius = Shader.PropertyToID("_Radius")
            ;

        Material renderMat = null;
        Material aoMat
        {
            get
            {
                if (renderMat == null)
                {
                    renderMat = new Material(Shader.Find("Hidden/Post FX/Ambient Occlusion"));
                }
                return renderMat;
            }
        }

        public void ReadyScreenRayData(CommandBuffer buffer, int width, int heigh,
            AmbientOcclusionSetting setting, int colorTexId)
        {
            buffer.BeginSample("AmbientOcclusion");
            if (setting.downSample)
                buffer.GetTemporaryRT(OcclisionTexture1, width / 2, heigh / 2, 0, FilterMode.Bilinear,
                    RenderTextureFormat.Default);
            else
                buffer.GetTemporaryRT(OcclisionTexture1, width, heigh, 0, FilterMode.Bilinear,
                    RenderTextureFormat.Default);

            buffer.GetTemporaryRT(OcclisionTexture, width, heigh, 0, FilterMode.Bilinear,
                RenderTextureFormat.Default);
            buffer.GetTemporaryRT(ShaderProperty.TempBufferId, width, heigh, 0, FilterMode.Bilinear,
                RenderTextureFormat.DefaultHDR);

            var mat = aoMat;
            mat.SetFloat(SampleCount, (int)setting.simpleCount);
            mat.SetFloat(Intensity, setting.intensity);
            mat.SetFloat(Radius, setting.radius);
            mat.SetFloat(Downsample,(setting.downSample)? 0.5f : 1.0f);
            mat.EnableKeyword("FOG_OFF");

            buffer.Blit(null, OcclisionTexture1, mat, 2);

            buffer.SetGlobalTexture(ShaderProperty._MainTex, OcclisionTexture1);
            buffer.Blit(null, ShaderProperty.TempBufferId, mat, 4);
            buffer.SetGlobalTexture(ShaderProperty._MainTex, ShaderProperty.TempBufferId);
            buffer.Blit(null, OcclisionTexture, mat, 5);

            buffer.Blit(colorTexId, ShaderProperty.TempBufferId);

            buffer.SetGlobalTexture(ShaderProperty._MainTex, ShaderProperty.TempBufferId);
            buffer.Blit(null, colorTexId, mat, 6);


            buffer.ReleaseTemporaryRT(ShaderProperty.TempBufferId);
            buffer.ReleaseTemporaryRT(OcclisionTexture1);
            buffer.ReleaseTemporaryRT(OcclisionTexture);

            buffer.EndSample("AmbientOcclusion");

        }

    }
}