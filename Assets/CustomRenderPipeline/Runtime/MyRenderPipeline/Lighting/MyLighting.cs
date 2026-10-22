
using CustomRenderPipline.DefferPipeline;
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline
{
    struct OtherLight
    {
        public Vector4 lightColor;
        public Vector4 lightPosition;
        public Vector4 lightDirectionsAndMasks;
        public Vector4 lightSpotAngles;
        public Vector4 lightShadowData;
    }
    public class MyLighting 
    {
        const string bufferName = "Lighting";

        /// <summary>
        /// 支持的直接光和间接光的数量
        /// </summary>
        const int maxDirLightCount = 4, _MAX_OTHER_LIGHT_COUNT = 256;
        CullingResults cullingResults;
        ClusterLight clusterLight;

        CommandBuffer buffer = new CommandBuffer
        {
            name = bufferName
        };

        static int
            dirLightCountId = Shader.PropertyToID("_DirectionalLightCount"),
            dirLightColorsId = Shader.PropertyToID("_DirectionalLightColors"),
            dirLightDirectionsAndMasksId =
                Shader.PropertyToID("_DirectionalLightDirectionsAndMasks"),
            dirLightShadowDataId =
                Shader.PropertyToID("_DirectionalLightShadowData");

        public static int MySH9Array = Shader.PropertyToID("_Sh9"),
            MySkybox = Shader.PropertyToID("_MySkybox");

        static int
            otherLightCountId = Shader.PropertyToID("_OtherLightCount"),
            otherLightBufferId = Shader.PropertyToID("_OtherLightBuffer");

        static Vector4[]
            dirLightColors = new Vector4[maxDirLightCount],
            dirLightDirectionsAndMasks = new Vector4[maxDirLightCount],
            dirLightShadowData = new Vector4[maxDirLightCount];

        private NativeArray<OtherLight> _OtherLightArray =
            new NativeArray<OtherLight>(_MAX_OTHER_LIGHT_COUNT, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        private ComputeBuffer _OtherLightBuffer = new
            ComputeBuffer(_MAX_OTHER_LIGHT_COUNT, Marshal.SizeOf<OtherLight>());


        MyShadow shadows;

        public Material material;


        public MyLighting(Material material)
        {
            shadows = new MyShadow(material);
            this.material = material;
            Shader.SetGlobalBuffer(otherLightBufferId, _OtherLightBuffer);
            GC.SuppressFinalize(_OtherLightBuffer);
        }



        /// <summary>
        /// 准备以及复制灯光数据，在灯光中会一同处理阴影数据
        /// </summary>
        /// <param name="cullingResults">裁剪数据</param>
        /// <param name="shadowSettings">阴影设置数据</param>
        /// <param name="renderingLayerMask">渲染遮罩层</param>
        public void Setup(
            ScriptableRenderContext context, int renderingLayerMask,
            CullingResults cullingResults, ShadowSetting shadowSettings,
            float shadowDistance, Camera camera
        )
        {
            this.cullingResults = cullingResults;
            buffer.BeginSample(bufferName);
            buffer.SetGlobalVectorArray(MySH9Array, MyRenderObject.SH9);
            if (MyRenderObject.SceneSkybox != null)
                buffer.SetGlobalTexture(MySkybox, MyRenderObject.SceneSkybox);


            shadows.Setup(context, shadowDistance, shadowSettings, camera); //注册阴影数据
            SetupLights(renderingLayerMask);  //注册灯光，传递灯光数据
            shadows.Render(renderingLayerMask);                             //渲染阴影
            buffer.EndSample(bufferName);
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
        }

        /// <summary>		/// 清除阴影纹理		/// </summary>
        public void Cleanup()
        {
            shadows.Cleanup();
        }

        public void Dispose()
        {
            _OtherLightArray.Dispose();
            _OtherLightBuffer.Dispose();
            //clusterLight?.Dispose();

        }

        public void ReadyClusterLight(Camera camera, ClusterLightSetting setting,
            CommandBuffer buffer, int width, int height)
        {
            if (clusterLight == null)
                clusterLight = new ClusterLight();

            if (setting.isUse)
            {
                buffer.EnableShaderKeyword("_USE_CLUSTER");
            }
            else
            {
                buffer.DisableShaderKeyword("_USE_CLUSTER");
                return;
            }

            clusterLight.ComputeLightCluster(buffer, setting.clusterLightCS,
                camera, width, height, setting.renderVisableLight);
        }

        /// <summary>	/// 准备以及传递灯光数据	/// </summary>
        void SetupLights(int renderingLayerMask)
        {
            //所有可见的灯光数据
            NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
            int dirLightCount = 0, otherLightCount = 0;
            //接下来需要进行灯光渲染

            //枚举每一个灯光数据进行注册
            for (int i = 0; i < visibleLights.Length; i++)
            {
                VisibleLight visibleLight = visibleLights[i];   //目前灯光数据
                Light light = visibleLight.light;               //该灯光的灯光内部数据
                if ((light.renderingLayerMask & renderingLayerMask) != 0)
                {   //判断该灯光是否要在本摄像机中支持
                    switch (visibleLight.lightType)
                    {           //判断灯光类型，进行对应的初始化
                        case LightType.Directional:
                            if (dirLightCount < maxDirLightCount)
                            {
                                SetupDirectionalLight(
                                    dirLightCount++, ref visibleLight, light
                                );
                            }
                            break;
                        case LightType.Point:
                            if (otherLightCount < _MAX_OTHER_LIGHT_COUNT)
                            {
                                SetupPointLight(
                                    otherLightCount++, ref visibleLight, light
                                );
                            }
                            break;
                        case LightType.Spot:
                            if (otherLightCount < _MAX_OTHER_LIGHT_COUNT)
                            {
                                SetupSpotLight(otherLightCount++, ref visibleLight, light);
                            }
                            break;
                    }
                }
            }

            buffer.SetGlobalInt(dirLightCountId, dirLightCount);
            if (dirLightCount > 0)
            {
                buffer.SetGlobalVectorArray(dirLightColorsId, dirLightColors);
                buffer.SetGlobalVectorArray(
                    dirLightDirectionsAndMasksId, dirLightDirectionsAndMasks
                );
                buffer.SetGlobalVectorArray(dirLightShadowDataId, dirLightShadowData);
            }

            buffer.SetGlobalInt(otherLightCountId, otherLightCount);

            _OtherLightBuffer.SetData(_OtherLightArray, 0, 0, otherLightCount);
        }
        
        //Matrix4x4 GetViewMatrix(Light light)
        //{
        //    Transform transform = light.transform;
        //    //获得视图矩阵
        //    var lookMatrix = Matrix4x4.LookAt(transform.position, transform.position + transform.forward, transform.up);
        //    var scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
        //    //得到最终的viewMat，之后需要和投影矩阵配合，实现阴影渲染
        //    var viewMatrix = scaleMatrix * lookMatrix.inverse;
        //    return viewMatrix;
        //}

        /// <summary>
        /// 注册直接光数据，存储到数组中，之后传递给GPU
        /// </summary>
        /// <param name="index">数组编号</param>
        /// <param name="visibleLight">可视灯光数据，存储了矩阵数据</param>
        /// <param name="light">灯光数据</param>
        void SetupDirectionalLight(
            int index, ref VisibleLight visibleLight, Light light
        )
        {
            dirLightColors[index] = visibleLight.finalColor;                    //存颜色
            Vector4 dirAndMask = -visibleLight.localToWorldMatrix.GetColumn(2); //方向
            dirAndMask.w = light.renderingLayerMask.ReinterpretAsFloat();       //遮罩层
            dirLightDirectionsAndMasks[index] = dirAndMask;                     //存储方向和遮罩层
            dirLightShadowData[index] =
                shadows.ReserveDirectionalShadows(light);         //存储阴影数据
        }

        /// <summary>	/// 注册射灯数据	/// </summary>
        void SetupSpotLight(
            int index, ref VisibleLight visibleLight, Light light
        )
        {
            OtherLight otherLight = _OtherLightArray[index];

            otherLight.lightColor = visibleLight.finalColor;
            otherLight.lightColor.w = visibleLight.range;
            Vector4 position = visibleLight.localToWorldMatrix.GetColumn(3);
            position.w =
                1f / Mathf.Max(visibleLight.range * visibleLight.range, 0.00001f);
            otherLight.lightPosition = position;
            Vector4 dirAndMask = -visibleLight.localToWorldMatrix.GetColumn(2);
            dirAndMask.w = light.renderingLayerMask.ReinterpretAsFloat();
            otherLight.lightDirectionsAndMasks = dirAndMask;

            float innerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * light.innerSpotAngle);
            float outerCos = Mathf.Cos(Mathf.Deg2Rad * 0.5f * visibleLight.spotAngle);
            float angleRangeInv = 1f / Mathf.Max(innerCos - outerCos, 0.001f);
            otherLight.lightSpotAngles = new Vector4(
                angleRangeInv, -outerCos * angleRangeInv, outerCos
            );
            otherLight.lightShadowData = shadows.ReserveOtherShadows(light);

            _OtherLightArray[index] = otherLight;
        }

        /// <summary>	/// 注册点光数据	/// </summary>
        void SetupPointLight(
            int index, ref VisibleLight visibleLight, Light light
        )
        {
            OtherLight otherLight = _OtherLightArray[index];
            otherLight.lightColor = visibleLight.finalColor;
            otherLight.lightColor.w = visibleLight.range;
            Vector4 position = visibleLight.localToWorldMatrix.GetColumn(3);        //世界坐标
            position.w =
                1f / Mathf.Max(visibleLight.range * visibleLight.range, 0.00001f);  //点光范围
            otherLight.lightPosition = position;                                  //存储点光位置以及范围
            otherLight.lightSpotAngles = new Vector4(0f, 1f, 10f);                      //与射灯进行区分的赋值
            Vector4 dirAndmask = Vector4.one;
            dirAndmask.w = light.renderingLayerMask.ReinterpretAsFloat();
            otherLight.lightDirectionsAndMasks = dirAndmask;
            otherLight.lightShadowData = shadows.ReserveOtherShadows(light);

            _OtherLightArray[index] = otherLight;
        }


    }
}