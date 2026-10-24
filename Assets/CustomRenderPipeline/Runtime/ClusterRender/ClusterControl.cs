using CustomRenderPipline.Custer;
using System;
using UnityEditor;
using UnityEngine;

namespace Cluster
{

    public class ClusterProverty
    {
        private static int vTFeedbackParamId = Shader.PropertyToID("_VTFeedbackParam"),
            imageMVPId = Shader.PropertyToID("_ImageMVP"),
            levelData = Shader.PropertyToID("_LevelData"),
            levelPadding = Shader.PropertyToID("_LevelPadding"),
            textureNodesBuffer = Shader.PropertyToID("_TextureNodesBuffer"),
            sumTextureCount = Shader.PropertyToID("_SumTextureCount"),
            tilesPerAxis = Shader.PropertyToID("_TilesPerAxis"),
            normalDebugMode = Shader.PropertyToID("_NormalDebugMode"),
            rvtTrilinearBlend = Shader.PropertyToID("_RVTTrilinearBlend"),

            virtualAlbedo = Shader.PropertyToID("_VirtualAlbedo"),
            virtualNormal = Shader.PropertyToID("_VirtualNormal"),
            virtualMask = Shader.PropertyToID("_VirtualMask"),
            virtualEmission = Shader.PropertyToID("_VirtualEmission"),
            virtualDetail = Shader.PropertyToID("_VirtualDetail");


        public static int VTFeedbackParamId => vTFeedbackParamId;
        public static int ImageMVPId => imageMVPId;
        public static int TextureNodesBuffer => textureNodesBuffer;
        public static int LevelData => levelData;
        public static int LevelPadding => levelPadding;
        public static int SumTextureCount => sumTextureCount;
        public static int TilesPerAxis => tilesPerAxis;
        public static int NormalDebugMode => normalDebugMode;

        /// <summary>RVT mip 混合开关：0=单 mip（快）/ 1=min-max 线性混合（原实现，慢一倍）。</summary>
        public static int RVTTrilinearBlend => rvtTrilinearBlend;

        /// <summary>
        /// 「阴影交给 MeshRenderer、BRG 只画 GBuffer」模式用的排除 layer（-1 = 关闭该模式）。
        /// BRG 物体被挪到这个 layer，GBuffer pass 与 DrawUnsupportedShaders 排除它，
        /// 于是它的 MeshRenderer 只画阴影（走 Unity 的 SRP Batcher 路径，快 4 倍），不画 GBuffer。
        /// 注意：排除的是【GBuffer】，不是阴影 —— 名字里的 Exclude 指的是"把 GBuffer 绘制排除掉"。
        /// </summary>
        public static int BRGShadowExcludeLayer = -1;

        /// <summary>
        /// BRG 批次专用 layer（相机可见、灯光**不可见**）——用于让 BRG 物理上无法投阴影。
        ///
        /// 背景（2026-10-21）：删掉 BRG shader 的 ShadowCaster pass 之后，阴影 pass 仍然
        /// 比"完全不开 BRG"慢 ~1.6ms，说明 `context.DrawShadows` 依旧把 BRG 批次画进了
        /// 阴影图（BatchFilterSettings.shadowCastingMode = Off 在 2022.3 的 SRP 下不可靠）。
        ///
        /// 这里换一条【剔除层面】的路：`BatchFilterSettings.layer` 决定该批次属于哪个 layer，
        /// Unity 用视图自己的 cullingMask 过滤批次。
        ///   · 相机的 cullingMask 加上这一层 → 主画面照画；
        ///   · 灯光的 cullingMask 去掉这一层 → 阴影视图的剔除直接pass掉该批次。
        /// 两层分工：MeshRenderer 的物体在 BRGShadowExcludeLayer(31)（灯光**要**看见 → 投阴影），
        /// BRG 批次在 BRGCullLayer(30)（灯光**不要**看见 → 永不投阴影）。
        /// </summary>
        public static int BRGCullLayer = 30;

        /// <summary>
        /// 「需 alpha 镂空的物体（草/蕨/灌木）」专用的 rendering layer 位（不是 GameObject layer，
        /// 是 Renderer.renderingLayerMask 的位，与 BRGCullLayer 的 GameObject 层互不干扰）。
        /// Cluster 阴影 shader 不采样贴图、无法做镂空，这类物体改走 SRP 阴影路径；
        /// Shadows.cs 用这个位做 rendering layer mask 过滤，只挑它们进 context.DrawShadows，
        /// 其余物体（renderingLayerMask 不含此位）被自动跳过，不会与 Cluster 重复绘制。
        /// </summary>
        public const int CutoffRenderingLayerBit = 29;


        /// <summary>当前生效的 Tile 轴数：useTileVT 关时为 1（旧路径，行为完全等价），开启时为 TilesPerAxis。</summary>
        public static int TilesPerAxisValue { get; set; } = 1;

