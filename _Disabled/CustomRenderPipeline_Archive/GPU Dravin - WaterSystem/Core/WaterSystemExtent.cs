using System;
using UnityEngine;
using UnityEngine.Rendering;
using static KWS.KWS_ShaderConstants;

namespace KWS
{

    //拓展类，用来定义一些重要函数以及方法，在主文件哪里只负责主要逻辑
    public partial class WaterSystem
    {
        internal float CurrentMaxWaveHeight => Mathf.Max(0, Settings.WindSpeed * 0.4f - 0.75f);

        public bool IsCameraUnderwater { get; private set; }
        /// <summary>
        /// World space bounds of the rendered mesh (the box size relative to the wind speed).
        /// </summary>
        /// <returns></returns>
        public Bounds WorldSpaceBounds
        {
            get
            {
                 return new Bounds(WaterRelativeWorldPosition + Settings.WaterScale3 / 2, 
                     new Vector3(Settings.WaterScale3.x, Mathf.Abs(Settings.WaterScale3.y) + CurrentMaxWaveHeight, Settings.WaterScale3.z));
            }
        }

        private Transform _waterTransform;
        internal Transform WaterRootTransform
        {
            get
            {
                if (_waterTransform == null) _waterTransform = transform;
                return _waterTransform;
            }
        }

        internal Vector3 WaterRelativeWorldPosition
        {
            get
            {
                //if (Settings.WaterMeshType == WaterMeshTypeEnum.InfiniteOcean)
                //{
                //    var pos = KW_Extensions.GetCameraPositionFast(_currentCamera);
                //    pos.y = WaterPivotWorldPosition.y;
                //    return pos;
                //}
                //else return WaterPivotWorldPosition;
                return WaterPivotWorldPosition;
            }
        }

        internal Quaternion WaterPivotWorldRotation => WaterRootTransform.rotation;
        internal Vector3 WaterPivotWorldPosition => WaterRootTransform.position;


        void SetAmbientLightToShaders()
        {
            // return half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
            SphericalHarmonicsL2 sh;
            LightProbes.GetInterpolatedProbe(WaterRelativeWorldPosition, null, out sh);
            var ambient = new Vector3(sh[0, 0] - sh[0, 6], sh[1, 0] - sh[1, 6], sh[2, 0] - sh[2, 6]);
            ambient = Vector3.Max(ambient, Vector3.zero);
            Shader.SetGlobalVector(KWS_ShaderConstants.DynamicWaterParams.KWS_AmbientColor, ambient);
        }

        void SetGlobalCameraShaderParams(Camera _currentCamera)
        {
            var cameraProjectionMatrix = _currentCamera.projectionMatrix;
            if (IsSinglePassStereoEnabled)
            {
                var arr_matrix_I_VP = new Matrix4x4[2];
                for (uint eyeIndex = 0; eyeIndex < 2; eyeIndex++)
                {
                    var matrix_cameraProjeciton = _currentCamera.GetStereoProjectionMatrix((Camera.StereoscopicEye)eyeIndex);
                    var matrix_V = GL.GetGPUProjectionMatrix(matrix_cameraProjeciton, true);
                    var matrix_P = _currentCamera.GetStereoViewMatrix((Camera.StereoscopicEye)eyeIndex);
                    var matrix_VP = matrix_V * matrix_P;

                    CurrentVPMatrixStereo[eyeIndex] = matrix_VP;
                    arr_matrix_I_VP[eyeIndex] = matrix_VP.inverse;
                }

                Shader.SetGlobalMatrixArray(CameraMatrix.KWS_MATRIX_VP_STEREO, CurrentVPMatrixStereo);
                Shader.SetGlobalMatrixArray(CameraMatrix.KWS_MATRIX_I_VP_STEREO, arr_matrix_I_VP);
            }
            else
            {
                var matrix_V = GL.GetGPUProjectionMatrix(cameraProjectionMatrix, true);
                var maitix_P = _currentCamera.worldToCameraMatrix;
                CurrentVPMatrix = matrix_V * maitix_P;

                Shader.SetGlobalMatrix(CameraMatrix.KWS_MATRIX_VP, CurrentVPMatrix);
                Shader.SetGlobalMatrix(CameraMatrix.KWS_MATRIX_I_VP, CurrentVPMatrix.inverse);
            }


            Shader.SetGlobalVector(DynamicWaterParams.KWS_CameraForward, _currentCamera.transform.forward);

            var fogState = 0;
            if (RenderSettings.fog)
            {
                if (RenderSettings.fogMode == FogMode.Linear) fogState = 1;
                else if (RenderSettings.fogMode == FogMode.Exponential) fogState = 2;
                else if (RenderSettings.fogMode == FogMode.ExponentialSquared) fogState = 3;
            }
            Shader.SetGlobalInt(DynamicWaterParams.KWS_FogState, fogState);
            Shader.SetGlobalInt(DynamicWaterParams.KWS_UnderwaterVisible, IsCameraUnderwater ?  1 : 0);


        }


        public static void CalculateNearPlaneWorldPoints(ref Vector3[] points, Camera cam)
        {
            points[0] = cam.ViewportToWorldPoint(new Vector3(0, 0, cam.nearClipPlane));
            points[1] = cam.ViewportToWorldPoint(new Vector3(1, 0, cam.nearClipPlane));
            points[2] = cam.ViewportToWorldPoint(new Vector3(0, 1, cam.nearClipPlane));
            points[3] = cam.ViewportToWorldPoint(new Vector3(1, 1, cam.nearClipPlane));
        }
        bool IsPointInsideAABB(Vector3 point, Vector3 min, Vector3 max)
        {
            return (point.x >= min.x && point.x <= max.x) &&
                   (point.y >= min.y && point.y <= max.y) &&
                   (point.z >= min.z && point.z <= max.z);
        }


        Vector3[] _nearPlaneWorldPoints = new Vector3[4];
        bool IsUnderwaterVisible(Camera cam, Bounds waterBounds)
        {
            CalculateNearPlaneWorldPoints(ref _nearPlaneWorldPoints, cam);
            var min = waterBounds.min;
            var max = waterBounds.max;

            if (IsPointInsideAABB(_nearPlaneWorldPoints[0], min, max)
             || IsPointInsideAABB(_nearPlaneWorldPoints[1], min, max)
             || IsPointInsideAABB(_nearPlaneWorldPoints[2], min, max)
             || IsPointInsideAABB(_nearPlaneWorldPoints[3], min, max))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

    }
}