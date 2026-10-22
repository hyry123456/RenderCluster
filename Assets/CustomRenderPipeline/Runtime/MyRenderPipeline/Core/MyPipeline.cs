using CustomRenderPipline.DefferPipeline;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline
{
    public class MyPipeline : RenderPipelineBase
    {
        int width, height;
        /// <summary> /// 洢GBufferId  /// </summary>
        RenderTargetIdentifier[] gBuffers;
        DefferSetting defferSetting;

        MyLighting myLighting;
        StochasticScreenSpaceReflection spaceReflect = new StochasticScreenSpaceReflection();
        AmbientOcclusion ambientOcclusion = new AmbientOcclusion();

        bool RenderSSR
        {
            get
            {
                return defferSetting.ssrSetting.useSSR;
            }
        }

        bool RenderAO
        {
            get
            {
                return defferSetting.ambientOcclusionSetting.useAO;
            }
        }
        public MyPipeline(Shader shader)
        {
            //GraphicsSettings.useScriptableRenderPipelineBatching = true;
            //һȾbuffer
            material = CoreUtils.CreateEngineMaterial(shader);
            myLighting = new MyLighting(material);
        }

        public override void Dispose()
        {
            //MyPipeline 持有：自己的 MyLighting（Persistent NativeArray + ComputeBuffer）、
            //基类 RenderPipelineBase 初始化的 lighting，以及 SSR（postFXStack 已归档移出 Assets）。
            //原来这里全部被注释掉，导致每次运行结束都被报 Persistent 内存泄漏，
            //必须在管线销毁时全部释放
            myLighting?.Dispose();
            lighting?.Dispose();
            spaceReflect?.Dispose();
        }

        public void Render(ScriptableRenderContext context, Camera camera,
            RenderSetting renderSetting, ShadowSetting shadowSetting,
            DefferSetting defferSetting)
        {
            this.camera = camera;
            this.context = context;
            this.renderSetting = renderSetting;
            this.defferSetting = defferSetting;

            PrepareBuffer();
            PrepareForSceneWindow();        //׼UI

            int renderLayerMask = renderSetting.RenderLayer;

            float shadowDistance;
            //׼޳
            if (!Cull(shadowSetting.maxDistance, out shadowDistance))
            {   //޳׼
                return;     //׼ʧܾ˳
            }
            buffer.EnableShaderKeyword("_MY_LIGHTING");

            //lighting.Setup(
            //    context, renderLayerMask, cullingResults, shadowSetting);
            myLighting.Setup(
                context, renderLayerMask, cullingResults, shadowSetting,
                shadowDistance, camera);

            Setup(renderLayerMask);


            //ȾGbufferݣ׼ͼ
            DrawGBuffer(renderLayerMask);

            //ȾGBufferɫԼ͸Լպ
            DrawGBufferLater();

            DrawGizmosBeforeFX(DefferProperty.GBufferDepthId, (int)DefferRenderPass._CopyDepth);       //ںǰ׼һGizmosҪ
                        DrawFinal();
            DrawGizmosAfterFX();    //յGizmosЧ
            buffer.DisableShaderKeyword("_MY_LIGHTING");

            Cleanup();              //
            Submit();
        }

        private void CreateGBuffer()
        {
            //ȡͼ
            buffer.GetTemporaryRT(
                DefferProperty.GBufferDepthId, width, height,
                32, FilterMode.Point, RenderTextureFormat.Depth, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            //洢ȫGbuffer
            gBuffers = new RenderTargetIdentifier[DefferProperty.GBufferIds.Length];
            //ֵ
            for (int i = 0; i < gBuffers.Length; i++)
            {
                gBuffers[i] = DefferProperty.GBufferIds[i];
            }
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[0], width, height, 0,
                FilterMode.Bilinear, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[1], width, height, 0,
                FilterMode.Bilinear, defferSetting.accuracyNormal ?
                RenderTextureFormat.ARGB64 : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            //ΪHDRԷ
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[2], width, height, 0,
                FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            //ΪHDR洢HDRķ
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[3], width, height, 0,
                FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
        }

        void Setup(int renderLayer)
        {
            //׼ݣ޳֮
            context.SetupCameraProperties(camera);
            width = (int)(camera.pixelWidth * renderSetting.renderScale);
            height = (int)(camera.pixelHeight * renderSetting.renderScale);


//            if (camera.cameraType == CameraType.Game && ClusterControl.Instance.ClusterAssembleAsset.UseRVT)
//            {
//#if UNITY_EDITOR
//                if (!EditorApplication.isPaused)     //ͣʱȾFeedback
//#endif
//                    if (clusterCore != null)
//                        clusterCore.RenderFeedback(width, height, renderLayer,
//                            buffer, context, cullingResults, camera);
//                //ȾɣҪȾ
//                //ʼȾ̣ĿǰЧѾܸˣ
//                //ӦʱҪŻҸôʱᵼ¶ʧ
//            }

            buffer.SetGlobalVector(ShaderProperty.BufferSizeId, new Vector4(
                width, height, 1.0f / width, 1.0f / height));
            //GBufferĿ
            buffer.GetTemporaryRT(
                ShaderProperty.ColorAttachmentId, width, height, 0, FilterMode.Bilinear,
                renderSetting.allowHDR ?
                    RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default,
                RenderTextureReadWrite.Default, (int)renderSetting.antiAiasing, false
            );
            CreateGBuffer();

            GPUDravinSystem.Instance.SetUp(context, buffer, camera, width, height,
                renderSetting);


            //ȾĿ꣬еȾĿ
            buffer.SetRenderTarget(
                gBuffers,
                DefferProperty.GBufferDepthId
            );

            buffer.BeginSample(SampleName);

            //ǰõĿ
            buffer.ClearRenderTarget(true, true, Color.clear);

            Matrix4x4 frustum = GetFrustumMatrix();
            buffer.SetGlobalMatrix(ShaderProperty.FrustumCornersRayId, frustum);

            Matrix4x4 matrix4X4 = camera.projectionMatrix * camera.worldToCameraMatrix;
            buffer.SetGlobalMatrix(ShaderProperty.InverseVPMatrixId, matrix4X4.inverse);
            buffer.SetGlobalMatrix(DefferProperty.InverseProjectionMatrix, camera.projectionMatrix.inverse);

            Matrix4x4 clipToScreenMatrix = Matrix4x4.identity;
            clipToScreenMatrix.SetRow(0, new Vector4(width * 0.5f, 0, 0, width * 0.5f));
            clipToScreenMatrix.SetRow(1, new Vector4(0, height * 0.5f, 0, height * 0.5f));
            clipToScreenMatrix.SetRow(2, new Vector4(0, 0, 1.0f, 0));
            clipToScreenMatrix.SetRow(3, new Vector4(0, 0, 0, 1.0f));
            var viewToScreenMatrix = clipToScreenMatrix * camera.projectionMatrix;
            buffer.SetGlobalMatrix(DefferProperty.ViewToScreenMatrixId, viewToScreenMatrix);



            ExecuteBuffer();
        }


        /// <summary>  /// ȾGBuffer   /// </summary>
        void DrawGBuffer(int renderingLayerMask)
        {

            var renderObjects = MyRenderObject.Instance;
            if (renderObjects != null)
            {
                var staticJob = new StaticObjectViewJob();
                var planes = GPUDravinSystem.Instance.GetFrustumPlanes();
                staticJob.Plane0 = planes[0]; staticJob.Plane1 = planes[1];
                staticJob.Plane2 = planes[2]; staticJob.Plane3 = planes[3];
                staticJob.Plane4 = planes[4]; staticJob.Plane5 = planes[5];

                staticJob.staticObjs = renderObjects.StaticCPUObjects;
                int staticCount = renderObjects.CurrentStaticObjectsCount;
                JobHandle handle = staticJob.Schedule(staticCount, 1);
                //ȴҵ
                handle.Complete();
                for (int i = 0; i < staticCount; i++)
                {
                    if (staticJob.staticObjs[i].isUse)
                    {
                        var obj = renderObjects.StaticCPUObjects_data[i];
                        for (int j = 0; j < obj.renderMesh.subMeshCount; j++)
                            buffer.DrawMesh(obj.renderMesh,
                                obj.transform.localToWorldMatrix, obj.material, j, 0);
                    }

                    //if (GPUDravinSystem.Instance.FrustumCulling(renderObjects.StaticCPUObjects[i].range))
                    //{
                    //    var obj = renderObjects.StaticCPUObjects_data[i];
                    //    for (int j = 0; j < obj.renderMesh.subMeshCount; j++)
                    //        buffer.DrawMesh(obj.renderMesh,
                    //            obj.transform.localToWorldMatrix, obj.material, j, 0);
                    //}
                }

            }
            //Debug.Log(("Use Job:" + (Time.realtimeSinceStartup - startTime) * 1000f) + "ms");
            //ȾGPUı׼PBR
            GPUDravinSystem.Instance.DrawGBuffer(context, buffer, camera);

            myLighting.ReadyClusterLight(camera, defferSetting.lightingSettings.clusterLightSetting,
                buffer, width, height);

            DrawGBufferFinal();         //BRDF


            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

        }

        /// <summary>
        /// ȾGBufferյɫݣԼ֮ݣպԼ͸е壬
        /// 䵽Ϊмƹݣͼвü
        /// </summary>
        void DrawGBufferLater()
        {

            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

            sortingSettings.criteria = SortingCriteria.CommonTransparent;
            drawingSettings.sortingSettings = sortingSettings;
            filteringSettings.renderQueueRange = RenderQueueRange.transparent;
            drawingSettings.SetShaderPassName(0, DefferProperty.LitShaderTagId);
            drawingSettings.SetShaderPassName(1, DefferProperty.LitShader2TagId);

            ExecuteBuffer();

            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );

            //ComputeShaderʵֵ
            GPUDravinSystem.Instance.DrawTransparent(context, buffer, camera);

            ExecuteBuffer();
        }

        /// <summary>        /// ȷGBufferɫ        /// </summary>
        void DrawGBufferFinal()
        {
            DrawGBufferFinal(ShaderProperty.ColorAttachmentId);
            //ǰûӰ죬ҲSSR Add֪
            if (renderSetting.CopyDepth)
            {
                buffer.GetTemporaryRT(ShaderProperty.CameraDepthTexId, width, height,
                    32, FilterMode.Point, RenderTextureFormat.Depth);
                Draw(DefferProperty.GBufferDepthId, ShaderProperty.CameraDepthTexId,
                    (int)DefferRenderPass._CopyDepth);
            }

            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

            ExecuteBuffer();
            //պ
            context.DrawSkybox(camera);
            GPUDravinSystem.Instance.DrawSkyboxLater(context, buffer, camera);

            if (RenderAO)
            {
                ambientOcclusion.ReadyScreenRayData(buffer, width, height, defferSetting.ambientOcclusionSetting,
                    ShaderProperty.ColorAttachmentId);
            }
            //֤Ⱦ׼BRDF̺ٽи
            GPUDravinSystem.Instance.DrawOpaque(context, buffer, camera);


            if (RenderSSR)
            {
                //ΪչҪЧֱֲʹBRDFȾʱִеķ,
                GPUDravinSystem.Instance.DrawBeforeSSR(context, buffer, camera);

                spaceReflect.BeginRender(buffer, width, height, camera, defferSetting.ssrSetting);
            }


            //ɫ
            if (renderSetting.CopyColor)
            {
                buffer.GetTemporaryRT(ShaderProperty.CameraColorTexId, width, height,
                    0, FilterMode.Point, renderSetting.allowHDR ? RenderTextureFormat.DefaultHDR
                    : RenderTextureFormat.Default);

                buffer.Blit(ShaderProperty.ColorAttachmentId, ShaderProperty.CameraColorTexId);
            }

            ExecuteBuffer();
        }

        /// <summary>
        /// ҲǸSSSGBufferȾ
        /// </summary>
        private void DrawGBufferFinal(int targetTexId)
        {
            //注意：不能使用 (BuiltinRenderTextureType)0（None）作为 Blit 源，
            //None 是无法解析的“空”标识，会导致 "temporary render texture not found (Blit source)"，
            //且顶点着色器需要 _SourceTexture_TexelSize 判断 UV 翻转，必须绑定一个有效源纹理
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, targetTexId, material, (int)DefferRenderPass._GBufferDraw);
            ExecuteBuffer();
        }

        /// <summary>   /// ɫȾ   /// </summary>
        void DrawFinal()
        {
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId,
                ShaderProperty.ColorAttachmentId);

            RenderTargetIdentifier target = camera.targetTexture == null ?
                (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget
                : camera.targetTexture;

#if UNITY_EDITOR


            if (camera.cameraType != CameraType.Game)
            {
                buffer.Blit(ShaderProperty.ColorAttachmentId, target);
            }
            else
            {
                buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.DontCare);
                buffer.SetViewport(camera.pixelRect);

                int pass;
                switch (defferSetting.debugMode)
                {
                    case DefferDebugMode.Normal:
                        pass = (int)DefferRenderPass._DebugNormal; break;
                    case DefferDebugMode.Albedo:
                        pass = (int)DefferRenderPass._DebugAlbedo; break;
                    case DefferDebugMode.PBRData:
                        pass = (int)DefferRenderPass._DebugPBR; break;
                    default:
                        pass = (int)DefferRenderPass._CopyColor; break;
                }

                //Copy
                buffer.DrawProcedural(
                    Matrix4x4.identity, material, pass,
                    MeshTopology.Triangles, 3);
            }

#else
            buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.Store);
            buffer.SetViewport(camera.pixelRect);

            //Copy
            buffer.DrawProcedural(
                Matrix4x4.identity, material, (int)DefferRenderPass._CopyColor,
                MeshTopology.Triangles, 3);