        /// <summary>
        /// 当前生效的法线诊断模式（0=关闭）。供 DefferPipeline 的最终 blit 判断：
        /// 非 0 时强制走 _DebugAlbedo pass 输出诊断通道，用户只改 Assemble 上这一个字段就能立刻看到诊断。
        ///
        /// 【必须实时读资产，不能缓存】以前是 `public static int ... { get; set; }`，只有
        /// ClusterCore.UpdateRequest（条件：Play + 未暂停 + cameraType==Game + UseRVT 开）里才同步。
        /// 一旦在那之后关掉 UseRVT / 退出 Play / 暂停 / 关掉 Game 视图，缓存值就永久停在旧值
        /// → 最终 blit 一直被强制到 _DebugAlbedo → 表现为「Normal Debug 明明设回 0 了，
        /// 管线资产自己的 Debug Mode 还是不生效」。直接读资产后，Off 状态对渲染零干预。
        /// </summary>
        public static int NormalDebugModeValue
        {
            get
            {
                var asset = ClusterControl.Instance.ClusterAssembleAsset;
                return asset == null ? 0 : (int)asset.normalDebugMode;
            }
        }

        public static int VirtualAlbedo => virtualAlbedo;
        public static int VirtualNormal => virtualNormal;
        public static int VirtualMask => virtualMask;
        public static int VirtualEmission => virtualEmission;
        public static int VirtualDetail => virtualDetail;
    }




    public class ClusterControl
    {
        private static ClusterControl instance;

        private ClusterAssembleAsset assembleAsset;

        private Material rvtMat;
        public Material RVT_Mat
        {
            get
            {
                if (rvtMat == null)
                    rvtMat = UnityEngine.Rendering.CoreUtils.CreateEngineMaterial(
                        Shader.Find("Unlit/RVTShader"));
                return rvtMat;
            }
        }

        public static ClusterControl Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new ClusterControl();
                    instance.assembleAsset = Resources.Load<ClusterAssembleAsset>("Cluster/assemble");

                }
#if UNITY_EDITOR
                if (instance.assembleAsset == null)
                {
                    instance.assembleAsset = ScriptableObject.CreateInstance<ClusterAssembleAsset>();
                    AssetDatabase.CreateAsset(instance.assembleAsset,
                        @"Assets/Resources/Cluster/assemble.asset");//在传入的路径中创建资源
                    AssetDatabase.SaveAssets(); //存储资源
                    AssetDatabase.Refresh(); //刷新
                }
#endif
                return instance;
            }
        }

        public ClusterAssembleAsset ClusterAssembleAsset
        {
            get
            {
                return assembleAsset;
            }
        }

        /// <summary>
        /// 安全取 assemble 资产：**不会**创建单例、也不会在磁盘上建资产。
        /// 原因：`ClusterControl.Instance` 是懒加载单例（资产不存在时会 AssetDatabase.CreateAsset），
        /// 在编辑器导入期间调用会直接抛 UnityException —— 所以每帧路径上要判断相机类型的场合
        /// 必须用这个。
        /// </summary>
        public static ClusterAssembleAsset TryGetAssembleAsset()
        {
            return instance != null ? instance.assembleAsset : null;
        }

#if UNITY_EDITOR

        System.Threading.Tasks.Task task;
        bool isRunning = false, isSimple;
        Mesh mesh; int currentIndex = 0, sunMeshCount;
        string name; int[] tris; Vector3[] points;
        Vector2Int[] subMeshRange;
        private void SaveMeshTask()
        {
            try
            {
                for (int i = 0; i < sunMeshCount; i++)
                {
                    currentIndex = i;

                    ClustersData clusters = ClusterTools.CreateClusterData(
                        tris, subMeshRange[i].x, subMeshRange[i].y, points, isSimple);
                    assembleAsset.SaveClusters(clusters, name + i.ToString());
                }

                ClusterTools.currentState = -1;
                isRunning = false;
                Debug.Log(name + "执行完毕");
            }
            catch (Exception e)
            {
                Debug.LogError(e.ToString());
                isRunning = false;
            }
        }

        public void SaveMesh(Mesh mesh, bool isSimple)
        {
            if (isRunning)
            {
                Debug.LogError("正在执行中，请等待前一个完成");
                return;
            }
            this.mesh = mesh;
            sunMeshCount = mesh.subMeshCount;
            name = mesh.name;
            tris = mesh.triangles; points = mesh.vertices;
            subMeshRange = new Vector2Int[sunMeshCount];
            this.isSimple = isSimple;
            for (int i = 0; i < sunMeshCount; i++)
            {
                var descript = mesh.GetSubMesh(i);
                subMeshRange[i] = new Vector2Int(descript.indexStart, descript.indexCount);
            }

            try
            {
                task = new System.Threading.Tasks.Task(SaveMeshTask);
                isRunning = true;
                task.Start();
            }
            catch (Exception e)
            {
                Debug.LogError(e.ToString());
            }

        }
    }
#endif
}