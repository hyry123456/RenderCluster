using CustomRenderPipline.Custer;
using System.ComponentModel;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;


namespace Cluster
{
    /// <summary>
    /// 整条管线的绘制路径。**一个开关管全部**（GBuffer / RVT Feedback / 阴影一起切），
    /// 不允许出现「GBuffer 走 Cluster、阴影走 SRP」这类混搭。
    /// </summary>
    public enum ClusterMode
    {
        /// <summary>Unity 常规路径：逐物体剔除 + SRP Batcher（context.DrawRenderers / DrawShadows）。</summary>
        SRP = 0,
        /// <summary>
        /// Cluster：场景里的静态物体全部改由簇绘制 ——
        /// GBuffer、RVT Feedback、阴影三者共用同一份烘焙簇数据，各自一次 GPU 逐簇剔除。
        /// 动态物体（动画/移动的 Transform）仍走 SRP，它们本来就快，没必要进簇。
        /// </summary>
        Cluster = 1,
        /// <summary>
        /// ClusterDebug：绘制路径与 Cluster 完全相同，只额外【强制不做剔除】（全簇可见）。
        /// 判据：Debug 下能画出来、Cluster 下画不出来 ⇒ 剔除口径/包围球有问题；
        ///       两者都画不出来 ⇒ 绘制链（缓冲绑定 / shader 取数）有问题。
        /// </summary>
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
        /// 整条管线的绘制路径（**唯一开关**）：
        ///   SRP     = Unity 常规（逐物体剔除 + SRP Batcher），Cluster 相关代码全部不参与；
        ///   Cluster = 静态物体的 GBuffer / RVT Feedback / 阴影全部由簇绘制；
        ///   ClusterDebug = 同 Cluster，但强制不做剔除（排查剔除 vs 绘制链）。
        /// 几何 / 灯光 / 级联设置都不用动，随时互切对比。
        /// </summary>
        public ClusterMode clusterMode = ClusterMode.SRP;

        /// <summary>
        /// **独立于 clusterMode 的子开关**：是否让 Cluster 接管 GBuffer（+ RVT Feedback）。
        ///
        /// true（默认）—— 静态不透明物体的 GBuffer / RVT Feedback 由簇绘制：
        ///   几何从 `_Positions` 等缓冲里按可见簇还原，按「detail 贴图对」分组，每组一次
        ///   `DrawProceduralIndirect`（本场景 13 组）。相比 SRP 逐物体提交（~900 次 draw），
        ///   **CPU 侧 draw 提交从几百次降到十几次**；GPU 侧顶点走 SRV 取数，比 IA 略贵。
        ///
        /// false —— 即使 clusterMode = Cluster（阴影走簇），模型 GBuffer 也仍由 SRP 的
        ///   DrawRenderers 画。用于把「阴影」和「GBuffer」两条性能曲线彻底分开排查。
        ///
        /// 只有 Game 摄像机 + UseRVT + clusterMode ≠ SRP 时才会真的生效；Scene 视图永远走 SRP。
        /// </summary>
        public bool clusterGBuffer = true;

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
        /// 阴影里「小到看不出来」的簇直接剔掉：簇投影到阴影图上的半径小于这个 texel 数就跳过。
        ///
        /// 判据：texelRadius = 簇世界半径 × projScale，projScale 由该级联的正交投影与 tile 尺寸算出。
        /// 一个半径不到 1 texel 的簇，它投出来的阴影本身就只有一两个像素、再经 PCF 模糊后基本不可见，
        /// 但顶点开销是实打实的（每簇 384 个顶点 × 4 个级联）。
        ///
        /// ⚠️ 0 = 关闭（默认，行为与以前完全一致）。想再快就试 0.5 ~ 1.0；
        ///    调太大远处小物体的阴影会整片消失（细长物体不受影响——它们的包围球半径很大）。
        /// </summary>
        public float clusterShadowMinTexels = 0f;

        /// <summary>
        /// 排查用：非 0 时 **GBuffer 与阴影的剔除都跳过**，所有簇全部可见。
        ///
        /// 判据（配合 clusterMode = ClusterDebug 已默认开启）：
        ///   · 全开后画得出来、正常时画不出来 ⇒ 剔除口径 / 包围球有问题；
        ///   · 全开也画不出来                 ⇒ 绘制链本身坏了（缓冲没绑上 / shader 取数错）。
        /// 正常渲染时务必保持关闭（否则等于退回全画）。
        /// </summary>
        public bool clusterForceAll = false;

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