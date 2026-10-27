using CustomRenderPipline.Custer;
using System.ComponentModel;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;


namespace Cluster
{
    /// <summary>阴影用哪条路径渲染。</summary>
    public enum ClusterShadowMode
    {
        /// <summary>Unity 常规路径：逐物体剔除 + SRP Batcher + context.DrawShadows。</summary>
        SRP = 0,
        /// <summary>真正的 cluster 路径：一次 DrawProcedural 把全部簇画一遍（顶点拉取，不绑顶点缓冲）。
        /// 【当前不做剔除】：视锥剔除交给 GPU 的 NDC 裁剪，阴影裁剪留到以后。</summary>
        Cluster = 1,
        /// <summary>ClusterDebug：与 Cluster 【完全相同】的 GPU 路径，只额外限频打印统计信息。
        /// 仅调试用，不要用来测性能（打印本身有开销）。</summary>
        ClusterDebug = 2,
    }

    /// <summary>   /// 对贴图的缩放大小选择    /// </summary>
    public enum SectorScale
    {
        Full = 1,
        Half = 2,
        Quarter = 4,
        Eigth = 8,
    }

    public enum MipsLevel
    {
        Quarter = 4,
        ninth = 8,
    }

    public enum RVTDebugMode
    {
        Albedo,
        Normal,
        Mask,
        Emission,
        Detail,
    }


    /// <summary>
    /// 簇集合文件，存储所有簇文件的映射
    /// </summary>
    public class ClusterAssembleAsset : ScriptableObject
    {

        /// <summary>        /// 默认创建的贴图组大小        /// </summary>
        [Min(1)]
        public int TexturesCount = 10;

        ///// <summary>        /// 渲染簇的材质        /// </summary>
        //public Shader ClusterShader;

        ///// <summary>      /// 裁剪簇用的材质        /// </summary>
        //public ComputeShader CullingCompute;

        /// <summary>        /// 最高行可存储的图片数量        /// </summary>
        public int CellCount    = 1;
        /// <summary>        /// 最高行的图片大小        /// </summary>
        //注意：CellSize 会进入主Pass的Mip计算公式（uv = baseUV * CellSize 后求导数），
        //因此与 FeedbackScale 强耦合：FeedbackScale 必须保持 Full，否则反馈Pass算出的
        //Mip会比主Pass粗一级，主Pass读取的页表层级永远不被分配，表现为贴图发糊或丢失
        public int CellSize     = 512;
        /// <summary>        /// 填充大小        /// </summary>
        public int paddingSize  = 1;
        [Range(1, 7)]
        ///// <summary>        /// 最大Mips级别        /// </summary>
        public int mipsLevel = 3;
        //public MipsLevel mipsLevel = MipsLevel.Quarter;
        [Range(-5f, 5f)]
        /// <summary>        /// Mips偏移，用来控制Mips映射情况        /// </summary>
        public float mipsOffset = 1.0f;
        /// <summary>        /// Mips改变速度，控制更改Mips的速度        /// </summary>
        [Range(0f, 1f)]
        public float mipsChangeSpeed = 0.5f;

        public SectorScale FeedbackScale = SectorScale.Half,
            FeedbackZoomScale = SectorScale.Half;

        public RenderTexture debugFeedback,
            debugFeedbackZoom;

        public RenderTexture rvtDebugTex;
        public RVTDebugMode debugMode;

        /// <summary>
        /// RVT mip 混合方式。**这是 per-pixel 成本最大的一刀**：
        /// 关（false，默认）= 每个 tap 只取【一个】mip ⇒ 页表随机读与图集取样全部减半；
        /// 开（true）= 原实现，取 min/max 两个 mip 再线性混合（mip 间三线性），画面更平滑但慢一倍。
        /// 业界（UE RVT）同样是每页一个 mip、不做 mip 间混合。
        /// </summary>
        public bool rvtTrilinearBlend = false;

        public bool UseRVT = true;

