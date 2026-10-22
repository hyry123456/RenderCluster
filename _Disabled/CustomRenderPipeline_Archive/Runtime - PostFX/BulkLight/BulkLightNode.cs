using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline

{

    public enum Texture3DSize
    {
        _1 = 1,
        _4 = 4,
        _8 = 8,
        _16 = 16,
        _32 = 32,
        _64 = 64,
        _128 = 128,
        _256 = 256,
        _512 = 512,
    }

    public enum ShadowMapSize
    {
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096,
        _8192 = 8192,
    }

    public enum ShadowFilter
    {
        _1 = 1,
        _2 = 2,
        _3 = 3,
        _6 = 6,
    }


    enum BulkMode
    {
        Bake,
        Realtime,
    }

    /// <summary> /// 根据灯光创建Mesh，用来加入到体积光渲染栈中 /// </summary>
    public class BulkLightNode : MonoBehaviour, IBulkLight
    {
        static int
            Extend = Shader.PropertyToID("_Extend"),
            Offset = Shader.PropertyToID("_Offset"),
            RayCount = Shader.PropertyToID("_RayCount"),
            LightIntensity = Shader.PropertyToID("_LightIntensity"),
            Light3D = Shader.PropertyToID("_Light3D");

        public Vector3 extend = Vector3.one;
        public Vector3 offset = Vector3.zero;

        [SerializeField]
        BulkMode bulkMode;

        public Light[] renderLights;                        //需要渲染到贴图的灯光
        public ComputeShader computeShader;                 //渲染时执行的CS
        public Texture3DSize size = Texture3DSize._16;      //渲染到的贴图大小
        public ShadowMapSize shadowMapSize = ShadowMapSize._1024;
        public ShadowFilter filter = ShadowFilter._2;       //灯光采样的次数
        [Range(12, 36)]
        public int rayCount = 16;
        [Range(0f, 15.0f)]
        public float lightIntensity = 3f;



        Bounds bounds;
        Material material;
        LightMap3D lightMap;
        BulkLightStruct lightStruct;
        BulkLight bulkLight;

        private void Awake()
        {
            bulkLight = BulkLight.CreateInstance();

            if (bulkMode == BulkMode.Realtime)
            {
                lightStruct = new BulkLightStruct()
                {
                    center = transform.position + offset,
                    extend = extend,
                };
                //只进行提交，提交后在切换场景时父类会自己清除
                bulkLight.AddBulkLightBox(lightStruct);
            }
            else
            {
                if (renderLights == null || renderLights.Length == 0)
                {
                    return;
                }

                //创建范围，方便剔除
                bounds = new Bounds();
                bounds.center = transform.position + offset;
                bounds.extents = extend * 0.5f;

                //具体的贴图获取是在这里得到的
                lightMap = new LightMap3D(bounds, renderLights, computeShader,
                    shadowMapSize, size, filter, RenderTextureFormat.ARGB32);

                //创建材质，加入到渲染栈中
                material = new Material(Shader.Find("CustomRP/BulkLightNode"));
                material.SetVector(Extend, extend);
                material.SetVector(Offset, offset + transform.position);
                material.SetTexture(Light3D, lightMap.LightMap);
                material.SetInt(RayCount, rayCount);
                material.SetFloat(LightIntensity, lightIntensity);
                bulkLight.AddBulkLightDrawNode(this);
            }
        }

        //private void Start()
        //{

        //}


        private void OnDestroy()
        {
            if(bulkMode == BulkMode.Bake)
            {
                //Awake 中 renderLights 为空时会提前 return，lightMap 可能从未创建；
                //退出播放/域重载时 bulkLight 也可能已置空，两者都必须判空
                if (bulkLight != null)
                    bulkLight.RemoveBulkLightDrawNode(this);
                if (lightMap != null)
                {
                    lightMap.Dispose();
                    lightMap = null;
                }
            }
        }

#if UNITY_EDITOR

        public bool CheckLightIntersect = false;

        private void OnDrawGizmos()
        {
            Color color = Color.yellow;
            color.a = 0.3f;
            Gizmos.color = color;
            Gizmos.DrawCube(offset + transform.position, extend);
            Gizmos.color = Color.white;
            Gizmos.DrawWireCube(offset + transform.position, extend);
        }

        bool CheckLight(Bounds bounds, Light light)
        {
            Vector3 point = bounds.ClosestPoint(light.transform.position);
            switch (light.type)
            {
                case LightType.Spot:
                    float dotV = Vector3.Dot((point - light.transform.position).normalized, light.transform.forward);
                    //在夹角中
                    if (dotV >= Mathf.Cos(Mathf.Deg2Rad * light.spotAngle / 2))
                    {
                        //在范围中
                        if (Vector3.Distance(point, light.transform.position) < light.range)
                            return true;
                    }
                    else
                    {
                        Vector3 rayToBox = (point - light.transform.position).normalized;
                        Vector3 lightRay = light.transform.forward;
                        Vector3 lightToBox = (rayToBox - lightRay).normalized;
                        float angle = Mathf.Acos(Vector3.Dot(rayToBox, lightToBox)) * Mathf.Rad2Deg;
                        Vector3 lightNearRay = lightToBox * Mathf.Clamp01(light.spotAngle * 0.5f / angle);
                        lightNearRay = (lightNearRay + lightRay).normalized;
                        //ray = new Ray(light.transform.position, lightNearRay);
                        Ray ray = new Ray(light.transform.position, lightNearRay);
                        float len;
                        if (bounds.IntersectRay(ray, out len))
                        {
                            if (len < light.range)
                                return true;
                        }
                    }
                    break;
                case LightType.Point:
                    if (Vector3.Distance(point, light.transform.position) < light.range)
                        return true;
                    break;
                default:
                    break;
            }
            return false;
        }


        private void OnValidate()
        {
            if (CheckLightIntersect)
            {
                CheckLightIntersect = false;
                Bounds bulkBound = new Bounds();
                bulkBound.center = transform.position + offset;
                bulkBound.extents = extend * 0.5f;

                System.Collections.Generic.List<Light> lights = new System.Collections.Generic.List<Light>();

                Light[] checkLights = GameObject.FindObjectsOfType<Light>();
                
                for(int i = 0; i < checkLights.Length; i++)
                {
                    if (CheckLight(bulkBound, checkLights[i]))
                    {
                        lights.Add(checkLights[i]);
                    }
                }
                renderLights = lights.ToArray();
            }

            if(material != null)
            {
                material.SetVector(Extend, extend);
                material.SetVector(Offset, offset + transform.position);
                material.SetTexture(Light3D, lightMap.LightMap);
                material.SetInt(RayCount, rayCount);
                material.SetFloat(LightIntensity, lightIntensity);
                bounds.center = transform.position + offset;
                bounds.extents = extend * 0.5f;
            }
        }
#endif

        public void DrawBulkLightNode(CommandBuffer buffer)
        {
            //视锥体剔除，GPUDravinSystem的视锥会每次渲染时更新
            if (GPUDravinSystem.Instance.FrustumCulling(bounds))
            {
                buffer.DrawMesh(BulkLight.BulkLightCube, transform.localToWorldMatrix, material);
            }
        }

    }
}