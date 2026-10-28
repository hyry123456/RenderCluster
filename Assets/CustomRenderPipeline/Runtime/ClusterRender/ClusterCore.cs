using Common;
using CustomRenderPipline;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Cluster
{

    public class ClusterCore
    {

        public ClusterCore()
        {
            Initialize();
        }

        public static ShaderTagId feedbackTagId = new ShaderTagId("Feedback");

        const string _FeedbackName = "FeedbackTex", _FbZoomTexName = "FeedbackZoomTex";

        ///// <summary>        /// 需要调入的颜色队列        /// </summary>
        //private PoolingList<Color32> WaitCallQueue = new PoolingList<Color32>();

        /// <summary>
        /// GPU 请求压缩：把全屏 Feedback RT 压成定长 (texId, mip) 标志数组后回读，
        /// 替代原来的全屏回读 + CPU 逐像素扫描。键空间 = 贴图数 × mips级数（约 300），
        /// 数据量从 8MB/帧 降到约 1.2KB/帧，且不再有 NativeArray 跨线程生命周期问题
        /// </summary>
        private ComputeShader feedbackCompactCS;
        private ComputeBuffer requestFlags;
        private int kernelClear, kernelCompact;
        private bool warnedNoCompactCS;

        private static readonly int
            FeedbackTexId = Shader.PropertyToID("_FeedbackTex"),
            RequestFlagsId = Shader.PropertyToID("_RequestFlags"),
            SumTextureCountId = Shader.PropertyToID("_SumTextureCount"),
            MaxMipIndexId = Shader.PropertyToID("_MaxMipIndex"),
            FlagsCountId = Shader.PropertyToID("_FlagsCount"),
            TexWidthId = Shader.PropertyToID("_TexWidth"),
            TexHeightId = Shader.PropertyToID("_TexHeight");

        /// <summary>
        /// 等待设置贴图结点数据的数组。Tile 粒度虚拟贴图后 Key 扩展为 (texId, mip, tileIndex) 三元组，
        /// 故改用结构体承载，避免与 DrawTile 需要的 tile 坐标丢失。
        /// </summary>
        private struct WaitSetEntry
        {
            public int texId;
            public int mip;
            public int tileIndex;
            public CacheNode node;
        }
        private List<WaitSetEntry> WaitSetList;

        /// <summary>        /// 直接渲染到的Feedback        /// </summary>
        private RenderTexture feedbackTex;
        /// <summary>        /// 缩放大小后的Feedback        /// </summary>
        private RenderTexture downScaleFeedback;
        private int fbScale, fbZoomScale, mipsLevel;
        private float mipsOffset, mipsChangeSpeed;


        /// <summary>        /// 异步的回读请求        /// </summary>
        private AsyncGPUReadbackRequest m_ReadbackRequest;

        private LevelCache levelCache;
        private TileTable tileTable;

        private System.Threading.Tasks.Task task;
        private bool isExecute = false, isFinish = true;
        /// <summary>Cleanup 之后置真：阻止再发起回读/Task，并防止TextureNodesMap被重新创建</summary>
        private bool disposed = false;

        /// <summary>已经打过“最低级页池已满”日志的贴图编号，避免每帧重复刷屏</summary>
        private HashSet<int> warnedMinsFull = new HashSet<int>();

        /// <summary>
        /// 本帧从 LevelCache 取出的 LRU 淘汰记录（结点, 旧贴图编号），在 ProcessPendingEvict 中处理
        /// </summary>
        private readonly List<KeyValuePair<CacheNode, int>> pendingEvictList
            = new List<KeyValuePair<CacheNode, int>>();

        /// <summary>ProcessPendingEvict 的按贴图分组字典（复用，避免每帧分配）</summary>
        private readonly Dictionary<int, List<CacheNode>> evictGroup
            = new Dictionary<int, List<CacheNode>>();

        private void Initialize()
        {
            ClusterAssembleAsset asset = ClusterControl.Instance.ClusterAssembleAsset;
            fbScale = Convert.ToInt32(asset.FeedbackScale);
            fbZoomScale = Convert.ToInt32(asset.FeedbackZoomScale);
            mipsLevel = Convert.ToInt32(asset.mipsLevel);
            mipsOffset = asset.mipsOffset;
            mipsChangeSpeed = asset.mipsChangeSpeed;

            //Tile 粒度虚拟贴图（useTileVT）开关：
            //关 -> T=1，全部索引公式退化为旧“整图一格”路径，零回归；
            //开 -> 取 asset.TilesPerAxis 作为每轴 tile 数，反馈/分配/采样全部走 pageId。
            int tileAxis = asset.useTileVT ? Mathf.Clamp(asset.TilesPerAxis, 1, 16) : 1;
            ClusterProverty.TilesPerAxisValue = tileAxis;
            Shader.SetGlobalInt(ClusterProverty.TilesPerAxis, tileAxis);

            if (levelCache == null)
            {
                // mipsLevel 直接索引 _LevelData[7] / _LevelPadding[7]，必须限幅
                mipsLevel = Mathf.Clamp(mipsLevel, 1, 7);

                // ===== 图集尺寸保护 =====
                // TileTable 的图集是边长 = (padding·2^(mips-1)·2 + CellSize) · CellCount · mips 的正方形。
                // 超过 SystemInfo.maxTextureSize 时 RenderTexture.Create() 会【静默失败】——
                // 对象非 null 但 IsCreated()==false，采样落到默认值，表现为整屏黑/白或取样错乱，
                // 而且不抛异常、日志里也只有一行 Create failed。调参极易踩到：
                // CellSize=2048 / CellCount=6 / mips=4 会算出 49920²、5 张约 46GB，必失败。
                // 这里提前算出来并降 CellCount，把它变成一条明确的报错。
                int cellCount = Mathf.Max(1, asset.CellCount);
                int cellSize = Mathf.Max(1, asset.CellSize);
                int cellSpan = asset.paddingSize * (1 << (mipsLevel - 1)) * 2 + cellSize;
                long atlasSide = (long)cellSpan * cellCount * mipsLevel;
                int maxSide = SystemInfo.maxTextureSize;
                if (atlasSide > maxSide)
                {
                    int allowed = Mathf.Max(1, (int)(maxSide / (long)cellSpan / mipsLevel));
                    long gb = atlasSide * atlasSide * 4L * 3 / (1024L * 1024L * 1024L);
                    Debug.LogError($"[RVT] 图集边长 {atlasSide} 超过 GPU 上限 {maxSide}" +
                        $"（3 张约 {gb}GB，必然创建失败）。已临时把 CellCount {cellCount} 降为 {allowed}。" +
                        $"请到 assemble.asset 调小 CellSize / CellCount / Mips Level。");
                    cellCount = allowed;
                }

                levelCache = new LevelCache(mipsLevel, cellCount, mipsLevel);
                tileTable = new TileTable(cellSize, cellCount,
                    asset.paddingSize, ClusterControl.Instance.RVT_Mat, mipsLevel);

                // 图集刚创建：回填给所有【已存在】的材质。
                // 必须绑成材质属性（不是纯全局纹理），Unity 才会沿用图集的 anisoLevel=8，
                // 否则掠射角表面（桶身、地面）会丢各向异性过滤，比标准路径明显糊。
                ClusterObject.ApplyAtlasTextures(
                    tileTable._VirtualAlbedos, tileTable._VirtualNormals, tileTable._VirtualMasks);

                // 图集一创建就把预览槽接上，不必等第一帧反馈跑完（否则 Play 的头几帧还是 Missing）
                SyncDebugTex(asset, tileTable);
            }

            Shader.SetGlobalVector(ClusterProverty.VTFeedbackParamId,
                new Vector4(mipsChangeSpeed, asset.CellSize, mipsLevel - 1, mipsOffset));
            //Shader.SetGlobalInt(ClusterProverty.MaxTextureCount, asset.CellCount);
            // 【长度必须恒定】TileTable 现在固定分配 7 个元素（与 UnityInput.hlsl 的 [7] 对齐）。
            // 以前按 mipsLevel 动态分配，Unity 会在首次注册时把全局数组尺寸锁死，
            // 之后把 mips 从 3 调到 4 就命中
            //   "Property (_LevelData) exceeds previous array size (4 vs 3). Cap to previous size."
            // 最高一级被截成 (0,0,0,0) → 该级所有页映射到图集原点 → 整屏黑/什么都不渲染。
            // 固定 7 之后无论 mips 怎么改都不会再被截断。
            Shader.SetGlobalVectorArray(ClusterProverty.LevelData, tileTable.LevelData);
            Shader.SetGlobalVectorArray(ClusterProverty.LevelPadding, tileTable.LevelPadding);

            Shader.SetGlobalVectorArray("_QuarterRange",
                new Vector4[] {
                    new Vector4(0, 0),
                    new Vector4(0.5f, 0),
                    new Vector4(0, 0.5f),
                    new Vector4(0.5f, 0.5f),
                });

            Shader.SetGlobalTexture(ClusterProverty.VirtualAlbedo, tileTable._VirtualAlbedos);
            Shader.SetGlobalTexture(ClusterProverty.VirtualNormal, tileTable._VirtualNormals);
            Shader.SetGlobalTexture(ClusterProverty.VirtualMask, tileTable._VirtualMasks);

            //GPU 请求压缩的 compute（compute shader 不能 Shader.Find，只能走 Resources 引用加载）
            if (feedbackCompactCS == null)
            {
                feedbackCompactCS = Resources.Load<ComputeShader>("Cluster/RVTFeedbackCompact");
                if (feedbackCompactCS != null)
                {
                    kernelClear = feedbackCompactCS.FindKernel("ClearFlags");
                    kernelCompact = feedbackCompactCS.FindKernel("CompactFeedback");
                }
            }
        }

        /// <summary>
        /// 按当前贴图数确保请求标志数组足够大（只增不减，增长才重建）。
        /// 有未完成的回读时不能重建——Release 会让在途请求出错，推迟一帧，
        /// 新贴图的请求本帧丢弃、下一帧自愈
        /// </summary>
        private void EnsureFlagsBuffer(int textureCount)
        {
            int need = textureCount * mipsLevel;
            if (need <= 0) return;
            if (requestFlags != null && requestFlags.count >= need) return;
            if (requestFlags != null && !m_ReadbackRequest.done) return;
            requestFlags?.Release();
            //必须是 Structured 类型，否则绑定不到 compute 的 RWStructuredBuffer
            requestFlags = new ComputeBuffer(need, sizeof(uint), ComputeBufferType.Structured);
        }

        private void CreateFeedbackTexs(int width, int heigh)
        {
            int fbWidth = width / fbScale, fbHeigh = heigh / fbScale;
            if (feedbackTex == null)
            {
                //必须要设置为线性空间，不然颜色值会被调整，导致数组对应错误
                feedbackTex = RenderTexture.GetTemporary(fbWidth, fbHeigh, 32,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

                int zoomWid = width / fbZoomScale, zoomHei = heigh / fbZoomScale;

                downScaleFeedback = RenderTexture.GetTemporary(zoomWid, zoomHei, 32,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                feedbackTex.name = _FeedbackName;
                downScaleFeedback.name = _FbZoomTexName;
            }
            if (feedbackTex.width != fbWidth || feedbackTex.height != fbHeigh)
            {
                RenderTexture.ReleaseTemporary(feedbackTex);
                RenderTexture.ReleaseTemporary(downScaleFeedback);


                feedbackTex = new RenderTexture(fbWidth, fbHeigh, 32, RenderTextureFormat.ARGB32);
                int zoomWid = width / fbZoomScale, zoomHei = heigh / fbZoomScale;

                downScaleFeedback = new RenderTexture(zoomWid, zoomHei, 0, RenderTextureFormat.ARGB32);
                feedbackTex.name = _FeedbackName;
                downScaleFeedback.name = _FbZoomTexName;
            }
        }

        /// <summary>    /// 发起回读请求    /// </summary>
        private void NewRequest(CommandBuffer buffer)
        {
            if (feedbackCompactCS == null)
            {
                //compute 缺失时不再回读；置 isFinish 防止 UpdateRequest 反复处理上一帧的旧请求
                if (!warnedNoCompactCS)
                {
                    warnedNoCompactCS = true;
                    Debug.LogError("RVTFeedbackCompact.compute 加载失败（应为 Resources/Cluster/ 下），RVT 页面请求已停用");
                }
                isFinish = true;
                return;
            }

            int textureCount = TextureNodesMap.Instance.AllTexturesNodeCount;
            EnsureFlagsBuffer(textureCount);
            if (requestFlags == null) { isFinish = true; return; }

            //GPU 压缩：ClearFlags 清空 + CompactFeedback 逐像素置位，
            //两个 kernel 在同一次 dispatch 里连续执行，无需 CPU 参与
            buffer.SetComputeTextureParam(feedbackCompactCS, kernelCompact, FeedbackTexId, feedbackTex);
            buffer.SetComputeIntParam(feedbackCompactCS, SumTextureCountId, TextureNodesMap.Instance.AllTexturesNodeCount);
            buffer.SetComputeIntParam(feedbackCompactCS, MaxMipIndexId, mipsLevel - 1);
            buffer.SetComputeIntParam(feedbackCompactCS, FlagsCountId, requestFlags.count);
            buffer.SetComputeIntParam(feedbackCompactCS, TexWidthId, feedbackTex.width);
            buffer.SetComputeIntParam(feedbackCompactCS, TexHeightId, feedbackTex.height);
            buffer.SetComputeBufferParam(feedbackCompactCS, kernelClear, RequestFlagsId, requestFlags);
            buffer.SetComputeBufferParam(feedbackCompactCS, kernelCompact, RequestFlagsId, requestFlags);
            buffer.DispatchCompute(feedbackCompactCS, kernelClear, (requestFlags.count + 63) / 64, 1, 1);
            Profiler.BeginSample("RVT.Compact");
            buffer.DispatchCompute(feedbackCompactCS, kernelCompact,
                (feedbackTex.width + 7) / 8, (feedbackTex.height + 7) / 8, 1);
            Profiler.EndSample();

            //只回读标志数组（约 1.2KB），不再回读全屏 Feedback（约 8MB）。
            //注意：fbZoomScale 降采样路径已废弃——压缩后全屏请求本来无损，
            //再降采样反而会重新引入 GetMaxFeedback 每个块只保留一个样本的丢请求问题
            m_ReadbackRequest = AsyncGPUReadback.Request(requestFlags);
        }

        /// <summary>        /// 调入最小级别的贴图        /// </summary>
        private void RequestMinsMap(CommandBuffer buffer)
        {
            //调入最低级完成，准备代码检查
            //var system = RenderSystem;
            TextureNodesMap textureNodes = TextureNodesMap.Instance;
            int lodCount = 0;
            //Debug.Log(levelCache.GetEmptyNodeCount(mipsLevel - 1));
            //levelCache.CheckEmptyNodeCount(mipsLevel - 1);

            //必须先处理上帧累积的 LRU 淘汰（降级旧贴图的页表映射、清理被淘汰结点），
            //再执行本帧的页面分配——AllocateNode 会立即重用被淘汰的结点
            ProcessPendingEvict();

            while (textureNodes.needCallMinsMap.Count > 0 && lodCount < 50)
            {
                int index = textureNodes.needCallMinsMap[0];
                textureNodes.needCallMinsMap.RemoveAt(0);
                textureNodes.DequeueMinsMap(index);

                //Tile 粒度虚拟贴图：最低级需为每张贴图分配 T*T 个页（每 tile 一个图集 cell）。
                //已驻留的 tile 直接复用，不再重绘，避免每帧重复提交。
                int T = ClusterProverty.TilesPerAxisValue;
                int Tsq = T * T;
                CacheNode[] minNodes = new CacheNode[Tsq];
                //isNewTile：本帧新分配的 tile 才需要绘制 + 写页表；
                //已驻留的 tile 只保留原样 —— 之前的写法把它们也塞进 InitialTextureNode，
                //而 InitialTextureNode 会把「所有 mip 级别」都重指到最低级结点，
                //等于把已经升级到高 mip 的 tile 全部打回最糊，贴图会周期性变糊。
                bool[] isNewTile = new bool[Tsq];
                bool allocOk = true;
                for (int t = 0; t < Tsq; t++)
                {
                    int val = textureNodes.NodeMipsIsExis(mipsLevel - 1, index, t, out CacheNode existing);
                    if (val == 1) { minNodes[t] = existing; continue; }
                    CacheNode node = levelCache.AllocateNode(mipsLevel - 1);
                    if (node == null || node.texId != -1)
                    {
                        allocOk = false; break;
                    }
                    minNodes[t] = node;
                    isNewTile[t] = true;
                }
                if (!allocOk)
                {
                    //分配失败：释放本帧已分配但未注册的页，整张贴图放回队列下一帧重试
                    for (int t = 0; t < Tsq; t++)
                    {
                        if (minNodes[t] != null && minNodes[t].texId == -1)
                        {
                            minNodes[t].texId = -1;
                            levelCache.ReleaseNode(minNodes[t]);
                        }
                    }
                    if (warnedMinsFull.Add(index))
                        Debug.LogError("最低级页分配失败（异常），已加入重试队列");
                    textureNodes.QueueMinsMap(index);
                    continue;
                }
                //分配全部成功：只对新分配的 tile 设置身份并绘制
                for (int t = 0; t < Tsq; t++)
                {
                    if (!isNewTile[t]) continue;   //已驻留的 tile：不重绘、也不动它的 mip 映射
                    minNodes[t].texId = index;
                    minNodes[t].tileX = t % T;
                    minNodes[t].tileY = t / T;
                    tileTable.DrawTile(minNodes[t], index, buffer);
                }
                //只把新分配的 tile 写进页表（null 项会被 InitialTextureNode 跳过）
                CacheNode[] registerNodes = new CacheNode[Tsq];
                bool anyNew = false;
                for (int t = 0; t < Tsq; t++)
                    if (isNewTile[t]) { registerNodes[t] = minNodes[t]; anyNew = true; }
                //全部 tile 都已驻留时不必再整块上传页表缓冲（InitialTextureNode 会 SetData 整个 buffer）
                if (anyNew)
                    textureNodes.InitialTextureNode(index, registerNodes);  //刚调入的 tile 把所有 mip 先指向最低级结点
                lodCount++;
            }
            //if(textureNodes.needRemoveNode.Count > 0)
            //    Debug.Log("Set Empty = " + textureNodes.needRemoveNode.Count);
            //注意：needRemoveNode 的回收移到下面 WaitSetList 应用之后执行。
            //LRU 淘汰后同一结点可能又在 WaitSetList 里被重新分配给新贴图，
            //先应用再回收，配合 texId != -1 检查可以防止结点被双分配
            //（一边在空闲链表里，一边又被注册给新贴图）

            //task有数据，表示执行过了，如果完成，就进行处理数据
            if (task != null && task.IsCompleted)
            {
                if(WaitSetList == null)
                {
                    isExecute = false; isFinish = true;
                    task = null;
                }
                else
                {
                    int T = ClusterProverty.TilesPerAxisValue;
                    for (int i = 0; i < 15 && i < WaitSetList.Count; i++)
                    {
                        var e = WaitSetList[i];
                        int texId = e.texId, mip = e.mip, tileIndex = e.tileIndex;
                        CacheNode node = e.node;
                    //已属于本页（texId 与 tile 坐标均匹配）→ 仅更新页表映射，无需重绘
                    if (node.texId == texId && node.tileX == tileIndex % T && node.tileY == tileIndex / T)
                    {
                        textureNodes.UpdateTextureData(mip, node.MipsLevel, texId, tileIndex, node);
                    }
                    else
                    {
                        //先把旧主人的同 tile 映射降级（此时 node 仍持旧 tile 坐标，降级目标正确）
                        textureNodes.SetCacheNodeToLowMips(node);
                        //再认领为新页
                        node.texId = texId;
                        node.tileX = tileIndex % T;
                        node.tileY = tileIndex / T;
                        tileTable.DrawTile(node, texId, buffer);
                        //更新位置数据
                        textureNodes.UpdateTextureData(mip, node.MipsLevel, texId, tileIndex, node);
                    }
                }
                    if (WaitSetList.Count < 15)
                    {
                        isExecute = false; isFinish = true;
                        task = null;
                        WaitSetList.Clear();
                        WaitSetList = null;
                    }
                    else
                        WaitSetList.RemoveRange(0, 15);
                }
            }

            //回收贴图移除产生的空闲页（任何 Mips 级别）。
            //texId != -1 表示该结点已被本帧重新分配（LRU 淘汰后的重用），跳过防止双分配
            while(textureNodes.needRemoveNode.Count > 0)
            {
                var removeNode = textureNodes.needRemoveNode[0];
                textureNodes.needRemoveNode.RemoveAt(0);
                textureNodes.DequeueRemoveNode(removeNode);
                if (removeNode.texId == -1)
                    levelCache.ReleaseNode(removeNode);
            }

            //把当前 Debug Mode 选中的图集挂到资产的预览槽上（仅 Inspector 预览用）
            SyncDebugTex(ClusterControl.Instance.ClusterAssembleAsset, tileTable);
        }

        /// <summary>
        /// 把 assemble.asset.debugMode 选中的图集绑定到 rvtDebugTex，**只用于 Inspector 里看原子图集**。
        /// 注意这是运行时临时引用：图集是运行时 new 出来的 RenderTexture，资产里存不下（磁盘上恒为 fileID:0），
        /// 所以 Edit 模式下永远是 None；退出 Play / 域重载时 Unity 会销毁这些 RT，
        /// 若字段还留着旧引用，Inspector 就显示 **Missing (Render Texture)**。
        /// 因此这里除了每帧重绑，还会在 Cleanup 里显式置 null（显示 None 而不是 Missing）。
        /// 它跟画面无关，也不参与任何渲染分支，Normal Debug 的开关不会影响它。
        /// </summary>
        private static void SyncDebugTex(ClusterAssembleAsset asset, TileTable table)
        {
            if (asset == null || table == null) return;
            switch (asset.debugMode)
            {
                case RVTDebugMode.Albedo:
                    asset.rvtDebugTex = table._VirtualAlbedos;
                    break;
                case RVTDebugMode.Normal:
                    asset.rvtDebugTex = table._VirtualNormals;
                    break;
                case RVTDebugMode.Mask:
                    asset.rvtDebugTex = table._VirtualMasks;
                    break;
            }
        }

        /// <summary>
        /// 处理本帧累积的 LRU 淘汰记录（主线程，RequestMinsMap 开头调用）。
        /// 按贴图分组整组处理：同一贴图本帧可能被淘汰多个级别的页，逐条处理会把
        /// 高级别的映射降级到同样已被淘汰的结点上造成串页。组内逻辑见
        /// TextureNodesMap.ProcessTextureEviction
        /// </summary>
        private void ProcessPendingEvict()
        {
            if (pendingEvictList.Count == 0)
                levelCache.TakePendingEvict(pendingEvictList);
            if (pendingEvictList.Count == 0)
                return;

            TextureNodesMap textureNodes = TextureNodesMap.Instance;
            if (textureNodes == null)
            {
                pendingEvictList.Clear();
                return;
            }

            //按贴图分组（字典复用，避免每帧分配）
            evictGroup.Clear();
            foreach (var pair in pendingEvictList)
            {
                int oldTexId = pair.Value;
                if (oldTexId < 0) continue;
                if (!evictGroup.TryGetValue(oldTexId, out var list))
                {
                    list = new List<CacheNode>();
                    evictGroup.Add(oldTexId, list);
                }
                list.Add(pair.Key);
            }
            foreach (var kv in evictGroup)
                textureNodes.ProcessTextureEviction(kv.Key, kv.Value);

            pendingEvictList.Clear();
        }

        /// <summary>        /// 对读入的Feeback贴图进行异步处理        /// </summary>
        /// <param name="flags">GPU 压缩后的请求标志数组，key = texId * mipsLevel + mip，1 = 本帧被请求</param>
        private void ExecuteFeedback(uint[] flags)
        {
            try
            {
                var nodeMap = TextureNodesMap.Instance;
                int T = ClusterProverty.TilesPerAxisValue;
                int Tsq = T * T;
                int sumTex = nodeMap.AllTexturesNodeCount;   // 每个 mip 级别的页数 = 贴图数 × T*T
                int mips = mipsLevel;

                //pageId 去重集合：每个被请求但未就绪的页（texId, mip, tileIndex）只处理一次
                HashSet<long> waitCall = new HashSet<long>();
                for (int key = 0; key < flags.Length; key++)
                {
                    if (flags[key] == 0) continue;
                    int mip = key / sumTex;
                    int rem = key % sumTex;
                    int texId = rem / Tsq;
                    int tileIndex = rem % Tsq;
                    //防御：贴图数动态增长后若标志数组容量没跟上，texId 可能越界
                    if (texId >= nodeMap.RealTextureCount) continue;
                    int val = nodeMap.NodeMipsIsExis(mip, texId, tileIndex, out CacheNode node);
                    if (val == 1)
                    {
                        //如果在队列中，就调入使用中区间，就算已经在使用中也照样调用，只不过放在尾部而已。
                        levelCache.MoveNodeToUse(node);
                    }
                    else if (val == 0)
                    {
                        long k = ((long)texId << 32) | ((long)mip << 16) | (uint)tileIndex;
                        //调入等待队列，等已调入的确定后在进行后序调入
                        if (!waitCall.Contains(k))
                            waitCall.Add(k);
                    }
                    //否则就是不存在的结点
                }
                long[] waitCallCol = new long[waitCall.Count];
                waitCall.CopyTo(waitCallCol);
                Array.Sort(waitCallCol, (a, b) =>
                {
                    long ta = a >> 32, tb = b >> 32;
                    if (ta != tb) return (int)(ta - tb);
                    int ma = (int)((a >> 16) & 0xFFFF), mb = (int)((b >> 16) & 0xFFFF);
                    if (ma != mb) return ma - mb;
                    return (int)((a & 0xFFFF) - (b & 0xFFFF));
                });
                WaitSetList = new List<WaitSetEntry>();
                int curTexTile = -1;
                int finalMipLevel = -1;
                for (int i = 0; i < waitCallCol.Length; i++)
                {
                    long k = waitCallCol[i];
                    int texId = (int)(k >> 32);
                    int mip = (int)((k >> 16) & 0xFFFF);
                    int tileIndex = (int)(k & 0xFFFF);
                    int texTile = texId * Tsq + tileIndex;
                    if (texTile == curTexTile)
                        //同一 (texId, tile) 之前已处理到更高的 mip，低于它的请求已被覆盖
                        if (mip < finalMipLevel) continue;
                        else finalMipLevel = mip;
                    else
                    {
                        curTexTile = texTile;
                        finalMipLevel = mip;
                    }
                    CacheNode node = null;
                    //注意：循环必须限制在 MipsLevel 以内（mips）。最低级页只能由 needCallMinsMap 队列分配，
                    //反馈升级不分配最低级空页，否则 mins 页池会泄漏。
                    while (node == null && finalMipLevel < mips)
                    {
                        int val = nodeMap.NodeMipsIsExis(finalMipLevel, texId, tileIndex, out node);
                        if (val == 1)
                        {
                            levelCache.MoveNodeToUse(node);
                            //判断本身是否已经映射到该结点（请求的 mip 槽位）
                            if (nodeMap.CurrentNodeLevel(mip, texId, tileIndex) != finalMipLevel)
                                //非映射到该位置就进行映射
                                WaitSetList.Add(new WaitSetEntry { texId = texId, mip = mip, tileIndex = tileIndex, node = node });
                        }
                        else if (finalMipLevel < mips - 1)
                        {
                            node = levelCache.AllocateNode(finalMipLevel);
                            //在低级找到了结点
                            if (node != null)
                                //绘制该结点，且将贴图都映射到到该结点
                                WaitSetList.Add(new WaitSetEntry { texId = texId, mip = mip, tileIndex = tileIndex, node = node });
                        }
                        finalMipLevel++;
                    }
                }
                //注意：这里原来在每帧反馈分析结束后调用 SetAllLevelInEmpty() 把全部已使用页
                //一次性清回空闲链表，那是“每帧全清、按需找回”的粗暴实现，已废弃。
                //现在由 AllocateNode 内部的 LRU 淘汰负责腾让空间：不可见的页会逐渐沉到
                //使用链表头部（LRU 端），被下一次分配淘汰，语义等价且不会整表扫描
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
            }


            //while (WaitCallQueue.Count > 0)
            //{
            //    Color32 col = WaitCallQueue.GetValue(0);
            //    WaitCallQueue.RemoveAt(0);
            //    int texId = col.r * 256 + col.g;
            //    int curLevel = col.b;

            //    //if (col.b == mipsLevel - 1)
            //    //{
            //    //    var node = system.GetTextureData(texId);
            //    //    if (node != null && node.useCount > 0)      //判断这个结点是否已经被移除
            //    //        Debug.LogError("最低级Mips不存在，结点不够");
            //    //    continue;
            //    //}

            //    var pair = new Pair<int, int>(col.b, texId);
            //    if (WaitSetTexs.TryGetValue(pair, out var data))
            //        continue;   //已经已经在了，就退出
            //    CacheNode cacheNode = null;

            //    while(cacheNode == null)
            //    {
            //        int val = nodeMap.NodeMipsIsExis(col.b, texId, out CacheNode node);
            //    }
            //    cacheNode = levelCache.GetEmptyNode(col.b);
            //    if (cacheNode == null)
            //    {
            //        cacheNode = nodeMap.GetLowMipsNode(texId, col.b);
            //        if (cacheNode == null)      //被移除的结点，不管
            //            continue;
            //        //var list = WaitSetTexs
            //        if (cacheNode.texId != texId) Debug.LogError("找到了错误的结点");
            //        WaitSetTexs.Add(pair, cacheNode);   //插入，等待之后调入
            //    }
            //    else
            //    {
            //        WaitSetTexs.Add(pair, cacheNode);
            //    }
            //}

            //WaitSetList = WaitSetTexs.ToList();    //转换为数组，放到主线程处理
            //WaitSetTexs.Clear();                //清除这个Map
            //levelCache.SetAllLevelInEmpty();    //腾出空间，把不常用的设置在前面
            //}
            //catch( Exception e )
            //{
            //    Debug.LogError( e.ToString() );
            //}

        }


        /// <summary>    /// 检测回读请求状态    /// </summary>
        private void UpdateRequest(CommandBuffer buffer)
        {
            // RVT mip 混合开关（全局）：0=单 mip（快）/ 1=min-max 线性混合（原实现）
            Shader.SetGlobalFloat(ClusterProverty.RVTTrilinearBlend,
                ClusterControl.Instance.ClusterAssembleAsset.rvtTrilinearBlend ? 1f : 0f);

            // ===== Profiler 采样点（CPU 侧）=====
            // 命名规范：<子系统>.<动作>，便于在 Profiler 的 CPU Usage / Timeline 里直接搜。
            //   Cull.Scene         —— ScriptableRenderContext.Cull（场景剔除）            //   RVT.Feedback       —— RVT 反馈 pass：把整个不透明场景再画一遍到全屏 RT
            //   RVT.RequestMins    —— 分配最低级页 + 应用上帧反馈结果（CPU 侧）
            //   RVT.PageDraw       —— 把贴图页画进图集（CPU marker + GPU marker，GPU 侧才是大头）
            //   RVT.Compact        —— 反馈压缩 compute（把全屏反馈压成请求标志数组）
            Profiler.BeginSample("RVT.RequestMins");
            RequestMinsMap(buffer);       //渲染最低级的Mips，这个要在主线程逐帧检查
            Profiler.EndSample();


            if (m_ReadbackRequest.done && !m_ReadbackRequest.hasError
                && isExecute == false && isFinish == false)
            {
                //GPU 压缩后的请求标志：每项对应一个 (texId, mip) key，1 = 本帧被请求。
                //先拷贝进托管数组再交给后台 Task：GetData 的 NativeArray 生命周期绑定本次回读，
                //托管数组由 GC 保活——彻底消除原来 colors 大数组的跨线程释放竞态
                //（以前 Task 还在读 colors 时 Cleanup 就 Dispose，抛 ObjectDisposedException）
                NativeArray<uint> flags = m_ReadbackRequest.GetData<uint>();
                uint[] requestList = new uint[flags.Length];
                flags.CopyTo(requestList);

                // 更新数据并分发事件
                task = new System.Threading.Tasks.Task(() => ExecuteFeedback(requestList));       //创建一个任务，表示可以等待运行了
                task.Start();
                //Debug.Log("Start");
                isExecute = true;
            }
        }
        /// <summary>	/// 执行Buffer	/// </summary>
        private void ExecuteBuffer(CommandBuffer buffer, ScriptableRenderContext context)
        {
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        public void RenderFeedback(int width, int height, int layerMask, 
            CommandBuffer buffer, ScriptableRenderContext context, 
            CullingResults cullingResults, Camera camera)
        {
            //已销毁（退出播放/管线销毁）：直接返回。
            //必须挡在 Instance 检查之前——Instance getter 会重新创建 TextureNodesMap，
            //那会凭空多出一份 Persistent 页表数组和 ComputeBuffer，又是一次静默泄漏
            if (disposed) return;
            if (TextureNodesMap.Instance == null) return;
#if UNITY_EDITOR
            Initialize();
#endif
            CreateFeedbackTexs(width, height);

            UpdateRequest(buffer);

            ExecuteBuffer(buffer, context);

            //判断读取是否完成，完成的话就渲染新的，等待调用
            //注意：这里不能按“相机静止”跳过 Feedback 重绘。反馈分析除了申请新页面，
            //还负责 MoveNodeToUse 刷新 LRU；且一旦某帧的页面请求因异常丢失
            //（例如图集被占满），必须靠下一帧重新分析才能补上。
            //跳过重绘会让丢失的页面在相机静止期间永远补不回来
            if ((m_ReadbackRequest.done && isFinish) || m_ReadbackRequest.hasError)
            {
                isFinish = false;
                Profiler.BeginSample("RVT.Feedback");

                buffer.SetRenderTarget(feedbackTex);
                buffer.ClearRenderTarget(true, true, Color.white);
                buffer.DisableShaderKeyword("_FEED_BACK_DEBUG");

                // clusterMode 非 SRP 时，反馈也用 Cluster 画：与 GBuffer 共用同一份可见簇列表，
                // 天然保证「请求的页 = 采样的页」（用两套几何画反馈/GBuffer 时它们是会分叉的）。
                bool clusterDrawn = ClusterGBufferRenderer.HasInstance
                    && ClusterGBufferRenderer.Instance.DrawFeedback(buffer);
                Profiler.BeginSample("Cluster.FeedbackDraw");
                ExecuteBuffer(buffer, context);
                Profiler.EndSample();

                if (!clusterDrawn)
                {
                    //设置该摄像机的物体排序模式，目前是渲染普通物体，因此用一般排序方式
                    var sortingSettings = new SortingSettings(camera)
                    {
                        criteria = SortingCriteria.CommonOpaque
                    };

                    //第一次渲染只绘制GBuffer，且GBuffer仅渲染非透明
                    var drawingSettings = new DrawingSettings(
                        feedbackTagId, sortingSettings
                    )
                    {
                        enableDynamicBatching = true,
                        enableInstancing = true,
                    };
                    var filteringSettings = new FilteringSettings(
                        RenderQueueRange.opaque, renderingLayerMask: (uint)layerMask
                    );

                    //进行渲染的执行方法
                    context.DrawRenderers(
                        cullingResults, ref drawingSettings, ref filteringSettings
                    );
                }

                //缩放纹理，并准备读入CPU
                NewRequest(buffer);

                ClusterControl.Instance.ClusterAssembleAsset.debugFeedback = feedbackTex;
                ClusterControl.Instance.ClusterAssembleAsset.debugFeedbackZoom = downScaleFeedback;

                //Profiler 必须成对且在同一作用域：以前 EndSample 写在这个 if 之后，
                //条件不成立的那些帧就会报 Non-matching Profiler.EndSample
                Profiler.EndSample();
            }
        }

        public void Cleanup()
        {
            if (disposed) return;
            disposed = true;

            //仍须先等在途的反馈分析 Task：Task 现在只读托管数组（无释放竞态），
            //但会操作 levelCache / TextureNodesMap 的链表，不能与下面的 Dispose 并发
            if (task != null && !task.IsCompleted)
            {
                try
                {
                    //Task 体现在只迭代约 300 条标志，微秒级完成；超时兜底防止极端情况死锁
                    task.Wait(2000);
                }
                catch (Exception ex)
                {
                    Debug.LogError("等待反馈分析任务结束时异常: " + ex);
                }
            }
            task = null;
            //WaitSetList 由 Task 写引用、主线程消费，Task 结束后无人再用，可安全置空
            WaitSetList = null;

            //GPU 请求压缩后不再有 colors 大数组；释放请求标志 ComputeBuffer
            requestFlags?.Release();
            requestFlags = null;

            // 清掉写进资产的【运行时调试引用】：下面要销毁图集/反馈 RT，
            // 留着旧引用就是一个悬空对象 → Inspector 里显示 "Missing (Render Texture)"。
            // 置 null 后回到 "None"，Play 时再由 SyncDebugTex / RenderFeedback 重新接上。
            var debugAsset = ClusterControl.Instance.ClusterAssembleAsset;
            if (debugAsset != null)
            {
                debugAsset.rvtDebugTex = null;
                debugAsset.debugFeedback = null;
                debugAsset.debugFeedbackZoom = null;
            }

            // 释放后立刻置 null：Cleanup 可能被调用多次（退出 Play + 域重载 + 管线 Dispose），
            // 不置 null 会二次 Dispose 已释放/已销毁的对象
            if (levelCache != null) { levelCache.Dispose(); levelCache = null; }
            if (tileTable != null) { tileTable.Dispose(); tileTable = null; }
            //页表 NativeArray 与 ComputeBuffer 同样是 Persistent 分配
            TextureNodesMap.Instance?.Dispose();
            pendingEvictList.Clear();
            evictGroup.Clear();
        }
    }
}