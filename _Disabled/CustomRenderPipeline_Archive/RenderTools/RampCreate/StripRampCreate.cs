using System.Drawing;
using UnityEngine;

namespace CustomRenderPipline.Tool
{
    /// <summary>   /// 条形Ramp贴图创建器   /// </summary>
    public class StripRampCreate : MonoBehaviour
    {
        /// <summary>       /// 条形贴图的高度        /// </summary>
        public int height = 3;
        public RampSize rampSize = RampSize._64;
        public ComputeShader rampCreateCS;

        public Gradient gradient = new Gradient();
        public RenderTexture target;
        public bool save = false;

        public RenderTextureFormat textureFormat = RenderTextureFormat.ARGB32;

        private void OnValidate()
        {
            if (rampCreateCS != null)
            {
                if (target != null)
                    RenderTexture.ReleaseTemporary(target);
                int size = (int)rampSize;
                target = RenderTexture.GetTemporary(size, height,
                    0, textureFormat);
                target.enableRandomWrite = true;
                target.Create();
                ComputeBuffer buffer = RenderTools.GetGradientColorsBuffer(gradient);
                rampCreateCS.SetBuffer(2, "_ColorsBuffer", buffer);
                rampCreateCS.SetTexture(2, "Result", target);
                rampCreateCS.Dispatch(2, size / 32 + 1, height / 32 + 1, 1);
                buffer.Dispose();
            }
            if (save)
            {
                save = false;
                RenderTools.Save(target, FormatMap(textureFormat), "RampStrip");
            }
        }

        private TextureFormat FormatMap(RenderTextureFormat format)
        {
            switch (format)
            {
                case RenderTextureFormat.ARGB32:
                    return TextureFormat.ARGB32;
                case RenderTextureFormat.ARGB64:
                    return TextureFormat.RGBA64;
                default : return TextureFormat.ARGB32;
            }
        }
    }
}