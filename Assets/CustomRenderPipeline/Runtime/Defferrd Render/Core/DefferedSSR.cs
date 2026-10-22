//using UnityEngine;
//using UnityEngine.Rendering;

//namespace CustomRenderPipline.DefferPipeline
//{
//    class SSRProperty
//    {
//        public static int
//            ReflectionBuffer = Shader.PropertyToID("_ReflectionBuffer"),
//            JitterSizeAndOffsetId = Shader.PropertyToID("_JitterSizeAndOffset"),
//            TemporalBufferId = Shader.PropertyToID("_TemporalBuffer"),
//            ProjectId = Shader.PropertyToID("_Project"),
//            NoiseId = Shader.PropertyToID("_Noise"),
//            NoiseSizeId = Shader.PropertyToID("_NoiseSize"),
//            NumStepsId = Shader.PropertyToID("_NumSteps"),
//            ThicknessId = Shader.PropertyToID("_Thickness"),
//            WorldToCamMatId = Shader.PropertyToID("_WorldToCameraMatrix"),
//            ProjectionMatId = Shader.PropertyToID("_ProjectionMatrix"),
//            BRDF_BiasId = Shader.PropertyToID("_BRDFBias"),
//            RayReuseId = Shader.PropertyToID("_RayReuse"),
//            UseNormalizeId = Shader.PropertyToID("_UseNormalization"),
//            EdgeFactorId = Shader.PropertyToID("_EdgeFactor"),
//            FirefliesId = Shader.PropertyToID("_Fireflies"),
//            SSR_SizeId = Shader.PropertyToID("_SSR_Size");

//        public static int 
//            SSR_TargetTexId = Shader.PropertyToID("_SSRTargetTex"),
//            ResolveTexId = Shader.PropertyToID("_ResolveTex"),
//            RayCastTexId = Shader.PropertyToID("_RayCastTex"),
//            RayCastMaskId = Shader.PropertyToID("_RayCastMaskTex");
//    }

//    public enum SSRPass
//    {
//        RayCast = 0,
//        Resovle = 1,
//        Temporal = 2,
//        CombineSSR = 3,
//    }

//    public class DefferedSSR
//    {
////#if UNITY_EDITOR
////        RenderTexture scenePreFrame;
////#endif
////        RenderTexture gamePreFrame;

//        // From Unity TAA
//        private int m_SampleIndex = 0;
//        private const int k_SampleCount = 64;
//        Matrix4x4 prevViewProjectionMatrix;
//        Matrix4x4 viewProjectionMatrix;
//        //这个RT需要一直存在，作为减噪的根据
//        public RenderTexture temporalBuffer;

//        //public RenderTargetIdentifier[] rayTargets = new RenderTargetIdentifier[2];
//        RenderTargetIdentifier[] rayTargets = new RenderTargetIdentifier[2];
//        Vector2 jitterSample;

//        private void SetSSR_RayTarget()
//        {
//            rayTargets[0] = SSRProperty.RayCastTexId;
//            rayTargets[1] = SSRProperty.RayCastMaskId;
//            jitterSample = GenerateRandomOffset();
//        }

//		/// <summary>/// 将SSR数据写入到目标纹理上/// </summary>
//		/// <param name="preFrameRenderFinal">上一帧渲染出来的纹理</param>
//		public void DrawSSR(SSR_Setting ssrSetting, 
//			int width, int height, CommandBuffer buffer, Camera camera)
//		{
//            buffer.BeginSample("Deffer_SSR");
//			int ssr_Width = width / 2, ssr_Height = height / 2;
//            jitterSample = GenerateRandomOffset();

//            ssrSetting.SSRMat.SetVector(SSRProperty.JitterSizeAndOffsetId,
//			    new Vector4
//				    (
//					    (float)ssr_Width / (float)ssrSetting.Noise.width,
//					    (float)ssr_Height / (float)ssrSetting.Noise.height,
//                        jitterSample.x,
//                        jitterSample.y
//				    )
//			);
//            ssrSetting.SSRMat.SetVector(SSRProperty.SSR_SizeId, 
//                new Vector2((float)ssr_Width, (float)ssr_Height));

//			ReadySSRBuffer(width, height);//准备Buffer
//			UpdateVariable(ssrSetting.SSRMat, ssrSetting, camera);   //更新参数

