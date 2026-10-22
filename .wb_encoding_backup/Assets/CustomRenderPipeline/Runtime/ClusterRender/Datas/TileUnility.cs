using UnityEngine;
using UnityEngine.Rendering;

namespace Cluster
{
    public abstract class TileUnility
    {
        public abstract void RenderTile(CommandBuffer buffer, RenderTargetIdentifier[] rvtBuffersId,
            MaterialPropertyBlock block, Mesh screenMesh, Matrix4x4 matrix);
    }

    public class BRDF_Tile : TileUnility
    {
        public Texture2D albedo;
        public Texture2D normal;
        public Texture2D mask;
        public Texture2D emison;
        public Texture2D detial;

        public override void RenderTile(CommandBuffer buffer, RenderTargetIdentifier[] rvtBuffersId,
            MaterialPropertyBlock block, Mesh screenMesh, Matrix4x4 matrix)
        {

            //…Ë÷√»´≤øÃ˘Õº
            block.SetTexture(TileTable._AlbedoId,   albedo);
            block.SetTexture(TileTable._NormalId,   normal);
            block.SetTexture(TileTable._PBRDataId,  mask);
            block.SetTexture(TileTable._EmissionId, emison);
            block.SetTexture(TileTable._DetailId,   detial);

            buffer.DrawMesh(screenMesh, matrix, TileTable.StandardTileMat, 0, 3, block);
        }
    }
}