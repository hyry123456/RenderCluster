using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.UI;

namespace CustomRenderPipline.MyRenderPipeline
{
    public struct MyCPURenderData
    {
        public Vector4 range;
        //public MyMeshRender obj;
        public bool isUse;
    }

    [ExecuteAlways]
    public class MyRenderObject : MonoBehaviour
    {
        private static MyRenderObject instance;
        public static MyRenderObject Instance
        {
            get
            {
                return instance;
            }
        }

        private static Cubemap _sceneSkybox;
        private static List<Vector4> _sh9 = new List<Vector4>();
        public static List<Vector4> SH9=>_sh9;
        public static Cubemap SceneSkybox => _sceneSkybox;
        public static void SetSceneLighting(List<Vector4> sh9, Cubemap cubemap)
        {
            _sh9 = sh9;
            _sceneSkybox = cubemap;
        }

        public static MyRenderObject CreateInstance()
        {
            if (MyRenderObject.Instance == null)
            {
                GameObject gameObject = new GameObject("My Render Objects");
                gameObject.AddComponent<MyRenderObject>();
            }
            return instance;
        }

        private NativeArray<MyCPURenderData> staticCPUObjects;
        public NativeArray<MyCPURenderData> StaticCPUObjects => staticCPUObjects;

        private MyMeshRender[] staticCPUObjects_data;
        public MyMeshRender[] StaticCPUObjects_data => staticCPUObjects_data;

        private int staticCPUObjectsCount;
        public int CurrentStaticObjectsCount => staticCPUObjectsCount;

        //private StaticObjectJob staticObjectJob;
        //public StaticObjectJob _StaticObjectJob => staticObjectJob;


        private void Awake()
        {
            if(instance == null)
            {
                instance = this;
                staticCPUObjects = new NativeArray<MyCPURenderData>(5, Allocator.Persistent);
                staticCPUObjects_data = new MyMeshRender[5];
                staticCPUObjectsCount = 0;
#if UNITY_EDITOR
                if (UnityEditor.EditorApplication.isPlaying)

#endif
                {
                    DontDestroyOnLoad(gameObject);
                }
                gameObject.hideFlags = HideFlags.HideAndDontSave;

                return;
            }
            else
                DestroyImmediate(this);
        }

        private Vector4 GetRange(Transform transform, Bounds bounds)
        {
            Vector3 center = transform.TransformPoint(bounds.center);
            Vector3 size = bounds.size, scale = transform.localScale;
            float x = size.x * scale.x, y = size.y * scale.y, z = size.z * scale.z;
            float radius = Mathf.Sqrt(x * x + y * y + z * z) / 2;
            return new Vector4(center.x, center.y, center.z, radius);
        }


        public void InsertStaticCPURender(MyMeshRender myMeshRender, Bounds meshBounds)
        {
            if(staticCPUObjects.Length == staticCPUObjectsCount)
            {
                var temArr = new NativeArray<MyCPURenderData>(staticCPUObjectsCount * 2, Allocator.Persistent);
                var temDatas = new MyMeshRender[staticCPUObjectsCount * 2];
                for(int i = 0; i < staticCPUObjectsCount; i++)
                {
                    temArr[i] = staticCPUObjects[i];
                    temDatas[i] = staticCPUObjects_data[i];
                }

                staticCPUObjects.Dispose();
                staticCPUObjects = temArr;
                staticCPUObjects_data = temDatas;
            }
            staticCPUObjects[staticCPUObjectsCount] = new MyCPURenderData()
            {
                range = GetRange(myMeshRender.transform, meshBounds),
                isUse = false,
            };
            staticCPUObjects_data[staticCPUObjectsCount] = myMeshRender;
            staticCPUObjectsCount++;
        }

        public void RemoveStaticCPURender(MyMeshRender myMeshRender)
        {
            if (staticCPUObjects == null)
                return;
            for(int i = 0;i < staticCPUObjectsCount; i++)
            {
                //找到就删除
                if (staticCPUObjects_data[i] == myMeshRender)
                {
                    staticCPUObjects[i] = staticCPUObjects[staticCPUObjectsCount - 1];
                    staticCPUObjects_data[i] = staticCPUObjects_data[staticCPUObjectsCount - 1];
                    staticCPUObjectsCount--;
                    return;
                }
            }
            //cpuDatas.Remove(myMeshRender);
        }

        private void OnDestroy()
        {
            if(staticCPUObjects != null && staticCPUObjects.IsCreated)
            {
                staticCPUObjects.Dispose();
            }
            if(instance == this)
                instance = null;
        }
    }

    //[BurstCompile(CompileSynchronously = true)]
    /// <summary>    /// 静态物体的裁剪作业    /// </summary>
    public struct StaticObjectViewJob : IJobParallelFor
    {

        public NativeArray<MyCPURenderData> staticObjs;
        [ReadOnly]
        public Vector4 Plane0, Plane1, Plane2, Plane3, Plane4, Plane5;

        private bool IsOutside(Vector4 plane, Vector4 range)
        {
            if (Vector3.Dot(plane, range) + plane.w < -range.w)
                return true;
            return false;
        }

        public void Execute(int i)
        {
            var obj = staticObjs[i];
            Vector4 range = obj.range;

            if(IsOutside(Plane0, range) || IsOutside(Plane1, range) ||
                IsOutside(Plane2, range) || IsOutside(Plane3, range) ||
                IsOutside(Plane4, range) || IsOutside(Plane5, range))
            {
                obj.isUse = false;
                staticObjs[i] = obj;
            }
            else
            {
                obj.isUse = true;
                staticObjs[i] = obj;
            }
        }
    }

    //[BurstCompile(CompileSynchronously = true)]
    /// <summary>    /// 静态物体的裁剪作业    /// </summary>
    public struct StaticObjectShadowJob : IJobParallelFor
    {

        public NativeArray<MyCPURenderData> staticObjs;
        [ReadOnly]
        public Vector4 Plane0, Plane1, Plane2, Plane3, Plane4;

        private bool IsOutside(Vector4 plane, Vector4 range)
        {
            if (Vector3.Dot(plane, range) + plane.w < -range.w)
                return true;
            return false;
        }

        public void Execute(int i)
        {
            var obj = staticObjs[i];
            Vector4 range = obj.range;

            if (IsOutside(Plane0, range) || IsOutside(Plane1, range) ||
                IsOutside(Plane2, range) || IsOutside(Plane3, range) ||
                IsOutside(Plane4, range))
            {
                obj.isUse = false;
                staticObjs[i] = obj;
            }
            else
            {
                obj.isUse = true;
                staticObjs[i] = obj;
            }
        }
    }
}
