using Cluster;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace CustomRenderPipline.DefferPipeline
{
    class DefferProperty
    {
        public static int GBufferDepthId =
            Shader.PropertyToID("_GBufferDepthTex");//Gbuffer的深度图
                                                    //存储GBuffer的一般贴图的数组
        public static int[] GBufferIds =
        {
            Shader.PropertyToID("_GBufferRT0"),     //rgb:abledo,w:metalness
            Shader.PropertyToID("_GBufferRT1"),     //R,G:EncodeNormal
            Shader.PropertyToID("_GBufferRT2"),     //rgb:emissive,w:roughness
            Shader.PropertyToID("_GBufferRT3"),     //rgb:reflect,w:AO
        };

        public static ShaderTagId
            LitShaderTagId = new ShaderTagId("CustomLit"),          //透明物体，使用前向渲染
            LitShader2TagId = new ShaderTagId("CustomLitAdd"),      //第二个透明Pass
            GBufferShaderTagId = new ShaderTagId("OutGBuffer");     //输出GBuffer

        public static int
            InverseProjectionMatrix = Shader.PropertyToID("_InverseProjectionMatrix"),
            ViewToScreenMatrixId = Shader.PropertyToID("_ViewToScreenMatrix");
    }

    enum DefferRenderPass
    {
        _CopyColor = 0,
        _CopyDepth = 1,
        _BlitDepth = 2,
        _GBufferDraw = 3,
        _AddReflect = 4,
        _DebugNormal = 5,
        _DebugAlbedo = 6,
        _DebugPBR = 7,
    }

    /// <summary> /// 延迟管线的实际渲染类   /// </summary>
    public class DefferPipeline : RenderPipelineBase
    {
        DefferSetting defferSetting;

        int width, height;
        /// <summary> /// 存储GBuffer的所有Id  /// </summary>
        RenderTargetIdentifier[] gBuffers;

        StochasticScreenSpaceReflection spaceReflect = new StochasticScreenSpaceReflection();
        AmbientOcclusion ambientOcclusion = new AmbientOcclusion();
        ClusterCore clusterCore = new ClusterCore();

        /// <summary>本帧 GBuffer 是否交给 Cluster 绘制（Game 摄像机 + 资产开关 + 数据就绪）。</summary>
        /// <summary>本帧 GBuffer 是否由 Cluster 绘制（= clusterMode 非 SRP 且剔除已就绪）。</summary>
        bool useCluster;

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

        public DefferPipeline(Shader shader)
        {
            //创建一个材质用来渲染buffer
            material = CoreUtils.CreateEngineMaterial(shader);
        }

        public void Render(ScriptableRenderContext context, Camera camera,
            RenderSetting renderSetting, ShadowSetting shadowSetting,
            DefferSetting defferSetting)
        {
            this.camera = camera;
            this.context = context;
            this.renderSetting = renderSetting;
            this.defferSetting = defferSetting;
            GraphicsSettings.useScriptableRenderPipelineBatching = renderSetting.useSRPBatching;


            //执行单个摄像机的gbuffer前操作
            var beforeGBuffers = camera.GetCommandBuffers(CameraEvent.BeforeGBuffer);
            foreach (var command in beforeGBuffers)
            {
                context.ExecuteCommandBuffer(command);
            }

            PrepareBuffer();
            PrepareForSceneWindow();        //准备UI数据

            // 植被风：把场景 WindZone 的参数推到全局属性（草的顶点位移在 Vegetation.shader 里做）。
            // 放在任何绘制之前、每帧一次即可。
            VegetationWind.Update();

            int renderLayerMask = renderSetting.RenderLayer;

            //准备剔除数据
            if (!Cull(shadowSetting.maxDistance, renderLayerMask))
            {   //摄像机剔除准备
                return;     //准备失败就退出
            }

            Profiler.BeginSample(SampleName);
            ExecuteBuffer();
            //之后加后处理、灯光等数据准备
            //准备灯光数据，在灯光数据中会进行阴影数据准备
            lighting.Setup(
                context, renderLayerMask, cullingResults, shadowSetting);
            //本摄像机渲染准备
            Setup(renderLayerMask);

            Profiler.EndSample();
            //渲染Gbuffer数据，准备深度图
            DrawGBuffer(renderLayerMask);

            lighting.ReadyClusterLight(camera, defferSetting.lightingSettings.clusterLightSetting,
                buffer, width, height);

            //渲染GBuffer最终颜色以及透明队列以及天空盒
            DrawGBufferLater();

            DrawUnsupportedShaders();   //绘制不支持的纹理


            DrawGizmosBeforeFX(DefferProperty.GBufferDepthId, (int)DefferRenderPass._CopyDepth);       //在后处理前准备一下Gizmos需要的数据
                                                                                                       //GL.wireframe = false;

            DrawFinal();

#if UNITY_EDITOR

            //注意：Instance 是懒加载单例（资产不存在时会自动 AssetDatabase.CreateAsset），
            //必须放在摄像机类型判断之后，否则编辑器预览渲染（非 Game 摄像机）也会触发创建，
            //在资产导入期间调用会直接抛 UnityException
            if (camera.cameraType == CameraType.Game && ClusterControl.Instance.ClusterAssembleAsset.MipsDebug)
            {
                buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                buffer.ClearRenderTarget(true, true, Color.black);
                buffer.EnableShaderKeyword("_FEED_BACK_DEBUG");

                //设置该摄像机的物体排序模式，目前是渲染普通物体，因此用一般排序方式
                var sortingSettings = new SortingSettings(camera)
                {
                    criteria = SortingCriteria.CommonOpaque
                };

                //第一次渲染只绘制GBuffer，且GBuffer仅渲染非透明
                var drawingSettings = new DrawingSettings(
                    ClusterCore.feedbackTagId, sortingSettings
                )
                {
                    enableDynamicBatching = true,
                    enableInstancing = true,
                };
                var filteringSettings = new FilteringSettings(
                    RenderQueueRange.opaque, renderingLayerMask: (uint)renderLayerMask
                );
                ExecuteBuffer();
                //进行渲染的执行方法
                context.DrawRenderers(
                    cullingResults, ref drawingSettings, ref filteringSettings
                );

                buffer.DisableShaderKeyword("_FEED_BACK_DEBUG");
                ExecuteBuffer();
            }
#endif


            DrawGizmosAfterFX();    //绘制最终的Gizmos效果

            var afterfinal = camera.GetCommandBuffers(CameraEvent.AfterFinalPass);
            foreach (var command in afterfinal)
            {
                context.ExecuteCommandBuffer(command);
            }

            Cleanup();              //清除数据
            Submit();
        }

        public override void Dispose()
        {
            //释放虚拟贴图资源（图集、页缓存、回读缓冲、页表），
            //否则 Persistent 分配的 NativeArray 与 ComputeBuffer 会在运行结束后被报内存泄漏
            if (clusterCore != null)
                clusterCore.Cleanup();
            // Cluster GBuffer 的 GPU 缓冲（顶点/索引/剔除）也必须显式释放
            if (ClusterGBufferRenderer.HasInstance)
                ClusterGBufferRenderer.Instance.Release();
            // 阴影侧的 GPU 缓冲 + 镂空物体被打上的 renderingLayerMask（打位/还原成对，别留脏数据）
            if (ClusterShadowRenderer.HasInstance)
                ClusterShadowRenderer.Instance.Release();
            lighting?.Dispose();
            spaceReflect?.Dispose();
        }

        /// <summary>	/// 执行摄像机数据进行阴影剔除，将不需要的部分进行剔除	/// </summary>
        /// <param name="maxShadowDistance">剔除距离</param>
        /// <returns>是否成功剔除</returns>
        bool Cull(float maxShadowDistance, int renderLayer)
        {
            //执行剔除，有可能剔除失败，因为要进行区分
            if (camera.TryGetCullingParameters(out ScriptableCullingParameters p))
            {
                //控制阴影距离
                p.shadowDistance = Mathf.Min(maxShadowDistance, camera.farClipPlane);
                //赋值裁剪结果
                //Profiler 采样点：Cull.Scene —— 场景剔除（ScriptableRenderContext.Cull）。
                //它是主线程上最贵的一段之一：内部会跑 Unity 的整个剔除管线。
                Profiler.BeginSample("Cull.Scene");
                cullingResults = context.Cull(ref p);
                Profiler.EndSample();


                return true;
            }



            return false;
        }

        void Setup(int renderLayer)
        {
            //准备摄像机数据，剔除之类的
            context.SetupCameraProperties(camera);
            width = (int)(camera.pixelWidth * renderSetting.renderScale);
            height = (int)(camera.pixelHeight * renderSetting.renderScale);

            GPUDravinSystem.Instance.SetUp(context, buffer, camera, width, height,
                renderSetting);

            // ===== Cluster 画 GBuffer：准备 + 一次剔除 =====
            // 剔除结果（可见簇列表 + indirect args）本帧被 GBuffer 和 Feedback 共用，
            // 所以必须在这里、在 RenderFeedback 之前 dispatch 完。
            // 触发条件 = 同一个 clusterMode 开关（不允许 GBuffer 和阴影各走一半）。
            // 只对 Game 摄像机生效：Scene 视图始终走 SRP，天然可作为 A/B 对照。
            useCluster = false;
            var gbuf = ClusterGBufferRenderer.HasInstance ? ClusterGBufferRenderer.Instance : null;
            if (gbuf != null) gbuf.active = false;

            var clusterAsset = ClusterControl.TryGetAssembleAsset();
            if (camera.cameraType == CameraType.Game && clusterAsset != null
                && clusterAsset.UseRVT && clusterAsset.clusterMode != ClusterMode.SRP)
            {
                if (gbuf == null) gbuf = ClusterGBufferRenderer.Instance;
                gbuf.debugForceAll = (clusterAsset.clusterMode == ClusterMode.ClusterDebug) ? 1 : 0;
                gbuf.active = true;
                if (gbuf.Prepare(camera))
                {
                    gbuf.Cull(buffer);
                    Profiler.BeginSample("Cluster.GBufferCull");
                    ExecuteBuffer();
                    Profiler.EndSample();
                    useCluster = true;
                }
            }

            if (camera.cameraType == CameraType.Game && ClusterControl.Instance.ClusterAssembleAsset.UseRVT)
            {
#if UNITY_EDITOR
                if (!EditorApplication.isPaused && Application.isPlaying)     //暂停时不渲染Feedback
#endif
                {
                    if (clusterCore != null && ClusterControl.Instance.ClusterAssembleAsset.UseRVT)
                        clusterCore.RenderFeedback(width, height, renderLayer,
                            buffer, context, cullingResults, camera);
                }

                //反馈渲染完成，接下来要尝试渲染了
                //接下来正常开始完成渲染流程，目前效率已经很高了，
                //应该暂时不需要优化，而且该代码时会导致丢失，不方便
            }

            buffer.SetGlobalVector(ShaderProperty.BufferSizeId, new Vector4(
                width, height, 1.0f / width, 1.0f / height));
            //GBuffer后处理到的目标纹理
            buffer.GetTemporaryRT(
                ShaderProperty.ColorAttachmentId, width, height, 0, FilterMode.Bilinear, 
                renderSetting.allowHDR ?
                    RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default,
                RenderTextureReadWrite.Default, (int)renderSetting.antiAiasing, false
            );
            CreateGBuffer();


            //设置渲染目标，传递所有的渲染目标
            buffer.SetRenderTarget(
                gBuffers,
                DefferProperty.GBufferDepthId
            );

            Profiler.BeginSample(SampleName);

            //清除当前设置的目标纹理的数据
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

        private void CreateGBuffer()
        {
            //获取深度图
            buffer.GetTemporaryRT(
                DefferProperty.GBufferDepthId, width, height,
                32, FilterMode.Point, RenderTextureFormat.Depth, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            //存储全部的Gbuffer数组
            gBuffers = new RenderTargetIdentifier[DefferProperty.GBufferIds.Length];
            //赋值编号
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
            //设置为HDR，方便自发光
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[2], width, height, 0,
                FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
            //设置为HDR，用来存储HDR的反射数据
            buffer.GetTemporaryRT(DefferProperty.GBufferIds[3], width, height, 0,
                FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default,
                (int)renderSetting.antiAiasing);
        }

        /// <summary>  /// 渲染GBuffer的数据   /// </summary>
        void DrawGBuffer(int renderingLayerMask)
        {
            //设置该摄像机的物体排序模式，目前是渲染普通物体，因此用一般排序方式
            sortingSettings = new SortingSettings(camera)
            {
                criteria = SortingCriteria.CommonOpaque
            };

            //第一次渲染只绘制GBuffer，且GBuffer仅渲染非透明
            drawingSettings = new DrawingSettings(
                DefferProperty.GBufferShaderTagId, sortingSettings
            )
            {
                enableDynamicBatching = renderSetting.useDynamicBatching,
                enableInstancing = renderSetting.useGPUInstancing,
                perObjectData =
                PerObjectData.ReflectionProbes |
                PerObjectData.Lightmaps | PerObjectData.ShadowMask |
                PerObjectData.LightProbe | PerObjectData.OcclusionProbe |
                PerObjectData.LightProbeProxyVolume |
                PerObjectData.OcclusionProbeProxyVolume
            };
            filteringSettings = new FilteringSettings(
                RenderQueueRange.opaque, renderingLayerMask: (uint)renderingLayerMask);

            // Cluster 接管 GBuffer 的物体：从 SRP 绘制里剔出去（不 Disable MeshRenderer，
            // 否则 SRP 阴影那条路会一起失效）。
            bool clusterDraw = useCluster && ClusterGBufferRenderer.Instance.IsReady;
            if (clusterDraw)
                filteringSettings.renderingLayerMask &=
                    ~(1u << ClusterProverty.ClusterRenderingLayerBit);

            //进行渲染的执行方法
            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );

            // 簇绘制（逐组间接绘制，几何由 GPU 从簇缓冲还原）
            if (clusterDraw && ClusterGBufferRenderer.Instance.DrawGBuffer(buffer))
            {
                Profiler.BeginSample("Cluster.GBufferDraw");
                ExecuteBuffer();
                Profiler.EndSample();
            }

            // 几何由上面的 context.DrawRenderers(cullingResults) 走 SRP 绘制；
            // 纹理精度由 RVT / Tile-VT 保证。
        }


        /// <summary>
        /// 渲染GBuffer的最终的颜色数据，以及之后的数据，比如天空盒以及透明队列的物体，
        /// 分配到这里是为了中间插入灯光数据，用深度图进行裁剪
        /// </summary>
        void DrawGBufferLater()
        {
            DrawGBufferFinal();         //BRDF

            ////深度写回
            //if (RenderSSR && defferSetting.DepthWriteBack)
            //{
            //    Draw(ShaderProperty.CameraDepthTexId, DefferProperty.GBufferDepthId,
            //        (int)DefferRenderPass._CopyDepth);
            //}


            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

//#if UNITY_EDITOR
//            drawingSettings.SetShaderPassName(0, DefferProperty.DefaultTagId);
//            filteringSettings.renderQueueRange = RenderQueueRange.all;
//            ExecuteBuffer();
//            context.DrawRenderers(
//                cullingResults, ref drawingSettings, ref filteringSettings
//            );
//#endif

            sortingSettings.criteria = SortingCriteria.CommonTransparent;
            drawingSettings.sortingSettings = sortingSettings;
            filteringSettings.renderQueueRange = RenderQueueRange.transparent;
            drawingSettings.SetShaderPassName(0, DefferProperty.LitShaderTagId);
            drawingSettings.SetShaderPassName(1, DefferProperty.LitShader2TagId);

            ExecuteBuffer();

            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );



            ExecuteBuffer();
        }

        /// <summary>        /// 确定GBuffer最后的颜色        /// </summary>
        void DrawGBufferFinal()
        {
            DrawGBufferFinal(ShaderProperty.ColorAttachmentId);

            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

            ExecuteBuffer();
            //绘制天空盒
            context.DrawSkybox(camera);

            if (RenderAO)
            {
                ambientOcclusion.ReadyScreenRayData(buffer, width, height, defferSetting.ambientOcclusionSetting,
                    ShaderProperty.ColorAttachmentId);
            }

            //深度提前拷贝，反正后面没影响，也好让SSR Add知道深度数据
            if (renderSetting.CopyDepth)
            {
                buffer.GetTemporaryRT(ShaderProperty.CameraDepthTexId, width, height,
                    32, FilterMode.Point, RenderTextureFormat.Depth);
                Draw(DefferProperty.GBufferDepthId, ShaderProperty.CameraDepthTexId,
                    (int)DefferRenderPass._CopyDepth);
            }

            //拷贝颜色
            if (renderSetting.CopyColor)
            {
                buffer.GetTemporaryRT(ShaderProperty.CameraColorTexId, width, height,
                    0, FilterMode.Point, renderSetting.allowHDR ? RenderTextureFormat.DefaultHDR
                    : RenderTextureFormat.Default);

                // 这里原来是无材质的 buffer.Blit。Unity 2022 起它会报
                // "State comes from an incompatible keyword space"：CommandBuffer 上残留着
                // 阴影/光照阶段用 buffer.EnableShaderKeyword 设的全局 keyword
                // （_DIRECTIONAL_PCF3/5/7、_OTHER_PCF3/5/7、_CASCADE_BLEND_SOFT/DITHER、
                // _USE_CLUSTER、VISABLE_LIGHT_COUNT 等，都属于 DefferCamera 的 keyword 空间），
                // 而无材质 Blit 内部用的是 Hidden/Internal-Loading（只有 4 个 stereo keyword），
                // 两者空间对不上，Unity 会做 LocalKeywordState::Union 并断言失败。
                // 改成走管线自己的 material + DrawProcedural：keyword 空间一致，不再冲突，
                // 行为也与 Blit 等价（全屏拷贝 _CopyColor）。
                Draw(ShaderProperty.ColorAttachmentId, ShaderProperty.CameraColorTexId,
                    (int)DefferRenderPass._CopyColor);
            }

            if (RenderSSR)
            {
                buffer.SetRenderTarget(
                    gBuffers,
                    DefferProperty.GBufferDepthId);

                buffer.SetRenderTarget(
                    ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

                spaceReflect.BeginRender(buffer, width, height, camera, defferSetting.ssrSetting);
            }


            ExecuteBuffer();
        }

        /// <summary>
        /// 绘制最终纹理，也就是根据SSS和GBuffer数据渲染出来的纹理
        /// </summary>
        private void DrawGBufferFinal(int targetTexId)
        {
            //Draw(0, targetTexId, (int)DefferRenderPass._GBufferDraw);
            //注意：不能使用 (BuiltinRenderTextureType)0（None）作为 Blit 源，
            //None 是无法解析的“空”标识，会导致 "temporary render texture not found (Blit source)"，
            //且顶点着色器需要 _SourceTexture_TexelSize 判断 UV 翻转，必须绑定一个有效源纹理
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, targetTexId, material, (int)DefferRenderPass._GBufferDraw);
            ExecuteBuffer();
        }

        /// <summary>   /// 将最终颜色渲染到摄像机   /// </summary>
        void DrawFinal()
        {
            buffer.SetGlobalTexture(ShaderProperty.SourceTextureId, 
                ShaderProperty.ColorAttachmentId);

            RenderTargetIdentifier target = camera.targetTexture == null ?
                (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget
                : camera.targetTexture;

#if UNITY_EDITOR

            // ===== pass 选择：Debug Mode 与法线诊断共用一份，两个分支都要走 =====
            // 【坑】之前 Scene 视图分支被硬编码成 _CopyColor（修 keyword 空间不兼容时顺手写死的），
            // 结果管线资产上的 Debug Mode 在 Scene 视图里【完全没反应】——而 Scene 视图正是
            // 平时看效果的主要窗口。现在 Game / Scene 两条分支共用同一个 pass，行为一致。
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

            if(camera.cameraType != CameraType.Game)
            {
                // 同上方 CopyColor 处的说明：无材质 Blit 会因 keyword 空间不兼容而断言失败，
                // 这里同样改走管线 material + DrawProcedural。
                Draw(ShaderProperty.ColorAttachmentId, target, pass);
            }
            else
            {
                buffer.SetRenderTarget(target, RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.DontCare);
                buffer.SetViewport(camera.pixelRect);

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


        /// <summary>	/// 清除使用过的数据，因为纹理图大多数都是在内存中的，因此需要我们手动释放	/// </summary>
        void Cleanup()
        {
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            buffer.ReleaseTemporaryRT(ShaderProperty.ColorAttachmentId);
            buffer.ReleaseTemporaryRT(DefferProperty.GBufferDepthId);

            if(renderSetting.CopyDepth)
                buffer.ReleaseTemporaryRT(ShaderProperty.CameraDepthTexId);
            if (renderSetting.CopyColor)
                buffer.ReleaseTemporaryRT(ShaderProperty.CameraColorTexId);

            for (int i = 0; i < DefferProperty.GBufferIds.Length; i++)
            {
                buffer.ReleaseTemporaryRT(DefferProperty.GBufferIds[i]);
            }

            lighting.Cleanup();		//灯光数据清除

        }

        /// <summary>        /// 提交方法，将所有命令上传        /// </summary>
        void Submit()
        {
            Profiler.EndSample();
            ExecuteBuffer();
            context.Submit();
        }

    }
}