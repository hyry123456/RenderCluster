using Common;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace Cluster
{
    public struct TextureMapNode
    {
        public Texture2D albedo; public Texture2D normal;
        public Texture2D mask; public Texture2D emision;
        public Texture2D detail;        //detail只需要一张
        public TextureMapNode(Texture2D ab, Texture2D no, Texture2D pbr, Texture2D em,
            Texture2D de)
        {
            albedo = ab == null ? Texture2D.whiteTexture : ab;
            normal = no == null ? Texture2D.normalTexture : no;
            mask = pbr == null ? Texture2D.whiteTexture : pbr;
            emision = em == null ? Texture2D.blackTexture : em;
            detail = de == null ? Texture2D.grayTexture : de;
        }

        public TextureMapNode(Texture2D[] textures)
        {
            albedo = textures[0];
            normal = textures[1];
            mask = textures[2];
            emision = textures[3];
            detail = textures[4];
        }
    }

    /// <summary>    /// 贴图的结构体节点    /// </summary>
    public struct RVTTransferNode
    {
        public int width;
        public int height;
        public int mipsLevel;
    }

    /// <summary>    /// 渲染PBR时的贴图结点，存储贴图和使用情况    /// </summary>
    public class TexturesNode
    {
        //public Texture2D albedo;
        //public Texture2D normal;
        //public Texture2D mask;
        //public Texture2D emison;
        //public Texture2D detial;
        public TileUnility tileData;
        public int useCount;
    }

    public class TextureNodesMap
    {
        /// <summary>每轴 tile 数 T（useTileVT 关时为 1，旧路径）；Tsq = T*T；pagesPerMip = 贴图数 × Tsq</summary>
        private int tileAxis = 1;
        private int Tsq = 1;
        private int pagesPerMip = 0;

        private TextureNodesMap() 
        {
            var assemble = ClusterControl.Instance.ClusterAssembleAsset;
            int mipsLevel = Convert.ToInt32(assemble.mipsLevel);
            //Tile 粒度：每贴图 T*T 页，每个 mip 级别页数为 贴图数 × Tsq
            tileAxis = ClusterProverty.TilesPerAxisValue;
            Tsq = tileAxis * tileAxis;
            pagesPerMip = assemble.TexturesCount * Tsq;

            allMipsCacheNode = new CacheNode[mipsLevel][];
            for(int i = 0; i < allMipsCacheNode.Length; i++)
                allMipsCacheNode[i] = new CacheNode[pagesPerMip];

            textureMap = new Dictionary<TextureMapNode, int>();
            texturesNodes = new TexturesNode[assemble.TexturesCount];

            textureNodes = new NativeArray<RVTTransferNode>(mipsLevel * pagesPerMip, Allocator.Persistent);
            texturesBuffer = new ComputeBuffer(textureNodes.Length, Marshal.SizeOf<RVTTransferNode>(),
                ComputeBufferType.Default);

            //_SumTextureCount = 每个 mip 级别的页数，与 Shader 侧 pageId 公式保持一致
            Shader.SetGlobalInt(ClusterProverty.SumTextureCount, pagesPerMip);
            Shader.SetGlobalBuffer(ClusterProverty.TextureNodesBuffer, texturesBuffer);
        }


        private static TextureNodesMap instance;
        public static TextureNodesMap Instance { get
            {
                //if(instance == null)
                //    instance = new TextureNodesMap();
                return instance;
            } 
        }

        public static TextureNodesMap CreateInstance()
        {
            if (instance == null)
            {
                //在构造前先把 T 设置成当前资产配置，消除对 ClusterCore.Initialize 的时序依赖：
                //场景物体 OnEnable 调用本方法时，DefferPipeline 可能尚未首次 Render（那时才跑
                //ClusterCore.Initialize 设置 TilesPerAxisValue）。若此处用默认 T=1 构造页表，
                //而 useTileVT 实际开启（T=8），FeedbackPass 编码的 pageId 会按 T=8 展开、
                //页表却只有 T=1 的槽位，导致 allMipsCacheNode[mip][pageIndex] 越界崩溃。
                var asset = ClusterControl.Instance.ClusterAssembleAsset;
                int tileAxis = asset.useTileVT ? Mathf.Clamp(asset.TilesPerAxis, 1, 16) : 1;
                ClusterProverty.TilesPerAxisValue = tileAxis;
                Shader.SetGlobalInt(ClusterProverty.TilesPerAxis, tileAxis);
                instance = new TextureNodesMap();
            }
            return instance;
        }

        /// <summary>
        /// 释放页表数组与ComputeBuffer，在渲染管线销毁时调用，
        /// 否则 Persistent 分配的 NativeArray 会在每次运行结束后被 Unity 报内存泄漏
        /// </summary>
        public void Dispose()
        {
            if (textureNodes.IsCreated)
                textureNodes.Dispose();
            if (texturesBuffer != null)
                texturesBuffer.Dispose();
            //注意：PoolingList 没有 Clear()，只有 RemoveAll()（size 归零）。
            //四个队列/镜像集合必须成对清空，保持「队列内容 == 去重集合内容」的不变式
            needCallMinsMap.RemoveAll();
            minsMapQueued.Clear();
            needRemoveNode.RemoveAll();
            removeQueued.Clear();
            instance = null;     //置空后下次 CreateInstance 会重建，避免访问已释放的数组
        }


        /// <summary>    /// 根据贴图映射到贴图结点位置  /// </summary>
        private Dictionary<TextureMapNode, int> textureMap;

        /// <summary>        
        /// 所有Mips等级对应的图片结点，被调入的都在里面，
        /// 一旦被移除，也表示这个图片被这个图片完全没被调入
        /// </summary>
        private CacheNode[][] allMipsCacheNode;

        //public CacheNode[][] AllMipsCacheNode => allMipsCacheNode;

        /// <summary>        /// 贴图对应的序列        /// </summary>
        private TexturesNode[] texturesNodes;

        /// <summary>
        /// 读取所有的图片结点数量，这只是结点数量，不一定是真正的贴图数量，
        /// 还有这个数据只能在外界读，在内部读取因为allMipsCacheNode
        /// 会更新，导致不准确
        /// </summary>
        public int AllTexturesNodeCount
        {
            get
            {
                return allMipsCacheNode[0].Length;
            }
        }

        /// <summary>实际贴图数（不含 tile 维度），用于反馈解码的 texId 越界过滤</summary>
        public int RealTextureCount => texturesNodes.Length;

        public int MipsLevel
        {
            get
            {
                return allMipsCacheNode.Length;
            }
        }

        /// <summary>
        /// 存储所有级别贴图数据用的数组，
        /// 将多级数据都存放在这个线性数据中
        /// </summary>
        private NativeArray<RVTTransferNode> textureNodes;
        private ComputeBuffer texturesBuffer;

        /// <summary>       /// 存储需要调入的最小级别的贴图        /// </summary>
        public PoolingList<int> needCallMinsMap = new PoolingList<int>();
        public PoolingList<CacheNode> needRemoveNode = new PoolingList<CacheNode>();

        //needCallMinsMap 的内容镜像，用于 QueueMinsMap 去重，防止同一贴图重复入队
        private HashSet<int> minsMapQueued = new HashSet<int>();

        //needRemoveNode 的内容镜像。同一个 CacheNode 可能被多个 Mips 级别共享，
        //移除贴图时会多次遇到，去重防止同一结点被回收两次破坏链表
        private HashSet<CacheNode> removeQueued = new HashSet<CacheNode>();

        /// <summary>        /// 标准BRDF的贴图分配方式        /// </summary>
        public int AllocateTextureId(TextureMapNode texturesNode)
        {
            int index;
            //在结点中就使用个数加一
            if (textureMap.TryGetValue(texturesNode, out index))
            {
                texturesNodes[index].useCount++;
                return index;
            }
            BRDF_Tile tile = new BRDF_Tile();
            tile.albedo = texturesNode.albedo;
            tile.normal = texturesNode.normal;
            tile.mask = texturesNode.mask;
            tile.emison = texturesNode.emision;
            tile.detial = texturesNode.detail;

            //否则就创建结点
            for (int i = 0; i < texturesNodes.Length; i++)
            {
                if (texturesNodes[i] == null)
                {
                    texturesNodes[i] = new TexturesNode();
                    texturesNodes[i].tileData = tile;

                    textureMap.Add(texturesNode, i);
                    texturesNodes[i].useCount = 1;
                    QueueMinsMap(i);         //声明需要调入该贴图
                    return i;
                }
                //如果有找到空闲位置，就塞到空闲位置上
                else if (texturesNodes[i].useCount == 0)
                {
                    texturesNodes[i].tileData = tile;

                    textureMap.Add(texturesNode, i);//这里只需要加入，当useCount=0时会直接执行删除
                    texturesNodes[i].useCount++;
                    QueueMinsMap(i);         //声明需要调入该贴图
                    return i;
                }
            }

            //没有空闲位置，扩大数组。
            TexturesNode[] temTexNodes = new TexturesNode[texturesNodes.Length * 2];
            int j = 0;
            for (; j < texturesNodes.Length; j++)
                temTexNodes[j] = texturesNodes[j];

            temTexNodes[j] = new TexturesNode();    //创建一个新结点
            temTexNodes[j].tileData = tile;
            temTexNodes[j].useCount = 1;

            textureMap.Add(texturesNode, j);
            texturesNodes = temTexNodes;        //替换为新的
            QueueMinsMap(j);         //声明需要调入该贴图

            int preLineCount = allMipsCacheNode[0].Length;  //存储之前一行的数量（= 旧贴图数 × Tsq）
            int newLineCount = temTexNodes.Length * Tsq;     //新贴图数 × Tsq
            //用一个多维数组，存储每一张图片使用的Mips级别和位置，
            //因此这个数组只需要存储位置结点即可，有调入的就存储结点，没有调入的就没有结点
            //CacheNode[][] tem = new CacheNode[allMipsCacheNode.Length][];
            for (int i = 0; i < allMipsCacheNode.Length; i++)
            {
                CacheNode[] tem = new CacheNode[newLineCount];
                //把老的拷贝到新的
                for (int k = 0; k < preLineCount; k++)
                    tem[k] = allMipsCacheNode[i][k];
                //清理老的数据
                Array.Clear(allMipsCacheNode[i], 0, allMipsCacheNode[i].Length);
                allMipsCacheNode[i] = tem;
            }

            NativeArray<RVTTransferNode> temArr = new NativeArray<RVTTransferNode>(
                allMipsCacheNode.Length * newLineCount, Allocator.Persistent);

            for (int i = 0; i < allMipsCacheNode.Length; i++)  //循环Mips
            {
                for (int k = 0; k < preLineCount; k++)  //赋值之前的数据
                    temArr[i * newLineCount + k] = textureNodes[i * preLineCount + k];
            }
            texturesBuffer.Dispose();
            texturesBuffer = new ComputeBuffer(temArr.Length, Marshal.SizeOf<RVTTransferNode>(),
                ComputeBufferType.Default);
            texturesBuffer.SetData(temArr);

            textureNodes.Dispose();
            textureNodes = temArr;

            pagesPerMip = newLineCount;
            Shader.SetGlobalInt(ClusterProverty.SumTextureCount, newLineCount);
            Shader.SetGlobalBuffer(ClusterProverty.TextureNodesBuffer, texturesBuffer);

            return j;
        }

        /// <summary>        /// 标准的移除BRDF结点的方法        /// </summary>
        public void RemoveTextureId(int index)
        {
            TexturesNode tN = texturesNodes[index];
            if (tN == null)
            {
                Debug.LogError("TextureNode is Null");
            }
            BRDF_Tile tile = (BRDF_Tile)tN.tileData;
            TextureMapNode mapNode = new TextureMapNode(
                tile.albedo, tile.normal, tile.mask, tile.emison, tile.detial);
            if (textureMap.TryGetValue(mapNode, out index))
            {
                texturesNodes[index].useCount--;
                //该贴图空闲了，从字典中移除，且在图片页表中全部移除
                if (texturesNodes[index].useCount == 0)
                {
                    textureMap.Remove(mapNode);
                    texturesNodes[index] = null;

                    //从Buffer中移除该图片，声明这个图片已经不在RVT中（含所有 tile）
                    bool minExists = false;
                    for (int t = 0; t < Tsq; t++)
                    {
                        int pageIndex = index * Tsq + t;
                        for (int i = 0; i < allMipsCacheNode.Length; i++)
                        {
                            var node = allMipsCacheNode[i][pageIndex];
                            if (node != null)
                            {
                                node.texId = -1;
                                //任何级别/任何 tile 占用的页都要回收，QueueRemoveNode 内部去重
                                QueueRemoveNode(node);
                                if (i == allMipsCacheNode.Length - 1)
                                    minExists = true;
                            }
                            allMipsCacheNode[i][pageIndex] = null;
                        }
                    }
                    if (!minExists)
                        Debug.LogError("最低级别结点不存在");
                    return;
                }
            }
            else
            {
                Debug.LogError("不存在贴图，却调出");
            }
        }


        public void RemoveTerrainId(int[] indexs)
        {
            for(int i = 0;i < indexs.Length;i++)
            {
                int id = indexs[i];
                texturesNodes[id] = null;
                //从Buffer中移除该图片，声明这个图片已经不在RVT中（含所有 tile）
                bool minExists = false;
                for (int t = 0; t < Tsq; t++)
                {
                    int pageIndex = id * Tsq + t;
                    for (int j = 0; j < allMipsCacheNode.Length; j++)
                    {
                        var node = allMipsCacheNode[j][pageIndex];
                        if (node != null)
                        {
                            node.texId = -1;
                            if (j == allMipsCacheNode.Length - 1)//最后一个 mip 时才移除
                            {
                                minExists = true;
                                //必须走去重入队：同一结点被多个 Mips 级别共享时，
                                //直接 Add 会造成队列里出现重复结点，ReleaseNode 执行两次
                                //会把同一个物理页挂进空闲链表两次，之后被两张贴图同时分配（串页）
                                QueueRemoveNode(node);
                            }
                        }
                        allMipsCacheNode[j][pageIndex] = null;
                    }
                }
                if (!minExists)
                    Debug.LogError("最低级别结点不存在");
            }
        }

        public TexturesNode GetTextureData(int textureIndex)
        {
            return texturesNodes[textureIndex];
        }

        /// <summary>
        /// 设置贴图结点的初始数据：把每个 tile 的最低级页都映射到对应 tile 的最低级结点，
        /// 使该贴图所有 mips 级别（尚未被反馈升级前）都先显示最低级（最糊但一定存在）。
        /// minNodes 长度必须 == Tsq，按 tileIndex 顺序排列（tileIndex = tileY*T + tileX）。
        /// </summary>
        public void InitialTextureNode(int texId, CacheNode[] minNodes)
        {
            if (minNodes == null || minNodes.Length != Tsq)
            {
                Debug.LogError("InitialTextureNode: minNodes 长度必须为 Tsq，当前 Tsq=" + Tsq);
                return;
            }
            for (int t = 0; t < Tsq; t++)
            {
                CacheNode node = minNodes[t];
                if (node == null) continue;

                // 关键修复：同一个 CacheNode 对象会被 LevelCache 回收后复用到【不同】贴图。
                // 而本方法会把该 node 同时写进 allMipsCacheNode 的「所有 mip 级别」同一 (texId,tile) 槽位，
                // 旧身份(texId/tile)下的全部 mip 槽引用并未清除 —— 一旦 node.texId 被改成新 texId，
                // 旧贴图的 mip 槽就指向了新 texId 的 node → 页表串页（"贴图29的槽被贴图31占用"）。
                // 因此在认领新身份前，先按旧 (texId,tile) 把该 node 在所有 mip 级别的残留引用清空。
                if (node.texId != -1)
                {
                    int oldTile = node.tileY * tileAxis + node.tileX;
                    int oldPageIndex = node.texId * Tsq + oldTile;
                    for (int i = 0; i < allMipsCacheNode.Length; i++)
                    {
                        if (allMipsCacheNode[i][oldPageIndex] == node)
                            allMipsCacheNode[i][oldPageIndex] = null;
                    }
                }

                node.texId = texId;
                node.tileX = t % tileAxis;
                node.tileY = t / tileAxis;
                //设置这张贴图的所有 mips 级别都先映射到该 tile 的最低级结点
                for (int i = 0; i < allMipsCacheNode.Length; i++)
                {
                    int pageIndex = texId * Tsq + t;
                    int bufIdx = i * pagesPerMip + pageIndex;
                    var data = textureNodes[bufIdx];
                    data.mipsLevel = allMipsCacheNode.Length - 1;   //最低级
                    data.width = node.WidthId;
                    data.height = node.HeightId;
                    textureNodes[bufIdx] = data;
                    allMipsCacheNode[i][pageIndex] = node;          //声明这个位置映射到了这张贴图上
                }
            }
            //一次性上传整个缓冲，避免 Tsq×mipsLevel 次单条 SetData
            texturesBuffer.SetData(textureNodes);
        }

        public void Check()
        {
            for(int i = 0; i < MipsLevel; i++)
            {
                int len = allMipsCacheNode[i].Length;
                for(int p = 0; p < len; p++)
                {
                    var n = allMipsCacheNode[i][p];
                    if (n != null)
                    {
                        int texId = p / Tsq;
                        if (n.texId != texId)
                            Debug.LogError("结点出错: page=" + p + " texId=" + n.texId + " 期望=" + texId);
                    }
                }
            }
        }

        /// <summary>
        /// 更新贴图某页（texId, tileIndex）在某 mips 级别的结点映射数据。
        /// 索引公式与 Shader 侧 pageId 保持一致：bufIdx = mip * pagesPerMip + texId*Tsq + tileIndex
        /// </summary>
        public void UpdateTextureData(int mipBegin, int mipEnd, int texId, int tileIndex, CacheNode node)
        {
            RVTTransferNode texNode;
            for(int mipLevel = mipBegin; mipLevel <= mipEnd; mipLevel++)
            {
                int pageIndex = texId * Tsq + tileIndex;
                int bufIdx = mipLevel * pagesPerMip + pageIndex;
                texNode = textureNodes[bufIdx];
                texNode.width = node.WidthId; texNode.height = node.HeightId;
                texNode.mipsLevel = node.MipsLevel;
                textureNodes[bufIdx] = texNode;
                texturesBuffer.SetData(textureNodes, bufIdx, bufIdx, 1);
                node.texId = texId;
                allMipsCacheNode[mipLevel][pageIndex] = node;      //声明这个位置映射到这张贴图上
            }
        }

        /// <summary>
        /// 把使用当前结点的贴图全部溢出，释放传入的结点位置。
        /// 注意与 ProcessTextureEviction（LRU 淘汰的整组处理）区分：
        /// 本方法用于反馈升级路径——结点已被淘汰重用给新贴图，先把「当前主人」
        /// （node.texId）在该结点所属 tile 上的映射降级挤走，随后调用方会把结点夺给申请者。
        /// 仅处理 node 自身所在的 tile（tileX/tileY）。
        /// </summary>
        public void SetCacheNodeToLowMips(CacheNode node)
        {
            if (node.texId == -1) return;   //未调入的贴图，直接退出

            int tileIndex = node.tileY * tileAxis + node.tileX;

            CacheNode lowMips = null;
            for(int i = node.MipsLevel + 1; i < allMipsCacheNode.Length; i++)
            {
                int pageIndex = node.texId * Tsq + tileIndex;
                if (allMipsCacheNode[i][pageIndex] != null)    //找到最低级，一定存在的，因为有最低Mips存在
                {
                    lowMips = allMipsCacheNode[i][pageIndex];
                    break;
                }
            }
            if (lowMips == null) return;   //没有更低级的结点可降级，保持原映射

            //数据校验（替换原来的 UnEqual 裸判断）：降级目标必须仍属于当前主人，
            //否则说明页表已损坏（目标结点可能已被淘汰重用），此时降级会把映射
            //指到别人的页上造成串页，必须中止并报警
            if (lowMips.texId != node.texId)
            {
                Debug.LogError("RVT页表数据错误：贴图 " + node.texId + " 级别 "
                    + node.MipsLevel + " 的降级目标结点被贴图 " + lowMips.texId + " 占用，已跳过本次降级");
                return;
            }

            for (int i = 0; i < allMipsCacheNode.Length; i++)
            {
                int pageIndex = node.texId * Tsq + tileIndex;
                //若当前结点占据了该贴图这个 tile 这个级别的映射，就切换
                if (allMipsCacheNode[i][pageIndex] != node) continue;

                int bufIdx = i * pagesPerMip + pageIndex;
                var texNode = textureNodes[bufIdx];

                texNode.height = lowMips.HeightId; texNode.width = lowMips.WidthId;
                texNode.mipsLevel = lowMips.MipsLevel;
                textureNodes[bufIdx] = texNode;   //更新到这个新节点
                texturesBuffer.SetData(textureNodes, bufIdx, bufIdx, 1);

                allMipsCacheNode[i][pageIndex] = lowMips;      //更改映射方向
            }
        }

        /// <summary>
        /// LRU 淘汰的整组处理（主线程调用）：同一贴图本帧可能被淘汰多个级别的页，
        /// 且每个 tile 的页表相互独立。逐 tile 处理——先找该 tile 本帧未被淘汰的
        /// 最低存活级别作为降级目标，再把该 tile 所有被淘汰结点占据的映射统一切到目标；
        /// 整组无存活页则清空该 tile 映射并重新入队重画（RequestMinsMap 会按 tile 跳过已驻留者）
        /// </summary>
        /// <param name="texId">被淘汰的贴图编号</param>
        /// <param name="evictedNodes">本帧该贴图被淘汰的全部结点</param>
        public void ProcessTextureEviction(int texId, List<CacheNode> evictedNodes)
        {
            if (texId < 0 || evictedNodes == null || evictedNodes.Count == 0) return;

            // 逐 tile 独立处理
            for (int t = 0; t < Tsq; t++)
            {
                int pageIndex = texId * Tsq + t;

                //1. 找该 tile 本帧未被淘汰的最低存活级别作为降级目标
                int surviveLevel = -1;
                for (int i = 0; i < allMipsCacheNode.Length; i++)
                {
                    var n = allMipsCacheNode[i][pageIndex];
                    if (n == null) continue;
                    bool evicted = false;
                    for (int k = 0; k < evictedNodes.Count; k++)
                    {
                        if (evictedNodes[k] == n) { evicted = true; break; }
                    }
                    if (!evicted) { surviveLevel = i; break; }
                }

                if (surviveLevel < 0)
                {
                    //本帧该 tile 所有页都被淘汰：失去驻留根，清空映射并重新入队重画
                    for (int i = 0; i < allMipsCacheNode.Length; i++)
                        allMipsCacheNode[i][pageIndex] = null;
                    if (GetTextureData(texId) != null)
                        QueueMinsMap(texId);
                    continue;
                }

                CacheNode target = allMipsCacheNode[surviveLevel][pageIndex];
                //数据校验：存活结点必须仍属于该贴图
                if (target.texId != texId)
                {
                    Debug.LogError("RVT页表数据错误：贴图 " + texId + " 级别 " + surviveLevel
                        + " 的存活结点被贴图 " + target.texId + " 占用，已跳过本次降级");
                    continue;
                }

                //2. 把该 tile 所有被淘汰结点占据的映射统一切到目标结点
                for (int k = 0; k < evictedNodes.Count; k++)
                {
                    var node = evictedNodes[k];
                    // 仅处理属于本 tile 的被淘汰结点
                    if (node.tileY * tileAxis + node.tileX != t) continue;
                    for (int i = 0; i < allMipsCacheNode.Length; i++)
                    {
                        if (allMipsCacheNode[i][pageIndex] != node) continue;

                        int bufIdx = i * pagesPerMip + pageIndex;
                        var texNode = textureNodes[bufIdx];

                        texNode.height = target.HeightId; texNode.width = target.WidthId;
                        texNode.mipsLevel = target.MipsLevel;
                        textureNodes[bufIdx] = texNode;   //更新到这个新节点
                        texturesBuffer.SetData(textureNodes, bufIdx, bufIdx, 1);

                        allMipsCacheNode[i][pageIndex] = target;      //更改映射方向
                    }
                }
            }
        }

        /// <summary>
        /// 最低级页被 LRU 淘汰时调用：清空该贴图的所有页表映射（CPU 端，含所有 tile）。
        /// 注意：GPU 页表 textureNodes 里的条目暂不清理——着色器没有"无效页"回退逻辑，
        /// 写入全零位置会直接采样到图集 (0,0) 槽位；保持旧值最多错显示一帧，
        /// 随后贴图重新入 needCallMinsMap 重画即恢复
        /// </summary>
        public void EvictTextureNodes(int texId)
        {
            for (int t = 0; t < Tsq; t++)
            {
                int pageIndex = texId * Tsq + t;
                for (int i = 0; i < allMipsCacheNode.Length; i++)
                    allMipsCacheNode[i][pageIndex] = null;
            }
        }

        /// <summary>
        /// 把贴图加入最低级调入队列（带去重，防止同一贴图重复入队导致重复分配页面）
        /// </summary>
        public void QueueMinsMap(int index)
        {
            if (minsMapQueued.Add(index))
                needCallMinsMap.Add(index);
        }

        /// <summary>消费方调用：从队列取出贴图后同步去重集合</summary>
        public void DequeueMinsMap(int index)
        {
            minsMapQueued.Remove(index);
        }

        /// <summary>入队待回收结点（带去重，防止同一结点重复回收破坏链表）</summary>
        public void QueueRemoveNode(CacheNode node)
        {
            if (removeQueued.Add(node))
                needRemoveNode.Add(node);
        }

        /// <summary>消费方调用：取出待回收结点后同步去重集合</summary>
        public void DequeueRemoveNode(CacheNode node)
        {
            removeQueued.Remove(node);
        }

        /// <summary>
        /// 从传入的MipsLevel开始，找更低级的结点（同 tile）
        /// </summary>
        public CacheNode GetLowMipsNode(int texId, int mips, int tileIndex)
        {
            for (int i = mips + 1; i < allMipsCacheNode.Length; i++)
            {
                int pageIndex = texId * Tsq + tileIndex;
                if (allMipsCacheNode[i][pageIndex] != null)    //找到最低级，一定存在的，因为有最低Mips存在
                {
                    return allMipsCacheNode[i][pageIndex];
                }
            }
            return null;
        }

        /// <summary>
        /// 判断该贴图的这个 tile 的 Mips 等级是否存在与虚拟贴图中
        /// </summary>
        /// <param name="mips">Mips等级</param>
        /// <param name="texId">贴图编号</param>
        /// <param name="tileIndex">tile 序号（tileY*T + tileX）</param>
        /// <returns>-1删除的结点，0为结点不存在，1为结点以及存在</returns>
        public int NodeMipsIsExis(int mips, int texId, int tileIndex, out CacheNode node)
        {
            node = allMipsCacheNode[mips][texId * Tsq + tileIndex];
            if(node == null)
            {
                //Debug.LogError("未调入贴图，却访问");
                return -1;
            }
            if (node.MipsLevel == mips)
                return 1;
            return 0;
        }

        public int CurrentNodeLevel(int mips, int texId, int tileIndex)
        {
            var node = allMipsCacheNode[mips][texId * Tsq + tileIndex];
            if (node == null)
            {
                //Debug.LogError("未调入贴图，却访问");
                return -1;
            }
            return node.MipsLevel;
        }
    }
}