using KWS;
using UnityEngine;

namespace CustomRenderPipline
{
    [CreateAssetMenu(menuName = "Rendering/GPUDravin/Terrain Data")]
    public class TerrainPlaneData : ScriptableObject
    {
        public ComputeShader textureCompute;

        [Tooltip("顶点精度")]
        public PlaneMeshScaleMode meshScale = PlaneMeshScaleMode.Medium;
        [Tooltip("细分程度")]
        public float nodeLevel = 1;

        [Tooltip("Terrain Spilt Count,xy= column, row"), SerializeField]
        private Vector2Int spiltCount = new Vector2Int(32, 32);

        [HideInInspector]
        public TerrainData terrainData;

        /// <summary>
        /// Get Current terrain size for PlaneTool
        /// </summary>
        public Vector4 TerrainSize
        {
            get
            {
                Vector4 vector = terrainData.size;
                vector.y = vector.z; vector.z = 0;
                vector.w = terrainData.size.y * 2;
                return vector;
            }
        }

        public int _TextureNodeCount
        {
            get { return spiltCount.x * spiltCount.y; }
        }

        public int _ColumnCount
        {
            get { return spiltCount.x; }
        }
        public int _RowCount
        {
            get { return spiltCount.y; }
        }
    }
}