//			Vector4 project = new Vector4(Mathf.Abs(camera.projectionMatrix.m00 * 0.5f),
//				Mathf.Abs(camera.projectionMatrix.m11 * 0.5f),
//				((camera.farClipPlane * camera.nearClipPlane) /
//				(camera.nearClipPlane - camera.farClipPlane)) * 0.5f, 0.0f);
//            ssrSetting.SSRMat.SetVector(SSRProperty.ProjectId, project);

//			buffer.GetTemporaryRT(SSRProperty.ResolveTexId, ssr_Width, ssr_Height, 0, FilterMode.Bilinear,
//				RenderTextureFormat.DefaultHDR);
//			buffer.GetTemporaryRT(SSRProperty.RayCastTexId, ssr_Width, ssr_Height, 0, FilterMode.Bilinear,
//				RenderTextureFormat.ARGBHalf);
//			buffer.GetTemporaryRT(SSRProperty.RayCastMaskId, ssr_Width, ssr_Height, 0, FilterMode.Bilinear,
//				RenderTextureFormat.RHalf);

//            SetSSR_RayTarget();

//			buffer.SetRenderTarget(rayTargets, SSRProperty.RayCastTexId);

//			buffer.DrawProcedural(
//				Matrix4x4.identity, ssrSetting.SSRMat, (int)SSRPass.RayCast, MeshTopology.Quads, 6
//			);

//            //渲染出最终的颜色贴图
//            Draw(ShaderProperty.ColorAttachmentId, SSRProperty.ResolveTexId, SSRPass.Resovle,
//                buffer, ssrSetting.SSRMat);


//            buffer.SetGlobalTexture(SSRProperty.ReflectionBuffer, SSRProperty.ResolveTexId);

//			if (ssrSetting.useTemporal)
//			{
//				buffer.SetGlobalFloat("_TScale", ssrSetting.scale);
//				buffer.SetGlobalFloat("_TResponse", ssrSetting.response);

//				buffer.GetTemporaryRT(SSRProperty.TemporalBufferId, width, height, 
//                    0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
//				//设置前一张Temporal RT
//				buffer.SetGlobalTexture("_PreviousBuffer", temporalBuffer);

//				Draw(SSRProperty.ResolveTexId, SSRProperty.TemporalBufferId, SSRPass.Temporal,
//                    buffer, ssrSetting.SSRMat);

//                //Draw(SSRProperty.TemporalBufferId, temporalBuffer, SSRPass.Copy,
//                //                buffer, ssrSetting.SSRMat);
//                buffer.Blit(SSRProperty.TemporalBufferId, temporalBuffer);

//				buffer.ReleaseTemporaryRT(SSRProperty.TemporalBufferId);
//				buffer.SetGlobalTexture(SSRProperty.ReflectionBuffer, temporalBuffer);
//			}

//			buffer.GetTemporaryRT(SSRProperty.SSR_TargetTexId, width, height, 0, FilterMode.Bilinear,
//				RenderTextureFormat.DefaultHDR);
//			Draw(temporalBuffer, SSRProperty.SSR_TargetTexId, SSRPass.CombineSSR,
//                buffer, ssrSetting.SSRMat);

//			buffer.ReleaseTemporaryRT(SSRProperty.RayCastTexId);
//			buffer.ReleaseTemporaryRT(SSRProperty.RayCastMaskId);
//			buffer.ReleaseTemporaryRT(SSRProperty.ResolveTexId);
//            buffer.EndSample("Deffer_SSR");
//        }

//        public void SaveTexture(CommandBuffer buffer, int source, Camera camera)
//        {
//#if UNITY_EDITOR
//            //if (camera.cameraType == CameraType.SceneView)
//            //    buffer.Blit(source, scenePreFrame);
//            //else
//            //{
//            //    buffer.Blit(source, gamePreFrame);
//            //}
//#else
//                //buffer.Blit(source, gamePreFrame);
//#endif
//        }


//        //public void Save

//        private float GetHaltonValue(int index, int radix)
//        {
//            float result = 0f;
//            float fraction = 1f / (float)radix;

//            while (index > 0)
//            {
//                result += (float)(index % radix) * fraction;

//                index /= radix;
//                fraction /= (float)radix;
//            }

//            return result;
//        }

