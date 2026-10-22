
using System.Drawing;
using System.IO;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace CustomRenderPipline
{

    /// <summary>  /// 静态类，封装渲染中的通用方法  /// </summary>
    public static class RenderTools
    {
        /// <summary>    /// 根据动画曲线生成计算数组    /// </summary>
        /// <param name="curve">曲线数据</param>
        /// <param name="maxSize">数值最大值</param>
        /// <returns>数组对象</returns>
        public static Vector4[] GetCurveArray(AnimationCurve curve, int maxSize)
        {
            Vector4[] re = new Vector4[maxSize];
            int i = 0;
            for (; i < curve.length; i++)
            {
                re[i] = new Vector4(curve.keys[i].time,
                    curve.keys[i].value, curve.keys[i].inTangent,
                    curve.keys[i].outWeight);
            }
            for (; i < maxSize; i++)
                re[i] = Vector4.one;
            return re;
        }

        /// <summary>/// 根据Gradient生成透明度计算数组  /// </summary>
        /// <param name="gradient">颜色对象</param>
        /// <param name="maxSize">数组最大大小</param>
        /// <returns>计算数据</returns>
        public static Vector4[] GetGradientAlphas(Gradient gradient, int maxSize)
        {
           Vector4[] re = new Vector4[maxSize];
            GradientAlphaKey[] alphaKeys = gradient.alphaKeys;
            int i = 0;
            for (; i < alphaKeys.Length; i++)
                re[i] = new Vector4(alphaKeys[i].alpha, alphaKeys[i].time);
            for (; i < maxSize; i++)
                re[i] = Vector4.one;
            return re;
        }

        /// <summary>/// 根据Gradient生成颜色计算数组  /// </summary>
        /// <param name="gradient">颜色对象</param>
        /// <param name="maxSize">数组最大大小</param>
        /// <returns>计算数据</returns>
        public static Vector4[] GetGradientColors(Gradient gradient, int maxSize)
        {
            Vector4[] re = new Vector4[maxSize];
            GradientColorKey[] colorKeys = gradient.colorKeys;
            int i = 0;
            for (; i < colorKeys.Length; i++)
            {
                re[i] = colorKeys[i].color;
                re[i].w = colorKeys[i].time;
            }
            for (; i < maxSize; i++)
                re[i] = Vector4.one;
            return re;
        }

        /// <summary>        /// 根据Gradient的颜色生成颜色Buffer        /// </summary>
        public static ComputeBuffer GetGradientColorsBuffer(Gradient gradient)
        {
            GradientColorKey[] colorKeys = gradient.colorKeys;
            ComputeBuffer buffer = new ComputeBuffer(colorKeys.Length, 4 * 4);
            Vector4[] datas = new Vector4[colorKeys.Length];
            for (int i=0; i < colorKeys.Length; i++)
            {
                datas[i] = colorKeys[i].color;
                datas[i].w = colorKeys[i].time;
            }
            buffer.SetData(datas);
            return buffer;
        }

        public static void Save(RenderTexture origin, TextureFormat saveFormat,
            string name)
        {
            RenderTexture temp = RenderTexture.GetTemporary(origin.width,
                origin.height, 0, origin.format);
            Graphics.Blit(origin, temp);
            RenderTexture.active = temp;

            Texture2D png = new Texture2D(origin.width, origin.height,
                saveFormat, false);
            png.ReadPixels(new Rect(0, 0, origin.width, origin.height), 0, 0);
            byte[] bytes = png.EncodeToPNG();
            string path = string.Format("Assets/"+ name +".png");
            FileStream fs = File.Open(path, FileMode.Create);
            BinaryWriter writer = new BinaryWriter(fs);
            writer.Write(bytes);
            writer.Flush();
            writer.Close();
            fs.Close();
            GameObject.DestroyImmediate(png);
            RenderTexture.ReleaseTemporary(temp);
        }
    }
}