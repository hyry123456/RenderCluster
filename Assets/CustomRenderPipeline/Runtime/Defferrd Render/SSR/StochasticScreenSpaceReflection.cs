using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace CustomRenderPipline.DefferPipeline
{
    public enum RenderResolution
    {
        Full = 1,
        Half = 2
    };

    public enum DebugPass
    {
        Combine = 9,
        SSRColor = 10
    };
    public enum TraceApprox
    {
        HiZTrace = 0,
        LinearTrace = 1
    };

    [System.Serializable]
    public class ScreenSpaceReflectionSetting
    {
        public bool useSSR;
        public RenderResolution RayCastingResolution = RenderResolution.Full;
        public TraceApprox TraceMethod = TraceApprox.HiZTrace;


        public Texture PreintegratedGF_LUT = null;
        public Texture BlueNoise_LUT = null;
        [Range(1, 9)]
        public int SpatioSampler = 9;
        [Range(0, 0.99f)]
        public float TemporalWeight = 0.98f;
        [Range(1, 5)]
        public float TemporalScale = 1.25f;

        [Range(0, 1)]
        public float BRDFBias = 0.7f;
        [Range(1, 4)]
        public int RayNums = 1;
        [Range(0.05f, 5)]
        public float Thickness = 0.1f;
        [Range(0, 0.5f)]
        public float ScreenFade = 0.1f;

        [Header("HiZ_Trace Property")]
        [Range(32, 512)]
        public int HiZ_RaySteps = 58;
        [Range(4, 10)]
        public int HiZ_MaxLevel = 10;
        [Range(0, 2)]
        public int HiZ_StartLevel = 1;
        [Range(0, 2)]
        public int HiZ_StopLevel = 0;

        [Header("Linear_Trace Property")]

        public bool Linear_TraceBehind = false;
        public bool Linear_TowardRay = true;
        public int Linear_RayDistance = 512;

        [Range(32, 512)]
        public int Linear_RaySteps = 256;

        [Range(5, 20)]
        public int Linear_StepSize = 10;

        [Header("DeBug Property")]

        public bool Denoise = true;
        public bool RunTimeDebugMod = true;
        public bool UseSeparateRender = true;   //分别渲染，减低压力

        public DebugPass DeBugPass = DebugPass.SSRColor;


    }
    //class SSR_TexturesNode
    //{
    //    public RenderTexture SSR_HierarchicalDepth_RT;
    //    public RenderTexture SSR_SceneColor_RT;
    //    public RenderTexture[] SSR_TraceMask_RT;
    //    public RenderTexture SSR_Spatial_RT;
    //    public RenderTexture SSR_TemporalPrev_RT;
    //    public RenderTexture SSR_TemporalCurr_RT;
    //    public RenderTexture SSR_CombineScene_RT;
    //    public RenderTargetIdentifier[] SSR_TraceMask_ID;

    //    public SSR_TexturesNode()
    //    {
    //        SSR_TraceMask_RT = new RenderTexture[2];
    //        SSR_TraceMask_ID = new RenderTargetIdentifier[2];
    //    }
    //}

    public class StochasticScreenSpaceReflection
    {
        private Vector2 RandomSampler = new Vector2(1, 1);
        private Vector2 CameraSize;
        private Matrix4x4 SSR_ProjectionMatrix;
        private Matrix4x4 SSR_ViewProjectionMatrix;
        private Matrix4x4 SSR_Prev_ViewProjectionMatrix;
        private Matrix4x4 SSR_WorldToCameraMatrix;
        private Matrix4x4 SSR_CameraToWorldMatrix;
        private Camera camera;

        ///////////////////////////////PASS/////////////////////////////////

        private static int RenderPass_HiZ_Depth = 0;
        private static int RenderPass_Linear2D_SingelSPP = 1;
        private static int RenderPass_HiZ3D_SingelSpp = 2;
        private static int RenderPass_Linear2D_MultiSPP = 3;
        private static int RenderPass_HiZ3D_MultiSpp = 4;
        private static int RenderPass_Spatiofilter_SingleSPP = 5;
        private static int RenderPass_Spatiofilter_MultiSPP = 6;
        private static int RenderPass_Temporalfilter_SingleSPP = 7;
        private static int RenderPass_Temporalfilter_MultiSpp = 8;
        ///////////////////////////////PASS/////////////////////////////////

        ScreenSpaceReflectionSetting ssrSetting;

        private Material StochasticScreenSpaceReflectionMaterial_ = null;
        private Material StochasticScreenSpaceReflectionMaterial
        {
            get
            {
                if (StochasticScreenSpaceReflectionMaterial_ == null) 
                    StochasticScreenSpaceReflectionMaterial_ = new Material(Shader.Find("Hidden/StochasticScreenSpaceReflection"));
                return StochasticScreenSpaceReflectionMaterial_;
            }
        }

        private RenderTexture[] SSR_TraceMask_RT = new RenderTexture[2]; private RenderTargetIdentifier[] SSR_TraceMask_ID = new RenderTargetIdentifier[2];
        private RenderTexture SSR_Spatial_RT, SSR_TemporalPrev_RT, SSR_TemporalCurr_RT, SSR_CombineScene_RT, SSR_HierarchicalDepth_RT, SSR_HierarchicalDepth_BackUp_RT, SSR_SceneColor_RT;

        public void BeginRender(CommandBuffer buffer, int width, int height,
            Camera camera, ScreenSpaceReflectionSetting ssrSetting)
        {
            this.ssrSetting = ssrSetting;
            this.camera = camera;
            RandomSampler = GenerateRandomOffset();
            SSR_UpdateVariable(width, height);
            buffer.BeginSample("StochasticScreenSpaceReflection");
            RenderScreenSpaceReflection(buffer);
            buffer.EndSample("StochasticScreenSpaceReflection");
        }

        private int m_SampleIndex = 0;
        private const int k_SampleCount = 64;
        private float GetHaltonValue(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;

            while (index > 0)
            {
                result += (index % radix) * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }
        private Vector2 GenerateRandomOffset()
        {
            var offset = new Vector2(GetHaltonValue(m_SampleIndex & 1023, 2),
                GetHaltonValue(m_SampleIndex & 1023, 3));
            if (m_SampleIndex++ >= k_SampleCount)
                m_SampleIndex = 0;
            return offset;
        }

        private void SSR_UpdateVariable(int width, int height)
        {
            Vector2 HalfCameraSize = new Vector2(width / 2, height / 2);
            Vector2 CurrentCameraSize = new Vector2(width, height);


            if (CameraSize != CurrentCameraSize)
            {
                CameraSize = CurrentCameraSize;

                ////////////SceneColor and HierarchicalDepth RT
                RenderTexture.ReleaseTemporary(SSR_HierarchicalDepth_RT);
                SSR_HierarchicalDepth_RT = RenderTexture.
                    GetTemporary(width, height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
                SSR_HierarchicalDepth_RT.filterMode = FilterMode.Point;
                SSR_HierarchicalDepth_RT.useMipMap = true;
                // 关键修复：本 RT 的 mip 链由 HiZ 循环手工构建（DrawProcedural + CopyTexture），
                // 绝不可让 Unity 再自动生成 mip。autoGenerateMips=true 会让 Unity 在该 RT 被采样时
                // 用 Hidden/Internal-Loading 做自动 mip 生成，而它与同命令缓冲里的 DefferCamera
                // （15 个 local keyword）关键字空间不兼容，触发 "incompatible keyword space" 断言崩溃。
                SSR_HierarchicalDepth_RT.autoGenerateMips = false;

                RenderTexture.ReleaseTemporary(SSR_HierarchicalDepth_BackUp_RT);
                SSR_HierarchicalDepth_BackUp_RT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
                SSR_HierarchicalDepth_BackUp_RT.filterMode = FilterMode.Point;
                SSR_HierarchicalDepth_BackUp_RT.useMipMap = true;
                SSR_HierarchicalDepth_BackUp_RT.autoGenerateMips = false;



                RenderTexture.ReleaseTemporary(SSR_SceneColor_RT);
                SSR_SceneColor_RT = RenderTexture.
                    GetTemporary(width, height, 0, RenderTextureFormat.DefaultHDR);

                /////////////RayMarching and RayMask RT
                RenderTexture.ReleaseTemporary(SSR_TraceMask_RT[0]);
                SSR_TraceMask_RT[0] = RenderTexture.
                    GetTemporary(width / (int)ssrSetting.RayCastingResolution,
                    height / (int)ssrSetting.RayCastingResolution, 0, RenderTextureFormat.ARGBHalf);
                SSR_TraceMask_RT[0].filterMode = FilterMode.Point;
                SSR_TraceMask_ID[0] = SSR_TraceMask_RT[0].colorBuffer;

                RenderTexture.ReleaseTemporary(SSR_TraceMask_RT[1]);
                SSR_TraceMask_RT[1] = RenderTexture.GetTemporary(
                    width / (int)ssrSetting.RayCastingResolution,
                    height / (int)ssrSetting.RayCastingResolution, 0, RenderTextureFormat.ARGBHalf);
                SSR_TraceMask_RT[1].filterMode = FilterMode.Point;
                SSR_TraceMask_ID[1] = SSR_TraceMask_RT[1].colorBuffer;

                ////////////Spatial RT
                RenderTexture.ReleaseTemporary(SSR_Spatial_RT);
                SSR_Spatial_RT = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.ARGBHalf);
                SSR_Spatial_RT.filterMode = FilterMode.Bilinear;

                ////////////Temporal RT
                RenderTexture.ReleaseTemporary(SSR_TemporalPrev_RT);
                SSR_TemporalPrev_RT = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.ARGBHalf);
                SSR_TemporalPrev_RT.filterMode = FilterMode.Bilinear;

                RenderTexture.ReleaseTemporary(SSR_TemporalCurr_RT);
                SSR_TemporalCurr_RT = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.ARGBHalf);
                SSR_TemporalCurr_RT.filterMode = FilterMode.Bilinear;

                ////////////Combine RT
                RenderTexture.ReleaseTemporary(SSR_CombineScene_RT);
                SSR_CombineScene_RT = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.DefaultHDR);
                SSR_CombineScene_RT.filterMode = FilterMode.Point;

                ////////////Update Uniform Variable
                SSR_UpdateUniformVariable();
            }

            ////////////Set Matrix
#if UNITY_EDITOR
            //运行时实时更新
            if (ssrSetting.RunTimeDebugMod)
            {
                SSR_UpdateUniformVariable();
            }
#endif

            StochasticScreenSpaceReflectionMaterial.SetVector(SSR_SC.SSR_Jitter_ID, new Vector4(CameraSize.x / 1024, CameraSize.y / 1024, RandomSampler.x, RandomSampler.y));
            SSR_WorldToCameraMatrix = camera.worldToCameraMatrix;
            SSR_CameraToWorldMatrix = SSR_WorldToCameraMatrix.inverse;
            SSR_ProjectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
            SSR_ViewProjectionMatrix = SSR_ProjectionMatrix * SSR_WorldToCameraMatrix;
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_ProjectionMatrix_ID, SSR_ProjectionMatrix);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_ViewProjectionMatrix_ID, SSR_ViewProjectionMatrix);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_InverseProjectionMatrix_ID, SSR_ProjectionMatrix.inverse);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_InverseViewProjectionMatrix_ID, SSR_ViewProjectionMatrix.inverse);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_WorldToCameraMatrix_ID, SSR_WorldToCameraMatrix);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_CameraToWorldMatrix_ID, SSR_CameraToWorldMatrix);
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_LastFrameViewProjectionMatrix_ID, SSR_Prev_ViewProjectionMatrix);

            Matrix4x4 warpToScreenSpaceMatrix = Matrix4x4.identity;
            warpToScreenSpaceMatrix.m00 = HalfCameraSize.x; warpToScreenSpaceMatrix.m03 = HalfCameraSize.x;
            warpToScreenSpaceMatrix.m11 = HalfCameraSize.y; warpToScreenSpaceMatrix.m13 = HalfCameraSize.y;

            Matrix4x4 SSR_ProjectToPixelMatrix = warpToScreenSpaceMatrix * SSR_ProjectionMatrix;
            StochasticScreenSpaceReflectionMaterial.SetMatrix(SSR_SC.SSR_ProjectToPixelMatrix_ID, SSR_ProjectToPixelMatrix);

            Vector4 SSR_ProjInfo = new Vector4
                    ((-2 / (CameraSize.x * SSR_ProjectionMatrix[0])),
                    (-2 / (CameraSize.y * SSR_ProjectionMatrix[5])),
                    ((1 - SSR_ProjectionMatrix[2]) / SSR_ProjectionMatrix[0]),
                    ((1 + SSR_ProjectionMatrix[6]) / SSR_ProjectionMatrix[5]));
            StochasticScreenSpaceReflectionMaterial.SetVector(SSR_SC.SSR_ProjInfo_ID, SSR_ProjInfo);

            Vector3 SSR_ClipInfo = (float.IsPositiveInfinity(camera.farClipPlane)) ?
                    new Vector3(camera.nearClipPlane, -1, 1) :
                    new Vector3(camera.nearClipPlane * camera.farClipPlane, camera.nearClipPlane - camera.farClipPlane, camera.farClipPlane);
            StochasticScreenSpaceReflectionMaterial.SetVector(SSR_SC.SSR_CameraClipInfo_ID, SSR_ClipInfo);
        }

        private void SSR_UpdateUniformVariable()
        {
            StochasticScreenSpaceReflectionMaterial.SetTexture(
                SSR_SC.SSR_PreintegratedGF_LUT_ID, ssrSetting.PreintegratedGF_LUT);
            StochasticScreenSpaceReflectionMaterial.SetTexture(
                SSR_SC.SSR_Noise_ID, ssrSetting.BlueNoise_LUT);
            StochasticScreenSpaceReflectionMaterial.SetVector(
                SSR_SC.SSR_ScreenSize_ID, CameraSize);
            StochasticScreenSpaceReflectionMaterial.SetVector(
                SSR_SC.SSR_RayCastSize_ID, CameraSize / (int)ssrSetting.RayCastingResolution);
            StochasticScreenSpaceReflectionMaterial.SetVector(
                SSR_SC.SSR_NoiseSize_ID, new Vector2(1024, 1024));
            StochasticScreenSpaceReflectionMaterial.SetFloat(
                SSR_SC.SSR_BRDFBias_ID, ssrSetting.BRDFBias);
            StochasticScreenSpaceReflectionMaterial.SetFloat(
                SSR_SC.SSR_ScreenFade_ID, ssrSetting.ScreenFade);
            StochasticScreenSpaceReflectionMaterial.SetFloat(
                SSR_SC.SSR_Thickness_ID, ssrSetting.Thickness);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_RayStepSize_ID, ssrSetting.Linear_StepSize);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_TraceDistance_ID, ssrSetting.Linear_RayDistance);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_NumSteps_Linear_ID, ssrSetting.Linear_RaySteps);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_NumSteps_HiZ_ID, ssrSetting.HiZ_RaySteps);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_NumRays_ID, ssrSetting.RayNums);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_BackwardsRay_ID, ssrSetting.Linear_TowardRay ? 1 : 0);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_CullBack_ID, ssrSetting.Linear_TowardRay ? 1 : 0);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_TraceBehind_ID, ssrSetting.Linear_TraceBehind ? 1 : 0);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_HiZ_MaxLevel_ID, ssrSetting.HiZ_MaxLevel);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_HiZ_StartLevel_ID, ssrSetting.HiZ_StartLevel);
            StochasticScreenSpaceReflectionMaterial.SetInt(
                SSR_SC.SSR_HiZ_StopLevel_ID, ssrSetting.HiZ_StopLevel);
            if (ssrSetting.Denoise)
            {
                StochasticScreenSpaceReflectionMaterial.SetInt(SSR_SC.SSR_NumResolver_ID, ssrSetting.SpatioSampler);
                StochasticScreenSpaceReflectionMaterial.SetFloat(SSR_SC.SSR_TemporalScale_ID, ssrSetting.TemporalScale);
                StochasticScreenSpaceReflectionMaterial.SetFloat(SSR_SC.SSR_TemporalWeight_ID, ssrSetting.TemporalWeight);
            }
            else
            {
                StochasticScreenSpaceReflectionMaterial.SetInt(SSR_SC.SSR_NumResolver_ID, 1);
                StochasticScreenSpaceReflectionMaterial.SetFloat(SSR_SC.SSR_TemporalScale_ID, 0);
                StochasticScreenSpaceReflectionMaterial.SetFloat(SSR_SC.SSR_TemporalWeight_ID, 0);
            }
        }

        int currentState = 0;

        void SeparateRender(CommandBuffer buffer)
        {
            switch (currentState)
            {
                case 0:
                    //拷贝延迟渲染的深度
                    buffer.Blit(DefferProperty.GBufferDepthId, SSR_HierarchicalDepth_RT);
                    for (int i = 1; i < ssrSetting.HiZ_MaxLevel; ++i)
                    {
                        buffer.SetGlobalInt(SSR_SC.SSR_HiZ_PrevDepthLevel_ID, i - 1);
                        buffer.SetRenderTarget(SSR_HierarchicalDepth_BackUp_RT, i);
                        //buffer.DrawMesh(GraphicsUtility.mesh, Matrix4x4.identity, StochasticScreenSpaceReflectionMaterial, 0, RenderPass_HiZ_Depth);
                        buffer.DrawProcedural(Matrix4x4.identity, StochasticScreenSpaceReflectionMaterial,
                            RenderPass_HiZ_Depth, MeshTopology.Triangles, 3);
                        buffer.CopyTexture(SSR_HierarchicalDepth_BackUp_RT, 0, i, SSR_HierarchicalDepth_RT, 0, i);
                    }
                    ////////Set SceneColorRT//////
                    buffer.SetGlobalTexture(SSR_SC.SSR_SceneColor_ID, SSR_SceneColor_RT);
                    buffer.CopyTexture(ShaderProperty.ColorAttachmentId, SSR_SceneColor_RT);
                    break;
                case 1:
                    buffer.SetGlobalTexture(SSR_SC.SSR_HierarchicalDepth_ID, SSR_HierarchicalDepth_RT);

                    ////////RayCasting//////
                    buffer.SetGlobalTexture(SSR_SC.SSR_Trace_ID, SSR_TraceMask_RT[0]);
                    buffer.SetGlobalTexture(SSR_SC.SSR_Mask_ID, SSR_TraceMask_RT[1]);
                    if (ssrSetting.TraceMethod == TraceApprox.HiZTrace)
                    {
                        //SSR_TraceMask_RT[0]是深度，随便传的，不需要
                        buffer.BlitMRT(SSR_TraceMask_ID, SSR_TraceMask_RT[0], StochasticScreenSpaceReflectionMaterial,
                            (ssrSetting.RayNums > 1) ? RenderPass_HiZ3D_MultiSpp : RenderPass_HiZ3D_SingelSpp);
                    }
                    else
                    {
                        buffer.BlitMRT(SSR_TraceMask_ID, SSR_TraceMask_RT[0], StochasticScreenSpaceReflectionMaterial,
                            (ssrSetting.RayNums > 1) ? RenderPass_Linear2D_MultiSPP : RenderPass_Linear2D_SingelSPP);
                    }
                    break;
                case 2:
                    ////////Spatial filter//////  
                    buffer.SetGlobalTexture(SSR_SC.SSR_Spatial_ID, SSR_Spatial_RT);
                    buffer.BlitSRT(SSR_Spatial_RT, StochasticScreenSpaceReflectionMaterial,
                        (ssrSetting.RayNums > 1) ? RenderPass_Spatiofilter_MultiSPP : RenderPass_Spatiofilter_SingleSPP);
                    buffer.CopyTexture(SSR_Spatial_RT, SSR_TemporalCurr_RT);

                    ////////Temporal filter//////
                    buffer.SetGlobalTexture(SSR_SC.SSR_TemporalPrev_ID, SSR_TemporalPrev_RT);
                    buffer.SetGlobalTexture(SSR_SC.SSR_TemporalCurr_ID, SSR_TemporalCurr_RT);
                    buffer.BlitSRT(SSR_TemporalCurr_RT, StochasticScreenSpaceReflectionMaterial,
                        (ssrSetting.RayNums > 1) ? RenderPass_Temporalfilter_MultiSpp : RenderPass_Temporalfilter_SingleSPP);
                    buffer.CopyTexture(SSR_TemporalCurr_RT, SSR_TemporalPrev_RT);
                    break;
            }
            currentState = (currentState + 1) % 3;
        }

        void CombineRender(CommandBuffer buffer)
        {
            //拷贝延迟渲染的深度
            buffer.Blit(DefferProperty.GBufferDepthId, SSR_HierarchicalDepth_RT);
            for (int i = 1; i < ssrSetting.HiZ_MaxLevel; ++i)
            {
                buffer.SetGlobalInt(SSR_SC.SSR_HiZ_PrevDepthLevel_ID, i - 1);
                buffer.SetRenderTarget(SSR_HierarchicalDepth_BackUp_RT, i);
                //buffer.DrawMesh(GraphicsUtility.mesh, Matrix4x4.identity, StochasticScreenSpaceReflectionMaterial, 0, RenderPass_HiZ_Depth);
                buffer.DrawProcedural(Matrix4x4.identity, StochasticScreenSpaceReflectionMaterial,
                    RenderPass_HiZ_Depth, MeshTopology.Triangles, 3);
                buffer.CopyTexture(SSR_HierarchicalDepth_BackUp_RT, 0, i, SSR_HierarchicalDepth_RT, 0, i);
            }
            ////////Set SceneColorRT//////
            buffer.SetGlobalTexture(SSR_SC.SSR_SceneColor_ID, SSR_SceneColor_RT);
            buffer.CopyTexture(ShaderProperty.ColorAttachmentId, SSR_SceneColor_RT);

            buffer.SetGlobalTexture(SSR_SC.SSR_HierarchicalDepth_ID, SSR_HierarchicalDepth_RT);

            ////////RayCasting//////
            buffer.SetGlobalTexture(SSR_SC.SSR_Trace_ID, SSR_TraceMask_RT[0]);
            buffer.SetGlobalTexture(SSR_SC.SSR_Mask_ID, SSR_TraceMask_RT[1]);
            if (ssrSetting.TraceMethod == TraceApprox.HiZTrace)
            {
                //SSR_TraceMask_RT[0]是深度，随便传的，不需要
                buffer.BlitMRT(SSR_TraceMask_ID, SSR_TraceMask_RT[0], StochasticScreenSpaceReflectionMaterial,
                    (ssrSetting.RayNums > 1) ? RenderPass_HiZ3D_MultiSpp : RenderPass_HiZ3D_SingelSpp);
            }
            else
            {
                buffer.BlitMRT(SSR_TraceMask_ID, SSR_TraceMask_RT[0], StochasticScreenSpaceReflectionMaterial,
                    (ssrSetting.RayNums > 1) ? RenderPass_Linear2D_MultiSPP : RenderPass_Linear2D_SingelSPP);
            }

            ////////Spatial filter//////  
            buffer.SetGlobalTexture(SSR_SC.SSR_Spatial_ID, SSR_Spatial_RT);
            buffer.BlitSRT(SSR_Spatial_RT, StochasticScreenSpaceReflectionMaterial,
                (ssrSetting.RayNums > 1) ? RenderPass_Spatiofilter_MultiSPP : RenderPass_Spatiofilter_SingleSPP);
            buffer.CopyTexture(SSR_Spatial_RT, SSR_TemporalCurr_RT);

            ////////Temporal filter//////
            buffer.SetGlobalTexture(SSR_SC.SSR_TemporalPrev_ID, SSR_TemporalPrev_RT);
            buffer.SetGlobalTexture(SSR_SC.SSR_TemporalCurr_ID, SSR_TemporalCurr_RT);
            buffer.BlitSRT(SSR_TemporalCurr_RT, StochasticScreenSpaceReflectionMaterial,
                (ssrSetting.RayNums > 1) ? RenderPass_Temporalfilter_MultiSpp : RenderPass_Temporalfilter_SingleSPP);
            buffer.CopyTexture(SSR_TemporalCurr_RT, SSR_TemporalPrev_RT);
        }

        private void RenderScreenSpaceReflection(CommandBuffer buffer)
        {
#if UNITY_EDITOR
            if (ssrSetting.UseSeparateRender)
                SeparateRender(buffer);
            else
                CombineRender(buffer);

#else
                SeparateRender(buffer);
#endif


            ////////Combien Reflection//////
            //////Combien Reflection//////
            buffer.SetGlobalTexture(SSR_SC.SSR_CombineScene_ID, SSR_CombineScene_RT);
#if UNITY_EDITOR
            buffer.BlitSRT(SSR_CombineScene_RT, ShaderProperty.ColorAttachmentId, StochasticScreenSpaceReflectionMaterial, (int)ssrSetting.DeBugPass);
#else
                        buffer.BlitSRT(SSR_CombineScene_RT, ShaderProperty.ColorAttachmentId, StochasticScreenSpaceReflectionMaterial, 9);
#endif
            SSR_Prev_ViewProjectionMatrix = SSR_ViewProjectionMatrix;
        }

        public void Dispose()
        {
            RenderTexture.ReleaseTemporary(SSR_HierarchicalDepth_RT);
            RenderTexture.ReleaseTemporary(SSR_SceneColor_RT);
            RenderTexture.ReleaseTemporary(SSR_TraceMask_RT[0]);
            RenderTexture.ReleaseTemporary(SSR_TraceMask_RT[1]);
            RenderTexture.ReleaseTemporary(SSR_Spatial_RT);
            RenderTexture.ReleaseTemporary(SSR_TemporalPrev_RT);
            RenderTexture.ReleaseTemporary(SSR_TemporalCurr_RT);
            RenderTexture.ReleaseTemporary(SSR_CombineScene_RT);
        }

        //public static void BlitMRT(this CommandBuffer buffer, RenderTargetIdentifier[] colorIdentifier, RenderTargetIdentifier depthIdentifier, Material mat, int pass)
        //{
        //    buffer.SetRenderTarget(colorIdentifier, depthIdentifier);
        //    buffer.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
        //}


    }

    public static class SSR_SC
    {
        public static int SSR_Jitter_ID = Shader.PropertyToID("_SSR_Jitter");
        public static int SSR_BRDFBias_ID = Shader.PropertyToID("_SSR_BRDFBias");
        public static int SSR_NumSteps_Linear_ID = Shader.PropertyToID("_SSR_NumSteps_Linear");
        public static int SSR_NumSteps_HiZ_ID = Shader.PropertyToID("_SSR_NumSteps_HiZ");
        public static int SSR_NumRays_ID = Shader.PropertyToID("_SSR_NumRays");
        public static int SSR_NumResolver_ID = Shader.PropertyToID("_SSR_NumResolver");
        public static int SSR_ScreenFade_ID = Shader.PropertyToID("_SSR_ScreenFade");
        public static int SSR_Thickness_ID = Shader.PropertyToID("_SSR_Thickness");
        public static int SSR_TemporalScale_ID = Shader.PropertyToID("_SSR_TemporalScale");
        public static int SSR_TemporalWeight_ID = Shader.PropertyToID("_SSR_TemporalWeight");
        public static int SSR_ScreenSize_ID = Shader.PropertyToID("_SSR_ScreenSize");
        public static int SSR_RayCastSize_ID = Shader.PropertyToID("_SSR_RayCastSize");
        public static int SSR_NoiseSize_ID = Shader.PropertyToID("_SSR_NoiseSize");
        public static int SSR_RayStepSize_ID = Shader.PropertyToID("_SSR_RayStepSize");
        public static int SSR_ProjInfo_ID = Shader.PropertyToID("_SSR_ProjInfo");
        public static int SSR_CameraClipInfo_ID = Shader.PropertyToID("_SSR_CameraClipInfo");
        public static int SSR_TraceDistance_ID = Shader.PropertyToID("_SSR_TraceDistance");
        public static int SSR_BackwardsRay_ID = Shader.PropertyToID("_SSR_BackwardsRay");
        public static int SSR_TraceBehind_ID = Shader.PropertyToID("_SSR_TraceBehind");
        public static int SSR_CullBack_ID = Shader.PropertyToID("_SSR_CullBack");
        public static int SSR_HiZ_PrevDepthLevel_ID = Shader.PropertyToID("_SSR_HiZ_PrevDepthLevel");
        public static int SSR_HiZ_MaxLevel_ID = Shader.PropertyToID("_SSR_HiZ_MaxLevel");
        public static int SSR_HiZ_StartLevel_ID = Shader.PropertyToID("_SSR_HiZ_StartLevel");
        public static int SSR_HiZ_StopLevel_ID = Shader.PropertyToID("_SSR_HiZ_StopLevel");



        public static int SSR_Noise_ID = Shader.PropertyToID("_SSR_Noise");
        public static int SSR_PreintegratedGF_LUT_ID = Shader.PropertyToID("_SSR_PreintegratedGF_LUT");

        public static int SSR_HierarchicalDepth_ID = Shader.PropertyToID("_SSR_HierarchicalDepth_RT");
        public static int SSR_SceneColor_ID = Shader.PropertyToID("_SSR_SceneColor_RT");
        public static int SSR_CombineScene_ID = Shader.PropertyToID("_SSR_CombienReflection_RT");



        public static int SSR_Trace_ID = Shader.PropertyToID("_SSR_RayCastRT");
        public static int SSR_Mask_ID = Shader.PropertyToID("_SSR_RayMask_RT");
        public static int SSR_Spatial_ID = Shader.PropertyToID("_SSR_Spatial_RT");
        public static int SSR_TemporalPrev_ID = Shader.PropertyToID("_SSR_TemporalPrev_RT");
        public static int SSR_TemporalCurr_ID = Shader.PropertyToID("_SSR_TemporalCurr_RT");



        public static int SSR_ProjectionMatrix_ID = Shader.PropertyToID("_SSR_ProjectionMatrix");
        public static int SSR_ViewProjectionMatrix_ID = Shader.PropertyToID("_SSR_ViewProjectionMatrix");
        public static int SSR_LastFrameViewProjectionMatrix_ID = Shader.PropertyToID("_SSR_LastFrameViewProjectionMatrix");
        public static int SSR_InverseProjectionMatrix_ID = Shader.PropertyToID("_SSR_InverseProjectionMatrix");
        public static int SSR_InverseViewProjectionMatrix_ID = Shader.PropertyToID("_SSR_InverseViewProjectionMatrix");
        public static int SSR_WorldToCameraMatrix_ID = Shader.PropertyToID("_SSR_WorldToCameraMatrix");
        public static int SSR_CameraToWorldMatrix_ID = Shader.PropertyToID("_SSR_CameraToWorldMatrix");
        public static int SSR_ProjectToPixelMatrix_ID = Shader.PropertyToID("_SSR_ProjectToPixelMatrix");

    }
}
