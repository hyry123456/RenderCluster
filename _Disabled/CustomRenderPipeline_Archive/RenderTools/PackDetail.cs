using System.IO;
using UnityEngine;

namespace DefferedRender
{

    /// <summary>
    /// 用来将根据图片的颜色值写入目标纹理的某一个通道的工具类,
    /// 前提是图片大小是一致的，不一致就用拷贝工具进行复制
    /// </summary>
    public class PackDetail : MonoBehaviour
    {
        public Texture2D albedo;
        public Texture2D normal;
        public Texture2D roughness;
        public RenderTexture target;


        public int targetWidth = 512, targetHeight = 512;

        public bool beginWrite, save ;
        public Shader shader;
        private Material material;
        private Material RenderMat
        {
            get
            {
                if (material == null)
                {
                    material = new Material(shader);
                }
                return material;
            }
        }


        private void OnValidate()
        {
            if (beginWrite)
            {
                if (target == null)
                    target = RenderTexture.GetTemporary(targetWidth,
                        targetHeight, 0, RenderTextureFormat.ARGB32);
                if (target.width != targetWidth || target.height != targetHeight)
                    target = RenderTexture.GetTemporary(targetWidth,
                        targetHeight, 0, RenderTextureFormat.ARGB32);
                
                target.enableRandomWrite = true;
                target.Create();

                //int kernel_WriteChannel = compute.FindKernel("WriteInChannel");
                var mat = RenderMat;
                RenderTexture temp = RenderTexture.GetTemporary
                    (target.width, target.height, 0, RenderTextureFormat.Default);
                Graphics.Blit(target, temp);
                mat.SetTexture("_Albedo", albedo == null ? Texture2D.whiteTexture : albedo);
                mat.SetTexture("_Normal", normal == null ? Texture2D.normalTexture : normal);
                mat.SetTexture("_Roughness", roughness == null ? Texture2D.whiteTexture : roughness);
                Graphics.SetRenderTarget(target);
                Graphics.Blit(null, mat, 0);
                RenderTexture.ReleaseTemporary(temp);
                //compute.SetTexture(kernel_WriteChannel, "Result", target);
                //compute.SetTexture(kernel_WriteChannel, "_Origin", origin);
                //compute.SetVector("_Range", writeRange);
                //compute.SetInts("_TextureSizes", new int[] { origin.width, origin.height });
                //compute.SetInts("_Mode", new int[] { (int)originCol, (int)writeChannel });
                //compute.SetInt("_IsMinus", isMinus ? 1 : 0);
                //compute.Dispatch(kernel_WriteChannel, 
                //    origin.width / 32 + 1, origin.height / 32 + 1, 1);
                beginWrite = false;
            }

            if (save)
            {

                RenderTexture.active = target;

                Texture2D png = new Texture2D(target.width, target.height,
                    TextureFormat.ARGB32, false);
                png.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                Debug.Log(png.width);
                byte[] bytes = png.EncodeToPNG();
                string path = string.Format("Assets/" + albedo.name + "_Detial.png");
                FileStream fs = File.Open(path, FileMode.Create);
                BinaryWriter writer = new BinaryWriter(fs);
                writer.Write(bytes);
                writer.Flush();
                writer.Close();
                fs.Close();
                DestroyImmediate(png);
                save = false;
            }
        }

    }
}