using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
	///// <summary>
	/////	вʵǰ2ʶҪ
	///// </summary>
	//public enum CameraRenderMode
 //   {
	//	_CopyColor = 0,
	//	_CopyDepth = 1,
	//	_
	//}

	/// <summary>
	/// Ⱦ߻࣬һϵEditorĺԼ
	/// </summary>
	public abstract class RenderPipelineBase
    {
		protected const string bufferName = "Render Camera";
		protected Material material;

		protected Camera camera;
		protected RenderSetting renderSetting;
		protected CommandBuffer buffer = new CommandBuffer
		{
			name = bufferName
		};
		protected ScriptableRenderContext context;
		protected CullingResults cullingResults;
		protected SortingSettings sortingSettings;
		protected DrawingSettings drawingSettings;
		protected FilteringSettings filteringSettings;

		protected Lighting lighting = new Lighting();

		public abstract void Dispose();


		/// <summary>	/// ͼƬƣBlitҪע	/// </summary>
		/// <param name="from"></param>
		/// <param name="to">Ŀ</param>
		protected void Draw(
			RenderTargetIdentifier from, RenderTargetIdentifier to, int pass
		)
		{
			buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, from);
			//buffer.Blit(null, to, material, pass);

			buffer.SetRenderTarget(to, RenderBufferLoadAction.DontCare,
				RenderBufferStoreAction.DontCare);

			buffer.DrawProcedural(
				Matrix4x4.identity, material, pass,
				MeshTopology.Triangles, 3);
		}


		/// <summary>	/// ִBuffer	/// </summary>
		protected void ExecuteBuffer()
		{
			context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}

		protected string SampleName { get; set; }

		protected Matrix4x4 GetFrustumMatrix()
		{
			Matrix4x4 frustumCorners = Matrix4x4.identity;
			Transform cameraTransform = camera.transform;
			float fov = camera.fieldOfView;
			float near = camera.nearClipPlane;
			//aspect = width / height
			float aspect = camera.aspect;

			//ƽĸ߶ȣfov*0.5*Mathf.Deg2RedһǶֵʹtanֵǸ߶ˣ廭ͼ
			float halfHeight = near * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
			//halfHeight * aspectwidthС䷽ȥұԵķ
			Vector3 toRight = cameraTransform.right * halfHeight * aspect;
			//ͬõķ
			Vector3 toTop = cameraTransform.up * halfHeight;

			//Ϸ
			Vector3 topLeft = cameraTransform.forward * near + toTop - toRight;
			//õnearΪ1ʱϷĳ
			float scale = topLeft.magnitude / near;
			//׼÷
			topLeft.Normalize();
			//ŴСʹΪnearΪ1ʱĴС
			topLeft *= scale;

			//Ϸ
			Vector3 topRight = cameraTransform.forward * near + toRight + toTop;
			topRight.Normalize();
			topRight *= scale;

			//·
			Vector3 bottomLeft = cameraTransform.forward * near - toTop - toRight;
			bottomLeft.Normalize();
			bottomLeft *= scale;

			//·
			Vector3 bottomRight = cameraTransform.forward * near + toRight - toTop;
			bottomRight.Normalize();
			bottomRight *= scale;

			//ȷˣnearΪ1ʱԭ㵽ĸĶķǰȵķֵ
			//潫Щݴݸ
			frustumCorners.SetRow(0, bottomLeft);
			frustumCorners.SetRow(1, bottomRight);
			frustumCorners.SetRow(2, topRight);
			frustumCorners.SetRow(3, topLeft);

			return frustumCorners;
		}



#if UNITY_EDITOR

        protected static ShaderTagId[] legacyShaderTagIds = {
			new ShaderTagId("Always"),
			new ShaderTagId("ForwardBase"),
			new ShaderTagId("PrepassBase"),
			new ShaderTagId("Vertex"),
			new ShaderTagId("VertexLMRGBM"),
			new ShaderTagId("VertexLM")
		};

		static Material errorMaterial;

		/// <summary>	/// UIܹڴʾ	/// </summary>
		protected void PrepareForSceneWindow()
		{
			if (camera.cameraType == CameraType.SceneView)
			{   //ֻгſò
				ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
			}
		}

		/// <summary>	/// ׼ܷһδ	/// </summary>
		protected void PrepareBuffer()
		{
			Profiler.BeginSample("Editor Only");
			buffer.name = SampleName = camera.name;
			Profiler.EndSample();
		}

		/// <summary>	/// ֵ֧һ±Ч	/// </summary>
		protected void DrawUnsupportedShaders()
		{
			if (errorMaterial == null)
			{
				errorMaterial =
					new Material(Shader.Find("Hidden/InternalErrorShader"));
			}
			var drawingSettings = new DrawingSettings(
				legacyShaderTagIds[0], new SortingSettings(camera)
			)
			{
				overrideMaterial = errorMaterial
			};
			for (int i = 1; i < legacyShaderTagIds.Length; i++)
			{
				drawingSettings.SetShaderPassName(i, legacyShaderTagIds[i]);
			}
            var filteringSettings = FilteringSettings.defaultValue;
            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );
		}

		protected void DrawGizmosBeforeFX(int depthId, int pass)
		{
			if (Handles.ShouldRenderGizmos())
			{
                Draw(depthId, BuiltinRenderTextureType.CameraTarget, pass);
                ExecuteBuffer();
				context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
			}

		}

		protected void DrawGizmosAfterFX()
		{
			if (Handles.ShouldRenderGizmos())
			{
				context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
			}
		}

#else

		protected void PrepareForSceneWindow() { }

		protected void PrepareBuffer() {}

		protected void DrawUnsupportedShaders() {}

		protected void DrawGizmosBeforeFX(int depth, int t) {}
		protected void DrawGizmosAfterFX() {}

#endif


    }
}