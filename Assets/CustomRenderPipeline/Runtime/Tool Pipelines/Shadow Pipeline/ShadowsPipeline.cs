using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    /// <summary>/// ȹߣݴȾӰֻȾӰ /// </summary>
    public class ShadowsPipeline : RenderPipelineBase
    {

        static int 
            //ShadowMapId = Shader.PropertyToID("_ShadowPipelineShadowMap"),//ùȾĿ
            ShadowPancakingId = Shader.PropertyToID("_ShadowPancaking");

        public static ShaderTagId
            //CustomLitTag = new ShaderTagId("CustomLit"),
            ShadowTag = new ShaderTagId("ShadowCaster");


        RenderTexture shadowTarget;
        Light light;

        public override void Dispose()
        {
            if (shadowTarget != null)
            {
                //先解除相机对旧目标的绑定再释放，否则 Unity 会警告：
                //"Releasing render texture that is set as Camera.targetTexture!"
                if (camera != null)
                    camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(shadowTarget);
                //置空，避免重复释放；nodes 为空时每帧的 Dispose 调用也会变成空操作
                shadowTarget = null;
            }
        }
        public void Render(ScriptableRenderContext context, Camera camera, IShadowPipeline shadow)
        {
            this.camera = camera;
            this.context = context;
            PrepareBuffer();

            
            int size;
            Bounds bounds;
            light = shadow.SetUpCamera(out size, out bounds);

            //SetupCameraData(bounds);

            if (light == null) return;

            //׼޳
            if (!Cull())
            {   //޳׼
                return;     //׼ʧܾ˳
            }
            buffer.BeginSample(SampleName);


            Matrix4x4[] vps;
            RenderShadow(size, bounds, out vps);


            shadow.BeforeEnd(shadowTarget, context, buffer, light, vps);
            buffer.EndSample(SampleName);
            ExecuteBuffer();
            context.Submit();
        }

        /// <summary>	/// ִݽӰ޳ҪĲֽ޳	/// </summary>
        /// <param name="maxShadowDistance">޳</param>
        /// <returns>Ƿɹ޳</returns>
        bool Cull()
        {

            //ִ޳п޳ʧܣΪҪ
            if (camera.TryGetCullingParameters(out ScriptableCullingParameters p))
            {
                //Ӱ
                p.shadowDistance = 0;
                //ֵü
                cullingResults = context.Cull(ref p);
                return true;
            }

            return false;
        }

        Vector4 atlasSizes;

        void RenderShadow(int mapSize, Bounds bounds, out Matrix4x4[] vps)
        {
            Matrix4x4 vp;
            switch (light.type)
            {           //жϵƹͣжӦĳʼ
                case LightType.Directional:
                    SetupDirectionalLight(light, mapSize, bounds);
                    vp = RenderShadows(new Vector2(mapSize, mapSize));
                    vps = new Matrix4x4[] { vp };
                    return;

                case LightType.Point:
                    Matrix4x4 project = SetupPointLight(mapSize, bounds);

                    vps = new Matrix4x4[6];
                    for (int i = 0; i < 6; i++)
                    {
                        SetPointCamera(i, mapSize);
                        //ͶӰɫʱƫ
                        vps[i] = RenderPointsShadows(project);
                    }

                    return;
                case LightType.Spot:
                    SetupSpotLight(light, mapSize, bounds);
                    vp = RenderShadows(new Vector2(mapSize, mapSize));
                    vps = new Matrix4x4[] { vp };
                    return;
            }
            vps = new Matrix4x4[] { Matrix4x4.identity };


        }

        Matrix4x4 RenderShadows(Vector2 mapSize)
        {
            //øģʽĿǰȾͨ壬һʽ
            sortingSettings = new SortingSettings(camera)
            {
                criteria = SortingCriteria.CommonOpaque
            };

            //һȾֻGBufferGBufferȾ͸
            drawingSettings = new DrawingSettings(
                ShadowTag, sortingSettings
            )
            {
                enableDynamicBatching = true,
                enableInstancing = true,
            };
            filteringSettings = new FilteringSettings(
                RenderQueueRange.all, renderingLayerMask : (uint)light.renderingLayerMask);

            //Matrix4x4 viewMatrix = camera.transform.worldToLocalMatrix;
            Matrix4x4 viewMatrix = camera.worldToCameraMatrix;
            //Debug.Log(viewMatrix);
            Matrix4x4 projectionMatrix = camera.projectionMatrix;

            buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            buffer.SetGlobalDepthBias(0f, light.shadowBias);
            buffer.SetGlobalFloat(ShadowPancakingId, 1f);

            buffer.SetInvertCulling(true);
            ExecuteBuffer();
            //Ⱦִз
            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );


            //buffer.SetInvertCulling(false);
            //ExecuteBuffer();
            ////Ⱦִз
            //context.DrawRenderers(
            //    cullingResults, ref drawingSettings, ref filteringSettings
            //);

            buffer.SetInvertCulling(false);
            buffer.SetGlobalDepthBias(0f, 0f);
            buffer.SetGlobalFloat(ShadowPancakingId, 0f);
            ExecuteBuffer();
            return projectionMatrix * viewMatrix;
        }

        Matrix4x4 RenderPointsShadows(Matrix4x4 project)
        {
            Matrix4x4 viewMatrix = camera.worldToCameraMatrix;

            buffer.SetViewProjectionMatrices(viewMatrix, project);
            buffer.SetGlobalDepthBias(0f, light.shadowBias);
            ExecuteBuffer();
            //Ⱦִз
            context.DrawRenderers(
                cullingResults, ref drawingSettings, ref filteringSettings
            );

            buffer.SetGlobalDepthBias(0f, 0f);
            ExecuteBuffer();

            return project * viewMatrix;
        }


        void SetDirectionCamera(Bounds bounds)
        {
            Vector3[] poss = GetBoundsPosition(bounds);

            camera.transform.SetPositionAndRotation(bounds.center,
                light.transform.rotation);
            camera.orthographic = true;

            float orthSize = 0, near = float.MaxValue, far = float.MinValue;
            for (int i = 0; i < poss.Length; i++)
            {
                Vector3 camPos = camera.transform.InverseTransformPoint(poss[i]);
                orthSize = Mathf.Max(
                    Mathf.Max(Mathf.Abs(camPos.x), Mathf.Abs(camPos.y)),
                    orthSize);
                near = Mathf.Min(near, camPos.z);
                far = Mathf.Max(far, camPos.z);
            }
            camera.orthographicSize = orthSize + 1;
            camera.nearClipPlane = near;
            camera.farClipPlane = far;
            camera.ResetProjectionMatrix();
        }

        void SetPointCameraRange(Bounds bounds)
        {
            Vector3[] poss = GetBoundsPosition(bounds);

            float orthSize = 0, near = float.MaxValue, far = float.MinValue;
            for (int i = 0; i < poss.Length; i++)
            {
                Vector3 camPos = camera.transform.InverseTransformPoint(poss[i]);
                orthSize = Mathf.Max(
                    Mathf.Max(Mathf.Abs(camPos.x), Mathf.Abs(camPos.y)),
                    orthSize);
                near = Mathf.Min(near, camPos.z);
                far = Mathf.Max(far, camPos.z);
            }
            camera.orthographic = true;
            camera.orthographicSize = orthSize + 1;
            camera.nearClipPlane = near;
            camera.farClipPlane = far;
            camera.ResetProjectionMatrix();
        }


        void SetSpotCamera(Bounds bounds)
        {
            Vector3[] poss = GetBoundsPosition(bounds);

            camera.transform.SetPositionAndRotation(light.transform.position,
                light.transform.rotation);
            camera.orthographic = false;

            float orthSize = 0, far = float.MinValue;
            for (int i = 0; i < poss.Length; i++)
            {
                Vector3 camPos = camera.transform.InverseTransformPoint(poss[i]);
                orthSize = Mathf.Max(
                    Mathf.Max(Mathf.Abs(camPos.x), Mathf.Abs(camPos.y)),
                    orthSize);
                far = Mathf.Max(far, camPos.z);
            }
            camera.nearClipPlane = light.shadowNearPlane;
            camera.farClipPlane = Mathf.Min(far, light.range);
            camera.fieldOfView = light.spotAngle;

            camera.ResetProjectionMatrix();
        }

        readonly static Vector3[] PointLightDirections = new
            Vector3[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };


        void SetTileViewport(int index, float tileSize)
        {
            Vector2 offset = new Vector2(index % 3, index / 3);
            Rect rect = new Rect(
                offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
            );
            //ȾͼΧ
            buffer.SetViewport(rect);
        }

        /// <summary>  
        /// õԴڸôεĵƹͶӰΧͬʱȾΧڸ÷Χ
        /// </summary>
        /// <param name="bounds">ȾΧ</param>
        /// <param name="index">ڼȾ</param>
        /// <param name="mapSize">ÿһС</param>
        void SetPointCamera(int index, int mapSize)
        {
            camera.transform.forward = PointLightDirections[index];

            //float orthSize = 0, far = float.MinValue;
            //for (int i = 0; i < poss.Length; i++)
            //{
            //    Vector3 camPos = camera.transform.InverseTransformPoint(poss[i]);
            //    orthSize = Mathf.Max(
            //        Mathf.Max(Mathf.Abs(camPos.x), Mathf.Abs(camPos.y)),
            //        orthSize);
            //    far = Mathf.Max(far, camPos.z);
            //}
            //camera.nearClipPlane = light.shadowNearPlane;
            //camera.farClipPlane = Mathf.Min(far, light.range);

            SetTileViewport(index, mapSize);
        }

        Vector3[] GetBoundsPosition(Bounds bounds)
        {
            Vector3[] vector3s = new Vector3[8];
            Vector3 size = bounds.extents;
            vector3s[0] = bounds.center + new Vector3(-size.x, -size.y, -size.z);
            vector3s[1] = bounds.center + new Vector3(size.x, -size.y, -size.z);
            vector3s[2] = bounds.center + new Vector3(-size.x, size.y, -size.z);
            vector3s[3] = bounds.center + new Vector3(-size.x, -size.y, size.z);

            vector3s[4] = bounds.center + new Vector3(size.x, size.y, -size.z);
            vector3s[5] = bounds.center + new Vector3(size.x, -size.y, size.z);
            vector3s[6] = bounds.center + new Vector3(size.x, -size.y, size.z);
            vector3s[7] = bounds.center + new Vector3(size.x, size.y, size.z);
            return vector3s;
        }

        /// <summary>
        /// עֱӹݣͶӰȣ֮󴫵ݸGPU
        /// </summary>
        /// <param name="light">ƹ</param>
        void SetupDirectionalLight(Light light, int mapSize, Bounds bounds)
        {

            //濪ʼʼȾ
            if (shadowTarget != null)
            {
                //先解除相机对旧目标的绑定再释放，否则 Unity 会警告：
                //"Releasing render texture that is set as Camera.targetTexture!"
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(shadowTarget);
            }
            shadowTarget = RenderTexture.GetTemporary(mapSize, mapSize, 32,
                RenderTextureFormat.Shadowmap, RenderTextureReadWrite.Default);
            shadowTarget.filterMode = FilterMode.Bilinear;
            camera.targetTexture = shadowTarget;
            SetDirectionCamera(bounds);
            buffer.SetRenderTarget(
                shadowTarget,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
            );
            buffer.ClearRenderTarget(true, false, Color.clear);

        }

        /// <summary>
        /// עֱӹݣ洢У֮󴫵ݸGPU
        /// </summary>
        /// <param name="light">ƹ</param>
        void SetupSpotLight(Light light, int mapSize, Bounds bounds)
        {
            //濪ʼʼȾ
            if (shadowTarget != null)
            {
                //先解除相机对旧目标的绑定再释放，否则 Unity 会警告：
                //"Releasing render texture that is set as Camera.targetTexture!"
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(shadowTarget);
            }
            shadowTarget = RenderTexture.GetTemporary(mapSize, mapSize, 32,
                RenderTextureFormat.Shadowmap, RenderTextureReadWrite.Default);
            shadowTarget.filterMode = FilterMode.Bilinear;
            shadowTarget.wrapMode = TextureWrapMode.Clamp;

            camera.targetTexture = shadowTarget;
            SetSpotCamera(bounds);
            buffer.SetRenderTarget(
                shadowTarget,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
            );
            buffer.ClearRenderTarget(true, false, Color.clear);

        }

        Rect defaultRect = new Rect(0, 0, 1, 1);

        Matrix4x4 SetupPointLight(int mapSize, Bounds bounds)
        {

            //
            if (shadowTarget != null)
            {
                //先解除相机对旧目标的绑定再释放，否则 Unity 会警告：
                //"Releasing render texture that is set as Camera.targetTexture!"
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(shadowTarget);
            }
            shadowTarget = RenderTexture.GetTemporary(mapSize * 3, mapSize * 2, 32,
                RenderTextureFormat.Shadowmap, RenderTextureReadWrite.Default);
            //RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            shadowTarget.filterMode = FilterMode.Bilinear;
            camera.targetTexture = shadowTarget;
            buffer.SetRenderTarget(
                shadowTarget,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
            );
            buffer.ClearRenderTarget(true, false, Color.clear);

            //position
            camera.transform.position = light.transform.position;
            camera.orthographic = false;

            //fov
            float texelSize = 2f / mapSize;
            float filterSize = texelSize * 4f;
            float bias = light.shadowNormalBias * filterSize * 1.4142136f;
            float fovBias =
                Mathf.Atan(1f + bias + filterSize) * Mathf.Rad2Deg * 2f;
            camera.fieldOfView = fovBias;

            //far-near
            camera.farClipPlane = light.range;
            camera.nearClipPlane = light.shadowNearPlane;

            //ӦͶӰ㣬֤ӰȾʱܰӦȾȷ
            camera.rect = new Rect(0, 0, 1.0f / 3, 0.5f);
            camera.ResetProjectionMatrix();
            Matrix4x4 projectMatrix = camera.projectionMatrix;

            //ܹ
            SetPointCameraRange(bounds);
            camera.rect = defaultRect;      //RectͶӰȷûҪ

            //ȷҪȾȫ
            sortingSettings = new SortingSettings(camera)
            {
                criteria = SortingCriteria.CommonOpaque
            };
            drawingSettings = new DrawingSettings(
                ShadowTag, sortingSettings
            )
            {
                enableDynamicBatching = true,
                enableInstancing = true,
            };
            filteringSettings = new FilteringSettings(
                RenderQueueRange.all, light.renderingLayerMask);

            return projectMatrix;
        }

    }

    /// <summary>/// ȹעҪȹִеΪ /// </summary>
    public class ShadowPipelineRegister : RenderAsset
    {
        GameObject drawCamera;
        ShadowsPipeline depthPipeline;

        private ShadowPipelineRegister()
        {
            //注意：ScriptableObject 的构造函数里不能创建 GameObject，
            //Unity 会抛 Internal_CreateGameObject 异常，且对象停留在半初始化状态
            //（drawCamera/depthPipeline 为 null），后续 Enroll 必然空引用。
            //GameObject 创建必须挪到 OnEnable（Unity 对 CreateInstance 出来的
            //ScriptableObject 会调用 OnEnable）
            nodes = new Common.PoolingList<IShadowPipeline>();
        }

        private void OnEnable()
        {
            InitDrawCamera();
        }

        /// <summary>
        /// 创建深度摄像机和阴影管线。幂等：OnEnable 与 Instance 兜底都会调用，
        /// 防止某些路径下 OnEnable 未触发导致 drawCamera 为 null
        /// </summary>
        private void InitDrawCamera()
        {
            if (drawCamera != null) return;
            drawCamera = new GameObject("DepthPipelineCamera");
            var piplineCam = drawCamera.AddComponent<PipelineCamera>();
            piplineCam.cameraRenderAsset = this;

            Camera camera = drawCamera.AddComponent<Camera>();

            DontDestroyOnLoad(piplineCam);
            drawCamera.gameObject.SetActive(false); //默认关闭，需要渲染时再打开

            depthPipeline = new ShadowsPipeline();
        }

        private static ShadowPipelineRegister instance;

        public static ShadowPipelineRegister Instance
        {
            get
            {
                if(instance == null)
                    //RenderAsset 继承自 ScriptableObject，必须用 CreateInstance 创建，
                    //直接用 new 会触发 "must be instantiated using the ScriptableObject.CreateInstance" 警告
                    instance = ScriptableObject.CreateInstance<ShadowPipelineRegister>();
                //兜底补初始化：万一当前 Unity 版本对运行时创建的 ScriptableObject
                //没有调用 OnEnable，这里保证 drawCamera 一定就绪，避免 Enroll 空引用
                instance.InitDrawCamera();
                return instance;
            }
        }

        /// <summary>  /// нڵ   /// </summary>
        private Common.PoolingList<IShadowPipeline> nodes;

        /// <summary>   /// עýڵ㣬뵽    /// </summary>
        /// <param name="node"></param>
        public void Enroll(IShadowPipeline node)
        {
            if (nodes.Have(node))
                return;
            nodes.Add(node);
            if (drawCamera != null)
                drawCamera.SetActive(true);
        }

        public override void Render(ScriptableRenderContext context, Camera camera, int renderLayer)
        {
            if(nodes.Count == 0)
            {
                if (drawCamera != null)
                    drawCamera.SetActive(false);
                depthPipeline?.Dispose();
                return;
            }
            var node = nodes[0];
            depthPipeline?.Render(context, camera, node);
            //ִнƳһڵ
            if(node.IsEnding()) nodes.RemoveAt(0);
        }

        public override void Dispose()
        {
            if (depthPipeline != null)
            {
                depthPipeline.Dispose();
                depthPipeline = null;
            }
        }
    }

    /// <summary> /// ȹߵĽڵĽӿ /// </summary>
    public interface IShadowPipeline
    {
        /// <summary> /// ȷӰͼСԼҪȾĵƹ  /// </summary>
        /// <param name="mapSize">ӰͼС</param>
        /// <returns>ҪȾĵƹ</returns>
        public Light SetUpCamera(out int mapSize, out Bounds bounds);

        /// <summary>  /// ͼȾΪ  /// </summary>
        public void BeforeEnd(RenderTexture depthRT, ScriptableRenderContext context, 
            CommandBuffer buffer, Light light, Matrix4x4[] vpMatrixs);

        /// <summary>   /// Ƿִϣл /// </summary>
        public bool IsEnding();

    }

}