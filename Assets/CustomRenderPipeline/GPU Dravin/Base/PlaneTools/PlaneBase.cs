using CustomRenderPipline;
using UnityEngine;
using UnityEngine.Rendering;

namespace KWS
{
    public enum PlaneMeshScaleMode
    {
        VeryLow = 16,
        Low = 32,
        Medium = 64,
        High = 128,
        Ultra = 176,
    }

    /// <summary>
    /// 平面数据的抽象类，只定义处理流程，即设置数据传递的方法
    /// </summary>
    public class PlaneBase
    {
        /// <summary>
        /// 初始化一个Args Buffer，用来存储最终裁剪数据，
        /// 因为本质上所有裁剪前数据格式是一致的，因此只需要独立存储裁剪结果即可
        /// </summary>
        public ComputeBuffer args;
        /// <summary> /// LodMap，为了方便计算处理，所有LodMap的大小是一致的，都是5级划分,宽80像素  /// </summary>
        public RenderTexture lodMap;
        /// <summary>  /// 存储最终Node数据的Buffer    /// </summary>
        public ComputeBuffer _culledPatchBuffer;
        /// <summary>  /// 存储根据距离生成的模型细分数据(Node)，未裁剪    /// </summary>
        public ComputeBuffer _finalNodeListBuffer;
        /// <summary>  /// 平面Mesh，这个mesh是有边界数据的Mesh   /// </summary>
        public Mesh planeMesh;
        /// <summary>
        /// 更新剩余时间，为了减少每一次更新的损耗时间，将刷新变为间隔刷新，而不是逐帧
        /// </summary>
        public float updateTime;
        /// <summary>        /// 是否需要边界检测        /// </summary>
        public bool isEdgeCheck;

        private Vector3 worldPosition;
        public Vector3 WorldPosition
        {
            get { return worldPosition; }
            set { worldPosition = value; }
        }

        private float nodeEvaluation;
        public float NodeEvaluation => nodeEvaluation;

        private Vector4[] sizes;
        public Vector4[] NodeSize => sizes;

        private Vector3 extent;
        /// <summary>        /// 裁剪块拓展，因为水面等会偏移，为了避免错误，需要对每一个块进行拓展        /// </summary>
        public Vector3 Extent => extent;

#if UNITY_EDITOR
        public ComputeBuffer _boundsBuffer;
        public Mesh cubeMesh;
        public bool useBounds;
#endif

        /// <summary>
        /// Create every plane must data
        /// </summary>
        /// <param name="planeScaleMode">Mesh Mode</param>
        /// <param name="edgeCheck">is plane edge check</param>
        /// <param name="planeSize">sum plane size, xy:width and length, z:bound min y, w: bound y length</param>
        /// <param name="worldPosition">plane begin world position</param>
        /// <param name="nodeEvaluation">subdivision degree</param>
        /// <param name="extent">every node extent size</param>
        public PlaneBase(PlaneMeshScaleMode planeScaleMode, bool edgeCheck, Vector4 planeSize,
            Vector3 worldPosition, float nodeEvaluation, Vector3 extent, bool useBounds)
        {
            nodeEvaluation = Mathf.Clamp(nodeEvaluation, 0.0f, 2.0f);
            lodMap = RenderTexture.GetTemporary(80, 80, 0, RenderTextureFormat.RHalf, 
                RenderTextureReadWrite.Linear);
            lodMap.enableRandomWrite = true;
            lodMap.Create();
            planeMesh = MeshUtility.CreatePlaneMesh(new Vector3(1, 0, 1) * (int)planeScaleMode,
                (int)planeScaleMode);
            args = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
            args.SetData(new uint[] { planeMesh.GetIndexCount(0), 0, 0, 0, 0 });

            int maxNodeSize = 180, cullMaxNodeCount = 150;      //默认大小，这两个基本足够了

            _culledPatchBuffer = new ComputeBuffer(cullMaxNodeCount, 16, ComputeBufferType.Append);
            //final Node Buffer
            _finalNodeListBuffer = new ComputeBuffer(maxNodeSize, 16, ComputeBufferType.Append);

#if UNITY_EDITOR
            this.useBounds = useBounds;
            if(useBounds)
            {
                _boundsBuffer = new ComputeBuffer(cullMaxNodeCount, 16, ComputeBufferType.Append);
                cubeMesh = MeshUtility.CreateCube(1);
            }
#endif

            updateTime = -1;        //初始化为负数，也就是立刻刷新
            isEdgeCheck = edgeCheck;
            this.worldPosition = worldPosition;
            this.nodeEvaluation = nodeEvaluation;
            this.extent = extent;

            //planeSzie.xy is all plane scale, z is plane low, w is plane top
            sizes = new Vector4[5];
            Vector4 curSize = planeSize;
            curSize.x /= 5; curSize.y /= 5;//Plane have 5 Node
            for (int i = 4; i >= 0; i--)
            {
                sizes[i] = curSize;
                curSize.x /= 2; curSize.y /= 2;
            }
        }

        /// <summary>
        /// 释放方法，一般只需要创建上面的几个数据，初始清除也是只清除上面几个
        /// </summary>
        public virtual void Release()
        {
            if (args != null && args.IsValid())
            {
                args.Release();
                lodMap.Release();
                _culledPatchBuffer.Release();
                _finalNodeListBuffer.Release();
                GameObject.DestroyImmediate(planeMesh);
#if UNITY_EDITOR
                if (useBounds)
                {
                    _boundsBuffer.Release();
                    GameObject.DestroyImmediate(cubeMesh);
                }
#endif
            }
        }

        /// <summary>
        /// 在循环生成树之前的行为，比如地形需要启动Keyword和传递数据
        /// </summary>
        public virtual void BeforePingPongCreateTree(ComputeShader compute) {}

        /// <summary>
        /// 循环生成树之后的行为，关闭Keyword，避免影响到后序执行
        /// </summary>
        public virtual void AfterPingPongCreateTree(ComputeShader compute) { }

        /// <summary>
        /// 裁剪前的设置行为，注意裁剪有两种情况，也就是视线和阴影，两个都会调用该函数
        /// </summary>
        public virtual void BeforeCulling(ComputeShader compute, CommandBuffer cmd) { }
        /// <summary>
        /// 裁剪后的设置行为，注意裁剪有两种情况，也就是视线和阴影，两个都会调用该函数
        /// </summary>
        public virtual void AfterCulling(ComputeShader compute, CommandBuffer cmd) { }

    }
}