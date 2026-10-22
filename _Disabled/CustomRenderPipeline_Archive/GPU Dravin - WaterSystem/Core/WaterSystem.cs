using CustomRenderPipline.DefferPipeline;
using CustomRenderPipline;
using UnityEngine;
using UnityEngine.Rendering;
using static KWS.KWS_ShaderConstants;
using UnityEditor;

namespace KWS
{

    public enum RefractionModeEnum
    {
        Simple,
        PhysicalAproximationIOR
    }

    [ExecuteAlways]
    public partial class WaterSystem : MonoBehaviour
    {
        [SerializeField] public WaterSystemSetting Settings;

        public static bool IsSinglePassStereoEnabled;
        public Texture skybox;
        internal SharedData SharedData = new SharedData();
        internal FFT_GPU FFTComponentLod0 = new FFT_GPU();
        internal FFT_GPU FFTComponentLod1 = new FFT_GPU();
        internal FFT_GPU FFTComponentLod2 = new FFT_GPU();
        WaterCaustic caustic;
        WaterVolumetricLight volumetricLight;

        ScreenSpacePlaneReflection ssrp;
        MaskDepthNormal mdn;
        Underwater underwater = new Underwater();
        public Material boundsMat;

        [SerializeField]
        //TerrainBuilder planeBuilder;
        PlaneBase plane;
        private Material material;
        public Material WaterMat => material;
        //public Shader waterShader
        //public TerrainPlaneData terrainData;
        DrawNode drawNode;
        //Terrain terrain;

        internal Matrix4x4 CurrentVPMatrix;
        internal Matrix4x4[] CurrentVPMatrixStereo = new Matrix4x4[2];

        internal bool UseMultipleSimulations => Settings.WindSpeed > 1.2f;


        private void OnEnable()
        {
            drawNode = new DrawNode();
            drawNode.queue = 1000;              //水要最后渲染，设置为1000
            drawNode.setupEvent = Setup;
            drawNode.gBufferEvent = DrawGBuffer;
            //drawNode.opaqueEvent = DrawOpaque;
            drawNode.skyboxLater = DrawSkyboxLater;
            drawNode.Submit();

            //大小有问题
            plane = new PlaneBase(Settings.meshScale, true, Settings.WaterScale,
                transform.position, 1, Vector3.one * 3, false);

            InitializeComponents(); InitializeMat();
            IsSinglePassStereoEnabled = KWS_CoreUtils.IsSinglePassStereoEnabled();


            ssrp = new ScreenSpacePlaneReflection(this);
            mdn = new MaskDepthNormal();
            caustic = new WaterCaustic(this);
            volumetricLight = new WaterVolumetricLight(this);
            //先这么设置
            SharedData.WaterShaderPassID = 0;
        }

