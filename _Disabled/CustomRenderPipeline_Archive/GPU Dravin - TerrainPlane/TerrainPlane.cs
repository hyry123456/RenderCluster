using CustomRenderPipline;
using UnityEngine;
using UnityEngine.Rendering;

namespace KWS
{
    public class TerrainPlane : PlaneBase
    {
        const string TerrainKeyword = "MIN_MAX_HIGHT_MAP";
        [SerializeField]
        private RenderTexture _minMaxHeightMap;     //Min Max height Texture
        private RenderTexture _hieghtMap;

        public TerrainPlane(
            Vector3 worldPosition, TerrainPlaneData data) : 
            base(data.meshScale, false, data.TerrainSize, worldPosition, data.nodeLevel, Vector3.zero, false)
        {
            TerrainData terrainData = data.terrainData;
            _minMaxHeightMap = TerrainTextureCreate.CreateMinMaxHightMips(terrainData.heightmapTexture,
                data.textureCompute);
            _hieghtMap = terrainData.heightmapTexture;
        }

        public override void Release()
        {
            base.Release();
            if(_minMaxHeightMap != null)
            {
                _minMaxHeightMap.Release();
            }
        }

        public override void BeforePingPongCreateTree(ComputeShader compute)
        {
            compute.SetKeyword(TerrainKeyword, true);
            compute.SetTexture(PlaneTool._kernelOfTraverseQuadTree, PlaneConstant.MinMaxHeightMap, _minMaxHeightMap);
            compute.SetInt(PlaneConstant.MinMaxHeightMapMipsOffset, _minMaxHeightMap.mipmapCount - 5);  //5¼¶Min Map
        }

        public override void AfterPingPongCreateTree(ComputeShader compute)
        {
            compute.SetKeyword(TerrainKeyword, false);
        }

        public override void BeforeCulling(ComputeShader compute, CommandBuffer cmd)
        {
            cmd.EnableShaderKeyword(TerrainKeyword);
            cmd.SetComputeTextureParam(compute, PlaneTool._kernelOfBuildPatches, PlaneConstant.MinMaxHeightMap, _minMaxHeightMap);
            cmd.SetComputeTextureParam(compute, PlaneTool._kernelOfShadowBuildPatches, PlaneConstant.MinMaxHeightMap, _minMaxHeightMap);
        }

        public override void AfterCulling(ComputeShader compute, CommandBuffer cmd)
        {
            cmd.EnableShaderKeyword(TerrainKeyword);
        }

        public void Check(TerrainPlaneData data)
        {
            if (data.terrainData.heightmapTexture != _hieghtMap)
            {
                _minMaxHeightMap?.Release();
                _minMaxHeightMap = TerrainTextureCreate.CreateMinMaxHightMips(data.terrainData.heightmapTexture,
                    data.textureCompute);
            }
        }
    }
}