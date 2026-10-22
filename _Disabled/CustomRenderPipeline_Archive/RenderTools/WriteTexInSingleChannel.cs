using System.IO;
using UnityEngine;

namespace DefferedRender
{
    //用来选择根据的轴
    enum ChoseColor
    {
        R, G, B, A, Grey, One, Zero
    }
    //用来选择写入的通道
    enum WriteInChannel
    {
        R, G, B, A
    }

    /// <summary>
    /// 用来将根据图片的颜色值写入目标纹理的某一个通道的工具类,
    /// 前提是图片大小是一致的，不一致就用拷贝工具进行复制
    /// </summary>
    public class WriteTexInSingleChannel : MonoBehaviour
    {
        public Texture2D origin;
        public RenderTexture target;

        //选择原图片的颜色
        [SerializeField]
        ChoseColor originCol;
        //写入的通道
        [SerializeField]
        WriteInChannel writeChannel;

        [Tooltip("数据设置范围，左边是最小值，右边是最大值，根据源图片像素在范围中映射写入")]
        public Vector2 writeRange = new Vector2(0, 1);
        public int targetWidth = 512, targetHeight = 512;

        public bool isMinus;

        public bool beginWrite, save;
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
            writeRange.x = Mathf.Clamp(writeRange.x, 0, 1);
            writeRange.y = Mathf.Clamp(writeRange.y, 0, 1);
            writeRange.x = Mathf.Min(writeRange.x, writeRange.y);
            if (shader == null) return;
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
                mat.SetTexture("_Result", temp);
                mat.SetTexture("_Origin", origin);
                mat.SetVector("_Range", writeRange);
                mat.SetVector("_Mode", new Vector2( (int)originCol, (int)writeChannel ));
                mat.SetInt("_IsMinus", isMinus ? 1 : 0);
                Graphics.SetRenderTarget(target);
                Graphics.Blit(origin, mat, 0);
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
                string path = string.Format("Assets/" + origin.name + "_WriteChannel.png");
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