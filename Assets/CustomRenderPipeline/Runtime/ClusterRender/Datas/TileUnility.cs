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

        public override void RenderTile(CommandBuffer buffer, RenderTargetIdentifier[] rvtBuffersId,
            MaterialPropertyBlock block, Mesh screenMesh, Matrix4x4 matrix)
        {

            //设置全部贴图
            block.SetTexture(TileTable._AlbedoId,   albedo);
            block.SetTexture(TileTable._NormalId,   normal);
            block.SetTexture(TileTable._PBRDataId,  mask);
            block.SetTexture(TileTable._EmissionId, emison);

            // 注意 shaderPass 必须是 3：RVTShader 的 Pass3 才是「把源贴图拷进图集」的写入入口。
            buffer.DrawMesh(screenMesh, matrix, TileTable.StandardTileMat, 0, 3, block);
        }
    }
}