using System.Collections.Generic;
using UnityEngine;

namespace CustomRenderPipline
{


    /// <summary> /// 地型系统中打包贴图时使用的方法类 /// </summary>
    public static class TerrainTextureCreate
    {
        public static RenderTexture CreateMinMaxHightMips(Texture hightTex, ComputeShader createCS)
        {
            List<RenderTexture> renders = new List<RenderTexture>();

            //生成一张2^n且+1的图片
            int size = Mathf.Max(hightTex.width, hightTex.height);
            size = GetAdjustHightSize(size);
            var adjustHightTex = RenderTexture.GetTemporary(size, size, 0,
                RenderTextureFormat.R16);
            adjustHightTex.enableRandomWrite = true;
            adjustHightTex.Create();

            Graphics.Blit(hightTex, adjustHightTex);   //拷贝出一张目标纹理

            int firstKer = createCS.FindKernel("DrawFirstMinMaxHeightMap");
            RenderTexture first = RenderTexture.GetTemporary(size - 1, size - 1, 0,
                RenderTextureFormat.RG32);
            first.enableRandomWrite = true; first.Create();

            createCS.SetTexture(firstKer, "_OriginHeighTex", adjustHightTex);
            createCS.SetTexture(firstKer, "Result", first);
            createCS.Dispatch(firstKer, adjustHightTex.width / 32 + 1, adjustHightTex.height / 32 + 1, 1);

            renders.Add(first);
            adjustHightTex.Release();

            RenderTexture pre = renders[renders.Count - 1];

            int mipsKer = createCS.FindKernel("DrawMipsMinMaxHeightMap");
            for (int i = 0; pre.width >= 10; i++)
            {
                RenderTexture next = RenderTexture.GetTemporary(
                    pre.width / 2, pre.height / 2, 0,
                    RenderTextureFormat.RG32);
                next.enableRandomWrite = true; next.Create();
                createCS.SetTexture(mipsKer, "Result", next);
                createCS.SetTexture(mipsKer, "_MipsHightTex", pre);   //写入前一张纹理大小
                createCS.Dispatch(mipsKer, next.width / 8 + 1, next.height / 8 + 1, 1);
                renders.Add(next);
                pre = next;
            }
            

            RenderTexture re =
                TextureUtility.CreateRenderTextureWithMipTextures(renders.ToArray(), RenderTextureFormat.RG32);

            for (int i = 0; i < renders.Count; i++)
                renders[i].Release();   //释放全部纹理
            return re;
        }

        public static List<RenderTexture> CreateMinMaxHightMipsDebug(Texture hightTex, ComputeShader createCS)
        {
            List<RenderTexture> renders = new List<RenderTexture>();

            //生成一张2^n且+1的图片
            int size = Mathf.Max(hightTex.width, hightTex.height);
            size = GetAdjustHightSize(size);
            RenderTexture begin  = RenderTexture.GetTemporary(size, size, 0,
                RenderTextureFormat.R16);
            begin.enableRandomWrite = true;
            begin.Create();

            Graphics.Blit(hightTex, begin);   //拷贝出一张目标纹理

            int firstKer = createCS.FindKernel("DrawFirstMinMaxHeightMap");
            RenderTexture first = RenderTexture.GetTemporary(size - 1, size - 1, 0,
                RenderTextureFormat.RG32);
            first.enableRandomWrite = true; first.Create();

            createCS.SetTexture(firstKer, "_OriginHeighTex", begin);
            createCS.SetTexture(firstKer, "Result", first);
            createCS.Dispatch(firstKer, begin.width / 32 + 1, begin.height / 32 + 1, 1);

            renders.Add(first);

            RenderTexture pre = renders[renders.Count - 1];

            int mipsKer = createCS.FindKernel("DrawMipsMinMaxHeightMap");
            for (int i = 0; pre.width >= 10; i++)
            {
                RenderTexture next = RenderTexture.GetTemporary(
                    pre.width / 2, pre.height / 2, 0,
                    RenderTextureFormat.RG32);
                next.enableRandomWrite = true; next.Create();
                createCS.SetTexture(mipsKer, "Result", next);
                createCS.SetTexture(mipsKer, "_MipsHightTex", pre);   //写入前一张纹理大小
                createCS.Dispatch(mipsKer, next.width / 8 + 1, next.height / 8 + 1, 1);
                renders.Add(next);
                pre = next;
            }

            return renders;
        }

        /// <summary>   /// 调整高度贴图大小，让最小Mips变为5个像素    /// </summary>
        private static int GetAdjustHightSize(int currentSize)
        {
            int size = 5;
            while (size < currentSize)
                size *= 2;
            return size + 1;
        }

        public static RenderTexture CreateNormalByHight(RenderTexture hightTex, ComputeShader createCS)
        {
            RenderTexture normalTex = RenderTexture.GetTemporary(hightTex.width,
                hightTex.height, 0, RenderTextureFormat.ARGB64,
                RenderTextureReadWrite.Default, 1);
            normalTex.enableRandomWrite = true; 
            normalTex.Create();
            int normalKer = createCS.FindKernel("GenerateNormal");
            createCS.SetTexture(normalKer, "HeightTex", hightTex);
            createCS.SetTexture(normalKer, "NormalTex", normalTex);
            createCS.Dispatch(normalKer, normalTex.width / 8 + 1, normalTex.height / 8 + 1, 1);
            return normalTex;
        }
    }
}