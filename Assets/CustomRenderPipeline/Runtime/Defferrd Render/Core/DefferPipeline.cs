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

        /// <summary>「给相机补排除 layer」只警告一次（否则每帧刷屏）。</summary>
        static bool s_warnedCameraLayer;
        /// <summary>「给相机补 BRG 批次专用 layer」只警告一次。</summary>
        static bool s_warnedBRGCullLayer;

        int width, height;
        /// <summary> /// 存储GBuffer的所有Id  /// </summary>
        RenderTargetIdentifier[] gBuffers;

        StochasticScreenSpaceReflection spaceReflect = new StochasticScreenSpaceReflection();
        AmbientOcclusion ambientOcclusion = new AmbientOcclusion();
        ClusterCore clusterCore = new ClusterCore();

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

            // ===== 「BRG 只画 GBuffer、阴影交给 MeshRenderer」模式的开关 =====
            // BRG 接管阴影后，阴影 pass 从 0.96ms 涨到 4.8ms（Unity 的 SRP Batcher 按材质合批，
            // 1219 个投射物 → 4 个 SRP Batch；BRG 只能按 (mesh,材质,submesh) 合批 → 134 条命令）。
            // 所以让 BRG 放弃投影，MeshRenderer 保留原材质画阴影。
            // 前提：相机的 cullingMask 必须包含排除用的 layer（否则物体直接消失）。
            // ⚠️ 必须在本帧 Cull()【之前】决定 —— 剔除发生在 cull 阶段，layer 换了才进得了投射物集合。
            int exclLayer = -1;
            string exclReason = null;
            var assemble = ClusterControl.TryGetAssembleAsset();   // 安全取，不会触发资产创建
            if (assemble == null) exclReason = "assemble 资产未加载";
            else if (!assemble.useBRG) exclReason = "useBRG = 0";
            else
            {
                int L = assemble.brgGBufferExcludeLayer;
                if (L < 0 || L >= 32)
                    exclReason = $"brgGBufferExcludeLayer = {L}（需 0..31；-1 表示关闭该模式）";
                else
                {
                    // 相机看不到该 layer ⇒ 物体直接消失（连阴影也没）。这里是"排除法"最容易踩的一脚：
                    // 相机的 Culling Mask 若被手工改过（不是 Everything），整个模式会静默失效，
                    // 表现就是"阴影还是 4ms、控制台什么也没有"。所以直接补进去 + 报一次警告。
                    if ((camera.cullingMask & (1 << L)) == 0)
                    {
                        camera.cullingMask |= 1 << L;
                        if (!s_warnedCameraLayer)
                        {
                            s_warnedCameraLayer = true;
                            Debug.LogWarning($"[RVT/BRG] 相机 \"{camera.name}\" 的 Culling Mask 不含 layer {L}，" +
                                "已自动补上（仅本次运行有效）。要永久生效请手动打开该 layer。");
                        }
                    }
                    exclLayer = L;
                }
            }
            if (exclLayer != ClusterProverty.BRGShadowExcludeLayer)
            {
                ClusterProverty.BRGShadowExcludeLayer = exclLayer;
                BatchRenderManager.Instance?.ApplyShadowOnlyMode(exclLayer);
                // 这条日志是排查「阴影还是 4ms」的第一落点：
                // 显示「阴影归属 = BRG 批次」就说明模式没生效，括号里会写原因。
                Debug.Log(exclLayer >= 0
                    ? $"[RVT/BRG] 阴影归属 = MeshRenderer：BRG 物体已挪到 layer {exclLayer}" +
                      $"（实例数 {BatchRenderManager.ShadowOnlyInstanceCount}），" +
                      "BRG 批次不再投阴影（GBuffer 仍由 BRG 画）。"
                    : $"[RVT/BRG] 阴影归属 = BRG 批次（原因：{exclReason}）。");
            }

            // BRG 批次自己所在的 layer（BRGCullLayer）必须在【相机】的 cullingMask 里，
            // 否则连主画面都不画。灯光的 cullingMask 会在 Shadows.ReserveDirectionalShadows
            // 里被自动去掉这一位 ⇒ BRG 只进主画面、进不了阴影视图。
            {
                int cl = ClusterProverty.BRGCullLayer;
                if (cl >= 0 && cl < 32 && (camera.cullingMask & (1 << cl)) == 0)
                {
                    camera.cullingMask |= 1 << cl;
                    if (!s_warnedBRGCullLayer)
                    {
                        s_warnedBRGCullLayer = true;
                        Debug.LogWarning($"[RVT/BRG] 相机 \"{camera.name}\" 的 Culling Mask 已自动加上 " +
                            $"layer {cl}（BRG 批次专用层，仅本次运行有效）。");
                    }
                }
            }


            // 法线诊断开关：每帧按资产实时值下发，和 DrawFinal 里选 pass 用的是同一个值。
            // 放在这里（而不是 ClusterCore 的反馈帧里）是为了让它在「关掉 RVT / 暂停 / 非 Game 相机」
            // 这些反馈不跑的情况下也立即生效、立即失效——诊断只应是叠加，不能反过来锁死 Debug 核心。
            Shader.SetGlobalFloat(ClusterProverty.NormalDebugMode, ClusterProverty.NormalDebugModeValue);

            //执行单个摄像机的gbuffer前操作
            var beforeGBuffers = camera.GetCommandBuffers(CameraEvent.BeforeGBuffer);
            foreach (var command in beforeGBuffers)
            {
                context.ExecuteCommandBuffer(command);
            }

            PrepareBuffer();
            PrepareForSceneWindow();        //准备UI数据

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
                GPUDravinSystem.Instance.DrawFeedback(context, buffer, camera);

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
                )
                {
                    // 同 ClusterCore 的 feedback pass：排除 shadow-only 用的 layer，
                    // 否则那批 MeshRenderer（材质是 Unlit/ClusterPBR，带 Feedback pass）会被重复画一遍。
                    layerMask = ClusterProverty.BRGShadowExcludeLayer >= 0
                        ? unchecked((int)~(1u << ClusterProverty.BRGShadowExcludeLayer))
                        : ~0,
                };
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
            //释放 BRG 管理器（BatchRendererGroup / 实例矩阵缓冲 / 注册的 batch）
            if (BatchRenderManager.Instance != null)
                BatchRenderManager.Instance.Dispose();
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
                //它是主线程上最贵的一段之一：内部会跑 Unity 的整个剔除管线，
                //并且【会触发 BRG 的 OnPerformCulling 回调】（那段在渲染工作线程上，
                //要切到 Render Thread 行才看得到，标记名 BRG.Cull）。
                Profiler.BeginSample("Cull.Scene");
                cullingResults = context.Cull(ref p);
                Profiler.EndSample();

                // 剔除对照：无论 BRG 开还是关，都用 Unity 内置 GeometryUtility 统计一遍
                // 场景 MeshRenderer 的视锥可见数，作为「相机到底看得见多少」的稳定基线，
                // 用来判断 BRG 的 1159/1159 究竟是「剔除写错」还是「本来就都看得见」。
                // 内部每 300 帧才真跑一次，不产生额外开销。
                CullDiagnostics.ReportSceneRenderers(camera);

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
                RenderQueueRange.opaque, renderingLayerMask: (uint)renderingLayerMask)
            {
                // 「阴影交给 MeshRenderer」模式：这些物体的 MeshRenderer 仍 enabled，
                // 它们只画阴影、不该画 GBuffer（否则与 BRG 批次双重绘制）⇒ 排除掉该 layer。
                // FilteringSettings.layerMask 是 int，这里按位当作 uint 用，需显式转换
                layerMask = unchecked((int)(
                    ClusterProverty.BRGShadowExcludeLayer >= 0
                        ? ~(1u << ClusterProverty.BRGShadowExcludeLayer)
                        : 0xffffffffu
                )),
            };

            //进行渲染的执行方法
            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );

            //=== BRG 迁移 Step1（附加式）：GBuffer MRT 仍绑定的此刻提交一次，
            //让 BRG 把 GBuffer pass 额外绘制进 GBuffer。原 DrawRenderers 已先画过，故为叠加，
            //画面兜底仍在；若 BRG 提交时机/裁剪有问题，仍可对比排查。===
            if (ClusterControl.Instance.ClusterAssembleAsset.useBRG
                && BatchRenderManager.Instance != null
                && BatchRenderManager.Instance.HasBatches)
            {
                BatchRenderManager.Instance.UpdateMatrices();
                //Frame Debugger 标记：BRG 的 draw command 在 context.Submit() 时才真正绘制，
                //用 C# Profiler 样本圈出独立标题 "BRG.Draw"，便于在 Profiler/Frame Debugger 里
                //确认 BRG 有没有画、画了什么。（注：原先写的 "RenderLoop.DrawBRG" 与 Unity 内部
                //同名 marker 冲突，且 CommandBuffer 样本会被 ExecuteBuffer 的 Clear 断开 → 报
                //"Non-matching Profiler.EndSample"，所以改成 C# Profiler 样本并改名。）
                //
                //【重要】这里量到的是【context.Submit 的 CPU 成本】，不是 BRG 绘制的 GPU 成本。
                //BRG 的 GPU 成本要去 Frame Debugger / RenderDoc 看 "Hybrid Batch Group" 条目。
                //另外这次 Submit 是【帧中提交】（整帧的最后一次 Submit 在 Submit() 里），
                //目的是让 BRG 的 GBuffer 绘制赶在光照 pass 之前落地，代价是多一次提交/次刷新。
                ExecuteBuffer();
                Profiler.BeginSample("BRG.Draw");
                context.Submit();
                Profiler.EndSample();
                ExecuteBuffer();
            }

            // 旧的「逐簇 DrawMesh」路径已移除：几何统一由上面的 BRG 绘制，
            // 纹理精度由 Tile-VT 保证，不再需要簇这一层。

            //渲染GPU驱动的标准PBR数据
            GPUDravinSystem.Instance.DrawGBuffer(context, buffer, camera);
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

            //绘制ComputeShader实现的物体
            GPUDravinSystem.Instance.DrawTransparent(context, buffer, camera);



            ExecuteBuffer();
        }

        /// <summary>        /// 确定GBuffer最后的颜色        /// </summary>
        void DrawGBufferFinal()
        {
            DrawGBufferFinal(ShaderProperty.ColorAttachmentId);

            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

            //放在这里，保证先渲染完默认模型后渲染天空盒
            GPUDravinSystem.Instance.DrawOpaque(context, buffer, camera);

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

            GPUDravinSystem.Instance.DrawSkyboxLater(context, buffer, camera);

            if (RenderSSR)
            {
                buffer.SetRenderTarget(
                    gBuffers,
                    DefferProperty.GBufferDepthId);


                //为了拓展需要反射效果，但是又不直接使用BRDF的渲染时执行的方法,
                GPUDravinSystem.Instance.DrawBeforeSSR(context, buffer, camera);
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
            // 法线诊断模式（Assemble.normalDebugMode）非 0 时优先级最高：
            // BRG 把诊断内容写进了 RT0(albedo) 通道，这里在 GBuffer 渲染之后的最终 blit
            // 直接输出它。这样只改 Assemble 上那一个字段就能看到诊断，
            // 不需要再去 RenderPipeline 资产把 Debug Mode 切成 Albedo。
            // 注意：NormalDebugModeValue 现在是【实时读资产】，0 就是 0——
            // 以前它是个只在 RVT 反馈帧里同步的静态缓存，关掉 RVT/暂停后就永久停在旧值，
            // 会把这里钉死在 _DebugAlbedo，表现为「Normal Debug 关掉了，管线 Debug Mode 还是不起作用」。
            if (ClusterProverty.NormalDebugModeValue > 0)
            {
                pass = (int)DefferRenderPass._DebugAlbedo;
            }
            else switch (defferSetting.debugMode)
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

            GPUDravinSystem.Instance.CleanUp(context, buffer);

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