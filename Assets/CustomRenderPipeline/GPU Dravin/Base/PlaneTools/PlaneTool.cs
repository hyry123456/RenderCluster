using CustomRenderPipline;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KWS
{
    public struct RenderPatch
    {
        Vector2 position;
        uint4 lod;      //xyz是node值，w是patch值
    };

    [ExecuteAlways]
    public class PlaneTool : MonoBehaviour
    {
        private static PlaneTool instance;
        public static PlaneTool Instance
        {
            get
            {
                if(instance == null)
                {
                    GameObject go = new GameObject("PlaneTool");
                    go.AddComponent<PlaneTool>();
                    go.hideFlags = HideFlags.DontSave;
                }
                return instance;
            }
        }
        private const int MAX_LOD_NODE_COUNT = 5, _TempNodeBufferSize = 150;

        private ComputeShader _planeCompute;
        ComputeBuffer _maxLODNodeList;          //Begin Node
        ComputeBuffer _nodeListA;               //ping-pong Temp buffer
        ComputeBuffer _nodeListB;               //ping-pong Temp buffer
        ComputeBuffer _indirectArgsBuffer;      //Lod diviate indirect

        public static int
            _kernelOfTraverseQuadTree,  //ping-pong Build Node
            _kernelOfBuildLodMap,       //Write LodMap by Node
            _kernelOfBuildPatches,      //Culling Final Node
            _kernelOfShadowBuildPatches,    //Culling Shadow
            _kernelOfBounds;

        private void Awake()
        {
            if(instance != null) 
            {
                DestroyImmediate(this);
                return;
            }
            instance = this;
            Initialize();
        }

        private void OnDisable()
        {
            if(instance == this)
            {
                instance = null;
                _maxLODNodeList.Release();
                _nodeListA.Release();
                _nodeListB.Release();
                _indirectArgsBuffer.Release();
            }
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        private void Initialize()
        {
            //Get Shader and kernel
            _planeCompute = Resources.Load<ComputeShader>("PlaneCompute");
            InitKernels();

            //Create Max Lod Buffer
            _maxLODNodeList = new ComputeBuffer(MAX_LOD_NODE_COUNT *
                MAX_LOD_NODE_COUNT, 8, ComputeBufferType.Append);
            InitMaxLODNodeListDatas();

            //默认最终结点Node数量为200，先试一下
            //int maxNodeSize = 200;


            //Temp buffer
            _nodeListA = new ComputeBuffer(_TempNodeBufferSize, 8, ComputeBufferType.Append);
            _nodeListB = new ComputeBuffer(_TempNodeBufferSize, 8, ComputeBufferType.Append);

            //Lod diviate indirect
            _indirectArgsBuffer = new ComputeBuffer(3, 4, ComputeBufferType.IndirectArguments);
            _indirectArgsBuffer.SetData(new uint[] { 1, 1, 1 });

        }

        private void InitKernels()
        {
            _kernelOfTraverseQuadTree = _planeCompute.FindKernel("TraverseQuadTree");
            _kernelOfBuildPatches = _planeCompute.FindKernel("BuildPatches");
            _kernelOfBuildLodMap = _planeCompute.FindKernel("BuildLodMap");
            _kernelOfShadowBuildPatches = _planeCompute.FindKernel("BuildPatchesShadow");
            _kernelOfBounds = _planeCompute.FindKernel("BuildBounds");


            _planeCompute.SetTexture(_kernelOfTraverseQuadTree, PlaneConstant.MinMaxHeightMap, Texture2D.blackTexture);
            _planeCompute.SetTexture(_kernelOfBuildPatches, PlaneConstant.MinMaxHeightMap, Texture2D.blackTexture);
            _planeCompute.SetTexture(_kernelOfBounds, PlaneConstant.MinMaxHeightMap, Texture2D.blackTexture);
            _planeCompute.SetTexture(_kernelOfShadowBuildPatches, PlaneConstant.MinMaxHeightMap, Texture2D.blackTexture);
        }

        private void InitMaxLODNodeListDatas()
        {
            //Max Node Count is 5
            var maxLODNodeCount = MAX_LOD_NODE_COUNT;
            //Initial max Node Id
            uint2[] datas = new uint2[maxLODNodeCount * maxLODNodeCount];
            var index = 0;
            for (uint i = 0; i < maxLODNodeCount; i++)
            {
                for (uint j = 0; j < maxLODNodeCount; j++)
                {
                    datas[index] = new uint2(i, j);
                    index++;
                }
            }
            _maxLODNodeList.SetData(datas);
        }

        private void InitializeBufferAndCompute(PlaneBase plane)
        {
            _maxLODNodeList.SetCounterValue((uint)_maxLODNodeList.count);
            _nodeListA.SetCounterValue(0);
            _nodeListB.SetCounterValue(0);
            plane._finalNodeListBuffer.SetCounterValue(0);
            plane._culledPatchBuffer.SetCounterValue(0);
            ComputeBuffer.CopyCount(_maxLODNodeList, _indirectArgsBuffer, 0);//初始化一开始的Node数量，用于初次ping-pong

            _planeCompute.SetKeyword(new LocalKeyword(_planeCompute, PlaneConstant.EDGE_CHECK), plane.isEdgeCheck);      //边界处理
            _planeCompute.SetKeyword(new LocalKeyword(_planeCompute, PlaneConstant.MIN_MAX_HIGHT_MAP), false);


            _planeCompute.SetVectorArray(PlaneConstant.NodeSize, plane.NodeSize);

            _planeCompute.SetFloats(PlaneConstant.NodeEvaluation, plane.NodeEvaluation);
            _planeCompute.SetTexture(_kernelOfBuildLodMap, PlaneConstant.LodMap, plane.lodMap);
            _planeCompute.SetVector(PlaneConstant.BeginPosition, plane.WorldPosition);

            _planeCompute.SetBuffer(_kernelOfTraverseQuadTree, PlaneConstant.AppendFinalNodeList, plane._finalNodeListBuffer);
            _planeCompute.SetBuffer(_kernelOfBuildLodMap, PlaneConstant.FinalNodeList, plane._finalNodeListBuffer);
        }

        public void UpdatePlaneIndirect(PlaneBase planeBase)
        {
            if (Camera.main == null || planeBase == null) return;
            _planeCompute.SetVector(PlaneConstant.CameraPositionOS, Camera.main.transform.position - planeBase.WorldPosition);
            planeBase.updateTime -= Time.fixedDeltaTime;
            if(planeBase.updateTime > 0) return;    //isn't update time
#if UNITY_EDITOR
            if (!EditorApplication.isPaused && Application.isPlaying)
                planeBase.updateTime = 0.4f;            //per 0.4s update
            else
                planeBase.updateTime = -1f;
#else
                planeBase.updateTime = 0.4f;            //per 0.4s update
#endif
            InitializeBufferAndCompute(planeBase);
            planeBase.BeforePingPongCreateTree(_planeCompute);

            ComputeBuffer consumeNodeList = _nodeListA;
            ComputeBuffer appendNodeList = _nodeListB;
            int level = 4, edgeCount = 5;       //default level is 5, edge Count is 5

            for (var lod = level; lod >= 0; lod--)
            {
                _planeCompute.SetInt(PlaneConstant.CurrentLod, lod);
                _planeCompute.SetInt(PlaneConstant.CurrentEdgeSize, edgeCount - 1);
                edgeCount *= 2;
                if (lod == 4)
                {
                    _planeCompute.SetBuffer(_kernelOfTraverseQuadTree, PlaneConstant.ConsumeNodeList, _maxLODNodeList);
                }
                else
                {
                    _planeCompute.SetBuffer(_kernelOfTraverseQuadTree, PlaneConstant.ConsumeNodeList, consumeNodeList);
                }
                _planeCompute.SetBuffer(_kernelOfTraverseQuadTree, PlaneConstant.AppendNodeList, appendNodeList);
                _planeCompute.DispatchIndirect(_kernelOfTraverseQuadTree, _indirectArgsBuffer, 0);

                ComputeBuffer.CopyCount(appendNodeList, _indirectArgsBuffer, 0);
                var temp = consumeNodeList;
                consumeNodeList = appendNodeList;
                appendNodeList = temp;
            }

            _planeCompute.SetInt(PlaneConstant.CurrentLod, 4);
            //不要清除_finalNodeListBuffer的Count值，后面要用
            ComputeBuffer.CopyCount(planeBase._finalNodeListBuffer, _indirectArgsBuffer, 0);
            _planeCompute.DispatchIndirect(_kernelOfBuildLodMap, _indirectArgsBuffer, 0);

            planeBase.AfterPingPongCreateTree(_planeCompute);
        }

        public void CullingNode(CommandBuffer buffer, PlaneBase planeBase)
        {
            buffer.SetComputeVectorArrayParam(_planeCompute, PlaneConstant.CameraFrustumPlanes, GPUDravinSystem.Instance.CameraPlane);
            buffer.SetComputeVectorArrayParam(_planeCompute, PlaneConstant.NodeSize, planeBase.NodeSize);
            buffer.SetComputeVectorParam(_planeCompute, PlaneConstant.BeginPosition, planeBase.WorldPosition);
            buffer.SetComputeBufferParam(_planeCompute, _kernelOfBuildPatches, PlaneConstant.FinalNodeList, planeBase._finalNodeListBuffer);    //未裁剪
            buffer.SetComputeBufferParam(_planeCompute, _kernelOfBuildPatches, PlaneConstant.CulledPatchList, planeBase._culledPatchBuffer);    //裁剪后
            buffer.SetComputeVectorParam(_planeCompute, PlaneConstant.Extent, planeBase.Extent);
            buffer.CopyCounterValue(planeBase._finalNodeListBuffer, _indirectArgsBuffer, 0);
            buffer.DisableShaderKeyword(PlaneConstant.MIN_MAX_HIGHT_MAP);

            planeBase.BeforeCulling(_planeCompute, buffer);

            buffer.DispatchCompute(_planeCompute, _kernelOfBuildPatches, _indirectArgsBuffer, 0);

            //要偏移4字节，因为Mesh indirect的第二个才是存储数量
            buffer.CopyCounterValue(planeBase._culledPatchBuffer, planeBase.args, 4);
            buffer.SetBufferCounterValue(planeBase._culledPatchBuffer, 0);
#if UNITY_EDITOR
            if (planeBase.useBounds)
            {
                buffer.SetComputeBufferParam(_planeCompute, _kernelOfBounds, PlaneConstant.FinalNodeList, planeBase._finalNodeListBuffer);    //未裁剪
                buffer.SetComputeBufferParam(_planeCompute, _kernelOfBounds, PlaneConstant.BoundsList, planeBase._boundsBuffer);    //裁剪后
                buffer.DispatchCompute(_planeCompute, _kernelOfBounds, _indirectArgsBuffer, 0);
                buffer.SetBufferCounterValue(planeBase._boundsBuffer, 0);
            }
#endif
            planeBase.AfterCulling(_planeCompute, buffer);
        }

        public void CullingShadowNode(CommandBuffer buffer, PlaneBase planeBase)
        {
            buffer.SetComputeVectorArrayParam(_planeCompute, PlaneConstant.CameraFrustumPlanes, GPUDravinSystem.Instance.ShadowPlane);
            buffer.SetComputeVectorArrayParam(_planeCompute, PlaneConstant.NodeSize, planeBase.NodeSize);
            buffer.SetComputeVectorParam(_planeCompute, PlaneConstant.BeginPosition, planeBase.WorldPosition);
            buffer.SetComputeBufferParam(_planeCompute, _kernelOfShadowBuildPatches, PlaneConstant.FinalNodeList, planeBase._finalNodeListBuffer);    //未裁剪
            buffer.SetComputeBufferParam(_planeCompute, _kernelOfShadowBuildPatches, PlaneConstant.CulledPatchList, planeBase._culledPatchBuffer);    //裁剪后
            buffer.SetComputeVectorParam(_planeCompute, PlaneConstant.Extent, planeBase.Extent);
            buffer.CopyCounterValue(planeBase._finalNodeListBuffer, _indirectArgsBuffer, 0);
            buffer.DisableShaderKeyword(PlaneConstant.MIN_MAX_HIGHT_MAP);

            planeBase.BeforeCulling(_planeCompute, buffer);

            buffer.DispatchCompute(_planeCompute, _kernelOfShadowBuildPatches, _indirectArgsBuffer, 0);

            //要偏移4字节，因为Mesh indirect的第二个才是存储数量
            buffer.CopyCounterValue(planeBase._culledPatchBuffer, planeBase.args, 4);
            buffer.SetBufferCounterValue(planeBase._culledPatchBuffer, 0);
            planeBase.AfterCulling(_planeCompute, buffer);
        }

    }
}