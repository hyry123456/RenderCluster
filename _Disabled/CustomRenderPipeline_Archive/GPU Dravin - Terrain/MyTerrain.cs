using Cluster;
using KWS;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    [ExecuteAlways]
    public class MyTerrain : MonoBehaviour, IDrawShadow, IDrawOpaque, IDrawFeedback,
        IDrawGBuffer
    {
        [SerializeField]
        //TerrainBuilder planeBuilder;
        TerrainPlane terrainPlane;
        public Material material;
        public TerrainPlaneData terrainData;
        [SerializeField]
        private RenderTexture normalMap;
        DrawNode drawNode;

        private static Material terrainTileMat;
        public static Material TerrainTileMat => terrainTileMat;

        private ComputeBuffer indexsBuffer;


        private void OnEnable()
        {
            var terrain = GetComponent<Terrain>();
            if (terrain == null) return;
            if(terrainData == null) return;

            if (terrainTileMat == null)
                terrainTileMat = new Material(Shader.Find("Unlit/TerrainTile"));
            normalMap = terrain.normalmapTexture;
            terrainData.terrainData = terrain.terrainData;
            drawNode = new DrawNode();
            if (Application.isPlaying)
            {
                var instance = TextureNodesMap.CreateInstance();
                var indexs = instance.AllocateTerrainId(
                    terrainData._TextureNodeCount, terrainData._ColumnCount, 
                    terrainData._RowCount, terrain.terrainData);
                indexsBuffer = new ComputeBuffer(indexs.Length, 4);
                indexsBuffer.SetData(indexs);
                indexs.Dispose();
                drawNode.gBufferEvent = DrawGBuffer;
                drawNode.feedbackEvent = DrawFeedback;
            }
            else
            {
                drawNode.opaqueEvent = DrawOpaque;
            }

            terrainPlane = new TerrainPlane(transform.position, terrainData);
            drawNode.shadowEvent = DrawShadow;
            drawNode.Submit();
        }
        private void OnDisable()
        {
            if(terrainPlane != null)
            {
                terrainPlane.Release();
                drawNode.Remove();
                var instance = TextureNodesMap.Instance;
                if (instance != null && indexsBuffer != null)
                {
                    int[] indexs = new int[indexsBuffer.count];
                    indexsBuffer.GetData(indexs);
                    instance.RemoveTerrainId(indexs);
                }
                indexsBuffer?.Dispose();
            }
            terrainPlane = null;
            drawNode = null;
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        private Terrain terrain;

        private void LateUpdate()
        {
#if UNITY_EDITOR
            if(terrainPlane != null)
            {
                if(terrain == null) terrain = GetComponent<Terrain>();
                terrainData.terrainData = terrain.terrainData;
                terrainPlane.Check(terrainData);
            }
#endif
            terrainPlane.WorldPosition = transform.position;
            //更新平面数据
            PlaneTool.Instance.UpdatePlaneIndirect(terrainPlane);
        }

        public void DrawShadow(ScriptableRenderContext context, CommandBuffer buffer)
        {
            PlaneTool.Instance.CullingShadowNode(buffer, terrainPlane);
            buffer.SetGlobalBuffer(PlaneConstant.Nodes, terrainPlane._culledPatchBuffer);

            buffer.SetGlobalVectorArray(PlaneConstant.NodeSize, terrainPlane.NodeSize);
            buffer.SetGlobalTexture(PlaneConstant.LodMap, terrainPlane.lodMap);
            buffer.SetGlobalVector(PlaneConstant.BeginPosition, transform.position);
            buffer.SetGlobalTexture(PlaneConstant.HeighMap, terrainData.terrainData.heightmapTexture);
            buffer.SetGlobalFloat(PlaneConstant.PointCount, (int)terrainData.meshScale);
            buffer.DrawMeshInstancedIndirect(terrainPlane.planeMesh, 0, material, 1,
                terrainPlane.args);
        }

        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            PlaneTool.Instance.CullingNode(buffer, terrainPlane);

            buffer.SetGlobalInt(PlaneConstant.ColumnCount, terrainData._ColumnCount);
            buffer.SetGlobalInt(PlaneConstant.RowCount, terrainData._RowCount);
            buffer.SetGlobalBuffer(PlaneConstant.TerrainIndexs, indexsBuffer);  //图片指针
            material.SetTexture(PlaneConstant.NormalMap, normalMap);
            //buffer.SetGlobalMatrix(PlaneConstant.ObjectToWorld, transform.localToWorldMatrix);

            buffer.SetGlobalBuffer(PlaneConstant.Nodes, terrainPlane._culledPatchBuffer);

            buffer.SetGlobalVectorArray(PlaneConstant.NodeSize, terrainPlane.NodeSize);
            buffer.SetGlobalTexture(PlaneConstant.LodMap, terrainPlane.lodMap);
            buffer.SetGlobalVector(PlaneConstant.BeginPosition, transform.position);
            buffer.SetGlobalTexture(PlaneConstant.HeighMap, terrainData.terrainData.heightmapTexture);
            buffer.SetGlobalFloat(PlaneConstant.PointCount, (int)terrainData.meshScale);
            buffer.DrawMeshInstancedIndirect(terrainPlane.planeMesh, 0, material, 0,
                terrainPlane.args);
        }

        public void DrawFeedback(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            PlaneTool.Instance.CullingNode(buffer, terrainPlane);
            buffer.SetGlobalInt(PlaneConstant.ColumnCount, terrainData._ColumnCount);
            buffer.SetGlobalInt(PlaneConstant.RowCount, terrainData._RowCount);
            buffer.SetGlobalBuffer(PlaneConstant.TerrainIndexs, indexsBuffer);  //图片指针

            buffer.SetGlobalBuffer(PlaneConstant.Nodes, terrainPlane._culledPatchBuffer);

            buffer.SetGlobalVectorArray(PlaneConstant.NodeSize, terrainPlane.NodeSize);
            buffer.SetGlobalTexture(PlaneConstant.LodMap, terrainPlane.lodMap);
            buffer.SetGlobalVector(PlaneConstant.BeginPosition, transform.position);
            buffer.SetGlobalTexture(PlaneConstant.HeighMap, terrainData.terrainData.heightmapTexture);
            buffer.SetGlobalFloat(PlaneConstant.PointCount, (int)terrainData.meshScale);
            buffer.DrawMeshInstancedIndirect(terrainPlane.planeMesh, 0, material, 3,
                terrainPlane.args);
        }


        Vector4 GetVector4(Vector2 ve0, Vector2 ve1)
        {
            return new Vector4(ve0.x, ve0.y, ve1.x, ve1.y);
        }

        public void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            //planeBuilder.ReadyPatchByCameraFrustumPlanes(buffer, camera, transform);
            PlaneTool.Instance.CullingNode(buffer, terrainPlane);
            buffer.SetGlobalBuffer(PlaneConstant.Nodes, terrainPlane._culledPatchBuffer);

            buffer.SetGlobalVectorArray(PlaneConstant.NodeSize, terrainPlane.NodeSize);
            buffer.SetGlobalTexture(PlaneConstant.LodMap, terrainPlane.lodMap);
            buffer.SetGlobalVector(PlaneConstant.BeginPosition, transform.position);
            buffer.SetGlobalTexture(PlaneConstant.HeighMap, terrainData.terrainData.heightmapTexture);
            buffer.SetGlobalFloat(PlaneConstant.PointCount, (int)terrainData.meshScale);

            material.SetTexture(PlaneConstant.NormalMap, normalMap);
            TerrainData data = terrainData.terrainData;

            for (int i = 0; i < data.alphamapTextureCount; i++)
            {
                int begin = i * 4;
                buffer.SetGlobalTexture("_BlendMap", data.alphamapTextures[i]);

                for (int j = 0; j < 4 && j + begin < data.terrainLayers.Length; j++)
                {
                    var layer = data.terrainLayers[j + begin];
                    buffer.SetGlobalTexture($"_AlbedoMap{j}", layer.diffuseTexture);
                    buffer.SetGlobalTexture($"_NormalMap{j}",
                        layer.normalMapTexture ? layer.normalMapTexture : Texture2D.normalTexture);
                    buffer.SetGlobalTexture($"_MaskMap{j}",
                        layer.maskMapTexture ? layer.maskMapTexture : Texture2D.whiteTexture);
                    buffer.SetGlobalVector($"_ScaleOffset{j}", GetVector4(layer.tileSize, layer.tileOffset));
                    buffer.SetGlobalVector($"_PBRData{j}", new Vector3(layer.metallic, layer.smoothness, layer.normalScale));
                    buffer.SetGlobalVector($"_Color{j}", layer.specular);
                }
                if (i == 0)
                buffer.DrawMeshInstancedIndirect(terrainPlane.planeMesh, 0, material, 4,
                    terrainPlane.args);
                else
                    buffer.DrawMeshInstancedIndirect(terrainPlane.planeMesh, 0, material, 5,
                        terrainPlane.args);
            }


        }
    }
}