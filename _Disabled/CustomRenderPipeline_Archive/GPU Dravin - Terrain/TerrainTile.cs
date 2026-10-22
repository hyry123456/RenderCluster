using Cluster;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public class TerrainTile : TileUnility
    {
        public UnityEngine.TerrainData terrainData;
        public int tileRow, tileColumn;
        public Vector2 tileScale;

        public override void RenderTile(CommandBuffer buffer, RenderTargetIdentifier[] rvtBuffersId, MaterialPropertyBlock block, Mesh screenMesh, Matrix4x4 matrix)
        {
            block.SetVector("_TileScale", tileScale);
            block.SetVector("_TerrainSize", terrainData.size);
            //if(TileTable.level == 0)
            //    Debug.Log($"y = {tileRow} x = {tileColumn} –¥»Î¡Ànode X = {TileTable.width} node Y = {TileTable.height}");

            for (int i = 0; i < terrainData.alphamapTextureCount; i++)
            {
                int begin = i * 4;
                block.SetTexture("_BlendMap", terrainData.GetAlphamapTexture(i));
                block.SetFloat("_RowId", tileRow);
                block.SetFloat("_ColumnId", tileColumn);
                block.SetInt("_AlphaMapIndex", i);
                for (int j = 0; j < 4 && j + begin < terrainData.terrainLayers.Length; j++)
                {
                    var layer = terrainData.terrainLayers[j + begin];
                    string index = j.ToString();
                    block.SetTexture($"_AlbedoMap{index}", layer.diffuseTexture);
                    block.SetTexture($"_NormalMap{index}", 
                        layer.normalMapTexture? layer.normalMapTexture : Texture2D.normalTexture);
                    block.SetTexture($"_MaskMap{index}", 
                        layer.maskMapTexture ? layer.maskMapTexture : Texture2D.whiteTexture);
                    block.SetVector($"_ScaleOffset{index}", GetVector4(layer.tileSize, layer.tileOffset));
                    block.SetVector($"_PBRData{index}", new Vector3(layer.metallic, layer.smoothness, layer.normalScale));
                    block.SetVector($"_Color{index}", layer.specular);
                }
                buffer.DrawMesh(screenMesh, matrix, MyTerrain.TerrainTileMat, 0, 0, block);
            }
        }

        Vector4 GetVector4(Vector2 ve0, Vector2 ve1)
        {
            return new Vector4(ve0.x, ve0.y, ve1.x, ve1.y);
        }
    }
}