        /// <summary>
        /// 阴影渲染走哪条路：
        ///   SRP     = Unity 常规（逐物体剔除 + SRP Batcher + context.DrawShadows）；
        ///   Cluster = 逐簇 GPU 剔除 + 每级联一次 DrawProceduralIndirect（顶点拉取）。
        /// 两条路可以随时互切对比，几何/灯光/级联设置【都不用动】。
        /// </summary>
        public ClusterShadowMode clusterShadowMode = ClusterShadowMode.SRP;

        /// <summary>
        /// Cluster 路径下，期望【一个簇】投影到阴影图上占多少 texel —— 用于逐簇 LOD 选择。
        ///
        /// 它【不是顶点数】：每簇顶点数固定 384（128 三角形 × 3），写死在 shader 的
        /// VERTS_PER_CLUSTER 里，与本字段无关。
        ///
        /// 判据：把簇的世界包围球半径 r 投影到阴影图上得 texelRadius
        ///   · texelRadius &gt; 本值      ⇒ 簇在图上很粗，只能用最细的 LOD（LOD0）
        ///   · texelRadius × 2 &lt;= 本值  ⇒ 簇在图上很小，用最粗的 LOD
        ///   · 落在中间                   ⇒ 选对应那一级
        /// 这样远处级联的阴影三角形数才真正降下来（cluster 阴影省的是三角形，不是 draw call）。
        ///
        /// ⚠️ 设 &lt;= 0 = 关闭 LOD 判据（compute 里直接跳过该分支）。
        /// 当前烘焙只出 LOD0（1 级），即使 &gt; 0 也没有额外效果 —— 等做多级 LOD 时才有意义。
        /// </summary>
        public float clusterTargetTexels = 0f;

        /// <summary>
        /// 排查用：非 0 时 compute 跳过所有剔除，所有簇对所有级联全可见。
        ///
        /// 配合 clusterShadowMode = ClusterDebug 用，判据：
        ///   · 开启后可见数 = 全部簇数 ⇒ compute 链正常，问题在平面/包围球判定
        ///   · 开启后可见数仍是 0     ⇒ 剔除链本身坏了（buffer 没绑上 / kernel 没跑）
        /// 正常渲染时务必保持关闭（否则等于退回全画）。
        /// </summary>
        public bool clusterShadowForceAll = false;

        /// <summary>Tile 粒度虚拟贴图开关。默认关：RVT 走「整图一格」(T=1) 旧路径，与历史行为完全等价。
        /// 开启后，每张贴图按 TilesPerAxis×TilesPerAxis 切块，反馈编码 32-bit pageId=(texId,mip,tileX,tileY)，
        /// LevelCache 以「页(cell)」为分配/淘汰单元，实现按页流式驻留（近清远糊、按需载入）。
        /// 显存恒定 = 图集容量（CellCount 决定的 cell 数），与 T 无关；T 只改变
        /// 「每贴图分辨率 = T×CellSize」与「同时驻留贴图数 = (CellCount/T)²」的交换。</summary>
        public bool useTileVT = false;

        /// <summary>每张贴图切成的 tile 数（每轴）。2/4/8/16。默认 8 → 4K 源图可达 512×8=4096² 满分辨率。
        /// 仅 useTileVT 开启时生效；关闭时一律按 1 处理（旧路径）。</summary>
        [Range(1, 16)]
        public int TilesPerAxis = 8;

        [ReadOnly(true)]
        public string savePath = Application.streamingAssetsPath + "/ClusterData/";

        private void OnEnable()
        {
            savePath = Application.streamingAssetsPath + "/ClusterData/";
        }

#if UNITY_EDITOR
        public bool MipsDebug = false;


        public void SaveClusters(ClustersData clusters, string name)
        {

            if (!Directory.Exists(savePath))
                Directory.CreateDirectory(savePath);
            string path = savePath + name + ".cluster";
            if (!File.Exists(path))
                File.Create(path).Dispose();
            File.WriteAllText(path, clusters.ToString(), Encoding.UTF8);
        }
#endif

    }
}