//        //生成随机的偏移值
//        private Vector2 GenerateRandomOffset()
//        {
//            var offset = new Vector2(
//                    GetHaltonValue(m_SampleIndex & 1023, 2),
//                    GetHaltonValue(m_SampleIndex & 1023, 3));

//            if (++m_SampleIndex >= k_SampleCount)
//                m_SampleIndex = 0;

//            return offset;
//        }



//        /// <summary> /// 生成所有需要的Buffer   /// </summary>
//        private void ReadySSRBuffer(int width, int height)
//        {
//            if (temporalBuffer == null || temporalBuffer.width != width
//                || temporalBuffer.height != height)
//            {
//                if (temporalBuffer != null)
//                    RenderTexture.ReleaseTemporary(temporalBuffer);
//                temporalBuffer = null;
////                if (gamePreFrame != null)
////                {
////                    RenderTexture.ReleaseTemporary(gamePreFrame);
////                    gamePreFrame = null;

////#if UNITY_EDITOR
////                    RenderTexture.ReleaseTemporary(scenePreFrame);
////                    scenePreFrame = null;
////#endif
////                }
//            }

//            if (temporalBuffer == null)
//            {
//                temporalBuffer = RenderTexture.GetTemporary(width, height, 0,
//                    RenderTextureFormat.DefaultHDR);
//                temporalBuffer.name = "Temporal";
//            }

////            if(gamePreFrame == null)
////            {
////                gamePreFrame = RenderTexture.GetTemporary(width, height, 0,
////                    RenderTextureFormat.DefaultHDR);
////                gamePreFrame.name = "GamePreFrame";
////#if UNITY_EDITOR
////                scenePreFrame = RenderTexture.GetTemporary(width, height, 0,
////                    RenderTextureFormat.DefaultHDR);
////                scenePreFrame.name = "ScenePreFrame";
////#endif
////            }


//        }

//        //private void UpdateVariable(Material material, SSR setting,
//        //    Camera camera)
//        //{
//        //    material.SetFloat(SSRProperty.BRDF_BiasId, setting.bRDF_Bias);
//        //    material.SetInt(SSRProperty.RayReuseId, setting.rayReuse ? 1 : 0);
//        //    material.SetInt(SSRProperty.UseNormalizeId, setting.useNomraliztion ? 1 : 0);
//        //    material.SetFloat(SSRProperty.EdgeFactorId, setting.edgeFactor);
//        //    material.SetInt(SSRProperty.FirefliesId, setting.reduceFireflies ? 1 : 0);

//        //    Texture2D noise = setting.Noise;
//        //    material.SetTexture(SSRProperty.NoiseId, noise);
//        //    material.SetVector(SSRProperty.NoiseSizeId, new Vector2(noise.width, noise.height));
//        //    material.SetInt(SSRProperty.NumStepsId, setting.rayMarchingSetp);

//        //    //由于Unity底层传入的单位矩阵，因此需要在这里重新获取
//        //    Matrix4x4 worldToCameraMatrix = camera.worldToCameraMatrix;

//        //    //根据当前图形API调整矩阵
//        //    Matrix4x4 projectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
//        //    viewProjectionMatrix = projectionMatrix * worldToCameraMatrix;

//        //    material.SetMatrix(SSRProperty.ProjectionMatId, projectionMatrix);
//        //    material.SetMatrix(SSRProperty.WorldToCamMatId, worldToCameraMatrix);


//        //    material.SetMatrix("_PrevViewProjectionMatrix", prevViewProjectionMatrix);

//        //    prevViewProjectionMatrix = viewProjectionMatrix;



//        //}


//        /// <summary>	/// 进行图片绘制，这个方法不是Blit，需要注意	/// </summary>
//        /// <param name="from">根据纹理</param>
//        /// <param name="to">目标纹理</param>
//        protected void Draw(
//            RenderTargetIdentifier from, RenderTargetIdentifier to,
//            SSRPass mode, CommandBuffer buffer, Material material
//        )
//        {
//            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, from);
//            //buffer.Blit(null, to, material, pass);

//            buffer.SetRenderTarget(to, RenderBufferLoadAction.DontCare,
//                RenderBufferStoreAction.Store);

//            buffer.DrawProcedural(
//                Matrix4x4.identity, material, (int)mode,
//                MeshTopology.Triangles, 3);
//        }
//    }
//}