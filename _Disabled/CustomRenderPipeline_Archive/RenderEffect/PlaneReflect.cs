using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{
    public class PlaneReflect : MonoBehaviour
    {
        public Camera oriCamera;
        Camera reflectCamera;
        [Range(0.1f, 0.8f)]
        public float renderScale = 0.3f;
        public Vector2 planeXZ = Vector2.one;
        Vector4 reflectPlane;

        PipelineCamera pipelineCamera;

        public RenderTexture renderTarget;

        CommandBuffer beforeOpaque;
        CommandBuffer afterfinal;

        public GameObject controlFather;
        public MeshRenderer[] SetMaterials;
        private List<Material> materials;
        Bounds bounds;
        private Plane[] planes = new Plane[6];

        private const string PlaneReflectKeyWord = "_PLANE_REFLECT";
        private static int ReflectTexureId = Shader.PropertyToID("_PlaneReflectTex");
        private static int PlaneFrontId = Shader.PropertyToID("_PlaneFront");
        private static int PlaneRightId = Shader.PropertyToID("_PlaneRight");

        private void Start()
        {
            GameObject game = new GameObject("ReflectCamera");
            game.hideFlags = HideFlags.HideInHierarchy;
            reflectCamera = game.AddComponent<Camera>();
            reflectCamera.CopyFrom(oriCamera);

            pipelineCamera = GetComponent<PipelineCamera>();
            if(pipelineCamera != null)
            {
                var pipe = game.AddComponent<PipelineCamera>();
                pipe.CopyFrom(pipelineCamera);
            }


            bounds = new Bounds(transform.position, new Vector3(planeXZ.x,
                0, planeXZ.y));
            reflectPlane = new Vector4(transform.up.x, transform.up.y, transform.up.z,
                -Vector3.Dot(transform.up, transform.position));

            materials = new List<Material>();

            //初始化要设置的材质
            if (SetMaterials != null && SetMaterials.Length > 0)
            {
                for (int i = 0; i < SetMaterials.Length; i++)
                {
                    if (SetMaterials[i].material != null)
                    {
                        materials.Add(SetMaterials[i].material);
                    }
                }
            }
            if(controlFather != null)
            {
                MeshRenderer[] meshes = controlFather.GetComponentsInChildren<MeshRenderer>();
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i].material != null)
                    {
                        materials.Add(meshes[i].material);
                    }
                }
            }

            beforeOpaque = new CommandBuffer();
            afterfinal = new CommandBuffer();
            beforeOpaque.SetInvertCulling(true);
            reflectCamera.AddCommandBuffer(CameraEvent.BeforeGBuffer, beforeOpaque);
            reflectCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, beforeOpaque);


            //准备渲染目标以及赋值贴图
            InitializeRenderTarget();

            reflectCamera.cameraType = CameraType.Reflection;



        }


        private void Update()
        {
            GeometryUtility.CalculateFrustumPlanes(oriCamera, planes);
            if(transform.hasChanged)
                reflectPlane = new Vector4(transform.up.x, transform.up.y, transform.up.z,
                    -Vector3.Dot(transform.up, transform.position));

            if (CheckPlane())
            {
                reflectCamera.gameObject.SetActive(true);
                InitializeRenderTarget();



                Matrix4x4 reflectMat = ObliqueMatrix(reflectPlane, reflectCamera);
                reflectCamera.projectionMatrix = reflectMat;

                //将计算得到的镜像矩阵应用到镜像摄像机
                reflectCamera.worldToCameraMatrix = oriCamera.worldToCameraMatrix 
                    * ReflectMatrix(reflectPlane);
            }
            else
            {
                reflectCamera.gameObject.SetActive(false);
            }
        }

        private bool CheckPlane()
        {
            Vector3 direct = Camera.main.transform.position - transform.position;
            return Vector3.Dot(direct, transform.up) > 0 &&
                GeometryUtility.TestPlanesAABB(planes, bounds);
        }

