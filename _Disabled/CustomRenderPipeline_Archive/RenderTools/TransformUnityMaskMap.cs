using System.IO;
using UnityEngine;

namespace CustomRenderPipline
{
    /// <summary>
    /// 将Unity的Mask贴图数据转移为本管线的MaskMap
    /// </summary>
    public class TransformUnityMaskMap : MonoBehaviour
    {
        public Texture origin;
        public bool BeginWrite;
        public ComputeShader compute;
        public string path;
        RenderTexture temp;

        private void OnValidate()
        {
            if (BeginWrite)
            {
                BeginWrite = false;
                UnityEngine.Experimental.Rendering.GraphicsFormat
                    format = origin.graphicsFormat;
                if (temp != null)
                    RenderTexture.ReleaseTemporary(temp);
                temp = RenderTexture.GetTemporary(origin.width, origin.height, 0);
                temp.useMipMap = true;
                temp.enableRandomWrite = true;
                temp.autoGenerateMips = false;
                temp.Create();
                Graphics.Blit(origin, temp);


                int kernel_WriteChannel = compute.FindKernel("WriteInChannel");
                compute.SetTexture(kernel_WriteChannel, "Result", temp);
                compute.SetTexture(kernel_WriteChannel, "_Origin", origin);
                compute.SetInts("_TextureSizes", new int[] { origin.width, origin.height });
                compute.SetInts("_Mode", new int[] { 0, 0 });       //金属度
                compute.SetInt("_IsMinus", 1);

                compute.Dispatch(kernel_WriteChannel,
                    origin.width / 32 + 1, origin.height / 32 + 1, 1);
                compute.SetInt("_IsMinus", 0);
                compute.SetInts("_Mode", new int[] { 1, 3 });       //遮罩
                compute.Dispatch(kernel_WriteChannel,
                    origin.width / 32 + 1, origin.height / 32 + 1, 1);

                compute.SetInts("_Mode", new int[] { 2, 2 });       //Detail
                compute.Dispatch(kernel_WriteChannel,
                    origin.width / 32 + 1, origin.height / 32 + 1, 1);

                compute.SetInts("_Mode", new int[] { 3, 1 });       //Smoothness
                compute.SetInt("_IsMinus", 1);
                compute.Dispatch(kernel_WriteChannel,
                    origin.width / 32 + 1, origin.height / 32 + 1, 1);

                //if(origin.mipmapCount > 1)
                //{
                //    temp.GenerateMips();
                //    temp.Create();
                //}

                if(path == null || path.Length <= 1)
                {
                    return;
                }
                int width = temp.width;
                int height = temp.height;
                Texture2D texture2D = new Texture2D(width, height, TextureFormat.ARGB32, false);
                RenderTexture.active = temp;
                texture2D.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture2D.Apply();
                byte[] vs = texture2D.EncodeToPNG();
                string savePath = path + "/" + origin.name + ".png";


                FileStream fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                Debug.Log(savePath);
                fileStream.Write(vs, 0, vs.Length);
                fileStream.Dispose();
                fileStream.Close();
            }

        }
    }
}