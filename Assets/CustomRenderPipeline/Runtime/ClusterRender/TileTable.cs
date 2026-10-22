using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Cluster
{
    /// <summary>
    /// 页表类，用来进行RVT位置的分配
    /// </summary>
    public class TileTable
    {
        public RenderTexture _VirtualAlbedos;
        public RenderTexture _VirtualNormals;
        public RenderTexture _VirtualMasks;
        public RenderTexture _VirtualEmisons;
        public RenderTexture _VirtualDetails;

        private static int
            PaddingSizeId = Shader.PropertyToID("_PaddingSize"),
            // 全部加 _RVT*Src 前缀：这些属性由 MaterialPropertyBlock 喂给 RVTShader，
            // 用 Unity 已知名（_NormalMap/_EmissionMap/_DetailMap 等）会被静默跳过、
            // 采样落到默认白图且不报错。_NormalMap 已实际踩过（法线整片失效）。
            AlbedoId = Shader.PropertyToID("_RVTAlbedoSrc"),
            NormalId = Shader.PropertyToID("_RVTNormalSrc"),
            PBRDataId = Shader.PropertyToID("_RVTPBRDataSrc"),
            EmissionId = Shader.PropertyToID("_RVTEmissionSrc"),
            DetailId = Shader.PropertyToID("_RVTDetailSrc"),
            TileOriginId = Shader.PropertyToID("_TileOrigin"),
            TileScaleId = Shader.PropertyToID("_TileScale")
            ;

        public static int _AlbedoId => AlbedoId;
        public static int _NormalId => NormalId;
        public static int _PBRDataId => PBRDataId;
        public static int _EmissionId => EmissionId;
        public static int _DetailId => DetailId;
        public static int _TileOriginId => TileOriginId;
        public static int _TileScaleId => TileScaleId;

        private Mesh screenMesh;

        /// <summary> /// 存储GBuffer的所有Id  /// </summary>
        RenderTargetIdentifier[] rvtBuffersId;


        ///// <summary>        /// 最高级一行有多少张图片        /// </summary>
        //private int maxTexCount;
        /// <summary>        /// 最高级贴图大小        /// </summary>
        private int maxTexSize;
        /// <summary>        /// 最低级的padding数量        /// </summary>
        private int paddingSize;
        /// <summary>        /// Mips级别数量        /// </summary>
        private int MaxMipsCount;
        /// <summary>        /// 1f / MipsCount        /// </summary>
        private float INV_MipsCount;
        ///// <summary>        /// 有数据的部分占贴图的比例，这个是定值        /// </summary>
        //private float paddingScale;
        private static Material tileMat;
        private MaterialPropertyBlock block = new MaterialPropertyBlock();

        /// <summary>
        /// Default tile mat, can down Mips Tex Size and Direct write Texture
        /// </summary>
        public static Material StandardTileMat => tileMat;

        /// <summary>
        /// 全局数组 _LevelData / _LevelPadding 的固定长度，必须与 UnityInput.hlsl 的
        /// float4 _LevelData[7] / _LevelPadding[7] 一致。
        /// Unity 的全局数组尺寸在首次 SetGlobalVectorArray 注册时就固定，之后变大会被静默截断，
        /// 所以这里【绝不能】按 mipsLevel 动态分配（详见构造函数里的说明）。
        /// </summary>
        public const int MaxShaderMips = 7;

        private Vector4[] levelData, levelPadding;

        public Vector4[] LevelData => levelData;
        public Vector4[] LevelPadding => levelPadding;

        private void CreateScreenMesh()
        {
            List<Vector3> quadVertexList = new List<Vector3>();
            List<int> quadTriangleList = new List<int>();
            List<Vector2> quadUVList = new List<Vector2>();

            quadVertexList.Add(new Vector3(0, 1, 0.1f));
            quadUVList.Add(new Vector2(0, 1));
            quadVertexList.Add(new Vector3(0, 0, 0.1f));
            quadUVList.Add(new Vector2(0, 0));
            quadVertexList.Add(new Vector3(1, 0, 0.1f));
            quadUVList.Add(new Vector2(1, 0));
            quadVertexList.Add(new Vector3(1, 1, 0.1f));
            quadUVList.Add(new Vector2(1, 1));

            quadTriangleList.Add(0);
            quadTriangleList.Add(1);
            quadTriangleList.Add(2);

            quadTriangleList.Add(2);
            quadTriangleList.Add(3);
            quadTriangleList.Add(0);

            screenMesh = new Mesh();
            screenMesh.SetVertices(quadVertexList);
            screenMesh.SetUVs(0, quadUVList);
            screenMesh.SetTriangles(quadTriangleList, 0);

        }


        /// <summary>  /// 创建页表和对应的RVT   /// </summary>
        /// <param name="maxCellSize">最大块的像素大小</param>
        /// <param name="mipsLevel">最大Mips等级</param>
        /// <param name="maxCellCount">一行有多少个最大块</param>
        /// <param name="paddingSize">每次填充的像素数量</param>
        public TileTable(int maxCellSize, 
            int maxCellCount, int paddingSize, Material material, int mipLevel)
        {
            maxTexSize = maxCellSize; MaxMipsCount = mipLevel;
            int texSizeY = (paddingSize * (int)Mathf.Pow(2, mipLevel - 1) * 2 + maxCellSize) * maxCellCount;
            int finalTexSizeY = texSizeY * mipLevel;
            int texSizeX = texSizeY * mipLevel;
            INV_MipsCount = 1.0f / mipLevel;

            _VirtualAlbedos = CreateAtlasRT(texSizeX, finalTexSizeY);
            _VirtualNormals = CreateAtlasRT(texSizeX, finalTexSizeY);
            _VirtualMasks   = CreateAtlasRT(texSizeX, finalTexSizeY);
            _VirtualEmisons = CreateAtlasRT(texSizeX, finalTexSizeY);
            _VirtualDetails = CreateAtlasRT(texSizeX, finalTexSizeY);

            // 【必须固定 7，不能按 mipLevel 动态分配】
            // Unity 的全局数组尺寸在【首次 SetGlobalVectorArray 注册时】就被固定，
            // 之后再用更大的数组去设会命中：
            //   "Property (_LevelData) exceeds previous array size (4 vs 3). Cap to previous size."
            // 被截断后最高一级的 levelData/levelPadding 恒为 (0,0,0,0)，
            // 该 mip 级所有页都映射到图集原点 → 采样全是垃圾 → 表现为整屏黑/什么都不渲染。
            // 这里与 UnityInput.hlsl 的 float4 _LevelData[7] / _LevelPadding[7] 对齐，
            // 固定长度后无论 mipsLevel 怎么改都不会再被截断（多出来的槽位保持 0，
            // 永远不会访问到：mip 被 clamp 到 mipsLevel-1 <= 6）。
            levelData = new Vector4[MaxShaderMips]; levelPadding = new Vector4[MaxShaderMips];
            for(int i = 0; i < mipLevel; i++)
            {
                //目标的贴图数据部分大小
                int currentTexSize = maxTexSize / (int)Mathf.Pow(2, i);
                //目标贴图的填充部分大小
                int currentPaddingSize = (int)Mathf.Pow(2, MaxMipsCount - i - 1) * paddingSize;
                float texSize = (currentTexSize + currentPaddingSize * 2.0f);
                //x:mip offset, yz:tile scale of sumTex X and Y
                levelData[i] = new Vector4(INV_MipsCount * i,
                    texSize / texSizeX, texSize / finalTexSizeY);
                //xy:padding scale of sumTex X and Y, zw:tile value scale of sum Tex
                levelPadding[i] = new Vector4(currentPaddingSize / (float)texSizeX,
                    currentPaddingSize / (float)finalTexSizeY);
                levelPadding[i].z = levelData[i].y - levelPadding[i].x * 2;
                levelPadding[i].w = levelData[i].z - levelPadding[i].y * 2;
            }


            //maxTexCount = maxCellCount;

            this.paddingSize = paddingSize;

            CreateScreenMesh();
            if(tileMat == null)
                tileMat = material;

            //存储全部的Gbuffer数组
            rvtBuffersId = new RenderTargetIdentifier[5];
        }

        /// <summary>
        /// 创建一张 RVT 图集 RT。
        /// anisoLevel=8 是关键：图集必须靠各向异性过滤才不会在掠射角糊掉。
        /// 场景贴图（如桶的 albedo/normal）导入设置是 aniso=9，标准路径用 sampler_MainTex
        /// 吃到了这个设置；图集若停在默认 aniso=1，桶的圆柱侧面和地面就会明显更糊，
        /// 表现为「RVT 怎么都不如不用 RVT 清晰」。采样器已在 shader 里改成 sampler_Virtual*，
        /// 只有「材质属性纹理」的 anisoLevel 才会被 Unity 采用（见 ClusterObject.ApplyAtlasTextures）。
        /// </summary>
        private static RenderTexture CreateAtlasRT(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.Default, 0);
            rt.anisoLevel = 8;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Repeat;
            return rt;
        }

        public void Dispose()
        {
            // Unity 重载了 == null：对「已销毁的 Unity 对象」返回 true，可安全兜住。
            // 退出 Play / 域重载时 RenderTexture 可能已被 Unity 提前销毁，
            // 直接 Release() 会抛 MissingReferenceException（之前就在这里报错）。
            ReleaseRT(ref _VirtualAlbedos);
            ReleaseRT(ref _VirtualNormals);
            ReleaseRT(ref _VirtualMasks);
            ReleaseRT(ref _VirtualEmisons);
            ReleaseRT(ref _VirtualDetails);
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                rt = null;
            }
        }

        //public static int width, height, level;

        /// <summary>        /// 根据结点数据，调入贴图        /// </summary>
        public void DrawTile(CacheNode node, int texId, CommandBuffer buffer)
        {
            if (screenMesh == null)
                CreateScreenMesh();

            Matrix4x4 matrix;

            //贴图整张大小
            int texWidth = _VirtualAlbedos.width, texHeight = _VirtualAlbedos.height;
            //目标的贴图数据部分大小
            int currentTexSize = maxTexSize / (int)Mathf.Pow(2, node.MipsLevel);
            //目标贴图的填充部分大小
            int currentPaddingSize = (int)Mathf.Pow(2, MaxMipsCount - node.MipsLevel - 1) * paddingSize;
            //target tex node scale in all tex (include Padding)
            float nodeScaleX = (currentTexSize + currentPaddingSize * 2) / (float)texWidth,
                nodeScaleH = (currentTexSize + currentPaddingSize * 2) / (float)texHeight;
            //有效数据的起始范围，用来模拟填充效果, 其不需要区分，
            //因为绘制时只会映射出方块区域在目标位置，可以当作就是正方形
            float valueBeg = currentPaddingSize / (currentTexSize + currentPaddingSize * 2.0f);
            Vector3 begin = new Vector3(
                INV_MipsCount * node.MipsLevel + node.WidthId * nodeScaleX,
                node.HeightId * nodeScaleH, 0);
            begin.x = begin.x * 2.0f - 1.0f; begin.y = begin.y * 2.0f - 1.0f;
            matrix = Matrix4x4.TRS(
                    begin,   //渲染的起点，从-1开始,z必须为0
                    Quaternion.identity,
                    new Vector3(nodeScaleX * 2, nodeScaleH * 2, 1));     //渲染的大小，图片总长度为2

            var textureMaps = TextureNodesMap.Instance;

            //需要先能够传递所有贴图
            TexturesNode textureNode = textureMaps.GetTextureData(texId);

            if (textureNode == null)
            {
                Debug.LogError("结点不存在");
                return;       //这种情况一定是异步问题，导致结点已经舍去，但是还是设置了值
            }

            //注意：必须先把5张图集赋值给 rvtBuffersId，再调用 SetRenderTarget。
            //CommandBuffer.SetRenderTarget(RenderTargetIdentifier[]) 在入队时就会拷贝数组内容，
            //原来的写法在每次会话的第一页绘制时数组还是全0（BuiltinRenderTextureType.None），
            //第一页会画到空目标上，图集里留下一个永不绘制的黑块
            rvtBuffersId[0] = _VirtualAlbedos;
            rvtBuffersId[1] = _VirtualNormals;
            rvtBuffersId[2] = _VirtualMasks;
            rvtBuffersId[3] = _VirtualEmisons;
            rvtBuffersId[4] = _VirtualDetails;

            buffer.SetRenderTarget(rvtBuffersId, _VirtualAlbedos.depthBuffer);
            block.SetMatrix(ClusterProverty.ImageMVPId, matrix);
            block.SetFloat(PaddingSizeId, valueBeg);

            //Tile 粒度虚拟贴图：把源贴图的采样范围限制到本 tile 的子矩形。
            //_TileOrigin = (tileX, tileY)/T，_TileScale = 1/T；
            //T=1（useTileVT 关）时 tileX=tileY=0、T=1，origin=0/scale=1，等价于采样整张源贴图。
            int T = ClusterProverty.TilesPerAxisValue;
            float invT = 1.0f / T;
            block.SetVector(TileOriginId, new Vector4((float)node.tileX * invT, (float)node.tileY * invT, 0f, 0f));
            block.SetVector(TileScaleId, new Vector4(invT, invT, 0f, 0f));

            // ===== Profiler 采样点 =====
            // C# 侧只量到「记录命令」的成本（很小）；真正的开销在 GPU（一次 5 渲染目标、CellSize² 的绘制）。
            // 所以这里额外打一个【命令缓冲 GPU marker】：Begin/End 都在同一个 buffer 内、中间没有
            // ExecuteBuffer（buffer.Clear() 会把 marker 切断 → Non-matching EndSample），
            // 因此它是合法的成对采样，能在 Profiler 的 GPU 时间线 / Frame Debugger 里看到。
            Profiler.BeginSample("RVT.PageDraw");
            buffer.BeginSample("RVT.PageDraw");
            textureNode.tileData.RenderTile(buffer, rvtBuffersId, block, screenMesh, matrix);
            buffer.EndSample("RVT.PageDraw");
            Profiler.EndSample();

            //////设置全部贴图
            ////block.SetTexture(AlbedoId, textureNode.albedo);
            ////block.SetTexture(NormalId, textureNode.normal);
            ////block.SetTexture(PBRDataId, textureNode.mask);
            ////block.SetTexture(EmissionId, textureNode.emison);
            ////block.SetTexture(DetailId, textureNode.detial);

            ////绘制未完成
            //buffer.DrawMesh(screenMesh, matrix, material, 0, 3, block);
            block.Clear();
        }


    }
}