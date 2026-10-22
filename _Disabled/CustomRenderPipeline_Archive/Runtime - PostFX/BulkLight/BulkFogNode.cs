using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline
{

    /// <summary>  /// 体积雾效果 /// </summary>
    public class BulkFogNode : MonoBehaviour, IDrawTransparent
    {
        public Vector3 extend = Vector3.one;
        public Vector3 offset = Vector3.zero;

        public Light[] renderLights;                        //需要渲染到贴图的灯光
        public ComputeShader computeShader;                 //渲染时执行的CS
        public Texture3DSize size = Texture3DSize._16;      //渲染到的贴图大小

        public ShadowMapSize shadowMapSize = ShadowMapSize._1024;
        public ShadowFilter filter = ShadowFilter._2;       //灯光采样的次数

        [Range(0f, 1f)]
        public float fogMaxDepth = 1f;
        [Range(0f, 1f)]
        public float fogMinDepth = 0f;
        [Range(0f, 1f)]
        public float depthFallOff = 0.5f;
        [Range(0f, 1f)]
        public float fogMaxHight = 1f;
        [Range(0f, 1f)]
        public float fogMinHight = 0f;
        [Range(0f, 1f)]
        public float hightFallOff = 0.5f;
        public Color fogColor = Color.gray;
        [Min(10)]
        public int rayCount = 16;

        public Vector3 FogFrequency = new Vector3(0.1f, 0.1f, 0.1f);
        public Vector3 FogSpeed = Vector3.one;

        public Texture noiseTex;

        [Min(0.1f)]
        public float fogIntensity = 1;


        Bounds bounds;
        Material material;
        LightMap3D lightMap;
        DrawNode drawNode;

        private void Start()
        {
            //创建范围，方便剔除
            bounds = new Bounds();
            bounds.center = transform.position + offset;
            bounds.extents = extend * 0.5f;

            lightMap = new LightMap3D(bounds, renderLights, computeShader,
                shadowMapSize, size, filter, RenderTextureFormat.ARGB32);

            material = new Material(Shader.Find("CustomRP/BulkFog"));
            drawNode = new DrawNode();
            drawNode.transparentEvent = DrawTransparent;
            drawNode.Submit();
        }


        private void OnDestroy()
        {
            lightMap.Dispose();
            drawNode.Remove();
        }


#if UNITY_EDITOR
        public bool CheckLightIntersect = false;

        private void OnDrawGizmos()
        {
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

                for (int i = 0; i < checkLights.Length; i++)
                {
                    if (CheckLight(bulkBound, checkLights[i]))
                    {
                        lights.Add(checkLights[i]);
                    }
                }
                renderLights = lights.ToArray();
            }
        }
#endif

        public void DrawTransparent(ScriptableRenderContext context, CommandBuffer buffer, Camera camera)
        {
            material.SetFloat("_FogMaxDepth", fogMaxDepth);
            material.SetFloat("_FogMinDepth", fogMinDepth);
            material.SetFloat("_FogDepthFallOff", depthFallOff);
            material.SetFloat("_FogMaxHight", fogMaxHight);
            material.SetFloat("_FogMinHight", fogMinHight);
            material.SetFloat("_FogPosYFallOff", hightFallOff);
            material.SetVector("_FogColor", fogColor);
            material.SetVector("_Extend", extend);
            material.SetVector("_Offset", transform.position + offset);
            material.SetInt("_RayCount", rayCount);
            material.SetTexture("_Noise", noiseTex == null ? Texture2D.blackTexture : noiseTex);
            material.SetTexture("_Light3D", lightMap.LightMap);
            material.SetFloat("_FogIntensity", fogIntensity);
            material.SetVector("_FogFrequency", FogFrequency);
            material.SetVector("_FogSpeed", FogSpeed);

            buffer.DrawMesh(BulkLight.BulkLightCube, transform.localToWorldMatrix, material);
        }
    }
}