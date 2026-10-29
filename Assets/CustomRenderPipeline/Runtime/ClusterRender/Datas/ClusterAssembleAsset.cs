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
        /// Cluster 阴影正交投影 z 范围的扩张倍数上限（1 = 不扩张，等于 Unity 原生 z 范围）。
        ///
        /// 【2026-10-29 实测：默认 2；实测 1 会缺块，所以不要设 1】
        /// · 当年"缺块"有一部分是 `PadOrthoDepth` 第一版 `m22/=pad; m23/=pad` 的 bug ——
        ///   那等于把 z 板整体往一侧平移半个板，而不是对称扩张。已改成围绕板中心对称。
        /// · 但即使修好，pad=1（= Unity 原生 z 范围）**仍然缺块** ⇒ 级联 z 板确实需要比原生略宽，
        ///   这是几何决定的（高处物体沿光轴离板中心比 R 更远），不是 bug。
        /// · pad 的代价现在可控：compute 里有 z 边界剔除（PlanesPerCascade = 8），
        ///   「整簇落在 z 板外、必被光栅化器裁掉」的簇会在剔除阶段就排掉，不会白付 setup。
        /// </summary>
        [Range(1, 64)]
        public float clusterShadowMaxDepthPad = 2f;

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