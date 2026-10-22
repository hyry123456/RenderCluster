using UnityEngine;

namespace CustomRenderPipline
{

    public class TextureArrayCreate
    {

        /// <summary>
        /// 该方法和Copy的作用一样，但是由于图像API底层的原因，对于引擎生成的图片使用Copy，
        /// 一般的纹理使用该方法拷贝
        /// </summary>
        public static Texture2DArray CreateTextureArrayDirect(Texture2D[] texture2D)
        {
            if (texture2D == null || texture2D.Length == 0) return null;
            for (int i = 1; i < texture2D.Length; i++)
            {
                if (texture2D[i].width != texture2D[i - 1].width)
                {
                    Debug.LogError("Use Texture Pixel None Equal");
                    return null;
                }
            }
            Texture2DArray texture2DArray = new Texture2DArray(texture2D[0].width, texture2D[0].height,
                texture2D.Length, TextureFormat.ARGB32, true, true);

            for (int i = 0; i < texture2D.Length; i++)
            {
                for (int j = 0; j < texture2DArray.mipmapCount; j++)
                {
                    texture2DArray.SetPixels(texture2D[i].GetPixels(j), i, j);
                }
            }
            texture2DArray.Apply();
            return texture2DArray;
        }

        /// <summary>  /// 创建TextureArray，这个方法会检测图片大小是否一致，不一致时会进行纹理拷贝  /// </summary>
        /// <param name="texture2D">根据的纹理数组</param>
        public static Texture2DArray CreateTextureArray(Texture2D[] texture2D)
        {
            if (texture2D == null || texture2D.Length == 0) return null;
            int maxWidth = int.MinValue, maxHeight = int.MinValue;
            for(int i = 0; i < texture2D.Length; i++)
            {
                maxWidth = Mathf.Max(texture2D[i].width, maxWidth);
                maxHeight = Mathf.Max(texture2D[i].height, maxHeight);
            }

            for(int i = 0; i < texture2D.Length; i++)
            {
                if (texture2D[i].width != maxWidth || texture2D[i].height != maxHeight)
                {
                    Debug.Log("Recaculate");
                    texture2D[i] = AdjustTextureSize(texture2D[i], maxWidth, maxHeight);
                }
            }
            Texture2DArray texture2DArray = new Texture2DArray(maxWidth, maxHeight,
                texture2D.Length, TextureFormat.ARGB32, true, true);

            for (int i = 0; i < texture2D.Length; i++)
            {
                for (int j = 0; j < texture2D[i].mipmapCount; j++)
                {
                    texture2DArray.SetPixels(texture2D[i].GetPixels(j), i, j);
                }
            }
            texture2DArray.filterMode = FilterMode.Bilinear;
            texture2DArray.wrapMode = TextureWrapMode.Repeat;
            texture2DArray.Apply();
            return texture2DArray;
        }

        public static Texture2D CreateTexture2D(int size, Color color, TextureFormat textureFormat)
        {
            Texture2D texture = new Texture2D(size, size, textureFormat, true, true);
            Color[] colors = new Color[size * size];
            for (int i = 0; i < size * size; i++) colors[i] = color; 
            for(int i=0; i<texture.minimumMipmapLevel; i++)
            {
                texture.SetPixels(colors, i);
            }
            texture.Apply();
            return texture;
        }

        public static Texture2D AdjustTextureSize(Texture2D origin, int width, int height)
        {
            RenderTexture render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(origin, render);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = render;

            // 创建修改后的纹理，保持与源纹理相同压缩格式
            Texture2D newTextureByRenderTexture = new Texture2D(width, height, TextureFormat.RGBA32, 
                true);
            // 读取像素到创建的纹理中
            newTextureByRenderTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            // 应用修改到GPU上
            newTextureByRenderTexture.Apply();
            // 停止当前RenderTexture工作
            RenderTexture.active = previous;
            // 释放内存
            RenderTexture.ReleaseTemporary(render);
            return newTextureByRenderTexture;
        }
    }
}