#if UNITY_EDITOR

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(planeXZ.x,
                0, planeXZ.y));
        }

#endif


        private void InitializeRenderTarget()
        {
            int width = (int)(oriCamera.pixelWidth * renderScale), hight = (int)(oriCamera.pixelHeight * renderScale);
            if (renderTarget == null || renderTarget.width != width || renderTarget.height != hight)
            {
                if (renderTarget == null)
                    RenderTexture.ReleaseTemporary(renderTarget);
                renderTarget = RenderTexture.GetTemporary(width, hight, 32, RenderTextureFormat.ARGB32);
                renderTarget.useMipMap = true;
                renderTarget.autoGenerateMips = false;
                if (!renderTarget.IsCreated())
                    renderTarget.Create();
                renderTarget.name = "PlaneReflect";

                reflectCamera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, afterfinal);
                afterfinal.Clear();
                afterfinal.SetInvertCulling(false);
                afterfinal.GenerateMips(renderTarget);
                reflectCamera.AddCommandBuffer(CameraEvent.AfterFinalPass, afterfinal);

                for (int i = 0; i < materials.Count; i++)
                {
                    materials[i].EnableKeyword(PlaneReflectKeyWord);
                    materials[i].SetTexture(ReflectTexureId, renderTarget);
                    materials[i].SetVector(PlaneFrontId, transform.forward);
                    materials[i].SetVector(PlaneRightId, transform.right);
                }
                reflectCamera.targetTexture = renderTarget;
                //修改渲染目标后需要重新计算投影矩阵
                reflectCamera.ResetProjectionMatrix();
            }
        }

        private void OnDestroy()
        {

            if (beforeOpaque != null)
            {
                beforeOpaque.Clear(); beforeOpaque = null;
                afterfinal.Clear(); afterfinal = null;
            }
            if (renderTarget == null)
                RenderTexture.ReleaseTemporary(renderTarget);
            if(materials != null)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    materials[i].DisableKeyword(PlaneReflectKeyWord);
                }
                materials.Clear();
            }
        }
        private Matrix4x4 ObliqueMatrix(Vector4 plane, Camera camera)
        {
            //世界空间的平面先变换到相机空间
            plane = (camera.worldToCameraMatrix.inverse).transpose * plane;


            Matrix4x4 proj = camera.projectionMatrix;

            //计算近裁面的最远角点Q
            Vector4 q = default(Vector4);
            q.x = (Mathf.Sign(plane.x) + proj.m02) / proj.m00;
            q.y = (Mathf.Sign(plane.y) + proj.m12) / proj.m11;
            q.z = -1.0f;
            q.w = (1.0f + proj.m22) / proj.m23;
            Vector4 c = plane * (2.0f / Vector4.Dot(plane, q));

            //计算M3'
            proj.m20 = c.x;
            proj.m21 = c.y;
            proj.m22 = c.z + 1.0f;
            proj.m23 = c.w;
            return proj;
        }

        private Matrix4x4 ReflectMatrix(Vector4 plane)
        {
            Matrix4x4 m = default(Matrix4x4);
            m.m00 = -2 * plane.x * plane.x + 1;
            m.m01 = -2 * plane.x * plane.y;
            m.m02 = -2 * plane.x * plane.z;
            m.m03 = -2 * plane.x * plane.w;

            m.m10 = -2 * plane.x * plane.y;
            m.m11 = -2 * plane.y * plane.y + 1;
            m.m12 = -2 * plane.y * plane.z;
            m.m13 = -2 * plane.y * plane.w;

            m.m20 = -2 * plane.z * plane.x;
            m.m21 = -2 * plane.z * plane.y;
            m.m22 = -2 * plane.z * plane.z + 1;
            m.m23 = -2 * plane.z * plane.w;

            m.m30 = 0; m.m31 = 0;
            m.m32 = 0; m.m33 = 1;
            return m;
        }

    }
}