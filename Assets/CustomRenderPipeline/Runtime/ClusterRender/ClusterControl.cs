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
            rvtTrilinearBlend = Shader.PropertyToID("_RVTTrilinearBlend"),

            virtualAlbedo = Shader.PropertyToID("_VirtualAlbedo"),
            virtualNormal = Shader.PropertyToID("_VirtualNormal"),
            virtualMask = Shader.PropertyToID("_VirtualMask");


        public static int VTFeedbackParamId => vTFeedbackParamId;
        public static int ImageMVPId => imageMVPId;
        public static int TextureNodesBuffer => textureNodesBuffer;
        public static int LevelData => levelData;
        public static int LevelPadding => levelPadding;
        public static int SumTextureCount => sumTextureCount;
        public static int TilesPerAxis => tilesPerAxis;
        /// <summary>RVT mip 混合开关：0=单 mip（快）/ 1=min-max 线性混合（原实现，慢一倍）。</summary>
        public static int RVTTrilinearBlend => rvtTrilinearBlend;


        /// <summary>
        /// 「需 alpha 镂空的物体（草/蕨/灌木）」专用的 rendering layer 位（不是 GameObject layer，
        /// 是 Renderer.renderingLayerMask 的位）。
        /// Cluster 阴影 shader 不采样贴图、无法做镂空，这类物体改走 SRP 阴影路径；
        /// Shadows.cs 用这个位做 rendering layer mask 过滤，只挑它们进 context.DrawShadows，
        /// 其余物体（renderingLayerMask 不含此位）被自动跳过，不会与 Cluster 重复绘制。
        /// </summary>
        public const int CutoffRenderingLayerBit = 29;

        /// <summary>
        /// 「由 Cluster 画 GBuffer」的物体专用 rendering layer 位（Renderer.renderingLayerMask 的位）。
        /// 这些物体仍然保留 MeshRenderer（否则 SRP 阴影那条路会一起失效），只是 GBuffer pass
        /// 用 FilteringSettings.renderingLayerMask 把它们剔出去，避免与 Cluster 重复绘制。
        /// 只在 clusterMode 非 SRP 时打这个位；Scene 视图不清位 ⇒ 那里照旧走 SRP。
        /// </summary>
        public const int ClusterRenderingLayerBit = 28;


        /// <summary>当前生效的 Tile 轴数：useTileVT 关时为 1（旧路径，行为完全等价），开启时为 TilesPerAxis。</summary>
        public static int TilesPerAxisValue { get; set; } = 1;

        public static int VirtualAlbedo => virtualAlbedo;
        public static int VirtualNormal => virtualNormal;
        public static int VirtualMask => virtualMask;
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
                    // 导入/预览渲染期间（AssetPreviewUpdater、out-of-process import worker）禁止落盘建资产，
                    // 否则会抛 UnityException 并每帧刷错。此时只给一个【内存临时实例】，
                    // 其 clusterMode 默认 SRP ⇒ Shadows.Render 不会触发 Cluster 重建，渲染链不崩、转化也关着。
                    // 真正落盘只发生在「可写」上下文（Play 模式首次、或正常编辑器操作）。
                    instance.assembleAsset = ScriptableObject.CreateInstance<ClusterAssembleAsset>();
                    try
                    {
                        if (!EditorApplication.isUpdating)
                        {
                            AssetDatabase.CreateAsset(instance.assembleAsset,
                                @"Assets/Resources/Cluster/assemble.asset");//在传入的路径中创建资源
                            AssetDatabase.SaveAssets(); //存储资源
                            AssetDatabase.Refresh(); //刷新
                        }
                    }
                    catch (UnityException)
                    {
                        // 导入/预览上下文里 CreateAsset 被禁：已有内存实例可用，忽略即可。
                    }
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