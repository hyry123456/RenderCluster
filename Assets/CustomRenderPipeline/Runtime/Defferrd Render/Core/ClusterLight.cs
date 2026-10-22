using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.DefferPipeline
{


    [ExecuteInEditMode]
    /// <summary>/// ӽǿռȷƹüCluster /// </summary>
	public class ClusterLight
	{
		/// <summary>/// ÿһTileĵƹ/// </summary>
		const int _Tile_Max_Light_Count = 32;

		//¼ÿһtileľƹ
		private ComputeBuffer _tileLightsIndicesBuffer;
		//¼ÿһtileĵƹ
		private ComputeBuffer _tileLightsArgsBuffer;
		ComputeShader _computeShader;


		private Vector4 BuildZBufferParams(float near, float far)
		{
			var result = new Vector4();
			result.x = 1 - far / near;
			result.y = 1 - result.x;
			result.z = result.x / far;
			result.w = result.y / far;
			return result;
		}

		private const int _TileSize = 16;

		public void ComputeLightCluster(CommandBuffer buffer,
			ComputeShader computeShader, Camera camera,
			int width, int height, bool debugTex)
		{
			if (computeShader == null) return;
			else _computeShader = computeShader;

			//uint tileSizeX, tileSizeY, tileSizeZ;
			//_computeShader.GetKernelThreadGroupSizes(0, out tileSizeX, out tileSizeY, out tileSizeZ);

			var tileCountX = Mathf.CeilToInt(width * 1f / _TileSize);
			var tileCountY = Mathf.CeilToInt(height * 1f / _TileSize);


			buffer.SetGlobalVector(ShaderConstants.DeferredTileParams, new Vector4(_TileSize, _TileSize, tileCountX, tileCountY));
		EnsureTileComputeBuffer(tileCountX, tileCountY);

		// 每帧都通过 CommandBuffer 重新绑定 RWBuffer。
		// 旧写法是「仅在创建缓冲区时」用 _computeShader.SetBuffer 立即模式绑定，有两个问题：
		//  1) ComputeShader 资产一旦被重新导入（例如它 include 的 Common.hlsl 被改动），
		//     旧的 ComputeShader 对象会被销毁并重建，所有立即模式绑定全部丢失；
		//     而此时缓冲区还在（尺寸没变），EnsureTileComputeBuffer 不会重建它，
		//     绑定永远不会补上 → 报
		//     "Compute shader (ClusterLight): Property (_RWTileLightsArgsBuffer) at kernel index (0) is not set"
		//  2) buffer.DispatchCompute 是延迟执行，本就该配 buffer.SetComputeBufferParam
		buffer.SetComputeBufferParam(_computeShader, 0,
			ShaderConstants.RWTileLightsArgsBuffer, _tileLightsArgsBuffer);
		buffer.SetComputeBufferParam(_computeShader, 0,
			ShaderConstants.RWTileLightsIndicesBuffer, _tileLightsIndicesBuffer);

		var nearPlaneZ = camera.nearClipPlane;
			var nearPlaneHeight = Mathf.Tan(Mathf.Deg2Rad * camera.fieldOfView * 0.5f) * 2 * camera.nearClipPlane;
			var nearPlaneWidth = camera.aspect * nearPlaneHeight;

            var zbufferParams = BuildZBufferParams(camera.nearClipPlane, camera.farClipPlane);
            buffer.SetComputeVectorParam(_computeShader, ShaderConstants.ZBufferParams, zbufferParams);
            buffer.SetComputeVectorParam(_computeShader, ShaderConstants.CameraNearPlaneLB, 
				new Vector4(-nearPlaneWidth / 2, -nearPlaneHeight / 2, nearPlaneZ, 0));
			var basisH = new Vector2(_TileSize * nearPlaneWidth / width, 0);
			var basisV = new Vector2(0, _TileSize * nearPlaneHeight / height);
			buffer.SetComputeVectorParam(_computeShader, ShaderConstants.CameraNearBasisH, basisH);
			buffer.SetComputeVectorParam(_computeShader, ShaderConstants.CameraNearBasisV, basisV);

            if (debugTex)
            {
                buffer.GetTemporaryRT(ShaderConstants.OutTexture, width, height,
					0, FilterMode.Bilinear, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default,
					1, true);
				buffer.SetRenderTarget(ShaderConstants.OutTexture);
				buffer.SetComputeTextureParam(_computeShader, 0,
					ShaderConstants.OutTexture, ShaderConstants.OutTexture);
            buffer.EnableShaderKeyword("VISABLE_LIGHT_COUNT");
			}
			else buffer.DisableShaderKeyword("VISABLE_LIGHT_COUNT");



			buffer.SetComputeMatrixParam(_computeShader, "_CameraMatrixV", camera.worldToCameraMatrix);

			buffer.DispatchCompute(_computeShader, 0, tileCountX, tileCountY, 1);

            if (debugTex)
            {
				buffer.ReleaseTemporaryRT(ShaderConstants.OutTexture);
			}
		}


		/// <summary>/// TileҪCompute Buffer	/// </summary>
		private void EnsureTileComputeBuffer(int tileCountX, int tileCountY)
		{
			var tileCount = tileCountX * tileCountY;
			var argsBufferSize = tileCount;
			var indicesBufferSize = tileCount * _Tile_Max_Light_Count;
			if (_tileLightsArgsBuffer != null && _tileLightsArgsBuffer.count != argsBufferSize)
			{
				_tileLightsArgsBuffer.Dispose();
				_tileLightsArgsBuffer = null;
			}
			if (_tileLightsArgsBuffer == null)
			{
				//ֻint
			_tileLightsArgsBuffer = new ComputeBuffer(argsBufferSize, sizeof(int));
			Shader.SetGlobalBuffer(ShaderConstants.TileLightsArgsBuffer, _tileLightsArgsBuffer);
			// RWBuffer 的绑定改到 ComputeLightCluster 里每帧用 buffer.SetComputeBufferParam 做，
			// 这里不再用立即模式的 SetBuffer（原因见那边的注释）
			GC.SuppressFinalize(_tileLightsArgsBuffer);
            }

            if (_tileLightsIndicesBuffer != null && _tileLightsIndicesBuffer.count != indicesBufferSize)
			{
				_tileLightsIndicesBuffer.Dispose();
				_tileLightsIndicesBuffer = null;
			}
			if (_tileLightsIndicesBuffer == null)
			{
			_tileLightsIndicesBuffer = new ComputeBuffer(indicesBufferSize, sizeof(int));
			GC.SuppressFinalize(_tileLightsIndicesBuffer);
			Shader.SetGlobalBuffer(ShaderConstants.TileLightsIndicesBuffer, _tileLightsIndicesBuffer);
			}
		}


		public void Dispose()
        {
			_tileLightsArgsBuffer?.Dispose();
			_tileLightsIndicesBuffer?.Dispose();
        }



		public static class ShaderConstants
		{
            //public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int OutTexture = Shader.PropertyToID("_OutTexture");
            public static readonly int ZBufferParams = Shader.PropertyToID("_ZBufferParams");
            public static readonly int CameraNearPlaneLB = Shader.PropertyToID("_CameraNearPlaneLB");
            public static readonly int CameraNearBasisH = Shader.PropertyToID("_CameraNearBasisH");
            public static readonly int CameraNearBasisV = Shader.PropertyToID("_CameraNearBasisV");
            public static readonly int RWTileLightsArgsBuffer = Shader.PropertyToID("_RWTileLightsArgsBuffer");
            public static readonly int RWTileLightsIndicesBuffer = Shader.PropertyToID("_RWTileLightsIndicesBuffer");


            ////*************Ϊȫ*****************//

            public static readonly int TileLightsArgsBuffer = Shader.PropertyToID("_TileLightsArgsBuffer");
            public static readonly int TileLightsIndicesBuffer = Shader.PropertyToID("_TileLightsIndicesBuffer");

            /// <summary>/// ÿһTile/// </summary>
            public static readonly int DeferredTileParams = Shader.PropertyToID("_DeferredTileParams");

		}


	}
}