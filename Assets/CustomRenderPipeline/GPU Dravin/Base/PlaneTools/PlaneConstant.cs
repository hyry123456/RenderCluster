
using UnityEngine;

namespace KWS
{

    public class PlaneConstant 
    {
        public const string EDGE_CHECK = "EDGE_CHECK",
            MIN_MAX_HIGHT_MAP = "MIN_MAX_HIGHT_MAP";

        public static readonly int CurrentLod = Shader.PropertyToID("_CurrentLod"),
            CurrentEdgeSize = Shader.PropertyToID("_CurrentEdgeSize"),
            ConsumeNodeList = Shader.PropertyToID("ConsumeNodeList"),
            AppendNodeList = Shader.PropertyToID("AppendNodeList"),
            AppendFinalNodeList = Shader.PropertyToID("AppendFinalNodeList"),
            FinalNodeList = Shader.PropertyToID("FinalNodeList"),
            CulledPatchList = Shader.PropertyToID("CulledPatchList"),
            BoundsList = Shader.PropertyToID("BoundsList"),
            NodeSize = Shader.PropertyToID("_NodeSize"),
            CameraPositionOS = Shader.PropertyToID("_CameraPositionOS"),
            NodeEvaluation = Shader.PropertyToID("_NodeEvaluation"),
            LodMap = Shader.PropertyToID("_LodMap"),
            BeginPosition = Shader.PropertyToID("_BeginPosition"),
            Extent = Shader.PropertyToID("_Extent"),
            Nodes = Shader.PropertyToID("_Nodes"),
            PointCount = Shader.PropertyToID("_PointCount"),
            HeighMap = Shader.PropertyToID("_HeighMap"),
            MinMaxHeightMap = Shader.PropertyToID("_MinMaxHeightMap"),
            MinMaxHeightMapMipsOffset = Shader.PropertyToID("_MinMaxHMapMipsOffset"),
            CameraFrustumPlanes = Shader.PropertyToID("_CameraFrustumPlanes");

        public static readonly int ColumnCount = Shader.PropertyToID("_ColumnCount"),
            RowCount = Shader.PropertyToID("_RowCount"),
            TerrainIndexs = Shader.PropertyToID("_TerrainIndexs"),

            NormalMap = Shader.PropertyToID("_NormalMap");


    }
}