        private void OnDisable()
        {
            if (plane != null)
            {
                plane.Release();
                Release();
            }
            drawNode?.Remove();
            plane = null;
            ssrp?.Release();
            caustic?.Release();
            volumetricLight?.Release();
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        #region PLANE_BASE

        private void LateUpdate()
        {
            PlaneTool.Instance.UpdatePlaneIndirect(plane);
        }
#if UNITY_EDITOR

        private void OnValidate()
        {
            InitializeMat();
            ssrp?.UpdateSetting(this);
            underwater.UpdateSetting(Settings);
            caustic?.OnChange(Settings);
        }

#endif

        public void DrawOpaque(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
        }

        public void DrawSkyboxLater(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            if (Settings.UseCaustic)
            {
                caustic.ExecuteInstance(camera, buffer, this);
            }

            //渲染反射
            if (Settings.UseScreenSpaceReflection)
            {
                ssrp.Execute(camera, buffer, this);
            }

            if (Settings.UseVolumetricLight)
            {
                volumetricLight.Execute(camera, buffer, this);
            }

            UpdateMaterialParams(buffer);       //一切数据都搞定后再设置

            buffer.SetRenderTarget(
                ShaderProperty.ColorAttachmentId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                DefferProperty.GBufferDepthId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

#if UNITY_EDITOR
            if (plane.useBounds)
            {
                boundsMat.SetBuffer(PlaneConstant.BoundsList, plane._boundsBuffer);
                buffer.DrawMeshInstancedIndirect(plane.cubeMesh, 0, boundsMat, 1, plane.args);
            }
#endif

            DrawWaterMesh(buffer, 0);
            if (Settings.UseUnderwater && IsCameraUnderwater)
            {
                underwater.Execute(camera, buffer, Settings);
            }
        }

        public void DrawWaterMesh(CommandBuffer buffer, int pass)
        {
            //buffer.DrawMeshInstancedIndirect(planeBuilder.PatchMesh, 0, material, pass,
            //    planeBuilder.patchIndirectArgs);
            buffer.DrawMeshInstancedIndirect(plane.planeMesh, 0, material, pass, plane.args);
        }

        public void Setup(ScriptableRenderContext context, CommandBuffer buffer, Camera camera, int width, int height, bool useHDR)
        {
            ExectuteWaterFFT(camera);
            SetGlobalCameraShaderParams(camera);        //设置水计算位置等需要的实时数据

            ssrp.InitializeRT(width, height, this, camera.cameraType == CameraType.SceneView);
            mdn.CheckTextures(width, height);
            volumetricLight.Check(width, height, this);
        }

        public void DrawGBuffer(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            //提前裁剪，方便之后使用
            PlaneTool.Instance.CullingNode(buffer, plane);

            material.SetBuffer(PlaneConstant.Nodes, plane._culledPatchBuffer);
            material.SetVectorArray(PlaneConstant.NodeSize, plane.NodeSize);
            material.SetTexture(PlaneConstant.LodMap, plane.lodMap);
            material.SetVector(PlaneConstant.BeginPosition, transform.position);
            material.SetInt(PlaneConstant.PointCount, (int)Settings.meshScale);

            mdn.Execute(buffer, this);
        }

        #endregion

        public void InitializeComponents()
        {
            FFTComponentLod0.Initialize(this, 0);
            FFTComponentLod1.Initialize(this, 1);
            FFTComponentLod2.Initialize(this, 2);

            SharedData.CubemapReflection = skybox;
        }

        public void InitializeMat()
        {
            material = new Material(Shader.Find(ShaderNames.WaterShaderName));

            material.SetKeyword(WaterKeywords.STEREO_INSTANCING_ON, IsSinglePassStereoEnabled);

            //FFT
            material.SetInt(ConstantWaterParams.KWS_UseMultipleSimulations, UseMultipleSimulations ? 1 : 0);
            
            //Refraction
            material.SetInt(ConstantWaterParams.KWS_UseRefractionIOR, Settings.RefractionMode == RefractionModeEnum.PhysicalAproximationIOR ? 1 : 0);
            material.SetInt(ConstantWaterParams.KWS_UseRefractionDispersion, Settings.UseRefractionDispersion ? 1 : 0);
            material.SetFloat(ConstantWaterParams.KWS_RefractionAproximatedDepth, Settings.RefractionAproximatedDepth);
            material.SetFloat(ConstantWaterParams.KWS_RefractionSimpleStrength, Settings.RefractionSimpleStrength);
            material.SetFloat(ConstantWaterParams.KWS_RefractionDispersionStrength, Settings.RefractionDispersionStrength * KWS_Settings.Water.MaxRefractionDispersion);

            //reflect
            material.SetTexture(ReflectionsID.KWS_CubemapReflectionRT, skybox);

            //under water---water color
            material.SetFloat(ConstantWaterParams.KW_WindSpeed, Settings.WindSpeed);
            material.SetFloat(ConstantWaterParams.KW_Transparent, Settings.Transparent);
            material.SetFloat(ConstantWaterParams.KW_Turbidity, Settings.Turbidity);
            material.SetColor(ConstantWaterParams.KW_WaterColor, Settings.WaterColor);
            material.SetColor(ConstantWaterParams.KW_TurbidityColor, Settings.TurbidityColor);

            //只有使用了UnderWater之后才会使用这个
            //Shader.SetGlobalTexture(MaskPassID.KW_WaterDepth_ID, _waterDepthRT.rt);
            //Shader.SetGlobalTexture(MaskPassID.KWS_WaterTexID, _waterIdRT.rt);
            Shader.SetGlobalTexture(MaskPassID.KW_WaterMaskScatterNormals_ID, Texture2D.blackTexture);
            //Shader.SetGlobalVector(MaskPassID.KWS_WaterMask_RTHandleScale, _waterMaskRT.rtHandleProperties.rtHandleScale);
            Shader.SetGlobalVector(CausticID.KW_CausticLodSettings, KWS_Settings.Caustic.LodSettings);


            SetAmbientLightToShaders();     //设置环境光
        }

        void ExectuteWaterFFT(Camera camera)
        {
            RenderFFT();        //先准备波纹贴图
            if (Settings.UseUnderwater)
            {
                IsCameraUnderwater = IsUnderwaterVisible(camera, WorldSpaceBounds);
            }

        }

        void Release()
        {
            FFTComponentLod0.Release();
            FFTComponentLod1.Release();
            FFTComponentLod2.Release();
            SharedData.WaterShaderPassID = 0;
            SharedData.Release();
            Resources.UnloadUnusedAssets();
        }

        void RenderFFT()
        {
            var time = KW_Extensions.TotalTime();

            time *= Settings.TimeScale;
            //time = 0.001f;
            var windTurbulence = Mathf.Lerp(0.05f, 0.5f, Settings.WindTurbulence);
            int fftSize = (int)Settings.FFT_SimulationSize;
            var timeScaleRelativeToFFTSize = (Mathf.RoundToInt(Mathf.Log(fftSize, 2)) - 5) / 4.0f;

            float lod0_Time = Mathf.Lerp(time, time, Settings.WindTurbulence);
            lod0_Time = Mathf.Lerp(lod0_Time, lod0_Time * 0.7f, timeScaleRelativeToFFTSize);

            FFTComponentLod0.ComputeFFT(windTurbulence, Mathf.Clamp(Settings.WindSpeed, 0, 2), Settings.WindRotation * Mathf.Deg2Rad, lod0_Time * 1.25f);

            if (UseMultipleSimulations)
            {
                FFTComponentLod1.ComputeFFT(windTurbulence, Mathf.Clamp(Settings.WindSpeed, 0, 6), Settings.WindRotation * Mathf.Deg2Rad, time * 0.9f);
                FFTComponentLod2.ComputeFFT(windTurbulence, Mathf.Clamp(Settings.WindSpeed, 0, 40), Settings.WindRotation * Mathf.Deg2Rad, time * 0.4f);
            }
        }

        private void UpdateMaterialParams(CommandBuffer cmd)
        {
            //var settings = WaterInstance.Settings;
            //var sharedData = WaterInstance.SharedData;

            //cmd.SetGlobalVector(DynamicWaterParams.KW_WaterPosition, WaterInstance.WaterRelativeWorldPosition);
            //cmd.SetGlobalFloat(DynamicWaterParams.KW_Time, WaterInstance.UseNetworkTime ? WaterInstance.NetworkTime : KW_Extensions.TotalTime());
            //cmd.SetGlobalInt(DynamicWaterParams.KWS_WaterPassID, sharedData.WaterShaderPassID);
            //cmd.SetGlobalInt(DynamicWaterParams.KWS_UnderwaterVisible, WaterInstance.IsCameraUnderwater ? 1 : 0);

            SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.FFT);
            //SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.CubemapReflection);
            cmd.SetGlobalInt(DynamicWaterParams.KWS_WaterPassID, SharedData.WaterShaderPassID);

            if (Settings.UseScreenSpaceReflection) SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.SSR);
            if (Settings.UseVolumetricLight)
            {
                cmd.EnableShaderKeyword(WaterKeywords.USE_VOLUMETRIC_LIGHT);
                SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.VolumetricLighting);
            }
            else cmd.DisableShaderKeyword(WaterKeywords.USE_VOLUMETRIC_LIGHT);
            //if (settings.UsePlanarReflection) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.PlanarReflection);
            //if (settings.UseFlowMap) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Flowing);
            //if (settings.UseFluidsSimulation) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.FluidsSimulation);
            //if (settings.UseDynamicWaves) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.DynamicWaves);
            //if (settings.UseShorelineRendering) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Shoreline);
            //if (settings.UseVolumetricLight) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.VolumetricLighting);
            //if (settings.UseCausticEffect) WaterInstance.SharedData.UpdateShaderParams(cmd, SharedData.PassEnum.Caustic);


        }


    }
}