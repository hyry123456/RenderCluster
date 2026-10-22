
using UnityEngine;

namespace CustomRenderPipline
{
    public struct RenderPatch
    {
        Vector2 position;
        uint4 lod;      //xyz是node值，w是patch值
    };

    public struct TerrainBounds
    {
        Vector3 minPosition;
        Vector3 maxPosition;
    };
    public class PlaneUtility
    {
        public const uint MAX_NODE_ID = 34124; //5x5+10x10+20x20+40x40+80x80+160x160 - 1
        public const int MAX_LOD = 5;

        /// <summary>
        /// MAX LOD下，世界由5x5个区块组成
        /// </summary>
        public const int MAX_LOD_NODE_COUNT = 5;

        private static Mesh _unitCubeMesh;

        public static Mesh unitCubeMesh
        {
            get
            {
                if (!_unitCubeMesh)
                {
                    _unitCubeMesh = MeshUtility.CreateCube(1);
                }
                return _unitCubeMesh;
            }
        }
    }
}