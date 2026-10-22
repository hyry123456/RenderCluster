using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public static class GraphicsUtility 
    {
        public static void BlitMRT(this CommandBuffer buffer, RenderTargetIdentifier[] colorIdentifier, RenderTargetIdentifier depthIdentifier, Material mat, int pass)
        {
            buffer.SetRenderTarget(colorIdentifier, depthIdentifier);
            buffer.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
        }

        public static void BlitSRT(this CommandBuffer buffer, RenderTargetIdentifier destination, Material mat, int pass)
        {
            buffer.SetRenderTarget(destination);
            //buffer.DrawMesh(mesh, Matrix4x4.identity, mat, 0, pass);
            buffer.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
        }

        public static void BlitSRT(this CommandBuffer buffer, Texture source, RenderTargetIdentifier destination, Material mat, int pass)
        {
            buffer.SetGlobalTexture(ShaderProperty._MainTex, source);
            buffer.SetRenderTarget(destination);
            //buffer.DrawMesh(mesh, Matrix4x4.identity, mat, 0, pass);
            buffer.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
        }

    }
}