#endif

            ExecuteBuffer();
        }


        /// <summary>	/// ִݽӰ޳ҪĲֽ޳	/// </summary>
        /// <param name="maxShadowDistance">޳</param>
        /// <returns>Ƿɹ޳</returns>
        bool Cull(float maxShadowDistance, out float shadowDistance)
        {
            shadowDistance = 0.0f;


            //ִ޳п޳ʧܣΪҪ
            if (camera.TryGetCullingParameters(out ScriptableCullingParameters p))
            {
                //Ӱ
                shadowDistance = Mathf.Min(maxShadowDistance, camera.farClipPlane);
                p.shadowDistance = 0;
                //ֵü
                cullingResults = context.Cull(ref p);
                return true;
            }


            return false;
        }


        /// <summary>	/// ʹùݣΪͼڴеģҪֶͷ	/// </summary>
        void Cleanup()
        {
            buffer.ReleaseTemporaryRT(ShaderProperty.ColorAttachmentId);
            buffer.ReleaseTemporaryRT(DefferProperty.GBufferDepthId);

            if (renderSetting.CopyDepth)
                buffer.ReleaseTemporaryRT(ShaderProperty.CameraDepthTexId);
            if (renderSetting.CopyColor)
                buffer.ReleaseTemporaryRT(ShaderProperty.CameraColorTexId);

            for (int i = 0; i < DefferProperty.GBufferIds.Length; i++)
            {
                buffer.ReleaseTemporaryRT(DefferProperty.GBufferIds[i]);
            }

            //lighting.Cleanup();		//ƹ

            GPUDravinSystem.Instance.CleanUp(context, buffer);

        }

        /// <summary>        /// ύϴ        /// </summary>
        void Submit()
        {
            buffer.EndSample(SampleName);
            ExecuteBuffer();
            context.Submit();
        }
    }
}